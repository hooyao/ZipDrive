## MODIFIED Requirements

### Requirement: Structure cache integration

`ArchiveStructure` instances SHALL be cached via `IArchiveStructureCache` using `GenericCache<ArchiveStructure>` with `ObjectStorageStrategy`. The structure trie is built once per cache miss and served from cache on subsequent accesses. `ArchiveStructureCache` SHALL accept an `IFilenameEncodingDetector` and a fallback `Encoding` as constructor parameters. During structure building, it SHALL partition entries by UTF-8 flag, buffer non-UTF8 entries for batch encoding detection, then decode filenames once with the detected encoding before trie insertion.

#### Scenario: Cache miss triggers structure build

- **WHEN** `IArchiveStructureCache.GetOrBuildAsync(archive)` is called for the first time
- **THEN** the ZIP Central Directory is parsed via `IZipReader`
- **AND** a `TrieDictionary<ZipEntryInfo>` is built
- **AND** the `ArchiveStructure` is cached

#### Scenario: Cache hit returns existing structure

- **WHEN** `GetOrBuildAsync(archive)` is called for an already-cached archive
- **THEN** the cached `ArchiveStructure` is returned without re-parsing

#### Scenario: Estimated memory tracked for capacity

- **WHEN** an `ArchiveStructure` is built with 10,000 entries
- **THEN** `EstimatedSizeBytes` is approximately `10000 * 114` bytes (~1.1 MB)
- **AND** this value is reported to the cache for capacity tracking

#### Scenario: Invalidation removes cached structure

- **WHEN** `Invalidate(archiveVirtualPath)` is called
- **THEN** the cached `ArchiveStructure` for that archive is removed
- **AND** the next `GetOrBuildAsync` call triggers a fresh build

#### Scenario: UTF-8 entries inserted immediately

- **WHEN** a ZIP entry has the UTF-8 flag (bit 11) set
- **THEN** its filename is decoded as UTF-8 and inserted into the trie during streaming
- **AND** no buffering or encoding detection is performed for this entry

#### Scenario: Non-UTF8 entries buffered for batch detection

- **WHEN** a ZIP entry does NOT have the UTF-8 flag set
- **THEN** the entry is buffered with its raw `FileNameBytes`
- **AND** the entry is NOT inserted into the trie until encoding detection completes

#### Scenario: Detected encoding applied to buffered entries

- **WHEN** all non-UTF8 entries have been buffered
- **AND** `IFilenameEncodingDetector.DetectEncoding` returns a high-confidence result
- **THEN** all buffered entries are decoded with the detected encoding
- **AND** decoded filenames are normalized and inserted into the trie

#### Scenario: Fallback encoding used when detection fails

- **WHEN** `IFilenameEncodingDetector.DetectEncoding` returns `null`
- **THEN** all buffered entries are decoded with the configured fallback encoding

#### Scenario: Shift-JIS archive produces correct trie keys

- **WHEN** a ZIP archive contains Shift-JIS encoded filenames without UTF-8 flag
- **THEN** the trie keys contain correctly decoded Japanese filenames
- **AND** `GetEntry` with the correct Japanese path returns the corresponding `ZipEntryInfo`

#### Scenario: Parent directories synthesized after encoding detection

- **WHEN** non-UTF8 entries are decoded and inserted into the trie
- **THEN** missing parent directories are synthesized using the decoded paths
- **AND** `DirectoryExists` returns `true` for parent segments of decoded paths

---

## ADDED Requirements

### Requirement: Encoding detection dependency injection

`ArchiveStructureCache` SHALL accept `IFilenameEncodingDetector` and `Encoding fallbackEncoding` as constructor parameters. The `IFilenameEncodingDetector` SHALL be registered in the DI container. The `Encoding` SHALL be resolved from `MountOptions.FallbackEncoding` in `Program.cs` and passed as a plain parameter to avoid circular project references between Caching and FileSystem.

#### Scenario: Constructor accepts detector and fallback

- **WHEN** `ArchiveStructureCache` is constructed
- **THEN** it accepts an `IFilenameEncodingDetector` parameter
- **AND** it accepts an `Encoding fallbackEncoding` parameter
- **AND** both are stored for use during structure building

#### Scenario: Null detector disables encoding detection

- **WHEN** `ArchiveStructureCache` is constructed with a null `IFilenameEncodingDetector`
- **THEN** all non-UTF8 entries are decoded with the fallback encoding directly
- **AND** no detection is attempted
