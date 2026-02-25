## 1. DiskStorageStrategy — Per-Process Subdirectory

- [x] 1.1 Add `string? processSubdirectory` parameter to `DiskStorageStrategy` constructor (after `tempDirectory`), default `null` for backward compatibility
- [x] 1.2 In constructor: if `processSubdirectory` is not null, set `_tempDirectory = Path.Combine(baseDir, processSubdirectory)` instead of just `baseDir`
- [x] 1.3 Add `DeleteCacheDirectory()` method that calls `Directory.Delete(_tempDirectory, recursive: true)` with try/catch logging
- [x] 1.4 Build to verify compilation

## 2. DualTierFileCache — Mount Letter Integration

- [x] 2.1 Add `IOptions<MountSettings>` constructor parameter to `DualTierFileCache`
- [x] 2.2 Compute subdirectory name: `$"ZipDrive-{Environment.ProcessId}-{mountSettings.MountPoint[0]}"`
- [x] 2.3 Pass computed subdirectory name to `DiskStorageStrategy` constructor
- [x] 2.4 Add `DeleteCacheDirectory()` method that delegates to `DiskStorageStrategy` (expose via `_diskStorageStrategy` field)
- [x] 2.5 Build to verify compilation

## 3. Expose DeleteCacheDirectory Through the Chain

- [x] 3.1 Add `DeleteCacheDirectory()` method to `DiskStorageStrategy` (done in 1.3)
- [x] 3.2 `IStorageStrategy<T>` does NOT need to change — `DeleteCacheDirectory` is specific to `DiskStorageStrategy`
- [x] 3.3 `DualTierFileCache` keeps a `_diskStorageStrategy` field reference alongside `_diskCache`
- [x] 3.4 Build to verify compilation

## 4. CacheMaintenanceService — Shutdown Cleanup

- [x] 4.1 After `_fileCache.Clear()` in the `ExecuteAsync` shutdown block, add `_fileCache.DeleteCacheDirectory()`
- [x] 4.2 Wrap in try/catch with warning log on failure
- [x] 4.3 Build to verify compilation

## 5. DI Wiring

- [x] 5.1 `IOptions<MountSettings>` already registered in `Program.cs` — no change needed
- [x] 5.2 Build full solution — succeeded

## 6. Fix Existing Tests

- [x] 6.1 `GenericCacheIntegrationTests.cs` — backward-compatible, no changes needed (uses `null` processSubdirectory by default)
- [x] 6.2 `DualTierFileCacheTests.cs` — added `IOptions<MountSettings>` with `MountPoint = "T:\"`
- [x] 6.3 `EnduranceTest.cs` — added `IOptions<MountSettings>` with default `MountSettings`
- [x] 6.4 `VfsTestFixture.cs` + `ZipVirtualFileSystemTests.cs` — added `IOptions<MountSettings>`
- [x] 6.5 Build and run all existing tests — 242 tests pass, zero regressions

## 7. New Unit Tests

- [x] 7.1 Test: `DiskStorageStrategy` with `processSubdirectory` creates the subdirectory under the base temp dir
- [x] 7.2 Test: `DiskStorageStrategy.StoreAsync` stores files inside the process subdirectory
- [x] 7.3 Test: `DiskStorageStrategy.DeleteCacheDirectory` removes the entire subdirectory recursively
- [x] 7.4 Test: `DiskStorageStrategy` with `processSubdirectory: null` behaves as before (backward compat)
- [x] 7.5 Test: `DualTierFileCache` constructs correct subdirectory name from `MountSettings.MountPoint`
- [x] 7.6 Test: `DualTierFileCache.DeleteCacheDirectory` removes the process subdirectory (via delegation)
- [x] 7.7 Test: Two `DiskStorageStrategy` instances with different subdirectory names create isolated directories
- [x] 7.8 Run full test suite — 249 tests pass (7 new + 242 existing)
