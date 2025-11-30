# ZipDrive V3 - Caching Layer Design Document

**Version:** 1.0
**Last Updated:** 2025-11-23
**Status:** Design Phase

---

## Executive Summary

**The Core Problem**: ZIP archives provide only **sequential access** (compressed streams), but Windows file system operations via DokanNet require **random access** at arbitrary offsets. Without caching, every read operation would require full decompression from the beginning, making the system **completely unusable**.

**The Solution**: Dual-tier caching that materializes (fully decompresses) ZIP entries into random-access storage with TTL-based expiration and capacity limits.

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

### 3.1 Component Overview

```
┌─────────────────────────────────────────────────────────┐
│                  IFileCache (Interface)                  │
│  GetOrAddAsync(key, size, ttl, factory)                 │
└─────────────────────────────────────────────────────────┘
                            ↓
┌─────────────────────────────────────────────────────────┐
│              DualTierFileCache (Coordinator)             │
│  - Routes to appropriate tier based on size              │
│  - Manages tier selection cutoff                         │
│  - Aggregates metrics from both tiers                    │
└─────────────────────────────────────────────────────────┘
            ↓                              ↓
┌──────────────────────────┐  ┌──────────────────────────┐
│   MemoryTierCache        │  │   DiskTierCache          │
│  (Small files < 50MB)    │  │  (Large files ≥ 50MB)    │
│                          │  │                          │
│  - MemoryCache backend   │  │  - MemoryMappedFile      │
│  - Byte[] storage        │  │  - Temp file storage     │
│  - Auto TTL eviction     │  │  - Manual LRU eviction   │
│  - Size-based eviction   │  │  - Periodic cleanup      │
└──────────────────────────┘  └──────────────────────────┘
```

### 3.2 Cache Key Design

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

### 3.3 Data Flow Diagrams

#### 3.3.1 Cache Miss Flow (Materialization)

```
1. DokanNet: ReadFile("archive.zip\folder\file.txt", offset=50000)
   ↓
2. Resolve path: archiveKey="archive.zip", internalPath="folder/file.txt"
   ↓
3. Generate cache key: "archive.zip:folder/file.txt"
   ↓
4. IFileCache.GetOrAddAsync(key, sizeBytes=80MB, ttl=30min, factory)
   ↓
5. DualTierFileCache: size >= 50MB → Route to DiskTierCache
   ↓
6. DiskTierCache.GetOrAddAsync:
   ├─ Check cache: MISS
   ├─ Check if thundering herd (another thread materializing same key)
   │  └─ If yes: Wait for other thread, then return result
   ├─ Acquire materialization lock for this key
   ├─ Call factory: Open ZIP entry → Sequential stream
   ├─ Create temp file: /tmp/{guid}.zip2vd
   ├─ Decompress fully: stream.CopyTo(tempFile) [80MB written]
   ├─ Create MemoryMappedFile from temp file
   ├─ Store: _cache[key] = new CacheEntry(mmf, size, createdAt, ttl)
   ├─ Release materialization lock
   └─ Return: mmf.CreateViewStream() [Random-access stream!]
   ↓
7. Caller: stream.Seek(50000) → Read 4096 bytes → Instant!
   ↓
8. Future reads: Cache HIT → Return existing MMF → Instant!
```

#### 3.3.2 Cache Hit Flow

```
1. DokanNet: ReadFile("archive.zip\folder\file.txt", offset=100000)
   ↓
2. Generate cache key: "archive.zip:folder/file.txt"
   ↓
3. IFileCache.GetOrAddAsync(key, ..., factory)
   ↓
4. DiskTierCache.GetOrAddAsync:
   ├─ Check cache: HIT (entry exists, not expired)
   ├─ Update LastAccessedAt (for LRU)
   └─ Return: existingMmf.CreateViewStream()
   ↓
5. Caller: stream.Seek(100000) → Read → Instant! (< 1ms)
```

#### 3.3.3 Eviction Flow (Capacity Exceeded)

