# ZipDrive - File Content Cache Design Document

**Version:** 3.0
**Last Updated:** 2026-02-27
**Status:** ✅ Implementation Complete (Core + Strategy-Owned Materialization + Dual-Tier + Observability)

---

## Executive Summary

**The Core Problem**: ZIP archives provide only **sequential access** (compressed streams), but Windows file system operations via DokanNet require **random access** at arbitrary offsets. Without caching, every read operation would require full decompression from the beginning, making the system **completely unusable**.

**The Solution**: Dual-tier caching that materializes (fully decompresses) ZIP entries into random-access storage with TTL-based expiration and capacity limits.

**API Design**: Generic `ICache<T>` interface where the factory returns `CacheFactoryResult<T>` containing both the value and metadata (including size). `CacheFactoryResult<T>` implements `IAsyncDisposable` with an `OnDisposed` callback, enabling chained resource cleanup. Strategies own the full materialization pipeline via `MaterializeAsync()` — they call the factory, consume the stream, and dispose resources, eliminating intermediate buffering.

**Unified Architecture**: A single `GenericCache<TStored, TValue>` implementation handles all caching concerns (TTL, eviction, capacity, concurrency). Storage differences are abstracted via `IStorageStrategy`:
- `MemoryStorageStrategy` → byte[] for small files
- `DiskStorageStrategy` → MemoryMappedFile for large files
- `ObjectStorageStrategy<T>` → any object (ZIP structure, metadata)

**Related Documents**:
- This document describes the **File Content Cache** (decompressed file data)
- For the **ZIP Structure Cache** (parsed Central Directory metadata), see [`ZIP_STRUCTURE_CACHE_DESIGN.md`](ZIP_STRUCTURE_CACHE_DESIGN.md)
- For analysis and a proposed **off-heap memory-tier allocator**, see [`MEMORY_CACHE_ARCHITECTURE_DESIGN.md`](MEMORY_CACHE_ARCHITECTURE_DESIGN.md)
- For **formal requirements and scenarios**, see [`openspec/specs/file-content-cache/spec.md`](../../openspec/specs/file-content-cache/spec.md)

**Two-Level Caching Architecture**:
```
┌─────────────────────────────────────────────────────────────────────────────┐
│                         Complete Caching System                              │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                             │
│  1. Structure Cache (ZIP_STRUCTURE_CACHE_DESIGN.md)                         │
│     • Stores parsed Central Directory metadata                              │
│     • ZipEntryInfo: offset, sizes, compression method                       │
│     • LRU/LFU eviction, access extends TTL                                  │
│     • Memory: ~114 bytes per ZIP entry                                      │
│                                                                             │
│  2. File Content Cache (THIS DOCUMENT)                                      │
│     • Stores decompressed file data                                         │
│     • Memory tier (< 50MB) + Disk tier (≥ 50MB)                             │
│     • Converts sequential ZIP streams to random-access                      │
│     • TTL eviction + capacity limits                                        │
│                                                                             │
└─────────────────────────────────────────────────────────────────────────────┘
```

---

## 1. Problem Statement

### 1.1 The Fundamental Mismatch

**ZIP Archive Reality:**
```csharp
ZipArchiveEntry entry = archive.GetEntry("file.txt");
Stream stream = entry.Open(); // Sequential only, compressed

// To read byte at offset 50000:
// 1. Decompress from byte 0
// 2. Skip first 50000 bytes
// 3. Read desired byte
// 4. Discard stream
```

**Windows File System via DokanNet:**
```csharp
// Windows doesn't read sequentially!
ReadFile(fileName, buffer, offset: 50000, length: 4096);
ReadFile(fileName, buffer, offset: 100000, length: 4096);
ReadFile(fileName, buffer, offset: 25000, length: 4096);
// ↑ Random access at any offset, in any order
```

### 1.2 Performance Impact Without Caching

**Example: Reading 100MB video file from ZIP**

| Operation | Without Cache | With Cache |
|-----------|---------------|------------|
| Read offset 0 (4KB) | Decompress 0→4KB (4KB work) | Decompress entire file once (100MB work) |
| Read offset 1MB (4KB) | Decompress 0→1MB (1MB work) | Seek to 1MB, read (instant) |
| Read offset 50MB (4KB) | Decompress 0→50MB (50MB work) | Seek to 50MB, read (instant) |
| **Total decompression** | **51MB+ decompressed** | **100MB decompressed once** |

**Real-world scenarios:**
- Video player: 1000+ random reads → **Impossible without cache**
- Text editor: Open file at end → **Minutes without cache, instant with cache**
- Windows Explorer thumbnail: Random chunk reads → **Hangs without cache**

### 1.3 Why Dual-Tier?

**Single Tier Problems:**

1. **Memory-only cache:**
   - ❌ Can't cache files larger than RAM
   - ❌ 10GB ZIP with 5GB files → constant eviction/thrashing
   - ❌ Multiple large files → impossible

2. **Disk-only cache:**
   - ❌ Slow for small files (disk I/O overhead)
   - ❌ SSD wear for frequent small file access
   - ❌ Unnecessary disk writes for temporary files

**Dual-Tier Advantages:**
- ✅ Small files (< 50MB): Fast in-memory access
- ✅ Large files (≥ 50MB): Memory-mapped disk files (OS manages actual RAM usage)
- ✅ Optimal for mixed workloads
- ✅ Total capacity: RAM + Disk = 12GB+ effective cache

---

## 2. Requirements

### 2.1 Functional Requirements

**FR1: Materialization**
- System MUST fully decompress ZIP entries into random-access storage
- Materialized content MUST support `Stream.Seek()` to any offset
- Materialization MUST happen transparently (caller unaware)

**FR2: Dual-Tier Storage**
- System MUST route small files (< cutoff) to memory tier
- System MUST route large files (≥ cutoff) to disk tier
- Cutoff MUST be configurable (default: 50MB)

**FR3: Time-To-Live (TTL)**
- Each cache entry MUST have a configurable TTL
- System MUST automatically evict expired entries
- TTL MUST be configurable per-entry and globally (default: 30 minutes)

**FR4: Capacity Limits**
- Memory tier MUST enforce size-based capacity limit (default: 2GB)
- Disk tier MUST enforce size-based capacity limit (default: 10GB)
- System MUST prevent exceeding capacity via eviction

**FR5: Eviction Strategy**
- When capacity exceeded, system MUST evict entries to make space
- Expired entries MUST be evicted first (TTL-based)
- If no expired entries, system MUST evict least-recently-used (LRU)
- System MUST never evict entries currently in use

**FR6: Cache Key Generation**
- Cache key MUST uniquely identify: `{archiveKey}:{entryPath}`
- Example: `"archive.zip:folder/file.txt"`
- Keys MUST be case-sensitive (ZIP paths are case-sensitive)

**FR7: Concurrency (CRITICAL)**
- Cache MUST be thread-safe for concurrent reads
- Cache MUST be thread-safe for concurrent cache entry creation
- Multiple threads MAY read same cached entry simultaneously
- Cache MUST prevent duplicate materialization (thundering herd)
- Cache MUST use multi-layer locking strategy:
  - Layer 1: Lock-free cache lookup (fast path for hits)
  - Layer 2: Per-key materialization lock (prevents thundering herd)
  - Layer 3: Eviction lock (only when evicting, does not block reads)
- Different cache keys MUST NOT block each other during materialization

**FR8: Error Handling**
- Cache MUST handle corrupt ZIP entries gracefully
- Cache MUST handle disk-full scenarios
- Failed materialization MUST NOT cache invalid data

### 2.2 Non-Functional Requirements

**NFR1: Performance**
- Cache hit: < 1ms overhead
- Cache miss (small file): Decompress + cache in < 100ms for 10MB file
- Cache miss (large file): Decompress + cache in < 2s for 100MB file
- Eviction: < 10ms for single entry

**NFR2: Memory Efficiency**
- Memory tier: Zero allocations for cache hits (return existing byte[])
- Disk tier: Use memory-mapped files (OS manages actual RAM)
- No memory leaks (all resources disposed)

**NFR3: Observability**
- Cache MUST expose metrics:
  - Hit rate (hits / total requests)
  - Miss rate
  - Eviction count
  - Current size (bytes)
  - Entry count
- Metrics MUST be real-time queryable

**NFR4: Configurability**
- All limits configurable via appsettings.json
- Support for disabling tiers (e.g., memory-only mode)
- No hardcoded magic numbers

**NFR5: Testability**
- Cache behavior MUST be deterministic
- Time-based logic MUST use `TimeProvider` (mockable)
- All public methods MUST be unit-testable

---

## 3. Architecture Design

### 3.1 Unified Cache Architecture

The caching system uses a **single generic cache implementation** with **pluggable storage strategies**. All caching concerns (TTL, eviction, capacity, concurrency) are handled uniformly. The only variation is how data is stored and cleaned up.

**Key Design Decision:** `GenericCache<T>` exposes only one type parameter `T` (the value type returned to callers). The internal storage representation (`TStored`) is hidden inside the storage strategy, not exposed to users.

```
┌─────────────────────────────────────────────────────────────────┐
│                      ICache<T> Interface                        │
│  GetOrAddAsync(key, ttl, factory) → T                           │
│  Factory returns CacheFactoryResult<T> with size                │
│                                                                 │
│  User only sees T (Stream, ArchiveStructure, etc.)              │
│  Internal storage type is HIDDEN                                │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│                 GenericCache<T> Implementation                  │
│                                                                 │
│  Unified Behavior (same for all cache types):                   │
│  • TTL-based expiration                                         │
│  • Capacity limits (size-based)                                 │
│  • Pluggable eviction policy (LRU, LFU, etc.)                   │
│  • Three-layer concurrency (lock-free hit, per-key, eviction)   │
│  • Thundering herd prevention via Lazy<Task<T>>                 │
│  • Metrics (hit rate, size, eviction count)                     │
│                                                                 │
│  Internal: IStorageStrategy<T> (hidden from user)               │
│  • Stores data as opaque StoredEntry                            │
│  • Retrieves T from StoredEntry                                 │
│  • Handles cleanup/disposal                                     │
└─────────────────────────────────────────────────────────────────┘
                              │
        ┌─────────────────────┼─────────────────────┐
        ▼                     ▼                     ▼
┌───────────────┐   ┌───────────────┐   ┌───────────────────┐
│ MemoryStorage │   │  DiskStorage  │   │  ObjectStorage    │
│   Strategy    │   │   Strategy    │   │    Strategy       │
│               │   │               │   │                   │
│ Internal:     │   │ Internal:     │   │ Internal:         │
│  byte[]       │   │  MMF + path   │   │  T object         │
│               │   │               │   │                   │
│ Returns:      │   │ Returns:      │   │ Returns:          │
│  Stream       │   │  Stream       │   │  T                │
│               │   │               │   │                   │
│ Cleanup:      │   │ Cleanup:      │   │ Cleanup:          │
│  GC           │   │  Dispose MMF  │   │  GC (or           │
│               │   │  + delete file│   │   IDisposable)    │
│               │   │               │   │                   │
│ Use case:     │   │ Use case:     │   │ Use case:         │
│ ICache<Stream>│   │ ICache<Stream>│   │ ICache<T>         │
│ Small files   │   │ Large files   │   │ ZIP structure,    │
│ (< 50MB)      │   │ (≥ 50MB)      │   │ any object        │
└───────────────┘   └───────────────┘   └───────────────────┘

User perspective:
  ICache<Stream> smallFileCache;   // Don't know it uses byte[] internally
  ICache<Stream> largeFileCache;   // Don't know it uses MMF internally
  ICache<ArchiveStructure> structCache;  // Stores object directly
```

