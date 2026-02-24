# Project Context

## Purpose

**ZipDrive V3** is a Windows virtual file system that mounts ZIP archives (and potentially TAR, 7Z) as accessible Windows drives using DokanNet. Users can browse and read files inside ZIP archives as if they were regular folders in Windows Explorer, without extracting them to disk.

**Problem Solved**: Traditional workflow (Extract → Use → Delete) wastes storage and time. ZipDrive enables Mount → Use directly → Unmount.

**Current Status**: Core caching layer (42 tests) and streaming ZIP reader (15 tests) complete. DokanNet integration pending.

## Tech Stack

- **Framework**: .NET 10.0 (`net10.0`)
- **Language**: C# 13/14
- **SDK Version**: 10.0.100
- **File System Driver**: DokanNet 2.1.0 (user-mode file system via Dokany)
- **Testing**: XUnit, FluentAssertions
- **Logging**: Serilog (structured logging)
- **DI**: Microsoft.Extensions.DependencyInjection
- **Configuration**: Microsoft.Extensions.Configuration / Options pattern

## Project Conventions

### Code Style

- **Nullable Reference Types**: Enabled throughout (`<Nullable>enable</Nullable>`)
- **Async/Await**: Use `async`/`await` for all I/O operations (no synchronous file access)
- **Immutability**: Prefer `record` types for models in Domain layer
- **Dependency Injection**: All services registered via DI, avoid `new` for services
- **Logging**: Use `ILogger<T>` with structured logging (include context properties)
- **Cancellation**: Always respect `CancellationToken` in async methods
- **Naming**: PascalCase for public members, camelCase with underscore prefix for private fields (`_fieldName`)

### Architecture Patterns

**Clean Architecture (Onion Architecture)** with strict dependency rules:

```
Presentation (CLI) → Application → Domain ← Infrastructure
```

| Pattern | Location | Purpose |
|---------|----------|---------|
| Clean Architecture | Solution structure | Dependency inversion, testability |
| Pluggable Strategy | `IStorageStrategy<T>`, `IEvictionPolicy` | Storage and eviction extensibility |
| Borrow/Return (RAII) | `GenericCache<T>`, `ICacheHandle<T>` | Reference counting, eviction protection |
| Lazy Materialization | `Lazy<Task<T>>` | Thundering herd prevention |
| Async Cleanup | `DiskStorageStrategy` | Non-blocking eviction |

**Layer Structure**:
- `ZipDriveV3.Domain` - Core interfaces and models (zero external dependencies)
- `ZipDriveV3.Application` - Use cases and orchestration
- `ZipDriveV3.Infrastructure.*` - Implementations (Caching, Archives.Zip, FileSystem)
- `ZipDriveV3.Cli` - Command-line entry point

### Testing Strategy

- **Target**: 80%+ code coverage
- **Unit Tests**: Cache behavior, eviction policies, path resolution, tree building
- **Integration Tests**: Sequential-to-random conversion, capacity enforcement, concurrent access
- **Concurrency Tests**: Thundering herd (10+ threads), parallel materialization, eviction under load
- **Tools**: XUnit, FluentAssertions, `FakeTimeProvider` for TTL tests

**Workflow**: Code Change → Build → Write Tests → Run Tests → Pass → Done

### Git Workflow

- **Main branch**: `main`
- **Feature branches**: `feat/V3`, `feat/<feature-name>`
- **Commit style**: Conventional commits (`feat:`, `fix:`, `docs:`, `refactor:`, `test:`)

## Domain Context

### Two-Level Caching Architecture

ZIP archives provide sequential-only access (compressed streams), but Windows file systems require random access at arbitrary offsets. The caching layer solves this by materializing decompressed content.

**Structure Cache**: Stores parsed ZIP Central Directory metadata for fast lookups
- `ArchiveStructure` with `Dictionary<string, ZipEntryInfo>` for O(1) entry lookup
- ~114 bytes per entry (10,000-file ZIP ≈ 1.1 MB)

**File Content Cache**: Stores decompressed file data
- Memory tier: `byte[]` for small files (< 50MB)
- Disk tier: `MemoryMappedFile` backed by temp files for large files (≥ 50MB)
- LRU eviction with TTL-based expiration

### Four-Layer Concurrency Strategy

1. **Layer 1 (Lock-free)**: `ConcurrentDictionary.TryGetValue` for cache hits
2. **Layer 2 (Per-key)**: `Lazy<Task<T>>` prevents duplicate materialization
3. **Layer 3 (Eviction)**: Global lock only when capacity exceeded
4. **Layer 4 (RefCount)**: Borrowed entries protected from eviction

### Key Domain Terms

- **Archive**: A compressed file container (ZIP, TAR, 7Z)
- **Entry**: A file or directory within an archive
- **Materialization**: Decompressing and caching entry content
- **Borrow/Return**: RAII pattern for cache access with reference counting
- **Thundering Herd**: Multiple threads requesting same uncached resource simultaneously

## Important Constraints

- **Windows x64 only** - DokanNet is Windows-specific
- **Dokany v2.1.0.1000** must be installed (kernel driver dependency)
- **Read-only** - No write support to archives
- **Compression support**: Store (0) and Deflate (8) methods only (no LZMA yet)
- **No encryption support** - Password-protected ZIPs not supported yet

### Performance Targets

- Mount latency: 10GB ZIP in < 5 seconds
- Structure cache build: < 100ms for 10,000-entry ZIP
- Cache hit: < 1ms overhead
- Directory listing: 1000 entries in < 100ms
- Concurrent reads: 100+ simultaneous file reads

## External Dependencies

| Dependency | Purpose | Version |
|------------|---------|---------|
| DokanNet | User-mode file system driver | 2.1.0 |
| Dokany | Kernel driver (must be installed) | v2.1.0.1000 |
| System.IO.MemoryMappedFiles | Large file caching | Built-in |
| System.IO.Compression | ZIP reading (reference only) | Built-in |
| Serilog | Structured logging | Latest |
| Microsoft.Extensions.* | DI, Configuration, Hosting | Latest |
