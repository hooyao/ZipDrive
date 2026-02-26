## Why

The cache factory in `ZipVirtualFileSystem.ReadFileAsync` buffers the entire decompressed file into a `MemoryStream` before handing it to the storage strategy. This causes three problems: (1) hard crash for files >2.1GB (`MemoryStream` limited to `int.MaxValue`), (2) pointless RAM waste for disk-tier files (a 500MB file is buffered in RAM just to be copied to a temp file), and (3) ZIP extraction logic leaking into the Application layer via a factory lambda that constructs `IZipReader`, decompresses entries, and manages `CacheFactoryResult<Stream>` — all domain knowledge that belongs in Infrastructure.

## What Changes

- **BREAKING**: `IStorageStrategy<T>.StoreAsync(CacheFactoryResult<T>)` renamed to `MaterializeAsync(Func<...> factory)` — the storage strategy now receives the factory delegate and drives the full materialization pipeline (call factory, consume stream, dispose resources, return `StoredEntry`).
- `CacheFactoryResult<T>` gains `IAsyncDisposable` with an optional `OnDisposed` callback, enabling the strategy to clean up factory-produced resources (e.g., `IZipReader` file handles) after consuming the stream.
- `GenericCache<T>.MaterializeAndCacheAsync` simplified: delegates to a single `strategy.MaterializeAsync(factory, ct)` call instead of separate factory + store calls. Pre-eviction removed (post-store eviction already proven safe by Soft Capacity design).
- `DualTierFileCache` replaced by `FileContentCache` — a purpose-built class that owns ZIP extraction, tier routing, and caching. Implements new `IFileContentCache` domain interface.
- `ZipVirtualFileSystem` simplified: removes `IZipReaderFactory`, `CacheOptions`, and factory lambda dependencies. Calls `IFileContentCache.ReadAsync(archivePath, entry, cacheKey, buffer, offset, ct)` instead.
- For disk tier: decompressed bytes stream directly from ZIP to temp file (~80KB buffer), eliminating the intermediate `MemoryStream` entirely.

## Capabilities

### New Capabilities
- `file-content-read`: Domain interface (`IFileContentCache`) for reading file content from archives, hiding caching and extraction details from the Application layer.

### Modified Capabilities
- `file-content-cache`: Storage strategy interface changes from `StoreAsync(result)` to `MaterializeAsync(factory)`. `CacheFactoryResult<T>` becomes `IAsyncDisposable`. Pre-eviction removed from `GenericCache`.
- `dual-tier-cache-coordinator`: `DualTierFileCache` replaced by `FileContentCache` which merges tier routing with extraction ownership. The coordinator no longer exists as a standalone class.
- `virtual-file-system`: `ReadFileAsync` implementation delegates to `IFileContentCache` instead of constructing a factory lambda. `IZipReaderFactory` and `CacheOptions` dependencies removed from `ZipVirtualFileSystem`.

## Impact

- **Code**: `IStorageStrategy<T>` (interface change), `GenericCache<T>`, `DiskStorageStrategy`, `MemoryStorageStrategy`, `ObjectStorageStrategy<T>`, `CacheFactoryResult<T>`, `ZipVirtualFileSystem`, `DualTierFileCache` (removed), new `FileContentCache` and `IFileContentCache`.
- **Tests**: All tests referencing `IStorageStrategy.StoreAsync`, `DualTierFileCache`, or the factory delegate pattern need updating. Thundering herd and concurrent access tests should verify identical behavior.
- **DI**: `Program.cs` wires `FileContentCache` instead of `DualTierFileCache`. `IZipReaderFactory` no longer injected into `ZipVirtualFileSystem`.
- **No runtime behavior change** for cache hits, thundering herd prevention, eviction, RefCount, or TTL. The `Lazy<Task<CacheEntry>>` mechanism in `GenericCache.BorrowAsync` is untouched.
