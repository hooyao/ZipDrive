## 1. Adapter Guarded Methods

- [x] 1.1 Add `GuardedReadFileAsync(string path, byte[] buffer, long offset, CancellationToken ct)` to `DokanFileSystemAdapter`
- [x] 1.2 Add `GuardedListDirectoryAsync(string path, CancellationToken ct)` to `DokanFileSystemAdapter`
- [x] 1.3 Add `GuardedGetFileInfoAsync(string path, CancellationToken ct)` to `DokanFileSystemAdapter`
- [x] 1.4 Add `GuardedFileExistsAsync(string path, CancellationToken ct)` to `DokanFileSystemAdapter`
- [x] 1.5 Add `GuardedDirectoryExistsAsync(string path, CancellationToken ct)` to `DokanFileSystemAdapter`

## 2. EnduranceSuiteBase Adapter Migration

- [x] 2.1 Change `EnduranceSuiteBase` constructor from `IVirtualFileSystem vfs` to `DokanFileSystemAdapter adapter`
- [x] 2.2 Change field from `Vfs` to `Adapter`, update all base class methods (`GetFilesAsync`, `RunWorkloadLoopAsync` error reporting)
- [x] 2.3 Change `GetFilesAsync` to use `Adapter.GuardedListDirectoryAsync`

## 3. Migrate Existing Suites to Adapter

- [x] 3.1 Update `NormalReadSuite` — constructor + all `Vfs.*` → `Adapter.Guarded*`
- [x] 3.2 Update `PartialReadSuite` — constructor + all `Vfs.*` → `Adapter.Guarded*`
- [x] 3.3 Update `ConcurrencyStressSuite` — constructor + all `Vfs.*` → `Adapter.Guarded*`
- [x] 3.4 Update `EdgeCaseSuite` — constructor + all `Vfs.*` → `Adapter.Guarded*`
- [x] 3.5 Update `EvictionValidationSuite` — constructor + all `Vfs.*` → `Adapter.Guarded*`
- [x] 3.6 Update `PathResolutionSuite` — constructor + all `Vfs.*` → `Adapter.Guarded*`
- [x] 3.7 Update `LatencyMeasurementSuite` — constructor + all `Vfs.*` → `Adapter.Guarded*`

## 4. EnduranceTest Fixture

- [x] 4.1 Build `DokanFileSystemAdapter` in `EnduranceTest.InitializeAsync` (real VFS + MountSettings + NullLogger)
- [x] 4.2 Pass adapter to all suite constructors instead of VFS
- [x] 4.3 Pass `IArchiveManager` (_vfs cast) to DynamicReloadSuite for lifecycle ops

## 5. Rewrite DynamicReloadSuite — Logical Scenarios Through Adapter

- [x] 5.1 AddAndRead scenarios (15 tasks): BasicAddAndVerify, ImmediateRead, IdempotentOverwrite, NestedSubdirectory — all using `Adapter.Guarded*Async`
- [x] 5.2 RemoveDuringRead scenarios (10 tasks): DeleteDuringLargeRead, DeleteDuringMultiFileRead, DeleteDuringListDirectory
- [x] 5.3 RapidChurn scenarios (8 tasks): ReplaceContent (SHA-256 must match NEW zip), RapidToggle (add+delete cancels)
- [x] 5.4 ExplorerBrowsing scenarios (12 tasks): TreeWalk, BrowseDuringAdd, BrowseDuringRemove
- [x] 5.5 Adversarial scenarios (5 tasks): NonexistentPaths, ReadBeyondEOF, ReadDirectory, ListDeletedArchive, ShellMetadata
- [x] 5.6 CrossArchiveInterference scenarios (10 tasks): SameFileName isolation, ConcurrentRemove, VirtualFolderOrphan
- [x] 5.7 BulkCopy scenarios (3 tasks): copy 30 ZIPs simultaneously, verify all, delete all
- [x] 5.8 Rename scenarios (3 tasks): RenameAndAccess, RenameToNonZip
- [x] 5.9 ConcurrencyRace scenarios (6 tasks): ThunderingHerdFirstAccess, StructureCacheEvictionDuringRead
- [x] 5.10 DegradationMonitoring scenarios (2 tasks): TempFileAccumulation, HandleLeakProbe

## 6. Post-Run Assertions

- [x] 6.1 Add trie-disk consistency check: `GetRegisteredArchives().Count` matches `.zip` file count on disk
- [x] 6.2 Add temp file count == 0 after DeleteCacheDirectory
- [x] 6.3 Add `PendingCleanupCount == 0` assertion
- [x] 6.4 Verify no orphan virtual folders (every virtual folder has at least one archive under it)

## 7. Build and Test

- [x] 7.1 Verify all 382+ non-endurance tests still pass
- [x] 7.2 Run endurance test CI duration (~72s), verify zero errors
