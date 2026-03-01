[![CI](https://github.com/hooyao/ZipDrive/actions/workflows/ci.yml/badge.svg)](https://github.com/hooyao/ZipDrive/actions/workflows/ci.yml)
[![Release](https://github.com/hooyao/ZipDrive/actions/workflows/release.yml/badge.svg)](https://github.com/hooyao/ZipDrive/actions/workflows/release.yml)

# ZipDrive

**Mount ZIP archives as native Windows drives — browse and stream files instantly.**

ZipDrive turns any directory of ZIP files into a browsable Windows drive letter. Unlike Windows Explorer's built-in "Open as folder" (which silently extracts entire archives to temp before you can open a single file), ZipDrive decompresses incrementally in 10MB chunks — the first byte is available in ~50ms, while the rest extracts in the background. Open a 5GB video from a ZIP and it starts playing immediately. No upfront wait. No temp folder bloat. Just `R:\archive.zip\folder\file.txt` in every app.

## Why ZipDrive?

| | Windows Built-in ZIP | Other Mount Tools | **ZipDrive** |
|---|---|---|---|
| Real drive letter | No (folder view only) | Varies | **Yes — works in every app** |
| Upfront extraction | Yes (hidden temp copy) | Often required | **No — incremental chunked extraction, first byte in ~50ms** |
| Multi-archive mount | No | Rarely | **Yes — entire directories of ZIPs** |
| Large file support (>4 GB) | Limited | Varies | **Full ZIP64 support** |
| International filenames | Mojibake common | Varies | **Auto charset detection (Shift-JIS, GBK, EUC-KR, ...)** |
| Concurrent access | Single-threaded | Varies | **100+ simultaneous readers, validated by 8-hour soak test** |
| Observability | None | None | **OpenTelemetry metrics & tracing** |
| Open source | No | Rarely | **Yes — clean architecture, extensible** |

## Key Features

- **Virtual Drive Mounting** — Mount any directory of ZIP archives as a Windows drive letter
- **Multi-Archive Discovery** — Automatically discovers and indexes ZIP files recursively
- **Chunked Incremental Extraction** — Decompresses files in 10MB chunks; completed chunks are served instantly while extraction continues in the background. A 5GB file starts serving reads in ~50ms instead of waiting 25+ seconds for full extraction
- **Dual-Tier Caching** — Small files (< 50MB) cached in memory as byte arrays, large files cached on disk via NTFS sparse files — each tier with independent capacity limits and LRU eviction
- **Streaming ZIP Reader** — Custom ZIP parser with ZIP64 support; parses Central Directory via streaming enumeration without loading the entire index into memory
- **Automatic Charset Detection** — Correctly displays Japanese, Chinese, Korean, and other non-Latin filenames via statistical encoding detection (UTF.Unknown, a .NET port based on Mozilla's Universal Charset Detector)
- **Thundering Herd Prevention** — Lock-free cache hits with per-key deduplication; 100 concurrent requests for the same uncached file trigger only one decompression, and all readers access completed chunks concurrently
- **OpenTelemetry Observability** — Opt-in metrics, tracing, and structured logging with Aspire Dashboard support
- **Background Cache Maintenance** — Automatic LRU eviction and expired entry cleanup with configurable intervals
- **Shell Metadata Short-Circuit** — Intercepts Windows Explorer probes (`desktop.ini`, `thumbs.db`, etc.) before any ZIP parsing occurs
- **Drag-and-Drop Launch** — Drag a folder onto `ZipDrive.exe` to mount it instantly — no command line needed

## Prerequisites

- **Windows x64** (DokanNet is Windows-specific)
- **[Dokany v2.1.0.1000](https://github.com/dokan-dev/dokany/releases/tag/v2.1.0.1000)** — Dokan file system driver
- **.NET 10.0 SDK** — Required for building from source

## Quick Start

### Drag and Drop

The easiest way: **drag a folder onto `ZipDrive.exe`** in Windows Explorer. All ZIP files in that folder are mounted at `R:\` (configurable in `appsettings.jsonc`).

### Using the Command Line

```bash
ZipDrive.exe --Mount:ArchiveDirectory="D:\my-zips" --Mount:MountPoint="R:\"
```

This mounts all ZIP files found under `D:\my-zips` as the `R:\` drive.

### Building from Source

```powershell
# Build
dotnet build ZipDrive.slnx

# Run
dotnet run --project src/ZipDrive.Cli/ZipDrive.Cli.csproj -- --Mount:ArchiveDirectory="D:\my-zips" --Mount:MountPoint="R:\"
```

### Publishing a Single-File Executable

```powershell
dotnet publish src/ZipDrive.Cli/ZipDrive.Cli.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./publish
```

Output: `publish/ZipDrive.exe` (~74 MB) + `publish/appsettings.jsonc`

## Configuration

All settings are read from `appsettings.jsonc` (shipped alongside the executable). If everything is configured there, the executable can run without any command-line arguments:

```bash
ZipDrive.exe
```

Command-line arguments **override** `appsettings.jsonc` values using the `--Section:Key=Value` syntax:

```powershell
ZipDrive.exe --Mount:ArchiveDirectory="D:\my-zips" --Mount:MountPoint="Z:\" --Cache:TempDirectory="D:\zipdrive-cache"
```

### Mount Options

| Setting | Default | Description |
|---------|---------|-------------|
| `Mount:MountPoint` | `R:\` | Drive letter to mount |
| `Mount:ArchiveDirectory` | (required) | Root directory containing ZIP archives |
| `Mount:MaxDiscoveryDepth` | `6` | Maximum directory depth for archive discovery |
| `Mount:ShortCircuitShellMetadata` | `true` | Skip Windows shell metadata probes (desktop.ini, thumbs.db, etc.) to avoid unnecessary ZIP parsing |
| `Mount:FallbackEncoding` | `utf-8` | Fallback encoding for non-UTF8 filenames when auto-detection fails. Accepts any .NET encoding name (e.g., `shift_jis`, `gb2312`) |
| `Mount:EncodingConfidenceThreshold` | `0.5` | Minimum confidence (0.0-1.0) for charset auto-detection. Lower values accept more guesses |

### Cache Options

| Setting | Default | Description |
|---------|---------|-------------|
| `Cache:MemoryCacheSizeMb` | `2048` | Memory tier capacity (MB) |
| `Cache:DiskCacheSizeMb` | `10240` | Disk tier capacity (MB) |
| `Cache:SmallFileCutoffMb` | `50` | Files smaller than this go to memory; larger go to disk |
| `Cache:ChunkSizeMb` | `10` | Chunk size for incremental disk-tier extraction. First-byte latency is roughly `ChunkSizeMb / decompression_speed`. 10 MB gives ~50ms at ~200 MB/s |
| `Cache:TempDirectory` | System temp | Directory for disk-tier cache files. Set this to a fast SSD path to improve large-file read performance. When `null`, defaults to the OS temp directory |
| `Cache:DefaultTtlMinutes` | `30` | Cache entry time-to-live |
| `Cache:EvictionCheckIntervalSeconds` | `10` | Background maintenance sweep interval |

### OpenTelemetry

| Setting | Default | Description |
|---------|---------|-------------|
| `OpenTelemetry:Endpoint` | (disabled) | OTLP gRPC exporter endpoint — set to enable telemetry |

OpenTelemetry is **opt-in**. To enable metrics and tracing, set the endpoint:

```bash
ZipDrive.exe --OpenTelemetry:Endpoint="http://localhost:18889" ...
```

To visualize metrics and traces locally, run the [Aspire Dashboard](https://learn.microsoft.com/en-us/dotnet/aspire/fundamentals/dashboard/overview):

```bash
docker run -p 18888:18888 -p 18889:18889 mcr.microsoft.com/dotnet/aspire-dashboard
```

Then open `http://localhost:18888`.

## Architecture

ZipDrive follows **Clean Architecture** with strict dependency rules:

```
Presentation (CLI)
  -> Application (orchestration)
    -> Domain (interfaces, models — zero external dependencies)
  <- Infrastructure (caching, ZIP reader, Dokan adapter)
```

### Data Flow

```
DokanNet ReadFile("R:\archive.zip\folder\file.txt")
  |
  v
Archive Prefix Tree: resolve path -> archive key + internal path
  |
  v
Structure Cache: get/build parsed ZIP Central Directory
  |
  v
File Content Cache: get/decompress file data (memory tier or chunked disk tier)
  |
  v
Stream: seek + read -> return to DokanNet
```

### Project Structure

```
src/
  ZipDrive.Domain/                        Core interfaces and models (zero dependencies)
  ZipDrive.Application/                   Path resolution, archive discovery, VFS orchestration
  ZipDrive.Infrastructure.Archives.Zip/   Streaming ZIP reader with ZIP64 support
  ZipDrive.Infrastructure.Caching/        Generic cache, chunked extraction, dual-tier routing, LRU eviction
  ZipDrive.Infrastructure.FileSystem/     DokanNet adapter and mount lifecycle
  ZipDrive.Cli/                           Entry point, DI, OpenTelemetry wiring

tests/
  ZipDrive.Domain.Tests/                  Domain layer unit tests
  ZipDrive.Infrastructure.Caching.Tests/  Cache behavior tests
  ZipDrive.Infrastructure.Archives.Zip.Tests/  ZIP reader tests
  ZipDrive.Infrastructure.Tests/          Infrastructure tests
  ZipDrive.IntegrationTests/              Integration scenarios
  ZipDrive.EnduranceTests/                Long-running soak tests
  ZipDrive.Benchmarks/                    Performance benchmarks
  ZipDrive.TestHelpers/                   Shared test utilities

src/Docs/                                   Design documents
openspec/                                   Specification-driven development artifacts
```

## Testing

```bash
# Run all tests
dotnet test

# Run specific test project
dotnet test tests/ZipDrive.Infrastructure.Caching.Tests

# Run tests matching a filter
dotnet test --filter "FullyQualifiedName~ThunderingHerd"

# Run endurance test (default: ~72 seconds)
dotnet test tests/ZipDrive.EnduranceTests

# Run extended endurance test (e.g., 8 hours)
ENDURANCE_DURATION_HOURS=8 dotnet test tests/ZipDrive.EnduranceTests
```

**Test coverage**: 325 tests across unit, integration, concurrency, and endurance suites. 8-hour soak test validated with zero errors and zero handle leaks.

## Design Documents

Detailed design documents are available in `src/Docs/`:

- [Caching Design](src/Docs/CACHING_DESIGN.md) — Comprehensive caching architecture
- [Chunked Extraction](src/Docs/CHUNKED_EXTRACTION_DESIGN.md) — Incremental chunk-based extraction design
- [Concurrency Strategy](src/Docs/CONCURRENCY_STRATEGY.md) — Multi-layer concurrency model
- [Streaming ZIP Reader](src/Docs/STREAMING_ZIP_READER_DESIGN.md) — ZIP format parsing details
- [VFS Architecture](src/Docs/VFS_ARCHITECTURE_DESIGN.md) — Virtual file system design
- [ZIP Structure Cache](src/Docs/ZIP_STRUCTURE_CACHE_DESIGN.md) — Metadata caching strategy
- [Implementation Checklist](src/Docs/IMPLEMENTATION_CHECKLIST.md) — Development roadmap

## Technology Stack

- **.NET 10.0** / C# 13
- **DokanNet** — Windows user-mode file system driver
- **OpenTelemetry** — Metrics, tracing (OTLP export)
- **UtfUnknown (UTF.Unknown)** — Statistical charset detection for non-UTF8 ZIP filenames
- **Serilog** — Structured logging
- **System.IO.MemoryMappedFiles** — Disk-tier cache storage
- **XUnit + FluentAssertions** — Testing
- **BenchmarkDotNet** — Performance benchmarks
- **NuGet Central Package Management** — All package versions defined in [`Directory.Packages.props`](Directory.Packages.props)

## License

See [LICENSE](LICENSE) for details.
