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
