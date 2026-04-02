## Why

Users want to drag a single ZIP or RAR file directly onto ZipDrive.exe, but the current system only accepts a folder (directory of archives). This is the most intuitive gesture for users who just want to peek inside one archive. The infrastructure already supports individual archive operations (`DescribeFile`, `AddArchiveAsync`), so the gap is only at the entry-point layer.

## What Changes

- **Single-file detection**: `DokanHostedService` detects whether `Mount:ArchiveDirectory` points to a file or directory, branching to the appropriate mount path
- **Single-file mount path**: New `MountSingleFileAsync` on VFS that uses existing `DescribeFile` + `AddArchiveAsync` (no discovery scan)
- **No FileSystemWatcher for single-file mode**: Watcher is skipped entirely when mounting a single file
- **Volume label from filename**: Single-file mode uses the archive filename (without extension) as the volume label (e.g., `game.zip` → `game`)
- **Spectre.Console user-facing notices**: Pretty bordered panels for user-facing messages (TIP, WARNING, ERROR), mixed inline with Serilog log output
  - **TIP**: When single file is mounted successfully — "Drag a FOLDER to mount multiple archives at once"
  - **WARNING**: When a directory contains zero supported archives — explains what formats are supported, notes dynamic reload will pick up new files
  - **ERROR**: When a dragged file is not a supported format — shows the unsupported extension and lists supported formats
- **Improved error messages**: All validation errors updated with clearer wording and actionable guidance
- **New dependency**: `Spectre.Console` NuGet package (1.67 MB, zero transitive deps on .NET 10, MIT)

## Capabilities

### New Capabilities
- `single-file-mount`: Detection of file vs. directory input, single-file mount path, volume label derivation, FileSystemWatcher skip
- `user-notices`: Spectre.Console Panel-based user-facing notices (TIP/WARNING/ERROR) with colored borders, used at startup for mount mode feedback and validation errors

### Modified Capabilities
- `drag-drop-launch`: Error messages updated for file-not-supported and path-not-found scenarios; `ArgPreprocessor` unchanged but `DokanHostedService` validation logic changes
- `dokan-hosted-service`: Validation flow branches on file vs. directory; single-file mode skips FileSystemWatcher setup
- `archive-discovery`: `DescribeFile` already exists and is reused; new empty-directory warning after `DiscoverAsync` returns zero results

## Impact

- **Code**: `DokanHostedService` (validation + mount branching), `ArchiveVirtualFileSystem` (new `MountSingleFileAsync`), `IVirtualFileSystem` (interface addition), CLI `Program.cs` (Spectre.Console DI/setup if needed)
- **Dependencies**: Add `Spectre.Console` to `Directory.Packages.props` and `ZipDrive.Cli.csproj` (or a shared project if notices are used from Infrastructure.FileSystem)
- **Binary size**: +1.67 MB (~2.3% of current 74 MB single-file build)
- **Config**: No new config keys; `Mount:ArchiveDirectory` semantics broadened to accept file paths
- **Breaking changes**: None — existing folder drag-and-drop and CLI usage unchanged
