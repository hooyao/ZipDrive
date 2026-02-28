## Context

The current endurance test (`tests/ZipDrive.EnduranceTests/EnduranceTest.cs`) runs 26 concurrent tasks against a ~50MB fixture with tight cache limits. It validates full-file SHA-256 checksums and checks for handle leaks. However, it lacks partial-read verification, latency measurement, comprehensive edge case coverage, and fail-fast error handling. The test defaults to ~72 seconds for CI.

The test infrastructure consists of:
- `TestZipGenerator` — generates ZIP fixtures with deterministic content and embedded `__manifest__.json`
- `ZipManifest` / `ManifestEntry` — models for the embedded manifest
- `ZipProfile` enum — predefined fixture profiles (TinyFiles, EnduranceMixed, EdgeCases, etc.)
- `ZipVirtualFileSystem` — the system under test, providing `ReadFileAsync`, `ListDirectoryAsync`, etc.

## Goals / Non-Goals

**Goals:**
- 100 concurrent tasks exercising all cache code paths simultaneously
- SHA-256 verification on both full-file reads and partial reads (5-8 samples per file at strategic offsets)
- Fail-fast: first error kills all tasks immediately with rich diagnostic output for debugging
- Latency measurement with p50/p95/p99/max reporting (informational, no hard assertions)
- Comprehensive normal use case and edge case coverage via 7 virtual test suites
- Duration-aware fixture sizing: ~50MB for CI (< 1h), ~700MB for manual 24h runs
- Tighter cache limits (`MemoryCacheSizeMb=1`, `DiskCacheSizeMb=10`, `SmallFileCutoffMb=1`) for constant eviction

**Non-Goals:**
- Production code changes — this is purely test infrastructure
- Hard-fail latency assertions — CI environments are too noisy for reliable latency thresholds
- Write/mutation testing — ZipDrive is read-only
- Multi-process or distributed testing

## Decisions

### 1. Suite-Based Architecture (virtual separation, shared VFS)

**Decision**: Define an `IEnduranceSuite` interface. Each suite owns a group of tasks with its own error tracking, operation counters, and latency recording. All suites share the same `ZipVirtualFileSystem` instance and run concurrently in a single `[Fact]`.

**Rationale**: The user wants "virtually separate" suites that load the VFS simultaneously. An interface provides clean separation without test framework complexity (no xUnit collection fixtures or parallel test classes). Each suite reports independently, making it easy to identify which category of workload failed.

**Alternatives considered**:
- Separate `[Fact]` methods with shared fixture: Would require `IClassFixture` and concurrent test execution config. xUnit v2 runs test methods sequentially within a class by default. More complexity for same result.
- Single monolithic method (current approach): Hard to attribute errors to workload categories.

### 2. Fail-Fast via Linked CancellationTokenSource

**Decision**: Use two `CancellationTokenSource` instances: `_durationCts` (timeout) and `_failFastCts` (linked to duration). Any task that encounters an error captures an `EnduranceFailure` object via `Interlocked.CompareExchange` (only first error wins), then calls `_failFastCts.Cancel()`. All 100 tasks observe this token and exit cleanly.

**Rationale**: After cancellation, subsequent errors are cascading noise (e.g., `ObjectDisposedException` from cancelled streams). Only the first error matters for debugging. `Interlocked.CompareExchange` is lock-free and guarantees exactly one error is captured.

**Post-cancellation drain**: After `Task.WhenAll` returns, allow up to 5 seconds for `using` blocks to dispose cache handles before asserting `BorrowedEntryCount == 0`.

### 3. EnduranceFailure — Rich Error Context Object

**Decision**: Capture a structured `EnduranceFailure` record containing: suite name, task ID, workload name, elapsed time, file path, operation description, expected/actual values (for checksum mismatches), exception with full stack trace, and a snapshot of cache state (`MemoryEntryCount`, `DiskEntryCount`, `BorrowedEntryCount`, `PendingCleanupCount`).

**Rationale**: A 24-hour test that fails at hour 18 must provide enough context to reproduce the bug without re-running. The cache state snapshot is critical for diagnosing eviction-related races.

**Format**: Rendered as a formatted block in test output via `ITestOutputHelper`, not just an exception message.

### 4. Partial Checksum Samples in Manifest

**Decision**: Extend `ZipManifest` with a `PartialSamples` list. Each `PartialSample` contains `FileName`, `Offset`, `Length`, and `Sha256`. During fixture generation, `TestZipGenerator` computes 5-8 samples per file at strategic positions:

1. **Start**: offset 0, length min(fileSize, 64KB)
2. **Chunk boundary cross**: offset (chunkSize - 32KB), length 64KB — straddles the 1MB chunk boundary
3. **Mid-file**: offset fileSize/2, length 64KB
4. **Random 1**: deterministic offset from seed
5. **Random 2**: deterministic offset from seed
6. **Near-end**: offset (fileSize - 64KB), length 64KB
7. **Tail**: offset (fileSize - 4KB), length 4KB

