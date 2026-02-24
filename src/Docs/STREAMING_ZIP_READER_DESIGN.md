# Streaming ZIP Reader Design for ZipDrive V3

**Status:** ✅ Implemented
**Date:** 2026-01-18
**Author:** Claude Code

---

## Executive Summary

This document describes a **proprietary streaming ZIP reader** that processes ZIP archives without loading the entire Central Directory into memory. The key innovation is using `IAsyncEnumerable<ZipCentralDirectoryEntry>` to yield entries one-by-one during parsing, enabling memory-efficient processing of archives with millions of entries.

**Key Goals:**
1. **Streaming Central Directory parsing** - process entries one-by-one, NOT loading all into memory ✅
2. **Efficient file extraction** - single seek to Local Header, linear read through compressed data ✅
3. **ZIP64 support** - handle large files (>4GB) and large archives (>65535 entries) transparently ✅
4. **Integration with existing cache infrastructure** - uses `GenericCache<ArchiveStructure>` with `ObjectStorageStrategy` ✅

**Implementation Status:**
- All core components implemented and tested
- 15 unit tests passing
- Integrated with existing caching infrastructure

---

## 1. Architecture Overview

### 1.1 Component Hierarchy

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                           IZipReader (Low-Level)                             │
│  Purpose: Raw ZIP format parsing with streaming enumeration                  │
│                                                                             │
│  • ReadEocdAsync() → ZipEocd                                                │
│  • StreamCentralDirectoryAsync() → IAsyncEnumerable<ZipCentralDirectoryEntry>│
│  • OpenEntryStreamAsync() → Stream (decompression)                          │
└─────────────────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                    IArchiveStructureCache (Integration)                      │
│  Purpose: Cache parsed ZIP structures using GenericCache<ArchiveStructure>   │
│                                                                             │
│  • GetOrBuildAsync() → ArchiveStructure                                     │
│  • Uses IZipReader.StreamCentralDirectoryAsync() to build incrementally     │
│  • ObjectStorageStrategy<ArchiveStructure> for caching                      │
└─────────────────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                      ZipArchiveProvider (IArchiveProvider)                   │
│  Purpose: High-level archive operations, integrates with caching             │
│                                                                             │
│  • CanOpen() → magic number detection                                       │
│  • OpenAsync() → IArchiveSession                                            │
└─────────────────────────────────────────────────────────────────────────────┘
```

### 1.2 Data Flow for Building ArchiveStructure

```
1. IArchiveStructureCache.GetOrBuildAsync("archive.zip", "/path/to/archive.zip")
   ↓
2. Cache MISS → Call factory
   ↓
3. Factory: await BuildArchiveStructureAsync(absolutePath)
   ↓
4. IZipReader.ReadEocdAsync(stream)
   └── Returns: ZipEocd { CentralDirectoryOffset, EntryCount, IsZip64 }
   ↓
5. IZipReader.StreamCentralDirectoryAsync(stream, eocd)
   └── Yields: ZipCentralDirectoryEntry one-by-one (NO bulk allocation)
   ↓
6. For each entry:
   ├── Create ZipEntryInfo (struct, ~40 bytes)
   ├── Add to Dictionary<string, ZipEntryInfo>
   └── Build DirectoryNode tree incrementally
   ↓
7. Return ArchiveStructure { Entries, RootDirectory }
   ↓
