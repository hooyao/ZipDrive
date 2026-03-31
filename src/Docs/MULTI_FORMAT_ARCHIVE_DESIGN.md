# ZipDrive - Multi-Format Archive Support Design Document

**Version:** 1.0
**Date:** 2026-03-30
**Status:** Draft — Under Review
**Author:** Claude Code

---

## Executive Summary

**The Problem**: ZipDrive is hardwired to ZIP. The `ZipEntryInfo` record struct lives in Domain and leaks into every layer. `ArchiveStructureCache` directly calls `IZipReader`. `FileContentCache` takes `IZipReaderFactory`. The prefetch system hardcodes ZIP local header parsing and Deflate decompression. Adding a second format (RAR, 7Z, TAR) is impossible without modifying the caching layer, which is the project's most critical and most tested subsystem.

**The Solution**: Introduce a format-agnostic `ArchiveEntryInfo` domain model, three new provider interfaces (`IArchiveStructureBuilder`, `IArchiveEntryExtractor`, `IPrefetchStrategy`), and a `IFormatRegistry` that resolves providers by format ID. The caching layer drops its project reference to `Infrastructure.Archives.Zip` and delegates format-specific work through Domain interfaces. Each archive format lives in its own infrastructure project. The CLI composition root wires providers via DI.

**First New Format**: RAR (via SharpCompress). Mounted directories can contain mixed `.zip` + `.rar` files.

**Design Principle**: The caching layer is format-blind. It knows how to cache streams and manage eviction. It does not know how to parse headers or decompress data. Format-specific intelligence lives in format provider projects, accessed only through domain interfaces.

**Related Documents**:
- [`CACHING_DESIGN.md`](CACHING_DESIGN.md) — GenericCache, storage strategies, borrow/return
- [`STREAMING_ZIP_READER_DESIGN.md`](STREAMING_ZIP_READER_DESIGN.md) — ZIP reader internals
- [`ZIP_STRUCTURE_CACHE_DESIGN.md`](ZIP_STRUCTURE_CACHE_DESIGN.md) — Archive structure caching
- [`DYNAMIC_RELOAD_DESIGN.md`](DYNAMIC_RELOAD_DESIGN.md) — FileSystemWatcher, per-archive add/remove

---

## Table of Contents

