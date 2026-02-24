## Context

ZipDrive V3 has ~99 structured log statements (Serilog) but zero metrics infrastructure, zero distributed tracing, and no way to measure cache efficiency or extraction performance. The caching layer (`GenericCache<T>`) already tracks `_hits`/`_misses` internally and logs at Debug level for cache/evict/expire events, but these are not exportable as metrics.

Additionally, the dual-tier cache architecture was designed (both `MemoryStorageStrategy` and `DiskStorageStrategy` exist, `CacheOptions.SmallFileCutoffMb` is defined) but never wired together. Currently `Program.cs` registers a single `GenericCache<Stream>` with `MemoryStorageStrategy` only — all files load into memory regardless of size, risking OOM for large files.

The VFS (`ZipVirtualFileSystem`) takes `ICache<Stream>` directly and has no awareness of tiers.

## Goals / Non-Goals

**Goals:**
- Emit metrics via `System.Diagnostics.Metrics` from infrastructure projects (zero OTel NuGet deps in libraries)
- Export metrics, traces, and logs via OpenTelemetry SDK to Aspire Dashboard (OTLP) from CLI project only
- Get CPU/memory/GC metrics for free via `OpenTelemetry.Instrumentation.Runtime`
- Wire up the dual-tier file content cache using the existing `MemoryStorageStrategy`, `DiskStorageStrategy`, and `CacheOptions.SmallFileCutoffMb`
- Trace cache operations (borrow hit/miss, materialization, eviction) — not per-Dokan-request
- Promote cache lifecycle log events (cached, evicted, TTL expired) from Debug to Information

**Non-Goals:**
- Per-ReadFile/FindFiles/GetFileInformation Dokan tracing (too noisy, not needed)
- Custom dashboards or Grafana configuration
- Alerting or SLO definitions
- Distributed tracing across multiple processes (single-process app)
- Custom health check endpoints (future work)

## Decisions

### Decision 1: Instrument with `System.Diagnostics.*`, export with OpenTelemetry

**Choice**: Use `System.Diagnostics.Metrics` (Meter, Counter, Histogram, ObservableGauge) and `System.Diagnostics.ActivitySource` for instrumentation. Wire OpenTelemetry SDK + OTLP exporter only in `ZipDriveV3.Cli`.

**Why over alternative (OTel SDK everywhere)**: Infrastructure projects stay dependency-free. `System.Diagnostics.Metrics` is built into the .NET runtime — no NuGet packages needed. OTel SDK subscribes to these instruments at the composition root. This preserves clean architecture: domain/infrastructure layers have zero coupling to any telemetry vendor.

**Why over alternative (Prometheus-net directly)**: Prometheus-net is vendor-specific. `System.Diagnostics.Metrics` is vendor-neutral — can switch exporters (OTLP, Prometheus, Console) without changing instrumentation code.

### Decision 2: Aspire Dashboard for local visualization

**Choice**: Use `mcr.microsoft.com/dotnet/aspire-dashboard` Docker container as the OTLP receiver and UI.

**Why over Jaeger+Prometheus+Grafana**: Single container vs 3-4 containers. Shows traces, metrics, and logs in one UI. .NET-native, designed for exactly this use case. Lower barrier for dev workflow.

**Configuration**: OTLP endpoint defaults to `http://localhost:4317` (gRPC) or `http://localhost:4318` (HTTP). Configurable via `appsettings.json` under `"OpenTelemetry"` section.

### Decision 3: Three static Meter/ActivitySource instances by subsystem

**Choice**: Define three `Meter` instances and three `ActivitySource` instances, one per subsystem:

```
Meter: "ZipDriveV3.Caching"      → cache hits, misses, evictions, size, materialization duration
Meter: "ZipDriveV3.Zip"          → extraction duration (bucketed by file size)
Meter: "ZipDriveV3.Dokan"        → read request latency (overall Dokan-to-response)

ActivitySource: "ZipDriveV3.Caching"  → spans for borrow, materialize, evict
ActivitySource: "ZipDriveV3.Zip"      → spans for extraction
ActivitySource: "ZipDriveV3.Dokan"    → (reserved, not used initially)
```

**Why not one global Meter**: Granular subscription — can enable/disable metrics per subsystem. Follows OTel convention of one Meter per library/module.

**Why static**: `Meter` and `ActivitySource` are designed to be long-lived singletons. Static fields in a dedicated class per project avoid DI complexity and match .NET guidance.

### Decision 4: Dual-tier coordinator via `DualTierFileCache`

**Choice**: Introduce `DualTierFileCache` that implements `ICache<Stream>` and wraps two `GenericCache<Stream>` instances (one with `MemoryStorageStrategy`, one with `DiskStorageStrategy`). Routes based on `CacheOptions.SmallFileCutoffBytes`.

