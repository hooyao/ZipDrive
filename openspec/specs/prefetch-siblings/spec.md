# prefetch-siblings Specification

## Purpose
TBD - created by archiving change prefetch-siblings. Update Purpose after archive.
## Requirements
### Requirement: Sibling Prefetch On File Read
When a file is read from a ZIP archive, the system SHALL asynchronously warm sibling files in the same directory into the cache using a single sequential read of the ZIP, without blocking the triggering read response.

#### Scenario: Prefetch fires after first file read
- **WHEN** `ReadFileAsync` is called for a file inside a ZIP archive
- **THEN** `PrefetchSiblingsAsync` is dispatched fire-and-forget after the read is initiated
- **AND** the `ReadFileAsync` response is not delayed by the prefetch operation

#### Scenario: Subsequent sibling reads are cache hits
- **WHEN** a sibling file in the same directory is read after prefetch has completed
- **THEN** the sibling is served from cache as a hit with no extraction required

---

### Requirement: Sibling Prefetch On Directory Listing
When a directory inside a ZIP archive is listed, the system SHALL asynchronously warm all qualifying small files in that directory into the cache.

#### Scenario: Prefetch fires after directory listing
- **WHEN** `FindFilesAsync` is called for a directory inside a ZIP archive
- **THEN** `PrefetchSiblingsAsync` is dispatched fire-and-forget after the listing completes
- **AND** the listing response is not delayed by the prefetch operation

#### Scenario: Explorer thumbnail generation benefits from listing trigger
- **WHEN** Windows Explorer lists a directory and then reads each file for thumbnail generation
- **THEN** files are already warm in cache before individual `ReadFile` calls arrive

---

### Requirement: Span Selection Algorithm
The system SHALL select a contiguous span of sibling entries around a trigger file that maximizes the fill ratio (wanted bytes / total span bytes), respecting configurable knobs.

#### Scenario: High-density span selected
- **WHEN** all siblings are tightly packed in the ZIP with no large holes
- **THEN** all siblings within `PrefetchMaxFiles` are included in the span

#### Scenario: Sparse span shrinks to meet fill ratio
- **WHEN** a large hole entry exists between two wanted siblings such that fill ratio < `PrefetchFillRatioThreshold`
- **THEN** the endpoint that creates the largest hole is removed
- **AND** the algorithm repeats until fill ratio meets the threshold or window has one entry

#### Scenario: Large directory is capped before span selection
- **WHEN** a directory contains more files than `PrefetchMaxDirectoryFiles`
- **THEN** only the `PrefetchMaxDirectoryFiles` files nearest to the trigger by `LocalHeaderOffset` are considered
- **AND** span selection runs on this reduced candidate set

#### Scenario: Span capped at MaxFiles
- **WHEN** the candidate window after directory cap contains more than `PrefetchMaxFiles` entries
- **THEN** a centered window of `PrefetchMaxFiles` around the trigger is selected before span selection

---

### Requirement: Sequential ZIP Read With Hole Discard
The prefetch SHALL perform a single sequential read of the selected span, decompressing wanted files and reading-but-discarding hole entries without decompressing them.

#### Scenario: Single seek for entire span
- **WHEN** a prefetch plan covers N sibling files with holes between them
- **THEN** the archive is seeked exactly once to the span start
- **AND** all entries are processed in a single forward pass

#### Scenario: Hole bytes read but not decompressed
- **WHEN** a non-target entry (hole) lies between two wanted entries in the span
- **THEN** its compressed bytes are read and discarded without calling any decompressor
- **AND** stream position advances to the next entry's local header

#### Scenario: Wanted entry decompressed and warmed
- **WHEN** a wanted entry is reached during sequential scan
- **THEN** its compressed bytes are read and decompressed
- **AND** the decompressed stream is pushed into the cache via `WarmAsync`

---

### Requirement: Per-Directory In-Flight Deduplication
The system SHALL prevent duplicate concurrent sequential reads of the same directory by maintaining a per-directory in-flight guard.

#### Scenario: Second concurrent prefetch for same directory is skipped
- **WHEN** two `ReadFile` calls for files in the same directory trigger prefetch concurrently
- **THEN** the first prefetch proceeds with the sequential read
- **AND** the second prefetch returns immediately without performing I/O
- **AND** `GenericCache` thundering herd protection handles any residual concurrent `WarmAsync` calls for individual entries

#### Scenario: Guard released after prefetch completes
- **WHEN** a prefetch for a directory completes (success or error)
- **THEN** the in-flight guard for that directory is removed
- **AND** a subsequent prefetch trigger for that directory can proceed

---

### Requirement: Prefetch Configuration
All prefetch behavior SHALL be configurable via `CacheOptions` with defaults that are safe for production use.

#### Scenario: Prefetch disabled by config
- **WHEN** `Cache:PrefetchEnabled` is set to `false`
- **THEN** no prefetch is triggered on any `ReadFileAsync` or `FindFilesAsync` call

#### Scenario: File size threshold filters large files
- **WHEN** a sibling file's uncompressed size exceeds `PrefetchFileSizeThresholdMb`
- **THEN** that sibling is excluded from prefetch candidates

#### Scenario: Default configuration is safe
- **WHEN** no prefetch config is specified
- **THEN** defaults are: `PrefetchEnabled=true`, `PrefetchFileSizeThresholdMb=10`, `PrefetchMaxFiles=20`, `PrefetchMaxDirectoryFiles=300`, `PrefetchFillRatioThreshold=0.80`

---

### Requirement: Prefetch Telemetry
The system SHALL emit metrics for prefetch operations to enable observability.

#### Scenario: Prefetch metrics emitted
- **WHEN** a prefetch operation completes
- **THEN** counters are incremented for files warmed and bytes read
- **AND** a histogram records the span read duration
- **AND** skipped-due-to-in-flight events are counted separately

