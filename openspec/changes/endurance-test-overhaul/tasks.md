## 1. Manifest and Fixture Infrastructure

- [x] 1.1 Add `PartialSample` model to `ZipManifest.cs` with `FileName`, `Offset`, `Length`, `Sha256` properties and JSON serialization attributes
- [x] 1.2 Add `PartialSamples` list property to `ZipManifest` class
- [x] 1.3 Implement partial sample generation in `TestZipGenerator` — compute 5-8 SHA-256 samples per file at strategic offsets (start, chunk boundary, mid, random×2, near-end, tail) with reduced samples for files < 1MB and minimal for < 64KB
- [x] 1.4 Add `ZipProfile.EnduranceFull` enum value and `GenerateEnduranceFullFiles()` method — files sized for `SmallFileCutoffMb=1`: small (10KB-900KB) and large (1MB-50MB)
- [x] 1.5 Add `GetEnduranceFullFixture()` static method returning ~700MB fixture definition (13 ZIPs: 5 small, 4 large, 3 mixed, 1 edge), total under 1GB
- [x] 1.6 Write unit tests for partial sample generation — verify sample count, offsets, and SHA-256 correctness

## 2. Fail-Fast and Error Infrastructure

- [x] 2.1 Create `EnduranceFailure` record in `EnduranceFailure.cs` with properties: Suite, TaskId, Workload, Elapsed, FilePath, Operation, ExpectedHash, ActualHash, SampleDescription, Exception, CacheMemoryEntries, CacheDiskEntries, CacheBorrowedCount, CachePendingCleanup
- [x] 2.2 Implement `FormatDiagnostic()` method on `EnduranceFailure` that renders the formatted diagnostic block (suite, task, file, operation, error, cache state, stack trace)
- [x] 2.3 Implement fail-fast mechanism in main test class: linked `CancellationTokenSource`, `Interlocked.CompareExchange` for first-error capture, `CaptureCacheState()` helper

## 3. Suite Architecture

- [x] 3.1 Define `IEnduranceSuite` interface with `Name`, `TaskCount`, `RunAsync(CancellationToken)`, `GetResult()`, `PrintReport(ITestOutputHelper)` members and `SuiteResult` record (Errors, TotalOperations, VerifiedOperations, Latencies)
- [x] 3.2 Create `LatencyRecorder` class with `Record(category, elapsedMs)`, reservoir sampling (100K max per category), and `ComputePercentiles()` returning p50/p95/p99/max per category

## 4. Suite Implementations

- [x] 4.1 Implement `NormalReadSuite` (25 tasks): random file browser with SHA-256, sequential chunked reader (4KB), sequential chunked reader (64KB), hot file access pattern — all with fail-fast error reporting
- [x] 4.2 Implement `PartialReadSuite` (20 tasks): random offset reads verified against partial samples, chunk boundary crossing reads, multi-chunk spanning reads, binary search simulation — all with fail-fast
- [x] 4.3 Implement `ConcurrencyStressSuite` (20 tasks): thundering herd (20 concurrent same-file reads), parallel materialization (20 concurrent different-file reads), concurrent structure cache access — all with fail-fast
- [x] 4.4 Implement `EdgeCaseSuite` (10 tasks): zero-byte file, single-byte file, exact cutoff boundary file, read at EOF, Store-compressed entry — all with fail-fast
- [x] 4.5 Implement `EvictionValidationSuite` (10 tasks): cold scan (read all files sequentially), re-read after TTL expiry, burst-idle cycling (50 reads → 30s idle → repeat), interleaved hot/cold access — all with fail-fast
- [x] 4.6 Implement `PathResolutionSuite` (8 tasks): FileExistsAsync/DirectoryExistsAsync stress, GetFileInfoAsync, nested directory traversal, non-existent path handling — all with fail-fast
- [x] 4.7 Implement `LatencyMeasurementSuite` (5 tasks): dedicated readers measuring Stopwatch per read, first-seen hit/miss classification, Linear/Random categorization, recording into `LatencyRecorder`

## 5. Main Test Class Rewrite

- [x] 5.1 Rewrite `EnduranceTest.cs` — `InitializeAsync` with duration-aware fixture selection (< 1h → small fixture, >= 1h → full fixture), tightened cache config (`MemoryCacheSizeMb=1`, `DiskCacheSizeMb=10`, `SmallFileCutoffMb=1`)
- [x] 5.2 Implement main `[Fact]` method: instantiate all 7 suites + 2 maintenance tasks, launch 100 tasks via `Task.WhenAll`, 5-second drain on fail-fast, per-suite assertions, global `BorrowedEntryCount == 0` assertion
- [x] 5.3 Implement maintenance loop (2 tasks on dedicated threads): `EvictExpired` + `ProcessPendingCleanup` every 2 seconds, errors trigger fail-fast
- [x] 5.4 Implement latency report output: print p50/p95/p99/max per category via `ITestOutputHelper`, compare against design targets (informational only)
- [x] 5.5 Implement manifest preloading with partial samples: load `__manifest__.json` from each archive, store both entries and partial samples for verification

## 6. Verification and Validation

- [x] 6.1 Run endurance test with CI defaults (~72s) — verify all suites execute, zero errors, zero handle leaks, latency report prints
- [ ] 6.2 Run endurance test with `ENDURANCE_DURATION_HOURS=1` — verify full fixture generation, 100 tasks sustained, all checksums pass (manual verification — requires ~700MB fixture generation)
- [x] 6.3 Inject a deliberate SHA-256 corruption and verify fail-fast triggers with correct diagnostic output (validated during development — fail-fast correctly captures and formats ChunkedStream buffer bug)
- [x] 6.4 Verify `dotnet build` succeeds and all existing tests still pass (332 tests pass: 72+33+70+149+8)
