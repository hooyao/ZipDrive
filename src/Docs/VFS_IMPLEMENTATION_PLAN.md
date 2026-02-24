# ZipDrive Implementation Plan: VFS Architecture

**Version:** 1.0
**Created:** 2025-01-25
**Status:** Implemented

---

## Overview

This document outlines the implementation plan for the VFS architecture redesign described in [VFS_ARCHITECTURE_DESIGN.md](./VFS_ARCHITECTURE_DESIGN.md).

---

## Project Structure (Final)

```
src/
├── ZipDrive.Domain/
│   ├── Abstractions/
│   │   ├── IVirtualFileSystem.cs           # NEW
│   │   ├── IArchiveStructureCache.cs       # Existing
│   │   ├── IPathResolver.cs                # Existing
│   │   ├── IArchiveProvider.cs             # Existing
│   │   ├── IArchiveSession.cs              # Existing
│   │   ├── IArchiveRegistry.cs             # Existing
│   │   └── IFileSystemTree.cs              # Existing
│   │
│   ├── Models/
│   │   ├── VfsFileInfo.cs                  # NEW
│   │   ├── VfsVolumeInfo.cs                # NEW
│   │   ├── VfsMountOptions.cs              # NEW
│   │   ├── VfsMountStateChangedEventArgs.cs # NEW
│   │   ├── ArchiveStructure.cs             # Existing
│   │   ├── ZipEntryInfo.cs                 # Existing
│   │   ├── DirectoryNode.cs                # Existing
│   │   ├── FileNode.cs                     # Existing
│   │   └── PathResolutionResult.cs         # Existing
│   │
│   └── Exceptions/
│       ├── VfsException.cs                 # NEW (base + derived)
│       ├── ZipExceptions.cs                # Existing
│       └── ...
│
├── ZipDrive.Application/
│   └── Services/
│       ├── ZipVirtualFileSystem.cs         # NEW - Main implementation
│       └── PathResolver.cs                 # Existing
│
├── ZipDrive.Infrastructure.FileSystem.Dokan/  # NEW PROJECT
│   ├── ZipDrive.Infrastructure.FileSystem.Dokan.csproj
│   ├── DokanFileSystemAdapter.cs           # IDokanOperations2 (thin adapter)
│   ├── DokanHostedService.cs               # IHostedService for mount lifecycle
│   └── Converters/
│       ├── DokanStatusConverter.cs         # VfsException → NtStatus
│       └── DokanModelConverter.cs          # VfsFileInfo → FileInformation
│
├── ZipDrive.Infrastructure.Caching/      # UNCHANGED
│   ├── GenericCache.cs
│   ├── ArchiveStructureCache.cs
│   ├── MemoryStorageStrategy.cs
│   ├── DiskStorageStrategy.cs
│   ├── ObjectStorageStrategy.cs
│   ├── LruEvictionPolicy.cs
│   └── ...
│
├── ZipDrive.Infrastructure.Archives.Zip/ # UNCHANGED
│   ├── ZipReader.cs
│   ├── IZipReader.cs
│   └── ...
│
├── ZipDrive.Cli/
│   ├── Program.cs                          # MODIFIED - DI wiring
│   └── appsettings.json                    # MODIFIED - Configuration
│
└── Docs/
    ├── VFS_ARCHITECTURE_DESIGN.md          # NEW
    ├── VFS_IMPLEMENTATION_PLAN.md          # NEW (this file)
    ├── CACHING_DESIGN.md                   # Existing
    └── ...

tests/
├── ZipDrive.Domain.Tests/
│   └── VfsModelsTests.cs                   # NEW
├── ZipDrive.Application.Tests/
│   └── ZipVirtualFileSystemTests.cs        # NEW
└── ZipDrive.Infrastructure.FileSystem.Dokan.Tests/
    └── DokanAdapterTests.cs                # NEW
```

---

## Implementation Phases

### Phase 1: Domain Layer Extensions

**Goal**: Define platform-independent VFS interfaces and models.

**Files to Create:**

