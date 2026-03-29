# ZipDrive - Per-Archive Dynamic Reload Design Document

**Version:** 3.0
**Date:** 2026-03-29
**Status:** Implemented
**Author:** Claude Code

---

## Executive Summary

**The Problem**: ZipDrive currently requires a restart when ZIP archives are added to or removed from the archive directory. An earlier experimental approach (`VfsScope`) solved this by tearing down the entire VFS and rebuilding from scratch on any change. This nukes warm caches for all archives — a 2GB memory cache and 10GB disk cache are discarded because one ZIP was added.

**The Solution**: Per-archive granular add/remove with shared cache infrastructure. When a ZIP appears, we add one trie entry. When a ZIP disappears, we drain in-flight operations for *that archive only*, remove its trie entry, and clean up only its cache entries. All other archives continue serving from warm caches with zero disruption.

**Design Principle**: Shared caches (one memory tier, one disk tier, one structure cache) stay alive for the process lifetime. Per-archive tracking is a lightweight indexing layer on top — not separate cache instances.

**Related Documents**:
- [`CACHING_DESIGN.md`](CACHING_DESIGN.md) — GenericCache, storage strategies, borrow/return
- [`CONCURRENCY_STRATEGY.md`](CONCURRENCY_STRATEGY.md) — Three-layer concurrency + RefCount
- [`ZIP_STRUCTURE_CACHE_DESIGN.md`](ZIP_STRUCTURE_CACHE_DESIGN.md) — Archive structure caching

---

## Table of Contents

