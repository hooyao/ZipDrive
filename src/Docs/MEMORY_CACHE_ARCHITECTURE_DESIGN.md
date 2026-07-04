# ZipDrive - Memory Cache Architecture Analysis and Off-Heap Design

**Version:** 1.0  
**Last Updated:** 2026-04-06  
**Status:** Proposed

---

## 1. Context

`MemoryStorageStrategy` currently materializes small files into a managed `byte[]` and serves reads through `MemoryStream`.

That design is simple and safe, but it has two weaknesses for a long-lived cache:

1. **Large managed allocations create GC pressure and heap holes**
2. **The process memory budget is hard to control precisely**

The obvious replacement — allocating one unmanaged block per cache entry with `NativeMemory.AlignedAlloc()` and freeing it manually — improves GC behavior, but it **does not solve fragmentation by itself**. It merely moves fragmentation from the managed heap to the native allocator.

This document analyzes the trade-offs and proposes a memory-tier architecture that keeps the current `GenericCache` concurrency model while moving cached payloads off-heap in a controlled way.

---

## 2. Current State

Today the memory tier behaves like this:

- `FileContentCache` routes files below `SmallFileCutoffBytes` to `MemoryStorageStrategy`
- `MemoryStorageStrategy` allocates one managed `byte[]` per cached entry
- `GenericCache` owns TTL, eviction, ref-counted borrowing, and concurrency
- disk-tier storage is already separated behind `IStorageStrategy<Stream>`

This means the allocation policy is currently **per-entry / per-object**, not **arena-based**.

### 2.1 What is good about the current design

- extremely simple lifetime model
- safe with concurrent readers
- no pinning
- no native leaks
- integrates cleanly with `ICacheHandle<T>` disposal semantics

### 2.2 What is not good enough

- long-lived large arrays migrate to LOH / managed heap pressure zones
- memory usage is visible to the runtime, but not shaped by a cache-specific allocator
- per-entry allocation/free amplifies fragmentation under churn
- eviction removes logical entries, but physical heap recovery is left to the GC

---

## 3. Why “just use unmanaged aligned memory” is not enough

Switching from:

- `new byte[size]`

to:

- `NativeMemory.AlignedAlloc(size, alignment)`

solves only part of the problem.

### 3.1 What it improves

- payload bytes are no longer tracked by the GC
- large cached files stop fragmenting the managed heap
- the cache can maintain its own explicit memory accounting

### 3.2 What it does not improve

- **native allocator fragmentation still exists**
- freeing arbitrary sizes in arbitrary order still creates holes
- allocation latency becomes allocator-dependent
- the cache still has no placement policy, only a storage primitive

If every cache entry owns an independently allocated unmanaged block, the system becomes:

- GC-friendlier
- but still **allocator-fragmentation-prone**

So the real design problem is not “managed vs unmanaged”.  
The real design problem is **general-purpose allocator vs cache-shaped allocator**.

---

## 4. Design goals

For ZipDrive’s memory tier, the allocator should:

1. **move payload bytes off-heap**
2. **bound memory usage explicitly**
3. **minimize fragmentation under churn**
4. **preserve lock-free cache hits in `GenericCache`**
5. **preserve ref-count-based eviction safety**
6. **avoid moving buffers while readers are active**
7. **keep the implementation compatible with the existing disk tier cutoff**
8. **avoid `ArrayPool`-style reuse semantics for long-lived cache entries**

Non-goals for the first version:

- background compaction of live entries
- relocating live entries while handles are borrowed
- NUMA-aware placement
- cross-process shared memory

---

## 5. Option analysis

### 5.1 Option A - Keep managed `byte[]`

**Pros**

- simplest code
- safest lifetime model
- zero unsafe code

**Cons**

- managed heap fragmentation risk
- less direct control over process memory footprint
- GC cost grows with cached payload volume

**Verdict:** acceptable baseline, but not ideal for a large, long-lived content cache.

### 5.2 Option B - One unmanaged allocation per entry

**Pros**

- removes payloads from the managed heap
- simple conceptual migration from `byte[]`

**Cons**

- still fragmented under mixed-size churn
- still one allocation/free per entry
- native leaks become a correctness risk
- no allocator-level locality guarantees

**Verdict:** better than managed arrays, but still the wrong shape for a cache.

### 5.3 Option C - Off-heap slab / arena allocator

Allocate larger regions up front, then sub-allocate cache entries from those regions.

**Pros**

- much lower fragmentation
- predictable memory accounting
- better locality
- amortized allocation cost
- cache-specific eviction and allocator policy can cooperate

**Cons**

- more implementation complexity
- requires custom stream/view over unmanaged memory
- requires careful teardown and diagnostics

**Verdict:** best fit for ZipDrive.

### 5.4 Option D - Reserve large virtual regions and commit on demand

This is a stronger version of Option C:

