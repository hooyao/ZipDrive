## 1. Dependencies and Project Setup

- [x] 1.1 Add KTrie NuGet package (v3.0.1+) to `ZipDrive.Domain` project
- [x] 1.2 Add KTrie NuGet package to `ZipDrive.Infrastructure.Caching` project
- [x] 1.3 Verify solution builds with new dependency (`dotnet build ZipDrive.slnx`)

## 2. Domain Models

- [x] 2.1 Create `ArchiveDescriptor` record in `Domain/Models/` with `VirtualPath`, `PhysicalPath`, `SizeBytes`, `LastModifiedUtc`, `Name` properties
- [x] 2.2 Create `ArchiveTrieResult` readonly record struct with `Status`, `Archive`, `InternalPath`, `VirtualFolderPath` and factory methods (`VirtualRoot()`, `Folder()`, `AtArchiveRoot()`, `Inside()`, `NotFound()`)
- [x] 2.3 Create `ArchiveTrieStatus` enum (`VirtualRoot`, `VirtualFolder`, `ArchiveRoot`, `InsideArchive`, `NotFound`)
- [x] 2.4 Create `VirtualFolderEntry` readonly record struct with `Name`, `IsArchive`, `Archive` properties
- [x] 2.5 Create `VfsFileInfo` record struct with `Name`, `FullPath`, `IsDirectory`, `SizeBytes`, `CreationTime`, `LastWriteTime`, `Attributes`
- [x] 2.6 Create `VfsVolumeInfo` record struct with `VolumeLabel`, `FileSystemName`, `TotalBytes`, `FreeBytes`, `IsReadOnly`
- [x] 2.7 Create `VfsMountOptions` record with `RootPath`, `MaxDiscoveryDepth` (default 6)
- [x] 2.8 Create `CaseInsensitiveCharComparer : IEqualityComparer<char>` in `Domain/` for Windows case-insensitive trie matching

## 3. Domain Exceptions

- [x] 3.1 Create `VfsException` base class in `Domain/Exceptions/`
- [x] 3.2 Create `VfsFileNotFoundException : VfsException`
- [x] 3.3 Create `VfsDirectoryNotFoundException : VfsException`
- [x] 3.4 Create `VfsAccessDeniedException : VfsException`

## 4. Domain Interfaces

- [x] 4.1 Create `IArchiveTrie` interface in `Domain/Abstractions/` with `Resolve`, `ListFolder`, `IsVirtualFolder`, `AddArchive`, `RemoveArchive`, `Archives`, `ArchiveCount`
- [x] 4.2 Create `IArchiveDiscovery` interface with `DiscoverAsync(rootPath, maxDepth, cancellationToken)`
- [x] 4.3 Create `IVirtualFileSystem` interface with `MountAsync`, `UnmountAsync`, `IsMounted`, `GetFileInfoAsync`, `ListDirectoryAsync`, `ReadFileAsync`, `FileExistsAsync`, `DirectoryExistsAsync`, `GetVolumeInfo`, `MountStateChanged` event
- [x] 4.4 Revise `IPathResolver` interface: change `Resolve` return type from `PathResolutionResult` to `ArchiveTrieResult`

## 5. Revise ArchiveStructure to Use KTrie

- [x] 5.1 Replace `IReadOnlyDictionary<string, ZipEntryInfo> Entries` with `TrieDictionary<ZipEntryInfo> Entries` in `ArchiveStructure`
- [x] 5.2 Remove `DirectoryNode RootDirectory` property from `ArchiveStructure`
- [x] 5.3 Add `ListDirectory(string dirPath)` method to `ArchiveStructure` using `GetByPrefix` + direct-child filtering
- [x] 5.4 Update `DirectoryExists` method to handle trailing slash normalization
- [x] 5.5 Update `GetEntry` method to work with trie lookup
- [x] 5.6 Remove `GetDirectory` method (replaced by `DirectoryExists` + `ListDirectory`)
- [x] 5.7 Delete `DirectoryNode` class (no longer needed)
- [x] 5.8 Update `ArchiveStructureCache` build logic to construct `TrieDictionary<ZipEntryInfo>` instead of Dictionary + DirectoryNode
- [x] 5.9 Add parent directory synthesis in structure builder (ensure directories exist for all file paths)
- [x] 5.10 Fix existing tests that reference `DirectoryNode` or `ArchiveStructure.RootDirectory`
- [x] 5.11 Run existing cache tests to verify they still pass (`dotnet test --filter "FullyQualifiedName~Caching"`)

