## Context

ZipDrive serves files from ZIP archives via DokanNet. Every cache miss requires: seek to `LocalHeaderOffset`, read local header, decompress entry. For sequential access patterns (Explorer thumbnails, media players, game engines), each sibling file independently incurs this full penalty.

The caching layer (`GenericCache`, `FileContentCache`, storage strategies) is stable and battle-tested with 332 passing tests and a validated 24-hour soak. The constraint is: **do not modify the concurrency primitives or storage strategies**. The prefetch feature lives entirely at the `ZipVirtualFileSystem` (Application) and `FileContentCache` coordinator levels.

Current call path: `DokanFileSystemAdapter` → `ZipVirtualFileSystem.ReadFileAsync` → `FileContentCache.ReadAsync` → `GenericCache.BorrowAsync` → `IZipReader.OpenEntryStreamAsync` (one entry, one seek).

## Goals / Non-Goals

**Goals:**
- Warm sibling files into the existing cache in a single sequential ZIP read when a file is accessed
- Trigger on both `ReadFileAsync` (locality-aware, centered on trigger) and `FindFilesAsync` (bulk warm before Explorer opens individual files)
- Discard "hole" entries between wanted siblings without decompressing them (read compressed bytes, advance position, no inflate cost)
- Deduplicate concurrent prefetch attempts for the same directory via an in-flight guard
- Keep all new config opt-in with sensible defaults; default enabled

**Non-Goals:**
- Cross-directory or recursive prefetch
- Prefetch for large files (disk-tier, ≥ `PrefetchFileSizeThresholdMb`)
- Predictive/ML-based access pattern learning
- Modifying `GenericCache`, storage strategies, or `IZipReader`

## Decisions

### D1: WarmAsync on IFileContentCache, not a side-channel

**Decision**: Add `Task WarmAsync(string archivePath, ZipEntryInfo entry, string cacheKey, Stream decompressedStream, CancellationToken ct)` to `IFileContentCache`.

**Rationale**: `ZipVirtualFileSystem` already has the decompressed stream from the sequential read. Pushing it through `FileContentCache.WarmAsync` keeps tier routing (memory vs disk by size), TTL, and telemetry in one place. The alternative — calling `GenericCache.BorrowAsync` directly — would bypass the coordinator and duplicate tier-routing logic.

**Implementation**: `WarmAsync` calls `BorrowAsync` with a factory that wraps the provided stream, then immediately disposes the handle. The entry stays cached but unprotected. No changes to `GenericCache` internals.

### D2: Sequential raw FileStream read, not IZipReader per entry

**Decision**: `PrefetchSiblingsAsync` opens one raw `FileStream` on the archive, seeks once to span start, then reads linearly — parsing local headers and decompressing or skipping entries in order.

**Rationale**: `IZipReader.OpenEntryStreamAsync` seeks per entry. For N siblings this is N seeks. A single sequential pass with skip-over-holes amortizes the I/O cost across all entries. The `ZipLocalHeader` parsing structures already exist in `ZipDrive.Infrastructure.Archives.Zip`.

**Hole handling**: When between two wanted entries, compute gap = `nextWanted.LocalHeaderOffset - currentStreamPosition`. Read gap bytes into a small reusable discard buffer (ArrayPool, 64KB). No decompression — raw bytes only. This is faster than seeking for gaps < a threshold (heuristic: always read-and-discard since fill ratio ≥ 80% guarantees holes are small relative to payload).

### D3: SpanSelector as a pure static algorithm

**Decision**: Extract span selection into a `static class SpanSelector` with a single `Select(IReadOnlyList<ZipEntryInfo> candidates, ZipEntryInfo trigger, PrefetchOptions opts) → PrefetchPlan` method.

**Rationale**: Zero dependencies, fully deterministic, trivially unit-testable without any I/O mocking. Separation of concerns — `PrefetchSiblingsAsync` handles I/O, `SpanSelector` handles logic.

