# file-content-cache Specification

## Purpose
TBD - created by archiving change formalize-cache-layer. Update Purpose after archive.
## Requirements
### Requirement: Borrow/Return Pattern

The cache SHALL use a borrow/return pattern where callers receive a handle that MUST be disposed after use. Entries with active handles (RefCount > 0) SHALL NOT be evicted.

#### Scenario: Borrowed entry protected from eviction

- **WHEN** a caller borrows an entry via `BorrowAsync()`
- **THEN** the entry's RefCount is incremented
- **AND** the entry cannot be evicted until the handle is disposed

#### Scenario: Disposing handle allows eviction

- **WHEN** a caller disposes the `ICacheHandle<T>` returned from `BorrowAsync()`
- **THEN** the entry's RefCount is decremented
- **AND** the entry becomes eligible for eviction when RefCount reaches 0

#### Scenario: Multiple borrowers share entry

- **WHEN** multiple callers borrow the same cache key concurrently
- **THEN** each caller receives a valid handle to the same cached data
- **AND** the entry's RefCount reflects the number of active handles

---

### Requirement: Cache Hit Fast Path

Cache lookups SHALL be lock-free for maximum performance. The fast path SHALL use `ConcurrentDictionary.TryGetValue()` with no locking. Cache hits SHALL be logged at Debug level and emit a metric counter increment.

#### Scenario: Cache hit returns without locking

- **WHEN** a cached entry exists and is not expired
- **THEN** the value is returned via lock-free dictionary lookup
- **AND** LastAccessedAt and AccessCount are updated atomically

#### Scenario: Hit rate tracking

- **WHEN** cache hits and misses occur
- **THEN** the cache SHALL track hit/miss counts
- **AND** expose a `HitRate` property (hits / total requests)

---

### Requirement: Thundering Herd Prevention

When multiple threads request the same uncached key simultaneously, the cache SHALL materialize the entry exactly once using `Lazy<Task<T>>` with `ExecutionAndPublication` mode.

#### Scenario: Concurrent requests for same key

- **WHEN** 10 threads request the same uncached key simultaneously
- **THEN** the factory is called exactly once
- **AND** all 10 threads receive valid handles to the materialized entry

#### Scenario: Different keys materialize in parallel

- **WHEN** threads request different uncached keys simultaneously
- **THEN** each key's factory runs in parallel
- **AND** one key's materialization does not block another

---

### Requirement: TTL-Based Expiration

Each cache entry SHALL have a configurable time-to-live (TTL). Entries past their TTL SHALL be considered expired and eligible for eviction.

#### Scenario: Entry expires after TTL

- **WHEN** an entry's TTL has elapsed since creation
- **THEN** subsequent lookups treat the entry as a cache miss
- **AND** the entry is eligible for eviction

#### Scenario: Expired entry evicted on capacity check

- **WHEN** the cache performs eviction due to capacity pressure
- **THEN** expired entries with RefCount = 0 SHALL be evicted first
- **AND** only non-expired entries are considered by the eviction policy

---

### Requirement: Capacity-Based Eviction

The cache SHALL enforce a maximum capacity in bytes. When capacity is exceeded, entries SHALL be evicted using the configured eviction policy.

#### Scenario: Eviction triggered on capacity exceeded

- **WHEN** adding a new entry would exceed capacity
- **THEN** the cache evicts entries with RefCount = 0 until sufficient space exists
- **AND** entries are selected by the `IEvictionPolicy.SelectVictims()` method

#### Scenario: Soft capacity overage allowed

- **WHEN** all entries are currently borrowed (RefCount > 0)
- **THEN** the cache allows temporary capacity overage
- **AND** logs a warning about the condition
- **AND** eviction resumes when entries become available

#### Scenario: Post-store eviction check

- **WHEN** a new entry is added to the cache
- **THEN** a post-store capacity check runs
- **AND** the newly added entry is temporarily protected during this check

