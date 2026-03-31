namespace ZipDrive.Infrastructure.Archives.Zip;

/// <summary>
/// Pure span selection algorithm for sibling prefetch.
/// Selects a contiguous window of sibling entries around a trigger file,
/// maximising useful bytes relative to span bytes (fill ratio).
///
/// Uses ZipEntryInfo (LocalHeaderOffset, CompressedSize) from ZipFormatMetadataStore
/// — this is a ZIP-specific optimization.
/// </summary>
internal static class SpanSelector
{
    internal static ZipPrefetchPlan Select(
        IReadOnlyList<ZipEntryInfo> candidates,
        ZipEntryInfo trigger,
        int maxFiles,
        double fillRatioThreshold)
    {
        if (candidates.Count == 0 || maxFiles <= 0)
            return Empty();

        List<ZipEntryInfo> sorted = [.. candidates.OrderBy(e => e.LocalHeaderOffset)];
        int triggerIdx = FindInsertionIndex(sorted, trigger.LocalHeaderOffset);
        List<ZipEntryInfo> window = TakeCenteredWindow(sorted, triggerIdx, maxFiles);

        if (window.Count == 0)
            return Empty();

        while (window.Count > 1)
        {
            double ratio = ComputeFillRatio(window);
            if (ratio >= fillRatioThreshold)
                break;

            double ratioWithoutFirst = window.Count > 1 ? ComputeFillRatio(window, skipFirst: true) : 0;
            double ratioWithoutLast  = window.Count > 1 ? ComputeFillRatio(window, skipLast: true)  : 0;

            if (ratioWithoutFirst >= ratioWithoutLast)
                window.RemoveAt(0);
            else
                window.RemoveAt(window.Count - 1);
        }

        if (window.Count == 0)
            return Empty();

        long spanStart = window[0].LocalHeaderOffset;
        long spanEnd   = window[^1].LocalHeaderOffset + window[^1].CompressedSize;

        return new ZipPrefetchPlan
        {
            Entries   = window,
            SpanStart = spanStart,
            SpanEnd   = spanEnd
        };
    }

    private static ZipPrefetchPlan Empty() => new()
    {
        Entries   = [],
        SpanStart = 0,
        SpanEnd   = 0
    };

    private static int FindInsertionIndex(List<ZipEntryInfo> sorted, long offset)
    {
        int lo = 0, hi = sorted.Count;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (sorted[mid].LocalHeaderOffset < offset)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }

    private static List<ZipEntryInfo> TakeCenteredWindow(
        List<ZipEntryInfo> sorted, int centerIdx, int maxFiles)
    {
        int count = sorted.Count;
        if (count == 0) return [];

        centerIdx = Math.Clamp(centerIdx, 0, count - 1);

        int half  = maxFiles / 2;
        int start = Math.Max(0, centerIdx - half);
        int end   = Math.Min(count, start + maxFiles);

        start = Math.Max(0, end - maxFiles);

        return sorted.GetRange(start, end - start);
    }

    private static double ComputeFillRatio(
        List<ZipEntryInfo> window,
        bool skipFirst = false,
        bool skipLast  = false)
    {
        int startIdx = skipFirst ? 1 : 0;
        int endIdx   = skipLast  ? window.Count - 2 : window.Count - 1;

        if (startIdx > endIdx)
            return 0.0;

        long usefulBytes = 0;
        for (int i = startIdx; i <= endIdx; i++)
            usefulBytes += window[i].CompressedSize;

        long spanStart = window[startIdx].LocalHeaderOffset;
        long spanEnd   = window[endIdx].LocalHeaderOffset + window[endIdx].CompressedSize;
        long spanBytes = spanEnd - spanStart;

        return spanBytes <= 0 ? 1.0 : (double)usefulBytes / spanBytes;
    }
}
