## Context

ZIP archives created on Japanese, Chinese, Korean, and other non-Latin Windows systems encode filenames using legacy code pages (Shift-JIS/CP932, GBK/CP936, EUC-KR/CP949, Windows-1251, etc.) without setting the ZIP UTF-8 flag (bit 11). ZipDrive currently decodes all non-UTF8 filenames as CP437 (DOS), producing garbled output for international archives.

The current flow in `ZipReader.StreamCentralDirectoryAsync` (line 368-370) eagerly decodes filename bytes to a string using `_fallbackEncoding` (CP437), then discards the raw bytes. `ArchiveStructureCache.BuildStructureAsync` consumes the decoded `FileName` string as a trie key. Once the bytes are gone, re-decoding with a better encoding requires re-parsing the entire ZIP.

**Constraints:**
- `Caching` project cannot reference `FileSystem` (circular dependency). Config values from `MountOptions` must be resolved in `Program.cs` and passed as plain parameters.
- KTrie `TrieDictionary<ZipEntryInfo>` is string-keyed and char-traversed. Byte-level keys are not possible with this library. Trie keys must be decoded Unicode strings to match against Dokan's Unicode paths.
- `ZipCentralDirectoryEntry` is a `readonly record struct` — no mutable backing fields for lazy caching.

## Goals / Non-Goals

**Goals:**
- Automatically detect the correct encoding for non-UTF8 ZIP filenames using statistical charset detection
- Support single-encoding archives (99% case) with high accuracy via per-archive detection
- Support mixed-encoding archives via per-entry detection fallback
- Preserve raw filename bytes to enable decode-once (no re-decode or trie mutation)
- Provide configurable fallback encoding and confidence threshold
- Emit telemetry for detection outcomes
- Maintain backward compatibility for ASCII-only and UTF-8 archives (no regression)

**Non-Goals:**
- Password-protected ZIP support (separate feature)
- LZMA or other compression methods (orthogonal)
- User-interactive encoding selection (auto-detection only)
- Byte-level trie keys (KTrie is string-only; Dokan requires Unicode matching)

## Decisions

### Decision 1: Bytes-first struct — replace `FileName` with `FileNameBytes` + `DecodeFileName()`

**Choice:** Remove the `FileName` string property from `ZipCentralDirectoryEntry`. Store raw bytes in `FileNameBytes` (always populated). Provide `DecodeFileName(Encoding?)` method for on-demand decoding.

**Alternatives considered:**
- **Keep both `FileName` and `FileNameBytes`**: Larger struct, dual representation, unclear which to use. Consumers must know to ignore `FileName` for non-UTF8 entries.
- **Lazy `FileName` property**: `readonly record struct` cannot have mutable backing fields for lazy caching. A computed property would re-decode on every access.
- **Keep `FileName`, add `FileNameBytes` only for non-UTF8**: Asymmetric API. The plan's original approach of decode-then-redecode requires trie mutation (remove old key + re-insert), which is more complex and wasteful.

**Rationale:** Single source of truth (`FileNameBytes`), decode happens exactly once at the right time, no trie mutation needed. The `readonly record struct` constraint makes lazy caching impossible anyway, so a method is the natural API.

**Impact on `IsDirectory`:** Currently checks `FileName.EndsWith('/')`. Changed to `FileNameBytes.Length > 0 && FileNameBytes[^1] == (byte)'/'`. The `/` character (0x2F) is the same byte in CP437, Shift-JIS, GBK, EUC-KR, UTF-8, and all relevant legacy encodings, so this check is encoding-agnostic.

**Impact on `ZipReader`:** Lines 364-370 simplified. No longer calls `encoding.GetString()`. Sets `FileNameBytes = fileNameBytes` in the `with` expression. The `_fallbackEncoding` field on `ZipReader` becomes unused and can be removed.

### Decision 2: Two-level detection — per-archive fast path, per-entry fallback

**Choice:** `IFilenameEncodingDetector` exposes two methods:
- `DetectEncoding(IReadOnlyList<byte[]> allBytes)` — concatenates all filename bytes for statistical accuracy (per-archive)
- `DetectSingleEntry(byte[] filenameBytes)` — detects from a single filename (per-entry fallback)

Both return `DetectionResult?` (nullable record with `Encoding` and `Confidence`).

**Alternatives considered:**
- **Per-archive only**: Fails on mixed-encoding archives (e.g., Japanese + Chinese filenames from different locale machines). Low confidence results apply wrong encoding to all entries.
- **Per-entry only**: Short filenames (5-20 bytes) give poor detection accuracy. Much more expensive (N detection calls vs 1).
- **Per-entry always, archive as hint**: Unnecessarily expensive for the 99% single-encoding case.

**Rationale:** Per-archive detection handles the common case efficiently (one detection call, high accuracy). Per-entry fallback activates only when archive-level confidence is below threshold, handling the mixed-encoding edge case without penalizing normal archives.

### Decision 3: Detection chain — UtfUnknown → system OEM → fallback

**Choice:** Three-tier chain within `FilenameEncodingDetector`:
1. `CharsetDetector.DetectFromBytes()` (UtfUnknown library) — statistical detection. If confidence >= threshold, return the detected encoding.
2. System OEM code page (`Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage)`) — validate that decoding produces no `\uFFFD` replacement characters.
3. Return `null` — caller uses configured fallback encoding.

**Alternatives considered:**
- **UtfUnknown only**: Misses the case where the system OEM encoding matches the archive (common for users on a Japanese/Chinese Windows system).
- **System OEM first**: Misses cases where the archive was created on a different-locale system than the one running ZipDrive.

