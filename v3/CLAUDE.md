# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**ZipDrive V3** is a clean-architecture rewrite of the ZipDrive virtual file system. It mounts ZIP archives (and potentially other formats like TAR, 7Z) as accessible Windows drives using DokanNet. The V3 project is currently in active development, focusing on implementing a robust dual-tier caching system with pluggable eviction policies.

**Key Difference from V1**: V3 uses clean architecture with strict separation of concerns, async/await throughout, and modern .NET patterns. The caching layer is the core component that solves the fundamental mismatch between ZIP's sequential access and Windows file system's random access requirements.

## Technology Stack

- **Framework**: .NET 10.0 (`net10.0`)
- **Language**: C# 13/14 (implied by .NET 10)
- **SDK Version**: 10.0.100
- **Project Structure**: Clean Architecture / Onion Architecture
- **Key Libraries**: `System.IO.MemoryMappedFiles`, `System.Threading.Channels`, `System.Collections.Concurrent`

## Prerequisites

- **Windows x64 only** - Uses DokanNet which is Windows-specific
- **Dokany v2.1.0.1000** must be installed from https://github.com/dokan-dev/dokany/releases/tag/v2.1.0.1000
- **.NET 10.0 SDK** (Note: Currently targets .NET 10 preview - may need adjustment to .NET 8 LTS for production)

## Build and Run Commands

```bash
# Build the entire solution
dotnet build ZipDriveV3.slnx

# Build in Release mode
dotnet build ZipDriveV3.slnx -c Release

# Run all tests
dotnet test

# Run tests in specific project
dotnet test tests/ZipDriveV3.Domain.Tests/ZipDriveV3.Domain.Tests.csproj

# Run specific test
dotnet test --filter "FullyQualifiedName~ThunderingHerd"

# Run CLI (when implemented)
dotnet run --project src/ZipDriveV3.Cli/ZipDriveV3.Cli.csproj
```

## Architecture

