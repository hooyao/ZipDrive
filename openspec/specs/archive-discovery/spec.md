## ADDED Requirements

### Requirement: Discover ZIP files in a directory tree

`IArchiveDiscovery.DiscoverAsync` SHALL scan a root directory recursively up to a configurable maximum depth, finding all files with a `.zip` extension. Discovery SHALL be a one-time operation performed at mount time.

#### Scenario: Discover ZIPs at root level (depth=1)

- **WHEN** `DiscoverAsync("D:\Archives", maxDepth: 1)` is called
- **AND** `D:\Archives` contains `backup.zip` and subdirectory `games/`
- **THEN** the result contains `ArchiveDescriptor { VirtualPath="backup.zip" }`
- **AND** the result does NOT contain any ZIPs from `games/` subdirectory

#### Scenario: Discover ZIPs in subdirectories (depth=2)

- **WHEN** `DiscoverAsync("D:\Archives", maxDepth: 2)` is called
- **AND** `D:\Archives\games\` contains `doom.zip`
- **THEN** the result contains `ArchiveDescriptor { VirtualPath="games/doom.zip" }`

#### Scenario: Depth limit is respected

- **WHEN** `DiscoverAsync("D:\Archives", maxDepth: 1)` is called
- **AND** `D:\Archives\games\retro\` contains `duke.zip` (depth 2)
- **THEN** the result does NOT contain `duke.zip`

#### Scenario: Empty directory returns empty list

- **WHEN** `DiscoverAsync("D:\EmptyDir", maxDepth: 6)` is called
- **AND** the directory contains no ZIP files at any depth
- **THEN** the result is an empty list

---

### Requirement: Maximum depth constraint

The `maxDepth` parameter SHALL be clamped to the range 1-6 inclusive. Values outside this range SHALL be clamped without error.

#### Scenario: Depth clamped to minimum

- **WHEN** `DiscoverAsync(root, maxDepth: 0)` is called
- **THEN** the effective depth is 1 (immediate children only)

#### Scenario: Depth clamped to maximum

- **WHEN** `DiscoverAsync(root, maxDepth: 100)` is called
- **THEN** the effective depth is 6

#### Scenario: Default depth

- **WHEN** `DiscoverAsync(root)` is called without specifying `maxDepth`
- **THEN** the default depth of 6 is used

---

### Requirement: Virtual path preserves directory structure

Each `ArchiveDescriptor` produced by discovery SHALL have a `VirtualPath` that is the ZIP file's path relative to the root directory, using forward slashes.

#### Scenario: Root-level ZIP

- **WHEN** root is `"D:\Archives"` and `"D:\Archives\backup.zip"` is found
- **THEN** `VirtualPath` is `"backup.zip"`

#### Scenario: Nested ZIP

- **WHEN** root is `"D:\Archives"` and `"D:\Archives\games\retro\duke.zip"` is found
- **THEN** `VirtualPath` is `"games/retro/duke.zip"`

#### Scenario: Virtual path uses forward slashes on all platforms

- **WHEN** running on Windows where physical path uses backslashes
- **THEN** `VirtualPath` still uses forward slashes (e.g., `"games/doom.zip"` not `"games\doom.zip"`)

---

### Requirement: ArchiveDescriptor metadata

Each discovered archive SHALL produce an `ArchiveDescriptor` containing the virtual path, absolute physical path, file size in bytes, and last modified time in UTC.

#### Scenario: Descriptor contains correct metadata

- **WHEN** `"D:\Archives\games\doom.zip"` is discovered
- **AND** the file is 50 MB and last modified 2025-01-15T10:30:00Z
- **THEN** the descriptor has `PhysicalPath = "D:\Archives\games\doom.zip"`
- **AND** `SizeBytes = 52428800`
- **AND** `LastModifiedUtc = 2025-01-15T10:30:00Z`

---

### Requirement: Graceful handling of inaccessible files

Discovery SHALL skip ZIP files that cannot be accessed (permission denied, locked by another process) and continue scanning. A warning SHALL be logged for each skipped file.

#### Scenario: Inaccessible ZIP is skipped

- **WHEN** `"D:\Archives\locked.zip"` exists but is locked by another process
- **AND** `"D:\Archives\readable.zip"` is accessible
- **THEN** the result contains only the descriptor for `readable.zip`
- **AND** a warning is logged for `locked.zip`

#### Scenario: Non-existent root directory

- **WHEN** `DiscoverAsync("D:\NonExistent", maxDepth: 6)` is called
- **AND** the directory does not exist
- **THEN** a `DirectoryNotFoundException` is thrown

---

### Requirement: Cancellation support

`DiscoverAsync` SHALL respect the `CancellationToken` parameter and abort discovery promptly when cancellation is requested.

#### Scenario: Cancellation during discovery

- **WHEN** cancellation is requested while scanning a directory tree
- **THEN** `OperationCanceledException` is thrown
- **AND** any partial results are discarded
