## MODIFIED Requirements

### Requirement: OpenTelemetry OTLP export configuration

The CLI application SHALL support configurable OTLP export for sending telemetry to an OpenTelemetry collector or Aspire Dashboard. The metric export interval SHALL be configurable via `OpenTelemetry:MetricExportIntervalSeconds`, defaulting to 15 seconds.

#### Scenario: Custom OTLP endpoint from configuration

- **WHEN** `appsettings.json` contains an `"OpenTelemetry"` section with `"Endpoint"` set to a custom URL
- **THEN** the OTLP exporter SHALL use the configured endpoint

#### Scenario: Default metric export interval

- **WHEN** `OpenTelemetry:MetricExportIntervalSeconds` is absent or zero
- **THEN** the `PeriodicExportingMetricReader` SHALL use an export interval of 15000 milliseconds

#### Scenario: Custom metric export interval

- **WHEN** `OpenTelemetry:MetricExportIntervalSeconds` is set to a positive integer (e.g., 30)
- **THEN** the `PeriodicExportingMetricReader` SHALL use that value multiplied by 1000 as `ExportIntervalMilliseconds`

#### Scenario: OpenTelemetry disabled by default

- **WHEN** no custom OpenTelemetry endpoint is configured (empty or absent `Endpoint`)
- **THEN** the OpenTelemetry SDK SHALL NOT be registered (zero overhead, opt-in)

#### Scenario: Telemetry gracefully degrades when collector unavailable

- **WHEN** the OTLP endpoint is unreachable (no Aspire Dashboard running)
- **THEN** the application SHALL continue to function normally
- **AND** telemetry export failures SHALL NOT cause application errors or crashes

---

### Requirement: Configuration file includes OpenTelemetry section

The `appsettings.json` SHALL include an `"OpenTelemetry"` configuration section with export interval.

#### Scenario: OpenTelemetry section in appsettings.json

- **WHEN** the default `appsettings.json` is deployed
- **THEN** it SHALL contain an `"OpenTelemetry"` section with `"Endpoint"` defaulting to `""` (disabled)
- **AND** `"MetricExportIntervalSeconds"` defaulting to `15`
