## Context

When Windows Explorer opens a folder of small files, the shell fires a burst of `ReadFile` calls to generate thumbnails/previews. In ZipDrive, each call currently lands in `FileContentCache.ReadAsync()` as an independent cache miss, which opens a new `ZipReader` (new `FileStream`), seeks independently to `LocalHeaderOffset`, and decompresses independently — even when the target entries are physically adjacent in the ZIP.

This produces O(N) seeks and O(N) `FileStream` opens for what could be a single sequential read pass through one `FileStream`.

The coalescing batch reader addresses this by collecting concurrent cache-miss requests for small files from the same archive within a short time window, grouping physically adjacent entries into batches, and executing each batch as a single sequential pass.

**Only the memory tier is affected.** Large files (disk tier, `>= SmallFileCutoffMb`) continue to use `ChunkedDiskStorageStrategy` unchanged.

## Goals / Non-Goals

**Goals:**
- Batch concurrent memory-tier cache misses for the same archive into a single sequential `ZipReader` pass
- Configurable coalescing window with fast-path bypass (no added latency for isolated reads)
- Configurable density threshold to avoid over-reading sparse ZIPs
- Optional speculative caching of "hole" entries encountered during the sequential pass
- No regression in correctness, thread safety, or existing thundering-herd protection

**Non-Goals:**
- Disk tier (chunked extraction) — not affected
- Reordering already-cached reads (hits go through existing lock-free path)
- Seeking backward within a batch (entries are processed strictly in forward offset order)
- Automatic density tuning or machine-learning-based prefetch

## Decisions

### Decision 1: New `CoalescingBatchCoordinator` component, not modification of `FileContentCache`

`FileContentCache` owns tier routing and factory delegation. Adding coalescing directly there would mix three concerns (routing, coalescing, extraction). Instead, introduce `CoalescingBatchCoordinator` as a collaborator injected into `FileContentCache`. It manages the pending queue and batch scheduling; `FileContentCache` calls it for memory-tier misses only.

**Alternative considered**: Decorator wrapping `IFileContentCache`. Rejected because the decorator would need to duplicate the tier routing logic and the `BorrowAsync` signature to intercept at the right level.

### Decision 2: Two-timer adaptive window (`FastPathMs` + `WindowMs`)

A fixed 500ms window adds 500ms latency to every isolated small-file read (e.g., opening a single image). Two timers solve this:

- **FastPathMs** (default: 20ms): After the first request, wait this long for a second request before giving up and firing solo.
- **WindowMs** (default: 500ms): Once a second request arrives, extend the window to collect the full burst.

If no second request arrives within `FastPathMs`, the entry is extracted immediately via the existing per-entry path — zero added latency. If a burst is detected, all requests in the `WindowMs` window are batched.

**Alternative considered**: Single configurable delay. Rejected because it forces a latency tradeoff on all reads.

### Decision 3: Per-archive pending queues with `TaskCompletionSource<ICacheHandle<Stream>>`

Each archive gets its own `ConcurrentQueue<PendingRequest>`. Each `PendingRequest` holds a `TaskCompletionSource<ICacheHandle<Stream>>` that the batch runner signals when its entry is extracted and cached. Callers await their specific TCS and are unblocked as soon as their entry is ready, even if the batch is still running for later entries.

This preserves the `ICacheHandle<T>` borrow/return contract — the coordinator calls `BorrowAsync` for each entry individually, then hands the handle back to the original caller via TCS.

### Decision 4: Greedy forward-scan batch grouping with Option A density formula

Sort pending entries by `LocalHeaderOffset`. Walk forward, accumulating entries. After each addition, compute:

```
density = sum(CompressedSize of accumulated entries) / (last.end - first.start)
```

Where `last.end = last.LocalHeaderOffset + last.CompressedSize + estimated_local_header_size` (30 bytes estimated overhead per header).

If adding the next entry would drop density below `DensityThreshold`, close the current batch and start a new one. This is O(N log N) for the sort, O(N) for the scan — negligible for bursts of thumbnail reads.