**Algorithm**:
1. Sort candidates by `LocalHeaderOffset`
2. Find trigger in sorted list; take centered window of `min(count, MaxFiles)` around it
3. Compute `fillRatio = usefulBytes / spanBytes` where `spanBytes` includes holes
4. While `fillRatio < FillRatioThreshold` and `window.Count > 1`: remove the endpoint (first or last) whose removal most improves ratio
5. Return surviving window as `PrefetchPlan`

### D4: Two-level candidate cap — MaxDirectoryFiles then MaxFiles

**Decision**: Two separate config knobs with clear semantics:
- `PrefetchMaxDirectoryFiles` (default 300): cap on how many directory entries we load into memory for the algorithm. If `ListDirectory()` returns more, sort by offset and take the 300 nearest to the trigger by offset position.
- `PrefetchMaxFiles` (default 20): cap on how many files the span selector will include in the prefetch plan.

**Rationale**: Prevents O(N) metadata work in very large directories while still allowing the algorithm to see enough context to make a good span decision.

### D5: Fire-and-forget with per-directory in-flight guard

**Decision**: `PrefetchSiblingsAsync` is called with `_ = PrefetchSiblingsAsync(...)` (fire-and-forget). The `ReadFileAsync` / `FindFilesAsync` response is not delayed. A `ConcurrentDictionary<string, byte> _prefetchInFlight` keyed on `"archiveKey:dirPath"` prevents duplicate concurrent sequential reads of the same directory.

**Rationale**: Prefetch latency must never slow down the read that triggered it. The in-flight guard ensures two simultaneous `ReadFile` calls on siblings don't both perform the full sequential scan — the second one returns immediately, and `GenericCache`'s thundering herd protection handles any residual duplicate `WarmAsync` calls.

**Token**: Prefetch tasks use a `CancellationToken` derived from the VFS lifetime (`IHostApplicationLifetime`), not the per-request token (which is cancelled when the Dokan request completes).

### D6: Trigger on both ReadFile and FindFiles

**Decision**: Hook prefetch in both `ReadFileAsync` (after dispatching the actual read) and `FindFilesAsync` (after returning the listing).

**Rationale**: `FindFilesWithPattern` fires before any `ReadFile` in Explorer's thumbnail generation sequence. Triggering on listing gives a head start — files are warming while the listing response is still being processed by Explorer. `ReadFile` trigger catches non-Explorer access patterns (apps that open files without listing first). The in-flight guard prevents double-work when both triggers fire for the same directory.

## Risks / Trade-offs

**[Cache pressure from aggressive prefetch]** → Mitigation: `PrefetchFileSizeThresholdMb` (default 10MB, well below the 50MB tier cutoff) limits per-file size. `PrefetchMaxFiles=20` caps per-trigger volume. Normal LRU eviction handles overflow.

**[Sequential read wastes I/O if user doesn't continue]** → Mitigation: Fill ratio threshold (80%) ensures only high-density regions are read. Fire-and-forget means no read is blocked waiting for prefetch to complete.

**[WarmAsync called with a stream that errors mid-read]** → Mitigation: `WarmAsync` propagates exceptions to the background task only; the triggering `ReadFileAsync` has already returned. Logged at Warning level, not Fatal.

**[Large directories (>300 files) get truncated window]** → Accepted trade-off: we take the 300 nearest-by-offset entries, which are the most spatially local and most likely to benefit. Rare edge case.

**[Prefetch warms a file that gets evicted before it's read]** → Accepted: normal LRU behavior. Prefetch is best-effort, not guaranteed.

## Migration Plan

- Feature is additive; no existing behavior changes
- `PrefetchEnabled: true` default — can set to `false` in `appsettings.jsonc` to disable entirely
- No schema changes, no data migration, no rollback complexity
- Can be deployed and reverted by config change alone

## Open Questions

- None — all decisions made in exploration phase.
