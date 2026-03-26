## Context

ZipDrive currently discovers ZIP archives once at startup via `ArchiveDiscovery.DiscoverAsync()`, populates an `ArchiveTrie`, and serves the mounted drive from that static snapshot. All VFS-related services (`ArchiveTrie`, `ArchiveStructureCache`, `FileContentCache`, `ZipVirtualFileSystem`) are registered as singletons and live for the entire process lifetime.

When users add or remove ZIP files from `Mount:ArchiveDirectory` while ZipDrive is running, the mounted drive does not reflect changes. The only option is a full restart.

The `DokanFileSystemAdapter` is the Dokan callback target — it holds a reference to `IVirtualFileSystem` and delegates all I/O to it. Dokan binds to the adapter instance at mount time; the adapter cannot be replaced, but its internal VFS reference can.

## Goals / Non-Goals

**Goals:**
- Mounted drive reflects ZIP file additions/deletions in `ArchiveDirectory` without restart
- Zero corruption risk: in-flight reads complete safely during reload
- Clean resource lifecycle: old caches (memory + disk) are fully disposed after swap
- No thread-safety changes to existing components (ArchiveTrie, GenericCache remain single-writer)

**Non-Goals:**
- Proactive Explorer notification (user presses F5; no `FindFirstChangeNotification` / `DokanNotifyXxx`)
- Detecting changes to ZIP file *contents* (only file-level add/delete/rename)
- Hot-reload of configuration changes (only archive directory contents)
- Recursive subdirectory watching (FileSystemWatcher covers top-level + existing MaxDiscoveryDepth)

## Decisions

### 1. Full VFS instance replacement (not incremental update)

Each reload creates an entirely new VFS instance graph: ArchiveTrie, StructureCache, FileContentCache, ZipVirtualFileSystem. The old instance is disposed after drain.

**Alternatives considered:**
- *Incremental diff (add/remove archives in existing trie + invalidate cache entries)*: Requires thread-safe mutations on ArchiveTrie (KTrie thread safety unknown), GenericCache `TryRemove` API (doesn't exist), and prefix-based cache eviction. High complexity, high risk.
- *ReaderWriterLockSlim around VFS access*: Dokan callbacks are synchronous (`.GetAwaiter().GetResult()`). A writer waiting for drain would block all readers. Deadlock-prone.

**Rationale:** Full replacement is lock-free on the read path, requires no changes to existing components, and provides clean cache lifecycle. The cost (rebuilding trie + re-parsing structures on demand) is negligible — trie build is <1ms for hundreds of archives, and structure cache populates lazily.

### 2. VfsScope lifecycle container

A new `VfsScope` class owns the full VFS instance graph plus a per-scope maintenance timer. Implements `IAsyncDisposable`. Dispose sequence: stop timer → unmount VFS → clear caches → delete disk cache directory.

**Rationale:** Encapsulating lifecycle in one object ensures no resource leaks. The maintenance timer (replacing `CacheMaintenanceService` HostedService) dies with the scope, so there's no stale reference problem.

### 3. DI scope per reload

VFS-related services registered as Scoped. Each reload calls `IServiceProvider.CreateScope()` and resolves `IVirtualFileSystem` from the scope. Shared infrastructure (TimeProvider, IZipReaderFactory, IEvictionPolicy, IFilenameEncodingDetector, configuration options) stays Singleton.

**Alternatives considered:**
- *Manual `new` (factory pattern)*: Works but bypasses DI, making testing harder and coupling construction to one place.
- *Autofac child containers*: Adds a dependency for something achievable with standard DI.

**Rationale:** Standard `IServiceProvider.CreateScope()` + `GetRequiredService` is idiomatic .NET, requires no new dependencies, and allows normal DI testing.

### 4. Adapter drain with reference counting

`DokanFileSystemAdapter` gains `volatile bool _draining`, `int _activeCount` (Interlocked), and `TaskCompletionSource _drainTcs`. Each callback increments on entry, decrements on exit (finally block). When decrement reaches 0 during drain, TCS signals completion.

`SwapAsync` sets draining=true, waits for activeCount==0 or 30-second timeout, swaps VFS reference, clears drain flag.

During drain, new requests receive `NtStatus.DeviceBusy` (Explorer auto-retries on short failures).

Double-check pattern: after incrementing, re-check `_draining` to handle the race where drain starts between the flag check and the increment.

### 5. FileSystemWatcher with 2-second debounce

`DokanHostedService` creates a `FileSystemWatcher` on `ArchiveDirectory` for `*.zip` (Created, Deleted, Renamed events). A debounce timer resets on each event; reload triggers only after 2 seconds of quiet.

**Rationale:** Batch operations (copying 20 ZIPs) generate many events. Debouncing coalesces them into a single reload.

### 6. Isolated temp directories per scope

Each `ChunkedDiskStorageStrategy` instance uses a unique subdirectory (e.g., `%TEMP%/ZipDrive-{pid}/scope-{guid}/`). This prevents old and new scopes from conflicting on disk cache files.

## Risks / Trade-offs

- **Brief service interruption during drain** → Mitigated by sub-second typical drain time; DeviceBusy allows Explorer retry
- **30s timeout may discard slow in-flight reads** → Acceptable: worst case is a single InternalError in Explorer; user retries
- **Cold cache after reload** → Structure cache repopulates lazily on first access (~100ms per archive). Content cache starts empty — first reads are cache misses. Acceptable because reload is infrequent.
- **FileSystemWatcher reliability** → FSW can miss events under heavy load or on network drives. Acceptable for this use case; user can always trigger F5. Future: could add periodic poll as backup.
- **Memory spike during swap** → Two VFS instances briefly coexist (old draining + new serving). Mitigated by quick drain + immediate old scope disposal.
