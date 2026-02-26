## MODIFIED Requirements

### Requirement: Pluggable Storage Strategy

The cache SHALL use `IStorageStrategy<T>` to abstract storage and retrieval. The strategy SHALL own the full materialization pipeline: it receives the factory delegate, calls it, consumes the result, disposes factory resources, and returns a `StoredEntry`.

#### Scenario: Memory storage strategy

- **WHEN** using `MemoryStorageStrategy`
- **THEN** `MaterializeAsync` SHALL call the factory, copy the stream to a `MemoryStream`, extract `byte[]` via `ToArray()`, and dispose the factory result
- **AND** retrieval returns a seekable `MemoryStream`
- **AND** cleanup is GC-based (no async cleanup required)

#### Scenario: Disk storage strategy

- **WHEN** using `DiskStorageStrategy`
- **THEN** `MaterializeAsync` SHALL call the factory and pipe the stream directly to a temp file via `CopyToAsync` (no intermediate buffer)
- **AND** the factory result SHALL be disposed after the stream is fully consumed (closing the source stream and any owning resources)
- **AND** a `MemoryMappedFile` SHALL be created from the temp file for random-access reads
- **AND** cleanup requires async file deletion

#### Scenario: Object storage strategy

- **WHEN** using `ObjectStorageStrategy<T>`
- **THEN** `MaterializeAsync` SHALL call the factory and store the object directly (no serialization)
- **AND** the factory result SHALL be disposed (no-op for non-disposable values)
- **AND** retrieval returns the same object reference
- **AND** cleanup is GC-based

#### Scenario: Strategy disposes factory result after consumption

- **WHEN** any storage strategy completes `MaterializeAsync`
- **THEN** the `CacheFactoryResult<T>` SHALL be disposed via `DisposeAsync()`
- **AND** this SHALL dispose `Value` (if disposable) and invoke `OnDisposed` (if set)

#### Scenario: Strategy handles factory exceptions

- **WHEN** the factory throws an exception during `MaterializeAsync`
- **THEN** the strategy SHALL propagate the exception
- **AND** no partially-written storage artifacts SHALL remain (temp files cleaned up)

---

### Requirement: Factory Result with Metadata

The factory function SHALL return `CacheFactoryResult<T>` implementing `IAsyncDisposable`, containing the value, its size in bytes, optional metadata, and an optional cleanup callback.

#### Scenario: Factory provides size

- **WHEN** a cache miss triggers the factory
- **THEN** the factory returns `CacheFactoryResult<T>` with `SizeBytes`
- **AND** the storage strategy uses this size for capacity tracking

#### Scenario: Factory result disposed after storage

- **WHEN** the storage strategy finishes consuming the factory result
- **THEN** `CacheFactoryResult<T>.DisposeAsync()` SHALL be called
- **AND** if `Value` implements `IAsyncDisposable`, it SHALL be disposed
- **AND** if `Value` implements `IDisposable` (but not `IAsyncDisposable`), it SHALL be disposed
- **AND** if `OnDisposed` is set, it SHALL be invoked after `Value` disposal

#### Scenario: OnDisposed callback chains resource cleanup

- **WHEN** a factory creates an `IZipReader` and a decompressed stream
- **THEN** it SHALL set `OnDisposed` to dispose the `IZipReader` after the stream is disposed
- **AND** the strategy's `await using` on the factory result SHALL trigger both disposals

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

When multiple threads request the same uncached key simultaneously, the cache SHALL materialize the entry exactly once using `Lazy<Task<T>>` with `ExecutionAndPublication` mode. The `MaterializeAndCacheAsync` method SHALL delegate to `strategy.MaterializeAsync(factory, ct)` for the actual data pipeline.

#### Scenario: Concurrent requests for same key

- **WHEN** 10 threads request the same uncached key simultaneously
- **THEN** the factory is called exactly once (inside `strategy.MaterializeAsync`)
- **AND** all 10 threads receive valid handles to the materialized entry

#### Scenario: Different keys materialize in parallel

- **WHEN** threads request different uncached keys simultaneously
- **THEN** each key's materialization runs in parallel
- **AND** one key's materialization does not block another

---

### Requirement: Capacity-Based Eviction

The cache SHALL enforce a maximum capacity in bytes. When capacity is exceeded after storing a new entry, a post-store eviction check SHALL run to converge back to capacity.

#### Scenario: Post-store eviction converges to capacity

- **WHEN** a new entry is materialized and stored
- **AND** `_currentSizeBytes` exceeds `_capacityBytes`
- **THEN** the cache SHALL run `EvictIfNeededAsync(0)` to evict entries with RefCount = 0
- **AND** the newly added entry SHALL be protected by a temporary RefCount increment during this check

#### Scenario: Soft capacity overage allowed

- **WHEN** all entries are currently borrowed (RefCount > 0)
- **THEN** the cache allows temporary capacity overage
- **AND** logs a warning about the condition
- **AND** eviction resumes when entries become available
