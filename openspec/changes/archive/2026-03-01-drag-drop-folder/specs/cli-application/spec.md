## MODIFIED Requirements

### Requirement: Standard .NET Generic Host

The CLI application SHALL use `Host.CreateDefaultBuilder(args)` for configuration, DI, and lifecycle management. The host SHALL also register OpenTelemetry SDK for metrics and tracing export. Before creating the host, the application SHALL preprocess `args` to rewrite bare positional arguments to named configuration keys.

#### Scenario: Host loads configuration sources

- **WHEN** the application starts
- **THEN** configuration is loaded from `appsettings.json`, environment variables, and command-line arguments (in that priority order, last wins)

#### Scenario: DI container wires all services

- **WHEN** the host builds
- **THEN** `IVirtualFileSystem`, `IArchiveTrie`, `IArchiveStructureCache`, `ICache<Stream>` (via `DualTierFileCache`), `IArchiveDiscovery`, `IPathResolver`, `DokanFileSystemAdapter`, and `DokanHostedService` are all registered

#### Scenario: OpenTelemetry SDK registered

- **WHEN** the host builds
- **THEN** `AddOpenTelemetry()` SHALL be called with metrics and tracing configured
- **AND** metrics SHALL subscribe to Meters: `"ZipDrive.Caching"`, `"ZipDrive.Zip"`, `"ZipDrive.Dokan"`
- **AND** tracing SHALL subscribe to ActivitySources: `"ZipDrive.Caching"`, `"ZipDrive.Zip"`, `"ZipDrive.Dokan"`
- **AND** runtime instrumentation SHALL be enabled for GC, threadpool, and process metrics
- **AND** OTLP exporter SHALL be configured

#### Scenario: Bare positional arg preprocessed before host creation

- **WHEN** args contains a first element not starting with `--`
- **THEN** the application SHALL prepend `--Mount:ArchiveDirectory=<value>` to the args array before passing to `Host.CreateDefaultBuilder`
