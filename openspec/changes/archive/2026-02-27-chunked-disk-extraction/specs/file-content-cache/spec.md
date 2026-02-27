## MODIFIED Requirements

### Requirement: Pluggable Storage Strategy

The cache SHALL use `IStorageStrategy<T>` to abstract storage and retrieval. Different strategies handle memory, disk, and object storage.

#### Scenario: Memory storage strategy

- **WHEN** using `MemoryStorageStrategy`
- **THEN** data is stored as `byte[]` in memory
- **AND** retrieval returns a seekable `MemoryStream`
- **AND** cleanup is GC-based (no async cleanup required)

#### Scenario: Chunked disk storage strategy

- **WHEN** using `ChunkedDiskStorageStrategy`
- **THEN** data is extracted incrementally in fixed-size chunks to an NTFS sparse file
- **AND** `MaterializeAsync` SHALL return after the first chunk is extracted
- **AND** a background task SHALL continue extracting remaining chunks
- **AND** retrieval returns a `ChunkedStream` that blocks on unextracted regions
- **AND** cleanup cancels the background extraction and deletes the sparse file
- **AND** cleanup requires async file deletion

#### Scenario: Object storage strategy

- **WHEN** using `ObjectStorageStrategy<T>`
- **THEN** objects are stored directly (no serialization)
- **AND** retrieval returns the same object reference
- **AND** cleanup is GC-based

---

### Requirement: Per-process cache directory isolation

The disk storage strategy SHALL always create a per-process subdirectory under the configured TempDirectory (or system temp) to isolate cache files from concurrent ZipDrive processes.

#### Scenario: Process subdirectory created on startup

- **WHEN** `ChunkedDiskStorageStrategy` is constructed
- **THEN** a directory named `ZipDrive-{pid}` is created under the base temp directory
- **AND** all subsequent cache files are stored inside this subdirectory
- **AND** the directory is created if it does not already exist

#### Scenario: Base temp directory created if missing

- **WHEN** the configured `TempDirectory` does not exist
- **THEN** the base directory is created first
- **AND** then the process subdirectory is created inside it

#### Scenario: Cache files stored in process subdirectory

- **WHEN** `MaterializeAsync` creates a new cache file
- **THEN** the file path is `{baseDir}/ZipDrive-{pid}/{guid}.zip2vd.chunked`
- **AND** the file is an NTFS sparse file sized to the full `UncompressedSize`

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
