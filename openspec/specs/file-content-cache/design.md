# File Content Cache - Design Document

**Capability**: `file-content-cache`
**Version**: 1.0
**Status**: Implemented

---

## 1. Context

### 1.1 Problem Statement

ZIP archives provide only **sequential access** to compressed data. To read byte N, you must decompress bytes 0 through N. However, Windows file system operations via DokanNet require **random access** at arbitrary offsets in any order.

```
ZIP Archive Reality:
  entry.Open() → Sequential compressed stream
  To read offset 50,000: Decompress 0→50,000, then read

Windows via DokanNet:
  ReadFile(offset=50,000, length=4096)
  ReadFile(offset=100,000, length=4096)
  ReadFile(offset=25,000, length=4096)
  ↑ Random access, any order, multiple times
```

Without caching, a video player making 1000+ random reads would decompress the entire file 1000+ times, making the system **completely unusable**.

### 1.2 Stakeholders

- **End Users**: Expect instant file access after initial open
- **DokanNet Integration**: Requires seekable streams with low latency
- **System Resources**: Memory and disk constrained; cache must stay within limits
- **Concurrent Operations**: Multiple readers accessing same/different files simultaneously

### 1.3 Constraints

- Windows x64 only (DokanNet/Dokany dependency)
- Memory budget: Configurable, default 2GB
- Disk budget: Configurable, default 10GB in temp directory
- Must support files from bytes to multi-gigabyte
- Must handle 100+ concurrent read operations

---

## 2. Goals / Non-Goals

### Goals

1. **Convert sequential to random access** - Materialize ZIP entries into seekable storage
2. **Minimize redundant work** - Cache results, prevent thundering herd
3. **Protect data integrity** - Never evict entries while they're being read
4. **Stay within resource limits** - Enforce memory and disk capacity
5. **Support pluggable policies** - Allow custom eviction and storage strategies
6. **Enable deterministic testing** - Use `TimeProvider` abstraction

### Non-Goals

- Write support (read-only caching)
- Distributed caching (single-machine only)
- Encryption of cached data
- Cross-process cache sharing
- Persistence across restarts

---

## 3. Decisions

### 3.1 Generic Cache with Pluggable Strategies

**Decision**: Single `GenericCache<T>` implementation with `IStorageStrategy<T>` and `IEvictionPolicy` interfaces.

**Rationale**: All caches share identical logic for TTL, capacity, eviction, and concurrency. Only storage format differs:
- Memory tier: `byte[]` stored in RAM
- Disk tier: `MemoryMappedFile` backed by temp files
- Object tier: Direct object references (for metadata)

**Alternatives Considered**:
- Separate implementations per tier → Code duplication, inconsistent behavior
- `Microsoft.Extensions.Caching.Memory` → No pluggable eviction, unpredictable compaction

### 3.2 Borrow/Return Pattern with Reference Counting

**Decision**: `BorrowAsync()` returns `ICacheHandle<T>` that must be disposed. Entries with `RefCount > 0` cannot be evicted.

**Rationale**: Prevents data corruption when eviction runs while entries are in use.

```
WITHOUT RefCount:
  Thread 1: Borrows "file.zip", starts reading
  Thread 2: Triggers eviction, evicts "file.zip"
  Thread 1: 💥 Access violation / corrupted data

WITH RefCount:
  Thread 1: Borrows → RefCount=1, starts reading
  Thread 2: Triggers eviction, skips "file.zip" (RefCount > 0)
  Thread 1: Finishes, disposes → RefCount=0, now evictable
```

**Alternatives Considered**:
- Copy-on-read → Expensive for large files, defeats caching purpose
- Reader-writer locks per entry → Complex, doesn't scale with many entries

### 3.3 Four-Layer Concurrency Strategy

**Decision**: Layered locking maximizes concurrency while preventing thundering herd.

| Layer | Lock Type | When | Purpose |
|-------|-----------|------|---------|
| 1 | None (lock-free) | Cache hit | `ConcurrentDictionary.TryGetValue()` |
| 2 | Per-key (`Lazy<Task<T>>`) | Cache miss | Prevents duplicate materialization |
| 3 | Global eviction lock | Capacity exceeded | Serializes eviction only |
| 4 | RefCount | Always | Protects borrowed entries |

