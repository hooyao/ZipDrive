## 1. CacheFactoryResult<T> — Add IAsyncDisposable

- [x] 1.1 Add `IAsyncDisposable` to `CacheFactoryResult<T>` with `Func<ValueTask>? OnDisposed` property. `DisposeAsync` disposes `Value` (if disposable) then invokes `OnDisposed`.
- [x] 1.2 Add unit tests: dispose with `IDisposable` Value, dispose with `IAsyncDisposable` Value, dispose with `OnDisposed` callback, dispose with non-disposable Value (no-op), double-dispose is safe.

## 2. IStorageStrategy<T> — Rename StoreAsync to MaterializeAsync

- [x] 2.1 Change `IStorageStrategy<T>.StoreAsync(CacheFactoryResult<T>, CancellationToken)` to `MaterializeAsync(Func<CancellationToken, Task<CacheFactoryResult<T>>>, CancellationToken)` returning `Task<StoredEntry>`.
- [x] 2.2 Update `MemoryStorageStrategy.MaterializeAsync`: call factory, `CopyToAsync` to `MemoryStream`, extract `byte[]`, dispose factory result via `await using`, return `StoredEntry`.
- [x] 2.3 Update `DiskStorageStrategy.MaterializeAsync`: call factory, pipe `result.Value` directly to temp `FileStream` via `CopyToAsync` (no intermediate buffer), dispose factory result via `await using`, create MMF, return `StoredEntry`. Handle factory exceptions by cleaning up partial temp files.
- [x] 2.4 Update `ObjectStorageStrategy<T>.MaterializeAsync`: call factory, box the value, dispose factory result (no-op for non-disposable), return `StoredEntry`.
- [x] 2.5 Update all storage strategy tests to pass factory delegates instead of pre-built `CacheFactoryResult` instances. Verify `DiskStorageStrategy` no longer requires an intermediate `MemoryStream`.

## 3. GenericCache<T> — Simplify MaterializeAndCacheAsync

- [x] 3.1 Replace the two-step flow (`factory()` + `strategy.StoreAsync(result)`) with single `strategy.MaterializeAsync(factory, ct)` call. Remove pre-eviction (`EvictIfNeededAsync(result.SizeBytes)`). Keep post-store eviction check, CacheEntry creation, RefCount protection, and all telemetry.
- [x] 3.2 Update `GenericCache` unit tests to verify: materialization still works, thundering herd (10 concurrent requests = 1 factory call), per-key independence, post-store eviction converges, RefCount protection during post-store eviction.

## 4. IFileContentCache — Domain Interface

- [x] 4.1 Create `IFileContentCache` interface in `ZipDrive.Domain/Abstractions/` with `ReadAsync(string archivePath, ZipEntryInfo entry, string cacheKey, byte[] buffer, long offset, CancellationToken ct)` and maintenance methods (`EvictExpired`, `Clear`, `ProcessPendingCleanup`, `DeleteCacheDirectory`) and observable properties (`CurrentSizeBytes`, `CapacityBytes`, `HitRate`, `EntryCount`, `BorrowedEntryCount`).

## 5. FileContentCache — Infrastructure Implementation

- [x] 5.1 Create `FileContentCache` class in `Infrastructure.Caching` implementing `IFileContentCache`. Constructor takes `IZipReaderFactory`, `IOptions<CacheOptions>`, `IEvictionPolicy`, `TimeProvider`, `ILoggerFactory`. Creates two `GenericCache<Stream>` instances (memory + disk tier). Routing: `entry.UncompressedSize < cutoffBytes` → memory, else → disk.
- [x] 5.2 Implement `ReadAsync`: build factory delegate (creates `IZipReader`, opens entry stream, sets `OnDisposed` to dispose reader, handles factory exceptions), call `cache.BorrowAsync(key, ttl, factory, ct)`, seek + read from handle, dispose handle, return bytes read.
- [x] 5.3 Implement maintenance methods delegating to both tiers: `EvictExpired`, `Clear`, `ProcessPendingCleanup`, `DeleteCacheDirectory`.
- [x] 5.4 Implement aggregated properties: `CurrentSizeBytes`, `CapacityBytes`, `HitRate`, `EntryCount`, `BorrowedEntryCount` — summing/combining both tiers.
- [x] 5.5 Add unit tests for `FileContentCache`: small file routed to memory tier, large file routed to disk tier, cache hit returns cached content, offset reading, EOF handling. Verify factory creates/disposes `IZipReader` correctly.
- [x] 5.6 Add thundering herd test: 10+ concurrent `ReadAsync` calls for the same cache key result in exactly 1 extraction from ZIP.

## 6. ZipVirtualFileSystem — Simplify Dependencies

- [x] 6.1 Replace `DualTierFileCache`, `IZipReaderFactory`, and `CacheOptions` dependencies with `IFileContentCache`. Update `ReadFileAsync` to call `_fileContentCache.ReadAsync(archivePath, entry, cacheKey, buffer, offset, ct)`. Remove the factory lambda entirely.
- [x] 6.2 Update `ZipVirtualFileSystem` unit/integration tests for new constructor signature and `ReadAsync` delegation.

## 7. DI and Wiring

- [x] 7.1 Update `Program.cs` (CLI): register `FileContentCache` as `IFileContentCache` singleton. Remove `DualTierFileCache` registration. Update `CacheMaintenanceService` to reference `FileContentCache`.
- [x] 7.2 Remove `DualTierFileCache` class and `IFileCache` interface (both replaced by `FileContentCache`/`IFileContentCache`).

## 8. Integration and Endurance Tests

- [x] 8.1 Update `DualTierFileCache` integration tests to target `FileContentCache`. Verify same routing behavior (small → memory, large → disk).
- [x] 8.2 Run full test suite (`dotnet test`) — all tests must pass.
- [x] 8.3 Run endurance test (`dotnet test tests/ZipDrive.EnduranceTests/`) — verify zero errors, zero handle leaks, SHA-256 content verification passes.
