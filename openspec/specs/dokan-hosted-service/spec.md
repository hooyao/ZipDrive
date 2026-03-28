## ADDED Requirements

### Requirement: Mount on startup

`DokanHostedService` SHALL mount the VFS and create a DokanNet instance when the application starts. Before mounting, it SHALL validate that `ArchiveDirectory` is a non-empty path pointing to an existing directory.

#### Scenario: Successful mount

- **WHEN** `ExecuteAsync` is called
- **THEN** `IVirtualFileSystem.MountAsync` is called with options from configuration
- **AND** `DokanInstanceBuilder` creates a `DokanInstance` with `WriteProtection | FixedDrive` options
- **AND** the service blocks on `WaitForFileSystemClosedAsync` until shutdown

#### Scenario: Mount failure due to missing Dokany driver

- **WHEN** Dokany is not installed
- **THEN** the service catches the exception, logs a clear error with installation instructions, and shuts down the host

#### Scenario: ArchiveDirectory is empty

- **WHEN** `ArchiveDirectory` is empty or whitespace
- **THEN** the service SHALL print an error message to the console
- **AND** the service SHALL print "Press any key to exit..." and call `Console.ReadKey()` before stopping
- **AND** the host SHALL be stopped

#### Scenario: ArchiveDirectory does not exist as a directory

- **WHEN** `ArchiveDirectory` is set but does not refer to an existing directory (including paths that point to files)
- **THEN** the service SHALL print an error message including the invalid path to the console
- **AND** the service SHALL print "Press any key to exit..." and call `Console.ReadKey()` before stopping
- **AND** the host SHALL be stopped

---

### Requirement: Clean unmount on shutdown

The service SHALL cleanly unmount the drive when the application is stopped (Ctrl+C or host shutdown).

#### Scenario: Ctrl+C triggers unmount

- **WHEN** `StopAsync` is called (via Ctrl+C or host stop)
- **THEN** `Dokan.RemoveMountPoint` is called with the configured mount point
- **AND** `IVirtualFileSystem.UnmountAsync` is called to clear caches

---

### Requirement: Configuration binding

The service SHALL read mount configuration from `IOptions<MountOptions>` bound from the `Mount` section of `appsettings.json`.

#### Scenario: Configuration from appsettings.json

- **WHEN** appsettings.json contains `{ "Mount": { "MountPoint": "R:\\", "ArchiveDirectory": "D:\\Archives" } }`
- **THEN** the service mounts at `R:\` with archives from `D:\Archives`

#### Scenario: Command-line override

- **WHEN** the app is run with `Mount:MountPoint=Z:\`
- **THEN** the mount point is `Z:\` regardless of appsettings.json value

<!-- Added by dynamic-reload-v2 change -->

## ADDED Requirements

### Requirement: FileSystemWatcher for archive directory
DokanHostedService SHALL create a FileSystemWatcher on the archive directory with `*.zip` filter, `NotifyFilters.FileName | NotifyFilters.DirectoryName`, `IncludeSubdirectories = true`, and `InternalBufferSize = 65536`. The watcher SHALL start after VFS mount and stop before shutdown.

#### Scenario: Watcher detects new ZIP
- **WHEN** a ZIP file is created in the archive directory
- **THEN** a Created event is forwarded to the ArchiveChangeConsolidator

#### Scenario: Watcher detects deleted ZIP
- **WHEN** a ZIP file is deleted from the archive directory
- **THEN** a Deleted event is forwarded to the ArchiveChangeConsolidator

#### Scenario: Watcher detects renamed ZIP
- **WHEN** a ZIP file is renamed in the archive directory
- **THEN** a Renamed event is forwarded to the ArchiveChangeConsolidator

### Requirement: Event filtering before consolidator
Events SHALL be filtered before reaching the consolidator:
1. Extension validation: only `.zip` extensions (case-insensitive) pass
2. Path normalization via ArchivePathHelper.ToVirtualPath (resolves 8.3 short names)
3. Depth filter: discard events exceeding MaxDiscoveryDepth
4. Directory events: trigger FullReconciliationAsync

#### Scenario: Non-zip file filtered
- **WHEN** `Created("readme.txt")` fires
- **THEN** the event is NOT forwarded to the consolidator

#### Scenario: Rename .zip to .txt produces only Removed
- **WHEN** `Renamed("data.zip", "data.txt")` fires
- **THEN** only `OnDeleted("data.zip")` is forwarded (not `OnCreated("data.txt")`)

#### Scenario: ZIP beyond MaxDiscoveryDepth filtered
- **WHEN** a ZIP at depth 7 fires Created (MaxDiscoveryDepth is 6)
- **THEN** the event is NOT forwarded to the consolidator

#### Scenario: Directory rename triggers reconciliation
- **WHEN** a directory rename event fires
- **THEN** FullReconciliationAsync is triggered (not incremental add/remove)

### Requirement: ApplyDeltaAsync processes consolidated deltas
ApplyDeltaAsync SHALL process removals first, then modifications (remove + re-add), then additions. It SHALL use IArchiveDiscovery.DescribeFile for descriptor construction and the file-readability probe with retry for additions.

#### Scenario: Delta with additions and removals
- **WHEN** delta = { Added: ["new.zip"], Removed: ["old.zip"] }
- **THEN** "old.zip" is removed first, then "new.zip" is added

### Requirement: FullReconciliationAsync on buffer overflow
On FileSystemWatcher Error event (buffer overflow), the service SHALL clear the consolidator's pending events and run a full reconciliation: discover current disk state, diff against registered archives, apply add/remove.

#### Scenario: Buffer overflow recovery
- **WHEN** FileSystemWatcher fires an Error event
- **THEN** consolidator pending is cleared, full discovery runs, and VFS state matches disk state

### Requirement: Network path detection
At startup, the service SHALL detect if the archive directory is a network path (UNC or network drive) and log a warning about potential notification reliability issues.

#### Scenario: Network path warning
- **WHEN** the archive directory is `\\server\share\archives`
- **THEN** a warning is logged about FileSystemWatcher reliability on network paths

### Requirement: Configurable quiet period
The consolidation quiet period SHALL be configurable via `MountSettings.DynamicReloadQuietPeriodSeconds` (default: 5).

#### Scenario: Custom quiet period
- **WHEN** `DynamicReloadQuietPeriodSeconds` is set to 2
- **THEN** the consolidator uses a 2-second quiet period

### Requirement: Archive directory deletion handling
If FullReconciliationAsync fails with DirectoryNotFoundException (archive directory deleted/ejected), the service SHALL log the error, stop the watcher, and NOT crash the host.

#### Scenario: Archive directory deleted
- **WHEN** the archive directory is deleted while the service is running
- **THEN** the error is logged and the watcher is stopped gracefully