- reserve address space in large arenas
- commit pages lazily
- release whole arenas when fully free

**Pros**

- best control over commit footprint
- aligns well with Windows page semantics
- excellent foundation for large, long-lived caches

**Cons**

- more platform-specific
- more complexity than a first migration likely needs

**Verdict:** good long-term direction, but should sit behind an abstraction so non-Windows builds can still function.

---

## 6. Recommended architecture

Use an **off-heap arena allocator** for the memory tier, with:

- **fixed-size arenas**
- **size classes for small/medium cached files**
- **dedicated extents for unusually large “memory-tier” files**
- **non-moving allocations**
- **explicit disposal when cache entries are evicted and no longer borrowed**

### 6.1 High-level layout

```text
FileContentCache
    -> GenericCache<Stream>
        -> ArenaMemoryStorageStrategy
            -> IUnmanagedMemoryAllocator
                -> SmallSizeClassAllocator
                -> MediumRunAllocator
                -> LargeExtentAllocator
```

### 6.2 Allocation policy

#### Small entries

Example size classes:

- 4 KB
- 8 KB
- 16 KB
- 32 KB
- 64 KB
- 128 KB
- 256 KB
- 512 KB
- 1 MB

Each size class owns slabs composed of equal-size slots.  
This eliminates external fragmentation **within the class** and bounds internal waste to the class rounding factor.

#### Medium entries

For entries above the small-class range, allocate page-aligned runs from a medium arena, for example:

- 2 MB to 16 MB using page multiples

This avoids exploding the number of size classes while still keeping allocations arena-backed.

#### Large entries

Entries near the upper memory-tier cutoff should not be forced into small slabs.

Recommended behavior:

- either route them to disk earlier
- or allocate them as dedicated extents backed by one arena segment

For ZipDrive, the simplest rule is:

- **keep the memory tier optimized for small and medium objects**
- **push near-cutoff large objects toward the disk tier**

That matches the existing dual-tier architecture.

### 6.3 Alignment policy

Use two levels of alignment:

- **64-byte alignment** for slot payload starts (cache-line friendly)
- **page alignment** for arena segments / medium runs

Implementation note:

- Windows backend: prefer `VirtualAlloc` reserve/commit for arena segments
- portable backend: use `NativeMemory.AlignedAlloc` per segment, not per entry

The important point is that **alignment is applied to arenas/extents**, not used as a substitute for a real allocation strategy.

---

## 7. Proposed ZipDrive object model

### 7.1 New storage objects

```csharp
internal sealed class UnmanagedBufferHandle : SafeHandle
{
    public nuint Length { get; }
    public nuint Capacity { get; }
    public int SizeClassId { get; }
}

internal sealed class ArenaBackedBuffer
{
    public UnmanagedBufferHandle Handle { get; }
    public int Length { get; }
}
```

`StoredEntry.Data` would hold an `ArenaBackedBuffer` instead of `byte[]`.

### 7.2 New storage strategy

Replace `MemoryStorageStrategy` with a dedicated unmanaged implementation, for example:

```csharp
public sealed class ArenaMemoryStorageStrategy : IStorageStrategy<Stream>
```

Responsibilities:

- ask the allocator for a slot
- copy the factory stream into unmanaged memory
- publish an immutable read-only view
- return a stream that reads from unmanaged memory without copying
- free the slot on eviction

### 7.3 Reader shape

Avoid copying unmanaged payloads back into managed arrays on cache hits.

Possible reader forms:

- custom `Stream` over pointer + length
- `UnmanagedMemoryStream` if the lifetime model remains safe
- `MemoryManager<byte>` wrapper if later APIs need `Memory<byte>`/`ReadOnlyMemory<byte>`

For ZipDrive, a custom read-only stream is usually the clearest choice because:

- lifetime is owned by the cache entry
- reads are sequential and offset-based
- disposal does not own the underlying allocation

---

## 8. Fragmentation strategy

The key rule is:

> Do not let the general-purpose allocator decide the lifetime shape of the cache.

### 8.1 How fragmentation is reduced

1. **same-size objects share slabs**
2. **medium objects use page runs, not arbitrary malloc blocks**
3. **large objects are isolated**
4. **whole arenas are returned only when fully free**
5. **entries are never moved while borrowed**

### 8.2 Why this fits ZipDrive better than compaction

ZipDrive already has a strong ref-count borrow/return model:

- borrowed entries must stay stable
- eviction cannot invalidate active readers

That makes moving compaction expensive and risky.  
A **non-moving slab/arena allocator** fits the existing cache semantics much better.

---

## 9. Memory budgeting

The allocator should track at least:

- reserved bytes
- committed bytes
- bytes in active entries
- bytes stranded as internal fragmentation
- free bytes per size class
- bytes in pending-orphan cleanup

Admission should be based on **committed + requested**, not only logical cache size.

