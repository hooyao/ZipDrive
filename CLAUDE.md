
# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**ZipDrive** is a clean-architecture rewrite of the ZipDrive virtual file system. It mounts ZIP archives (and potentially other formats like TAR, 7Z) as accessible Windows drives using DokanNet. The project has the **core caching layer and streaming ZIP reader implemented and tested**.

**Current Status**: Core caching layer (66 tests), streaming ZIP reader (15 tests), dual-tier cache coordinator, OpenTelemetry observability, DokanNet adapter, and background cache maintenance implemented. 196 total tests passing. 8-hour soak test validated.

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
- **Key Libraries**: `System.IO.MemoryMappedFiles`, `System.Threading.Channels`, `System.Collections.Concurrent`, `System.Diagnostics.Metrics`, `OpenTelemetry`

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

# Run endurance test for extended duration (e.g., 8 hours)
ENDURANCE_DURATION_HOURS=8 dotnet test tests/ZipDrive.EnduranceTests/ZipDrive.EnduranceTests.csproj
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

Output: `publish/ZipDrive.exe` (~74MB) + `publish/appsettings.json`.

Run with:
```bash
ZipDrive.exe --Mount:ArchiveDirectory="D:\my-zips" --Mount:MountPoint="R:\"
```

**Versioning**: `Directory.Build.props` sets `<Version>1.0.0-dev</Version>` as the default. The startup log displays the version with the `+commit-hash` metadata stripped (e.g., `ZipDrive 1.0.0-dev starting`). For release builds, the CI pipeline overrides this via `-p:Version=1.0.0` on the `dotnet publish` command line (see `.github/workflows/release.yml`), producing `ZipDrive 1.0.0 starting`.

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
- **DualTierFileCache**: Routes to memory or disk tier based on `CacheOptions.SmallFileCutoffMb` (default 50MB)
- **MemoryStorageStrategy**: `byte[]` storage for small files (< 50MB)
- **DiskStorageStrategy**: `MemoryMappedFile` backed by temp files for large files (≥ 50MB)
- **ObjectStorageStrategy<T>**: Direct object storage for metadata caching
- **CacheTelemetry**: Static metrics (counters, histograms, observable gauges) and ActivitySource for tracing

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

**Four-Layer Concurrency Strategy** (prevents thundering herd + data corruption):
1. **Layer 1 (Lock-free)**: `ConcurrentDictionary.TryGetValue` for cache hits (< 100ns, zero contention)
2. **Layer 2 (Per-key)**: `Lazy<Task<T>>` prevents duplicate materialization of same file
3. **Layer 3 (Eviction)**: Global lock only when capacity exceeded (infrequent)
4. **Layer 4 (RefCount)**: Borrowed entries protected from eviction during use

**Key Features**:
- TTL-based expiration (configurable via `CacheOptions.DefaultTtlMinutes`, default: 30 minutes)
- Size-based capacity limits (2GB memory + 10GB disk)
- Async cleanup (< 1ms eviction latency via mark-for-deletion)
- Pluggable `IEvictionPolicy` (Strategy pattern)
- `Clear()` and `ClearAsync()` for cleanup/shutdown
- **`CacheMaintenanceService`**: Background `IHostedService` that periodically calls `EvictExpired()` and `ProcessPendingCleanup()` at `CacheOptions.EvictionCheckIntervalSeconds` interval (default: 60s)

**Documentation**: See `src/Docs/`:
- [`CACHING_DESIGN.md`](src/Docs/CACHING_DESIGN.md) - Comprehensive design (1500+ lines)
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
- `MountOptions`: Configuration POCO with `ShortCircuitShellMetadata` toggle (default: `true`)

**Shell Metadata Short-Circuit**: Windows Explorer probes every folder for metadata files like `desktop.ini`, `thumbs.db`, and `autorun.inf`. Without filtering, these probes trigger unnecessary ZIP Central Directory parsing. The `ShellMetadataFilter` intercepts these in `CreateFile` before any string allocation occurs, returning `FileNotFound` immediately. Controlled via `Mount:ShortCircuitShellMetadata` in `appsettings.json`.

**Debug Logging**: All Dokan file system operations log at `Debug` level with the command name and file path, enabling detailed diagnostics when the Serilog minimum level is lowered.

### Presentation Layer (`src/ZipDrive.Cli`)

Command-line interface entry point with OpenTelemetry SDK wiring.

**Key Responsibilities**:
- DI registration for all services (including `DualTierFileCache`)
- OpenTelemetry SDK configuration (opt-in; OTLP export to Aspire Dashboard when endpoint configured)
- Serilog structured logging
- Configuration binding (`Mount`, `Cache`, `OpenTelemetry` sections)

## Key Design Patterns

| Pattern | Location | Purpose |
|---------|----------|---------|
| **Clean Architecture** | Solution structure | Dependency inversion, testability |
| **Pluggable Strategy** | `IStorageStrategy<T>`, `IEvictionPolicy` | Storage and eviction extensibility |
| **Borrow/Return (RAII)** | `GenericCache<T>`, `ICacheHandle<T>` | Reference counting, eviction protection |
| **Lazy Materialization** | `Lazy<Task<T>>` | Thundering herd prevention |
| **Async Cleanup** | `DiskStorageStrategy` | Non-blocking eviction |
| **Dual-Tier Routing** | `DualTierFileCache` | Size-based memory/disk routing |
| **Static Telemetry** | `CacheTelemetry`, `ZipTelemetry`, `DokanTelemetry` | Zero-DI metrics/tracing |
| **Object Pooling** | Archive sessions (future) | Reuse expensive resources |

