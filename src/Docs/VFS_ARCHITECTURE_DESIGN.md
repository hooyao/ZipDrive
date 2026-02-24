# ZipDrive Architecture Redesign: Abstract Virtual File System

**Version:** 1.0
**Created:** 2025-01-25
**Status:** Implemented

---

## Executive Summary

This document describes the architectural redesign of ZipDrive to separate DokanNet integration from core file system logic. The key change is introducing a **platform-independent `IVirtualFileSystem` abstraction** that handles all business logic, with DokanNet serving only as a thin adapter layer.

**Key Benefits:**
- Clean separation of concerns (DokanNet adapter ~200 lines, no business logic)
- Platform-independent core (could support FUSE on Linux in future)
- Testable business logic without DokanNet dependency
- Existing caching layer remains unchanged

---

## Architecture Overview

```
┌─────────────────────────────────────────────────────────────────────────────────┐
│                              PRESENTATION LAYER                                  │
│  ┌─────────────────────────────────────────────────────────────────────────┐    │
│  │  ZipDrive.Cli (Program.cs, FsHostedService)                           │    │
│  └───────────────────────────────────┬─────────────────────────────────────┘    │
└──────────────────────────────────────┼──────────────────────────────────────────┘
                                       │ mounts via
                                       ▼
┌─────────────────────────────────────────────────────────────────────────────────┐
│                         PLATFORM ADAPTER LAYER (Windows)                         │
│  ┌─────────────────────────────────────────────────────────────────────────┐    │
│  │  ZipDrive.Infrastructure.FileSystem.Dokan                             │    │
│  │  ┌───────────────────────────────────────────────────────────────────┐  │    │
│  │  │  DokanFileSystemAdapter : IDokanOperations2                       │  │    │
│  │  │  ─────────────────────────────────────────────────────────────────│  │    │
│  │  │  • THIN ADAPTER (~200 lines, NO business logic)                   │  │    │
│  │  │  • Translates DokanNet calls → IVirtualFileSystem calls           │  │    │
│  │  │  • Converts VfsFileInfo → FileInformation                         │  │    │
│  │  │  • Converts exceptions → NtStatus codes                           │  │    │
│  │  └───────────────────────────────────┬───────────────────────────────┘  │    │
│  └──────────────────────────────────────┼──────────────────────────────────┘    │
└─────────────────────────────────────────┼───────────────────────────────────────┘
                                          │ delegates to
                                          ▼
┌─────────────────────────────────────────────────────────────────────────────────┐
│                              DOMAIN LAYER (Platform-Independent)                 │
│  ┌─────────────────────────────────────────────────────────────────────────┐    │
│  │  <<interface>> IVirtualFileSystem                                       │    │
│  │  ───────────────────────────────────────────────────────────────────────│    │
│  │  + GetFileInfoAsync(path) → VfsFileInfo?                                │    │
│  │  + ListDirectoryAsync(path) → IReadOnlyList<VfsFileInfo>                │    │
│  │  + ReadFileAsync(path, buffer, offset) → int                            │    │
│  │  + FileExistsAsync(path) → bool                                         │    │
│  │  + DirectoryExistsAsync(path) → bool                                    │    │
│  │  + GetVolumeInfo() → VfsVolumeInfo                                      │    │
│  │  + MountAsync(options) / UnmountAsync()                                 │    │
│  └─────────────────────────────────────────────────────────────────────────┘    │
│                                                                                 │
│  ┌──────────────────────────────┐  ┌────────────────────────────────────────┐   │
│  │  VfsFileInfo (record struct) │  │  VfsVolumeInfo (record struct)         │   │
│  │  - Name, FullPath            │  │  - VolumeLabel, FileSystemName         │   │
│  │  - IsDirectory, SizeBytes    │  │  - TotalBytes, FreeBytes               │   │
│  │  - CreationTime, LastWrite   │  │  - Features (ReadOnly, etc.)           │   │
│  │  - Attributes                │  │                                        │   │
│  └──────────────────────────────┘  └────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────────────────────────┘
                                          ▲
                                          │ implements
┌─────────────────────────────────────────┼───────────────────────────────────────┐
│                              APPLICATION LAYER                                   │
│  ┌──────────────────────────────────────┴──────────────────────────────────┐    │
│  │  ZipVirtualFileSystem : IVirtualFileSystem                              │    │
│  │  ───────────────────────────────────────────────────────────────────────│    │
│  │  Dependencies (injected):                                               │    │
│  │    • IArchiveStructureCache (ZIP metadata caching)                      │    │
│  │    • ICache<Stream> (file content caching)                              │    │
│  │    • IPathResolver (virtual path → archive + internal path)             │    │
│  │    • Func<string, IZipReader> (reader factory for extraction)           │    │
│  │                                                                         │    │
│  │  Responsibilities:                                                      │    │
│  │    • Path resolution and normalization                                  │    │
│  │    • Directory listing from cached ArchiveStructure                     │    │
│  │    • File reading with cache integration (borrow/return pattern)        │    │
│  │    • Volume info aggregation                                            │    │
│  └─────────────────────────────────────────────────────────────────────────┘    │
└─────────────────────────────────────────────────────────────────────────────────┘
                                          │
                                          │ uses
                                          ▼
┌─────────────────────────────────────────────────────────────────────────────────┐
│                           INFRASTRUCTURE LAYER (Unchanged)                       │
│  ┌────────────────────────────────┐  ┌──────────────────────────────────────┐   │
│  │  ZipDrive.Infrastructure.    │  │  ZipDrive.Infrastructure.         │   │
│  │  Caching                       │  │  Archives.Zip                        │   │
│  │  ────────────────────────────  │  │  ─────────────────────────────────── │   │
│  │  • GenericCache<T>             │  │  • ZipReader : IZipReader            │   │
│  │  • ArchiveStructureCache       │  │  • Streaming CD enumeration          │   │
│  │  • MemoryStorageStrategy       │  │  • ZIP64 support                     │   │
│  │  • DiskStorageStrategy         │  │  • Store/Deflate decompression       │   │
│  │  • LruEvictionPolicy           │  │                                      │   │
│  └────────────────────────────────┘  └──────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────────────────────────┘
```

