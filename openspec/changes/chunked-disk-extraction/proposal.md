## Why

For files routed to the disk tier (>= 50MB), `DiskStorageStrategy` extracts the entire decompressed file to a temp file before serving the first byte. A 5GB video blocks the user for ~25 seconds; even a 200MB file takes ~1 second. Every disk-tier cache miss has perceptible latency. Incremental chunk-based extraction eliminates this by serving the first byte after a single 10MB chunk (~50ms), while decompression continues in the background.

## What Changes

- **Replace `DiskStorageStrategy` with `ChunkedDiskStorageStrategy`**: Drop-in replacement implementing `IStorageStrategy<Stream>`. Extracts files in fixed-size chunks (default 10MB) to an NTFS sparse file. Returns after the first chunk is ready; background task continues extracting remaining chunks.
- **New `ChunkedFileEntry`**: Internal type replacing `DiskCacheEntry`. Tracks chunk state via `BitArray` + `TaskCompletionSource<bool>[]`. Owns the sparse backing file, background extraction task, and `CancellationTokenSource`.
- **New `ChunkedStream`**: `Stream` subclass replacing `MemoryMappedViewStream` returned by old `Retrieve()`. Maps reads to chunks, blocks on unextracted regions via `EnsureChunkReadyAsync()`, handles cross-chunk boundary reads.
- **Remove `DiskStorageStrategy` and `DiskCacheEntry`**: Fully replaced, no longer needed.
- **New `ChunkSizeMb` configuration**: Added to `CacheOptions` (default 10MB).
- **No changes to `GenericCache<T>`**: All existing concurrency (Lazy<Task>, RefCount, eviction lock), borrow/return pattern, TTL, capacity tracking, and telemetry work unchanged.
- **No changes to tier routing**: `FileContentCache` keeps the same two-tier split (memory < 50MB, disk >= 50MB). Only the disk tier's storage strategy changes.

## Capabilities

### New Capabilities
- `chunked-disk-extraction`: Incremental chunk-based decompression for disk-tier cache entries. Covers chunk lifecycle, sparse file management, background extraction, per-chunk completion signaling, ChunkedStream read semantics, concurrent reader access during extraction, and extraction cancellation on eviction.

### Modified Capabilities
- `file-content-cache`: Disk tier storage strategy changes from extract-all (`DiskStorageStrategy`) to incremental (`ChunkedDiskStorageStrategy`). Materialization completes after first chunk instead of full file. `CacheFactoryResult.OnDisposed` lifecycle extends to cover background extraction. New `ChunkSizeMb` config option.
- `dual-tier-cache-coordinator`: Disk tier scenario updated — `DiskStorageStrategy` replaced by `ChunkedDiskStorageStrategy`. Routing logic unchanged but disk tier behavior changes (incremental materialization, `ChunkedStream` returned by `Retrieve()`).

## Impact

- **Caching layer** (`src/ZipDrive.Infrastructure.Caching`): `DiskStorageStrategy.cs` and `DiskCacheEntry` removed. New files: `ChunkedDiskStorageStrategy.cs`, `ChunkedFileEntry.cs`, `ChunkedStream.cs`. `CacheOptions.cs` gains `ChunkSizeMb` property.
- **FileContentCache**: Constructor changes `DiskStorageStrategy` → `ChunkedDiskStorageStrategy`. No routing logic changes.
- **Configuration**: `appsettings.jsonc` gains `ChunkSizeMb` under `Cache` section.
- **Tests**: Existing disk-tier tests adapted for chunked behavior. New tests for chunk lifecycle, concurrent reads during extraction, cross-chunk boundary reads, extraction cancellation, and endurance scenarios.
- **No external API changes**: `IFileContentCache`, `ICache<T>`, `ICacheHandle<T>` interfaces unchanged. DokanNet adapter and VFS layer unaffected.
