## ADDED Requirements

### Requirement: Suite-based endurance test architecture
The endurance test SHALL consist of a single `[Fact]` method that launches 7 virtual test suites concurrently, totaling 100 tasks, against a shared `ZipVirtualFileSystem` instance. Each suite SHALL track its own errors, operation counts, and verification results independently.

#### Scenario: All suites run concurrently
- **WHEN** the endurance test starts
- **THEN** all 7 suites launch their tasks concurrently (100 tasks total) and run until the duration expires or fail-fast triggers

#### Scenario: Per-suite result reporting
- **WHEN** the endurance test completes (success or fail-fast)
- **THEN** each suite reports its own operation count, verified count, and error list independently

#### Scenario: Suite task distribution
- **WHEN** the 100 tasks are allocated across suites
- **THEN** the distribution SHALL be: Normal Reads (25), Partial Reads (20), Concurrency Stress (20), Edge Cases (10), Eviction Validation (10), Path Resolution (8), Latency Measurement (5), plus 2 Maintenance tasks

### Requirement: Fail-fast on first error
The endurance test SHALL cancel all running tasks immediately when any task encounters an error. Only the first error SHALL be captured with full diagnostic context.

#### Scenario: Exception triggers fail-fast
- **WHEN** any task throws an unhandled exception (other than `OperationCanceledException`)
- **THEN** the system captures an `EnduranceFailure` record and cancels the shared `CancellationTokenSource`, stopping all 100 tasks

#### Scenario: Checksum mismatch triggers fail-fast
- **WHEN** a SHA-256 verification (full or partial) detects a mismatch
- **THEN** the system captures an `EnduranceFailure` record with expected and actual hashes and cancels all tasks

#### Scenario: Only first error is captured
- **WHEN** multiple tasks encounter errors after cancellation propagates
- **THEN** only the first error (captured via `Interlocked.CompareExchange`) is reported; subsequent errors are discarded

#### Scenario: Graceful drain after cancellation
- **WHEN** fail-fast cancellation fires
- **THEN** the system waits up to 5 seconds for all tasks to finish current operations and dispose cache handles before asserting `BorrowedEntryCount == 0`

### Requirement: Rich error diagnostics
The `EnduranceFailure` record SHALL contain sufficient context to reproduce and debug the failure without re-running the 24-hour test.

#### Scenario: Exception failure diagnostics
- **WHEN** a task fails with an exception
- **THEN** the `EnduranceFailure` includes: suite name, task ID, workload name, elapsed time since test start, file path, operation description, full exception with stack trace, and cache state snapshot (memory entry count, disk entry count, borrowed count, pending cleanup count)

#### Scenario: Checksum failure diagnostics
- **WHEN** a task detects a SHA-256 mismatch
- **THEN** the `EnduranceFailure` includes all fields from exception failures plus: expected hash, actual hash, offset, length, and sample description (e.g., "chunk boundary cross at 1MB")

#### Scenario: Formatted error output
- **WHEN** the test fails
- **THEN** the error is rendered as a formatted diagnostic block via `ITestOutputHelper` with clear section headers (Suite, Task, File, Operation, Error, Cache State, Stack Trace)

### Requirement: Partial read checksum verification
The manifest SHALL contain pre-computed SHA-256 checksums for byte ranges within files. The endurance test SHALL verify partial reads against these samples.

#### Scenario: Partial samples generated during fixture creation
- **WHEN** `TestZipGenerator` creates a ZIP file
- **THEN** the embedded `__manifest__.json` includes a `partialSamples` array with 5-8 `PartialSample` entries per file (for files >= 64KB), each containing `fileName`, `offset`, `length`, and `sha256`

#### Scenario: Strategic sample placement
- **WHEN** partial samples are generated for a file >= 1MB
- **THEN** samples SHALL be placed at: start (offset 0, 64KB), chunk boundary cross (chunkSize - 32KB, 64KB), mid-file (fileSize/2, 64KB), two deterministic random offsets (64KB each), near-end (fileSize - 64KB, 64KB), and tail (fileSize - 4KB, 4KB)

#### Scenario: Reduced samples for small files
- **WHEN** partial samples are generated for a file < 1MB but >= 64KB
- **THEN** samples SHALL include: start (offset 0, 64KB), mid-file, and tail

#### Scenario: Minimal samples for tiny files
- **WHEN** partial samples are generated for a file < 64KB
- **THEN** a single sample covering the entire file (offset 0, length = fileSize) SHALL be generated

#### Scenario: Partial read verification in test
- **WHEN** a partial read task reads bytes at a specific offset and length
- **THEN** the read data SHALL be hashed with SHA-256 and compared against the matching `PartialSample` entry; mismatch triggers fail-fast

### Requirement: Full-file checksum verification
All full-file reads SHALL be verified against the manifest's per-entry SHA-256 hash (existing behavior, preserved).

#### Scenario: Full file SHA-256 match
- **WHEN** a task reads an entire file via `ReadFileAsync` at offset 0
- **THEN** the SHA-256 of the returned bytes SHALL match the `ManifestEntry.Sha256` value