```
1. Cache miss for 200MB file
   ↓
2. DiskTierCache: currentSize=9.9GB + 200MB > capacity=10GB
   ↓
3. Acquire eviction lock (prevent concurrent eviction)
   ↓
4. Evict expired entries first:
   ├─ Iterate all entries
   ├─ If (now > createdAt + ttl): Evict
   ├─ Free: 500MB (3 expired entries)
   └─ currentSize now: 9.4GB
   ↓
5. Still need space: 9.4GB + 200MB > 10GB
   ↓
6. LRU eviction (among non-expired):
   ├─ Sort by LastAccessedAt (ascending)
   ├─ Evict oldest: entry1 (300MB freed)
   ├─ Evict next: entry2 (200MB freed)
   ├─ Total freed: 500MB
   └─ currentSize now: 8.9GB
   ↓
7. Release eviction lock
   ↓
8. Now have space: 8.9GB + 200MB < 10GB
   ↓
9. Materialize new entry: Success!
```

---

## 4. Detailed Component Design

### 4.1 IFileCache Interface

```csharp
namespace ZipDriveV3.Infrastructure.Caching;

/// <summary>
/// File cache abstraction for materialized ZIP entries.
/// Converts sequential ZIP streams into random-access streams.
/// </summary>
public interface IFileCache
{
    /// <summary>
    /// Gets cached entry or materializes via factory.
    /// Returns a random-access stream that supports Seek().
    /// </summary>
    /// <param name="cacheKey">Unique key: {archiveKey}:{entryPath}</param>
    /// <param name="sizeBytes">Uncompressed size (for tier routing & capacity)</param>
    /// <param name="ttl">Time-to-live for this entry</param>
    /// <param name="factory">Decompression factory (returns sequential ZIP stream)</param>
    /// <returns>Random-access stream (seekable)</returns>
    Task<Stream> GetOrAddAsync(
        string cacheKey,
        long sizeBytes,
        TimeSpan ttl,
        Func<CancellationToken, Task<Stream>> factory,
        CancellationToken cancellationToken = default);

    /// <summary>Current cache size in bytes (both tiers)</summary>
    long CurrentSizeBytes { get; }

    /// <summary>Cache capacity in bytes (both tiers)</summary>
    long CapacityBytes { get; }

    /// <summary>Cache hit rate (0.0 to 1.0)</summary>
    double HitRate { get; }

    /// <summary>Total number of cached entries (both tiers)</summary>
    int EntryCount { get; }

    /// <summary>Manually trigger eviction of expired entries</summary>
    void EvictExpired();
}
```

### 4.2 DualTierFileCache (Coordinator)

**Responsibilities:**
- Route requests to appropriate tier based on file size
- Aggregate metrics from both tiers
- Coordinate tier-specific operations
- Track overall hit/miss rates

**Key Design Decisions:**
- No caching logic itself, purely a coordinator
- Thread-safe routing (no locks needed, stateless routing)
- Metrics aggregated on-demand

### 4.3 MemoryTierCache (Small Files)

**Technology:** Custom simple cache with `ConcurrentDictionary<string, CacheEntry>`

**Storage:** `byte[]` in RAM

**Why NOT Built-in MemoryCache?**

We initially considered using `Microsoft.Extensions.Caching.Memory.MemoryCache`, but it has critical limitations:

| Feature | MemoryCache | Our Requirement |
|---------|-------------|-----------------|
| TTL Support | ✅ Yes | ✅ Need |
| Size Limits | ⚠️ Compacts unpredictably | ❌ Need control |
| Eviction Policy | ❌ No (priority-based only) | ❌ Need pluggable LRU/LFU |
| Eviction Order | ❌ Unpredictable | ❌ Need deterministic |

**MemoryCache Problems:**
- When full, it "compacts" by evicting entries based on priority, NOT LRU
- You cannot control **which** entries get evicted
- No way to plug in custom eviction policies
- Eviction order is unpredictable

**Our Solution: Simple Custom Cache**

