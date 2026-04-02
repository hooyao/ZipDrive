## ADDED Requirements

### Requirement: ProbeAsync before trie registration
During mount and dynamic reload, the VFS SHALL call `IArchiveStructureBuilder.ProbeAsync(absolutePath)` for each discovered archive BEFORE registering it in the archive trie. Based on the `ArchiveProbeResult` and `MountSettings.HideUnsupportedArchives`, the VFS SHALL: (a) register normally if supported, (b) register with `(NOT SUPPORTED)` suffix if unsupported and hide=false, or (c) skip registration if unsupported and hide=true.

#### Scenario: Supported archive registered normally
- **WHEN** `ProbeAsync` returns `IsSupported == true` for `photos.rar`
- **THEN** the trie entry is registered as `photos.rar`

#### Scenario: Unsupported archive registered with suffix (hide=false)
- **WHEN** `ProbeAsync` returns `IsSupported == false` for `solid.rar`
- **AND** `HideUnsupportedArchives` is `false`
- **THEN** the trie entry is registered as `solid.rar (NOT SUPPORTED)`
- **AND** a `LogWarning` is emitted

#### Scenario: Unsupported archive hidden (hide=true)
- **WHEN** `ProbeAsync` returns `IsSupported == false` for `solid.rar`
- **AND** `HideUnsupportedArchives` is `true`
- **THEN** no trie entry is registered
- **AND** a `LogWarning` is emitted naming the archive and the config key

### Requirement: FormatId passed through all cache calls
The VFS SHALL pass `ArchiveDescriptor.FormatId` to `IArchiveStructureCache.GetOrBuildAsync` and `IFileContentCache.ReadAsync`. This enables the caching layer to delegate to the correct format provider on cache miss.

#### Scenario: ReadFileAsync passes formatId to FileContentCache
- **WHEN** a file inside a RAR archive is read
- **THEN** `FileContentCache.ReadAsync` receives `formatId == "rar"`

#### Scenario: Structure cache receives formatId
- **WHEN** a directory listing triggers lazy structure building for a RAR archive
- **THEN** `ArchiveStructureCache.GetOrBuildAsync` receives `formatId == "rar"`
- **AND** the RAR structure builder is used

## MODIFIED Requirements

### Requirement: Read file content
`ReadFileAsync` SHALL use `ArchiveEntryInfo` (not `ZipEntryInfo`) when accessing entry metadata from `ArchiveStructure`. It SHALL pass `archive.FormatId`, the `ArchiveEntryInfo`, and the `internalPath` to `IFileContentCache.ReadAsync`.

#### Scenario: Read entire small file
- **WHEN** `ReadFileAsync` is called for a 1KB file with offset=0
- **THEN** 1KB of decompressed data is returned

#### Scenario: Read file at offset
- **WHEN** `ReadFileAsync` is called with offset=5000
- **THEN** data starting at byte 5000 is returned

#### Scenario: Read past end of file
- **WHEN** `ReadFileAsync` is called with offset beyond the file's `UncompressedSize`
- **THEN** 0 bytes are returned

#### Scenario: Read at or beyond EOF
- **WHEN** `ReadFileAsync` is called with offset == `UncompressedSize`
- **THEN** 0 bytes are returned

#### Scenario: Cache hit for repeated reads
- **WHEN** the same file is read twice
- **THEN** the second read is a cache hit

#### Scenario: Read from a directory path
- **WHEN** `ReadFileAsync` is called with a directory path
- **THEN** an error is returned

#### Scenario: Read from non-existent file
- **WHEN** `ReadFileAsync` is called for a file not in the archive
- **THEN** `VfsFileNotFoundException` is thrown

### Requirement: List directory contents
`ListDirectoryAsync` SHALL return `ArchiveEntryInfo`-based entries (not `ZipEntryInfo`). All fields used for directory listing (`IsDirectory`, `UncompressedSize`, `LastModified`, `Attributes`) are present on `ArchiveEntryInfo`.

#### Scenario: List virtual root
- **WHEN** listing the root of the virtual drive with mixed ZIP and RAR archives
- **THEN** both ZIP and RAR archive folders appear as directory entries

#### Scenario: List archive root (lazy structure load)
- **WHEN** listing the root of a RAR archive folder
- **THEN** the RAR structure is built via `ArchiveStructureCache` using the RAR structure builder
- **AND** entries are returned with correct metadata

#### Scenario: List directory inside archive
- **WHEN** listing a subdirectory inside an archive
- **THEN** direct children are returned (both files and directories)

#### Scenario: List non-existent directory
- **WHEN** listing a directory that does not exist
- **THEN** `VfsDirectoryNotFoundException` is thrown

### Requirement: Mount lifecycle
`MountAsync` SHALL call `ProbeAsync` for each discovered archive before trie registration. The probe results, combined with `HideUnsupportedArchives` config, determine which archives are registered and under what name.

#### Scenario: Successful mount
- **WHEN** `MountAsync` is called with a directory containing mixed `.zip` and `.rar` files
- **THEN** all supported archives are registered in the trie
- **AND** unsupported archives are handled per `HideUnsupportedArchives` config

#### Scenario: Mount with no supported archive files
- **WHEN** `MountAsync` is called with a directory containing only `.txt` files
- **THEN** the trie is empty but mount succeeds

#### Scenario: Unmount clears state
- **WHEN** `UnmountAsync` is called
- **THEN** trie is cleared, caches are cleared, and `IFormatRegistry.OnArchiveRemoved` is called for each archive

### Requirement: VFS operations guard with per-archive ArchiveNode
VFS guard operations SHALL use `ArchiveEntryInfo` (not `ZipEntryInfo`) for entry lookups. Guard behavior (TryEnter/Exit, drain) is unchanged.

#### Scenario: ReadFileAsync during drain returns FileNotFound
- **WHEN** an archive is being drained (removed via dynamic reload)
- **AND** `ReadFileAsync` is called for a file in that archive
- **THEN** `VfsFileNotFoundException` is thrown

#### Scenario: ReadFileAsync for active archive succeeds
- **WHEN** an archive is active (not draining)
- **AND** `ReadFileAsync` is called for a valid file
- **THEN** file content is returned

#### Scenario: ListDirectoryAsync guarded for ArchiveRoot
- **WHEN** listing an archive's root directory
- **THEN** the archive's `ArchiveNode.TryEnter` is called

#### Scenario: VirtualRoot and VirtualFolder paths are NOT guarded
- **WHEN** listing the virtual root or a virtual folder
- **THEN** no archive guard is involved