| File | Description |
|------|-------------|
| `src/ZipDrive.Domain/Abstractions/IVirtualFileSystem.cs` | Main VFS interface |
| `src/ZipDrive.Domain/Models/VfsFileInfo.cs` | File/directory info record struct |
| `src/ZipDrive.Domain/Models/VfsVolumeInfo.cs` | Volume info record struct |
| `src/ZipDrive.Domain/Models/VfsMountOptions.cs` | Mount options record |
| `src/ZipDrive.Domain/Models/VfsMountStateChangedEventArgs.cs` | Mount state event args |
| `src/ZipDrive.Domain/Exceptions/VfsException.cs` | VFS exception hierarchy |

**Tasks:**
1. Create `IVirtualFileSystem` interface with all methods
2. Create `VfsFileInfo` record struct with all properties
3. Create `VfsVolumeInfo` record struct
4. Create `VfsFeatures` enum
5. Create `VfsMountOptions` record
6. Create `VfsMountStateChangedEventArgs` class
7. Create exception hierarchy:
   - `VfsException` (base)
   - `VfsFileNotFoundException`
   - `VfsDirectoryNotFoundException`
   - `VfsAccessDeniedException`
8. Add unit tests for model validation

**Dependencies**: None (new code only)

---

### Phase 2: ZipVirtualFileSystem Implementation

**Goal**: Implement IVirtualFileSystem using existing caching and ZIP infrastructure.

**Files to Create:**

| File | Description |
|------|-------------|
| `src/ZipDrive.Application/Services/ZipVirtualFileSystem.cs` | Main implementation |

**Tasks:**

1. **Create class skeleton with DI constructor**
   ```csharp
   public ZipVirtualFileSystem(
       IArchiveStructureCache structureCache,
       ICache<Stream> fileCache,
       IPathResolver pathResolver,
       Func<string, IZipReader> readerFactory,
       ILogger<ZipVirtualFileSystem> logger)
   ```

2. **Implement `GetFileInfoAsync`**
   - Resolve path using `IPathResolver`
   - Handle special cases: root directory, archive root
   - Get structure from cache
   - Lookup entry in `ArchiveStructure.Entries`
   - Convert to `VfsFileInfo`

3. **Implement `ListDirectoryAsync`**
   - Resolve path
   - Handle root: return list of archives
   - Get structure from cache
   - Navigate to `DirectoryNode`
   - Convert children to `VfsFileInfo` list

4. **Implement `ReadFileAsync`** (most complex)
   - Resolve path
   - Get structure, lookup entry
   - Build cache key: `"{archiveKey}:{internalPath}"`
   - Use `ICache<Stream>.BorrowAsync()` with factory lambda:
     ```csharp
     async ct => {
         using var reader = _readerFactory(absolutePath);
         var stream = await reader.OpenEntryStreamAsync(entryInfo, ct);
         // Copy to MemoryStream
         var ms = new MemoryStream((int)entryInfo.UncompressedSize);
         await stream.CopyToAsync(ms, ct);
         return new CacheFactoryResult<Stream> {
             Value = new MemoryStream(ms.ToArray(), writable: false),
             SizeBytes = entryInfo.UncompressedSize
         };
     }
     ```
   - Seek to offset, read into buffer
   - Return bytes read

5. **Implement `FileExistsAsync` / `DirectoryExistsAsync`**
   - Wrapper around GetFileInfoAsync

6. **Implement `GetVolumeInfo`**
   - Aggregate total size from all archives
   - Return fixed values for read-only FS

7. **Implement `MountAsync` / `UnmountAsync`**
   - Store mount options
   - Set `IsMounted` flag
   - Raise `MountStateChanged` event

8. **Add integration tests**
   - Test with real ZIP files
   - Test caching behavior
   - Test directory listing
   - Test file reading at various offsets

**Dependencies**: Phase 1 complete

---

### Phase 3: Dokan Adapter (NEW Project)

**Goal**: Create thin DokanNet adapter that delegates to IVirtualFileSystem.

**Files to Create:**

