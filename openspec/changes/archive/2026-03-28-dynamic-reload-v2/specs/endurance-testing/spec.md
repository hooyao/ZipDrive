## ADDED Requirements

### Requirement: DynamicReloadSuite tests through DokanFileSystemAdapter
The endurance test SHALL build a complete DokanFileSystemAdapter instance backed by real VFS, caches, trie, and watcher. All test operations SHALL go through the adapter (not VFS directly).

#### Scenario: Adapter-based testing
- **WHEN** the endurance test runs
- **THEN** all read/list/stat operations are invoked via DokanFileSystemAdapter methods (CreateFile, ReadFile, FindFiles, GetFileInformation)

### Requirement: Concurrent logical use-case tasks
The DynamicReloadSuite SHALL run multiple concurrent tasks, each executing a logical use-case sequence. Suites:

1. **AddAndReadSuite (15 tasks)**: Copy ZIP → wait for watcher → FindFiles → ReadFile → verify SHA-256
2. **RemoveDuringReadSuite (10 tasks)**: Start ReadFile → delete ZIP → verify clean error or completion
3. **RapidChurnSuite (8 tasks)**: Add → read → delete → re-add different ZIP same name → verify new content
4. **ExplorerBrowsingSuite (12 tasks)**: FindFiles root → GetFileInformation → FindFiles subdirs → ReadFile
5. **AdversarialSuite (5 tasks)**: Nonexistent paths, past-EOF reads, write attempts, reads during removal
6. **BulkCopySuite (3 tasks)**: Copy 30 ZIPs simultaneously → verify all accessible → delete all
7. **RenameSuite (3 tasks)**: Add → rename → verify old=NotFound, new=accessible

#### Scenario: All suites complete without errors
- **WHEN** the endurance test runs for the configured duration
- **THEN** zero errors, zero handle leaks (BorrowedEntryCount == 0), all suites performed operations

### Requirement: Dedicated reload-only archives
The DynamicReloadSuite SHALL use dedicated archives that other endurance suites do NOT access. This prevents the reload suite from disrupting concurrent read suites.

#### Scenario: Archive isolation
- **WHEN** the DynamicReloadSuite removes an archive
- **THEN** no other suite's task fails because of the removal

### Requirement: SHA-256 verification on every read
Every ReadFile in the DynamicReloadSuite SHALL verify content against the embedded __manifest__.json SHA-256 hash.

#### Scenario: Content integrity after reload
- **WHEN** a ZIP is removed and re-added, then read
- **THEN** the content matches the new ZIP's manifest (not stale cached data from the old ZIP)

### Requirement: Fail-fast with diagnostics
The first error SHALL cancel all tasks immediately with rich diagnostics: suite name, task ID, operation, expected vs actual, cache state, stack trace.

#### Scenario: Error cancels all tasks
- **WHEN** any task encounters an unexpected error
- **THEN** all 56 tasks are cancelled within 1 second and diagnostics are printed