Recommended flow:

1. check whether the requested allocation fits the memory-tier budget
2. if not, trigger cache eviction first
3. retry allocation
4. if still not possible, route to disk tier or fail fast

This keeps process memory control explicit instead of hoping the runtime or OS allocator cleans up quickly enough.

---

## 10. Concurrency and lifetime model

The current `GenericCache` concurrency model should remain unchanged.

### 10.1 Keep these invariants

- cache hits stay lock-free via `ConcurrentDictionary.TryGetValue`
- same-key materialization stays deduplicated via `Lazy<Task<T>>`
- eviction still never destroys entries with `RefCount > 0`
- orphan cleanup still happens after the final `ICacheHandle<T>` disposal

### 10.2 What changes

Only the physical storage lifecycle changes:

- **before:** GC eventually collects `byte[]`
- **after:** allocator frees slot/extent explicitly

That means `Dispose(StoredEntry)` becomes meaningful for the memory tier and must:

- return the slot to its size class
- or release the extent
- update allocator metrics

### 10.3 Safety rule

The underlying unmanaged allocation must remain valid until:

1. the entry has been removed from the dictionary, and
2. its borrow count reaches zero

That is already how the cache models storage destruction, so the allocator should plug into the existing entry lifecycle rather than invent a second lifetime system.

---

## 11. Proposed implementation phases

### Phase 1 - Introduce allocator abstractions

- add `IUnmanagedMemoryAllocator`
- add `ArenaBackedBuffer`
- add `UnmanagedReadOnlyStream`
- keep `GenericCache` unchanged

### Phase 2 - Replace the memory-tier storage strategy

- implement `ArenaMemoryStorageStrategy`
- keep the disk tier unchanged
- keep `FileContentCache` routing unchanged

### Phase 3 - Add observability

- fragmentation ratio
- arena occupancy
- size-class pressure
- allocation failures / fallbacks to disk

### Phase 4 - Tune routing

- possibly lower the in-memory cutoff
- route “awkwardly large” objects to disk sooner
- optionally add a dedicated medium-object threshold

---

## 12. Industrial references

### 12.1 Memcached

Memcached is the classic reference for a cache-shaped allocator:

- memory is split into slab classes
- each slab class serves similarly sized items
- eviction happens within that structure

Relevant lesson for ZipDrive:

> use **size classes and slabs** to control fragmentation instead of one malloc per key.

Reference:

- https://docs.memcached.org/

### 12.2 Meta CacheLib

CacheLib is a stronger modern reference:

- slab-based memory allocator
- cache pools
- rebalance and eviction policies designed together

Relevant lesson for ZipDrive:

> treat allocator design and cache policy as one system, not two unrelated layers.

References:

- https://cachelib.org/
- https://www.usenix.org/system/files/osdi20-berg.pdf

### 12.3 Redis

Redis is useful as a counterexample:

- it relies heavily on the underlying allocator (commonly jemalloc)
- memory management quality depends on allocator behavior and defragmentation features

Relevant lesson for ZipDrive:

> using unmanaged memory alone is not enough; allocator policy still matters.

Reference:

- https://redis.io/docs/latest/develop/reference/eviction/

### 12.4 Caffeine

Caffeine is mostly an eviction-policy reference, not an off-heap allocator reference:

- on-heap Java cache
- strong admission/eviction policy via Window TinyLFU

Relevant lesson for ZipDrive:

> allocator efficiency and eviction efficiency are separate concerns; a strong cache needs both.

Reference:

- https://github.com/ben-manes/caffeine/wiki/Efficiency

---

## 13. Recommendation

For ZipDrive, the best next-step architecture is:

1. **replace per-entry managed arrays with off-heap storage**
2. **do not replace them with per-entry unmanaged malloc blocks**
3. **introduce a cache-specific slab/arena allocator**
4. **keep allocations non-moving**
5. **reuse the existing `GenericCache` ref-count and eviction semantics**
6. **continue routing large objects to disk instead of trying to keep everything in RAM**

In short:

> The correct upgrade path is not “`byte[]` -> `AlignedAlloc`”.  
> The correct upgrade path is “per-entry heap allocation -> arena/slab allocator with explicit budgeting”.

---

## 14. Concrete ZipDrive proposal

If this design is implemented, the recommended steady-state architecture is:

```text
< 1 MB         -> slab size classes in unmanaged arenas
1 MB - 16 MB   -> page-run allocator in unmanaged arenas
> 16 MB        -> prefer disk tier unless policy explicitly allows dedicated extents
```

With:

- 64-byte payload alignment
- page-aligned arena segments
- explicit allocator metrics
- explicit free on final entry release

This gives ZipDrive:

- lower GC impact
- better control over memory footprint
- lower fragmentation than both managed arrays and per-entry unmanaged allocations
- minimal disruption to the proven concurrency model already in `GenericCache`
