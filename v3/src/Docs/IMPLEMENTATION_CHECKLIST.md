# Caching Layer - Implementation Checklist

**Design Status:** ✅ Complete
**Implementation Status:** ⏳ Ready to Start

---

## Phase 1: Core Interfaces & Models (Estimated: 1-2 hours)

### 1.1 Create Interface Files

- [ ] `IFileCache.cs` - Main cache interface
  ```csharp
  Task<Stream> GetOrAddAsync(string cacheKey, long sizeBytes, TimeSpan ttl,
                             Func<CancellationToken, Task<Stream>> factory, ...);
  long CurrentSizeBytes { get; }
  long CapacityBytes { get; }
  double HitRate { get; }
  int EntryCount { get; }
  void EvictExpired();
  ```

- [ ] `IEvictionPolicy.cs` - Pluggable eviction strategy
  ```csharp
  IEnumerable<ICacheEntry> SelectVictims(IReadOnlyCollection<ICacheEntry> entries,
                                          long requiredBytes, ...);
  ```

- [ ] `ICacheEntry.cs` - Entry metadata for eviction
  ```csharp
  string CacheKey { get; }
  long SizeBytes { get; }
  DateTimeOffset CreatedAt { get; }
  DateTimeOffset LastAccessedAt { get; }
  int AccessCount { get; }
  ```

### 1.2 Create Options & Configuration

- [ ] `CacheOptions.cs` - Configuration model
  ```csharp
  int MemoryCacheSizeMb { get; set; } = 2048;
  int DiskCacheSizeMb { get; set; } = 10240;
  int SmallFileCutoffMb { get; set; } = 50;
  string? TempDirectory { get; set; }
  int DefaultTtlMinutes { get; set; } = 30;
  int EvictionCheckIntervalSeconds { get; set; } = 60;
  ```

### 1.3 Create Helper Classes

- [ ] `NonDisposingStream.cs` - Stream wrapper that doesn't dispose underlying stream
  ```csharp
  public class NonDisposingStream : Stream
  {
      private readonly Stream _innerStream;
      protected override void Dispose(bool disposing) { /* Don't dispose inner */ }
  }
  ```

**Phase 1 Checkpoint:** All interfaces defined, solution compiles

---

## Phase 2: Memory Tier Implementation (Estimated: 2-3 hours)

### 2.1 MemoryTierCache Class

**Note:** We use a simple custom cache with `ConcurrentDictionary`, NOT built-in `MemoryCache`.
Why? MemoryCache has no pluggable eviction policy and unpredictable compaction.

- [ ] Private fields:
  - [ ] `ConcurrentDictionary<string, CacheEntry> _cache`
  - [ ] `IEvictionPolicy _evictionPolicy`
  - [ ] `Lock _evictionLock` (.NET 10)
  - [ ] `TimeProvider _timeProvider`
  - [ ] `long _capacityBytes`
  - [ ] `long _currentSizeBytes`

- [ ] Constructor: Accept `capacityBytes`, `IEvictionPolicy`, `ILogger`, `TimeProvider?`

- [ ] `GetOrAddAsync()` implementation:
  - [ ] Fast path: Check `_cache.TryGetValue()` + not expired (NO LOCK!)
  - [ ] If hit: Update `LastAccessedAt`, `AccessCount++`, return `MemoryStream`
  - [ ] Slow path: Cache miss
    - [ ] Call `EvictIfNeeded(sizeBytes)` to make space
    - [ ] Call factory, materialize to `byte[]`
    - [ ] Create `CacheEntry` with timestamps
    - [ ] Add to `_cache`, update `_currentSizeBytes`
  - [ ] Return `new MemoryStream(bytes, writable: false)`

- [ ] `EvictIfNeeded()` implementation:
  - [ ] Early return if capacity not exceeded (NO LOCK in common case!)
  - [ ] Lock eviction lock (.NET 10 Lock)
  - [ ] Phase 1: Evict all expired entries
  - [ ] Phase 2: If still need space, call `_evictionPolicy.SelectVictims()`
  - [ ] For each victim: `_cache.TryRemove()`, update `_currentSizeBytes`
  - [ ] Release lock