#### Scenario: Full file SHA-256 mismatch
- **WHEN** the computed SHA-256 does not match the manifest
- **THEN** fail-fast triggers with expected/actual hashes in the `EnduranceFailure`

### Requirement: Latency measurement and reporting
The endurance test SHALL measure read latency across multiple categories and print a summary report. Latency results SHALL NOT cause test failure.

#### Scenario: Latency recording per read
- **WHEN** a latency measurement task performs a `ReadFileAsync` call
- **THEN** the elapsed time is recorded with its category (CacheHit.Small, CacheHit.Large, CacheMiss.Small, CacheMiss.Large, Linear, Random, PartialRead)

#### Scenario: Cache hit vs miss classification
- **WHEN** a latency task reads a file for the first time
- **THEN** it is classified as CacheMiss; subsequent reads of the same file are classified as CacheHit

#### Scenario: Percentile report output
- **WHEN** the endurance test completes (success or failure)
- **THEN** a latency report is printed showing p50, p95, p99, and max for each category, plus comparison against design targets (cache hit < 1ms, cache miss small < 100ms, cache miss large first byte ~50ms)

#### Scenario: Bounded sample collection
- **WHEN** the test runs for extended duration (24h)
- **THEN** latency samples SHALL be bounded via reservoir sampling (max 100K per category) to prevent unbounded memory growth

### Requirement: Duration-aware fixture sizing
The fixture size SHALL scale with the configured test duration to balance CI speed and long-run coverage.

#### Scenario: CI duration (< 1 hour)
- **WHEN** `ENDURANCE_DURATION_HOURS` is less than 1.0 (or absent, defaulting to 0.02)
- **THEN** the test uses the existing ~50MB fixture (`GetEnduranceFixture()`)

#### Scenario: Manual endurance duration (>= 1 hour)
- **WHEN** `ENDURANCE_DURATION_HOURS` is >= 1.0
- **THEN** the test generates a full ~700MB fixture (`GetEnduranceFullFixture()`) with 13 ZIPs spanning small (10KB-900KB), large (1MB-50MB), mixed, and edge case files, total size under 1GB

### Requirement: Tight cache configuration for eviction pressure
The endurance test SHALL use cache limits small enough to force constant eviction under the test workload.

#### Scenario: Cache configuration values
- **WHEN** the endurance test initializes the cache
- **THEN** it SHALL use: `MemoryCacheSizeMb=1`, `DiskCacheSizeMb=10`, `SmallFileCutoffMb=1`, `ChunkSizeMb=1`, `DefaultTtlMinutes=1`, `EvictionCheckIntervalSeconds=2`

#### Scenario: Eviction occurs during test
- **WHEN** total file content exceeds cache capacity (which it always will with ~700MB content vs 11MB cache)
- **THEN** eviction cycles occur, entries are re-materialized on subsequent access, and all reads still pass checksum verification

### Requirement: Normal Read Suite
The Normal Read Suite (25 tasks) SHALL exercise standard file access patterns with full-file SHA-256 verification.

#### Scenario: Random file browser with verification
- **WHEN** a browser task runs
- **THEN** it randomly selects archives and files, reads entire files, and verifies SHA-256 against manifest

#### Scenario: Sequential chunked reader (4KB blocks)
- **WHEN** a sequential reader task runs with 4KB block size
- **THEN** it reads entire files in 4KB sequential chunks, accumulates content, and verifies the full SHA-256

#### Scenario: Sequential chunked reader (64KB blocks)
- **WHEN** a sequential reader task runs with 64KB block size
- **THEN** it reads entire files in 64KB sequential chunks, accumulates content, and verifies the full SHA-256

#### Scenario: Hot file access pattern
- **WHEN** a hot-file task runs
- **THEN** it repeatedly reads the same file in a tight loop, verifying SHA-256 each time, exercising LRU promotion and cache hit paths

### Requirement: Partial Read Suite
The Partial Read Suite (20 tasks) SHALL exercise partial/random reads with checksum verification against pre-computed partial samples.

#### Scenario: Random offset read with partial verification
- **WHEN** a task reads at a random offset matching a partial sample
- **THEN** the read data SHA-256 SHALL match the sample's pre-computed hash

#### Scenario: Chunk boundary crossing read
- **WHEN** a task reads 64KB starting at (chunkSize - 32KB)
- **THEN** the read crosses a chunk boundary in `ChunkedStream` and the SHA-256 matches the partial sample

#### Scenario: Multi-chunk spanning read
- **WHEN** a task reads a buffer spanning 3+ chunks (e.g., 3MB read)
- **THEN** the read completes without error and content is correct

#### Scenario: Binary search simulation
- **WHEN** a task reads 1 byte at many different offsets across a large file
- **THEN** each read returns the correct byte (verified against partial samples where offsets align, otherwise no error = pass)

### Requirement: Concurrency Stress Suite
The Concurrency Stress Suite (20 tasks) SHALL exercise concurrent access patterns that test the cache's concurrency layers.

#### Scenario: Thundering herd — same file concurrent reads
- **WHEN** N tasks (e.g., 20) simultaneously read the same uncached file
- **THEN** the file is materialized exactly once (via `Lazy<Task<T>>`), all readers get correct data, and SHA-256 passes for all

