
# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**ZipDrive** is a clean-architecture rewrite of the ZipDrive virtual file system. It mounts ZIP archives (and potentially other formats like TAR, 7Z) as accessible Windows drives using DokanNet. The project has the **core caching layer and streaming ZIP reader implemented and tested**.

**Current Status**: Core caching layer with chunked incremental extraction (149 tests), streaming ZIP reader (33 tests), file content cache with strategy-owned materialization, OpenTelemetry observability, DokanNet adapter, background cache maintenance, automatic charset detection for non-UTF8 filenames, drag-and-drop folder launch, and sibling prefetch with coalescing batch reader implemented. 347 total tests passing. 24-hour soak test validated with 100 concurrent tasks, partial-read SHA-256 verification, and latency measurement.

## Development Workflow Requirements

**IMPORTANT**: All code changes must follow this workflow:

1. **Build** - Any code change must compile successfully (`dotnet build`)
2. **Write Tests** - New code requires unit or integration tests to cover the new functionality
3. **Run Tests** - Execute tests for affected components (`dotnet test`)
4. **Pass** - All tests must pass before claiming a task is complete

```
Code Change → Build → Write Tests → Run Tests → Pass → Done
```

**A task is NOT complete until all related tests pass.**

## Technology Stack

- **Framework**: .NET 10.0 (`net10.0`)
- **Language**: C# 13/14 (implied by .NET 10)
- **SDK Version**: 10.0.100
- **Project Structure**: Clean Architecture / Onion Architecture
- **Package Management**: NuGet Central Package Management (`Directory.Packages.props` at repo root)
- **Key Libraries**: `System.IO.MemoryMappedFiles`, `System.Threading.Channels`, `System.Collections.Concurrent`, `System.Diagnostics.Metrics`, `OpenTelemetry`, `UTF.Unknown`

## Cross-Platform Considerations

ZipDrive's CLI and DokanNet adapter are **Windows-only**, but the underlying infrastructure libraries (`ZipDrive.Infrastructure.Caching`, `ZipDrive.Infrastructure.Archives.Zip`, `ZipDrive.Domain`, `ZipDrive.Application`) are designed to be **cross-platform**. Key platform-specific handling:

- **Sparse file creation** (`ChunkedDiskStorageStrategy.SetSparseAttribute`): On Windows/NTFS, must explicitly call `FSCTL_SET_SPARSE` via `DeviceIoControl` P/Invoke before `SetLength`, otherwise the OS pre-allocates the full file size. On Linux (ext4/btrfs/xfs), `SetLength` creates sparse files by default — no ioctl needed. The method uses `OperatingSystem.IsWindows()` to branch.
- **File sharing** (`FileShare.Read` / `FileShare.ReadWrite`): Used by `ChunkedFileEntry` (writer) and `ChunkedStream` (readers) for concurrent access to the backing sparse file. Works on both Windows and Linux.
- **Any new platform-specific code** should follow this pattern: guard with `OperatingSystem.IsWindows()` / `OperatingSystem.IsLinux()`, provide fallback behavior, and log at Debug level when a platform feature is unavailable.

## Prerequisites

- **Windows x64 only** - Uses DokanNet which is Windows-specific
- **Dokany v2.1.0.1000** must be installed from https://github.com/dokan-dev/dokany/releases/tag/v2.1.0.1000
- **.NET 10.0 SDK** (Note: Currently targets .NET 10 preview - may need adjustment to .NET 8 LTS for production)

## Build and Run Commands

```bash
# Build the entire solution
dotnet build ZipDrive.slnx

# Build in Release mode
dotnet build ZipDrive.slnx -c Release

# Run all tests
dotnet test

# Run tests in specific project
dotnet test tests/ZipDrive.Domain.Tests/ZipDrive.Domain.Tests.csproj

# Run specific test
dotnet test --filter "FullyQualifiedName~ThunderingHerd"

# Run CLI
dotnet run --project src/ZipDrive.Cli/ZipDrive.Cli.csproj

# Run endurance test (default: ~72 seconds for CI)
dotnet test tests/ZipDrive.EnduranceTests/ZipDrive.EnduranceTests.csproj

# Run endurance test for extended duration (e.g., 24 hours)
ENDURANCE_DURATION_HOURS=24 dotnet test tests/ZipDrive.EnduranceTests/ZipDrive.EnduranceTests.csproj
```

## Publishing a Release

Build a single-file executable (requires .NET 10 runtime on target machine):

```bash
dotnet publish src/ZipDrive.Cli/ZipDrive.Cli.csproj \
  -c Release -r win-x64 --self-contained false \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -o ./publish
```

Output: `publish/ZipDrive.exe` (~74MB) + `publish/appsettings.jsonc`.

Run with:
```bash
ZipDrive.exe --Mount:ArchiveDirectory="D:\my-zips" --Mount:MountPoint="R:\"
```

**Drag-and-drop**: You can also drag a folder onto `ZipDrive.exe` in Windows Explorer. The folder path is automatically used as `Mount:ArchiveDirectory`. The mount point defaults to `R:\` from `appsettings.jsonc`.

**Versioning**: `Directory.Build.props` sets `<Version>1.0.0-dev</Version>` as the default — **do NOT modify this file for releases**. The startup log displays the version with the `+commit-hash` metadata stripped (e.g., `ZipDrive 1.0.0-dev starting`). For release builds, the CI pipeline overrides this via `-p:Version=1.0.7` on the `dotnet publish` command line (see `.github/workflows/release.yml`), producing `ZipDrive 1.0.7 starting`.

**Release Process**: To publish a new release, push a tag matching the pattern `release-X.Y.Z` (e.g., `release-1.0.7`). This triggers the Release workflow (`.github/workflows/release.yml`) which builds, publishes, and creates a GitHub Release with the `ZipDrive-X.Y.Z.zip` asset. The tag name is parsed to extract the version number. Use `gh release list` to check the latest release before choosing a version number.

```bash
# Check latest release
gh release list --limit 1

