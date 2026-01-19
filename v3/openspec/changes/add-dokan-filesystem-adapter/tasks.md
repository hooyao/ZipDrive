# Tasks: Add DokanNet FileSystem Adapter

## 1. Project Setup

- [ ] 1.1 Add DokanNet 2.1.0 NuGet package to `ZipDriveV3.Infrastructure.FileSystem`
- [ ] 1.2 Add project reference to `ZipDriveV3.Domain`
- [ ] 1.3 Add project reference to `ZipDriveV3.Infrastructure.Caching`
- [ ] 1.4 Add project reference to `ZipDriveV3.Infrastructure.Archives.Zip`

## 2. Domain Interface Implementations

- [ ] 2.1 Implement `IFileSystemTree` as `ZipFileSystemTree` wrapping `DirectoryNode`
- [ ] 2.2 Implement `IArchiveSession` as `ZipArchiveSession`
  - Constructor takes `ArchiveStructure`, `IZipReader`, `IFileCache`
  - `BuildTreeAsync()` returns `ZipFileSystemTree`
  - `OpenReadAsync()` uses cache borrow/return pattern
- [ ] 2.3 Implement `IArchiveProvider` as `ZipArchiveProvider`
  - `CanOpen()` checks `.zip` extension and PK signature
  - `OpenAsync()` uses `IZipReader` to parse structure, returns `ZipArchiveSession`

## 3. DokanNet Adapter

- [ ] 3.1 Create `ZipDokanOperations` implementing `IDokanOperations`
- [ ] 3.2 Implement `CreateFile` - validate path exists, handle access modes
- [ ] 3.3 Implement `ReadFile` - borrow from cache, seek, read, dispose handle
- [ ] 3.4 Implement `FindFilesWithPattern` - enumerate directory from tree
- [ ] 3.5 Implement `GetFileInformation` - map `ZipEntryInfo` to `FileInformation`
- [ ] 3.6 Implement `GetVolumeInformation` - return archive name, read-only flags
- [ ] 3.7 Implement `GetDiskFreeSpace` - return total/free space info
- [ ] 3.8 Implement `Mounted`/`Unmounted` - lifecycle logging
- [ ] 3.9 Implement write operation stubs returning `AccessDenied`
  - `WriteFile`, `SetFileAttributes`, `SetFileTime`
  - `DeleteFile`, `DeleteDirectory`, `MoveFile`
  - `SetEndOfFile`, `SetAllocationSize`, `FlushFileBuffers`
- [ ] 3.10 Implement remaining operations as no-op or `NotImplemented`
  - `Cleanup`, `CloseFile`, `LockFile`, `UnlockFile`
  - `GetFileSecurity`, `SetFileSecurity`, `FindStreams`

## 4. Virtual Drive Service

- [ ] 4.1 Create `IVirtualDriveService` interface in Domain
  - `MountAsync(MountOptions, CancellationToken)`
  - `UnmountAsync(CancellationToken)`
  - `IsMounted`, `MountPath` properties
- [ ] 4.2 Implement `VirtualDriveService`
  - Create `DokanInstance` on mount
  - Use `SemaphoreSlim` for thread-safe state transitions
  - Call `Dokan.RemoveMountPoint()` on unmount
- [ ] 4.3 Create `DokanLogger` adapter for `ILogger<T>`

## 5. CLI Integration

- [ ] 5.1 Update CLI to parse `--FilePath` and `--MountPath` arguments
- [ ] 5.2 Configure DI container with all services
  - `IArchiveProvider` → `ZipArchiveProvider`
  - `IArchiveStructureCache` → `ArchiveStructureCache`
  - `IFileCache` → `GenericCache<Stream>` with dual-tier coordinator
  - `IVirtualDriveService` → `VirtualDriveService`
- [ ] 5.3 Implement `VirtualDriveHostedService` as `IHostedService`
  - `StartAsync()` calls `IVirtualDriveService.MountAsync()`
  - `StopAsync()` calls `IVirtualDriveService.UnmountAsync()`
- [ ] 5.4 Register hosted service and run host

## 6. Helper Components

- [ ] 6.1 Create `CacheHandleStream` wrapper
  - Wraps `ICacheHandle<Stream>` and underlying stream
  - Disposes handle when stream is disposed
  - Supports optional `Range` for partial reads
- [ ] 6.2 Create `FileInformationMapper` utility
  - Maps `ZipEntryInfo` → `FileInformation`
  - Maps `DirectoryNode` → `FileInformation`
- [ ] 6.3 Implement dual-tier cache coordinator
  - Routes to memory or disk based on `SmallFileCutoffMb`
  - Uses `MemoryStorageStrategy` and `DiskStorageStrategy`

## 7. Testing

- [ ] 7.1 Unit tests for `ZipFileSystemTree`
  - Find node by path
  - List children
  - Handle missing paths
- [ ] 7.2 Unit tests for `ZipArchiveSession`
  - Build tree from structure
  - Open read with caching
- [ ] 7.3 Unit tests for `ZipDokanOperations`
  - CreateFile validation
  - ReadFile with mock cache
  - FindFilesWithPattern results
  - Write operations return AccessDenied
- [ ] 7.4 Unit tests for `VirtualDriveService`
  - Mount/unmount state transitions
  - Concurrent mount prevention
- [ ] 7.5 Integration tests with real ZIP files
  - Mount, read file, verify content
  - List directories, verify structure
  - Unmount cleanly

## 8. Documentation

- [ ] 8.1 Update CLAUDE.md with new components
- [ ] 8.2 Add XML documentation to public APIs
- [ ] 8.3 Update IMPLEMENTATION_CHECKLIST.md status

## Dependencies

- Tasks 2.x can run in parallel
- Task 3.x depends on 2.x (needs `IArchiveSession`)
- Task 4.x depends on 3.x (needs `ZipDokanOperations`)
- Task 5.x depends on 4.x (needs `IVirtualDriveService`)
- Task 6.x can run in parallel with 3.x-4.x
- Task 7.x depends on corresponding implementation tasks
