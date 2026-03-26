using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZipDrive.Domain.Abstractions;
using ZipDrive.Infrastructure.Caching;

namespace ZipDrive.Infrastructure.FileSystem;

/// <summary>
/// Owns a complete VFS instance graph (trie, caches, VFS) plus a per-scope maintenance timer.
/// Created per reload cycle; disposed when swapped out.
/// </summary>
public sealed class VfsScope : IAsyncDisposable
{
    private readonly IServiceScope _scope;
    private readonly IFileContentCache _fileCache;
    private readonly IArchiveStructureCache _structureCache;
    private readonly PeriodicTimer _maintenanceTimer;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _maintenanceLoop;
    private readonly ILogger<VfsScope> _logger;
    private bool _disposed;

    public IVirtualFileSystem Vfs { get; }

    public VfsScope(
        IServiceScope scope,
        IVirtualFileSystem vfs,
        IFileContentCache fileCache,
        IArchiveStructureCache structureCache,
        TimeSpan maintenanceInterval,
        ILogger<VfsScope> logger)
    {
        _scope = scope;
        Vfs = vfs;
        _fileCache = fileCache;
        _structureCache = structureCache;
        _logger = logger;
        _maintenanceTimer = new PeriodicTimer(maintenanceInterval);
        _maintenanceLoop = RunMaintenanceAsync(_cts.Token);
    }

    /// <summary>
    /// Creates a VfsScope from a DI scope. Resolves all scoped services and starts maintenance.
    /// Call <see cref="IVirtualFileSystem.MountAsync"/> on <see cref="Vfs"/> after construction.
    /// </summary>
    public static VfsScope Create(IServiceProvider rootProvider, ILogger<VfsScope> logger)
    {
        var scope = rootProvider.CreateScope();
        var sp = scope.ServiceProvider;

        var vfs = sp.GetRequiredService<IVirtualFileSystem>();
        var fileCache = sp.GetRequiredService<IFileContentCache>();
        var structureCache = sp.GetRequiredService<IArchiveStructureCache>();
        var cacheOptions = sp.GetRequiredService<IOptions<CacheOptions>>();

        return new VfsScope(
            scope, vfs, fileCache, structureCache,
            cacheOptions.Value.EvictionCheckInterval, logger);
    }

    private async Task RunMaintenanceAsync(CancellationToken ct)
    {
        _logger.LogDebug("VfsScope maintenance loop started");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _maintenanceTimer.WaitForNextTickAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                _fileCache.EvictExpired();
                _structureCache.EvictExpired();

                int cleaned = _fileCache.ProcessPendingCleanup();
                if (cleaned > 0)
                    _logger.LogDebug("VfsScope maintenance: processed {CleanupCount} cleanup items", cleaned);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during VfsScope maintenance sweep");
            }
        }
        _logger.LogDebug("VfsScope maintenance loop stopped");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        _logger.LogInformation("Disposing VfsScope...");

        // 1. Stop maintenance timer
        await _cts.CancelAsync();
        try { await _maintenanceLoop; }
        catch (OperationCanceledException) { }
        _maintenanceTimer.Dispose();
        _cts.Dispose();

        // 2. Unmount VFS
        try
        {
            if (Vfs.IsMounted)
                await Vfs.UnmountAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error unmounting VFS during scope disposal");
        }

        // 3. Clear caches
        try
        {
            _fileCache.Clear();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error clearing file content cache during scope disposal");
        }

        // 4. Delete disk cache directory
        try
        {
            _fileCache.DeleteCacheDirectory();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error deleting cache directory during scope disposal");
        }

        // 5. Dispose DI scope
        _scope.Dispose();

        _logger.LogInformation("VfsScope disposed");
    }
}
