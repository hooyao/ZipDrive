[![CI](https://github.com/hooyao/ZipDrive/actions/workflows/ci.yml/badge.svg)](https://github.com/hooyao/ZipDrive/actions/workflows/ci.yml)
[![Release](https://github.com/hooyao/ZipDrive/actions/workflows/release.yml/badge.svg?event=push)](https://github.com/hooyao/ZipDrive/actions/workflows/release.yml)

# ZipDrive

**Mount ZIP and RAR archives as native Windows drives — browse and stream files instantly.**

ZipDrive turns ZIP and RAR archives into a browsable Windows drive letter. Unlike Windows Explorer's built-in "Open as folder" (which silently extracts entire archives to temp), ZipDrive decompresses incrementally in 10MB chunks — the first byte is available in ~50ms, while the rest extracts in the background. Open a 5GB video from an archive and it starts playing immediately.

## Why ZipDrive?

| | Windows Built-in | Other Mount Tools | **ZipDrive** |
|---|---|---|---|
| Real drive letter | No (folder view only) | Varies | **Yes — works in every app** |
| Upfront extraction | Yes (hidden temp copy) | Often required | **No — incremental chunked extraction, first byte in ~50ms** |
| Multi-archive mount | No | Rarely | **Yes — entire directories of ZIPs** |
| Single-file mount | No | Varies | **Yes — drag one ZIP/RAR onto the exe** |
| Large file support (>4 GB) | Limited | Varies | **Full ZIP64 support** |
| International filenames | Mojibake common | Varies | **Auto charset detection (Shift-JIS, GBK, EUC-KR, ...)** |
| RAR support | No | Varies | **RAR4 & RAR5 (non-solid) via SharpCompress** |
| Concurrent access | Single-threaded | Varies | **100+ simultaneous readers, validated by 12-hour soak test** |
| Open source | No | Rarely | **Yes — clean architecture, extensible** |

## Key Features

- **Virtual Drive Mounting** — Mount a directory of archives or a single archive file as a Windows drive letter
- **Multi-Format Support** — ZIP (with ZIP64) and RAR (RAR4/RAR5 non-solid) via a pluggable provider architecture
- **Chunked Incremental Extraction** — Decompresses in 10MB chunks; first byte in ~50ms, background extraction continues
- **Dual-Tier Caching** — Small files (< 50MB) in memory, large files on disk via NTFS sparse files — independent LRU eviction per tier
- **Automatic Charset Detection** — Correctly displays non-Latin filenames via statistical encoding detection (Shift-JIS, GBK, EUC-KR, etc.)
- **Dynamic Reload** — Add or remove archive files from the directory while ZipDrive is running; changes appear automatically
- **Sibling Prefetch** (opt-in) — Proactively warms nearby files in a single sequential pass. Disabled by default
- **Thundering Herd Prevention** — Lock-free cache hits with per-key deduplication across 100+ concurrent readers
- **OpenTelemetry Observability** — Opt-in metrics, tracing, and structured logging with Aspire Dashboard support
- **Drag-and-Drop Launch** — Drag a folder or a single archive file onto `ZipDrive.exe` — no command line needed

## Prerequisites

- **Windows x64** (DokanNet is Windows-specific)
- **[Dokany v2.3.1.1000](https://github.com/dokan-dev/dokany/releases/tag/v2.3.1.1000)** — Dokan file system driver
- **.NET 10.0 Runtime** — Required to run (SDK required for building from source)

## Quick Start

### Drag and Drop

**Drag a folder** onto `ZipDrive.exe` — all ZIP and RAR files in that folder are mounted at `R:\`.

**Drag a single ZIP or RAR file** onto `ZipDrive.exe` — just that archive is mounted.

![Drag and drop a folder onto ZipDrive.exe to mount](Animation.gif)

### Command Line

```bash
# Mount all archives in a directory
ZipDrive.exe --Mount:ArchiveDirectory="D:\my-zips" --Mount:MountPoint="R:\"

# Mount a single archive file
ZipDrive.exe --Mount:ArchiveDirectory="D:\Downloads\game.zip"
```

### Building from Source

```powershell
dotnet build ZipDrive.slnx
dotnet run --project src/ZipDrive.Cli/ZipDrive.Cli.csproj -- --Mount:ArchiveDirectory="D:\my-zips"
```

### Publishing a Single-File Executable

```powershell
dotnet publish src/ZipDrive.Cli/ZipDrive.Cli.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./publish
```

Output: `publish/ZipDrive.exe` (~74 MB) + `publish/appsettings.jsonc`

## Configuration

All settings are in `appsettings.jsonc` (shipped alongside the executable). Command-line arguments override with `--Section:Key=Value` syntax.

### Mount Options

| Setting | Default | Description |
|---------|---------|-------------|
| `Mount:MountPoint` | `R:\` | Drive letter to mount |
| `Mount:ArchiveDirectory` | (required) | Path to a directory of archives **or** a single archive file |
| `Mount:MaxDiscoveryDepth` | `6` | Maximum directory depth for archive discovery |
| `Mount:ShortCircuitShellMetadata` | `true` | Skip Windows shell metadata probes to avoid unnecessary ZIP parsing |
| `Mount:FallbackEncoding` | `utf-8` | Fallback encoding for non-UTF8 filenames (e.g., `shift_jis`, `gb2312`) |
| `Mount:EncodingConfidenceThreshold` | `0.5` | Minimum confidence (0.0-1.0) for charset auto-detection |
| `Mount:UseFolderNameAsVolumeLabel` | `false` | Use the archive directory folder name as the drive's volume label |
| `Mount:DynamicReloadQuietPeriodSeconds` | `5` | Delay before processing file system changes (debounce) |
| `Mount:HideUnsupportedArchives` | `false` | Hide unsupported archives (e.g., solid RAR) instead of showing them with a warning |

### Cache Options

| Setting | Default | Description |
|---------|---------|-------------|
| `Cache:MemoryCacheSizeMb` | `2048` | Memory tier capacity (MB) |
| `Cache:DiskCacheSizeMb` | `10240` | Disk tier capacity (MB) |
| `Cache:SmallFileCutoffMb` | `50` | Files smaller than this go to memory; larger go to disk |
| `Cache:ChunkSizeMb` | `10` | Chunk size for incremental disk-tier extraction (~50ms first-byte latency) |
| `Cache:TempDirectory` | System temp | Directory for disk-tier cache files |
| `Cache:DefaultTtlMinutes` | `30` | Cache entry time-to-live |
| `Cache:EvictionCheckIntervalSeconds` | `10` | Background maintenance sweep interval |

### Prefetch Options (under `Cache:Prefetch`)

Prefetch is **disabled by default** because background software (Explorer, antivirus, indexers) can trigger cascading I/O. Enable only if you control what accesses the drive.

| Setting | Default | Description |
|---------|---------|-------------|
| `Cache:Prefetch:Enabled` | `false` | Master on/off switch |
| `Cache:Prefetch:OnRead` | `true` | Prefetch siblings when a file is opened (active once `Enabled=true`) |
| `Cache:Prefetch:OnListDirectory` | `false` | Prefetch on directory listing — keep off to avoid cascade |
| `Cache:Prefetch:FileSizeThresholdMb` | `10` | Max file size for prefetch candidates |
| `Cache:Prefetch:MaxFiles` | `20` | Max siblings per prefetch span |
| `Cache:Prefetch:FillRatioThreshold` | `0.80` | Min density (wanted bytes / span bytes) |

### OpenTelemetry

Opt-in. Set the endpoint to enable metrics and tracing:

```bash
ZipDrive.exe --OpenTelemetry:Endpoint="http://localhost:18889"
```

Visualize with [Aspire Dashboard](https://learn.microsoft.com/en-us/dotnet/aspire/fundamentals/dashboard/overview): `docker run -p 18888:18888 -p 18889:18889 mcr.microsoft.com/dotnet/aspire-dashboard`, then open `http://localhost:18888`.

## Architecture

ZipDrive follows **Clean Architecture** with strict dependency rules:

```
Presentation (CLI)
  -> Application (orchestration)
    -> Domain (interfaces, models — zero external dependencies)
  <- Infrastructure (caching, ZIP reader, RAR reader, Dokan adapter)
```

### Data Flow

```
DokanNet ReadFile("R:\archive.zip\folder\file.txt")
  -> Archive Prefix Tree: resolve path -> archive key + internal path
  -> Structure Cache: get/build parsed Central Directory
  -> File Content Cache: get/decompress file data (memory or chunked disk tier)
  -> Stream: seek + read -> return to DokanNet
```

### Project Structure

```
src/
  ZipDrive.Domain/                        Core interfaces and models (zero dependencies)
  ZipDrive.Application/                   Path resolution, archive discovery, VFS orchestration
  ZipDrive.Infrastructure.Archives.Zip/   Streaming ZIP reader with ZIP64 support
  ZipDrive.Infrastructure.Archives.Rar/   RAR4/RAR5 provider via SharpCompress
  ZipDrive.Infrastructure.Caching/        Generic cache, chunked extraction, dual-tier routing
  ZipDrive.Infrastructure.FileSystem/     DokanNet adapter, mount lifecycle, user notices
  ZipDrive.Cli/                           Entry point, DI, OpenTelemetry wiring
```

## Testing

```bash
dotnet test                                     # Run all tests (~480 tests)
dotnet test --filter "FullyQualifiedName~ThunderingHerd"   # Run specific tests
dotnet test tests/ZipDrive.EnduranceTests       # Endurance soak (~72s default)
ENDURANCE_DURATION_HOURS=12 dotnet test tests/ZipDrive.EnduranceTests  # Extended
```

Concurrency model formally verified with TLA+ (`specs/formal/`). See [Concurrency Strategy](src/Docs/CONCURRENCY_STRATEGY.md) for details.

## Design Documents

Detailed design docs in [`src/Docs/`](src/Docs/):
[Caching](src/Docs/CACHING_DESIGN.md) | [Chunked Extraction](src/Docs/CHUNKED_EXTRACTION_DESIGN.md) | [Concurrency](src/Docs/CONCURRENCY_STRATEGY.md) | [ZIP Reader](src/Docs/STREAMING_ZIP_READER_DESIGN.md) | [VFS](src/Docs/VFS_ARCHITECTURE_DESIGN.md) | [Multi-Format](src/Docs/MULTI_FORMAT_ARCHIVE_DESIGN.md) | [Dynamic Reload](src/Docs/DYNAMIC_RELOAD_DESIGN.md)

## Technology Stack

- **.NET 10.0** / C# 13 — [DokanNet](https://github.com/dokan-dev/dokan-dotnet) — [SharpCompress](https://github.com/adamhathcock/sharpcompress) — [Spectre.Console](https://spectreconsole.net/) — [OpenTelemetry](https://opentelemetry.io/) — [Serilog](https://serilog.net/) — [UtfUnknown](https://github.com/CharsetDetector/UTF-unknown) — [XUnit](https://xunit.net/) + [FluentAssertions](https://fluentassertions.com/) — [BenchmarkDotNet](https://benchmarkdotnet.org/)

## License

See [LICENSE](LICENSE) for details.
