## 1. Configuration

- [x] 1.1 Add `MetricExportIntervalSeconds` (default: 15) to `OpenTelemetry` section in `appsettings.jsonc`

## 2. Implementation

- [x] 2.1 Update `Program.cs` to read `OpenTelemetry:MetricExportIntervalSeconds` from configuration
- [x] 2.2 Pass `MetricReaderOptions` with `ExportIntervalMilliseconds` to `.AddOtlpExporter()` overload

## 3. Documentation

- [x] 3.1 Update `CLAUDE.md` OpenTelemetry configuration section with new setting
