# Dokany → WinFsp Migration Plan

## 1. Executive Summary

Migrate ZipDrive's virtual file system driver layer from **DokanNet** (Dokany) to **WinFsp** (`winfsp.net` NuGet package) while introducing a **zero-heap-allocation read path** using `IMemoryOwner<byte>` and memory-mapped files to return pointers directly to the WinFsp kernel buffer.

### Goals

| Goal | Description |
|------|-------------|
| **Driver swap** | Replace DokanNet with WinFsp (`winfsp.net`) — lower kernel overhead, actively maintained |
| **Zero-heap reads** | Eliminate per-read `ArrayPool` rent/return and intermediate `byte[]` copies |
| **IMemoryOwner** | Expose cached file content as `IMemoryOwner<byte>` with pinned memory for pointer access |
| **Memory-mapped storage** | Replace `byte[]` (GC heap) in `MemoryStorageStrategy` with anonymous `MemoryMappedFile` |
| **Direct pointer copy** | In the WinFsp `Read` callback, copy directly from mmap pointer → WinFsp `IntPtr` buffer |

### Read-Path Before vs After

```
BEFORE (Dokany):
  WinFsp IntPtr ← CopyTo ← byte[] (ArrayPool) ← Stream.Read ← MemoryStream(byte[])
  Allocations: 1 ArrayPool rent per read + MemoryStream per borrow

AFTER (WinFsp + mmap):
  WinFsp IntPtr ← Unsafe.CopyBlock ← mmap pointer (IMemoryOwner<byte>)
  Allocations: 0 per read (only on cache miss: mmap creation)
```

---

## 2. Architecture Impact Analysis

### 2.1 Layers Affected

```
┌─────────────────────────────────────────────────────────────────────┐
│ Layer                           │ Impact    │ Changes Required        │
├─────────────────────────────────┼───────────┼─────────────────────────┤
│ Domain (Abstractions)           │ MODERATE  │ New IMemoryOwner-based  │
│                                 │           │ ReadFileAsync overload  │
│                                 │           │ in IVirtualFileSystem   │
├─────────────────────────────────┼───────────┼─────────────────────────┤
│ Application (VFS orchestration) │ MODERATE  │ Add zero-copy read path │
│                                 │           │ through FileContentCache│
├─────────────────────────────────┼───────────┼─────────────────────────┤
│ Infrastructure.Caching          │ MAJOR     │ New MmapStorageStrategy,│
│                                 │           │ IMemoryOwner handles    │
├─────────────────────────────────┼───────────┼─────────────────────────┤
│ Infrastructure.FileSystem       │ REPLACE   │ New WinFsp adapter +    │
│                                 │           │ hosted service          │
├─────────────────────────────────┼───────────┼─────────────────────────┤
│ Presentation (CLI)              │ MINOR     │ DI registration swap,   │
│                                 │           │ telemetry meter rename  │
└─────────────────────────────────┴───────────┴─────────────────────────┘
```

### 2.2 Unchanged Layers

- **Infrastructure.Archives.Zip** — extraction logic unchanged
- **Infrastructure.Archives.Rar** — extraction logic unchanged
- **ChunkedDiskStorageStrategy** — large file disk path unchanged (already uses file-backed storage)
- **GenericCache** — borrow/return/eviction logic unchanged
- **ArchiveChangeConsolidator** — FileSystemWatcher logic unchanged

---

## 3. Phase 1: WinFsp File System Adapter

### 3.1 Package Changes

| Action | Package | Version |
|--------|---------|---------|
| **Remove** | `DokanNet` (2.3.0.3) | — |
| **Add** | `winfsp.net` (2.1.25156) | Central in `Directory.Packages.props` |

### 3.2 New: `WinFspFileSystemAdapter` (replaces `DokanFileSystemAdapter`)

Extends `Fsp.FileSystemBase` instead of implementing `IDokanOperations2`.

#### API Mapping: DokanNet → WinFsp