## Critical Concurrency Rules

When working with the caching layer:

1. **Never use global locks for cache hits** - Layer 1 must be lock-free
2. **Use per-key locks for materialization** - `Lazy<Task<T>>` with `ExecutionAndPublication` mode
3. **Different keys must not block each other** - Parallel materialization is critical
4. **Eviction must not block reads** - Use separate eviction lock
5. **Always use TimeProvider for TTL** - Enables deterministic testing with `FakeTimeProvider`
6. **Borrow/Return pattern is mandatory** - Always dispose `ICacheHandle<T>` to allow eviction
7. **Entries with RefCount > 0 are protected** - Never evict borrowed entries

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
- **Concurrency**: 23 tasks (8 browsers, 5 sequential readers, 4 path stress, 3 same-file thundering herd, 2 different-file parallel, 1 maintenance loop)
- **Cache config**: Tight limits (2MB memory, 20MB disk, 5MB cutoff, 1min TTL, 2s maintenance interval) to force eviction
- **Verification**: SHA-256 content checks on every read against embedded `__manifest__.json`
- **Post-run assertions**: Zero errors, zero handle leaks (`BorrowedEntryCount == 0`), verified reads > 0, maintenance ran
- **Fixture**: `EnduranceMixed` profile generates files spanning both memory tier (<5MB) and disk tier (>=5MB)
- **Validated**: 8-hour soak test passed with zero errors and zero data corruption

## Configuration Schema

### CacheOptions (`appsettings.json` → "Cache" section)

```json
{
  "Cache": {
    "MemoryCacheSizeMb": 2048,              // Memory tier capacity (2GB)
    "DiskCacheSizeMb": 10240,               // Disk tier capacity (10GB)
    "SmallFileCutoffMb": 50,                // Routing threshold (< cutoff → memory, >= cutoff → disk)
    "TempDirectory": null,                  // null = system temp dir
    "DefaultTtlMinutes": 30,               // Entry expiration (used by ZipVirtualFileSystem + ArchiveStructureCache)
    "EvictionCheckIntervalSeconds": 60      // CacheMaintenanceService sweep interval
  }
}
```

All six options are wired and active. `CacheOptions` exposes computed properties `DefaultTtl`, `EvictionCheckInterval`, `MemoryCacheSizeBytes`, `DiskCacheSizeBytes`, and `SmallFileCutoffBytes`.

**Tuning Guidelines**:
- Low memory systems: Reduce `MemoryCacheSizeMb`, lower `SmallFileCutoffMb`
- High memory systems: Increase both memory capacity and cutoff
- Fast SSD: Aggressive disk caching with larger `DiskCacheSizeMb`

### OpenTelemetry (`appsettings.json` → "OpenTelemetry" section)

OpenTelemetry is **opt-in**. When `Endpoint` is empty or absent, no OTel SDK is registered (zero overhead). Set the endpoint to enable metrics and tracing export.

```json
{
  "OpenTelemetry": {
    "Endpoint": "http://localhost:18889"
  }
}
```

Or via CLI: `--OpenTelemetry:Endpoint=http://localhost:18889`

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
| Phase 3 (Storage) | ✅ Complete | `MemoryStorageStrategy`, `DiskStorageStrategy`, `ObjectStorageStrategy<T>` |
| Phase 4 (Eviction) | ✅ Complete | `LruEvictionPolicy` |
| Phase 5 (Tests) | ✅ Complete | 42 integration tests passing |
| Phase 6 (Coordinator) | ✅ Complete | `DualTierFileCache` with size-hint routing (6 tests) |
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
| CLI | ✅ Complete | OTel wiring, DualTierFileCache DI, config binding, single-file publish |
| Multi-archive | ✅ Complete | `ArchiveTrie` + `ArchiveDiscovery` |
| Observability | ✅ Complete | OpenTelemetry metrics/tracing, Aspire Dashboard |
| Dual-tier Cache | ✅ Complete | `DualTierFileCache` with size-hint routing |
| Cache Maintenance | ✅ Complete | `CacheMaintenanceService` background eviction + cleanup |
| Endurance Testing | ✅ Complete | 8-hour soak test with SHA-256 verification, 23 concurrent tasks |

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
- **Cache miss (large)**: < 2s for 100MB file decompression + caching
- **Eviction**: < 1ms (mark phase only, cleanup async)
- **Concurrent reads**: Support 100+ simultaneous file reads
- **Directory listing**: 1000 entries in < 100ms

## Success Criteria

ZipDrive is considered complete when:
- [x] Caching layer fully implemented with 80%+ test coverage (66 tests)
- [x] ZIP reader implemented and tested (15 tests)
- [x] DokanNet adapter functional (mount/unmount works)
- [x] CLI accepts arguments and mounts drives
- [x] Dual-tier cache coordinator implemented and tested (6 tests)
- [x] OpenTelemetry observability (metrics, tracing, Aspire Dashboard)
- [x] Background cache maintenance with configurable interval
- [ ] All performance targets met
- [x] No memory leaks (validated with 8-hour soak test, zero handle leaks)
- [x] Comprehensive documentation written
- [x] Single-file release build (`dotnet publish` with `PublishSingleFile`)
