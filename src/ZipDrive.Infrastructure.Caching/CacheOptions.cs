namespace ZipDrive.Infrastructure.Caching;

/// <summary>
/// Configuration options for the dual-tier file cache.
/// </summary>
/// <remarks>
/// These options are typically bound from appsettings.jsonc under the "Cache" section.
/// All size values can be tuned based on available system resources.
/// </remarks>
public class CacheOptions
{
    /// <summary>
    /// Gets or sets the memory tier capacity in megabytes.
    /// Default: 2048 MB (2 GB).
    /// </summary>
    /// <remarks>
    /// Adjust based on available system RAM. Lower values for memory-constrained systems,
    /// higher values for systems with abundant RAM.
    /// </remarks>
    public int MemoryCacheSizeMb { get; set; } = 2048;

    /// <summary>
    /// Gets or sets the disk tier capacity in megabytes.
    /// Default: 10240 MB (10 GB).
    /// </summary>
    /// <remarks>
    /// Adjust based on available disk space. SSDs can handle larger cache sizes effectively.
    /// Ensure sufficient free space in the temp directory.
    /// </remarks>
    public int DiskCacheSizeMb { get; set; } = 10240;

    /// <summary>
    /// Gets or sets the size cutoff in megabytes for routing between tiers.
    /// Files smaller than this value go to memory tier, files equal or larger go to disk tier.
    /// Default: 50 MB.
    /// </summary>
    /// <remarks>
    /// Tuning guidelines:
    /// <list type="bullet">
    /// <item><description>Lower cutoff (e.g., 20 MB): More files on disk, conserve RAM</description></item>
    /// <item><description>Higher cutoff (e.g., 100 MB): More files in memory, faster access</description></item>
    /// </list>
    /// </remarks>
    public int SmallFileCutoffMb { get; set; } = 50;

    /// <summary>
    /// Gets or sets the temporary directory for disk tier cache files.
    /// Default: null (uses system temp directory).
    /// </summary>
    /// <remarks>
    /// <para>
    /// If specified, must have sufficient space for <see cref="DiskCacheSizeMb"/>.
    /// </para>
    /// <para>
    /// Recommended: Use a dedicated SSD partition for optimal performance.
    /// </para>
    /// </remarks>
    public string? TempDirectory { get; set; }

    /// <summary>
    /// Gets or sets the default time-to-live for cache entries in minutes.
    /// Default: 30 minutes.
    /// </summary>
    /// <remarks>
    /// Entries are automatically evicted after this duration to prevent stale data
    /// and unbounded growth. Can be overridden per-entry in GetOrAddAsync().
    /// </remarks>
    public int DefaultTtlMinutes { get; set; } = 30;

    /// <summary>
    /// Gets or sets the interval for periodic eviction checks in seconds.
    /// Default: 60 seconds.
    /// </summary>
    /// <remarks>
    /// The disk tier uses a background timer to clean up expired entries and
    /// process pending deletions. Lower values provide more frequent cleanup
    /// but higher CPU overhead.
    /// </remarks>
    public int EvictionCheckIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Gets the memory tier capacity in bytes (computed property).
    /// </summary>
    internal long MemoryCacheSizeBytes => MemoryCacheSizeMb * 1024L * 1024L;

    /// <summary>
    /// Gets the disk tier capacity in bytes (computed property).
    /// </summary>
    internal long DiskCacheSizeBytes => DiskCacheSizeMb * 1024L * 1024L;

    /// <summary>
    /// Gets the size cutoff in bytes (computed property).
    /// </summary>
    internal long SmallFileCutoffBytes => SmallFileCutoffMb * 1024L * 1024L;

    /// <summary>
    /// Gets the default TTL as a TimeSpan (computed property).
    /// </summary>
    public TimeSpan DefaultTtl => TimeSpan.FromMinutes(DefaultTtlMinutes);

    /// <summary>
    /// Gets or sets the chunk size in megabytes for incremental disk-tier extraction.
    /// Tradeoff: smaller = lower first-byte latency, more TCS overhead.
    /// Default: 10 MB.
    /// </summary>
    /// <remarks>
    /// At 10 MB chunk size, a 5 GB file produces 500 chunks with ~40 KB of TCS overhead.
    /// First-byte latency is approximately ChunkSizeMb / 200 seconds at ~200 MB/s decompression speed.
    /// </remarks>
    public int ChunkSizeMb { get; set; } = 10;

    /// <summary>
    /// Gets the chunk size in bytes (computed property).
    /// </summary>
    internal int ChunkSizeBytes => ChunkSizeMb * 1024 * 1024;

    /// <summary>
    /// Gets the eviction check interval as a TimeSpan (computed property).
    /// </summary>
    public TimeSpan EvictionCheckInterval => TimeSpan.FromSeconds(EvictionCheckIntervalSeconds);
}