---

## Data Flow Diagrams

### File Read Operation

```
┌─────────────────────────────────────────────────────────────────────────────────┐
│  1. Windows App: Read "R:\archive.zip\docs\readme.txt" at offset=5000           │
│     ↓                                                                           │
│  2. DokanNet Kernel Driver → IRP_MJ_READ                                        │
│     ↓                                                                           │
│  ┌──────────────────────────────────────────────────────────────────────────┐   │
│  │  3. DokanFileSystemAdapter.ReadFile(fileName, buffer, offset, info)      │   │
│  │     ────────────────────────────────────────────────────────────────────│   │
│  │     // THIN ADAPTER - just translate and delegate                        │   │
│  │     bytesRead = await _vfs.ReadFileAsync(fileName, buffer, offset, ct);  │   │
│  │     return NtStatus.Success;                                             │   │
│  └───────────────────────────────────┬──────────────────────────────────────┘   │
│                                      ↓                                          │
│  ┌──────────────────────────────────────────────────────────────────────────┐   │
│  │  4. ZipVirtualFileSystem.ReadFileAsync(virtualPath, buffer, offset)      │   │
│  │     ────────────────────────────────────────────────────────────────────│   │
│  │     // 4a. Resolve path                                                  │   │
│  │     resolution = _pathResolver.Resolve(virtualPath);                     │   │
│  │     // → archiveKey="archive.zip", internalPath="docs/readme.txt"        │   │
│  │                                                                          │   │
│  │     // 4b. Get cached structure                                          │   │
│  │     structure = await _structureCache.GetOrBuildAsync(archiveKey, path); │   │
│  │                                                                          │   │
│  │     // 4c. Lookup entry metadata                                         │   │
│  │     entry = structure.GetEntry(internalPath);                            │   │
│  │     // → { LocalHeaderOffset, CompressedSize, UncompressedSize, ... }    │   │
│  │                                                                          │   │
│  │     // 4d. Borrow cached file content (or materialize)                   │   │
│  │     cacheKey = $"{archiveKey}:{internalPath}";                           │   │
│  │     using handle = await _fileCache.BorrowAsync(cacheKey, ttl, factory); │   │
│  │                                                                          │   │
│  │     // 4e. Read from cached stream                                       │   │
│  │     handle.Value.Seek(offset);                                           │   │
│  │     return handle.Value.Read(buffer);                                    │   │
│  └───────────────────────────────────┬──────────────────────────────────────┘   │
│                                      ↓                                          │
│  ┌─────────Cache Miss─────────────────────────────────────────────────────┐     │
│  │  5. Factory Lambda (materialization)                                    │     │
│  │     ───────────────────────────────────────────────────────────────────│     │
│  │     using reader = _readerFactory(absolutePath);                        │     │
│  │     stream = await reader.OpenEntryStreamAsync(entry);                  │     │
│  │     // Decompress → copy to cache (Memory or Disk by size)              │     │
│  │     return new CacheFactoryResult { Value=stream, SizeBytes=size };     │     │
│  └────────────────────────────────────────────────────────────────────────┘     │
└─────────────────────────────────────────────────────────────────────────────────┘
```

