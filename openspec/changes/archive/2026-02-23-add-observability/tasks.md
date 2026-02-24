## 1. Static Telemetry Classes

- [x] 1.1 Create `CacheTelemetry.cs` in `ZipDriveV3.Infrastructure.Caching` — define `internal static` class with `Meter("ZipDriveV3.Caching")`, `ActivitySource("ZipDriveV3.Caching")`, and all cache instruments: `Counter<long> Hits`, `Counter<long> Misses`, `Counter<long> Evictions`, `Histogram<double> MaterializationDuration`, and observable gauge callbacks for size/count/utilization
- [x] 1.2 Create `ZipTelemetry.cs` in `ZipDriveV3.Infrastructure.Archives.Zip` — define `Meter("ZipDriveV3.Zip")`, `ActivitySource("ZipDriveV3.Zip")`, `Histogram<double> ExtractionDuration`, `Counter<long> BytesExtracted`
- [x] 1.3 Create `DokanTelemetry.cs` in `ZipDriveV3.Infrastructure.FileSystem` — define `Meter("ZipDriveV3.Dokan")`, `Histogram<double> ReadDuration`
- [x] 1.4 Create `SizeBucketClassifier.cs` in `ZipDriveV3.Infrastructure.Caching` — static helper method that maps file size in bytes to `"tiny"`, `"small"`, `"medium"`, `"large"`, `"xlarge"`, or `"huge"` string tag
- [x] 1.5 Write unit tests for `SizeBucketClassifier` — verify all 6 boundary values

## 2. GenericCache Instrumentation

- [x] 2.1 Add optional `string name` parameter to `GenericCache<T>` constructor — store as `_name` field, default to `typeof(T).Name`
- [x] 2.2 Add `CacheTelemetry.Hits.Add(1, tier)` in `BorrowAsync` on cache hit (line 117), using `_name` as the `tier` tag value
- [x] 2.3 Add `CacheTelemetry.Misses.Add(1, tier)` in `BorrowAsync` on cache miss (line 128)
- [x] 2.4 Add `CacheTelemetry.Evictions.Add(1, tier, reason)` in `TryEvictEntry` — determine `reason` tag from call site context (`"policy"`, `"expired"`, or `"manual"`)
- [x] 2.5 Wrap factory execution in `MaterializeAndCacheAsync` with `Stopwatch` and record `CacheTelemetry.MaterializationDuration` with `tier` and `size_bucket` tags
- [x] 2.6 Add `Activity` span `"cache.borrow"` in `BorrowAsync` — set tags `tier` and `result` (`"hit"` or `"miss"`)
- [x] 2.7 Add child `Activity` span `"cache.materialize"` in `MaterializeAndCacheAsync` — set tags `tier`, `size_bucket`, `size_bytes`
- [x] 2.8 Add `Activity` span `"cache.evict"` in `EvictIfNeededAsync` — set tags `tier`, `reason`, `evicted_count`, `evicted_bytes`
- [x] 2.9 Register observable gauge callbacks in `CacheTelemetry` — these need references to cache instances; use a static registration pattern where each `GenericCache` instance registers itself on construction
- [x] 2.10 Update existing DI registrations in `Program.cs` to pass `name` parameter: `"memory"`, `"disk"`, `"structure"`

## 3. Log Level Promotion

- [x] 3.1 Promote materialization log (line 209) from `LogInformation` to include `{Tier}` and `{MaterializationMs}` structured properties (already Info level, add missing properties)
- [x] 3.2 Promote eviction log in `TryEvictEntry` (line 375) from `LogDebug` to `LogInformation` — add `{Key}`, `{SizeBytes}`, `{Tier}`, `{Reason}` structured properties
- [x] 3.3 Add `{Tier}` tag to expired entries batch log (line 287)

## 4. ZIP Extraction Metrics

- [x] 4.1 Add `Stopwatch` around the extraction operation in `ZipReader.OpenEntryStreamAsync` — record `ZipTelemetry.ExtractionDuration` with `size_bucket` and `compression` tags
- [x] 4.2 Add `ZipTelemetry.BytesExtracted.Add(uncompressedSize, compression)` after successful extraction
- [x] 4.3 Write unit tests verifying extraction metrics are recorded with correct tags (mock or use `MeterListener`)

## 5. Dokan Read Latency Metric

- [x] 5.1 Add `Stopwatch` in `DokanFileSystemAdapter.ReadFile` — record `DokanTelemetry.ReadDuration` with `result` tag (`"success"` or `"error"`)

## 6. DualTierFileCache

- [x] 6.1 Create `DualTierFileCache.cs` in `ZipDriveV3.Infrastructure.Caching` implementing `ICache<Stream>` — constructor takes `CacheOptions`, `IEvictionPolicy`, `TimeProvider?`, `ILogger?`; internally creates two `GenericCache<Stream>` (memory with `MemoryStorageStrategy`, disk with `DiskStorageStrategy`)
- [x] 6.2 Implement `BorrowAsync` with size-hint routing — add `BorrowAsync` overload or use a convention (e.g., check both tiers for existing entry first, then route new entries by size hint)
- [x] 6.3 Implement aggregated properties: `CurrentSizeBytes`, `CapacityBytes`, `HitRate`, `EntryCount`, `BorrowedEntryCount` — summing/combining both tiers
- [x] 6.4 Implement `EvictExpired()` — delegate to both tiers
- [x] 6.5 Update `ZipVirtualFileSystem.ReadFileAsync` to pass size hint when calling `BorrowAsync` (the VFS has `entry.Value.UncompressedSize` available)
- [x] 6.6 Write unit tests: small file routes to memory tier, large file routes to disk tier, cache hit returns from correct tier, aggregated properties sum correctly
- [x] 6.7 Write integration test: end-to-end read of a small and large file through `DualTierFileCache` with real `MemoryStorageStrategy` and `DiskStorageStrategy`

## 7. CLI OpenTelemetry Wiring

- [x] 7.1 Add NuGet packages to `ZipDriveV3.Cli.csproj`: `OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`, `OpenTelemetry.Instrumentation.Runtime`, `OpenTelemetry.Instrumentation.Process`
- [x] 7.2 Add `AddOpenTelemetry()` registration in `Program.cs` — `.WithMetrics()` subscribing to all 3 Meters + runtime + process instrumentation + OTLP exporter; `.WithTracing()` subscribing to all 3 ActivitySources + OTLP exporter
- [x] 7.3 Add `"OpenTelemetry"` section to `appsettings.json` with `"Endpoint": "http://localhost:4317"`
- [x] 7.4 Replace single `GenericCache<Stream>` DI registration with `DualTierFileCache` registration using `CacheOptions`
- [x] 7.5 Verify no OTel NuGet packages in infrastructure project `.csproj` files

## 8. Verification

- [x] 8.1 Build solution — `dotnet build ZipDriveV3.slnx` passes with zero errors
- [x] 8.2 Run all existing tests — `dotnet test` passes (no regressions)
- [x] 8.3 Run new telemetry unit tests — all pass
- [x] 8.4 Run new DualTierFileCache tests — all pass
- [ ] 8.5 Manual smoke test: start Aspire Dashboard (`docker run -p 18888:18888 -p 4317:4317 mcr.microsoft.com/dotnet/aspire-dashboard`), run ZipDrive with a test ZIP, verify metrics and traces appear in dashboard at `http://localhost:18888`
