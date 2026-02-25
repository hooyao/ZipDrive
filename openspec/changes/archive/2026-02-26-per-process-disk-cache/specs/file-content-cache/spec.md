## Delta: Per-Process Disk Cache Directory

### Requirement: Per-process cache directory isolation

The disk storage strategy SHALL always create a per-process subdirectory under the configured TempDirectory (or system temp) to isolate cache files from concurrent ZipDrive processes.

#### Scenario: Process subdirectory created on startup

- **WHEN** `DiskStorageStrategy` is constructed
- **THEN** a directory named `ZipDrive-{pid}` is created under the base temp directory
- **AND** all subsequent cache files are stored inside this subdirectory
- **AND** the directory is created if it does not already exist

#### Scenario: Base temp directory created if missing

- **WHEN** the configured `TempDirectory` does not exist
- **THEN** the base directory is created first
- **AND** then the process subdirectory is created inside it

#### Scenario: Cache files stored in process subdirectory

- **WHEN** `StoreAsync` creates a new cache file
- **THEN** the file path is `{baseDir}/ZipDrive-{pid}/{guid}.zip2vd.cache`
- **AND** the file is accessible via memory-mapped file as before

---

### Requirement: Process subdirectory cleanup on shutdown

The disk storage strategy SHALL delete its entire process subdirectory on graceful shutdown, after individual cache files have been cleared.

#### Scenario: Directory deleted on clean shutdown

- **WHEN** `DeleteCacheDirectory()` is called after `Clear()`
- **THEN** the `ZipDrive-{pid}` directory is deleted recursively
- **AND** success is logged at Information level

#### Scenario: Directory deletion failure is non-fatal

- **WHEN** `DeleteCacheDirectory()` fails (e.g., file still locked by OS)
- **THEN** a warning is logged with the exception details
- **AND** the process continues to shut down normally
- **AND** the orphaned directory remains on disk

#### Scenario: CacheMaintenanceService invokes directory cleanup

- **WHEN** `CacheMaintenanceService` stops (stoppingToken cancelled)
- **THEN** it calls `_fileCache.Clear()` first (existing behavior)
- **THEN** it calls `_fileCache.DeleteCacheDirectory()` to remove the process subdirectory

---

### Requirement: Multi-instance isolation

Multiple concurrent ZipDrive processes SHALL have completely isolated disk cache directories.

#### Scenario: Two processes use separate directories

- **WHEN** Process A (PID 1000) and Process B (PID 2000) both run ZipDrive
- **AND** both use the same base `TempDirectory`
- **THEN** Process A's cache files are in `{baseDir}/ZipDrive-1000/`
- **AND** Process B's cache files are in `{baseDir}/ZipDrive-2000/`
- **AND** neither process interferes with the other's files
