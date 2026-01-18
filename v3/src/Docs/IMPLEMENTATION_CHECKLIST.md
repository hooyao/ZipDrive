# Caching Layer - Implementation Checklist

**Design Status:** ✅ Complete
**Implementation Status:** ✅ Core Complete (Phase 1-4)

---

## Architecture Overview

The caching layer uses a **generic cache with pluggable storage strategies**:

```
┌─────────────────────────────────────────────────────────────────┐
│                      GenericCache<T>                            │
│  • Borrow/Return pattern with reference counting                │
│  • Three-layer concurrency (lock-free → per-key → eviction)     │
│  • Pluggable IEvictionPolicy                                    │
│  • Pluggable IStorageStrategy<T>                                │
└─────────────────────────────────────────────────────────────────┘
                              │
         ┌────────────────────┼────────────────────┐
         ▼                    ▼                    ▼
┌─────────────────┐  ┌─────────────────┐  ┌─────────────────┐
│MemoryStorage    │  │DiskStorage      │  │ObjectStorage<T> │
│Strategy         │  │Strategy         │  │Strategy         │
│                 │  │                 │  │                 │
│• byte[] in RAM  │  │• MemoryMapped   │  │• Direct object  │
│• Fast, volatile │  │  Files          │  │  reference      │
│• Small files    │  │• Large files    │  │• Metadata cache │
└─────────────────┘  └─────────────────┘  └─────────────────┘
```

---

## Phase 1: Core Interfaces & Models ✅ COMPLETE

### 1.1 Cache Interfaces

- [x] `ICache<T>.cs` - Generic cache interface with borrow/return pattern
  ```csharp
  Task<ICacheHandle<T>> BorrowAsync(string cacheKey, TimeSpan ttl,
      Func<CancellationToken, Task<CacheFactoryResult<T>>> factory, ...);
  long CurrentSizeBytes { get; }
  long CapacityBytes { get; }
  double HitRate { get; }
  int EntryCount { get; }
  int BorrowedEntryCount { get; }
  void EvictExpired();
  ```

- [x] `ICacheHandle<T>.cs` - Handle returned from BorrowAsync (RAII pattern)
  ```csharp
  T Value { get; }
  string CacheKey { get; }
  long SizeBytes { get; }
  void Dispose(); // Decrements RefCount, allows eviction
  ```

- [x] `IEvictionPolicy.cs` - Pluggable eviction strategy
  ```csharp
  IEnumerable<ICacheEntry> SelectVictims(IReadOnlyCollection<ICacheEntry> entries,
      long requiredBytes, long currentSize, long capacity);
  ```

- [x] `ICacheEntry.cs` - Entry metadata for eviction
  ```csharp
  string CacheKey { get; }
  long SizeBytes { get; }
  DateTimeOffset CreatedAt { get; }
  DateTimeOffset LastAccessedAt { get; }
  int AccessCount { get; }
  ```

### 1.2 Storage Strategy Interfaces

- [x] `IStorageStrategy<T>.cs` - Pluggable storage abstraction
  ```csharp
  Task<StoredEntry> StoreAsync(CacheFactoryResult<T> result, CancellationToken ct);
  T Retrieve(StoredEntry stored);
  void Dispose(StoredEntry stored);
  bool RequiresAsyncCleanup { get; }
  ```

- [x] `StoredEntry.cs` - Opaque wrapper for stored data
  ```csharp
  object Data { get; }       // Internal storage (byte[], MMF, object)
  long SizeBytes { get; }    // For capacity tracking
  string? FilePath { get; }  // For disk strategy cleanup
  ```

- [x] `CacheFactoryResult<T>.cs` - Factory output with metadata
  ```csharp
  T Value { get; }
  long SizeBytes { get; }
  IReadOnlyDictionary<string, object>? Metadata { get; }
  ```

### 1.3 Internal Classes

- [x] `CacheEntry.cs` - Internal cache entry with reference counting
  ```csharp
  string CacheKey { get; }
  StoredEntry Stored { get; }
  DateTimeOffset CreatedAt { get; }
  TimeSpan Ttl { get; }
  DateTimeOffset LastAccessedAt { get; set; }
  int AccessCount { get; set; }
  int RefCount { get; }  // Thread-safe reference count
  void IncrementRefCount();
  void DecrementRefCount();
  ```

- [x] `CacheHandle<T>.cs` - Handle implementation (decrements RefCount on dispose)

**Phase 1 Checkpoint:** ✅ All interfaces defined, solution compiles

---

## Phase 2: GenericCache Implementation ✅ COMPLETE

### 2.1 GenericCache<T> Class