# Tag and push to trigger release pipeline
git tag release-1.0.8
git push origin release-1.0.8
```

**Single-file note**: Serilog cannot auto-discover sink assemblies in single-file mode. The CLI explicitly passes `ConfigurationReaderOptions` with the Console sink assembly. If adding new Serilog sinks, register their assemblies in `Program.cs`.

## Architecture

ZipDrive follows **Clean Architecture** (Onion Architecture) with strict dependency rules.

### High-Level Data Flow

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                           Complete Read Flow                                 │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                             │
│  1. DokanNet: ReadFile("R:\archive.zip\folder\file.txt", offset=5000)       │
│     ↓                                                                       │
│  2. Archive Prefix Tree: Resolve path                                       │
│     ├── archiveKey = "archive.zip"                                          │
│     ├── absolutePath = "D:\archives\archive.zip"                            │
│     └── internalPath = "folder/file.txt"                                    │
│     ↓                                                                       │
│  3. Structure Cache: GetOrBuildAsync("archive.zip")                         │
│     ├── HIT: Return cached ArchiveStructure                                 │
│     └── MISS: Parse Central Directory → Build structure → Cache            │
│     ↓                                                                       │
│  4. Lookup: structure.Entries["folder/file.txt"] → ZipEntryInfo             │
│     └── { LocalHeaderOffset, CompressedSize, CompressionMethod, ... }       │
│     ↓                                                                       │
│  5. File Content Cache: GetOrAddAsync("archive.zip:folder/file.txt")        │
│     ├── HIT: Return cached random-access stream                             │
│     └── MISS: Stream extract → Materialize → Cache → Return                 │
│     ↓                                                                       │
│  6. Stream: Seek(5000), Read(4096) → Return to DokanNet                     │
│                                                                             │
└─────────────────────────────────────────────────────────────────────────────┘
```

### Two-Level Caching Architecture

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                         Archive Prefix Tree                                  │
│                        (Path → Archive Mapping)                              │
│                                                                             │
│  R:\                                                                        │
│  ├── archive1.zip\ ──→ ArchiveStructureCache                                │
│  ├── archive2.zip\ ──→ ArchiveStructureCache                                │
│  └── data.zip\     ──→ ArchiveStructureCache                                │
└─────────────────────────────────────────────────────────────────────────────┘
                              │
        ┌─────────────────────┴─────────────────────┐
        ▼                                           ▼