| DokanNet Method | WinFsp Method | Notes |
|-----------------|---------------|-------|
| `CreateFile` | `Open` + `GetSecurityByName` | WinFsp separates security check from open |
| `ReadFile` | `Read` | `IntPtr Buffer` instead of `NativeMemory<byte>` |
| `FindFiles` | `ReadDirectory` | Uses `DirectoryBuffer` pattern |
| `FindFilesWithPattern` | (handled by WinFsp kernel) | WinFsp does pattern matching in kernel |
| `GetFileInformation` | `GetFileInfo` | Returns `Fsp.Interop.FileInfo` struct |
| `GetVolumeInformation` | `GetVolumeInfo` | Returns `Fsp.Interop.VolumeInfo` struct |
| `GetDiskFreeSpace` | (in `GetVolumeInfo`) | Combined into VolumeInfo |
| `GetFileSecurity` | `GetSecurity` | |
| `Mounted` | `Mounted` | |
| `Unmounted` | `Unmounted` | |
| `Cleanup` | `Cleanup` | |
| `CloseFile` | `Close` | |
| `WriteFile` | `Write` | → STATUS_ACCESS_DENIED |
| `DeleteFile` | — | Not implemented (read-only) |
| `MoveFile` | `Rename` | → STATUS_ACCESS_DENIED |
| All write ops | — | → STATUS_ACCESS_DENIED |

#### Read Method Implementation (Zero-Heap)

```csharp
public override Int32 Read(
    Object FileNode,
    Object FileDesc,
    IntPtr Buffer,        // WinFsp provides this — caller's buffer
    UInt64 Offset,
    UInt32 Length,
    out UInt32 BytesTransferred)
{
    var ctx = (FileContext)FileNode;

    // ZERO-HEAP PATH: borrow IMemoryOwner<byte> from cache,
    // copy directly from pinned mmap pointer → WinFsp IntPtr buffer
    using ICacheHandle<IMemoryOwner<byte>> handle = _vfs.BorrowFileContentAsync(
        ctx.Path, (long)Offset, (int)Length).GetAwaiter().GetResult();

    int bytesRead = handle.Value.Memory.Length;
    if (bytesRead > 0)
    {
        unsafe
        {
            using var pin = handle.Value.Memory.Pin();
            System.Buffer.MemoryCopy(
                pin.Pointer,
                (void*)Buffer,
                Length,
                bytesRead);
        }
    }

    BytesTransferred = (uint)bytesRead;
    return STATUS_SUCCESS;
}
```

**Key advantage**: No `ArrayPool.Rent`, no intermediate `byte[]`, no `Stream.Read` — single `MemoryCopy` from pinned mmap to kernel buffer.

#### Open Method (File Context Pattern)

WinFsp uses `Open` with `FileNode`/`FileDesc` pattern instead of DokanNet's stateless `CreateFile`:

```csharp
public override Int32 Open(
    String FileName,
    UInt32 CreateOptions,
    UInt32 GrantedAccess,
    out Object FileNode,
    out Object FileDesc,
    out FileInfo FileInfo,
    out String NormalizedName)
{
    // ShellMetadata short-circuit
    if (_shortCircuitShellMetadata && ShellMetadataFilter.IsShellMetadataPath(FileName))
    {
        FileNode = null; FileDesc = null;
        FileInfo = default; NormalizedName = null;
        return STATUS_OBJECT_NAME_NOT_FOUND;
    }

    VfsFileInfo vfsInfo = _vfs.GetFileInfoAsync(FileName).GetAwaiter().GetResult();

    FileNode = new FileContext { Path = FileName, IsDirectory = vfsInfo.IsDirectory };
    FileDesc = null;
    FileInfo = ToWinFspFileInfo(vfsInfo);
    NormalizedName = null;

    return STATUS_SUCCESS;
}
```

#### FileContext Class