## 6. Archive Trie Implementation

- [x] 6.1 Create `ArchiveTrie : IArchiveTrie` in `Application/Services/` using `TrieDictionary<ArchiveDescriptor>`
- [x] 6.2 Implement `AddArchive` with trailing `/` key convention and virtual folder derivation into `HashSet<string>`
- [x] 6.3 Implement `Resolve` using `LongestPrefixMatch` and status classification (VirtualRoot, VirtualFolder, ArchiveRoot, InsideArchive, NotFound)
- [x] 6.4 Implement `ListFolder` using `EnumerateByPrefix` + direct-child filtering (archives and subfolders)
- [x] 6.5 Implement `IsVirtualFolder` via HashSet lookup
- [x] 6.6 Implement `RemoveArchive` (remove trie key; virtual folder cleanup deferred)
- [x] 6.7 Accept `IEqualityComparer<char>?` in constructor for platform-aware case sensitivity
- [x] 6.8 Write unit tests for archive registration and lookup (4 tests)
- [x] 6.9 Write unit tests for longest prefix match resolution (6 tests covering all status values)
- [x] 6.10 Write unit tests for virtual folder derivation (3 tests)
- [x] 6.11 Write unit tests for folder listing (3 tests including mixed archives and subfolders)
- [x] 6.12 Write unit tests for case-insensitive matching (2 tests)

## 7. Path Resolver Implementation

- [x] 7.1 Revise `PathResolver` in `Application/Services/` to accept `IArchiveTrie` dependency
- [x] 7.2 Implement path normalization (backslash → forward slash, trim, collapse)
- [x] 7.3 Delegate to `IArchiveTrie.Resolve` after normalization
- [x] 7.4 Update existing `PathResolver` tests to match new `ArchiveTrieResult` return type
- [x] 7.5 Write unit tests for normalization edge cases (backslashes, double slashes, trailing slashes, null/empty)

## 8. Archive Discovery Implementation

- [x] 8.1 Create `ArchiveDiscovery : IArchiveDiscovery` in `Application/Services/`
- [x] 8.2 Implement recursive directory scan with depth clamping (1-6)
- [x] 8.3 Compute relative `VirtualPath` with forward slashes from physical paths
- [x] 8.4 Populate `ArchiveDescriptor` metadata (PhysicalPath, SizeBytes, LastModifiedUtc)
- [x] 8.5 Handle inaccessible files gracefully (skip + log warning, continue scanning)
- [x] 8.6 Handle non-existent root directory (throw `DirectoryNotFoundException`)
- [x] 8.7 Support `CancellationToken` for aborting discovery
- [x] 8.8 Write unit tests for depth limiting (4 tests)
- [x] 8.9 Write integration test with real directory structure (1 multi-folder test)
- [x] 8.10 Write unit tests for path normalization (1 test for forward slashes)

## 9. ZipVirtualFileSystem Implementation

