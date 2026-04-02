namespace ZipDrive.Infrastructure.Archives.Zip;

/// <summary>
/// Describes a contiguous span of sibling ZIP entries selected for prefetch
/// in a single sequential read. ZIP-specific optimization.
/// </summary>
internal sealed class ZipPrefetchPlan
{
    /// <summary>
    /// The ordered list of entries to decompress and warm into cache.
    /// Sorted by LocalHeaderOffset ascending.
    /// </summary>
    public required IReadOnlyList<ZipEntryInfo> Entries { get; init; }

    /// <summary>
    /// The byte offset in the ZIP file where the sequential read begins.
    /// </summary>
    public required long SpanStart { get; init; }

    /// <summary>
    /// The byte offset in the ZIP file where the sequential read ends.
    /// </summary>
    public required long SpanEnd { get; init; }

    /// <summary>Returns true if there are no entries to prefetch.</summary>
    public bool IsEmpty => Entries.Count == 0;
}
