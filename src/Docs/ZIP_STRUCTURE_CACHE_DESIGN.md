# ZIP Structure Cache Design Document

**Version:** 1.1
**Last Updated:** 2026-01-18
**Status:** ✅ Implemented

---

## Executive Summary

This document describes the **ZIP Structure Cache** - a memory-efficient caching layer that stores parsed ZIP archive metadata (Central Directory) to enable fast file lookups and single-seek streaming extraction.

**Key Goals:**
1. **Fast path resolution**: Archive prefix tree for O(log n) path lookups
2. **Minimal memory footprint**: Store only Central Directory data (~114 bytes per entry)
3. **Single-seek extraction**: Linear read from local header through compressed data
4. **Multi-archive support**: Multiple ZIPs mounted simultaneously

**Implementation Status:**
- ✅ `IArchiveStructureCache` interface defined
- ✅ `ArchiveStructureCache` implementation complete
- ✅ Integration with `GenericCache<ArchiveStructure>` via `ObjectStorageStrategy`
- ✅ Streaming Central Directory parsing via `IZipReader.StreamCentralDirectoryAsync()`
- ✅ `IArchivePrefixTree` implemented as `ArchiveTrie` (multi-archive support)

---

## 1. Problem Statement

### 1.1 The Path Resolution Challenge

When DokanNet receives a file operation like `ReadFile("R:\archive.zip\folder\file.txt")`, we need to:

1. **Identify the archive**: Which ZIP file contains this path?
2. **Locate the entry**: Where is `folder/file.txt` within that ZIP?
3. **Extract efficiently**: Read the file data with minimal seeks

Without a structure cache, each operation would require:
- Parsing the ZIP's Central Directory (at end of file)
- Scanning all entries to find the target file
- This is O(n) per operation - unacceptable for large archives

### 1.2 The Extraction Challenge

ZIP files are not random-access friendly:

```
ZIP File Layout:
┌─────────────────────────────────────────────────────────────────────┐
│ [Local Header 1][Data 1][Local Header 2][Data 2]...[Central Dir][EOCD] │
└─────────────────────────────────────────────────────────────────────┘
```

**Central Directory** (at end) contains:
- All file metadata (names, sizes, compression methods)
- **Offset to each Local Header** (not directly to data)

