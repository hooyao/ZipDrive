namespace ZipDrive.Domain.Configuration;

/// <summary>
/// Configuration options for the sibling prefetch feature.
/// Bound from the "Cache:Prefetch" configuration subsection.
/// </summary>
public sealed class PrefetchOptions
{
    /// <summary>
    /// Master switch for sibling prefetch. Default: <c>false</c>.
    /// Disabled by default because some applications (file managers, indexers, antivirus)
    /// enumerate every directory on mount, which can trigger massive prefetch I/O and
    /// overflow the cache. Enable explicitly when your workload benefits from it.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Whether an explicit file read (ReadFile) triggers prefetch for siblings.
    /// Default: <c>true</c>. Only takes effect when <see cref="Enabled"/> is <c>true</c>.
    /// This is the recommended trigger — it only fires on actual file opens.
    /// </summary>
    public bool OnRead { get; set; } = true;

    /// <summary>
    /// Whether a directory listing (FindFiles) triggers prefetch in addition to individual
    /// file reads. Default: <c>false</c>. Recommended to keep disabled even when
    /// <see cref="Enabled"/> is on — image viewers like FastStone issue directory
    /// listings to all sibling folders, which prefetches files across unrelated directories.
    /// Only takes effect when <see cref="Enabled"/> is <c>true</c>.
    /// </summary>
    public bool OnListDirectory { get; set; } = false;

    /// <summary>
    /// Maximum uncompressed file size (in MB) for a sibling to be eligible for prefetch.
    /// Files larger than this are excluded from the prefetch plan. Default: 10 MB.
    /// </summary>
    public int FileSizeThresholdMb { get; set; } = 10;

    /// <summary>
    /// Maximum number of files in the prefetch span (centered window). Default: 20.
    /// </summary>
    public int MaxFiles { get; set; } = 20;

    /// <summary>
    /// Maximum number of directory entries scanned for candidates before capping. Default: 300.
    /// If the directory has more entries, the <see cref="MaxDirectoryFiles"/> nearest
    /// by <c>LocalHeaderOffset</c> to the trigger are used.
    /// </summary>
    public int MaxDirectoryFiles { get; set; } = 300;

    /// <summary>
    /// Minimum fill ratio (wanted compressed bytes / span bytes) required to include
    /// a multi-entry window. Default: 0.80 (80%).
    /// </summary>
    public double FillRatioThreshold { get; set; } = 0.80;

    /// <summary>Gets the file size threshold in bytes.</summary>
    public long FileSizeThresholdBytes => FileSizeThresholdMb * 1024L * 1024L;
}