### 3.2 What's Unified vs What's Pluggable

| Aspect | Unified (GenericCache) | Pluggable (Strategy) |
|--------|------------------------|----------------------|
| TTL expiration | ✅ | |
| Capacity limits | ✅ | |
| Eviction policy | ✅ (via IEvictionPolicy) | |
| Concurrency handling | ✅ | |
| Thundering herd prevention | ✅ | |
| Metrics collection | ✅ | |
| Cache key management | ✅ | |
| **Data storage** | | ✅ IStorageStrategy |
| **Data retrieval** | | ✅ IStorageStrategy |
| **Data cleanup/disposal** | | ✅ IStorageStrategy |

### 3.3 Cache Key Design

**Format:** `{archiveKey}:{internalPath}`

**Examples:**
```
"archive.zip:file.txt"
"archive.zip:folder/subfolder/document.pdf"
"data.zip:images/photo.jpg"
```

**Why This Format:**
- Uniquely identifies file across all archives
- Simple string comparison
- Human-readable for debugging
- No collisions (archive keys are unique)

### 3.4 Data Flow Diagrams

#### 3.4.1 Cache Miss Flow (Materialization with Borrow)

```
1. DokanNet: ReadFile("archive.zip\folder\file.txt", offset=50000)
   ↓
2. Resolve path: archiveKey="archive.zip", internalPath="folder/file.txt"
   ↓
3. Generate cache key: "archive.zip:folder/file.txt"
   ↓
4. Business logic determines: 80MB file → use disk cache (DiskStorageStrategy)
   ↓
5. GenericCache<Stream>.BorrowAsync(key, ttl=30min, factory)
   ↓
6. GenericCache: Layer 1 (lock-free) - Check ConcurrentDictionary
   └─ MISS: Key not found
   ↓
7. GenericCache: Layer 2 (per-key) - Get/create Lazy<Task<T>> for key
   ├─ Check if another thread materializing same key (thundering herd)
   │  └─ If yes: Wait for same Lazy<Task<T>>, share result
   └─ If no: This thread will materialize
   ↓
8. GenericCache: Delegate to IStorageStrategy.MaterializeAsync(factory)
   ├─ Strategy calls factory internally to get CacheFactoryResult
   ├─ Strategy consumes stream (e.g., DiskStrategy pipes ZIP → temp file)
   └─ Strategy disposes factory resources via CacheFactoryResult.DisposeAsync()
   ↓
9. Strategy returns StoredEntry (opaque, wraps MMF + path or byte[])
   ↓
10. GenericCache: Store entry in ConcurrentDictionary
    └─ _cache[key] = new CacheEntry(storedEntry, RefCount=0, ...)
   ↓
11. GenericCache: Increment RefCount (entry now borrowed)
    └─ entry.IncrementRefCount() → RefCount = 1
   ↓
12. GenericCache: Create CacheHandle and return to caller
    └─ Return new CacheHandle(entry, value, cache)
   ↓
13. Caller: using (handle) { stream.Seek(50000) → Read 4096 bytes }
    └─ Entry PROTECTED from eviction while RefCount > 0
   ↓
14. Caller: handle.Dispose() → RefCount decremented
    └─ entry.DecrementRefCount() → RefCount = 0 → Entry now evictable
```

#### 3.4.2 Cache Hit Flow (with Borrow)

```
1. DokanNet: ReadFile("archive.zip\folder\file.txt", offset=100000)
   ↓
2. Generate cache key: "archive.zip:folder/file.txt"
   ↓
3. GenericCache.BorrowAsync(key, ttl, factory)
   ↓
4. GenericCache: Layer 1 (lock-free) - ConcurrentDictionary.TryGetValue
   ├─ HIT: Entry found
   ├─ Check TTL: Not expired
   ├─ Increment RefCount BEFORE returning: entry.IncrementRefCount()
   ├─ Update LastAccessedAt (for LRU)
   └─ Call IStorageStrategy.Retrieve(storedEntry)
   ↓
5. DiskStorageStrategy.Retrieve → Unwrap MMF, return mmf.CreateViewStream()
   ↓
6. GenericCache: Return CacheHandle to caller
   ↓
7. Caller: using (handle) { stream.Seek(100000) → Read } (< 1ms)
   ↓
8. Caller: handle.Dispose() → RefCount decremented → Entry evictable

Note: Factory is NEVER called on cache hit.
      Entry is PROTECTED during use (RefCount > 0).
```

#### 3.4.3 Eviction Flow (Capacity Exceeded with RefCount Check)

```
1. Cache miss for 200MB file
   ↓
2. GenericCache: currentSize=9.9GB + 200MB > capacity=10GB
   ↓
3. GenericCache: Acquire eviction lock (Layer 3)
   └─ Note: Does NOT block Layer 1 (reads) or Layer 2 (other key materializations)
   ↓
4. GenericCache: Evict expired entries first (TTL-based)
   ├─ Iterate all entries
   ├─ Filter: (now > createdAt + ttl) AND (RefCount == 0)  ← CRITICAL
   │  └─ Skip entries with RefCount > 0 (currently borrowed)
   ├─ For each evictable victim: IStorageStrategy.Dispose(stored)
   │  └─ DiskStorageStrategy: Queue file deletion (async cleanup)
   ├─ Free: 500MB (3 expired, unborrowed entries)
   └─ currentSize now: 9.4GB
   ↓
5. Still need space: 9.4GB + 200MB > 10GB
   ↓
6. GenericCache: LRU eviction via IEvictionPolicy (among non-expired, unborrowed)
   ├─ Filter candidates: Only entries with RefCount == 0  ← CRITICAL
   ├─ IEvictionPolicy.SelectVictims(neededSpace=700MB, evictableEntries)
   ├─ Policy sorts by LastAccessedAt (ascending)
   ├─ Evict oldest unborrowed: entry1 (300MB freed)
   ├─ Evict next unborrowed: entry2 (200MB freed)
   ├─ For each: IStorageStrategy.Dispose(stored)
   ├─ Total freed: 500MB
   └─ currentSize now: 8.9GB
   ↓
7. GenericCache: Release eviction lock
   ↓
8. Now have space: 8.9GB + 200MB < 10GB
   ↓
9. Continue materialization (back to step 8 in Cache Miss Flow)

Note: Entries with RefCount > 0 are NEVER evicted.
      If all entries are borrowed, eviction cannot free space → soft capacity overage.
```

#### 3.4.4 Reference Count Lifecycle

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                        Cache Entry RefCount Lifecycle                        │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                             │
│  ┌─────────────┐        ┌─────────────┐        ┌─────────────┐              │
│  │  RefCount=0 │        │  RefCount=1 │        │  RefCount=2 │              │
│  │   (IDLE)    │───────▶│  (IN USE)   │───────▶│  (IN USE)   │              │
│  │             │ Borrow │             │ Borrow │  2 readers  │              │
│  │  Evictable  │        │  Protected  │        │  Protected  │              │
│  └─────────────┘        └─────────────┘        └─────────────┘              │
│        ▲                      │                      │                      │
│        │                      │ Dispose              │ Dispose              │
│        │                      ▼                      ▼                      │
│        │                ┌─────────────┐        ┌─────────────┐              │
│        │                │  RefCount=0 │        │  RefCount=1 │              │
│        └────────────────│   (IDLE)    │◀───────│  (IN USE)   │              │
│                         │  Evictable  │        │  Protected  │              │
│                         └─────────────┘        └─────────────┘              │
│                                                                             │
│  Eviction Rule: ONLY evict entries where RefCount == 0                      │
│  Thundering Herd: All waiters get handles, all increment RefCount           │
│                                                                             │
└─────────────────────────────────────────────────────────────────────────────┘
```

---

## 4. Detailed Component Design

### 4.1 Generic Cache Interface

```csharp
namespace ZipDrive.Infrastructure.Caching;

/// <summary>
/// Result from cache factory, containing the cached value and metadata.
/// The factory is responsible for preparing the data and reporting its size.
/// Implements IAsyncDisposable to allow storage strategies to dispose the
/// value and chain resource cleanup via OnDisposed.
/// </summary>
/// <typeparam name="T">Type of cached value</typeparam>
public sealed class CacheFactoryResult<T> : IAsyncDisposable
{
    /// <summary>The cached value to store.</summary>
    public required T Value { get; init; }

    /// <summary>
    /// Size in bytes (for capacity tracking and tier routing).
    /// Discovered by the factory during data preparation.
    /// </summary>
    public required long SizeBytes { get; init; }

    /// <summary>
    /// Optional metadata (e.g., content type, compression ratio, original filename).
    /// </summary>
    public IReadOnlyDictionary<string, object>? Metadata { get; init; }

    /// <summary>
    /// Optional callback invoked after Value is disposed.
    /// Use to chain cleanup of owning resources (e.g., dispose an IZipReader
    /// after the decompressed stream has been consumed by the storage strategy).
    /// </summary>
    public Func<ValueTask>? OnDisposed { get; init; }

    public async ValueTask DisposeAsync()
    {
        if (Value is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync();
        else if (Value is IDisposable disposable)
            disposable.Dispose();
        if (OnDisposed is not null)
            await OnDisposed();
    }
}

/// <summary>
/// Handle to a borrowed cache entry. MUST be disposed after use.
/// The entry is protected from eviction while the handle is active.
/// Multiple handles can reference the same entry (reference counted).
/// </summary>
/// <typeparam name="T">Type of cached value</typeparam>
public interface ICacheHandle<T> : IDisposable
{
    /// <summary>The cached value (Stream, ArchiveStructure, etc.)</summary>
    T Value { get; }

    /// <summary>Cache key for debugging/logging</summary>
    string CacheKey { get; }

    /// <summary>Size of the cached entry in bytes</summary>
    long SizeBytes { get; }
}

/// <summary>
/// Generic cache abstraction with pluggable eviction policies.
/// Uses borrow/return pattern to protect entries from eviction during use.
/// </summary>
/// <typeparam name="T">Type of cached value</typeparam>
public interface ICache<T>
{
    /// <summary>
    /// Borrows a cached entry or creates via factory.
    /// The returned handle MUST be disposed after use to allow eviction.
    /// </summary>
    /// <param name="cacheKey">Unique cache key</param>
    /// <param name="ttl">Time-to-live for this entry</param>
    /// <param name="factory">
    /// Factory that produces the value AND its metadata (including size).
    /// The factory is only called on cache miss.
    /// </param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Handle to the cached entry - MUST be disposed after use</returns>
    /// <example>
    /// using (var handle = await cache.BorrowAsync(key, ttl, factory, ct))
    /// {
    ///     var stream = handle.Value;
    ///     await stream.ReadAsync(buffer);  // Safe - won't be evicted
    /// }  // Dispose() allows eviction
    /// </example>
    Task<ICacheHandle<T>> BorrowAsync(
        string cacheKey,
        TimeSpan ttl,
        Func<CancellationToken, Task<CacheFactoryResult<T>>> factory,
        CancellationToken cancellationToken = default);