---

### Requirement: Pluggable Eviction Policy

The cache SHALL accept an `IEvictionPolicy` implementation that selects entries for eviction. The policy receives only entries with RefCount = 0.

#### Scenario: LRU eviction policy

- **WHEN** using `LruEvictionPolicy`
- **THEN** entries are evicted in order of `LastAccessedAt` (oldest first)
- **AND** eviction continues until sufficient space is freed

#### Scenario: Custom eviction policy

- **WHEN** a custom `IEvictionPolicy` is provided
- **THEN** the cache uses that policy's `SelectVictims()` method
- **AND** the policy receives current/capacity bytes and all evictable entries

---

### Requirement: Pluggable Storage Strategy

The cache SHALL use `IStorageStrategy<T>` to abstract storage and retrieval. Different strategies handle memory, disk, and object storage.

#### Scenario: Memory storage strategy

- **WHEN** using `MemoryStorageStrategy`
- **THEN** data is stored as `byte[]` in memory
- **AND** retrieval returns a seekable `MemoryStream`
- **AND** cleanup is GC-based (no async cleanup required)

#### Scenario: Disk storage strategy

- **WHEN** using `DiskStorageStrategy`
- **THEN** data is stored as a `MemoryMappedFile` backed by a temp file
- **AND** retrieval returns a seekable `MemoryMappedViewStream`
- **AND** cleanup requires async file deletion

#### Scenario: Object storage strategy

- **WHEN** using `ObjectStorageStrategy<T>`
- **THEN** objects are stored directly (no serialization)
- **AND** retrieval returns the same object reference
- **AND** cleanup is GC-based

---

### Requirement: Async Cleanup Queue

For storage strategies that require async cleanup (e.g., file deletion), the cache SHALL queue disposed entries for background processing.

#### Scenario: Disk entry queued for cleanup

- **WHEN** a disk-backed entry is evicted
- **THEN** its `StoredEntry` is added to `_pendingCleanup` queue
- **AND** the cache size is decremented immediately
- **AND** actual file deletion occurs asynchronously

#### Scenario: ProcessPendingCleanup batch processing

- **WHEN** `ProcessPendingCleanup(maxItems)` is called
- **THEN** up to `maxItems` entries are dequeued and disposed
- **AND** errors are logged but do not stop processing

---

### Requirement: Factory Result with Metadata

The factory function SHALL return `CacheFactoryResult<T>` containing the value, its size in bytes, and optional metadata.

#### Scenario: Factory provides size

- **WHEN** a cache miss triggers the factory
- **THEN** the factory returns `CacheFactoryResult<T>` with `SizeBytes`
- **AND** the cache uses this size for capacity tracking

#### Scenario: Size determined during materialization

- **WHEN** decompressing a ZIP entry
- **THEN** the final size is discovered during decompression
- **AND** the factory reports the discovered size in the result

---

### Requirement: Clear Operations

The cache SHALL provide `Clear()` and `ClearAsync()` methods for forced cleanup during shutdown. These methods ignore RefCount and forcibly remove all entries.

#### Scenario: Synchronous clear

- **WHEN** `Clear()` is called
- **THEN** all entries are removed regardless of RefCount
- **AND** all stored data is disposed immediately
- **AND** pending cleanup queue is also processed

#### Scenario: Async clear with yielding

- **WHEN** `ClearAsync()` is called
- **THEN** entries are removed and disposed
- **AND** the operation yields every 10 entries
- **AND** supports cancellation via `CancellationToken`

---

### Requirement: Observable Metrics

The cache SHALL expose real-time metrics for monitoring and debugging. In addition to properties, the cache SHALL emit metrics via `System.Diagnostics.Metrics` instruments defined in `CacheTelemetry`.

#### Scenario: Capacity metrics

- **WHEN** querying cache state
- **THEN** `CurrentSizeBytes` returns the current total size of cached entries
- **AND** `CapacityBytes` returns the configured maximum capacity

