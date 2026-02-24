## Why

ZipDrive V3 has ~99 structured log statements but zero metrics, zero tracing, and no way to measure cache efficiency, extraction performance, or resource usage. Without observability, we cannot answer basic operational questions: Is the cache helping? Which files are slow to extract? Is memory under pressure? Additionally, the dual-tier cache (memory + disk) was designed but never wired up — `DiskStorageStrategy` exists but is unused, and all files load into memory regardless of size, risking OOM on large files.

## What Changes

- **Add OpenTelemetry instrumentation** using `System.Diagnostics.Metrics` and `System.Diagnostics.ActivitySource` in infrastructure projects (zero OTel package dependencies in libraries)
- **Add OpenTelemetry SDK + OTLP exporter** in CLI project only, targeting Aspire Dashboard for local visualization
- **Add `OpenTelemetry.Instrumentation.Runtime`** for free CPU/memory/GC/threadpool metrics
- **Wire up dual-tier file content cache** — route files < `SmallFileCutoffMb` to `MemoryStorageStrategy`, files >= cutoff to `DiskStorageStrategy` via a new coordinator
- **Add cache lifecycle logging** — promote file-cached, file-evicted, and TTL-expired log events from Debug to Information with structured properties
- **Add cache-focused tracing** — `ActivitySource` spans for cache borrow (hit/miss), materialization, and eviction (not per-Dokan-operation)
- **Add metrics instruments**:
  - `Counter`: cache hits/misses (tagged by tier), cache evictions (tagged by reason), bytes read
  - `Histogram`: materialization duration (tagged by size bucket), extraction duration from ZIP
  - `ObservableGauge`: cache size bytes, cache entry count, cache utilization percentage

## Capabilities

### New Capabilities
- `telemetry`: OpenTelemetry instrumentation layer — defines metrics instruments, activity sources, and export configuration. Covers cache metrics, extraction metrics, and runtime metrics.
- `dual-tier-cache-coordinator`: Routes file content cache requests to memory or disk tier based on file size threshold. Wraps two `GenericCache<Stream>` instances behind a unified interface.

### Modified Capabilities
- `file-content-cache`: Add requirement for observable metrics to be emitted via `System.Diagnostics.Metrics` (currently only exposes properties). Add requirement for structured log events at Information level for cache/evict/expire lifecycle events.
- `cli-application`: Add OpenTelemetry SDK registration and Aspire Dashboard OTLP export configuration.

## Impact

- **New NuGet packages** (CLI project only): `OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`, `OpenTelemetry.Instrumentation.Runtime`, `OpenTelemetry.Instrumentation.Process`
- **New project or namespace**: Metrics/tracing instrument definitions (static `Meter` and `ActivitySource` instances)
- **Modified DI registration**: `Program.cs` changes to wire dual-tier coordinator and OTel SDK
- **Modified `GenericCache`**: Add `System.Diagnostics.Metrics` counter/histogram recordings alongside existing log statements
- **Modified `ZipVirtualFileSystem`**: Use coordinator instead of single `ICache<Stream>`
- **New `appsettings.json` section**: OTLP endpoint configuration (defaults to `localhost:4317` for Aspire Dashboard)
- **No breaking changes to public APIs**
