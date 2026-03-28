## ADDED Requirements

### Requirement: Event consolidation with quiet period
The ArchiveChangeConsolidator SHALL queue FileSystemWatcher events and flush a net delta after a configurable quiet period of silence. The quiet period timer SHALL reset on each incoming event.

#### Scenario: Single Created event after quiet period
- **WHEN** a `Created("game.zip")` event arrives and no further events arrive for the quiet period duration
- **THEN** the flush callback receives `delta = { Added: ["game.zip"] }`

#### Scenario: Single Deleted event after quiet period
- **WHEN** a `Deleted("game.zip")` event arrives and no further events arrive for the quiet period
- **THEN** the flush callback receives `delta = { Removed: ["game.zip"] }`

### Requirement: Consolidation state machine
The consolidator SHALL apply the following state transitions to compute net deltas per file path:

| Current State | + Event | → New State |
|---|---|---|
| (none) | Created | Added |
| (none) | Deleted | Removed |
| Added | Deleted | Noop |
| Removed | Created | Modified |
| Modified | Deleted | Removed |
| Noop | Created | Added |
| Noop | Deleted | Removed |

Renamed events SHALL be decomposed into `Deleted(oldPath) + Created(newPath)`.

#### Scenario: Created then Deleted within window produces Noop
- **WHEN** `Created("temp.zip")` followed by `Deleted("temp.zip")` within the quiet period
- **THEN** the flush callback is NOT invoked (or receives an empty delta)

#### Scenario: Deleted then Created within window produces Modified
- **WHEN** `Deleted("data.zip")` followed by `Created("data.zip")` within the quiet period
- **THEN** the flush callback receives `delta = { Modified: ["data.zip"] }`

#### Scenario: Modified then Deleted within window produces Removed
- **WHEN** `Deleted("a.zip")`, `Created("a.zip")`, then `Deleted("a.zip")` within the quiet period
- **THEN** the flush callback receives `delta = { Removed: ["a.zip"] }`

#### Scenario: Renamed event
- **WHEN** `Renamed("old.zip", "new.zip")` arrives
- **THEN** the flush callback receives `delta = { Added: ["new.zip"], Removed: ["old.zip"] }`

### Requirement: Atomic flush via dictionary swap
The consolidator SHALL use `Interlocked.Exchange` to atomically swap the pending dictionary during flush. Events arriving during flush processing SHALL land in the new dictionary and be processed by the next flush cycle.

#### Scenario: Event during flush processing is not lost
- **WHEN** a flush is in progress and a new `Created("late.zip")` event arrives
- **THEN** the event is captured in the next flush cycle, not lost

### Requirement: Burst debounce
The consolidator SHALL handle event bursts by resetting the quiet period timer on each event. A single flush SHALL occur after the burst subsides.

#### Scenario: 10 events in rapid succession
- **WHEN** 10 `Created` events arrive 100ms apart (1s total burst) and then silence for the quiet period
- **THEN** exactly one flush occurs containing all 10 archives as Added

### Requirement: TimeProvider injection
The consolidator SHALL accept a `TimeProvider` parameter for testability. Tests SHALL use `FakeTimeProvider` to control time advancement without real delays.

#### Scenario: Test with FakeTimeProvider
- **WHEN** the consolidator is constructed with `FakeTimeProvider` and events are submitted
- **THEN** flush timing is controlled by advancing the FakeTimeProvider, not by wall-clock time

### Requirement: ClearPending for reconciliation
The consolidator SHALL expose a `ClearPending()` method that atomically discards all pending events. This SHALL be called before full reconciliation to prevent stale pre-overflow events from re-applying.

#### Scenario: ClearPending before reconciliation
- **WHEN** `ClearPending()` is called while events are pending
- **THEN** all pending events are discarded and the next flush produces an empty delta

### Requirement: Graceful disposal
The consolidator SHALL await any in-flight timer callback before completing disposal. Events arriving after disposal SHALL be silently accepted (no exception) but never flushed.

#### Scenario: Event after Dispose
- **WHEN** the consolidator is disposed and `OnCreated("late.zip")` is called
- **THEN** no exception is thrown and no flush callback is invoked
