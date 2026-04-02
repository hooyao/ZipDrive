## MODIFIED Requirements

### Requirement: FileSystemWatcher for archive directory
The `FileSystemWatcher` SHALL watch for all supported archive formats, not just `*.zip`. Since `FileSystemWatcher` only supports a single filter pattern, the watcher SHALL use a broad filter and event handlers SHALL check `IFormatRegistry.SupportedExtensions` before enqueueing to the consolidator. The watcher SHALL NOT be started in single-file mount mode.

#### Scenario: Watcher detects new ZIP
- **WHEN** a new `.zip` file appears in the archive directory (directory mode)
- **THEN** the consolidator receives a Created event

#### Scenario: Watcher detects new RAR
- **WHEN** a new `.rar` file appears in the archive directory (directory mode)
- **THEN** the consolidator receives a Created event

#### Scenario: Watcher detects deleted RAR
- **WHEN** a `.rar` file is deleted from the archive directory (directory mode)
- **THEN** the consolidator receives a Deleted event

#### Scenario: Watcher detects renamed RAR
- **WHEN** a `.rar` file is renamed within the archive directory (directory mode)
- **THEN** the consolidator receives appropriate Deleted + Created events

#### Scenario: Watcher not started in single-file mode
- **WHEN** the system is in single-file mount mode
- **THEN** `StartWatcher()` SHALL NOT be called
- **AND** no `FileSystemWatcher` or `ArchiveChangeConsolidator` instances SHALL be created

### Requirement: Event filtering before consolidator
Event handlers SHALL call `IsSupportedArchive(path)` which checks `IFormatRegistry.SupportedExtensions` instead of the previous `IsZipExtension` hardcoded check. Non-archive file events SHALL be discarded before reaching the consolidator.

#### Scenario: Non-archive file filtered
- **WHEN** a `.txt` file is created in the archive directory
- **THEN** no event is passed to the consolidator

#### Scenario: Rename .rar to .txt produces only Removed
- **WHEN** `data.rar` is renamed to `data.txt`
- **THEN** a Deleted event for `data.rar` is passed to the consolidator
- **AND** no Created event is passed (`.txt` is not a supported extension)

#### Scenario: Archive beyond MaxDiscoveryDepth filtered
- **WHEN** a `.rar` file is created at a depth beyond `MaxDiscoveryDepth`
- **THEN** no event is passed to the consolidator

#### Scenario: Directory rename triggers reconciliation
- **WHEN** a directory is renamed
- **THEN** a full reconciliation is triggered (unchanged behavior)

## ADDED Requirements

### Requirement: Three-way path validation
`DokanHostedService.ExecuteAsync` SHALL validate `Mount:ArchiveDirectory` with a three-way check: `File.Exists()` → single-file mode, `Directory.Exists()` → directory mode, neither → error with notice.

#### Scenario: File path triggers single-file mode
- **WHEN** `Mount:ArchiveDirectory` resolves to an existing file
- **THEN** the system SHALL call `MountSingleFileAsync` on the VFS

#### Scenario: Directory path triggers directory mode
- **WHEN** `Mount:ArchiveDirectory` resolves to an existing directory
- **THEN** the system SHALL call `MountAsync` with `VfsMountOptions` (unchanged behavior)

#### Scenario: Non-existent path triggers error
- **WHEN** `Mount:ArchiveDirectory` resolves to neither a file nor a directory
- **THEN** the system SHALL display an ERROR notice and call `WaitForKeyAndStop()`

### Requirement: Unsupported file format handling
When in single-file mode and the file's format is not recognized by `IFormatRegistry`, the system SHALL display a detailed ERROR notice and exit.

#### Scenario: Unsupported file dragged
- **WHEN** `Mount:ArchiveDirectory` points to an existing file with extension `.docx`
- **THEN** an ERROR notice SHALL display: the filename, the unsupported extension, and all supported formats from `IFormatRegistry.SupportedExtensions`
- **AND** the system SHALL call `WaitForKeyAndStop()`
