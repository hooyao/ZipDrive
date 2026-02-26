## Context

The file content caching pipeline has a structural flaw: `GenericCache<T>` forces a materialization boundary between the factory (which produces data) and the storage strategy (which persists it). For `T = Stream`, this means the factory must buffer the entire decompressed file into a `MemoryStream` before the storage strategy can consume it. This is the root cause of three bugs: OOM crash for >2.1GB files, wasteful RAM for disk-tier files, and ZIP extraction logic leaking into the Application layer.

The caching infrastructure is mature — 270 tests pass, including an 8-hour endurance soak test with 23 concurrent tasks, SHA-256 content verification, and tight cache limits. The thundering herd prevention via `Lazy<Task<CacheEntry>>` in `GenericCache.BorrowAsync` is well-tested. The fix must preserve all concurrency invariants.

Current dependency violations in `ZipVirtualFileSystem`:
- Depends on concrete `DualTierFileCache` (not an abstraction)
- Depends on `IZipReaderFactory` (Infrastructure.Archives.Zip)
- Depends on `CacheOptions` (Infrastructure.Caching)
- Contains a factory lambda with ZIP extraction logic (Infrastructure concern)

## Goals / Non-Goals

**Goals:**
- Eliminate the intermediate `MemoryStream` buffer for disk-tier files (direct ZIP → disk streaming)
- Support files >2.1GB without OOM crashes
- Move ZIP extraction logic from Application layer to Infrastructure layer
- Remove `IZipReaderFactory`, `CacheOptions`, and `DualTierFileCache` dependencies from `ZipVirtualFileSystem`
- Preserve all thundering herd, RefCount, eviction, and concurrency guarantees
- Maintain backward compatibility for `ObjectStorageStrategy<T>` (used by `ArchiveStructureCache`)

**Non-Goals:**
- Changing the thundering herd mechanism (`Lazy<Task>` in `BorrowAsync`)
- Changing the eviction, RefCount, or TTL systems
- Adding pre-eviction back (post-store eviction is proven correct by Soft Capacity design)
- Supporting write operations or new archive formats
- Changing `ArchiveStructureCache` behavior

## Decisions

### Decision 1: Strategy-owned materialization pipeline

**Choice**: Change `IStorageStrategy<T>` from `StoreAsync(CacheFactoryResult<T>)` to `MaterializeAsync(Func<CancellationToken, Task<CacheFactoryResult<T>>> factory, CancellationToken ct)`. The strategy receives the factory delegate and drives the full pipeline: call factory → consume value → dispose resources → return `StoredEntry`.

**Why not keep factory and store separate?** The separation forces a materialization boundary — the factory must produce a complete `T` before the strategy can consume it. For streams, this means buffering. Merging them allows `DiskStorageStrategy` to pipe directly from the factory-produced stream to a temp file without buffering.

**Why not add a streaming overload alongside the existing method?** Adding a second method increases interface complexity and forces callers to choose. The merged approach is simpler and handles all cases uniformly.

**Impact on ObjectStorageStrategy<T>**: Minimal — it calls the factory, takes the object, boxes it. The `DisposeAsync` on the result is a no-op since `ArchiveStructure` doesn't implement `IDisposable`. Behavior is identical.

### Decision 2: CacheFactoryResult<T> as IAsyncDisposable with OnDisposed callback

**Choice**: `CacheFactoryResult<T>` implements `IAsyncDisposable`. Its `DisposeAsync` disposes `Value` (if disposable) and then invokes an optional `Func<ValueTask>? OnDisposed` callback.

**Why not use an OwnedStream wrapper?** An `OwnedStream` that wraps a stream + its owning `IZipReader` would work but adds a new `Stream` subclass that must forward all methods correctly. The `OnDisposed` callback keeps resource ownership in the factory result — the factory knows what it opened and registers cleanup. Simpler, no new types.

**Why not just dispose Value?** For ZIP extraction, the decompressed stream depends on the `IZipReader`'s underlying file handle. Disposing the stream doesn't close the reader. The `OnDisposed` callback chains: stream → reader.

### Decision 3: FileContentCache replaces DualTierFileCache

**Choice**: A new `FileContentCache` class replaces `DualTierFileCache`. It implements `IFileContentCache` (a domain interface with `ReadAsync`) and owns extraction, routing, and caching.

**Why merge instead of wrapping?** `DualTierFileCache` was a thin router that passed through a factory delegate constructed elsewhere. The factory contained ZIP extraction logic that doesn't belong at the call site. By merging routing + extraction + caching into `FileContentCache`, the extraction logic lives where it belongs (Infrastructure) and the Application layer just calls `ReadAsync`.

**Why a domain interface?** `IFileContentCache` lives in the Domain layer so `ZipVirtualFileSystem` (Application) can depend on it without referencing Infrastructure types. The implementation `FileContentCache` lives in Infrastructure.Caching.

### Decision 4: Remove pre-eviction from GenericCache

**Choice**: Remove the pre-eviction call (`EvictIfNeededAsync(result.SizeBytes)`) that currently runs between factory and store. Rely entirely on post-store eviction.

**Why is this safe?** The codebase already documents this as "Soft Capacity Design" — multiple concurrent materializations can cause temporary overage, and the post-store check converges back. Pre-eviction was always best-effort (two concurrent materializations could both pass pre-eviction and both store). The 8-hour soak test validates post-store eviction under pressure.

**What changes?** `MaterializeAndCacheAsync` becomes: `stored = await strategy.MaterializeAsync(factory, ct)` → create CacheEntry → add to cache → post-store eviction if over capacity.

### Decision 5: GenericCache.BorrowAsync and thundering herd — UNCHANGED

**Choice**: Do not modify `BorrowAsync`, the `_materializationTasks` ConcurrentDictionary, or the `Lazy<Task<CacheEntry>>` mechanism. The change is entirely within `MaterializeAndCacheAsync`, which is what the `Lazy<Task>` wraps.

**Proof of correctness**: The `Lazy<Task<CacheEntry>>` ensures exactly one execution of `MaterializeAndCacheAsync` per key. Inside that method, the only change is replacing two calls (`factory()` + `strategy.StoreAsync(result)`) with one call (`strategy.MaterializeAsync(factory)`). The output is still a `StoredEntry`. All downstream code (CacheEntry creation, RefCount management, cache insertion, post-store eviction) is identical.

## Risks / Trade-offs

**[Risk] Pre-eviction removal causes more frequent overage** → The post-store eviction check (already in the codebase) runs after every materialization. The overage window is bounded by the time to materialize one entry. The endurance test validates this under tight cache limits (2MB memory, 20MB disk).

**[Risk] Strategy must handle factory exceptions** → If the factory throws, the strategy must not leak resources. Each strategy implementation must use try/catch or `await using` to ensure factory results are disposed on failure.

**[Risk] `IStorageStrategy<T>` interface change** → This is a breaking change to an internal interface. All three implementations (`Memory`, `Disk`, `Object`) and all tests referencing `StoreAsync` must be updated. Mitigated by: the interface is internal to the caching assembly, not public API.

**[Trade-off] `FileContentCache` couples extraction to caching** → The extraction logic (creating `IZipReader`, decompressing) now lives in `FileContentCache` rather than the VFS. This is intentional — it moves domain knowledge to the right layer. If TAR/7Z support is added later, `FileContentCache` would need to support multiple archive providers, but the `IArchiveProvider` abstraction already exists for this.

**[Trade-off] `CacheFactoryResult<T>` gains IAsyncDisposable** → All consumers of the factory delegate must now potentially dispose the result. Since only strategies call factory delegates (after this change), and strategies always dispose, this is contained.