**Rationale**:
- Layer 1: 99% of operations (cache hits) have zero contention
- Layer 2: 10 threads requesting same uncached file → 1 materialization (not 10!)
- Layer 3: Doesn't block reads or parallel materializations
- Layer 4: Entries are never corrupted during use

**Performance Impact**:
- Cache hit: < 100ns (lock-free)
- Thundering herd: 10x less work
- Different keys: Fully parallel

### 3.4 Factory Returns Size in Result

**Decision**: Factory function returns `CacheFactoryResult<T>` containing value, size, and optional metadata.

**Rationale**: When decompressing a ZIP entry, the final size is discovered during decompression. Requiring size upfront forces either:
- Pre-reading the entire entry (wasteful)
- Trusting uncompressed size header (can be wrong)

With `CacheFactoryResult<T>`, the factory reports the actual size after preparing the data.

### 3.5 Soft Capacity Overage

**Decision**: When all entries are borrowed, allow temporary capacity overage rather than blocking or throwing.

**Rationale**:
- Overage is bounded by concurrent operations (finite borrowers)
- Cache converges back to capacity when handles are disposed
- Better UX than blocking reads or failing with exceptions

```csharp
if (evictableEntries.Count == 0)
{
    _logger?.LogWarning("All entries borrowed, allowing soft overage");
    return; // Will converge when handles disposed
}
```

### 3.6 Async Cleanup Queue for Disk Tier

**Decision**: Evicted disk entries are queued for background deletion rather than deleted inline.

**Rationale**: Deleting large temp files can take 10-100ms. Inline deletion during eviction would block the eviction lock, degrading concurrent operations.

```
Inline deletion:
  Evict entry → Delete 500MB file (100ms) → Release lock
  Other threads wait 100ms

Async cleanup:
  Evict entry → Queue for deletion (<1ms) → Release lock
  Background thread deletes files later
```

---

## 4. Architecture

### 4.1 Component Diagram

```
┌─────────────────────────────────────────────────────────────────┐
│                      ICache<T> Interface                        │
│  BorrowAsync(key, ttl, factory) → ICacheHandle<T>               │
│  CurrentSizeBytes, CapacityBytes, HitRate, EntryCount           │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│                   GenericCache<T> Implementation                │
│                                                                 │
│  ┌─────────────────────────────────────────────────────────┐    │
│  │ Layer 1: ConcurrentDictionary<string, CacheEntry>       │    │
│  │          Lock-free reads, O(1) lookup                   │    │
│  └─────────────────────────────────────────────────────────┘    │
│  ┌─────────────────────────────────────────────────────────┐    │
│  │ Layer 2: ConcurrentDictionary<string, Lazy<Task<...>>> │    │
│  │          Per-key materialization, thundering herd      │    │
│  └─────────────────────────────────────────────────────────┘    │
│  ┌─────────────────────────────────────────────────────────┐    │
│  │ Layer 3: Lock _evictionLock                             │    │
│  │          Capacity enforcement, doesn't block reads      │    │
│  └─────────────────────────────────────────────────────────┘    │
│  ┌─────────────────────────────────────────────────────────┐    │
│  │ Layer 4: CacheEntry.RefCount                            │    │
│  │          Eviction protection via borrow/return          │    │
│  └─────────────────────────────────────────────────────────┘    │
│                                                                 │
│  Dependencies:                                                  │
│  • IStorageStrategy<T> - How to store/retrieve/dispose          │
│  • IEvictionPolicy - Which entries to evict                     │
│  • TimeProvider - For deterministic TTL testing                 │
└─────────────────────────────────────────────────────────────────┘
                              │
        ┌─────────────────────┼─────────────────────┐
        ▼                     ▼                     ▼
┌───────────────┐   ┌───────────────┐   ┌───────────────────┐
│ MemoryStorage │   │  DiskStorage  │   │  ObjectStorage    │
│   Strategy    │   │   Strategy    │   │    Strategy<T>    │
│               │   │               │   │                   │
│ Store: byte[] │   │ Store: MMF +  │   │ Store: T object   │
│ Retrieve:     │   │   temp file   │   │ Retrieve: same T  │
│  MemoryStream │   │ Retrieve:     │   │ Cleanup: GC       │
│ Cleanup: GC   │   │  MMF stream   │   │                   │
│               │   │ Cleanup:      │   │ Use: Metadata     │
│ Use: Small    │   │  delete file  │   │   caching         │
│   files       │   │               │   │                   │
│   (< 50MB)    │   │ Use: Large    │   │                   │
│               │   │   files       │   │                   │
└───────────────┘   └───────────────┘   └───────────────────┘
```

