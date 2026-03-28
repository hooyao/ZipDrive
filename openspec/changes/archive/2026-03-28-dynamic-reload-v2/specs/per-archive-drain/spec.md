## ADDED Requirements

### Requirement: ArchiveNode tracks in-flight operations
Each registered archive SHALL have an ArchiveNode that tracks the count of in-flight VFS operations via atomic TryEnter/Exit. TryEnter SHALL return false if the node is draining.

#### Scenario: TryEnter succeeds when not draining
- **WHEN** `TryEnter()` is called on a non-draining node
- **THEN** it returns true and ActiveOps is incremented by 1

#### Scenario: TryEnter rejected during drain
- **WHEN** `DrainAsync` has been called and `TryEnter()` is called
- **THEN** it returns false and ActiveOps is unchanged

#### Scenario: Exit decrements ActiveOps
- **WHEN** `Exit()` is called after a successful `TryEnter()`
- **THEN** ActiveOps is decremented by 1

#### Scenario: Exit without matching TryEnter triggers debug assertion
- **WHEN** `Exit()` is called on a node with ActiveOps == 0
- **THEN** a `Debug.Assert` fires (ActiveOps went negative)

### Requirement: DrainAsync waits for in-flight operations
DrainAsync SHALL set the draining flag, cancel the DrainToken, and wait until all in-flight operations complete or the timeout expires.

#### Scenario: Drain with no active operations completes immediately
- **WHEN** `DrainAsync(5s)` is called with ActiveOps == 0
- **THEN** it completes in < 100ms and IsDraining is true

#### Scenario: Drain waits for active operations
- **WHEN** `DrainAsync(5s)` is called with ActiveOps == 2 and both operations subsequently call Exit()
- **THEN** drain completes after the second Exit() call

#### Scenario: Drain timeout
- **WHEN** `DrainAsync(100ms)` is called with ActiveOps == 1 and the operation never exits
- **THEN** drain returns after ~100ms with ActiveOps still == 1

#### Scenario: Double-drain reuses existing drain
- **WHEN** `DrainAsync` is called twice on the same node
- **THEN** the second call awaits the existing drain (does not overwrite the TaskCompletionSource)

### Requirement: DrainToken cancels in-flight prefetch
ArchiveNode SHALL expose a `DrainToken` (CancellationToken) that is cancelled when DrainAsync is called. Fire-and-forget operations (prefetch) SHALL use this token.

#### Scenario: Prefetch cancelled on drain
- **WHEN** a prefetch is in progress using DrainToken and DrainAsync is called
- **THEN** the prefetch receives OperationCanceledException and exits

### Requirement: ArchiveGuard disposable struct
An ArchiveGuard struct SHALL encapsulate TryEnter/Exit to eliminate boilerplate. It SHALL implement IDisposable so `using` enforces Exit.

#### Scenario: ArchiveGuard usage
- **WHEN** `ArchiveGuard.TryEnter(nodes, key, out guard)` succeeds and the guard is disposed
- **THEN** `TryEnter()` was called on entry and `Exit()` was called on dispose

### Requirement: Prefetch participates in drain guard
Fire-and-forget prefetch operations SHALL call TryEnter/Exit on the ArchiveNode. If draining, prefetch SHALL bail out immediately.

#### Scenario: Prefetch enters archive guard
- **WHEN** prefetch starts for an archive and the archive is not draining
- **THEN** ActiveOps is incremented, and DrainAsync waits for prefetch to complete before proceeding
