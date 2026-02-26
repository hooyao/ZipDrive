## ADDED Requirements

### Requirement: IFileContentCache domain interface

The system SHALL provide an `IFileContentCache` interface in the Domain layer that abstracts file content reading from archives. Callers SHALL provide the archive path, entry metadata, cache key, output buffer, and read offset. The interface SHALL hide all caching, extraction, and tier-routing details from the caller.

#### Scenario: Read file content via IFileContentCache

- **WHEN** `ReadAsync(archivePath, entry, cacheKey, buffer, offset, ct)` is called
- **THEN** the implementation SHALL return decompressed bytes from the specified archive entry at the given offset
- **AND** the caller SHALL NOT need to know about `IZipReader`, storage strategies, or cache tiers

#### Scenario: Cache hit returns cached content

- **WHEN** `ReadAsync` is called for an entry that is already cached
- **THEN** the cached random-access stream SHALL be used
- **AND** no ZIP extraction SHALL occur

#### Scenario: Cache miss triggers extraction and caching

- **WHEN** `ReadAsync` is called for an entry not in cache
- **THEN** the implementation SHALL extract the entry from the archive, cache the result, and return the requested bytes

#### Scenario: Offset at or beyond EOF returns zero

- **WHEN** `ReadAsync` is called with an offset >= entry's `UncompressedSize`
- **THEN** the return value SHALL be 0

---

### Requirement: FileContentCache owns extraction pipeline

The `FileContentCache` implementation SHALL own the ZIP extraction logic. It SHALL create `IZipReader` instances, open decompressed streams, and pass them as factory delegates to `GenericCache<Stream>`.

#### Scenario: Factory creates IZipReader and decompressed stream

- **WHEN** a cache miss triggers the factory
- **THEN** the factory SHALL create an `IZipReader` for the archive path
- **AND** open a decompressed stream via `reader.OpenEntryStreamAsync(entry, ct)`
- **AND** return a `CacheFactoryResult<Stream>` with the raw stream and `entry.UncompressedSize` as `SizeBytes`

#### Scenario: Factory registers reader cleanup via OnDisposed

- **WHEN** the factory creates a `CacheFactoryResult<Stream>`
- **THEN** it SHALL set `OnDisposed` to a callback that disposes the `IZipReader`
- **AND** the strategy SHALL invoke this cleanup after consuming the stream

#### Scenario: Factory exception does not leak reader

- **WHEN** the factory throws during `OpenEntryStreamAsync`
- **THEN** the `IZipReader` SHALL be disposed before the exception propagates

---

### Requirement: Dual-tier routing within FileContentCache

`FileContentCache` SHALL route cache operations to a memory tier or disk tier based on the entry's `UncompressedSize` relative to `CacheOptions.SmallFileCutoffBytes`.

#### Scenario: Small file routed to memory tier

- **WHEN** an entry's `UncompressedSize` < `SmallFileCutoffBytes`
- **THEN** the operation SHALL be delegated to the memory-tier `GenericCache<Stream>`

#### Scenario: Large file routed to disk tier

- **WHEN** an entry's `UncompressedSize` >= `SmallFileCutoffBytes`
- **THEN** the operation SHALL be delegated to the disk-tier `GenericCache<Stream>`

---

### Requirement: Maintenance and lifecycle operations

`FileContentCache` SHALL expose `EvictExpired()`, `Clear()`, `DeleteCacheDirectory()`, and `ProcessPendingCleanup()` for use by `CacheMaintenanceService` and shutdown logic.

#### Scenario: EvictExpired delegates to both tiers

- **WHEN** `EvictExpired()` is called
- **THEN** both memory and disk tier caches SHALL have expired entries evicted

#### Scenario: Clear removes all entries from both tiers

- **WHEN** `Clear()` is called
- **THEN** all entries in both tiers SHALL be removed and disposed

#### Scenario: ProcessPendingCleanup processes both tiers

- **WHEN** `ProcessPendingCleanup(maxItems)` is called
- **THEN** up to `maxItems` pending cleanup items SHALL be processed per tier

---

### Requirement: Observable metrics on FileContentCache

`FileContentCache` SHALL expose aggregated metrics across both tiers for monitoring.

#### Scenario: CurrentSizeBytes sums both tiers

- **WHEN** `CurrentSizeBytes` is queried
- **THEN** the result SHALL be the sum of memory tier and disk tier current sizes

#### Scenario: EntryCount sums both tiers

- **WHEN** `EntryCount` is queried
- **THEN** the result SHALL be the sum of both tiers' entry counts

#### Scenario: BorrowedEntryCount sums both tiers

- **WHEN** `BorrowedEntryCount` is queried
- **THEN** the result SHALL be the sum of both tiers' borrowed entry counts