- [x] 9.1 Create `ZipVirtualFileSystem : IVirtualFileSystem` in `Application/Services/`
- [x] 9.2 Inject dependencies: `IArchiveTrie`, `IArchiveStructureCache`, `ICache<Stream>` (file content cache), `IArchiveDiscovery`, `IPathResolver`, `Func<string, IZipReader>` (reader factory)
- [x] 9.3 Implement `MountAsync`: call discovery, populate archive trie, set `IsMounted`, raise event
- [x] 9.4 Implement `UnmountAsync`: clear caches, reset state, raise event
- [x] 9.5 Implement `GetFileInfoAsync` for all path types (virtual root, virtual folder, archive root, inside archive file, inside archive directory)
- [x] 9.6 Implement `ListDirectoryAsync` for virtual root, virtual folder, archive root, and archive subdirectory
- [x] 9.7 Implement `ReadFileAsync` with cache borrow/return pattern, offset handling, and EOF handling
- [x] 9.8 Implement `FileExistsAsync` (true only for files, false for directories)
- [x] 9.9 Implement `DirectoryExistsAsync` (true for archives, virtual folders, archive directories)
- [x] 9.10 Implement `GetVolumeInfo` returning static `VfsVolumeInfo`
- [x] 9.11 Add guard checks: throw `InvalidOperationException` if not mounted
- [x] 9.12 Wrap infrastructure exceptions (`ZipException`, `IOException`) in `VfsException` subtypes
- [x] 9.13 Write unit tests for `MountAsync`/`UnmountAsync` lifecycle (4 tests)
- [x] 9.14 Write unit tests for `ListDirectoryAsync` across all path types (5 tests)
- [x] 9.15 Write unit tests for `GetFileInfoAsync` across all path types (5 tests)
- [x] 9.16 Write unit tests for `ReadFileAsync` including offset, EOF, cache hit scenarios (7 tests)
- [x] 9.17 Write unit tests for `FileExistsAsync` and `DirectoryExistsAsync` (7 tests)
- [x] 9.18 Write unit tests for exception wrapping (3 tests + 1 volume info)

## 10. Test ZIP File Generator

- [x] 10.1 Create `TestZipGenerator` utility class in a shared test helpers project (e.g., `tests/ZipDrive.TestHelpers/`)
- [x] 10.2 Define `ZipManifest` model: JSON-serializable record containing list of `ManifestEntry { FileName, UncompressedSize, CompressedSize, Crc32, Sha256, IsDirectory, CompressionMethod }`
- [x] 10.3 Implement `GenerateZipAsync(string outputPath, ZipProfile profile)` that creates a real ZIP file with embedded `__manifest__.json` at the root containing all file metadata
- [x] 10.4 Define `ZipProfile` presets for test coverage:
  - `TinyFiles`: 50 files, 1KB-10KB each (all memory tier, < 50MB cutoff)
  - `SmallFiles`: 100 files, 100KB-5MB each (memory tier)
  - `MixedFiles`: 80 small files (1KB-10MB) + 10 medium files (20-49MB) + 10 large files (50-200MB, disk tier)
  - `LargeFiles`: 20 files, 50-500MB each (all disk tier, >= 50MB cutoff)
  - `DeepNesting`: 200 files in deeply nested directories (10+ levels deep)
  - `FlatStructure`: 500 files all in root directory
  - `VideoSimulation`: 5 large sequential files (100-500MB) simulating video content
  - `EdgeCases`: Files with unicode names, empty files (0 bytes), single-byte files, exactly-50MB file (cutoff boundary)
- [x] 10.5 Implement file content generation: fill files with deterministic pseudo-random data seeded by file path (so content is reproducible and verifiable without storing expected data)
- [x] 10.6 Implement SHA-256 checksum computation for each file and embed in manifest
- [x] 10.7 Implement `GenerateTestFixtureAsync(string rootDir)` that generates 100 ZIP files across a multi-folder structure:
  - `rootDir/games/fps/` → 10 ZIPs (MixedFiles profile)
  - `rootDir/games/rpg/` → 10 ZIPs (SmallFiles profile)
  - `rootDir/games/retro/classic/` → 5 ZIPs (TinyFiles, depth=3)
  - `rootDir/docs/manuals/` → 10 ZIPs (SmallFiles profile)
  - `rootDir/docs/technical/deep/nested/` → 5 ZIPs (DeepNesting, depth=4)
  - `rootDir/media/videos/` → 10 ZIPs (VideoSimulation profile)
  - `rootDir/media/music/` → 10 ZIPs (SmallFiles profile)
  - `rootDir/archives/` → 15 ZIPs (MixedFiles profile)
  - `rootDir/backup/` → 10 ZIPs (LargeFiles profile)
  - `rootDir/` → 5 ZIPs at root level (EdgeCases profile)
  - `rootDir/edge/` → 10 ZIPs (EdgeCases profile)
- [x] 10.8 Add `VerifyFileContent(IVirtualFileSystem vfs, string archiveVirtualPath, ManifestEntry entry)` helper that reads a file via VFS and verifies SHA-256 matches manifest
- [x] 10.9 Add `LoadManifest(IVirtualFileSystem vfs, string archiveVirtualPath)` helper that reads and deserializes `__manifest__.json` from a mounted archive

