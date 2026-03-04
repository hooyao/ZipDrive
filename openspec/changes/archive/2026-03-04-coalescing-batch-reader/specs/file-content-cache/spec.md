## MODIFIED Requirements

### Requirement: Cache Hit Fast Path

Cache lookups SHALL be lock-free for maximum performance. The fast path SHALL use `ConcurrentDictionary.TryGetValue()` with no locking. Cache hits SHALL be logged at Debug level and emit a metric counter increment. Memory-tier hits SHALL bypass the coalescing coordinator entirely.

#### Scenario: Cache hit returns without locking
- **WHEN** a cached entry exists and is not expired
- **THEN** the value is returned via lock-free dictionary lookup
- **AND** LastAccessedAt and AccessCount are updated atomically

#### Scenario: Hit rate tracking
- **WHEN** cache hits and misses occur
- **THEN** the cache SHALL track hit/miss counts
- **AND** expose a `HitRate` property (hits / total requests)

#### Scenario: Memory-tier hit bypasses coalescing
- **WHEN** a memory-tier cache hit occurs
- **THEN** the coalescing coordinator SHALL NOT be invoked
- **AND** the hit is served immediately via the lock-free path

---

## ADDED Requirements

### Requirement: Memory-Tier Miss Routes Through Coalescing Coordinator
When a memory-tier cache miss occurs and `CoalescingOptions.Enabled` is `true`, `FileContentCache` SHALL submit the request to the `CoalescingBatchCoordinator` rather than invoking the extraction factory directly.

#### Scenario: Memory-tier miss with coalescing enabled
- **WHEN** `ReadAsync()` is called for an entry with `UncompressedSize < SmallFileCutoffBytes`
- **AND** the entry is not present in the memory cache
- **AND** `CoalescingOptions.Enabled` is `true`
- **THEN** the request SHALL be submitted to `CoalescingBatchCoordinator.SubmitAsync()`
- **AND** `ReadAsync()` SHALL await the `TaskCompletionSource` result from the coordinator

#### Scenario: Memory-tier miss with coalescing disabled
- **WHEN** `ReadAsync()` is called for an entry with `UncompressedSize < SmallFileCutoffBytes`
- **AND** `CoalescingOptions.Enabled` is `false`
- **THEN** the existing factory delegate path SHALL be used directly
- **AND** behavior SHALL be identical to pre-feature code
