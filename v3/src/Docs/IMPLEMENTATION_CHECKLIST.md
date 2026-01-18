# ZipDrive V3 - Implementation Checklist

**Design Status:** ✅ Complete
**Implementation Status:** ✅ Core Complete (Caching + ZIP Reader)

---

## Part 1: Caching Layer

### Architecture Overview

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

## Part 2: Streaming ZIP Reader

### Architecture Overview

The streaming ZIP reader provides memory-efficient parsing of ZIP archives:

```
┌─────────────────────────────────────────────────────────────────┐
│                         IZipReader                               │
│  • ReadEocdAsync() → ZipEocd                                    │
│  • StreamCentralDirectoryAsync() → IAsyncEnumerable<CDEntry>    │
│  • OpenEntryStreamAsync() → Stream (decompressed)               │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│                   IArchiveStructureCache                         │
│  • GetOrBuildAsync() → ArchiveStructure                         │
│  • Uses GenericCache<ArchiveStructure> + ObjectStorageStrategy  │
└─────────────────────────────────────────────────────────────────┘
```

---

### Phase 1: ZIP Format Structures ✅ COMPLETE

- [x] `ZipConstants.cs` - Signatures, header sizes, compression methods
- [x] `ZipEocd.cs` - End of Central Directory record
- [x] `ZipCentralDirectoryEntry.cs` - Central Directory file header
- [x] `ZipLocalHeader.cs` - Local file header

### Phase 2: Domain Models ✅ COMPLETE

- [x] `ZipEntryInfo.cs` - Minimal extraction metadata (~40 bytes struct)
- [x] `ArchiveStructure.cs` - Cached archive metadata container
- [x] `DirectoryNode.cs` - Tree structure for directory listing

### Phase 3: Exception Hierarchy ✅ COMPLETE

- [x] `ZipException` - Base exception
- [x] `CorruptZipException` - Archive corruption
- [x] `InvalidSignatureException` - Bad magic number
- [x] `EocdNotFoundException` - EOCD not found
- [x] `TruncatedArchiveException` - Premature EOF
- [x] `UnsupportedCompressionException` - Unknown compression method
- [x] `EncryptedEntryException` - Encrypted entry
- [x] `Zip64NotSupportedException` - ZIP64 not supported (reserved)

### Phase 4: IZipReader Implementation ✅ COMPLETE

- [x] `IZipReader.cs` - Interface definition
- [x] `ZipReader.cs` - Full implementation
  - [x] `ReadEocdAsync()` - Locate and parse EOCD with ZIP64 support
  - [x] `StreamCentralDirectoryAsync()` - `IAsyncEnumerable` streaming enumeration
  - [x] `ReadLocalHeaderAsync()` - Parse local header for extraction
  - [x] `OpenEntryStreamAsync()` - Open decompression stream (Store/Deflate)
- [x] `SubStream.cs` - Bounded read-only stream wrapper

### Phase 5: Cache Integration ✅ COMPLETE

- [x] `IArchiveStructureCache.cs` - Cache interface
- [x] `ArchiveStructureCache.cs` - Implementation using GenericCache
  - [x] Streaming CD parsing via `IZipReader.StreamCentralDirectoryAsync()`
  - [x] Incremental dictionary and tree building
  - [x] Memory estimation (~114 bytes per entry)
  - [x] Thundering herd prevention via GenericCache's `Lazy<Task>`

### Phase 6: Testing ✅ COMPLETE

- [x] Test project: `ZipDriveV3.Infrastructure.Archives.Zip.Tests`
- [x] EOCD Tests (4 tests)
  - [x] `ReadEocdAsync_ValidZip_ReturnsCorrectEntryCount`
  - [x] `ReadEocdAsync_EmptyZip_ReturnsZeroEntries`
  - [x] `ReadEocdAsync_InvalidFile_ThrowsEocdNotFoundException`
  - [x] `ReadEocdAsync_TooSmallFile_ThrowsCorruptZipException`
- [x] Central Directory Streaming Tests (5 tests)
  - [x] `StreamCentralDirectoryAsync_ValidZip_YieldsAllEntries`
  - [x] `StreamCentralDirectoryAsync_EmptyZip_YieldsNoEntries`
  - [x] `StreamCentralDirectoryAsync_LargeZip_StreamsEfficiently`
  - [x] `StreamCentralDirectoryAsync_Cancellation_StopsEnumeration`
  - [x] `StreamCentralDirectoryAsync_DirectoryEntry_HasIsDirectoryTrue`
- [x] Local Header & Extraction Tests (3 tests)
  - [x] `ReadLocalHeaderAsync_ValidEntry_ReturnsCorrectHeaderSize`
  - [x] `OpenEntryStreamAsync_StoreCompression_ExtractsCorrectly`
  - [x] `OpenEntryStreamAsync_DeflateCompression_ExtractsCorrectly`
- [x] SubStream Tests (3 tests)
  - [x] `SubStream_BoundedRead_DoesNotReadBeyondBounds`
  - [x] `SubStream_Seek_StaysWithinBounds`
  - [x] `SubStream_SeekBeyondBounds_Throws`

**Phase 6 Checkpoint:** ✅ All 15 ZIP reader tests passing

---

## Overall Summary

| Component | Phase | Status | Tests |
|-----------|-------|--------|-------|
| **Caching Layer** | | | |
| Core interfaces & models | 1 | ✅ Complete | - |
| GenericCache | 2 | ✅ Complete | - |
| Storage strategies | 3 | ✅ Complete | - |
| LRU eviction policy | 4 | ✅ Complete | - |
| Caching integration tests | 5 | ✅ Complete | 42 |
| Dual-tier coordinator | 6 | ⏳ Pending | - |
| Observability | 7 | ⏳ Pending | - |
| **ZIP Reader** | | | |
| ZIP format structures | 1 | ✅ Complete | - |
| Domain models | 2 | ✅ Complete | 11 |
| Exception hierarchy | 3 | ✅ Complete | - |
| IZipReader implementation | 4 | ✅ Complete | - |
| Cache integration | 5 | ✅ Complete | - |
| ZIP reader tests | 6 | ✅ Complete | 15 |

**Total Tests: 68 (all passing)**

---

## Next Steps

1. ⏳ Implement `ZipArchiveProvider` (IArchiveProvider implementation)
2. ⏳ Implement DokanNet file system adapter
3. ⏳ Create dual-tier cache coordinator
4. ⏳ Add performance benchmarks
5. ⏳ Add observability/metrics
