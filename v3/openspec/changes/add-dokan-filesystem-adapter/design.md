# Design: DokanNet FileSystem Adapter

## Context

ZipDrive V3 has a complete caching layer (`GenericCache<T>` with borrow/return pattern) and streaming ZIP reader (`IZipReader` with `ArchiveStructureCache`). The missing piece is the DokanNet adapter that exposes these as a mountable Windows drive.

**Existing Components:**
- `ArchiveStructureCache` - Caches parsed ZIP Central Directory (see `ZIP_STRUCTURE_CACHE_DESIGN.md`)
- `GenericCache<T>` - Dual-tier file content cache with borrow/return (see `CACHING_DESIGN.md`)
- `IZipReader` / `ZipReader` - Streaming ZIP parser with entry extraction
- `DirectoryNode` - Tree structure for directory hierarchy
- `PathResolver` - Splits virtual paths into archive key + internal path

**Constraints:**
- DokanNet callbacks run on kernel thread pool threads
- Must handle concurrent file operations (multiple Explorer windows, applications)
- Read-only (no write support)
- Windows x64 only, requires Dokany driver installed

## Goals / Non-Goals

**Goals:**
- Mount a single ZIP archive as a Windows drive letter
- Support all read operations via DokanNet callbacks
- Integrate with existing cache and ZIP reader infrastructure
- Handle concurrent file access correctly
- Clean mount/unmount lifecycle

**Non-Goals:**
- Multi-archive support (mounting folder of ZIPs) - future work
- Write support (create, modify, delete files)
- Network drive emulation
- Linux/macOS support

## Architecture

### Component Diagram

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                              CLI Host (Program.cs)                          │
│  - Parse arguments (--FilePath, --MountPath)                               │
│  - Configure DI container                                                   │
│  - Start VirtualDriveHostedService                                         │
└─────────────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                      VirtualDriveHostedService                              │
│  - IHostedService implementation                                            │
│  - Calls IVirtualDriveService.MountAsync() on StartAsync                    │
│  - Calls IVirtualDriveService.UnmountAsync() on StopAsync                   │
└─────────────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                         IVirtualDriveService                                │
│  + MountAsync(MountOptions, CancellationToken) → Task                      │
│  + UnmountAsync(CancellationToken) → Task                                  │
│  + IsMounted: bool                                                          │
│  + MountPath: string?                                                       │
└─────────────────────────────────────────────────────────────────────────────┘
                                    │
            ┌───────────────────────┴───────────────────────┐
            ▼                                               ▼
┌───────────────────────────────┐       ┌───────────────────────────────────────┐
│     VirtualDriveService       │       │         ZipDokanOperations            │
│  - Creates DokanInstance      │       │  - Implements IDokanOperations        │
│  - Manages mount/unmount      │       │  - Delegates to IArchiveSession       │
│  - Handles DokanNet options   │       │  - Uses IFileCache for content        │
└───────────────────────────────┘       └───────────────────────────────────────┘
                                                            │
            ┌───────────────────────────────────────────────┴──────────┐
            ▼                                                          ▼
┌───────────────────────────────┐       ┌───────────────────────────────────────┐
│      ZipArchiveSession        │       │            IFileCache                  │
│  - Implements IArchiveSession │       │  - Borrow/return pattern              │
│  - Holds ArchiveStructure     │       │  - Memory tier (< 50MB)               │
│  - Provides IFileSystemTree   │       │  - Disk tier (≥ 50MB)                 │
│  - Opens entry streams        │       │  - TTL + LRU eviction                 │
└───────────────────────────────┘       └───────────────────────────────────────┘
            │
            ▼
┌───────────────────────────────┐
│         IZipReader            │
│  - ReadEocdAsync()            │
│  - StreamCentralDirectoryAsync│
│  - OpenEntryStreamAsync()     │
└───────────────────────────────┘
```

### Data Flow: ReadFile Operation

```
1. DokanNet: ReadFile("\\folder\\file.txt", buffer, offset=5000, length=4096)
   │
   ▼
2. ZipDokanOperations.ReadFile()
   │
   ├─ (a) Resolve path via IArchiveSession.Tree.FindNode("folder/file.txt")
   │      └─ Returns FileNode with ZipEntryInfo
   │
   ├─ (b) Generate cache key: "{archivePath}:{entryPath}"
   │      └─ Example: "D:\\archives\\test.zip:folder/file.txt"
   │
   ├─ (c) IFileCache.BorrowAsync(cacheKey, ttl, factory, ct)
   │      │
   │      ├─ Cache HIT: Return existing ICacheHandle<Stream>
   │      │
   │      └─ Cache MISS: Execute factory
   │         │
   │         ├─ ZipArchiveSession.OpenReadAsync(entryPath)
   │         │   └─ IZipReader.OpenEntryStreamAsync(entryInfo)
   │         │       └─ Decompress Store/Deflate → Stream
   │         │
   │         └─ Materialize to byte[] or MemoryMappedFile
   │            └─ Return CacheFactoryResult<Stream>
   │
   ├─ (d) handle.Value.Seek(offset)
   │
   ├─ (e) handle.Value.Read(buffer, 0, length)
   │
   └─ (f) handle.Dispose() ← CRITICAL: Decrements RefCount
   │
   ▼
