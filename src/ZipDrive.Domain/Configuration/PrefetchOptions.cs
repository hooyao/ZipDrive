namespace ZipDrive.Domain.Configuration;

/// <summary>
/// Configuration options for the sibling prefetch feature.
/// Bound from the "Cache" configuration section.
/// </summary>
public sealed class PrefetchOptions
{
    /// <summary>
    /// Whether sibling prefetch is enabled. Default: <c>true</c>.
    /// </summary>
    public bool PrefetchEnabled { get; set; } = true;

    /// <summary>
    /// Whether an explicit file read (ReadFile) triggers prefetch for siblings.
    /// Default: <c>true</c>. Set to <c>false</c> to disable read-triggered prefetch
    /// while still allowing directory-listing-triggered prefetch.
    /// </summary>
    public bool PrefetchOnRead { get; set; } = true;

    /// <summary>
    /// Whether a directory listing (FindFiles) triggers prefetch in addition to individual
    /// file reads. Default: <c>true</c>. Set to <c>false</c> to only prefetch on explicit
    /// file reads, reducing eager I/O when browsing without opening files.
    /// </summary>
    public bool PrefetchOnListDirectory { get; set; } = true;

    /// <summary>
    /// Maximum uncompressed file size (in MB) for a sibling to be eligible for prefetch.
    /// Files larger than this are excluded from the prefetch plan. Default: 10 MB.
    /// </summary>
    public int PrefetchFileSizeThresholdMb { get; set; } = 10;

    /// <summary>
    /// Maximum number of files in the prefetch span (centered window). Default: 20.
    /// </summary>
    public int PrefetchMaxFiles { get; set; } = 20;

    /// <summary>
    /// Maximum number of directory entries scanned for candidates before capping. Default: 300.
    /// If the directory has more entries, the <see cref="PrefetchMaxDirectoryFiles"/> nearest
    /// by <c>LocalHeaderOffset</c> to the trigger are used.
    /// </summary>
    public int PrefetchMaxDirectoryFiles { get; set; } = 300;

    /// <summary>
    /// Minimum fill ratio (wanted compressed bytes / span bytes) required to include
    /// a multi-entry window. Default: 0.80 (80%).
    /// </summary>
    public double PrefetchFillRatioThreshold { get; set; } = 0.80;

    /// <summary>Gets the file size threshold in bytes.</summary>
    public long PrefetchFileSizeThresholdBytes => PrefetchFileSizeThresholdMb * 1024L * 1024L;
}
