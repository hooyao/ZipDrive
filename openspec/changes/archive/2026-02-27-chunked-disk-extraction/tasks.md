## 1. Core Types

- [x] 1.1 Create `ChunkedFileEntry` class with chunk state tracking (`BitArray`, `TaskCompletionSource<bool>[]`), extraction lifecycle (`CancellationTokenSource`, `Task`), chunk index calculation methods (`GetChunkIndex`, `GetChunkOffset`, `GetChunkLength`), `IsChunkReady`, `WaitForChunkAsync`, and `IDisposable` implementation
- [x] 1.2 Create `ChunkedStream : Stream` with chunk-aware `ReadAsync`/`Read`, `EnsureChunkReadyAsync` safety gate, cross-chunk boundary read handling, `Seek`/`Position` support, and `Dispose` that closes only the reader's `FileStream`
- [x] 1.3 Write unit tests for `ChunkedFileEntry`: chunk index calculations, last chunk partial size, TCS signaling order, cancellation propagation, exception propagation, double-dispose safety
- [x] 1.4 Write unit tests for `ChunkedStream`: read from ready chunk (instant), read from pending chunk (waits), cross-chunk boundary reads, EOF returns zero, seek updates position, concurrent readers independent positions, cancelled/failed extraction throws, sync Read fallback

## 2. Storage Strategy

- [x] 2.1 Create `ChunkedDiskStorageStrategy : IStorageStrategy<Stream>` with `MaterializeAsync` (sparse file creation, background extraction start, await first chunk, return full-size `StoredEntry`), `Retrieve` (return fresh `ChunkedStream`), `Dispose` (cancel extraction, delete file), `RequiresAsyncCleanup = true`, and per-process temp directory isolation
- [x] 2.2 Implement background extraction task in `ChunkedFileEntry.ExtractAsync`: sequential chunk writing with `FileShare.Read`, flush after each chunk, bitmap + TCS signaling, error/cancellation handling in `finally` with `DeflateStream` + `OnDisposed` cleanup
- [x] 2.3 Write unit tests for `ChunkedDiskStorageStrategy`: `MaterializeAsync` returns after first chunk, reports full size, `Retrieve` returns fresh `ChunkedStream` each call, `Dispose` cancels extraction and deletes file, `RequiresAsyncCleanup` returns true

## 3. Integration

- [x] 3.1 Add `ChunkSizeMb` property to `CacheOptions` with default 10, computed `ChunkSizeBytes` property
- [x] 3.2 Update `FileContentCache` constructor: replace `DiskStorageStrategy` with `ChunkedDiskStorageStrategy`, pass `ChunkSizeBytes` from options, update `_diskStorageStrategy` field type
- [x] 3.3 Update `FileContentCache.DeleteCacheDirectory` to call `ChunkedDiskStorageStrategy.DeleteCacheDirectory`
- [x] 3.4 Remove `DiskStorageStrategy.cs` and `DiskCacheEntry` record (fully replaced)
- [x] 3.5 Update DI wiring in `ZipDrive.Cli/Program.cs` if needed (verify `FileContentCache` constructor resolves correctly)
- [x] 3.6 Add `ChunkSizeMb` to `appsettings.jsonc` under `Cache` section

## 4. Telemetry

- [x] 4.1 Add chunked extraction metrics to `CacheTelemetry`: `cache.chunks.extracted` counter, `cache.chunks.waits` counter, `cache.chunks.wait_duration` histogram, `cache.chunks.extraction_duration` histogram
- [x] 4.2 Instrument `ChunkedFileEntry.ExtractAsync` with chunk extraction counter and duration
- [x] 4.3 Instrument `ChunkedStream.EnsureChunkReadyAsync` with chunk wait counter and duration when await is needed
- [x] 4.4 Add structured logging: extraction start (Information), chunk completion (Debug), reader wait (Debug), extraction complete (Information), extraction cancelled (Warning)

## 5. Integration Tests

- [x] 5.1 Write thundering herd test: 20 threads `BorrowAsync` same key → 1 `MaterializeAsync`, 1 extraction task, all get valid `ChunkedStream` handles
- [x] 5.2 Write concurrent readers during extraction test: readers at different offsets, SHA-256 verify each chunk, ready chunks < 1ms, pending chunks block correctly
- [x] 5.3 Write eviction during extraction test: evict entry while extraction runs → extraction cancelled, no file leaks, no handle leaks
- [x] 5.4 Write RefCount protection test: entry with active readers not evicted under capacity pressure
- [x] 5.5 Write post-eviction re-borrow test: after eviction, same key triggers fresh extraction
- [x] 5.6 Write FileContentCache routing tests: < 50MB → memory tier, >= 50MB → chunked disk tier, exact cutoff boundary

## 6. Adapt Existing Tests

- [x] 6.1 Update existing disk-tier tests in `ZipDrive.Domain.Tests` / `ZipDrive.Infrastructure.Tests` to work with `ChunkedDiskStorageStrategy` instead of `DiskStorageStrategy`
- [x] 6.2 Verify endurance test suite passes with new chunked disk tier (no test changes expected — tests go through `FileContentCache` interface). NOTE: Endurance test has pre-existing failure on `main` (eviction counter = 0) — not caused by chunked extraction change.
- [x] 6.3 Build solution and run full test suite — all 318 non-endurance tests pass. Endurance test failure is pre-existing.

## 7. Endurance Testing

- [x] 7.1 Extend endurance test fixture with files >= 50MB that exercise chunked extraction path. Added 2 large files (6-8MB) to EnduranceMixed profile, set ChunkSizeMb=1 to produce multiple chunks. Fixed pre-existing endurance test failure (PeriodicTimer starvation → dedicated Thread with initial increment).
- [x] 7.2 Add concurrent reader scenarios that read from large files at random offsets during extraction. Added RunRandomOffsetLargeFileReaderAsync (3 tasks, random seeks on largest files).
- [x] 7.3 Run endurance test for extended duration, verify zero errors, zero handle leaks (`BorrowedEntryCount == 0`), SHA-256 integrity. Passed 3/3 consecutive runs + full suite (325 tests).