┌───────────────────────────────┐   ┌───────────────────────────────────────┐
│   Structure Cache             │   │   File Content Cache                   │
│   (ZIP Central Directory)     │   │   (Decompressed File Data)             │
│                               │   │                                       │
│  • ZipEntryInfo per file      │   │  • Memory Tier (< 50MB files)         │
│  • LocalHeaderOffset          │   │  • Disk Tier (≥ 50MB files)           │
│  • Compressed/Uncompressed    │   │  • Dual-tier with LRU eviction        │
│    sizes                      │   │  • TTL-based expiration               │
│  • Compression method         │   │                                       │
│  • LRU/LFU + TTL eviction     │   │                                       │
└───────────────────────────────┘   └───────────────────────────────────────┘
```

### Layer Structure

```
┌─────────────────────────────────────────────┐
│  Presentation (ZipDrive.Cli)              │
│  ↓ depends on                               │
│  Application (ZipDrive.Application)       │
│  ↓ depends on                               │
│  Domain (ZipDrive.Domain) ← Core          │
│  ↑ implemented by                           │
│  Infrastructure (ZipDrive.Infrastructure.*) │
└─────────────────────────────────────────────┘
```

### Domain Layer (`src/ZipDrive.Domain`)

**Purpose**: Contains enterprise logic, entities, and interfaces. Zero external dependencies.

**Key Abstractions**:
- `IArchivePrefixTree`: Maps virtual paths to archives (multi-archive support)
- `IArchiveStructureCache`: Caches parsed ZIP Central Directory metadata
- `IArchiveProvider`: Pluggable archive format handler (ZIP, TAR, 7Z)
- `IArchiveSession`: Manages lifecycle of an open archive
- `IArchiveRegistry`: Multi-archive management
- `IFileSystemTree`: Hierarchical structure representation
- `IPathResolver`: Path normalization and virtual-to-internal mapping

**Models**: Immutable records (`ArchiveDescriptor`, `ArchiveInfo`, `ZipEntryInfo`, `ArchiveStructure`, etc.)

### Application Layer (`src/ZipDrive.Application`)

**Purpose**: Orchestrates use cases, implements domain logic.

**Key Services**:
- `PathResolver`: Implementation of path resolution logic

### Infrastructure Layer

#### **Caching (`src/ZipDrive.Infrastructure.Caching`) - CRITICAL COMPONENT**

This is the **most important** subsystem. It solves the core problem: ZIP provides sequential-only access (compressed streams), but Windows file system requires random access at arbitrary offsets.

**Architecture**: Generic cache with pluggable storage strategies, borrow/return pattern, and dual-tier routing
- **GenericCache<T>**: Single cache implementation with reference counting and `System.Diagnostics.Metrics` instrumentation
- **FileContentCache**: Owns ZIP extraction, tier routing, and caching. Routes to memory or disk tier based on `CacheOptions.SmallFileCutoffMb` (default 50MB)
- **MemoryStorageStrategy**: `byte[]` storage for small files (< 50MB)
- **ChunkedDiskStorageStrategy**: Incremental chunk-based extraction for large files (≥ 50MB). Decompresses in configurable chunks (default 10MB) to NTFS sparse files. `MaterializeAsync` returns after the first chunk (~50ms), background task continues extracting. Replaces the former `DiskStorageStrategy`.
- **ChunkedFileEntry**: Tracks chunk state via `int[]` with `Volatile` reads/writes + `TaskCompletionSource<bool>[]` for per-chunk completion signaling. Owns the sparse backing file, background extraction task, and `CancellationTokenSource`.
- **ChunkedStream**: `Stream` subclass returned by `ChunkedDiskStorageStrategy.Retrieve()`. Maps reads to chunks, blocks on unextracted regions via `EnsureChunkReadyAsync()` safety gate. Each borrower gets an independent instance with its own `FileStream` (unbuffered via `bufferSize: 1` to prevent stale reads from sparse file regions being written by the background extractor).
- **ObjectStorageStrategy<T>**: Direct object storage for metadata caching
- **CacheTelemetry**: Static metrics (counters, histograms, observable gauges) and ActivitySource for tracing. Includes chunked extraction metrics (`cache.chunks.extracted`, `cache.chunks.waits`, `cache.chunks.wait_duration`, `cache.chunks.extraction_duration`).

**Why Custom Cache (not built-in MemoryCache)?**
- Built-in `MemoryCache` lacks pluggable eviction policies
- Unpredictable compaction behavior when capacity exceeded
- No control over which entries get evicted
- We need deterministic LRU/LFU/Size-First strategies

**Borrow/Return Pattern with Reference Counting**:
- `BorrowAsync()` returns `ICacheHandle<T>` that increments `RefCount`
- Entries with `RefCount > 0` are protected from eviction
- `Dispose()` on handle decrements `RefCount`, allowing eviction
- Prevents data corruption when reading while eviction occurs

**Five-Layer Concurrency Strategy** (prevents thundering herd + data corruption):
1. **Layer 1 (Lock-free)**: `ConcurrentDictionary.TryGetValue` for cache hits (< 100ns, zero contention)
2. **Layer 2 (Per-key)**: `Lazy<Task<T>>` prevents duplicate materialization of same file
3. **Layer 3 (Eviction)**: Global lock only when capacity exceeded (infrequent)
4. **Layer 4 (RefCount)**: Borrowed entries protected from eviction during use
5. **Layer 5 (Per-chunk)**: `TaskCompletionSource<bool>[]` signals chunk completion — readers await specific chunks with zero polling, multiple readers served concurrently from completed chunks

**Key Features**:
- TTL-based expiration (configurable via `CacheOptions.DefaultTtlMinutes`, default: 30 minutes)
- Size-based capacity limits (2GB memory + 10GB disk)
- Async cleanup (< 1ms eviction latency via mark-for-deletion)
- Pluggable `IEvictionPolicy` (Strategy pattern)
- `Clear()` and `ClearAsync()` for cleanup/shutdown
- **`CacheMaintenanceService`**: Background `IHostedService` that periodically calls `EvictExpired()` and `ProcessPendingCleanup()` at `CacheOptions.EvictionCheckIntervalSeconds` interval (default: 60s)

**Documentation**: See `src/Docs/`:
- [`CACHING_DESIGN.md`](src/Docs/CACHING_DESIGN.md) - Comprehensive design (1500+ lines)
- [`CHUNKED_EXTRACTION_DESIGN.md`](src/Docs/CHUNKED_EXTRACTION_DESIGN.md) - Incremental chunk-based extraction design
- [`CONCURRENCY_STRATEGY.md`](src/Docs/CONCURRENCY_STRATEGY.md) - Multi-layer locking details
- [`IMPLEMENTATION_CHECKLIST.md`](src/Docs/IMPLEMENTATION_CHECKLIST.md) - Implementation steps

#### **ZIP Structure Cache (`src/ZipDrive.Infrastructure.Caching`) - NEW**

The **Structure Cache** stores parsed ZIP metadata (Central Directory) to enable fast lookups and streaming extraction without re-parsing archives.

**Key Components**:
- `IArchivePrefixTree`: Maps virtual paths like `\\archive.zip\\folder\\file.txt` to archive + internal path
- `IArchiveStructureCache`: LRU/LFU cache of parsed `ArchiveStructure` per ZIP file
- `ArchiveStructure`: Contains `Dictionary<string, ZipEntryInfo>` for O(1) entry lookup
- `ZipEntryInfo`: Minimal metadata for streaming extraction (~40 bytes per entry)

**ZipEntryInfo Fields** (stored per file):
```csharp
LocalHeaderOffset   // Seek position for extraction
CompressedSize      // Bytes to read from ZIP
UncompressedSize    // Output buffer size
CompressionMethod   // 0=Store, 8=Deflate
IsDirectory         // Directory flag
LastModified        // Timestamp
```

**Streaming Extraction Flow**:
```
1. Seek to LocalHeaderOffset
2. Stream read Local Header (30 bytes + variable fields)
3. Continue stream reading CompressedSize bytes
4. Decompress (Store/Deflate) → Output stream
```
Single seek, linear read. Local Header overhead is ~100 bytes (negligible).

**Eviction Strategy**:
- Hybrid LRU/LFU + TTL-based expiration
- Any file access extends TTL for the entire archive structure
- Cache unit = per-archive (not per-file)

**Memory Estimate**: ~114 bytes per ZIP entry (including file name strings)
- 10,000-file ZIP ≈ 1.1 MB structure cache

**Documentation**: [`ZIP_STRUCTURE_CACHE_DESIGN.md`](src/Docs/ZIP_STRUCTURE_CACHE_DESIGN.md)

#### **Archives (`src/ZipDrive.Infrastructure.Archives.Zip`) - STREAMING ZIP READER**

The streaming ZIP reader provides memory-efficient parsing of ZIP archives using `IAsyncEnumerable`.

**Key Components**:
- `IZipReader`: Low-level ZIP reader interface
- `ZipReader`: Full implementation with streaming Central Directory enumeration
- `SubStream`: Bounded read-only stream wrapper for compressed data regions

**ZIP Format Structures** (`Formats/`):
- `ZipConstants`: Signatures, header sizes, compression methods
- `ZipEocd`: End of Central Directory record (supports ZIP64)
- `ZipCentralDirectoryEntry`: Central Directory file header
- `ZipLocalHeader`: Local file header

**Key Features**:
- **Streaming CD parsing**: `IAsyncEnumerable<ZipCentralDirectoryEntry>` yields entries one-by-one
- **ZIP64 support**: Handles large files (>4GB) and large archives (>65535 entries)
- **Compression**: Store (0) and Deflate (8) methods supported
- **Memory efficient**: ~114 bytes per entry (struct + filename + dictionary overhead)
- **Single-seek extraction**: Read Local Header + compressed data in one linear read
- **Automatic charset detection**: `FilenameEncodingDetector` uses UtfUnknown (Mozilla's Universal Charset Detector) with a three-tier chain: statistical detection → system OEM code page → configurable fallback. Supports per-archive detection (fast path) and per-entry detection (fallback for mixed-encoding archives)

**Filename Encoding**:
- `ZipCentralDirectoryEntry` stores raw `FileNameBytes` (not decoded strings). Filenames are decoded on demand via `DecodeFileName(Encoding?)`.
- `IFilenameEncodingDetector` interface with `DetectArchiveEncoding()` (per-archive, returns `Encoding?`) and `ResolveEntryEncoding()` (per-entry, always returns non-null `Encoding`).
- `ArchiveStructureCache.BuildStructureAsync` partitions entries by UTF-8 flag: UTF-8 entries insert immediately, non-UTF8 entries are buffered for batch detection then decoded once.
- Configuration via `MountSettings.FallbackEncoding` and `MountSettings.EncodingConfidenceThreshold`.

**Extraction Flow**:
```
1. IZipReader.ReadEocdAsync() → ZipEocd (locate Central Directory)
2. IZipReader.StreamCentralDirectoryAsync(eocd) → yields ZipCentralDirectoryEntry one-by-one
3. Build ArchiveStructure with Dictionary<string, ZipEntryInfo> + DirectoryNode tree
4. IZipReader.OpenEntryStreamAsync(entryInfo) → Stream (decompressed)
```

**Exception Hierarchy** (`Domain/Exceptions/`):
- `ZipException` → `CorruptZipException` → `InvalidSignatureException`, `EocdNotFoundException`, `TruncatedArchiveException`
- `UnsupportedCompressionException`, `EncryptedEntryException`

**Documentation**: [`STREAMING_ZIP_READER_DESIGN.md`](src/Docs/STREAMING_ZIP_READER_DESIGN.md)

#### **FileSystem (`src/ZipDrive.Infrastructure.FileSystem`)**

DokanNet integration for Windows file system mounting.

**Key Components**:
- `DokanFileSystemAdapter`: Implements `IDokanOperations2`, translates Dokan calls to `IVirtualFileSystem`
- `DokanHostedService`: `IHostedService` that manages mount/unmount lifecycle
- `DokanTelemetry`: Static `Meter("ZipDrive.Dokan")` with read latency histogram
- `ShellMetadataFilter`: Zero-allocation static helper that identifies Windows shell metadata paths (`desktop.ini`, `thumbs.db`, `$RECYCLE.BIN`, etc.) using `ReadOnlySpan<char>` matching
- `MountSettings` (in `Domain.Configuration`): Configuration POCO with all mount options including `ShortCircuitShellMetadata`, `FallbackEncoding`, and `EncodingConfidenceThreshold`

**ReadFile Buffer Pooling**: `DokanFileSystemAdapter.ReadFile()` uses `ArrayPool<byte>.Shared.Rent()` to avoid per-read `byte[]` allocations. The rented array may be larger than the Dokan native buffer, so `bytesRead` is capped to `buffer.Span.Length` and only valid bytes are copied via `AsSpan(0, bytesRead).CopyTo(buffer.Span)`. The array is returned in a `finally` block. **Do NOT return the rented array via `buffer.ReturnArray(rentedArray, copyBack: true)`** — when `copyBack` is `true` the API copies the entire rented array back to the native buffer (no byte-count parameter), which leaks stale `ArrayPool` data beyond `bytesRead`.

**Shell Metadata Short-Circuit**: Windows Explorer probes every folder for metadata files like `desktop.ini`, `thumbs.db`, and `autorun.inf`. Without filtering, these probes trigger unnecessary ZIP Central Directory parsing. The `ShellMetadataFilter` intercepts these in `CreateFile` before any string allocation occurs, returning `FileNotFound` immediately. Controlled via `Mount:ShortCircuitShellMetadata` in `appsettings.jsonc`.

**Unmanaged Memory (~394MB)**: Profiling shows ~394MB unmanaged memory at runtime. This is almost entirely **Dokany driver infrastructure** (Dokany kernel driver and user-mode library `dokan2.dll` communication buffers), not ZipDrive code. The managed heap is typically ~1-2MB. This is the normal baseline cost of a FUSE-like file system on Windows and is outside our control.

**Debug Logging**: All Dokan file system operations log at `Debug` level with the command name and file path, enabling detailed diagnostics when the Serilog minimum level is lowered.

### Presentation Layer (`src/ZipDrive.Cli`)

Command-line interface entry point with OpenTelemetry SDK wiring.

**Key Responsibilities**:
- DI registration for all services (including `FileContentCache`)
- OpenTelemetry SDK configuration (opt-in; OTLP export to Aspire Dashboard when endpoint configured)
- Serilog structured logging
- Configuration binding (`Mount`, `Cache`, `OpenTelemetry` sections)
- Drag-and-drop arg rewriting (`ArgPreprocessor`)

**Drag-and-Drop Support**: When a folder is dragged onto `ZipDrive.exe`, Windows passes the path as a bare positional arg (`args[0]`). `ArgPreprocessor.RewriteBareArgs()` detects this (first arg not starting with `--`) and prepends `--Mount:ArchiveDirectory=<path>` to the args array. Prepending ensures an explicit `--Mount:ArchiveDirectory` later in the args wins (last-wins semantics). The host builder uses `UseContentRoot(AppContext.BaseDirectory)` so config files are found relative to the exe, not the dragged folder's location. Command-line args are re-added at the end of `ConfigureAppConfiguration` to override `appsettings.jsonc` defaults.

**Validation UX**: `DokanHostedService` validates `ArchiveDirectory` is a non-empty, existing directory. On validation failure, the error is printed to stderr and `Console.ReadKey()` keeps the auto-created console window open so drag-and-drop users can read the message.

## Key Design Patterns

| Pattern | Location | Purpose |
|---------|----------|---------|
| **Clean Architecture** | Solution structure | Dependency inversion, testability |
| **Pluggable Strategy** | `IStorageStrategy<T>`, `IEvictionPolicy` | Storage and eviction extensibility |
| **Strategy-Owned Materialization** | `IStorageStrategy.MaterializeAsync()` | Strategy calls factory, consumes stream, disposes resources — eliminates intermediate buffering |
| **Borrow/Return (RAII)** | `GenericCache<T>`, `ICacheHandle<T>` | Reference counting, eviction protection |
| **Lazy Materialization** | `Lazy<Task<T>>` | Thundering herd prevention |
| **Chunked Extraction** | `ChunkedDiskStorageStrategy`, `ChunkedFileEntry`, `ChunkedStream` | Incremental decompression with per-chunk TCS signaling |
| **Async Cleanup** | `ChunkedDiskStorageStrategy` | Non-blocking eviction |
| **Dual-Tier Routing** | `FileContentCache` | Size-based memory/disk routing |
| **Static Telemetry** | `CacheTelemetry`, `ZipTelemetry`, `DokanTelemetry` | Zero-DI metrics/tracing |
| **Object Pooling** | Archive sessions (future) | Reuse expensive resources |
| **Bytes-First Decoding** | `ZipCentralDirectoryEntry.FileNameBytes` | Defer string decode until encoding is known |
| **Arg Rewriting** | `ArgPreprocessor` | Translate bare positional args to named config keys for drag-and-drop |

## Critical Concurrency Rules

When working with the caching layer:

1. **Never use global locks for cache hits** - Layer 1 must be lock-free
2. **Use per-key locks for materialization** - `Lazy<Task<T>>` with `ExecutionAndPublication` mode
3. **Different keys must not block each other** - Parallel materialization is critical
4. **Eviction must not block reads** - Use separate eviction lock
5. **Always use TimeProvider for TTL** - Enables deterministic testing with `FakeTimeProvider`
6. **Borrow/Return pattern is mandatory** - Always dispose `ICacheHandle<T>` to allow eviction
7. **Entries with RefCount > 0 are protected** - Never evict borrowed entries
8. **ChunkedStream readers must use unbuffered FileStream** - `bufferSize: 1` prevents stale reads from sparse file regions written by the background extractor (the internal FileStream buffer can cache zeros from unwritten regions before the chunk is extracted)

## Testing Strategy

### Unit Tests
- **Target**: 80%+ code coverage
- **Focus**: Cache behavior, eviction policies, path resolution, tree building
- **Tools**: XUnit, FluentAssertions
- **Mocking**: Use `FakeTimeProvider` for TTL tests

### Integration Tests
- **Scenarios**: Sequential-to-random conversion, capacity enforcement, concurrent access
- **Test Data**: Real ZIP files with various sizes and structures

### Concurrency Tests
- **Thundering Herd**: 20 concurrent reads of same uncached file (should materialize once)
- **Parallel Materialization**: 20 concurrent reads of different files should not block each other
- **Eviction Under Load**: Capacity exceeded with concurrent requests

### Endurance Tests (`tests/ZipDrive.EnduranceTests`)
- **Duration**: Configurable via `ENDURANCE_DURATION_HOURS` env var (default: 0.02 = ~72s for CI)
- **Concurrency**: 100 tasks across 7 virtual suites + 2 maintenance tasks
- **Suites**: NormalReadSuite (25), PartialReadSuite (20), ConcurrencyStressSuite (20), EdgeCaseSuite (10), EvictionValidationSuite (10), PathResolutionSuite (8), LatencyMeasurementSuite (5)
- **Cache config**: Tight limits (1MB memory, 10MB disk, 1MB cutoff, 1min TTL, 2s maintenance interval) to force constant eviction
- **Verification**: Full-file SHA-256 on every read + partial-read SHA-256 at 5-8 strategic offsets per file (start, chunk boundary cross, mid, random, near-end, tail) against embedded `__manifest__.json`
- **Fail-fast**: First error cancels all 100 tasks immediately with rich diagnostics (suite, task, file, operation, cache state, stack trace)
- **Latency reporting**: p50/p95/p99/max per category (CacheHit, CacheMiss, Linear, Random, PartialRead) with reservoir sampling (100K max per category)
- **Duration-aware fixtures**: CI (<1h) uses ~50MB fixture; manual (>=1h) generates ~700MB fixture with `EnduranceFull` profile
- **Post-run assertions**: Zero errors, zero handle leaks (`BorrowedEntryCount == 0`), all suites performed operations, maintenance ran
- **Validated**: 24-hour soak test passed with zero errors and zero data corruption

## Configuration Schema

### MountSettings (`appsettings.jsonc` → "Mount" section)

```json
{
  "Mount": {
    "MountPoint": "R:\\",
    "ArchiveDirectory": "",
    "MaxDiscoveryDepth": 6,
    "ShortCircuitShellMetadata": true,
    "FallbackEncoding": "utf-8",
    "EncodingConfidenceThreshold": 0.5
  }
}
```

`MountSettings` is a pure DTO in `Domain.Configuration` (no framework dependencies). The `FallbackEncoding` accepts any .NET encoding name (e.g., `shift_jis`, `gb2312`, `euc-kr`). The `EncodingConfidenceThreshold` controls how confident the charset detector must be before accepting a result (0.0-1.0).

### CacheOptions (`appsettings.jsonc` → "Cache" section)

```json
{
  "Cache": {
    "MemoryCacheSizeMb": 2048,              // Memory tier capacity (2GB)
    "DiskCacheSizeMb": 10240,               // Disk tier capacity (10GB)
    "SmallFileCutoffMb": 50,                // Routing threshold (< cutoff → memory, >= cutoff → disk)
    "ChunkSizeMb": 10,                      // Chunk size for incremental disk-tier extraction
    "TempDirectory": null,                  // null = system temp dir
    "DefaultTtlMinutes": 30,               // Entry expiration (used by ZipVirtualFileSystem + ArchiveStructureCache)
    "EvictionCheckIntervalSeconds": 60,     // CacheMaintenanceService sweep interval
    "PrefetchEnabled": true,               // Master on/off switch for sibling prefetch
    "PrefetchOnRead": true,                // Trigger prefetch on cold file reads
    "PrefetchOnListDirectory": true,       // Trigger prefetch on directory listings
    "PrefetchFileSizeThresholdMb": 10,     // Max file size for prefetch candidates (larger files skip)
    "PrefetchMaxFiles": 20,                // Max siblings per prefetch span
    "PrefetchMaxDirectoryFiles": 300,      // Candidate cap before nearest-offset trim
    "PrefetchFillRatioThreshold": 0.80     // Min density (wanted bytes / span bytes) to accept a span
  }
}
```

All options are wired and active. `CacheOptions` exposes computed properties `DefaultTtl`, `EvictionCheckInterval`, `MemoryCacheSizeBytes`, `DiskCacheSizeBytes`, `SmallFileCutoffBytes`, `ChunkSizeBytes`, and `PrefetchFileSizeThresholdBytes` (via `PrefetchOptions`).

**Tuning Guidelines**:
- Low memory systems: Reduce `MemoryCacheSizeMb`, lower `SmallFileCutoffMb`
- High memory systems: Increase both memory capacity and cutoff
- Fast SSD: Aggressive disk caching with larger `DiskCacheSizeMb`

### OpenTelemetry (`appsettings.jsonc` → "OpenTelemetry" section)

OpenTelemetry is **opt-in**. When `Endpoint` is empty or absent, no OTel SDK is registered (zero overhead). Set the endpoint to enable metrics and tracing export.

```json
{
  "OpenTelemetry": {
    "Endpoint": "http://localhost:18889",
    "MetricExportIntervalSeconds": 15
  }
}
```

`MetricExportIntervalSeconds` controls how often metrics are exported to the OTLP collector (default: 15s). Lower values give smoother dashboard charts; higher values reduce export overhead. If the value is absent or non-positive (≤ 0), it defaults to 15.

Or via CLI: `--OpenTelemetry:Endpoint=http://localhost:18889 --OpenTelemetry:MetricExportIntervalSeconds=30`

