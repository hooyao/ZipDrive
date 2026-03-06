## Context

The OTel metrics pipeline uses `PeriodicExportingMetricReader` which defaults to a 60-second export interval. Observable gauges (cache size, process memory, CPU) are collected and exported on this timer. Dashboard viewers interpolate between sparse data points, rendering cliff-like drops to zero between 60s intervals — making it appear that metrics are missing.

Current OTel setup in `Program.cs` calls `.AddOtlpExporter()` with no `MetricReaderOptions`, inheriting the SDK default of 60s.

## Goals / Non-Goals

**Goals:**
- Reduce default metric export interval to 15 seconds for smooth dashboard rendering
- Make the interval configurable via `appsettings.jsonc` / CLI args

**Non-Goals:**
- Changing trace export behavior (traces are event-driven, not periodic)
- Adding new metrics instruments
- Changing the OTLP protocol or exporter type

## Decisions

### Use `AddOtlpExporter` overload with `MetricReaderOptions`

The `AddOtlpExporter` method has an overload accepting `MetricReaderOptions`. This is the simplest way to set the export interval without changing the exporter pipeline:

```csharp
.AddOtlpExporter(
    o => o.Endpoint = new Uri(endpoint),
    new MetricReaderOptions
    {
        PeriodicExportingMetricReaderOptions = new()
        {
            ExportIntervalMilliseconds = intervalMs
        }
    })
```

**Alternative considered**: Manually creating a `PeriodicExportingMetricReader` and adding it via `.AddReader()`. This is more verbose and offers no benefit for our use case.

### Read interval from configuration, default 15s

The interval is read from `OpenTelemetry:MetricExportIntervalSeconds` as an integer. If absent or zero, default to 15. This keeps the config schema simple (integer seconds) while the SDK needs milliseconds (multiply by 1000).

**Why 15s?** Balances chart smoothness against export overhead. At 15s, dashboards get 4 data points per minute — enough for a continuous line. Going lower (e.g., 5s) adds marginal visual benefit with 3x the export volume.

## Risks / Trade-offs

- **[Increased OTLP traffic]** → 4x more export calls than default. Negligible for local dashboards; for remote collectors, users can increase the interval via config.
- **[Config key naming]** → Using `MetricExportIntervalSeconds` (not `ExportIntervalSeconds`) to leave room for future trace batch interval config without ambiguity.