### Directory Listing

```
┌─────────────────────────────────────────────────────────────────────────────────┐
│  1. Explorer: List "R:\archive.zip\docs\"                                       │
│     ↓                                                                           │
│  2. DokanNet → DokanFileSystemAdapter.FindFiles("\archive.zip\docs\", ...)      │
│     ↓                                                                           │
│  ┌──────────────────────────────────────────────────────────────────────────┐   │
│  │  3. DokanFileSystemAdapter (THIN)                                        │   │
│  │     var files = await _vfs.ListDirectoryAsync(fileName, ct);             │   │
│  │     return files.Select(f => ToDokanFileInformation(f)).ToList();        │   │
│  └───────────────────────────────────┬──────────────────────────────────────┘   │
│                                      ↓                                          │
│  ┌──────────────────────────────────────────────────────────────────────────┐   │
│  │  4. ZipVirtualFileSystem.ListDirectoryAsync(virtualPath)                 │   │
│  │     ────────────────────────────────────────────────────────────────────│   │
│  │     // 4a. Resolve path                                                  │   │
│  │     resolution = _pathResolver.Resolve(virtualPath);                     │   │
│  │                                                                          │   │
│  │     // 4b. Special case: root directory lists archives                   │   │
│  │     if (resolution.Status == RootDirectory)                              │   │
│  │         return archives.Select(ToVfsFileInfo);                           │   │
│  │                                                                          │   │
│  │     // 4c. Get cached structure                                          │   │
│  │     structure = await _structureCache.GetOrBuildAsync(...);              │   │
│  │                                                                          │   │
│  │     // 4d. Navigate to directory node                                    │   │
│  │     dirNode = structure.GetDirectory(internalPath);                      │   │
│  │                                                                          │   │
│  │     // 4e. Convert children to VfsFileInfo                               │   │
│  │     return dirNode.Subdirectories.Concat(dirNode.Files)                  │   │
│  │            .Select(ToVfsFileInfo);                                       │   │
│  └──────────────────────────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────────────────────────┘
```

### GetFileInformation

```
┌─────────────────────────────────────────────────────────────────────────────────┐
│  1. Explorer/App: GetFileInfo("R:\archive.zip\docs\readme.txt")                 │
│     ↓                                                                           │
│  2. DokanNet → DokanFileSystemAdapter.GetFileInformation(...)                   │
│     ↓                                                                           │
│  ┌──────────────────────────────────────────────────────────────────────────┐   │
│  │  3. DokanFileSystemAdapter (THIN)                                        │   │
│  │     var info = await _vfs.GetFileInfoAsync(fileName, ct);                │   │
│  │     if (info == null) return DokanResult.FileNotFound;                   │   │
│  │     fileInfo = ToDokanFileInformation(info.Value);                       │   │
│  │     return DokanResult.Success;                                          │   │
│  └───────────────────────────────────┬──────────────────────────────────────┘   │
│                                      ↓                                          │
│  ┌──────────────────────────────────────────────────────────────────────────┐   │
│  │  4. ZipVirtualFileSystem.GetFileInfoAsync(virtualPath)                   │   │
│  │     ────────────────────────────────────────────────────────────────────│   │
│  │     // 4a. Resolve path                                                  │   │
│  │     resolution = _pathResolver.Resolve(virtualPath);                     │   │
│  │                                                                          │   │
│  │     // 4b. Handle special cases (root, archive root)                     │   │
│  │     if (resolution.Status == RootDirectory)                              │   │
│  │         return CreateRootDirectoryInfo();                                │   │
│  │     if (resolution.Status == ArchiveRoot)                                │   │
│  │         return CreateArchiveRootInfo(resolution.ArchiveKey);             │   │
│  │                                                                          │   │
│  │     // 4c. Get cached structure                                          │   │
│  │     structure = await _structureCache.GetOrBuildAsync(...);              │   │
│  │                                                                          │   │
│  │     // 4d. Lookup entry or directory                                     │   │
│  │     entry = structure.GetEntry(internalPath);                            │   │
│  │     if (entry != null) return ToVfsFileInfo(entry);                      │   │
│  │                                                                          │   │
│  │     dir = structure.GetDirectory(internalPath);                          │   │
│  │     if (dir != null) return ToVfsFileInfo(dir);                          │   │
│  │                                                                          │   │
│  │     return null; // Not found                                            │   │
│  └──────────────────────────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────────────────────────┘
```

