## ADDED Requirements

### Requirement: WarmAsync — Push Pre-Materialized Stream Into Cache
The `IFileContentCache` interface SHALL expose a `WarmAsync` method that accepts an already-decompressed stream and stores it in the appropriate cache tier without invoking the internal `IZipReader` extraction pipeline.

#### Scenario: WarmAsync stores stream in correct tier
- **WHEN** `WarmAsync` is called with a `ZipEntryInfo` whose `UncompressedSize` is below `SmallFileCutoffBytes`
- **THEN** the stream is stored in the memory tier

#### Scenario: WarmAsync on existing key is a no-op
- **WHEN** `WarmAsync` is called for a key that is already present in cache
- **THEN** the existing entry is returned unchanged
- **AND** no duplicate extraction occurs (thundering herd protection applies)

#### Scenario: WarmAsync does not block caller
- **WHEN** `WarmAsync` completes
- **THEN** the entry is in cache but has RefCount 0 (handle immediately disposed)
- **AND** the entry is eligible for LRU eviction if capacity is exceeded
