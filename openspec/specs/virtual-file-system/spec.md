## ADDED Requirements

### Requirement: Mount lifecycle

`IVirtualFileSystem.MountAsync` SHALL discover ZIP archives under the root directory, populate the archive trie, and set `IsMounted` to `true`. `UnmountAsync` SHALL clear all caches, release resources, and set `IsMounted` to `false`.

#### Scenario: Successful mount

- **WHEN** `MountAsync(options)` is called with a valid root directory containing ZIP files
- **THEN** `IsMounted` becomes `true`
- **AND** the archive trie is populated with discovered archives
- **AND** the `MountStateChanged` event is raised

#### Scenario: Mount with no ZIP files

- **WHEN** `MountAsync(options)` is called with a root directory containing no ZIP files
- **THEN** `IsMounted` becomes `true`
- **AND** the archive trie is empty
- **AND** listing the root directory returns an empty collection

#### Scenario: Mount with invalid root directory

- **WHEN** `MountAsync(options)` is called with a non-existent root directory
- **THEN** a `DirectoryNotFoundException` is thrown
- **AND** `IsMounted` remains `false`

#### Scenario: Unmount clears state

- **WHEN** `UnmountAsync()` is called while mounted
- **THEN** `IsMounted` becomes `false`
- **AND** the archive structure cache is cleared
- **AND** the `MountStateChanged` event is raised

#### Scenario: Operations on unmounted VFS

- **WHEN** any file operation is called while `IsMounted` is `false`
- **THEN** an `InvalidOperationException` is thrown

---

### Requirement: List directory contents

`ListDirectoryAsync` SHALL return the direct children of a directory path as a collection of `VfsFileInfo` records. It SHALL handle three contexts: virtual root, virtual folders, and directories inside archives.

#### Scenario: List virtual root

- **WHEN** `ListDirectoryAsync("")` is called
- **AND** archives `"games/doom.zip"` and `"backup.zip"` are registered
- **THEN** the result contains entries for `"games"` (directory) and `"backup.zip"` (directory)

#### Scenario: List virtual folder

- **WHEN** `ListDirectoryAsync("games")` is called
- **AND** archives `"games/doom.zip"` and `"games/quake.zip"` are registered
- **THEN** the result contains entries for `"doom.zip"` (directory) and `"quake.zip"` (directory)

#### Scenario: List archive root (lazy structure load)

- **WHEN** `ListDirectoryAsync("games/doom.zip")` is called for the first time
- **THEN** the archive structure is loaded lazily via `IArchiveStructureCache`
- **AND** the result contains the root-level entries of the ZIP archive

#### Scenario: List directory inside archive

- **WHEN** `ListDirectoryAsync("games/doom.zip/maps")` is called
- **AND** the ZIP contains `maps/e1m1.wad`, `maps/e1m2.wad`, and `maps/textures/`
- **THEN** the result contains `VfsFileInfo` entries for `e1m1.wad`, `e1m2.wad`, and `textures` (directory)

#### Scenario: List non-existent directory

- **WHEN** `ListDirectoryAsync("nonexistent")` is called
- **AND** no archive or virtual folder matches
- **THEN** a `VfsDirectoryNotFoundException` is thrown

---

### Requirement: Get file information

`GetFileInfoAsync` SHALL return a `VfsFileInfo` record for any valid path (files, directories, archives, virtual folders).

#### Scenario: Get info for a file inside archive

- **WHEN** `GetFileInfoAsync("games/doom.zip/readme.txt")` is called
- **AND** the ZIP contains `readme.txt` with size 1024 bytes
- **THEN** the result has `Name = "readme.txt"`, `IsDirectory = false`, `SizeBytes = 1024`
- **AND** `LastWriteTime` matches the ZIP entry's last modified time

#### Scenario: Get info for a directory inside archive

- **WHEN** `GetFileInfoAsync("games/doom.zip/maps")` is called
- **AND** `maps/` exists as a directory in the ZIP
- **THEN** the result has `Name = "maps"`, `IsDirectory = true`, `SizeBytes = 0`

#### Scenario: Get info for an archive (appears as directory)

- **WHEN** `GetFileInfoAsync("games/doom.zip")` is called
- **THEN** the result has `Name = "doom.zip"`, `IsDirectory = true`
- **AND** `SizeBytes` is the ZIP file's physical size on disk

#### Scenario: Get info for a virtual folder

- **WHEN** `GetFileInfoAsync("games")` is called
- **AND** `"games"` is a derived virtual folder
- **THEN** the result has `Name = "games"`, `IsDirectory = true`, `SizeBytes = 0`

#### Scenario: Get info for virtual root

- **WHEN** `GetFileInfoAsync("")` is called
- **THEN** the result has `Name = ""`, `IsDirectory = true`

#### Scenario: Get info for non-existent path

- **WHEN** `GetFileInfoAsync("nonexistent/file.txt")` is called
- **THEN** a `VfsFileNotFoundException` is thrown

---

### Requirement: Read file content

`ReadFileAsync` SHALL read decompressed file content from an archive entry at a specified byte offset into a provided buffer. It SHALL use the file content cache with borrow/return pattern.

#### Scenario: Read entire small file

