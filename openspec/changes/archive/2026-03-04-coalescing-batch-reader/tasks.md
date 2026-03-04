## 1. Configuration

- [x] 1.1 Add `CoalescingOptions` POCO to `ZipDrive.Infrastructure.Caching` with properties: `Enabled`, `FastPathMs`, `WindowMs`, `DensityThreshold`, `SpeculativeCache` and correct defaults
- [x] 1.2 Register `IOptions<CoalescingOptions>` in `Program.cs` DI wiring, bound from `"Coalescing"` config section
- [x] 1.3 Add `"Coalescing"` section to `appsettings.jsonc` with all five properties and their defaults

## 2. CoalescingBatchCoordinator — Core

- [x] 2.1 Create `PendingRequest` record: `archivePath`, `entry` (`ZipEntryInfo`), `cacheKey`, `TaskCompletionSource<ICacheHandle<Stream>>`
- [x] 2.2 Create `CoalescingBatchCoordinator` class with per-archive `ConcurrentDictionary<string, Queue<PendingRequest>>` and two-timer logic (`FastPathMs` / `WindowMs`)
- [x] 2.3 Implement `SubmitAsync(archivePath, entry, cacheKey, ct)` — enqueues request, starts timer if not running, returns awaitable TCS task
- [x] 2.4 Implement timer logic: start `FastPathMs` timer on first request; if second request arrives within window, extend to `WindowMs`; on expiry call `DispatchBatchAsync`

## 3. CoalescingBatchCoordinator — Batch Dispatch

- [x] 3.1 Implement `GroupIntoBatches(requests)`: sort by `LocalHeaderOffset`, greedy forward scan using density formula `sum(CompressedSize) / (last.end - first.start)`, split on threshold violation
- [x] 3.2 Implement `ExecuteBatchAsync(batch, zipReader)`: sequential pass — read Local Header, decompress requested entry, signal TCS; advance past hole entries (read 30-byte LH header, seek forward `FileNameLength + ExtraFieldLength + CompressedSize`)
- [x] 3.3 Implement speculative caching in `ExecuteBatchAsync`: when `SpeculativeCache=true`, decompress hole entries and call `BorrowAsync` for them; release handle immediately after storing
- [x] 3.4 Implement `DispatchBatchAsync`: for each batch group open one `ZipReader`, seek to first `LocalHeaderOffset`, call `ExecuteBatchAsync`, dispose reader; single-entry batches use existing factory path
- [x] 3.5 Handle `BorrowAsync` returning existing entry for a key already being materialized (thundering herd interplay) — pass the existing handle to the TCS without re-extracting

## 4. FileContentCache Integration

- [x] 4.1 Inject `CoalescingBatchCoordinator` (or `null` when disabled) into `FileContentCache` constructor
- [x] 4.2 In `ReadAsync()`, add coalescing branch: if memory tier miss AND coordinator != null → call `coordinator.SubmitAsync()` and await TCS; else existing factory path
- [x] 4.3 Verify disk-tier path is completely unchanged (no coordinator involvement)

## 5. Telemetry

- [x] 5.1 Add `coalescing.batches_fired` Counter to `CacheTelemetry`
- [x] 5.2 Add `coalescing.entries_per_batch` Histogram to `CacheTelemetry`
- [x] 5.3 Add `coalescing.speculative_cached` Counter to `CacheTelemetry`
- [x] 5.4 Emit metrics in `CoalescingBatchCoordinator.DispatchBatchAsync` and `ExecuteBatchAsync`

## 6. Tests — Unit

- [x] 6.1 `CoalescingOptions` defaults test: verify all five defaults are correct
- [x] 6.2 `GroupIntoBatches` unit tests: empty input, single entry, all above threshold, density split, exact threshold boundary
- [x] 6.3 Density formula unit test: verify sum/range calculation matches expected values for known offsets and sizes
- [x] 6.4 `FastPathMs` timer fires solo: single request with no second arrival → extracted without batch overhead
- [x] 6.5 `WindowMs` trigger: two requests within `FastPathMs` → coordinator uses full window, both batched together
- [x] 6.6 Hole skip (no speculative cache): hole entry bytes advanced without decompression; only requested entries materialized
- [x] 6.7 Speculative caching: hole entry decompressed and cached; subsequent read of hole entry is a cache hit
- [x] 6.8 Coalescing disabled: `Enabled=false` → standard factory path used, no window delay

## 7. Tests — Integration

- [x] 7.1 Burst read test: 20 concurrent `ReadAsync` calls for different small entries from same archive → verify single `ZipReader` seek (or minimal seeks), all entries cached correctly
- [x] 7.2 SHA-256 correctness: each entry extracted via batch produces bytes identical to individual extraction
- [x] 7.3 Concurrent callers of same entry during batch: thundering herd protection holds — single materialization, multiple handles served correctly
- [x] 7.4 Large file bypass: entry >= `SmallFileCutoffMb` never goes through coordinator, disk tier used

## 8. Build and Verify

- [x] 8.1 `dotnet build ZipDrive.slnx` — zero warnings and errors
- [x] 8.2 `dotnet test` — all 346 tests pass including 14 new coalescing tests
