## ADDED Requirements

### Requirement: Archive registration and lookup

The `IArchiveTrie` SHALL use KTrie `TrieDictionary<ArchiveDescriptor>` to store registered archives. Archive keys SHALL be virtual paths with a trailing `/` (e.g., `"games/doom.zip/"`). The trie SHALL support adding and removing archives at runtime.

#### Scenario: Register a single archive

- **WHEN** `AddArchive` is called with an `ArchiveDescriptor` having `VirtualPath = "backup.zip"`
- **THEN** the trie contains the key `"backup.zip/"` mapped to that descriptor
- **AND** `ArchiveCount` returns 1

#### Scenario: Register multiple archives in subdirectories

- **WHEN** `AddArchive` is called for `"games/doom.zip"`, `"games/quake.zip"`, and `"docs/manuals.zip"`
- **THEN** all three archives are retrievable from the trie
- **AND** `ArchiveCount` returns 3

#### Scenario: Remove an archive

- **WHEN** `RemoveArchive("games/doom.zip")` is called
- **THEN** the archive is no longer resolvable
- **AND** `RemoveArchive` returns `true`

#### Scenario: Remove a non-existent archive

- **WHEN** `RemoveArchive("nonexistent.zip")` is called
- **THEN** `RemoveArchive` returns `false`
- **AND** no other archives are affected

---

### Requirement: Longest prefix match for archive boundary detection

The `IArchiveTrie` SHALL use `GetLongestPrefixMatch` to find the archive boundary in a virtual path. The matched key determines which archive a path belongs to, and the remaining path becomes the internal path.

#### Scenario: Resolve a path inside an archive

- **WHEN** `Resolve("games/doom.zip/maps/e1m1.wad")` is called
- **THEN** the result has `Status = InsideArchive`
- **AND** `Archive.VirtualPath` is `"games/doom.zip"`
- **AND** `InternalPath` is `"maps/e1m1.wad"`

#### Scenario: Resolve a path at archive root

- **WHEN** `Resolve("games/doom.zip")` is called
- **THEN** the result has `Status = ArchiveRoot`
- **AND** `Archive.VirtualPath` is `"games/doom.zip"`
- **AND** `InternalPath` is `""`

#### Scenario: Resolve an empty path (virtual root)

- **WHEN** `Resolve("")` is called
- **THEN** the result has `Status = VirtualRoot`

#### Scenario: Resolve a path that does not match any archive

- **WHEN** `Resolve("nonexistent/path")` is called
- **AND** no archive or virtual folder matches
- **THEN** the result has `Status = NotFound`

#### Scenario: Trailing slash is normalized

- **WHEN** `Resolve("games/doom.zip/")` is called
- **THEN** the result has `Status = ArchiveRoot`
- **AND** `InternalPath` is `""`

---

### Requirement: Virtual folder derivation

The `IArchiveTrie` SHALL automatically derive virtual folders from registered archive paths. A virtual folder exists if and only if it is an ancestor of at least one registered archive.

#### Scenario: Virtual folder created from archive path

- **WHEN** archive `"games/doom.zip"` is registered
- **THEN** `IsVirtualFolder("games")` returns `true`

#### Scenario: Nested virtual folders created

- **WHEN** archive `"a/b/c/data.zip"` is registered
- **THEN** `IsVirtualFolder("a")` returns `true`
- **AND** `IsVirtualFolder("a/b")` returns `true`
- **AND** `IsVirtualFolder("a/b/c")` returns `true`

#### Scenario: Root-level archive creates no virtual folders