ZipDrive V3 follows **Clean Architecture** (Onion Architecture) with strict dependency rules.

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
│  Presentation (ZipDriveV3.Cli)              │
│  ↓ depends on                               │
│  Application (ZipDriveV3.Application)       │
│  ↓ depends on                               │
│  Domain (ZipDriveV3.Domain) ← Core          │
│  ↑ implemented by                           │
│  Infrastructure (ZipDriveV3.Infrastructure.*) │
└─────────────────────────────────────────────┘
```

### Domain Layer (`src/ZipDriveV3.Domain`)

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

### Application Layer (`src/ZipDriveV3.Application`)

**Purpose**: Orchestrates use cases, implements domain logic.

**Key Services**:
- `PathResolver`: Implementation of path resolution logic

### Infrastructure Layer

#### **Caching (`src/ZipDriveV3.Infrastructure.Caching`) - CRITICAL COMPONENT**

This is the **most important** subsystem. It solves the core problem: ZIP provides sequential-only access (compressed streams), but Windows file system requires random access at arbitrary offsets.

**Architecture**: Dual-tier caching with unified eviction policy
- **Memory Tier** (files < 50MB): `ConcurrentDictionary` + `byte[]` storage
- **Disk Tier** (files ≥ 50MB): `MemoryMappedFile` backed by temp files

**Why Custom Cache (not built-in MemoryCache)?**
- Built-in `MemoryCache` lacks pluggable eviction policies
- Unpredictable compaction behavior when capacity exceeded
- No control over which entries get evicted
- We need deterministic LRU/LFU/Size-First strategies

**Three-Layer Concurrency Strategy** (prevents thundering herd):
1. **Layer 1 (Lock-free)**: `ConcurrentDictionary.TryGetValue` for cache hits (< 100ns, zero contention)
2. **Layer 2 (Per-key)**: `Lazy<Task<T>>` prevents duplicate materialization of same file
3. **Layer 3 (Eviction)**: Global lock only when capacity exceeded (infrequent)

**Key Features**:
- TTL-based expiration (default: 30 minutes)
- Size-based capacity limits (2GB memory + 10GB disk)
- Async cleanup (< 1ms eviction latency via mark-for-deletion)
- Pluggable `IEvictionPolicy` (Strategy pattern)

**Documentation**: See `src/Docs/`:
- [`CACHING_DESIGN.md`](src/Docs/CACHING_DESIGN.md) - Comprehensive design (1500+ lines)
- [`CONCURRENCY_STRATEGY.md`](src/Docs/CONCURRENCY_STRATEGY.md) - Multi-layer locking details
- [`IMPLEMENTATION_CHECKLIST.md`](src/Docs/IMPLEMENTATION_CHECKLIST.md) - Implementation steps

#### **ZIP Structure Cache (`src/ZipDriveV3.Infrastructure.Caching`) - NEW**

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

#### **Archives (`src/ZipDriveV3.Infrastructure.Archives.Zip`)**

Handles ZIP format parsing, entry enumeration, and stream extraction.

#### **FileSystem (`src/ZipDriveV3.Infrastructure.FileSystem`)**

DokanNet integration for Windows file system mounting (to be implemented).

### Presentation Layer (`src/ZipDriveV3.Cli`)

Command-line interface entry point (currently placeholder).

## Key Design Patterns

| Pattern | Location | Purpose |
|---------|----------|---------|
| **Clean Architecture** | Solution structure | Dependency inversion, testability |
| **Pluggable Strategy** | `IArchiveProvider`, `IEvictionPolicy` | Format extensibility, eviction algorithms |
| **Dual-Tier Caching** | `DualTierFileCache` | Memory vs disk trade-off |
| **Lazy Materialization** | `Lazy<Task<T>>` | Thundering herd prevention |
| **Async Cleanup** | `DiskTierCache` | Non-blocking eviction |
| **Object Pooling** | Archive sessions (future) | Reuse expensive resources |

## Critical Concurrency Rules

When working with the caching layer:

1. **Never use global locks for cache hits** - Layer 1 must be lock-free
2. **Use per-key locks for materialization** - `Lazy<Task<T>>` with `ExecutionAndPublication` mode
3. **Different keys must not block each other** - Parallel materialization is critical
4. **Eviction must not block reads** - Use separate eviction lock
5. **Always use TimeProvider for TTL** - Enables deterministic testing with `FakeTimeProvider`

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
- **Thundering Herd**: 10+ threads requesting same uncached file (should materialize once)
- **Parallel Materialization**: Different keys should not block each other
- **Eviction Under Load**: Capacity exceeded with concurrent requests

## Configuration Schema

### CacheOptions (`appsettings.json` → "Cache" section)

```json
{
  "Cache": {
    "MemoryCacheSizeMb": 2048,              // Memory tier capacity (2GB)
    "DiskCacheSizeMb": 10240,               // Disk tier capacity (10GB)
    "SmallFileCutoffMb": 50,                // Routing threshold
    "TempDirectory": null,                  // null = system temp
    "DefaultTtlMinutes": 30,                // Entry expiration
    "EvictionCheckIntervalSeconds": 60      // Periodic cleanup
  }
}
```

**Tuning Guidelines**:
- Low memory systems: Reduce `MemoryCacheSizeMb`, lower `SmallFileCutoffMb`
- High memory systems: Increase both memory capacity and cutoff
- Fast SSD: Aggressive disk caching with larger `DiskCacheSizeMb`

## Common Development Tasks

### Implementing a New Eviction Policy

1. Create class implementing `IEvictionPolicy`
2. Implement `SelectVictims()` method with your algorithm (LRU, LFU, Size-First, etc.)
3. Register in DI container
4. Add unit tests verifying eviction order

### Adding Support for New Archive Format (e.g., TAR)

1. Create new project: `ZipDriveV3.Infrastructure.Archives.Tar`
2. Implement `IArchiveProvider` with `CanOpen()` and `OpenAsync()`
3. Implement `IArchiveSession` for TAR-specific operations
4. Register provider in DI container
5. Add integration tests with sample TAR files

### Debugging Cache Behavior

1. Enable verbose logging in `appsettings.json`
2. Check cache hit/miss metrics (exposed via `IFileCache.HitRate`)
3. Monitor eviction events in logs
4. Use deterministic tests with `FakeTimeProvider` to reproduce timing issues

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
- Simpler than per-key semaphores (V1's approach had deadlock risks)

### Why async cleanup?
- Deleting large temp files blocks the caller (high latency)
- Mark-for-deletion phase takes < 1ms (instant)
- Background task processes deletions asynchronously
- Acceptable tradeoff: temporary over-capacity for low eviction latency

### Why .NET 10 target?
⚠️ **Note**: `global.json` currently specifies .NET 10.0.100 (preview). For production deployment, consider targeting .NET 8 LTS instead.

## Implementation Roadmap (Active Task: Caching)

Refer to [`IMPLEMENTATION_CHECKLIST.md`](src/Docs/IMPLEMENTATION_CHECKLIST.md) for granular steps.

1. **Phase 1 (Interfaces)**: `IFileCache`, `IEvictionPolicy`, `CacheOptions`
2. **Phase 2 (Memory Tier)**: `MemoryTierCache` with `ConcurrentDictionary` and `Lazy<Task<T>>`
3. **Phase 3 (Disk Tier)**: `DiskTierCache` with `MemoryMappedFile` and async cleanup
4. **Phase 4 (Policies)**: Implement `LruEvictionPolicy`
5. **Phase 5 (Coordinator)**: `DualTierFileCache` to route based on file size
6. **Phase 6 (Integration)**: Verify sequential-to-random conversion

## Known Limitations / Future Work

- [ ] Mount/Unmount implementation (DokanNet integration)
- [ ] CLI argument parsing and hosted service
- [ ] TAR/7Z format providers (extensibility is designed in)
- [ ] Health checks and metrics endpoints
- [ ] Password-protected ZIP support (ZipCrypto, AES)
- [ ] Write support (currently read-only)
- [ ] ZIP64 support (files > 4GB, archives > 65535 entries)
- [ ] LZMA compression support
- [ ] Warm tier caching (three-tier strategy)

## Code Style Conventions

- **Nullable Reference Types**: Enabled throughout (`<Nullable>enable</Nullable>`)
- **Async/Await**: Use `async`/`await` for all I/O operations (no synchronous file access)
- **Immutability**: Prefer `record` types for models in Domain layer
- **Dependency Injection**: All services registered via DI, avoid `new` for services
- **Logging**: Use `ILogger<T>` with structured logging (include context properties)
- **Cancellation**: Always respect `CancellationToken` in async methods

## Related Documentation

- **Parent Project Context**: See `../.claude/CLAUDE.md` for V1 architecture analysis
- **File Content Caching**: [`src/Docs/CACHING_DESIGN.md`](src/Docs/CACHING_DESIGN.md)
- **ZIP Structure Cache**: [`src/Docs/ZIP_STRUCTURE_CACHE_DESIGN.md`](src/Docs/ZIP_STRUCTURE_CACHE_DESIGN.md)
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

V3 is considered complete when:
- ✅ Caching layer fully implemented with 80%+ test coverage
- ✅ ZIP provider implemented and tested
- ✅ DokanNet adapter functional (mount/unmount works)
- ✅ CLI accepts arguments and mounts drives
- ✅ All performance targets met
- ✅ No memory leaks (validated with 24hr soak test)
- ✅ Comprehensive documentation written
