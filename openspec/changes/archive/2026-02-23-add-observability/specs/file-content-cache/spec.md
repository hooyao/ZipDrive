## ADDED Requirements

### Requirement: Cache instance name for metric tagging

Each `GenericCache<T>` instance SHALL accept an optional `string name` constructor parameter that identifies the cache instance for metric tagging purposes.

#### Scenario: Name used as tier tag on metrics

- **WHEN** a `GenericCache<T>` is constructed with `name: "memory"`
- **THEN** all metric recordings (counters, histograms) emitted by that instance SHALL include a `tier` tag with value `"memory"`

#### Scenario: Name defaults to type name

- **WHEN** a `GenericCache<T>` is constructed without a `name` parameter
- **THEN** the `tier` tag SHALL default to the generic type argument name (e.g., `"Stream"`, `"ArchiveStructure"`)

---

## MODIFIED Requirements

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
