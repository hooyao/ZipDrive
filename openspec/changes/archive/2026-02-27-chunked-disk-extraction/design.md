## Context

ZipDrive mounts ZIP archives as Windows drives via DokanNet. ZIP files provide sequential-only access (Deflate compression requires left-to-right decompression), but Windows file system operations require random access at arbitrary offsets. The caching layer bridges this by materializing (decompressing) ZIP entries into random-access storage.

The current disk tier uses `DiskStorageStrategy`, which pipes the entire decompressed stream to a temp file via `CopyToAsync`, then creates a `MemoryMappedFile` for random access. This means `MaterializeAsync` blocks until 100% of the file is decompressed — 25 seconds for a 5GB file at ~200MB/s throughput.

The existing architecture is designed for pluggable storage strategies via `IStorageStrategy<Stream>`. `GenericCache<T>` handles all concurrency (lock-free hits, per-key `Lazy<Task>` thundering herd prevention, RefCount eviction protection). Storage strategies only implement `MaterializeAsync`, `Retrieve`, and `Dispose`. This makes the disk tier storage strategy a clean replacement target.

See [`src/Docs/CHUNKED_EXTRACTION_DESIGN.md`](../../src/Docs/CHUNKED_EXTRACTION_DESIGN.md) for the comprehensive design document with full implementation details, concurrency analysis, and code sketches.

## Goals / Non-Goals

**Goals:**
- First-byte latency of ~50ms for all disk-tier files (vs. 250ms-25s today)
- All disk-tier files (>= 50MB) benefit — no additional size threshold
- Transparent to callers — `ChunkedStream` implements `Stream`
- Zero changes to `GenericCache<T>`, `ICache<T>`, `ICacheHandle<T>`, `CacheEntry`
- Concurrent readers served from completed chunks while extraction continues
- Clean cancellation when entries are evicted mid-extraction

**Non-Goals:**
- Random seek optimization for Deflate (sequential constraint is fundamental)
- Individual chunk eviction (entry evicted as a unit)
- Resumable extraction after eviction (Deflate state cannot be checkpointed)
- Write support (ZipDrive is read-only)
- Store-method (compression=0) direct-read optimization (future work)

## Decisions

### Decision 1: Replace disk tier, not add third tier

Replace `DiskStorageStrategy` with `ChunkedDiskStorageStrategy` rather than adding a separate chunked tier with a threshold.

**Rationale**: A 50MB file produces 5 chunks (~400 bytes TCS overhead — trivial). Adding a third tier means a third `GenericCache<Stream>` instance, a third routing branch, an extra config parameter, and no latency improvement for files between 50-200MB. Every disk-tier file benefits from incremental extraction.

**Alternative rejected**: Three-tier with `ChunkedExtractionThresholdMb`. Added complexity without meaningful benefit since chunking overhead is negligible at any disk-tier size.

### Decision 2: NTFS sparse file as backing store (not MMF, not per-chunk files)

Use a single NTFS sparse file per entry with `FileStream` (not `MemoryMappedFile`).

**Rationale**: `MemoryMappedFile` maps the entire file into virtual address space. Reading an unallocated sparse region returns zeros silently — no error, no exception. This would bypass the `EnsureChunkReadyAsync` safety gate and silently serve corrupt data. `FileStream` with `FileShare.ReadWrite` lets us interpose the chunk-ready check on every read. NTFS sparse files allocate physical clusters lazily, so the pre-allocated file doesn't consume disk until data is written.

**Alternative rejected**: One temp file per chunk. 500 files for a 5GB entry causes FS fragmentation and excessive file handle usage.

### Decision 3: TaskCompletionSource per chunk for signaling

Use `TaskCompletionSource<bool>[]` for chunk completion signaling rather than polling, `ManualResetEvent`, or `Channel<T>`.

**Rationale**: TCS integrates naturally with async/await. Readers call `await TCS[i].Task` — zero polling, zero spin-waiting. Multiple readers can await the same TCS. Failed/cancelled extractions propagate exceptions through `TrySetException`/`TrySetCanceled`. Overhead: ~80 bytes per TCS × 500 chunks = 40KB for a 5GB file — negligible.

### Decision 4: Greedy extraction (continue after readers disconnect)

Background extraction continues to completion even when all readers disconnect (RefCount drops to 0 but entry stays in cache).

**Rationale**: Deflate decompression cannot resume from the middle — stopping at chunk 100 means restarting from chunk 0 on next access. If the user opened the file once, they're likely to access it again. The TTL + eviction policy will reclaim the entry if it's truly unused.

### Decision 5: MaterializeAsync returns after first chunk, reports full size

`MaterializeAsync` awaits only `TCS[0].Task` (~50ms), then returns `StoredEntry` with `SizeBytes` = full `UncompressedSize`.

**Rationale**: Reporting full size upfront is conservative but correct for capacity planning. The sparse file will eventually consume the full size. Under-reporting would allow over-admission of multiple large files, leading to disk exhaustion when they all expand. `GenericCache` treats `StoredEntry.SizeBytes` as immutable — no dynamic size update mechanism exists, and adding one would violate the zero-changes-to-GenericCache goal.

### Decision 6: Synchronous Read fallback via GetAwaiter().GetResult()

`ChunkedStream.Read()` (sync) delegates to `ReadAsync().GetAwaiter().GetResult()`.

**Rationale**: DokanNet may invoke `Stream.Read()` synchronously. The extraction task uses `FileOptions.Asynchronous` (IOCP threads), not thread pool threads. DokanNet has its own thread pool. No deadlock risk. The common case (chunk already ready) is a lock-free `BitArray` check with no async await needed.

## Risks / Trade-offs

**[Risk] Sparse file reads return zeros for unextracted regions** → `EnsureChunkReadyAsync` is the mandatory safety gate. Unit tests specifically verify reads block until chunk completion. The `BitArray` + TCS double-check pattern ensures correctness.

**[Risk] Synchronous Read deadlock on thread pool exhaustion** → Extraction uses IOCP threads (async file I/O), DokanNet uses separate thread pool. No shared thread pool contention. Mitigation: monitor thread pool utilization in endurance tests.

**[Risk] Regression from replacing battle-tested DiskStorageStrategy** → All existing disk-tier tests adapted for new strategy. Endurance test extended with chunked scenarios. `IStorageStrategy<Stream>` interface guarantees behavioral compatibility. Old code preserved in git history.

**[Risk] Capacity accounting inaccuracy during extraction** → Full size reported immediately is conservative (may under-utilize capacity temporarily). Converges to accurate once extraction completes. Over-admission (the dangerous case) is prevented.

**[Risk] Many concurrent extractions saturate disk I/O** → Cache capacity naturally bounds concurrent entries. Each extraction does sequential I/O (efficient). Future optimization: extraction semaphore to limit concurrency.

**[Trade-off] Deflate mid-file seek still requires decompressing everything before the target** → Fundamental compression constraint. Chunking doesn't eliminate this but improves it: wait proportional to seek position (not full file). Once extracted, all chunks are instant on re-read.
