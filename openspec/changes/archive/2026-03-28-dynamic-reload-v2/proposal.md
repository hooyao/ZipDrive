## Why

ZipDrive requires a restart when ZIP archives are added to or removed from the archive directory. An earlier experimental approach (VfsScope) solved this by tearing down the entire VFS and rebuilding from scratch on any change — destroying 12GB of warm cache data because one ZIP was added. We need surgical per-archive add/remove that preserves warm caches for unaffected archives.

## What Changes

- Add `FileSystemWatcher` monitoring the archive directory for `*.zip` create/delete/rename events
- Add `ArchiveChangeConsolidator` that batches watcher events and flushes net deltas after a quiet period
- Add per-archive ref counting (`ArchiveNode`) with drain-before-removal for clean concurrent shutdown
- Add `GenericCache.TryRemove(key)` for targeted cache entry removal with orphaned-entry cleanup
- Add per-archive key registry in `FileContentCache` for efficient `RemoveArchive(archiveKey)`
- Add `ReaderWriterLockSlim` to `ArchiveTrie` for thread-safe concurrent read/write
- Add `IArchiveManager` interface separating archive lifecycle from file system operations
- **BREAKING**: `IVirtualFileSystem` no longer owns archive add/remove — callers must use `IArchiveManager`
- Fix `ArchiveStructureCache.Invalidate` to actually remove entries (currently returns false)
- Fix `ArchiveTrie.RemoveArchive` to clean up orphaned virtual folders
- Make `ArchiveTrie.ListFolder` thread-safe (materialize inside read lock)
- Prefetch must participate in per-archive drain guard to prevent cache leak on removal

## Capabilities

### New Capabilities
- `archive-change-consolidator`: FileSystemWatcher event batching with consolidation rules (add+delete=noop, delete+add=modified), quiet period debounce, atomic flush, buffer overflow recovery via full reconciliation
- `per-archive-drain`: Per-archive ref counting (ArchiveNode) with TryEnter/Exit guard, DrainAsync with timeout, DrainToken for cancelling in-flight prefetch, ArchiveGuard disposable struct
- `archive-manager`: IArchiveManager interface (AddArchiveAsync, RemoveArchiveAsync, GetRegisteredArchives), single-file discovery via IArchiveDiscovery.DescribeFile, file-readability probe with retry for half-copied ZIPs
- `cache-targeted-removal`: GenericCache.TryRemove with orphaned entry tracking, FileContentCache.RemoveArchive with per-archive key index, ArchiveStructureCache.Invalidate fix

### Modified Capabilities
- `file-content-cache`: Add `RemoveArchive(string archiveKey)` method and per-archive key registry (`HashSet<string>` with `Lock`)
- `dokan-hosted-service`: Add FileSystemWatcher, ArchiveChangeConsolidator, ApplyDeltaAsync, FullReconciliationAsync, network path detection, configurable quiet period
- `virtual-file-system`: Add per-archive ArchiveNode guards to all VFS operations, MountAsync uses AddArchiveAsync internally, prefetch participates in drain guard
- `archive-trie`: Add ReaderWriterLockSlim for thread safety, RebuildVirtualFolders on remove, materialize ListFolder inside lock
- `endurance-testing`: New DynamicReloadSuite with 7 use-case suites (56 tasks) testing through real DokanFileSystemAdapter instance

## Impact

- **Domain interfaces**: New `IArchiveManager` in `ZipDrive.Domain.Abstractions`. Modified `IFileContentCache` (add `RemoveArchive`). Modified `ICache<T>` (add `TryRemove`).
- **Infrastructure.Caching**: `GenericCache` gains `TryRemove` + orphan tracking. `CacheEntry` gains `IsOrphaned`/`MarkOrphaned`. `FileContentCache` gains per-archive key index. `ArchiveStructureCache.Invalidate` fixed. `ChunkedFileEntry.Dispose` must handle missing directory.
- **Application**: `ArchiveTrie` gains `ReaderWriterLockSlim`. `ZipVirtualFileSystem` implements `IArchiveManager`, gains `ArchiveNode` map + `ArchiveGuard`. New `ArchiveNode`, `ArchiveGuard`, `ArchivePathHelper` classes.
- **Infrastructure.FileSystem**: `DokanHostedService` gains `FileSystemWatcher` + `ArchiveChangeConsolidator`. `DokanFileSystemAdapter` unchanged.
- **Configuration**: `MountSettings` gains `DynamicReloadQuietPeriodSeconds`.
- **DI**: Everything stays Singleton. No scoped services.
- **Tests**: 51 test cases across 10 categories + 7 endurance suites. New test project for consolidator/watcher integration.