- [x] Private fields:
  - [x] `ConcurrentDictionary<string, CacheEntry> _cache` (Layer 1: lock-free)
  - [x] `ConcurrentDictionary<string, Lazy<Task<CacheEntry>>> _materializationTasks` (Layer 2)
  - [x] `Lock _evictionLock` (.NET 10) (Layer 3)
  - [x] `ConcurrentQueue<StoredEntry> _pendingCleanup` (async cleanup)
  - [x] `IStorageStrategy<T> _storageStrategy`
  - [x] `IEvictionPolicy _evictionPolicy`
  - [x] `TimeProvider _timeProvider`
  - [x] `long _capacityBytes`, `_currentSizeBytes`, `_hits`, `_misses`

- [x] Constructor: Accept `IStorageStrategy<T>`, `IEvictionPolicy`, `capacityBytes`, `TimeProvider?`, `ILogger?`

- [x] `BorrowAsync()` implementation:
  - [x] Layer 1: Lock-free lookup via `_cache.TryGetValue()` + not expired
  - [x] If hit: Increment RefCount, update LastAccessedAt/AccessCount, return handle
  - [x] Layer 2: Use `Lazy<Task<CacheEntry>>` for thundering herd prevention
  - [x] Materialize, store via strategy, add to cache
  - [x] Temporary RefCount hold during post-store eviction check
  - [x] Return `CacheHandle<T>` that decrements RefCount on dispose

- [x] `EvictIfNeededAsync()` implementation:
  - [x] Fast path: Check capacity WITHOUT lock
  - [x] Slow path: Layer 3 eviction lock
  - [x] Phase 1: Evict expired entries (only if RefCount = 0)
  - [x] Phase 2: Use `_evictionPolicy.SelectVictims()` (only entries with RefCount = 0)

- [x] `Clear()` / `ClearAsync()` - Force clear all entries (for cleanup/shutdown)

- [x] `ProcessPendingCleanup()` - Background cleanup for async strategies

- [x] Properties: `CurrentSizeBytes`, `CapacityBytes`, `HitRate`, `EntryCount`, `BorrowedEntryCount`

**Phase 2 Checkpoint:** ✅ GenericCache complete with borrow/return pattern

---

## Phase 3: Storage Strategies ✅ COMPLETE

### 3.1 MemoryStorageStrategy

- [x] Stores data as `byte[]` in memory
- [x] Returns `MemoryStream` on retrieve (seekable, random access)
- [x] `RequiresAsyncCleanup = false` (GC handles cleanup)
- [x] Best for: Small files (< 50MB default cutoff)

### 3.2 DiskStorageStrategy

- [x] Stores data as `MemoryMappedFile` backed by temp file
- [x] Temp file naming: `{guid}.zip2vd.cache`
- [x] Returns `MemoryMappedViewStream` on retrieve (seekable, random access)
- [x] `RequiresAsyncCleanup = true` (file deletion)
- [x] Best for: Large files (≥ 50MB)

### 3.3 ObjectStorageStrategy<T>

- [x] Stores objects directly (no serialization)
- [x] Returns same object reference on retrieve
- [x] `RequiresAsyncCleanup = false`
- [x] Best for: Metadata caching (e.g., `ArchiveStructure`)

**Phase 3 Checkpoint:** ✅ All storage strategies implemented

---

## Phase 4: Eviction Policies ✅ COMPLETE

### 4.1 LruEvictionPolicy

- [x] Implement `IEvictionPolicy`
- [x] `SelectVictims()`:
  - [x] Order entries by `LastAccessedAt` (ascending = oldest first)
  - [x] TakeWhile accumulated size >= space needed + 10% buffer

### 4.2 Future: Additional Policies (Not Yet Implemented)

- [ ] `LfuEvictionPolicy` - Least Frequently Used
- [ ] `SizeFirstEvictionPolicy` - Evict largest files first
- [ ] `HybridEvictionPolicy` - Combine LRU + LFU

**Phase 4 Checkpoint:** ✅ LRU eviction policy implemented

---

## Phase 5: Integration Tests ✅ COMPLETE

### 5.1 MemoryStorageStrategy Tests

- [x] `MemoryCache_BorrowAsync_CacheMiss_CallsFactoryAndCachesData`
- [x] `MemoryCache_BorrowAsync_CacheHit_ReturnsWithoutCallingFactory`
- [x] `MemoryCache_BorrowAsync_RandomAccess_SupportsSeek`
- [x] `MemoryCache_RefCount_ProtectsFromEviction`
- [x] `MemoryCache_TtlExpiration_EvictsExpiredEntries`
- [x] `MemoryCache_CapacityEviction_EvictsLruEntries`

