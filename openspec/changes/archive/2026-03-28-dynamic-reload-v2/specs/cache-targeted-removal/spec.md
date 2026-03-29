## ADDED Requirements

### Requirement: GenericCache.TryRemove removes entry by key
GenericCache SHALL expose a `TryRemove(string cacheKey)` method that removes a specific entry from the cache dictionary. It SHALL also be added to the `ICache<T>` interface.

#### Scenario: Remove existing entry with RefCount 0
- **WHEN** `TryRemove(key)` is called for an entry with RefCount == 0
- **THEN** it returns true, the entry is removed, CurrentSizeBytes is decremented, and storage is enqueued to _pendingCleanup

#### Scenario: Remove non-existent key
- **WHEN** `TryRemove("nonexistent")` is called
- **THEN** it returns false with no side effects

#### Scenario: Remove borrowed entry (RefCount > 0)
- **WHEN** `TryRemove(key)` is called for an entry with RefCount > 0
- **THEN** it returns true, the entry is removed from the dictionary (preventing new borrows), the entry is marked as orphaned, and storage cleanup is deferred until the last handle is disposed

#### Scenario: BorrowAsync after TryRemove is a cache miss
- **WHEN** `TryRemove(key)` is called then `BorrowAsync(key, ...)` is called
- **THEN** the factory is invoked (cache miss) and a new entry is created

### Requirement: Orphaned entry cleanup on last handle return
When a handle for an orphaned entry (removed via TryRemove while borrowed) is disposed and RefCount reaches 0, the stored entry SHALL be enqueued to _pendingCleanup for background disposal.

#### Scenario: Orphaned entry cleaned up after handle dispose
- **WHEN** TryRemove marks an entry as orphaned and the last handle is disposed
- **THEN** the entry's storage is enqueued to _pendingCleanup and ProcessPendingCleanup disposes it

### Requirement: TryRemove clears materialization tasks first
TryRemove SHALL remove the key from `_materializationTasks` BEFORE checking `_cache`. This prevents new threads from joining an in-flight materialization after removal.

#### Scenario: TryRemove during in-progress materialization
- **WHEN** `TryRemove(key)` is called while a BorrowAsync factory is executing for that key
- **THEN** TryRemove returns false (entry not yet in _cache), but the materialization task entry is removed from _materializationTasks

### Requirement: All TryRemove cleanup deferred to _pendingCleanup
TryRemove SHALL always defer storage cleanup to `_pendingCleanup` (even for RefCount == 0 entries) to avoid a TOCTOU race with BorrowAsync Layer 1.

#### Scenario: No immediate Dispose on TryRemove
- **WHEN** TryRemove succeeds for an entry with RefCount == 0
- **THEN** storage is enqueued to _pendingCleanup (not Dispose called directly)

### Requirement: ChunkedFileEntry.Dispose handles missing directory
ChunkedFileEntry.Dispose SHALL catch DirectoryNotFoundException and IOException from missing backing files. These are expected during shutdown when DeleteCacheDirectory runs before orphaned entry cleanup.

#### Scenario: Dispose after directory deleted
- **WHEN** ChunkedFileEntry.Dispose is called after the cache directory has been deleted
- **THEN** no exception is thrown (logged at Debug level)

### Requirement: IArchiveStructureStore exposes TryRemove
IArchiveStructureStore SHALL expose TryRemove delegating to the underlying GenericCache. ArchiveStructureCache.Invalidate SHALL use TryRemove instead of returning false.

#### Scenario: Invalidate removes cached structure
- **WHEN** `ArchiveStructureCache.Invalidate("game.zip")` is called for a cached archive
- **THEN** it returns true and the next GetOrBuildAsync triggers a cache miss (rebuilds from ZIP)