- [ ] `IsExpired(entry)`: `_timeProvider.GetUtcNow() > entry.CreatedAt + entry.Ttl`

- [ ] Properties: `CurrentSizeBytes`, `CapacityBytes`, `EntryCount`

### 2.2 CacheEntry Class (for Memory Tier)

- [ ] Properties:
  ```csharp
  string CacheKey
  byte[] Data
  long SizeBytes
  DateTimeOffset CreatedAt
  TimeSpan Ttl
  DateTimeOffset LastAccessedAt { get; set; } // Mutable for LRU
  int AccessCount { get; set; } // For LFU policy
  ```

### 2.3 MemoryTierCache Tests

- [ ] `CacheMiss_MaterializesAndStores()`
- [ ] `CacheHit_ReturnsSameData()`
- [ ] `CacheHit_UpdatesLastAccessedAt()`
- [ ] `TtlExpiration_EntryRemovedAfterTtl()` (use FakeTimeProvider)
- [ ] `CapacityExceeded_EvictsUsingPolicy()`
- [ ] `EvictionPolicy_LruSelectsOldestAccessed()`
- [ ] `ConcurrentAccess_ThreadSafe()`
- [ ] `NoLockOnCacheHit_FastPath()`

**Phase 2 Checkpoint:** Memory tier complete, all tests passing

---

## Phase 3: Disk Tier Implementation (Estimated: 4-5 hours)

### 3.1 DiskTierCache Class

- [ ] Private fields:
  - [ ] `ConcurrentDictionary<string, Lazy<Task<CachedEntry>>> _activeCache`
  - [ ] `ConcurrentQueue<CachedEntry> _pendingDeletion`
  - [ ] `Lock _evictionLock` (.NET 10)
  - [ ] `Timer _cleanupTimer`
  - [ ] `IEvictionPolicy _evictionPolicy`

- [ ] Constructor:
  - [ ] Accept capacity, temp dir, logger, TimeProvider, IEvictionPolicy
  - [ ] Initialize cleanup timer (every 5 seconds)

- [ ] `GetOrAddAsync()` implementation:
  - [ ] Fast path: Check `_activeCache.TryGetValue()`
  - [ ] If found and not expired: Update LastAccessedAt, return stream
  - [ ] Slow path: Use `Lazy<Task<T>>` pattern:
    ```csharp
    var lazy = _activeCache.GetOrAdd(key, _ => new Lazy<Task<CachedEntry>>(
        async () => await MaterializeAsync(...)));
    var entry = await lazy.Value;
    ```
  - [ ] Before materialization: Call `EvictToMakeSpaceAsync(sizeBytes)`
  - [ ] Materialize to temp file + create MemoryMappedFile
  - [ ] Return `entry.MemoryMappedFile.CreateViewStream()`

- [ ] `MaterializeAsync()` helper:
  - [ ] Create temp file: `Path.Combine(tempDir, $"{Guid.NewGuid()}.zip2vd")`
  - [ ] Call factory, copy to temp file
  - [ ] Create MemoryMappedFile
  - [ ] Return new CachedEntry

- [ ] `EvictToMakeSpaceAsync()`:
  - [ ] Lock eviction (using `.NET 10 Lock`)
  - [ ] Phase 1: Evict all expired entries
  - [ ] Phase 2: If still need space, call `_evictionPolicy.SelectVictims()`
  - [ ] For each victim: Call `MarkForDeletion(entry)`
  - [ ] Release lock

- [ ] `MarkForDeletion()` (Phase 1 of eviction):
  - [ ] Remove from `_activeCache`
  - [ ] Set `entry.PendingDeletion = true`
  - [ ] Enqueue to `_pendingDeletion`
  - [ ] Update `_currentSizeBytes` (immediate)
  - [ ] Log "Marked for deletion"

