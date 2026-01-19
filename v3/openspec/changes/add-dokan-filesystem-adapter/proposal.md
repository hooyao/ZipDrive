# Change: Add DokanNet FileSystem Adapter

## Why

The core caching layer (42 tests) and streaming ZIP reader (15 tests) are complete, but there's no way to expose them as a mountable Windows drive. The `ZipDriveV3.Infrastructure.FileSystem` project is empty. Without a DokanNet adapter implementing `IDokanOperations`, users cannot mount ZIP archives as virtual drives—which is the entire purpose of ZipDrive.

## What Changes

- **Add DokanNet package reference** to `ZipDriveV3.Infrastructure.FileSystem`
- **Implement `ZipDokanOperations`** class implementing `DokanNet.IDokanOperations`
- **Implement `IArchiveSession`** for ZIP archives using `IZipReader` and caching
- **Implement `IFileSystemTree`** using `DirectoryNode` from `ArchiveStructure`
- **Implement `ZipArchiveProvider`** as the `IArchiveProvider` for ZIP format
- **Implement `ArchiveRegistry`** for managing mounted archives
- **Add `IVirtualDriveService`** interface and implementation for mount/unmount lifecycle
- **Wire up CLI** to accept arguments and mount drives via hosted service

### Key Operations to Implement

| DokanNet Operation | Implementation |
|-------------------|----------------|
| `CreateFile` | Path validation, return Success for existing entries |
| `ReadFile` | Use `IFileCache.BorrowAsync()` → seek/read → dispose handle |
| `FindFilesWithPattern` | Use `DirectoryNode` tree from `ArchiveStructure` |
| `GetFileInformation` | Map `ZipEntryInfo` to `FileInformation` struct |
| `GetVolumeInformation` | Return archive name and read-only flags |
| `Mounted`/`Unmounted` | Lifecycle callbacks for logging/cleanup |

### Read-Only Operations (Return NotImplemented)

`WriteFile`, `SetFileAttributes`, `SetFileTime`, `DeleteFile`, `DeleteDirectory`, `MoveFile`, `SetEndOfFile`, `SetAllocationSize`, `FlushFileBuffers`

## Impact

- **Affected specs**: New `dokan-filesystem` capability (first spec in this project)
- **Affected code**:
  - `src/ZipDriveV3.Infrastructure.FileSystem/` (currently empty)
  - `src/ZipDriveV3.Infrastructure.Archives.Zip/` (add `ZipArchiveProvider`, `ZipArchiveSession`)
  - `src/ZipDriveV3.Cli/` (wire up DI and hosted service)
- **Dependencies**: DokanNet 2.1.0 NuGet package, Dokany driver installed on system
- **Risk**: Medium - DokanNet callbacks run on kernel thread pool, must be careful with async/blocking