A lightweight cache that:
- Uses `ConcurrentDictionary` for thread-safe storage
- Implements `IEvictionPolicy` (same interface as disk tier!)
- Handles TTL via `CreatedAt + Ttl` comparison
- Uses simple eviction lock (only during eviction, not reads)

**Why This is NOT V1's Over-Engineering:**

| Aspect | V1 Custom LRU | V3 Simple Cache |
|--------|---------------|-----------------|
| Locking | Per-key semaphores + global lock | Single eviction lock |
| Complexity | 180 lines, deadlock risks | ~60 lines, simple |
| Data Structure | LinkedList + 2 ConcurrentDictionaries | Just ConcurrentDictionary |
| LRU Tracking | Manual LinkedList management | Timestamps + policy |
| Eviction Policy | Hardcoded LRU | Pluggable IEvictionPolicy |

**Key Features:**
- **Unified Architecture**: Same `IEvictionPolicy` as disk tier
- **Predictable Eviction**: We control exactly what gets evicted
- **Simple Locking**: Only lock during eviction, not on cache hits
- **TTL Support**: Manual expiration check (fast, O(1))

**Implementation Sketch:**

```csharp
public sealed class MemoryTierCache
{
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private readonly IEvictionPolicy _evictionPolicy;
    private readonly Lock _evictionLock = new(); // .NET 10
    private readonly TimeProvider _timeProvider;
    private readonly long _capacityBytes;
    private long _currentSizeBytes;

    public async Task<Stream> GetOrAddAsync(
        string cacheKey, long sizeBytes, TimeSpan ttl,
        Func<CancellationToken, Task<Stream>> factory,
        CancellationToken cancellationToken)
    {
        // Fast path: Cache hit (no lock!)
        if (_cache.TryGetValue(cacheKey, out var entry) && !IsExpired(entry))
        {
            entry.LastAccessedAt = _timeProvider.GetUtcNow();
            entry.AccessCount++;
            return new MemoryStream(entry.Data, writable: false);
        }

        // Slow path: Cache miss
        await EvictIfNeededAsync(sizeBytes);
        var data = await MaterializeAsync(factory, cancellationToken);

        var newEntry = new CacheEntry(cacheKey, data, sizeBytes,
                                       _timeProvider.GetUtcNow(), ttl);
        _cache[cacheKey] = newEntry;
        Interlocked.Add(ref _currentSizeBytes, sizeBytes);

        return new MemoryStream(data, writable: false);
    }

    private void EvictIfNeeded(long requiredBytes)
    {
        if (_currentSizeBytes + requiredBytes <= _capacityBytes)
            return; // No eviction needed

        using (_evictionLock.EnterScope())
        {
            // Phase 1: Evict expired entries first
            foreach (var entry in _cache.Values.Where(IsExpired).ToList())
            {
                if (_cache.TryRemove(entry.CacheKey, out _))
                    Interlocked.Add(ref _currentSizeBytes, -entry.SizeBytes);
            }

            // Phase 2: Use eviction policy if still need space
            if (_currentSizeBytes + requiredBytes > _capacityBytes)
            {
                var victims = _evictionPolicy.SelectVictims(
                    _cache.Values.Cast<ICacheEntry>().ToList(),
                    requiredBytes, _currentSizeBytes, _capacityBytes);

                foreach (var victim in victims)
                {
                    if (_cache.TryRemove(victim.CacheKey, out _))
                        Interlocked.Add(ref _currentSizeBytes, -victim.SizeBytes);
                }
            }
        }
    }

    private bool IsExpired(CacheEntry entry) =>
        _timeProvider.GetUtcNow() > entry.CreatedAt + entry.Ttl;
}
```

**Benefits of This Approach:**
- ✅ **Unified**: Same eviction policy interface as disk tier
- ✅ **Simple**: ~60 lines vs V1's 180 lines
- ✅ **Predictable**: Deterministic eviction order
- ✅ **Fast**: No lock on cache hits
- ✅ **Testable**: TimeProvider for deterministic tests

### 4.4 DiskTierCache (Large Files)