## 11. Integration Tests - Correctness (Operation Integrity)

### 11.1 Mount and Discovery

- [x] 11.1.1 Test mount with 100 ZIPs across multi-folder structure: verify all archives discovered, archive trie populated correctly, virtual folders derived
- [x] 11.1.2 Test discovery depth limiting: mount with depth=1 and verify only root-level ZIPs found; mount with depth=6 and verify deeply nested ZIPs found
- [x] 11.1.3 Test mount with empty directory: verify mount succeeds, `IsMounted` is true, root listing is empty
- [x] 11.1.4 Test mount with non-existent directory: verify `DirectoryNotFoundException` thrown
- [x] 11.1.5 Test unmount clears all state: mount, access files (populate caches), unmount, verify caches cleared, operations throw `InvalidOperationException`

### 11.2 Virtual Folder Navigation

- [x] 11.2.1 Test `ListDirectoryAsync("")`: verify root contains mix of virtual folders and root-level archives
- [x] 11.2.2 Test `ListDirectoryAsync("games")`: verify subfolder listing with archives and nested subfolders
- [x] 11.2.3 Test `ListDirectoryAsync("games/fps")`: verify leaf folder listing with archives only
- [x] 11.2.4 Test `GetFileInfoAsync` for virtual root, virtual folder, archive-as-folder: verify `IsDirectory=true`, correct names
- [x] 11.2.5 Test `DirectoryExistsAsync` returns true for virtual folders, false for non-existent paths
- [x] 11.2.6 Test `FileExistsAsync` returns false for virtual folders and archives (they are directories, not files)

### 11.3 Archive Content Navigation

- [x] 11.3.1 Test `ListDirectoryAsync("games/fps/archive01.zip")`: verify root-level entries inside the ZIP match manifest
- [x] 11.3.2 Test `ListDirectoryAsync("games/fps/archive01.zip/subdir")`: verify subdirectory listing matches manifest
- [x] 11.3.3 Test deeply nested directory listing (10+ levels): `ListDirectoryAsync("docs/technical/deep/nested/archive.zip/a/b/c/d/e/f/g/h")`
- [x] 11.3.4 Test flat ZIP listing (500 files in root): verify all entries returned
- [x] 11.3.5 Test `GetFileInfoAsync` for files inside archive: verify name, size, IsDirectory=false, timestamps match manifest
- [x] 11.3.6 Test `GetFileInfoAsync` for directories inside archive: verify IsDirectory=true
- [x] 11.3.7 Test `FileExistsAsync` returns true for files, false for directories inside archive
- [x] 11.3.8 Test `DirectoryExistsAsync` returns true for directories inside archive, false for files

### 11.4 File Read Correctness (Content Verification)

- [x] 11.4.1 Test read entire small file (< 50MB, memory tier): read via VFS, compute SHA-256, verify matches manifest checksum
- [x] 11.4.2 Test read entire large file (>= 50MB, disk tier): same SHA-256 verification
- [x] 11.4.3 Test read file at cutoff boundary (exactly 50MB): verify correct tier routing and content integrity
- [x] 11.4.4 Test read with offset: read from middle of file, verify bytes match expected content at that offset
- [x] 11.4.5 Test read past EOF: offset near end of file, buffer larger than remaining, verify partial read with correct byte count
- [x] 11.4.6 Test read at EOF: offset equals file size, verify 0 bytes returned
- [x] 11.4.7 Test read beyond EOF: offset greater than file size, verify 0 bytes returned
- [x] 11.4.8 Test read empty file (0 bytes): verify 0 bytes returned
- [x] 11.4.9 Test read single-byte file: verify correct byte returned
- [x] 11.4.10 Test read with various buffer sizes (1 byte, 4KB, 64KB, 1MB, 16MB): verify content matches for each
- [x] 11.4.11 Test sequential chunked read of entire file (4KB chunks): concatenate all chunks, verify SHA-256 matches
- [x] 11.4.12 Test random-offset reads: read 50 random offsets within a file, verify each byte range matches expected content
- [x] 11.4.13 Verify all 100 archives: iterate every file in every manifest, read via VFS, verify SHA-256 (comprehensive sweep)

