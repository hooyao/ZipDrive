## Why

When multiple ZipDrive processes mount different drive letters concurrently (e.g., Process 1 mounts R:\, Process 2 mounts S:\), all disk-tier cache files land in the same flat directory (`TempDirectory` or `%TEMP%`). While GUID-named files avoid collisions, this creates two problems:

1. **No process isolation** — orphaned `.zip2vd.cache` files from a crashed process are indistinguishable from files belonging to a running process. No way to clean up selectively.
2. **Cluttered shared directory** — hundreds of cache files from multiple processes intermixed in one flat directory, making debugging and manual cleanup difficult.

## What Changes

- **Per-process subdirectory**: `DiskStorageStrategy` always creates a subdirectory `ZipDrive-{pid}` under the configured `TempDirectory` (or system temp). All disk cache files for that process are stored inside this subdirectory.
- **Directory cleanup on shutdown**: When the process exits cleanly, the entire `ZipDrive-{pid}` directory is deleted recursively (after the existing `Clear()` deletes individual cache files).

## Capabilities

### Modified Capabilities

- `file-content-cache`: DiskStorageStrategy creates and cleans up a per-process subdirectory instead of storing files directly in TempDirectory.

## Impact

- **Modified files**: `DiskStorageStrategy.cs`, `DualTierFileCache.cs`, `CacheMaintenanceService.cs`
- **No new NuGet dependencies**
- **No breaking API changes** — `DiskStorageStrategy` always creates the per-process subdirectory internally
- **Test updates**: Existing `DiskStorageStrategy` tests in `GenericCacheIntegrationTests.cs` updated with `SearchOption.AllDirectories` for the new subdirectory structure. New tests verify directory creation, cleanup on shutdown, and null-tempDirectory fallback.
