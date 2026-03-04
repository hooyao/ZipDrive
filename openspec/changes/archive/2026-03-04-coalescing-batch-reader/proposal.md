## Why

When a user opens a folder of small files in Windows Explorer, the shell fires a burst of `ReadFile` calls simultaneously to generate thumbnails/previews. Each call currently opens an independent `ZipReader`, seeks independently, and extracts independently — even when the target entries are physically adjacent in the ZIP. This produces O(N) seeks and O(N) FileStream opens for what could be a single sequential read pass.

## What Changes

- Introduce a **coalescing window** that batches concurrent cache-miss requests for small files from the same archive within a configurable time window
- Sort pending requests by `LocalHeaderOffset` and group into batches where the ratio of useful bytes to total read range meets a configurable density threshold
- Execute each batch as a **single sequential read** through one `ZipReader`, decompressing each entry independently and populating the memory cache for all entries in the batch
- Entries that fall outside density threshold are extracted individually (current behavior)
- Optional **speculative caching**: when hole entries (unrequested files physically between two requested files) are encountered during the sequential pass, decompress and cache them too
- New `Coalescing` configuration section in `appsettings.jsonc`
- Memory tier only — large files (disk tier) are unaffected

## Capabilities

### New Capabilities
- `coalescing-batch-reader`: Batches concurrent small-file cache-miss requests for the same archive into a single sequential ZIP read pass, with configurable coalescing window, density threshold, and speculative caching of hole entries

### Modified Capabilities
- `file-content-cache`: Memory-tier materialization path gains a coalescing stage before the per-entry factory delegate fires

## Impact

- **New component**: `CoalescingBatchReader` (or decorator around `FileContentCache`) in `ZipDrive.Infrastructure.Caching`
- **Modified**: `FileContentCache` — memory-tier miss path routes through coalescing coordinator
- **Modified**: `CacheOptions` → new `Coalescing` subsection added to config schema
- **Modified**: `ZipVirtualFileSystem` or DI wiring to inject coalescing coordinator
- **New tests**: Unit tests for density calculation, batch grouping, window expiry, speculative caching; integration tests with burst reads
- **No breaking changes** — coalescing is opt-in via `Coalescing:Enabled` (default: `true`); disabling restores exact current behavior
