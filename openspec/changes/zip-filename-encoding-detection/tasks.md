## 1. NuGet Dependency

- [x] 1.1 Add `<PackageVersion Include="UTF.Unknown" Version="2.6.0" />` to `Directory.Packages.props`
- [x] 1.2 Add `<PackageReference Include="UTF.Unknown" />` to `src/ZipDrive.Infrastructure.Archives.Zip/ZipDrive.Infrastructure.Archives.Zip.csproj`

## 2. ZipCentralDirectoryEntry — Bytes-First Struct

- [x] 2.1 Replace `FileName` string property with `FileNameBytes` byte array property in `ZipCentralDirectoryEntry.cs`
- [x] 2.2 Add `DecodeFileName(Encoding? encoding = null)` method that defaults to UTF-8 (if IsUtf8) or CP437
- [x] 2.3 Update `IsDirectory` to check `FileNameBytes[^1] == (byte)'/'` instead of `FileName.EndsWith('/')`
- [x] 2.4 Build to verify struct compiles

## 3. ZipReader — Stop Eager Decoding

- [x] 3.1 Update `StreamCentralDirectoryAsync` (lines 364-370): set `FileNameBytes = fileNameBytes` in the `with` expression, remove `encoding.GetString()` call
- [x] 3.2 Update `ParseCentralDirectoryHeader`: change `FileName = ""` placeholder to `FileNameBytes = Array.Empty<byte>()`
- [x] 3.3 Remove `_fallbackEncoding` field and constructor parameter from `ZipReader` (no longer needed)
- [x] 3.4 Build to verify ZipReader compiles

## 4. Encoding Detection Service

- [x] 4.1 Create `IFilenameEncodingDetector.cs` interface in Archives.Zip with `DetectEncoding(IReadOnlyList<byte[]>)` and `DetectSingleEntry(byte[])` methods, both returning `DetectionResult?`
- [x] 4.2 Create `DetectionResult` record: `record DetectionResult(Encoding Encoding, float Confidence)`
- [x] 4.3 Create `FilenameEncodingDetector.cs` implementation with three-tier chain: UtfUnknown → system OEM → null
- [x] 4.4 Build to verify detection service compiles

## 5. Configuration

- [x] 5.1 Add `FallbackEncoding` (string, default `"utf-8"`) and `EncodingConfidenceThreshold` (float, default `0.5f`) to `MountOptions.cs`
- [x] 5.2 Add `"FallbackEncoding": "utf-8"` and `"EncodingConfidenceThreshold": 0.5` to `Mount` section in `appsettings.json`

## 6. Telemetry

- [x] 6.1 Add `Counter<long> EncodingDetections` to `ZipTelemetry.cs` with name `zip.encoding.detections`

## 7. ArchiveStructureCache — Partition/Detect/Decode Flow

- [x] 7.1 Add `IFilenameEncodingDetector` and `Encoding fallbackEncoding` constructor parameters to `ArchiveStructureCache`
- [x] 7.2 Refactor `BuildStructureAsync` Phase 1: partition entries by `IsUtf8` flag — UTF-8 entries decode and insert immediately, non-UTF8 entries buffer `(ZipCentralDirectoryEntry, ZipEntryInfo)` tuples
- [x] 7.3 Implement Phase 2: call `detector.DetectEncoding(bufferedBytes)`, determine fast path (high confidence) vs slow path (per-entry)
- [x] 7.4 Implement Phase 3 fast path: decode all buffered entries with detected encoding, normalize, insert into trie, call `EnsureParentDirectories`
- [x] 7.5 Implement Phase 3 slow path: per-entry detection with fallback chain (entry-detected → archive-hint → fallback encoding)
- [x] 7.6 Record telemetry counter for each detection outcome with `result` and `encoding` tags
- [x] 7.7 Build to verify full solution compiles

## 8. DI Wiring

- [x] 8.1 In `Program.cs`: resolve `MountOptions.FallbackEncoding` string to `Encoding` instance (with warning fallback to UTF-8 for invalid names)
- [x] 8.2 In `Program.cs`: register `IFilenameEncodingDetector` as singleton `FilenameEncodingDetector` with threshold from config
- [x] 8.3 In `Program.cs`: update `ArchiveStructureCache` registration to pass `IFilenameEncodingDetector` and resolved `Encoding`

## 9. Fix Existing Test Compilation

- [x] 9.1 Update `tests/ZipDrive.TestHelpers/VfsTestFixture.cs`: pass `IFilenameEncodingDetector` and `Encoding.UTF8` to `ArchiveStructureCache` constructor
- [x] 9.2 Update `tests/ZipDrive.Domain.Tests/ZipVirtualFileSystemTests.cs`: update `ArchiveStructureCache` construction
- [x] 9.3 Update `tests/ZipDrive.EnduranceTests/EnduranceTest.cs`: update `ArchiveStructureCache` construction
- [x] 9.4 Update all `ZipReaderTests.cs` assertions: migrate `.FileName` to `.DecodeFileName()`
- [x] 9.5 Update `FileReadCorrectnessTests.cs` if it references `ZipCentralDirectoryEntry.FileName`
- [x] 9.6 Build and run all existing tests — verify 196+ tests pass (zero regressions)

## 10. New Unit Tests — FilenameEncodingDetector

- [x] 10.1 Create `tests/ZipDrive.Infrastructure.Archives.Zip.Tests/FilenameEncodingDetectorTests.cs`
- [x] 10.2 Test: Shift-JIS bytes detected as CP932
- [x] 10.3 Test: GBK bytes detected as GBK
- [x] 10.4 Test: EUC-KR bytes detected as EUC-KR
- [x] 10.5 Test: Cyrillic (Windows-1251) bytes detected correctly
- [x] 10.6 Test: Empty list returns null
- [x] 10.7 Test: ASCII-only bytes return ASCII/UTF-8 compatible result
- [x] 10.8 Test: High threshold (0.99) rejects low-confidence detection
- [x] 10.9 Test: `DetectSingleEntry` with sufficient-length Japanese filename

## 11. New Integration Tests — Encoding with ArchiveStructureCache

- [x] 11.1 Create `tests/ZipDrive.Infrastructure.Archives.Zip.Tests/EncodingIntegrationTests.cs` with binary-level ZIP construction helper (builds ZIPs without UTF-8 flag)
- [x] 11.2 Test: Single-encoding Shift-JIS archive — all entries decode to correct Japanese paths
- [x] 11.3 Test: Single-encoding GBK archive — all entries decode to correct Chinese paths
- [x] 11.4 Test: Mixed Japanese + Chinese archive — per-entry detection produces correct paths for both
- [x] 11.5 Test: Mixed Korean + Cyrillic archive — per-entry detection handles both encodings
- [x] 11.6 Test: Mixed with ASCII majority — ASCII entries unaffected, non-ASCII entries detected
- [x] 11.7 Test: ASCII-only archive — no detection needed, entries decode correctly as CP437/ASCII
- [x] 11.8 Test: UTF-8 flagged entries bypass detection entirely
- [x] 11.9 Test: Parent directories synthesized correctly for decoded non-Latin paths
- [x] 11.10 Run full test suite — all tests pass
