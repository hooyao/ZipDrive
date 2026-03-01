## ADDED Requirements

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

---

### Requirement: OpenTelemetry OTLP export configuration

The CLI application SHALL support configurable OTLP export for sending telemetry to an OpenTelemetry collector or Aspire Dashboard.

#### Scenario: OpenTelemetry disabled by default

- **WHEN** no custom OpenTelemetry endpoint is configured (empty or absent `Endpoint`)
- **THEN** the OpenTelemetry SDK SHALL NOT be registered (zero overhead, opt-in)

#### Scenario: Custom OTLP endpoint from configuration

- **WHEN** `appsettings.json` contains an `"OpenTelemetry"` section with `"Endpoint"` set to a custom URL
- **THEN** the OTLP exporter SHALL use the configured endpoint

#### Scenario: Telemetry gracefully degrades when collector unavailable

- **WHEN** the OTLP endpoint is unreachable (no Aspire Dashboard running)
- **THEN** the application SHALL continue to function normally
- **AND** telemetry export failures SHALL NOT cause application errors or crashes

---

### Requirement: OpenTelemetry NuGet packages in CLI only

The CLI project SHALL be the only project that references OpenTelemetry NuGet packages.

#### Scenario: Required OTel packages

- **WHEN** the CLI project is built
- **THEN** it SHALL reference: `OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`, `OpenTelemetry.Instrumentation.Runtime`, `OpenTelemetry.Instrumentation.Process`

---

### Requirement: Dual-tier cache DI registration

The CLI application SHALL register `DualTierFileCache` as the `ICache<Stream>` implementation, replacing the current single-tier memory-only registration.

#### Scenario: DualTierFileCache wired with both tiers

- **WHEN** the DI container resolves `ICache<Stream>`
- **THEN** it SHALL return a `DualTierFileCache` instance
- **AND** the memory tier SHALL be a `GenericCache<Stream>` with `MemoryStorageStrategy` and capacity from `CacheOptions.MemoryCacheSizeBytes`
- **AND** the disk tier SHALL be a `GenericCache<Stream>` with `DiskStorageStrategy` and capacity from `CacheOptions.DiskCacheSizeBytes`
- **AND** the cutoff SHALL be `CacheOptions.SmallFileCutoffBytes`

---

### Requirement: Configuration file includes OpenTelemetry section

The `appsettings.json` SHALL include an `"OpenTelemetry"` configuration section.

#### Scenario: OpenTelemetry section in appsettings.json

- **WHEN** the default `appsettings.json` is deployed
- **THEN** it SHALL contain an `"OpenTelemetry"` section with `"Endpoint"` defaulting to `""` (disabled; user must set endpoint to enable)