### 4.2 Data Flow: Cache Miss (Materialization)

```
1. DokanNet: ReadFile("archive.zip\folder\file.txt", offset=50000)
   ↓
2. Resolve path: archiveKey="archive.zip", internalPath="folder/file.txt"
   ↓
3. Generate cache key: "archive.zip:folder/file.txt"
   ↓
4. GenericCache.BorrowAsync(key, ttl=30min, factory)
   ↓
5. Layer 1: ConcurrentDictionary.TryGetValue → MISS
   ↓
6. Layer 2: GetOrAdd Lazy<Task<CacheEntry>>
   ├─ First thread: Creates Lazy, executes factory
   └─ Other threads: Await same Task (thundering herd prevented)
   ↓
7. Factory executes:
   ├─ Decompress ZIP entry
   └─ Return CacheFactoryResult { Value, SizeBytes }
   ↓
8. Layer 3: EvictIfNeeded (if capacity exceeded)
   └─ Only evicts entries with RefCount = 0
   ↓
9. IStorageStrategy.StoreAsync(result) → StoredEntry
   ↓
10. Create CacheEntry(key, stored, RefCount=0)
    ↓
11. Temporary RefCount hold:
    ├─ entry.IncrementRefCount() → RefCount = 1
    ├─ _cache[key] = entry
    ├─ Post-store eviction check (entry protected)
    └─ entry.DecrementRefCount() → RefCount = 0
    ↓
12. All borrowers: IncrementRefCount, return CacheHandle
    ↓
13. Caller: using (handle) { stream.Seek(50000); stream.Read(); }
    └─ Entry PROTECTED from eviction
    ↓
14. handle.Dispose() → DecrementRefCount → Entry evictable
```

### 4.3 Data Flow: Cache Hit

```
1. GenericCache.BorrowAsync(key, ttl, factory)
   ↓
2. Layer 1: ConcurrentDictionary.TryGetValue → HIT
   ├─ Check TTL: Not expired
   ├─ IncrementRefCount (before returning!)
   ├─ Update LastAccessedAt, AccessCount
   └─ IStorageStrategy.Retrieve(stored) → value
   ↓
3. Return CacheHandle(entry, value)
   ↓
4. Caller: using (handle) { /* use value */ }
   ↓
5. handle.Dispose() → DecrementRefCount

Note: Factory NEVER called on hit. Entry PROTECTED during use.
```

### 4.4 Data Flow: Eviction

```
1. New entry needs 200MB, current=9.9GB, capacity=10GB
   ↓
2. Fast path (no lock): Check if eviction needed → Yes
   ↓
3. Acquire _evictionLock (Layer 3)
   └─ Does NOT block Layer 1 (reads) or Layer 2 (materializations)
   ↓
4. Double-check: Still need eviction? → Yes
   ↓
5. Phase 1: Evict expired entries (TTL-based)
   ├─ Filter: expired AND RefCount = 0
   ├─ For each: Remove from cache, dispose storage
   └─ Freed: 500MB (3 entries)
   ↓
6. Still need space? 9.4GB + 200MB > 10GB → Yes
   ↓
7. Phase 2: IEvictionPolicy.SelectVictims()
   ├─ Input: entries where RefCount = 0
   ├─ LRU sorts by LastAccessedAt (oldest first)
   └─ Returns entries until enough space freed
   ↓
8. Evict selected victims
   ├─ Memory: dispose inline
   └─ Disk: queue for async cleanup
   ↓
9. Release _evictionLock
   ↓
10. Continue with materialization
```

