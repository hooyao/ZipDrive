## Context

ZipDrive's caching layer (`Infrastructure.Caching`) has a hard `ProjectReference` to `Infrastructure.Archives.Zip`. ZIP-specific types (`ZipEntryInfo`, `IZipReaderFactory`, `IZipReader`) leak through Domain, Application, and Caching layers. Adding any new archive format requires modifying `FileContentCache` and `ArchiveStructureCache` — the two most concurrency-sensitive components with 149+ tests.

Full design details: [`src/Docs/MULTI_FORMAT_ARCHIVE_DESIGN.md`](../../src/Docs/MULTI_FORMAT_ARCHIVE_DESIGN.md) (reviewed, all agent-review findings resolved).

## Goals / Non-Goals

**Goals:**
- Zero performance regression for ZIP (cache hit path unchanged)
- Break `Infrastructure.Caching` → `Infrastructure.Archives.Zip` project reference
- No ZIP-specific types in Domain layer
- Mixed `.zip` + `.rar` archives in the same mount directory
- Incremental migration (each phase compiles and tests pass)

**Non-Goals:**
- Solid RAR support (format limitation — sequential decompression impractical for VFS)
- TAR / 7Z / LZMA support (validates extensibility, implement later)
- Optimized RAR prefetch (no contiguous-span optimization for RAR in V1)
- Password-protected archives (orthogonal feature)
- Write support

## Decisions

### 1. Provider Plugin Architecture (not session-based)

**Choice**: Three separate interfaces (`IArchiveStructureBuilder`, `IArchiveEntryExtractor`, `IPrefetchStrategy`) resolved by `IFormatRegistry`.

**Over**: Activating existing `IArchiveProvider` + `IArchiveSession` interfaces.

**Rationale**: `IArchiveSession` implies long-lived open archive state, but ZipDrive's extraction model is stateless — each extraction opens a fresh reader. Splitting structure building from extraction follows SRP and matches how ZIP and RAR actually work differently (ZIP has Central Directory for structure; RAR just enumerates headers). The existing `IArchiveProvider`/`IArchiveSession`/`IArchiveRegistry` are deleted (unused stubs from an earlier design iteration).

### 2. `ArchiveEntryInfo` without extraction coordinates

**Choice**: `ArchiveEntryInfo` contains only consumer-facing fields (`UncompressedSize`, `IsDirectory`, `LastModified`, `Attributes`, `IsEncrypted`, `Checksum`). No `LocalHeaderOffset`, `CompressedSize`, or `CompressionMethod`.

**Over**: Including an opaque `long ExtractionKey` field.

**Rationale**: One `long` can't encode all ZIP extraction metadata (`LocalHeaderOffset` + `CompressedSize` + `CompressionMethod`), so a side-channel store is needed anyway. The extractor interface takes `(archivePath, internalPath)` — the internal path string is the universal entry identifier across all formats. Format-specific metadata lives in `ZipFormatMetadataStore` (internal to Archives.Zip).

### 3. `ExtractionResult` as plain DTO (not `IAsyncDisposable`)

**Choice**: `ExtractionResult` is a data transfer object with `Stream`, `SizeBytes`, and `OnDisposed` callback. Not `IAsyncDisposable`.

**Over**: Making it `IAsyncDisposable` with its own `DisposeAsync()`.

**Rationale**: `CacheFactoryResult<Stream>.DisposeAsync()` already disposes `Value` (the Stream) then calls `OnDisposed`. If `ExtractionResult` also disposed the Stream, we'd get a double-dispose. The callback `OnDisposed` chains format resource cleanup (file handles, archive instances) AFTER the stream is consumed by the storage strategy.

### 4. Binary signature detection for RAR (no SharpCompress for probing)

**Choice**: `RarSignature` static class reads ~30 bytes to detect RAR version (magic bytes) and solid flag (header flags). Used by `ProbeAsync` and `DetectFormat`.

**Over**: `RarArchive.Open()` + `archive.IsSolid`.

**Rationale**: `RarArchive.Open()` parses the entire entry list (~tens of ms for large archives). Binary probe reads 64 bytes (< 0.1ms). This runs during discovery for every `.rar` file in the mount directory. RAR5 solid flag is at `MainArchiveHeader.Flags & 0x0001`; RAR4 is at `HEAD_FLAGS & 0x0008`.

### 5. Solid RAR: three-layer UX (not throw, not silent skip)

**Choice**: Solid archives appear as `name.rar (NOT SUPPORTED)` folder with `NOT_SUPPORTED_WARNING.txt` inside. Configurable hide via `Mount:HideUnsupportedArchives`.

**Over**: Throwing `NotSupportedException` (floods logs on every Dokan callback) or silently skipping (user confused why archive is missing).

**Rationale**: The renamed folder is visible at a glance in Explorer. The warning file explains why and how to fix (re-create without solid). The config gives users control. `ProbeAsync` detects solid BEFORE trie registration so the suffixed key is correct from the start.

### 6. `FormatRegistry` in Application layer

**Choice**: `FormatRegistry` implementation lives in `Application/Services/`.

**Over**: `Cli/` or new `Infrastructure.Common` project.

**Rationale**: Test projects can reference Application without pulling in the entire host. Application already depends on Domain (where the interfaces live). The registry receives providers via constructor injection (`IEnumerable<IArchiveStructureBuilder>`, etc.) — no format-specific project references.

### 7. `ZipFormatMetadataStore` cleanup via `IFormatRegistry.OnArchiveRemoved`

**Choice**: `IFormatRegistry` gets `OnArchiveRemoved(archiveKey)` method. VFS calls it during `RemoveArchiveAsync`. Registry fans out to providers via `IArchiveMetadataCleanup` interface.

**Over**: Event-based notification or cache eviction callback.

**Rationale**: Explicit call site (VFS already calls `StructureCache.Invalidate` and `FileContentCache.RemoveArchive` in sequence — adding one more call is natural). The `IArchiveMetadataCleanup` interface is optional — only providers with metadata stores implement it.

### 8. SharpCompress for RAR (not native libraries)

**Choice**: SharpCompress (MIT, pure managed C#, 317M+ NuGet downloads).

**Over**: SevenZipSharp (LGPL, native 7z.dll), unrar.dll (restrictive license, native).

**Rationale**: Only pure-managed option. Zero native dependencies means single-file publish works. MIT license. Targets net10.0. Non-solid RAR extraction via `RarArchive.Open()` + `OpenEntryStream()` — comparable performance to ZIP.

## Risks / Trade-offs

| Risk | Mitigation |
|------|------------|
| Large migration PR (~25 test files) | Changes are mechanical type renames. Each of 10 phases compiles independently. |
| `ZipFormatMetadataStore` unbounded growth | `IFormatRegistry.OnArchiveRemoved()` called on every archive removal. Concrete wiring, not hand-waved. |
| Solid RAR users cannot access contents | Three-layer UX: renamed folder + warning file + hide config. Clear remediation instructions. |
| SharpCompress bugs with certain RAR archives | 317M+ downloads, actively maintained. Test with RAR4 + RAR5 non-solid. |
| `FileSystemWatcher` `*.*` noise | `IsSupportedArchive()` filter in event handlers before consolidator. |
| Double-dispose of extraction streams | `ExtractionResult` is not `IAsyncDisposable`. `CacheFactoryResult` owns stream disposal. |