- **WHEN** archive `"backup.zip"` is the only registered archive
- **THEN** `IsVirtualFolder("backup.zip")` returns `false` (it's an archive, not a folder)

#### Scenario: Resolve a virtual folder path

- **WHEN** `Resolve("games")` is called
- **AND** `"games"` is a derived virtual folder
- **THEN** the result has `Status = VirtualFolder`
- **AND** `VirtualFolderPath` is `"games"`

---

### Requirement: Virtual folder listing

The `IArchiveTrie` SHALL support listing the direct children (archives and subfolders) of a virtual folder path.

#### Scenario: List root folder contents

- **WHEN** archives `"games/doom.zip"`, `"docs/manuals.zip"`, and `"backup.zip"` are registered
- **AND** `ListFolder("")` is called
- **THEN** the result contains `VirtualFolderEntry { Name="games", IsArchive=false }`
- **AND** `VirtualFolderEntry { Name="docs", IsArchive=false }`
- **AND** `VirtualFolderEntry { Name="backup.zip", IsArchive=true, Archive=<descriptor> }`

#### Scenario: List subfolder contents

- **WHEN** archives `"games/doom.zip"` and `"games/quake.zip"` are registered
- **AND** `ListFolder("games")` is called
- **THEN** the result contains `VirtualFolderEntry { Name="doom.zip", IsArchive=true }`
- **AND** `VirtualFolderEntry { Name="quake.zip", IsArchive=true }`

#### Scenario: List folder with mixed archives and subfolders

- **WHEN** archives `"games/doom.zip"` and `"games/retro/duke.zip"` are registered
- **AND** `ListFolder("games")` is called
- **THEN** the result contains `VirtualFolderEntry { Name="doom.zip", IsArchive=true }`
- **AND** `VirtualFolderEntry { Name="retro", IsArchive=false }`
- **AND** the result does NOT contain `"duke.zip"` (it's nested, not direct)

---

### Requirement: Platform-aware case sensitivity for archive trie

The archive trie SHALL use case-insensitive comparison on Windows and case-sensitive comparison on other platforms. This applies to archive path lookups and virtual folder resolution.

#### Scenario: Case-insensitive resolution on Windows

- **WHEN** running on Windows
- **AND** archive `"games/doom.zip"` is registered
- **AND** `Resolve("GAMES/DOOM.ZIP/maps/e1m1.wad")` is called
- **THEN** the result has `Status = InsideArchive`
- **AND** the correct archive is resolved

#### Scenario: Case-sensitive resolution on Linux

- **WHEN** running on Linux
- **AND** archive `"games/doom.zip"` is registered
- **AND** `Resolve("GAMES/DOOM.ZIP/maps/e1m1.wad")` is called
- **THEN** the result has `Status = NotFound`

<!-- Added by dynamic-reload-v2 change -->

## MODIFIED Requirements

### Requirement: Thread-safe read/write via ReaderWriterLockSlim
ArchiveTrie SHALL use a ReaderWriterLockSlim to protect all operations. Read operations (Resolve, ListFolder, IsVirtualFolder, Archives, ArchiveCount) SHALL take shared read locks. Write operations (AddArchive, RemoveArchive) SHALL take exclusive write locks.

#### Scenario: Concurrent Resolve during AddArchive
- **WHEN** 10 threads call Resolve() concurrently while 1 thread calls AddArchive()
- **THEN** no exceptions occur, all Resolve calls return correct results, and after AddArchive completes the new archive is discoverable

#### Scenario: Concurrent Resolve during RemoveArchive
- **WHEN** 10 threads call Resolve() on other archives while 1 thread calls RemoveArchive()
- **THEN** no exceptions occur, Resolve for the removed archive returns NotFound after removal, Resolve for other archives returns correct results throughout

### Requirement: ListFolder materialized inside read lock
ListFolder SHALL materialize its results to a List before returning, NOT yield lazily. This ensures enumeration completes inside the read lock.

#### Scenario: ListFolder returns complete snapshot
- **WHEN** ListFolder is called concurrently with RemoveArchive
- **THEN** the returned list is a complete, consistent snapshot (not a partially-enumerated lazy sequence)

### Requirement: RemoveArchive rebuilds virtual folders
RemoveArchive SHALL rebuild the _virtualFolders HashSet from remaining archives after removal. This ensures orphaned virtual folders (empty intermediate directories) are cleaned up.

#### Scenario: Last archive in folder removed
- **WHEN** "games/doom.zip" and "games/quake.zip" exist, then "games/quake.zip" is removed, then "games/doom.zip" is removed
- **THEN** after the second removal, IsVirtualFolder("games") returns false

#### Scenario: Virtual folder preserved while archives remain
- **WHEN** "games/doom.zip" is removed but "games/quake.zip" still exists
- **THEN** IsVirtualFolder("games") still returns true
