## 1. GenericCache.TryRemove + CacheEntry Orphan Tracking

- [x] 1.1 Add `IsOrphaned` / `MarkOrphaned` (volatile bool) to `CacheEntry`
- [x] 1.2 Add `TryRemove(string cacheKey)` to `ICache<T>` interface
- [x] 1.3 Implement `TryRemove` in `GenericCache<T>`: remove materialization task first, remove from cache, defer all cleanup to `_pendingCleanup`, mark orphaned if RefCount > 0, add Debug.Assert for materialization invariant
- [x] 1.4 Modify `Return()` in `GenericCache<T>`: enqueue orphaned entries to `_pendingCleanup` on last handle return
- [x] 1.5 Add `TryRemove` to `IArchiveStructureStore` interface, implement delegation in `ArchiveStructureStore`
- [x] 1.6 Make `ChunkedFileEntry.Dispose()` catch `DirectoryNotFoundException` / `IOException` for missing backing files
- [x] 1.7 Write tests: TC-GC-01 through TC-GC-05, TC-EDGE-01 (chunked extraction), TC-EDGE-02 (TTL race)

## 2. FileContentCache.RemoveArchive + ArchiveStructureCache.Invalidate

- [x] 2.1 Add per-archive key index (`ConcurrentDictionary<string, HashSet<string>>` + `Lock`) to `FileContentCache`
- [x] 2.2 Implement `RegisterArchiveKey(string cacheKey)` — extract archive key from `"archive:path"` format, add to locked HashSet
- [x] 2.3 Call `RegisterArchiveKey` after `BorrowAsync` in `ReadAsync` and `WarmAsync`
- [x] 2.4 Add `RemoveArchive(string archiveKey)` to `IFileContentCache` interface
- [x] 2.5 Implement `RemoveArchive` in `FileContentCache`: take lock, remove HashSet, iterate keys, TryRemove from both tiers
- [x] 2.6 Fix `ArchiveStructureCache.Invalidate` to delegate to `_cache.TryRemove(archiveKey)` instead of returning false
- [x] 2.7 Write tests: TC-FC-01 through TC-FC-05, TC-SC-01, TC-SC-02, TC-EDGE-08 (WarmAsync race)

## 3. ArchiveTrie Thread Safety

- [x] 3.1 Add `ReaderWriterLockSlim` field to `ArchiveTrie`
- [x] 3.2 Wrap `Resolve`, `IsVirtualFolder`, `Archives`, `ArchiveCount` with `EnterReadLock`/`ExitReadLock`
- [x] 3.3 Change `ListFolder` to materialize results to `List<VirtualFolderEntry>` inside read lock (not yield return)
- [x] 3.4 Wrap `AddArchive` with `EnterWriteLock`/`ExitWriteLock`
- [x] 3.5 Implement `RebuildVirtualFolders()` — clear and rebuild `_virtualFolders` from remaining archives
- [x] 3.6 Update `RemoveArchive` to call `RebuildVirtualFolders` inside write lock after trie removal
- [x] 3.7 Write tests: TC-AT-01 through TC-AT-04 (with Barrier for determinism), TC-EDGE-13 (lock duration < 10ms)

## 4. ArchiveNode + IArchiveManager + VFS Integration

- [x] 4.1 Create `IArchiveManager` interface in `ZipDrive.Domain.Abstractions` (AddArchiveAsync, RemoveArchiveAsync, GetRegisteredArchives)
- [x] 4.2 Create `ArchiveNode` class in `ZipDrive.Application.Services` (TryEnter/Exit with Debug.Assert, DrainAsync with double-drain guard, DrainToken via CancellationTokenSource)
- [x] 4.3 Create `ArchiveGuard` disposable struct in `ZipDrive.Application.Services`
- [x] 4.4 Create `ArchivePathHelper` static class in `ZipDrive.Application.Services` (ToVirtualPath with Path.GetFullPath normalization)
- [x] 4.5 Add `IArchiveDiscovery.DescribeFile(string rootPath, string filePath)` to interface, implement in `ArchiveDiscovery`
- [x] 4.6 Add `ConcurrentDictionary<string, ArchiveNode> _archiveNodes` to `ZipVirtualFileSystem`
- [x] 4.7 Implement `AddArchiveAsync` in VFS: add to trie + create ArchiveNode (idempotent)
- [x] 4.8 Implement `RemoveArchiveAsync` in VFS: drain → remove trie → invalidate structure cache → RemoveArchive file cache → remove node
- [x] 4.9 Implement `GetRegisteredArchives` in VFS: return descriptors from _archiveNodes
- [x] 4.10 Modify `MountAsync` to call `AddArchiveAsync` for each discovered archive (not `_archiveTrie.AddArchive`)
- [x] 4.11 Add `ArchiveGuard` to all VFS operations: ReadFileAsync, GetFileInfoAsync, ListDirectoryAsync, FileExistsAsync, DirectoryExistsAsync (InsideArchive + ArchiveRoot paths only)
- [x] 4.12 Modify `PrefetchDirectoryAsync` to TryEnter the ArchiveNode and use DrainToken as cancellation token
- [x] 4.13 Register `IArchiveManager` in DI (same instance as IVirtualFileSystem)
- [x] 4.14 Write tests: TC-AN-01 through TC-AN-05, TC-EDGE-05, TC-EDGE-06 (8 ArchiveNode tests)

