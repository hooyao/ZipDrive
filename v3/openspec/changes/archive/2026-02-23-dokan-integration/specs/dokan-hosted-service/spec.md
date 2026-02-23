## ADDED Requirements

### Requirement: Mount on startup

`DokanHostedService` SHALL mount the VFS and create a DokanNet instance when the application starts.

#### Scenario: Successful mount

- **WHEN** `ExecuteAsync` is called
- **THEN** `IVirtualFileSystem.MountAsync` is called with options from configuration
- **AND** `DokanInstanceBuilder` creates a `DokanInstance` with `WriteProtection | FixedDrive` options
- **AND** the service blocks on `WaitForFileSystemClosedAsync` until shutdown

#### Scenario: Mount failure due to missing Dokany driver

- **WHEN** Dokany is not installed
- **THEN** the service catches the exception, logs a clear error with installation instructions, and shuts down the host

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
