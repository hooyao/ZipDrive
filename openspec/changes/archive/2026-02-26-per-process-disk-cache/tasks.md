## 1. DiskStorageStrategy — Per-Process Subdirectory

- [x] 1.1 In constructor: always compute `_tempDirectory = Path.Combine(baseDir, $"ZipDrive-{Environment.ProcessId}")`
- [x] 1.2 `Directory.CreateDirectory(_tempDirectory)` if it doesn't exist (existing pattern)
- [x] 1.3 Add `DeleteCacheDirectory()` method that calls `Directory.Delete(_tempDirectory, recursive: true)` with try/catch logging
- [x] 1.4 Build to verify compilation

## 2. DualTierFileCache — Expose DeleteCacheDirectory

- [x] 2.1 Keep `_diskStorageStrategy` field reference alongside `_diskCache` in `DualTierFileCache`
- [x] 2.2 Add `DeleteCacheDirectory()` method that delegates to `_diskStorageStrategy.DeleteCacheDirectory()`
- [x] 2.3 Build to verify compilation

## 3. CacheMaintenanceService — Shutdown Cleanup

- [x] 3.1 After `_fileCache.Clear()` in the `ExecuteAsync` shutdown block, add `_fileCache.DeleteCacheDirectory()`
- [x] 3.2 Wrap in try/catch with warning log on failure
- [x] 3.3 Build to verify compilation

## 4. Fix Existing Tests

- [x] 4.1 `GenericCacheIntegrationTests.cs` — update `Directory.GetFiles` calls to use `SearchOption.AllDirectories`
- [x] 4.2 `DualTierFileCacheTests.cs` — already uses `SearchOption.AllDirectories`
- [x] 4.3 `EnduranceTest.cs`, `VfsTestFixture.cs`, `ZipVirtualFileSystemTests.cs` — no changes needed (`DualTierFileCache` constructor unchanged)
- [x] 4.4 Build and run all existing tests — pass

## 5. New Unit Tests

- [x] 5.1 Test: `DiskStorageStrategy` creates `ZipDrive-{pid}` subdirectory under base temp dir
- [x] 5.2 Test: `DiskStorageStrategy.StoreAsync` stores files inside the process subdirectory
- [x] 5.3 Test: `DiskStorageStrategy.DeleteCacheDirectory` removes the entire subdirectory recursively
- [x] 5.4 Test: `DualTierFileCache` constructs correct `ZipDrive-{pid}` subdirectory
- [x] 5.5 Test: `DualTierFileCache.DeleteCacheDirectory` removes the process subdirectory
- [x] 5.6 Test: `DiskStorageStrategy` with null tempDirectory creates subdirectory under system temp
- [x] 5.7 Run full test suite — 249 tests pass (6 new + existing)
