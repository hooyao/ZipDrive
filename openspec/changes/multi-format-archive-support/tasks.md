## 1. Domain Foundation (additive, no breakage)

- [x] 1.1 Create `ArchiveEntryInfo` readonly record struct in `src/ZipDrive.Domain/Models/`
- [x] 1.2 Create `ExtractionResult` sealed class (plain DTO, not IAsyncDisposable) in `src/ZipDrive.Domain/Models/`
- [x] 1.3 Create `ArchiveProbeResult` sealed record in `src/ZipDrive.Domain/Models/`
- [x] 1.4 Create `IArchiveStructureBuilder` interface (with `ProbeAsync` default impl) in `src/ZipDrive.Domain/Abstractions/`
- [x] 1.5 Create `IArchiveEntryExtractor` interface in `src/ZipDrive.Domain/Abstractions/`
- [x] 1.6 Create `IPrefetchStrategy` interface in `src/ZipDrive.Domain/Abstractions/`
- [x] 1.7 Create `IArchiveMetadataCleanup` interface in `src/ZipDrive.Domain/Abstractions/`
- [x] 1.8 Create `IFormatRegistry` interface (with `OnArchiveRemoved`) in `src/ZipDrive.Domain/Abstractions/`
- [x] 1.9 Add `FormatId` property to `ArchiveDescriptor` (default `"zip"` for backward compat)
- [x] 1.10 Add `FormatId` property to `ArchiveStructure`
- [x] 1.11 Add `HideUnsupportedArchives` to `MountSettings` (default `false`)
- [x] 1.12 Build solution — verify zero compilation errors

## 2. ZIP Provider Extraction (additive, new code alongside existing)

- [x] 2.1 Create `ZipFormatMetadataStore` (internal, ConcurrentDictionary-based) in `Infrastructure.Archives.Zip`
- [x] 2.2 Create `ZipStructureBuilder` implementing `IArchiveStructureBuilder` — relocate ~300 lines from `ArchiveStructureCache.BuildStructureAsync`, populate `ZipFormatMetadataStore` as side effect
- [x] 2.3 Create `ZipEntryExtractor` implementing `IArchiveEntryExtractor` — use `ZipFormatMetadataStore.Get()` + `IZipReaderFactory`
- [x] 2.4 Move `SpanSelector` from `Application/Services/` to `Infrastructure.Archives.Zip/`
- [x] 2.5 Move `PrefetchPlan` from `Application/Services/` to `Infrastructure.Archives.Zip/`
- [x] 2.6 Create `ZipPrefetchStrategy` implementing `IPrefetchStrategy` — relocate prefetch logic from `ZipVirtualFileSystem` lines 479-681
- [x] 2.7 Implement `IArchiveMetadataCleanup` on `ZipStructureBuilder` to call `ZipFormatMetadataStore.Remove()`
- [x] 2.8 Write unit tests for `ZipStructureBuilder` (structure build + metadata store population)
- [x] 2.9 Write unit tests for `ZipEntryExtractor` (extraction via metadata store, resource cleanup)
- [x] 2.10 Write unit tests for `ZipFormatMetadataStore` (populate, get, remove, thread safety)
- [x] 2.11 Build solution — verify zero compilation errors

## 3. FormatRegistry Implementation

- [x] 3.1 Create `FormatRegistry` implementing `IFormatRegistry` in `Application/Services/` — collect providers via `IEnumerable<T>`, build extension-to-format map, implement `OnArchiveRemoved` fan-out
- [x] 3.2 Write unit tests for `FormatRegistry` (resolve by FormatId, DetectFormat, SupportedExtensions, unknown format throws, OnArchiveRemoved fans out)
- [x] 3.3 Build solution — verify zero compilation errors

## 4. ArchiveStructure Migration (BREAKING — ~25 test files)