3. Return DokanResult.Success with bytesRead
```

### Data Flow: FindFilesWithPattern Operation

```
1. DokanNet: FindFilesWithPattern("\\folder\\", "*", FillFindData)
   │
   ▼
2. ZipDokanOperations.FindFilesWithPattern()
   │
   ├─ (a) session.Tree.FindNode("folder") → DirectoryNode
   │
   ├─ (b) For each child in node.Subdirectories:
   │      └─ FillFindData(CreateFileInformation(child, isDirectory=true))
   │
   ├─ (c) For each child in node.Files:
   │      └─ FillFindData(CreateFileInformation(child, isDirectory=false))
   │
   └─ (d) Return DokanResult.Success
```

## Decisions

### Decision 1: Synchronous vs Asynchronous DokanNet Operations

**Decision:** Use synchronous implementations that internally call `.GetAwaiter().GetResult()` on async operations.

**Rationale:**
- DokanNet's `IDokanOperations` interface methods are synchronous
- DokanNet 2.x does not have async versions of these callbacks
- Our caching layer is async (`BorrowAsync`, `OpenEntryStreamAsync`)
- Using `.GetAwaiter().GetResult()` in a dedicated thread pool is acceptable
- DokanNet runs callbacks on its own thread pool, not the .NET ThreadPool

**Alternatives Considered:**
- Async-over-sync wrappers: Not possible (DokanNet API is sync)
- Blocking with `Task.Result`: Same as chosen approach but worse exception handling

### Decision 2: Cache Key Format

**Decision:** Use `{absoluteArchivePath}:{entryPath}` as cache key.

**Rationale:**
- Absolute path ensures uniqueness across different archives with same name
- Colon separator is invalid in Windows paths, safe delimiter
- Entry path uses forward slashes (ZIP standard)

**Example:** `D:\archives\test.zip:folder/document.txt`

### Decision 3: File Handle Management

**Decision:** Do NOT use per-file-handle state. Each ReadFile call borrows from cache independently.

**Rationale:**
- Simpler implementation (no handle tracking)
- Cache borrow/return pattern already handles concurrent access
- DokanNet may call ReadFile from different threads for same handle
- RefCount in cache prevents eviction during active reads

**Alternatives Considered:**
- Per-handle Stream wrapper: Complex, requires tracking handle-to-stream mapping
- Keep stream open between operations: Memory pressure, complex lifecycle

### Decision 4: IArchiveSession Lifecycle

**Decision:** Create `ZipArchiveSession` once per mount, keep alive until unmount.

**Rationale:**
- `ArchiveStructure` (parsed Central Directory) is expensive to build
- Session holds reference to `ArchiveStructureCache` entry
- Single session simplifies resource management

**Implementation:**
```csharp
public class ZipArchiveSession : IArchiveSession
{
    private readonly ArchiveStructure _structure;
    private readonly IZipReader _reader;
    private readonly IFileCache _fileCache;

    // Tree built lazily from _structure.RootDirectory
    public IFileSystemTree Tree => _tree ??= BuildTree(_structure);
}
```

### Decision 5: Error Mapping

**Decision:** Map exceptions to DokanResult codes consistently.

| Exception | DokanResult |
|-----------|-------------|
| `FileNotFoundException` | `FileNotFound` |
| `DirectoryNotFoundException` | `PathNotFound` |
| `IOException` | `InternalError` |
| `UnauthorizedAccessException` | `AccessDenied` |
| `OperationCanceledException` | `InternalError` |
| `ZipException` (corrupt) | `InternalError` |

## Risks / Trade-offs

### Risk 1: Blocking Async Operations

**Risk:** `.GetAwaiter().GetResult()` can cause deadlocks if called from certain contexts.

**Mitigation:**
- DokanNet uses its own thread pool, not the .NET synchronization context
- Our async operations don't capture context (`ConfigureAwait(false)`)
- Tested pattern used successfully in V1

### Risk 2: Cache Eviction During Read

**Risk:** File could be evicted from cache while being read.

**Mitigation:**
- Borrow/return pattern increments `RefCount`
- Entries with `RefCount > 0` are protected from eviction
- `Dispose()` on handle decrements `RefCount`

### Risk 3: Large Directory Listings

**Risk:** ZIP with 100,000+ files could be slow to list.

**Mitigation:**
- `DirectoryNode` tree pre-built during `ArchiveStructure` creation
- Listing is O(n) where n = direct children only
- Consider pagination in future if needed

### Risk 4: Concurrent Mount/Unmount

**Risk:** Race condition between mount and unmount operations.

**Mitigation:**
- `VirtualDriveService` uses lock for state transitions
- `IsMounted` property is thread-safe
- `UnmountAsync` waits for DokanNet cleanup before returning

## Component Details

### ZipDokanOperations

Implements all 25+ `IDokanOperations` methods:

**Read Operations (Implemented):**
- `CreateFile` - Validate path exists, return Success
- `ReadFile` - Borrow from cache, seek, read, dispose handle
- `FindFilesWithPattern` - List directory contents from tree
- `GetFileInformation` - Map ZipEntryInfo to FileInformation
- `GetVolumeInformation` - Return volume label, read-only flags
- `GetDiskFreeSpace` - Return archive size info
- `Mounted` / `Unmounted` - Lifecycle logging

**Write Operations (Return NotImplemented):**
- `WriteFile`, `SetFileAttributes`, `SetFileTime`
- `DeleteFile`, `DeleteDirectory`, `MoveFile`
- `SetEndOfFile`, `SetAllocationSize`
- `FlushFileBuffers`, `LockFile`, `UnlockFile`

**Optional Operations (Return Success/NotImplemented):**
- `Cleanup` - Called when handle closed, no-op
- `CloseFile` - Called after Cleanup, no-op
- `GetFileSecurity` - Return NotImplemented (use default)
- `SetFileSecurity` - Return NotImplemented

### ZipArchiveSession

```csharp
public class ZipArchiveSession : IArchiveSession, IAsyncDisposable
{
    private readonly string _archivePath;
    private readonly ArchiveStructure _structure;
    private readonly IZipReader _reader;
    private readonly IFileCache _fileCache;
    private IFileSystemTree? _tree;

