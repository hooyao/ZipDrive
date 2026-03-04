namespace ZipDrive.Infrastructure.Caching;

/// <summary>
/// Configuration options for the coalescing batch reader.
/// Bound from the "Coalescing" section in appsettings.jsonc.
/// </summary>
public sealed class CoalescingOptions
{
    /// <summary>
    /// Whether coalescing is enabled. Default: true.
    /// Set to false to restore the original per-entry extraction behavior with no window delay.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How long to wait (in milliseconds) after the first cache-miss request before firing it solo,
    /// if no second request arrives. Default: 0ms (no delay).
    /// </summary>
    /// <remarks>
    /// This is the "fast path" timer. A single isolated read incurs at most this much
    /// additional latency. When a second request arrives within this window, the coordinator
    /// extends to the full <see cref="WindowMs"/> to collect the burst.
    /// Setting this to 0 dispatches immediately with no delay — effectively zero overhead
    /// for sequential access patterns (e.g., Explorer thumbnail generation).
    /// </remarks>
    public int FastPathMs { get; set; } = 0;

    /// <summary>
    /// The full coalescing window in milliseconds, used once a burst is detected.
    /// All requests arriving within this window from the first request are batched together.
    /// Default: 500ms.
    /// </summary>
    public int WindowMs { get; set; } = 500;

    /// <summary>
    /// Minimum density ratio (0.0–1.0) of useful bytes to total bytes read for a batch to be formed.
    /// A value of 0.8 means at most 20% of the sequential range may consist of hole bytes.
    /// Default: 0.8.
    /// </summary>
    public double DensityThreshold { get; set; } = 0.8;

    /// <summary>
    /// When true, hole entries (unrequested entries physically between two requested entries)
    /// are also decompressed and cached during the sequential pass.
    /// Default: false.
    /// </summary>
    public bool SpeculativeCache { get; set; } = false;
}
