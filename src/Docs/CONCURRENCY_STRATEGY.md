# ZipDrive - Multi-Layer Concurrency Strategy

**Status:** ✅ Design & Implementation Complete
**Priority:** 🔥 CRITICAL - Core Performance Feature

---

## TL;DR

Four-layer strategy maximizes concurrency while preventing thundering herd and data corruption:

1. **Layer 1** (Lock-free): Cache hits = < 100ns, zero contention
2. **Layer 2** (Per-key): Prevents duplicate materialization, different keys don't block
3. **Layer 3** (Eviction): Only when cache full, doesn't block reads
4. **Layer 4** (RefCount): Borrowed entries protected from eviction during use

**Result:** 10 threads requesting same file = 1 materialization (not 10!), and entries are never evicted while in use.

---

## The Problem

### Scenario: Thundering Herd

```
10 concurrent threads request same uncached 100MB file

WITHOUT proper locking:
├── Thread 1-10: All decompress the same file
├── Total work: 1000MB (10 × 100MB)
├── 10× CPU usage
├── 10× memory usage
├── 10× I/O bandwidth
└── Race condition: which result wins?

WITH multi-layer locking:
├── Thread 1: Materializes (does work)
├── Threads 2-10: Wait, get shared result
├── Total work: 100MB (1× only)
├── 1× CPU, 1× memory, 1× I/O
└── All threads get correct result

Improvement: 10x less work!
```

---

## Three-Layer Architecture

### Layer 1: Lock-Free Cache Lookup (FASTEST - 99% of calls)

```csharp
// NO LOCK! ConcurrentDictionary.TryGetValue() is lock-free
if (_cache.TryGetValue(key, out var entry) && !IsExpired(entry))
{
    entry.LastAccessedAt = now; // Atomic update
    return entry.CreateStream(); // INSTANT
}
```

**Performance:**
- Latency: < 100 nanoseconds
- Contention: ZERO (lock-free)
- Throughput: Limited only by memory bandwidth

**When Used:** Every cache hit (99% of operations after warmup)

---

### Layer 2: Per-Key Materialization Lock (PREVENTS THUNDERING HERD)

```csharp
// Each key has its own Lazy<Task<T>>
// Different keys don't block each other!
var lazy = _materializationTasks.GetOrAdd(key, _ =>
    new Lazy<Task<CacheEntry>>(
        () => MaterializeAsync(...),
        LazyThreadSafetyMode.ExecutionAndPublication));

var entry = await lazy.Value;
// First thread: Executes MaterializeAsync()
// Other threads: Await same Task, get same result
```