```csharp
internal sealed class FileContext
{
    public required string Path { get; init; }
    public required bool IsDirectory { get; init; }
}
```

### 3.3 Status Code Mapping

| DokanResult | WinFsp STATUS | Constant |
|-------------|---------------|----------|
| `DokanResult.Success` | `STATUS_SUCCESS` | 0 |
| `DokanResult.FileNotFound` | `STATUS_OBJECT_NAME_NOT_FOUND` | 0xC0000034 |
| `DokanResult.PathNotFound` | `STATUS_OBJECT_PATH_NOT_FOUND` | 0xC000003A |
| `DokanResult.AccessDenied` | `STATUS_ACCESS_DENIED` | 0xC0000022 |
| `DokanResult.NotImplemented` | `STATUS_INVALID_DEVICE_REQUEST` | 0xC0000010 |
| `DokanResult.InternalError` | `STATUS_UNEXPECTED_IO_ERROR` | 0xC00000E9 |

---

## 4. Phase 2: WinFsp Hosted Service

### 4.1 New: `WinFspHostedService` (replaces `DokanHostedService`)

#### Mount Lifecycle Mapping

| DokanHostedService | WinFspHostedService |
|--------------------|---------------------|
| `new Dokan(logger)` | — (no equivalent; logging via FileSystemHost) |
| `new DokanInstanceBuilder(_dokan)` | `new FileSystemHost(adapter)` |
| `.ConfigureOptions(opts => { ... })` | `host.SectorSize = 4096; host.Prefix = ...` |
| `dokanBuilder.Build(_adapter)` | `host.Mount(mountPoint)` |
| `_dokanInstance.WaitForFileSystemClosedAsync(...)` | `host.Mount()` blocks / use Task.Run |
| `_dokan.RemoveMountPoint(mp)` | `host.Unmount()` |
| `_dokanInstance.Dispose()` | `host.Dispose()` |
| `DllNotFoundException` (Dokany) | Check for winfsp DLL load failure |

#### Configuration Mapping

```csharp
// BEFORE (DokanNet):
var dokanBuilder = new DokanInstanceBuilder(_dokan)
    .ConfigureOptions(options =>
    {
        options.Options = DokanOptions.WriteProtection
                        | DokanOptions.FixedDrive
                        | DokanOptions.MountManager;
        options.MountPoint = _mountSettings.MountPoint;
    });

// AFTER (WinFsp):
var host = new FileSystemHost(_adapter);
host.FileSystemName = "NTFS";   // Same as current (Dokany #947 workaround)
host.Prefix = null;              // Local mount
host.SectorSize = 4096;
host.SectorsPerAllocationUnit = 1;
host.VolumeCreationTime = (ulong)DateTime.Now.ToFileTimeUtc();
host.VolumeSerialNumber = 0;
host.Mount(
    _mountSettings.MountPoint,
    null,                         // SecurityDescriptor
    false,                        // Synchronized (false = async dispatch)
    0);                           // DebugLog
```

### 4.2 FileSystemWatcher Integration

The existing `ArchiveChangeConsolidator` and `FileSystemWatcher` logic is **driver-independent**. The only change is the hosted service class name and mount/unmount calls. All watcher code, delta application, and reconciliation logic transfers unchanged.

---

## 5. Phase 3: Zero-Heap IMemoryOwner Read Path

### 5.1 New Domain Abstraction: `IBufferHandle`

```csharp
/// <summary>
/// Zero-copy handle to cached file content for a specific (offset, length) region.
/// The caller receives a Memory<byte> slice that is valid until Dispose.
/// Backed by pinned memory-mapped file view — no heap allocation.
/// </summary>
public interface IBufferHandle : IDisposable
{
    /// <summary>
    /// The file content bytes for the requested region.
    /// Valid until this handle is disposed.
    /// </summary>
    ReadOnlyMemory<byte> Data { get; }

    /// <summary>
    /// Actual number of bytes available (may be less than requested at EOF).
    /// </summary>
    int BytesRead { get; }
}
```

