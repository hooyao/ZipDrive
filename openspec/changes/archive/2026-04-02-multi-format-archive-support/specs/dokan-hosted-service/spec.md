## MODIFIED Requirements

### Requirement: FileSystemWatcher for archive directory
The `FileSystemWatcher` SHALL watch for all supported archive formats, not just `*.zip`. Since `FileSystemWatcher` only supports a single filter pattern, the watcher SHALL use a broad filter and event handlers SHALL check `IFormatRegistry.SupportedExtensions` before enqueueing to the consolidator.

#### Scenario: Watcher detects new ZIP
- **WHEN** a new `.zip` file appears in the archive directory
- **THEN** the consolidator receives a Created event

#### Scenario: Watcher detects new RAR
- **WHEN** a new `.rar` file appears in the archive directory
- **THEN** the consolidator receives a Created event

#### Scenario: Watcher detects deleted RAR
- **WHEN** a `.rar` file is deleted from the archive directory
- **THEN** the consolidator receives a Deleted event

#### Scenario: Watcher detects renamed RAR
- **WHEN** a `.rar` file is renamed within the archive directory
- **THEN** the consolidator receives appropriate Deleted + Created events

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