**Local Visualization**: Run Aspire Dashboard (`docker run -p 18888:18888 -p 18889:18889 mcr.microsoft.com/dotnet/aspire-dashboard`) and open `http://localhost:18888` for traces, metrics, and logs.

## Common Development Tasks

### Implementing a New Eviction Policy

1. Create class implementing `IEvictionPolicy`
2. Implement `SelectVictims()` method with your algorithm (LRU, LFU, Size-First, etc.)
3. Register in DI container
4. Add unit tests verifying eviction order

### Adding a New NuGet Package

This solution uses **Central Package Management**. All package versions are defined in `Directory.Packages.props` at the repo root.

1. Add `<PackageVersion Include="PackageName" Version="x.y.z" />` to `Directory.Packages.props`
2. Add `<PackageReference Include="PackageName" />` (no `Version` attribute) to the consuming `.csproj` file(s)

**Never** add a `Version` attribute to `<PackageReference>` in `.csproj` files — all versions are centrally managed.

### Adding Support for New Archive Format (e.g., TAR)

1. Create new project: `ZipDrive.Infrastructure.Archives.Tar`
2. Implement `IArchiveProvider` with `CanOpen()` and `OpenAsync()`
3. Implement `IArchiveSession` for TAR-specific operations
4. Register provider in DI container
5. Add integration tests with sample TAR files