### 5.2 Extended IVirtualFileSystem

```csharp
public interface IVirtualFileSystem
{
    // ... existing methods ...

    /// <summary>
    /// Reads file content with zero-heap allocation.
    /// Returns a handle to pinned memory-mapped data that can be copied
    /// directly to a native buffer via pointer.
    /// </summary>
    Task<IBufferHandle> ReadFileDirectAsync(
        string path,
        long offset,
        int length,
        CancellationToken cancellationToken = default);
}
```

### 5.3 Extended IFileContentCache

```csharp
public interface IFileContentCache
{
    // ... existing ReadAsync (byte[] buffer) ...

    /// <summary>
    /// Zero-copy read: returns a handle to the cached data region.
    /// The data is backed by a memory-mapped view and is valid until the handle is disposed.
    /// </summary>
    Task<IBufferHandle> ReadDirectAsync(
        string archivePath,
        string formatId,
        ArchiveEntryInfo entry,
        string internalPath,
        string cacheKey,
        long offset,
        int length,
        CancellationToken cancellationToken = default);
}
```

### 5.4 Implementation: `MmapBufferHandle`

```csharp
internal sealed class MmapBufferHandle : IBufferHandle
{
    private readonly ICacheHandle<MmapStorageEntry> _cacheHandle;
    private readonly ReadOnlyMemory<byte> _slice;

    public MmapBufferHandle(
        ICacheHandle<MmapStorageEntry> cacheHandle,
        long offset,
        int bytesRead)
    {
        _cacheHandle = cacheHandle;
        _slice = cacheHandle.Value.GetMemory(offset, bytesRead);
        BytesRead = bytesRead;
    }

    public ReadOnlyMemory<byte> Data => _slice;
    public int BytesRead { get; }

    public void Dispose() => _cacheHandle.Dispose();
}
```

---

## 6. Phase 4: Memory-Mapped File Storage Strategy

### 6.1 New: `MmapStorageStrategy` (replaces `MemoryStorageStrategy`)

```csharp
/// <summary>
/// Stores file content in anonymous memory-mapped files.
/// Zero-heap: data lives outside the GC heap in virtual memory pages
/// managed by the OS VMM. Read access via pointer (IMemoryOwner<byte>).
/// </summary>
public sealed class MmapStorageStrategy : IStorageStrategy<MmapStorageEntry>
{
    public async Task<StoredEntry> MaterializeAsync(
        Func<CancellationToken, Task<CacheFactoryResult<Stream>>> factory,
        CancellationToken cancellationToken)
    {
        await using var result = await factory(cancellationToken).ConfigureAwait(false);
        long size = result.SizeBytes;

        // Create anonymous memory-mapped file (no backing disk file)
        var mmf = MemoryMappedFile.CreateNew(
            mapName: null,
            capacity: size,
            MemoryMappedFileAccess.ReadWrite);

        // Copy decompressed stream → mmap view
        using (var accessor = mmf.CreateViewStream(0, size, MemoryMappedFileAccess.Write))
        {
            await result.Value.CopyToAsync(accessor, cancellationToken)
                .ConfigureAwait(false);
        }

        var entry = new MmapStorageEntry(mmf, (int)size);
        return new StoredEntry(entry, size);
    }

    public MmapStorageEntry Retrieve(StoredEntry stored)
        => (MmapStorageEntry)stored.Data;

    public void Dispose(StoredEntry stored)
    {
        var entry = (MmapStorageEntry)stored.Data;
        entry.Dispose();
    }

    public bool RequiresAsyncCleanup => false;
}
```

### 6.2 New: `MmapStorageEntry`

