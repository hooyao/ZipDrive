## 1. Config and Domain

- [x] 1.1 Add `PrefetchEnabled`, `PrefetchFileSizeThresholdMb`, `PrefetchMaxFiles`, `PrefetchMaxDirectoryFiles`, `PrefetchFillRatioThreshold` to `CacheOptions` with defaults (true, 10, 20, 300, 0.80)
- [x] 1.2 Add computed property `PrefetchFileSizeThresholdBytes` to `CacheOptions`
- [x] 1.3 Add `WarmAsync(string archivePath, ZipEntryInfo entry, string cacheKey, Stream decompressedStream, CancellationToken ct)` to `IFileContentCache`
- [x] 1.4 Update `appsettings.jsonc` with the five new `Cache:Prefetch*` keys and comments

## 2. FileContentCache WarmAsync

- [x] 2.1 Implement `WarmAsync` in `FileContentCache` — route to memory or disk tier by size, call `BorrowAsync` with a factory that wraps the provided stream, immediately dispose the handle
- [x] 2.2 Add unit test: `WarmAsync` stores entry in correct tier (memory for small, disk for large)
- [x] 2.3 Add unit test: `WarmAsync` on existing key is a no-op (entry unchanged, no duplicate extraction)
- [x] 2.4 Add unit test: after `WarmAsync`, `ReadAsync` for the same key returns a cache hit

## 3. SpanSelector Algorithm

- [x] 3.1 Create `PrefetchPlan` record: `IReadOnlyList<ZipEntryInfo> Entries`, `long SpanStart`, `long SpanEnd`
- [x] 3.2 Create `static class SpanSelector` with `Select(IReadOnlyList<ZipEntryInfo> candidates, ZipEntryInfo trigger, int maxFiles, double fillRatioThreshold) → PrefetchPlan`
- [x] 3.3 Implement span selection: sort by offset, find trigger, take centered window of `maxFiles`, shrink from endpoints while fill ratio < threshold
- [x] 3.4 Add unit test: all dense siblings selected when fill ratio is high
- [x] 3.5 Add unit test: large-hole endpoint removed when fill ratio < threshold
- [x] 3.6 Add unit test: window capped at `maxFiles` entries centered on trigger
- [x] 3.7 Add unit test: single-entry window returned when no combination meets threshold
- [x] 3.8 Add unit test: trigger file itself excluded from prefetch candidates (already being read)

## 4. Telemetry

- [x] 4.1 Add prefetch counters to `CacheTelemetry`: `prefetch.files_warmed`, `prefetch.bytes_read`, `prefetch.skipped_inflight`
- [x] 4.2 Add histogram: `prefetch.span_read_duration` (ms)

## 5. PrefetchSiblingsAsync in ZipVirtualFileSystem

- [x] 5.1 Add `ConcurrentDictionary<string, byte> _prefetchInFlight` field to `ZipVirtualFileSystem`
- [x] 5.2 Implement `PrefetchSiblingsAsync(ArchiveDescriptor archive, ArchiveStructure structure, string triggerInternalPath, CancellationToken ct)`:
  - Build `dirKey = $"{archiveKey}:{dirPath}"`; return early if already in-flight (TryAdd fails)
  - Call `structure.ListDirectory(dir)` → filter (not dir, not trigger, size ≤ threshold)
  - If `candidates.Count > MaxDirectoryFiles`: sort by offset, take `MaxDirectoryFiles` nearest to trigger
  - Call `SpanSelector.Select(candidates, triggerEntry, MaxFiles, FillRatioThreshold)` → `PrefetchPlan`
  - If plan is empty: return
  - Open one raw `FileStream` on the archive, seek to `plan.SpanStart`
  - For each entry in plan order: read local header bytes (skip), then either decompress + `WarmAsync` (wanted) or read-and-discard compressed bytes (hole)
  - Remove `dirKey` from `_prefetchInFlight` in `finally`
  - Emit telemetry on completion
- [x] 5.3 Hook into `ReadFileAsync`: after dispatching the actual read, fire-and-forget `PrefetchSiblingsAsync` if `PrefetchEnabled`, using VFS lifetime `CancellationToken`
- [x] 5.4 Hook into `FindFilesAsync`: after building the listing, fire-and-forget `PrefetchSiblingsAsync` with no specific trigger (use first entry by offset as trigger, or pass null for "warm all in window")
- [x] 5.5 Inject `IHostApplicationLifetime` into `ZipVirtualFileSystem` for the long-lived prefetch cancellation token

## 6. Integration Tests

- [x] 6.1 Test: read file A in a directory with small siblings → siblings are in cache after prefetch completes (use `Task.Delay` + `EntryCount` or dedicated wait helper)
- [x] 6.2 Test: two concurrent reads in same directory trigger only one sequential scan (verify single-file I/O using in-flight counter or log assertion)
- [x] 6.3 Test: `PrefetchEnabled=false` → no siblings warmed after read
- [x] 6.4 Test: sibling above size threshold excluded from prefetch candidates
- [x] 6.5 Test: directory with >300 files — only nearest 300 by offset considered, algorithm still runs

## 7. Build and Test

- [x] 7.1 `dotnet build ZipDrive.slnx` — zero warnings/errors
- [x] 7.2 `dotnet test` — all existing + new tests pass