#### Scenario: Entry metrics

- **WHEN** querying cache state
- **THEN** `EntryCount` returns the total number of cached entries
- **AND** `BorrowedEntryCount` returns entries with RefCount > 0

#### Scenario: Hit rate metric

- **WHEN** querying cache state
- **THEN** `HitRate` returns hits / (hits + misses) as a double 0.0 to 1.0

#### Scenario: Metrics emitted on cache hit

- **WHEN** a cache hit occurs in `BorrowAsync`
- **THEN** `CacheTelemetry.Hits` Counter SHALL be incremented with the instance's `tier` tag

#### Scenario: Metrics emitted on cache miss

- **WHEN** a cache miss occurs in `BorrowAsync`
- **THEN** `CacheTelemetry.Misses` Counter SHALL be incremented with the instance's `tier` tag

#### Scenario: Metrics emitted on materialization

- **WHEN** a cache entry is materialized
- **THEN** `CacheTelemetry.MaterializationDuration` Histogram SHALL record the factory execution time in milliseconds with `tier` and `size_bucket` tags

#### Scenario: Metrics emitted on eviction

- **WHEN** a cache entry is evicted
- **THEN** `CacheTelemetry.Evictions` Counter SHALL be incremented with `tier` and `reason` tags

---

### Requirement: Manual Expired Eviction

The cache SHALL provide an `EvictExpired()` method for manual cleanup of expired entries (only entries with RefCount = 0).

#### Scenario: Manual eviction of expired entries

- **WHEN** `EvictExpired()` is called
- **THEN** all expired entries with RefCount = 0 are removed
- **AND** their storage is disposed
- **AND** the count and bytes evicted are logged

---

### Requirement: TimeProvider Integration

The cache SHALL use `TimeProvider` for all time-based operations to enable deterministic testing.

#### Scenario: TTL uses TimeProvider

- **WHEN** calculating expiration
- **THEN** the cache uses `_timeProvider.GetUtcNow()`
- **AND** tests can inject `FakeTimeProvider` for deterministic behavior

#### Scenario: Last access timestamp

- **WHEN** an entry is accessed
- **THEN** `LastAccessedAt` is set using `_timeProvider.GetUtcNow()`

---

### Requirement: Cache instance name for metric tagging

Each `GenericCache<T>` instance SHALL accept an optional `string name` constructor parameter that identifies the cache instance for metric tagging purposes.

#### Scenario: Name used as tier tag on metrics

- **WHEN** a `GenericCache<T>` is constructed with `name: "memory"`
- **THEN** all metric recordings (counters, histograms) emitted by that instance SHALL include a `tier` tag with value `"memory"`

#### Scenario: Name defaults to type name

- **WHEN** a `GenericCache<T>` is constructed without a `name` parameter
- **THEN** the `tier` tag SHALL default to the generic type argument name (e.g., `"Stream"`, `"ArchiveStructure"`)

---

### Requirement: Structured lifecycle logging

The cache SHALL log lifecycle events (materialization, eviction, TTL expiry) at Information level with structured properties for observability.

#### Scenario: Materialization logged at Information level

- **WHEN** a cache entry is successfully materialized
- **THEN** the cache SHALL log at Information level with properties: `{Key}`, `{SizeBytes}`, `{SizeMb}`, `{Tier}`, `{MaterializationMs}`, `{UtilizationPct}`

#### Scenario: Eviction logged at Information level

- **WHEN** a cache entry is evicted (by policy, expiry, or manual)
- **THEN** the cache SHALL log at Information level with properties: `{Key}`, `{SizeBytes}`, `{Tier}`, `{Reason}`

#### Scenario: TTL expiry batch logged with tier

- **WHEN** expired entries are evicted in a batch
- **THEN** the cache SHALL log at Information level with properties: `{Count}`, `{SizeMb}`, `{Tier}`

---

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

---