**Rationale:** UtfUnknown provides the broadest language coverage. System OEM is a good second guess for locally-created archives. The configurable fallback is the user's escape hatch.

### Decision 4: BuildStructureAsync flow — partition, detect, decode, insert

**Choice:** Restructure `BuildStructureAsync` into three phases:

```
Phase 1 — Stream + Partition
  foreach cdEntry from ZipReader:
    convert to ZipEntryInfo (no filename needed)
    if IsUtf8:
      decode with UTF-8, normalize, insert into trie immediately
    else:
      buffer (cdEntry, entryInfo) for batch detection

Phase 2 — Detect (only if buffer non-empty)
  archiveResult = detector.DetectEncoding(buffer.Select(e => e.cd.FileNameBytes))
  if archiveResult.Confidence >= threshold:
    effectiveEncoding = archiveResult.Encoding  (fast path)
  else:
    per-entry mode activated  (slow path)

Phase 3 — Decode + Insert buffered entries
  Fast path: decode all with effectiveEncoding
  Slow path: for each entry:
    entryResult = detector.DetectSingleEntry(entry.cd.FileNameBytes)
    encoding = entryResult?.Encoding
              ?? archiveResult?.Encoding  (low-conf archive hint)
              ?? fallbackEncoding
    decode, normalize, insert into trie
```

**Alternatives considered:**
- **Original plan (decode-then-redecode)**: Decode all with CP437 during streaming, then re-decode and re-key trie entries after detection. Requires trie mutation (remove + re-insert), two decodes per entry, more complex and error-prone.
- **Two-pass streaming**: Stream CD entries twice (once for bytes, once for insertion). Wasteful — requires re-seeking and re-parsing the ZIP Central Directory.

**Rationale:** One decode per entry, one trie insertion per entry, no trie mutation. UTF-8 entries are handled immediately (encoding is known). Non-UTF8 entries are buffered — acceptable because the trie itself would consume similar memory. The caller (`GetOrBuildAsync`) already expects the full structure after the method returns, so buffering doesn't change the memory profile.

### Decision 5: Configuration on MountOptions, resolved in Program.cs

**Choice:** Add two properties to `MountOptions`:
- `FallbackEncoding` (string, default `"utf-8"`)
- `EncodingConfidenceThreshold` (float, default `0.5f`)

`Program.cs` resolves `FallbackEncoding` string to `System.Text.Encoding` and passes it as a constructor parameter to `ArchiveStructureCache`. The threshold is passed to `FilenameEncodingDetector`'s constructor.

**Rationale:** Avoids circular project reference (`Caching` → `FileSystem`). Clean separation: config lives on `MountOptions`, resolution happens at composition root.

### Decision 6: Telemetry counter with detection path tags

**Choice:** Add to `ZipTelemetry`:
```csharp
Counter<long> EncodingDetections  // "zip.encoding.detections"
  tags: result = {archive_detected, entry_detected, archive_hint, system_oem, fallback}
        encoding = {the encoding name, e.g. "shift_jis", "gb2312", "utf-8"}
```

**Rationale:** Follows existing static telemetry pattern (`ZipTelemetry.Meter`). Tags distinguish which detection path was taken for observability. The encoding name tag helps identify which locales are common across a fleet.

### Decision 7: Test data — generic multilingual strings, not customer data

**Choice:** Use well-known generic terms encoded in each legacy code page:

| Script | Encoding | Test Filename |
|--------|----------|---------------|
| Japanese | Shift-JIS (CP932) | `テスト文書/データ.txt` |
| Chinese | GBK (CP936) | `测试文件/报告.txt` |
| Korean | EUC-KR (CP949) | `테스트/보고서.txt` |
| Cyrillic | Windows-1251 | `Документы/отчёт.txt` |
| Thai | Windows-874 | `ทดสอบ/ข้อมูล.txt` |

Test ZIP archives are constructed at the binary level (not via `System.IO.Compression.ZipArchive`, which always sets the UTF-8 flag).

**Rationale:** Generic dictionary words (test, document, data, report) avoid customer data exposure. Binary-level ZIP construction bypasses .NET's UTF-8 flag enforcement.

## Risks / Trade-offs

**[Short filenames may defeat per-entry detection]** → UtfUnknown needs statistical signal. Filenames under ~10 bytes may produce low-confidence results. Mitigation: fall through to archive-level hint or configured fallback. Acceptable because short filenames are often ASCII-compatible.

**[Mixed-encoding detection is inherently imperfect]** → Shift-JIS bytes decoded as GBK may produce valid-but-wrong Chinese characters (no `\uFFFD`). At the byte level, you cannot distinguish "correctly decoded in the wrong encoding" from "correctly decoded in the right encoding." Mitigation: document this as a known limitation. This matches 7-Zip's behavior — even 7-Zip sometimes guesses wrong on mixed-encoding archives.

**[Buffering non-UTF8 entries increases peak memory]** → For a 100K-entry archive where all entries are non-UTF8, buffering adds ~11MB (struct + byte arrays). Mitigation: this is comparable to the trie's own memory footprint and is transient (released after trie insertion).

**[Breaking API change on `ZipCentralDirectoryEntry`]** → Removing `FileName` property breaks all consumers. Mitigation: there are only ~10 call sites (1 in source, ~9 in tests). All are in this repo. The migration is mechanical: `.FileName` → `.DecodeFileName()`.

**[New NuGet dependency (UTF.Unknown)]** → Adds a transitive dependency to the Archives.Zip project. Mitigation: UTF.Unknown is a well-maintained, MIT-licensed library with no transitive dependencies of its own. It's the .NET port of Mozilla's Universal Charset Detector.
