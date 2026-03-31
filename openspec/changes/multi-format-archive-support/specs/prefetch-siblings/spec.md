## MODIFIED Requirements

### Requirement: Sibling Prefetch On File Read
On cache miss for a file read, the VFS SHALL delegate prefetch to `IPrefetchStrategy` resolved via `IFormatRegistry.GetPrefetchStrategy(archive.FormatId)`. If the registry returns null (format has no prefetch optimization), prefetch SHALL be skipped silently. The VFS SHALL NOT contain any format-specific prefetch logic.

#### Scenario: Prefetch fires after first file read (ZIP)
- **WHEN** a file in a ZIP archive is read for the first time (cache miss)
- **AND** `Prefetch:Enabled` is true and `Prefetch:OnRead` is true
- **THEN** `ZipPrefetchStrategy.PrefetchAsync` is called

#### Scenario: Prefetch skipped for RAR (no strategy)
- **WHEN** a file in a RAR archive is read for the first time
- **THEN** no prefetch is attempted (registry returns null for "rar")

#### Scenario: Subsequent sibling reads are cache hits
- **WHEN** ZIP prefetch has warmed sibling files
- **AND** those siblings are read
- **THEN** they are served from cache (cache hit, no extraction)

### Requirement: Sequential ZIP Read With Hole Discard
This requirement is now implemented by `ZipPrefetchStrategy` in `Infrastructure.Archives.Zip` (moved from VFS). The behavior is unchanged: single seek to span start, linear read discarding holes, decompressing wanted entries, warming via `IFileContentCache.WarmAsync`.

#### Scenario: Single seek for entire span
- **WHEN** `ZipPrefetchStrategy.PrefetchAsync` is called
- **THEN** one `FileStream.Seek` positions to `SpanStart`
- **AND** compressed data is read linearly until `SpanEnd`

#### Scenario: Hole bytes read but not decompressed
- **WHEN** the span contains entries not in the wanted set
- **THEN** their compressed bytes are consumed (discarded) without decompression

#### Scenario: Wanted entry decompressed and warmed
- **WHEN** a wanted entry's compressed data is read
- **THEN** it is decompressed (Store or Deflate) and `WarmAsync`-ed into the cache

### Requirement: Span Selection Algorithm
`SpanSelector` and `PrefetchPlan` SHALL reside in `Infrastructure.Archives.Zip` (moved from Application). They use `ZipEntryInfo.LocalHeaderOffset` and `CompressedSize` from `ZipFormatMetadataStore` for span computation. The algorithm is unchanged: centered window, fill-ratio shrinking, MaxFiles cap.

#### Scenario: High-density span selected
- **WHEN** candidates are closely packed by offset
- **THEN** fill ratio is high and all candidates are included

#### Scenario: Sparse span shrinks to meet fill ratio
- **WHEN** initial window has fill ratio below threshold
- **THEN** endpoints are removed until ratio >= threshold or only 1 entry remains

#### Scenario: Large directory is capped before span selection
- **WHEN** directory has more files than `MaxDirectoryFiles`
- **THEN** only the `MaxDirectoryFiles` nearest by offset to the trigger are considered

#### Scenario: Span capped at MaxFiles
- **WHEN** candidates exceed `MaxFiles`
- **THEN** the centered window contains at most `MaxFiles` entries