1. [Problem Statement](#1-problem-statement)
2. [Design Goals and Non-Goals](#2-design-goals-and-non-goals)
3. [Architecture Overview](#3-architecture-overview)
4. [GenericCache.TryRemove — Targeted Entry Removal](#4-genericcachetryremove--targeted-entry-removal)
5. [FileContentCache — Per-Archive Key Registry](#5-filecontentcache--per-archive-key-registry)
6. [ArchiveStructureCache.Invalidate — Make It Work](#6-archivestructurecacheinvalidate--make-it-work)
7. [ArchiveTrie — Thread-Safe Read/Write](#7-archivetrie--thread-safe-readwrite)
8. [ArchiveNode — Per-Archive Ref Count and Drain](#8-archivenode--per-archive-ref-count-and-drain)
9. [ZipVirtualFileSystem — AddArchive / RemoveArchive](#9-zipvirtualfilesystem--addarchive--removearchive)
10. [ArchiveChangeConsolidator — Event Batching](#10-archivechangeconsolidator--event-batching)
11. [DokanHostedService — FileSystemWatcher Integration](#11-dokanhostedservice--filesystemwatcher-integration)
12. [Lifecycle Walkthroughs](#12-lifecycle-walkthroughs)
13. [What Does NOT Change](#13-what-does-not-change)
14. [Test Cases](#14-test-cases)
15. [Implementation Phases](#15-implementation-phases)
16. [Risks and Mitigations](#16-risks-and-mitigations)

---

## 1. Problem Statement

### 1.1 Why VfsScope Was Unsatisfying

The VfsScope approach (experimental branch) worked as follows:

```
FileSystemWatcher detects *.zip change
  → Create new DI scope (new trie, new caches, new VFS)
  → Drain ALL in-flight Dokan operations (global drain, up to 30s)
  → Atomically swap VFS reference in adapter
  → Dispose old scope (clears ALL caches, deletes disk cache directory)
```

Problems:
- **Cache destruction**: Adding one 1KB ZIP file invalidates 12GB of warm cache data.
- **Global disruption**: ALL Dokan operations are blocked during drain — even for unaffected archives.
- **Rebuild cost**: Structure caches for all archives must be rebuilt on first access after reload.
- **DI complexity**: Required Singleton/Scoped split, VfsScope lifecycle management, adapter VFS swapping.

### 1.2 What We Want

```
FileSystemWatcher detects "newgame.zip" added
  → Add one trie entry (microseconds)
  → Done. Structure cache builds lazily on first access.
  → All other archives: zero disruption, warm caches intact.

FileSystemWatcher detects "oldgame.zip" deleted
  → Drain only oldgame.zip's in-flight operations
  → Remove its trie entry
  → Remove its structure cache entry
  → Remove its file content cache entries
  → All other archives: zero disruption.
```

---

## 2. Design Goals and Non-Goals

### Goals

- **Surgical add/remove**: Only the affected archive is touched.
- **Warm cache preservation**: Unchanged archives keep all cached data.
- **Zero disruption to unaffected archives**: Reads to other archives proceed without blocking.
- **Clean drain on removal**: In-flight operations for a removed archive complete before cleanup.
- **Watcher noise resilience**: Rapid events are consolidated (add+delete = no-op).
- **Buffer overflow recovery**: Full reconciliation scan as fallback.
- **Singleton DI**: No scoped services, no VfsScope, no adapter swapping.

### Non-Goals

- Per-archive cache capacity limits (global limits still apply).
- Per-archive eviction policies (global LRU still applies).
- Write support for archives.
- Hot-reload of archive *content* (if a ZIP's bytes change in-place, that requires structure cache invalidation + content cache flush — treated as remove+add via the "Modified" consolidation path).

---

## 3. Architecture Overview

### 3.1 Component Diagram

```
┌──────────────────────────────────────────────────────────────────────────┐
│                          DokanHostedService                              │
│  ┌──────────────────┐    ┌─────────────────────────────────────┐         │
│  │ FileSystemWatcher │───▶│ ArchiveChangeConsolidator            │         │
│  │  *.zip create     │    │  Queue events, consolidate every 5s │         │
│  │  *.zip delete     │    │  Add+Delete = Noop                  │         │
│  │  *.zip rename     │    │  Delete+Add = Modified              │         │
│  └──────────────────┘    └───────────────┬─────────────────────┘         │
└──────────────────────────────────────────┼───────────────────────────────┘
                                           │ ArchiveChangeDelta
                                           ▼
┌──────────────────────────────────────────────────────────────────────────┐
│                       ZipVirtualFileSystem                                │
│                                                                          │
│  AddArchiveAsync(descriptor)     RemoveArchiveAsync(archiveKey)          │
│    │                               │                                     │
│    ├─ archiveTrie.AddArchive()     ├─ archiveNode.DrainAsync(30s)        │
│    └─ archiveNodes[key] = new      ├─ archiveTrie.RemoveArchive()        │
│                                    ├─ structureCache.Invalidate()        │
│                                    ├─ fileContentCache.RemoveArchive()   │
│                                    └─ archiveNodes.TryRemove()           │
│                                                                          │
│  ReadFileAsync / ListDirectoryAsync / GetFileInfoAsync:                  │
│    archiveNode.TryEnter() ──▶ do work ──▶ archiveNode.Exit()            │
│    (returns false if draining → VfsFileNotFoundException)                 │
│                                                                          │
│  ┌─────────────────────────────────────────────────────┐                 │
│  │ ConcurrentDictionary<string, ArchiveNode>           │                 │
│  │   "game.zip" → ArchiveNode { ActiveOps=3 }         │                 │
│  │   "data.zip" → ArchiveNode { ActiveOps=0 }         │                 │
│  └─────────────────────────────────────────────────────┘                 │
└──────────────────────────────────────────────────────────────────────────┘
         │                       │                       │
         ▼                       ▼                       ▼
┌─────────────────┐  ┌───────────────────┐  ┌───────────────────────────┐
│  ArchiveTrie    │  │ ArchiveStructure  │  │ FileContentCache          │
│  (RWLock)       │  │ Cache             │  │                           │
│                 │  │                   │  │ ┌───────────────────────┐ │
│ AddArchive()    │  │ GetOrBuildAsync() │  │ │ Per-Archive Key Index │ │
│ RemoveArchive() │  │ Invalidate()      │  │ │ "game.zip" → {k1,k2} │ │
│ Resolve()       │  │   (now works via  │  │ │ "data.zip" → {k3}    │ │
│                 │  │    TryRemove)     │  │ └───────────────────────┘ │
│ Read: shared    │  │                   │  │                           │
│ Write: exclusive│  │                   │  │ RemoveArchive("game.zip") │
│                 │  │                   │  │   → TryRemove(k1)        │
│                 │  │                   │  │   → TryRemove(k2)        │
└─────────────────┘  └───────────────────┘  └───────────────────────────┘
                                                      │
                                            ┌─────────┴──────────┐
                                            ▼                    ▼
                                    ┌──────────────┐    ┌──────────────┐
                                    │ GenericCache  │    │ GenericCache  │
                                    │ (memory tier) │    │ (disk tier)   │
                                    │               │    │               │
                                    │ + TryRemove() │    │ + TryRemove() │
                                    └──────────────┘    └──────────────┘
```

### 3.2 Key Principle: Shared Cache, Per-Archive Index

The cache infrastructure is **shared**:
- One `GenericCache<Stream>` for memory tier (2GB capacity)
- One `GenericCache<Stream>` for disk tier (10GB capacity)
- One `GenericCache<ArchiveStructure>` for structure cache (256MB)
- One `CacheMaintenanceService` sweeping all caches periodically
- Global LRU eviction, global capacity enforcement

Per-archive tracking is a **lightweight index** inside `FileContentCache`:
- `ConcurrentDictionary<string, HashSet<string>>` (with a `Lock` for HashSet mutations) mapping `archiveKey → {cacheKeys}`
- Populated on every cache insert (ReadAsync, WarmAsync)
- Used only during `RemoveArchive()` to find which keys to remove
- Cost: ~100 bytes per cached file (one string reference in the set)
- HashSet gives O(1) deduplication — no unbounded growth from evict/re-cache cycles

---

## 4. GenericCache.TryRemove — Targeted Entry Removal

### 4.1 The Problem

`GenericCache<T>` has no way to remove a specific entry by key. It only supports:
- `EvictExpired()` — removes all expired entries with `RefCount == 0`
- `Clear()` — removes everything (shutdown only)

We need `TryRemove(key)` to support per-archive cleanup.

### 4.2 Design

**File:** `src/ZipDrive.Infrastructure.Caching/GenericCache.cs`

```csharp
/// <summary>
/// Removes a specific entry by key.
/// If the entry is currently borrowed (RefCount > 0), it is removed from the cache
/// dictionary immediately (preventing new borrows), but storage cleanup is deferred
/// until the last handle is returned.
///
/// INVARIANT: In the RemoveArchive flow, the caller must ensure no materializations
/// are in progress for this key. This is guaranteed by DrainAsync + trie removal
/// before cache cleanup. A debug assertion verifies this at runtime.
///
/// Size accounting note: _currentSizeBytes is decremented immediately even for
/// borrowed entries. Physical storage persists until the last handle returns, so
/// actual usage temporarily exceeds reported size. This matches the existing
/// undercount-safe invariant (see MaterializeAndCacheAsync).
/// </summary>
/// <returns>True if the entry was found and removed from the cache dictionary.</returns>
public bool TryRemove(string cacheKey)
{
    // Remove materialization task FIRST — prevents new threads from joining
    // an in-flight materialization via GetOrAdd after we remove from _cache.
    // Does NOT cancel an already-executing factory, but the drain-first ordering
    // in RemoveArchiveAsync guarantees no materializations are in progress.
    _materializationTasks.TryRemove(cacheKey, out _);

    if (!_cache.TryRemove(cacheKey, out CacheEntry? removed))
        return false;

    Debug.Assert(
        !_materializationTasks.ContainsKey(cacheKey),
        $"TryRemove({cacheKey}): materialization task still present — drain-first invariant violated");

    long size = removed.Stored.SizeBytes;
    Interlocked.Add(ref _currentSizeBytes, -size);

    CacheTelemetry.Evictions.Add(1,
        _tierTag,
        new KeyValuePair<string, object?>("reason", "removed"));

    // Always defer cleanup to _pendingCleanup — avoids a narrow TOCTOU race where
    // BorrowAsync Layer 1 gets a reference via TryGetValue, then TryRemove disposes
    // storage, then BorrowAsync calls Retrieve on disposed storage. By deferring to
    // the cleanup queue, there's a time window for any accidental borrow to increment
    // RefCount before ProcessPendingCleanup runs. For the common case (drain-first
    // ordering), this is simply a negligible delay in cleanup.
    if (removed.RefCount == 0)
    {
        _pendingCleanup.Enqueue(removed.Stored);
    }
    else
    {
        // Active borrows exist — mark as orphaned.
        // Cleanup happens in Return() when last handle is disposed.
        removed.MarkOrphaned();
    }

    _logger.LogInformation("Removed: {Key} ({SizeBytes} bytes, {Tier} tier, RefCount={RefCount})",
        cacheKey, size, _name, removed.RefCount);

    return true;
}
```

### 4.3 Orphaned Entry Cleanup

Add to `CacheEntry`:
```csharp
private volatile bool _orphaned;
public bool IsOrphaned => _orphaned;
public void MarkOrphaned() => _orphaned = true;
```

Modify `Return()` in `GenericCache`:
```csharp
private void Return(CacheEntry entry)
{
    entry.DecrementRefCount();

    // If entry was removed while borrowed, clean up after last handle returns.
    // Always defer to _pendingCleanup (not direct Dispose) for consistency with
    // TryRemove's deferred cleanup approach.
    if (entry.RefCount == 0 && entry.IsOrphaned)
    {
        _pendingCleanup.Enqueue(entry.Stored);
        _logger.LogDebug("Cleaned up orphaned entry: {Key}", entry.CacheKey);
    }
    else
    {
        _logger.LogDebug("Returned: {Key} (RefCount={RefCount})", entry.CacheKey, entry.RefCount);
    }
}
```

**Thread safety of orphan cleanup**: `Interlocked.Decrement` in `DecrementRefCount()` guarantees exactly one thread sees `RefCount == 0`. Combined with the volatile `_orphaned` flag, exactly one thread enqueues cleanup.

### 4.4 ChunkedFileEntry.Dispose Must Handle Missing Directory

At shutdown, `CacheMaintenanceService` calls `Clear()` → `DeleteCacheDirectory()`. If an orphaned disk-tier entry's last handle returns *after* `DeleteCacheDirectory`, the cleanup tries to delete a file in a non-existent directory. `ChunkedFileEntry.Dispose()` (and `ChunkedDiskStorageStrategy.Dispose(StoredEntry)`) must catch `DirectoryNotFoundException` and `IOException` from missing backing files — these are expected during shutdown and should be logged at Debug level, not thrown.

### 4.4 Also Add to ICache<T> Interface

```csharp
/// <summary>
/// Removes a specific entry by key. Returns true if found and removed.
/// </summary>
bool TryRemove(string cacheKey);
```

---

## 5. FileContentCache — Per-Archive Key Registry

### 5.1 Design

**File:** `src/ZipDrive.Infrastructure.Caching/FileContentCache.cs`

Add fields:
```csharp
private readonly ConcurrentDictionary<string, HashSet<string>> _archiveKeyIndex = new(StringComparer.OrdinalIgnoreCase);
private readonly Lock _keyIndexLock = new();
```

Add helper:
```csharp
private void RegisterArchiveKey(string cacheKey)
{
    int colonIndex = cacheKey.IndexOf(':');
    if (colonIndex <= 0) return;
    string archiveKey = cacheKey[..colonIndex];

    using (_keyIndexLock.EnterScope())
    {
        if (!_archiveKeyIndex.TryGetValue(archiveKey, out HashSet<string>? set))
        {
            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _archiveKeyIndex[archiveKey] = set;
        }
        set.Add(cacheKey); // Idempotent — no duplicates
    }
}
```

Call `RegisterArchiveKey(cacheKey)` after `BorrowAsync` succeeds in both `ReadAsync` and `WarmAsync`.

**Why `HashSet<string>` with `Lock` instead of `ConcurrentBag`?** `ConcurrentBag` accumulates duplicate keys when files are evicted and re-cached, causing unbounded growth. Under tight cache limits (endurance test: 1MB memory, 10MB disk), a file could be evicted and re-cached hundreds of times. `HashSet.Add` is O(1) idempotent, the lock body is a single insertion (~100ns, uncontended in the common case since it follows a BorrowAsync that takes microseconds-to-milliseconds), and memory usage stays bounded.

### 5.2 RemoveArchive Method

```csharp
/// <inheritdoc />
public int RemoveArchive(string archiveKey)
{
    HashSet<string>? keys;
    using (_keyIndexLock.EnterScope())
    {
        _archiveKeyIndex.Remove(archiveKey, out keys);
    }

    if (keys == null || keys.Count == 0)
        return 0;

    int removed = 0;
    foreach (string key in keys)
    {
        // Try both tiers — entry lives in exactly one, but we don't track which
        if (_memoryCache.TryRemove(key)) removed++;
        else if (_diskCache.TryRemove(key)) removed++;
    }

    _logger.LogInformation("RemoveArchive: {ArchiveKey} — removed {Count} cached file entries", archiveKey, removed);
    return removed;
}
```

**Note on concurrent RegisterArchiveKey + RemoveArchive race:** If a `ReadAsync` completes between `RemoveArchive` taking the key set and cleaning cache entries, `RegisterArchiveKey` may re-create the index entry with one stale key. This is a minor (~100 bytes) leak that only occurs when a read is mid-flight during removal AND the archive is never re-added. Acceptable — the stale entry expires via TTL.

### 5.3 Interface Addition

Add to `IFileContentCache`:
```csharp
/// <summary>
/// Removes all cached file content entries belonging to the specified archive.
/// Active borrows continue working; storage cleanup is deferred until handles are returned.
/// Returns the number of entries removed.
/// </summary>
int RemoveArchive(string archiveKey);
```

---

## 6. ArchiveStructureCache.Invalidate — Make It Work

### 6.1 Current State

`Invalidate(archiveKey)` currently logs a warning and returns `false`:
```csharp
_logger.LogWarning("Invalidate called but direct removal is not supported.");
return false;
```

### 6.2 Updated Implementation

**File:** `src/ZipDrive.Infrastructure.Caching/ArchiveStructureCache.cs`

The underlying `IArchiveStructureStore` wraps a `GenericCache<ArchiveStructure>`. Once `GenericCache` has `TryRemove`, we expose it through `IArchiveStructureStore` and use it:

```csharp
public bool Invalidate(string archiveKey)
{
    ArgumentException.ThrowIfNullOrWhiteSpace(archiveKey);

    bool removed = _cache.TryRemove(archiveKey);
    if (removed)
        _logger.LogInformation("Invalidated structure cache for {ArchiveKey}", archiveKey);
    else
        _logger.LogDebug("Invalidate: {ArchiveKey} not found in cache (already expired or never cached)", archiveKey);

    return removed;
}
```

`IArchiveStructureStore` needs a `TryRemove` method added (it wraps `ICache<ArchiveStructure>`).

---

## 7. ArchiveTrie — Thread-Safe Read/Write

### 7.1 The Problem

`ArchiveTrie` uses `TrieDictionary<ArchiveDescriptor>` and `HashSet<string>`. Neither is thread-safe. Currently, all writes happen at mount time (single-threaded), and all reads happen from Dokan callbacks (concurrent). Dynamic reload introduces concurrent writes alongside reads.

### 7.2 Design: ReaderWriterLockSlim

**File:** `src/ZipDrive.Application/Services/ArchiveTrie.cs`

```csharp
private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
```

**Read operations** (hot path — every Dokan callback):
- `Resolve()`: `_lock.EnterReadLock()` / `ExitReadLock()`
- `ListFolder()`: `_lock.EnterReadLock()` / `ExitReadLock()` — materialize result to `List<>` inside the lock
- `IsVirtualFolder()`: `_lock.EnterReadLock()` / `ExitReadLock()`
- `Archives`, `ArchiveCount`: `_lock.EnterReadLock()` / `ExitReadLock()`

**Write operations** (rare — only on archive add/remove):
- `AddArchive()`: `_lock.EnterWriteLock()` / `ExitWriteLock()`
- `RemoveArchive()`: `_lock.EnterWriteLock()` / `ExitWriteLock()`

### 7.3 Virtual Folder Cleanup on Remove

The current `RemoveArchive` doesn't clean up orphaned virtual folders. After removing the last archive under `"games/"`, the virtual folder `"games"` should also disappear.

Solution: rebuild `_virtualFolders` from remaining archives inside the write lock:

```csharp
public bool RemoveArchive(string virtualPath)
{
    _lock.EnterWriteLock();
    try
    {
        string key = virtualPath + "/";
        bool removed = _trie.Remove(key);
        if (removed)
            RebuildVirtualFolders();
        return removed;
    }
    finally { _lock.ExitWriteLock(); }
}

private void RebuildVirtualFolders()
{
    _virtualFolders.Clear();
    foreach (ArchiveDescriptor archive in _trie.Values)
    {
        string[] parts = archive.VirtualPath.Split('/');
        string current = "";
        for (int i = 0; i < parts.Length - 1; i++)
        {
            current = i == 0 ? parts[i] : current + "/" + parts[i];
            _virtualFolders.Add(current);
        }
    }
}
```

Cost: O(N) where N = number of archives. For 1000 archives, this is microseconds. Acceptable since removals are rare and run inside a write lock that excludes readers.

### 7.4 Performance Impact

`ReaderWriterLockSlim` read lock acquisition is ~20ns with zero writer contention (the common case). Since writes happen at most once every few seconds (consolidation window), readers almost never contend with writers. Compared to the current zero-lock code, the overhead is negligible relative to the ~1ms total Dokan callback time.

---

## 8. ArchiveNode — Per-Archive Ref Count and Drain

### 8.1 Purpose

Each registered archive has an `ArchiveNode` that tracks how many VFS operations are currently in-flight for that archive. When removal is requested, the node enters "draining" mode: new operations are rejected, and removal waits for in-flight operations to complete.

### 8.2 Design

**File:** `src/ZipDrive.Application/Services/ArchiveNode.cs` (new)

```csharp
internal sealed class ArchiveNode
{
    public ArchiveDescriptor Descriptor { get; }
    private int _activeOps;
    private volatile bool _draining;
    private TaskCompletionSource? _drainTcs;
    private CancellationTokenSource? _drainCts;

    public ArchiveNode(ArchiveDescriptor descriptor) => Descriptor = descriptor;
    public bool IsDraining => _draining;
    public int ActiveOps => Volatile.Read(ref _activeOps);

    /// <summary>
    /// Cancellation token that is cancelled when drain starts.
    /// Pass this to fire-and-forget operations (e.g., prefetch) so they abort promptly.
    /// </summary>
    public CancellationToken DrainToken => (_drainCts ??= new CancellationTokenSource()).Token;
```

### 8.3 TryEnter / Exit Pattern

```csharp
public bool TryEnter()
{
    if (_draining) return false;           // Fast rejection
    Interlocked.Increment(ref _activeOps);
    if (_draining)                         // Double-check after increment
    {
        if (Interlocked.Decrement(ref _activeOps) == 0)
            _drainTcs?.TrySetResult();
        return false;
    }
    return true;
}

public void Exit()
{
    int newCount = Interlocked.Decrement(ref _activeOps);
    Debug.Assert(newCount >= 0, $"ArchiveNode.Exit called without matching TryEnter (ActiveOps went to {newCount})");
    if (newCount == 0 && _draining)
        _drainTcs?.TrySetResult();
}
```

The double-check in `TryEnter` prevents a race where drain starts between the check and the increment. This is the same pattern used in the experimental branch's adapter-level drain, but scoped per-archive.

`Exit()` includes a debug assertion to catch unpaired calls — if `ActiveOps` goes negative, the counter is permanently corrupted and drain will never complete.

### 8.4 DrainAsync

```csharp
public async Task DrainAsync(TimeSpan timeout)
{
    // Guard against double-drain: if already draining, await existing drain
    if (_draining)
    {
        if (_drainTcs != null && timeout > TimeSpan.Zero)
        {
            using var cts2 = new CancellationTokenSource(timeout);
            try { await _drainTcs.Task.WaitAsync(cts2.Token); }
            catch (OperationCanceledException) { }
        }
        return;
    }

    // Create TCS before setting _draining — volatile write to _draining provides
    // a release fence, ensuring the prior store to _drainTcs is visible to any
    // thread that observes _draining == true.
    _drainTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    _draining = true;

    // Cancel per-archive token (aborts in-flight prefetch for this archive)
    _drainCts?.Cancel();

    if (Volatile.Read(ref _activeOps) == 0)
        _drainTcs.TrySetResult();   // Already drained

    if (timeout <= TimeSpan.Zero) return;

    using var cts = new CancellationTokenSource(timeout);
    try { await _drainTcs.Task.WaitAsync(cts.Token); }
    catch (OperationCanceledException) { /* timeout — proceed anyway */ }
}
```

### 8.5 Prefetch Must Participate in the Drain Guard

Fire-and-forget prefetch (`PrefetchDirectoryAsync`) runs outside the VFS operation's `TryEnter/Exit` scope — it launches after `ReadFileAsync` completes. Without protection, prefetch continues writing cache entries for an archive that is being removed, re-creating index entries after `RemoveArchive` has cleaned them up.

**Solution:** Prefetch must:
1. Call `node.TryEnter()` at the start of `PrefetchDirectoryAsync`. If draining, bail out immediately.
2. Wrap the entire prefetch body in `try/finally { node.Exit() }`.
3. Use `node.DrainToken` (combined with `_appLifetime.ApplicationStopping`) as the cancellation token, so `DrainAsync` cancels in-flight prefetches promptly.

This means `DrainAsync` waits for both regular VFS operations AND in-flight prefetches before proceeding with cleanup.

### 8.6 Usage in VFS Operations — ArchiveGuard Helper

To avoid repeating the TryEnter/Exit boilerplate in 7+ locations, use a disposable guard struct:

```csharp
private readonly struct ArchiveGuard : IDisposable
{
    private readonly ArchiveNode? _node;

    private ArchiveGuard(ArchiveNode node) => _node = node;

    public static bool TryEnter(
        ConcurrentDictionary<string, ArchiveNode> nodes,
        string archiveKey,
        out ArchiveGuard guard)
    {
        guard = default;
        if (!nodes.TryGetValue(archiveKey, out var node) || !node.TryEnter())
            return false;
        guard = new ArchiveGuard(node);
        return true;
    }

    public void Dispose() => _node?.Exit();
}
```

Usage in VFS methods:
```csharp
if (!ArchiveGuard.TryEnter(_archiveNodes, archive.VirtualPath, out var guard))
    throw new VfsFileNotFoundException(path);

using (guard)
{
    // structure cache + file content cache work
}
```

The compiler enforces disposal via `using`, making it impossible to forget `Exit()`.

---

## 9. ZipVirtualFileSystem — AddArchive / RemoveArchive

### 9.1 New Interface: IArchiveManager (NOT on IVirtualFileSystem)

Archive lifecycle management (add/remove/list) is a separate concern from file system operations (read/list/stat). The `DokanFileSystemAdapter` never calls add/remove. The `DokanHostedService` never calls read/list. Mixing them violates ISP and makes mocking harder.

**New interface** in `src/ZipDrive.Domain/Abstractions/IArchiveManager.cs`:
```csharp
public interface IArchiveManager
{
    Task AddArchiveAsync(ArchiveDescriptor archive, CancellationToken ct = default);
    Task RemoveArchiveAsync(string archiveKey, CancellationToken ct = default);
    IEnumerable<ArchiveDescriptor> GetRegisteredArchives();
}
```

`ZipVirtualFileSystem` implements both `IVirtualFileSystem` and `IArchiveManager`. The `DokanHostedService` depends on `IArchiveManager` (for watcher-driven add/remove) and `IVirtualFileSystem` (for mount/unmount). The `DokanFileSystemAdapter` depends only on `IVirtualFileSystem` — unchanged.

### 9.2 AddArchiveAsync

```
1. archiveTrie.AddArchive(descriptor)        — write lock, O(path length)
2. archiveNodes[key] = new ArchiveNode(desc)  — ConcurrentDictionary, O(1)
3. Log. Done.
```

No cache warming. Structure cache builds lazily on first Dokan access.

**Idempotency:** If the archive already exists in the trie, `AddArchive` overwrites the descriptor. The old `ArchiveNode` is replaced — any in-flight operations holding the old node continue working (they have their own reference). This handles the `Created → Created` (idempotent) consolidation path.

### 9.2.1 MountAsync Must Use AddArchiveAsync

**CRITICAL:** `MountAsync` must call `AddArchiveAsync` for each discovered archive — not `_archiveTrie.AddArchive` directly. Otherwise `_archiveNodes` is never populated and every VFS operation fails the guard check.

```csharp
public async Task MountAsync(VfsMountOptions options, CancellationToken cancellationToken = default)
{
    IReadOnlyList<ArchiveDescriptor> archives = await _discovery.DiscoverAsync(
        options.RootPath, options.MaxDiscoveryDepth, cancellationToken);

    foreach (ArchiveDescriptor archive in archives)
    {
        await AddArchiveAsync(archive, cancellationToken); // NOT _archiveTrie.AddArchive(archive)
    }

    IsMounted = true;
    MountStateChanged?.Invoke(this, true);
}
```

This ensures every archive gets an `ArchiveNode`, and any future logic added to `AddArchiveAsync` (logging, validation, telemetry) applies uniformly to both mount-time and runtime additions.

### 9.2.2 IArchiveDiscovery.DescribeFile — Single-File Discovery

`ApplyDeltaAsync` in `DokanHostedService` needs to build `ArchiveDescriptor` from a single file path. This logic already exists in `ArchiveDiscovery.ScanDirectory` (FileInfo → relative path → descriptor). Rather than duplicate it, extract a reusable method:

Add to `IArchiveDiscovery`:
```csharp
/// <summary>
/// Creates an ArchiveDescriptor for a single file, computing VirtualPath
/// relative to the given root. Returns null if the file is inaccessible.
/// </summary>
ArchiveDescriptor? DescribeFile(string rootPath, string filePath);
```

`ApplyDeltaAsync` calls this instead of constructing descriptors manually.

### 9.3 RemoveArchiveAsync

```
1. archiveNode.DrainAsync(30s)                — wait for in-flight ops
2. archiveTrie.RemoveArchive(key)             — write lock, rebuilds virtual folders
3. archiveNodes.TryRemove(key)                — ConcurrentDictionary
4. structureCache.Invalidate(key)             — TryRemove from GenericCache
5. fileContentCache.RemoveArchive(key)        — iterate key set, TryRemove each
6. Log. Done.
```

Order matters:
- Drain first → no new operations can start.
- Trie removal second → path resolution returns NotFound for any stragglers.
- Cache cleanup last → all data for this archive is freed.

### 9.4 VFS Operation Guards

Methods that need per-archive guards (the `InsideArchive` and `ArchiveRoot` code paths):
- `ReadFileAsync` — guards entire read + prefetch trigger
- `GetFileInfoAsync` — guards `InsideArchive` and `ArchiveRoot` branches
- `ListDirectoryAsync` — guards `ArchiveRoot` and `InsideArchive` branches
- `FileExistsAsync` — guards `InsideArchive` branch
- `DirectoryExistsAsync` — guards `InsideArchive` branch

Methods that do NOT need guards:
- `VirtualRoot` / `VirtualFolder` resolution — these don't access archive data.
- `GetVolumeInfo` — static data.

---

## 10. ArchiveChangeConsolidator — Event Batching

### 10.1 Purpose

`FileSystemWatcher` fires events one at a time, often in bursts (e.g., copying a file triggers Created, then multiple Changed events). The consolidator queues events and flushes a **net delta** after a quiet period.

### 10.2 Consolidation Rules — Full State Machine

| Current State | + Event | → New State | Rationale |
|---------------|---------|-------------|-----------|
| (none) | Created | Added | New archive appeared |
| (none) | Deleted | Removed | Archive disappeared |
| Added | Created | Added | Idempotent |
| Added | Deleted | **Noop** | Appeared and disappeared within window |
| Removed | Deleted | Removed | Idempotent |
| Removed | Created | **Modified** | Archive replaced — flush caches and re-register |
| Modified | Created | Modified | Still needs flush + re-register |
| Modified | Deleted | **Removed** | Was replaced, then deleted — just remove |
| Noop | Created | Added | Revived after cancellation |
| Noop | Deleted | Removed | Deleted after cancellation |
| Renamed(old, new) | — | Removed(old) + Added(new) | Decomposed into primitives |

### 10.3 Design

**File:** `src/ZipDrive.Infrastructure.FileSystem/ArchiveChangeConsolidator.cs` (new)

Key fields:
```csharp
ConcurrentDictionary<string, ChangeKind> _pending;   // relativePath → net change
Timer _timer;                                          // fires after quiet period
Func<ArchiveChangeDelta, Task> _onFlush;              // callback with consolidated delta
TimeSpan _quietPeriod;                                 // default 5 seconds
TimeProvider _timeProvider;                            // injectable for testing
```

**`TimeProvider` injection is required** for testability. Without it, all consolidator tests require real 5-second sleeps. The existing codebase uses `FakeTimeProvider` extensively (GenericCache tests, FileContentCache tests). The consolidator must follow the same pattern.

### 10.3.1 Atomic Flush via Dictionary Swap

**CRITICAL:** The original design's `ToArray()` + per-key `TryRemove()` loses events arriving between snapshot and removal. Instead, atomically swap the pending dictionary:

```csharp
private ConcurrentDictionary<string, ChangeKind> _pending = new(StringComparer.OrdinalIgnoreCase);

private async Task FlushAsync()
{
    // Atomic swap — events arriving during flush processing land in the new dictionary
    var snapshot = Interlocked.Exchange(
        ref _pending,
        new ConcurrentDictionary<string, ChangeKind>(StringComparer.OrdinalIgnoreCase));

    var added = snapshot.Where(x => x.Value == ChangeKind.Added).Select(x => x.Key).ToList();
    var removed = snapshot.Where(x => x.Value == ChangeKind.Removed).Select(x => x.Key).ToList();
    var modified = snapshot.Where(x => x.Value == ChangeKind.Modified).Select(x => x.Key).ToList();

    if (added.Count == 0 && removed.Count == 0 && modified.Count == 0)
        return;

    await _onFlush(new ArchiveChangeDelta(added, removed, modified));
}
```

Events arriving during `_onFlush` execution are captured in the new dictionary and processed by the next flush cycle. Zero event loss.

### 10.4 Quiet Period Semantics

The quiet period is **configurable** via `MountSettings.DynamicReloadQuietPeriodSeconds` (default: 5). Add to `MountSettings`:
```csharp
public int DynamicReloadQuietPeriodSeconds { get; set; } = 5;
```

The timer resets on every event. Only fires after `_quietPeriod` of silence. This handles:
- **Bulk copy**: 50 ZIPs copied in 3 seconds → one flush with 50 additions.
- **Rapid replace**: Delete + re-create within 5s → one "Modified" event.
- **Noise**: Windows shell probing → events filtered by `*.zip` pattern.

Power users on fast local SSDs may prefer 2s for snappier response. Network paths may benefit from longer windows (10s+).

### 10.5 Consolidator Disposal

`ArchiveChangeConsolidator.Dispose()` must use `Timer.DisposeAsync()` (or equivalent) to await any in-flight timer callback before returning. Without this, a callback could fire after disposal and invoke `_onFlush` on a partially torn-down system. Disposal order: (1) disable timer, (2) await in-flight callback, (3) dispose timer.

Events arriving after `Dispose()` are silently dropped — the `_pending` dictionary still accepts writes (no crash) but the timer never fires again. The watcher is disposed immediately after the consolidator, so events stop shortly after.

### 10.6 Buffer Overflow → Full Reconciliation

When `FileSystemWatcher`'s internal buffer overflows (too many events), it fires an `Error` event. The consolidator exposes `ForceFlush()`, but the real recovery is a **full reconciliation** in `DokanHostedService`.

**IMPORTANT:** Before reconciliation, clear the consolidator's pending dictionary to prevent stale pre-overflow events from re-applying after reconciliation completes. Use `Interlocked.Exchange` to atomically swap to a fresh dictionary (same pattern as `FlushAsync`):

```
1. Consolidator: Interlocked.Exchange(_pending, new empty dictionary)  ← discard stale events
2. ArchiveDiscovery.DiscoverAsync() → archives currently on disk
3. IArchiveManager.GetRegisteredArchives() → archives currently in memory
4. Diff: toAdd = onDisk - inMemory, toRemove = inMemory - onDisk
5. Apply: RemoveArchiveAsync for each toRemove, AddArchiveAsync for each toAdd
```

The consolidator needs a `ClearPending()` method for this:
```csharp
public void ClearPending()
{
    Interlocked.Exchange(ref _pending, new ConcurrentDictionary<string, ChangeKind>(StringComparer.OrdinalIgnoreCase));
}
```

---

## 11. DokanHostedService — FileSystemWatcher Integration

### 11.1 Changes from Main

The existing `DokanHostedService` on main is simple: mount VFS → create Dokan → wait. We add:

1. **FileSystemWatcher** watching `*.zip` in `ArchiveDirectory` (recursive, `NotifyFilters.FileName | NotifyFilters.DirectoryName`, `InternalBufferSize = 65536`).
2. **ArchiveChangeConsolidator** receiving watcher events.
3. **ApplyDeltaAsync** callback processing consolidated deltas with file-readability probe.
4. **FullReconciliationAsync** for buffer overflow recovery and directory rename recovery.
5. **Shutdown** stops watcher + consolidator before Dokan unmount.
6. **Dependency on `IArchiveManager`** (not `IVirtualFileSystem`) for add/remove/list.
7. **Dependency on `IArchiveDiscovery`** for single-file descriptor building and full reconciliation.

### 11.1.1 FileSystemWatcher Configuration

```csharp
_watcher = new FileSystemWatcher(_mountSettings.ArchiveDirectory, "*.zip")
{
    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
    IncludeSubdirectories = true,
    InternalBufferSize = 65536,   // 64KB — holds ~860 events vs default 8KB (~107)
    EnableRaisingEvents = true
};
```

- **`NotifyFilters.DirectoryName`**: Catches directory renames (e.g., `games/` → `old-games/`), which make all ZIPs under the old path stale. On directory rename, trigger full reconciliation.
- **`InternalBufferSize = 65536`**: Default 8KB is too small for bulk copy of 100+ ZIPs. Each event is ~76 bytes; 64KB holds ~860 events. Full reconciliation is the fallback but should be rare.

**Network path detection at startup:** Before creating the watcher, check if `ArchiveDirectory` is a network path (UNC prefix `\\` or `DriveInfo.DriveType == DriveType.Network`). If so, log a prominent warning: *"Archive directory is on a network path. FileSystemWatcher may miss events. Consider enabling periodic reconciliation."* Optionally start a periodic reconciliation timer (configurable via `MountSettings.NetworkReconciliationIntervalMinutes`, default: 0 = disabled).

**Archive directory deletion/ejection:** If the archive directory itself is deleted, moved, or the drive is ejected, `FileSystemWatcher` fires an `Error` event. The `FullReconciliationAsync` handler calls `ArchiveDiscovery.DiscoverAsync`, which throws `DirectoryNotFoundException`. Wrap reconciliation in try-catch — on `DirectoryNotFoundException`, log the error and stop the watcher. Do not crash the host.

### 11.1.2 Shared Path Helper: ArchivePathHelper

A shared static helper ensures watcher paths and discovery paths produce identical virtual path strings. Without this, `RemoveArchiveAsync` won't find archives registered by discovery if the two code paths produce different strings.

**File:** `src/ZipDrive.Application/Services/ArchivePathHelper.cs` (new)

```csharp
public static class ArchivePathHelper
{
    /// <summary>
    /// Converts an absolute file path to a virtual path relative to rootPath.
    /// Normalizes: GetFullPath on both inputs, forward slashes, no leading slash.
    /// Handles 8.3 short names via GetFullPath normalization.
    /// </summary>
    public static string ToVirtualPath(string rootPath, string absolutePath)
    {
        string normalizedRoot = Path.GetFullPath(rootPath); // resolves 8.3, trailing slashes
        string normalizedFile = Path.GetFullPath(absolutePath);
        string relative = Path.GetRelativePath(normalizedRoot, normalizedFile);
        return relative.Replace('\\', '/');
    }
}
```

Both `ArchiveDiscovery.DiscoverAsync` and `DokanHostedService` event handlers must use this method. `ArchiveDiscovery.DescribeFile` calls it internally.

### 11.1.3 Event Filtering Before Consolidator

Events are filtered before reaching the consolidator:
1. **Extension validation**: For `Renamed` events, validate both old and new names end with `.zip` (case-insensitive). Renaming `data.zip` → `data.txt` produces only `Removed("data.zip")`. Renaming `readme.txt` → `readme.zip` produces only `Added("readme.zip")`.
2. **Path normalization**: All paths go through `ArchivePathHelper.ToVirtualPath()` (resolves 8.3 short names, UNC paths, case normalization).
3. **Depth filter**: Count `/` separators in the virtual path. Discard events exceeding `MaxDiscoveryDepth`. Without this, the watcher (which has no depth limit) detects ZIPs that `ArchiveDiscovery` ignores, creating add/remove cycles during reconciliation.
4. **Directory events**: On directory rename/delete, trigger `FullReconciliationAsync` (diff is too complex to compute incrementally).
5. **UNC paths and junctions**: `ArchivePathHelper.ToVirtualPath` uses `Path.GetFullPath` which resolves mapped drives to their underlying path. NTFS junctions are NOT followed by `FileSystemWatcher.IncludeSubdirectories` — changes inside junction targets are not detected. This is a documented limitation.

### 11.1.4 File-Readability Probe in ApplyDeltaAsync

**CRITICAL:** On Windows, `FileSystemWatcher` fires `Created` the instant the MFT entry appears — before the file copy completes. Opening a half-copied ZIP fails with `IOException` or `EocdNotFoundException`. Antivirus scanners also hold exclusive locks for 0.5-5 seconds after creation.

`ApplyDeltaAsync` must probe file readability with retry before calling `AddArchiveAsync`:

```
For each Added/Modified file:
  1. Attempt to open with FileShare.Read | FileShare.Delete
  2. If IOException: retry with exponential backoff (1s, 2s, 4s, 8s, 16s, 30s)
  3. Max 6 retries (~60s total). If still locked, log warning and skip.
  4. On success: call IArchiveDiscovery.DescribeFile() then AddArchiveAsync.
```

This handles both slow copies and antivirus locks.

### 11.2 DokanFileSystemAdapter — NO Changes

The adapter stays thin. It holds a stable `IVirtualFileSystem` reference injected at construction time. No swapping, no in-flight counter, no drain at the adapter level. All per-archive lifecycle management lives in the VFS layer.

### 11.3 Program.cs DI — NO Changes

Everything stays `Singleton`. No `Scoped` services needed because the same VFS, cache, and trie instances are mutated in place rather than replaced.

---

## 12. Lifecycle Walkthroughs

### 12.1 Startup

```
DokanHostedService.ExecuteAsync()
  │
  ├─ VFS.MountAsync()
  │    ├─ ArchiveDiscovery.DiscoverAsync() → [desc1, desc2, ...]
  │    └─ For each: await AddArchiveAsync(desc)  ← populates trie + ArchiveNode
  │
  ├─ StartWatcher()
  │    ├─ Create FileSystemWatcher("*.zip", recursive)
  │    └─ Create ArchiveChangeConsolidator(quietPeriod=5s, callback=ApplyDeltaAsync)
  │
  ├─ Create Dokan instance → mount drive
  └─ Block on WaitForFileSystemClosedAsync
```

### 12.2 ZIP Added

```
User copies newgame.zip into archive directory

  FileSystemWatcher → Created("newgame.zip")
  Consolidator.OnCreated("newgame.zip")
    pending["newgame.zip"] = Added
    Timer reset to 5s

  (5s quiet)

  Consolidator.FlushAsync()
    delta = { Added: ["newgame.zip"], Removed: [], Modified: [] }

  ApplyDeltaAsync(delta)
    Build ArchiveDescriptor from FileInfo("newgame.zip")
    VFS.AddArchiveAsync(descriptor)
      archiveTrie.AddArchive(desc)     ← write lock, microseconds
      archiveNodes["newgame.zip"] = new ArchiveNode(desc)

  Done. Next Dokan callback for "newgame.zip\*" resolves correctly.
  Structure cache builds lazily on first access (~100ms for 10K entries).
```

### 12.3 ZIP Removed

```
User deletes oldgame.zip from archive directory

  FileSystemWatcher → Deleted("oldgame.zip")
  Consolidator.OnDeleted("oldgame.zip")
    pending["oldgame.zip"] = Removed
    Timer reset to 5s

  (5s quiet)

  Consolidator.FlushAsync()
    delta = { Added: [], Removed: ["oldgame.zip"], Modified: [] }

  ApplyDeltaAsync(delta)
    VFS.RemoveArchiveAsync("oldgame.zip")
      │
      ├─ archiveNode.DrainAsync(30s)
      │    _draining = true
      │    New callbacks for oldgame.zip → TryEnter() = false → FileNotFound
      │    In-flight reads complete → Exit() → drain signals
      │
      ├─ archiveTrie.RemoveArchive("oldgame.zip")    ← write lock
      │    _trie.Remove("oldgame.zip/")
      │    RebuildVirtualFolders()
      │
      ├─ structureCache.Invalidate("oldgame.zip")
      │    _cache.TryRemove("oldgame.zip") → disposes ArchiveStructure
      │
      ├─ fileContentCache.RemoveArchive("oldgame.zip")
      │    _archiveKeyIndex["oldgame.zip"] → {"oldgame.zip:map.dat", "oldgame.zip:readme.txt"}
      │    _memoryCache.TryRemove("oldgame.zip:map.dat")     → orphan or dispose
      │    _memoryCache.TryRemove("oldgame.zip:readme.txt")  → orphan or dispose
      │
      └─ archiveNodes.TryRemove("oldgame.zip")

  Done. All cache data for oldgame.zip freed.
  Other archives: zero disruption.
```

### 12.4 ZIP Replaced (Delete + Re-Create Within Window)

```
  FileSystemWatcher: Deleted("data.zip"), then Created("data.zip")
  Consolidator:
    OnDeleted → pending["data.zip"] = Removed
    OnCreated → prev=Removed → pending["data.zip"] = Modified

  FlushAsync: delta = { Modified: ["data.zip"] }

  ApplyDeltaAsync:
    VFS.RemoveArchiveAsync("data.zip")    ← drain + full cleanup
    Build new ArchiveDescriptor
    VFS.AddArchiveAsync(newDescriptor)    ← re-register

  Next access: structure cache miss → parses new ZIP content.
```

### 12.5 Rapid Add + Delete (Cancel Out)

```
  FileSystemWatcher: Created("temp.zip"), then Deleted("temp.zip")
  Consolidator:
    OnCreated → pending["temp.zip"] = Added
    OnDeleted → prev=Added → pending["temp.zip"] = Noop

  FlushAsync: no non-Noop entries → callback not invoked. Nothing happens.
```

### 12.6 Concurrent Read During Removal

```
  Thread A: ReadFile("oldgame.zip/level1.dat")
    Resolves to archive "oldgame.zip"
    archiveNode.TryEnter() → true (ActiveOps = 1)
    Enters structure cache + file content cache...

  Thread B: RemoveArchiveAsync("oldgame.zip") starts
    archiveNode.DrainAsync(30s) → _draining = true, ActiveOps = 1 → waits

  Thread C: ReadFile("oldgame.zip/level2.dat")
    archiveNode.TryEnter() → false (draining) → VfsFileNotFoundException

  Thread A: ReadFile completes normally
    archiveNode.Exit() → ActiveOps = 0 → drain TCS signals

  Thread B: Drain complete → proceeds with trie + cache cleanup
```

### 12.7 Shutdown

```
  DokanHostedService.StopAsync()
    StopWatcher()
      consolidator.Dispose()   ← stops timer
      watcher.Dispose()        ← stops events

    dokan.RemoveMountPoint()

    VFS.UnmountAsync()
      structureCache.Clear()

    CacheMaintenanceService.StopAsync()
      fileContentCache.Clear()
      fileContentCache.DeleteCacheDirectory()

    Dispose Dokan instances
```

---

## 13. What Does NOT Change

| Component | Status |
|-----------|--------|
| `DokanFileSystemAdapter` | **Unchanged.** VFS reference is stable. No swapping, no drain. |
| `Program.cs` DI | **Unchanged.** All services remain Singleton. |
| `CacheMaintenanceService` | **Unchanged.** One global timer, sweeps all caches. |
| `GenericCache` core | **Unchanged.** BorrowAsync, eviction, thundering herd prevention, TTL — all work as before. |
| `FileContentCache` core | **Unchanged.** ReadAsync/WarmAsync flow identical (only adds key registration). |
| `ChunkedDiskStorageStrategy` | **Unchanged.** |
| `MemoryStorageStrategy` | **Unchanged.** |
| `Prefetch` system | **Unchanged.** If an archive is removed during prefetch, structure cache miss fails gracefully. |
| Endurance tests | **Unchanged** (existing suites). New DynamicReloadSuite added. |

---

## 14. Test Cases

> **Testing requirement:** All unit tests for `ArchiveChangeConsolidator` must use injectable `TimeProvider` (via `FakeTimeProvider`) — not real timers. Concurrency tests must use deterministic synchronization primitives (`Barrier`, `ManualResetEventSlim`) to force interleaving, not probabilistic "run N threads for M seconds."

### 14.1 GenericCache.TryRemove

#### TC-GC-01: Remove existing entry with RefCount 0

**Scenario:** An entry exists in the cache with `RefCount == 0`.
**Action:** Call `TryRemove(cacheKey)`.
**Expectation:**
- Returns `true`.
- `ContainsKey(cacheKey)` returns `false`.
- `CurrentSizeBytes` decremented by the entry's size.
- `EntryCount` decremented by 1.
- Storage strategy `Dispose` called (or entry queued for async cleanup if disk tier).

#### TC-GC-02: Remove non-existent key

**Scenario:** The cache does not contain the given key.
**Action:** Call `TryRemove("nonexistent")`.
**Expectation:**
- Returns `false`.
- `CurrentSizeBytes` unchanged.
- `EntryCount` unchanged.

#### TC-GC-03: Remove borrowed entry (RefCount > 0) — deferred cleanup

**Scenario:** An entry is currently borrowed (`RefCount == 1`).
**Action:** Call `TryRemove(cacheKey)`.
**Verification mechanism:** Use a test-double `IStorageStrategy<T>` that records `Dispose(StoredEntry)` calls with a `ConcurrentBag<StoredEntry>` (same pattern as existing `StubZipReaderFactory` in the test suite). Assert the bag is empty after TryRemove, then non-empty after handle dispose.
**Expectation:**
- Returns `true`.
- `ContainsKey(cacheKey)` returns `false`.
- `CurrentSizeBytes` decremented immediately.
- The active `ICacheHandle<T>` continues to work (its stream/data is still valid).
- Storage strategy `Dispose` is **NOT** called yet (spy bag is empty).
- When the handle is disposed: entry is enqueued to `_pendingCleanup`. Call `ProcessPendingCleanup()` → storage strategy `Dispose` IS called (spy bag has 1 entry).

#### TC-GC-04: Remove key with in-progress materialization

**Scenario:** A `BorrowAsync` call is in progress for a key (factory blocks on `ManualResetEventSlim`).
**Action:** Call `TryRemove(cacheKey)` while materialization is running, then release the factory.
**Expectation:**
- `TryRemove` returns `false` (entry not yet in `_cache` dictionary).
- The materialization lazy task entry IS removed from `_materializationTasks`.
- **However**, the already-executing factory still completes and inserts the entry into `_cache`. This is expected and safe — in the `RemoveArchiveAsync` flow, drain-first ordering guarantees no materializations are in progress. This test documents the behavior for awareness; it is NOT a correctness bug.
- After factory completes: `ContainsKey(cacheKey)` returns `true` (ghost entry). The entry will expire via TTL.

#### TC-GC-05: BorrowAsync after TryRemove returns cache miss

**Scenario:** Call `TryRemove(key)` then `BorrowAsync(key, ...)`.
**Action:** Borrow after removal.
**Expectation:**
- Factory is invoked (cache miss — entry was removed).
- New entry created and returned successfully.

---

### 14.2 FileContentCache.RemoveArchive

#### TC-FC-01: Remove archive with entries in memory tier

**Scenario:** Archive "game.zip" has 3 cached files, all in memory tier (`UncompressedSize < 50MB`).
**Action:** Call `RemoveArchive("game.zip")`.
**Expectation:**
- Returns `3`.
- All 3 entries removed from memory tier (`ContainsKey` returns false for each).
- Disk tier unaffected.

#### TC-FC-02: Remove archive with entries in both tiers

**Scenario:** Archive "big.zip" has 2 files in memory tier and 1 file in disk tier.
**Action:** Call `RemoveArchive("big.zip")`.
**Expectation:**
- Returns `3`.
- Memory tier loses 2 entries, disk tier loses 1 entry.
- Disk tier entry's temp file queued for async cleanup.

#### TC-FC-03: Remove nonexistent archive

**Scenario:** Archive "nosuch.zip" has no cached entries.
**Action:** Call `RemoveArchive("nosuch.zip")`.
**Expectation:**
- Returns `0`.
- No side effects.

#### TC-FC-04: Remove archive then re-cache files

**Scenario:** Remove "game.zip", then trigger `ReadAsync` for a file in "game.zip".
**Action:** `RemoveArchive("game.zip")` then `ReadAsync("game.zip", entry, "game.zip:file.txt", ...)`.
**Expectation:**
- `RemoveArchive` returns the count of entries removed.
- `ReadAsync` triggers a cache miss, re-materializes the entry.
- Entry is cached again and accessible.
- New key is registered in the per-archive index.

#### TC-FC-05: Remove archive while entry is borrowed

**Scenario:** A file from "game.zip" is currently borrowed (`BorrowAsync` handle not yet disposed).
**Action:** Call `RemoveArchive("game.zip")`.
**Expectation:**
- Returns the count (entry is removed from cache dictionary).
- The active handle continues to work (stream is still readable).
- After handle dispose, storage is cleaned up.

---

### 14.3 ArchiveStructureCache.Invalidate

#### TC-SC-01: Invalidate cached archive

**Scenario:** Archive "game.zip" has its structure cached (via prior `GetOrBuildAsync`).
**Action:** Call `Invalidate("game.zip")`.
**Expectation:**
- Returns `true`.
- `CachedArchiveCount` decremented by 1.
- Next `GetOrBuildAsync("game.zip", ...)` triggers a cache miss and rebuilds from ZIP.

#### TC-SC-02: Invalidate uncached archive

**Scenario:** Archive "never-accessed.zip" has no cached structure.
**Action:** Call `Invalidate("never-accessed.zip")`.
**Expectation:**
- Returns `false`.
- No side effects.

---

### 14.4 ArchiveTrie Thread Safety

#### TC-AT-01: Concurrent Resolve during AddArchive

**Scenario:** Pre-populate trie with 5 archives. 10 threads calling `Resolve()` on the 5 known archives concurrently while 1 thread calls `AddArchive()` for a new 6th archive.
**Action:** Use `Barrier(12)` to synchronize all threads at start. Run 1000 iterations per thread.
**Expectation (verified per iteration):**
- No exceptions thrown.
- Every `Resolve(knownArchive)` returns `ArchiveRoot` or `InsideArchive` with the correct `ArchiveDescriptor` (verify `VirtualPath` matches).
- Every `Resolve("nonexistent")` returns `NotFound`.
- After `AddArchive` completes, a final `Resolve(newArchive)` returns `ArchiveRoot`.
- **Validate the test CAN fail:** Temporarily remove the `ReaderWriterLockSlim`, run the test, and verify it produces exceptions or incorrect results. If it passes without the lock, the test has no teeth.

#### TC-AT-02: Concurrent Resolve during RemoveArchive

**Scenario:** Pre-populate trie with 5 archives. 10 threads calling `Resolve()` on archives 1-4 while 1 thread calls `RemoveArchive()` on archive 5.
**Action:** Use `Barrier(12)` to synchronize all threads. Run 1000 iterations per thread.
**Expectation (verified per iteration):**
- No exceptions thrown.
- Every `Resolve(archives[0..3])` returns the correct status and descriptor throughout the test.
- After `RemoveArchive` completes, `Resolve(archive5)` returns `NotFound`.
- `Resolve` for archives 1-4 continues returning correct results (not corrupted by the removal).
- Virtual folders are consistent: if archive 5 was the last in its folder, `IsVirtualFolder` returns `false` after removal.

#### TC-AT-03: RemoveArchive cleans up virtual folders

**Scenario:** Two archives: `"games/doom.zip"` and `"games/quake.zip"`. Virtual folder `"games"` exists.
**Action:** `RemoveArchive("games/doom.zip")`.
**Expectation:**
- `IsVirtualFolder("games")` still returns `true` (quake.zip remains).
- `RemoveArchive("games/quake.zip")`.
- `IsVirtualFolder("games")` now returns `false`.

#### TC-AT-04: RemoveArchive returns false for unknown archive

**Scenario:** Archive "nosuch.zip" was never added.
**Action:** `RemoveArchive("nosuch.zip")`.
**Expectation:** Returns `false`. No side effects.

---

### 14.5 ArchiveNode Drain

#### TC-AN-01: Drain with no active operations

**Scenario:** `ArchiveNode` with `ActiveOps == 0`.
**Action:** Call `DrainAsync(TimeSpan.FromSeconds(5))`.
**Expectation:**
- Returns immediately (completes in < 100ms).
- `IsDraining` is `true`.

#### TC-AN-02: Drain with active operations that complete

**Scenario:** `ArchiveNode` with `ActiveOps == 2`. Two operations will exit after being signaled.
**Action:** Call `TryEnter()` twice (ActiveOps = 2). Start `DrainAsync(TimeSpan.FromSeconds(5))` on a `Task.Run`. Verify drain has NOT completed (use `Task.WhenAny` with a 100ms delay). Call `Exit()` once — verify drain still not complete. Call `Exit()` second time.
**Expectation:**
- Drain task completes after the second `Exit()` (structural: assert drain task `IsCompleted` within 100ms of second Exit).
- `ActiveOps == 0` at completion.
- No wall-clock timing dependency — completion is gated on `Exit()` calls, not elapsed time.

#### TC-AN-03: Drain timeout

**Scenario:** `ArchiveNode` with `ActiveOps == 1`. The operation never exits.
**Action:** Call `DrainAsync(TimeSpan.FromMilliseconds(100))`.
**Expectation:**
- Drain returns after ~100ms (timeout).
- `ActiveOps == 1` (still active).
- `IsDraining == true`.

#### TC-AN-04: TryEnter rejected during drain

**Scenario:** `DrainAsync` has been called.
**Action:** Call `TryEnter()`.
**Expectation:**
- Returns `false`.
- `ActiveOps` unchanged.

#### TC-AN-05: TryEnter succeeds when not draining

**Scenario:** Normal operation, `IsDraining == false`.
**Action:** Call `TryEnter()`, then `Exit()`.
**Expectation:**
- `TryEnter()` returns `true`.
- `ActiveOps` increments to 1.
- After `Exit()`, `ActiveOps` returns to 0.

---

### 14.6 ArchiveChangeConsolidator

#### TC-CC-01: Single Created event

**Scenario:** One `Created("game.zip")` event, wait for quiet period.
**Expectation:** Flush callback receives `delta = { Added: ["game.zip"], Removed: [], Modified: [] }`.

#### TC-CC-02: Single Deleted event

**Scenario:** One `Deleted("game.zip")` event, wait for quiet period.
**Expectation:** Flush callback receives `delta = { Removed: ["game.zip"] }`.

#### TC-CC-03: Created then Deleted within window — Noop

**Scenario:** `Created("temp.zip")` then `Deleted("temp.zip")` within quiet period.
**Expectation:** Flush callback is **not invoked** (or receives empty delta).

#### TC-CC-04: Deleted then Created within window — Modified

**Scenario:** `Deleted("data.zip")` then `Created("data.zip")` within quiet period.
**Expectation:** Flush callback receives `delta = { Modified: ["data.zip"] }`.

#### TC-CC-05: Renamed event

**Scenario:** `Renamed("old.zip", "new.zip")`.
**Expectation:** Flush callback receives `delta = { Added: ["new.zip"], Removed: ["old.zip"] }`.

#### TC-CC-06: Burst of events with debounce

**Scenario:** 10 `Created` events fired 100ms apart (total 1s) via `FakeTimeProvider`.
**Action:** Fire 10 `OnCreated` events, advancing `FakeTimeProvider` by 100ms between each. Then advance by the full quiet period. Call `ForceFlush()` (or let timer fire via `FakeTimeProvider.Advance`).
**Expectation (observable behavior):**
- Flush callback is invoked exactly **once** (assert call count on spy callback).
- Delta contains all 10 archives as Added.
- No flush occurs during the 1s burst (assert callback count == 0 before final advance).

#### TC-CC-07: Events after flush are independent

**Scenario:** Flush occurs for batch 1. Then new events arrive.
**Expectation:** New events are in a fresh pending set, not mixed with batch 1.

---

### 14.7 VFS AddArchiveAsync / RemoveArchiveAsync Integration

#### TC-VFS-01: AddArchive makes files accessible

**Scenario:** VFS is mounted with no archives. Add archive "game.zip" containing "readme.txt".
**Action:** `AddArchiveAsync(gameDescriptor)`, then `ReadFileAsync("game.zip/readme.txt", ...)`.
**Expectation:**
- `ReadFileAsync` succeeds and returns correct bytes.
- `GetFileInfoAsync("game.zip/readme.txt")` returns valid metadata.

#### TC-VFS-02: RemoveArchive makes files inaccessible

**Scenario:** VFS has archive "game.zip" with cached files.
**Action:** `RemoveArchiveAsync("game.zip")`, then `ReadFileAsync("game.zip/readme.txt", ...)`.
**Expectation:**
- `ReadFileAsync` throws `VfsFileNotFoundException`.
- `GetFileInfoAsync("game.zip")` throws `VfsFileNotFoundException`.
- `fileContentCache.ContainsKey("game.zip:readme.txt")` returns `false`.

#### TC-VFS-03: RemoveArchive does not affect other archives

**Scenario:** VFS has "game.zip" and "data.zip". "data.zip" has warm cache entries.
**Action:** `RemoveArchiveAsync("game.zip")`.
**Expectation:**
- `ReadFileAsync("data.zip/file.bin", ...)` still works (cache hit).
- `fileContentCache.ContainsKey("data.zip:file.bin")` returns `true`.

#### TC-VFS-04: Concurrent reads during RemoveArchive (deterministic interleaving)

**Scenario:** Thread A reads "game.zip/bigfile.bin". Thread B calls `RemoveArchiveAsync("game.zip")`. Thread C tries to read after drain starts.
**Setup:** Use a test-double `IFileContentCache` whose `ReadAsync` blocks on a `ManualResetEventSlim` (gate). This holds Thread A inside the ArchiveNode guard with `ActiveOps == 1`.
**Action:**
1. Start Thread A's `ReadFileAsync` — it enters the node guard and blocks on the gate.
2. Start Thread B's `RemoveArchiveAsync` — it calls `DrainAsync`, which waits (ActiveOps == 1).
3. Verify Thread B's task is NOT completed (assert `!removeTask.IsCompleted`).
4. Start Thread C's `ReadFileAsync` — it calls `TryEnter()` which returns false (draining).
5. Verify Thread C gets `VfsFileNotFoundException`.
6. Release the gate — Thread A's read completes, calls `Exit()`, drain signals.
7. Verify Thread B's task completes (assert `removeTask.IsCompleted` within 1s).
**Expectation:**
- Thread A's read returns successfully (correct data).
- Thread B's drain waits until Thread A exits, then removes.
- Thread C's read fails immediately with `VfsFileNotFoundException`.
- After removal: `fileContentCache.ContainsKey("game.zip:bigfile.bin")` returns `false`.

#### TC-VFS-05: AddArchive after RemoveArchive (re-add)

**Scenario:** Remove "game.zip", then re-add it with a new descriptor.
**Action:** `RemoveArchiveAsync("game.zip")`, then `AddArchiveAsync(newGameDescriptor)`.
**Expectation:**
- Reads work after re-add.
- Structure cache rebuilds (cache miss) on first access.

---

### 14.8 End-to-End FileSystemWatcher Integration

#### TC-E2E-01: Copy ZIP into directory → archive appears in VFS

**Setup:** Real VFS + real `FileSystemWatcher` + `ArchiveChangeConsolidator` with short quiet period (500ms via `FakeTimeProvider` or reduced config for test speed). Pre-built test ZIP with known `__manifest__.json`. Temp directory as archive root.
**Scenario:** VFS mounted with empty archive directory. Copy "newgame.zip" into the directory via `File.Copy`.
**Action:** Poll `ReadFileAsync("newgame.zip/readme.txt", ...)` with 200ms intervals, up to 10s timeout.
**Expectation:**
- Within 10s, `ReadFileAsync` succeeds and returns bytes matching manifest SHA-256.
- `ListDirectoryAsync("\\")` includes "newgame.zip".
- `GetFileInfoAsync("newgame.zip")` returns `IsDirectory == true`.

#### TC-E2E-02: Delete ZIP from directory → archive disappears from VFS

**Setup:** Same as TC-E2E-01. VFS mounted with "game.zip" present and a file already cached (trigger a `ReadFileAsync` first).
**Scenario:** Delete "game.zip" via `File.Delete`.
**Action:** Poll `ReadFileAsync("game.zip/readme.txt", ...)` with 200ms intervals, expecting it to eventually throw `VfsFileNotFoundException`, up to 40s timeout (5s consolidation + 30s drain max).
**Expectation:**
- Within 40s, `ReadFileAsync` throws `VfsFileNotFoundException`.
- `fileContentCache.ContainsKey("game.zip:readme.txt")` returns `false`.
- `ListDirectoryAsync("\\")` does NOT include "game.zip".

#### TC-E2E-03: Buffer overflow triggers full reconciliation

**Setup:** VFS mounted with 3 archives ("a.zip", "b.zip", "c.zip"). Manually add 2 more ("d.zip", "e.zip") to disk and delete 1 ("a.zip") without going through the watcher.
**Scenario:** Simulate buffer overflow by calling `FullReconciliationAsync()` directly (or invoking the watcher `Error` handler).
**Action:** After reconciliation, verify VFS state.
**Expectation:**
- `ReadFileAsync("a.zip/...")` throws `VfsFileNotFoundException` (removed).
- `ReadFileAsync("d.zip/...")` succeeds (added by reconciliation).
- `ReadFileAsync("e.zip/...")` succeeds (added by reconciliation).
- `ReadFileAsync("b.zip/...")` still works (unchanged, warm cache preserved).
- `ReadFileAsync("c.zip/...")` still works (unchanged, warm cache preserved).

---

### 14.9 Additional Edge Case Tests (from review)

#### TC-EDGE-01: TryRemove during active chunked disk extraction

**Scenario:** A large file is being extracted in the background by `ChunkedDiskStorageStrategy`. `TryRemove` is called for that cache key before extraction completes.
**Action:** Materialize a large file (triggers background chunked extraction), call `TryRemove` before extraction finishes.
**Expectation:**
- `TryRemove` returns true.
- The background extraction is cancelled (via `ChunkedFileEntry.Dispose` → `CancellationTokenSource.Cancel`).
- The partially-written sparse temp file is cleaned up (via `_pendingCleanup`).
- No `IOException` from the background extraction task.

#### TC-EDGE-02: TTL expiration racing with TryRemove

**Scenario:** `EvictExpired()` and `TryRemove()` execute concurrently for the same key.
**Action:** Use `FakeTimeProvider` to advance past expiration. Run `EvictExpired()` and `TryRemove()` concurrently on thread pool.
**Expectation:**
- Exactly one succeeds (atomic `ConcurrentDictionary.TryRemove`).
- `CurrentSizeBytes` decremented exactly once.
- `_storageStrategy.Dispose` called exactly once (no double-free).

#### TC-EDGE-03: AddArchiveAsync for already-existing archive

**Scenario:** `AddArchiveAsync` called with a `VirtualPath` that already exists in the trie.
**Action:** Add "game.zip" twice.
**Expectation:**
- Second call succeeds (idempotent).
- Trie descriptor updated to new value.
- Old `ArchiveNode` replaced. In-flight operations on old node continue working.

#### TC-EDGE-04: Concurrent RemoveArchiveAsync for same archive

**Scenario:** Two threads both call `RemoveArchiveAsync("game.zip")` simultaneously.
**Action:** Use `Barrier` to synchronize entry into `RemoveArchiveAsync`.
**Expectation:**
- One thread drains and cleans up. The other sees node already gone and returns silently.
- No double-free. No exceptions.
- Cache entries removed exactly once.

#### TC-EDGE-05: DrainAsync called twice on same node

**Scenario:** `DrainAsync` is called twice on an `ArchiveNode`.
**Action:** Call `DrainAsync(5s)` then `DrainAsync(5s)` again.
**Expectation:**
- Second call detects `_draining == true` and awaits the existing `_drainTcs`. No overwrite.
- Both calls complete when active ops reach zero.

#### TC-EDGE-06: Exit without matching TryEnter

**Scenario:** `Exit()` called on a node with `ActiveOps == 0`.
**Action:** Call `Exit()` directly.
**Expectation:**
- `Debug.Assert` fires (in debug builds).
- `ActiveOps` becomes -1 (detectable in test via property).

#### TC-EDGE-07: Consolidator Modified → Deleted transition

**Scenario:** `Deleted("a.zip")`, `Created("a.zip")` (= Modified), then `Deleted("a.zip")` within window.
**Action:** Feed three events, wait for flush.
**Expectation:**
- Final delta: `{ Removed: ["a.zip"] }`. The Modified state transitions to Removed on second Deleted.

#### TC-EDGE-08: RemoveArchive while WarmAsync is in progress

**Scenario:** Prefetch is warming files from "game.zip". `RemoveArchive("game.zip")` is called concurrently.
**Action:** Start `WarmAsync` on a slow factory, then `RemoveArchive`.
**Expectation:**
- `RemoveArchive` clears the index. WarmAsync completing afterward may re-create a stale index entry.
- The stale entry is a minor leak (~100 bytes). Verify it doesn't cause exceptions.

#### TC-EDGE-09: Prefetch cancelled on drain

**Scenario:** Prefetch is mid-execution for "game.zip". `RemoveArchiveAsync("game.zip")` starts.
**Action:** Start a slow prefetch (blocked on `ManualResetEventSlim`), then call `RemoveArchiveAsync`.
**Expectation:**
- `ArchiveNode.DrainToken` is cancelled.
- Prefetch receives `OperationCanceledException` and exits.
- Drain completes after prefetch exits (via `ArchiveGuard.Dispose` → `node.Exit()`).
- Cache cleanup runs after drain.

#### TC-EDGE-10: Consolidator events after Dispose

**Scenario:** Consolidator is disposed. A lingering watcher event fires and calls `OnCreated`.
**Action:** Dispose consolidator. Then call `OnCreated("late.zip")`.
**Expectation:**
- No exception thrown (the `_pending` dictionary still accepts writes).
- No flush callback invoked (timer is disposed).
- The event is silently dropped.

#### TC-EDGE-11: RemoveArchiveAsync before mount completes

**Scenario:** VFS is not yet mounted (`IsMounted == false`).
**Action:** Call `RemoveArchiveAsync("game.zip")`.
**Expectation:**
- Throws `InvalidOperationException` ("VFS is not mounted") from `EnsureMounted()`.

#### TC-EDGE-12: Sustained event stream prevents premature flush

**Scenario:** Events arrive every 2 seconds for 30 seconds (quiet period is 5s). Use `FakeTimeProvider`.
**Action:** Fire `OnCreated` every 2s for 15 iterations. Advance `FakeTimeProvider` accordingly.
**Expectation:**
- Flush callback is NOT invoked during the 30s stream (no 5s quiet window achieved).
- After the last event, advance `FakeTimeProvider` by 5s.
- Flush callback invoked exactly once with all 15 archives as Added.

#### TC-EDGE-13: ArchiveTrie write lock duration under load

**Scenario:** Trie with 1000 registered archives. Call `RemoveArchive` (triggers `RebuildVirtualFolders`).
**Action:** Measure `RemoveArchive` wall-clock time using `Stopwatch`.
**Expectation:**
- Duration < 10ms (including write lock acquisition, trie removal, virtual folder rebuild).
- This validates the design assumption that "for 1000 archives, this is microseconds."

#### TC-EDGE-14: Event filtering for non-.zip and case-variant filenames

**Scenario:** FileSystemWatcher fires events for various filenames.
**Action:** Feed events: `Created("readme.txt")`, `Created("GAME.ZIP")`, `Created("archive.zip.bak")`, `Renamed("data.zip", "data.txt")`, `Renamed("notes.txt", "notes.zip")`.
**Expectation (after applying event filtering in DokanHostedService):**
- `"readme.txt"` → filtered out (not .zip).
- `"GAME.ZIP"` → passes filter, consolidator receives `OnCreated("GAME.ZIP")`.
- `"archive.zip.bak"` → filtered out (extension is .bak, not .zip).
- `Renamed("data.zip", "data.txt")` → only `OnDeleted("data.zip")` (old was .zip, new is not).
- `Renamed("notes.txt", "notes.zip")` → only `OnCreated("notes.zip")` (new is .zip, old was not).

#### TC-EDGE-15: AddArchiveAsync idempotent (same archive twice)

**Scenario:** `AddArchiveAsync` called twice with the same `VirtualPath` but different `PhysicalPath`.
**Action:** Add "game.zip" with path A, then add "game.zip" with path B.
**Expectation:**
- Second call succeeds without exception.
- `ReadFileAsync("game.zip/file.txt")` reads from path B (not path A).
- Old ArchiveNode is replaced; no leaked resources.

---

### 14.10 Endurance Test — DynamicReloadSuite

#### 14.10.1 Core Requirement

**ALL endurance tests MUST go through `DokanFileSystemAdapter` using `Guarded*Async` methods, not VFS directly.** The adapter is the entry point that production code uses — testing through VFS skips error translation, buffer handling, and any future adapter-level logic.

#### 14.10.2 Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                    Endurance Test Harness                         │
│                                                                  │
│  DokanFileSystemAdapter (real instance, with Guarded*Async)      │
│    └── ZipVirtualFileSystem (real, implements IArchiveManager)   │
│          ├── ArchiveTrie (real, with RWLock)                     │
│          ├── ArchiveStructureCache (real, tight TTL)             │
│          ├── FileContentCache (real, 1MB mem + 10MB disk)        │
│          └── ArchiveNode map (per-archive drain)                 │
│                                                                  │
│  ┌────────── Test tasks call these methods ──────────┐           │
│  │ Adapter.GuardedReadFileAsync(path, buf, offset)   │           │
│  │ Adapter.GuardedListDirectoryAsync(path)           │           │
│  │ Adapter.GuardedGetFileInfoAsync(path)             │           │
│  │ Adapter.GuardedFileExistsAsync(path)              │           │
│  │ Adapter.GuardedDirectoryExistsAsync(path)         │           │
│  │                                                   │           │
│  │ IArchiveManager.AddArchiveAsync(desc)  ← lifecycle│           │
│  │ IArchiveManager.RemoveArchiveAsync(key)           │           │
│  └───────────────────────────────────────────────────┘           │
└─────────────────────────────────────────────────────────────────┘
```

**Adapter `Guarded*Async` methods** are public async methods on `DokanFileSystemAdapter` that accept normal string paths and byte[] buffers (no Dokan native memory types). They delegate to the VFS, providing a test-facing API that works without Dokany installed.

#### 14.10.3 Adapter Changes Required

Add 5 public methods to `DokanFileSystemAdapter`:
```csharp
public Task<int> GuardedReadFileAsync(string path, byte[] buffer, long offset, CancellationToken ct = default)
    => _vfs.ReadFileAsync(path, buffer, offset, ct);
public Task<IReadOnlyList<VfsFileInfo>> GuardedListDirectoryAsync(string path, CancellationToken ct = default)
    => _vfs.ListDirectoryAsync(path, ct);
public Task<VfsFileInfo> GuardedGetFileInfoAsync(string path, CancellationToken ct = default)
    => _vfs.GetFileInfoAsync(path, ct);
public Task<bool> GuardedFileExistsAsync(string path, CancellationToken ct = default)
    => _vfs.FileExistsAsync(path, ct);
public Task<bool> GuardedDirectoryExistsAsync(string path, CancellationToken ct = default)
    => _vfs.DirectoryExistsAsync(path, ct);
```

**All `EnduranceSuiteBase` and every suite** must change from `IVirtualFileSystem Vfs` to `DokanFileSystemAdapter Adapter` and call `Adapter.Guarded*Async` instead of `Vfs.*Async`.

#### 14.10.4 Use-Case Scenarios (Logical Sequences Through Adapter)

Each scenario is a multi-step sequence emulating realistic or adversarial user behavior. Tasks loop for the test duration.

**Category 1: AddAndRead (15 tasks)**

| Scenario | Steps | Verifies |
|----------|-------|----------|
| BasicAddAndVerify (5 tasks) | File.Copy ZIP → poll `GuardedDirectoryExistsAsync` until true → `GuardedListDirectoryAsync` verify count → `GuardedReadFileAsync` 3 files, SHA-256 → `GuardedGetFileInfoAsync` verify size → File.Delete → poll until false | Full add→serve→remove lifecycle |
| ImmediateRead (3 tasks) | File.Copy → immediately `GuardedListDirectoryAsync` (may throw VfsDirectoryNotFoundException) → retry after poll → SHA-256 verify | Structure cache lazy-build race |
| IdempotentOverwrite (2 tasks) | Copy ZIP A → read file, hash=X → overwrite with same content → wait for Modified consolidation → read file, hash must still =X | AddArchiveAsync idempotency |
| NestedSubdirectory (5 tasks) | Copy ZIP with deep nesting → `GuardedDirectoryExistsAsync("zip/sub1/sub2")` → `GuardedListDirectoryAsync("zip/sub1")` → `GuardedReadFileAsync("zip/sub1/sub2/deep.txt")` SHA-256 | Deep path resolution after lazy build |

**Category 2: RemoveDuringRead (10 tasks)**

| Scenario | Steps | Verifies |
|----------|-------|----------|
| DeleteDuringLargeRead (4 tasks) | Add large ZIP (disk tier) → start `GuardedReadFileAsync` → 100ms later File.Delete → read either completes with correct SHA-256 OR throws VfsFileNotFoundException → verify archive gone | ArchiveNode drain with in-flight chunked extraction |
| DeleteDuringMultiFileRead (3 tasks) | Add ZIP with 20+ files → launch 10 concurrent `GuardedReadFileAsync` → 50ms later File.Delete → each read: success or FileNotFound → count outcomes | Concurrent TryEnter/Exit with DrainAsync |
| DeleteDuringListDirectory (3 tasks) | Add ZIP → loop `GuardedListDirectoryAsync` rapidly → File.Delete → each call: full listing or VfsDirectoryNotFoundException → no partial listings | ArchiveTrie RWLock atomicity |

**Category 3: RapidChurn — Stale Cache Detection (8 tasks)**

| Scenario | Steps | Verifies |
|----------|-------|----------|
| ReplaceContent (4 tasks) | Copy ZIP-A → read, hash=X → delete → copy ZIP-B (same filename, different content) → Modified consolidation → read, hash MUST =Y (not X) | FileContentCache.RemoveArchive clears old data |
| RapidToggle (4 tasks) | Tight loop: copy+delete 20x → wait 10s → verify final state matches disk → if present, SHA-256 verify | Consolidator state machine: Added+Deleted=Noop |

**Category 4: ExplorerBrowsing (12 tasks)**

| Scenario | Steps | Verifies |
|----------|-------|----------|
| TreeWalk (6 tasks) | `GuardedListDirectoryAsync("")` → pick archive → `GuardedGetFileInfoAsync` → `GuardedListDirectoryAsync(archive)` → pick file → `GuardedReadFileAsync` 4KB | Metadata consistency during concurrent reload |
| BrowseDuringAdd (3 tasks) | Continuous root listing loop → concurrent File.Copy → archive appears atomically | Trie AddArchive under write lock |
| BrowseDuringRemove (3 tasks) | Continuous root listing → concurrent File.Delete → archive disappears atomically, no phantom entries | Trie RemoveArchive + RebuildVirtualFolders |

**Category 5: Adversarial (5 tasks)**

| Scenario | Steps | Verifies |
|----------|-------|----------|
| NonexistentPaths (1 task) | `GuardedReadFileAsync("fake.zip/file")`, `GuardedFileExistsAsync("no.zip/x")`, `GuardedDirectoryExistsAsync("no.zip")` → correct exceptions/false | Clean error paths |
| ReadBeyondEOF (1 task) | `GuardedReadFileAsync(path, buf, size+1000)` → expect 0 bytes, buffer untouched | Out-of-range offset handling |
| ReadDirectory (1 task) | `GuardedReadFileAsync("archive.zip")`, `GuardedReadFileAsync("archive.zip/subdir")` → expect VfsAccessDeniedException | File vs directory path distinction |
| ListDeletedArchive (1 task) | Add → delete → immediately `GuardedListDirectoryAsync` in loop → transitions from success to VfsDirectoryNotFoundException | Drain guard clean cutoff |
| ShellMetadata (1 task) | `GuardedFileExistsAsync("archive.zip/desktop.ini")`, `GuardedDirectoryExistsAsync("$RECYCLE.BIN")` → false quickly | ShellMetadataFilter correctness |

**Category 6: CrossArchiveInterference (10 tasks)**

| Scenario | Steps | Verifies |
|----------|-------|----------|
| SameFileName (4 tasks) | archiveX has `data.txt` hash_X, archiveY has `data.txt` hash_Y → continuous reads on X → delete Y → reads on X NEVER return hash_Y | Per-archive cache key isolation |
| ConcurrentRemove (4 tasks) | Add A, B, C → concurrent reads on all → delete B → reads on A and C NEVER fail | Per-archive ArchiveNode isolation |
| VirtualFolderOrphan (2 tasks) | Add `games/a.zip` and `games/b.zip` → `IsVirtualFolder("games")` = true → delete a.zip → still true → delete b.zip → false | RebuildVirtualFolders correctness |

**Category 7: BulkCopy (3 tasks)**

| Scenario | Steps | Verifies |
|----------|-------|----------|
| BulkAddAndVerify (3 tasks) | Copy 30 ZIPs simultaneously → poll all accessible → SHA-256 one file each → delete all → verify all gone | Consolidator batching 30+ events |

**Category 8: Rename (3 tasks)**

| Scenario | Steps | Verifies |
|----------|-------|----------|
| RenameAndAccess (2 tasks) | Copy → read hash_A → File.Move → old path NotFound, new path accessible, same hash_A | Watcher rename decomposition |
| RenameToNonZip (1 task) | Move game.zip → game.bak → old gone, new NOT visible → move back → accessible again | Extension filter in watcher |

**Category 9: Concurrency Race Stressors (6 tasks)**

| Scenario | Steps | Verifies |
|----------|-------|----------|
| ThunderingHerdFirstAccess (4 tasks) | Add new archive → 20 concurrent `GuardedListDirectoryAsync` → all return identical results → 20 concurrent `GuardedReadFileAsync` → all produce same SHA-256 | ArchiveStructureCache thundering herd |
| StructureCacheEvictionDuringRead (2 tasks) | Fill structure cache past capacity → read from evicted archive → must succeed (rebuild on miss) | Transparent structure rebuild |

**Category 10: Degradation Monitoring (2 tasks)**

| Scenario | Steps | Verifies |
|----------|-------|----------|
| TempFileAccumulation (1 task) | Every 30s: count `.zip2vd.chunked` files in cache dir → must trend toward 0 between cycles | No sparse file leaks |
| HandleLeakProbe (1 task) | Every 10s: sample `BorrowedEntryCount` → record max → must be bounded by concurrent task count | No handle leaks |

#### 14.10.5 Task Allocation

| Category | Tasks | Purpose |
|----------|-------|---------|
| AddAndRead | 15 | Add lifecycle, lazy structure build |
| RemoveDuringRead | 10 | Drain mechanism, concurrent read safety |
| RapidChurn | 8 | Stale cache detection, consolidator FSM |
| ExplorerBrowsing | 12 | Browsing patterns, metadata consistency |
| Adversarial | 5 | Error paths, shell filter, boundary conditions |
| CrossArchiveInterference | 10 | Archive isolation, virtual folder lifecycle |
| BulkCopy | 3 | Batch consolidation |
| Rename | 3 | Rename decomposition, extension filtering |
| ConcurrencyRace | 6 | Thundering herd, structure eviction |
| DegradationMonitoring | 2 | Temp files, handle leaks |
| **Maintenance** | **2** | Eviction + cleanup loops |
| **Static archive readers** | **14** | Background load from existing NormalRead/PartialRead suites |
| **Total** | **~90** | (leaves headroom under 100) |

#### 14.10.6 Post-Run Assertions

After all tasks complete:
1. Zero errors
2. `BorrowedEntryCount == 0` (no handle leaks)
3. All suites performed operations (`TotalOperations > 0` for each)
4. Cache maintenance ran
5. Trie-disk consistency: `GetRegisteredArchives().Count` matches `.zip` file count on disk
6. No orphan virtual folders
7. Temp file count == 0 after `DeleteCacheDirectory`
8. `PendingCleanupCount == 0`

#### 14.10.7 Duration & Fixture

- **CI (default):** `ENDURANCE_DURATION_HOURS=0.02` (~72s). Small fixture: 5 dedicated reload ZIPs, ~50MB total.
- **Manual (extended):** `ENDURANCE_DURATION_HOURS=24`. Larger fixture: 20 reload ZIPs, ~500MB total. Enables degradation monitoring scenarios.

---

## 15. Implementation Phases

### Phase 1: GenericCache.TryRemove + CacheEntry orphan tracking
- Add `TryRemove` to `GenericCache<T>` and `ICache<T>`
- Add `IsOrphaned` / `MarkOrphaned` to `CacheEntry`
- Modify `Return()` for orphan cleanup — always defer to `_pendingCleanup`
- Ensure `ChunkedFileEntry.Dispose()` handles missing directory (catch `DirectoryNotFoundException`)
- Expose `TryRemove` via `IArchiveStructureStore`
- Add `Debug.Assert` in `Return()` for negative RefCount detection
- Tests: TC-GC-01 through TC-GC-05, TC-EDGE-01, TC-EDGE-02

### Phase 2: FileContentCache.RemoveArchive + ArchiveStructureCache.Invalidate
- Add per-archive key index (`HashSet<string>` with `Lock`) to `FileContentCache`
- Implement `RemoveArchive(archiveKey)` and `RegisterArchiveKey(cacheKey)`
- Fix `ArchiveStructureCache.Invalidate` to delegate to `TryRemove`
- Add `RemoveArchive` to `IFileContentCache`
- Tests: TC-FC-01 through TC-FC-05, TC-SC-01 through TC-SC-02, TC-EDGE-08

### Phase 3: ArchiveTrie thread safety
- Add `ReaderWriterLockSlim` to `ArchiveTrie`
- Wrap all read/write operations
- Materialize `ListFolder` to `List<>` inside read lock
- Implement `RebuildVirtualFolders` on remove
- Tests: TC-AT-01 through TC-AT-04 (use `Barrier` for deterministic interleaving), TC-EDGE-13

### Phase 4: ArchiveNode + IArchiveManager + VFS integration
- Create `ArchiveNode` with TryEnter/Exit (Debug.Assert on underflow), DrainAsync (double-drain guard), DrainToken
- Create `IArchiveManager` interface in Domain
- Create `ArchiveGuard` disposable struct
- Create `ArchivePathHelper` static helper
- Add `AddArchiveAsync` (idempotent), `RemoveArchiveAsync`, `GetRegisteredArchives` to VFS (implementing `IArchiveManager`)
- Modify `MountAsync` to call `AddArchiveAsync` (not `_archiveTrie.AddArchive` directly)
- Add ArchiveGuard to all VFS operations (`InsideArchive` + `ArchiveRoot` paths)
- Make prefetch participate in ArchiveNode guard (TryEnter/Exit + DrainToken)
- Add `IArchiveDiscovery.DescribeFile` for single-file descriptor building
- Tests: TC-AN-01 through TC-AN-05, TC-VFS-01 through TC-VFS-05, TC-EDGE-03 through TC-EDGE-06, TC-EDGE-09, TC-EDGE-11, TC-EDGE-15

### Phase 5: ArchiveChangeConsolidator + DokanHostedService watcher
- Create `ArchiveChangeConsolidator` with `TimeProvider` injection, atomic flush via `Interlocked.Exchange`, `ClearPending()`, proper `DisposeAsync`
- Add `FileSystemWatcher` to `DokanHostedService` (64KB buffer, FileName + DirectoryName filters)
- Event filtering: extension validation via `ArchivePathHelper`, depth filter, directory rename → reconciliation, 8.3 short name normalization
- File-readability probe with exponential backoff in `ApplyDeltaAsync`
- `FullReconciliationAsync` with `IArchiveDiscovery` (clear pending before reconciliation)
- Network path detection + warning at startup
- Archive directory deletion handling (catch `DirectoryNotFoundException` in reconciliation)
- Add `DynamicReloadQuietPeriodSeconds` to `MountSettings`
- Tests: TC-CC-01 through TC-CC-07, TC-EDGE-07, TC-EDGE-10, TC-EDGE-12, TC-EDGE-14, TC-E2E-01 through TC-E2E-03

### Phase 6: Endurance test — DynamicReloadSuite
- Build full `DokanFileSystemAdapter` instance backed by real stack
- Implement 7 use-case suites (AddAndRead, RemoveDuringRead, RapidChurn, ExplorerBrowsing, Adversarial, BulkCopy, Rename)
- Dedicated reload-only archives with SHA-256 manifests
- Fail-fast + post-run assertions + latency recording

---

## 16. Risks and Mitigations

### Risk: ArchiveTrie ReaderWriterLockSlim contention

Heavy read load + occasional write could cause momentary stalls.

**Mitigation:** Write operations (add/remove) are rare (at most once per consolidation window). `ReaderWriterLockSlim` allows unlimited concurrent readers with ~20ns overhead. Even under 100 concurrent Dokan threads, read lock acquisition is negligible.

### Risk: Drain timeout leaves active operations running

If a Dokan operation hangs (e.g., large chunked extraction), drain may timeout after 30s.

**Mitigation:** After timeout, proceed with removal anyway. The per-archive `DrainToken` cancels in-flight prefetches. Orphaned entry handles will clean up when eventually disposed. Log a warning with the count of remaining active operations.

### Risk: FileSystemWatcher misses events (buffer overflow)

Windows `FileSystemWatcher` has a finite internal buffer (64KB configured). Under extreme load, events can be lost.

**Mitigation:** On `Error` event, trigger full reconciliation scan. This is O(N) where N = archives on disk, but only fires under pathological conditions. On directory rename, also trigger reconciliation.

### Risk: FileSystemWatcher unreliable on network paths (SMB)

`ReadDirectoryChangesW` on SMB2/3 can silently drop notifications under load, after idle timeout, or after brief network interruption. No `Error` event fires.

**Mitigation:** Detect network paths at startup via `DriveInfo.DriveType` or UNC prefix and log a prominent warning. Document the limitation. Consider an optional periodic reconciliation timer (e.g., every 5 minutes) when the archive directory is on a network path.

### Risk: Created fires before file copy completes

On Windows, `FileSystemWatcher` fires `Created` the instant the MFT entry appears — before the copy finishes. Antivirus scanners also lock files briefly.

**Mitigation:** File-readability probe with exponential backoff in `ApplyDeltaAsync` (max 6 retries, ~60s total). See Section 11.1.3.

### Risk: ZIP file locked during removal (Windows open handle semantics)

If a `FileStream` is open for extraction when the user deletes a ZIP, Windows prevents deletion — the file remains until the handle closes.

**Mitigation:** This is actually fine. The watcher fires Deleted, but the file still exists. The drain will complete (extraction finishes, prefetch cancelled via DrainToken), then the file is truly deleted. If the file is still present when we try to remove, the caches still contain valid data. If it's gone, subsequent access failures are expected.

### Risk: Stale key-index entries after concurrent read + remove

If `ReadAsync` completes between `RemoveArchive` taking the key set and cleaning entries, `RegisterArchiveKey` re-creates the index with one stale key.

**Mitigation:** The stale entry is ~100 bytes and only occurs when a read is mid-flight during removal AND the archive is never re-added. The stale cache entry expires via TTL (default 30 minutes). Acceptable tradeoff vs. adding cross-layer coupling to prevent it.
