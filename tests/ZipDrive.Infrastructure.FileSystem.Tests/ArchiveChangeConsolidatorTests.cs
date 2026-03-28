using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ZipDrive.Infrastructure.FileSystem;

namespace ZipDrive.Infrastructure.FileSystem.Tests;

/// <summary>
/// Tests for ArchiveChangeConsolidator — event batching and consolidation state machine.
/// Covers TC-CC-01 through TC-CC-07, TC-EDGE-07, TC-EDGE-10.
/// Uses ForceFlushAsync for deterministic testing (no real timers).
/// </summary>
public class ArchiveChangeConsolidatorTests : IAsyncDisposable
{
    private readonly List<ArchiveChangeDelta> _flushedDeltas = new();
    private readonly ArchiveChangeConsolidator _consolidator;

    public ArchiveChangeConsolidatorTests()
    {
        _consolidator = new ArchiveChangeConsolidator(
            TimeSpan.FromHours(1), // Long period — we flush manually via ForceFlushAsync
            delta =>
            {
                _flushedDeltas.Add(delta);
                return Task.CompletedTask;
            },
            NullLogger.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _consolidator.DisposeAsync();
    }

    // TC-CC-01: Single Created event
    [Fact]
    public async Task OnCreated_SingleEvent_ProducesAdded()
    {
        _consolidator.OnCreated("game.zip");
        await _consolidator.ForceFlushAsync();

        _flushedDeltas.Should().HaveCount(1);
        _flushedDeltas[0].Added.Should().ContainSingle("game.zip");
        _flushedDeltas[0].Removed.Should().BeEmpty();
        _flushedDeltas[0].Modified.Should().BeEmpty();
    }

    // TC-CC-02: Single Deleted event
    [Fact]
    public async Task OnDeleted_SingleEvent_ProducesRemoved()
    {
        _consolidator.OnDeleted("game.zip");
        await _consolidator.ForceFlushAsync();

        _flushedDeltas.Should().HaveCount(1);
        _flushedDeltas[0].Removed.Should().ContainSingle("game.zip");
    }

    // TC-CC-03: Created then Deleted = Noop
    [Fact]
    public async Task Created_ThenDeleted_ProducesNoop()
    {
        _consolidator.OnCreated("temp.zip");
        _consolidator.OnDeleted("temp.zip");
        await _consolidator.ForceFlushAsync();

        // Noop entries are filtered — delta should be empty (no callback or empty)
        _flushedDeltas.Should().BeEmpty("Created+Deleted cancels out to Noop");
    }

    // TC-CC-04: Deleted then Created = Modified
    [Fact]
    public async Task Deleted_ThenCreated_ProducesModified()
    {
        _consolidator.OnDeleted("data.zip");
        _consolidator.OnCreated("data.zip");
        await _consolidator.ForceFlushAsync();

        _flushedDeltas.Should().HaveCount(1);
        _flushedDeltas[0].Modified.Should().ContainSingle("data.zip");
        _flushedDeltas[0].Added.Should().BeEmpty();
        _flushedDeltas[0].Removed.Should().BeEmpty();
    }

    // TC-CC-05: Renamed event
    [Fact]
    public async Task OnRenamed_ProducesRemovedAndAdded()
    {
        _consolidator.OnRenamed("old.zip", "new.zip");
        await _consolidator.ForceFlushAsync();

        _flushedDeltas.Should().HaveCount(1);
        _flushedDeltas[0].Removed.Should().ContainSingle("old.zip");
        _flushedDeltas[0].Added.Should().ContainSingle("new.zip");
    }

    // TC-CC-06: Burst of events — all consolidated into one flush
    [Fact]
    public async Task BurstOfEvents_ConsolidatedIntoOneDelta()
    {
        for (int i = 0; i < 10; i++)
            _consolidator.OnCreated($"archive{i}.zip");

        await _consolidator.ForceFlushAsync();

        _flushedDeltas.Should().HaveCount(1);
        _flushedDeltas[0].Added.Should().HaveCount(10);
    }

    // TC-CC-07: Events after flush are independent
    [Fact]
    public async Task EventsAfterFlush_IndependentBatch()
    {
        _consolidator.OnCreated("batch1.zip");
        await _consolidator.ForceFlushAsync();

        _consolidator.OnCreated("batch2.zip");
        await _consolidator.ForceFlushAsync();

        _flushedDeltas.Should().HaveCount(2);
        _flushedDeltas[0].Added.Should().ContainSingle("batch1.zip");
        _flushedDeltas[1].Added.Should().ContainSingle("batch2.zip");
    }

    // TC-EDGE-07: Modified then Deleted = Removed
    [Fact]
    public async Task Modified_ThenDeleted_ProducesRemoved()
    {
        _consolidator.OnDeleted("a.zip");   // → Removed
        _consolidator.OnCreated("a.zip");   // → Modified
        _consolidator.OnDeleted("a.zip");   // → Removed
        await _consolidator.ForceFlushAsync();

        _flushedDeltas.Should().HaveCount(1);
        _flushedDeltas[0].Removed.Should().ContainSingle("a.zip");
        _flushedDeltas[0].Added.Should().BeEmpty();
        _flushedDeltas[0].Modified.Should().BeEmpty();
    }

    // TC-EDGE-10: Events after Dispose — no exception, no flush
    [Fact]
    public async Task EventsAfterDispose_SilentlyDropped()
    {
        await _consolidator.DisposeAsync();

        var act = () => _consolidator.OnCreated("late.zip");
        act.Should().NotThrow();

        _flushedDeltas.Should().BeEmpty();
    }

    // ClearPending discards all events
    [Fact]
    public async Task ClearPending_DiscardsAllEvents()
    {
        _consolidator.OnCreated("a.zip");
        _consolidator.OnDeleted("b.zip");
        _consolidator.ClearPending();
        await _consolidator.ForceFlushAsync();

        _flushedDeltas.Should().BeEmpty("ClearPending should discard all pending events");
    }
}
