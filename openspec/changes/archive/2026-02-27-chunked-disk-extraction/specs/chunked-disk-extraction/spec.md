## ADDED Requirements

### Requirement: Chunked incremental extraction

The disk tier storage strategy SHALL extract decompressed file data in fixed-size chunks rather than extracting the entire file before serving reads. `MaterializeAsync` SHALL return after the first chunk is extracted, while a background task continues extracting remaining chunks.

#### Scenario: MaterializeAsync returns after first chunk

- **WHEN** `ChunkedDiskStorageStrategy.MaterializeAsync` is called
- **THEN** it SHALL create an NTFS sparse backing file sized to `UncompressedSize`
- **AND** start a background extraction task
- **AND** await only the first chunk's completion (~50ms for 10MB)
- **AND** return a `StoredEntry` with `SizeBytes` equal to the full `UncompressedSize`

#### Scenario: Background extraction continues sequentially

- **WHEN** `MaterializeAsync` returns after the first chunk
- **THEN** the background extraction task SHALL continue extracting chunks sequentially
- **AND** each chunk SHALL be written to the correct offset in the sparse backing file
- **AND** each chunk SHALL be signaled as complete via its `TaskCompletionSource<bool>`

#### Scenario: Full size reported for capacity accounting

- **WHEN** `MaterializeAsync` returns the `StoredEntry`
- **THEN** `SizeBytes` SHALL equal the full `UncompressedSize` (not the currently-extracted amount)
- **AND** `GenericCache` SHALL use this value for capacity tracking

---

### Requirement: Chunk size configuration

The chunk size for incremental extraction SHALL be configurable via `CacheOptions.ChunkSizeMb`.

#### Scenario: Default chunk size is 10 MB

- **WHEN** no custom `ChunkSizeMb` is configured
- **THEN** the chunk size SHALL default to 10 MB (10,485,760 bytes)

#### Scenario: Custom chunk size applied

- **WHEN** `ChunkSizeMb` is set to 4
- **THEN** all chunked extractions SHALL use 4 MB chunks
- **AND** a 200 MB file SHALL produce 50 chunks

#### Scenario: Last chunk may be smaller

- **WHEN** a file's `UncompressedSize` is not evenly divisible by chunk size
- **THEN** the last chunk SHALL contain the remaining bytes
- **AND** its length SHALL be `UncompressedSize - (ChunkCount - 1) * ChunkSize`

---

### Requirement: Chunk completion signaling

Each chunk SHALL have a `TaskCompletionSource<bool>` that signals when the chunk data is written to disk and ready for reading.

#### Scenario: Ready chunk returns instantly

- **WHEN** a reader requests data from a chunk that is already extracted
- **THEN** the read SHALL proceed immediately without any await
- **AND** the chunk's `BitArray` entry SHALL be `true`

#### Scenario: Pending chunk blocks reader

- **WHEN** a reader requests data from a chunk that is not yet extracted
- **THEN** the reader SHALL `await` the chunk's `TaskCompletionSource<bool>.Task`
- **AND** the reader SHALL be woken when the background extraction signals the TCS
- **AND** the reader SHALL then read from the backing file

#### Scenario: Extraction error propagated to waiting readers

- **WHEN** the background extraction task encounters an error (corrupt ZIP, I/O failure)
- **THEN** all pending `TaskCompletionSource<bool>` entries SHALL receive `TrySetException`
- **AND** readers awaiting those chunks SHALL receive the exception

#### Scenario: Extraction cancellation propagated to waiting readers

- **WHEN** the background extraction task is cancelled (eviction or shutdown)
- **THEN** all pending `TaskCompletionSource<bool>` entries SHALL receive `TrySetCanceled`
- **AND** readers awaiting those chunks SHALL receive `TaskCanceledException`

---

### Requirement: ChunkedStream transparent read interface

`ChunkedStream` SHALL implement `Stream` and transparently handle chunk-aware reads. Callers SHALL NOT need to know about the chunking mechanism.

#### Scenario: Sequential read from completed chunks

- **WHEN** a caller reads sequentially from offset 0
- **THEN** `ChunkedStream` SHALL serve data from completed chunks without blocking
- **AND** the caller SHALL see a normal seekable `Stream`

#### Scenario: Read across chunk boundary

- **WHEN** a single `Read` call spans two or more chunks
- **THEN** `ChunkedStream` SHALL read from the first chunk, then the next
- **AND** each chunk SHALL be verified as ready before reading
- **AND** the total bytes returned SHALL be the sum of bytes from all spanned chunks

#### Scenario: Seek to arbitrary offset

- **WHEN** a caller sets `Position` to an arbitrary offset
- **THEN** subsequent reads SHALL start from that offset
- **AND** the corresponding chunk SHALL be awaited if not yet extracted

#### Scenario: Read beyond EOF returns zero

- **WHEN** a caller reads at or beyond `UncompressedSize`
- **THEN** `ChunkedStream` SHALL return 0 bytes

