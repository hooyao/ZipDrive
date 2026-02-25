using System.Runtime.Versioning;
using DokanNet;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZipDrive.Domain.Abstractions;
using ZipDrive.Domain.Configuration;
using ZipDrive.Domain.Models;

namespace ZipDrive.Infrastructure.FileSystem;

/// <summary>
/// Background service that manages the Dokan mount lifecycle.
/// Mounts VFS on start, unmounts on Ctrl+C / host shutdown.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DokanHostedService : BackgroundService
{
    private readonly IVirtualFileSystem _vfs;
    private readonly DokanFileSystemAdapter _adapter;
    private readonly MountSettings _mountSettings;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<DokanHostedService> _logger;

    private Dokan? _dokan;
    private DokanInstance? _dokanInstance;

    public DokanHostedService(
        IVirtualFileSystem vfs,
        DokanFileSystemAdapter adapter,
        IOptions<MountSettings> mountSettings,
        IHostApplicationLifetime lifetime,
        ILogger<DokanHostedService> logger)
    {
        _vfs = vfs;
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
                _logger.LogError("Mount:ArchiveDirectory is required. Set it in appsettings.jsonc or via command line: Mount:ArchiveDirectory=<path>");
                _lifetime.StopApplication();
                return;
            }

            // Step 1: Mount VFS (discover ZIPs, build archive trie)
            await _vfs.MountAsync(new VfsMountOptions
            {
                RootPath = _mountSettings.ArchiveDirectory,
                MaxDiscoveryDepth = _mountSettings.MaxDiscoveryDepth
            }, stoppingToken);

            _logger.LogInformation("VFS mounted: archives discovered");

            // Step 2: Create Dokan instance
            _dokan = new Dokan(new DokanNetLogger(_logger));

            var dokanBuilder = new DokanInstanceBuilder(_dokan)
                .ConfigureOptions(options =>
                {
                    options.Options = DokanOptions.WriteProtection | DokanOptions.FixedDrive;
                    options.MountPoint = _mountSettings.MountPoint;
                });

            _dokanInstance = dokanBuilder.Build(_adapter);

            _logger.LogInformation("Drive mounted at {MountPoint}. Press Ctrl+C to unmount.", _mountSettings.MountPoint);

            // Step 3: Block until Dokan file system is closed
            await _dokanInstance.WaitForFileSystemClosedAsync(uint.MaxValue);
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