**Technology:** `MemoryMappedFile` on temporary disk storage

**Storage:** Temp files + Memory-mapped file handles

**Key Features:**
- True random access via `CreateViewStream()`
- OS manages actual memory usage (virtual memory)
- Manual LRU + TTL eviction
- Periodic cleanup timer (every 1 minute)

**Why Memory-Mapped Files?**
- `Stream.Seek()` works perfectly (random access)
- OS caches hot pages in RAM automatically
- Can handle files larger than RAM (virtual memory)
- Proven in V1 to work well

**Eviction Strategy:**
1. **Periodic TTL scan** (every 1 minute via Timer)
2. **On-demand eviction** (when adding new entry and capacity exceeded)
3. **Two-phase eviction:**
   - Phase 1: Evict all expired entries first
   - Phase 2: If still need space, use pluggable eviction policy (default: LRU)
4. **Async cleanup:** Evicted entries marked for deletion and cleaned up asynchronously to minimize latency

**Pluggable Eviction Policy:**
- Strategy pattern for eviction algorithms
- Default: LRU (Least Recently Used)
- Future: LFU (Least Frequently Used), Size-based, Custom policies
- Interface: `IEvictionPolicy`

**Async Cleanup Strategy:**
- Eviction DOES NOT block GetOrAddAsync
- Evicted entries marked with `PendingDeletion` flag
- Background task cleans up marked entries
- Tolerates temporary over-capacity (evicted but not yet cleaned)
- Benefits: < 1ms eviction latency (just mark), cleanup happens async

### 4.5 Multi-Layer Concurrency Strategy (CRITICAL)

**Design Goal:** Maximize concurrency while preventing thundering herd and ensuring thread-safety.

#### 4.5.1 The Concurrency Challenge

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

#### 4.5.2 Three-Layer Locking Architecture

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

#### 4.5.3 Implementation: Per-Key Lock with Lazy<Task<T>>