8. GenericCache stores via ObjectStorageStrategy<ArchiveStructure>
```

---

## 2. ZIP File Format Reference

### 2.1 File Layout

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                            ZIP FILE STRUCTURE                                │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                             │
│  ┌─────────────────────────────────────────────────────────────────────┐   │
│  │ LOCAL FILE HEADER 1 (30+ bytes)                                      │   │
│  │ [Signature 0x04034b50][Version][Flags][Method][Time][Date]           │   │
│  │ [CRC-32][CompSize][UncompSize][NameLen][ExtraLen][Name][Extra]       │   │
│  ├─────────────────────────────────────────────────────────────────────┤   │
│  │ COMPRESSED DATA 1 (CompressedSize bytes)                             │   │
│  ├─────────────────────────────────────────────────────────────────────┤   │
│  │ [DATA DESCRIPTOR 1] (optional, if bit 3 set)                         │   │
│  └─────────────────────────────────────────────────────────────────────┘   │
│                                                                             │
│  ... (more local headers and data) ...                                      │
│                                                                             │
│  ┌─────────────────────────────────────────────────────────────────────┐   │
│  │ CENTRAL DIRECTORY FILE HEADER 1 (46+ bytes)                          │   │
│  │ [Signature 0x02014b50][VerMadeBy][VerNeeded][Flags][Method]          │   │
│  │ [Time][Date][CRC-32][CompSize][UncompSize][NameLen][ExtraLen]        │   │
│  │ [CommentLen][DiskStart][IntAttr][ExtAttr][LocalHeaderOffset]         │   │
│  │ [Name][Extra][Comment]                                               │   │
│  ├─────────────────────────────────────────────────────────────────────┤   │
│  │ CENTRAL DIRECTORY FILE HEADER 2 ... N                                │   │
│  └─────────────────────────────────────────────────────────────────────┘   │
│                                                                             │
│  ┌─────────────────────────────────────────────────────────────────────┐   │
│  │ [ZIP64 END OF CENTRAL DIRECTORY RECORD] (if ZIP64)                   │   │
│  │ [ZIP64 END OF CENTRAL DIRECTORY LOCATOR] (if ZIP64)                  │   │
│  └─────────────────────────────────────────────────────────────────────┘   │
│                                                                             │
│  ┌─────────────────────────────────────────────────────────────────────┐   │
│  │ END OF CENTRAL DIRECTORY RECORD (22+ bytes)                          │   │
│  │ [Signature 0x06054b50][DiskNum][DiskWithCD][EntriesOnDisk]           │   │
│  │ [TotalEntries][CDSize][CDOffset][CommentLen][Comment]                │   │
│  └─────────────────────────────────────────────────────────────────────┘   │
│                                                                             │
└─────────────────────────────────────────────────────────────────────────────┘
```

### 2.2 End of Central Directory Record (EOCD)

**Signature:** `0x06054b50` (little-endian: `50 4B 05 06`)

| Offset | Size | Field | Description |
|--------|------|-------|-------------|
| 0 | 4 | Signature | `0x06054b50` |
| 4 | 2 | Disk number | Number of this disk |
| 6 | 2 | Disk with CD start | Disk where Central Directory starts |
| 8 | 2 | CD entries on disk | Central Directory entries on this disk |
| 10 | 2 | Total CD entries | Total Central Directory entries |
| 12 | 4 | CD size | Size of Central Directory in bytes |
| 16 | 4 | CD offset | Offset to Central Directory from archive start |
| 20 | 2 | Comment length | Length of ZIP file comment |
| 22 | Variable | Comment | ZIP file comment (max 65,535 bytes) |

### 2.3 Central Directory File Header

**Signature:** `0x02014b50` (little-endian: `50 4B 01 02`)

| Offset | Size | Field | Maps to ZipEntryInfo |
|--------|------|-------|---------------------|
| 0 | 4 | Signature | - |
| 4 | 2 | Version made by | (OS detection) |
| 6 | 2 | Version needed | - |
| 8 | 2 | General purpose bit flag | `IsEncrypted`, encoding |
| 10 | 2 | Compression method | `CompressionMethod` |
| 12 | 2 | Last mod time | `LastModified` |
| 14 | 2 | Last mod date | `LastModified` |
| 16 | 4 | CRC-32 | `Crc32` |
| 20 | 4 | Compressed size | `CompressedSize` |
| 24 | 4 | Uncompressed size | `UncompressedSize` |
| 28 | 2 | Filename length | - |
| 30 | 2 | Extra field length | - |
| 32 | 2 | File comment length | - |
| 34 | 2 | Disk number start | - |
| 36 | 2 | Internal file attributes | - |
| 38 | 4 | External file attributes | `Attributes`, `IsDirectory` |
| 42 | 4 | Local header offset | `LocalHeaderOffset` |
| 46 | Variable | Filename | (path in archive) |
| ... | Variable | Extra field | (ZIP64 info if needed) |
| ... | Variable | File comment | (ignored) |

### 2.4 Local File Header

**Signature:** `0x04034b50` (little-endian: `50 4B 03 04`)

