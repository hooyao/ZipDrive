using FluentAssertions;
using ZipDrive.Application.Services;
using ZipDrive.Domain.Models;

namespace ZipDrive.Domain.Tests;

public sealed class SpanSelectorTests
{
    // ══════════════════════════════════════════════════════════════════
    // Helpers
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates a ZipEntryInfo with a given offset and compressed size.
    /// Entries are placed contiguously by default (no gaps) unless you set offset explicitly.
    /// </summary>
    private static ZipEntryInfo Entry(long offset, long compressedSize) => new()
    {
        LocalHeaderOffset = offset,
        CompressedSize = compressedSize,
        UncompressedSize = compressedSize,
        CompressionMethod = 0,
        IsDirectory = false,
        LastModified = DateTime.UtcNow,
        Attributes = FileAttributes.Normal
    };

    /// <summary>
    /// Builds a list of entries placed back-to-back starting at offset 0,
    /// each with the given size.
    /// </summary>
    private static List<ZipEntryInfo> DenseEntries(int count, long size = 1000)
    {
        var list = new List<ZipEntryInfo>(count);
        long offset = 0;
        for (int i = 0; i < count; i++)
        {
            list.Add(Entry(offset, size));
            offset += size;
        }
        return list;
    }

    // ══════════════════════════════════════════════════════════════════
    // Dense sibling selection
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void Select_DenseSiblings_AllIncludedUpToMaxFiles()
    {
        // 5 tightly-packed entries of equal size → fill ratio = 1.0 → all included
        List<ZipEntryInfo> entries = DenseEntries(5, size: 1000);
        ZipEntryInfo trigger = entries[2]; // center
        List<ZipEntryInfo> candidates = entries.Where(e => e.LocalHeaderOffset != trigger.LocalHeaderOffset).ToList();

        PrefetchPlan plan = SpanSelector.Select(candidates, trigger, maxFiles: 10, fillRatioThreshold: 0.80);

        plan.IsEmpty.Should().BeFalse();
        plan.Entries.Count.Should().Be(4); // all 4 non-trigger siblings
    }

    [Fact]
    public void Select_DenseSiblings_CappedAtMaxFiles()
    {
        // 10 tight entries, max 4 → only 4 selected
        List<ZipEntryInfo> entries = DenseEntries(10, size: 500);
        ZipEntryInfo trigger = entries[5];
        List<ZipEntryInfo> candidates = entries.Where(e => e.LocalHeaderOffset != trigger.LocalHeaderOffset).ToList();

        PrefetchPlan plan = SpanSelector.Select(candidates, trigger, maxFiles: 4, fillRatioThreshold: 0.80);

        plan.Entries.Count.Should().BeLessThanOrEqualTo(4);
        plan.IsEmpty.Should().BeFalse();
    }

    // ══════════════════════════════════════════════════════════════════
    // Hole (sparse span) shrinking
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void Select_LargeHoleEndpoint_RemovedToMeetFillRatio()
    {
        // Layout: [A(100), B(100), <gap=8000>, C(100)]
        // Trigger is C. Without shrinking: fill = (100+100+100)/(100+100+8000+100) = 300/8300 ≈ 3.6% → below 0.80
        // After removing A (widening endpoint): fill = (100+100)/(100+8000+100) ≈ 2.4% still bad
        // After removing A and shrinking further, eventually only B+C remain: fill = 200/(8100) still bad
        // Until only C remains (1 entry): returned as-is
        // Actually: candidates = [A, B], trigger = C.
        // Sorted by offset: [A(0), B(100), C(8200)].
        // With trigger at offset 8200, candidates at 0 and 100.
        // Centered window of 3: [A, B, C_placeholder] → after exclusion of trigger from candidates, window = [A, B]
        // fill([A,B]) = (100+100)/(100+100) = 1.0 → fine. So let's engineer a real hole case.
        //
        // Better: [A(0,100), <hole(100,8000)>, B(8100,100), C(8200,100)]
        // Candidates=[A,hole_entry,B]. Trigger=B.
        // We model holes as entries that are NOT in candidates (candidates are siblings we WANT).
        // Span = continuous region including ALL entries (wanted and holes).
        // SpanSelector only sees wanted candidates; span = from first to last wanted entry in window.
        // Holes are implicit: SpanEnd - SpanStart - sum(wantedSizes) inferred by caller.
        //
        // Scenario: want [A, C], hole between them is implicit.
        // A is at offset 0, size 100. C is at offset 8100, size 100.
        // Span = [0 .. 8200], spanBytes = 8200, useful = 200. fill = 200/8200 ≈ 2.4% → below 80%.
        // SpanSelector should remove the worst endpoint. With only 2 entries, after removing one, window=1, loop stops.

        ZipEntryInfo a = Entry(offset: 0, compressedSize: 100);
        ZipEntryInfo c = Entry(offset: 8100, compressedSize: 100);
        ZipEntryInfo trigger = Entry(offset: 4000, compressedSize: 100); // trigger in the middle

        // Candidates are a and c (not trigger); span a-c has fill=200/8200 < 80%
        var candidates = new List<ZipEntryInfo> { a, c };

        PrefetchPlan plan = SpanSelector.Select(candidates, trigger, maxFiles: 10, fillRatioThreshold: 0.80);

        // Span a..c has 2.4% fill ratio. After shrinking: one of a or c is removed.
        // The remaining window will be a single entry, which is fine (loop stops at count=1).
        plan.Entries.Count.Should().Be(1);
    }