    /// <summary>Current cache size in bytes</summary>
    long CurrentSizeBytes { get; }

    /// <summary>Cache capacity in bytes</summary>
    long CapacityBytes { get; }

    /// <summary>Cache hit rate (0.0 to 1.0)</summary>
    double HitRate { get; }

    /// <summary>Total number of cached entries</summary>
    int EntryCount { get; }

    /// <summary>Number of entries currently borrowed (RefCount > 0)</summary>
    int BorrowedEntryCount { get; }

    /// <summary>Manually trigger eviction of expired entries (only evicts entries with RefCount = 0)</summary>
    void EvictExpired();
}

/// <summary>
/// File content cache abstraction for materialized ZIP entries.
/// Owns ZIP extraction, tier routing, and caching.
/// Converts sequential ZIP streams into random-access streams.
/// </summary>
public interface IFileContentCache
{
    Task<ICacheHandle<Stream>> GetOrExtractAsync(
        string archiveKey,
        ZipEntryInfo entryInfo,
        string archivePath,
        TimeSpan ttl,
        CancellationToken cancellationToken = default);
    // Also exposes EvictExpired(), ProcessPendingCleanup(), Clear(), etc.
}
```

### 4.2 Storage Strategy Interface

The storage strategy abstracts how cached data is stored, retrieved, and cleaned up. The internal storage representation is hidden from cache users via the opaque `StoredEntry` wrapper.

```csharp
namespace ZipDrive.Infrastructure.Caching;

/// <summary>
/// Opaque wrapper for internally stored data.
/// Hides the actual storage type (byte[], MMF, etc.) from cache users.
/// </summary>
public sealed class StoredEntry
{
    /// <summary>Internal data (byte[], DiskCacheEntry, or T object)</summary>
    internal object Data { get; }

    /// <summary>Size in bytes for capacity tracking</summary>
    internal long SizeBytes { get; }

    internal StoredEntry(object data, long sizeBytes)
    {
        Data = data;
        SizeBytes = sizeBytes;
    }
}

/// <summary>
/// Strategy for storing and retrieving cached data.
/// Implementations handle the specifics of data storage and cleanup.
/// The internal storage type is hidden from cache users.
/// </summary>
/// <typeparam name="TValue">Type of value returned to caller (e.g., Stream, ArchiveStructure)</typeparam>
public interface IStorageStrategy<TValue>
{
    /// <summary>
    /// Calls the factory delegate, consumes the result, disposes factory resources,
    /// and returns an opaque StoredEntry. The strategy owns the full materialization
    /// pipeline — this enables direct streaming (e.g., ZIP → disk) without intermediate buffering.
    /// </summary>
    /// <param name="factory">Factory delegate that produces the value to cache</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Opaque stored entry wrapping the internal representation</returns>
    Task<StoredEntry> MaterializeAsync(
        Func<CancellationToken, Task<CacheFactoryResult<TValue>>> factory,
        CancellationToken cancellationToken);

    /// <summary>
    /// Retrieves the value from the stored entry.
    /// Called on cache hits to return data to the caller.
    /// </summary>
    /// <param name="stored">The opaque stored entry</param>
    /// <returns>The value to return to caller</returns>
    TValue Retrieve(StoredEntry stored);

    /// <summary>
    /// Disposes/cleans up the stored data when evicted.
    /// For GC-managed data, this may be a no-op.
    /// For disk-based storage, this deletes temp files.
    /// </summary>
    /// <param name="stored">The stored entry to clean up</param>
    void Dispose(StoredEntry stored);

    /// <summary>
    /// Whether disposal requires async cleanup (e.g., file deletion).
    /// If true, cache will queue for background cleanup instead of inline disposal.
    /// </summary>
    bool RequiresAsyncCleanup { get; }
}
```

### 4.3 Built-in Storage Strategies

#### 4.3.1 MemoryStorageStrategy (Small Files)

```csharp
/// <summary>
/// Stores file content as byte[] in memory.
/// Use for small files (< 50MB by default).
/// Internal storage: byte[]
/// Returns: Stream (MemoryStream wrapping the byte[])
/// </summary>
public sealed class MemoryStorageStrategy : IStorageStrategy<Stream>
{
    public async Task<StoredEntry> MaterializeAsync(
        Func<CancellationToken, Task<CacheFactoryResult<Stream>>> factory,
        CancellationToken ct)
    {
        await using var result = await factory(ct);
        using var ms = new MemoryStream((int)result.SizeBytes);
        await result.Value.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();
        return new StoredEntry(bytes, result.SizeBytes);
    }

    public Stream Retrieve(StoredEntry stored)
    {
        var bytes = (byte[])stored.Data;
        return new MemoryStream(bytes, writable: false);
    }

    public void Dispose(StoredEntry stored)
    {
        // No-op: byte[] is garbage collected
    }

    public bool RequiresAsyncCleanup => false;
}
```

#### 4.3.2 DiskStorageStrategy (Large Files)

```csharp
/// <summary>
/// Stores file content as memory-mapped file on disk.
/// Use for large files (≥ 50MB by default).
/// Internal storage: DiskCacheEntry (temp file path + MMF)
/// Returns: Stream (MMF view stream)
/// </summary>
public sealed class DiskStorageStrategy : IStorageStrategy<Stream>
{
    private readonly string _tempDirectory;

    public DiskStorageStrategy(string tempDirectory)
    {
        _tempDirectory = tempDirectory;
    }

    public async Task<StoredEntry> MaterializeAsync(
        Func<CancellationToken, Task<CacheFactoryResult<Stream>>> factory,
        CancellationToken ct)
    {
        var tempPath = Path.Combine(_tempDirectory, $"{Guid.NewGuid()}.cache");

        // Call factory and pipe directly to temp file — no intermediate buffer
        await using (var result = await factory(ct))
        {
            await using var fileStream = new FileStream(tempPath, FileMode.Create,
                FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
            await result.Value.CopyToAsync(fileStream, ct);
            await fileStream.FlushAsync(ct);
        }
        // Factory result (and its Value stream + OnDisposed callback) are now disposed

        // Create memory-mapped file for random access
        var mmf = MemoryMappedFile.CreateFromFile(tempPath, FileMode.Open,
            null, result.SizeBytes, MemoryMappedFileAccess.Read);

        var entry = new DiskCacheEntry(tempPath, mmf, result.SizeBytes);
        return new StoredEntry(entry, result.SizeBytes);
    }

    public Stream Retrieve(StoredEntry stored)
    {
        var entry = (DiskCacheEntry)stored.Data;
        return entry.MemoryMappedFile.CreateViewStream(0, entry.Size, MemoryMappedFileAccess.Read);
    }

    public void Dispose(StoredEntry stored)
    {
        var entry = (DiskCacheEntry)stored.Data;
        entry.MemoryMappedFile.Dispose();
        File.Delete(entry.TempFilePath);
    }

    public bool RequiresAsyncCleanup => true; // File deletion can be slow
}

/// <summary>
/// Internal storage representation for disk-cached files.
/// Not exposed to cache users.
/// </summary>
internal sealed record DiskCacheEntry(string TempFilePath, MemoryMappedFile MemoryMappedFile, long Size);
```

#### 4.3.3 ObjectStorageStrategy (ZIP Structure, Metadata)

```csharp
/// <summary>
/// Stores any object directly in memory.
/// Use for parsed structures like ZIP metadata, configuration, etc.
/// Internal storage: T object
/// Returns: T (same object)
/// </summary>
public sealed class ObjectStorageStrategy<T> : IStorageStrategy<T>
{
    public async Task<StoredEntry> MaterializeAsync(
        Func<CancellationToken, Task<CacheFactoryResult<T>>> factory,
        CancellationToken ct)
    {
        await using var result = await factory(ct);
        return new StoredEntry(result.Value!, result.SizeBytes);
    }

    public T Retrieve(StoredEntry stored)
    {
        return (T)stored.Data;
    }

    public void Dispose(StoredEntry stored)
    {
        // Dispose if IDisposable, otherwise GC handles it
        if (stored.Data is IDisposable disposable)
            disposable.Dispose();
    }

    public bool RequiresAsyncCleanup => false;
}
```

### 4.4 GenericCache Implementation

The `GenericCache<T>` implements all caching logic uniformly, delegating only storage concerns to the strategy. It uses a **borrow/return pattern** with **reference counting** to protect entries from eviction while in use.

```csharp
public sealed class GenericCache<T> : ICache<T>
{
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<CacheEntry>>> _materializationTasks = new();
    private readonly ConcurrentQueue<StoredEntry> _pendingCleanup = new();

    private readonly IStorageStrategy<T> _storageStrategy;
    private readonly IEvictionPolicy _evictionPolicy;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _evictionLock = new();
    private readonly long _capacityBytes;
    private long _currentSizeBytes;

    // Metrics
    private long _hits;
    private long _misses;

