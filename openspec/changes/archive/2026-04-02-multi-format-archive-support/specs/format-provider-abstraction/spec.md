## ADDED Requirements

### Requirement: ArchiveEntryInfo format-agnostic domain model
The system SHALL define `ArchiveEntryInfo` as a readonly record struct in the Domain layer containing only consumer-facing fields: `UncompressedSize` (long), `IsDirectory` (bool), `LastModified` (DateTime), `Attributes` (FileAttributes), `IsEncrypted` (bool), `Checksum` (uint). Format-specific extraction metadata (offsets, compression methods, block indices) SHALL NOT appear in this type.

#### Scenario: ArchiveEntryInfo contains no format-specific fields
- **WHEN** a developer inspects `ArchiveEntryInfo` fields
- **THEN** there are no fields named `LocalHeaderOffset`, `CompressedSize`, `CompressionMethod`, or any format-specific extraction coordinate

#### Scenario: ArchiveEntryInfo used by ArchiveStructure
- **WHEN** `ArchiveStructure.Entries` is accessed
- **THEN** it returns `TrieDictionary<ArchiveEntryInfo>` (not `TrieDictionary<ZipEntryInfo>`)

### Requirement: IArchiveStructureBuilder interface
The system SHALL define `IArchiveStructureBuilder` in the Domain layer with: `FormatId` (string), `SupportedExtensions` (IReadOnlyList<string>), `BuildAsync(archiveKey, absolutePath, ct)` returning `ArchiveStructure`, and `ProbeAsync(absolutePath, ct)` returning `ArchiveProbeResult`. The `ProbeAsync` method SHALL have a default implementation returning `IsSupported=true`.

#### Scenario: ZIP builder registered
- **WHEN** the DI container resolves `IEnumerable<IArchiveStructureBuilder>`
- **THEN** a builder with `FormatId == "zip"` and `SupportedExtensions == [".zip"]` is present

#### Scenario: RAR builder registered
- **WHEN** the DI container resolves `IEnumerable<IArchiveStructureBuilder>`
- **THEN** a builder with `FormatId == "rar"` and `SupportedExtensions == [".rar"]` is present

#### Scenario: ProbeAsync default returns supported
- **WHEN** `ProbeAsync` is called on a builder that does not override it
- **THEN** the result is `ArchiveProbeResult(true)`

### Requirement: IArchiveEntryExtractor interface
The system SHALL define `IArchiveEntryExtractor` in the Domain layer with: `FormatId` (string) and `ExtractAsync(archivePath, internalPath, ct)` returning `ExtractionResult`. Each format provider SHALL implement this interface.

#### Scenario: Extract returns decompressed stream
- **WHEN** `ExtractAsync` is called with a valid archive path and internal path
- **THEN** the returned `ExtractionResult.Stream` contains the fully decompressed entry data
- **AND** `ExtractionResult.SizeBytes` equals the entry's uncompressed size

#### Scenario: Extract for non-existent entry throws
- **WHEN** `ExtractAsync` is called with an internal path that does not exist in the archive
- **THEN** a `FileNotFoundException` is thrown

### Requirement: ExtractionResult is a plain DTO
`ExtractionResult` SHALL be a sealed class (not `IAsyncDisposable`) with properties: `Stream` (Stream), `SizeBytes` (long), `OnDisposed` (Func<ValueTask>?, nullable). The `OnDisposed` callback SHALL only clean up format-specific resources (file handles, archive instances) — it SHALL NOT dispose the Stream (that is owned by `CacheFactoryResult`).

#### Scenario: ExtractionResult does not double-dispose stream
- **WHEN** `CacheFactoryResult.DisposeAsync()` is called with an `ExtractionResult`-sourced stream
- **THEN** the stream is disposed exactly once (by `CacheFactoryResult`)
- **AND** `OnDisposed` is called after stream disposal to clean up format resources