**Local Header** (before each file's data) contains:
- Repeated metadata + variable-length fields
- Compressed data **immediately follows**

To read a file, we need both pieces of information.

---

## 2. Solution Architecture

### 2.1 Two-Level Cache Structure

```
┌─────────────────────────────────────────────────────────────────────┐
│                    Archive Prefix Tree                               │
│                    (Path → Archive Mapping)                          │
│                                                                     │
│  R:\                                                                │
│  ├── archive1.zip\ ──→ ArchiveStructureCache (archive1.zip)         │
│  ├── archive2.zip\ ──→ ArchiveStructureCache (archive2.zip)         │
│  └── data.zip\     ──→ ArchiveStructureCache (data.zip)             │
└─────────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────────┐
│                  Archive Structure Cache                             │
│              (Per-Archive Entry Metadata)                            │
│                                                                     │
│  archive1.zip:                                                      │
│  ├── folder/                                                        │
│  │   ├── file1.txt ──→ ZipEntryInfo { offset, sizes, method }       │
│  │   └── file2.pdf ──→ ZipEntryInfo { offset, sizes, method }       │
│  └── readme.md     ──→ ZipEntryInfo { offset, sizes, method }       │
└─────────────────────────────────────────────────────────────────────┘
```

### 2.2 Component Overview

```
┌─────────────────────────────────────────────────────────────────────┐
│                     IArchivePrefixTree                               │
│  - RegisterArchive(archiveKey, absolutePath)                        │
│  - UnregisterArchive(archiveKey)                                    │
│  - Resolve(virtualPath) → (archiveKey, internalPath)?               │
└─────────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────────┐
│                  IArchiveStructureCache                              │
│  - GetOrBuildAsync(archiveKey, absolutePath) → ArchiveStructure     │
│  - Invalidate(archiveKey)                                           │
│  - Eviction: LRU/LFU + TTL (access extends TTL)                     │
└─────────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────────┐
│                     ArchiveStructure                                 │
│  - ArchiveKey: string                                               │
│  - AbsolutePath: string (physical ZIP file location)                │
│  - Entries: Dictionary<string, ZipEntryInfo>                        │
│  - DirectoryTree: nested structure for directory listing            │
└─────────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────────┐
│                       ZipEntryInfo                                   │
│  (Minimal metadata for streaming extraction)                        │
│                                                                     │
│  - LocalHeaderOffset: long     (seek position)                      │
│  - CompressedSize: long        (bytes to read)                      │
│  - UncompressedSize: long      (output buffer size)                 │
│  - CompressionMethod: ushort   (0=Store, 8=Deflate)                 │
│  - IsDirectory: bool                                                │
│  - LastModified: DateTime                                           │
└─────────────────────────────────────────────────────────────────────┘
```

---

## 3. Data Flow

### 3.1 Path Resolution Flow

```
1. DokanNet: ReadFile("R:\archive.zip\folder\file.txt", offset=50000)
   ↓
2. IArchivePrefixTree.Resolve("\\archive.zip\\folder\\file.txt")
   ├── Match prefix: "archive.zip" → Found in tree
   ├── archiveKey = "archive.zip"
   └── internalPath = "folder/file.txt"
   ↓
3. IArchiveStructureCache.GetOrBuildAsync("archive.zip", absolutePath)
   ├── Cache HIT? → Return cached ArchiveStructure
   └── Cache MISS? → Parse Central Directory, build structure, cache it
   ↓
4. ArchiveStructure.Entries["folder/file.txt"]
   └── Returns ZipEntryInfo { LocalHeaderOffset=12345, CompressedSize=8000, ... }
   ↓
5. Proceed to file content extraction (see below)
```

### 3.2 Streaming Extraction Flow

```
Given: ZipEntryInfo { LocalHeaderOffset=12345, CompressedSize=8000, CompressionMethod=8 }

1. Open FileStream to ZIP file (or get from pool)
   ↓
2. Seek to LocalHeaderOffset (12345)
   ↓
3. Stream read Local Header (30 bytes fixed):
   ┌────────────────────────────────────────────────────────────────┐
   │ Signature(4) | Version(2) | Flags(2) | Method(2) | Time(2) |  │
   │ Date(2) | CRC32(4) | CompSize(4) | UncompSize(4) |            │
   │ FileNameLen(2) | ExtraLen(2)                                  │
   └────────────────────────────────────────────────────────────────┘
   ↓
4. Continue stream read: fileName (FileNameLen bytes) + extra (ExtraLen bytes)
   └── These bytes are "overhead" but part of same linear read (~100 bytes)
   ↓
5. Now positioned at compressed data start
   ↓
6. Stream read CompressedSize bytes → Decompress (Deflate/Store)
   ↓
7. Pass decompressed stream to file content cache for materialization
```

**Key Insight:** Steps 2-6 are ONE linear read operation:
```
Seek(12345) → Read(30 + FileNameLen + ExtraLen + CompressedSize)
```

No additional seeks required. The Local Header overhead is negligible.

---

## 4. Detailed Component Design

### 4.1 ZipEntryInfo (Minimal Metadata)

```csharp
/// <summary>
/// Minimal metadata for a ZIP entry, parsed from Central Directory.
/// Designed for memory efficiency - stores only what's needed for extraction.
/// </summary>
public readonly record struct ZipEntryInfo
{
    /// <summary>
    /// Absolute byte offset to the Local File Header in the ZIP file.
    /// Used as seek position for extraction.
    /// </summary>
    public required long LocalHeaderOffset { get; init; }

    /// <summary>
    /// Size of compressed data in bytes.
    /// Used to know how many bytes to read after Local Header.
    /// </summary>
    public required long CompressedSize { get; init; }

    /// <summary>
    /// Size of uncompressed data in bytes.
    /// Used for output buffer allocation and validation.
    /// </summary>
    public required long UncompressedSize { get; init; }

    /// <summary>
    /// ZIP compression method.
    /// 0 = Store (no compression), 8 = Deflate.
    /// </summary>
    public required ushort CompressionMethod { get; init; }

    /// <summary>
    /// True if this entry represents a directory.
    /// </summary>
    public required bool IsDirectory { get; init; }

    /// <summary>
    /// Last modification timestamp.
    /// </summary>
    public required DateTime LastModified { get; init; }

    /// <summary>
    /// File attributes (for Windows Explorer display).
    /// </summary>
    public required FileAttributes Attributes { get; init; }
}
```

**Memory footprint per entry:** ~40 bytes (struct, no heap allocation for entry itself)

For a ZIP with 10,000 files: ~400 KB structure cache (plus file name strings)

### 4.2 ArchiveStructure

```csharp
/// <summary>
/// Cached structure of a single ZIP archive.
/// Built by parsing the Central Directory once, then cached.
/// </summary>
public sealed class ArchiveStructure
{
    /// <summary>
    /// Unique key identifying this archive (e.g., "archive.zip").
    /// </summary>
    public required string ArchiveKey { get; init; }

    /// <summary>
    /// Absolute filesystem path to the ZIP file.
    /// </summary>
    public required string AbsolutePath { get; init; }

    /// <summary>
    /// Flat dictionary of all entries, keyed by internal path.
    /// Path uses forward slashes, no leading slash: "folder/file.txt"
    /// </summary>
    public required IReadOnlyDictionary<string, ZipEntryInfo> Entries { get; init; }

    /// <summary>
    /// Hierarchical tree structure for directory listing.
    /// </summary>
    public required DirectoryNode RootDirectory { get; init; }

    /// <summary>
    /// Total number of entries in the archive.
    /// </summary>
    public int EntryCount => Entries.Count;

    /// <summary>
    /// Timestamp when this structure was built.
    /// Used for cache invalidation if ZIP file changes.
    /// </summary>
    public required DateTimeOffset BuiltAt { get; init; }

    /// <summary>
    /// Last access timestamp (updated on any access).
    /// Used for LRU eviction.
    /// </summary>
    public DateTimeOffset LastAccessedAt { get; set; }

    /// <summary>
    /// Access count for LFU eviction.
    /// </summary>
    public int AccessCount { get; set; }
}

/// <summary>
/// Tree node for directory structure (used for FindFiles operations).
/// </summary>
public sealed class DirectoryNode
{
    public string Name { get; init; } = "";
    public bool IsDirectory => true;
    public Dictionary<string, DirectoryNode> Subdirectories { get; } = new();
    public Dictionary<string, ZipEntryInfo> Files { get; } = new();
}
```

### 4.3 IArchiveStructureCache Interface

```csharp
/// <summary>
/// Cache for parsed ZIP archive structures.
/// Implements LRU/LFU eviction with TTL-based expiration.
/// </summary>
public interface IArchiveStructureCache
{
    /// <summary>
    /// Gets the cached structure or builds it by parsing the ZIP.
    /// Any access extends the TTL and updates LRU/LFU metrics.
    /// </summary>
    /// <param name="archiveKey">Unique archive identifier.</param>
    /// <param name="absolutePath">Filesystem path to the ZIP file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The archive structure with entry metadata.</returns>
    Task<ArchiveStructure> GetOrBuildAsync(
        string archiveKey,
        string absolutePath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Invalidates a cached structure (e.g., if ZIP file changed).
    /// </summary>
    void Invalidate(string archiveKey);

    /// <summary>
    /// Manually evicts expired entries.
    /// </summary>
    void EvictExpired();

    /// <summary>
    /// Current number of cached archive structures.
    /// </summary>
    int CachedArchiveCount { get; }

    /// <summary>
    /// Estimated memory usage in bytes.
    /// </summary>
    long EstimatedMemoryBytes { get; }
}
```

### 4.4 IArchivePrefixTree Interface

```csharp
/// <summary>
/// Prefix tree for mapping virtual paths to archives.
/// Supports multiple mounted archives under a single mount point.
/// </summary>
public interface IArchivePrefixTree
{
    /// <summary>
    /// Registers an archive in the prefix tree.
    /// </summary>
    /// <param name="archiveKey">Virtual name (e.g., "archive.zip").</param>
    /// <param name="absolutePath">Filesystem path to the ZIP file.</param>
    void RegisterArchive(string archiveKey, string absolutePath);

    /// <summary>
    /// Unregisters an archive from the prefix tree.
    /// </summary>
    void UnregisterArchive(string archiveKey);

    /// <summary>
    /// Resolves a virtual path to archive key and internal path.
    /// </summary>
    /// <param name="virtualPath">Path from DokanNet (e.g., "\\archive.zip\\folder\\file.txt").</param>
    /// <returns>
    /// Tuple of (archiveKey, absolutePath, internalPath) if matched,
    /// or null if path doesn't match any registered archive.
    /// </returns>
    (string ArchiveKey, string AbsolutePath, string InternalPath)? Resolve(string virtualPath);

    /// <summary>
    /// Lists all registered archive keys (for root directory listing).
    /// </summary>
    IEnumerable<string> ListArchives();
}
```

---

## 5. Cache Eviction Strategy

### 5.1 Hybrid LRU/LFU + TTL

The structure cache uses a hybrid eviction strategy:

```
┌─────────────────────────────────────────────────────────────────────┐
│                    Eviction Strategy                                 │
├─────────────────────────────────────────────────────────────────────┤
│                                                                     │
│  1. TTL-Based Hard Eviction                                         │
│     - Each structure has a TTL (default: 30 minutes)                │
│     - ANY access to the structure extends TTL                       │
│     - Expired structures evicted on next cleanup cycle              │
│                                                                     │
│  2. LRU/LFU Soft Eviction (when memory pressure)                    │
│     - Track LastAccessedAt (for LRU)                                │
│     - Track AccessCount (for LFU)                                   │
│     - Evict least-used structures when capacity exceeded            │
│                                                                     │
│  3. Access Granularity                                              │
│     - Cache unit = entire archive structure                         │
│     - Any file access within archive = structure access             │
│     - Single file read extends TTL for whole archive                │
│                                                                     │
└─────────────────────────────────────────────────────────────────────┘
```

### 5.2 Access Tracking

```csharp
// When any file in an archive is accessed:
public async Task<ZipEntryInfo?> GetEntryAsync(string archiveKey, string internalPath)
{
    var structure = await _structureCache.GetOrBuildAsync(archiveKey, absolutePath);

    // Update access metrics (extends TTL, updates LRU/LFU)
    structure.LastAccessedAt = _timeProvider.GetUtcNow();
    structure.AccessCount++;

    return structure.Entries.TryGetValue(internalPath, out var entry) ? entry : null;
}
```

### 5.3 Configuration

```csharp
public class StructureCacheOptions
{
    /// <summary>
    /// Maximum number of archive structures to cache.
    /// Default: 100 archives.
    /// </summary>
    public int MaxCachedArchives { get; set; } = 100;

    /// <summary>
    /// Maximum memory for structure cache in MB.
    /// Default: 256 MB.
    /// </summary>
    public int MaxMemoryMb { get; set; } = 256;

    /// <summary>
    /// Default TTL for structure cache entries.
    /// Extended on every access.
    /// Default: 30 minutes.
    /// </summary>
    public int DefaultTtlMinutes { get; set; } = 30;

    /// <summary>
    /// Eviction policy: LRU or LFU.
    /// Default: LRU.
    /// </summary>
    public EvictionPolicy Policy { get; set; } = EvictionPolicy.LRU;
}
```

---

## 6. ZIP Parsing Strategy

### 6.1 Central Directory Parsing (Structure Build)

```
ZIP File:
┌─────────────────────────────────────────────────────────────────────┐
│ [Local Headers + Data...]        [Central Directory]    [EOCD]      │
│                                  ↑                      ↑           │
│                                  │                      │           │
│                                  │    ┌─────────────────┘           │
│                                  │    │                             │
│                                  │    ▼                             │
│                              Read EOCD (22+ bytes at end)           │
│                              Get: centralDirOffset, centralDirSize  │
│                                  │                                  │
│                                  ▼                                  │
│                              Seek to centralDirOffset               │
│                              Read Central Directory entries         │
│                              Build ArchiveStructure                 │
└─────────────────────────────────────────────────────────────────────┘

Parsing Steps:
1. Seek to end - 22 bytes (minimum EOCD size)
2. Search backwards for EOCD signature (0x06054b50)
3. Read EOCD: centralDirOffset, centralDirSize, entryCount
4. Seek to centralDirOffset
5. For each entry (entryCount times):
   a. Read Central Directory File Header (46 bytes fixed)
   b. Read fileName (fileNameLength bytes)
   c. Skip extraField and fileComment
   d. Create ZipEntryInfo with localHeaderOffset, sizes, method
   e. Add to entries dictionary and directory tree
6. Return ArchiveStructure
```

### 6.2 Supported Compression Methods

| Method | Value | Support | Notes |
|--------|-------|---------|-------|
| Store | 0 | ✅ Full | No compression, direct copy |
| Deflate | 8 | ✅ Full | Most common, use DeflateStream |
| Deflate64 | 9 | ❌ None | Rare, not in .NET BCL |
| BZip2 | 12 | ❌ None | Rare |
| LZMA | 14 | ⏳ Future | Used by 7-Zip |
| PPMd | 98 | ❌ None | Very rare |

**Design Decision:** Support Store (0) and Deflate (8) only. These cover 99%+ of real-world ZIP files.

### 6.3 Extraction Algorithm

```csharp
public async Task<Stream> ExtractEntryAsync(
    string absolutePath,
    ZipEntryInfo entry,
    CancellationToken cancellationToken)
{
    // 1. Open file stream (or get from pool)
    await using var fileStream = new FileStream(
        absolutePath,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        bufferSize: 81920,
        useAsync: true);

    // 2. Seek to local header
    fileStream.Seek(entry.LocalHeaderOffset, SeekOrigin.Begin);

    // 3. Read local header (30 bytes fixed)
    var header = new byte[30];
    await fileStream.ReadExactlyAsync(header, cancellationToken);

    // 4. Parse variable-length fields
    var fileNameLength = BitConverter.ToUInt16(header, 26);
    var extraFieldLength = BitConverter.ToUInt16(header, 28);

    // 5. Skip fileName and extraField (continue linear read)
    var skipBytes = fileNameLength + extraFieldLength;
    fileStream.Seek(skipBytes, SeekOrigin.Current);

    // 6. Now positioned at compressed data - wrap in decompression stream
    var compressedStream = new SubStream(fileStream, entry.CompressedSize);

    return entry.CompressionMethod switch
    {
        0 => compressedStream,  // Store - no decompression
        8 => new DeflateStream(compressedStream, CompressionMode.Decompress),
        _ => throw new NotSupportedException($"Compression method {entry.CompressionMethod} not supported")
    };
}
```

---

## 7. Integration with File Content Cache

The structure cache and file content cache work together:

```
┌─────────────────────────────────────────────────────────────────────┐
│                      Complete Read Flow                              │
└─────────────────────────────────────────────────────────────────────┘

1. DokanNet: ReadFile("\\archive.zip\\folder\\file.txt", offset=5000, length=4096)
   ↓
2. Prefix Tree: Resolve → archiveKey="archive.zip", internalPath="folder/file.txt"
   ↓
3. Structure Cache: GetOrBuildAsync("archive.zip")
   ├── HIT: Return cached ArchiveStructure (updates access metrics)
   └── MISS: Parse Central Directory, cache, return
   ↓
4. Lookup: structure.Entries["folder/file.txt"] → ZipEntryInfo
   ↓
5. File Content Cache: GetOrAddAsync(
       cacheKey = "archive.zip:folder/file.txt",
       sizeBytes = entry.UncompressedSize,
       ttl = 30min,
       factory = () => ExtractEntryAsync(absolutePath, entry))
   ├── HIT: Return cached random-access stream
   └── MISS: Extract via streaming, materialize, cache, return
   ↓
6. Stream: Seek(5000), Read(4096) → Return to DokanNet
```

---

## 8. Memory Estimation

### 8.1 Per-Entry Memory

| Component | Size | Notes |
|-----------|------|-------|
| ZipEntryInfo struct | ~40 bytes | Fixed overhead |
| File name string | ~50 bytes avg | Varies by path length |
| Dictionary entry | ~24 bytes | Key + value reference |
| **Total per entry** | **~114 bytes** | |

### 8.2 Example Calculations

| Archive Size | Entry Count | Structure Cache Size |
|--------------|-------------|---------------------|
| Small (100 files) | 100 | ~11 KB |
| Medium (1,000 files) | 1,000 | ~114 KB |
| Large (10,000 files) | 10,000 | ~1.1 MB |
| Huge (100,000 files) | 100,000 | ~11 MB |

With default 256 MB limit: Can cache ~20 huge archives or ~2,000 medium archives.

---

## 9. Error Handling

### 9.1 Corrupt ZIP Handling

```csharp
public async Task<ArchiveStructure> GetOrBuildAsync(...)
{
    try
    {
        return await BuildStructureAsync(absolutePath, cancellationToken);
    }
    catch (InvalidDataException ex)
    {
        _logger.LogError(ex, "Corrupt ZIP file: {Path}", absolutePath);
        throw new ArchiveCorruptException($"Cannot parse ZIP: {absolutePath}", ex);
    }
    catch (EndOfStreamException ex)
    {
        _logger.LogError(ex, "Truncated ZIP file: {Path}", absolutePath);
        throw new ArchiveCorruptException($"Truncated ZIP: {absolutePath}", ex);
    }
}
```

### 9.2 Unsupported Compression

```csharp
// During extraction
if (entry.CompressionMethod != 0 && entry.CompressionMethod != 8)
{
    throw new CompressionNotSupportedException(
        $"Compression method {entry.CompressionMethod} not supported. " +
        "Only Store (0) and Deflate (8) are supported.");
}
```

---

## 10. Future Enhancements

### 10.1 Planned

- [ ] ZIP64 support (files > 4GB, archives > 65535 entries)
- [ ] Encrypted ZIP support (ZipCrypto, AES)
- [ ] LZMA compression support
- [ ] File change detection (invalidate cache if ZIP modified)

### 10.2 Considered

- [ ] Partial structure loading (lazy parse for huge archives)
- [ ] Persistent structure cache (survive app restart)
- [ ] TAR/7Z format support (via pluggable providers)

---

## 11. Success Criteria

- [ ] Structure cache builds in < 100ms for 10,000-entry ZIP
- [ ] Path resolution < 1ms (prefix tree + dictionary lookup)
- [ ] Memory usage matches estimates (< 2 MB per 10,000 entries)
- [ ] LRU/LFU eviction works correctly
- [ ] TTL extension on access works correctly
- [ ] 80%+ test coverage

---

## 12. Related Documentation

- [CACHING_DESIGN.md](CACHING_DESIGN.md) - File content caching (materialization)
- [CONCURRENCY_STRATEGY.md](CONCURRENCY_STRATEGY.md) - Thread safety patterns
- [IMPLEMENTATION_CHECKLIST.md](IMPLEMENTATION_CHECKLIST.md) - Implementation progress