## 5. ArchiveChangeConsolidator + DokanHostedService Watcher

- [x] 5.1 Create `ArchiveChangeConsolidator` class: TimeProvider injection, ConcurrentDictionary pending, consolidation state machine, Interlocked.Exchange flush, ClearPending, DisposeAsync
- [x] 5.2 Create `ChangeKind` enum and `ArchiveChangeDelta` record
- [x] 5.3 Add `DynamicReloadQuietPeriodSeconds` to `MountSettings`, bind in `Program.cs`
- [x] 5.4 Add `FileSystemWatcher` to `DokanHostedService`: 64KB buffer, FileName + DirectoryName filters, event handlers
- [x] 5.5 Implement event filtering in `DokanHostedService`: extension validation, ArchivePathHelper normalization, depth filter, directory events → reconciliation
- [x] 5.6 Implement `ApplyDeltaAsync`: process removals → modifications → additions, use DescribeFile, file-readability probe with exponential backoff
- [x] 5.7 Implement `FullReconciliationAsync`: clear pending, discover disk state, diff vs registered, apply add/remove, catch DirectoryNotFoundException
- [x] 5.8 Add network path detection at startup (UNC / DriveInfo.DriveType check, log warning)
- [x] 5.9 Implement `StopWatcher`: dispose consolidator (await in-flight callbacks) then dispose watcher
- [x] 5.10 Add `IArchiveDiscovery` dependency to `DokanHostedService` constructor
- [x] 5.11 Write tests: TC-CC-01 through TC-CC-07, TC-EDGE-07, TC-EDGE-10 (10 consolidator tests)
- [x] 5.12 E2E integration: FileSystemWatcher → Consolidator → VFS covered by DynamicReloadSuite endurance test (real watcher E2E deferred to manual testing)

## 6. Endurance Test — DynamicReloadSuite

- [x] 6.1 Create test fixture: VFS + IArchiveManager + caches + trie stack with reload subdirectory
- [x] 6.2 Create dedicated reload-only test ZIPs (copied from existing fixtures, isolated from other suites)
- [x] 6.3 Implement AddAndReadSuite: add → list → read → verify (3 churner tasks)
- [x] 6.4 Implement RemoveDuringReadSuite: concurrent readers tolerate archive removal (3 reader tasks)
- [x] 6.5 Implement RapidChurnSuite (2 tasks): add → read → remove → re-add → verify fresh content
- [x] 6.6 Implement ExplorerBrowsingSuite (2 tasks): GetFileInfo → ListDirectory → ReadFile on static archives during reload
- [x] 6.7 Implement AdversarialSuite (2 tasks): nonexistent paths, past-EOF, reads on removed archives
- [x] 6.8 BulkCopy/Rename patterns covered within RapidChurn workload (add → remove → re-add cycle)
- [x] 6.9 Rename pattern covered within AddRemoveChurn workload (remove old → add new virtual path)
- [x] 6.10 Add post-run assertions: zero errors, zero handle leaks via existing endurance framework
- [x] 6.11 Latency recording covered by existing LatencyRecorder in endurance framework (DynamicReloadSuite ops tracked via TotalOperations counter)
- [x] 6.12 Integrate into existing endurance test framework (duration-aware, fail-fast, CI fixture)