```
┌─────────────────────────────────────────────────┐
│           DualTierFileCache : ICache<Stream>     │
│                                                  │
│  BorrowAsync(key, ttl, factory, ct)              │
│    │                                             │
│    ├─ sizeBytes known? (from factory result)     │
│    │  Problem: size unknown until factory runs   │
│    │                                             │
│    │  Solution: Wrap factory to intercept result │
│    │  and route AFTER materialization             │
│    │                                             │
│    ├─ < SmallFileCutoffBytes → _memoryCache      │
│    └─ >= SmallFileCutoffBytes → _diskCache        │
│                                                  │
│  Aggregated properties:                          │
│    CurrentSizeBytes = mem + disk                 │
│    HitRate = combined                            │
│    EntryCount = mem + disk                       │
│                                                  │
│  Metrics tags: tier=memory | tier=disk           │
└─────────────────────────────────────────────────┘
```

**Routing challenge**: The `ICache<T>.BorrowAsync()` signature takes a factory that returns `CacheFactoryResult<T>` — the size is only known after the factory executes. Two approaches:

- **Option A (Chosen): Size-hint parameter**. The caller (`ZipVirtualFileSystem`) already has `ZipEntryInfo.UncompressedSize` before calling `BorrowAsync`. Add an overload or a `sizeHint` to `CacheFactoryResult<T>` so the coordinator can route before materialization. This is clean because the VFS always knows the expected file size from the ZIP Central Directory metadata.

- **Option B (Rejected): Post-materialization routing**. Let the factory run first, then move the result to the correct tier. Rejected because it requires double-copy (materialize to temp, then store in correct tier) and adds latency.

**Implementation**: `DualTierFileCache` takes `CacheOptions`, creates two `GenericCache<Stream>` internally (or receives them via DI). `ZipVirtualFileSystem` continues to depend on `ICache<Stream>` — no API change needed. DI registration changes from a single `GenericCache<Stream>` to `DualTierFileCache`.

### Decision 5: Metrics instrument catalog

| Instrument | Type | Unit | Tags | Location |
|---|---|---|---|---|
| `cache.hits` | Counter\<long\> | `{hit}` | `tier=memory\|disk\|structure` | `GenericCache.BorrowAsync` L117 |
| `cache.misses` | Counter\<long\> | `{miss}` | `tier=memory\|disk\|structure` | `GenericCache.BorrowAsync` L128 |
| `cache.evictions` | Counter\<long\> | `{eviction}` | `reason=policy\|expired\|manual`, `tier` | `GenericCache.TryEvictEntry` L360 |
| `cache.size_bytes` | ObservableGauge\<long\> | `By` | `tier=memory\|disk\|structure` | Polls `CurrentSizeBytes` |
| `cache.entry_count` | ObservableGauge\<int\> | `{entry}` | `tier=memory\|disk\|structure` | Polls `EntryCount` |
| `cache.utilization` | ObservableGauge\<double\> | `1` | `tier` | Polls `CurrentSizeBytes / CapacityBytes` |
| `cache.materialization.duration` | Histogram\<double\> | `ms` | `size_bucket=tiny\|small\|medium\|large\|xlarge\|huge`, `tier` | `GenericCache.MaterializeAndCacheAsync` L175 |
| `zip.extraction.duration` | Histogram\<double\> | `ms` | `size_bucket`, `compression=store\|deflate` | `ZipReader.OpenEntryStreamAsync` |
| `zip.bytes_extracted` | Counter\<long\> | `By` | `compression` | `ZipReader.OpenEntryStreamAsync` |
| `dokan.read.duration` | Histogram\<double\> | `ms` | `result=success\|error` | `DokanFileSystemAdapter.ReadFile` |

**Size buckets for histograms:**

| Bucket | Range | Rationale |
|---|---|---|
| `tiny` | < 1 KB | Config files, manifests |
| `small` | 1 KB – 1 MB | Source code, small docs |
| `medium` | 1 MB – 10 MB | Images, small binaries |
| `large` | 10 MB – 50 MB | Near memory/disk cutoff |
| `xlarge` | 50 MB – 500 MB | Disk-cached territory |
| `huge` | > 500 MB | Very large files |

### Decision 6: GenericCache needs a `name` parameter for metric tagging

**Problem**: `GenericCache<T>` is generic — the same class backs memory tier, disk tier, and structure cache. Metrics need a `tier` tag to distinguish them.

**Solution**: Add an optional `string name` parameter to `GenericCache` constructor. This name is used as the `tier` tag value on all metrics emitted by that instance. Examples: `"memory"`, `"disk"`, `"structure"`.

The `Meter` and instruments are static (shared across instances), but each `GenericCache` instance records with its own `tier` tag. This means a single `cache.hits` counter shows breakdowns by tier in the dashboard.

