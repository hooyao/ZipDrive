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
