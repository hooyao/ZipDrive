## 1. DI Registration Changes

- [x] 1.1 Change VFS-related services from Singleton to Scoped in Program.cs (IArchiveTrie, IPathResolver, IArchiveDiscovery, IArchiveStructureStore, IArchiveStructureCache, IFileContentCache, IVirtualFileSystem)
- [x] 1.2 Remove CacheMaintenanceService HostedService registration from Program.cs
- [x] 1.3 Ensure ChunkedDiskStorageStrategy uses a unique temp subdirectory per scope (e.g. append Guid to path)
- [x] 1.4 Verify singleton services (TimeProvider, IZipReaderFactory, IEvictionPolicy, IFilenameEncodingDetector) remain unchanged
- [x] 1.5 Build and verify DI resolves correctly with new lifetimes

## 2. VfsScope Lifecycle Container

- [x] 2.1 Create VfsScope class implementing IAsyncDisposable, holding IVirtualFileSystem, IFileContentCache, IArchiveStructureCache, IServiceScope, and PeriodicTimer
- [x] 2.2 Implement maintenance loop (EvictExpired + ProcessPendingCleanup) driven by PeriodicTimer with CancellationTokenSource
- [x] 2.3 Implement DisposeAsync: cancel timer → await loop → UnmountAsync → Clear caches → DeleteCacheDirectory → dispose scope
- [x] 2.4 Ensure DisposeAsync is idempotent (guard with bool _disposed)
- [x] 2.5 Write unit tests for VfsScope lifecycle (creation, maintenance ticks, dispose sequence, idempotent dispose)

## 3. Adapter Drain Mechanism

- [x] 3.1 Add volatile `_vfs` field, `_draining` flag, `_activeCount` (int, Interlocked), and `_drainTcs` (TaskCompletionSource) to DokanFileSystemAdapter
- [x] 3.2 Change constructor to not require IVirtualFileSystem; add initial VFS via a setter or SwapAsync
- [x] 3.3 Implement enter/exit guard pattern: increment on entry, check drain (double-check after increment), decrement in finally, signal TCS when count hits 0 during drain
- [x] 3.4 Apply guard pattern to all read-path callbacks: CreateFile, ReadFile, FindFiles, FindFilesWithPattern, GetFileInformation, GetFileSecurity, FindStreams, Cleanup, CloseFile
- [x] 3.5 Write-path callbacks (WriteFile, DeleteFile, etc.) and stateless callbacks (GetVolumeInformation, GetDiskFreeSpace, Mounted, Unmounted) skip the guard — return AccessDenied / fixed values directly
- [x] 3.6 Implement SwapAsync(IVirtualFileSystem newVfs, TimeSpan timeout): set drain → wait for count==0 or timeout → swap → clear drain → return old VFS
- [x] 3.7 Write unit tests for drain mechanism (concurrent increment/decrement, drain blocks new requests, swap completes after drain, timeout forces swap)

## 4. Directory Watcher Integration

- [x] 4.1 Add FileSystemWatcher creation in DokanHostedService.ExecuteAsync after successful mount (filter: *.zip, events: Created, Deleted, Renamed)
- [x] 4.2 Implement 2-second debounce using System.Threading.Timer — reset on each event, fire reload callback after quiet period
- [x] 4.3 Implement 15-second reload cooldown: track last reload timestamp, defer reload if cooldown not elapsed, schedule at cooldown expiry
- [x] 4.4 Implement reload callback: create new scope → resolve VFS → MountAsync → SwapAsync → dispose old scope in background → update last reload timestamp
- [x] 4.4 Add error handling: if new VFS mount fails, log error, dispose new scope, keep current VFS running
- [x] 4.5 Dispose FileSystemWatcher and debounce timer in StopAsync
- [x] 4.6 Update StopAsync to dispose current VfsScope (stops maintenance timer, cleans caches)

## 5. DokanHostedService Refactor

- [x] 5.1 Refactor DokanHostedService to hold current VfsScope instead of bare IVirtualFileSystem
- [x] 5.2 Inject IServiceProvider instead of IVirtualFileSystem; create initial VfsScope from a new DI scope in ExecuteAsync
- [x] 5.3 Wire adapter with initial VFS via SwapAsync (or direct assignment before Dokan starts)
- [x] 5.4 Ensure DokanFileSystemAdapter is still registered as Singleton and injected into DokanHostedService

## 6. Integration Tests

- [x] 6.1 Write test: add a ZIP file to watched directory → verify reload triggers and new archive appears in VFS
- [x] 6.2 Write test: remove a ZIP file → verify reload triggers and archive disappears from VFS
- [x] 6.3 Write test: rapid batch of changes → verify debounce coalesces into single reload
- [x] 6.4 Write test: reload cooldown defers second reload to 15s after first
- [x] 6.5 Write test: in-flight read completes during drain without error
- [x] 6.6 Write test: drain timeout forces swap and service continues
- [x] 6.7 Write test: failed reload (bad mount) keeps old VFS serving
- [x] 6.8 Build and run full test suite to verify no regressions