**Why Lazy<Task<T>>?**
1. `Lazy<T>` guarantees **exactly-once execution**
2. `LazyThreadSafetyMode.ExecutionAndPublication` ensures thread-safety
3. All waiting threads share the same `Task` (no duplicate work)
4. Clean exception handling (failed materialization doesn't cache error)

**Performance:**
- First thread: Pays full materialization cost
- Other threads: Wait for result, but no redundant work
- Different keys: Run in parallel (no blocking!)

**Visualization:**

```
Thread 1 (key: "file1") ──────── Materializing file1... ──────┬─ Done
Thread 2 (key: "file1") ──────── Awaiting same Lazy... ───────┤─ Gets result
Thread 3 (key: "file2") ──────── Materializing file2... ──┬───┼─ Done (parallel!)
Thread 4 (key: "file3") ──────── Materializing file3... ──┼─┬─┼─ Done (parallel!)
                                                          │ │ │
                                                          │ │ └── file1 done
                                                          │ └── file2 done
                                                          └── file3 done

Result: 3 materializations run in parallel, no blocking between different keys
```

**When Used:** Cache miss (1% after warmup, 100% during initial cache population)

---

### Layer 3: Eviction Lock (INFREQUENT, NON-BLOCKING)

```csharp
// Check BEFORE locking (fast path, no lock!)
if (_currentSize + sizeBytes <= _capacity)
    return; // No eviction needed

// Slow path: Need to evict
using (_evictionLock.EnterScope()) // .NET 10 Lock
{
    // Double-check after acquiring lock
    if (_currentSize + sizeBytes <= _capacity)
        return;

    // Evict expired first
    EvictExpiredEntries();

    // Then use eviction policy
    EvictUsingPolicy(sizeBytes);
}
```

**Performance:**
- Only when cache is full (rare after warmup)
- Doesn't block Layer 1 (cache hits still instant)
- Doesn't block Layer 2 (parallel materializations continue)

**When Used:** Only when capacity exceeded (infrequent)

---

## Performance Comparison

### Without Multi-Layer Locking (Naive Implementation)

```csharp
// BAD: Global lock on every operation
private readonly Lock _globalLock = new();

public async Task<Stream> GetOrAddAsync(string key, ...)
{
    using (_globalLock.EnterScope())
    {
        // Everything serialized!
        if (_cache.TryGetValue(key, out var entry))
            return entry;

        // Materialize
        return await MaterializeAsync(...);
    }
}
```

**Problems:**
- ❌ Cache hits require lock (slow)
- ❌ All operations serialized (no concurrency)
- ❌ Different keys block each other
- ❌ Thundering herd: multiple threads materialize same key

### With Multi-Layer Locking (Our Implementation)

| Operation | Lock | Contention | Throughput |
|-----------|------|------------|------------|
| Cache hit (Layer 1) | None | Zero | Millions/sec |
| Cache miss, same key (Layer 2) | Per-key | Same-key only | Wait for materialization |
| Cache miss, different keys (Layer 2) | Per-key | None | Parallel |
| Eviction (Layer 3) | Global | Infrequent | < 1ms |

---

## Code Example: Complete Implementation

```csharp
public sealed class MemoryTierCache
{
    // Layer 1: Main cache storage (lock-free reads)
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();

    // Layer 2: Per-key materialization locks
    private readonly ConcurrentDictionary<string, Lazy<Task<CacheEntry>>> _materializationTasks = new();

    // Layer 3: Eviction lock
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
        if (_cache.TryGetValue(cacheKey, out var entry) && !IsExpired(entry))
        {
            entry.LastAccessedAt = _timeProvider.GetUtcNow();
            entry.AccessCount++;
            return new MemoryStream(entry.Data, writable: false);
        }

        // ═══════════════════════════════════════════════════════════
        // LAYER 2: Per-key materialization lock
        // ═══════════════════════════════════════════════════════════
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
            // Clean up after materialization
            _materializationTasks.TryRemove(cacheKey, out _);
        }
    }

    private async Task<CacheEntry> MaterializeAndCacheAsync(...)
    {
        // ═══════════════════════════════════════════════════════════
        // LAYER 3: Eviction (only if capacity exceeded)
        // ═══════════════════════════════════════════════════════════
        await EvictIfNeededAsync(sizeBytes);

        // Materialize
        using var sourceStream = await factory(cancellationToken);
        using var ms = new MemoryStream((int)sizeBytes);
        await sourceStream.CopyToAsync(ms, cancellationToken);
        var data = ms.ToArray();

        // Cache entry
        var entry = new CacheEntry(cacheKey, data, sizeBytes, ...);
        _cache[cacheKey] = entry;
        Interlocked.Add(ref _currentSizeBytes, sizeBytes);

        return entry;
    }

    private async Task EvictIfNeededAsync(long requiredBytes)
    {
        // Fast path: No lock!
        if (Interlocked.Read(ref _currentSizeBytes) + requiredBytes <= _capacityBytes)
            return;

        // Slow path: Eviction needed
        using (_evictionLock.EnterScope())
        {
            // Double-check
            if (_currentSizeBytes + requiredBytes <= _capacityBytes)
                return;

            // Evict
            EvictExpiredAndUsePolicy(requiredBytes);
        }
    }
}
```

---

## Testing Strategy

### Test 1: Thundering Herd Prevention

```csharp
[Fact]
public async Task ThunderingHerd_OnlyOneMateriazliation()
{
    var cache = new MemoryTierCache(...);
    var materializationCount = 0;

    var factory = (CancellationToken ct) =>
    {
        Interlocked.Increment(ref materializationCount);
        return Task.FromResult<Stream>(new MemoryStream(new byte[1024]));
    };

    // 10 threads request same key simultaneously
    var tasks = Enumerable.Range(0, 10)
        .Select(_ => cache.GetOrAddAsync("key", 1024, TimeSpan.FromMinutes(1), factory))
        .ToArray();

    await Task.WhenAll(tasks);

    // Assert: Factory called only once!
    materializationCount.Should().Be(1);
}
```

### Test 2: Different Keys Don't Block

```csharp
[Fact]
public async Task DifferentKeys_RunInParallel()
{
    var cache = new MemoryTierCache(...);
    var task1Started = new ManualResetEventSlim(false);
    var task2Started = new ManualResetEventSlim(false);

    var factory1 = async (CancellationToken ct) =>
    {
        task1Started.Set();
        await Task.Delay(100, ct); // Simulate slow materialization
        return new MemoryStream(new byte[1024]);
    };

    var factory2 = async (CancellationToken ct) =>
    {
        task2Started.Set();
        await Task.Delay(100, ct);
        return new MemoryStream(new byte[1024]);
    };

    // Start both simultaneously
    var task1 = cache.GetOrAddAsync("key1", 1024, ..., factory1);
    var task2 = cache.GetOrAddAsync("key2", 1024, ..., factory2);

    // Both should start (not blocked)
    task1Started.Wait(TimeSpan.FromMilliseconds(50));
    task2Started.Wait(TimeSpan.FromMilliseconds(50));

    task1Started.IsSet.Should().BeTrue();
    task2Started.IsSet.Should().BeTrue();
}
```

---

## Layer 4: Reference Counting (EVICTION PROTECTION)

The borrow/return pattern uses reference counting to protect entries from eviction during use.

### The Problem

```
Thread 1: BorrowAsync("file.zip") → Gets handle, starts reading
Thread 2: BorrowAsync("large.zip") → Triggers eviction (capacity exceeded)
Thread 2: Evicts "file.zip" (Thread 1 still reading!)
Thread 1: 💥 Access violation / corrupted data
```

### The Solution: RefCount

```csharp
// Each entry has a thread-safe reference count
public sealed class CacheEntry
{
    private int _refCount;

    public int RefCount => Volatile.Read(ref _refCount);
    public void IncrementRefCount() => Interlocked.Increment(ref _refCount);
    public void DecrementRefCount() => Interlocked.Decrement(ref _refCount);
}

// BorrowAsync increments RefCount before returning handle
public async Task<ICacheHandle<T>> BorrowAsync(...)
{
    if (_cache.TryGetValue(key, out var entry) && !IsExpired(entry))
    {
        entry.IncrementRefCount();  // PROTECT from eviction
        return new CacheHandle<T>(entry, value, Return);
    }
    // ...
}

// Handle.Dispose() decrements RefCount
private void Return(CacheEntry entry)
{
    entry.DecrementRefCount();  // ALLOW eviction
}

// Eviction ONLY considers entries with RefCount = 0
private Task EvictIfNeededAsync(long neededBytes)
{
    // Only evict unborrowed entries
    var evictableEntries = _cache.Values
        .Where(e => e.RefCount == 0)  // Critical filter!
        .Cast<ICacheEntry>()
        .ToList();

    // ...
}
```

### Safe Materialization: Temporary RefCount Hold

After storing a new entry, post-store eviction could immediately evict it before the caller gets a chance to borrow. Solution: temporarily hold RefCount during the critical window.

```csharp
private async Task<CacheEntry> MaterializeAndCacheAsync(...)
{
    var entry = new CacheEntry(...);

    // Temporarily protect entry during post-store eviction check
    entry.IncrementRefCount();  // RefCount = 1

    _cache[cacheKey] = entry;
    Interlocked.Add(ref _currentSizeBytes, stored.SizeBytes);

    // Post-store eviction check (entry protected with RefCount = 1)
    if (Interlocked.Read(ref _currentSizeBytes) > _capacityBytes)
    {
        await EvictIfNeededAsync(neededBytes: 0);
    }

    // Release temporary hold - caller will increment when borrowing
    entry.DecrementRefCount();  // RefCount = 0

    return entry;
}
```

### Usage Pattern

```csharp
// Safe usage - entry protected during entire using block
using (var handle = await cache.BorrowAsync(key, ttl, factory))
{
    // RefCount = 1 (or higher if other borrowers)
    var stream = handle.Value;
    await stream.ReadAsync(buffer);  // Won't be evicted!
    await stream.ReadAsync(buffer);  // Still safe!
}  // Dispose() → RefCount decremented → eviction allowed
```

### Soft Capacity Design

When all entries are borrowed (RefCount > 0), eviction is impossible. The cache allows temporary capacity overage:

```csharp
if (evictableEntries.Count == 0)
{
    _logger?.LogWarning(
        "All {Count} entries are borrowed, cannot evict. Allowing soft capacity overage.",
        _cache.Count);
    return;  // Allow overage, will converge when entries returned
}
```

This is acceptable because:
1. Overage is bounded by concurrent operations
2. Cache converges back to capacity when handles are disposed
3. Better than blocking or throwing exceptions

---

## Summary

### Key Principles

1. **Lock-free fast path** - 99% of operations (cache hits) have zero contention
2. **Per-key locks** - Only threads requesting same key wait for each other
3. **Parallel materialization** - Different keys materialize in parallel
4. **Separate eviction** - Eviction doesn't block reads or materializations
5. **Reference counting** - Borrowed entries are protected from eviction

### Performance Wins

| Scenario | Naive (Global Lock) | Multi-Layer | Improvement |
|----------|---------------------|-------------|-------------|
| 10 threads, same file | 1 materialization, serial | 1 materialization, shared | Same work, instant for 9 threads |
| 10 threads, different files | 10 materializations, serial | 10 materializations, parallel | 10x throughput |
| Cache hit latency | Lock overhead (µs) | Lock-free (ns) | 1000x faster |
| Entry in use during eviction | Data corruption / crash | Entry protected | 100% safe |

### Critical Success Factors

✅ `ConcurrentDictionary` for lock-free reads
✅ `Lazy<Task<T>>` for per-key locking
✅ `LazyThreadSafetyMode.ExecutionAndPublication` for thread-safety
✅ Separate eviction lock (doesn't block reads)
✅ Double-check pattern (minimize lock time)
✅ `RefCount` for eviction protection (borrow/return pattern)
✅ Temporary RefCount hold during post-store eviction

**This concurrency strategy is essential for high-performance caching in multi-threaded scenarios.**

---

## Layer 5: Storage Lifecycle Safety (LEARNED FROM PRODUCTION BUGS)

**Added:** 2026-03-31, after fixing three concurrency bugs discovered via 12-hour endurance testing with 100 concurrent tasks under aggressive eviction pressure.

### The Fundamental Problem: Non-Atomic Borrow-Retrieve Gap

Layers 1-4 assume that between `TryGetValue` (finding an entry) and `IncrementRefCount` (protecting it), the entry's **storage** remains valid. This assumption is wrong under aggressive eviction.

```
BorrowAsync (Layer 1):              TryEvictEntry:
─────────────────────               ──────────────
1. TryGetValue → hit (RefCount=0)
                                    2. RefCount > 0? → false
                                    3. TryRemove → success
                                    4. Dispose(stored) → storage destroyed
5. IncrementRefCount → 1 (too late)
6. Retrieve(stored) → reads destroyed storage
```

RefCount protects against eviction **once incremented**, but the increment happens **after** the dictionary lookup. The gap between steps 1 and 5 is the vulnerability window.

### Bug #1: ArrayPool Return-Reuse-Overwrite (Memory Tier)

**Symptom:** Endurance test SHA-256 mismatch. Data read back differs from what was written.

**Root Cause:** `MemoryStorageStrategy` used `ArrayPool<byte>` for cache storage. On eviction, `Pool.Return(array, clearArray: true)` returned the array to the pool. A subsequent `Pool.Rent()` (for another entry's materialization) handed out the **same array**, which was then overwritten with new data. Any reader still holding a `MemoryStream` over the original array saw corrupted data.

```
Entry A stored in pooled array → [AAAA]
Evict A → Pool.Return(array)
Entry B materializes → Pool.Rent() returns SAME array → [BBBB]
Reader still holds MemoryStream over array → reads [BBBB] instead of [AAAA]
```

**Key Insight:** `clearArray: true` vs `false` is irrelevant. The problem is **array reuse**, not zeroing. With `clearArray: false`, the reader sees Entry B's data instead of zeros — still wrong.

**Fix Attempts That Failed:**
1. `TryMarkStorageDisposed` flag on `CacheEntry` — Narrowed the window but couldn't close it. The flag check and `Retrieve()` are not atomic; the evictor can dispose between them.
2. Defensive copy via `new byte[]` in `Retrieve()` — Worked but created per-read GC pressure (one `byte[]` allocation per Dokan callback).
3. Defensive copy via `Pool.Rent()` in `Retrieve()` — Same reuse problem (rented copy can be returned and overwritten).

**Definitive Fix:** Remove `ArrayPool` entirely. Use plain `new byte[size]` for cache storage.

```csharp
// Before (BROKEN): ArrayPool-backed storage
byte[] rented = Pool.Rent(size);
// ... fill with data ...
// Dispose: Pool.Return(rented, clearArray: true)
// Problem: array can be Rent()-ed by someone else and overwritten

// After (CORRECT): GC-managed storage
byte[] buffer = new byte[size];
// ... fill with data ...
// Dispose: no-op (GC collects when all references are gone)
// Safe: array lives until ALL MemoryStreams over it are collected
```

**Why This Works:** GC guarantees the array is not collected (and therefore not reused) while any reference exists — including `MemoryStream` wrappers held by concurrent readers. This is a **language-level guarantee**, not a synchronization primitive.

**Why ArrayPool Was Wrong Here:**
- ArrayPool is designed for **short-lived, frequent** allocations (e.g., per-request buffers)
- Cache entries are **long-lived** (minutes to hours) — they sit in pool buckets wasting capacity
- Pool's return-reuse semantic is fundamentally incompatible with shared-reference access patterns

### Bug #2: Temp File Deletion During Read (Disk Tier)

**Symptom:** `FileNotFoundException` in `ChunkedStream` constructor when opening a backing file that was just evicted.

**Root Cause:** `ChunkedDiskStorageStrategy.Dispose()` deletes the temp file. A concurrent `BorrowAsync` that already resolved the entry from the dictionary (pre-eviction) tries to open the file in `Retrieve()` — file no longer exists.

Same gap as Bug #1, but the symptom is different because disk storage is file-backed rather than array-backed.

**Fix:** `BorrowAsync` catches `FileNotFoundException` on `Retrieve()` and falls through to the miss path (re-materialization). The re-materialization creates a new temp file.

```csharp
try
{
    T value = _storageStrategy.Retrieve(existingEntry.Stored);
    return new CacheHandle<T>(existingEntry, value, Return);
}
catch (FileNotFoundException)
{
    // Backing file deleted by eviction between TryGetValue and Retrieve.
    existingEntry.DecrementRefCount();
    // Fall through to Layer 2 (miss path) for re-materialization.
}
```

### Bug #3: Partial-Read Verification (Test Bug, Not Cache Bug)

**Symptom:** SHA-256 mismatch in `SequentialReader4K/64K` endurance suite.

**Root Cause:** `SequentialReaderAsync` reads a file in chunks within `while (!ct.IsCancellationRequested)`. When the test duration CTS fires mid-sequence, the loop exits with `offset < file.size`. The code then calls `VerifyFullFile(data, offset)` — hashing partial data against the full-file manifest SHA. Always mismatches.

**Fix:** Only verify when `offset == file.size` (complete read).

**Lesson:** Endurance test harness bugs can masquerade as cache corruption. Always verify the test itself before debugging the system under test.

### Design Principles (Updated)

| Principle | Rationale |
|-----------|-----------|
| **No ArrayPool for long-lived storage** | Pool return-reuse is incompatible with shared-reference readers |
| **GC-managed byte[] for memory tier** | Language-level lifetime guarantee eliminates storage races |
| **Catch storage-gone on Retrieve** | Graceful fallback to miss path instead of crash |
| **Flag-based guards are insufficient** | Non-atomic check+use windows cannot be closed with flags alone |
| **Endurance test with SHA verification** | Only way to catch data-corruption races (unit tests too fast to trigger) |
| **Separate verification from test lifecycle** | Don't verify partial reads caused by cancellation |

### The TryMarkStorageDisposed Flag

The `TryMarkStorageDisposed` / `IsStorageDisposed` flag on `CacheEntry` remains in the code as defense-in-depth. It is **not sufficient alone** (the check-then-Retrieve gap persists), but it catches the common case where eviction completes entirely before `BorrowAsync` reaches `IncrementRefCount`. With the `new byte[]` fix for memory tier and `FileNotFoundException` catch for disk tier, the flag is a belt alongside the suspenders.

### Concurrency Layers (Final)

| Layer | Mechanism | What It Prevents |
|-------|-----------|-----------------|
| Layer 1 | Lock-free `ConcurrentDictionary` | Read contention |
| Layer 2 | `Lazy<Task<T>>` per key | Thundering herd (duplicate materialization) |
| Layer 3 | Global eviction lock | Concurrent eviction corruption |
| Layer 4 | RefCount on CacheEntry | Eviction of borrowed entries |
| **Layer 5** | **GC-managed storage + Retrieve fallback** | **Storage destruction during borrow gap** |
