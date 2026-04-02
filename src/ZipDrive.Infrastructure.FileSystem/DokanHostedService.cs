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
    private readonly IFormatRegistry _formatRegistry;
    private readonly ILogger<DokanHostedService> _logger;

    private Dokan? _dokan;
    private DokanInstance? _dokanInstance;
    private FileSystemWatcher? _watcher;
    private ArchiveChangeConsolidator? _consolidator;
    private CancellationToken _stoppingToken;

    public DokanHostedService(
        IVirtualFileSystem vfs,
        IArchiveManager archiveManager,
        IArchiveDiscovery discovery,
        DokanFileSystemAdapter adapter,
        IOptions<MountSettings> mountSettings,
        IFormatRegistry formatRegistry,
        IHostApplicationLifetime lifetime,
        ILogger<DokanHostedService> logger)
    {
        _vfs = vfs;
        _archiveManager = archiveManager;
        _discovery = discovery;
        _adapter = adapter;
        _mountSettings = mountSettings.Value;
        _formatRegistry = formatRegistry;
        _lifetime = lifetime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;
        try
        {
            _logger.LogInformation("Starting ZipDrive VFS...");
            _logger.LogInformation("Archive path: {Path}", _mountSettings.ArchiveDirectory);
            _logger.LogInformation("Mount point: {Mount}", _mountSettings.MountPoint);

            string archivePath = _mountSettings.ArchiveDirectory;
            string supportedFormats = string.Join(", ", _formatRegistry.SupportedExtensions
                .Select(e => e.TrimStart('.').ToUpperInvariant() + $" ({e})"));

            // Validation: empty path
            if (string.IsNullOrWhiteSpace(archivePath))
            {
                _logger.LogError("Mount:ArchiveDirectory is required");
                UserNotice.Error(
                    "No archive path specified.\n" +
                    "Drag a ZIP/RAR file or a folder onto ZipDrive.exe,\n" +
                    "or set Mount:ArchiveDirectory in appsettings.jsonc.");
                WaitForKeyAndStop();
                return;
            }

            if (File.Exists(archivePath))
            {
                // Single-file mode — pre-check format before attempting mount
                string? detectedFormat = _formatRegistry.DetectFormat(archivePath);
                if (detectedFormat == null)
                {
                    string ext = Path.GetExtension(archivePath);
                    string filename = Path.GetFileName(archivePath);
                    _logger.LogError("Unsupported archive format: {File} ({Extension})", filename, ext);
                    UserNotice.Error(
                        $"Cannot mount \"{filename}\"\n" +
                        $"File type \"{ext}\" is not a supported archive format.\n" +
                        $"Supported formats: {supportedFormats}\n\n" +
                        "Tip: Drag a ZIP or RAR file, or a folder containing them.");
                    WaitForKeyAndStop();
                    return;
                }

                bool mounted = await _vfs.MountSingleFileAsync(archivePath, stoppingToken);
                if (!mounted)
                {
                    string filename = Path.GetFileName(archivePath);
                    _logger.LogError("Failed to mount archive: {File}", filename);
                    UserNotice.Error(
                        $"Cannot mount \"{filename}\"\n" +
                        "The file could not be accessed. It may be locked or unreadable.");
                    WaitForKeyAndStop();
                    return;
                }

                _logger.LogInformation("VFS mounted: single archive");

                UserNotice.Tip(
                    "You mounted a single archive file.\n" +
                    "Drag a FOLDER onto ZipDrive.exe to mount all archives inside it at once!");
            }
            else if (Directory.Exists(archivePath))
            {
                // Directory mode (existing behavior)
                DetectNetworkPath();

                await _vfs.MountAsync(new VfsMountOptions
                {
                    RootPath = archivePath,
                    MaxDiscoveryDepth = _mountSettings.MaxDiscoveryDepth
                }, stoppingToken);

                _logger.LogInformation("VFS mounted: archives discovered");

                // Warn if directory has no supported archives
                if (!_archiveManager.GetRegisteredArchives().Any())
                {
                    string dirName = Path.GetFileName(archivePath.TrimEnd(
                        Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? "";
                    if (string.IsNullOrEmpty(dirName))
                        dirName = archivePath;
                    UserNotice.Warning(
                        $"No supported archives found in \"{dirName}\"\n" +
                        $"Supported formats: {supportedFormats}\n" +
                        "The drive is mounted but empty. Add ZIP or RAR files and they will appear automatically.");
                }

                StartWatcher();
            }
            else
            {
                // Path not found
                _logger.LogError("Archive path does not exist: {Path}", archivePath);
                UserNotice.Error($"Path not found: {archivePath}");
                WaitForKeyAndStop();
                return;
            }

            // Create Dokan instance
            _dokan = new Dokan(new DokanNetLogger(_logger));

            var dokanBuilder = new DokanInstanceBuilder(_dokan)
                .ConfigureOptions(options =>
                {
                    options.Options = DokanOptions.WriteProtection | DokanOptions.FixedDrive | DokanOptions.MountManager;
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
            UserNotice.Error(
                "Dokany driver is not installed.\n" +
                "ZipDrive requires Dokany to mount virtual drives.\n" +
                "Download and install from: https://github.com/dokan-dev/dokany/releases");
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

            // FileSystemWatcher supports only a single glob pattern. Using "*" and filtering
            // in event handlers via IsSupportedArchive() is the only way to watch multiple
            // extensions (.zip, .rar, etc.) without creating multiple watchers.
            _watcher = new FileSystemWatcher(_mountSettings.ArchiveDirectory, "*")
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
        if (!IsSupportedArchive(e.FullPath)) return;

        if (!TryGetVirtualPath(e.FullPath, out string? virtualPath)) return;
        if (!IsWithinDepthLimit(virtualPath)) return;

        _logger.LogDebug("FileSystemWatcher: Created {Path}", e.Name);
        _consolidator?.OnCreated(virtualPath);
    }

    private void OnFileDeleted(object sender, FileSystemEventArgs e)
    {
        if (!IsSupportedArchive(e.FullPath)) return;

        if (!TryGetVirtualPath(e.FullPath, out string? virtualPath)) return;
        if (!IsWithinDepthLimit(virtualPath)) return;

        _logger.LogDebug("FileSystemWatcher: Deleted {Path}", e.Name);
        _consolidator?.OnDeleted(virtualPath);
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        bool oldIsSupported = IsSupportedArchive(e.OldFullPath);
        bool newIsSupported = IsSupportedArchive(e.FullPath);

        // If it's a directory rename, trigger full reconciliation
        if (Directory.Exists(e.FullPath) || (!oldIsSupported && !newIsSupported))
        {
            if (Directory.Exists(e.FullPath))
            {
                _logger.LogDebug("FileSystemWatcher: Directory renamed {Old} → {New}", e.OldName, e.Name);
                _ = Task.Run(FullReconciliationAsync).ContinueWith(
                    t => _logger.LogError(t.Exception, "Reconciliation faulted"),
                    TaskContinuationOptions.OnlyOnFaulted);
            }
            return;
        }

        _logger.LogDebug("FileSystemWatcher: Renamed {Old} → {New}", e.OldName, e.Name);

        // Only emit events for supported archive paths
        if (oldIsSupported && TryGetVirtualPath(e.OldFullPath, out string? oldVirtualPath))
        {
            if (IsWithinDepthLimit(oldVirtualPath))
                _consolidator?.OnDeleted(oldVirtualPath);
        }

        if (newIsSupported && TryGetVirtualPath(e.FullPath, out string? newVirtualPath))
        {
            if (IsWithinDepthLimit(newVirtualPath))
                _consolidator?.OnCreated(newVirtualPath);
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        _logger.LogWarning(e.GetException(), "FileSystemWatcher buffer overflow — running full reconciliation");
        _ = Task.Run(FullReconciliationAsync).ContinueWith(
                    t => _logger.LogError(t.Exception, "Reconciliation faulted"),
                    TaskContinuationOptions.OnlyOnFaulted);
    }

    // === Event Filtering ===

    private bool IsSupportedArchive(string path)
    {
        string ext = Path.GetExtension(path);
        return !string.IsNullOrEmpty(ext) &&
               _formatRegistry.SupportedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    private bool TryGetVirtualPath(string fullPath, out string virtualPath)
    {
        try
        {
            virtualPath = ArchivePathHelper.ToVirtualPath(_mountSettings.ArchiveDirectory, fullPath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to compute virtual path for {Path}", fullPath);
            virtualPath = "";
            return false;
        }
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

        // File-readability probe with exponential backoff (capped at ~10s total
        // to avoid blocking the flush callback for too long)
        TimeSpan[] delays = [
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)
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
                await Task.Delay(delays[attempt], _stoppingToken);
            }
        }

        _logger.LogWarning("File still inaccessible after retries, skipping (will be picked up by next reconciliation): {Path}", virtualPath);
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