### Debugging Cache Behavior

1. Start Aspire Dashboard for live metrics/traces: `docker run -p 18888:18888 -p 18889:18889 mcr.microsoft.com/dotnet/aspire-dashboard`
2. Check cache hit/miss metrics in dashboard (`cache.hits`, `cache.misses` counters by tier)
3. Monitor materialization duration histogram (`cache.materialization.duration`) by size bucket
4. Review eviction events in logs (now at Information level with `{Tier}` and `{Reason}` tags)
5. Use `dotnet-counters monitor --counters ZipDrive.Caching` for quick CLI metrics
6. Use deterministic tests with `FakeTimeProvider` to reproduce timing issues

### PR Merge Flow

When creating and merging a PR, follow this complete flow:

1. **Create branch and commit**: `git checkout -b feat/<name>`, stage changes, commit
2. **Push and create PR**: `git push -u origin feat/<name>`, then `gh pr create --title "..." --body "..."`
3. **Wait for CI**: Poll with `gh run list --branch feat/<name>` until the CI workflow succeeds
4. **Wait for Copilot review**: Poll `gh api repos/{owner}/{repo}/pulls/{pr}/comments` until comments appear (~2-3 minutes)
5. **Fix review comments**: Make code changes, commit and push to the PR branch
6. **Reply to each comment**: `gh api repos/{owner}/{repo}/pulls/{pr}/comments/{id}/replies -f body="Fixed in {commit}. {description}"`
7. **Resolve conversations**: Query thread IDs via GraphQL, then resolve each:
   ```bash
   # Get thread IDs
   gh api graphql -f query='{ repository(owner: "...", name: "...") { pullRequest(number: N) { reviewThreads(first: 20) { nodes { id isResolved } } } } }'
   # Resolve a thread
   gh api graphql -f query='mutation { resolveReviewThread(input: {threadId: "THREAD_ID"}) { thread { isResolved } } }'
   ```
