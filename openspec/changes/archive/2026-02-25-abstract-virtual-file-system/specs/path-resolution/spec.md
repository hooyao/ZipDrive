## ADDED Requirements

### Requirement: Path normalization

The `IPathResolver` SHALL normalize input paths before resolution. Normalization SHALL convert backslashes to forward slashes, remove leading and trailing slashes, and collapse consecutive slashes.

#### Scenario: Backslash conversion

- **WHEN** `Resolve("\\games\\doom.zip\\maps\\e1m1.wad")` is called
- **THEN** the path is normalized to `"games/doom.zip/maps/e1m1.wad"` before trie lookup

#### Scenario: Leading and trailing slash removal

- **WHEN** `Resolve("/games/doom.zip/")` is called
- **THEN** the path is normalized to `"games/doom.zip"` before trie lookup

#### Scenario: Consecutive slash collapse

- **WHEN** `Resolve("games//doom.zip///maps/e1m1.wad")` is called
- **THEN** the path is normalized to `"games/doom.zip/maps/e1m1.wad"` before trie lookup

#### Scenario: Empty and null paths

- **WHEN** `Resolve("")` or `Resolve(null)` is called
- **THEN** the result has `Status = VirtualRoot`

---

### Requirement: Two-part path resolution result

The `IPathResolver` SHALL produce an `ArchiveTrieResult` that clearly separates the archive identity from the internal path. The result SHALL include an `ArchiveTrieStatus` enum indicating the path type.

#### Scenario: Path inside an archive produces ArchiveEntryLocation

- **WHEN** `Resolve("games/doom.zip/maps/e1m1.wad")` is called
- **THEN** the result has `Status = InsideArchive`
- **AND** `Archive` is the `ArchiveDescriptor` for `"games/doom.zip"`
- **AND** `InternalPath` is `"maps/e1m1.wad"`

#### Scenario: Archive root produces empty internal path

- **WHEN** `Resolve("games/doom.zip")` is called
- **THEN** the result has `Status = ArchiveRoot`
- **AND** `InternalPath` is `""`

#### Scenario: Virtual folder produces folder path

- **WHEN** `Resolve("games")` is called
- **AND** `"games"` is a derived virtual folder
- **THEN** the result has `Status = VirtualFolder`
- **AND** `VirtualFolderPath` is `"games"`

#### Scenario: Virtual root

- **WHEN** `Resolve("")` is called
- **THEN** the result has `Status = VirtualRoot`

#### Scenario: Non-existent path

- **WHEN** `Resolve("does/not/exist")` is called
- **AND** no archive or virtual folder matches
- **THEN** the result has `Status = NotFound`

---

### Requirement: Path resolver delegates to archive trie

The `IPathResolver` SHALL delegate archive boundary detection to `IArchiveTrie.Resolve()`. The path resolver is responsible for normalization; the trie is responsible for matching.

#### Scenario: Resolver normalizes then delegates

- **WHEN** `Resolve("\\GAMES\\DOOM.ZIP\\maps\\e1m1.wad")` is called on Windows
- **THEN** the path is normalized to `"games/doom.zip/maps/e1m1.wad"` (or equivalent case per platform)
- **AND** the normalized path is passed to `IArchiveTrie.Resolve()`
- **AND** the trie's result is returned

---

### Requirement: Internal path uses forward slashes

The `InternalPath` field in `ArchiveTrieResult` SHALL always use forward slashes as the path separator, regardless of the input format. Internal paths SHALL NOT have a leading slash.

#### Scenario: Internal path normalized from backslash input

- **WHEN** the input path is `"archive.zip\\folder\\file.txt"`
- **THEN** `InternalPath` is `"folder/file.txt"` (forward slashes, no leading slash)