---

## Interface Definitions

### IVirtualFileSystem (Domain Layer)

```csharp
namespace ZipDrive.Domain.Abstractions;

/// <summary>
/// Platform-independent virtual file system abstraction.
/// Provides read-only file system operations over mounted archives.
/// </summary>
/// <remarks>
/// <para>
/// This interface abstracts all file system operations away from any specific
/// platform (Windows/DokanNet). Implementations include:
/// <list type="bullet">
/// <item><c>ZipVirtualFileSystem</c> - Production implementation for ZIP archives</item>
/// <item><c>MemoryVirtualFileSystem</c> - For unit testing without real files</item>
/// </list>
/// </para>
/// <para>
/// <strong>Thread Safety:</strong>
/// Implementations MUST be thread-safe. Multiple concurrent read operations
/// are expected from the DokanNet adapter.
/// </para>
/// </remarks>
public interface IVirtualFileSystem : IAsyncDisposable
{
    // ═══════════════════════════════════════════════════════════════════════
    // File/Directory Information
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Gets file or directory information for the given path.
    /// </summary>
    /// <param name="virtualPath">Virtual path (e.g., "\archive.zip\folder\file.txt").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>File info if found, null if path does not exist.</returns>
    Task<VfsFileInfo?> GetFileInfoAsync(
        string virtualPath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all entries in a directory.
    /// </summary>
    /// <param name="virtualPath">Virtual path to directory.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of file/directory entries.</returns>
    /// <exception cref="VfsDirectoryNotFoundException">If path is not a directory.</exception>
    Task<IReadOnlyList<VfsFileInfo>> ListDirectoryAsync(
        string virtualPath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a file exists at the given path.
    /// </summary>
    Task<bool> FileExistsAsync(
        string virtualPath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a directory exists at the given path.
    /// </summary>
    Task<bool> DirectoryExistsAsync(
        string virtualPath,
        CancellationToken cancellationToken = default);

    // ═══════════════════════════════════════════════════════════════════════
    // File Reading (Primary DokanNet interface)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Reads bytes from a file at the specified offset.
    /// </summary>
    /// <param name="virtualPath">Virtual path to file.</param>
    /// <param name="buffer">Buffer to read into.</param>
    /// <param name="offset">Offset within file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of bytes read.</returns>
    /// <remarks>
    /// This is the primary read method used by DokanNet.
    /// Implementation uses caching for efficient random access.
    /// </remarks>
    /// <exception cref="VfsFileNotFoundException">If file does not exist.</exception>
    Task<int> ReadFileAsync(
        string virtualPath,
        Memory<byte> buffer,
        long offset,
        CancellationToken cancellationToken = default);

    // ═══════════════════════════════════════════════════════════════════════
    // Volume Information
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Gets volume information (total size, free space, label, etc.).
    /// </summary>
    VfsVolumeInfo GetVolumeInfo();

    // ═══════════════════════════════════════════════════════════════════════
    // Lifecycle
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Mounts the virtual file system.
    /// </summary>
    /// <param name="options">Mount options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task MountAsync(VfsMountOptions options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Unmounts the virtual file system.
    /// </summary>
    Task UnmountAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the file system is currently mounted.
    /// </summary>
    bool IsMounted { get; }

    /// <summary>
    /// Event raised when mount state changes.
    /// </summary>
    event EventHandler<VfsMountStateChangedEventArgs>? MountStateChanged;
}
```