    public GenericCache(
        IStorageStrategy<T> storageStrategy,
        IEvictionPolicy evictionPolicy,
        long capacityBytes,
        TimeProvider? timeProvider = null)
    {
        _storageStrategy = storageStrategy;
        _evictionPolicy = evictionPolicy;
        _capacityBytes = capacityBytes;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ICacheHandle<T>> BorrowAsync(
        string cacheKey,
        TimeSpan ttl,
        Func<CancellationToken, Task<CacheFactoryResult<T>>> factory,
        CancellationToken cancellationToken = default)
    {
        // ═══════════════════════════════════════════════════════════
        // LAYER 1: Lock-free cache lookup (FAST PATH)
        // ═══════════════════════════════════════════════════════════
        if (_cache.TryGetValue(cacheKey, out var existingEntry) && !IsExpired(existingEntry))
        {
            // Increment RefCount BEFORE returning handle
            existingEntry.IncrementRefCount();
            existingEntry.LastAccessedAt = _timeProvider.GetUtcNow();
            existingEntry.AccessCount++;
            Interlocked.Increment(ref _hits);

            var value = _storageStrategy.Retrieve(existingEntry.Stored);
            return new CacheHandle<T>(existingEntry, value, this);
        }

        // ═══════════════════════════════════════════════════════════
        // LAYER 2: Per-key materialization lock (THUNDERING HERD PREVENTION)
        // ═══════════════════════════════════════════════════════════
        Interlocked.Increment(ref _misses);

        var lazy = _materializationTasks.GetOrAdd(cacheKey, _ =>
            new Lazy<Task<CacheEntry>>(
                () => MaterializeAndCacheAsync(cacheKey, ttl, factory, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            var entry = await lazy.Value;

            // Increment RefCount for each borrower (thundering herd: all get handles)
            entry.IncrementRefCount();

            var value = _storageStrategy.Retrieve(entry.Stored);
            return new CacheHandle<T>(entry, value, this);
        }
        finally
        {
            _materializationTasks.TryRemove(cacheKey, out _);
        }
    }

    /// <summary>
    /// Called by CacheHandle.Dispose() to return the entry to the cache.
    /// Decrements RefCount, allowing eviction when RefCount reaches 0.
    /// </summary>
    internal void Return(CacheEntry entry)
    {
        entry.DecrementRefCount();
    }

    private async Task<CacheEntry> MaterializeAndCacheAsync(
        string cacheKey,
        TimeSpan ttl,
        Func<CancellationToken, Task<CacheFactoryResult<T>>> factory,
        CancellationToken cancellationToken)
    {
        // Strategy owns the full pipeline: call factory → consume stream → dispose resources → return StoredEntry
        var stored = await _storageStrategy.MaterializeAsync(factory, cancellationToken);

        var entry = new CacheEntry(
            cacheKey, stored,
            _timeProvider.GetUtcNow(), ttl);

        // ═══════════════════════════════════════════════════════════
        // CRITICAL: Order matters for consistency!
        // 1. Add to cache FIRST
        // 2. Update size counter SECOND
        //
        // This ensures _currentSizeBytes may temporarily UNDERCOUNT
        // (safe - causes less eviction) but will NEVER OVERCOUNT
        // (dangerous - could prevent needed eviction).
        // ═══════════════════════════════════════════════════════════
        _cache[cacheKey] = entry;
        Interlocked.Add(ref _currentSizeBytes, stored.SizeBytes);

        // ═══════════════════════════════════════════════════════════
        // POST-STORE CAPACITY CHECK (Soft Capacity Design)
        //
        // Multiple concurrent materializations may cause temporary
        // overage. This check ensures we converge back to capacity.
        // See section 4.7 for detailed analysis.
        // ═══════════════════════════════════════════════════════════
        if (Interlocked.Read(ref _currentSizeBytes) > _capacityBytes)
        {
            await EvictIfNeededAsync(neededBytes: 0);
        }

        return entry;
    }

    private async Task EvictIfNeededAsync(long requiredBytes)
    {
        // Fast path: No eviction needed (common case, NO LOCK!)
        if (Interlocked.Read(ref _currentSizeBytes) + requiredBytes <= _capacityBytes)
            return;

        // Slow path: Need to evict
        using (_evictionLock.EnterScope())
        {
            // Double-check after acquiring lock
            if (_currentSizeBytes + requiredBytes <= _capacityBytes)
                return;

            // ═══════════════════════════════════════════════════════════
            // IMPORTANT: Only evict entries with RefCount = 0
            // Entries currently borrowed are protected from eviction
            // ═══════════════════════════════════════════════════════════

            // Phase 1: Evict expired entries (only if not borrowed)
            var now = _timeProvider.GetUtcNow();
            var expiredKeys = _cache
                .Where(kvp => now > kvp.Value.CreatedAt + kvp.Value.Ttl)
                .Where(kvp => kvp.Value.RefCount == 0)  // Only evict if not borrowed
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in expiredKeys)
                TryEvictEntry(key);

            // Phase 2: Use eviction policy if still need space
            if (_currentSizeBytes + requiredBytes > _capacityBytes)
            {
                // Only consider entries with RefCount = 0 for eviction
                var evictableEntries = _cache.Values
                    .Where(e => e.RefCount == 0)
                    .Cast<ICacheEntry>()
                    .ToList();

                var victims = _evictionPolicy.SelectVictims(
                    evictableEntries,
                    requiredBytes, _currentSizeBytes, _capacityBytes);

                foreach (var victim in victims)
                    TryEvictEntry(victim.CacheKey);
            }
        }
    }

    private void TryEvictEntry(string key)
    {
        if (_cache.TryGetValue(key, out var entry))
        {
            // Double-check RefCount before eviction (another thread might have borrowed)
            if (entry.RefCount > 0)
                return;  // Entry is borrowed, skip eviction

            if (_cache.TryRemove(key, out var removed))
            {
                Interlocked.Add(ref _currentSizeBytes, -removed.Stored.SizeBytes);

                if (_storageStrategy.RequiresAsyncCleanup)
                {
                    // Queue for background cleanup
                    _pendingCleanup.Enqueue(removed.Stored);
                }
                else
                {
                    // Inline cleanup (fast, GC-based)
                    _storageStrategy.Dispose(removed.Stored);
                }
            }
        }
    }

    // Background cleanup for strategies that require it (e.g., disk)
    public void ProcessPendingCleanup(int maxItems = 100)
    {
        var processed = 0;
        while (processed < maxItems && _pendingCleanup.TryDequeue(out var stored))
        {
            _storageStrategy.Dispose(stored);
            processed++;
        }
    }

    private bool IsExpired(CacheEntry entry)
        => _timeProvider.GetUtcNow() > entry.CreatedAt + entry.Ttl;

    // ICache<T> properties
    public long CurrentSizeBytes => Interlocked.Read(ref _currentSizeBytes);
    public long CapacityBytes => _capacityBytes;
    public double HitRate => _hits + _misses == 0 ? 0 : (double)_hits / (_hits + _misses);
    public int EntryCount => _cache.Count;
    public int BorrowedEntryCount => _cache.Values.Count(e => e.RefCount > 0);

    public void EvictExpired()
    {
        var now = _timeProvider.GetUtcNow();
        var expiredKeys = _cache
            .Where(kvp => now > kvp.Value.CreatedAt + kvp.Value.Ttl)
            .Where(kvp => kvp.Value.RefCount == 0)  // Only evict if not borrowed
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in expiredKeys)
            TryEvictEntry(key);
    }
}

/// <summary>
/// Handle to a borrowed cache entry. Dispose to return to cache.
/// </summary>
internal sealed class CacheHandle<T> : ICacheHandle<T>
{
    private readonly CacheEntry _entry;
    private readonly GenericCache<T> _cache;
    private int _disposed;

    public T Value { get; }
    public string CacheKey => _entry.CacheKey;
    public long SizeBytes => _entry.SizeBytes;

    internal CacheHandle(CacheEntry entry, T value, GenericCache<T> cache)
    {
        _entry = entry;
        Value = value;
        _cache = cache;
    }

    public void Dispose()
    {
        // Ensure we only decrement RefCount once
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _cache.Return(_entry);
        }
    }
}

/// <summary>
/// Internal cache entry with reference counting.
/// </summary>
internal sealed class CacheEntry : ICacheEntry
{
    public string CacheKey { get; }
    public StoredEntry Stored { get; }
    public long SizeBytes => Stored.SizeBytes;
    public DateTimeOffset CreatedAt { get; }
    public TimeSpan Ttl { get; }
    public DateTimeOffset LastAccessedAt { get; set; }
    public int AccessCount { get; set; }

    /// <summary>
    /// Reference count. Entry can only be evicted when RefCount = 0.
    /// </summary>
    private int _refCount;
    public int RefCount => Volatile.Read(ref _refCount);

    public CacheEntry(string cacheKey, StoredEntry stored,
        DateTimeOffset createdAt, TimeSpan ttl)
    {
        CacheKey = cacheKey;
        Stored = stored;
        CreatedAt = createdAt;
        Ttl = ttl;
        LastAccessedAt = createdAt;
        AccessCount = 1;
        _refCount = 0;  // Starts at 0, incremented when borrowed
    }

    public void IncrementRefCount() => Interlocked.Increment(ref _refCount);
    public void DecrementRefCount() => Interlocked.Decrement(ref _refCount);
}
```

### 4.5 Usage Examples

#### Small File Cache (Memory)
```csharp
// User only sees ICache<Stream> - internal byte[] storage is hidden
ICache<Stream> smallFileCache = new GenericCache<Stream>(
    new MemoryStorageStrategy(),
    new LruEvictionPolicy(),
    capacityBytes: 2L * 1024 * 1024 * 1024); // 2GB
```

#### Large File Cache (Disk)
```csharp
// User only sees ICache<Stream> - internal MMF storage is hidden
ICache<Stream> largeFileCache = new GenericCache<Stream>(
    new DiskStorageStrategy(tempDirectory: Path.GetTempPath()),
    new LruEvictionPolicy(),
    capacityBytes: 10L * 1024 * 1024 * 1024); // 10GB
```

#### ZIP Structure Cache (Object)
```csharp
// User sees ICache<ArchiveStructure> - stores object directly
ICache<ArchiveStructure> structureCache = new GenericCache<ArchiveStructure>(
    new ObjectStorageStrategy<ArchiveStructure>(),
    new LruEvictionPolicy(),
    capacityBytes: 500L * 1024 * 1024); // 500MB
```

#### Borrowing Cache Entries (CRITICAL: Borrow/Return Pattern)
```csharp
// CORRECT: Using borrow/return pattern
public async Task ProcessFileAsync(ICache<Stream> cache, string key, CancellationToken ct)
{
    // Borrow the entry - protected from eviction while handle is active
    using (var handle = await cache.BorrowAsync(key, TimeSpan.FromMinutes(30), factory, ct))
    {
        var stream = handle.Value;

        // Safe to read - entry will NOT be evicted during this block
        await stream.ReadAsync(buffer1, ct);
        await ProcessDataAsync(buffer1);

        stream.Position = 0;  // Can seek and re-read
        await stream.ReadAsync(buffer2, ct);

    }  // Dispose() called → RefCount decremented → Entry now evictable
}

// WRONG: Storing value outside handle scope (DON'T DO THIS)
public async Task<Stream> GetStreamWrong(ICache<Stream> cache, string key, CancellationToken ct)
{
    using (var handle = await cache.BorrowAsync(key, ttl, factory, ct))
    {
        return handle.Value;  // ❌ DANGEROUS: Stream escapes the using block!
    }
    // After this point, entry may be evicted and stream becomes invalid!
}
```

#### Business Layer Routing with Borrow Pattern
```csharp
public class FileContentService
{
    private readonly ICache<Stream> _memoryCache;  // Small files
    private readonly ICache<Stream> _diskCache;    // Large files
    private readonly long _sizeCutoff;

    public FileContentService(
        ICache<Stream> memoryCache,
        ICache<Stream> diskCache,
        long sizeCutoff = 50L * 1024 * 1024)  // 50MB default
    {
        _memoryCache = memoryCache;
        _diskCache = diskCache;
        _sizeCutoff = sizeCutoff;
    }

