using ZipDrive.Domain.Models;

namespace ZipDrive.Application.Services;

/// <summary>
/// Describes a contiguous span of sibling ZIP entries selected for prefetch
/// in a single sequential read.
/// </summary>
internal sealed class PrefetchPlan
{
    /// <summary>
    /// The ordered list of entries to decompress and warm into cache.
    /// Sorted by <see cref="ZipEntryInfo.LocalHeaderOffset"/> ascending.
    /// </summary>
    public required IReadOnlyList<ZipEntryInfo> Entries { get; init; }

    /// <summary>
    /// The byte offset in the ZIP file where the sequential read begins
    /// (LocalHeaderOffset of the first entry).
    /// </summary>
    public required long SpanStart { get; init; }

    /// <summary>
    /// The byte offset in the ZIP file where the sequential read ends
    /// (LocalHeaderOffset + CompressedSize of the last entry, adjusted after
    /// local header parsing).
    /// </summary>
    public required long SpanEnd { get; init; }

    /// <summary>
    /// Returns true if there are no entries to prefetch.
    /// </summary>
    public bool IsEmpty => Entries.Count == 0;
}