### Requirement: IPrefetchStrategy interface
The system SHALL define `IPrefetchStrategy` in the Domain layer with: `FormatId` (string) and `PrefetchAsync(archivePath, structure, dirInternalPath, triggerEntry?, contentCache, options, ct)`. Formats without prefetch optimization SHALL not register an implementation. `IFormatRegistry.GetPrefetchStrategy` SHALL return null for such formats.

#### Scenario: ZIP has prefetch strategy
- **WHEN** `IFormatRegistry.GetPrefetchStrategy("zip")` is called
- **THEN** a non-null `IPrefetchStrategy` is returned

#### Scenario: RAR has no prefetch strategy
- **WHEN** `IFormatRegistry.GetPrefetchStrategy("rar")` is called
- **THEN** null is returned

### Requirement: IFormatRegistry resolves providers by FormatId
The system SHALL define `IFormatRegistry` in the Domain layer. It SHALL resolve `IArchiveStructureBuilder`, `IArchiveEntryExtractor`, and `IPrefetchStrategy` by `FormatId`. It SHALL provide `DetectFormat(filePath)` returning the format ID or null, `SupportedExtensions` listing all registered extensions, and `OnArchiveRemoved(archiveKey)` to notify providers of archive invalidation.

#### Scenario: Resolve extractor by format
- **WHEN** `GetExtractor("zip")` is called
- **THEN** the ZIP entry extractor is returned

#### Scenario: Unknown format throws
- **WHEN** `GetExtractor("unknown")` is called
- **THEN** `NotSupportedException` is thrown

#### Scenario: DetectFormat by extension
- **WHEN** `DetectFormat("archive.rar")` is called
- **THEN** `"rar"` is returned

#### Scenario: SupportedExtensions includes all formats
- **WHEN** `SupportedExtensions` is accessed with ZIP and RAR providers registered
- **THEN** the list contains both `".zip"` and `".rar"`

#### Scenario: OnArchiveRemoved fans out to providers
- **WHEN** `OnArchiveRemoved("games/doom.zip")` is called
- **THEN** providers implementing `IArchiveMetadataCleanup` receive the call

### Requirement: Caching layer has no format project references
`Infrastructure.Caching.csproj` SHALL NOT contain a `ProjectReference` to any `Infrastructure.Archives.*` project. All format-specific operations SHALL be accessed through Domain interfaces (`IFormatRegistry`, `IArchiveStructureBuilder`, `IArchiveEntryExtractor`).

#### Scenario: Caching project compiles without Archives.Zip reference
- **WHEN** `dotnet build` is run on `ZipDrive.Infrastructure.Caching`
- **THEN** compilation succeeds without any `using ZipDrive.Infrastructure.Archives.*` directives

### Requirement: FormatRegistry implementation in Application layer
The `FormatRegistry` class SHALL live in the Application layer and collect providers via `IEnumerable<T>` constructor injection. It SHALL build extension-to-format mappings from `IArchiveStructureBuilder.SupportedExtensions`.

#### Scenario: FormatRegistry usable from test projects
- **WHEN** a test project references the Application project
- **THEN** it can instantiate `FormatRegistry` with mock providers without referencing any Archives.* project

### Requirement: ZipFormatMetadataStore cleanup on archive removal
`ZipFormatMetadataStore` SHALL implement `IArchiveMetadataCleanup`. When `CleanupArchive(archiveKey)` is called, all stored ZIP metadata for that archive SHALL be removed. This prevents unbounded growth during dynamic reload.

#### Scenario: Metadata removed after archive invalidation
- **WHEN** an archive is removed via dynamic reload
- **AND** `IFormatRegistry.OnArchiveRemoved(archiveKey)` is called
- **THEN** `ZipFormatMetadataStore` no longer contains entries for that archive

#### Scenario: Metadata populated on structure build
- **WHEN** `ZipStructureBuilder.BuildAsync` completes
- **THEN** `ZipFormatMetadataStore` contains `ZipEntryInfo` for every entry in the archive
