## 1. Project Setup and Dependencies

- [x] 1.1 Add `DokanNet` (2.3.0.3) NuGet package to `ZipDriveV3.Infrastructure.FileSystem` project
- [x] 1.2 Add project references to `ZipDriveV3.Infrastructure.FileSystem`: Domain, Application, Caching, Archives.Zip
- [x] 1.3 Add `Microsoft.Extensions.Hosting` NuGet package to `ZipDriveV3.Cli` project
- [x] 1.4 Add `Serilog.Extensions.Hosting` and `Serilog.Sinks.Console` to `ZipDriveV3.Cli`
- [x] 1.5 Add `System.Text.Encoding.CodePages` to `ZipDriveV3.Cli`
- [x] 1.6 Add project references to `ZipDriveV3.Cli`: Domain, Application, Infrastructure.FileSystem, Infrastructure.Caching, Infrastructure.Archives.Zip
- [x] 1.7 Verify solution builds (`dotnet build ZipDriveV3.slnx`)

## 2. Configuration

- [x] 2.1 Create `MountOptions` class in `ZipDriveV3.Infrastructure.FileSystem` with `MountPoint` (string, default "R:\\"), `ArchiveDirectory` (string, required), `MaxDiscoveryDepth` (int, default 6)
- [x] 2.2 Create `appsettings.json` in `ZipDriveV3.Cli` with `Mount` and `Cache` sections, plus basic `Serilog` console logging config
- [x] 2.3 Ensure `appsettings.json` is copied to output directory (set CopyToOutputDirectory in csproj)

## 3. DokanFileSystemAdapter

- [x] 3.1 Create `DokanFileSystemAdapter : IDokanOperations2` in `ZipDriveV3.Infrastructure.FileSystem`
- [x] 3.2 Inject `IVirtualFileSystem`, `IPathResolver`, `IArchiveStructureCache`, `ILogger<DokanFileSystemAdapter>`
- [x] 3.3 Implement `CreateFile`: convert path, check existence via VFS, set `info.IsDirectory`, reject write modes with `AccessDenied`, return `Success` for existing entries, `FileNotFound`/`PathNotFound` for missing
- [x] 3.4 Implement `ReadFile`: convert path via `fileName.Span.ToString()`, call `VFS.ReadFileAsync().GetAwaiter().GetResult()`, copy result to `buffer.Span`, set `bytesRead`
- [x] 3.5 Implement `FindFiles`: call `VFS.ListDirectoryAsync`, convert each `VfsFileInfo` to `FindFileInformation` with `FileName = name.AsMemory()`, set attributes/times/size
- [x] 3.6 Implement `FindFilesWithPattern`: same as FindFiles but filter results with `DokanHelper.DokanIsNameInExpression`
- [x] 3.7 Implement `GetFileInformation`: call `VFS.GetFileInfoAsync`, convert to `ByHandleFileInformation`
- [x] 3.8 Implement `GetVolumeInformation`: `volumeLabel.SetString("ZipDrive")`, `fileSystemName.SetString("ZipDriveFS")`, set features `CasePreservedNames | UnicodeOnDisk`
- [x] 3.9 Implement `GetDiskFreeSpace`: return 0 free bytes, total = sum of archive sizes
- [x] 3.10 Implement `GetFileSecurity`: return default read-only security descriptor (or `NotImplemented`)
- [x] 3.11 Implement `Mounted`/`Unmounted`: log mount point, return `Success`
- [x] 3.12 Implement `Cleanup`/`CloseFile`: no-op (no per-file state)
- [x] 3.13 Implement all write operations (`WriteFile`, `DeleteFile`, `DeleteDirectory`, `MoveFile`, `SetFileAttributes`, `SetFileTime`, `SetEndOfFile`, `SetAllocationSize`, `SetFileSecurity`, `FlushFileBuffers`, `LockFile`, `UnlockFile`, `FindStreams`): return `DokanResult.AccessDenied` or `NotImplemented` as appropriate
- [x] 3.14 Implement `DirectoryListingTimeoutResetIntervalMs` property (return 0 or reasonable default)
- [x] 3.15 Add exception-to-NtStatus mapping: catch `VfsFileNotFoundException` → `FileNotFound`, `VfsDirectoryNotFoundException` → `PathNotFound`, `VfsAccessDeniedException` → `AccessDenied`, unexpected → log + `InternalError`

