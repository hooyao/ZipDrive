## Why

ZIP archives from Japanese, Chinese, Korean, and other non-Latin systems encode filenames in legacy code pages (Shift-JIS, GBK, EUC-KR, Windows-1251) without setting the ZIP UTF-8 flag (bit 11). ZipDrive currently defaults to CP437 (DOS) for non-UTF8 entries, producing garbled filenames. This is the most common user-facing data corruption issue for international archives.

## What Changes

- **Add `FileNameBytes` to `ZipCentralDirectoryEntry`**: Store raw filename bytes always; remove eager `FileName` string property. Filenames are decoded on demand via `DecodeFileName(Encoding?)` method instead of being eagerly decoded in ZipReader.
- **Add encoding detection service**: New `IFilenameEncodingDetector` with two-level detection — per-archive (statistical, high accuracy) and per-entry fallback (for mixed-encoding archives).
- **Add UtfUnknown NuGet dependency**: Third-party charset detection library for statistical encoding inference.
- **Modify `ArchiveStructureCache.BuildStructureAsync`**: Partition UTF-8 vs non-UTF8 entries during streaming. UTF-8 entries insert into trie immediately. Non-UTF8 entries are buffered, encoding is detected from collected bytes, then decoded once and inserted. No re-decode or trie mutation.
- **Add configuration**: `FallbackEncoding` (string, default `"utf-8"`) and `EncodingConfidenceThreshold` (float, default `0.5`) on `MountOptions`.
- **Add telemetry**: Counter for encoding detection results with tags for detection path taken (archive-detected, entry-detected, system-oem, fallback) and encoding name.
- **Support mixed-encoding archives**: When per-archive detection confidence is low, fall back to per-entry detection for each filename individually.

## Capabilities

### New Capabilities

- `filename-encoding-detection`: Automatic charset detection and decoding for ZIP filenames encoded in legacy code pages. Covers the detection chain (UtfUnknown statistical detection → system OEM code page → configurable fallback), per-archive and per-entry detection modes, mixed-encoding archive handling, and configuration.

### Modified Capabilities

- `archive-structure-trie`: BuildStructureAsync changes to support bytes-first decoding — partitions entries by UTF-8 flag, buffers non-UTF8 entries for batch detection, then decodes once with detected encoding before trie insertion.

## Impact

- **New NuGet dependency**: `UTF.Unknown` (UtfUnknown) added to `Archives.Zip` project
- **Breaking API change in `ZipCentralDirectoryEntry`**: `FileName` string property replaced by `FileNameBytes` byte array + `DecodeFileName(Encoding?)` method. All consumers of `.FileName` must migrate to `.DecodeFileName()`.
- **Modified files**: `ZipCentralDirectoryEntry.cs`, `ZipReader.cs`, `ArchiveStructureCache.cs`, `MountOptions.cs`, `Program.cs`, `appsettings.json`, `ZipTelemetry.cs`
- **New files**: `IFilenameEncodingDetector.cs`, `FilenameEncodingDetector.cs` (in Archives.Zip)
- **Test updates**: All existing call sites that reference `ZipCentralDirectoryEntry.FileName` or construct `ArchiveStructureCache` directly need updating. New unit tests for detector and integration tests with multi-script test data (Japanese, Chinese, Korean, Cyrillic, Thai).
- **Dependency constraint**: `Caching` cannot reference `FileSystem` (circular). The fallback `Encoding` is resolved in `Program.cs` and passed to `ArchiveStructureCache` as a plain `Encoding` parameter.