### VFS Models (Domain Layer)

```csharp
namespace ZipDrive.Domain.Models;

/// <summary>
/// File or directory information in the virtual file system.
/// Platform-independent equivalent of DokanNet FileInformation.
/// </summary>
public readonly record struct VfsFileInfo
{
    /// <summary>Name of the file or directory (not full path).</summary>
    public required string Name { get; init; }

    /// <summary>Full virtual path.</summary>
    public required string FullPath { get; init; }

    /// <summary>True if this is a directory.</summary>
    public required bool IsDirectory { get; init; }

    /// <summary>File size in bytes (0 for directories).</summary>
    public required long SizeBytes { get; init; }

    /// <summary>Creation time (UTC).</summary>
    public required DateTime CreationTimeUtc { get; init; }

    /// <summary>Last access time (UTC).</summary>
    public required DateTime LastAccessTimeUtc { get; init; }

    /// <summary>Last write time (UTC).</summary>
    public required DateTime LastWriteTimeUtc { get; init; }

    /// <summary>File attributes.</summary>
    public required FileAttributes Attributes { get; init; }

    /// <summary>True if file is read-only.</summary>
    public bool IsReadOnly => (Attributes & FileAttributes.ReadOnly) != 0;
}

/// <summary>
/// Volume information for the virtual file system.
/// </summary>
public readonly record struct VfsVolumeInfo
{
    /// <summary>Volume label (displayed in Explorer).</summary>
    public required string VolumeLabel { get; init; }

    /// <summary>File system name (e.g., "ZipFS").</summary>
    public required string FileSystemName { get; init; }

    /// <summary>Total size of all mounted archives in bytes.</summary>
    public required long TotalBytes { get; init; }

    /// <summary>Free bytes available (always 0 for read-only).</summary>
    public required long FreeBytes { get; init; }

    /// <summary>Maximum component length for paths.</summary>
    public required uint MaximumComponentLength { get; init; }

    /// <summary>File system features (read-only, case-sensitive, etc.).</summary>
    public required VfsFeatures Features { get; init; }
}

/// <summary>
/// File system feature flags.
/// </summary>
[Flags]
public enum VfsFeatures
{
    None = 0,
    ReadOnly = 1,
    CasePreservingNames = 2,
    CaseSensitiveSearch = 4,
    UnicodeOnDisk = 8
}

/// <summary>
/// Options for mounting the file system.
/// </summary>
public record VfsMountOptions
{
    /// <summary>Path to archive file or folder containing archives.</summary>
    public required string ArchivePath { get; init; }

    /// <summary>Whether to recursively scan for archives in subfolders.</summary>
    public bool RecursiveScan { get; init; } = false;
}

/// <summary>
/// Event args for mount state changes.
/// </summary>
public class VfsMountStateChangedEventArgs : EventArgs
{
    public required bool IsMounted { get; init; }
    public required string? MountPath { get; init; }
    public Exception? Error { get; init; }
}
```

### VFS Exceptions (Domain Layer)

```csharp
namespace ZipDrive.Domain.Exceptions;

/// <summary>
/// Base exception for VFS operations.
/// </summary>
public class VfsException : Exception
{
    public VfsException(string message) : base(message) { }
    public VfsException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Thrown when a file is not found.
/// </summary>
public class VfsFileNotFoundException : VfsException
{
    public string VirtualPath { get; }

    public VfsFileNotFoundException(string virtualPath)
        : base($"File not found: {virtualPath}")
    {
        VirtualPath = virtualPath;
    }
}

/// <summary>
/// Thrown when a directory is not found.
/// </summary>
public class VfsDirectoryNotFoundException : VfsException
{
    public string VirtualPath { get; }

    public VfsDirectoryNotFoundException(string virtualPath)
        : base($"Directory not found: {virtualPath}")
    {
        VirtualPath = virtualPath;
    }
}

/// <summary>
/// Thrown when access is denied (e.g., write attempt on read-only FS).
/// </summary>
public class VfsAccessDeniedException : VfsException
{
    public string VirtualPath { get; }

    public VfsAccessDeniedException(string virtualPath, string reason)
        : base($"Access denied to {virtualPath}: {reason}")
    {
        VirtualPath = virtualPath;
    }
}
```