```csharp
/// <summary>
/// Wraps a MemoryMappedFile with a read-only MemoryMappedViewAccessor
/// for zero-copy pointer access to cached file content.
/// </summary>
internal sealed class MmapStorageEntry : IDisposable
{
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _accessor;
    private readonly unsafe byte* _pointer;
    private readonly int _length;

    public MmapStorageEntry(MemoryMappedFile mmf, int length)
    {
        _mmf = mmf;
        _length = length;
        _accessor = mmf.CreateViewAccessor(0, length, MemoryMappedFileAccess.Read);

        // Pin the view and get a raw pointer
        unsafe
        {
            byte* ptr = null;
            _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
            _pointer = ptr + _accessor.PointerOffset;
        }
    }

    public int Length => _length;

    /// <summary>
    /// Returns a Memory<byte> slice for the given (offset, length) region.
    /// Backed by the mmap pointer — no heap allocation.
    /// </summary>
    public ReadOnlyMemory<byte> GetMemory(long offset, int length)
    {
        int actualOffset = (int)Math.Min(offset, _length);
        int actualLength = Math.Min(length, _length - actualOffset);

        // Use MemoryManager<byte> subclass that wraps the raw pointer
        return new MmapMemoryManager(_pointer, _length)
            .Memory.Slice(actualOffset, actualLength);
    }

    public void Dispose()
    {
        unsafe
        {
            _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
        }
        _accessor.Dispose();
        _mmf.Dispose();
    }
}
```

### 6.3 New: `MmapMemoryManager`

```csharp
/// <summary>
/// MemoryManager that wraps a raw pointer from a memory-mapped view.
/// Enables creating Memory<byte> / Span<byte> over mmap'd memory without copying.
/// </summary>
internal sealed unsafe class MmapMemoryManager : MemoryManager<byte>
{
    private readonly byte* _pointer;
    private readonly int _length;

    public MmapMemoryManager(byte* pointer, int length)
    {
        _pointer = pointer;
        _length = length;
    }

    public override Span<byte> GetSpan() => new(_pointer, _length);

    public override MemoryHandle Pin(int elementIndex = 0)
        => new(_pointer + elementIndex);

    public override void Unpin() { /* mmap is always "pinned" */ }

    protected override void Dispose(bool disposing) { /* Lifetime managed by MmapStorageEntry */ }
}
```

### 6.4 Storage Strategy Routing

| File Size | Current Strategy | New Strategy |
|-----------|-----------------|--------------|
| < 50 MB | `MemoryStorageStrategy` (byte[] on GC heap) | `MmapStorageStrategy` (anonymous mmap, off-heap) |
| ≥ 50 MB | `ChunkedDiskStorageStrategy` (sparse file) | Unchanged (already file-backed) |

For ChunkedDiskStorageStrategy, a future optimization could mmap the sparse backing file for zero-copy reads of already-extracted chunks, but this is deferred to Phase 5.

---

## 7. Phase 5: ChunkedDisk Zero-Copy (Future)

For large files (≥ 50MB) currently served by `ChunkedDiskStorageStrategy`:

```
CURRENT: ChunkedStream → FileStream.ReadAsync → byte[] → copy to WinFsp IntPtr
FUTURE:  MemoryMappedFile over sparse file → pointer → copy to WinFsp IntPtr
```

This requires:
- `ChunkedStream` replaced with `ChunkedMmapView` that mmaps the backing sparse file
- Re-mmap on chunk completion (or use a single mmap and rely on OS page faults)
- Careful coordination with background extraction task

**Deferred**: The chunk-sync complexity makes this a separate effort.

---