| Offset | Size | Field | Description |
|--------|------|-------|-------------|
| 0 | 4 | Signature | `0x04034b50` |
| 4 | 2 | Version needed | - |
| 6 | 2 | General purpose bit flag | - |
| 8 | 2 | Compression method | - |
| 10 | 2 | Last mod time | - |
| 12 | 2 | Last mod date | - |
| 14 | 4 | CRC-32 | May be 0 if bit 3 set |
| 18 | 4 | Compressed size | May be 0 if bit 3 set |
| 22 | 4 | Uncompressed size | May be 0 if bit 3 set |
| 26 | 2 | Filename length | |
| 28 | 2 | Extra field length | |
| 30 | Variable | Filename | |
| ... | Variable | Extra field | |
| ... | Variable | **Compressed data** | Immediately follows |

### 2.5 ZIP64 Extended Information Extra Field

**Header ID:** `0x0001`

| Offset | Size | Field | Present When |
|--------|------|-------|--------------|
| 0 | 2 | Header ID | `0x0001` |
| 2 | 2 | Data size | Size of following data |
| 4 | 8 | Original size | Standard field = `0xFFFFFFFF` |
| 12 | 8 | Compressed size | Standard field = `0xFFFFFFFF` |
| 20 | 8 | Local header offset | Standard field = `0xFFFFFFFF` |
| 28 | 4 | Disk start number | Standard field = `0xFFFF` |

**Important:** Fields appear **only if** the corresponding standard field is set to the maximum value.

---

## 3. Data Structures

### 3.1 Low-Level ZIP Format Structures

```csharp
// ZipEocd - End of Central Directory
public readonly record struct ZipEocd
{
    public required long EntryCount { get; init; }
    public required long CentralDirectorySize { get; init; }
    public required long CentralDirectoryOffset { get; init; }
    public required bool IsZip64 { get; init; }
    public required long EocdPosition { get; init; }
    public string? Comment { get; init; }
}

// ZipCentralDirectoryEntry - Single CD entry
public readonly record struct ZipCentralDirectoryEntry
{
    public required ushort VersionMadeBy { get; init; }
    public required ushort VersionNeededToExtract { get; init; }
    public required ushort GeneralPurposeBitFlag { get; init; }
    public required ushort CompressionMethod { get; init; }
    public required ushort LastModFileTime { get; init; }
    public required ushort LastModFileDate { get; init; }
    public required uint Crc32 { get; init; }
    public required long CompressedSize { get; init; }
    public required long UncompressedSize { get; init; }
    public required string FileName { get; init; }
    public required long LocalHeaderOffset { get; init; }
    public required uint ExternalFileAttributes { get; init; }
}

// ZipLocalHeader - Local file header
public readonly record struct ZipLocalHeader
{
    public required ushort FileNameLength { get; init; }
    public required ushort ExtraFieldLength { get; init; }
    public required ushort CompressionMethod { get; init; }
    public required ushort GeneralPurposeBitFlag { get; init; }
    public int TotalHeaderSize => 30 + FileNameLength + ExtraFieldLength;
}
```

### 3.2 Domain Level Structures

```csharp
// ZipEntryInfo - Minimal metadata (~40 bytes struct)
public readonly record struct ZipEntryInfo
{
    public required long LocalHeaderOffset { get; init; }
    public required long CompressedSize { get; init; }
    public required long UncompressedSize { get; init; }
    public required ushort CompressionMethod { get; init; }
    public required bool IsDirectory { get; init; }
    public required DateTime LastModified { get; init; }
    public required FileAttributes Attributes { get; init; }
    public uint Crc32 { get; init; }
    public bool IsEncrypted { get; init; }
}

// ArchiveStructure - Cached archive metadata
public sealed class ArchiveStructure
{
    public required string ArchiveKey { get; init; }
    public required string AbsolutePath { get; init; }
    public required IReadOnlyDictionary<string, ZipEntryInfo> Entries { get; init; }
    public required DirectoryNode RootDirectory { get; init; }
    public required DateTimeOffset BuiltAt { get; init; }
    public bool IsZip64 { get; init; }
    public long TotalUncompressedSize { get; init; }
    public long EstimatedMemoryBytes { get; init; }
}

// DirectoryNode - Tree structure for directory listing
public sealed class DirectoryNode
{
    public string Name { get; init; } = "";
    public string FullPath { get; init; } = "";
    public Dictionary<string, DirectoryNode> Subdirectories { get; } = new();
    public Dictionary<string, ZipEntryInfo> Files { get; } = new();
}
```

