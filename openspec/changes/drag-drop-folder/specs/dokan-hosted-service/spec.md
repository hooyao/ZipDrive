## MODIFIED Requirements

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