    public ArchiveInfo Info { get; }
    public ArchiveCapabilities Capabilities => ArchiveCapabilities.Read;

    public Task<IFileSystemTree> BuildTreeAsync(CancellationToken ct)
        => Task.FromResult<IFileSystemTree>(_tree ??= new ZipFileSystemTree(_structure));

    public async Task<Stream> OpenReadAsync(string entryPath, Range? range, CancellationToken ct)
    {
        var entry = _structure.GetEntry(entryPath)
            ?? throw new FileNotFoundException(entryPath);

        var cacheKey = $"{_archivePath}:{entryPath}";
        var handle = await _fileCache.BorrowAsync(
            cacheKey,
            TimeSpan.FromMinutes(30),
            async token => {
                var stream = await _reader.OpenEntryStreamAsync(entry, token);
                // Materialize to memory or disk based on size
                return await MaterializeAsync(stream, entry.UncompressedSize, token);
            },
            ct);

        // Return wrapped stream that disposes handle on close
        return new CacheHandleStream(handle, range);
    }
}
```

### VirtualDriveService

```csharp
public class VirtualDriveService : IVirtualDriveService
{
    private readonly IArchiveProvider _provider;
    private readonly IFileCache _fileCache;
    private readonly ILogger<VirtualDriveService> _logger;

    private DokanInstance? _dokanInstance;
    private IArchiveSession? _session;
    private readonly SemaphoreSlim _mountLock = new(1, 1);

    public bool IsMounted => _dokanInstance != null;
    public string? MountPath { get; private set; }

    public async Task MountAsync(MountOptions options, CancellationToken ct)
    {
        await _mountLock.WaitAsync(ct);
        try
        {
            if (IsMounted) throw new InvalidOperationException("Already mounted");

            _session = await _provider.OpenAsync(options.SingleArchivePath!, ct);
            var operations = new ZipDokanOperations(_session, _fileCache, _logger);

            _dokanInstance = new DokanInstance(
                operations,
                options.MountPath,
                DokanOptions.FixedDrive | DokanOptions.WriteProtection,
                new DokanLogger(_logger));

            // Mount runs on background thread
            _ = Task.Run(() => _dokanInstance.WaitForFileSystemClosed(), CancellationToken.None);

            MountPath = options.MountPath;
            _logger.LogInformation("Mounted {Archive} at {MountPath}",
                options.SingleArchivePath, options.MountPath);
        }
        finally
        {
            _mountLock.Release();
        }
    }

    public async Task UnmountAsync(CancellationToken ct)
    {
        await _mountLock.WaitAsync(ct);
        try
        {
            if (!IsMounted) return;

            Dokan.RemoveMountPoint(MountPath);
            await (_session?.DisposeAsync() ?? ValueTask.CompletedTask);

            _dokanInstance = null;
            _session = null;
            MountPath = null;
        }
        finally
        {
            _mountLock.Release();
        }
    }
}
```

## Migration Plan

No migration needed - this is a new capability. The existing `ZipDriveV3.Infrastructure.FileSystem` project is empty.

## Open Questions

1. **Should we support mounting without a drive letter?** (UNC path like `\\?\ZipDrive\archive.zip\`)
   - Defer to future work

2. **How to handle ZIP files that change on disk during mount?**
   - Current: Ignore (cache may become stale)
   - Future: File watcher + cache invalidation

3. **Should large file extraction show progress?**
   - DokanNet doesn't support progress callbacks
   - Consider logging or events for debugging
