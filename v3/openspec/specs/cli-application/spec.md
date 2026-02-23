## ADDED Requirements

### Requirement: Standard .NET Generic Host

The CLI application SHALL use `Host.CreateDefaultBuilder(args)` for configuration, DI, and lifecycle management.

#### Scenario: Host loads configuration sources

- **WHEN** the application starts
- **THEN** configuration is loaded from `appsettings.json`, environment variables, and command-line arguments (in that priority order, last wins)

#### Scenario: DI container wires all services

- **WHEN** the host builds
- **THEN** `IVirtualFileSystem`, `IArchiveTrie`, `IArchiveStructureCache`, `ICache<Stream>`, `IArchiveDiscovery`, `IPathResolver`, `DokanFileSystemAdapter`, and `DokanHostedService` are all registered

---

### Requirement: Configuration file

The application SHALL include an `appsettings.json` with `Mount` and `Cache` configuration sections.

#### Scenario: Default configuration

- **WHEN** appsettings.json is loaded with defaults
- **THEN** `Mount.MountPoint` defaults to `R:\`, `Mount.ArchiveDirectory` has no default (required), `Mount.MaxDiscoveryDepth` defaults to 6, and `Cache` section matches `CacheOptions` defaults

---

### Requirement: Graceful shutdown

The application SHALL handle Ctrl+C gracefully, unmounting the drive and clearing caches before exit.

#### Scenario: Ctrl+C shuts down cleanly

- **WHEN** the user presses Ctrl+C
- **THEN** the host triggers `StopAsync` on `DokanHostedService`
- **AND** the drive is unmounted
- **AND** the application exits with code 0