#### Scenario: Parallel materialization — different files concurrent reads
- **WHEN** N tasks simultaneously read N different uncached files
- **THEN** all files materialize in parallel (different keys don't block each other) and all SHA-256 checks pass

#### Scenario: Concurrent structure cache access
- **WHEN** multiple tasks request structure from the same archive simultaneously
- **THEN** the Central Directory is parsed once and all tasks receive the same `ArchiveStructure`

### Requirement: Edge Case Suite
The Edge Case Suite (10 tasks) SHALL exercise boundary conditions and special file types.

#### Scenario: Zero-byte file read
- **WHEN** a task reads a 0-byte file
- **THEN** `ReadFileAsync` returns 0 bytes without error

#### Scenario: Single-byte file read
- **WHEN** a task reads a 1-byte file
- **THEN** `ReadFileAsync` returns exactly 1 byte with correct content

#### Scenario: File at exact cutoff boundary
- **WHEN** a task reads a file whose size equals `SmallFileCutoffMb` (1MB)
- **THEN** the file is routed to the disk tier (>= cutoff) and reads correctly

#### Scenario: Read at EOF
- **WHEN** a task calls `ReadFileAsync` with offset equal to file size
- **THEN** 0 bytes are returned without error

#### Scenario: Store-compressed entry
- **WHEN** a task reads a file that was stored without compression (Store method)
- **THEN** the file content is correct and SHA-256 matches

### Requirement: Eviction Validation Suite
The Eviction Validation Suite (10 tasks) SHALL exercise cache eviction behavior under pressure.

#### Scenario: Cold scan forces eviction wave
- **WHEN** a task sequentially reads all files in all archives (exceeding cache capacity)
- **THEN** eviction occurs for earlier entries, and re-reading those files still returns correct data

#### Scenario: Re-read after eviction
- **WHEN** a task reads a file, waits for TTL expiry (> 1 minute), then reads again
- **THEN** the file is re-materialized and SHA-256 still matches

#### Scenario: Burst-idle cycling
- **WHEN** a task performs a burst of 50 reads, idles for 30 seconds, then bursts again
- **THEN** idle entries may be evicted during the pause; subsequent reads re-materialize correctly

#### Scenario: Interleaved hot/cold access
- **WHEN** a task alternates between 2 "hot" files (read repeatedly) and random "cold" files
- **THEN** hot files tend to stay cached (LRU promotion) while cold files are evicted

### Requirement: Path Resolution Suite
The Path Resolution Suite (8 tasks) SHALL exercise VFS path resolution and structure cache lookups.

#### Scenario: File and directory existence checks
- **WHEN** a task repeatedly calls `FileExistsAsync` and `DirectoryExistsAsync` with valid paths
- **THEN** all calls return the expected boolean values without error

#### Scenario: File metadata query
- **WHEN** a task calls `GetFileInfoAsync` for files inside archives
- **THEN** the returned `VfsFileInfo` has correct size, name, and attributes

#### Scenario: Nested directory traversal
- **WHEN** a task recursively lists directories inside an archive
- **THEN** all directories and files are enumerated without error

#### Scenario: Non-existent path handling
- **WHEN** a task queries a path that does not exist inside an archive
- **THEN** the appropriate response is returned without crashing (FileExistsAsync returns false, or exception is handled)

### Requirement: Latency Measurement Suite
The Latency Measurement Suite (5 tasks) SHALL be dedicated readers that measure and categorize read latency using `Stopwatch`.

#### Scenario: Measure cache hit latency
- **WHEN** a task reads a file that was recently read (still cached)
- **THEN** the elapsed time is recorded under the CacheHit category

#### Scenario: Measure cache miss latency
- **WHEN** a task reads a file for the first time (not cached)
- **THEN** the elapsed time is recorded under the CacheMiss category

#### Scenario: Measure linear vs random access latency
- **WHEN** a task performs sequential reads and random-offset reads
- **THEN** each is recorded under the Linear or Random category respectively

### Requirement: Maintenance tasks
Two maintenance tasks SHALL run throughout the test, performing periodic eviction and cleanup.

#### Scenario: Periodic maintenance execution
- **WHEN** the maintenance task runs
- **THEN** it calls `EvictExpired()` on both file cache and structure cache, and `ProcessPendingCleanup()` on the file cache, every 2 seconds

#### Scenario: Maintenance errors trigger fail-fast
- **WHEN** the maintenance task encounters an exception
- **THEN** fail-fast triggers with the maintenance error as the `EnduranceFailure`

### Requirement: Post-run assertions
After all tasks complete (or fail-fast drain), the test SHALL validate global invariants.

#### Scenario: Zero handle leaks
- **WHEN** the test completes
- **THEN** `FileContentCache.BorrowedEntryCount` SHALL be 0

#### Scenario: Operations were performed
- **WHEN** the test completes successfully (no fail-fast)
- **THEN** each suite SHALL have performed at least 1 operation

#### Scenario: Fail-fast error surfaced
- **WHEN** fail-fast was triggered during the run
- **THEN** the test fails with the formatted `EnduranceFailure` diagnostic output