### 11.5 Edge Cases

- [x] 11.5.1 Test files with unicode names (Chinese, Japanese, emoji): verify discovery, listing, and read
- [x] 11.5.2 Test ZIPs where directory entries are omitted (synthesized parent dirs): verify `DirectoryExistsAsync` and `ListDirectoryAsync` work
- [x] 11.5.3 Test path resolution with backslashes: `ReadFileAsync("games\\fps\\archive01.zip\\file.txt")` normalizes correctly
- [x] 11.5.4 Test path resolution with double slashes: `ReadFileAsync("games//fps//archive01.zip//file.txt")`
- [x] 11.5.5 Test path resolution with trailing slashes: `ListDirectoryAsync("games/fps/archive01.zip/")`
- [x] 11.5.6 Test case sensitivity for archive paths (Windows: case-insensitive; match `"GAMES/FPS/ARCHIVE01.ZIP"`)
- [x] 11.5.7 Test case sensitivity for internal paths (always case-sensitive: `"README.TXT"` != `"readme.txt"`)
- [x] 11.5.8 Test read from non-existent file: verify `VfsFileNotFoundException`
- [x] 11.5.9 Test read from directory path: verify `VfsAccessDeniedException`
- [x] 11.5.10 Test list non-existent directory: verify `VfsDirectoryNotFoundException`
- [x] 11.5.11 Test operations after unmount: verify `InvalidOperationException`
- [x] 11.5.12 Test concurrent reads of same file from multiple threads (10 threads): all get correct data

### 11.6 Cache Behavior Correctness

- [x] 11.6.1 Test structure cache: first `ListDirectoryAsync` triggers build, second is cache hit (verify via hit/miss counters)
- [x] 11.6.2 Test structure cache invalidation: `Invalidate(archiveKey)`, then re-list triggers rebuild
- [x] 11.6.3 Test file content cache hit: read same file twice, second read is cache hit (verify counters)
- [x] 11.6.4 Test memory tier routing: read file < 50MB, verify stored in memory tier
- [x] 11.6.5 Test disk tier routing: read file >= 50MB, verify stored in disk tier
- [x] 11.6.6 Test cache eviction under pressure: fill cache beyond capacity, verify LRU entries evicted, recently-used entries retained
- [x] 11.6.7 Test thundering herd prevention: 20 threads request same uncached file simultaneously, verify file materialized exactly once (check miss count = 1)
- [x] 11.6.8 Test borrow/return pattern: borrow file, verify entry not evicted during active borrow, dispose handle, verify entry becomes evictable
- [x] 11.6.9 Test TTL expiration: set short TTL, access file, wait past TTL, verify entry evicted and re-read triggers cache miss
- [x] 11.6.10 Test structure cache memory estimation: build structure for 10,000-entry ZIP, verify `EstimatedSizeBytes` is within 20% of `10000 * 114`

## 12. Performance Benchmarks (BenchmarkDotNet)

- [x] 12.1 Add `BenchmarkDotNet` NuGet package to a new `tests/ZipDrive.Benchmarks/` project
- [x] 12.2 Benchmark: `ArchiveTrie.Resolve` - path resolution latency (target: < 1μs per call)
  - Params: 10, 100, 1000 registered archives
  - Paths: virtual root, virtual folder, archive root, inside archive (4 path depths)
- [x] 12.3 Benchmark: `ArchiveTrie.ListFolder` - virtual folder listing latency
  - Params: folder with 10, 50, 100 children
- [x] 12.4 Benchmark: `ArchiveStructure.ListDirectory` - trie prefix enumeration with direct-child filtering
  - Params: directory with 100, 1000, 10000 entries in ZIP
  - Measure filtering overhead vs theoretical trie prefix enumeration
- [x] 12.5 Benchmark: `ArchiveStructure.GetEntry` - trie exact lookup
  - Params: ZIP with 1000, 10000, 100000 entries
  - Paths at various depths (1, 3, 5, 10 segments)
- [x] 12.6 Benchmark: `ArchiveStructureCache.GetOrBuildAsync` - structure build time from real ZIP
  - Params: ZIP with 1000, 10000, 100000 entries
  - Target: < 100ms for 10,000 entries