#### Scenario: Synchronous Read supported

- **WHEN** a caller invokes `Stream.Read()` (synchronous overload)
- **THEN** `ChunkedStream` SHALL delegate to `ReadAsync` via `GetAwaiter().GetResult()`

---

### Requirement: Concurrent readers during extraction

Multiple readers SHALL be able to access completed chunks concurrently while the background extraction task continues writing subsequent chunks.

#### Scenario: Multiple readers on same entry

- **WHEN** 100 readers borrow the same cache key concurrently
- **THEN** each SHALL receive an independent `ChunkedStream` instance
- **AND** each `ChunkedStream` SHALL have its own `FileStream` and position
- **AND** reads to completed chunks SHALL proceed independently and concurrently

#### Scenario: Reader and extractor access backing file concurrently

- **WHEN** the extraction task writes to chunk N
- **AND** a reader reads from chunk M (where M < N and chunk M is completed)
- **THEN** both operations SHALL proceed without interference
- **AND** the backing file SHALL be opened with `FileShare.Read` by the writer and `FileShare.ReadWrite` by readers

#### Scenario: Readers do not interfere with each other

- **WHEN** two readers read from different offsets within the same completed chunk
- **THEN** each reader's `FileStream` position SHALL be independent
- **AND** neither reader SHALL affect the other's reads

---

### Requirement: Data integrity safety gate

`ChunkedStream` SHALL NEVER read from the sparse backing file without first verifying that the target chunk is extracted. Reading from an unextracted sparse file region would return zeros silently — the `EnsureChunkReadyAsync` check is load-bearing for data integrity.

#### Scenario: EnsureChunkReadyAsync called before every read

- **WHEN** `ChunkedStream.ReadAsync` is called for any offset
- **THEN** `EnsureChunkReadyAsync` SHALL be called for the target chunk index
- **AND** the read SHALL NOT proceed until the chunk is confirmed ready

#### Scenario: BitArray fast path for ready chunks

- **WHEN** `EnsureChunkReadyAsync` checks a chunk that is already extracted
- **THEN** it SHALL return immediately via a `BitArray` check (no async await, no TCS access)

---

### Requirement: Extraction cancellation on eviction

When a chunked entry is evicted, the background extraction task SHALL be cancelled and all resources cleaned up.

#### Scenario: Eviction cancels active extraction

- **WHEN** `ChunkedDiskStorageStrategy.Dispose` is called on an entry with a running extraction task
- **THEN** the `CancellationTokenSource` SHALL be cancelled
- **AND** the extraction task SHALL observe the cancellation and stop
- **AND** all pending `TaskCompletionSource` entries SHALL receive `TrySetCanceled`

#### Scenario: Backing file deleted after extraction stops

- **WHEN** an entry is disposed after extraction cancellation
- **THEN** the sparse backing file SHALL be deleted
- **AND** the `CancellationTokenSource` SHALL be disposed

#### Scenario: Async cleanup via pending queue

- **WHEN** a chunked entry is evicted by `GenericCache`
- **THEN** the `StoredEntry` SHALL be queued in `_pendingCleanup`
- **AND** `ProcessPendingCleanup` SHALL call `ChunkedDiskStorageStrategy.Dispose` to perform actual cleanup

---

### Requirement: Greedy extraction strategy

The background extraction task SHALL continue extracting all chunks to completion even when no readers are actively borrowing the entry, as long as the entry remains in the cache.

#### Scenario: Extraction continues after all readers disconnect

- **WHEN** all `ICacheHandle<Stream>` borrowers dispose their handles (RefCount drops to 0)
- **AND** the entry has not been evicted
- **THEN** the background extraction task SHALL continue until all chunks are extracted

#### Scenario: Extraction completes resource cleanup

- **WHEN** the background extraction task finishes extracting all chunks
- **THEN** it SHALL dispose the `DeflateStream` and invoke the `CacheFactoryResult.OnDisposed` callback
- **AND** the `ZipReader` holding the archive file handle SHALL be released

---

### Requirement: Chunked extraction telemetry

The chunked extraction system SHALL emit metrics for monitoring extraction progress and chunk wait behavior.

#### Scenario: Chunk extraction counted

- **WHEN** a chunk is successfully extracted and written to disk
- **THEN** a `cache.chunks.extracted` counter SHALL be incremented

#### Scenario: Chunk wait counted and timed

- **WHEN** a reader must wait for a chunk to be extracted
- **THEN** a `cache.chunks.waits` counter SHALL be incremented
- **AND** a `cache.chunks.wait_duration` histogram SHALL record the wait time in milliseconds

#### Scenario: Extraction start and completion logged

- **WHEN** chunked extraction starts for an entry
- **THEN** an Information-level log SHALL be emitted with `{Key}`, `{FileSize}`, `{ChunkCount}`, `{ChunkSize}`
- **AND** when extraction completes, an Information-level log SHALL be emitted with `{Key}`, `{FileSize}`, `{ElapsedMs}`, `{ThroughputMbps}`
