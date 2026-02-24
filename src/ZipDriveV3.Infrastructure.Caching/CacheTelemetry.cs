using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ZipDriveV3.Infrastructure.Caching;

/// <summary>
/// Static telemetry definitions for the caching subsystem.
/// Uses System.Diagnostics.Metrics and System.Diagnostics.ActivitySource (no OTel dependency).
/// </summary>
internal static class CacheTelemetry
{
    internal const string MeterName = "ZipDriveV3.Caching";
    internal const string ActivitySourceName = "ZipDriveV3.Caching";

    internal static readonly Meter Meter = new(MeterName);
    internal static readonly ActivitySource Source = new(ActivitySourceName);

    // === Counters ===

    internal static readonly Counter<long> Hits =
        Meter.CreateCounter<long>("cache.hits", unit: "{hit}", description: "Number of cache hits");

    internal static readonly Counter<long> Misses =
        Meter.CreateCounter<long>("cache.misses", unit: "{miss}", description: "Number of cache misses");

    internal static readonly Counter<long> Evictions =
        Meter.CreateCounter<long>("cache.evictions", unit: "{eviction}", description: "Number of cache evictions");

    // === Histograms ===

    internal static readonly Histogram<double> MaterializationDuration =
        Meter.CreateHistogram<double>("cache.materialization.duration", unit: "ms",
            description: "Time to materialize a cache entry");

    // === Observable Gauges ===
    // These are registered per-instance via RegisterInstance/UnregisterInstance.

    private static readonly List<WeakReference<ICacheMetricsSource>> _instances = [];
    private static readonly Lock _instanceLock = new();
    private static bool _gaugesRegistered;

    /// <summary>
    /// Registers a cache instance for observable gauge reporting.
    /// Call from GenericCache constructor.
    /// </summary>
    internal static void RegisterInstance(ICacheMetricsSource instance)
    {
        lock (_instanceLock)
        {
            _instances.Add(new WeakReference<ICacheMetricsSource>(instance));
            EnsureGaugesRegistered();
        }
    }

    /// <summary>
    /// Unregisters a cache instance from observable gauge reporting.
    /// </summary>
    internal static void UnregisterInstance(ICacheMetricsSource instance)
    {
        lock (_instanceLock)
        {
            _instances.RemoveAll(wr => !wr.TryGetTarget(out var target) || ReferenceEquals(target, instance));
        }
    }

    private static void EnsureGaugesRegistered()
    {
        if (_gaugesRegistered) return;
        _gaugesRegistered = true;

        Meter.CreateObservableGauge("cache.size_bytes", ObserveSizeBytes,
            unit: "By", description: "Current cache size in bytes");

        Meter.CreateObservableGauge("cache.entry_count", ObserveEntryCount,
            unit: "{entry}", description: "Number of entries in cache");

        Meter.CreateObservableGauge("cache.utilization", ObserveUtilization,
            unit: "1", description: "Cache utilization ratio (0.0 to 1.0)");
    }

    private static IEnumerable<Measurement<long>> ObserveSizeBytes()
    {
        foreach (var source in GetAliveInstances())
        {
            yield return new Measurement<long>(
                source.CurrentSizeBytes,
                new KeyValuePair<string, object?>("tier", source.Name));
        }
    }

    private static IEnumerable<Measurement<int>> ObserveEntryCount()
    {
        foreach (var source in GetAliveInstances())
        {
            yield return new Measurement<int>(
                source.EntryCount,
                new KeyValuePair<string, object?>("tier", source.Name));
        }
    }

    private static IEnumerable<Measurement<double>> ObserveUtilization()
    {
        foreach (var source in GetAliveInstances())
        {
            double utilization = source.CapacityBytes > 0
                ? (double)source.CurrentSizeBytes / source.CapacityBytes
                : 0.0;
            yield return new Measurement<double>(
                utilization,
                new KeyValuePair<string, object?>("tier", source.Name));
        }
    }

    private static List<ICacheMetricsSource> GetAliveInstances()
    {
        lock (_instanceLock)
        {
            var alive = new List<ICacheMetricsSource>();
            _instances.RemoveAll(wr =>
            {
                if (wr.TryGetTarget(out var target))
                {
                    alive.Add(target);
                    return false;
                }
                return true; // Remove dead references
            });
            return alive;
        }
    }
}

/// <summary>
/// Interface for cache instances to expose metrics to observable gauges.
/// </summary>
internal interface ICacheMetricsSource
{
    string Name { get; }
    long CurrentSizeBytes { get; }
    long CapacityBytes { get; }
    int EntryCount { get; }
}
