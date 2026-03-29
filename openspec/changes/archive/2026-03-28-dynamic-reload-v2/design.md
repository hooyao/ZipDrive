## Context

ZipDrive mounts ZIP archives as a Windows drive via DokanNet. The archive directory can contain hundreds of ZIPs. The current system discovers archives once at startup and requires a restart for changes. An experimental branch (VfsScope) proved dynamic reload is feasible but its "nuke everything" approach is unacceptable — it destroys warm caches (2GB memory + 10GB disk) for all archives on any single change.

The full design is documented in `src/Docs/DYNAMIC_RELOAD_DESIGN.md` (v2.0). This artifact summarizes the key architectural decisions.

## Goals / Non-Goals

**Goals:**
- Surgical per-archive add/remove: only the affected archive's data is touched
- Warm cache preservation: unchanged archives keep all cached data
- Zero disruption to unaffected archives during add/remove
- Clean drain on removal: in-flight operations complete before cleanup
- Watcher noise resilience: rapid events consolidated into net deltas
- Buffer overflow recovery via full reconciliation scan
- All services remain Singleton (no scoped DI, no VfsScope)

**Non-Goals:**
- Per-archive cache capacity limits or eviction policies (global limits apply)
- Write support for archives
- Hot-reload of archive content (treated as remove+add via "Modified" path)
- Real-time responsiveness (5s configurable quiet period is acceptable)

## Decisions

### D1: Shared cache with per-archive key index (not per-archive cache instances)

**Choice:** One shared `GenericCache<Stream>` per tier (memory, disk), with a `ConcurrentDictionary<string, HashSet<string>>` index mapping `archiveKey → {cacheKeys}` inside `FileContentCache`.

**Alternatives considered:**
- **Per-archive cache instances:** Clean removal semantics, but creates thousands of cache instances, each needing its own maintenance timer. Capacity enforcement becomes per-archive instead of global (wrong — we want one 2GB pool, not 1000 x 2MB pools).
- **Prefix scan on ConcurrentDictionary:** No secondary index needed, but O(N) scan over entire cache on every removal.

**Rationale:** Shared cache gives one timer, one eviction sweep, global capacity enforcement. The key index is ~100 bytes per cached file — negligible overhead. Removal iterates a small HashSet rather than scanning the entire cache.

### D2: ReaderWriterLockSlim for ArchiveTrie (not lock-free or immutable swap)

**Choice:** `ReaderWriterLockSlim` wrapping all trie read/write operations.

**Alternatives considered:**
- **Immutable + swap:** Rebuild entire trie on mutation, `Interlocked.Exchange`. Correct but O(N) rebuild cost per add/remove.
- **ConcurrentDictionary at top level:** Loses trie prefix-match capability needed for `Resolve`.
- **No locks (KTrie thread safety):** KTrie `TrieDictionary` has no internal synchronization; concurrent read+write corrupts node pointers.

**Rationale:** Read lock is ~20ns with zero writer contention (the common case). Writes happen at most once per consolidation window (5s). `ListFolder` must materialize to `List<>` inside the lock to prevent lazy enumeration outside the lock scope.

### D3: ArchiveNode per-archive drain (not global drain)

**Choice:** Each archive has an `ArchiveNode` with atomic `TryEnter`/`Exit` and `DrainAsync`. Removal drains only the affected archive.

**Alternatives considered:**
- **Global drain (VfsScope approach):** Blocks ALL operations. Simple but unnecessarily disruptive.
- **No drain (immediate removal):** Risk of data corruption — in-flight reads may access disposed cache entries.

**Rationale:** The double-check pattern in `TryEnter` (check → increment → re-check) is proven correct (used in the experimental branch at adapter level). Per-archive scope means unaffected archives see zero disruption. `DrainToken` (`CancellationTokenSource`) cancels in-flight prefetch for the draining archive.

### D4: IArchiveManager separate from IVirtualFileSystem (ISP)

**Choice:** New `IArchiveManager` interface for `AddArchiveAsync`, `RemoveArchiveAsync`, `GetRegisteredArchives`. `ZipVirtualFileSystem` implements both interfaces.

**Alternatives considered:**
- **Add methods to IVirtualFileSystem:** Simpler but conflates file system operations (used by adapter) with archive lifecycle (used by hosted service). Violates ISP and makes mocking harder.

**Rationale:** DokanFileSystemAdapter depends only on `IVirtualFileSystem`. DokanHostedService depends on `IArchiveManager`. Clean separation of consumer profiles.

### D5: Atomic flush via Interlocked.Exchange (not ToArray + TryRemove)

**Choice:** Consolidator's `FlushAsync` atomically swaps `_pending` with a fresh `ConcurrentDictionary` via `Interlocked.Exchange`.

**Alternatives considered:**
- **ToArray then TryRemove each key:** Events arriving between snapshot and removal are silently lost.

**Rationale:** Atomic swap guarantees zero event loss. Events during flush processing land in the new dictionary and are picked up by the next cycle.

### D6: All cleanup deferred to _pendingCleanup queue (not immediate Dispose)

**Choice:** `TryRemove` with `RefCount == 0` enqueues to `_pendingCleanup` instead of calling `Dispose` directly. Orphaned entries also enqueue on last handle return.

**Alternatives considered:**
- **Immediate Dispose for RefCount==0:** Narrow TOCTOU race with `BorrowAsync` Layer 1 — thread gets reference via `TryGetValue`, `TryRemove` disposes storage, thread calls `Retrieve` on disposed storage.

**Rationale:** Deferring to `_pendingCleanup` provides a time window for any accidental borrow to increment RefCount. `ProcessPendingCleanup` (called by existing `CacheMaintenanceService`) handles actual disposal. Cost: negligible delay in cleanup.

### D7: File-readability probe with exponential backoff for Added events

**Choice:** Before calling `AddArchiveAsync`, probe the file with `FileShare.Read | FileShare.Delete`. On failure, retry with backoff (1s, 2s, 4s, 8s, 16s, 30s — max 6 retries).

**Rationale:** Windows `FileSystemWatcher` fires `Created` before copy completes. Enterprise AV holds exclusive locks for 0.5-5s. Without the probe, `IZipReader.ReadEocdAsync` fails on half-copied or locked files.

## Risks / Trade-offs

- **[Stale key-index entries]** → Minor (~100 bytes) leak when read races with RemoveArchive. Expires via TTL.
- **[Drain timeout]** → After 30s, proceed anyway. DrainToken cancels prefetch. Orphaned handles clean up on disposal.
- **[Watcher buffer overflow]** → 64KB buffer (vs 8KB default). Full reconciliation as fallback.
- **[Network path silent notification loss]** → Detect at startup, log warning. Optional periodic reconciliation.
- **[RWLock contention]** → Write ops are rare (max 1 per 5s window). ~20ns read lock overhead is negligible.
- **[Ghost entries from in-flight materialization]** → Safe due to drain-first ordering. Debug.Assert verifies invariant.