### Decision 7: Tracing scoped to cache operations only

**Choice**: Create `Activity` spans for:
- `cache.borrow` — one span per `BorrowAsync` call, tagged with `hit`/`miss` and `tier`
- `cache.materialize` — child span of `cache.borrow` on miss, captures factory + store duration
- `cache.evict` — span per eviction batch (not per-entry), tagged with `reason` and count

**Not traced**: Individual Dokan `ReadFile`/`FindFiles`/`GetFileInformation` calls. These happen at very high frequency and would create excessive trace volume without adding insight beyond what the cache traces provide.

### Decision 8: Log level promotion for lifecycle events

**Current → Proposed:**

| Event | Current Level | Proposed Level | Structured Properties |
|---|---|---|---|
| File cached (materialized) | `Debug` (L209) | `Information` | `{Key}`, `{SizeBytes}`, `{SizeMb}`, `{Tier}`, `{MaterializationMs}`, `{UtilizationPct}` |
| File evicted | `Debug` (L375) | `Information` | `{Key}`, `{SizeBytes}`, `{Tier}`, `{Reason}` |
| TTL expired batch | `Information` (L287) | `Information` (keep) | Add `{Tier}` tag |
| Cache hit | `Debug` (L119) | `Debug` (keep) | Too frequent for Info |
| Cache miss | `Debug` (L129) | `Debug` (keep) | Too frequent for Info |

### Decision 9: OTel SDK wiring in CLI

**NuGet packages** (CLI project only):
- `OpenTelemetry.Extensions.Hosting` — `AddOpenTelemetry()` host builder extension
- `OpenTelemetry.Exporter.OpenTelemetryProtocol` — OTLP exporter for Aspire Dashboard
- `OpenTelemetry.Instrumentation.Runtime` — GC, threadpool, assembly metrics (free)
- `OpenTelemetry.Instrumentation.Process` — process memory, CPU, handle count (free)

**Registration pattern:**
```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m
        .AddMeter("ZipDriveV3.Caching")
        .AddMeter("ZipDriveV3.Zip")
        .AddMeter("ZipDriveV3.Dokan")
        .AddRuntimeInstrumentation()
        .AddProcessInstrumentation()
        .AddOtlpExporter())
    .WithTracing(t => t
        .AddSource("ZipDriveV3.Caching")
        .AddSource("ZipDriveV3.Zip")
        .AddSource("ZipDriveV3.Dokan")
        .AddOtlpExporter());
```

**Configuration** (`appsettings.json`):
```json
{
  "OpenTelemetry": {
    "Endpoint": "http://localhost:4317",
    "Protocol": "Grpc"
  }
}
```

### Decision 10: Where to define static instruments

**Choice**: Each infrastructure project defines its own static telemetry class:

- `ZipDriveV3.Infrastructure.Caching/CacheTelemetry.cs` — Meter + ActivitySource + all cache instruments
- `ZipDriveV3.Infrastructure.Archives.Zip/ZipTelemetry.cs` — Meter + ActivitySource + extraction instruments
- `ZipDriveV3.Infrastructure.FileSystem/DokanTelemetry.cs` — Meter + ActivitySource + read latency

These are `internal static` classes with `public static readonly` instruments. No DI needed — `System.Diagnostics` instruments are designed for this pattern.

## Risks / Trade-offs

**[Risk] Metric cardinality explosion from cache keys** → Mitigation: Never use cache key as a metric tag. Only use bounded tag sets: `tier`, `reason`, `size_bucket`, `compression`, `result`.

**[Risk] Trace volume from high-frequency cache hits** → Mitigation: Only trace cache operations, not per-Dokan-request. Cache hits are a Counter, not a Trace span. `cache.borrow` spans are only created when sampling allows (OTel SDK controls sampling rate).

**[Risk] DualTierFileCache adds complexity** → Mitigation: It implements the same `ICache<Stream>` interface. VFS is unchanged. The coordinator is thin (~100 lines) — it delegates all real work to the two underlying `GenericCache<Stream>` instances.

**[Risk] Size-hint routing requires caller awareness** → Mitigation: The VFS always has `ZipEntryInfo.UncompressedSize` available. A size hint on `CacheFactoryResult<T>` is optional and backward-compatible.

**[Trade-off] Aspire Dashboard requires Docker** → Acceptable for local dev. Developers without Docker can use `dotnet-counters` (works with `System.Diagnostics.Metrics` with no code changes) or the Console exporter as fallback.

**[Trade-off] .NET 10 OTel package compatibility** → The project targets .NET 10 preview. OTel packages may not have stable releases for .NET 10 yet. Mitigation: OTel packages target `netstandard2.0` / `net8.0` and generally work on newer runtimes. Pin to latest stable versions.