**Why Lazy<Task<T>>?**
- `Lazy<T>` guarantees single execution (exactly-once semantics)
- `LazyThreadSafetyMode.ExecutionAndPublication` ensures thread-safety
- All waiting threads share the same `Task` (no duplicate work)
- Clean exception handling (failed materialization doesn't cache error)

**Implementation:**

```csharp
public sealed class MemoryTierCache
{
    // Layer 1: Main cache storage (lock-free reads)
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();

    // Layer 2: Per-key materialization locks
    private readonly ConcurrentDictionary<string, Lazy<Task<CacheEntry>>> _materializationTasks = new();

    // Layer 3: Eviction lock (only for capacity management)
    private readonly Lock _evictionLock = new();

    public async Task<Stream> GetOrAddAsync(
        string cacheKey,
        long sizeBytes,
        TimeSpan ttl,
        Func<CancellationToken, Task<Stream>> factory,
        CancellationToken cancellationToken)
    {
        // ═══════════════════════════════════════════════════════════
        // LAYER 1: Lock-free cache lookup (FAST PATH)
        // ═══════════════════════════════════════════════════════════
        if (_cache.TryGetValue(cacheKey, out var existingEntry) && !IsExpired(existingEntry))
        {
            // Update access time for LRU (atomic, no lock needed)
            existingEntry.LastAccessedAt = _timeProvider.GetUtcNow();
            existingEntry.AccessCount++;

            _metrics.RecordHit();
            return new MemoryStream(existingEntry.Data, writable: false);
        }

        // ═══════════════════════════════════════════════════════════
        // LAYER 2: Per-key materialization lock (THUNDERING HERD PREVENTION)
        // ═══════════════════════════════════════════════════════════
        _metrics.RecordMiss();

        var lazy = _materializationTasks.GetOrAdd(cacheKey, _ =>
            new Lazy<Task<CacheEntry>>(
                () => MaterializeAndCacheAsync(cacheKey, sizeBytes, ttl, factory, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            var entry = await lazy.Value;
            return new MemoryStream(entry.Data, writable: false);
        }
        finally
        {
            // Clean up the Lazy after materialization completes
            // (success or failure - we don't want to cache errors)
            _materializationTasks.TryRemove(cacheKey, out _);
        }
    }

    private async Task<CacheEntry> MaterializeAndCacheAsync(
        string cacheKey,
        long sizeBytes,
        TimeSpan ttl,
        Func<CancellationToken, Task<Stream>> factory,
        CancellationToken cancellationToken)
    {
        // ═══════════════════════════════════════════════════════════
        // LAYER 3: Eviction (only if capacity exceeded)
        // ═══════════════════════════════════════════════════════════
        await EvictIfNeededAsync(sizeBytes);

        // Materialize (decompress)
        using var sourceStream = await factory(cancellationToken);
        using var ms = new MemoryStream((int)sizeBytes);
        await sourceStream.CopyToAsync(ms, cancellationToken);
        var data = ms.ToArray();

        // Create and store cache entry
        var entry = new CacheEntry(
            cacheKey, data, sizeBytes,
            _timeProvider.GetUtcNow(), ttl);

        _cache[cacheKey] = entry;
        Interlocked.Add(ref _currentSizeBytes, sizeBytes);

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

            // Phase 1: Evict expired entries
            var now = _timeProvider.GetUtcNow();
            var expiredKeys = _cache
                .Where(kvp => now > kvp.Value.CreatedAt + kvp.Value.Ttl)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in expiredKeys)
            {
                if (_cache.TryRemove(key, out var removed))
                    Interlocked.Add(ref _currentSizeBytes, -removed.SizeBytes);
            }

            // Phase 2: Use eviction policy if still need space
            if (_currentSizeBytes + requiredBytes > _capacityBytes)
            {
                var victims = _evictionPolicy.SelectVictims(
                    _cache.Values.Cast<ICacheEntry>().ToList(),
                    requiredBytes, _currentSizeBytes, _capacityBytes);

                foreach (var victim in victims)
                {
                    if (_cache.TryRemove(victim.CacheKey, out var removed))
                        Interlocked.Add(ref _currentSizeBytes, -removed.SizeBytes);
                }
            }
        }
    }
}
```

#### 4.5.4 Performance Characteristics

| Operation | Lock Type | Contention | Latency |
|-----------|-----------|------------|---------|
| Cache hit (Layer 1) | None (lock-free) | Zero | < 100ns |
| Cache miss, same key (Layer 2) | Per-key Lazy | Same-key only | Wait for materialization |
| Cache miss, different keys (Layer 2) | Per-key Lazy | None | Parallel materialization |
| Eviction needed (Layer 3) | Global Lock | Infrequent | < 1ms (mark only) |

#### 4.5.5 Thundering Herd Prevention Visualization

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

#### 4.5.6 Different Keys Don't Block Each Other

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

### 4.6 Pluggable Eviction Policy Architecture

**Design Goal:** Allow different eviction strategies without modifying cache implementation.

#### 4.5.1 IEvictionPolicy Interface

```csharp
namespace ZipDriveV3.Infrastructure.Caching;

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

#### 4.5.2 Built-in Policies

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

#### 4.5.3 Async Cleanup Architecture

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

---

## 5. Configuration Schema

### 5.1 Options Class

```csharp
namespace ZipDriveV3.Infrastructure.Caching;

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

**MemoryTierCache Tests:**
```csharp
[Fact] void CacheMiss_MaterializesAndStores()
[Fact] void CacheHit_ReturnsSameData()
[Fact] void TtlExpiration_EntryRemovedAfterTtl()
[Fact] void CapacityExceeded_EvictsToMakeSpace()
[Fact] void ConcurrentAccess_ThreadSafe()
[Fact] void EvictionCallback_UpdatesMetrics()
```

**DiskTierCache Tests:**
```csharp
[Fact] void CacheMiss_CreatesTempFileAndMmf()
[Fact] void CacheHit_ReturnsMmfStream()
[Fact] void RandomAccess_SeekWorks()
[Fact] void TtlExpiration_DeletesTempFile()
[Fact] void LruEviction_OldestAccessedEvictedFirst()
[Fact] void CapacityEnforcement_NeverExceedsLimit()
[Fact] void PeriodicCleanup_EvictesExpiredEntries()
[Fact] void ThunderingHerd_PreventsDuplicateMaterialization()
```

**DualTierFileCache Tests:**
```csharp
[Fact] void SmallFile_RoutesToMemoryTier()
[Fact] void LargeFile_RoutesToDiskTier()
[Fact] void CutoffBoundary_RoutesCorrectly()
[Fact] void Metrics_HitRateCalculatedCorrectly()
[Fact] void AggregateMetrics_SumsFromBothTiers()
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
        testData.Length,
        TimeSpan.FromMinutes(10),
        ct => Task.FromResult<Stream>(new MemoryStream(testData)));

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
    var factory = (CancellationToken ct) =>
    {
        factoryCallCount++;
        return Task.FromResult<Stream>(new MemoryStream(new byte[1024]));
    };

    await cache.GetOrAddAsync("key", 1024, TimeSpan.FromMinutes(1), factory);
    factoryCallCount.Should().Be(1); // Factory called

    // Cache hit (within TTL)
    await cache.GetOrAddAsync("key", 1024, TimeSpan.FromMinutes(1), factory);
    factoryCallCount.Should().Be(1); // Factory NOT called (cache hit)

    // Advance time past TTL
    fakeTime.Advance(TimeSpan.FromSeconds(61));
    cache.EvictExpired();

    // Next access should be cache miss
    await cache.GetOrAddAsync("key", 1024, TimeSpan.FromMinutes(1), factory);
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
```

---

## 7. Design Decisions (Resolved)

### 7.1 Thundering Herd Protection

**Question:** How to prevent multiple threads from materializing the same entry simultaneously?

**Options:**

**Option A: Per-Key Semaphore (V1 approach)**
```csharp
private readonly ConcurrentDictionary<string, SemaphoreSlim> _materializationLocks = new();

public async Task<Stream> GetOrAddAsync(...)
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

public async Task<Stream> GetOrAddAsync(...)
{
    var lazy = _cache.GetOrAdd(cacheKey, _ => new Lazy<Task<CachedEntry>>(
        async () => await MaterializeAsync(...)));

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

public async Task<Stream> GetOrAddAsync(...)
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
1. Define `IFileCache` interface
2. Define `CacheOptions` class
3. Create `NonDisposingStream` wrapper

### Phase 2: Memory Tier (2 hours)
4. Implement `MemoryTierCache` with `ConcurrentDictionary`
5. Implement eviction using `IEvictionPolicy`
6. Unit tests for memory tier

### Phase 3: Disk Tier (4 hours)
7. Implement `DiskTierCache`
8. Implement eviction logic (TTL + LRU)
9. Implement periodic cleanup timer
10. Unit tests for disk tier

### Phase 4: Dual-Tier Coordinator (1 hour)
11. Implement `DualTierFileCache`
12. Routing logic based on file size
13. Aggregate metrics

### Phase 5: Integration & Testing (3 hours)
14. Integration tests with real data
15. Performance benchmarks
16. Concurrency stress tests
17. Documentation updates

**Total Estimate: 11 hours**

---

## 10. Summary

**The caching layer is THE solution to the fundamental mismatch between ZIP (sequential) and Windows file system (random access).**

**Key Design Decisions:**
1. **Materialization:** Decompress fully, store in seekable format
2. **Dual-tier:** Memory for small files, disk (MMF) for large files
3. **TTL:** Automatic expiration prevents stale data and bounded growth
4. **Capacity + Pluggable Eviction:** Bounded resource usage with configurable eviction policy
5. **Simple Custom Cache:** NOT built-in MemoryCache (lacks pluggable eviction)
6. **Unified Architecture:** Same `IEvictionPolicy` for both tiers
7. **Lazy<T> for thundering herd:** Simple, built-in once-only guarantee
8. **Non-disposing streams:** Safe concurrent access
8. **Metrics + Logs:** Comprehensive observability

**Without this cache, ZipDrive is completely unusable. With it, performance is comparable to native file access.**

---

## Appendix A: V1 vs V3 Caching Comparison

| Aspect | V1 (Custom LRU) | V3 (Simple Custom Cache) |
|--------|-----------------|--------------------------|
| **Complexity** | 180 lines, complex | ~60 lines, simple |
| **Locking** | Per-key semaphore + global lock | Single eviction lock only |
| **TTL** | Manual tracking | Manual (CreatedAt + Ttl check) |
| **Capacity** | Manual size tracking | Manual (Interlocked counters) |
| **Eviction** | Hardcoded LRU | Pluggable IEvictionPolicy |
| **Deadlock Risk** | Yes (nested locks) | No (simple lock structure) |
| **Observability** | None | Metrics + Logs |
| **Testability** | Hard (time dependencies) | Easy (TimeProvider) |
| **Memory Tier** | Custom byte[] + LinkedList | ConcurrentDictionary + timestamps |
| **Disk Tier** | Memory-mapped files (good!) | Same (proven to work) |
| **Architecture** | Different for each tier | Unified IEvictionPolicy for both |

**Why NOT Built-in MemoryCache?**
- ❌ No pluggable eviction policy
- ❌ Unpredictable compaction when full
- ❌ Can't control which entries get evicted
- ❌ Priority-based eviction, not LRU

**Lesson:** V1 had the right idea (dual-tier, MMF) but over-engineered with per-key semaphores. V3 uses a simpler approach with unified eviction policy across both tiers.

---

## Appendix B: Design Refinements from Review

**Date:** 2025-11-23
**Status:** Design Approved, Ready for Implementation

### Key Refinements Made:

1. **All Capacity Limits Configurable** ✅
   - Memory tier capacity: Configurable via `MemoryCacheSizeMb`
   - Disk tier capacity: Configurable via `DiskCacheSizeMb`
   - Size cutoff: Configurable via `SmallFileCutoffMb`
   - Users can tune based on hardware

2. **Pluggable Eviction Policy** ✅
   - Strategy pattern with `IEvictionPolicy` interface
   - Default: LRU (Least Recently Used)
   - Future: LFU, Size-First, Custom policies
   - Extensible without modifying cache implementation

3. **Async Cleanup for Low Latency** ✅
   - Two-phase eviction: Mark (< 1ms) → Cleanup (async)
   - Eviction doesn't block GetOrAddAsync
   - Background task processes pending deletions
   - Tolerates temporary over-capacity (acceptable tradeoff)

4. **No "Warm Tier"** ✅
   - Keep architecture simple (two-tier only)
   - Option left open for future enhancement
   - Decision: YAGNI (You Aren't Gonna Need It... yet)

### Implementation Priority:

**Phase 1: Core Functionality**
1. IFileCache interface
2. MemoryTierCache (using MemoryCache)
3. DiskTierCache (using MemoryMappedFile)
4. DualTierFileCache coordinator

**Phase 2: Advanced Features**
5. IEvictionPolicy interface
6. LruEvictionPolicy implementation
7. Async cleanup with ConcurrentQueue

**Phase 3: Observability**
8. System.Diagnostics.Metrics integration
9. Comprehensive logging
10. Performance benchmarks

### Architecture Highlights:

```
Key Principles:
✅ Simplicity: Use built-in MemoryCache, not custom LRU
✅ Extensibility: Pluggable eviction policies
✅ Performance: Async cleanup, < 1ms eviction latency
✅ Configurability: All limits configurable
✅ Observability: Logs + Metrics
```

### Success Metrics:

- **Latency:** Cache hit < 1ms, eviction < 1ms (mark only)
- **Throughput:** Support 100+ concurrent GetOrAddAsync
- **Capacity:** Enforce limits, never exceed (except briefly during cleanup)
- **Correctness:** No race conditions, no memory leaks
- **Extensibility:** Can add new eviction policies without modifying cache

**Ready to implement!** 🚀