**CompressedSize is used for density** (not UncompressedSize) because it represents actual bytes read from disk.

### Decision 5: Skip holes without decompression; optionally cache them

For entries physically between two requested entries (holes):

- **Without `SpeculativeCache`**: Read the Local Header (30 bytes) to get `FileNameLength + ExtraFieldLength`, then advance the `FileStream` position by `FileNameLength + ExtraFieldLength + CompressedSize`. No decompression.
- **With `SpeculativeCache`**: Read and decompress the hole entry, call `BorrowAsync` for it too. Only skip if the hole entry is already cached (check `TryGetValue` before decompress).

The hole's `CompressedSize` is already known from `ZipEntryInfo` in `ArchiveStructure` — no seek needed. The Local Header read is required to skip the variable-length filename/extra fields.

**Using `File­Stream.Seek()` vs. reading forward**: Seeking ahead within a `FileStream` on a local HDD does not produce a physical seek — the OS read-ahead buffer covers short jumps. Using `Seek()` is fine and avoids reading hole bytes into managed memory when `SpeculativeCache` is off.

### Decision 6: Batch runner populates `GenericCache` by calling existing `BorrowAsync`

The batch runner does not bypass `GenericCache`. It calls `cache.BorrowAsync(cacheKey, ttl, factory, ct)` for each entry, where `factory` extracts from the already-positioned sequential `ZipReader`. This preserves:
- TTL assignment
- Reference counting
- Thundering herd protection (if another caller already materialized the same entry concurrently, `BorrowAsync` returns the existing entry)
- Eviction policy tracking

### Decision 7: Configuration in dedicated `Coalescing` section

```jsonc
{
  "Coalescing": {
    "Enabled": true,
    "FastPathMs": 20,
    "WindowMs": 500,
    "DensityThreshold": 0.8,
    "SpeculativeCache": false
  }
}
```

Separate from `Cache` section to keep concerns distinct. `CoalescingOptions` POCO bound independently.

## Risks / Trade-offs

**[Risk] Window adds 20ms minimum latency to first read of any uncached small file in a burst** → Mitigation: `FastPathMs` is the only latency cost for isolated reads (fires solo immediately after 20ms with no second request). For burst scenarios, the 20ms is absorbed into the full `WindowMs` window.

**[Risk] A long-running batch blocks its `ZipReader` FileStream for the full batch duration** → Mitigation: Other archives are unaffected (per-archive queues). Other callers for the *same* archive hitting the same entries are served by `GenericCache`'s existing thundering-herd protection. Only entries in the active batch experience sequential ordering.

**[Risk] Speculative cache consumes memory for entries nobody requested** → Mitigation: `SpeculativeCache` defaults to `false`. When enabled, hole entries are subject to normal LRU eviction; they hold no reference count after batch runner releases them (no `ICacheHandle` kept). They will be evicted first under memory pressure.

**[Risk] Density calculation uses compressed sizes, but uncompressed sizes determine actual memory consumption** → Mitigation: The density check is a heuristic for I/O efficiency, not memory accounting. Memory capacity enforcement remains in `GenericCache` via `CurrentSizeBytes` and eviction. Accept the imprecision.

**[Risk] Batch runner holds open a `ZipReader` (FileStream) across multiple entry extractions** → Mitigation: FileShare.Read is already set on all ZipReader FileStreams; concurrent independent reads from other batch runners or solo extractors are not blocked.

## Migration Plan

- `CoalescingOptions.Enabled` defaults to `true` — opt-out via config
- When `Enabled = false`, `FileContentCache` skips the coordinator entirely; behavior is identical to pre-feature
- No data migration required (in-memory cache state; no persistence)
- No breaking changes to `IFileContentCache` interface

## Open Questions

- Should `FastPathMs` be hidden (non-configurable) at a hardcoded value, since 20ms is almost always correct? Exposing it adds surface area without much practical tuning value.
- Should the coordnator emit dedicated telemetry counters (`coalescing.batches_fired`, `coalescing.entries_per_batch`, `coalescing.speculative_cached`)? Recommend yes — add to `CacheTelemetry`.
