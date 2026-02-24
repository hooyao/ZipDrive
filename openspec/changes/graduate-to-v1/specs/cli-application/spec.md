## MODIFIED Requirements

### Requirement: OpenTelemetry SDK registered

The CLI application SHALL use `Host.CreateDefaultBuilder(args)` for configuration, DI, and lifecycle management. The host SHALL also register OpenTelemetry SDK for metrics and tracing export.

#### Scenario: OpenTelemetry SDK registered

- **WHEN** the host builds
- **THEN** `AddOpenTelemetry()` SHALL be called with metrics and tracing configured
- **AND** metrics SHALL subscribe to Meters: `"ZipDrive.Caching"`, `"ZipDrive.Zip"`, `"ZipDrive.Dokan"`
- **AND** tracing SHALL subscribe to ActivitySources: `"ZipDrive.Caching"`, `"ZipDrive.Zip"`, `"ZipDrive.Dokan"`
- **AND** runtime instrumentation SHALL be enabled for GC, threadpool, and process metrics
- **AND** OTLP exporter SHALL be configured
