## ADDED Requirements

### Requirement: Coalescing Window Collection
The coordinator SHALL collect concurrent memory-tier cache-miss requests for the same archive within a configurable time window before dispatching a batch. The window SHALL use a two-timer adaptive strategy: a short `FastPathMs` timer fires immediately if no second request arrives, and a full `WindowMs` timer is used once a burst is detected.

#### Scenario: Isolated read fires after FastPathMs
- **WHEN** a single cache-miss request arrives for an archive
- **AND** no second request arrives within `FastPathMs` milliseconds
- **THEN** the entry SHALL be extracted immediately via the standard single-entry path
- **AND** no extra latency beyond `FastPathMs` is incurred

#### Scenario: Burst triggers full window
- **WHEN** a first cache-miss request arrives for an archive
- **AND** a second request arrives within `FastPathMs` milliseconds
- **THEN** the coordinator SHALL extend the collection window to `WindowMs` milliseconds
- **AND** all requests arriving within `WindowMs` of the first request SHALL be included in the batch

#### Scenario: Window fires at WindowMs expiry
- **WHEN** the full `WindowMs` window elapses
- **THEN** the coordinator SHALL dispatch a batch with all collected requests regardless of how many are pending

---

### Requirement: Batch Grouping by Physical Proximity
The coordinator SHALL sort collected requests by `LocalHeaderOffset` and group them into batches where the density of requested bytes meets or exceeds `DensityThreshold`.

#### Scenario: Density calculation
- **WHEN** grouping entries into a batch candidate
- **THEN** density SHALL be computed as: `sum(CompressedSize of requested entries) / (lastEntry.end - firstEntry.start)`
- **AND** `lastEntry.end = lastEntry.LocalHeaderOffset + lastEntry.CompressedSize + 30` (estimated local header overhead)

#### Scenario: Entries above threshold batched together
- **WHEN** adding the next entry keeps batch density >= `DensityThreshold`
- **THEN** the entry SHALL be included in the current batch

#### Scenario: Entry below threshold starts new batch
- **WHEN** adding the next entry would drop batch density below `DensityThreshold`
- **THEN** the current batch SHALL be closed
- **AND** a new batch SHALL be started with the excluded entry as its first member

#### Scenario: Single-entry batch
- **WHEN** a batch contains only one entry
- **THEN** it SHALL be extracted via the standard single-entry path (no sequential pass overhead)

---

### Requirement: Sequential Batch Extraction
For each batch group, the coordinator SHALL open a single `ZipReader`, seek once to the first entry's `LocalHeaderOffset`, and read entries sequentially without additional seeks.

