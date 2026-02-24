## Context

ZipDrive V3 has two complete infrastructure layers: a generic caching system (42 tests) and a streaming ZIP reader (15 tests). The Domain layer defines abstractions (`IArchiveStructureCache`, `IPathResolver`, `IArchiveProvider`, etc.) and models (`ArchiveStructure`, `ZipEntryInfo`, `DirectoryNode`). The Application layer has a minimal `PathResolver` implementation.

What's missing is the **Abstract Virtual File System** - the platform-independent layer that wires these components together into a usable file system API. This layer sits between the existing infrastructure and the future DokanNet adapter.

### Current State

| Component | Status | Changes Needed |
|-----------|--------|----------------|
| `IPathResolver` + `PathResolver` | Implemented | **Replace** with trie-based resolution |
| `ArchiveStructure` | Implemented | **Revise** internals: Dict+DirectoryNode → KTrie |
| `DirectoryNode` | Implemented | **Remove** (replaced by trie prefix enumeration) |
| `IArchiveStructureCache` + `ArchiveStructureCache` | Implemented | **Revise** to accept `ArchiveDescriptor` instead of raw key+path |
| `GenericCache<T>` | Implemented | No changes |
| `IZipReader` + `ZipReader` | Implemented | No changes |
| `IArchiveProvider`, `IArchiveRegistry`, `IArchiveSession` | Interfaces only | **Defer** (not needed for VFS MVP) |
| `IFileSystemTree` | Interface only | **Remove** (replaced by trie) |

### Constraints

- .NET 10.0 target (per `global.json`)
- Windows-primary but VFS layer must be cross-platform
- Fully async API (no synchronous I/O in VFS layer)
- Existing cache tests (42) and ZIP reader tests (15) must continue to pass

## Goals / Non-Goals

**Goals:**

- Define `IVirtualFileSystem` as the single async API for all file system operations
- Implement `IArchiveTrie` using KTrie for O(key_length) archive boundary detection via `GetLongestPrefixMatch`
- Implement `IArchiveDiscovery` for one-time ZIP discovery with configurable depth (1-6)
- Replace `Dictionary<string, ZipEntryInfo>` + `DirectoryNode` in `ArchiveStructure` with `TrieDictionary<ZipEntryInfo>` for unified lookup and directory listing
- Replace `PathResolver` with trie-based `IPathResolver` that produces `ArchiveTrieResult` (two-part: archive descriptor + internal path)
- Implement `ZipVirtualFileSystem` that orchestrates all components
- Platform-aware case sensitivity: archive paths follow OS conventions, ZIP internal paths are always case-sensitive

**Non-Goals:**

- DokanNet adapter implementation (separate change)
- Write support (read-only for MVP)
- File watcher for dynamic ZIP discovery (trie is mutable, but watcher is deferred)
- TAR/7Z support (interfaces are extensible, but only ZIP for now)
- CLI implementation or DI wiring (separate change)
- `IArchiveProvider` / `IArchiveSession` / `IArchiveRegistry` implementations (not needed; VFS uses `IArchiveTrie` + `IArchiveStructureCache` + `IZipReader` directly)

## Decisions

### Decision 1: KTrie for both archive lookup and internal structure