- [x] 4.1 Change `ArchiveStructure.Entries` from `TrieDictionary<ZipEntryInfo>` to `TrieDictionary<ArchiveEntryInfo>`
- [x] 4.2 Update `ArchiveStructure.GetEntry()` return type to `ArchiveEntryInfo?`
- [x] 4.3 Update `ArchiveStructure.ListDirectory()` return type to `IEnumerable<(string, ArchiveEntryInfo)>`
- [x] 4.4 Remove `IsZip64` from `ArchiveStructure`
- [x] 4.5 Update `ZipStructureBuilder` to produce `ArchiveEntryInfo` entries (conversion from `ZipCentralDirectoryEntry`)
- [ ] 4.6 Extract `DirectorySynthesizer` static helper to Domain (deferred — both builders have own inline impl, shared later if 7Z added)
- [x] 4.7 Update all Domain.Tests files referencing `ZipEntryInfo` or `ArchiveStructure`
- [x] 4.8 Update all Caching.Tests files referencing `ArchiveStructure`
- [x] 4.9 Update all EnduranceTests files referencing `ArchiveStructure`
- [x] 4.10 Update TestHelpers (`VfsTestFixture`)
- [x] 4.11 Update Benchmarks referencing `ArchiveStructure`
- [x] 4.12 Run full test suite — verify all ~389 tests pass

## 5. Caching Layer Migration

- [x] 5.1 Refactor `ArchiveStructureCache` — replace `IZipReaderFactory`/`IFilenameEncodingDetector` with `IFormatRegistry`, delegate `BuildStructureAsync` to `IArchiveStructureBuilder`
- [x] 5.2 Add `formatId` parameter to `IArchiveStructureCache.GetOrBuildAsync`
- [x] 5.3 Refactor `FileContentCache` — replace `IZipReaderFactory` with `IArchiveEntryExtractor`, factory delegates to extractor
- [x] 5.4 Update `IFileContentCache.ReadAsync` signature: add `internalPath`, change `ZipEntryInfo` to `ArchiveEntryInfo`
- [x] 5.5 Update `IFileContentCache.WarmAsync` signature: change `ZipEntryInfo` to `ArchiveEntryInfo`
- [x] 5.6 Update `FileContentCacheTests` — mock `IArchiveEntryExtractor` instead of `IZipReaderFactory`
- [x] 5.7 Update `FileContentCacheRemoveArchiveTests`
- [x] 5.8 Update `ChunkedExtractionIntegrationTests`
- [x] 5.9 Run caching test suite — verify all caching tests pass

## 6. VFS and Application Layer Migration

- [x] 6.1 Update `ZipVirtualFileSystem` — use `ArchiveEntryInfo` in all methods (`GetFileInfoAsync`, `ReadFileAsync`, `ListDirectoryAsync`)
- [x] 6.2 Pass `archive.FormatId` to `StructureCache.GetOrBuildAsync` and `FileContentCache.ReadAsync`
- [x] 6.3 Replace inline prefetch code with `IPrefetchStrategy` delegation
- [x] 6.4 Add `ProbeAsync` call before trie registration in mount + `AddArchiveAsync` flow
- [x] 6.5 Implement unsupported archive UX: suffix renaming + `HideUnsupportedArchives` check
- [x] 6.6 Update `ArchiveDiscovery.DiscoverAsync` — iterate `IFormatRegistry.SupportedExtensions`
- [x] 6.7 Update `ArchiveDiscovery.DescribeFile` — set `FormatId` via `IFormatRegistry.DetectFormat`
- [x] 6.8 Call `IFormatRegistry.OnArchiveRemoved` in `RemoveArchiveAsync`
- [x] 6.9 Update `ZipVirtualFileSystemTests`
- [x] 6.10 Update `ArchiveDiscoveryTests`
- [ ] 6.11 Update `PrefetchIntegrationTests` (move SpanSelector tests to Archives.Zip.Tests)
- [x] 6.12 Run full test suite — verify all tests pass

## 7. Break Caching → Archives.Zip Dependency

- [x] 7.1 Remove `<ProjectReference>` to `Infrastructure.Archives.Zip` from `Infrastructure.Caching.csproj`
- [x] 7.2 Remove all `using ZipDrive.Infrastructure.Archives.Zip` from caching source files
- [x] 7.3 Build solution — verify clean compilation with no format-specific imports in caching layer