- [ ] `ProcessPendingDeletions()` (Phase 2, async cleanup):
  - [ ] Process up to 100 entries per cycle
  - [ ] For each: Dispose MMF, delete temp file
  - [ ] Log cleanup time
  - [ ] Best-effort (catch exceptions, don't re-queue)

- [ ] Properties: `CurrentSizeBytes`, `CapacityBytes`, `EntryCount`

### 3.2 CachedEntry Record

- [ ] Properties:
  ```csharp
  string CacheKey
  MemoryMappedFile MemoryMappedFile
  string TempFilePath
  long SizeBytes
  DateTimeOffset CreatedAt
  TimeSpan Ttl
  DateTimeOffset LastAccessedAt { get; set; } // Mutable for LRU
  bool PendingDeletion { get; set; } // Mutable for async cleanup
  int AccessCount { get; set; } // For LFU policy
  ```

### 3.3 DiskTierCache Tests

- [ ] `CacheMiss_CreatesTempFileAndMmf()`
- [ ] `CacheHit_ReturnsMmfStream()`
- [ ] `RandomAccess_SeekWorks()` (verify can seek to any offset)
- [ ] `TtlExpiration_DeletesTempFile()` (use FakeTimeProvider)
- [ ] `LruEviction_OldestAccessedEvictedFirst()`
- [ ] `CapacityEnforcement_NeverExceedsLimit()`
- [ ] `AsyncCleanup_DoesNotBlockGetOrAdd()`
- [ ] `ThunderingHerd_PreventsDuplicateMaterialization()`

**Phase 3 Checkpoint:** Disk tier complete, all tests passing

---

## Phase 4: Eviction Policies (Estimated: 1-2 hours)

### 4.1 LruEvictionPolicy

- [ ] Implement `IEvictionPolicy`
- [ ] `SelectVictims()`:
  - [ ] Calculate space needed
  - [ ] Order entries by `LastAccessedAt` (ascending)
  - [ ] TakeWhile accumulated size >= space needed

### 4.2 Future: LfuEvictionPolicy

- [ ] Order by `AccessCount` (ascending)
- [ ] Tie-breaker: `LastAccessedAt`

### 4.3 Future: SizeFirstEvictionPolicy

- [ ] Order by `SizeBytes` (descending)
- [ ] Evict largest files first

### 4.4 Tests

- [ ] `LruPolicy_SelectsOldestAccessed()`
- [ ] `LruPolicy_FreesExactlyEnoughSpace()`

**Phase 4 Checkpoint:** Eviction policies implemented

---

## Phase 5: DualTierFileCache Coordinator (Estimated: 1 hour)

### 5.1 DualTierFileCache Class

- [ ] Constructor: Accept `IMemoryCache`, `CacheOptions`, `ILogger`, `TimeProvider?`
- [ ] Create both tiers: `MemoryTierCache`, `DiskTierCache`
- [ ] Calculate `_smallFileCutoffBytes` from options

- [ ] `GetOrAddAsync()`:
  - [ ] Increment `_totalRequests`
  - [ ] Route based on size:
    ```csharp
    if (sizeBytes < _smallFileCutoffBytes)
        return await _memoryTier.GetOrAddAsync(...);
    else
        return await _diskTier.GetOrAddAsync(...);
    ```
  - [ ] Log routing decision

- [ ] Properties (aggregate from both tiers):
  - [ ] `CurrentSizeBytes` = memory + disk
  - [ ] `CapacityBytes` = memory + disk
  - [ ] `HitRate` = _cacheHits / _totalRequests
  - [ ] `EntryCount` = memory + disk

- [ ] `EvictExpired()`: Call both tiers

- [ ] `DisposeAsync()`: Dispose disk tier (memory tier doesn't need it)

### 5.2 DualTierFileCache Tests

- [ ] `SmallFile_RoutesToMemoryTier()`
- [ ] `LargeFile_RoutesToDiskTier()`
- [ ] `CutoffBoundary_RoutesCorrectly()`
- [ ] `Metrics_AggregatedFromBothTiers()`

**Phase 5 Checkpoint:** Coordinator complete, all tests passing

---

## Phase 6: Integration & Testing (Estimated: 2-3 hours)

### 6.1 Integration Tests

- [ ] `EndToEnd_SequentialToRandomAccessConversion()`
  - [ ] Create test ZIP with 10MB file
  - [ ] First access: Cache miss, materialize
  - [ ] Seek to various offsets (5MB, 1KB, 8MB)
  - [ ] Verify data correctness
  - [ ] Second access: Cache hit, instant

- [ ] `TtlEviction_WithFakeTime()`
  - [ ] Cache entry with 1 min TTL
  - [ ] Verify cache hit within TTL
  - [ ] Advance fake time past TTL
  - [ ] Verify cache miss after eviction

- [ ] `CapacityEnforcement_LruEviction()`
  - [ ] 1MB capacity cache
  - [ ] Add 500KB file1
  - [ ] Add 500KB file2 (total 1MB)
  - [ ] Add 600KB file3 (should evict file1)
  - [ ] Verify capacity not exceeded
  - [ ] Verify file1 evicted, file2+file3 remain

- [ ] `ConcurrentAccess_StressTest()`
  - [ ] 100 threads, 1000 operations each
  - [ ] Mix of hits and misses
  - [ ] Verify no exceptions, no race conditions
  - [ ] Verify hit rate reasonable

### 6.2 Performance Benchmarks

- [ ] Measure cache hit latency (target: < 1ms)
- [ ] Measure cache miss latency:
  - [ ] 10MB file (target: < 100ms)
  - [ ] 100MB file (target: < 2s)
- [ ] Measure eviction latency (target: < 1ms for mark)
- [ ] Measure throughput (concurrent GetOrAddAsync)

**Phase 6 Checkpoint:** All integration tests passing, performance targets met

---

## Phase 7: Observability (Estimated: 1-2 hours)

### 7.1 Metrics Integration

- [ ] Add `System.Diagnostics.Metrics` support
- [ ] Counter: `cache_hits_total`
- [ ] Counter: `cache_misses_total`
- [ ] Counter: `cache_evictions_total`
- [ ] ObservableGauge: `cache_size_bytes`
- [ ] ObservableGauge: `cache_entry_count`
- [ ] Histogram: `cache_operation_duration_seconds`

### 7.2 Logging Enhancements

- [ ] Structured logging with key-value pairs
- [ ] Log levels:
  - [ ] Debug: Cache hits/misses, routing decisions
  - [ ] Information: Evictions, cleanup cycles
  - [ ] Warning: Capacity issues, cleanup failures
  - [ ] Error: Materialization failures

**Phase 7 Checkpoint:** Observability complete

---

## Final Checklist

### Code Quality
- [ ] All code follows C# conventions
- [ ] XML documentation on all public APIs
- [ ] No TODOs or HACKs in code
- [ ] Nullable reference types enabled
- [ ] No compiler warnings

### Testing
- [ ] 80%+ code coverage
- [ ] All unit tests passing
- [ ] All integration tests passing
- [ ] Performance benchmarks meet targets

### Documentation
- [ ] CACHING_DESIGN.md complete
- [ ] README.md up to date
- [ ] Code examples in comments
- [ ] Configuration documented

### Integration
- [ ] NuGet packages referenced correctly
- [ ] Dependency injection configured
- [ ] Configuration binding working

---

## Estimated Total Time

| Phase | Estimated Time |
|-------|----------------|
| Phase 1: Interfaces | 1-2 hours |
| Phase 2: Memory Tier | 2-3 hours |
| Phase 3: Disk Tier | 4-5 hours |
| Phase 4: Eviction Policies | 1-2 hours |
| Phase 5: Coordinator | 1 hour |
| Phase 6: Integration | 2-3 hours |
| Phase 7: Observability | 1-2 hours |
| **Total** | **12-18 hours** |

---

## Success Criteria

✅ All interfaces implemented
✅ Both tiers working independently
✅ Coordinator routing correctly
✅ 80%+ test coverage
✅ All tests passing
✅ Performance targets met (<1ms hit, <100ms miss for 10MB)
✅ No memory leaks
✅ Metrics exposed
✅ Documentation complete

**Ready to start Phase 1!** 🚀