    /// <summary>
    /// Borrows a file stream from the appropriate cache tier.
    /// Caller MUST dispose the returned handle after use.
    /// </summary>
    public async Task<ICacheHandle<Stream>> BorrowFileAsync(
        string cacheKey,
        long knownFileSize,  // From ZIP Central Directory
        TimeSpan ttl,
        Func<CancellationToken, Task<CacheFactoryResult<Stream>>> factory,
        CancellationToken ct)
    {
        // Route based on known file size (from ZIP structure cache)
        var cache = knownFileSize < _sizeCutoff ? _memoryCache : _diskCache;
        return await cache.BorrowAsync(cacheKey, ttl, factory, ct);
    }
}

// Usage in DokanNet callback:
public async Task<int> ReadFileAsync(string path, byte[] buffer, long offset, CancellationToken ct)
{
    var (archiveKey, entryPath) = ParsePath(path);
    var cacheKey = $"{archiveKey}:{entryPath}";
    var entryInfo = await _structureCache.GetEntryInfoAsync(archiveKey, entryPath);

    using (var handle = await _fileService.BorrowFileAsync(
        cacheKey,
        entryInfo.UncompressedSize,
        TimeSpan.FromMinutes(30),
        ct => ExtractFileAsync(archiveKey, entryPath, ct),
        ct))
    {
        var stream = handle.Value;
        stream.Position = offset;
        return await stream.ReadAsync(buffer, ct);
    }  // Handle disposed → entry can be evicted if needed
}
```

**Note:** The business layer knows the file size from the ZIP Structure Cache (which stores `ZipEntryInfo.UncompressedSize`), so it can route to the appropriate cache before calling the factory.

### 4.6 Multi-Layer Concurrency Strategy (CRITICAL)

**Design Goal:** Maximize concurrency while preventing thundering herd and ensuring thread-safety.

#### 4.6.1 The Concurrency Challenge

```
Scenario: 10 threads request same uncached 100MB file simultaneously

WITHOUT proper locking:
├── All 10 threads decompress the same file
├── 1000MB of redundant work (10 × 100MB)
├── 10× CPU, 10× memory, 10× I/O
└── Race condition: which result wins?

WITH multi-layer locking:
├── Thread 1: Materializes (does the work)
├── Threads 2-10: Wait on per-key lock, get shared result
├── Only 100MB decompressed (1× work)
└── All threads get correct result
```

#### 4.6.2 Three-Layer Locking Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                     GetOrAddAsync(key)                          │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│  LAYER 1: Lock-Free Cache Lookup (FASTEST)                     │
│  ─────────────────────────────────────────────────────────────  │
│  ConcurrentDictionary.TryGetValue() - NO LOCK                   │
│                                                                 │
│  if (_cache.TryGetValue(key, out entry) && !IsExpired(entry))   │
│  {                                                              │
│      entry.LastAccessedAt = now;  // Atomic update              │
│      return entry.CreateStream(); // INSTANT RETURN             │
│  }                                                              │
│                                                                 │
│  Performance: < 100ns (nanoseconds!)                            │
│  Contention: ZERO (lock-free)                                   │
└─────────────────────────────────────────────────────────────────┘
                              │ Cache miss
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│  LAYER 2: Per-Key Materialization Lock (PREVENTS THUNDERING)   │
│  ─────────────────────────────────────────────────────────────  │
│  Each key has its own lock - different keys don't block!        │
│                                                                 │
│  var lazy = _materializationTasks.GetOrAdd(key,                 │
│      _ => new Lazy<Task<CacheEntry>>(                           │
│          () => MaterializeAsync(key, factory),                  │
│          LazyThreadSafetyMode.ExecutionAndPublication));        │
│                                                                 │
│  var entry = await lazy.Value;                                  │
│  // First thread: Executes MaterializeAsync()                   │
│  // Other threads: Await same Task, get same result             │
│                                                                 │
│  Performance: First thread pays materialization cost            │
│               Other threads wait (but don't duplicate work)     │
│  Contention: Only between threads requesting SAME key           │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│  LAYER 3: Eviction Lock (INFREQUENT, NON-BLOCKING)              │
│  ─────────────────────────────────────────────────────────────  │
│  Only acquired when capacity exceeded and eviction needed       │
│                                                                 │
│  // Check if eviction needed BEFORE locking                     │
│  if (_currentSize + sizeBytes <= _capacity)                     │
│      return; // No lock needed! (common case)                   │
│                                                                 │
│  using (_evictionLock.EnterScope())                             │
│  {                                                              │
│      // Double-check after acquiring lock                       │
│      if (_currentSize + sizeBytes <= _capacity)                 │
│          return;                                                │
│                                                                 │
│      // Evict expired first, then use policy                    │
│      EvictExpiredEntries();                                     │
│      EvictUsingPolicy(sizeBytes);                               │
│  }                                                              │
│                                                                 │
│  Performance: Only when cache is full (rare after warmup)       │
│  Contention: Minimal (eviction is infrequent)                   │
│  Note: Does NOT block Layer 1 or Layer 2 operations             │
└─────────────────────────────────────────────────────────────────┘
```

**Note:** The three-layer locking is fully implemented in the `GenericCache<TStored, TValue>` class shown in section 4.4.

#### 4.6.3 Performance Characteristics

| Operation | Lock Type | Contention | Latency |
|-----------|-----------|------------|---------|
| Cache hit (Layer 1) | None (lock-free) | Zero | < 100ns |
| Cache miss, same key (Layer 2) | Per-key Lazy | Same-key only | Wait for materialization |
| Cache miss, different keys (Layer 2) | Per-key Lazy | None | Parallel materialization |
| Eviction needed (Layer 3) | Global Lock | Infrequent | < 1ms (mark only) |

#### 4.6.4 Thundering Herd Prevention Visualization

```
Timeline: 10 threads request same uncached file

Thread 1  ──┬── Layer 1: Miss ──┬── Layer 2: GetOrAdd (creates Lazy) ──┬── Materializing... ──┬── Done
Thread 2  ──┼── Layer 1: Miss ──┼── Layer 2: GetOrAdd (gets same Lazy)─┼── Awaiting...       ──┤── Gets result
Thread 3  ──┼── Layer 1: Miss ──┼── Layer 2: GetOrAdd (gets same Lazy)─┼── Awaiting...       ──┤── Gets result
Thread 4  ──┼── Layer 1: Miss ──┼── Layer 2: GetOrAdd (gets same Lazy)─┼── Awaiting...       ──┤── Gets result
...         │                   │                                      │                       │
Thread 10 ──┴── Layer 1: Miss ──┴── Layer 2: GetOrAdd (gets same Lazy)─┴── Awaiting...       ──┴── Gets result

Result: Only 1 materialization, 10 threads served correctly
```

#### 4.6.5 Different Keys Don't Block Each Other

```
Timeline: 3 threads request different files

Thread 1 (key1) ──┬── Layer 2: Materializing key1... ──────────────────┬── Done
Thread 2 (key2) ──┼── Layer 2: Materializing key2... ──────────────┬───┤── Done (parallel!)
Thread 3 (key3) ──┴── Layer 2: Materializing key3... ──────────┬───┤───┴── Done (parallel!)
                                                               │   │
                                                               │   └── key2 finishes
                                                               └── key3 finishes

Result: All 3 materializations run in parallel (no blocking)
```

### 4.7 Soft Capacity Design and Concurrency Invariants

**Design Goal:** Allow temporary capacity overage for maximum concurrency while ensuring eventual convergence and data consistency.

#### 4.7.1 The Concurrency vs Strict Capacity Tradeoff

**Strict Capacity (Not Used):**
```
To guarantee capacity is NEVER exceeded:
- Must hold lock from eviction check → through materialization → to size update
- Serializes all cache misses (even for different keys!)
- Severely limits concurrency
```

**Soft Capacity (Our Approach):**
```
Allow temporary overage, ensure convergence:
- Pre-store eviction: Make space based on current size
- Post-store check: If over capacity, evict again
- Maximum overage is bounded and transient
```

#### 4.7.2 Worst Case Overage Analysis

**Formula:**
```
Max Overage = (N - 1) × S

Where:
  N = number of concurrent materialization threads (for different keys)
  S = largest entry size being materialized
```

**Scenario:**
```
Initial state: Cache at 100% capacity (e.g., 2GB used, 2GB limit)

Thread1: EvictIfNeededAsync(100MB) → evicts 100MB → 1.9GB used
Thread2: EvictIfNeededAsync(100MB) → sees 1.9GB, fits → no eviction
Thread3: EvictIfNeededAsync(100MB) → sees 1.9GB, fits → no eviction
...
ThreadN: EvictIfNeededAsync(100MB) → sees 1.9GB, fits → no eviction

All threads proceed to materialize simultaneously:

Thread1: Interlocked.Add(100MB) → 2.0GB
Thread2: Interlocked.Add(100MB) → 2.1GB  ← POST-STORE CHECK triggers eviction
Thread3: Interlocked.Add(100MB) → 2.2GB  ← POST-STORE CHECK triggers eviction
...

Result: Temporary overage, but immediately corrected by post-store eviction
```

**Practical Bounds for ZipDrive:**

| Scenario | N (threads) | S (max entry) | Max Overage | % of Capacity |
|----------|-------------|---------------|-------------|---------------|
| Memory tier (typical) | 8 | 50MB | 350MB | 17.5% of 2GB |
| Memory tier (heavy) | 16 | 50MB | 750MB | 37.5% of 2GB |
| Disk tier (typical) | 8 | 500MB | 3.5GB | 35% of 10GB |
| Disk tier (extreme) | 32 | 1GB | 31GB | 310% of 10GB |

**Why This Is Acceptable:**
1. Overage is **transient** (milliseconds, not persistent)
2. Post-store eviction ensures **immediate convergence**
3. Alternative (strict locking) would **kill performance**
4. Memory tier overage bounded by cutoff (50MB default)

#### 4.7.3 `_currentSizeBytes` Consistency Invariants

**Critical Ordering Rules:**

```csharp
// ADD operation - cache first, then size
_cache[cacheKey] = entry;                           // Step 1
Interlocked.Add(ref _currentSizeBytes, sizeBytes);  // Step 2

// REMOVE operation - remove first, then size (only if remove succeeded)
if (_cache.TryRemove(key, out var removed))         // Step 1
{
    Interlocked.Add(ref _currentSizeBytes, -removed.SizeBytes);  // Step 2
    _storageStrategy.Dispose(removed.Stored);       // Step 3
}
```

**Why This Order Matters:**

| Failure Point | Cache-First (Our Design) | Size-First (Dangerous) |
|---------------|--------------------------|------------------------|
| Crash after Step 1 | Size **undercounts** (safe) | Size **overcounts** (dangerous) |
| Effect on eviction | May evict less than needed | May prevent needed eviction |
| Data consistency | Entry exists, will be counted next check | Phantom size, never freed |

**Invariant Guarantee:**
- `_currentSizeBytes` may temporarily **undercount** (safe)
- `_currentSizeBytes` will **never overcount** (dangerous would prevent eviction)

#### 4.7.4 Race Condition Analysis

**Race 1: Add interrupted before size update**
```
Thread1: _cache[key] = entry     → Entry in cache
Thread1: ────── CRASH ──────
Thread1: Interlocked.Add(+size)  → NEVER EXECUTED

Result: _currentSizeBytes undercounts by entry.SizeBytes
Impact: Safe - entry will be found in cache, size will be corrected
        on next access or eviction cycle
```

**Race 2: Concurrent eviction of same key (impossible)**
```
Eviction holds _evictionLock, so same key cannot be evicted twice.
TryRemove returns false for second attempt → no double-decrement.
```

**Race 3: Evict while adding same key**
```
Thread1: _cache[key] = entry              → Entry added
Thread2: _cache.TryRemove(key)            → Returns TRUE (removes Thread1's entry!)
Thread2: Interlocked.Add(-size)           → Size decremented
Thread1: Interlocked.Add(+size)           → Size incremented

Result: Entry removed, but size still increased
        Net effect: _currentSizeBytes overcounts!

MITIGATION: This race is extremely unlikely because:
1. Eviction only happens under _evictionLock
2. Thread1's entry was just added, unlikely to be eviction victim (LRU)
3. Even if it happens, post-store eviction will correct it

If this becomes a real issue, use CAS (Compare-And-Swap) pattern:
- Store entry with version number
- Only increment size if our version is still in cache
```

#### 4.7.5 Post-Store Eviction (Convergence Guarantee)

```csharp
// After storing entry
_cache[cacheKey] = entry;
Interlocked.Add(ref _currentSizeBytes, result.SizeBytes);

// Immediately check and correct any overage
if (Interlocked.Read(ref _currentSizeBytes) > _capacityBytes)
{
    await EvictIfNeededAsync(neededBytes: 0);  // Evict to get back under capacity
}
```

**Guarantee:** After `GetOrAddAsync` returns, cache will be at or below capacity (within eviction granularity).

### 4.8 Pluggable Eviction Policy Architecture

**Design Goal:** Allow different eviction strategies without modifying cache implementation.

#### 4.8.1 IEvictionPolicy Interface

```csharp
namespace ZipDrive.Infrastructure.Caching;

/// <summary>
/// Pluggable eviction policy for selecting victims when capacity exceeded.
/// </summary>
public interface IEvictionPolicy
{
    /// <summary>
    /// Selects entries to evict to free up required space.
    /// </summary>
    /// <param name="entries">All cache entries (non-expired)</param>
    /// <param name="requiredBytes">Space needed for new entry</param>
    /// <param name="currentBytes">Current cache size</param>
    /// <param name="capacityBytes">Maximum cache capacity</param>
    /// <returns>Entries to evict (ordered by priority)</returns>
    IEnumerable<ICacheEntry> SelectVictims(
        IReadOnlyCollection<ICacheEntry> entries,
        long requiredBytes,
        long currentBytes,
        long capacityBytes);
}

/// <summary>
/// Represents a cache entry for eviction policy decisions.
/// </summary>
public interface ICacheEntry
{
    string CacheKey { get; }
    long SizeBytes { get; }
    DateTimeOffset CreatedAt { get; }
    DateTimeOffset LastAccessedAt { get; }
    int AccessCount { get; } // For LFU policy
}
```

#### 4.8.2 Built-in Policies

**LRU (Least Recently Used) - Default:**
```csharp
public class LruEvictionPolicy : IEvictionPolicy
{
    public IEnumerable<ICacheEntry> SelectVictims(
        IReadOnlyCollection<ICacheEntry> entries,
        long requiredBytes,
        long currentBytes,
        long capacityBytes)
    {
        var spaceNeeded = requiredBytes - (capacityBytes - currentBytes);
        var freedSpace = 0L;

        // Evict least recently accessed first
        return entries
            .OrderBy(e => e.LastAccessedAt)
            .TakeWhile(e =>
            {
                if (freedSpace >= spaceNeeded) return false;
                freedSpace += e.SizeBytes;
                return true;
            });
    }
}
```

**LFU (Least Frequently Used) - Future:**
```csharp
public class LfuEvictionPolicy : IEvictionPolicy
{
    public IEnumerable<ICacheEntry> SelectVictims(...)
    {
        // Evict least frequently accessed first
        return entries
            .OrderBy(e => e.AccessCount)
            .ThenBy(e => e.LastAccessedAt) // Tie-breaker
            .TakeWhile(...);
    }
}
```

**Size-First Policy - Future:**
```csharp
public class SizeFirstEvictionPolicy : IEvictionPolicy
{
    public IEnumerable<ICacheEntry> SelectVictims(...)
    {
        // Evict largest files first (free space quickly)
        return entries
            .OrderByDescending(e => e.SizeBytes)
            .TakeWhile(...);
    }
}
```

#### 4.8.3 Async Cleanup Architecture

**Problem:** Deleting large temp files blocks GetOrAddAsync → High latency

**Solution:** Two-phase eviction with async cleanup

```
Phase 1: Mark for Deletion (< 1ms)
  - Set entry.PendingDeletion = true
  - Remove from active cache dictionary
  - Update size counters
  - Return immediately

Phase 2: Async Cleanup (background)
  - Background task periodically processes pending deletions
  - Dispose MemoryMappedFile
  - Delete temp file from disk
  - No blocking of GetOrAddAsync
```

**Implementation:**

```csharp
public sealed class DiskTierCache
{
    private readonly ConcurrentDictionary<string, CachedEntry> _activeCache = new();
    private readonly ConcurrentQueue<CachedEntry> _pendingDeletion = new();
    private readonly Timer _cleanupTimer;

    public DiskTierCache(...)
    {
        // Cleanup pending deletions every 5 seconds
        _cleanupTimer = new Timer(
            _ => ProcessPendingDeletions(),
            null,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5));
    }

    private void EvictEntry(CachedEntry entry)
    {
        // Phase 1: Mark (instant, < 1ms)
        if (_activeCache.TryRemove(entry.CacheKey, out var removed))
        {
            removed.PendingDeletion = true;
            _pendingDeletion.Enqueue(removed);

            // Update size counters immediately (tolerate over-capacity briefly)
            Interlocked.Add(ref _currentSizeBytes, -removed.SizeBytes);

            _logger.LogDebug("Marked for deletion: {Key} ({Size} bytes)",
                removed.CacheKey, removed.SizeBytes);
        }
    }

    private void ProcessPendingDeletions()
    {
        var processed = 0;
        var stopwatch = Stopwatch.StartNew();

        // Process up to 100 deletions per cycle (prevent long-running task)
        while (processed < 100 && _pendingDeletion.TryDequeue(out var entry))
        {
            try
            {
                // Phase 2: Actual cleanup (slow, but async)
                entry.MemoryMappedFile.Dispose();
                File.Delete(entry.TempFilePath);

                _logger.LogDebug("Cleaned up: {Key} (took {Ms}ms)",
                    entry.CacheKey, stopwatch.ElapsedMilliseconds);

                processed++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Cleanup failed for {Key}", entry.CacheKey);
                // Don't re-queue, best effort cleanup
            }
        }

        if (processed > 0)
        {
            _logger.LogInformation(
                "Processed {Count} pending deletions in {Ms}ms",
                processed, stopwatch.ElapsedMilliseconds);
        }
    }

    private record CachedEntry(...)
    {
        public bool PendingDeletion { get; set; }
    }
}
```

**Benefits:**
- ✅ GetOrAddAsync latency: < 1ms (just mark)
- ✅ Cleanup happens asynchronously (no blocking)
- ✅ Tolerates temporary over-capacity (evicted entries still count against size briefly)
- ✅ Batch processing (up to 100 deletions per 5-second cycle)
- ✅ Bounded cleanup time (prevents long-running task)

**Tradeoffs:**
- ⚠️ Temporary over-capacity (freed space not immediately available)
- ⚠️ Cleanup happens with delay (5 seconds max)
- ⚠️ Temp files persist briefly after eviction

**Why Acceptable:**
- Over-capacity is brief and bounded (< 5 seconds)
- Cleanup delay doesn't affect correctness (just resource cleanup)
- Performance benefit (< 1ms eviction) >> resource cost (brief over-capacity)

### 4.9 Reference Counting and Borrow/Return Pattern

**Design Goal:** Prevent eviction of cache entries while they are actively being read, ensuring data consistency without blocking concurrent access.

#### 4.9.1 The Problem: Eviction During Read

```
Without Reference Counting:

Thread1: BorrowAsync("file.bin") → Gets Stream
Thread1: stream.Read(buffer, 0, 4096)  ← Reading chunk 1
                                        │
Thread2: Adds large file, triggers eviction
Thread2: Evicts "file.bin" (LRU victim)
Thread2: Disposes MemoryMappedFile
Thread2: Deletes temp file
                                        │
Thread1: stream.Read(buffer, 4096, 4096)  ← CRASH! Stream invalid

Result: ObjectDisposedException or corrupted read
```

**Traditional Solutions (Not Used):**
1. **Reader-Writer Locks:** Heavy contention, blocks all reads during eviction
2. **Copy-on-Read:** Wasteful for large files, defeats caching purpose
3. **OS File Locking:** Windows-specific, doesn't work for memory tier

#### 4.9.2 Reference Counting Solution

```
With Reference Counting:

Thread1: BorrowAsync("file.bin")
         → RefCount++ → RefCount = 1
         → Returns CacheHandle
Thread1: using (handle) { stream.Read(...) }
                                        │
Thread2: Adds large file, triggers eviction
Thread2: SelectVictims() → "file.bin" has RefCount=1
Thread2: SKIPS "file.bin" (still borrowed!)
Thread2: Evicts different entry instead
                                        │
Thread1: stream.Read(...) ← Safe! Entry protected
Thread1: handle.Dispose()
         → RefCount-- → RefCount = 0
         → Entry now evictable

Result: No corruption, entry evicted only when safe
```

#### 4.9.3 API Design Rationale

**Why Borrow/Return Instead of Get/Release:**
- "Borrow" implies temporary ownership and obligation to return
- `IDisposable` pattern is natural fit (using block = automatic return)
- Familiar to .NET developers (similar to pooled objects)

**Why ICacheHandle<T> Instead of T Directly:**
```csharp
// BAD: Direct return - no way to track when caller is done
T value = await cache.GetAsync(key);
// When is it safe to evict? Unknown!

// GOOD: Handle wrapper - Dispose() signals completion
using (var handle = await cache.BorrowAsync(key, ttl, factory, ct))
{
    T value = handle.Value;
    // Safe to use value here
}  // Dispose() → RefCount-- → Now evictable
```

**Why RefCount Instead of Single Owner:**
```
Scenario: 10 threads read same file simultaneously (common for popular files)

Single Owner:
Thread1: Borrows "popular.jpg" → Success
Thread2: Borrows "popular.jpg" → BLOCKS (waiting for Thread1)
Thread3-10: Also blocked...
Result: Serialized access, terrible performance

Reference Counting:
Thread1: Borrows "popular.jpg" → RefCount=1, Success
Thread2: Borrows "popular.jpg" → RefCount=2, Success
Thread3-10: All succeed, RefCount=10
All threads read in parallel
As each finishes: RefCount decrements
When RefCount=0: Entry becomes evictable
Result: Maximum concurrency, no blocking
```

#### 4.9.4 Implementation Details

**Atomic RefCount Operations:**
```csharp
// Thread-safe increment/decrement using Interlocked
private int _refCount;
public int RefCount => Volatile.Read(ref _refCount);

public void IncrementRefCount() => Interlocked.Increment(ref _refCount);
public void DecrementRefCount() => Interlocked.Decrement(ref _refCount);
```

**Single-Dispose Guarantee in CacheHandle:**
```csharp
private int _disposed;

public void Dispose()
{
    // Interlocked.Exchange ensures exactly-once decrement
    // Even if Dispose() called multiple times
    if (Interlocked.Exchange(ref _disposed, 1) == 0)
    {
        _cache.Return(_entry);  // Decrements RefCount
    }
}
```

**Eviction Filter:**
```csharp
// In EvictIfNeededAsync()
var evictableEntries = _cache.Values
    .Where(e => e.RefCount == 0)  // ONLY unborrowed entries
    .Cast<ICacheEntry>()
    .ToList();

// In TryEvictEntry()
if (entry.RefCount > 0)
    return;  // Double-check before actual removal
```

#### 4.9.5 Edge Cases and Invariants

**Invariant 1: RefCount >= 0 Always**
- Starts at 0 (not borrowed)
- Incremented on BorrowAsync
- Decremented on Dispose
- Never goes negative (single-dispose guarantee)

**Invariant 2: Entry with RefCount > 0 is Never Evicted**
- Eviction filters out borrowed entries
- Double-check before TryRemove
- Even under memory pressure, borrowed entries survive

**Edge Case: All Entries Borrowed**
```
If ALL entries have RefCount > 0:
├─ Eviction cannot free any space
├─ New entry added anyway (soft capacity)
├─ Temporary over-capacity until some entries returned
└─ System converges as handles are disposed

This is acceptable:
- Rare scenario (all cached files actively being read)
- Bounded duration (reads are finite)
- Better than deadlock or data corruption
```

**Edge Case: Thundering Herd with Borrow**
```
10 threads request same uncached file:

Thread1: Creates Lazy<Task<CacheEntry>>
Thread1: Materializes file
Thread1: entry.RefCount = 0 (starts unborrowed)
Thread1: IncrementRefCount → RefCount = 1
Thread1: Returns CacheHandle

Thread2-10: Await same Lazy<Task>
Thread2-10: Each calls IncrementRefCount
Thread2-10: RefCount = 10 (all 10 threads hold handles)

All threads read in parallel
As each disposes: RefCount decrements
When all done: RefCount = 0, evictable
```

**Edge Case: Handle Escapes Using Block**
```csharp
// DANGEROUS PATTERN - DON'T DO THIS
ICacheHandle<Stream> leakedHandle;
using (var handle = await cache.BorrowAsync(...))
{
    leakedHandle = handle;
}  // Dispose() called here!

// leakedHandle.Value is now unsafe
// Entry may have been evicted!

// CORRECT PATTERN
Stream DoWork()
{
    using (var handle = await cache.BorrowAsync(...))
    {
        // Work with handle.Value here
        return ProcessAndCopy(handle.Value);
    }
}
```

#### 4.9.6 Metrics for Reference Counting

```csharp
public interface ICache<T>
{
    // Existing metrics
    int EntryCount { get; }
    long CurrentSizeBytes { get; }
    double HitRate { get; }

    // NEW: Reference counting metrics
    int BorrowedEntryCount { get; }  // Entries with RefCount > 0
}
```

**Useful for Monitoring:**
- High `BorrowedEntryCount` → Many concurrent reads (healthy)
- `BorrowedEntryCount == EntryCount` → All entries borrowed (potential memory pressure)
- Persistent high `BorrowedEntryCount` → Possible handle leak (investigate)

---

## 5. Configuration Schema

### 5.1 Options Class

```csharp
namespace ZipDrive.Infrastructure.Caching;

public class CacheOptions
{
    /// <summary>
    /// Memory tier capacity in MB (default: 2048 = 2GB).
    /// CONFIGURABLE: Adjust based on available system RAM.
    /// </summary>
    public int MemoryCacheSizeMb { get; set; } = 2048;

    /// <summary>
    /// Disk tier capacity in MB (default: 10240 = 10GB).
    /// CONFIGURABLE: Adjust based on available disk space.
    /// </summary>
    public int DiskCacheSizeMb { get; set; } = 10240;

    /// <summary>
    /// Size cutoff in MB (default: 50MB).
    /// CONFIGURABLE: Files smaller than this go to memory tier,
    /// files >= this go to disk tier. Tune based on workload.
    /// </summary>
    public int SmallFileCutoffMb { get; set; } = 50;

    /// <summary>
    /// Temp directory for disk cache (null = system temp).
    /// Must have enough space for DiskCacheSizeMb.
    /// </summary>
    public string? TempDirectory { get; set; }

    /// <summary>Default TTL in minutes (default: 30)</summary>
    public int DefaultTtlMinutes { get; set; } = 30;

    /// <summary>
    /// Eviction check interval in seconds (default: 60).
    /// How often the disk tier scans for expired entries.
    /// </summary>
    public int EvictionCheckIntervalSeconds { get; set; } = 60;
}
```

### 5.2 Configuration File

```json
{
  "Cache": {
    "MemoryCacheSizeMb": 2048,              // 2GB RAM
    "DiskCacheSizeMb": 10240,               // 10GB disk
    "SmallFileCutoffMb": 50,                // Files < 50MB → RAM, >= 50MB → disk
    "TempDirectory": null,                  // null = system temp (e.g., C:\Temp)
    "DefaultTtlMinutes": 30,                // 30 minutes TTL
    "EvictionCheckIntervalSeconds": 60      // Check for expired entries every minute
  }
}
```

### 5.3 Configuration Examples

**Low Memory System (4GB RAM):**
```json
{
  "Cache": {
    "MemoryCacheSizeMb": 512,     // Only 512MB RAM
    "DiskCacheSizeMb": 5120,      // 5GB disk
    "SmallFileCutoffMb": 20       // Lower cutoff (more to disk)
  }
}
```

**High Memory System (32GB RAM):**
```json
{
  "Cache": {
    "MemoryCacheSizeMb": 8192,    // 8GB RAM
    "DiskCacheSizeMb": 20480,     // 20GB disk
    "SmallFileCutoffMb": 100      // Higher cutoff (more in memory)
  }
}
```

**SSD with Fast Disk:**
```json
{
  "Cache": {
    "MemoryCacheSizeMb": 1024,    // 1GB RAM
    "DiskCacheSizeMb": 51200,     // 50GB disk (SSD is fast)
    "SmallFileCutoffMb": 10,      // Aggressive disk usage
    "TempDirectory": "D:\\ZipDriveCache"  // Dedicated SSD
  }
}
```

---

## 6. Testing Strategy

### 6.1 Unit Tests (Target: 80% coverage)

**GenericCache Tests:**
```csharp
[Fact] void CacheMiss_CallsFactoryAndStores()
[Fact] void CacheHit_ReturnsCachedValueWithoutCallingFactory()
[Fact] void TtlExpiration_EntryRemovedAfterTtl()
[Fact] void CapacityExceeded_EvictsToMakeSpace()
[Fact] void ConcurrentAccess_ThreadSafe()
[Fact] void ThunderingHerd_PreventsDuplicateMaterialization()
[Fact] void DifferentKeys_MaterializeInParallel()
[Fact] void Metrics_HitRateCalculatedCorrectly()
```

**Storage Strategy Tests:**
```csharp
// MemoryStorageStrategy
[Fact] void Store_ReturnsCorrectByteArray()
[Fact] void Retrieve_ReturnsReadOnlyMemoryStream()
[Fact] void Dispose_IsNoOp()

// DiskStorageStrategy
[Fact] void Store_CreatesTempFileAndMmf()
[Fact] void Retrieve_ReturnsSeekableViewStream()
[Fact] void Dispose_DeletesTempFile()
[Fact] void RandomAccess_SeekWorks()

// ObjectStorageStrategy
[Fact] void Store_ReturnsOriginalObject()
[Fact] void Retrieve_ReturnsOriginalObject()
[Fact] void Dispose_CallsIDisposableIfImplemented()
```

**Eviction Policy Tests:**
```csharp
[Fact] void LruPolicy_EvictsLeastRecentlyUsed()
[Fact] void LfuPolicy_EvictsLeastFrequentlyUsed()
[Fact] void SizeFirstPolicy_EvictsLargestFirst()
[Fact] void SelectVictims_FreesRequiredSpace()
```

### 6.2 Integration Tests

**Scenario 1: Sequential to Random Access Conversion**
```csharp
[Fact]
public async Task Cache_ConvertsSequentialToRandomAccess()
{
    // Arrange: Create test data (10MB)
    var testData = new byte[10 * 1024 * 1024];
    new Random(42).NextBytes(testData);

    var cache = CreateCache();

    // Act: Cache miss (materialize)
    var stream1 = await cache.GetOrAddAsync(
        "test.zip:file.bin",
        TimeSpan.FromMinutes(10),
        ct => Task.FromResult(new CacheFactoryResult<Stream>
        {
            Value = new MemoryStream(testData),
            SizeBytes = testData.Length
        }),
        CancellationToken.None);

    // Assert: Can seek to any position
    stream1.Seek(5 * 1024 * 1024, SeekOrigin.Begin);
    var buffer1 = new byte[4096];
    await stream1.ReadAsync(buffer1);

    stream1.Seek(1024, SeekOrigin.Begin);
    var buffer2 = new byte[4096];
    await stream1.ReadAsync(buffer2);

    // Verify data matches
    buffer1.Should().Equal(testData.AsSpan(5 * 1024 * 1024, 4096).ToArray());
    buffer2.Should().Equal(testData.AsSpan(1024, 4096).ToArray());
}
```

**Scenario 2: TTL Eviction with Fake Time**
```csharp
[Fact]
public async Task Cache_EvictsAfterTtl()
{
    // Arrange: Fake time provider
    var fakeTime = new FakeTimeProvider();
    var cache = CreateCache(timeProvider: fakeTime);

    // Act: Cache entry with 1 minute TTL
    var factoryCallCount = 0;
    Task<CacheFactoryResult<Stream>> Factory(CancellationToken ct)
    {
        factoryCallCount++;
        return Task.FromResult(new CacheFactoryResult<Stream>
        {
            Value = new MemoryStream(new byte[1024]),
            SizeBytes = 1024
        });
    }

    await cache.GetOrAddAsync("key", TimeSpan.FromMinutes(1), Factory, CancellationToken.None);
    factoryCallCount.Should().Be(1); // Factory called

    // Cache hit (within TTL)
    await cache.GetOrAddAsync("key", TimeSpan.FromMinutes(1), Factory, CancellationToken.None);
    factoryCallCount.Should().Be(1); // Factory NOT called (cache hit)

    // Advance time past TTL
    fakeTime.Advance(TimeSpan.FromSeconds(61));
    cache.EvictExpired();

    // Next access should be cache miss
    await cache.GetOrAddAsync("key", TimeSpan.FromMinutes(1), Factory, CancellationToken.None);
    factoryCallCount.Should().Be(2); // Factory called again
}
```

**Scenario 3: Capacity Enforcement**
```csharp
[Fact]
public async Task DiskCache_EnforcesCapacityWithLruEviction()
{
    // Arrange: 1MB capacity
    var cache = new DiskTierCache(
        capacityBytes: 1024 * 1024,
        tempDirectory: Path.GetTempPath(),
        logger: NullLogger.Instance);

    // Add 500KB file
    await AddTestFile(cache, "file1", 512 * 1024);
    cache.CurrentSizeBytes.Should().Be(512 * 1024);

    // Add 500KB file (total now 1MB)
    await AddTestFile(cache, "file2", 512 * 1024);
    cache.CurrentSizeBytes.Should().Be(1024 * 1024);

    // Add 600KB file (should evict file1 - LRU)
    await AddTestFile(cache, "file3", 600 * 1024);

    // Assert: Capacity not exceeded, file1 evicted
    cache.CurrentSizeBytes.Should().BeLessThanOrEqualTo(1024 * 1024);
    cache.EntryCount.Should().Be(2); // file2 and file3 remain
}

private static async Task AddTestFile(DiskTierCache cache, string key, int size)
{
    var data = new byte[size];
    await cache.GetOrAddAsync(
        key,
        TimeSpan.FromMinutes(30),
        ct => Task.FromResult(new CacheFactoryResult<Stream>
        {
            Value = new MemoryStream(data),
            SizeBytes = size
        }),
        CancellationToken.None);
}
```

---

## 7. Design Decisions (Resolved)

### 7.1 Thundering Herd Protection

**Question:** How to prevent multiple threads from materializing the same entry simultaneously?

**Options:**

**Option A: Per-Key Semaphore**
```csharp
private readonly ConcurrentDictionary<string, SemaphoreSlim> _materializationLocks = new();

public async Task<Stream> GetOrAddAsync(
    string cacheKey,
    TimeSpan ttl,
    Func<CancellationToken, Task<CacheFactoryResult<Stream>>> factory,
    CancellationToken cancellationToken)
{
    var semaphore = _materializationLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
    await semaphore.WaitAsync(cancellationToken);
    try
    {
        // Check cache again (might be populated by another thread)
        if (_cache.TryGetValue(cacheKey, out var entry))
            return entry.CreateStream();

        // Materialize
        // ...
    }
    finally
    {
        semaphore.Release();
    }
}
```
- ✅ Prevents duplicate materialization
- ❌ Complex lock management
- ❌ Memory leak risk (locks never removed)

**Option B: Lazy<T> Pattern**
```csharp
private readonly ConcurrentDictionary<string, Lazy<Task<CachedEntry>>> _cache = new();

public async Task<Stream> GetOrAddAsync(
    string cacheKey,
    TimeSpan ttl,
    Func<CancellationToken, Task<CacheFactoryResult<Stream>>> factory,
    CancellationToken cancellationToken)
{
    var lazy = _cache.GetOrAdd(cacheKey, _ => new Lazy<Task<CachedEntry>>(
        async () => await MaterializeAsync(factory, cancellationToken)));

    var entry = await lazy.Value;
    return entry.CreateStream();
}
```
- ✅ Simple, no explicit locks
- ✅ Built-in once-only guarantee
- ✅ Automatic cleanup on exception
- ❌ Lazy<T> wrapping adds complexity

**Option C: Simple Lock + Double-Check**
```csharp
private readonly Lock _materializationLock = new();

public async Task<Stream> GetOrAddAsync(
    string cacheKey,
    TimeSpan ttl,
    Func<CancellationToken, Task<CacheFactoryResult<Stream>>> factory,
    CancellationToken cancellationToken)
{
    // Fast path: Check cache
    if (_cache.TryGetValue(cacheKey, out var entry))
        return entry.CreateStream();

    // Slow path: Materialize under lock
    using (_materializationLock.EnterScope())
    {
        // Double-check (another thread might have materialized)
        if (_cache.TryGetValue(cacheKey, out entry))
            return entry.CreateStream();

        // Materialize
        // ...
    }
}
```
- ✅ Simple, no per-key state
- ❌ Global lock (serializes ALL misses, not just same key)
- ❌ Bad for concurrent misses on different files

**✅ DECISION:** Use **Lazy<T> pattern** for disk tier to prevent thundering herd. MemoryCache handles this automatically for memory tier.

### 7.2 Stream Ownership & Disposal

**✅ DECISION:** Return **non-disposing wrapper stream**. Cache owns the underlying resources (MemoryMappedFile), callers can safely dispose the wrapper without affecting cache.

### 7.3 Eviction During Read

**✅ DECISION:** Rely on **OS file locking**. Windows automatically prevents deletion of open files. When a stream is open, the temp file can't be deleted until all handles are closed. No extra code needed.

### 7.4 Metrics Collection

**✅ DECISION:** Use **both logs and System.Diagnostics.Metrics** for comprehensive observability:
- Logs: Human-readable debugging
- Metrics: Machine-queryable counters/gauges for monitoring dashboards

### 7.5 Eviction Policy (NEW)

**✅ DECISION:** Make eviction policy **pluggable via Strategy pattern**:
- Interface: `IEvictionPolicy`
- Default: LRU (Least Recently Used)
- Future: LFU, Size-First, Custom policies
- Configurable via DI

### 7.6 Async Cleanup (NEW)

**✅ DECISION:** Use **two-phase eviction with async cleanup**:
- Phase 1: Mark for deletion (< 1ms, non-blocking)
- Phase 2: Background cleanup (async, every 5 seconds)
- Trade temporary over-capacity for low eviction latency
- Acceptable because: cleanup delay doesn't affect correctness

---

## 8. Success Criteria

### 8.1 Functional Requirements Met

- [x] FR1: Materialization works (sequential → random access)
- [ ] FR2: Dual-tier routing works
- [ ] FR3: TTL eviction works
- [ ] FR4: Capacity limits enforced
- [ ] FR5: Eviction strategy (expired first, then LRU)
- [ ] FR6: Cache keys unique and correct
- [ ] FR7: Thread-safe concurrency
- [ ] FR8: Error handling (corrupt entries, disk full)

### 8.2 Non-Functional Requirements Met

- [ ] NFR1: Performance targets met (< 1ms hit, < 100ms miss for 10MB)
- [ ] NFR2: No memory leaks, all resources disposed
- [ ] NFR3: Metrics exposed (hit rate, size, evictions)
- [ ] NFR4: Configuration via appsettings.json
- [ ] NFR5: Testable with mocked time

### 8.3 Test Coverage

- [ ] 80% unit test coverage for caching layer
- [ ] All public methods tested
- [ ] Edge cases covered (TTL boundary, capacity boundary, concurrent access)
- [ ] Integration tests for real ZIP files

---

## 9. Implementation Plan

### Phase 1: Core Interfaces & Models (1 hour)
1. Define `CacheFactoryResult<T>` class
2. Define `ICache<T>` interface
3. Define `IStorageStrategy<TStored, TValue>` interface
4. Define `ICacheEntry` interface
5. Define `CacheOptions` class

### Phase 2: Storage Strategies (2 hours)
6. Implement `MemoryStorageStrategy`
7. Implement `DiskStorageStrategy`
8. Implement `ObjectStorageStrategy<T>`
9. Unit tests for all strategies

### Phase 3: GenericCache (3 hours)
10. Implement `GenericCache<TStored, TValue>`
11. Implement three-layer concurrency
12. Implement TTL expiration
13. Implement capacity management
14. Unit tests for GenericCache

### Phase 4: Eviction Policies (1 hour)
15. Define `IEvictionPolicy` interface
16. Implement `LruEvictionPolicy`
17. Unit tests for eviction policies

### Phase 5: Integration & Testing (3 hours)
18. Integration tests with all storage strategies
19. Performance benchmarks
20. Concurrency stress tests
21. Documentation updates

**Total Estimate: 10 hours**

---

## 10. Summary

**The caching layer is THE solution to the fundamental mismatch between ZIP (sequential) and Windows file system (random access).**

**Key Design Decisions:**
1. **Unified Cache:** Single `GenericCache<TStored, TValue>` handles all caching concerns
2. **Pluggable Storage:** `IStorageStrategy` abstracts data storage/retrieval/cleanup
3. **Generic Interface:** `ICache<T>` with factory returning `CacheFactoryResult<T>` (value + size)
4. **Size Discovery:** Factory discovers size during data preparation, not caller
5. **Three Storage Strategies:**
   - `MemoryStorageStrategy` → byte[] for small files
   - `DiskStorageStrategy` → MemoryMappedFile for large files
   - `ObjectStorageStrategy<T>` → objects for ZIP structure, metadata
6. **TTL:** Automatic expiration prevents stale data and bounded growth
7. **Capacity + Pluggable Eviction:** Bounded resource usage with configurable eviction policy
8. **Three-Layer Concurrency:** Lock-free hits, per-key materialization, eviction lock
9. **Async Cleanup:** Background cleanup for disk strategy (< 1ms eviction latency)
10. **Metrics:** Hit rate, size, eviction count

**Unified Architecture:**
```
                    GenericCache<TStored, TValue>
                    (TTL, Capacity, Eviction, Concurrency)
                              │
        ┌─────────────────────┼─────────────────────┐
        ▼                     ▼                     ▼
   MemoryStorage         DiskStorage          ObjectStorage
   (byte[] → Stream)     (MMF → Stream)       (T → T)
```

**Usage:**
```csharp
// All three caches use the SAME GenericCache implementation
// Only the storage strategy differs

var fileCache = new GenericCache<byte[], Stream>(
    new MemoryStorageStrategy(), evictionPolicy, capacity);

var largeFileCache = new GenericCache<DiskCacheEntry, Stream>(
    new DiskStorageStrategy(tempDir), evictionPolicy, capacity);

var structureCache = new GenericCache<ArchiveStructure, ArchiveStructure>(
    new ObjectStorageStrategy<ArchiveStructure>(), evictionPolicy, capacity);
```

**Without this cache, ZipDrive is completely unusable. With it, performance is comparable to native file access.**

---

## Appendix A: Why NOT Built-in MemoryCache?

- ❌ No pluggable eviction policy
- ❌ Unpredictable compaction when full
- ❌ Can't control which entries get evicted
- ❌ Priority-based eviction, not LRU

Our approach uses a simple custom cache with unified `IEvictionPolicy` interface for both memory and disk tiers.

---

## Appendix B: Design Refinements from Review

**Date:** 2026-01-18
**Status:** ✅ Implementation Complete

### Key Refinements Made:

1. **Unified GenericCache** ✅
   - Single `GenericCache<TStored, TValue>` implementation
   - All caching logic (TTL, eviction, concurrency) in one place
   - Storage strategy pattern for pluggable backends

2. **Storage Strategy Pattern** ✅
   - `IStorageStrategy<TStored, TValue>` interface
   - `MemoryStorageStrategy` for small files (byte[])
   - `DiskStorageStrategy` for large files (MemoryMappedFile)
   - `ObjectStorageStrategy<T>` for ZIP structure, metadata

3. **Generic Cache Interface** ✅
   - `ICache<T>` with `CacheFactoryResult<T>` return type
   - Factory discovers size during data preparation
   - Caller no longer needs to know size upfront

4. **Pluggable Eviction Policy** ✅
   - Strategy pattern with `IEvictionPolicy` interface
   - Default: LRU (Least Recently Used)
   - Extensible without modifying cache implementation

5. **Async Cleanup for Disk Strategy** ✅
   - `RequiresAsyncCleanup` flag on storage strategy
   - Background cleanup queue for slow operations
   - < 1ms eviction latency

### Implementation Priority:

**Phase 1: Core Functionality**
1. `CacheFactoryResult<T>` class
2. `ICache<T>` interface
3. `IStorageStrategy<TStored, TValue>` interface
4. `GenericCache<TStored, TValue>` implementation

**Phase 2: Storage Strategies**
5. `MemoryStorageStrategy`
6. `DiskStorageStrategy`
7. `ObjectStorageStrategy<T>`

**Phase 3: Eviction & Observability**
8. `IEvictionPolicy` interface
9. `LruEvictionPolicy`
10. Metrics integration

### Architecture Highlights:

```
Key Principles:
✅ Unified: One GenericCache handles all caching concerns
✅ Pluggable: Storage strategy abstracts data handling
✅ Generic: ICache<T> works with any type
✅ Reusable: Same cache for files AND ZIP structure
✅ Size Discovery: Factory reports size, not caller
✅ Performance: Async cleanup, < 1ms eviction latency
✅ Testability: TimeProvider, mockable strategies
```

### Success Metrics:

- **Latency:** Cache hit < 1ms, eviction < 1ms (mark only)
- **Throughput:** Support 100+ concurrent GetOrAddAsync
- **Capacity:** Enforce limits, never exceed (except briefly during cleanup)
- **Correctness:** No race conditions, no memory leaks
- **Extensibility:** Can add new storage strategies without modifying cache

**Ready to implement!** 🚀

---

## Related Documentation

- [`ZIP_STRUCTURE_CACHE_DESIGN.md`](ZIP_STRUCTURE_CACHE_DESIGN.md) - ZIP Central Directory structure cache
- [`CONCURRENCY_STRATEGY.md`](CONCURRENCY_STRATEGY.md) - Multi-layer locking details
- [`IMPLEMENTATION_CHECKLIST.md`](IMPLEMENTATION_CHECKLIST.md) - Implementation progress tracking
