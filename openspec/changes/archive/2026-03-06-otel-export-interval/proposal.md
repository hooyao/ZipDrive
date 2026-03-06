## Why

The default OTLP metric export interval is 60 seconds. With sparse data points, dashboard viewers (Aspire Dashboard, otel-desktop-viewer) interpolate gaps between points as zero, causing cliff-like drops in charts for always-present metrics like `cache.size_bytes`, `process.memory.usage`, and `process.cpu.count`. A shorter, configurable export interval fills the gaps so charts render smoothly.

## What Changes

- Add `MetricExportIntervalSeconds` to the `OpenTelemetry` configuration section (default: 15)
- Wire `PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds` from the new setting in `Program.cs`
- Update `appsettings.jsonc` with the new default

## Capabilities

### New Capabilities

_None_ — this is a configuration enhancement, not a new capability.

### Modified Capabilities

- `cli-application`: Add configurable metric export interval to the OpenTelemetry SDK registration

## Impact

- **Code**: `Program.cs` OTel metrics builder, `appsettings.jsonc`
- **Config**: New optional key `OpenTelemetry:MetricExportIntervalSeconds`
- **Dependencies**: No new NuGet packages (uses existing `OpenTelemetry.Exporter.OpenTelemetryProtocol` API)
- **Breaking**: None — existing configurations without the new key get a sensible default (15s)