### 5.2 DiskStorageStrategy Tests

- [x] `DiskCache_BorrowAsync_CacheMiss_CreatesMemoryMappedFile`
- [x] `DiskCache_BorrowAsync_RandomAccess_SupportsSeekOnLargeFile`
- [x] `DiskCache_CacheHit_ReusesMemoryMappedFile`
- [x] `DiskCache_Eviction_DeletesTempFile`

### 5.3 Concurrency Tests

- [x] `MemoryCache_ConcurrentBorrow_SameKey_OnlyMaterializesOnce` (thundering herd)
- [x] `MemoryCache_ConcurrentBorrow_DifferentKeys_MaterializesInParallel`
- [x] `MemoryCache_MultipleBorrowers_SameEntry_AllGetValidHandles`

**Phase 5 Checkpoint:** ✅ All 13 integration tests passing

---

## Phase 6: Dual-Tier Coordinator ⏳ NOT STARTED

### 6.1 DualTierFileCache Class (Future)

- [ ] Constructor: Create both memory and disk GenericCache instances
- [ ] Route based on file size (< cutoff → memory, ≥ cutoff → disk)
- [ ] Aggregate metrics from both caches
- [ ] Implement `IFileCache` interface

**Note:** Current implementation allows manual tier selection. Coordinator will automate routing.

---

## Phase 7: Observability ⏳ NOT STARTED

### 7.1 Metrics Integration

- [ ] Add `System.Diagnostics.Metrics` support
- [ ] Counter: `cache_hits_total`, `cache_misses_total`, `cache_evictions_total`
- [ ] ObservableGauge: `cache_size_bytes`, `cache_entry_count`
- [ ] Histogram: `cache_operation_duration_seconds`

### 7.2 Logging (Partially Complete)

- [x] Debug: Cache hits/misses
- [x] Information: Materialization, eviction
- [x] Warning: Capacity issues
- [ ] Structured logging improvements

---

## Final Checklist

### Code Quality
- [x] All code follows C# conventions
- [x] XML documentation on all public APIs
- [x] No TODOs or HACKs in code
- [x] Nullable reference types enabled
- [x] No compiler warnings

### Testing
- [x] 80%+ code coverage (core cache)
- [x] All unit tests passing
- [x] All integration tests passing
- [ ] Performance benchmarks meet targets

### Documentation
- [x] CACHING_DESIGN.md (needs update for new architecture)
- [x] CONCURRENCY_STRATEGY.md (needs RefCount documentation)
- [x] IMPLEMENTATION_CHECKLIST.md (this file - updated)
- [ ] README.md up to date

---

## Key Design Decisions

### Why Borrow/Return Pattern Instead of GetOrAdd?

**Problem:** With `GetOrAdd()`, caller holds reference but cache can evict entry, causing data corruption or access to disposed resources.

**Solution:** `BorrowAsync()` returns `ICacheHandle<T>` that increments `RefCount`. Entry cannot be evicted while `RefCount > 0`. Handle's `Dispose()` decrements `RefCount`, allowing eviction.

```csharp
// Safe - entry protected during use
using (var handle = await cache.BorrowAsync(key, ttl, factory))
{
    await handle.Value.ReadAsync(buffer);  // Won't be evicted!
}  // Dispose() allows eviction
```

### Why Pluggable Storage Strategies?

**Problem:** Memory and disk caching have different storage mechanisms (byte[] vs MMF), but share cache logic (eviction, TTL, concurrency).

**Solution:** `IStorageStrategy<T>` abstracts storage details. `GenericCache<T>` handles all cache logic. Storage strategies only handle Store/Retrieve/Dispose.

### Why Temporary RefCount Hold During Post-Store Eviction?

**Problem:** After storing a new entry, post-store eviction check could immediately evict the just-added entry (if capacity exceeded and entry has RefCount=0).

**Solution:** Temporarily increment RefCount before post-store eviction, decrement after. Entry protected during critical window.

---

## Summary

| Phase | Status | Description |
|-------|--------|-------------|
| Phase 1 | ✅ Complete | Core interfaces & models |
| Phase 2 | ✅ Complete | GenericCache with borrow/return |
| Phase 3 | ✅ Complete | Storage strategies (Memory, Disk, Object) |
| Phase 4 | ✅ Complete | LRU eviction policy |
| Phase 5 | ✅ Complete | Integration tests (13 passing) |
| Phase 6 | ⏳ Pending | Dual-tier coordinator |
| Phase 7 | ⏳ Pending | Observability (metrics) |

**Core caching layer is complete and tested!**