For small files (< 64KB), only sample 1 (the entire file) is generated. For files < 1MB, samples 1, 3, and 7 are generated.

**Rationale**: These positions target the most bug-prone areas: chunk boundary crossing (off-by-one in `ChunkedStream`), near-EOF (buffer underrun), and mid-file (general correctness). Deterministic random samples add coverage without explosion.

### 5. Latency Recording — Lightweight Concurrent Histogram

**Decision**: Each latency-measuring task records `Stopwatch.Elapsed` into a `ConcurrentBag<(string Category, double Ms)>`. Post-run, the main thread sorts and computes percentiles. Categories: `CacheHit.Small`, `CacheHit.Large`, `CacheMiss.Small`, `CacheMiss.Large`, `Linear`, `Random`, `PartialRead`.

**Cache hit vs miss classification**: Each latency task tracks "first seen" file paths. First access = miss, subsequent = hit. This is approximate but mirrors real access patterns without leaking cache internals.

**Rationale**: `ConcurrentBag` is allocation-friendly under high contention and has no lock overhead for adds. Percentile computation is a one-time post-run sort. No need for a proper histogram library for reporting-only metrics.

**Alternatives considered**:
- `System.Diagnostics.Metrics` histograms: Overkill for test-only reporting. Would require OTel SDK wiring in the test project.
- Pre-bucketed histogram: Adds complexity without benefit when we just sort at the end.

### 6. Duration-Aware Fixture Profiles

**Decision**: Detect `ENDURANCE_DURATION_HOURS`. If < 1.0, use the existing `GetEnduranceFixture()` (~50MB, 6 ZIPs). If >= 1.0, use a new `GetEnduranceFullFixture()` (~700MB, 13 ZIPs) with richer file distribution.

**Rationale**: Generating 700MB of ZIPs takes 30-60 seconds. For a 72-second CI run, that's unacceptable overhead. The existing ~50MB fixture still exercises all code paths in the short run; the full fixture provides broader coverage for the 24h soak.

### 7. New ZipProfile: `EnduranceFull`

**Decision**: Add a new `ZipProfile.EnduranceFull` enum value with a `GenerateEnduranceFullFiles()` method. This profile generates files sized for `SmallFileCutoffMb=1`:
- Small tier (< 1MB): files from 10KB to 900KB
- Large tier (>= 1MB): files from 1MB to 50MB

The `EnduranceMixed` profile remains unchanged for backward compatibility.

### 8. File Organization

**Decision**: Keep everything in the existing `EnduranceTest.cs` file, plus new supporting files:
- `EnduranceTest.cs` — main test class with setup, teardown, and the single `[Fact]`
- `IEnduranceSuite.cs` — interface definition
- `Suites/NormalReadSuite.cs`, `Suites/PartialReadSuite.cs`, etc. — one file per suite
- `EnduranceFailure.cs` — error context record
- `LatencyRecorder.cs` — concurrent latency collection and reporting

All in the `ZipDrive.EnduranceTests` project. Suite files go in a `Suites/` subfolder to keep the project root clean.

## Risks / Trade-offs

- **[Risk] 700MB fixture generation is slow (~30-60s)** → Mitigated by duration-aware profile selection. CI uses small fixture. Only manual 24h runs pay the cost.
- **[Risk] 100 concurrent tasks may starve the thread pool** → Mitigated by using `async/await` throughout (no blocking calls except the 2 maintenance threads). The maintenance loop already uses dedicated threads.
- **[Risk] `ConcurrentBag` for latency samples may grow large over 24h** → At ~1000 samples/sec across 5 latency tasks, 24h = ~430M entries × 24 bytes = ~10GB. This is too much. **Mitigation**: Use reservoir sampling (keep at most 100K samples per category, randomly replace older samples). This bounds memory at ~17MB total regardless of duration.
- **[Risk] Fail-fast cancellation may leave temp files on disk** → `DisposeAsync` still runs (xUnit guarantees `IAsyncLifetime.DisposeAsync`). The 5-second drain before assertion ensures handles are released. Temp directory cleanup in `DisposeAsync` handles the rest.
- **[Trade-off] Report-only latency (no hard fail)** → Acceptable because CI hardware varies wildly. A p99 of 200ms on a loaded CI runner doesn't indicate a bug. The report gives developers visibility; they can investigate outliers manually.
- **[Trade-off] "First seen" cache hit/miss classification is approximate** → A file read by task A (miss) may be a cache hit for task B moments later. This is realistic — it measures what the application actually experiences, not internal cache state.
