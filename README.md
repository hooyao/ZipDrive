# ZipDrive

A high-performance virtual file system that mounts ZIP archives as accessible Windows drives using [DokanNet](https://github.com/dokan-dev/dokany). Browse ZIP contents as if they were regular folders — no extraction needed.

## Features

- **Virtual Drive Mounting** — Mount any directory of ZIP archives as a Windows drive letter
- **Multi-Archive Discovery** — Automatically discovers and indexes ZIP files recursively
- **Two-Level Caching** — Structure cache (parsed ZIP metadata) + file content cache (decompressed data)
- **Dual-Tier File Cache** — Small files in memory, large files on disk via memory-mapped files
- **Streaming ZIP Reader** — Custom ZIP parser with ZIP64 support, no full extraction required
- **OpenTelemetry Observability** — Metrics, tracing, and structured logging with Aspire Dashboard support
- **Background Cache Maintenance** — Automatic LRU eviction and expired entry cleanup

## Prerequisites

- **Windows x64** (DokanNet is Windows-specific)
- **[Dokany v2.1.0.1000](https://github.com/dokan-dev/dokany/releases/tag/v2.1.0.1000)** — Dokan file system driver
- **.NET 10.0 SDK** — Required for building from source

## Quick Start

### Using the Published Executable

```bash
ZipDriveV3.Cli.exe --Mount:ArchiveDirectory="D:\my-zips" --Mount:MountPoint="R:\"
```

This mounts all ZIP files found under `D:\my-zips` as the `R:\` drive.

### Building from Source

```bash
# Build
dotnet build ZipDriveV3.slnx

# Run
dotnet run --project src/ZipDriveV3.Cli/ZipDriveV3.Cli.csproj -- \
  --Mount:ArchiveDirectory="D:\my-zips" --Mount:MountPoint="R:\"
```

### Publishing a Single-File Executable

```bash
dotnet publish src/ZipDriveV3.Cli/ZipDriveV3.Cli.csproj \
  -c Release -r win-x64 --self-contained false \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -o ./publish
```

Output: `publish/ZipDriveV3.Cli.exe` (~74 MB) + `publish/appsettings.json`

## Configuration

Configuration is loaded from `appsettings.json` and can be overridden via command-line arguments.

### Mount Options

| Setting | Default | Description |
|---------|---------|-------------|
| `Mount:MountPoint` | `R:\` | Drive letter to mount |
| `Mount:ArchiveDirectory` | (required) | Root directory containing ZIP archives |
| `Mount:MaxDiscoveryDepth` | `6` | Maximum directory depth for archive discovery |

### Cache Options

| Setting | Default | Description |
|---------|---------|-------------|
| `Cache:MemoryCacheSizeMb` | `2048` | Memory tier capacity (MB) |
| `Cache:DiskCacheSizeMb` | `10240` | Disk tier capacity (MB) |
| `Cache:SmallFileCutoffMb` | `50` | Files smaller than this go to memory; larger go to disk |
| `Cache:TempDirectory` | System temp | Directory for disk-cached temp files |
| `Cache:DefaultTtlMinutes` | `30` | Cache entry time-to-live |
| `Cache:EvictionCheckIntervalSeconds` | `60` | Background maintenance sweep interval |

### OpenTelemetry

| Setting | Default | Description |
|---------|---------|-------------|
| `OpenTelemetry:Endpoint` | (disabled) | OTLP gRPC exporter endpoint — set to enable telemetry |

OpenTelemetry is **opt-in**. To enable metrics and tracing, set the endpoint:

```bash
ZipDriveV3.Cli.exe --OpenTelemetry:Endpoint="http://localhost:18889" ...
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
File Content Cache: get/decompress file data (memory or disk tier)
  |
  v
Stream: seek + read -> return to DokanNet
```

### Project Structure

```
src/
  ZipDriveV3.Domain/                        Core interfaces and models (zero dependencies)
  ZipDriveV3.Application/                   Path resolution, archive discovery, VFS orchestration
  ZipDriveV3.Infrastructure.Archives.Zip/   Streaming ZIP reader with ZIP64 support
  ZipDriveV3.Infrastructure.Caching/        Generic cache, dual-tier routing, LRU eviction
  ZipDriveV3.Infrastructure.FileSystem/     DokanNet adapter and mount lifecycle
  ZipDriveV3.Cli/                           Entry point, DI, OpenTelemetry wiring

tests/
  ZipDriveV3.Domain.Tests/                  Domain layer unit tests
  ZipDriveV3.Infrastructure.Caching.Tests/  Cache behavior tests
  ZipDriveV3.Infrastructure.Archives.Zip.Tests/  ZIP reader tests
  ZipDriveV3.Infrastructure.Tests/          Infrastructure tests
  ZipDriveV3.IntegrationTests/              Integration scenarios
  ZipDriveV3.EnduranceTests/                Long-running soak tests
  ZipDriveV3.Benchmarks/                    Performance benchmarks
  ZipDriveV3.TestHelpers/                   Shared test utilities

src/Docs/                                   Design documents
openspec/                                   Specification-driven development artifacts
```

## Testing

```bash
# Run all tests
dotnet test

# Run specific test project
dotnet test tests/ZipDriveV3.Infrastructure.Caching.Tests

# Run tests matching a filter
dotnet test --filter "FullyQualifiedName~ThunderingHerd"

# Run endurance test (default: ~72 seconds)
dotnet test tests/ZipDriveV3.EnduranceTests

# Run extended endurance test (e.g., 8 hours)
ENDURANCE_DURATION_HOURS=8 dotnet test tests/ZipDriveV3.EnduranceTests
```

**Test coverage**: 196 tests across unit, integration, concurrency, and endurance suites. 8-hour soak test validated with zero errors and zero handle leaks.

## Design Documents

Detailed design documents are available in `src/Docs/`:

- [Caching Design](src/Docs/CACHING_DESIGN.md) — Comprehensive caching architecture
- [Concurrency Strategy](src/Docs/CONCURRENCY_STRATEGY.md) — Four-layer concurrency model
- [Streaming ZIP Reader](src/Docs/STREAMING_ZIP_READER_DESIGN.md) — ZIP format parsing details
- [VFS Architecture](src/Docs/VFS_ARCHITECTURE_DESIGN.md) — Virtual file system design
- [ZIP Structure Cache](src/Docs/ZIP_STRUCTURE_CACHE_DESIGN.md) — Metadata caching strategy
- [Implementation Checklist](src/Docs/IMPLEMENTATION_CHECKLIST.md) — Development roadmap

## Technology Stack

- **.NET 10.0** / C# 13
- **DokanNet** — Windows user-mode file system driver
- **OpenTelemetry** — Metrics, tracing (OTLP export)
- **Serilog** — Structured logging
- **System.IO.MemoryMappedFiles** — Disk-tier cache storage
- **XUnit + FluentAssertions** — Testing
- **BenchmarkDotNet** — Performance benchmarks
- **NuGet Central Package Management** — All package versions defined in [`Directory.Packages.props`](Directory.Packages.props)

## License

See [LICENSE](LICENSE) for details.