---

## 4. Interface Definitions

### 4.1 IZipReader Interface

```csharp
/// <summary>
/// Low-level ZIP archive reader with streaming Central Directory enumeration.
/// </summary>
public interface IZipReader : IAsyncDisposable
{
    /// <summary>
    /// Reads and parses the End of Central Directory record.
    /// Handles both standard ZIP and ZIP64 formats transparently.
    /// </summary>
    Task<ZipEocd> ReadEocdAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams Central Directory entries one-by-one without bulk allocation.
    /// </summary>
    IAsyncEnumerable<ZipCentralDirectoryEntry> StreamCentralDirectoryAsync(
        ZipEocd eocd,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a decompression stream for extracting a single entry.
    /// </summary>
    Task<Stream> OpenEntryStreamAsync(
        ZipEntryInfo entry,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the Local Header at a given offset.
    /// </summary>
    Task<ZipLocalHeader> ReadLocalHeaderAsync(
        long localHeaderOffset,
        CancellationToken cancellationToken = default);
}
```

### 4.2 IArchiveStructureCache Interface

```csharp
/// <summary>
/// Cache for parsed ZIP archive structures.
/// </summary>
public interface IArchiveStructureCache
{
    Task<ArchiveStructure> GetOrBuildAsync(
        string archiveKey,
        string absolutePath,
        CancellationToken cancellationToken = default);

    bool Invalidate(string archiveKey);
    void EvictExpired();

    int CachedArchiveCount { get; }
    long EstimatedMemoryBytes { get; }
    double HitRate { get; }
}
```

---

## 5. Streaming Algorithm

### 5.1 EOCD Location Algorithm

```
function FindAndReadEocd(stream):
    fileSize = stream.Length

    // Minimum EOCD size is 22 bytes
    if fileSize < 22:
        throw InvalidArchiveException("File too small for ZIP")

    // Search backwards for EOCD signature
    maxSearchRange = min(65557, fileSize)  // 22 + 65535 max comment
    searchStart = fileSize - maxSearchRange

    buffer = ReadBytes(stream, searchStart, maxSearchRange)

    for i = buffer.Length - 22 downto 0:
        if ReadUInt32(buffer, i) == 0x06054b50:
            eocd = ParseEocd(buffer, i)

            if NeedsZip64(eocd):
                eocd = ReadZip64Eocd(stream, eocdPosition)

            return eocd

    throw InvalidArchiveException("EOCD signature not found")
```

### 5.2 Streaming Central Directory Enumeration

```csharp
async IAsyncEnumerable<ZipCentralDirectoryEntry> StreamCentralDirectoryAsync(
    ZipEocd eocd,
    [EnumeratorCancellation] CancellationToken ct = default)
{
    _stream.Seek(eocd.CentralDirectoryOffset, SeekOrigin.Begin);
    byte[] headerBuffer = new byte[46];  // Reuse across iterations

    for (long i = 0; i < eocd.EntryCount; i++)
    {
        ct.ThrowIfCancellationRequested();

        // Read fixed header (46 bytes)
        await _stream.ReadExactlyAsync(headerBuffer, ct);

        // Validate signature
        var signature = BinaryPrimitives.ReadUInt32LittleEndian(headerBuffer);
        if (signature != 0x02014b50)
            throw new CorruptZipException("Invalid CD entry signature");

        // Parse variable-length fields
        var fileNameLen = BinaryPrimitives.ReadUInt16LittleEndian(headerBuffer.AsSpan(28));
        var extraLen = BinaryPrimitives.ReadUInt16LittleEndian(headerBuffer.AsSpan(30));
        var commentLen = BinaryPrimitives.ReadUInt16LittleEndian(headerBuffer.AsSpan(32));

        // Read filename
        var fileNameBytes = new byte[fileNameLen];
        await _stream.ReadExactlyAsync(fileNameBytes, ct);

        // Read extra field (for ZIP64)
        byte[]? extraField = extraLen > 0 ? new byte[extraLen] : null;
        if (extraField != null)
            await _stream.ReadExactlyAsync(extraField, ct);

        // Skip comment
        if (commentLen > 0)
            _stream.Seek(commentLen, SeekOrigin.Current);

        var entry = ParseCentralDirectoryEntry(headerBuffer, fileNameBytes, extraField);

        yield return entry;  // YIELD IMMEDIATELY - no buffering!
    }
}
```

