## ADDED Requirements

### Requirement: VfsScope owns VFS instance graph
VfsScope SHALL own a complete VFS instance including IVirtualFileSystem, IFileContentCache, and IArchiveStructureCache. All owned instances SHALL be created from a single DI scope.

#### Scenario: VfsScope created from DI scope
- **WHEN** a new VfsScope is created via `IServiceProvider.CreateScope()`
- **THEN** it SHALL hold references to IVirtualFileSystem, IFileContentCache, and IArchiveStructureCache resolved from that scope

### Requirement: VfsScope owns maintenance timer
VfsScope SHALL run a `PeriodicTimer`-based maintenance loop that periodically calls `EvictExpired()` on both caches and `ProcessPendingCleanup()` on the file content cache. The interval SHALL be configured via `CacheOptions.EvictionCheckIntervalSeconds`.

#### Scenario: Maintenance runs periodically
- **WHEN** VfsScope is active
- **THEN** it SHALL invoke `FileContentCache.EvictExpired()`, `ArchiveStructureCache.EvictExpired()`, and `FileContentCache.ProcessPendingCleanup()` at each interval tick

#### Scenario: Maintenance stops on dispose
- **WHEN** VfsScope is disposed
- **THEN** the maintenance timer SHALL be cancelled and no further maintenance ticks SHALL fire

### Requirement: VfsScope implements IAsyncDisposable
VfsScope SHALL implement `IAsyncDisposable`. DisposeAsync SHALL execute cleanup in this order: (1) cancel and await maintenance loop, (2) call `IVirtualFileSystem.UnmountAsync()`, (3) call `IFileContentCache.Clear()`, (4) call `IFileContentCache.DeleteCacheDirectory()`, (5) dispose the DI scope.

#### Scenario: Dispose sequence
- **WHEN** `DisposeAsync()` is called on a VfsScope
- **THEN** maintenance timer stops, VFS is unmounted, file content cache is cleared, disk cache directory is deleted, and DI scope is disposed

#### Scenario: Dispose is idempotent
- **WHEN** `DisposeAsync()` is called multiple times
- **THEN** subsequent calls SHALL be no-ops without throwing exceptions

### Requirement: VfsScope exposes VFS for adapter
VfsScope SHALL expose its `IVirtualFileSystem` instance via a public property so that `DokanFileSystemAdapter` can reference it during swap.

#### Scenario: VFS accessible after mount
- **WHEN** VfsScope is created and its VFS has completed `MountAsync`
- **THEN** the `Vfs` property SHALL return a mounted `IVirtualFileSystem` instance
