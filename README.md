[![CI](https://github.com/hooyao/ZipDrive/actions/workflows/ci.yml/badge.svg)](https://github.com/hooyao/ZipDrive/actions/workflows/ci.yml)
[![Release](https://github.com/hooyao/ZipDrive/actions/workflows/release.yml/badge.svg?event=push)](https://github.com/hooyao/ZipDrive/actions/workflows/release.yml)

# ZipDrive

**Mount ZIP and RAR archives as native Windows drives — browse and stream files instantly.**

ZipDrive turns any directory of ZIP and RAR files into a browsable Windows drive letter. Unlike Windows Explorer's built-in "Open as folder" (which silently extracts entire archives to temp before you can open a single file), ZipDrive decompresses incrementally in 10MB chunks — the first byte is available in ~50ms, while the rest extracts in the background. Open a 5GB video from a ZIP and it starts playing immediately. No upfront wait. No temp folder bloat. Just `R:\archive.zip\folder\file.txt` in every app.

## Why ZipDrive?

| | Windows Built-in ZIP | Other Mount Tools | **ZipDrive** |
|---|---|---|---|
| Real drive letter | No (folder view only) | Varies | **Yes — works in every app** |
| Upfront extraction | Yes (hidden temp copy) | Often required | **No — incremental chunked extraction, first byte in ~50ms** |
| Multi-archive mount | No | Rarely | **Yes — entire directories of ZIPs** |
| Large file support (>4 GB) | Limited | Varies | **Full ZIP64 support** |
| International filenames | Mojibake common | Varies | **Auto charset detection (Shift-JIS, GBK, EUC-KR, ...)** |
| RAR support | No | Varies | **RAR4 & RAR5 (non-solid) via SharpCompress** |
| Concurrent access | Single-threaded | Varies | **100+ simultaneous readers, validated by 12-hour soak test** |
| Observability | None | None | **OpenTelemetry metrics & tracing** |
| Open source | No | Rarely | **Yes — clean architecture, extensible** |

## Key Features

- **Virtual Drive Mounting** — Mount any directory of ZIP and RAR archives as a Windows drive letter
- **Multi-Format Support** — ZIP (with ZIP64) and RAR (RAR4/RAR5 non-solid) via a pluggable provider architecture. Solid RAR archives are flagged as unsupported with a clear explanation and remediation steps
- **Multi-Archive Discovery** — Automatically discovers and indexes archive files recursively
- **Chunked Incremental Extraction** — Decompresses files in 10MB chunks; completed chunks are served instantly while extraction continues in the background. A 5GB file starts serving reads in ~50ms instead of waiting 25+ seconds for full extraction
- **Dual-Tier Caching** — Small files (< 50MB) cached in memory as byte arrays, large files cached on disk via NTFS sparse files — each tier with independent capacity limits and LRU eviction
- **Streaming ZIP Reader** — Custom ZIP parser with ZIP64 support; parses Central Directory via streaming enumeration without loading the entire index into memory
- **Automatic Charset Detection** — Correctly displays Japanese, Chinese, Korean, and other non-Latin filenames via statistical encoding detection (UTF.Unknown, a .NET port based on Mozilla's Universal Charset Detector)
- **Sibling Prefetch** (opt-in) — When enabled, on first access to a file ZipDrive proactively warms nearby siblings in a single sequential pass through the ZIP. Disabled by default — see [How Prefetch Works](#how-prefetch-works) for details and side effects
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

![Drag and drop a folder onto ZipDrive.exe to mount](Animation.gif)

### Using the Command Line

```bash
ZipDrive.exe --Mount:ArchiveDirectory="D:\my-zips" --Mount:MountPoint="R:\"
```

This mounts all ZIP and RAR files found under `D:\my-zips` as the `R:\` drive.

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
| `Mount:UseFolderNameAsVolumeLabel` | `false` | When `true`, the mounted drive's volume label shows the archive directory folder name (e.g., `D:\my-zips` → "my-zips"). When `false`, the label is "ZipDrive" |

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

### Prefetch Options

Prefetch settings are nested under `Cache:Prefetch`. See [How Prefetch Works](#how-prefetch-works) for a full explanation.

| Setting | Default | Description |
|---------|---------|-------------|
| `Cache:Prefetch:Enabled` | `false` | Master on/off switch. Disabled by default — see side effects below |
| `Cache:Prefetch:OnRead` | `true` | Prefetch siblings when a file is opened. The recommended trigger — active as soon as `Enabled` is set to `true` |
| `Cache:Prefetch:OnListDirectory` | `false` | Prefetch siblings when a directory is listed. **Recommended to keep disabled** — image viewers and file managers enumerate sibling directories, causing prefetch across unrelated folders |
| `Cache:Prefetch:FileSizeThresholdMb` | `10` | Siblings larger than this are excluded from prefetch (they route to disk tier) |
| `Cache:Prefetch:MaxFiles` | `20` | Maximum siblings included in one prefetch span |
| `Cache:Prefetch:MaxDirectoryFiles` | `300` | Candidate cap: in very large directories, only the nearest N files by ZIP offset around the trigger are considered |
| `Cache:Prefetch:FillRatioThreshold` | `0.80` | Minimum density (wanted bytes / span bytes) required to accept a span. Lower values allow more hole bytes between files |

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

## How Prefetch Works

Sibling prefetch is an **opt-in** feature that reduces cold-start latency when you access multiple files in the same ZIP directory. It is **disabled by default** because it can cause unintended side effects with certain software.

### The mechanism

When you open a file inside a ZIP, ZipDrive must decompress it from the archive. Without prefetch, each file triggers a separate seek + decompress cycle. With prefetch enabled, ZipDrive proactively warms nearby siblings in a single pass:

```
Without prefetch:                     With prefetch:

Open file_003.raw                     Open file_003.raw
  → seek to file_003, decompress        → seek to file_001 (span start)
Open file_004.raw                        → decompress file_001..005 in one pass
  → seek to file_004, decompress        → all siblings now in cache
Open file_005.raw                     Open file_004.raw
  → seek to file_005, decompress        → instant cache hit (0ms)
                                      Open file_005.raw
3 seeks, 3 decompression cycles         → instant cache hit (0ms)

                                      1 seek, 1 decompression pass
```

**How the span is selected:**

1. When a file is read (cold miss), ZipDrive looks at all files in the same ZIP directory
2. Files above the size threshold (`FileSizeThresholdMb`, default 10 MB) are excluded
3. Up to `MaxFiles` (default 20) siblings nearest to the trigger by ZIP offset are selected
4. A **fill-ratio** check ensures the span is dense enough — if there are large gaps between files (unused ZIP data), the span is shrunk until at least 80% of the bytes read are wanted files
5. The selected files are decompressed in a single sequential read and placed in cache

### When prefetch helps

Prefetch is most effective when:
- You access many small files in the same directory (e.g., image sequences, web assets, game data)
- Files are physically close together in the ZIP (common — ZIP tools usually write directory contents sequentially)
- Your access pattern is sequential or semi-sequential

### Side effects and risks

**Prefetch is disabled by default** because the Windows file system API does not distinguish between a user intentionally opening a file and background software probing the drive:

- **File managers** (Windows Explorer, Total Commander) enumerate every directory to show previews, file counts, and metadata — each enumeration can trigger prefetch for that directory's contents
- **Image viewers** (FastStone, IrfanView) list sibling directories when you open an image — this causes prefetch to fire in every sibling folder, not just the one you're viewing
- **Search indexers** (Windows Search, Everything) crawl the entire drive — every indexed directory triggers prefetch
- **Antivirus scanners** scan files on access — each scanned file triggers prefetch for its siblings

The result can be a **cascade of I/O**: one directory listing triggers 20 file decompressions, across dozens of directories, potentially decompressing hundreds of files that are never actually read. This can:
- **Overflow the cache** — evicting files you actually need to make room for prefetched files nobody asked for
- **Saturate disk I/O** — continuous decompression competing with actual user reads
- **Increase CPU usage** — decompression is CPU-bound work

### How to configure it

**To enable prefetch** (recommended only if you control what software accesses the drive):

```bash
# Minimal: just flip the master switch — OnRead is true by default
ZipDrive.exe --Cache:Prefetch:Enabled=true ...
```

Or in `appsettings.jsonc`:
```jsonc
"Cache": {
  "Prefetch": {
    "Enabled": true
  }
}
```

**To selectively disable triggers** if you see unwanted I/O:

| Symptom | Fix |
|---------|-----|
| Browsing folders in Explorer causes heavy I/O | Set `OnListDirectory` to `false` (already the default) |
| Opening a single file decompresses many siblings | Set `OnRead` to `false` |
| All prefetch-related I/O must stop | Set `Enabled` to `false` (the default) |

**To fine-tune the scope:**

| Knob | Effect |
|------|--------|
| Lower `MaxFiles` (e.g., 5) | Fewer siblings per prefetch — less aggressive |
| Lower `FileSizeThresholdMb` (e.g., 2) | Skip medium files, only prefetch very small ones |
| Higher `FillRatioThreshold` (e.g., 0.95) | Require files to be tightly packed — skip sparse directories |

### Troubleshooting

If you observe abnormally high disk I/O or CPU usage after enabling prefetch:

1. **Check if background software is triggering it** — disable `OnListDirectory` first (it's the most common culprit)
2. **If I/O persists**, disable `OnRead` to rule out prefetch entirely
3. **Monitor via OpenTelemetry** — the `prefetch.files_warmed` and `prefetch.bytes_read` metrics (meter `ZipDrive.Caching`) show how much work prefetch is doing
4. **As a last resort**, set `Enabled` to `false` to disable the feature completely

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
  ZipDrive.Infrastructure.Archives.Rar/   RAR4/RAR5 provider via SharpCompress
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

# Run extended endurance test (e.g., 12 hours)
ENDURANCE_DURATION_HOURS=12 dotnet test tests/ZipDrive.EnduranceTests
```

**Test coverage**: 450+ tests across unit, integration, concurrency, and endurance suites. 12-hour soak test validated with zero errors and zero handle leaks. Concurrency model formally verified with TLA+ (see below).

## Design Documents

Detailed design documents are available in `src/Docs/`:

- [Caching Design](src/Docs/CACHING_DESIGN.md) — Comprehensive caching architecture
- [Chunked Extraction](src/Docs/CHUNKED_EXTRACTION_DESIGN.md) — Incremental chunk-based extraction design
- [Concurrency Strategy](src/Docs/CONCURRENCY_STRATEGY.md) — Multi-layer concurrency model with TLA+ formal verification
- [Streaming ZIP Reader](src/Docs/STREAMING_ZIP_READER_DESIGN.md) — ZIP format parsing details
- [VFS Architecture](src/Docs/VFS_ARCHITECTURE_DESIGN.md) — Virtual file system design
- [ZIP Structure Cache](src/Docs/ZIP_STRUCTURE_CACHE_DESIGN.md) — Metadata caching strategy
- [Multi-Format Archive Design](src/Docs/MULTI_FORMAT_ARCHIVE_DESIGN.md) — RAR provider and format-agnostic architecture
- [Dynamic Reload](src/Docs/DYNAMIC_RELOAD_DESIGN.md) — Per-archive add/remove with FileSystemWatcher
- [Implementation Checklist](src/Docs/IMPLEMENTATION_CHECKLIST.md) — Development roadmap

### Formal Verification

The cache concurrency protocol (5-layer BorrowAsync/Eviction strategy), chunked extraction synchronization, and archive drain protocol are formally specified in TLA+ (`specs/formal/`). The TLC model checker exhaustively verifies safety properties (no use-after-dispose, no unhandled exceptions, no stale reads) across all possible thread interleavings. See [Concurrency Strategy § Formal Verification](src/Docs/CONCURRENCY_STRATEGY.md#formal-verification-with-tla) for details.

## Technology Stack

- **.NET 10.0** / C# 13
- **DokanNet** — Windows user-mode file system driver
- **SharpCompress** — RAR4/RAR5 archive reading (MIT, pure managed C#)
- **OpenTelemetry** — Metrics, tracing (OTLP export)
- **UtfUnknown (UTF.Unknown)** — Statistical charset detection for non-UTF8 ZIP filenames
- **Serilog** — Structured logging
- **System.IO.MemoryMappedFiles** — Disk-tier cache storage
- **XUnit + FluentAssertions** — Testing
- **BenchmarkDotNet** — Performance benchmarks
- **NuGet Central Package Management** — All package versions defined in [`Directory.Packages.props`](Directory.Packages.props)

## License

See [LICENSE](LICENSE) for details.
