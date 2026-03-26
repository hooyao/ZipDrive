## Why

When users add or remove ZIP files from the `Mount:ArchiveDirectory` while ZipDrive is running, the mounted drive does not reflect the changes until a full restart. This forces users to unmount, close the application, and re-launch — a disruptive workflow for anyone actively managing archive collections. Dynamic reload allows the mounted drive to stay current with the directory contents without restart.

## What Changes

- Add `FileSystemWatcher` monitoring of `Mount:ArchiveDirectory` for `.zip` file additions, deletions, and renames
- Introduce `VfsScope` lifecycle container that owns a complete VFS instance (ArchiveTrie, StructureCache, FileContentCache) plus a per-scope maintenance timer
- Add drain mechanism to `DokanFileSystemAdapter` with reference counting, enabling atomic VFS swap with in-flight request safety
- Replace `CacheMaintenanceService` (HostedService) with per-scope `PeriodicTimer` inside `VfsScope`
- Change DI registrations: VFS-related services become Scoped (recreated per reload); infrastructure services remain Singleton
- Each `VfsScope` uses an isolated temp directory for disk cache to prevent cross-scope conflicts
- During drain, new Dokan requests receive `NtStatus.DeviceBusy` (Explorer auto-retries); 30-second timeout forces swap if drain stalls

## Capabilities

### New Capabilities
- `vfs-scope`: Lifecycle container owning a VFS instance, its caches, and maintenance timer. Implements IAsyncDisposable for clean teardown.
- `adapter-drain`: Reference-counting drain mechanism in DokanFileSystemAdapter for safe atomic VFS swap with timeout.
- `directory-watcher`: FileSystemWatcher integration with debounce for detecting archive directory changes and triggering reload.

### Modified Capabilities
- `dokan-hosted-service`: Gains reload orchestration (watcher setup, scope creation, swap, old scope disposal)
- `dokan-adapter`: Gains volatile VFS reference, drain state, reference counting, SwapAsync method
- `cli-application`: DI registration changes from all-Singleton to mixed Singleton/Scoped; CacheMaintenanceService removed

## Impact

- **Code**: DokanFileSystemAdapter, DokanHostedService, Program.cs modified; VfsScope new class; CacheMaintenanceService removed
- **Dependencies**: No new NuGet packages (FileSystemWatcher is in System.IO)
- **Config**: No new config keys required (uses existing Mount:ArchiveDirectory)
- **Behavior**: Mounted drive updates automatically when ZIP files change in source directory; user presses F5 in Explorer to see changes
- **Risk**: Brief DeviceBusy responses during swap (~100ms typical); 30s timeout worst case