#### Scenario: Single seek per batch
- **WHEN** a batch of N entries is dispatched
- **THEN** exactly one `FileStream.Seek()` SHALL occur (to the first entry's `LocalHeaderOffset`)
- **AND** subsequent entries SHALL be read by advancing the stream position forward only

#### Scenario: Hole entries skipped without decompression
- **WHEN** `SpeculativeCache` is `false`
- **AND** a hole entry (unrequested entry physically between two requested entries) is encountered
- **THEN** the coordinator SHALL read the 30-byte fixed Local Header to obtain `FileNameLength` and `ExtraFieldLength`
- **AND** SHALL advance the stream by `FileNameLength + ExtraFieldLength + CompressedSize` bytes
- **AND** SHALL NOT decompress the hole entry's data

#### Scenario: Hole entries speculatively cached
- **WHEN** `SpeculativeCache` is `true`
- **AND** a hole entry is encountered during a sequential pass
- **AND** the hole entry is not already present in the memory cache
- **THEN** the coordinator SHALL decompress the hole entry
- **AND** SHALL populate the memory cache for the hole entry's key
- **AND** the hole entry's cache handle SHALL be released immediately (no caller waiting for it)

#### Scenario: Callers unblocked as each entry completes
- **WHEN** the batch runner finishes decompressing an entry
- **THEN** the original caller waiting for that entry SHALL be unblocked immediately
- **AND** the batch runner SHALL continue extracting subsequent entries without waiting for callers to consume their results

---

### Requirement: Integration with GenericCache
The coordinator SHALL use `GenericCache.BorrowAsync()` to populate cache entries, preserving existing TTL, reference counting, and thundering-herd protection.

#### Scenario: BorrowAsync called per entry in batch
- **WHEN** the batch runner extracts an entry
- **THEN** it SHALL call `cache.BorrowAsync(cacheKey, ttl, factory, ct)` where the factory reads from the already-positioned sequential stream
- **AND** the resulting `ICacheHandle<Stream>` SHALL be passed to the original caller's `TaskCompletionSource`

#### Scenario: Concurrent solo materialization not disrupted
- **WHEN** a batch is running for entry X
- **AND** a concurrent independent caller has already started materializing entry X via the standard path
- **THEN** `BorrowAsync`'s existing `Lazy<Task<T>>` protection SHALL return the in-progress materialization
- **AND** the batch runner SHALL not start a duplicate extraction for entry X

---

### Requirement: Memory-Tier Exclusivity
The coalescing coordinator SHALL only be invoked for memory-tier cache misses (entries with `UncompressedSize < SmallFileCutoffBytes`). Disk-tier entries SHALL always use the existing `ChunkedDiskStorageStrategy` path.

#### Scenario: Large file bypasses coalescing
- **WHEN** `FileContentCache.ReadAsync()` is called for an entry with `UncompressedSize >= SmallFileCutoffBytes`
- **THEN** the request SHALL be routed directly to the disk cache
- **AND** the coalescing coordinator SHALL not be invoked

#### Scenario: Small file routes through coalescing
- **WHEN** `FileContentCache.ReadAsync()` is called for an entry with `UncompressedSize < SmallFileCutoffBytes`
- **AND** the entry is not in the memory cache
- **THEN** the request SHALL be submitted to the coalescing coordinator

---

### Requirement: Coalescing Disabled Bypass
When `CoalescingOptions.Enabled` is `false`, `FileContentCache` SHALL bypass the coordinator entirely and use the existing per-entry extraction path with identical behavior to pre-feature code.

#### Scenario: Disabled coalescing restores original behavior
- **WHEN** `Coalescing:Enabled` is `false` in configuration
- **AND** a memory-tier cache miss occurs
- **THEN** the entry SHALL be extracted immediately via the existing factory delegate
- **AND** no window delay SHALL be incurred

---

### Requirement: Coalescing Telemetry
The coordinator SHALL emit metrics for observability of batch behavior.

#### Scenario: Batch fired counter incremented
- **WHEN** a batch is dispatched to the sequential reader
- **THEN** `CacheTelemetry` SHALL increment a `coalescing.batches_fired` counter

#### Scenario: Entries per batch recorded
- **WHEN** a batch completes
- **THEN** `CacheTelemetry` SHALL record a `coalescing.entries_per_batch` histogram value with the count of requested entries in the batch

#### Scenario: Speculative entries cached counter
- **WHEN** a hole entry is speculatively cached
- **THEN** `CacheTelemetry` SHALL increment a `coalescing.speculative_cached` counter

---

### Requirement: Coalescing Configuration
The coalescing coordinator SHALL be configured via a dedicated `Coalescing` section in `appsettings.jsonc`.

#### Scenario: Default configuration is safe and effective
- **WHEN** no `Coalescing` section is present in configuration
- **THEN** defaults SHALL be: `Enabled=true`, `FastPathMs=20`, `WindowMs=500`, `DensityThreshold=0.8`, `SpeculativeCache=false`

#### Scenario: Configuration values bound from appsettings
- **WHEN** `appsettings.jsonc` contains a `Coalescing` section
- **THEN** all five properties SHALL be bound to `CoalescingOptions`
- **AND** `CoalescingOptions` SHALL be available via DI as `IOptions<CoalescingOptions>`