### 5.3 ZIP64 Extra Field Parsing

```csharp
ZipCentralDirectoryEntry ParseZip64Extra(ZipCentralDirectoryEntry entry, byte[] extraField)
{
    int offset = 0;

    while (offset + 4 <= extraField.Length)
    {
        ushort headerId = BinaryPrimitives.ReadUInt16LittleEndian(extraField.AsSpan(offset));
        ushort dataSize = BinaryPrimitives.ReadUInt16LittleEndian(extraField.AsSpan(offset + 2));
        offset += 4;

        if (headerId == 0x0001)  // ZIP64 extended info
        {
            int dataOffset = offset;

            // Fields appear only if corresponding 32-bit field was 0xFFFFFFFF
            if (entry.UncompressedSize == 0xFFFFFFFF && dataOffset + 8 <= offset + dataSize)
            {
                entry = entry with {
                    UncompressedSize = BinaryPrimitives.ReadInt64LittleEndian(extraField.AsSpan(dataOffset))
                };
                dataOffset += 8;
            }

            if (entry.CompressedSize == 0xFFFFFFFF && dataOffset + 8 <= offset + dataSize)
            {
                entry = entry with {
                    CompressedSize = BinaryPrimitives.ReadInt64LittleEndian(extraField.AsSpan(dataOffset))
                };
                dataOffset += 8;
            }

            if (entry.LocalHeaderOffset == 0xFFFFFFFF && dataOffset + 8 <= offset + dataSize)
            {
                entry = entry with {
                    LocalHeaderOffset = BinaryPrimitives.ReadInt64LittleEndian(extraField.AsSpan(dataOffset))
                };
            }

            break;
        }

        offset += dataSize;
    }

    return entry;
}
```

---

## 6. Memory Efficiency Analysis

### 6.1 Comparison: Streaming vs Buffered

| Archive Size | Entry Count | Streaming | Buffered (List + Dict) |
|--------------|-------------|-----------|------------------------|
| Small | 100 | ~11 KB | ~22 KB |
| Medium | 1,000 | ~114 KB | ~228 KB |
| Large | 10,000 | ~1.1 MB | ~2.2 MB |
| **Huge** | 100,000 | **~11 MB** | **~22 MB** |
| Massive | 1,000,000 | ~114 MB | ~228 MB |

**With streaming:** Only the final `Dictionary<string, ZipEntryInfo>` is allocated.
**Without streaming:** Would need `List<ZipCentralDirectoryEntry>` + dictionary copy.

### 6.2 Per-Entry Memory Breakdown

| Component | Size | Notes |
|-----------|------|-------|
| ZipEntryInfo struct | ~40 bytes | Fixed overhead |
| File name string | ~50 bytes avg | Varies by path length |
| Dictionary entry | ~24 bytes | Key + value reference |
| **Total per entry** | **~114 bytes** | |

---

## 7. Error Handling

### 7.1 Exception Hierarchy

```csharp
public class ZipException : Exception
{
    public string? FilePath { get; }
}

public class CorruptZipException : ZipException
{
    public long? CorruptionOffset { get; }
}

public class InvalidSignatureException : CorruptZipException
{
    public uint ExpectedSignature { get; }
    public uint ActualSignature { get; }
}

public class UnsupportedCompressionException : ZipException
{
    public ushort CompressionMethod { get; }
    public string EntryPath { get; }
}

public class EncryptedEntryException : ZipException
{
    public string EntryPath { get; }
}
```

### 7.2 Error Scenarios

| Error | Exception | Recovery |
|-------|-----------|----------|
| EOCD not found | `CorruptZipException` | None - not a valid ZIP |
| Invalid CD signature | `CorruptZipException` | None - archive corrupt |
| Truncated during parse | `CorruptZipException` | None - incomplete file |
| Unsupported compression | `UnsupportedCompressionException` | Return error for entry |
| Encrypted entry | `EncryptedEntryException` | Return error for entry |

---

## 8. Supported Compression Methods

