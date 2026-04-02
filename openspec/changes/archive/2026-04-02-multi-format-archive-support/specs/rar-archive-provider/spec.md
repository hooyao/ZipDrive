## ADDED Requirements

### Requirement: RAR structure building via SharpCompress
`RarStructureBuilder` SHALL implement `IArchiveStructureBuilder` with `FormatId == "rar"` and `SupportedExtensions == [".rar"]`. It SHALL use `SharpCompress.Archives.Rar.RarArchive` to enumerate entries and produce an `ArchiveStructure` with `ArchiveEntryInfo` entries and synthesized parent directories. The structure SHALL have `FormatId = "rar"`.

#### Scenario: Non-solid RAR structure built correctly
- **WHEN** `BuildAsync` is called with a non-solid RAR archive containing `folder/file.txt` (100 bytes)
- **THEN** the returned `ArchiveStructure` contains entries for `folder/` (directory) and `folder/file.txt` (file with `UncompressedSize == 100`)

#### Scenario: Parent directories synthesized
- **WHEN** `BuildAsync` is called with a RAR containing `a/b/c.txt` but no explicit directory entries
- **THEN** the structure contains synthesized directory entries for `a/` and `a/b/`

#### Scenario: RAR4 and RAR5 both supported
- **WHEN** `BuildAsync` is called with a RAR4 archive
- **THEN** structure is built successfully
- **WHEN** `BuildAsync` is called with a RAR5 archive
- **THEN** structure is built successfully

### Requirement: RAR entry extraction via SharpCompress
`RarEntryExtractor` SHALL implement `IArchiveEntryExtractor` with `FormatId == "rar"`. It SHALL use `RarArchive.Open` + `OpenEntryStream` for non-solid archives. Each `ExtractAsync` call SHALL create a fresh `RarArchive` instance (thread safety — SharpCompress is not thread-safe).

#### Scenario: Extract file from non-solid RAR
- **WHEN** `ExtractAsync` is called with a valid archive and internal path
- **THEN** the returned `ExtractionResult.Stream` is a seekable `MemoryStream` with the decompressed content
- **AND** `ExtractionResult.SizeBytes` matches the entry's uncompressed size
- **AND** `ExtractionResult.OnDisposed` disposes the underlying `RarArchive` instance

#### Scenario: Extract non-existent entry throws
- **WHEN** `ExtractAsync` is called with an internal path not in the archive
- **THEN** `FileNotFoundException` is thrown
- **AND** the `RarArchive` instance is disposed

#### Scenario: Extraction is cancellable
- **WHEN** `ExtractAsync` is called and the `CancellationToken` is cancelled during `CopyToAsync`
- **THEN** `OperationCanceledException` is thrown
- **AND** the `RarArchive` instance is disposed

### Requirement: Binary signature detection for RAR format and solid flag
`RarSignature` SHALL detect RAR version (4 or 5) from magic bytes and solid flag from main archive header flags without instantiating SharpCompress. RAR5 solid flag: `MainArchiveHeader.Flags & 0x0001`. RAR4 solid flag: `HEAD_FLAGS & 0x0008`. Total I/O SHALL be at most 64 bytes.

#### Scenario: Detect RAR5 signature
- **WHEN** `DetectVersion` is called with bytes starting with `52 61 72 21 1A 07 01 00`
- **THEN** it returns `5`

#### Scenario: Detect RAR4 signature
- **WHEN** `DetectVersion` is called with bytes starting with `52 61 72 21 1A 07 00`
- **THEN** it returns `4`

#### Scenario: Non-RAR returns 0
- **WHEN** `DetectVersion` is called with a ZIP file's header bytes
- **THEN** it returns `0`

#### Scenario: Solid RAR5 detected
- **WHEN** `IsSolidAsync` is called on a solid RAR5 archive
- **THEN** it returns `true`
- **AND** total bytes read from disk is at most 64

#### Scenario: Non-solid RAR5 not flagged as solid
- **WHEN** `IsSolidAsync` is called on a non-solid RAR5 archive
- **THEN** it returns `false`

#### Scenario: Solid RAR4 detected
- **WHEN** `IsSolidAsync` is called on a solid RAR4 archive
- **THEN** it returns `true`

### Requirement: ProbeAsync detects unsupported solid RAR archives
`RarStructureBuilder.ProbeAsync` SHALL use `RarSignature.IsSolidAsync` (binary, no SharpCompress) to detect solid archives. It SHALL return `ArchiveProbeResult(false, "Solid RAR archives are not supported")` for solid archives and `ArchiveProbeResult(true)` for non-solid.

#### Scenario: Solid RAR returns unsupported probe result
- **WHEN** `ProbeAsync` is called on a solid RAR archive
- **THEN** result is `IsSupported == false` with a non-null `UnsupportedReason`

#### Scenario: Non-solid RAR returns supported probe result
- **WHEN** `ProbeAsync` is called on a non-solid RAR archive
- **THEN** result is `IsSupported == true`

### Requirement: Solid RAR three-layer UX
Unsupported solid RAR archives SHALL appear in the virtual drive as `name.rar (NOT SUPPORTED)` containing a single `NOT_SUPPORTED_WARNING.txt` file. The warning file SHALL explain the limitation, provide remediation commands (WinRAR + CLI), and reference the `Mount:HideUnsupportedArchives` config option.

#### Scenario: Solid RAR appears as renamed folder with warning file
- **WHEN** a solid `games.rar` is in the archive directory
- **THEN** the virtual drive shows a folder `games.rar (NOT SUPPORTED)`
- **AND** that folder contains exactly one file: `NOT_SUPPORTED_WARNING.txt`
- **AND** reading that file returns UTF-8 text explaining solid compression, remediation, and config hint

#### Scenario: Warning file extraction has zero archive I/O
- **WHEN** `RarEntryExtractor.ExtractAsync` is called with `internalPath == "NOT_SUPPORTED_WARNING.txt"`
- **THEN** a `MemoryStream` wrapping static `byte[]` content is returned
- **AND** no `RarArchive` is instantiated

### Requirement: HideUnsupportedArchives configuration
`MountSettings` SHALL include `HideUnsupportedArchives` (bool, default `false`). When `true`, unsupported archives SHALL be excluded from the virtual drive entirely. A `LogWarning` SHALL be emitted for each filtered archive with the message including the archive name and config key to re-enable visibility.

#### Scenario: HideUnsupportedArchives false (default) shows renamed folder
- **WHEN** `HideUnsupportedArchives` is `false` and a solid RAR is discovered
- **THEN** the archive appears as `name.rar (NOT SUPPORTED)` with warning file

#### Scenario: HideUnsupportedArchives true hides unsupported archives
- **WHEN** `HideUnsupportedArchives` is `true` and a solid RAR is discovered
- **THEN** the archive does not appear in the virtual drive
- **AND** a `LogWarning` is emitted naming the archive and the config key

#### Scenario: Non-solid RAR unaffected by HideUnsupportedArchives
- **WHEN** `HideUnsupportedArchives` is `true` and a non-solid RAR is discovered
- **THEN** the archive is mounted normally with full contents

### Requirement: SharpCompress NuGet dependency
The `Infrastructure.Archives.Rar` project SHALL depend on `SharpCompress` (MIT license, pure managed C#). The version SHALL be managed centrally in `Directory.Packages.props`.

#### Scenario: SharpCompress in central package management
- **WHEN** `Directory.Packages.props` is inspected
- **THEN** it contains `<PackageVersion Include="SharpCompress" Version="..." />`