## 8. Dynamic Reload Multi-Format

- [x] 8.1 Update `DokanHostedService` FileSystemWatcher — broaden filter, add `IsSupportedArchive` check using `IFormatRegistry.SupportedExtensions`
- [x] 8.2 Replace `IsZipExtension` with `IsSupportedArchive` in all event handlers
- [x] 8.3 Update DokanHostedService tests
- [x] 8.4 Run dynamic reload tests — verify pass

## 9. RAR Provider

- [x] 9.1 Create `ZipDrive.Infrastructure.Archives.Rar` project (`dotnet new classlib`), add to solution
- [x] 9.2 Add `SharpCompress` to `Directory.Packages.props` and project reference
- [x] 9.3 Add project reference to `ZipDrive.Domain`
- [x] 9.4 Implement `RarSignature` static class — `DetectVersion` (magic bytes), `IsSolidAsync` (header flags, 64 bytes max I/O)
- [x] 9.5 Implement `RarStructureBuilder` — `BuildAsync` (SharpCompress), `ProbeAsync` (binary), solid warning structure with `NOT_SUPPORTED_WARNING.txt`
- [x] 9.6 Implement `RarEntryExtractor` — non-solid extraction + synthetic warning file handler
- [x] 9.7 Create `ZipDrive.Infrastructure.Archives.Rar.Tests` test project, add to solution
- [x] 9.8 Create RAR test fixtures: non-solid RAR5, non-solid RAR4, solid RAR5, directory-heavy RAR
- [x] 9.9 Write tests for `RarSignature` (RAR4/RAR5 detection, solid flag, non-RAR returns 0)
- [x] 9.10 Write tests for `RarStructureBuilder` (non-solid build, parent synthesis, solid warning structure, ProbeAsync)
- [x] 9.11 Write tests for `RarEntryExtractor` (non-solid extract, warning file, non-existent entry, cancellation)
- [x] 9.12 Write integration test for solid RAR three-layer UX (renamed folder, warning file content, HideUnsupportedArchives)

## 10. DI Wiring and Integration

- [x] 10.1 Update `Program.cs` — register ZIP provider classes (`ZipStructureBuilder`, `ZipEntryExtractor`, `ZipPrefetchStrategy`, `ZipFormatMetadataStore`)
- [x] 10.2 Register RAR provider classes (`RarStructureBuilder`, `RarEntryExtractor`)
- [x] 10.3 Register `FormatRegistry` as `IFormatRegistry`
- [x] 10.4 Remove old `IZipReaderFactory` registration from `FileContentCache`/`ArchiveStructureCache` DI
- [x] 10.5 Add `Cli` project reference to `Infrastructure.Archives.Rar`
- [x] 10.6 Update `appsettings.jsonc` with `HideUnsupportedArchives` field
- [x] 10.7 Run full test suite — verify all tests pass
- [ ] 10.8 Manual test: mount directory with mixed `.zip` + `.rar` (non-solid) + `.rar` (solid) — verify all three cases work in Explorer

## 11. Cleanup

- [ ] 11.1 Move `ZipEntryInfo` from Domain to `Infrastructure.Archives.Zip` as `internal` (deferred — high-risk, many refs)
- [x] 11.2 Delete unused interfaces: `IArchiveProvider`, `IArchiveSession`, `IArchiveRegistry`, `IFileSystemTree`, `FileNode`
- [ ] 11.3 Rename `ZipVirtualFileSystem` to `ArchiveVirtualFileSystem` (deferred — cosmetic, broad impact)
- [x] 11.4 Update `CLAUDE.md` with multi-format architecture description
- [ ] 11.5 Update endurance tests — add RAR fixtures, verify DynamicReloadSuite works with mixed formats
- [x] 11.6 Run full test suite — final verification (445 pass, 0 fail)
- [x] 11.7 Build in Release mode — verify clean