| Method | Value | Support | Notes |
|--------|-------|---------|-------|
| Store | 0 | ✅ Full | No compression, direct copy |
| Deflate | 8 | ✅ Full | Most common, use `DeflateStream` |
| Deflate64 | 9 | ❌ None | Rare, not in .NET BCL |
| BZip2 | 12 | ❌ None | Rare |
| LZMA | 14 | ⏳ Future | Used by 7-Zip |
| Zstandard | 93 | ⏳ Future | Modern, efficient |

**Design Decision:** Support Store (0) and Deflate (8) only initially. These cover 99%+ of real-world ZIP files.

---

## 9. Files to Create

### New Files

| File | Purpose |
|------|---------|
| `src/ZipDriveV3.Infrastructure.Archives.Zip/Formats/ZipEocd.cs` | EOCD structure |
| `src/ZipDriveV3.Infrastructure.Archives.Zip/Formats/ZipCentralDirectoryEntry.cs` | CD entry structure |
| `src/ZipDriveV3.Infrastructure.Archives.Zip/Formats/ZipLocalHeader.cs` | Local header structure |
| `src/ZipDriveV3.Infrastructure.Archives.Zip/Formats/ZipConstants.cs` | Signatures and constants |
| `src/ZipDriveV3.Infrastructure.Archives.Zip/IZipReader.cs` | Reader interface |
| `src/ZipDriveV3.Infrastructure.Archives.Zip/ZipReader.cs` | Reader implementation |
| `src/ZipDriveV3.Infrastructure.Archives.Zip/SubStream.cs` | Bounded stream wrapper |
| `src/ZipDriveV3.Domain/Models/ZipEntryInfo.cs` | Entry info struct |
| `src/ZipDriveV3.Domain/Models/ArchiveStructure.cs` | Structure container |
| `src/ZipDriveV3.Domain/Models/DirectoryNode.cs` | Tree node for directory listing |
| `src/ZipDriveV3.Domain/Abstractions/IArchiveStructureCache.cs` | Cache interface |
| `src/ZipDriveV3.Domain/Exceptions/ZipExceptions.cs` | Exception hierarchy |
| `src/ZipDriveV3.Infrastructure.Caching/ArchiveStructureCache.cs` | Cache implementation |
| `tests/ZipDriveV3.Infrastructure.Archives.Zip.Tests/` | Test project |

---

## 10. Implementation Phases

### Phase 1: Core Data Structures ✅ Complete
1. ✅ Create ZIP format structures (`ZipEocd`, `ZipCentralDirectoryEntry`, `ZipLocalHeader`, `ZipConstants`)
2. ✅ Create domain models (`ZipEntryInfo`, `ArchiveStructure`, `DirectoryNode`)
3. ✅ Create exception hierarchy (`ZipException`, `CorruptZipException`, `EocdNotFoundException`, etc.)
4. ✅ Add helper methods for DOS date/time conversion

### Phase 2: IZipReader Implementation ✅ Complete
1. ✅ Implement `ReadEocdAsync()` with ZIP64 support
2. ✅ Implement `StreamCentralDirectoryAsync()` with `IAsyncEnumerable`
3. ✅ Implement `ReadLocalHeaderAsync()`
4. ✅ Implement `OpenEntryStreamAsync()` with Store and Deflate support
5. ✅ Create `SubStream` helper class for bounded reading

### Phase 3: Cache Integration ✅ Complete
1. ✅ Implement `ArchiveStructureCache` using `GenericCache<ArchiveStructure>`
2. ⏳ Create DI registration extensions (pending)
3. ✅ Add integration tests

### Phase 4: Testing ✅ Complete
1. ✅ Create test ZIP fixtures (programmatically generated)
2. ✅ Unit tests for parsing logic (15 tests)
3. ✅ Integration tests for cache behavior
4. ⏳ Performance benchmarks (pending)

---

## 11. Related Documentation

- [ZIP_STRUCTURE_CACHE_DESIGN.md](ZIP_STRUCTURE_CACHE_DESIGN.md) - Structure caching design
- [CACHING_DESIGN.md](CACHING_DESIGN.md) - File content caching
- [CONCURRENCY_STRATEGY.md](CONCURRENCY_STRATEGY.md) - Thread safety patterns
