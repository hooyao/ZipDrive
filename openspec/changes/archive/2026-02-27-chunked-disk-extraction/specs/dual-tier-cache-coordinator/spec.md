## MODIFIED Requirements

### Requirement: Disk tier uses DiskStorageStrategy with MemoryMappedFile

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

---

### Requirement: Dual-tier routing by file size

The system SHALL provide a `FileContentCache` that routes cache operations to either a memory tier or a disk tier based on the expected file size relative to `CacheOptions.SmallFileCutoffBytes`.

#### Scenario: Small file routed to memory tier

- **WHEN** a file with `UncompressedSize` < `SmallFileCutoffBytes` is requested via `ReadAsync`
- **THEN** the operation SHALL be delegated to the memory-tier `GenericCache<Stream>` (backed by `MemoryStorageStrategy`)

#### Scenario: Large file routed to disk tier

- **WHEN** a file with `UncompressedSize` >= `SmallFileCutoffBytes` is requested via `ReadAsync`
- **THEN** the operation SHALL be delegated to the disk-tier `GenericCache<Stream>` (backed by `ChunkedDiskStorageStrategy`)

#### Scenario: Default cutoff is 50 MB

- **WHEN** no custom `SmallFileCutoffMb` is configured
- **THEN** the cutoff SHALL default to 50 MB (52,428,800 bytes)