8. **Merge**: `gh pr merge {number} --squash --delete-branch --admin`
9. **Update local main**: `git checkout main && git pull --ff-only origin main`

## Important Architectural Decisions

### Why NOT use built-in MemoryCache?
- No pluggable eviction policies (only priority-based)
- Unpredictable compaction behavior
- Can't control which entries get evicted
- Need unified `IEvictionPolicy` interface for both memory and disk tiers

### Why Lazy<Task<T>> for thundering herd?
- Built-in exactly-once execution guarantee
- `LazyThreadSafetyMode.ExecutionAndPublication` ensures thread-safety
- All waiting threads share the same materialization task
- Clean exception handling (failed materialization doesn't cache error)
- Simpler than per-key semaphores (avoids deadlock risks)

### Why async cleanup?
- Deleting large temp files blocks the caller (high latency)
- Mark-for-deletion phase takes < 1ms (instant)
- Background task processes deletions asynchronously
- Acceptable tradeoff: temporary over-capacity for low eviction latency

### Why .NET 10 target?
⚠️ **Note**: `global.json` currently specifies .NET 10.0.100 (preview). For production deployment, consider targeting .NET 8 LTS instead.

## Implementation Roadmap

Refer to [`IMPLEMENTATION_CHECKLIST.md`](src/Docs/IMPLEMENTATION_CHECKLIST.md) for granular steps.

### Caching Layer

| Phase | Status | Description |
|-------|--------|-------------|
| Phase 1 (Interfaces) | ✅ Complete | `ICache<T>`, `ICacheHandle<T>`, `IStorageStrategy<T>`, `IEvictionPolicy` |
| Phase 2 (GenericCache) | ✅ Complete | Borrow/return pattern, reference counting, four-layer concurrency |
| Phase 3 (Storage) | ✅ Complete | `MemoryStorageStrategy`, `ChunkedDiskStorageStrategy` (replaces DiskStorageStrategy), `ObjectStorageStrategy<T>` |
| Phase 4 (Eviction) | ✅ Complete | `LruEvictionPolicy` |
| Phase 5 (Tests) | ✅ Complete | 42 integration tests passing |
| Phase 6 (Coordinator) | ✅ Complete | `FileContentCache` with strategy-owned materialization and size-hint routing (6 tests) |
| Phase 7 (Observability) | ✅ Complete | OpenTelemetry metrics, tracing, Aspire Dashboard export |

### Streaming ZIP Reader

| Phase | Status | Description |
|-------|--------|-------------|
| Phase 1 (Structures) | ✅ Complete | `ZipEocd`, `ZipCentralDirectoryEntry`, `ZipLocalHeader`, `ZipConstants` |
| Phase 2 (Models) | ✅ Complete | `ZipEntryInfo`, `ArchiveStructure`, `DirectoryNode` |
| Phase 3 (Exceptions) | ✅ Complete | `ZipException` hierarchy |
| Phase 4 (IZipReader) | ✅ Complete | Streaming CD enumeration, file extraction |
| Phase 5 (Cache) | ✅ Complete | `ArchiveStructureCache` integration |
| Phase 6 (Tests) | ✅ Complete | 15 unit tests passing |

### Remaining Work

| Component | Status | Description |
|-----------|--------|-------------|
| ZipArchiveProvider | ⏳ Pending | `IArchiveProvider` implementation |
| DokanNet Adapter | ✅ Complete | `DokanFileSystemAdapter` + `DokanHostedService` |
| CLI | ✅ Complete | OTel wiring, FileContentCache DI, config binding, single-file publish |
| Multi-archive | ✅ Complete | `ArchiveTrie` + `ArchiveDiscovery` |
| Observability | ✅ Complete | OpenTelemetry metrics/tracing, Aspire Dashboard |
| Dual-tier Cache | ✅ Complete | `FileContentCache` with strategy-owned materialization and size-hint routing |
| Cache Maintenance | ✅ Complete | `CacheMaintenanceService` background eviction + cleanup |
| Chunked Extraction | ✅ Complete | `ChunkedDiskStorageStrategy` with incremental 10MB chunks, per-chunk TCS signaling, 66 new tests |
| Endurance Testing | ✅ Complete | 24-hour soak test with 100 concurrent tasks, full + partial SHA-256 verification, fail-fast diagnostics, latency reporting |
| Charset Detection | ✅ Complete | Automatic encoding detection for non-UTF8 ZIP filenames (Shift-JIS, GBK, EUC-KR, etc.) |
| Drag-and-Drop Launch | ✅ Complete | `ArgPreprocessor` rewrites bare args, `DokanHostedService` validates directory + press-any-key UX |
| Sibling Prefetch | ✅ Complete | `SpanSelector` + coalescing batch reader; fire-and-forget on cold reads and directory listings, per-directory in-flight guard, fill-ratio span selection, 15 new tests |

## Known Limitations / Future Work

- [x] Mount/Unmount implementation (DokanNet integration) - **Implemented**
- [x] CLI argument parsing and hosted service - **Implemented**
- [x] Dual-tier coordinator (automatic memory/disk routing) - **Implemented**
- [x] OpenTelemetry observability (metrics, tracing, Aspire Dashboard) - **Implemented**
- [ ] TAR/7Z format providers (extensibility is designed in)
- [ ] Health checks endpoints
- [ ] Password-protected ZIP support (ZipCrypto, AES)
- [ ] Write support (currently read-only)
- [x] ZIP64 support (files > 4GB, archives > 65535 entries) - **Implemented**
- [ ] LZMA compression support
- [ ] Direct-read for Store-compressed entries (bypass extraction for uncompressed files in ZIP)

## Code Style Conventions

- **Nullable Reference Types**: Enabled throughout (`<Nullable>enable</Nullable>`)
- **Async/Await**: Use `async`/`await` for all I/O operations (no synchronous file access)
- **Immutability**: Prefer `record` types for models in Domain layer
- **Dependency Injection**: All services registered via DI, avoid `new` for services
- **Logging**: Use `ILogger<T>` with structured logging (include context properties)
- **Cancellation**: Always respect `CancellationToken` in async methods

## Related Documentation

### OpenSpec Specifications
- **File Content Cache Spec**: [`openspec/specs/file-content-cache/spec.md`](openspec/specs/file-content-cache/spec.md) - Formal requirements and scenarios
- **Telemetry Spec**: [`openspec/specs/telemetry/spec.md`](openspec/specs/telemetry/spec.md) - Metrics, tracing, and observability requirements
- **Dual-Tier Cache Spec**: [`openspec/specs/dual-tier-cache-coordinator/spec.md`](openspec/specs/dual-tier-cache-coordinator/spec.md) - Size-based routing requirements
- **CLI Application Spec**: [`openspec/specs/cli-application/spec.md`](openspec/specs/cli-application/spec.md) - Host, OTel, and DI requirements
- **Endurance Testing Spec**: [`openspec/specs/endurance-testing/spec.md`](openspec/specs/endurance-testing/spec.md) - Suite architecture, fail-fast, partial checksums, latency measurement
- **Drag-and-Drop Launch Spec**: [`openspec/specs/drag-drop-launch/spec.md`](openspec/specs/drag-drop-launch/spec.md) - Bare arg rewriting for drag-and-drop

### Design Documents
- **File Content Caching**: [`src/Docs/CACHING_DESIGN.md`](src/Docs/CACHING_DESIGN.md)
- **ZIP Structure Cache**: [`src/Docs/ZIP_STRUCTURE_CACHE_DESIGN.md`](src/Docs/ZIP_STRUCTURE_CACHE_DESIGN.md)
- **Streaming ZIP Reader**: [`src/Docs/STREAMING_ZIP_READER_DESIGN.md`](src/Docs/STREAMING_ZIP_READER_DESIGN.md)
- **Concurrency Details**: [`src/Docs/CONCURRENCY_STRATEGY.md`](src/Docs/CONCURRENCY_STRATEGY.md)
- **Implementation Checklist**: [`src/Docs/IMPLEMENTATION_CHECKLIST.md`](src/Docs/IMPLEMENTATION_CHECKLIST.md)

## Performance Targets

- **Mount latency**: 10GB ZIP in < 5 seconds (lazy tree build)
- **Structure cache build**: < 100ms for 10,000-entry ZIP
- **Path resolution**: < 1ms (prefix tree + dictionary lookup)
- **Cache hit**: < 1ms overhead
- **Cache miss (small)**: < 100ms for 10MB file decompression + caching
- **Cache miss (large, first byte)**: ~50ms for first 10MB chunk (chunked extraction); full file decompresses in background
- **Eviction**: < 1ms (mark phase only, cleanup async)
- **Concurrent reads**: Support 100+ simultaneous file reads
- **Directory listing**: 1000 entries in < 100ms

## Success Criteria

ZipDrive is considered complete when:
- [x] Caching layer fully implemented with 80%+ test coverage (149 tests including chunked extraction)
- [x] ZIP reader implemented and tested (33 tests, including encoding detection)
- [x] DokanNet adapter functional (mount/unmount works)
- [x] CLI accepts arguments and mounts drives
- [x] Dual-tier cache coordinator with strategy-owned materialization (6 tests)
- [x] OpenTelemetry observability (metrics, tracing, Aspire Dashboard)
- [x] Background cache maintenance with configurable interval
- [ ] All performance targets met
- [x] No memory leaks (validated with 24-hour soak test, 100 concurrent tasks, zero handle leaks)
- [x] Comprehensive documentation written
- [x] Single-file release build (`dotnet publish` with `PublishSingleFile`)
