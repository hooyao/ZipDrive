## Why

When multiple ZipDrive processes mount different drive letters concurrently (e.g., Process 1 mounts R:\, Process 2 mounts S:\), all disk-tier cache files land in the same flat directory (`TempDirectory` or `%TEMP%`). While GUID-named files avoid collisions, this creates two problems:

1. **No process isolation** — orphaned `.zip2vd.cache` files from a crashed process are indistinguishable from files belonging to a running process. No way to clean up selectively.
2. **Cluttered shared directory** — hundreds of cache files from multiple processes intermixed in one flat directory, making debugging and manual cleanup difficult.

## What Changes

- **Per-process subdirectory**: `DiskStorageStrategy` creates a subdirectory `ZipDrive-{pid}-{mountLetter}` under the configured `TempDirectory` (or system temp). All disk cache files for that process are stored inside this subdirectory.
- **Directory cleanup on shutdown**: When the process exits cleanly, the entire `ZipDrive-{pid}-{mountLetter}` directory is deleted recursively (after the existing `Clear()` deletes individual cache files).
- **MountSettings dependency**: `DiskStorageStrategy` (or its caller) needs the mount point letter to construct the directory name.

## Capabilities

### Modified Capabilities

- `file-content-cache`: DiskStorageStrategy creates and cleans up a per-process subdirectory instead of storing files directly in TempDirectory.

## Impact

- **Modified files**: `DiskStorageStrategy.cs`, `DualTierFileCache.cs`, `CacheMaintenanceService.cs`, `appsettings.jsonc` (documentation comment only)
- **No new NuGet dependencies**
- **No breaking API changes** — `DiskStorageStrategy` constructor gains a `mountLetter` parameter, but this is internal infrastructure
- **Test updates**: Existing `DiskStorageStrategy` tests in `GenericCacheIntegrationTests.cs` may need minor updates for the new subdirectory structure. New tests verify directory creation, cleanup on shutdown, and multi-instance isolation.
