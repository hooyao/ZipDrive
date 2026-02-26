## REMOVED Requirements

### Requirement: Dual-tier routing by file size

**Reason**: `DualTierFileCache` is replaced by `FileContentCache`, which merges tier routing with extraction ownership. The routing logic (size vs cutoff) moves into `FileContentCache.ReadAsync`.

**Migration**: Replace `DualTierFileCache` with `FileContentCache` in DI registration. Callers that depended on `DualTierFileCache` directly should use `IFileContentCache` instead.

---

### Requirement: Size hint for pre-materialization routing

**Reason**: The size hint was needed because the factory was provided by the caller and had to be passed to the correct tier. With `FileContentCache` owning both the factory and routing, the entry's `UncompressedSize` is used directly for routing — no separate size hint parameter needed.

**Migration**: Callers no longer pass a size hint. `FileContentCache.ReadAsync` receives the `ZipEntryInfo` which contains `UncompressedSize`.

---

### Requirement: Aggregated cache properties

**Reason**: Aggregated properties move to `FileContentCache` (same behavior, different class). The `DualTierFileCache` class is removed.

**Migration**: Use `FileContentCache` properties (`CurrentSizeBytes`, `EntryCount`, `BorrowedEntryCount`, `HitRate`) instead.

---

### Requirement: EvictExpired delegates to both tiers

**Reason**: Same behavior moves to `FileContentCache.EvictExpired()`.

**Migration**: Call `FileContentCache.EvictExpired()` instead of `DualTierFileCache.EvictExpired()`.

---

### Requirement: ICache<Stream> interface compliance

**Reason**: `FileContentCache` implements `IFileContentCache` (a purpose-built domain interface) instead of `ICache<Stream>`. The generic `ICache<Stream>` interface exposed caching implementation details (factory delegates, TTL) to callers.

**Migration**: Replace `ICache<Stream>` / `DualTierFileCache` dependencies with `IFileContentCache`.

---

### Requirement: Disk tier uses DiskStorageStrategy with MemoryMappedFile

**Reason**: The requirement content moves to the `file-content-cache` spec under the modified `Pluggable Storage Strategy` requirement. `DiskStorageStrategy` behavior is unchanged except that it now calls the factory directly.

**Migration**: No behavior change. The strategy still produces `MemoryMappedFile`-backed `StoredEntry` objects.
