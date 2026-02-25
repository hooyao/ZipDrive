## ADDED Requirements

### Requirement: Raw filename bytes on ZipCentralDirectoryEntry

`ZipCentralDirectoryEntry` SHALL store raw filename bytes in a `FileNameBytes` property of type `byte[]`. This property SHALL always be populated during Central Directory parsing. The `FileName` string property SHALL be removed and replaced by a `DecodeFileName(Encoding?)` method that decodes `FileNameBytes` on demand. When no encoding is provided, the method SHALL use UTF-8 if the UTF-8 flag (bit 11) is set, otherwise CP437.

#### Scenario: FileNameBytes populated for UTF-8 entry

- **WHEN** a ZIP entry has bit 11 set in GeneralPurposeBitFlag
- **AND** the filename bytes are `[0x66, 0x69, 0x6C, 0x65, 0x2E, 0x74, 0x78, 0x74]`
- **THEN** `FileNameBytes` contains the raw byte array
- **AND** `DecodeFileName()` returns `"file.txt"` (decoded as UTF-8)

#### Scenario: FileNameBytes populated for non-UTF8 entry

- **WHEN** a ZIP entry does NOT have bit 11 set
- **AND** the filename bytes are Shift-JIS encoded
- **THEN** `FileNameBytes` contains the raw Shift-JIS byte array
- **AND** `DecodeFileName()` returns the CP437-decoded string (default fallback)
- **AND** `DecodeFileName(Encoding.GetEncoding(932))` returns the correct Japanese string

#### Scenario: IsDirectory from raw bytes

- **WHEN** `FileNameBytes` ends with byte `0x2F` (forward slash)
- **THEN** `IsDirectory` returns `true`
- **AND** no string allocation is required for this check

#### Scenario: IsDirectory from DOS attribute

- **WHEN** `FileNameBytes` does NOT end with `0x2F`
- **AND** `ExternalFileAttributes` has the DOS directory attribute bit set
- **THEN** `IsDirectory` returns `true`

---

### Requirement: Per-archive encoding detection

`IFilenameEncodingDetector.DetectEncoding` SHALL accept a list of filename byte arrays and return a `DetectionResult` containing the detected `Encoding` and a `Confidence` value (0.0 to 1.0). The method SHALL concatenate all byte arrays (NUL-separated) and run statistical charset detection via UtfUnknown. If confidence is below the configured threshold, the method SHALL try the system OEM code page. If the OEM code page produces replacement characters (`\uFFFD`) in decoded output, it SHALL be rejected. The method SHALL return `null` if no encoding meets the threshold.

#### Scenario: Shift-JIS archive detected

- **WHEN** all non-UTF8 entries contain Shift-JIS encoded filenames
- **AND** the confidence threshold is 0.5
- **THEN** `DetectEncoding` returns `DetectionResult` with `Encoding` = CP932 and `Confidence` >= 0.5

#### Scenario: GBK archive detected

- **WHEN** all non-UTF8 entries contain GBK encoded filenames
- **THEN** `DetectEncoding` returns `DetectionResult` with `Encoding` = GBK

#### Scenario: Empty list returns null

- **WHEN** `DetectEncoding` is called with an empty list
- **THEN** the result is `null`

#### Scenario: ASCII-only filenames

- **WHEN** all filename bytes are in the ASCII range (0x00-0x7F)
- **THEN** `DetectEncoding` returns a result with `Encoding` = ASCII or UTF-8
- **AND** any encoding will decode correctly (ASCII is a subset of all supported encodings)

#### Scenario: High threshold rejects low-confidence detection

- **WHEN** the confidence threshold is set to 0.99
- **AND** the detected confidence is 0.85
- **THEN** `DetectEncoding` returns `null` (detection rejected, falls through to OEM or null)

---

### Requirement: Per-entry encoding detection

`IFilenameEncodingDetector.DetectSingleEntry` SHALL accept a single filename byte array and return a `DetectionResult` or `null`. This method is the fallback for mixed-encoding archives where per-archive detection confidence is below threshold.

#### Scenario: Single Japanese filename detected

- **WHEN** a single entry's `FileNameBytes` contains Shift-JIS encoded bytes for a Japanese filename
- **AND** the byte sequence has sufficient length for statistical detection
- **THEN** `DetectSingleEntry` returns `DetectionResult` with `Encoding` = CP932

#### Scenario: Short filename returns null

- **WHEN** a single entry's `FileNameBytes` is very short (e.g., 3 bytes)
- **AND** statistical detection cannot determine encoding with sufficient confidence
- **THEN** `DetectSingleEntry` returns `null`

