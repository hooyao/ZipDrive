using System.Runtime.Versioning;
using DokanNet;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZipDrive.Application.Services;
using ZipDrive.Domain.Abstractions;
using ZipDrive.Domain.Configuration;
using ZipDrive.Domain.Models;

namespace ZipDrive.Infrastructure.FileSystem;

/// <summary>
/// Background service that manages the Dokan mount lifecycle and dynamic reload.
/// Mounts VFS on start, watches for archive directory changes, and unmounts on Ctrl+C / host shutdown.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DokanHostedService : BackgroundService
{
    private readonly IVirtualFileSystem _vfs;
    private readonly IArchiveManager _archiveManager;
    private readonly IArchiveDiscovery _discovery;
    private readonly DokanFileSystemAdapter _adapter;
    private readonly MountSettings _mountSettings;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<DokanHostedService> _logger;

    private Dokan? _dokan;
    private DokanInstance? _dokanInstance;
    private FileSystemWatcher? _watcher;
    private ArchiveChangeConsolidator? _consolidator;

    private static readonly string[] ZipExtensions = [".zip"];

    public DokanHostedService(
        IVirtualFileSystem vfs,
        IArchiveManager archiveManager,
        IArchiveDiscovery discovery,
        DokanFileSystemAdapter adapter,
        IOptions<MountSettings> mountSettings,
        IHostApplicationLifetime lifetime,
        ILogger<DokanHostedService> logger)
    {
        _vfs = vfs;
        _archiveManager = archiveManager;
        _discovery = discovery;
        _adapter = adapter;
        _mountSettings = mountSettings.Value;
        _lifetime = lifetime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation("Starting ZipDrive VFS...");
            _logger.LogInformation("Archive directory: {Dir}", _mountSettings.ArchiveDirectory);
            _logger.LogInformation("Mount point: {Mount}", _mountSettings.MountPoint);

            if (string.IsNullOrWhiteSpace(_mountSettings.ArchiveDirectory))
            {
                _logger.LogError("Mount:ArchiveDirectory is required. Set it in appsettings.jsonc, via command line (--Mount:ArchiveDirectory=<path>), or drag a folder onto ZipDrive.exe");
                Console.Error.WriteLine("Error: Mount:ArchiveDirectory is required.");
                Console.Error.WriteLine("Set it in appsettings.jsonc, via command line (--Mount:ArchiveDirectory=<path>),");
                Console.Error.WriteLine("or drag a folder onto ZipDrive.exe.");
                WaitForKeyAndStop();
                return;
            }

            if (!Directory.Exists(_mountSettings.ArchiveDirectory))
            {
                _logger.LogError("Mount:ArchiveDirectory does not exist: {ArchiveDirectory}", _mountSettings.ArchiveDirectory);
                Console.Error.WriteLine($"Error: Directory not found: {_mountSettings.ArchiveDirectory}");
                WaitForKeyAndStop();
                return;
            }

            // Detect network paths
            DetectNetworkPath();

            // Step 1: Mount VFS (discover ZIPs, build archive trie via AddArchiveAsync)
            await _vfs.MountAsync(new VfsMountOptions
            {
                RootPath = _mountSettings.ArchiveDirectory,
                MaxDiscoveryDepth = _mountSettings.MaxDiscoveryDepth
            }, stoppingToken);

            _logger.LogInformation("VFS mounted: archives discovered");

            // Step 2: Start FileSystemWatcher for dynamic reload
            StartWatcher();

            // Step 3: Create Dokan instance
            _dokan = new Dokan(new DokanNetLogger(_logger));

            var dokanBuilder = new DokanInstanceBuilder(_dokan)
                .ConfigureOptions(options =>
                {
                    options.Options = DokanOptions.WriteProtection | DokanOptions.FixedDrive;
                    options.MountPoint = _mountSettings.MountPoint;
                });

            _dokanInstance = dokanBuilder.Build(_adapter);

            _logger.LogInformation("Drive mounted at {MountPoint}. Press Ctrl+C to unmount.", _mountSettings.MountPoint);

            // Step 4: Block until Dokan file system is closed
            await _dokanInstance.WaitForFileSystemClosedAsync(uint.MaxValue);
        }
        catch (DllNotFoundException)
        {
            _logger.LogError("Dokany driver is not installed. ZipDrive requires Dokany to mount virtual drives");
            Console.Error.WriteLine("Error: Dokany driver is not installed.");
            Console.Error.WriteLine("ZipDrive requires Dokany to mount virtual drives.");
            Console.Error.WriteLine("Download and install from: https://github.com/dokan-dev/dokany/releases");
            WaitForKeyAndStop();
        }
        catch (DokanException ex)
        {
            _logger.LogError(ex, "Dokan mount failed. Ensure Dokany v2.1.0.1000 is installed from https://github.com/dokan-dev/dokany/releases/tag/v2.1.0.1000");
            _lifetime.StopApplication();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Mount cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during mount");
            _lifetime.StopApplication();
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Unmounting drive...");

        // Stop watcher first to prevent reloads during shutdown
        await StopWatcherAsync();

        try
        {
            if (_dokan != null)
            {
                _dokan.RemoveMountPoint(_mountSettings.MountPoint);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error removing mount point");
        }

        try
        {
            if (_vfs.IsMounted)
            {
                await _vfs.UnmountAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error unmounting VFS");
        }

        _dokanInstance?.Dispose();
        _dokan?.Dispose();

        _logger.LogInformation("Drive unmounted cleanly");

        await base.StopAsync(cancellationToken);
    }

    // === FileSystemWatcher ===

    private void StartWatcher()
    {
        try
        {
            var quietPeriod = TimeSpan.FromSeconds(
                _mountSettings.DynamicReloadQuietPeriodSeconds > 0
                    ? _mountSettings.DynamicReloadQuietPeriodSeconds
                    : 5);

            _consolidator = new ArchiveChangeConsolidator(quietPeriod, ApplyDeltaAsync, _logger);

            _watcher = new FileSystemWatcher(_mountSettings.ArchiveDirectory, "*.zip")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                IncludeSubdirectories = true,
                InternalBufferSize = 65536, // 64KB — holds ~860 events vs default 8KB
                EnableRaisingEvents = true
            };

            _watcher.Created += OnFileCreated;
            _watcher.Deleted += OnFileDeleted;
            _watcher.Renamed += OnFileRenamed;
            _watcher.Error += OnWatcherError;

            _logger.LogInformation("FileSystemWatcher started on {Dir} (quiet period: {QuietPeriod}s)",
                _mountSettings.ArchiveDirectory, quietPeriod.TotalSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start FileSystemWatcher on {Dir}. Dynamic reload disabled.",
                _mountSettings.ArchiveDirectory);
        }
    }

    private async ValueTask StopWatcherAsync()
    {
        if (_consolidator != null)
        {
            await _consolidator.DisposeAsync();
            _consolidator = null;
        }

        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }
    }

    // === Event Handlers ===

    private void OnFileCreated(object sender, FileSystemEventArgs e)
    {
        if (!IsValidZipEvent(e.FullPath)) return;

        string virtualPath = ArchivePathHelper.ToVirtualPath(_mountSettings.ArchiveDirectory, e.FullPath);
        if (!IsWithinDepthLimit(virtualPath)) return;

        _logger.LogDebug("FileSystemWatcher: Created {Path}", e.Name);
        _consolidator?.OnCreated(virtualPath);
    }

    private void OnFileDeleted(object sender, FileSystemEventArgs e)
    {
        if (!IsValidZipEvent(e.FullPath)) return;

        string virtualPath = ArchivePathHelper.ToVirtualPath(_mountSettings.ArchiveDirectory, e.FullPath);
        if (!IsWithinDepthLimit(virtualPath)) return;

        _logger.LogDebug("FileSystemWatcher: Deleted {Path}", e.Name);
        _consolidator?.OnDeleted(virtualPath);
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        bool oldIsZip = IsZipExtension(e.OldFullPath);
        bool newIsZip = IsZipExtension(e.FullPath);

        // If it's a directory rename, trigger full reconciliation
        if (Directory.Exists(e.FullPath) || (!oldIsZip && !newIsZip))
        {
            if (Directory.Exists(e.FullPath))
            {
                _logger.LogDebug("FileSystemWatcher: Directory renamed {Old} → {New}", e.OldName, e.Name);
                _ = Task.Run(FullReconciliationAsync);
            }
            return;
        }

        _logger.LogDebug("FileSystemWatcher: Renamed {Old} → {New}", e.OldName, e.Name);

        // Only emit events for .zip paths
        if (oldIsZip)
        {
            string oldVirtualPath = ArchivePathHelper.ToVirtualPath(_mountSettings.ArchiveDirectory, e.OldFullPath);
            if (IsWithinDepthLimit(oldVirtualPath))
                _consolidator?.OnDeleted(oldVirtualPath);
        }

        if (newIsZip)
        {
            string newVirtualPath = ArchivePathHelper.ToVirtualPath(_mountSettings.ArchiveDirectory, e.FullPath);
            if (IsWithinDepthLimit(newVirtualPath))
                _consolidator?.OnCreated(newVirtualPath);
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        _logger.LogWarning(e.GetException(), "FileSystemWatcher buffer overflow — running full reconciliation");
        _ = Task.Run(FullReconciliationAsync);
    }

    // === Event Filtering ===

    private static bool IsZipExtension(string path)
    {
        return path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsValidZipEvent(string fullPath)
    {
        return IsZipExtension(fullPath);
    }

    private bool IsWithinDepthLimit(string virtualPath)
    {
        int depth = virtualPath.Count(c => c == '/');
        return depth < _mountSettings.MaxDiscoveryDepth;
    }

    // === Delta Application ===

    private async Task ApplyDeltaAsync(ArchiveChangeDelta delta)
    {
        // Process removals first (frees resources)
        foreach (string virtualPath in delta.Removed)
        {
            await _archiveManager.RemoveArchiveAsync(virtualPath);
        }

        // Process modifications (remove + re-add)
        foreach (string virtualPath in delta.Modified)
        {
            await _archiveManager.RemoveArchiveAsync(virtualPath);
            await TryAddArchiveAsync(virtualPath);
        }

        // Process additions
        foreach (string virtualPath in delta.Added)
        {
            await TryAddArchiveAsync(virtualPath);
        }
    }

    private async Task TryAddArchiveAsync(string virtualPath)
    {
        string fullPath = Path.Combine(_mountSettings.ArchiveDirectory, virtualPath.Replace('/', '\\'));

        // File-readability probe with exponential backoff
        TimeSpan[] delays = [
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(30)
        ];

        for (int attempt = 0; attempt <= delays.Length; attempt++)
        {
            ArchiveDescriptor? descriptor = _discovery.DescribeFile(_mountSettings.ArchiveDirectory, fullPath);
            if (descriptor != null)
            {
                await _archiveManager.AddArchiveAsync(descriptor);
                return;
            }

            if (attempt < delays.Length)
            {
                _logger.LogDebug("File not yet readable, retrying in {Delay}s: {Path}",
                    delays[attempt].TotalSeconds, virtualPath);
                await Task.Delay(delays[attempt]);
            }
        }

        _logger.LogWarning("File still inaccessible after retries, skipping: {Path}", virtualPath);
    }

    // === Full Reconciliation ===

    private async Task FullReconciliationAsync()
    {
        _logger.LogInformation("Running full reconciliation...");

        _consolidator?.ClearPending();

        try
        {
            IReadOnlyList<ArchiveDescriptor> onDisk = await _discovery.DiscoverAsync(
                _mountSettings.ArchiveDirectory, _mountSettings.MaxDiscoveryDepth);

            var onDiskSet = onDisk.ToDictionary(a => a.VirtualPath, StringComparer.OrdinalIgnoreCase);
            var inMemorySet = _archiveManager.GetRegisteredArchives()
                .ToDictionary(a => a.VirtualPath, StringComparer.OrdinalIgnoreCase);

            // Remove archives no longer on disk
            foreach (string key in inMemorySet.Keys.Except(onDiskSet.Keys, StringComparer.OrdinalIgnoreCase))
            {
                await _archiveManager.RemoveArchiveAsync(key);
            }

            // Add archives not yet in memory
            foreach (string key in onDiskSet.Keys.Except(inMemorySet.Keys, StringComparer.OrdinalIgnoreCase))
            {
                await _archiveManager.AddArchiveAsync(onDiskSet[key]);
            }

            _logger.LogInformation("Full reconciliation complete");
        }
        catch (DirectoryNotFoundException ex)
        {
            _logger.LogError(ex, "Archive directory not found during reconciliation. Stopping watcher.");
            await StopWatcherAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during full reconciliation");
        }
    }

    // === Network Path Detection ===

    private void DetectNetworkPath()
    {
        try
        {
            string dir = _mountSettings.ArchiveDirectory;
            if (dir.StartsWith(@"\\") || dir.StartsWith("//"))
            {
                _logger.LogWarning(
                    "Archive directory is a UNC path ({Dir}). FileSystemWatcher may miss events on network paths. " +
                    "Consider a local directory for reliable dynamic reload.",
                    dir);
                return;
            }

            string? root = Path.GetPathRoot(dir);
            if (root != null && root.Length >= 2 && char.IsLetter(root[0]) && root[1] == ':')
            {
                try
                {
                    DriveInfo drive = new(root);
                    if (drive.DriveType == DriveType.Network)
                    {
                        _logger.LogWarning(
                            "Archive directory is on a network drive ({Dir}, drive type: {Type}). " +
                            "FileSystemWatcher may miss events.",
                            dir, drive.DriveType);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not determine drive type for {Root}", root);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Network path detection failed");
        }
    }

    // === Helpers ===

    private void WaitForKeyAndStop()
    {
        if (!Environment.UserInteractive || Console.IsInputRedirected)
        {
            _lifetime.StopApplication();
            return;
        }

        Console.Error.WriteLine();
        Console.Error.WriteLine("Press any key to exit...");
        try
        {
            Console.ReadKey(intercept: true);
        }
        catch (InvalidOperationException)
        {
            // Console input unavailable — continue shutdown
        }

        _lifetime.StopApplication();
    }

    /// <summary>
    /// Adapter for DokanNet's ILogger to Microsoft.Extensions.Logging.
    /// </summary>
    private sealed class DokanNetLogger : DokanNet.Logging.ILogger
    {
        private readonly ILogger _logger;

        public DokanNetLogger(ILogger logger) => _logger = logger;

        public void Debug(string message, params object[] args) => _logger.LogDebug(message, args);
        public void Info(string message, params object[] args) => _logger.LogInformation(message, args);
        public void Warn(string message, params object[] args) => _logger.LogWarning(message, args);
        public void Error(string message, params object[] args) => _logger.LogError(message, args);
        public void Fatal(string message, params object[] args) => _logger.LogCritical(message, args);

        public bool DebugEnabled => _logger.IsEnabled(LogLevel.Debug);
    }
}