- **WHEN** `ReadFileAsync("archive.zip/readme.txt", buffer, offset: 0)` is called
- **AND** the file is 500 bytes
- **AND** buffer size is 4096
- **THEN** 500 bytes are written to the buffer
- **AND** the return value is 500

#### Scenario: Read file at offset

- **WHEN** `ReadFileAsync("archive.zip/data.bin", buffer, offset: 1000)` is called
- **AND** the file is 5000 bytes
- **AND** buffer size is 2000
- **THEN** 2000 bytes are written to the buffer starting from offset 1000
- **AND** the return value is 2000

#### Scenario: Read past end of file

- **WHEN** `ReadFileAsync("archive.zip/data.bin", buffer, offset: 4500)` is called
- **AND** the file is 5000 bytes
- **AND** buffer size is 2000
- **THEN** 500 bytes are written to the buffer (remaining bytes from offset to EOF)
- **AND** the return value is 500

#### Scenario: Read at or beyond EOF

- **WHEN** `ReadFileAsync("archive.zip/data.bin", buffer, offset: 5000)` is called
- **AND** the file is 5000 bytes
- **THEN** the return value is 0

#### Scenario: Cache hit for repeated reads

- **WHEN** the same file is read multiple times
- **THEN** the file content cache returns the cached stream on subsequent reads
- **AND** the ZIP is NOT re-parsed

#### Scenario: Read from a directory path

- **WHEN** `ReadFileAsync("archive.zip/maps", buffer, offset: 0)` is called
- **AND** `maps` is a directory
- **THEN** a `VfsAccessDeniedException` is thrown

#### Scenario: Read from non-existent file

- **WHEN** `ReadFileAsync("archive.zip/nonexistent.txt", buffer, offset: 0)` is called
- **THEN** a `VfsFileNotFoundException` is thrown

---

### Requirement: File and directory existence checks

`FileExistsAsync` SHALL return `true` only for files (not directories). `DirectoryExistsAsync` SHALL return `true` for directories, archives, and virtual folders.

#### Scenario: File exists inside archive

- **WHEN** `FileExistsAsync("archive.zip/readme.txt")` is called
- **AND** the entry exists and is not a directory
- **THEN** the result is `true`

#### Scenario: File does not exist

- **WHEN** `FileExistsAsync("archive.zip/nonexistent.txt")` is called
- **THEN** the result is `false`

#### Scenario: FileExists returns false for directories

- **WHEN** `FileExistsAsync("archive.zip/maps")` is called
- **AND** `maps` is a directory in the archive
- **THEN** the result is `false`

#### Scenario: Directory exists for archive

- **WHEN** `DirectoryExistsAsync("games/doom.zip")` is called
- **AND** the archive is registered
- **THEN** the result is `true`

#### Scenario: Directory exists for virtual folder

- **WHEN** `DirectoryExistsAsync("games")` is called
- **AND** `"games"` is a derived virtual folder
- **THEN** the result is `true`

#### Scenario: Directory exists inside archive

- **WHEN** `DirectoryExistsAsync("archive.zip/maps")` is called
- **AND** `maps/` is a directory in the archive
- **THEN** the result is `true`

---

### Requirement: Volume information

`GetVolumeInfo` SHALL return a `VfsVolumeInfo` record with volume label, file system name, and read-only flag. It SHALL NOT require async I/O.

#### Scenario: Default volume info

- **WHEN** `GetVolumeInfo()` is called
- **THEN** the result has `FileSystemName = "ZipDriveFS"`
- **AND** `IsReadOnly = true`
- **AND** `VolumeLabel` contains a meaningful name

---

### Requirement: VFS exception hierarchy

The VFS layer SHALL throw domain-specific exceptions derived from `VfsException`. Infrastructure exceptions (e.g., `IOException`, `ZipException`) SHALL be wrapped in appropriate VFS exceptions.

#### Scenario: File not found throws VfsFileNotFoundException

- **WHEN** a file path resolves to an archive entry that does not exist
- **THEN** `VfsFileNotFoundException` is thrown with the path in the message

#### Scenario: Directory not found throws VfsDirectoryNotFoundException

- **WHEN** a directory listing is requested for a non-existent path
- **THEN** `VfsDirectoryNotFoundException` is thrown

#### Scenario: Corrupt ZIP throws VfsException

- **WHEN** an archive structure build fails due to a corrupt ZIP
- **THEN** a `VfsException` is thrown wrapping the underlying `ZipException`

---

### Requirement: All operations are async

All `IVirtualFileSystem` methods that perform I/O SHALL be async (return `Task<T>` or `ValueTask<T>`). Only `GetVolumeInfo()` and property accessors MAY be synchronous.

#### Scenario: Async method signatures

- **WHEN** `IVirtualFileSystem` is inspected
- **THEN** `ReadFileAsync`, `ListDirectoryAsync`, `GetFileInfoAsync`, `FileExistsAsync`, `DirectoryExistsAsync`, `MountAsync`, and `UnmountAsync` all return `Task<T>` or `ValueTask<T>`
- **AND** all accept `CancellationToken` parameters

<!-- Added by dynamic-reload-v2 change -->

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
