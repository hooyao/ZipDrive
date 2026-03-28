## MODIFIED Requirements

### Requirement: VFS operations guard with per-archive ArchiveNode
All VFS methods that access archive data (InsideArchive and ArchiveRoot code paths) SHALL wrap their logic with ArchiveGuard (TryEnter/Exit on the archive's ArchiveNode). If TryEnter returns false (archive draining or removed), the method SHALL throw VfsFileNotFoundException.

#### Scenario: ReadFileAsync during drain returns FileNotFound
- **WHEN** `ReadFileAsync("game.zip/file.txt")` is called while "game.zip" is draining
- **THEN** VfsFileNotFoundException is thrown

#### Scenario: ReadFileAsync for active archive succeeds
- **WHEN** `ReadFileAsync("game.zip/file.txt")` is called while "game.zip" is active
- **THEN** the read completes normally and ActiveOps is decremented after completion

#### Scenario: ListDirectoryAsync guarded for ArchiveRoot
- **WHEN** `ListDirectoryAsync("game.zip")` is called while "game.zip" is draining
- **THEN** VfsFileNotFoundException is thrown

#### Scenario: VirtualRoot and VirtualFolder paths are NOT guarded
- **WHEN** `ListDirectoryAsync("\\")` or `GetFileInfoAsync("games")` is called during any archive drain
- **THEN** the call succeeds (virtual folder operations don't access archive data)

### Requirement: MountAsync populates ArchiveNodes via AddArchiveAsync
MountAsync SHALL call AddArchiveAsync for each discovered archive instead of calling _archiveTrie.AddArchive directly. This ensures every archive has an ArchiveNode at mount time.

#### Scenario: VFS operations work after mount
- **WHEN** MountAsync completes with 5 discovered archives
- **THEN** all 5 archives have ArchiveNodes and ReadFileAsync succeeds for each

### Requirement: Prefetch participates in ArchiveNode drain guard
PrefetchDirectoryAsync SHALL call TryEnter on the ArchiveNode before starting. It SHALL use DrainToken as the cancellation token. On drain, prefetch exits promptly via OperationCanceledException.

#### Scenario: Prefetch holds drain open until complete
- **WHEN** prefetch is in progress for "game.zip" and RemoveArchiveAsync is called
- **THEN** DrainAsync waits for prefetch to complete (via TryEnter/Exit) before proceeding with cleanup

#### Scenario: Prefetch cancelled by DrainToken
- **WHEN** DrainAsync cancels the DrainToken while prefetch is running
- **THEN** prefetch receives OperationCanceledException and exits, allowing drain to complete