| File | Description |
|------|-------------|
| `src/ZipDrive.Infrastructure.FileSystem.Dokan/ZipDrive.Infrastructure.FileSystem.Dokan.csproj` | Project file |
| `src/ZipDrive.Infrastructure.FileSystem.Dokan/DokanFileSystemAdapter.cs` | IDokanOperations2 impl |
| `src/ZipDrive.Infrastructure.FileSystem.Dokan/DokanHostedService.cs` | IHostedService |
| `src/ZipDrive.Infrastructure.FileSystem.Dokan/Converters/DokanStatusConverter.cs` | Exception → NtStatus |
| `src/ZipDrive.Infrastructure.FileSystem.Dokan/Converters/DokanModelConverter.cs` | Model conversion |

**Tasks:**

1. **Create project file**
   ```xml
   <Project Sdk="Microsoft.NET.Sdk">
     <PropertyGroup>
       <TargetFramework>net10.0-windows</TargetFramework>
       <Nullable>enable</Nullable>
     </PropertyGroup>
     <ItemGroup>
       <PackageReference Include="DokanNet" Version="2.1.0" />
       <ProjectReference Include="..\ZipDrive.Domain\ZipDrive.Domain.csproj" />
     </ItemGroup>
   </Project>
   ```

2. **Implement `DokanFileSystemAdapter : IDokanOperations2`**

   Key methods (thin translation only):

   ```csharp
   public NtStatus ReadFile(string fileName, byte[] buffer, out int bytesRead,
       long offset, IDokanFileInfo info)
   {
       try
       {
           bytesRead = _vfs.ReadFileAsync(fileName, buffer, offset).GetAwaiter().GetResult();
           return DokanResult.Success;
       }
       catch (VfsFileNotFoundException)
       {
           bytesRead = 0;
           return DokanResult.FileNotFound;
       }
   }

   public NtStatus FindFiles(string fileName, out IList<FileInformation> files,
       IDokanFileInfo info)
   {
       try
       {
           var vfsFiles = _vfs.ListDirectoryAsync(fileName).GetAwaiter().GetResult();
           files = vfsFiles.Select(DokanModelConverter.ToFileInformation).ToList();
           return DokanResult.Success;
       }
       catch (VfsDirectoryNotFoundException)
       {
           files = Array.Empty<FileInformation>();
           return DokanResult.PathNotFound;
       }
   }

   public NtStatus GetFileInformation(string fileName, out FileInformation fileInfo,
       IDokanFileInfo info)
   {
       var vfsInfo = _vfs.GetFileInfoAsync(fileName).GetAwaiter().GetResult();
       if (vfsInfo == null)
       {
           fileInfo = default;
           return DokanResult.FileNotFound;
       }
       fileInfo = DokanModelConverter.ToFileInformation(vfsInfo.Value);
       return DokanResult.Success;
   }
   ```

   Other methods return `DokanResult.NotImplemented` or `DokanResult.AccessDenied` for write ops.

3. **Implement `DokanStatusConverter`**
   ```csharp
   public static NtStatus ToNtStatus(VfsException ex) => ex switch
   {
       VfsFileNotFoundException => DokanResult.FileNotFound,
       VfsDirectoryNotFoundException => DokanResult.PathNotFound,
       VfsAccessDeniedException => DokanResult.AccessDenied,
       _ => DokanResult.InternalError
   };
   ```

4. **Implement `DokanModelConverter`**
   ```csharp
   public static FileInformation ToFileInformation(VfsFileInfo vfs) => new()
   {
       FileName = vfs.Name,
       Attributes = vfs.Attributes,
       Length = vfs.SizeBytes,
       CreationTime = vfs.CreationTimeUtc,
       LastAccessTime = vfs.LastAccessTimeUtc,
       LastWriteTime = vfs.LastWriteTimeUtc
   };
   ```

5. **Implement `DokanHostedService`**
   ```csharp
   public class DokanHostedService : IHostedService
   {
       private readonly IVirtualFileSystem _vfs;
       private readonly DokanFileSystemAdapter _adapter;
       private readonly DokanOptions _options;
       private DokanInstance? _dokanInstance;

       public async Task StartAsync(CancellationToken ct)
       {
           await _vfs.MountAsync(_mountOptions, ct);
           _dokanInstance = DokanOperations.CreateFileSystem(
               _adapter, _mountPath, _options);
       }

       public Task StopAsync(CancellationToken ct)
       {
           _dokanInstance?.Dispose();
           return _vfs.UnmountAsync(ct);
       }
   }
   ```