## 8. End-to-End Read Flow (After Migration)

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                      Zero-Heap Read Flow (WinFsp + mmap)                    │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                             │
│  1. Windows ReadFile → WinFsp kernel driver → user-mode dispatch            │
│     ↓                                                                       │
│  2. WinFspFileSystemAdapter.Read(FileNode, IntPtr Buffer, Offset, Length)   │
│     • FileNode carries path from Open()                                     │
│     • IntPtr Buffer is caller's kernel buffer (no allocation)               │
│     ↓                                                                       │
│  3. IVirtualFileSystem.ReadFileDirectAsync(path, offset, length)            │
│     • Path resolution (archive + internalPath)                              │
│     • ArchiveGuard (ref counting)                                           │
│     ↓                                                                       │
│  4. FileContentCache.ReadDirectAsync(...)                                   │
│     • Routes to MmapStorageStrategy (small) or ChunkedDisk (large)         │
│     • BorrowAsync → returns ICacheHandle<MmapStorageEntry>                 │
│     ↓                                                                       │
│  5. Cache HIT:                                                              │
│     • MmapStorageEntry.GetMemory(offset, length) → ReadOnlyMemory<byte>    │
│     • Backed by mmap pointer — no allocation                                │
│     Cache MISS:                                                             │
│     • Factory → IArchiveEntryExtractor.ExtractAsync → decompressed stream   │
│     • MmapStorageStrategy.MaterializeAsync:                                 │
│       → MemoryMappedFile.CreateNew(capacity)                                │
│       → CopyToAsync(mmapViewStream)                                         │
│       → Return MmapStorageEntry with pointer                                │
│     ↓                                                                       │
│  6. WinFspFileSystemAdapter.Read() copies:                                  │
│     • Memory<byte>.Pin() → MemoryHandle.Pointer                            │
│     • Buffer.MemoryCopy(mmapPtr → winfspIntPtr, length)                    │
│     • Single memcpy, zero GC allocation                                     │
│     ↓                                                                       │
│  7. Return STATUS_SUCCESS with BytesTransferred                             │
│                                                                             │
└─────────────────────────────────────────────────────────────────────────────┘

ALLOCATION PROFILE (per read, cache hit):
  • Managed heap: 0 bytes (no ArrayPool, no byte[], no MemoryStream)
  • Stack: ~64 bytes (MmapBufferHandle struct, Span, MemoryHandle)
  • OS VMM: mmap pages already resident (created on cache miss only)
```

---

## 9. Concurrency Considerations

### 9.1 Mmap Thread Safety

- `MemoryMappedViewAccessor` is thread-safe for concurrent reads
- Multiple WinFsp Read callbacks can safely read from the same mmap view
- No per-read locking needed (read-only after materialization)
- `AcquirePointer`/`ReleasePointer` balanced in constructor/Dispose

### 9.2 Cache Borrow/Return

- Existing `GenericCache` borrow/return pattern protects `MmapStorageEntry` from eviction
  while any `IBufferHandle` is outstanding
- `MmapStorageEntry.Dispose()` only called when `RefCount == 0` (all handles returned)
- This prevents releasing mmap pointer while reads are in progress

### 9.3 Formal Verification

- Existing TLA+ specs (`GenericCache.tla`, `ChunkedExtraction.tla`) remain valid
- `MmapStorageStrategy` uses the same `StoredEntry` / `GenericCache` lifecycle
- No new concurrency primitives introduced

---

## 10. Dependency Changes

### 10.1 `Directory.Packages.props`

```xml
<!-- REMOVE -->
<PackageVersion Include="DokanNet" Version="2.3.0.3" />

<!-- ADD -->
<PackageVersion Include="winfsp.net" Version="2.1.25156" />
```

### 10.2 `ZipDrive.Infrastructure.FileSystem.csproj`

```xml
<!-- REMOVE -->
<PackageReference Include="DokanNet" />

<!-- ADD -->
<PackageReference Include="winfsp.net" />
```

### 10.3 `ZipDrive.Cli.csproj`

No direct DokanNet/WinFsp dependency (only via Infrastructure.FileSystem project reference).

---

## 11. Telemetry Changes

| Current | New |
|---------|-----|
| Meter: `ZipDrive.Dokan` | Meter: `ZipDrive.FileSystem` |
| Histogram: `dokan.read_duration` | Histogram: `fs.read_duration` |
| OTel AddMeter: `"ZipDrive.Dokan"` | OTel AddMeter: `"ZipDrive.FileSystem"` |
| OTel AddSource: `"ZipDrive.Dokan"` | OTel AddSource: `"ZipDrive.FileSystem"` |

---

## 12. DI Registration Changes

```csharp
// BEFORE:
services.AddSingleton<DokanFileSystemAdapter>();
services.AddHostedService<DokanHostedService>();