- [x] 12.7 Benchmark: `ReadFileAsync` cache hit path - end-to-end read latency when file is cached
  - Params: file sizes 1KB, 100KB, 1MB, 10MB
  - Target: < 1ms overhead per read
- [x] 12.8 Benchmark: `ReadFileAsync` cache miss path - end-to-end latency including decompression and caching
  - Params: file sizes 1MB, 10MB, 50MB, 100MB (Store vs Deflate)
- [x] 12.9 Benchmark: Sequential chunked read (video playback simulation) - read 100MB file in 4KB chunks linearly
  - Measure: total throughput MB/s, per-chunk latency
- [x] 12.10 Benchmark: Memory allocation profile using `[MemoryDiagnoser]` for all key operations

## 13. Endurance Test (12-Hour Stress Test)

- [x] 13.1 Create `EnduranceTestRunner` in `tests/ZipDrive.EnduranceTests/` project
- [x] 13.2 Setup: generate test fixture (100 ZIPs, multi-folder), mount VFS
- [x] 13.3 Implement workload profiles for 20 concurrent tasks:
  - **4 tasks: Sequential Video Reader** - pick random large ZIP, read a large file linearly in 64KB chunks from offset 0 to EOF, verify SHA-256 on completion, repeat with next random file
  - **4 tasks: Random File Browser** - pick random archive, list root directory, pick random subdirectory, list it, pick random file, read entire file, verify SHA-256, repeat
  - **4 tasks: Small File Scanner** - pick random archive with TinyFiles/SmallFiles profile, read every file in the archive sequentially, verify each SHA-256 against manifest, repeat with next archive
  - **2 tasks: Rapid Path Resolution** - continuously resolve random paths (virtual root, virtual folders, archive roots, deep internal paths) at max speed, verify status is correct
  - **2 tasks: Cache Pressure Generator** - continuously read large files (>= 50MB) from different archives to trigger eviction, verify content correctness on each read
  - **2 tasks: Thundering Herd Simulator** - 2 tasks synchronize (via barrier) to request the same uncached file simultaneously every 30 seconds, verify both get correct data
  - **2 tasks: Structure Cache Exerciser** - invalidate a random archive's structure cache, immediately re-list its directory, verify contents match manifest
- [x] 13.4 Implement progress reporting: log operations/second, cache hit rate, error count, memory usage every 60 seconds
- [x] 13.5 Implement correctness accounting: count total reads, verified reads, failed reads; test fails if any verification fails
- [x] 13.6 Implement graceful shutdown on first failure: if any task encounters a data mismatch, log details (archive, file, expected SHA-256, actual SHA-256, offset) and stop all tasks
- [x] 13.7 Implement 12-hour timeout with configurable duration via environment variable `ENDURANCE_DURATION_HOURS` (default: 12, can set to 1 for CI)
- [x] 13.8 Assert at end: zero verification failures, total operations > minimum threshold (e.g., > 100,000 reads)
- [x] 13.9 Report summary: total duration, total operations, ops/sec, cache hit rate, peak memory, errors

## 14. Test Infrastructure

- [x] 14.1 Create `tests/ZipDrive.TestHelpers/` shared project for `TestZipGenerator`, `ZipManifest`, `ZipProfile`, verification helpers
- [x] 14.2 Create `tests/ZipDrive.IntegrationTests/` project for correctness integration tests (sections 11.1-11.6)
- [x] 14.3 Create `tests/ZipDrive.Benchmarks/` project with BenchmarkDotNet
- [x] 14.4 Create `tests/ZipDrive.EnduranceTests/` project for 12-hour stress test
- [x] 14.5 Add xunit `[Collection]` attributes to integration tests that share the generated test fixture (avoid regenerating 100 ZIPs per test class)
- [x] 14.6 Implement `IAsyncLifetime` fixture that generates test ZIPs once for the entire test collection and cleans up after
- [x] 14.7 Verify all existing tests still pass (`dotnet test`)
- [x] 14.8 Update `ZIP_STRUCTURE_CACHE_DESIGN.md` to reflect KTrie-based architecture
