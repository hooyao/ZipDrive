using ZipDrive.Domain.Models;

namespace ZipDrive.Application.Services;

/// <summary>
/// Pure span selection algorithm for sibling prefetch.
/// Selects a contiguous window of sibling entries around a trigger file,
/// maximising useful bytes relative to span bytes (fill ratio).
/// </summary>
internal static class SpanSelector
{
    /// <summary>
    /// Selects the optimal prefetch span from a set of candidates.
    /// </summary>
    /// <param name="candidates">
    /// All eligible sibling entries (already filtered: not dir, not trigger,
    /// size within threshold). Need not be sorted.
    /// </param>
    /// <param name="trigger">The entry being read that triggers prefetch.</param>
    /// <param name="maxFiles">Maximum entries to include in the span.</param>
    /// <param name="fillRatioThreshold">
    /// Minimum ratio of useful compressed bytes to total span bytes (0.0–1.0).
    /// </param>
    /// <returns>A <see cref="PrefetchPlan"/> — may be empty if no viable span found.</returns>
    internal static PrefetchPlan Select(
        IReadOnlyList<ZipEntryInfo> candidates,
        ZipEntryInfo trigger,
        int maxFiles,
        double fillRatioThreshold)
    {
        if (candidates.Count == 0 || maxFiles <= 0)
            return Empty();

        // Step 1: Sort all candidates by offset, then build a unified sorted list
        // that includes the trigger so we can find its position.
        List<ZipEntryInfo> sorted = [.. candidates.OrderBy(e => e.LocalHeaderOffset)];

        // Step 2: Find trigger insertion point to center the window.
        int triggerIdx = FindInsertionIndex(sorted, trigger.LocalHeaderOffset);

        // Step 3: Take a centered window of maxFiles around the trigger position.
        List<ZipEntryInfo> window = TakeCenteredWindow(sorted, triggerIdx, maxFiles);

        if (window.Count == 0)
            return Empty();

        // Step 4: Shrink window from endpoints until fill ratio >= threshold or only 1 entry.
        while (window.Count > 1)
        {
            double ratio = ComputeFillRatio(window);
            if (ratio >= fillRatioThreshold)
                break;

            // Remove the endpoint whose removal most improves the fill ratio.
            // Try removing first vs last; keep the better result.
            double ratioWithoutFirst = window.Count > 1 ? ComputeFillRatio(window, skipFirst: true) : 0;
            double ratioWithoutLast  = window.Count > 1 ? ComputeFillRatio(window, skipLast: true)  : 0;

            if (ratioWithoutFirst >= ratioWithoutLast)
                window.RemoveAt(0);
            else
                window.RemoveAt(window.Count - 1);
        }

        if (window.Count == 0)
            return Empty();

        // SpanEnd is approximate (before local header parsing); the actual read
        // will parse local headers to determine exact data positions.
        long spanStart = window[0].LocalHeaderOffset;
        long spanEnd   = window[^1].LocalHeaderOffset + window[^1].CompressedSize;

        return new PrefetchPlan
        {
            Entries   = window,
            SpanStart = spanStart,
            SpanEnd   = spanEnd
        };
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static PrefetchPlan Empty() => new()
    {
        Entries   = [],
        SpanStart = 0,
        SpanEnd   = 0
    };

    /// <summary>
    /// Returns the index at which <paramref name="offset"/> would be inserted
    /// to keep <paramref name="sorted"/> ordered.
    /// </summary>
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

    /// <summary>
    /// Takes up to <paramref name="maxFiles"/> entries centered around
    /// <paramref name="centerIdx"/> from <paramref name="sorted"/>.
    /// </summary>
    private static List<ZipEntryInfo> TakeCenteredWindow(
        List<ZipEntryInfo> sorted, int centerIdx, int maxFiles)
    {
        int count = sorted.Count;
        if (count == 0) return [];

        // Clamp center to valid range
        centerIdx = Math.Clamp(centerIdx, 0, count - 1);

        int half  = maxFiles / 2;
        int start = Math.Max(0, centerIdx - half);
        int end   = Math.Min(count, start + maxFiles);

        // Slide start back if we hit the right edge
        start = Math.Max(0, end - maxFiles);

        return sorted.GetRange(start, end - start);
    }

    /// <summary>
    /// Computes fill ratio = sum(CompressedSize of all entries) / span bytes.
    /// Optionally skips first or last entry for shrink-candidate evaluation.
    /// </summary>
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
