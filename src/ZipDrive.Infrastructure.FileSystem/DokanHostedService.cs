using System.Runtime.Versioning;
using DokanNet;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
    private readonly IServiceProvider _serviceProvider;
    private readonly DokanFileSystemAdapter _adapter;
    private readonly MountSettings _mountSettings;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<DokanHostedService> _logger;

    private Dokan? _dokan;
    private DokanInstance? _dokanInstance;
    private VfsScope? _currentScope;

    // FileSystemWatcher + debounce + cooldown
    private FileSystemWatcher? _watcher;
    private Timer? _debounceTimer;
    private DateTime _lastReloadUtc = DateTime.MinValue;
    private volatile bool _reloadPending;
    private readonly Lock _reloadLock = new();

    private static readonly TimeSpan DebounceDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ReloadCooldown = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(30);

    public DokanHostedService(
        IServiceProvider serviceProvider,
        DokanFileSystemAdapter adapter,
        IOptions<MountSettings> mountSettings,
        IHostApplicationLifetime lifetime,
        ILogger<DokanHostedService> logger)
    {
        _serviceProvider = serviceProvider;
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

            // Step 1: Create initial VFS scope and mount
            _currentScope = VfsScope.Create(_serviceProvider, _serviceProvider.GetRequiredService<ILogger<VfsScope>>());
            await _currentScope.Vfs.MountAsync(new VfsMountOptions
            {
                RootPath = _mountSettings.ArchiveDirectory,
                MaxDiscoveryDepth = _mountSettings.MaxDiscoveryDepth
            }, stoppingToken);

            _adapter.SetVfs(_currentScope.Vfs);

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
        StopWatcher();

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

        // Dispose current VFS scope (stops maintenance, clears caches, deletes disk cache)
        if (_currentScope != null)
        {
            try
            {
                await _currentScope.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing VFS scope");
            }
            _currentScope = null;
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
            _watcher = new FileSystemWatcher(_mountSettings.ArchiveDirectory, "*.zip")
            {
                NotifyFilter = NotifyFilters.FileName,
                IncludeSubdirectories = true,
                EnableRaisingEvents = true
            };

            _watcher.Created += OnFileChanged;
            _watcher.Deleted += OnFileChanged;
            _watcher.Renamed += OnFileRenamed;

            _logger.LogInformation("FileSystemWatcher started on {Dir}", _mountSettings.ArchiveDirectory);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start FileSystemWatcher on {Dir}. Dynamic reload disabled.",
                _mountSettings.ArchiveDirectory);
        }
    }

    private void StopWatcher()
    {
        _debounceTimer?.Dispose();
        _debounceTimer = null;

        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        _logger.LogDebug("FileSystemWatcher: {ChangeType} {Path}", e.ChangeType, e.Name);
        ScheduleReload();
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        _logger.LogDebug("FileSystemWatcher: Renamed {OldName} → {NewName}", e.OldName, e.Name);
        ScheduleReload();
    }

    // === Debounce + Cooldown ===

    private void ScheduleReload()
    {
        _reloadPending = true;

        // Reset (or create) the debounce timer — fires after 2s of quiet
        lock (_reloadLock)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(OnDebounceElapsed, null, DebounceDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnDebounceElapsed(object? state)
    {
        if (!_reloadPending)
            return;

        var now = DateTime.UtcNow;
        var timeSinceLastReload = now - _lastReloadUtc;

        if (timeSinceLastReload < ReloadCooldown)
        {
            // Cooldown not elapsed — defer reload to when cooldown expires
            var delay = ReloadCooldown - timeSinceLastReload;
            _logger.LogDebug("Reload deferred by {Delay}s due to cooldown", delay.TotalSeconds);

            lock (_reloadLock)
            {
                _debounceTimer?.Dispose();
                _debounceTimer = new Timer(OnDebounceElapsed, null, delay, Timeout.InfiniteTimeSpan);
            }
            return;
        }

        _reloadPending = false;

        // Fire reload on a background thread (don't block the timer callback)
        _ = Task.Run(ExecuteReloadAsync);
    }

    private async Task ExecuteReloadAsync()
    {
        _logger.LogInformation("Reloading VFS: archive directory changed");

        VfsScope? newScope = null;
        try
        {
            // Create new scope and mount
            newScope = VfsScope.Create(_serviceProvider, _serviceProvider.GetRequiredService<ILogger<VfsScope>>());
            await newScope.Vfs.MountAsync(new VfsMountOptions
            {
                RootPath = _mountSettings.ArchiveDirectory,
                MaxDiscoveryDepth = _mountSettings.MaxDiscoveryDepth
            });

            // Swap VFS in the adapter (drains in-flight ops first)
            var oldVfs = await _adapter.SwapAsync(newScope.Vfs, DrainTimeout);
            var oldScope = _currentScope;
            _currentScope = newScope;
            _lastReloadUtc = DateTime.UtcNow;
            newScope = null; // prevent disposal in finally

            _logger.LogInformation("VFS reload complete");

            // Dispose old scope in background
            if (oldScope != null)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await oldScope.DisposeAsync();
                        _logger.LogInformation("Old VFS scope disposed");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error disposing old VFS scope");
                    }
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "VFS reload failed. Keeping current VFS.");

            // Dispose the failed new scope
            if (newScope != null)
            {
                try { await newScope.DisposeAsync(); }
                catch (Exception disposeEx)
                {
                    _logger.LogWarning(disposeEx, "Error disposing failed VFS scope");
                }
            }
        }
    }

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
