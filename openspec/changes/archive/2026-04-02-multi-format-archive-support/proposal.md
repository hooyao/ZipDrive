## Why

ZipDrive is hardwired to ZIP. `ZipEntryInfo` leaks from Domain through Caching and Application layers. `ArchiveStructureCache` directly calls `IZipReader`. `FileContentCache` takes `IZipReaderFactory`. Adding any second format (RAR, 7Z, TAR) requires modifying the caching layer — the project's most critical and most tested subsystem. Users need mixed-format mount directories (`.zip` + `.rar` coexisting), and RAR is the most requested format.

## What Changes

- **New domain model**: `ArchiveEntryInfo` (format-agnostic) replaces `ZipEntryInfo` in all layers outside the ZIP provider
- **New provider interfaces**: `IArchiveStructureBuilder`, `IArchiveEntryExtractor`, `IPrefetchStrategy`, `IFormatRegistry` in Domain
- **BREAKING**: `ArchiveStructure.Entries` changes from `TrieDictionary<ZipEntryInfo>` to `TrieDictionary<ArchiveEntryInfo>`
- **BREAKING**: `IFileContentCache.ReadAsync` and `WarmAsync` signatures change (add `formatId`, `internalPath`, use `ArchiveEntryInfo`)
- **BREAKING**: `IArchiveStructureCache.GetOrBuildAsync` adds `formatId` parameter
- **ZIP provider extraction**: `ZipStructureBuilder`, `ZipEntryExtractor`, `ZipPrefetchStrategy`, `ZipFormatMetadataStore` created in `Infrastructure.Archives.Zip`
- **Dependency break**: `Infrastructure.Caching` drops its `ProjectReference` to `Infrastructure.Archives.Zip`
- **RAR provider**: New `Infrastructure.Archives.Rar` project with `RarStructureBuilder`, `RarEntryExtractor` (via SharpCompress). Binary signature detection for format/solid-archive probing.
- **Solid RAR UX**: Unsupported solid archives show as `name.rar (NOT SUPPORTED)` with a warning file, or hidden via `Mount:HideUnsupportedArchives` config
- **Multi-format discovery**: `ArchiveDiscovery` scans all supported extensions. `DokanHostedService` FileSystemWatcher broadened.
- **Moved to Archives.Zip**: `SpanSelector`, `PrefetchPlan` (ZIP-specific optimization helpers)
- **Deleted**: Unused `IArchiveProvider`, `IArchiveSession`, `IArchiveRegistry`, `IFileSystemTree` interfaces
- `ArchiveDescriptor` gains `FormatId` field
- `MountSettings` gains `HideUnsupportedArchives` field

## Capabilities

### New Capabilities
- `format-provider-abstraction`: Format-agnostic provider interfaces (`IArchiveStructureBuilder`, `IArchiveEntryExtractor`, `IPrefetchStrategy`, `IFormatRegistry`, `ArchiveEntryInfo`), format registry, and the decoupling of caching from ZIP-specific code
- `rar-archive-provider`: RAR archive support via SharpCompress — structure building, entry extraction, binary signature detection, solid archive UX (warning folder + hide config)

### Modified Capabilities
- `file-content-cache`: `ReadAsync`/`WarmAsync` signatures change to accept `ArchiveEntryInfo` + `formatId` + `internalPath` instead of `ZipEntryInfo`. Extraction delegates to `IArchiveEntryExtractor` via `IFormatRegistry` instead of `IZipReaderFactory`.
- `archive-discovery`: Discovers all supported archive formats (not just `*.zip`). `DescribeFile` sets `FormatId`. Uses `IFormatRegistry.SupportedExtensions`.
- `dokan-hosted-service`: FileSystemWatcher broadened from `*.zip` to all supported extensions. `IsZipExtension` replaced with `IsSupportedArchive`.
- `prefetch-siblings`: Prefetch logic extracted from `ZipVirtualFileSystem` into `ZipPrefetchStrategy` (implements `IPrefetchStrategy`). VFS delegates to format-specific strategy via registry. `SpanSelector` and `PrefetchPlan` move to `Archives.Zip`.
- `virtual-file-system`: Uses `ArchiveEntryInfo` instead of `ZipEntryInfo`. Passes `formatId` through all cache calls. `ProbeAsync` check before trie registration for unsupported archive detection.

## Impact

- **Domain**: ~8 new files (interfaces + models). `ArchiveStructure`, `ArchiveDescriptor` modified. `ZipEntryInfo` eventually moves to Archives.Zip as internal.
- **Infrastructure.Caching**: `ArchiveStructureCache` and `FileContentCache` refactored to use `IFormatRegistry`. Project reference to Archives.Zip removed.
- **Infrastructure.Archives.Zip**: ~6 new files (provider classes). `SpanSelector` and `PrefetchPlan` move here.
- **Infrastructure.Archives.Rar**: New project. SharpCompress NuGet dependency.
- **Infrastructure.FileSystem**: `DokanHostedService` multi-format watcher.
- **Application**: `ZipVirtualFileSystem` prefetch delegation, `ArchiveDiscovery` multi-format, `FormatRegistry` implementation.
- **Tests**: ~25 test files affected (mechanical type migration). New test project for RAR.
- **Configuration**: `appsettings.jsonc` gains `Mount:HideUnsupportedArchives`.
- **NuGet**: `SharpCompress` added to `Directory.Packages.props`.
- **Design doc**: `src/Docs/MULTI_FORMAT_ARCHIVE_DESIGN.md` (complete, reviewed).