6. **Add unit tests** (mock IVirtualFileSystem)

**Dependencies**: Phase 1 and Phase 2 complete

---

### Phase 4: CLI Integration & End-to-End Testing

**Goal**: Wire everything together and verify end-to-end functionality.

**Files to Modify:**

| File | Description |
|------|-------------|
| `src/ZipDrive.Cli/Program.cs` | DI registration |
| `src/ZipDrive.Cli/appsettings.json` | Configuration |
| `ZipDrive.slnx` | Add new project reference |

**Tasks:**

1. **Update solution file**
   - Add `ZipDrive.Infrastructure.FileSystem.Dokan` project

2. **Update Program.cs with DI registration**
   ```csharp
   builder.Services.AddSingleton<IVirtualFileSystem, ZipVirtualFileSystem>();
   builder.Services.AddSingleton<IArchiveStructureCache, ArchiveStructureCache>();
   builder.Services.AddSingleton<ICache<Stream>>(sp => {
       var strategy = new MemoryStorageStrategy();
       var policy = new LruEvictionPolicy();
       return new GenericCache<Stream>(strategy, policy, 2L * 1024 * 1024 * 1024);
   });
   builder.Services.AddSingleton<IPathResolver, PathResolver>();
   builder.Services.AddSingleton<Func<string, IZipReader>>(sp =>
       path => new ZipReader(File.OpenRead(path)));
   builder.Services.AddHostedService<DokanHostedService>();
   ```

3. **Add configuration options**
   ```json
   {
     "Mount": {
       "ArchivePath": "D:\\Archives",
       "MountPath": "R:\\"
     },
     "Cache": {
       "MemoryCacheSizeMb": 2048,
       "DiskCacheSizeMb": 10240
     }
   }
   ```

4. **End-to-end testing**
   - Build: `dotnet build`
   - Run tests: `dotnet test`
   - Mount: `dotnet run --project src/ZipDrive.Cli -- --ArchivePath test.zip --MountPath R:\`
   - Verify in Explorer:
     - Browse directories
     - Open files
     - Check file properties
   - Unmount (Ctrl+C)

**Dependencies**: Phase 3 complete

---

## Verification Checklist

### Unit Tests
- [ ] VfsFileInfo model creation and validation
- [ ] VfsVolumeInfo model creation
- [ ] VfsException hierarchy
- [ ] ZipVirtualFileSystem.GetFileInfoAsync
- [ ] ZipVirtualFileSystem.ListDirectoryAsync
- [ ] ZipVirtualFileSystem.ReadFileAsync
- [ ] DokanModelConverter.ToFileInformation
- [ ] DokanStatusConverter.ToNtStatus

### Integration Tests
- [ ] ZipVirtualFileSystem with real ZIP file
- [ ] Directory listing at root
- [ ] Directory listing inside archive
- [ ] File info retrieval
- [ ] File reading at offset 0
- [ ] File reading at random offset
- [ ] Cache hit/miss behavior

### End-to-End Tests
- [ ] Mount ZIP archive
- [ ] Browse in Windows Explorer
- [ ] Open text file in Notepad
- [ ] Copy file from virtual drive
- [ ] Check file properties match ZIP metadata
- [ ] Unmount cleanly

---

## Risk Mitigation

| Risk | Mitigation |
|------|------------|
| DokanNet async compatibility | Use `.GetAwaiter().GetResult()` in adapter (DokanNet is sync) |
| Memory pressure from large files | Use existing dual-tier cache (Memory + Disk) |
| Path normalization edge cases | Reuse existing PathResolver, add tests for edge cases |
| Concurrent access bugs | All caching already thread-safe; VFS uses same patterns |

---

## Success Criteria

1. **DokanFileSystemAdapter is ≤200 lines** - No business logic in adapter
2. **All existing tests still pass** - No regressions
3. **New tests achieve 80% coverage** - For new code
4. **End-to-end works** - Mount, browse, read, unmount
5. **Caching layer unchanged** - Zero modifications to Infrastructure.Caching