### 4.5 Reference Count Lifecycle

```
┌─────────────────────────────────────────────────────────────────────────┐
│                     Cache Entry RefCount States                          │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                         │
│  ┌──────────────┐  Borrow   ┌──────────────┐  Borrow   ┌──────────────┐ │
│  │  RefCount=0  │ ─────────▶│  RefCount=1  │ ─────────▶│  RefCount=2  │ │
│  │   (IDLE)     │           │  (IN USE)    │           │  (IN USE)    │ │
│  │  Evictable ✓ │           │  Protected ✗ │           │  Protected ✗ │ │
│  └──────────────┘           └──────────────┘           └──────────────┘ │
│         ▲                          │                          │         │
│         │                          │ Dispose                  │ Dispose │
│         │                          ▼                          ▼         │
│         │                   ┌──────────────┐           ┌──────────────┐ │
│         │                   │  RefCount=0  │           │  RefCount=1  │ │
│         └───────────────────│   (IDLE)     │◀──────────│  (IN USE)    │ │
│                             │  Evictable ✓ │           │  Protected ✗ │ │
│                             └──────────────┘           └──────────────┘ │
│                                                                         │
│  Rule: ONLY evict entries where RefCount == 0                           │
│  Thundering Herd: All N waiters get handles → RefCount = N              │
│                                                                         │
└─────────────────────────────────────────────────────────────────────────┘
```

---

## 5. Interface Contracts

### 5.1 ICache<T>

```csharp
public interface ICache<T>
{
    /// <summary>
    /// Borrow entry or create via factory. Handle MUST be disposed.
    /// </summary>
    Task<ICacheHandle<T>> BorrowAsync(
        string cacheKey,
        TimeSpan ttl,
        Func<CancellationToken, Task<CacheFactoryResult<T>>> factory,
        CancellationToken cancellationToken = default);

    // Metrics
    long CurrentSizeBytes { get; }
    long CapacityBytes { get; }
    double HitRate { get; }
    int EntryCount { get; }
    int BorrowedEntryCount { get; }

    // Manual cleanup
    void EvictExpired();
}
```

### 5.2 ICacheHandle<T>

```csharp
public interface ICacheHandle<T> : IDisposable
{
    T Value { get; }          // The cached value
    string CacheKey { get; }  // For debugging
    long SizeBytes { get; }   // Entry size
}

// Usage:
using (var handle = await cache.BorrowAsync(key, ttl, factory))
{
    var stream = handle.Value;
    await stream.ReadAsync(buffer);  // Protected from eviction
}  // Dispose → eviction allowed
```

### 5.3 IStorageStrategy<T>

```csharp
public interface IStorageStrategy<T>
{
    Task<StoredEntry> StoreAsync(CacheFactoryResult<T> result, CancellationToken ct);
    T Retrieve(StoredEntry stored);
    void Dispose(StoredEntry stored);
    bool RequiresAsyncCleanup { get; }  // true for disk tier
}
```

### 5.4 IEvictionPolicy

```csharp
public interface IEvictionPolicy
{
    /// <summary>
    /// Select entries to evict. Only receives entries with RefCount = 0.
    /// </summary>
    IEnumerable<ICacheEntry> SelectVictims(
        IReadOnlyCollection<ICacheEntry> entries,  // RefCount = 0 only
        long requiredBytes,
        long currentBytes,
        long capacityBytes);
}
```

---

## 6. Risks / Trade-offs

### 6.1 Soft Capacity Overage

**Risk**: When all entries are borrowed, cache exceeds capacity.

**Mitigation**:
- Overage bounded by concurrent borrowers
- Log warning for monitoring
- Cache converges when handles disposed

**Trade-off**: Temporary overage vs blocking/failing reads.

### 6.2 Thundering Herd with Failures

**Risk**: If factory throws, all waiting threads receive the exception.

**Mitigation**:
- `Lazy<Task<T>>` is removed from `_materializationTasks` in `finally`
- Next request will try fresh materialization
- Failed materialization is NOT cached

### 6.3 Long-Held Handles

**Risk**: Callers holding handles indefinitely prevent eviction.

