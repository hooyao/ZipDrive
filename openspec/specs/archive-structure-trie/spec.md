## ADDED Requirements

### Requirement: Trie-based entry storage

`ArchiveStructure` SHALL use KTrie `TrieDictionary<ZipEntryInfo>` to store all ZIP entries. Directory entries SHALL be keyed with a trailing `/` (e.g., `"maps/"`). File entries SHALL be keyed without a trailing slash (e.g., `"maps/e1m1.wad"`). The trie SHALL always use case-sensitive (Ordinal) comparison per the ZIP specification.

#### Scenario: File entry lookup

- **WHEN** `GetEntry("maps/e1m1.wad")` is called
- **AND** the archive contains an entry at that path
- **THEN** the corresponding `ZipEntryInfo` is returned with correct metadata

#### Scenario: Directory entry lookup

- **WHEN** `GetEntry("maps/")` is called
- **AND** the archive contains a directory entry for `maps/`
- **THEN** a `ZipEntryInfo` with `IsDirectory = true` is returned

#### Scenario: Non-existent entry returns null

- **WHEN** `GetEntry("nonexistent.txt")` is called
- **AND** no such entry exists
- **THEN** `null` is returned

#### Scenario: Case-sensitive lookup

- **WHEN** `GetEntry("Maps/E1M1.WAD")` is called
- **AND** the archive contains `"maps/e1m1.wad"` (lowercase)
- **THEN** `null` is returned (case mismatch)

---

### Requirement: Directory existence check

`ArchiveStructure.DirectoryExists` SHALL check whether a directory entry exists in the trie. It SHALL accept paths with or without trailing slash.

#### Scenario: Directory exists with trailing slash

- **WHEN** `DirectoryExists("maps/")` is called
- **AND** the trie contains `"maps/"` with `IsDirectory = true`
- **THEN** the result is `true`

#### Scenario: Directory exists without trailing slash

- **WHEN** `DirectoryExists("maps")` is called
- **AND** the trie contains `"maps/"` with `IsDirectory = true`
- **THEN** the result is `true` (trailing slash is appended internally)

#### Scenario: File path is not a directory

- **WHEN** `DirectoryExists("readme.txt")` is called
- **AND** `"readme.txt"` is a file entry
- **THEN** the result is `false`

---

### Requirement: Directory listing via prefix enumeration

`ArchiveStructure.ListDirectory` SHALL return direct children of a directory by using trie prefix enumeration and filtering to one level of nesting.

#### Scenario: List root directory

- **WHEN** `ListDirectory("")` is called
- **AND** the archive contains `"maps/"`, `"maps/e1m1.wad"`, `"readme.txt"`
- **THEN** the result contains `("maps", ZipEntryInfo { IsDirectory=true })` and `("readme.txt", ZipEntryInfo { ... })`
- **AND** the result does NOT contain `"maps/e1m1.wad"` (nested, not direct child)

#### Scenario: List subdirectory

- **WHEN** `ListDirectory("maps")` is called
- **AND** the archive contains `"maps/e1m1.wad"`, `"maps/e1m2.wad"`, `"maps/textures/"`, `"maps/textures/brick.png"`
- **THEN** the result contains `("e1m1.wad", ...)`, `("e1m2.wad", ...)`, and `("textures", ZipEntryInfo { IsDirectory=true })`
- **AND** the result does NOT contain `("brick.png", ...)` (nested inside `textures/`)

#### Scenario: List empty directory

- **WHEN** `ListDirectory("empty")` is called
- **AND** the trie contains `"empty/"` but no children with prefix `"empty/"`
- **THEN** the result is empty

#### Scenario: Self-entry excluded from listing

- **WHEN** `ListDirectory("maps")` is called
- **THEN** the directory entry `"maps/"` itself is NOT included in the results

---

### Requirement: Synthesize missing parent directories

When building the structure trie from Central Directory entries, the builder SHALL create synthetic directory entries for any parent path segments that lack explicit directory entries in the ZIP.

#### Scenario: ZIP with no explicit directory entries

- **WHEN** a ZIP contains only `"docs/readme.txt"` and `"docs/guide.pdf"` (no `"docs/"` directory entry)
- **THEN** the structure trie contains a synthesized entry `"docs/"` with `IsDirectory = true`
- **AND** `DirectoryExists("docs")` returns `true`
- **AND** `ListDirectory("docs")` returns `"readme.txt"` and `"guide.pdf"`

#### Scenario: Deeply nested file without intermediate directories

- **WHEN** a ZIP contains only `"a/b/c/file.txt"` with no `"a/"`, `"a/b/"`, or `"a/b/c/"` entries
- **THEN** the structure trie contains synthesized entries for `"a/"`, `"a/b/"`, and `"a/b/c/"`
- **AND** `DirectoryExists("a/b")` returns `true`

#### Scenario: Explicit directory entry is not overwritten

- **WHEN** a ZIP contains both an explicit `"docs/"` directory entry and `"docs/readme.txt"`
- **THEN** the structure trie uses the explicit `"docs/"` entry (not a synthesized one)

---

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
