## MODIFIED Requirements

### Requirement: All endurance suites use DokanFileSystemAdapter
EnduranceSuiteBase SHALL accept `DokanFileSystemAdapter` (not `IVirtualFileSystem`). All VFS calls in all suites SHALL go through `Adapter.Guarded*Async` methods.

#### Scenario: NormalReadSuite reads through adapter
- **WHEN** NormalReadSuite reads a file
- **THEN** it calls `Adapter.GuardedReadFileAsync` (not `Vfs.ReadFileAsync`)

#### Scenario: All suites construct with adapter
- **WHEN** any endurance suite is constructed
- **THEN** it accepts `DokanFileSystemAdapter adapter` as its first parameter

### Requirement: EnduranceTest fixture builds adapter
EnduranceTest.InitializeAsync SHALL construct a `DokanFileSystemAdapter` instance and pass it to all suite constructors.

#### Scenario: Adapter constructed without Dokany
- **WHEN** the endurance test initializes
- **THEN** `DokanFileSystemAdapter` is constructed with real VFS + MountSettings + NullLogger (no Dokan runtime needed)

## ADDED Requirements

### Requirement: DynamicReloadSuite exercises logical user scenarios through adapter
The DynamicReloadSuite SHALL implement 10 categories of concurrent logical scenarios, each calling `Adapter.Guarded*Async` for reads and `IArchiveManager` for lifecycle. Categories: AddAndRead, RemoveDuringRead, RapidChurn, ExplorerBrowsing, Adversarial, CrossArchiveInterference, BulkCopy, Rename, ConcurrencyRace, DegradationMonitoring.

#### Scenario: AddAndRead lifecycle through adapter
- **WHEN** a ZIP is added and a task reads through adapter
- **THEN** `GuardedDirectoryExistsAsync` transitions from false to true, `GuardedReadFileAsync` returns correct SHA-256

#### Scenario: RemoveDuringRead produces clean outcome
- **WHEN** `RemoveArchiveAsync` is called while `GuardedReadFileAsync` is in progress
- **THEN** the read either completes with correct SHA-256 OR throws VfsFileNotFoundException (never crashes, never returns corrupt data)

#### Scenario: RapidChurn detects stale cache
- **WHEN** archive A is replaced by archive B (same filename, different content)
- **THEN** `GuardedReadFileAsync` after replacement returns hash matching B's manifest (not A's stale data)

#### Scenario: CrossArchive isolation
- **WHEN** archiveY is removed while archiveX is being read (both contain same filename)
- **THEN** reads from archiveX continue with correct hash_X, never return hash_Y

### Requirement: Post-run assertions detect degradation
After all tasks complete, the endurance test SHALL verify: zero errors, BorrowedEntryCount == 0, PendingCleanupCount == 0, trie-disk consistency, no orphan virtual folders, temp file count == 0.

#### Scenario: Handle leak detection
- **WHEN** the endurance test completes
- **THEN** `BorrowedEntryCount == 0` and no `.zip2vd.chunked` temp files remain