---

### Requirement: Mixed-encoding archive support

When per-archive detection confidence is below the configured threshold, `ArchiveStructureCache.BuildStructureAsync` SHALL fall back to per-entry detection for each non-UTF8 entry individually. For each entry, the encoding priority SHALL be: per-entry detected encoding, then low-confidence archive-level encoding, then configured fallback encoding.

#### Scenario: Mixed Japanese and Chinese filenames

- **WHEN** a ZIP contains 3 entries with Shift-JIS filenames and 2 entries with GBK filenames
- **AND** none have the UTF-8 flag set
- **AND** per-archive detection confidence is below threshold (mixed signal)
- **THEN** per-entry detection is used for each entry
- **AND** the Japanese entries decode correctly as Shift-JIS
- **AND** the Chinese entries decode correctly as GBK

#### Scenario: Mixed with ASCII majority

- **WHEN** a ZIP contains 8 ASCII entries, 1 Shift-JIS entry, and 1 GBK entry
- **THEN** per-entry detection is used for the non-ASCII entries
- **AND** ASCII entries decode correctly regardless of detected encoding

#### Scenario: Mixed Korean and Cyrillic filenames

- **WHEN** a ZIP contains entries encoded in both EUC-KR and Windows-1251
- **THEN** per-entry detection identifies the correct encoding for each entry independently

---

### Requirement: Detection chain ordering

The encoding detection chain SHALL execute in this order: (1) UtfUnknown statistical detection, (2) system OEM code page validation, (3) return null for caller's configured fallback. Each step SHALL only execute if the previous step failed to produce a result.

#### Scenario: UtfUnknown succeeds

- **WHEN** UtfUnknown detects Shift-JIS with confidence 0.8
- **AND** the threshold is 0.5
- **THEN** the result is CP932
- **AND** the system OEM step is NOT executed

#### Scenario: UtfUnknown fails, OEM succeeds

- **WHEN** UtfUnknown returns confidence below threshold
- **AND** the system OEM code page is CP932
- **AND** decoding the bytes with CP932 produces no `\uFFFD` characters
- **THEN** the result is CP932

#### Scenario: UtfUnknown fails, OEM fails

- **WHEN** UtfUnknown returns confidence below threshold
- **AND** decoding with the system OEM code page produces `\uFFFD` replacement characters
- **THEN** the result is `null`
- **AND** the caller uses the configured fallback encoding

---

### Requirement: Fallback encoding configuration

`MountOptions` SHALL include a `FallbackEncoding` property (string, default `"utf-8"`) and an `EncodingConfidenceThreshold` property (float, default `0.5f`). The `FallbackEncoding` string SHALL be resolved to a `System.Text.Encoding` instance at application startup in `Program.cs`.

#### Scenario: Default fallback is UTF-8

- **WHEN** no `FallbackEncoding` is specified in configuration
- **THEN** the fallback encoding is UTF-8

#### Scenario: Custom fallback encoding

- **WHEN** `FallbackEncoding` is set to `"shift_jis"` in appsettings.json
- **THEN** the fallback encoding is Shift-JIS (CP932)

#### Scenario: Default confidence threshold

- **WHEN** no `EncodingConfidenceThreshold` is specified
- **THEN** the threshold is 0.5

#### Scenario: Invalid fallback encoding name

- **WHEN** `FallbackEncoding` is set to an unrecognized encoding name
- **THEN** the application SHALL log a warning and fall back to UTF-8

---

### Requirement: Encoding detection telemetry

`ZipTelemetry` SHALL include a `Counter<long>` named `zip.encoding.detections` that records each encoding detection outcome. The counter SHALL have two tags: `result` (the detection path taken) and `encoding` (the encoding name used).

#### Scenario: Archive-level detection recorded

- **WHEN** per-archive detection succeeds with high confidence
- **THEN** the counter increments with `result=archive_detected` and `encoding=<name>`

#### Scenario: Per-entry detection recorded

- **WHEN** per-entry detection is used for an entry
- **THEN** the counter increments with `result=entry_detected` and `encoding=<name>`

#### Scenario: Fallback recorded

- **WHEN** no detection succeeds and the configured fallback is used
- **THEN** the counter increments with `result=fallback` and `encoding=<fallback_name>`

#### Scenario: System OEM recorded

- **WHEN** the system OEM code page is used as the detection result
- **THEN** the counter increments with `result=system_oem` and `encoding=<oem_name>`
