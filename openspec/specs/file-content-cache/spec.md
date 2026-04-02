## ADDED Requirements

### Requirement: Format-agnostic ReadAsync with extractor delegation
`FileContentCache.ReadAsync` SHALL accept `formatId` (string), `ArchiveEntryInfo entry`, and `internalPath` (string) instead of `ZipEntryInfo`. On cache miss, the factory delegate SHALL resolve `IArchiveEntryExtractor` via `IFormatRegistry.GetExtractor(formatId)` and call `ExtractAsync(archivePath, internalPath, ct)`. The `ExtractionResult.Stream` SHALL be passed as `CacheFactoryResult.Value` and `ExtractionResult.OnDisposed` as `CacheFactoryResult.OnDisposed`.

#### Scenario: Cache miss delegates to format extractor
- **WHEN** `ReadAsync` is called for an uncached RAR entry with `formatId == "rar"`
- **THEN** `IFormatRegistry.GetExtractor("rar")` is called
- **AND** the RAR extractor's `ExtractAsync` produces the decompressed stream
- **AND** the stream is cached and returned

#### Scenario: Cache hit bypasses extractor entirely
- **WHEN** `ReadAsync` is called for an already-cached entry (any format)
- **THEN** `IFormatRegistry` is not consulted
- **AND** the cached stream is returned directly

#### Scenario: formatId flows correctly for mixed archives
- **WHEN** a ZIP entry and a RAR entry are read in sequence
- **THEN** each cache miss uses the correct format's extractor

## MODIFIED Requirements

### Requirement: WarmAsync — Push Pre-Materialized Stream Into Cache
`WarmAsync` SHALL accept `ArchiveEntryInfo entry` (not `ZipEntryInfo`) for tier routing. The entry's `UncompressedSize` determines memory vs disk tier. Behavior is otherwise unchanged: stores the stream, no-op if already cached, immediate handle release.

#### Scenario: WarmAsync stores stream in correct tier
- **WHEN** `WarmAsync` is called with `ArchiveEntryInfo { UncompressedSize = 1MB }` and cutoff is 50MB
- **THEN** the stream is stored in the memory tier

#### Scenario: WarmAsync on existing key is a no-op
- **WHEN** `WarmAsync` is called with a key that already exists in the cache
- **THEN** no new entry is created and the existing entry is unchanged

#### Scenario: WarmAsync does not block caller
- **WHEN** `WarmAsync` returns
- **THEN** the cache handle has been released (RefCount == 0)

### Requirement: Factory Result with Metadata
The factory delegate SHALL return `CacheFactoryResult<Stream>` with `Value` set to the decompressed stream from `ExtractionResult.Stream`, `SizeBytes` from `ExtractionResult.SizeBytes`, and `OnDisposed` from `ExtractionResult.OnDisposed`. The `CacheFactoryResult` owns stream disposal — `ExtractionResult` is a plain DTO and SHALL NOT dispose the stream.

#### Scenario: Factory provides size
- **WHEN** the factory creates a result from an `ExtractionResult`
- **THEN** `SizeBytes` equals `ExtractionResult.SizeBytes`

#### Scenario: Size determined during materialization
- **WHEN** the storage strategy calls the factory
- **THEN** the resulting `SizeBytes` is used for capacity tracking
