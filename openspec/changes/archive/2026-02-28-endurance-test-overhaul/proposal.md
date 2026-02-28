## Why

The current endurance test (26 concurrent tasks, ~50MB fixture, no partial-read verification, no latency measurement) is insufficient for validating ZipDrive under sustained production-like load. A 24-hour soak test with 100 concurrent tasks, SHA-256 verification on both full and partial reads, latency reporting, and comprehensive edge case coverage is needed to build confidence before release.

## What Changes

- **Replace `EnduranceTest.cs`** with a suite-based architecture: 7 virtual test suites running 100 concurrent tasks against a shared `ZipVirtualFileSystem` instance
- **Extend `TestZipGenerator`** to produce a ~700MB fixture (under 1GB) with small files (< 1MB, memory tier) and large files (>= 1MB, disk tier), with Store and Deflate compression
- **Extend manifest format** with `partialSamples` — 5-8 pre-computed SHA-256 checksums at strategic offsets per file (start, chunk boundary, mid, random, near-end, tail)
- **Add fail-fast mechanism** — first error cancels all 100 tasks immediately with rich diagnostic output (suite, task, file, operation, cache state, stack trace)
- **Add latency measurement** — report-only p50/p95/p99/max for cache hit, cache miss, linear, and random access categories
- **Tighten cache limits** — `MemoryCacheSizeMb=1`, `DiskCacheSizeMb=10`, `SmallFileCutoffMb=1` to force constant eviction
- **Scale to 100 concurrent tasks** across suites: normal reads (25), partial reads (20), concurrency stress (20), edge cases (10), eviction validation (10), path resolution (8), latency measurement (5), maintenance (2)
- **Duration-aware fixture**: CI runs (< 1h) use existing ~50MB fixture; manual runs (`ENDURANCE_DURATION_HOURS >= 1`) generate full ~700MB fixture

## Capabilities

### New Capabilities
- `endurance-testing`: Comprehensive endurance test framework with suite-based architecture, fail-fast error handling, partial-read checksum verification, latency measurement/reporting, and 100-task concurrent workload

### Modified Capabilities
- `file-content-cache`: No requirement changes — only exercised more aggressively by the endurance test

## Impact

- **Tests**: `tests/ZipDrive.EnduranceTests/EnduranceTest.cs` — full rewrite
- **Test Helpers**: `tests/ZipDrive.TestHelpers/TestZipGenerator.cs` — extend fixture profiles and manifest format
- **Test Helpers**: `tests/ZipDrive.TestHelpers/ZipManifest.cs` — add `PartialSample` model and `PartialSamples` list
- **No production code changes** — this is purely a test infrastructure enhancement
- **CI**: Default 72s run remains unchanged; 24h run is manual via `ENDURANCE_DURATION_HOURS=24`