**Mitigation**:
- Documentation emphasizes disposing handles promptly
- `BorrowedEntryCount` metric for monitoring
- Future: Consider timeout on handles

### 6.4 Disk Cleanup Latency

**Risk**: Async cleanup queue grows if deletion is slower than eviction.

**Mitigation**:
- `PendingCleanupCount` metric for monitoring
- `ProcessPendingCleanup(maxItems)` for batch processing
- `Clear()` processes entire queue on shutdown

---

## 7. Testing Strategy

### 7.1 Unit Tests

| Test | Description |
|------|-------------|
| `BorrowAsync_CacheHit_ReturnsWithoutFactory` | Verifies factory not called on hit |
| `BorrowAsync_CacheMiss_CallsFactory` | Verifies factory called on miss |
| `BorrowAsync_ThunderingHerd_OnlyOneFactory` | 10 threads, 1 materialization |
| `BorrowAsync_DifferentKeys_ParallelMaterialization` | Keys don't block each other |
| `RefCount_BorrowedEntry_ProtectedFromEviction` | Eviction skips borrowed entries |
| `TTL_ExpiredEntry_EvictedOnAccess` | Expired treated as miss |
| `Capacity_ExceededWithBorrowed_SoftOverage` | Allows overage when all borrowed |
| `Clear_ForciblyRemovesAllEntries` | Ignores RefCount |

### 7.2 Integration Tests

| Test | Description |
|------|-------------|
| `MemoryStrategy_SeekAndRead_RandomAccess` | Stream supports arbitrary seek |
| `DiskStrategy_LargeFile_MemoryMapped` | 100MB file via MMF |
| `DiskStrategy_Eviction_DeletesTempFile` | Cleanup removes files |

### 7.3 Deterministic Time Testing

```csharp
var fakeTime = new FakeTimeProvider();
var cache = new GenericCache<Stream>(strategy, policy, capacity, fakeTime);

// Add entry with 30-minute TTL
await cache.BorrowAsync(key, TimeSpan.FromMinutes(30), factory);

// Advance time past TTL
fakeTime.Advance(TimeSpan.FromMinutes(31));

// Entry now expired
var handle = await cache.BorrowAsync(key, ttl, factory);
// Factory called again (cache miss due to expiration)
```

---

## 8. Configuration

### 8.1 CacheOptions

```json
{
  "Cache": {
    "MemoryCacheSizeMb": 2048,         // Memory tier capacity
    "DiskCacheSizeMb": 10240,          // Disk tier capacity
    "SmallFileCutoffMb": 50,           // Routing threshold
    "TempDirectory": null,             // null = system temp
    "DefaultTtlMinutes": 30,           // Entry lifetime
    "EvictionCheckIntervalSeconds": 60 // Background cleanup
  }
}
```

### 8.2 Tuning Guidelines

| Scenario | Recommendation |
|----------|----------------|
| Low memory system | Reduce `MemoryCacheSizeMb`, lower `SmallFileCutoffMb` |
| High memory system | Increase both memory capacity and cutoff |
| Fast SSD | Aggressive `DiskCacheSizeMb` |
| Many small files | Higher `SmallFileCutoffMb` for more memory caching |
| Few large files | Lower `SmallFileCutoffMb`, larger disk cache |

---

## 9. Future Considerations

### 9.1 Dual-Tier Coordinator

Automatic routing based on file size:
- `< SmallFileCutoffMb` → Memory tier
- `>= SmallFileCutoffMb` → Disk tier

### 9.2 Additional Eviction Policies

- **LFU (Least Frequently Used)**: Based on `AccessCount`
- **Size-First**: Evict largest files first
- **Hybrid**: Combine LRU + LFU

### 9.3 Observability

- `System.Diagnostics.Metrics` integration
- Health check endpoint
- Distributed tracing correlation

---

## 10. Related Documents

- `spec.md` - Formal requirements and scenarios
- `src/Docs/CACHING_DESIGN.md` - Implementation details
- `src/Docs/CONCURRENCY_STRATEGY.md` - Locking strategy deep dive
- `specs/archive-structure-cache` - ZIP metadata caching (uses ObjectStorageStrategy)