## 4. DokanHostedService

- [x] 4.1 Create `DokanHostedService : BackgroundService` in `ZipDriveV3.Infrastructure.FileSystem`
- [x] 4.2 Inject `IVirtualFileSystem`, `DokanFileSystemAdapter`, `IOptions<MountOptions>`, `ILogger<DokanHostedService>`
- [x] 4.3 Implement `ExecuteAsync`: call `VFS.MountAsync` with options, create `Dokan` instance, build `DokanInstance` via `DokanInstanceBuilder` with `WriteProtection | FixedDrive`, call `WaitForFileSystemClosedAsync`
- [x] 4.4 Implement `StopAsync`: call `Dokan.RemoveMountPoint(mountPoint)`, call `VFS.UnmountAsync()`
- [x] 4.5 Add error handling: catch `DokanException` on mount failure, log clear message about Dokany installation, call `IHostApplicationLifetime.StopApplication()`

## 5. CLI Application (Program.cs)

- [x] 5.1 Rewrite `Program.cs` with `Host.CreateDefaultBuilder(args)` pattern
- [x] 5.2 Register `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)` before host build
- [x] 5.3 Configure Serilog from configuration (`UseSerilog()`)
- [x] 5.4 Bind `Mount` config section to `MountOptions` via `services.Configure<MountOptions>(config.GetSection("Mount"))`
- [x] 5.5 Bind `Cache` config section to `CacheOptions` via `services.Configure<CacheOptions>(config.GetSection("Cache"))`
- [x] 5.6 Register singleton services: `IArchiveTrie` (ArchiveTrie with platform comparer), `IPathResolver` (PathResolver), `IArchiveDiscovery` (ArchiveDiscovery)
- [x] 5.7 Register `Func<string, IZipReader>` factory: `path => new ZipReader(File.OpenRead(path))`
- [x] 5.8 Register cache infrastructure: `ObjectStorageStrategy<ArchiveStructure>`, `MemoryStorageStrategy`, `LruEvictionPolicy`, `GenericCache<ArchiveStructure>`, `GenericCache<Stream>`, `ArchiveStructureCache`
- [x] 5.9 Register `IVirtualFileSystem` (ZipVirtualFileSystem) and `DokanFileSystemAdapter`
- [x] 5.10 Register `DokanHostedService` via `AddHostedService`
- [x] 5.11 Build and run: verify `dotnet build` succeeds

## 6. Build and Smoke Test

- [x] 6.1 Generate test ZIP fixture using `TestZipGenerator.GenerateTestFixtureAsync` (small scale) into a temp directory
- [x] 6.2 Run the CLI app with `Mount:ArchiveDirectory=<temp dir> Mount:MountPoint=R:\`
- [x] 6.3 From a separate terminal or programmatically: list `R:\` to verify virtual folders appear
- [x] 6.4 Navigate into an archive folder (e.g., `R:\games\fps\archive01.zip\`) to verify ZIP contents listed
- [x] 6.5 Read `__manifest__.json` from a mounted archive, parse it
- [x] 6.6 Read a small file (< 50MB) from the mounted drive, compute SHA-256, verify against manifest
- [x] 6.7 Read a large file (>= 50MB) from the mounted drive if available, verify SHA-256
- [x] 6.8 Verify Ctrl+C cleanly unmounts the drive
- [x] 6.9 Run `dotnet test` to verify all existing tests (172) still pass