// AFTER:
services.AddSingleton<WinFspFileSystemAdapter>();
services.AddHostedService<WinFspHostedService>();
```

---

## 13. Configuration Changes

### `appsettings.jsonc`

```jsonc
// No changes needed — Mount:MountPoint and Mount:ArchiveDirectory are driver-independent.
// WinFsp accepts the same drive letter format (e.g., "R:\").
```

### User-Facing Changes

| Aspect | Before | After |
|--------|--------|-------|
| Driver requirement | Dokany v2.3.1.1000 | WinFsp v2.0+ |
| Install URL | github.com/dokan-dev/dokany | winfsp.dev/rel |
| DLL dependency | `dokan2.dll` | `winfsp-x64.dll` |
| Error message (missing driver) | "Dokany driver is not installed" | "WinFsp driver is not installed" |

---

## 14. Test Impact

### 14.1 Tests That Need Updates

| Test File | Change |
|-----------|--------|
| `ArchiveChangeConsolidatorTests.cs` | **None** — driver-independent |
| `UserNoticeTests.cs` | **None** — driver-independent |
| Integration tests | Update any DokanNet-specific assertions |

### 14.2 New Tests Required

| Test | Description |
|------|-------------|
| `MmapStorageStrategyTests` | Materialize → Retrieve → verify data integrity |
| `MmapStorageEntryTests` | GetMemory at various offsets, thread-safety |
| `MmapMemoryManagerTests` | Pin/Unpin, Span correctness |
| `WinFspFileSystemAdapterTests` | Read → verify BytesTransferred |
| `MmapBufferHandleTests` | Borrow → read → dispose lifecycle |

### 14.3 Build Validation

```bash
dotnet build ZipDrive.slnx          # Must compile
dotnet test                          # All 450+ tests must pass
```

Note: WinFsp tests may need the WinFsp driver installed. Tests that exercise the adapter directly (without mounting) can use mock VFS.

---

## 15. Migration Checklist

### Phase 1: WinFsp Adapter (Infrastructure.FileSystem)

- [ ] Add `winfsp.net` to `Directory.Packages.props`
- [ ] Remove `DokanNet` from `Directory.Packages.props`
- [ ] Update `ZipDrive.Infrastructure.FileSystem.csproj` packages
- [ ] Create `FileContext.cs` (path + isDirectory)
- [ ] Create `WinFspFileSystemAdapter.cs` (extends `FileSystemBase`)
  - [ ] `GetSecurityByName` — path existence check
  - [ ] `Open` — create FileContext
  - [ ] `Close` — dispose FileContext
  - [ ] `Read` — zero-heap read via IBufferHandle
  - [ ] `GetFileInfo` — map VfsFileInfo → Fsp.Interop.FileInfo
  - [ ] `ReadDirectory` — list directory entries
  - [ ] `GetVolumeInfo` — map VfsVolumeInfo → Fsp.Interop.VolumeInfo
  - [ ] Write operations → STATUS_ACCESS_DENIED
- [ ] Create `WinFspHostedService.cs` (extends BackgroundService)
  - [ ] Mount lifecycle (FileSystemHost)
  - [ ] Shutdown lifecycle
  - [ ] Port FileSystemWatcher integration
  - [ ] Port error handling (missing driver, mount failure)
- [ ] Rename `DokanTelemetry` → `FileSystemTelemetry`
- [ ] Delete `DokanFileSystemAdapter.cs`
- [ ] Delete `DokanHostedService.cs`

### Phase 2: Zero-Heap Read Path (Domain + Caching)

- [ ] Create `IBufferHandle` interface in Domain
- [ ] Add `ReadFileDirectAsync` to `IVirtualFileSystem`
- [ ] Implement in `ArchiveVirtualFileSystem`
- [ ] Add `ReadDirectAsync` to `IFileContentCache`
- [ ] Implement in `FileContentCache`

### Phase 3: Memory-Mapped Storage (Caching)

- [ ] Create `MmapMemoryManager.cs`
- [ ] Create `MmapStorageEntry.cs`
- [ ] Create `MmapStorageStrategy.cs` (implements `IStorageStrategy<MmapStorageEntry>`)
- [ ] Create `MmapBufferHandle.cs`
- [ ] Update `FileContentCache` constructor to use `MmapStorageStrategy` for memory tier
- [ ] Verify existing `byte[]`-based `ReadAsync` still works (backward compat)

### Phase 4: DI + CLI Updates

- [ ] Update `Program.cs` DI registrations
- [ ] Update OTel meter/source names
- [ ] Update error messages (Dokany → WinFsp)
- [ ] Update `appsettings.jsonc` comments if needed

### Phase 5: Tests + Validation

- [ ] Write `MmapStorageStrategyTests`
- [ ] Write `MmapStorageEntryTests`
- [ ] Write `WinFspFileSystemAdapter` unit tests
- [ ] Run full test suite
- [ ] Manual smoke test (mount archive, read file from Explorer)

### Phase 6: Documentation

- [ ] Update `CLAUDE.md` (driver requirements, build commands)
- [ ] Update `README.md` (install instructions)
- [ ] Update `VFS_ARCHITECTURE_DESIGN.md` (new read flow diagram)
- [ ] Archive this plan (move to completed)

---

## 16. Risk Analysis

| Risk | Mitigation |
|------|------------|
| WinFsp .NET API is .NET Standard 2.0 (boxing, no Span) | The Read callback uses IntPtr — we use unsafe pointer directly. Boxing only on FileNode/FileDesc (one alloc per Open, not per Read). |
| WinFsp driver not installed on user machines | Clear error message with download link. Same UX as current Dokany requirement. |
| `MemoryMappedFile.CreateNew` may fail for very large files | Keep `MmapStorageStrategy` for small files only (< 50MB). Large files continue using `ChunkedDiskStorageStrategy`. |
| mmap pointer lifetime (use-after-free) | Existing `RefCount` borrow/return pattern prevents eviction while handle is active. Same guarantees as current `MemoryStream` pattern. |
| Async/sync mismatch (WinFsp callbacks are sync, VFS is async) | Use `.GetAwaiter().GetResult()` as current Dokan adapter does. WinFsp dispatches on thread pool, so blocking is acceptable. |
| `unsafe` code required for mmap pointer access | Already have `AllowUnsafeBlocks` in FileSystem csproj. Add to Caching csproj. |

---

## 17. Performance Expectations

| Metric | Current (Dokany) | Expected (WinFsp + mmap) |
|--------|-------------------|--------------------------|
| Read latency (cache hit, small file) | ~50μs (ArrayPool rent + Stream.Read + copy) | ~5μs (single memcpy from mmap) |
| GC pressure per read | 1 ArrayPool rent/return + MemoryStream alloc | 0 GC allocations |
| Throughput (sequential reads) | Limited by copy chain | Limited by memcpy bandwidth |
| First-byte latency (cache miss) | ~50ms (extraction) | ~50ms (extraction, unchanged) |
| Memory overhead per cached file | byte[] on GC heap (Gen2 promotion) | mmap pages (OS VMM, no GC pressure) |

---

## 18. Rollback Plan

If WinFsp migration encounters blocking issues:

1. Both adapters can coexist in the codebase (different namespaces)
2. DI registration can be switched via configuration flag
3. `MemoryStorageStrategy` (byte[]) remains as fallback if mmap has issues
4. All existing tests remain valid against the original code path