---

## Component Integration

### How Existing Components Fit

| Existing Component | Location | Role in New Architecture |
|-------------------|----------|--------------------------|
| `IArchiveStructureCache` | Domain/Abstractions | **Used by** `ZipVirtualFileSystem` for ZIP metadata |
| `ArchiveStructureCache` | Infrastructure/Caching | **Implementation** injected into `ZipVirtualFileSystem` |
| `GenericCache<T>` | Infrastructure/Caching | **Used by** both Structure Cache and File Content Cache |
| `MemoryStorageStrategy` | Infrastructure/Caching | **Used for** small file content caching |
| `DiskStorageStrategy` | Infrastructure/Caching | **Used for** large file content caching |
| `LruEvictionPolicy` | Infrastructure/Caching | **Used by** all caches |
| `IZipReader` | Infrastructure/Archives.Zip | **Used by** `ZipVirtualFileSystem` for extraction |
| `ZipReader` | Infrastructure/Archives.Zip | **Implementation** of `IZipReader` |
| `IPathResolver` | Domain/Abstractions | **Used by** `ZipVirtualFileSystem` for path normalization |
| `PathResolver` | Application/Services | **Implementation** injected into `ZipVirtualFileSystem` |
| `ArchiveStructure` | Domain/Models | **Returned by** structure cache, used for lookups |
| `DirectoryNode` | Domain/Models | **Used for** directory listing |
| `ZipEntryInfo` | Domain/Models | **Used for** file info and extraction |

### Files That Remain Unchanged

```
src/ZipDrive.Infrastructure.Caching/
├── GenericCache.cs              # ✓ Unchanged
├── ArchiveStructureCache.cs     # ✓ Unchanged
├── MemoryStorageStrategy.cs     # ✓ Unchanged
├── DiskStorageStrategy.cs       # ✓ Unchanged
├── ObjectStorageStrategy.cs     # ✓ Unchanged
├── LruEvictionPolicy.cs         # ✓ Unchanged
└── ...                          # ✓ All unchanged

src/ZipDrive.Infrastructure.Archives.Zip/
├── ZipReader.cs                 # ✓ Unchanged
├── IZipReader.cs                # ✓ Unchanged
└── ...                          # ✓ All unchanged

src/ZipDrive.Domain/
├── Abstractions/
│   ├── IArchiveStructureCache.cs  # ✓ Unchanged
│   ├── IPathResolver.cs           # ✓ Unchanged
│   └── ...
├── Models/
│   ├── ArchiveStructure.cs        # ✓ Unchanged
│   ├── ZipEntryInfo.cs            # ✓ Unchanged
│   ├── DirectoryNode.cs           # ✓ Unchanged
│   └── ...
└── Exceptions/
    └── ZipExceptions.cs           # ✓ Unchanged
```

---

## Key Design Decisions

| Decision | Rationale |
|----------|-----------|
| **IVirtualFileSystem in Domain** | Platform-independent; enables potential FUSE implementation later |
| **ReadFileAsync takes Memory<byte>** | Matches DokanNet signature directly, avoids buffer copy |
| **VfsFileInfo as record struct** | Immutable, stack-allocated, efficient for high-frequency use |
| **Dokan adapter ~200 lines** | All business logic in ZipVirtualFileSystem; adapter is pure translation |
| **Caching layer unchanged** | Proven, well-tested (42 tests), no modification needed |
| **VfsException hierarchy** | Clean mapping to NtStatus codes in adapter |

---

## Related Documentation

- [CACHING_DESIGN.md](./CACHING_DESIGN.md) - File content cache design (unchanged)
- [ZIP_STRUCTURE_CACHE_DESIGN.md](./ZIP_STRUCTURE_CACHE_DESIGN.md) - Structure cache design (unchanged)
- [STREAMING_ZIP_READER_DESIGN.md](./STREAMING_ZIP_READER_DESIGN.md) - ZIP reader design (unchanged)
- [IMPLEMENTATION_CHECKLIST.md](./IMPLEMENTATION_CHECKLIST.md) - Implementation status
