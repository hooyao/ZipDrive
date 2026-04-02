## ADDED Requirements

### Requirement: Single file detection
The system SHALL detect whether `Mount:ArchiveDirectory` points to a file or a directory by checking `File.Exists()` before `Directory.Exists()`.

#### Scenario: User provides a ZIP file path
- **WHEN** `Mount:ArchiveDirectory` is set to a path where `File.Exists()` returns true
- **THEN** the system SHALL enter single-file mount mode

#### Scenario: User provides a directory path
- **WHEN** `Mount:ArchiveDirectory` is set to a path where `Directory.Exists()` returns true
- **THEN** the system SHALL use the existing directory mount mode (unchanged behavior)

#### Scenario: User provides a non-existent path
- **WHEN** `Mount:ArchiveDirectory` is set to a path where both `File.Exists()` and `Directory.Exists()` return false
- **THEN** the system SHALL display an ERROR notice with "Path not found: {path}" and wait for key press

### Requirement: Single file mounting via VFS
The `IVirtualFileSystem` interface SHALL expose a `MountSingleFileAsync(string filePath, CancellationToken)` method that mounts a single archive file.

#### Scenario: Mount a supported archive file
- **WHEN** `MountSingleFileAsync` is called with a path to a supported archive (ZIP or RAR)
- **THEN** the system SHALL create an `ArchiveDescriptor` using `IArchiveDiscovery.DescribeFile()` with the file's parent directory as rootPath
- **AND** call `AddArchiveAsync` with the descriptor
- **AND** set `IsMounted` to true

#### Scenario: Mount an unsupported file type
- **WHEN** `MountSingleFileAsync` is called with a path to a file whose format is not detected by `IFormatRegistry`
- **THEN** the method SHALL return a result indicating format not recognized
- **AND** the caller SHALL display an ERROR notice listing the unsupported extension and all supported formats

### Requirement: Volume label from filename
In single-file mode, the volume label SHALL be derived from the archive filename without extension, truncated to 32 characters (NTFS limit).

#### Scenario: Normal filename
- **WHEN** a single file `game.zip` is mounted
- **THEN** the volume label SHALL be `game`

#### Scenario: Long filename
- **WHEN** a single file with a name exceeding 32 characters (without extension) is mounted
- **THEN** the volume label SHALL be truncated to 32 characters

### Requirement: Virtual path structure consistency
In single-file mode, the virtual path structure SHALL be identical to directory mode — the archive appears as a folder at the drive root.

#### Scenario: Single file virtual path
- **WHEN** `D:\Downloads\game.zip` is mounted as a single file
- **THEN** the virtual drive SHALL show `R:\game.zip\` as a folder containing the archive's contents

### Requirement: No FileSystemWatcher in single-file mode
The system SHALL NOT start a `FileSystemWatcher` when operating in single-file mode.

#### Scenario: Single file mode watcher behavior
- **WHEN** the system is in single-file mount mode
- **THEN** `StartWatcher()` SHALL NOT be called
- **AND** no `FileSystemWatcher` or `ArchiveChangeConsolidator` instances SHALL be created