1. [Problem Statement](#1-problem-statement)
2. [Design Goals and Non-Goals](#2-design-goals-and-non-goals)
3. [Architecture Overview](#3-architecture-overview)
4. [Domain Model Changes](#4-domain-model-changes)
5. [New Domain Interfaces](#5-new-domain-interfaces)
6. [ZIP Provider Extraction](#6-zip-provider-extraction)
7. [RAR Provider](#7-rar-provider)
8. [Caching Layer Refactor](#8-caching-layer-refactor)
9. [Application Layer Refactor](#9-application-layer-refactor)
10. [Dynamic Reload — Multi-Format](#10-dynamic-reload--multi-format)
11. [DI Wiring](#11-di-wiring)
12. [Performance Analysis](#12-performance-analysis)
13. [Migration Strategy](#13-migration-strategy)
14. [Test Strategy](#14-test-strategy)
15. [Implementation Phases](#15-implementation-phases)
16. [Risks and Mitigations](#16-risks-and-mitigations)

---

## 1. Problem Statement

### 1.1 ZIP-Specific Coupling Map

The table below shows every location where ZIP-specific types leak outside of `Infrastructure.Archives.Zip`.

| Layer | File | Coupling | Severity |
|-------|------|----------|----------|
| **Domain** | `Models/ZipEntryInfo.cs` | Type name, `LocalHeaderOffset`, `CompressionMethod` | **High** |
| **Domain** | `Models/ArchiveStructure.cs` | `TrieDictionary<ZipEntryInfo>`, `IsZip64` | **High** |
| **Domain** | `Abstractions/IFileContentCache.cs` | `ReadAsync(ZipEntryInfo)`, `WarmAsync(ZipEntryInfo)` | **High** |
| **Inf.Caching** | `ArchiveStructureCache.cs` | `IZipReaderFactory`, `IZipReader`, `ZipEocd`, `ZipCentralDirectoryEntry`, `IFilenameEncodingDetector` — the entire 300-line `BuildStructureAsync` is ZIP parsing | **High** |
| **Inf.Caching** | `FileContentCache.cs` | `IZipReaderFactory`, factory creates `IZipReader` for extraction | **High** |
| **Inf.Caching** | `.csproj` | `<ProjectReference>` to `Archives.Zip` | **High** |
| **Application** | `ZipVirtualFileSystem.cs:636-649` | Hardcoded compression methods `0` (Store), `8` (Deflate), ZIP local header parsing | **Medium** |
| **Application** | `SpanSelector.cs` | Sorts by `LocalHeaderOffset`, fill ratio via `CompressedSize` | **Medium** |
| **Application** | `PrefetchPlan.cs` | `IReadOnlyList<ZipEntryInfo>` | **Medium** |
| **Application** | `ArchiveDiscovery.cs:92` | Hardcoded `"*.zip"` | **Low** |
| **Inf.FileSystem** | `DokanHostedService.cs:189,298` | `"*.zip"` filter, `IsZipExtension()` | **Low** |

### 1.2 Why This Matters

Adding RAR support requires modifying `ArchiveStructureCache`, `FileContentCache`, and `ZipVirtualFileSystem` — all three are high-concurrency components with extensive test coverage. Without abstraction, each new format multiplies the complexity in these core files.

### 1.3 Format Comparison

| Aspect | ZIP | RAR (non-solid only) | 7Z (future) | TAR(.gz) (future) |
|--------|-----|-----|----|----|
| **Central Directory** | Yes (end of file) | No (sequential headers) | Yes (at end) | No |
| **Random Access** | Yes (seek to offset) | Yes (per-entry) | Limited (solid blocks) | No (sequential only) |
| **Compression** | Per-file (Store/Deflate) | Per-file | Solid block (LZMA2) | Outer wrapper (gzip/bz2/xz) |
| **Prefetch Strategy** | Contiguous byte-span read | None (V1) | TBD | TBD |
| **Extraction Model** | Seek → read local header → decompress | Open archive → extract by name | Open archive → extract by name | Stream through to target |
| **.NET Library** | Custom `IZipReader` | SharpCompress (MIT, pure managed) | SharpCompress / SevenZipSharp | SharpCompress / built-in |

The key insight: extraction models are fundamentally different across formats. We cannot have a single "seek to offset and decompress" abstraction. The interface must abstract at a higher level: "give me a decompressed stream for this entry."

---

## 2. Design Goals and Non-Goals

### Goals

1. **Zero performance regression for ZIP** — cache hit path unchanged, cache miss adds one virtual dispatch
2. **Break Caching → Archives.Zip dependency** — caching layer becomes format-blind
3. **No ZIP-specific types in Domain** — `ZipEntryInfo` moves to Archives.Zip as internal
4. **Mixed formats in one mount** — `.zip` + `.rar` coexist in the same directory
5. **Incremental migration** — each step compiles and tests pass
6. **RAR as first proof** — working RAR support validates the abstraction

### Non-Goals

- Optimized RAR prefetch (solid-block awareness) — future work
- TAR / 7Z support — validate architecture extensibility, implement later
- Password-protected archive support — orthogonal feature
- Write support for any format — still read-only

---

## 3. Architecture Overview

### 3.1 Target Dependency Graph

```
                          ┌──────────────────────┐
                          │    ZipDrive.Domain    │
                          │                       │
                          │  ArchiveEntryInfo     │
                          │  ArchiveStructure     │
                          │  IArchiveStructureBuilder  │
                          │  IArchiveEntryExtractor    │
                          │  IPrefetchStrategy         │
                          │  IFormatRegistry           │
                          │  IFileContentCache         │
                          │  IArchiveStructureCache    │
                          └───────────┬───────────┘
                                      │
                   ┌──────────────────┼──────────────────┐
                   │                  │                   │
         ┌────────▼────────┐  ┌──────▼──────┐  ┌───────▼────────┐
         │  Inf.Archives   │  │ Inf.Archives │  │  Inf.Caching   │
         │      .Zip       │  │    .Rar      │  │                │
         │                 │  │              │  │ (NO format     │
         │ ZipStructure-   │  │ RarStructure-│  │  references)   │
         │   Builder       │  │   Builder    │  │                │
         │ ZipEntry-       │  │ RarEntry-    │  │ ArchiveStructure│
         │   Extractor     │  │   Extractor  │  │   Cache        │
         │ ZipPrefetch-    │  │              │  │ FileContent-   │
         │   Strategy      │  │              │  │   Cache        │
         │ ZipFormatMeta-  │  │              │  │                │
         │   dataStore     │  │              │  │                │
         │ SpanSelector    │  │              │  │                │
         │ PrefetchPlan    │  │              │  │                │
         └────────┬────────┘  └──────┬──────┘  └───────┬────────┘
                  │                  │                   │
                  └──────────────────┼───────────────────┘
                                     │
                          ┌──────────▼──────────┐
                          │   ZipDrive.Cli      │
                          │  (Composition Root) │
                          │  FormatRegistry     │
                          │  DI wiring          │
                          └─────────────────────┘
```

### 3.2 Data Flow: ReadFile (Format-Agnostic)

```
DokanAdapter.ReadFile("R:\archive.rar\photo.jpg", offset=0)
  │
  ▼
ZipVirtualFileSystem.ReadFileAsync(path, buffer, offset)
  │  ArchiveTrie → ArchiveDescriptor { FormatId = "rar", PhysicalPath = "D:\archives\archive.rar" }
  │  StructureCache.GetOrBuildAsync("archive.rar", path, formatId="rar")
  │    └─ registry.GetStructureBuilder("rar") → RarStructureBuilder.BuildAsync()
  │         └─ SharpCompress: enumerate entries → ArchiveEntryInfo → ArchiveStructure
  │  structure.GetEntry("photo.jpg") → ArchiveEntryInfo { UncompressedSize = 5MB, ... }
  │
  ▼
FileContentCache.ReadAsync(archivePath, formatId="rar", entry, internalPath, cacheKey, buffer, offset)
  │  Tier routing: 5MB < 50MB cutoff → memory tier
  │  cache.BorrowAsync(key, ttl, factory, ct)
  │    HIT  → return cached stream (same as before, format-agnostic)
  │    MISS → factory():
  │             registry.GetExtractor("rar") → RarEntryExtractor
  │             extractor.ExtractAsync("D:\archive.rar", "photo.jpg", ct)
  │               └─ SharpCompress: open archive → find entry → decompress → Stream
  │             return CacheFactoryResult<Stream> { Value = stream, SizeBytes = 5MB }
  │  stream.Position = 0; stream.ReadAsync(buffer)
  │
  ▼
Return bytes to Dokan
```

### 3.3 Disposition of Existing Unused Interfaces

The Domain layer contains three interfaces from an earlier design iteration that overlap with the new provider interfaces:

| Existing Interface | Status | Reason |
|---|---|---|
| `IArchiveProvider` | **Delete** | Superseded by `IArchiveStructureBuilder` + `IArchiveEntryExtractor`. The new split is better (SRP: parsing vs extraction). |
| `IArchiveSession` | **Delete** | Superseded by `IArchiveEntryExtractor`. Session-based lifecycle adds complexity ZipDrive doesn't need (each extraction opens its own reader). |
| `IArchiveRegistry` | **Delete** | Marked "Deferred for future implementation" in the code. Superseded by `IFormatRegistry` (format resolution) + existing `IArchiveManager` (archive lifecycle). |
| `IFileSystemTree` / `FileNode` | **Delete** | Unused. `ArchiveStructure` with `TrieDictionary` replaced this approach. |
| `ArchiveCapabilities` | **Keep** | Used by `IArchiveSession` currently, but the concept is useful. Attach to `IArchiveStructureBuilder` or delete if unused after migration. |
| `ArchiveInfo` | **Keep** | Generic record, used by `IArchiveSession`. Evaluate after migration. |

These deletions happen in Phase 10 (cleanup) to avoid breaking any code during the migration.

### 3.4 What Does NOT Change

These components are already format-agnostic and require zero modifications:

- `GenericCache<T>` — works with any `T`
- `IStorageStrategy<Stream>` / `MemoryStorageStrategy` / `ChunkedDiskStorageStrategy` — receive decompressed `Stream`
- `IEvictionPolicy` / `LruEvictionPolicy` — format-unaware
- `CacheMaintenanceService` — calls generic `EvictExpired()` and `ProcessPendingCleanup()`
- `DokanFileSystemAdapter` — delegates to `IVirtualFileSystem`
- `ArchiveTrie` / `IArchivePrefixTree` — maps paths to `ArchiveDescriptor`
- `IPathResolver` / `PathResolver` — string manipulation only
- `ArchiveChangeConsolidator` — operates on virtual paths, format-unaware
- `ShellMetadataFilter` — path pattern matching only
- `CacheTelemetry`, `DokanTelemetry`, `ZipTelemetry`, `PrefetchTelemetry` — static metrics
- `ArchiveCapabilities`, `ArchiveInfo`, `VfsFileInfo` — already generic

---

## 4. Domain Model Changes

### 4.1 New: `ArchiveEntryInfo`

**File**: `src/ZipDrive.Domain/Models/ArchiveEntryInfo.cs`

```csharp
/// <summary>
/// Format-agnostic metadata for an archive entry.
/// Contains only consumer-facing fields needed for:
/// - File size display and EOF checks (UncompressedSize)
/// - Directory listing (IsDirectory, LastModified, Attributes)
/// - Error reporting (IsEncrypted)
/// - Integrity verification (Checksum)
///
/// Format-specific extraction metadata (offsets, compression methods, block indices)
/// stays in each format's provider project, accessed via the entry's internal path.
/// </summary>
public readonly record struct ArchiveEntryInfo
{
    /// <summary>
    /// Decompressed file size in bytes. Used for:
    /// - Cache tier routing (memory vs disk)
    /// - EOF checks in ReadAsync
    /// - Buffer allocation
    /// - File size display in directory listings
    /// </summary>
    public required long UncompressedSize { get; init; }

    /// <summary>True if this entry represents a directory.</summary>
    public required bool IsDirectory { get; init; }

    /// <summary>Last modification timestamp.</summary>
    public required DateTime LastModified { get; init; }

    /// <summary>File attributes for Windows Explorer display.</summary>
    public required FileAttributes Attributes { get; init; }

    /// <summary>True if the entry is encrypted (cannot extract without password).</summary>
    public bool IsEncrypted { get; init; }

    /// <summary>
    /// Integrity checksum. Semantics are format-defined:
    /// ZIP and RAR use CRC-32, 7Z may use CRC-32 or SHA-256.
    /// Zero if not available.
    /// </summary>
    public uint Checksum { get; init; }
}
```

**Design decision — why no `ExtractionKey`**: An earlier design included a `long ExtractionKey` field (opaque byte offset for ZIP, block index for RAR). This was rejected because:
1. It couples Domain to the concept of extraction coordinates, which is a provider concern
2. It requires a side-channel metadata store to reconstruct full format-specific metadata anyway
3. The `internalPath` string (already part of the trie key) is the universal entry identifier across all formats
4. The extractor interface takes `(archivePath, internalPath)` — no extraction key needed

**Design decision — why no `CompressedSize`**: CompressedSize is a format-internal field:
- ZIP uses it for SubStream bounds and prefetch fill-ratio
- RAR solid blocks don't have per-file compressed sizes
- TAR has no compression at the entry level
- Only the format provider's prefetch strategy needs it

### 4.2 Modified: `ArchiveStructure`

```diff
 public sealed class ArchiveStructure
 {
     public required string ArchiveKey { get; init; }
     public required string AbsolutePath { get; init; }
-    public required TrieDictionary<ZipEntryInfo> Entries { get; init; }
+    public required TrieDictionary<ArchiveEntryInfo> Entries { get; init; }
+    public required string FormatId { get; init; }
     public int EntryCount => Entries.Count;
     public required DateTimeOffset BuiltAt { get; init; }
-    public bool IsZip64 { get; init; }
     public long TotalUncompressedSize { get; init; }
     public long TotalCompressedSize { get; init; }
     public long EstimatedMemoryBytes { get; init; }
     public string? Comment { get; init; }

-    public ZipEntryInfo? GetEntry(string internalPath) { ... }
+    public ArchiveEntryInfo? GetEntry(string internalPath) { ... }
     public bool DirectoryExists(string dirPath) { ... }
-    public IEnumerable<(string Name, ZipEntryInfo Entry)> ListDirectory(string dirPath) { ... }
+    public IEnumerable<(string Name, ArchiveEntryInfo Entry)> ListDirectory(string dirPath) { ... }
 }
```

**Removed field**: `IsZip64` — ZIP-specific implementation detail. No consumer outside the ZIP reader needs to know this. If needed for diagnostics, the ZIP structure builder can log it.

**Kept fields**: `TotalCompressedSize` — remains useful across formats for compression ratio display. RAR and 7Z also report compressed sizes. If a format doesn't have this concept, it sets 0.

### 4.3 Modified: `ArchiveDescriptor`

```diff
 public sealed class ArchiveDescriptor
 {
     public required string VirtualPath { get; init; }
     public required string PhysicalPath { get; init; }
     public required long SizeBytes { get; init; }
     public required DateTime LastModifiedUtc { get; init; }
+    public required string FormatId { get; init; }

     public string Name => ...;
 }
```

### 4.4 Modified: `MountSettings`

```diff
 {
   "Mount": {
     "MountPoint": "R:\\",
     "ArchiveDirectory": "",
     "MaxDiscoveryDepth": 6,
     "ShortCircuitShellMetadata": true,
     "FallbackEncoding": "utf-8",
     "EncodingConfidenceThreshold": 0.5,
     "DynamicReloadQuietPeriodSeconds": 5,
-    "UseFolderNameAsVolumeLabel": false
+    "UseFolderNameAsVolumeLabel": false,
+    "HideUnsupportedArchives": false
   }
 }
```

`HideUnsupportedArchives`: When `false` (default), unsupported archives (e.g., solid RAR) appear as `name.rar (NOT SUPPORTED)` with a warning file inside. When `true`, they are excluded from the virtual drive entirely with a `LogWarning` for each filtered archive.

### 4.5 `ZipEntryInfo` Migration Path

| Phase | Status of `ZipEntryInfo` |
|-------|--------------------------|
| During migration | Remains in Domain alongside `ArchiveEntryInfo` for compilation compat |
| After all consumers migrated | Moves to `Infrastructure.Archives.Zip` as `internal` |
| Final | Removed from Domain entirely |

`ZipEntryInfo` continues to be used internally by the ZIP provider (`IZipReader.OpenEntryStreamAsync` still takes it). It just stops being visible outside the ZIP project.

---

## 5. New Domain Interfaces

### 5.1 `IArchiveStructureBuilder`

**File**: `src/ZipDrive.Domain/Abstractions/IArchiveStructureBuilder.cs`

```csharp
/// <summary>
/// Format-specific builder that parses an archive file and produces an ArchiveStructure.
/// Each archive format (ZIP, RAR, 7Z) implements this interface.
///
/// Called by ArchiveStructureCache on cache miss. The cache handles
/// caching, eviction, and thundering herd prevention — the builder just parses.
/// </summary>
public interface IArchiveStructureBuilder
{
    /// <summary>Format identifier (e.g., "zip", "rar", "7z").</summary>
    string FormatId { get; }

    /// <summary>File extensions this builder handles (e.g., [".zip"], [".rar", ".r00"]).</summary>
    IReadOnlyList<string> SupportedExtensions { get; }

    /// <summary>
    /// Builds an ArchiveStructure by parsing the archive file.
    /// Must populate the trie with ArchiveEntryInfo entries and synthesize parent directories.
    /// May also populate format-specific internal metadata stores as a side effect.
    /// </summary>
    Task<ArchiveStructure> BuildAsync(
        string archiveKey,
        string absolutePath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lightweight probe to detect unsupported archive variants (e.g., solid RAR)
    /// before trie registration. Reads only the file header (~30 bytes) — does NOT
    /// instantiate any archive library. Must be fast (< 0.1ms).
    /// Default implementation returns IsSupported=true (no unsupported variants).
    /// </summary>
    Task<ArchiveProbeResult> ProbeAsync(
        string absolutePath,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new ArchiveProbeResult(true));
}

public sealed record ArchiveProbeResult(bool IsSupported, string? UnsupportedReason = null);
```

### 5.2 `IArchiveEntryExtractor`

**File**: `src/ZipDrive.Domain/Abstractions/IArchiveEntryExtractor.cs`

```csharp
/// <summary>
/// Format-specific extractor that produces a decompressed stream for a single archive entry.
/// Called by FileContentCache on cache miss.
///
/// Resource management: the returned ExtractionResult is IAsyncDisposable.
/// The caller (FileContentCache's storage strategy) disposes it after stream consumption
/// via the CacheFactoryResult.OnDisposed callback.
/// </summary>
public interface IArchiveEntryExtractor
{
    /// <summary>Format identifier (e.g., "zip", "rar").</summary>
    string FormatId { get; }

    /// <summary>
    /// Extracts and decompresses a single entry from the archive.
    /// </summary>
    /// <param name="archivePath">Absolute path to the archive file.</param>
    /// <param name="internalPath">Entry path within the archive (forward slashes, no leading /).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// An ExtractionResult wrapping the decompressed stream and resource cleanup.
    /// The stream is fully decompressed and seekable (for memory-backed results)
    /// or at least forward-readable (for streaming results consumed by storage strategies).
    /// </returns>
    Task<ExtractionResult> ExtractAsync(
        string archivePath,
        string internalPath,
        CancellationToken cancellationToken = default);
}
```

### 5.3 `ExtractionResult`

**File**: `src/ZipDrive.Domain/Models/ExtractionResult.cs`

```csharp
/// <summary>
/// Result of extracting a single archive entry.
/// Wraps the decompressed stream alongside metadata and resource cleanup.
///
/// IMPORTANT: This type does NOT own the Stream for disposal purposes.
/// CacheFactoryResult.DisposeAsync() already disposes its Value (the Stream),
/// then calls OnDisposed. If ExtractionResult also disposed the Stream,
/// we'd get a double-dispose. Instead, ExtractionResult.OnDisposed only
/// cleans up format-specific resources (file handles, archive instances)
/// that outlive the stream.
/// </summary>
public sealed class ExtractionResult
{
    /// <summary>Decompressed data stream.</summary>
    public required Stream Stream { get; init; }

    /// <summary>Uncompressed size in bytes (used for cache tier routing).</summary>
    public required long SizeBytes { get; init; }

    /// <summary>
    /// Cleanup callback for format-specific resources (file handles, archive instances).
    /// Called by the consuming code (FileContentCache) via CacheFactoryResult.OnDisposed
    /// AFTER the stream has been consumed and disposed by the storage strategy.
    /// Null if no cleanup is needed (e.g., the stream is a self-contained MemoryStream).
    /// </summary>
    public Func<ValueTask>? OnDisposed { get; init; }
}
```

### 5.4 `IPrefetchStrategy`

**File**: `src/ZipDrive.Domain/Abstractions/IPrefetchStrategy.cs`

```csharp
/// <summary>
/// Format-specific prefetch strategy. Different archive formats have fundamentally
/// different optimization strategies:
///
/// - ZIP: Contiguous byte-span read (entries are independently accessible at known offsets)
/// - RAR: Solid-block pre-decompression (entries within a block must be decompressed sequentially)
/// - TAR: No prefetch benefit (already sequential)
///
/// Implementations live in the format's infrastructure project.
/// Returns null from IFormatRegistry if the format has no prefetch optimization.
/// </summary>
public interface IPrefetchStrategy
{
    /// <summary>Format identifier.</summary>
    string FormatId { get; }

    /// <summary>
    /// Performs format-specific prefetch for sibling entries in a directory.
    /// Called fire-and-forget by ZipVirtualFileSystem on cache miss or directory listing.
    /// </summary>
    Task PrefetchAsync(
        string archivePath,
        ArchiveStructure structure,
        string dirInternalPath,
        ArchiveEntryInfo? triggerEntry,
        IFileContentCache contentCache,
        PrefetchOptions options,
        CancellationToken cancellationToken = default);
}
```

### 5.5 `IFormatRegistry`

**File**: `src/ZipDrive.Domain/Abstractions/IFormatRegistry.cs`

```csharp
/// <summary>
/// Central registry for archive format providers. Resolves providers by format ID
/// and detects format from file path/extension.
/// Implementation collects all registered IArchiveStructureBuilder, IArchiveEntryExtractor,
/// and IPrefetchStrategy via DI (IEnumerable<T>) and indexes by FormatId.
/// </summary>
public interface IFormatRegistry
{
    IArchiveStructureBuilder GetStructureBuilder(string formatId);
    IArchiveEntryExtractor GetExtractor(string formatId);

    /// <summary>Returns null if the format has no prefetch optimization.</summary>
    IPrefetchStrategy? GetPrefetchStrategy(string formatId);

    /// <summary>
    /// Detects the format of an archive file by extension (and optionally magic bytes).
    /// Returns null if the file is not a recognized archive format.
    /// </summary>
    string? DetectFormat(string filePath);

    /// <summary>
    /// All file extensions supported across all registered providers (e.g., [".zip", ".rar"]).
    /// Used by ArchiveDiscovery and FileSystemWatcher.
    /// </summary>
    IReadOnlyList<string> SupportedExtensions { get; }

    /// <summary>
    /// Notifies all providers that an archive has been removed (dynamic reload / invalidation).
    /// Providers with format-specific metadata stores (e.g., ZipFormatMetadataStore) clean up here.
    /// </summary>
    void OnArchiveRemoved(string archiveKey);
}
```

### 5.6 Modified: `IFileContentCache`

```diff
  Task<int> ReadAsync(
      string archivePath,
+     string formatId,
-     ZipEntryInfo entry,
+     ArchiveEntryInfo entry,
+     string internalPath,
      string cacheKey,
      byte[] buffer,
      long offset,
      CancellationToken cancellationToken = default);

  Task WarmAsync(
-     ZipEntryInfo entry,
+     ArchiveEntryInfo entry,
      string cacheKey,
      Stream decompressedStream,
      CancellationToken cancellationToken = default);
```

### 5.7 Modified: `IArchiveStructureCache`

```diff
  Task<ArchiveStructure> GetOrBuildAsync(
      string archiveKey,
      string absolutePath,
+     string formatId,
      CancellationToken cancellationToken = default);
```

---

## 6. ZIP Provider Extraction

All ZIP-specific logic is extracted from the caching layer into `Infrastructure.Archives.Zip`.

### 6.1 `ZipStructureBuilder`

**File**: `src/ZipDrive.Infrastructure.Archives.Zip/ZipStructureBuilder.cs`

Contains the ~300 lines currently in `ArchiveStructureCache.BuildStructureAsync`:

```
Dependencies: IZipReaderFactory, IFilenameEncodingDetector, ZipFormatMetadataStore, ILogger
```

**Build pipeline** (unchanged logic, relocated):
1. Create `IZipReader`, call `ReadEocdAsync()` → `ZipEocd`
2. Stream `ZipCentralDirectoryEntry` via `StreamCentralDirectoryAsync(eocd)`
3. Partition by UTF-8 flag, detect encoding for non-UTF-8 entries
4. Convert `ZipCentralDirectoryEntry` → `ArchiveEntryInfo` (format-agnostic)
5. **Side effect**: Store `ZipCentralDirectoryEntry` → `ZipEntryInfo` mapping in `ZipFormatMetadataStore` (keyed by archivePath + internalPath)
6. Synthesize parent directories
7. Return `ArchiveStructure` with `FormatId = "zip"`

**Conversion mapping** (ZipCentralDirectoryEntry → ArchiveEntryInfo):

| ZipCentralDirectoryEntry / ZipEntryInfo | ArchiveEntryInfo | Destination of removed fields |
|---|---|---|
| `LocalHeaderOffset` | *(dropped)* | `ZipFormatMetadataStore` |
| `CompressedSize` | *(dropped)* | `ZipFormatMetadataStore` |
| `UncompressedSize` | `UncompressedSize` | |
| `CompressionMethod` | *(dropped)* | `ZipFormatMetadataStore` |
| `IsDirectory` | `IsDirectory` | |
| `LastModified` | `LastModified` | |
| `Attributes` | `Attributes` | |
| `Crc32` | `Checksum` | |
| `IsEncrypted` | `IsEncrypted` | |

### 6.2 `ZipFormatMetadataStore`

**File**: `src/ZipDrive.Infrastructure.Archives.Zip/ZipFormatMetadataStore.cs`

```csharp
/// <summary>
/// Stores ZIP-specific entry metadata needed for extraction and prefetch
/// that is not part of the format-agnostic ArchiveEntryInfo.
///
/// Thread-safe: concurrent reads during extraction, writes during structure building.
/// Keyed by (archivePath, internalPath). Lifetime tied to ArchiveStructureCache:
/// when an archive is invalidated, its entries are removed.
/// </summary>
internal sealed class ZipFormatMetadataStore
{
    // archivePath → (internalPath → ZipEntryInfo)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, ZipEntryInfo>> _store = new();

    /// <summary>
    /// Populates all entries for an archive. Called by ZipStructureBuilder during BuildAsync.
    /// Replaces any existing entries for the same archivePath (handles rebuild after invalidation).
    /// </summary>
    internal void Populate(string archivePath, IEnumerable<(string InternalPath, ZipEntryInfo Entry)> entries)
    {
        var dict = new ConcurrentDictionary<string, ZipEntryInfo>(StringComparer.Ordinal);
        foreach (var (path, entry) in entries)
            dict[path] = entry;
        _store[archivePath] = dict;
    }

    /// <summary>Retrieves ZIP metadata for a single entry. Throws if not found.</summary>
    internal ZipEntryInfo Get(string archivePath, string internalPath) { ... }

    /// <summary>Retrieves all entries for an archive (for prefetch). Returns null if not populated.</summary>
    internal IReadOnlyDictionary<string, ZipEntryInfo>? GetArchiveEntries(string archivePath) { ... }

    /// <summary>Removes all metadata for an archive (called on structure cache invalidation).</summary>
    internal void Remove(string archivePath) { ... }
}
```

**Lifetime management**: The store must be cleaned up when an archive's structure is invalidated (dynamic reload) or evicted (TTL/LRU).

**Concrete wiring**: Add `OnArchiveRemoved(string archiveKey)` to `IFormatRegistry`. The VFS calls it when `RemoveArchiveAsync` runs (right after calling `StructureCache.Invalidate` and `FileContentCache.RemoveArchive`). The registry fans out to all providers. The ZIP provider's handler calls `ZipFormatMetadataStore.Remove(archiveKey)`. The RAR provider's handler is a no-op (no format metadata store).

```csharp
// In IFormatRegistry:
void OnArchiveRemoved(string archiveKey);

// In FormatRegistry implementation:
public void OnArchiveRemoved(string archiveKey)
{
    foreach (var builder in _builders.Values)
        if (builder is IArchiveMetadataCleanup cleanup)
            cleanup.CleanupArchive(archiveKey);
}

// Optional interface for providers that need cleanup:
public interface IArchiveMetadataCleanup
{
    void CleanupArchive(string archiveKey);
}
```

`ZipStructureBuilder` implements `IArchiveMetadataCleanup` and calls `_metadataStore.Remove(archiveKey)`. Providers without format metadata stores ignore this.

**Alternative considered**: Store ZIP metadata inside `ArchiveEntryInfo` as an opaque `long ExtractionKey`. Rejected because:
1. One long can't encode `LocalHeaderOffset` + `CompressedSize` + `CompressionMethod`
2. Still requires a side-channel store for the remaining fields
3. Pollutes Domain with format-specific extraction semantics

### 6.3 `ZipEntryExtractor`

**File**: `src/ZipDrive.Infrastructure.Archives.Zip/ZipEntryExtractor.cs`

```csharp
public sealed class ZipEntryExtractor : IArchiveEntryExtractor
{
    public string FormatId => "zip";

    private readonly IZipReaderFactory _readerFactory;
    private readonly ZipFormatMetadataStore _metadataStore;

    public async Task<ExtractionResult> ExtractAsync(
        string archivePath, string internalPath, CancellationToken ct)
    {
        ZipEntryInfo zipEntry = _metadataStore.Get(archivePath, internalPath);
        IZipReader reader = _readerFactory.Create(archivePath);
        try
        {
            Stream decompressedStream = await reader.OpenEntryStreamAsync(zipEntry, ct);
            return new ExtractionResult
            {
                Stream = decompressedStream,
                SizeBytes = zipEntry.UncompressedSize,
                OnDisposed = async () => await reader.DisposeAsync()
            };
        }
        catch
        {
            await reader.DisposeAsync();
            throw;
        }
    }
}
```

This is essentially the same factory delegate currently in `FileContentCache.ReadAsync`, extracted into a dedicated class.

### 6.4 `ZipPrefetchStrategy`

**File**: `src/ZipDrive.Infrastructure.Archives.Zip/ZipPrefetchStrategy.cs`

Contains the current prefetch code from `ZipVirtualFileSystem.PrefetchSiblingsAsync` (lines 479-681). Key components relocated:

- `SpanSelector` (from `Application/Services/`) — sorts by `LocalHeaderOffset` from `ZipFormatMetadataStore`
- `PrefetchPlan` (from `Application/Services/`) — holds `IReadOnlyList<ZipEntryInfo>` + span bounds
- Sequential-span-read logic with ZIP local header parsing
- Inline Store/Deflate decompression
- `IFileContentCache.WarmAsync` calls for decompressed entries

```csharp
public sealed class ZipPrefetchStrategy : IPrefetchStrategy
{
    public string FormatId => "zip";

    private readonly ZipFormatMetadataStore _metadataStore;
    private readonly ILogger<ZipPrefetchStrategy> _logger;

    public async Task PrefetchAsync(
        string archivePath,
        ArchiveStructure structure,
        string dirInternalPath,
        ArchiveEntryInfo? triggerEntry,
        IFileContentCache contentCache,
        PrefetchOptions options,
        CancellationToken ct)
    {
        // 1. Get ZIP metadata for all entries in the directory
        var zipEntries = _metadataStore.GetArchiveEntries(archivePath);
        if (zipEntries == null) return;

        // 2. Build candidate list from structure + ZIP metadata
        // 3. Run SpanSelector (now internal to this project)
        // 4. Sequential span read with decompression
        // 5. WarmAsync each decompressed entry into contentCache

        // ... (relocated from ZipVirtualFileSystem.PrefetchSiblingsAsync)
    }
}
```

The `SpanSelector` and `PrefetchPlan` classes move to this project because:
- `SpanSelector` sorts by `LocalHeaderOffset` and computes fill ratio using `CompressedSize` — both ZIP-specific fields
- `PrefetchPlan` holds `IReadOnlyList<ZipEntryInfo>` — ZIP-specific type
- The "contiguous byte span" concept is inherently a ZIP optimization

---

## 7. RAR Provider

### 7.1 New Project: `ZipDrive.Infrastructure.Archives.Rar`

```
Project references: ZipDrive.Domain
NuGet dependency: SharpCompress (latest stable, MIT license, pure managed C#, zero native deps)
```

**Why SharpCompress**: RAR is a proprietary format with no public compression specification — hand-writing a reader is not feasible. SharpCompress is the only pure-managed .NET RAR library (317M+ NuGet downloads, MIT license, targets net10.0, single-file publish compatible). Alternatives (`SevenZipExtractor`, `unrar.dll`) all require native DLLs that break single-file publish.

### 7.2 SharpCompress RAR API Model

SharpCompress exposes `RarArchive` (random access) which supports:
- Entry listing via `Entries` collection
- Single-entry extraction via `entry.OpenEntryStream()`
- Async API with `CancellationToken` support

**Solid RAR archives are NOT supported.** SharpCompress's `RarArchive.OpenEntryStream()` throws `InvalidFormatException` on solid archives. Solid RAR requires sequential decompression of all preceding entries — this is a fundamental limitation of the format, not practical for a virtual filesystem. See [Section 7.5](#75-solid-archive-ux--three-layer-feedback) for the graceful degradation UX (renamed folder + warning file + optional hide).

### 7.3 Binary Signature Detection (Zero Library Dependency)

Both format detection and solid-archive probing use raw binary header parsing — no SharpCompress instantiation needed.

**RAR5 signature** (`52 61 72 21 1A 07 01 00`, 8 bytes):
```
Offset 0-7:  RAR5 magic signature (8 bytes)
Offset 8+:   Main Archive Header (VINT-encoded)
              → Header CRC (VINT)
              → Header Size (VINT)
              → Header Type (VINT, == 1 for Main Archive)
              → Header Flags (VINT)
                 Bit 0 (0x0001): Solid archive flag
```

**RAR4 signature** (`52 61 72 21 1A 07 00`, 7 bytes):
```
Offset 0-6:  RAR4 magic signature (7 bytes)
Offset 7-13: Marker block (7 bytes, skip)
Offset 14:   Main header start
              → HEAD_CRC (2 bytes)
              → HEAD_TYPE (1 byte, == 0x73 for Main Archive)
              → HEAD_FLAGS (2 bytes, little-endian)
                 Bit 3 (0x0008): Solid archive flag
              → HEAD_SIZE (2 bytes)
```

**Implementation in `RarStructureBuilder`**:

```csharp
internal static class RarSignature
{
    private static ReadOnlySpan<byte> Rar5Magic => [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00];
    private static ReadOnlySpan<byte> Rar4Magic => [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00];

    /// <summary>
    /// Detects RAR format version from first 8 bytes. Returns 0 if not RAR.
    /// </summary>
    internal static int DetectVersion(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 8 && header[..8].SequenceEqual(Rar5Magic)) return 5;
        if (header.Length >= 7 && header[..7].SequenceEqual(Rar4Magic)) return 4;
        return 0;
    }

    /// <summary>
    /// Detects if RAR archive is solid by reading the main header flags.
    /// Reads at most 64 bytes from the stream. Does NOT instantiate SharpCompress.
    /// </summary>
    internal static async Task<bool> IsSolidAsync(string filePath, CancellationToken ct)
    {
        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read,
            FileShare.Read, bufferSize: 64, useAsync: true);
        byte[] buf = new byte[64];
        int read = await fs.ReadAsync(buf, ct);
        if (read < 14) return false;

        int version = DetectVersion(buf);
        return version switch
        {
            5 => IsSolidRar5(buf.AsSpan(8, read - 8)),
            4 => IsSolidRar4(buf.AsSpan(14, read - 14)),
            _ => false
        };
    }

    private static bool IsSolidRar5(ReadOnlySpan<byte> afterSignature)
    {
        // Parse VINT fields: CRC, Size, Type, then Flags
        int offset = 0;
        offset += ReadVInt(afterSignature[offset..], out _); // Header CRC
        offset += ReadVInt(afterSignature[offset..], out _); // Header Size
        offset += ReadVInt(afterSignature[offset..], out _); // Header Type
        offset += ReadVInt(afterSignature[offset..], out long flags); // Flags
        return (flags & 0x0001) != 0; // Bit 0 = solid
    }

    private static bool IsSolidRar4(ReadOnlySpan<byte> mainHeader)
    {
        // HEAD_CRC(2) + HEAD_TYPE(1) + HEAD_FLAGS(2)
        if (mainHeader.Length < 5) return false;
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(mainHeader[3..5]);
        return (flags & 0x0008) != 0; // Bit 3 = solid
    }

    /// <summary>Reads a RAR5 VINT (variable-length integer). Returns bytes consumed.</summary>
    private static int ReadVInt(ReadOnlySpan<byte> data, out long value) { /* ... */ }
}
```

**Usage in `ProbeAsync`** (< 0.1ms, reads 64 bytes max):
```csharp
public async Task<ArchiveProbeResult> ProbeAsync(string absolutePath, CancellationToken ct)
{
    bool isSolid = await RarSignature.IsSolidAsync(absolutePath, ct);
    return isSolid
        ? new ArchiveProbeResult(false, "Solid RAR archives are not supported")
        : new ArchiveProbeResult(true);
}
```

**Usage in `IFormatRegistry.DetectFormat`** (replaces extension-only detection):
```csharp
public string? DetectFormat(string filePath)
{
    // Fast path: check extension first
    string ext = Path.GetExtension(filePath);
    if (_extensionToFormat.TryGetValue(ext, out var fmt)) return fmt;

    // Fallback: read 8 bytes for magic-byte detection (handles renamed files)
    Span<byte> header = stackalloc byte[8];
    using var fs = File.OpenRead(filePath);
    int read = fs.Read(header);
    if (read >= 8 && RarSignature.DetectVersion(header) > 0) return "rar";
    // ... other format checks ...
    return null;
}
```

### 7.4 `RarStructureBuilder`

**File**: `src/ZipDrive.Infrastructure.Archives.Rar/RarStructureBuilder.cs`

```csharp
public sealed class RarStructureBuilder : IArchiveStructureBuilder
{
    public string FormatId => "rar";
    public IReadOnlyList<string> SupportedExtensions => [".rar"];

    internal const string UnsupportedWarningFileName = "NOT_SUPPORTED_WARNING.txt";
    internal const string UnsupportedFolderSuffix = " (NOT SUPPORTED)";

    internal static readonly byte[] SolidWarningContent = Encoding.UTF8.GetBytes(
"""
This RAR archive uses solid compression and cannot be mounted by ZipDrive.

Solid RAR archives compress all files as a single continuous data stream.
Extracting any single file requires decompressing every preceding file first,
which makes random-access reads impractical for a virtual filesystem.

To access this archive's contents through ZipDrive, re-create it without
solid compression:

    WinRAR:  Uncheck "Create solid archive" in compression settings
    CLI:     rar a -s- output.rar input_files/

Alternatively, extract the archive manually with WinRAR or 7-Zip.

To hide unsupported archives from the virtual drive entirely, set:

    "Mount": {
        "HideUnsupportedArchives": true
    }

in appsettings.jsonc and restart ZipDrive.
""");

    public async Task<ArchiveStructure> BuildAsync(
        string archiveKey, string absolutePath, CancellationToken ct)
    {
        // Solid detection already done by ProbeAsync (binary, < 0.1ms) before trie
        // registration. BuildAsync is only called for non-solid archives.
        // Defense-in-depth: if somehow called for solid, build warning structure.
        if (await RarSignature.IsSolidAsync(absolutePath, ct))
        {
            _logger.LogWarning("Solid RAR archive reached BuildAsync (should have been caught by ProbeAsync): {Path}", absolutePath);
            return BuildSolidWarningStructure(archiveKey + UnsupportedFolderSuffix, absolutePath);
        }

        using var archive = RarArchive.Open(absolutePath);
        var trie = new TrieDictionary<ArchiveEntryInfo>();
        long totalUncompressed = 0;
        long totalCompressed = 0;

        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            string key = NormalizePath(entry.Key);
            trie[key] = new ArchiveEntryInfo
            {
                UncompressedSize = entry.Size,
                IsDirectory = entry.IsDirectory,
                LastModified = entry.LastModifiedTime ?? DateTime.MinValue,
                Attributes = entry.IsDirectory
                    ? FileAttributes.Directory | FileAttributes.ReadOnly
                    : FileAttributes.ReadOnly,
                Checksum = entry.Crc,
            };
            totalUncompressed += entry.Size;
            totalCompressed += entry.CompressedSize;
        }

        SynthesizeParentDirectories(trie);

        return new ArchiveStructure
        {
            ArchiveKey = archiveKey,
            AbsolutePath = absolutePath,
            Entries = trie,
            FormatId = "rar",
            BuiltAt = DateTimeOffset.UtcNow,
            TotalUncompressedSize = totalUncompressed,
            TotalCompressedSize = totalCompressed,
            EstimatedMemoryBytes = BaseOverhead + trie.Count * BytesPerEntry,
        };
    }

    /// <summary>
    /// Builds a minimal structure for solid archives: one warning text file
    /// so the user sees the archive folder in Explorer and understands why
    /// the contents are not available.
    /// </summary>
    private static ArchiveStructure BuildSolidWarningStructure(
        string archiveKey, string absolutePath)
    {
        var trie = new TrieDictionary<ArchiveEntryInfo>
        {
            [UnsupportedWarningFileName] = new ArchiveEntryInfo
            {
                UncompressedSize = SolidWarningContent.Length,
                IsDirectory = false,
                LastModified = DateTime.UtcNow,
                Attributes = FileAttributes.ReadOnly,
            }
        };

        return new ArchiveStructure
        {
            ArchiveKey = archiveKey,
            AbsolutePath = absolutePath,
            Entries = trie,
            FormatId = "rar",
            BuiltAt = DateTimeOffset.UtcNow,
            TotalUncompressedSize = SolidWarningContent.Length,
            TotalCompressedSize = 0,
            EstimatedMemoryBytes = BaseOverhead + BytesPerEntry,
        };
    }
}
```

### 7.5 Solid Archive UX — Three-Layer Feedback

**Layer 1 — Folder name**: The archive folder is renamed with a `(NOT SUPPORTED)` suffix. The user sees `solid.rar (NOT SUPPORTED)` in Explorer without clicking into it.

**Layer 2 — Warning file**: If the user clicks in, they find `NOT_SUPPORTED_WARNING.txt` explaining:
- Why solid compression is incompatible with virtual filesystems
- How to re-create the archive without solid compression (WinRAR + CLI commands)
- How to hide unsupported archives via `Mount:HideUnsupportedArchives` config

**Layer 3 — Config to hide**: `MountSettings.HideUnsupportedArchives` (default: `false`). When `true`, unsupported archives are excluded from discovery entirely. A `LogWarning` is emitted for each filtered archive:

```
"solid-archive.rar filtered (unsupported solid RAR). Set Mount:HideUnsupportedArchives=false to show."
```

```
R:\
├── normal.rar\                         ← works normally
│   ├── photo.jpg
│   └── doc.pdf
├── solid.rar (NOT SUPPORTED)\          ← renamed, user sees immediately
│   └── NOT_SUPPORTED_WARNING.txt       ← explanation + config hint
└── another.zip\                        ← ZIP unaffected
    └── ...
```

**Implementation flow**:

The current VFS registers archives in the trie BEFORE building structures (trie registration at `AddArchiveAsync`, structure building is lazy on first access). This means the trie key must be correct at registration time, before we know if the archive is solid.

**Solution**: Two-pass discovery for RAR archives.

1. `ArchiveDiscovery` discovers `solid.rar`, creates `ArchiveDescriptor { FormatId = "rar" }`
2. **New step**: VFS calls `IArchiveStructureBuilder.ProbeAsync(absolutePath)` — a lightweight check that opens the archive, reads the `IsSolid` flag, and returns a `ProbeResult { IsSupported, Reason? }`. This is fast (no full structure build).
3. Based on `ProbeResult` and `MountSettings.HideUnsupportedArchives`:
   - **Supported**: Register with original `VirtualPath` in trie. Normal flow.
   - **Unsupported + HideUnsupportedArchives=false**: Mutate `ArchiveDescriptor.VirtualPath` to `"solid.rar (NOT SUPPORTED)"` before trie registration. Structure builder returns the warning-only structure.
   - **Unsupported + HideUnsupportedArchives=true**: Skip registration entirely. Log: `"solid.rar filtered (unsupported solid RAR). Set Mount:HideUnsupportedArchives=false to show."`
4. Dokan shows the correctly named folder (or nothing if hidden).

**New method on `IArchiveStructureBuilder`**:
```csharp
/// <summary>
/// Lightweight probe to check if the archive is supported without full structure parsing.
/// Used during discovery to detect unsupported variants (e.g., solid RAR) early,
/// before trie registration.
/// </summary>
Task<ArchiveProbeResult> ProbeAsync(string absolutePath, CancellationToken ct = default);
```

```csharp
public sealed record ArchiveProbeResult(bool IsSupported, string? UnsupportedReason = null);
```

- ZIP builder: always returns `IsSupported = true` (no unsupported variants).
- RAR builder: opens `RarArchive`, checks `IsSolid`, returns result. ~1ms per archive (just header read).

**Who checks `HideUnsupportedArchives`**: The VFS's `AddArchiveAsync` (or a new `RegisterDiscoveredArchivesAsync` method). It has access to `MountSettings` and calls `ProbeAsync` before trie registration. This keeps the config check in the Application layer where it belongs — builders and extractors do not depend on `MountSettings`.

**Parent directory synthesis**: Shared with ZIP builder. Extract to a `DirectorySynthesizer` static helper in Domain (identical logic, ~30 lines).

### 7.6 `RarEntryExtractor`

**File**: `src/ZipDrive.Infrastructure.Archives.Rar/RarEntryExtractor.cs`

Handles two cases: real RAR entries (non-solid) and the synthetic warning file (solid).

```csharp
public sealed class RarEntryExtractor : IArchiveEntryExtractor
{
    public string FormatId => "rar";

    public async Task<ExtractionResult> ExtractAsync(
        string archivePath, string internalPath, CancellationToken ct)
    {
        // Synthetic warning file for solid archives — serve static content, no archive I/O
        if (internalPath == RarStructureBuilder.UnsupportedWarningFileName)
        {
            return new ExtractionResult
            {
                Stream = new MemoryStream(RarStructureBuilder.SolidWarningContent, writable: false),
                SizeBytes = RarStructureBuilder.SolidWarningContent.Length,
            };
        }

        // Normal non-solid extraction
        var archive = RarArchive.Open(archivePath);
        try
        {
            var entry = archive.Entries
                .FirstOrDefault(e => NormalizePath(e.Key) == internalPath && !e.IsDirectory)
                ?? throw new FileNotFoundException($"Entry not found in RAR: {internalPath}");

            var ms = new MemoryStream((int)entry.Size);
            using (var entryStream = entry.OpenEntryStream())
                await entryStream.CopyToAsync(ms, ct).ConfigureAwait(false);
            ms.Position = 0;

            return new ExtractionResult
            {
                Stream = ms,
                SizeBytes = entry.Size,
                OnDisposed = () => { archive.Dispose(); return ValueTask.CompletedTask; }
            };
        }
        catch { archive.Dispose(); throw; }
    }
}
```

**Warning file path**: The extractor recognizes `NOT_SUPPORTED_WARNING.txt` by exact name match against `RarStructureBuilder.UnsupportedWarningFileName`. No archive I/O — returns a `MemoryStream` wrapping the static `byte[]`. Zero-cost, cacheable.

**Non-solid extraction performance**: Comparable to ZIP — `RarArchive` seeks to the entry and decompresses only that entry. Each `ExtractAsync` call creates a fresh `RarArchive` instance (own file handle), matching ZipDrive's existing pattern of one `IZipReader` per extraction.

### 7.7 RAR Prefetch

No `IPrefetchStrategy` for RAR in V1. `IFormatRegistry.GetPrefetchStrategy("rar")` returns `null`.

ZIP prefetch works by reading a contiguous byte span (entries are laid out sequentially with known offsets). RAR has no equivalent public structure — SharpCompress doesn't expose entry byte offsets. Future optimization would require profiling whether `RarArchive.Open()` per entry is a bottleneck.

### 7.8 Thread Safety

SharpCompress is **not thread-safe**. Concurrent access to a single `RarArchive` instance causes data corruption.

This is fine for ZipDrive because:
- `FileContentCache` uses `Lazy<Task<T>>` per cache key — only one extraction per entry at a time
- Each `ExtractAsync` call creates its own `RarArchive` instance (own file handle)
- No shared state between concurrent extractions
- Same pattern as the current ZIP reader (one `IZipReader` per extraction)

---

## 8. Caching Layer Refactor

### 8.1 `ArchiveStructureCache` — Thin Delegation

**Before** (300 lines of ZIP parsing inline):
```csharp
private readonly IZipReaderFactory _zipReaderFactory;
private readonly IFilenameEncodingDetector _encodingDetector;

// BuildStructureAsync: EOCD reading, CD streaming, encoding detection, parent synthesis
```

**After** (~15 lines of delegation):
```csharp
private readonly IFormatRegistry _formatRegistry;

private async Task<CacheFactoryResult<ArchiveStructure>> BuildStructureAsync(
    string archiveKey, string absolutePath, string formatId, CancellationToken ct)
{
    IArchiveStructureBuilder builder = _formatRegistry.GetStructureBuilder(formatId);
    ArchiveStructure structure = await builder.BuildAsync(archiveKey, absolutePath, ct)
        .ConfigureAwait(false);

    long sizeBytes = structure.EstimatedMemoryBytes;

    return new CacheFactoryResult<ArchiveStructure>
    {
        Value = structure,
        SizeBytes = sizeBytes,
    };
}
```

**Removed constructor parameters**: `IZipReaderFactory`, `IFilenameEncodingDetector`
**Added constructor parameter**: `IFormatRegistry`

### 8.2 `FileContentCache` — Format-Agnostic Extraction

**Before**:
```csharp
private readonly IZipReaderFactory _zipReaderFactory;

// Factory creates IZipReader, calls OpenEntryStreamAsync(ZipEntryInfo)
Func<CancellationToken, Task<CacheFactoryResult<Stream>>> factory = async ct =>
{
    IZipReader reader = _zipReaderFactory.Create(archivePath);
    Stream decompressedStream = await reader.OpenEntryStreamAsync(entry, ct);
    return new CacheFactoryResult<Stream>
    {
        Value = decompressedStream,
        SizeBytes = entry.UncompressedSize,
        OnDisposed = async () => await reader.DisposeAsync()
    };
};
```

**After**:
```csharp
private readonly IFormatRegistry _formatRegistry;

Func<CancellationToken, Task<CacheFactoryResult<Stream>>> factory = async ct =>
{
    IArchiveEntryExtractor extractor = _formatRegistry.GetExtractor(formatId);
    ExtractionResult result = await extractor.ExtractAsync(archivePath, internalPath, ct);
    return new CacheFactoryResult<Stream>
    {
        Value = result.Stream,           // CacheFactoryResult owns Stream disposal
        SizeBytes = result.SizeBytes,
        OnDisposed = result.OnDisposed   // chains format resource cleanup AFTER stream disposal
    };
};
```

**Disposal chain**: `CacheFactoryResult.DisposeAsync()` → disposes `Value` (the Stream) → calls `OnDisposed` (the extractor's resource cleanup). `ExtractionResult` is NOT `IAsyncDisposable` — it is a data transfer object. This prevents double-dispose of the Stream.

**Removed constructor parameter**: `IZipReaderFactory`
**Added constructor parameter**: `IFormatRegistry`

**Signature change**:
```csharp
public async Task<int> ReadAsync(
    string archivePath,
    string formatId,            // NEW — which provider to use on miss
    ArchiveEntryInfo entry,     // CHANGED — from ZipEntryInfo
    string internalPath,        // NEW — entry path for extractor
    string cacheKey,
    byte[] buffer,
    long offset,
    CancellationToken cancellationToken = default)
```

**Why `internalPath` is a separate parameter** (not derived from `cacheKey`): The cache key format `"archiveVirtualPath:internalPath"` is a convention, not a contract. Parsing it is fragile. The caller (VFS) already has the internal path — pass it explicitly.

### 8.3 Project Reference Removal

**`ZipDrive.Infrastructure.Caching.csproj`**:
```diff
  <ItemGroup>
    <ProjectReference Include="..\ZipDrive.Domain\ZipDrive.Domain.csproj" />
-   <ProjectReference Include="..\ZipDrive.Infrastructure.Archives.Zip\ZipDrive.Infrastructure.Archives.Zip.csproj" />
  </ItemGroup>
```

This is the critical change. After this, adding a new format requires:
1. A new `Infrastructure.Archives.X` project (depends on Domain only)
2. Implementing `IArchiveStructureBuilder` + `IArchiveEntryExtractor`
3. Registering in DI
4. No changes to Caching, VFS, or any existing format provider

---

## 9. Application Layer Refactor

### 9.1 `ZipVirtualFileSystem` — Prefetch Delegation

**Before** (200+ lines of ZIP-specific code):
```csharp
private async Task PrefetchSiblingsAsync(
    ArchiveDescriptor archive, string dirInternalPath,
    ZipEntryInfo? triggerEntry, CancellationToken ct)
{
    // ZIP local header parsing, SpanSelector, Deflate decompression...
    // 200 lines of format-specific code
}
```

**After** (~10 lines):
```csharp
private async Task PrefetchSiblingsAsync(
    ArchiveDescriptor archive, string dirInternalPath,
    ArchiveEntryInfo? triggerEntry, CancellationToken ct)
{
    IPrefetchStrategy? strategy = _formatRegistry.GetPrefetchStrategy(archive.FormatId);
    if (strategy == null) return;

    ArchiveStructure structure = await _structureCache.GetOrBuildAsync(
        archive.VirtualPath, archive.PhysicalPath, archive.FormatId, ct)
        .ConfigureAwait(false);

    await strategy.PrefetchAsync(
        archive.PhysicalPath, structure, dirInternalPath,
        triggerEntry, _fileContentCache, _prefetchOptions, ct)
        .ConfigureAwait(false);
}
```

### 9.2 `ZipVirtualFileSystem` — Entry Type Migration

All methods update `ZipEntryInfo` → `ArchiveEntryInfo`:
- `GetFileInfoAsync`: `structure.GetEntry(internalPath)` returns `ArchiveEntryInfo?`
- `ReadFileAsync`: passes `ArchiveEntryInfo` to `_fileContentCache.ReadAsync()`
- `ListDirectoryAsync`: `ListDirectory()` yields `(string, ArchiveEntryInfo)`

### 9.3 `ArchiveDiscovery` — Multi-Format

**Before**:
```csharp
foreach (string filePath in Directory.EnumerateFiles(currentPath, "*.zip"))
```

**After**:
```csharp
private readonly IFormatRegistry _formatRegistry;

// In ScanDirectory:
foreach (string ext in _formatRegistry.SupportedExtensions)
{
    foreach (string filePath in Directory.EnumerateFiles(currentPath, $"*{ext}"))
    {
        string? formatId = _formatRegistry.DetectFormat(filePath);
        if (formatId == null) continue;

        results.Add(new ArchiveDescriptor
        {
            VirtualPath = virtualPath,
            PhysicalPath = filePath,
            SizeBytes = fileInfo.Length,
            LastModifiedUtc = fileInfo.LastWriteTimeUtc,
            FormatId = formatId,
        });
    }
}
```

### 9.4 `ArchiveDiscovery.DescribeFile` — Used by Dynamic Reload

`DescribeFile` is called by `DokanHostedService` during dynamic reload when a new archive file appears. It must also set `FormatId`:

```csharp
public ArchiveDescriptor? DescribeFile(string rootPath, string filePath)
{
    string? formatId = _formatRegistry.DetectFormat(filePath);
    if (formatId == null) return null;   // not a supported archive format

    // ... existing FileInfo logic ...
    return new ArchiveDescriptor
    {
        VirtualPath = virtualPath,
        PhysicalPath = Path.GetFullPath(filePath),
        SizeBytes = fileInfo.Length,
        LastModifiedUtc = fileInfo.LastWriteTimeUtc,
        FormatId = formatId,
    };
}
```

### 9.5 Moved to `Infrastructure.Archives.Zip`

| Class | From | To | Reason |
|-------|------|----|--------|
| `SpanSelector` | `Application/Services/` | `Archives.Zip/` | Sorts by `LocalHeaderOffset`, uses `CompressedSize` — ZIP-specific |
| `PrefetchPlan` | `Application/Services/` | `Archives.Zip/` | Holds `IReadOnlyList<ZipEntryInfo>`, span byte offsets — ZIP-specific |

---

## 10. Dynamic Reload — Multi-Format

### 10.1 `DokanHostedService.StartWatcher`

**Before**:
```csharp
_watcher = new FileSystemWatcher(_mountSettings.ArchiveDirectory, "*.zip")
```

**After**:
```csharp
// FileSystemWatcher only supports one filter pattern. Use *.* and filter in handlers.
_watcher = new FileSystemWatcher(_mountSettings.ArchiveDirectory)
{
    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
    IncludeSubdirectories = true,
    EnableRaisingEvents = true
};
```

### 10.2 `IsZipExtension` → `IsSupportedArchive`

```csharp
private readonly IFormatRegistry _formatRegistry;

private bool IsSupportedArchive(string path)
{
    string ext = Path.GetExtension(path);
    return _formatRegistry.SupportedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
}
```

Filter in event handlers (`OnCreated`, `OnDeleted`, `OnRenamed`) before enqueuing to `ArchiveChangeConsolidator`.

### 10.3 `DescribeFile` Update

`ArchiveDiscovery.DescribeFile` also needs `IFormatRegistry` to set `FormatId` on the descriptor.

---

## 11. DI Wiring

### 11.1 `FormatRegistry` Implementation

**File**: `src/ZipDrive.Application/Services/FormatRegistry.cs`

Lives in Application (not Cli) so test projects can reference it without pulling in the entire host. Application already depends on Domain (where the interfaces live) and has no format-specific dependencies. The registry receives providers via constructor injection — it does not reference any Archives.* project.

```csharp
public sealed class FormatRegistry : IFormatRegistry
{
    private readonly Dictionary<string, IArchiveStructureBuilder> _builders;
    private readonly Dictionary<string, IArchiveEntryExtractor> _extractors;
    private readonly Dictionary<string, IPrefetchStrategy> _prefetchers;
    private readonly Dictionary<string, string> _extensionToFormat;   // ".zip" → "zip"
    private readonly List<string> _extensions;

    public FormatRegistry(
        IEnumerable<IArchiveStructureBuilder> builders,
        IEnumerable<IArchiveEntryExtractor> extractors,
        IEnumerable<IPrefetchStrategy> prefetchers)
    {
        _builders = builders.ToDictionary(b => b.FormatId, StringComparer.OrdinalIgnoreCase);
        _extractors = extractors.ToDictionary(e => e.FormatId, StringComparer.OrdinalIgnoreCase);
        _prefetchers = prefetchers.ToDictionary(p => p.FormatId, StringComparer.OrdinalIgnoreCase);

        _extensionToFormat = new(StringComparer.OrdinalIgnoreCase);
        foreach (var builder in builders)
            foreach (var ext in builder.SupportedExtensions)
                _extensionToFormat[ext] = builder.FormatId;

        _extensions = [.. _extensionToFormat.Keys];
    }

    public IArchiveStructureBuilder GetStructureBuilder(string formatId) =>
        _builders.TryGetValue(formatId, out var b) ? b
        : throw new NotSupportedException($"No structure builder for format: {formatId}");

    public IArchiveEntryExtractor GetExtractor(string formatId) =>
        _extractors.TryGetValue(formatId, out var e) ? e
        : throw new NotSupportedException($"No entry extractor for format: {formatId}");

    public IPrefetchStrategy? GetPrefetchStrategy(string formatId) =>
        _prefetchers.TryGetValue(formatId, out var p) ? p : null;

    public string? DetectFormat(string filePath) =>
        _extensionToFormat.TryGetValue(Path.GetExtension(filePath), out var fmt) ? fmt : null;

    public IReadOnlyList<string> SupportedExtensions => _extensions;
}
```

### 11.2 `Program.cs` Service Registration

```csharp
// --- ZIP format provider ---
services.AddSingleton<IZipReaderFactory, ZipReaderFactory>();
services.AddSingleton<IFilenameEncodingDetector, FilenameEncodingDetector>();
services.AddSingleton<ZipFormatMetadataStore>();
services.AddSingleton<IArchiveStructureBuilder, ZipStructureBuilder>();
services.AddSingleton<IArchiveEntryExtractor, ZipEntryExtractor>();
services.AddSingleton<IPrefetchStrategy, ZipPrefetchStrategy>();

// --- RAR format provider ---
services.AddSingleton<IArchiveStructureBuilder, RarStructureBuilder>();
services.AddSingleton<IArchiveEntryExtractor, RarEntryExtractor>();
// No IPrefetchStrategy for RAR (solid blocks — no contiguous prefetch)

// --- Format registry (collects all providers via IEnumerable<T>) ---
services.AddSingleton<IFormatRegistry, FormatRegistry>();

// --- Caching (format-agnostic — NO reference to any Archives.* project) ---
services.AddSingleton<IArchiveStructureCache, ArchiveStructureCache>();
services.AddSingleton<IFileContentCache, FileContentCache>();

// --- VFS ---
services.AddSingleton<ZipVirtualFileSystem>();
services.AddSingleton<IVirtualFileSystem>(sp => sp.GetRequiredService<ZipVirtualFileSystem>());
services.AddSingleton<IArchiveManager>(sp => sp.GetRequiredService<ZipVirtualFileSystem>());
```

**Note**: The `ZipVirtualFileSystem` class name is now a misnomer. Consider renaming to `VirtualFileSystem` or `ArchiveVirtualFileSystem` in this refactor since it's no longer ZIP-specific.

---

## 12. Performance Analysis

### 12.1 Cache Hit Path (Zero Regression)

```
BEFORE:                                  AFTER:
entry.UncompressedSize < cutoff   →     entry.UncompressedSize < cutoff   (same field)
cache.BorrowAsync(key)            →     cache.BorrowAsync(key)            (same call)
ConcurrentDict.TryGetValue        →     ConcurrentDict.TryGetValue        (same)
stream.Position = offset           →     stream.Position = offset           (same)
stream.ReadAsync(buffer)           →     stream.ReadAsync(buffer)           (same)
```

The hot path never touches format-specific types. Changing `ZipEntryInfo` to `ArchiveEntryInfo` is a compile-time change with zero runtime impact on the cache hit path.

### 12.2 Cache Miss Path (One Extra Virtual Dispatch)

```
BEFORE:                                          AFTER:
_zipReaderFactory.Create(path)            →     _registry.GetExtractor(formatId)      (+1 dict lookup)
reader.OpenEntryStreamAsync(entry, ct)    →     extractor.ExtractAsync(path, name, ct) (+1 virtual call)
                                                  ↳ internally: _metadataStore.Get()   (+1 dict lookup)
                                                  ↳ internally: reader.OpenEntryStreamAsync(zipEntry, ct)
```

Additional overhead on cache miss: **2 dictionary lookups + 1 virtual dispatch**. This is ~100ns total — negligible compared to the millisecond-scale I/O of archive extraction.

### 12.3 Prefetch (Identical for ZIP)

The ZIP prefetch code is relocated, not changed. The `SpanSelector` algorithm, sequential span read, and inline decompression are identical. The only difference is that `ZipPrefetchStrategy` reads `ZipEntryInfo` from `ZipFormatMetadataStore` instead of from `ArchiveStructure.Entries` — same data, different access path.

### 12.4 Structure Building (Same)

The ZIP structure building logic moves from `ArchiveStructureCache.BuildStructureAsync` to `ZipStructureBuilder.BuildAsync`. Same code, same performance. The `ArchiveStructureCache` adds one virtual dispatch to call the builder.

### 12.5 Memory Overhead

`ArchiveEntryInfo` is smaller than `ZipEntryInfo` (fewer fields). Per entry:
- `ZipEntryInfo`: ~40 bytes (9 fields including `long` offsets)
- `ArchiveEntryInfo`: ~28 bytes (6 fields, no `long` offsets)
- `ZipFormatMetadataStore` entry: ~40 bytes (same as `ZipEntryInfo`, stored separately)

Net overhead for ZIP: ~28 bytes additional per entry (stored in both domain trie and metadata store). For a 10,000-file archive: ~280 KB. Acceptable.

For RAR: Only `ArchiveEntryInfo` (28 bytes per entry) + no metadata store overhead. Actually lower than the current ZIP overhead.

---

## 13. Migration Strategy

### 13.1 `ZipEntryInfo` Lifecycle

The migration must be incremental — each commit compiles and tests pass. Phase numbering matches Section 15.

```
Phase 1:  Domain Foundation (additive — ArchiveEntryInfo, new interfaces, FormatId fields)
Phase 2:  ZIP Provider Extraction (additive — ZipStructureBuilder, ZipEntryExtractor, etc.)
Phase 3:  FormatRegistry (additive — implementation + tests)
Phase 4:  ArchiveStructure Migration (BREAKING — TrieDictionary<ZipEntryInfo> → <ArchiveEntryInfo>, ~25 test files)
Phase 5:  Caching Layer Migration (BREAKING — FileContentCache/ArchiveStructureCache use IFormatRegistry)
Phase 6:  VFS Migration (BREAKING — prefetch delegation, multi-format ArchiveDiscovery)
Phase 7:  Dependency Break (remove Caching → Archives.Zip project reference)
Phase 8:  Dynamic Reload (multi-format FileSystemWatcher)
Phase 9:  RAR Provider (new project, SharpCompress, tests)
Phase 10: Cleanup (ZipEntryInfo → internal, rename ZipVirtualFileSystem, delete unused interfaces)
```

### 13.2 Parallel Development

Phases 3 (ZIP provider) and 9 (RAR provider) can be developed in parallel since they're independent new code.

### 13.3 Branch Strategy

Recommend a single feature branch for Phases 1-8 (architecture refactor), then a second branch for Phase 9 (RAR support). The refactor PR will be large but is primarily mechanical type changes + code relocation.

---

## 14. Test Strategy

### 14.1 Existing Test Impact

**Total affected test files: ~25** (broken down below).

| Test Project | Files Affected | Nature of Change |
|-------------|----------------|------------------|
| `ZipDrive.Domain.Tests` | 4 files: `ZipVirtualFileSystemTests`, `ArchiveDiscoveryTests`, `SpanSelectorTests`, `PrefetchIntegrationTests` | `ZipEntryInfo` → `ArchiveEntryInfo`. SpanSelector tests move to Archives.Zip.Tests. |
| `ZipDrive.Infrastructure.Caching.Tests` | 6 files: `FileContentCacheTests`, `FileContentCacheRemoveArchiveTests`, `ChunkedExtractionIntegrationTests`, `GenericCacheIntegrationTests`, `GenericCacheTryRemoveTests`, `PerProcessCacheDirectoryTests` | Mock `IArchiveEntryExtractor` instead of `IZipReaderFactory`. Signature updates. |
| `ZipDrive.Infrastructure.Archives.Zip.Tests` | 3 files: `ZipReaderTests`, `EncodingIntegrationTests`, `FilenameEncodingDetectorTests` | Minimal — these test ZIP internals. `ZipEntryInfo` stays internal. |
| `ZipDrive.EnduranceTests` | 10 files: `EnduranceTest` + 8 suites + `PartialSampleTests` | DI setup, entry type changes, `FileContentCache.ReadAsync` signature. RAR fixtures required (not optional). |
| `ZipDrive.TestHelpers` | 1 file: `VfsTestFixture` | Entry type + DI changes. |
| `ZipDrive.Benchmarks` | 2 files: `ArchiveStructureBenchmarks`, `EncodingDetectionBenchmarks` | Entry type changes. |

### 14.2 New Tests

| Component | Test Count (est.) | Focus |
|-----------|-------------------|-------|
| `ZipStructureBuilder` | 5-8 | Builds correct ArchiveEntryInfo + populates ZipFormatMetadataStore |
| `ZipEntryExtractor` | 3-5 | Extracts via metadata store, resource cleanup |
| `ZipPrefetchStrategy` | 5-8 | Relocated SpanSelector tests + integration |
| `RarStructureBuilder` | 5-8 | Builds correct ArchiveEntryInfo from RAR files |
| `RarEntryExtractor` | 3-5 | Extracts from non-solid RAR, warning file for solid |
| `FormatRegistry` | 3-5 | Resolution, detection, missing format error |
| `ArchiveDiscovery` multi-format | 2-3 | Discovers both .zip and .rar |
| Solid RAR UX (integration) | 3-4 | Renamed folder appears in trie, `NOT_SUPPORTED_WARNING.txt` readable, `HideUnsupportedArchives` filters correctly |
| `ZipFormatMetadataStore` lifecycle | 2-3 | Entries removed on archive invalidation via `OnArchiveRemoved` |

### 14.3 Test Data

- Existing ZIP fixtures are reused unchanged
- New RAR test fixtures (required):
  - Non-solid RAR5 with mixed file types (primary extraction tests)
  - Non-solid RAR4 (backward compatibility)
  - Solid RAR5 (for solid detection / warning UX tests)
  - Directory-heavy RAR (parent directory synthesis)
- Generate with WinRAR or `rar` CLI: `rar a -m3 test.rar files/` (non-solid), `rar a -s -m3 solid.rar files/` (solid)

---

## 15. Implementation Phases

Each phase produces a compiling, test-passing codebase.

| # | Phase | Description | Files Changed | Risk |
|---|-------|-------------|---------------|------|
| 1 | **Domain Foundation** | Add `ArchiveEntryInfo`, `ExtractionResult`, new interfaces (`IArchiveStructureBuilder`, `IArchiveEntryExtractor`, `IPrefetchStrategy`, `IFormatRegistry`). Add `FormatId` to `ArchiveDescriptor` + `ArchiveStructure`. All additive. | ~8 new files in Domain | **Low** |
| 2 | **ZIP Provider Extraction** | Create `ZipStructureBuilder`, `ZipFormatMetadataStore`, `ZipEntryExtractor`, `ZipPrefetchStrategy` in Archives.Zip. Move `SpanSelector` + `PrefetchPlan`. New code, existing code untouched. | ~6 new/moved files in Archives.Zip | **Low** |
| 3 | **FormatRegistry** | Create `FormatRegistry` implementation. Unit tests. | ~2 new files | **Low** |
| 4 | **ArchiveStructure Migration** | Change `TrieDictionary<ZipEntryInfo>` → `TrieDictionary<ArchiveEntryInfo>`. Update `GetEntry`, `ListDirectory`. This touches many test files. | ~20 files (model + tests) | **High** |
| 5 | **Caching Layer Migration** | Refactor `ArchiveStructureCache` + `FileContentCache` to use `IFormatRegistry`. Remove `IZipReaderFactory` deps. Update `IFileContentCache` signatures. | ~8 files (impl + tests) | **Medium** |
| 6 | **VFS Migration** | Update `ZipVirtualFileSystem` to use `ArchiveEntryInfo`, delegate prefetch to `IPrefetchStrategy`, pass `formatId`. Multi-format `ArchiveDiscovery`. | ~5 files | **Medium** |
| 7 | **Dependency Break** | Remove `<ProjectReference>` from Caching `.csproj`. Verify clean compilation. | 1 file | **Low** (should just work) |
| 8 | **Dynamic Reload** | Update `DokanHostedService` to support multi-format file watching. | ~2 files | **Low** |
| 9 | **RAR Provider** | Create `ZipDrive.Infrastructure.Archives.Rar` project with `RarStructureBuilder`, `RarEntryExtractor`. Add SharpCompress dependency. Wire in DI. | ~5 new files + `.csproj` | **Low** |
| 10 | **Cleanup** | Move `ZipEntryInfo` to Archives.Zip as internal. Rename `ZipVirtualFileSystem` → `ArchiveVirtualFileSystem`. Update CLAUDE.md. | ~10 files | **Low** |

---

## 16. Risks and Mitigations

### 16.1 `ZipFormatMetadataStore` Lifetime

**Risk**: Metadata store grows unbounded if archives are added but never invalidated.
**Mitigation**: `IFormatRegistry.OnArchiveRemoved(archiveKey)` (see Section 5.5 and 6.2) is called by the VFS during `RemoveArchiveAsync`, right after `StructureCache.Invalidate()` and `FileContentCache.RemoveArchive()`. The registry fans out to providers via `IArchiveMetadataCleanup`. The ZIP provider calls `ZipFormatMetadataStore.Remove()`. Concrete wiring, no hand-waving.

### 16.2 Solid RAR Archives

**Risk**: Users with solid RAR archives cannot access contents.
**Mitigation**: Three-layer feedback (see Section 7.5):
1. Folder renamed to `name.rar (NOT SUPPORTED)` — visible at a glance without clicking in
2. `NOT_SUPPORTED_WARNING.txt` inside with explanation, remediation commands, and config hint
3. `Mount:HideUnsupportedArchives=true` to hide entirely (with `LogWarning` per filtered archive)

This avoids both silent failure (user confused why archive is missing) and noisy failure (error logs on every Dokan callback).

### 16.3 SharpCompress Reliability

**Risk**: SharpCompress may have bugs or limitations with certain RAR archives.
**Mitigation**: SharpCompress is the most widely used .NET RAR library (317M+ NuGet downloads, MIT, actively maintained). Test with RAR4 + RAR5 non-solid archives. RAR5 encrypted archive support has an open issue (#372) — we already don't support encrypted archives. Document known limitations.

### 16.4 Large Migration PR

**Risk**: Phase 4 (ArchiveStructure migration) touches 20+ files simultaneously.
**Mitigation**: The changes are mechanical (type rename + field rename). Can be done with find-and-replace. Each file change is small. Thorough test pass after each sub-step.

### 16.5 FileSystemWatcher `*.*` Filter

**Risk**: Broader filter increases event noise (non-archive files trigger events).
**Mitigation**: `IsSupportedArchive()` check in event handlers before enqueueing to consolidator. The consolidator already handles deduplication.

### 16.6 Thread Safety of ZipFormatMetadataStore

**Risk**: Concurrent reads during extraction while writes happen during structure rebuild.
**Mitigation**: Uses `ConcurrentDictionary` at both levels. `Populate()` replaces the inner dict atomically via reference assignment. Readers on the old dict continue safely.

---

## Appendix A: Deleted/Moved Files Summary

| File | Action | Destination |
|------|--------|-------------|
| `Domain/Models/ZipEntryInfo.cs` | Move → internal | `Archives.Zip/ZipEntryInfo.cs` |
| `Application/Services/SpanSelector.cs` | Move | `Archives.Zip/SpanSelector.cs` |
| `Application/Services/PrefetchPlan.cs` | Move | `Archives.Zip/PrefetchPlan.cs` |

## Appendix B: New Files Summary

| File | Project | Purpose |
|------|---------|---------|
| `Domain/Models/ArchiveEntryInfo.cs` | Domain | Format-agnostic entry metadata |
| `Domain/Models/ExtractionResult.cs` | Domain | Extraction result with cleanup |
| `Domain/Abstractions/IArchiveStructureBuilder.cs` | Domain | Format-specific structure parsing |
| `Domain/Abstractions/IArchiveEntryExtractor.cs` | Domain | Format-specific entry extraction |
| `Domain/Abstractions/IPrefetchStrategy.cs` | Domain | Format-specific prefetch |
| `Domain/Abstractions/IFormatRegistry.cs` | Domain | Provider resolution |
| `Archives.Zip/ZipStructureBuilder.cs` | Archives.Zip | Relocated structure parsing |
| `Archives.Zip/ZipFormatMetadataStore.cs` | Archives.Zip | ZIP-specific metadata storage |
| `Archives.Zip/ZipEntryExtractor.cs` | Archives.Zip | ZIP extraction via metadata store |
| `Archives.Zip/ZipPrefetchStrategy.cs` | Archives.Zip | Relocated prefetch logic |
| `Archives.Rar/RarStructureBuilder.cs` | Archives.Rar (NEW) | RAR structure parsing |
| `Archives.Rar/RarEntryExtractor.cs` | Archives.Rar (NEW) | RAR entry extraction |
| `Cli/FormatRegistry.cs` | Cli | Provider registry implementation |

## Appendix C: `NuGet` Changes

| Package | Version | Project | Purpose |
|---------|---------|---------|---------|
| `SharpCompress` | latest stable | `Archives.Rar` | RAR4/RAR5 reading |
