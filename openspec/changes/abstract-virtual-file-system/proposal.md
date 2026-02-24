## Why

ZipDrive V3 has the core caching layer and streaming ZIP reader implemented, but no way to actually serve file system operations. The project needs a platform-independent **Abstract Virtual File System** layer that bridges the gap between the existing infrastructure (caching, ZIP parsing) and the platform adapter (DokanNet). This layer must be fully async, cross-platform as a .NET library, and decoupled from any specific file system driver. Without it, none of the existing infrastructure can be used to serve file operations.

## What Changes

- **New `IVirtualFileSystem` interface**: Fully async, platform-independent API for file system operations (read, list, get info, mount/unmount lifecycle)
- **New `IArchiveTrie`**: KTrie-based prefix tree that maps virtual paths to discovered ZIP archives, with platform-aware case sensitivity (case-insensitive on Windows, case-sensitive on Linux/macOS)
- **New `IArchiveDiscovery`**: Scans a root directory up to a configurable depth (max 6) to discover ZIP files at mount time
- **New `IPathResolver`**: Resolves virtual paths into a two-part structure (archive descriptor + internal path) using the archive trie
- **Revised `ArchiveStructure`**: Replaces `Dictionary<string, ZipEntryInfo>` + `DirectoryNode` with a single KTrie `TrieDictionary<ZipEntryInfo>` for both exact lookup and prefix-based directory listing
- **New `ZipVirtualFileSystem`**: Application-layer implementation that wires archive trie, structure cache, file content cache, and ZIP reader together
- **New VFS domain models**: `VfsFileInfo`, `VfsVolumeInfo`, `VfsMountOptions`, `ArchiveDescriptor`, `ArchiveEntryLocation`, `ArchiveTrieResult`
- **New VFS exceptions**: `VfsException` hierarchy for error propagation from VFS layer
- **New NuGet dependency**: [KTrie](https://www.nuget.org/packages/KTrie) (v3.0.1+) for trie data structures

## Capabilities

### New Capabilities

- `archive-trie`: KTrie-based prefix tree for mapping virtual paths to ZIP archives. Handles archive discovery registration, longest-prefix-match resolution, virtual folder derivation, and platform-aware case sensitivity. Mutable for future file watcher support.
- `path-resolution`: Resolves raw virtual paths (e.g., `\games\doom.zip\maps\e1m1.wad`) into a two-part structure: archive descriptor + internal path. Handles normalization, case sensitivity, edge cases (root, virtual folder, archive root, inside archive, not found).
- `archive-discovery`: Discovers ZIP files under a root directory up to a configurable depth (1-6). Produces `ArchiveDescriptor` records used to populate the archive trie. One-time scan at mount (no file watcher for MVP).
- `virtual-file-system`: The core `IVirtualFileSystem` interface and `ZipVirtualFileSystem` implementation. Fully async API for file read, directory listing, file/directory existence, file info, volume info, and mount/unmount lifecycle. Integrates archive trie, structure cache, file content cache, and ZIP reader.
- `archive-structure-trie`: Revised per-archive structure using KTrie `TrieDictionary<ZipEntryInfo>` instead of Dictionary + DirectoryNode. Supports O(key_length) exact lookup for file extraction and prefix enumeration for directory listing with direct-child filtering.

### Modified Capabilities

- `file-content-cache`: The VFS layer becomes the primary consumer of the file content cache via the borrow/return pattern. Cache keys change to `"{archiveVirtualPath}:{internalPath}"` format. No spec-level requirement changes (the cache API is unchanged), but integration patterns are formalized.

## Impact

- **New projects**: None (all code goes into existing `ZipDriveV3.Domain`, `ZipDriveV3.Application`, `ZipDriveV3.Infrastructure.Caching`)
- **Domain layer**: New interfaces (`IVirtualFileSystem`, `IArchiveTrie`, `IArchiveDiscovery`, `IPathResolver`), new models, new exceptions
- **Application layer**: New `ZipVirtualFileSystem` implementation
- **Infrastructure layer**: Revised `ArchiveStructureCache` to use KTrie-based `ArchiveStructure`
- **Dependencies**: KTrie NuGet package added to Domain and Infrastructure.Caching projects
- **Existing code**: `ArchiveStructure`, `DirectoryNode`, and `ArchiveStructureCache` will be revised to use KTrie
- **Tests**: New test projects/files for each capability (archive trie, path resolution, discovery, VFS operations)
- **Design docs**: `ZIP_STRUCTURE_CACHE_DESIGN.md` updated to reflect KTrie-based architecture
