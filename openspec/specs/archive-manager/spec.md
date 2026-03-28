## ADDED Requirements

### Requirement: IArchiveManager interface
A new `IArchiveManager` interface SHALL provide `AddArchiveAsync`, `RemoveArchiveAsync`, and `GetRegisteredArchives`. It SHALL be separate from `IVirtualFileSystem` (ISP).

#### Scenario: ZipVirtualFileSystem implements IArchiveManager
- **WHEN** the DI container resolves IArchiveManager
- **THEN** it returns the same ZipVirtualFileSystem instance that implements IVirtualFileSystem

### Requirement: AddArchiveAsync registers archive
AddArchiveAsync SHALL add the archive to the trie and create an ArchiveNode. It SHALL be idempotent — calling twice with the same VirtualPath overwrites the descriptor.

#### Scenario: Add new archive
- **WHEN** `AddArchiveAsync(descriptor)` is called for a new archive
- **THEN** the archive is discoverable via path resolution and the structure cache builds lazily on first access

#### Scenario: Add existing archive (idempotent)
- **WHEN** `AddArchiveAsync` is called with a VirtualPath that already exists
- **THEN** the descriptor is updated, no exception is thrown, and reads reflect the new descriptor

### Requirement: RemoveArchiveAsync drains and cleans up
RemoveArchiveAsync SHALL drain in-flight operations, remove the archive from the trie, invalidate the structure cache, and remove file content cache entries.

#### Scenario: Remove existing archive
- **WHEN** `RemoveArchiveAsync("game.zip")` is called
- **THEN** subsequent reads for "game.zip/*" throw VfsFileNotFoundException, structure cache entry is removed, and file content cache entries for "game.zip:*" are removed

#### Scenario: Remove nonexistent archive
- **WHEN** `RemoveArchiveAsync("nosuch.zip")` is called
- **THEN** it returns without error (no-op)

#### Scenario: Remove does not affect other archives
- **WHEN** `RemoveArchiveAsync("game.zip")` is called while "data.zip" has warm cache entries
- **THEN** reads for "data.zip/*" continue working with cache hits

### Requirement: MountAsync uses AddArchiveAsync
MountAsync SHALL call AddArchiveAsync for each discovered archive (not _archiveTrie.AddArchive directly). This ensures ArchiveNodes are populated at mount time.

#### Scenario: After mount, VFS operations work
- **WHEN** VFS.MountAsync completes
- **THEN** all discovered archives have ArchiveNodes and are accessible via VFS operations

### Requirement: IArchiveDiscovery.DescribeFile for single-file discovery
IArchiveDiscovery SHALL expose a `DescribeFile(rootPath, filePath)` method that creates an ArchiveDescriptor for a single file. This SHALL be used by ApplyDeltaAsync instead of manual descriptor construction.

#### Scenario: DescribeFile returns correct descriptor
- **WHEN** `DescribeFile(rootPath, filePath)` is called for an existing ZIP
- **THEN** it returns an ArchiveDescriptor with correct VirtualPath (relative, forward slashes), PhysicalPath, SizeBytes, LastModifiedUtc

#### Scenario: DescribeFile returns null for inaccessible file
- **WHEN** `DescribeFile(rootPath, filePath)` is called for a locked or missing file
- **THEN** it returns null

### Requirement: File-readability probe with retry
Before calling AddArchiveAsync for newly detected files, the system SHALL probe file readability with exponential backoff (1s, 2s, 4s, 8s, 16s, 30s — max 6 retries). This handles half-copied ZIPs and AV scanner locks.

#### Scenario: File locked during copy
- **WHEN** a ZIP file is being copied (locked by another process)
- **THEN** the probe retries until the file becomes readable, then calls AddArchiveAsync

#### Scenario: File permanently inaccessible
- **WHEN** a ZIP file is inaccessible after all retries
- **THEN** the file is skipped with a warning log

### Requirement: ArchivePathHelper shared path normalization
A shared `ArchivePathHelper.ToVirtualPath(rootPath, absolutePath)` SHALL normalize paths consistently between FileSystemWatcher events and ArchiveDiscovery. It SHALL resolve 8.3 short names via Path.GetFullPath.

#### Scenario: Watcher path matches discovery path
- **WHEN** a ZIP is discovered at startup and later detected by the watcher
- **THEN** both produce the same VirtualPath string
