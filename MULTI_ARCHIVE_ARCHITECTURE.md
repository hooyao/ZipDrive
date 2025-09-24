# Multi-Archive Virtual Drive Architecture

## 1. Goals & Scope
Provide a Windows (DokanNet) virtual drive that exposes a folder containing many archive files (zip, tar, rar, future formats) as a single mounted drive. Each archive appears as a top-level directory whose contents are the archive entries. Focus is on clean extensible architecture, pluggable archive formats, lazy enumeration, and robust lifecycle management. (Caching and micro-optimizations intentionally out of scope for this iteration.)

## 2. Layered Architecture (Logical View)
```
┌──────────────────────────────────────────────┐
│ CLI / Hosting (zipdrive.cli)                │  <-- parameter parsing, DI, logging bootstrap
├──────────────────────────────────────────────┤
│ VirtualDriveHost / Drive Orchestration      │  <-- mount lifecycle, health, metrics surface
├──────────────────────────────────────────────┤
│ DriveManager (Multi-Archive Controller)     │  <-- registry, watcher, session mgmt
├──────────────────────────────────────────────┤
│ FileSystemAdapter (Dokan IDokanOperations)  │  <-- translates Dokan calls <-> domain ops
├──────────────────────────────────────────────┤
│ Domain Core (Path, Tree, Index, Sessions)   │  <-- navigation, node model, capability routing
├──────────────────────────────────────────────┤
│ Archive Layer (Providers + Capabilities)    │  <-- IArchiveProvider implementations
├──────────────────────────────────────────────┤
│ Shared Infrastructure                       │  <-- configuration, logging, metrics, policies
└──────────────────────────────────────────────┘
```

## 3. Key Runtime Concepts
- Mount Root: Emulates source folder-of-archives. Top-level directory names map 1:1 to discovered archive files (e.g. `sample.zip` => `\sample` or `sample.zip` (configurable naming policy)).
- Archive Directory: Presents archive entries (directories + files) lazily.
- Discovery: Initial scan + file system watcher + periodic reconcile (fallback).
- Lazy Index: Built per archive only when accessed; incremental invalidation if underlying file changes (mtime/hash).
- Capability Negotiation: Providers advertise read/write, random access, password support, compression features.

## 4. Core Abstractions
```csharp
public interface IArchiveProvider {
    string FormatId { get; }              // e.g. "zip"
    bool CanOpen(string fileName, ReadOnlySpan<byte> headerSample);
    IArchiveSession Open(ArchiveOpenContext context); // lightweight handle
}

public interface IArchiveSession : IAsyncDisposable {
    ArchiveInfo Info { get; }
    IEnumerable<IArchiveEntry> Entries();             // May be lazy/yielding
    IArchiveEntry? GetEntry(string path);
    Stream OpenRead(IArchiveEntry entry, Range? range = null, CancellationToken ct = default);
    IArchiveCapabilities Capabilities { get; }
}

public interface IArchiveEntry {
    string Path { get; }                  // normalized internal path
    string Name { get; }
    bool   IsDirectory { get; }
    long   Size { get; }
    DateTimeOffset? ModifiedUtc { get; }
}

public interface IArchiveCapabilities {
    bool SupportsRandomAccess { get; }
    bool SupportsWrite { get; }
    bool SupportsPassword { get; }
}

public interface IArchiveRegistry {
    IReadOnlyCollection<ArchiveDescriptor> List();
    ArchiveDescriptor? Get(string archiveKey);
    event EventHandler<ArchivesChangedEventArgs> Changed; // add/remove/update
}

public interface IArchiveIndex { // per archive
    bool IsBuilt { get; }
    void EnsureBuilt(); // idempotent
    IFileSystemNode? GetNode(string relativePath);
    IEnumerable<IFileSystemNode> GetChildren(string relativePath);
}

public interface IFileSystemTree { // virtual root across archives
    IFileSystemNode? Resolve(VirtualPath path);
    IEnumerable<IFileSystemNode> List(VirtualPath path);
}

public interface IPathResolver {
    (string? ArchiveKey, string InnerPath, PathResolutionStatus Status) Split(string rawPath);
}

public interface IStreamOpener {
    Stream Open(VirtualFileHandle handle, Range? range, CancellationToken ct);
}
```

## 5. Object Model (Simplified)
- ArchiveDescriptor: { Key, PhysicalPath, FormatId, LastWriteTimeUtc, Size, Capabilities }
- VirtualNodeKinds: Root, ArchiveRoot, Directory, File, Symlink (future)
- FileSystemNode: { Kind, Name, FullVirtualPath, Size?, Timestamps, ProviderMetadata }
- VirtualFileHandle: Resolves to (ArchiveDescriptor, IArchiveEntry)

## 6. Path Handling
- Input Dokan path: `\\` (root) OR `\\ArchiveName` OR `\\ArchiveName\dir\file`.
- PathResolver Strategy:
  1. Normalize separators & trim
  2. First segment => archive key (case sensitivity policy configurable)
  3. Remainder => inner path inside archive
  4. If first segment missing => root listing (archives enumeration)

## 7. Lifecycle & Flow
1. Startup
   - Load config
   - Discover providers via DI + optional plugin assemblies
   - Initial scan of target folder (match include/exclude patterns)
   - Build ArchiveRegistry entries (no index build yet)
   - Mount Dokan with FileSystemAdapter
2. Operation (Example: Read File)
   - Dokan OpenFile(path)
   - PathResolver splits -> (archiveKey, innerPath)
   - Registry lookup -> descriptor
   - Acquire / reuse IArchiveSession (session pool keyed by archive) (ref counted)
   - ArchiveIndex.EnsureBuilt() (build entries map / tree if first time)
   - Locate entry -> open stream via IStreamOpener
   - Return handle; subsequent ReadFile uses provider stream (range aware)
