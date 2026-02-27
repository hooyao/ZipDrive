# dual-tier-cache-coordinator Specification

## Purpose
Routes file content cache requests to memory or disk tier based on file size threshold. Wraps two `GenericCache<Stream>` instances behind a unified `ICache<Stream>` interface.

## Requirements
### Requirement: Dual-tier routing by file size

The system SHALL provide a `DualTierFileCache` that implements `ICache<Stream>` and routes cache operations to either a memory tier or a disk tier based on the expected file size relative to `CacheOptions.SmallFileCutoffBytes`.

#### Scenario: Small file routed to memory tier

- **WHEN** a file with `UncompressedSize` < `SmallFileCutoffBytes` is requested via `BorrowAsync`
- **THEN** the operation SHALL be delegated to the memory-tier `GenericCache<Stream>` (backed by `MemoryStorageStrategy`)

#### Scenario: Large file routed to disk tier

- **WHEN** a file with `UncompressedSize` >= `SmallFileCutoffBytes` is requested via `BorrowAsync`
- **THEN** the operation SHALL be delegated to the disk-tier `GenericCache<Stream>` (backed by `ChunkedDiskStorageStrategy`)

#### Scenario: Default cutoff is 50 MB

- **WHEN** no custom `SmallFileCutoffMb` is configured
- **THEN** the cutoff SHALL default to 50 MB (52,428,800 bytes)

---

### Requirement: Size hint for pre-materialization routing

The `DualTierFileCache` SHALL accept a size hint to determine tier routing before the factory executes. This avoids materializing data and then moving it to a different tier.

#### Scenario: Size hint provided via extended BorrowAsync

- **WHEN** `BorrowAsync` is called with a size hint parameter
- **THEN** the coordinator SHALL use the size hint to select the target tier before invoking the factory
- **AND** the factory SHALL execute within the context of the selected tier's `GenericCache<Stream>`

#### Scenario: Cache hit bypasses size hint

- **WHEN** `BorrowAsync` is called and the entry already exists in either tier
- **THEN** the cached entry SHALL be returned from whichever tier holds it
- **AND** the size hint SHALL NOT cause re-routing of an existing entry

---

### Requirement: Aggregated cache properties

The `DualTierFileCache` SHALL expose aggregated metrics that combine both tiers.

#### Scenario: CurrentSizeBytes sums both tiers

- **WHEN** `CurrentSizeBytes` is queried on the coordinator
- **THEN** the result SHALL be the sum of memory tier `CurrentSizeBytes` and disk tier `CurrentSizeBytes`

#### Scenario: EntryCount sums both tiers

- **WHEN** `EntryCount` is queried on the coordinator
- **THEN** the result SHALL be the sum of memory tier `EntryCount` and disk tier `EntryCount`

#### Scenario: HitRate combines both tiers

- **WHEN** `HitRate` is queried on the coordinator
- **THEN** the result SHALL reflect the combined hit rate across both tiers (total hits / total requests)

#### Scenario: BorrowedEntryCount sums both tiers

- **WHEN** `BorrowedEntryCount` is queried on the coordinator
- **THEN** the result SHALL be the sum of memory tier and disk tier borrowed entry counts

---

### Requirement: EvictExpired delegates to both tiers

The coordinator SHALL delegate eviction operations to both underlying caches.

#### Scenario: EvictExpired cleans both tiers

- **WHEN** `EvictExpired()` is called on the coordinator
- **THEN** `EvictExpired()` SHALL be called on both the memory tier and the disk tier

---

### Requirement: ICache<Stream> interface compliance

The `DualTierFileCache` SHALL implement `ICache<Stream>` without requiring changes to consumers.

#### Scenario: VFS uses coordinator transparently

- **WHEN** `ZipVirtualFileSystem` receives an `ICache<Stream>` via dependency injection
- **THEN** it SHALL work identically whether the injected instance is a `GenericCache<Stream>` or a `DualTierFileCache`
- **AND** no changes to `ZipVirtualFileSystem` source code SHALL be required beyond passing the size hint

---

### Requirement: Disk tier uses ChunkedDiskStorageStrategy

The disk tier SHALL use `ChunkedDiskStorageStrategy` to store decompressed file data as NTFS sparse files with incremental chunk-based extraction.

#### Scenario: Large file stored with chunked extraction

- **WHEN** a file >= `SmallFileCutoffBytes` is materialized
- **THEN** the decompressed data SHALL be extracted incrementally in fixed-size chunks to an NTFS sparse file
- **AND** `MaterializeAsync` SHALL return after the first chunk is extracted
- **AND** a background task SHALL continue extracting remaining chunks
- **AND** retrieval SHALL return a `ChunkedStream` that serves completed chunks instantly and blocks on unextracted regions
- **AND** the sparse file SHALL be deleted when the cache entry is evicted

#### Scenario: Disk tier capacity enforced

- **WHEN** the disk tier exceeds `CacheOptions.DiskCacheSizeBytes`
- **THEN** LRU eviction SHALL remove entries until the tier is within capacity