**Choice:** Use [KTrie](https://www.nuget.org/packages/KTrie) `TrieDictionary<T>` for both the archive prefix tree and per-archive entry storage.

**Rationale:**
- `GetLongestPrefixMatch` solves the archive boundary detection problem in O(key_length)
- `GetByPrefix` enables directory listing without maintaining a separate tree
- Single data structure replaces Dictionary + DirectoryNode (simpler, less memory)
- Supports `IEqualityComparer<char>` for platform-aware case sensitivity
- Well-maintained library (v3.0.1, updated July 2025)

**Alternatives Considered:**
- **Dictionary + DirectoryNode** (current design): Two data structures, more memory, DirectoryNode tree requires manual maintenance
- **Custom segment-based trie**: More control but reinventing the wheel, higher maintenance burden
- **rm.Trie**: String-only (no generic values), less API surface

**Trailing Slash Convention:**
- Archive trie keys end with `/` (e.g., `"games/doom.zip/"`) to prevent partial prefix matches (e.g., `"doom.zip"` matching `"doom.zip-backup"`)
- Structure trie: directories end with `/` (e.g., `"maps/"`), files do not (e.g., `"maps/e1m1.wad"`)

### Decision 2: Platform-aware case sensitivity via IEqualityComparer\<char\>

**Choice:** Archive trie uses `CaseInsensitiveCharComparer` on Windows, default (Ordinal) on other platforms. Structure trie always uses Ordinal (ZIP spec mandates case-sensitive paths).

**Rationale:**
- Archive paths correspond to filesystem paths, which are case-insensitive on Windows
- ZIP internal paths must be case-sensitive per specification
- KTrie supports `IEqualityComparer<char>` in its constructor

**Implementation:**
```
Archive Trie: new TrieDictionary<ArchiveDescriptor>(CaseInsensitiveCharComparer.Instance)  // Windows
              new TrieDictionary<ArchiveDescriptor>()  // Linux/macOS
Structure Trie: new TrieDictionary<ZipEntryInfo>()  // Always Ordinal
```

### Decision 3: Virtual folders derived from archive paths, not from physical directory structure

**Choice:** Virtual folders exist only as ancestors of discovered ZIP archives. Empty physical directories are not shown.

**Rationale:**
- The VFS maps ZIPs as folders. Non-ZIP files and empty directories have no representation.
- Derived virtual folders are computed from archive paths at registration time (e.g., `"games/doom.zip"` implies virtual folder `"games"`)
- Keeps the virtual tree clean and predictable

**Implementation:** `HashSet<string>` of virtual folder paths, populated when archives are added to the trie.

### Decision 4: Two-part path representation (ArchiveEntryLocation)

**Choice:** After path resolution, results are expressed as a two-part structure: `ArchiveDescriptor` (which ZIP) + `string InternalPath` (path inside ZIP), wrapped in `ArchiveTrieResult`.

**Rationale:**
- Makes the archive boundary explicit in the type system
- Eliminates ambiguity about where filesystem path ends and ZIP path begins
- Callers don't need to re-parse paths

### Decision 5: IVirtualFileSystem as the single entry point

**Choice:** One interface with async methods for all file system operations. `ZipVirtualFileSystem` implements it by composing `IArchiveTrie`, `IArchiveStructureCache`, `ICache<Stream>`, and `Func<string, IZipReader>`.

**Rationale:**
- Clean separation: VFS handles business logic, DokanNet adapter handles translation
- Testable: can unit test VFS without DokanNet
- Cross-platform: VFS has zero platform-specific dependencies

**Key Methods:**
- `MountAsync(VfsMountOptions)` - Discovers ZIPs, populates archive trie
- `UnmountAsync()` - Clears caches, releases resources
- `GetFileInfoAsync(path)` - File/directory metadata
- `ListDirectoryAsync(path)` - Directory contents
- `ReadFileAsync(path, buffer, offset)` - File content via cache borrow/return
- `FileExistsAsync(path)` / `DirectoryExistsAsync(path)` - Existence checks
- `GetVolumeInfo()` - Volume label, capacity, features

### Decision 6: Lazy structure loading (unchanged from current design)

**Choice:** `ArchiveStructure` is loaded on first access to an archive, not at mount time.

**Rationale:**
- Mount time should be fast (only filesystem discovery, not ZIP parsing)
- User may mount a folder with 100 ZIPs but only access a few
- `IArchiveStructureCache` already handles lazy loading with thundering herd prevention
- Structure cache TTL (30 min default) evicts unused structures

### Decision 7: Revise existing ArchiveStructure, not create parallel type

**Choice:** Modify the existing `ArchiveStructure` class to use `TrieDictionary<ZipEntryInfo>` instead of creating a new class.

**Rationale:**
- Avoids confusing parallel types
- `ArchiveStructureCache` already builds and caches `ArchiveStructure`
- Only the internals change (Dict+Tree → Trie), the cache integration pattern stays the same
- Existing tests test cache behavior, not structure internals - they'll pass with revised type

**Breaking Changes:**
- `ArchiveStructure.Entries` type changes from `IReadOnlyDictionary<string, ZipEntryInfo>` to `TrieDictionary<ZipEntryInfo>`
- `ArchiveStructure.RootDirectory` (DirectoryNode) removed, replaced by `ListDirectory(dirPath)` method
- `DirectoryNode` class removed entirely
- `ArchiveStructure.GetDirectory(path)` removed, replaced by `DirectoryExists(path)` and `ListDirectory(path)`

### Decision 8: ArchiveDescriptor redesigned for VFS needs

**Choice:** Replace the existing `ArchiveDescriptor` record (which holds an `IArchiveSession`) with a simpler data record holding discovery metadata.

**Rationale:**
- Existing `ArchiveDescriptor` couples to `IArchiveSession`, which isn't needed for VFS
- VFS needs: virtual path, physical path, file size, last modified time
- `IArchiveSession` is part of the deferred `IArchiveProvider` pattern

### Decision 9: Ensure parent directories in structure trie

**Choice:** When building the structure trie, synthesize directory entries for any path segments that lack explicit directory entries in the ZIP.

**Rationale:**
- Some ZIP creators omit explicit directory entries (only file entries exist)
- Without synthesized directories, `DirectoryExists("maps/")` would return false even though files like `"maps/e1m1.wad"` exist
- Walk up from each file path, creating directory entries as needed

### Decision 10: Discovery returns relative virtual paths preserving folder structure

**Choice:** `IArchiveDiscovery.DiscoverAsync()` returns `ArchiveDescriptor` records with `VirtualPath` set to the path relative to the root directory (e.g., `"games/doom.zip"` not just `"doom.zip"`).

**Rationale:**
- Preserves the physical folder structure in the virtual tree
- Users organizing ZIPs in subfolders see the same structure in the virtual drive
- Consistent with the archive trie key format

## Risks / Trade-offs

### [Risk] KTrie GetByPrefix returns all descendants, not just direct children
**Mitigation:** Filter results in `ArchiveStructure.ListDirectory()` by checking if `remaining.IndexOf('/')` indicates a direct child. For a directory with 1000 entries, this is O(n) over the prefix-matched entries. Acceptable for file system use (directory listings are not hot-path operations). If profiling shows this is a bottleneck, we can add a secondary index.

### [Risk] KTrie may not support all operations we need
**Mitigation:** KTrie's `TrieDictionary<T>` implements `IDictionary<string, T>` and provides `GetByPrefix`, `GetLongestPrefixMatch`, and `ContainsKey`. These cover all our use cases. If edge cases arise, we can wrap KTrie behind our own interface and swap implementations.

### [Risk] Existing ArchiveStructureCache tests may break when ArchiveStructure internals change
**Mitigation:** The cache tests (42 tests) test cache behavior (hits, misses, eviction, concurrency), not ArchiveStructure internals. The cache stores ArchiveStructure as an opaque object via ObjectStorageStrategy. Tests that directly construct ArchiveStructure will need updating, but cache behavior tests should be unaffected.

### [Risk] TrieDictionary may have higher memory overhead than Dictionary for small archives
**Trade-off accepted:** For archives with < 100 entries, Dictionary would be more memory-efficient. However, the difference is negligible (< 10 KB), and the consistent API (prefix enumeration, longest prefix match) across both archive trie and structure trie justifies the choice.

### [Risk] Mutable archive trie without thread safety
**Mitigation:** For MVP, the trie is populated once at mount time and then read-only. When file watcher support is added later, we'll need a `ReaderWriterLockSlim` or copy-on-write pattern. The `IArchiveTrie` interface doesn't expose thread-safety guarantees, so this can be added without breaking the API.

### [Risk] Max depth 6 may discover too many ZIPs in deeply nested structures
**Mitigation:** Discovery is bounded by depth, not count. If a user has thousands of ZIPs, the trie population is O(n) and structure loading is lazy. Memory impact is ~200 bytes per discovered archive (ArchiveDescriptor + trie node). 10,000 archives ≈ 2 MB - acceptable.

## Open Questions

1. **Should `IVirtualFileSystem` expose a `Refresh()` or `Rescan()` method for manual re-discovery?** Currently only `MountAsync` triggers discovery. A manual rescan could be useful before file watcher support is added.

2. **Should `ArchiveStructure.ListDirectory()` return a materialized list or `IEnumerable`?** `IEnumerable` with yield return is lazy but can't be enumerated twice. DokanNet's `FindFiles` callback needs a list. Leaning toward `IEnumerable` at VFS layer, let adapter materialize.

3. **Should the VFS layer handle corrupt/unreadable ZIPs gracefully during discovery?** If one ZIP in 100 is corrupt, should discovery skip it (log warning) or fail the entire mount? Leaning toward skip + log.