    [Fact]
    public void Select_TightCluster_MeetsThresholdWithoutShrinking()
    {
        // [A(0,500), B(500,500), C(1000,500)] — candidates=[A,C], trigger=B sits at offset 500.
        // Span A..C = [0..1500], useful=1000, fill=1000/1500≈66.7%.
        // Threshold of 0.65 is below 66.7% so no shrinking needed → both A and C remain.
        ZipEntryInfo a = Entry(0, 500);
        ZipEntryInfo b = Entry(500, 500);
        ZipEntryInfo c = Entry(1000, 500);
        ZipEntryInfo trigger = b;
        var candidates = new List<ZipEntryInfo> { a, c };

        PrefetchPlan plan = SpanSelector.Select(candidates, trigger, maxFiles: 10, fillRatioThreshold: 0.65);

        plan.Entries.Count.Should().Be(2);
        plan.Entries.Should().Contain(a).And.Contain(c);
    }

    // ══════════════════════════════════════════════════════════════════
    // MaxFiles centering
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void Select_WindowCenteredOnTrigger()
    {
        // 9 dense entries [0..8]. Trigger = entry[4] (offset 4000).
        // maxFiles=4 → centered window around trigger (offset 4000) in the sorted candidates list.
        List<ZipEntryInfo> entries = DenseEntries(9, size: 1000);
        ZipEntryInfo trigger = entries[4];
        List<ZipEntryInfo> candidates = entries.Where(e => e.LocalHeaderOffset != trigger.LocalHeaderOffset).ToList();
        // candidates: offsets 0,1000,2000,3000,5000,6000,7000,8000

        PrefetchPlan plan = SpanSelector.Select(candidates, trigger, maxFiles: 4, fillRatioThreshold: 0.0);

        // Trigger at offset 4000 inserts between offset 3000 and 5000 in the sorted candidates list.
        // Centered window of 4 around that insertion point should contain offsets near 4000.
        plan.Entries.Count.Should().BeLessThanOrEqualTo(4);
        // The plan should not reach the extremes (offset 0 or 8000) when maxFiles=4 and we're centered at 4.
        plan.Entries.Should().NotContain(entries[0]); // offset 0 is too far
        plan.Entries.Should().NotContain(entries[8]); // offset 8000 is too far
    }

    // ══════════════════════════════════════════════════════════════════
    // Edge cases
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void Select_NoCandidates_ReturnsEmpty()
    {
        ZipEntryInfo trigger = Entry(0, 100);
        PrefetchPlan plan = SpanSelector.Select([], trigger, maxFiles: 10, fillRatioThreshold: 0.80);
        plan.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Select_MaxFilesZero_ReturnsEmpty()
    {
        var candidates = new List<ZipEntryInfo> { Entry(0, 100), Entry(100, 100) };
        ZipEntryInfo trigger = Entry(200, 100);
        PrefetchPlan plan = SpanSelector.Select(candidates, trigger, maxFiles: 0, fillRatioThreshold: 0.80);
        plan.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Select_SingleCandidate_ReturnedRegardlessOfFillRatio()
    {
        // Single candidate with fill ratio < threshold → loop stops at count=1, still returned
        ZipEntryInfo a = Entry(0, 10);
        ZipEntryInfo trigger = Entry(100_000, 10); // far away
        var candidates = new List<ZipEntryInfo> { a };

        PrefetchPlan plan = SpanSelector.Select(candidates, trigger, maxFiles: 10, fillRatioThreshold: 0.99);

        plan.IsEmpty.Should().BeFalse();
        plan.Entries.Count.Should().Be(1);
    }

    [Fact]
    public void Select_SpanBounds_CorrectlyCalculated()
    {
        // [A(0,100), B(200,100)] — gap of 100 between them
        ZipEntryInfo a = Entry(0, 100);
        ZipEntryInfo b = Entry(200, 100);
        ZipEntryInfo trigger = Entry(500, 50); // trigger outside span
        var candidates = new List<ZipEntryInfo> { a, b };

        // fill = 200 / (200+100) ≈ 66.7% — below 80%, so one entry removed. Two-entry window shrinks to 1.
        PrefetchPlan plan = SpanSelector.Select(candidates, trigger, maxFiles: 10, fillRatioThreshold: 0.80);

        plan.Entries.Count.Should().Be(1);
        plan.SpanStart.Should().Be(plan.Entries[0].LocalHeaderOffset);
        plan.SpanEnd.Should().Be(plan.Entries[0].LocalHeaderOffset + plan.Entries[0].CompressedSize);
    }

    [Fact]
    public void Select_SpanBounds_TightEntries_SpanEnd_EqualsLastEntryEnd()
    {
        ZipEntryInfo a = Entry(0, 400);
        ZipEntryInfo b = Entry(400, 400);
        ZipEntryInfo trigger = Entry(1000, 100);
        var candidates = new List<ZipEntryInfo> { a, b };

        PrefetchPlan plan = SpanSelector.Select(candidates, trigger, maxFiles: 10, fillRatioThreshold: 0.80);

        plan.Entries.Count.Should().Be(2);
        plan.SpanStart.Should().Be(0);
        plan.SpanEnd.Should().Be(800); // 400 + 400
    }
}
