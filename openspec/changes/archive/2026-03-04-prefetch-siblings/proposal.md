## Why

When a user or application opens one file inside a ZIP archive, sibling files in the same directory are almost always accessed next (e.g., Explorer thumbnail generation, media players, game engines). Each sibling currently incurs a full seek + decompress cold-start penalty. By speculatively reading and caching siblings in a single sequential pass through the ZIP after the first file is accessed, subsequent opens become cache hits at near-zero latency.

## What Changes

- Add `WarmAsync` to `IFileContentCache` / `FileContentCache` — a "push already-decompressed stream into cache" path, bypassing the internal `IZipReader` extraction pipeline.
- Add `PrefetchSiblingsAsync` to `ZipVirtualFileSystem` — triggered fire-and-forget on both `ReadFileAsync` and `FindFilesAsync`. Performs span selection and one sequential raw read of the ZIP, decompress only wanted files, discard holes without decompressing.
- Add `SpanSelector` — pure static algorithm that selects the optimal contiguous span of sibling entries around a trigger file, respecting fill-ratio threshold, max-files, and max-directory-file caps.
- Add per-directory in-flight guard (`ConcurrentDictionary<string, byte>`) in `ZipVirtualFileSystem` to prevent duplicate concurrent prefetch reads of the same directory.
- Add five new config keys under `Cache:` in `CacheOptions` / `appsettings.jsonc`.
- Add telemetry counters and histogram for prefetch operations to `CacheTelemetry`.

## Capabilities

### New Capabilities

- `prefetch-siblings`: Speculative sibling pre-warming — span selection algorithm, sequential multi-entry ZIP read, discard-not-decompress for holes, dual trigger points (ReadFile + FindFiles), in-flight deduplication guard, and configuration options.

### Modified Capabilities

- `file-content-cache`: Add `WarmAsync` method to the interface and implementation — new requirement for callers to push pre-materialized streams into the cache without going through the extraction pipeline.

## Impact

- **`ZipDrive.Domain`**: `IFileContentCache` gets a new `WarmAsync` method (additive, not breaking for existing callers).
- **`ZipDrive.Infrastructure.Caching`**: `FileContentCache` implements `WarmAsync`; `CacheOptions` gains 5 new config properties; `CacheTelemetry` gains prefetch metrics.
- **`ZipDrive.Application`**: `ZipVirtualFileSystem` gains `PrefetchSiblingsAsync`, `SpanSelector`, per-directory in-flight guard, and trigger hooks in `ReadFileAsync` / `FindFilesAsync`.
- **`appsettings.jsonc`**: New `Cache:Prefetch*` keys with documented defaults.
- No changes to: `GenericCache`, `MemoryStorageStrategy`, `ChunkedDiskStorageStrategy`, `ChunkedFileEntry`, `ChunkedStream`, `IZipReader`, `ZipReader`.
