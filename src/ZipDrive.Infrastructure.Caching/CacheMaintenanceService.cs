using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZipDrive.Domain.Abstractions;

namespace ZipDrive.Infrastructure.Caching;

/// <summary>
/// Background service that periodically evicts expired cache entries and processes
/// pending async cleanup (e.g., temp file deletion). Uses <see cref="CacheOptions.EvictionCheckIntervalSeconds"/>.
/// </summary>
public sealed class CacheMaintenanceService : BackgroundService
{
    private readonly DualTierFileCache _fileCache;
    private readonly IArchiveStructureCache _structureCache;
    private readonly TimeSpan _interval;
    private readonly ILogger<CacheMaintenanceService> _logger;

    public CacheMaintenanceService(
        DualTierFileCache fileCache,
        IArchiveStructureCache structureCache,
        IOptions<CacheOptions> options,
        ILogger<CacheMaintenanceService> logger)
    {
        _fileCache = fileCache;
        _structureCache = structureCache;
        _interval = options.Value.EvictionCheckInterval;
        _logger = logger;

        _logger.LogInformation(
            "CacheMaintenanceService initialized with interval={IntervalSeconds}s",
            _interval.TotalSeconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(_interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
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
                {
                    _logger.LogDebug("Processed {CleanupCount} pending cleanup items", cleaned);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during cache maintenance sweep");
            }
        }

        // Final cleanup: clear all cache entries (deletes disk tier temp files)
        try
        {
            _fileCache.Clear();
            _logger.LogInformation("Final cache cleanup completed");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during final cache cleanup");
        }

        _logger.LogInformation("CacheMaintenanceService stopped");
    }
}