3. Change Detection
   - FileSystemWatcher signals change to physical archive file
   - Registry stamps file hash/mtime diff -> marks descriptor Dirty
   - On next access to dirty archive: safely recreate session + index (double-checked swap)
4. Shutdown
   - Flush metrics, dispose sessions, unmount Dokan gracefully

## 8. Concurrency Model
- Per-archive async lock for index build (double-checked to avoid redundant builds)
- Session pool with max concurrent open archives (LRU closing least recently used sessions)
- Global limiter: MaxConcurrentStreams (config) via SemaphoreSlim
- Read operations are otherwise lock-light (index is immutable snapshot once built)

## 9. Error & Resilience Strategy
| Scenario | Handling |
|----------|----------|
| Corrupt archive during open | Mark descriptor as Faulted, raise event, surface directory placeholder with error file |
| Read transient IO error | Retry (small policy) then fail with Dokan error mapping |
| Password required (future) | Expose pseudo file prompting credentials or return access denied depending on policy |
| Index build failure | Log, mark Faulted, allow future retry on access |

## 10. Observability
- Metrics (examples):
  - archives_discovered
  - archives_active
  - index_build_duration_ms (histogram)
  - archive_open_failures_total
  - open_streams_current
  - bytes_read_total
  - path_resolution_failures_total
- Structured log events: ArchiveAdded, ArchiveRemoved, ArchiveUpdated, ArchiveCorrupt, IndexBuilt, IndexRebuilt, StreamOpened, StreamClosed.
- Health Probes: /health (basic), /health/archive (per-archive status snapshot: Ready | Building | Faulted | Dirty)

## 11. Configuration Model (Draft)
```jsonc
{
  "VirtualDrive": {
    "RootFolder": "D:/archives",        // physical folder to scan
    "ArchiveNameMode": "StripExtensions", // or KeepExtensions
    "IncludePatterns": ["*.zip", "*.tar", "*.rar"],
    "ExcludePatterns": ["*.part"],
    "Scan": { "IntervalSeconds": 300, "UseFileWatcher": true },
    "Concurrency": { "MaxOpenArchives": 32, "MaxConcurrentStreams": 128 },
    "Capabilities": { "CaseSensitive": false },
    "FaultHandling": { "QuarantineCorrupt": true, "RetryOpenCount": 2 },
    "Features": { "ExposeFaultedArchives": true }
  }
}
```

## 12. File System Adapter Responsibilities
Maps Dokan operations to domain operations; isolates all Dokan-specific nuances.
- CreateFile/OpenFile -> path resolution + node/type check
- ReadFile -> stream open (lazy) + range read; no buffering logic here
- FindFiles -> root => archives list, archive root => first-level entries, directory => children
- GetFileInformation -> map node metadata
- Cleanup/CloseFile -> release stream handle
- Unhandled ops (write, set times) -> return appropriate unsupported codes (until implemented)

## 13. Extensibility (Providers / Plugins)
- New format: implement IArchiveProvider; register via DI (assembly scanning or explicit registration)
- CapabilityDescriptor builtin helps adapter choose optimal strategies (e.g., random-range vs sequential only)
- Future: IWritableArchiveProvider extends IArchiveSession with mutation APIs (Add/Delete/Commit)
- Providers can optionally supply a lightweight fast header parser for discovery without full open

## 14. Security & Policy Hooks
- Path Validation: reject traversal patterns (though inner paths controlled by provider)
- Size Limits: reject extremely large archives beyond configured thresholds
- Capability Filter: disable providers not allowed by policy
- Audit Trail: record first access to each archive, optionally hash for integrity

## 15. Testing Strategy (Architecture Level)
- Unit: PathResolver, ArchiveRegistry, ArchiveIndex build logic (mock provider)
- Contract Tests: Generic test suite run against each provider (Zip/Tar/etc) validating semantics
- Concurrency Tests: Simulated parallel reads across many archives
- Fault Injection: Corrupt archive mid-operation -> ensure graceful degradation
- Performance (later): Index build benchmarks, stream open latency, enumeration of large archives

## 16. Open Questions / Review Points
1. Archive naming collisions (duplicate base names with different extensions) – naming policy fallback?
2. Strategy for extremely large directory listings (pagination virtual files vs chunked enumeration)?
3. Hot-reload of provider assemblies (needed or static load only)?
4. Password-protected archives UX path (virtual side-channel vs external API)?
5. Multi-root mounts (mount multiple source folders as union)?

## 17. Roadmap (High-Level Phases)
1. Core Scaffolding: Interfaces, registry, path resolver, dummy provider
2. ZIP Provider (read-only), Index + Tree, Dokan Adapter minimal set
3. Multi-Archive Discovery + Watcher + Dirty Rebuild
4. Additional Providers (Tar, maybe wrap external lib) + Capability negotiation
5. Observability & Health surfaces
6. Error/Fault handling refinements & Policy hooks
7. (Later) Write support & advanced features

## 18. Non-Goals (Current Iteration)
- Advanced caching strategies
- Write/mutate archives
- Compression/transcoding on-the-fly
- Distributed/remote archive sources

## 19. Benefits
- Clear separation of archive format logic from filesystem translation
- Immutable indices reduce contention and simplify concurrency
- Pluggable providers accelerate multi-format expansion
- Observability & policy hooks integrated from start

## 20. Summary
This architecture emphasizes modularity, lazy resource usage, and pluggability. It cleanly isolates Dokan specifics, archive format concerns, and multi-archive orchestration, enabling incremental feature growth (write support, caching, password handling) without destabilizing the core.