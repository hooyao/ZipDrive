## Context

ZipDrive currently only accepts a directory path via `Mount:ArchiveDirectory`. When a user drags a single ZIP/RAR file onto `ZipDrive.exe`, `ArgPreprocessor` rewrites it to `--Mount:ArchiveDirectory=<file-path>`, but `DokanHostedService.ExecuteAsync` fails at the `Directory.Exists()` check (line 74) and exits with an error.

The underlying infrastructure already supports individual archive operations: `ArchiveDiscovery.DescribeFile()` creates an `ArchiveDescriptor` from a single file, and `IArchiveManager.AddArchiveAsync()` registers it. The gap is only in the entry-point validation and mount orchestration.

For user-facing notices, the current approach uses `Console.Error.WriteLine` for errors and `ILogger.LogWarning` for warnings. These are functional but plain. The proposal calls for Spectre.Console `Panel` widgets to make user-facing messages visually distinct from program-level log output.

## Goals / Non-Goals

**Goals:**
- Support dragging a single ZIP/RAR file onto `ZipDrive.exe` with the same virtual path structure as directory mode (`R:\game.zip\...`)
- Provide clear, actionable error messages when a dragged file is not a supported format
- Warn users about single-file mode and empty directories with visually prominent notices via Spectre.Console
- Skip FileSystemWatcher entirely in single-file mode
- Use archive filename (sans extension) as volume label in single-file mode

**Non-Goals:**
- Flattened mount (exposing archive contents directly at `R:\` without the archive folder wrapper)
- Interactive TUI or persistent split-pane console layout
- FileSystemWatcher for single-file mode (no dynamic reload on file change)
- Changes to `ArgPreprocessor` (already works for both files and directories)
- Changes to `MountSettings` schema (no new config keys; `ArchiveDirectory` accepts both)

## Decisions

### 1. Detection and branching in DokanHostedService

**Decision**: Add `File.Exists()` check before the existing `Directory.Exists()` check in `ExecuteAsync`. Three-way branch: file → single-file mount, directory → existing logic, neither → error.

**Rationale**: Minimal change to the existing flow. `ArgPreprocessor` already maps bare args to `Mount:ArchiveDirectory` — no need for a separate config key. The property name becomes slightly misleading ("directory" vs "path"), but adding a new key would be a breaking config change for zero functional benefit.

**Alternative considered**: Add `Mount:ArchiveFile` as a separate config key. Rejected — two mutually exclusive keys add validation complexity and confuse the config surface.

### 2. Single-file mount via new VFS method

**Decision**: Add `MountSingleFileAsync(string filePath, CancellationToken)` to `IVirtualFileSystem` and `ArchiveVirtualFileSystem`. This method:
1. Computes `rootPath = Path.GetDirectoryName(filePath)`
2. Calls `_discovery.DescribeFile(rootPath, filePath)` to create `ArchiveDescriptor`
3. Returns null descriptor info if format not detected (caller handles error)
4. Calls `AddArchiveAsync(descriptor)` (reuses existing logic including `ProbeAsync`)
5. Sets volume label to `Path.GetFileNameWithoutExtension(filePath)` (truncated to 32 chars)
6. Sets `IsMounted = true`

**Rationale**: Reuses `DescribeFile` and `AddArchiveAsync` which already handle format detection, probe, and trie registration. The new method is ~15 lines.

**Alternative considered**: Have `DokanHostedService` call `DescribeFile` + `AddArchiveAsync` directly via `IArchiveManager`, bypassing VFS. Rejected — `IsMounted` and volume label are VFS concerns, not hosted service concerns.

### 3. Skip FileSystemWatcher for single files

**Decision**: `DokanHostedService.StartWatcher()` is simply not called when in single-file mode. No conditional logic inside `StartWatcher` — the call is skipped entirely.

**Rationale**: Watching a single file for changes adds complexity (parent dir filter, what to do on delete) with minimal user value. A user mounting a single file wants to peek inside, not monitor it.

### 4. Spectre.Console for user-facing notices

**Decision**: Add `Spectre.Console` NuGet dependency to `ZipDrive.Infrastructure.FileSystem` (where `DokanHostedService` lives). Create a static helper `UserNotice` with methods `Tip(title, body)`, `Warning(title, body)`, `Error(title, body)` that write `Panel` widgets directly to `AnsiConsole`.

**Rationale**: User-facing notices (TIP, WARNING, ERROR) are presentation concerns distinct from structured logging. Spectre.Console panels render cleanly in any terminal with automatic width adaptation. Using `AnsiConsole.Write()` directly (not Serilog) ensures box-drawing characters aren't mangled by structured log formatting. These notices are mixed inline with log output — they appear at the natural point in the startup sequence.

**Panel styles**:
- **TIP**: `RoundedBorder()`, `Color.Blue` border, header `[blue bold] TIP [/]`
- **WARNING**: `RoundedBorder()`, `Color.Yellow` border, header `[yellow bold] WARNING [/]`
- **ERROR**: `DoubleBorder()`, `Color.Red` border, header `[red bold] ERROR [/]`

**Dependency placement**: `ZipDrive.Infrastructure.FileSystem` (not CLI) because `DokanHostedService` is the caller. The `UserNotice` helper is a static class in this project.

**Alternative considered**: Put Spectre.Console in CLI only. Rejected — the notices are emitted from `DokanHostedService` in the Infrastructure layer, not from `Program.cs`.

### 5. Error message content

**Decision**: Three error scenarios with specific messaging:

| Scenario | Panel | Content |
|----------|-------|---------|
| Path empty | ERROR | "No archive path specified.\nDrag a ZIP/RAR file or a folder onto ZipDrive.exe, or set Mount:ArchiveDirectory in appsettings.jsonc." |
| Path not found | ERROR | "Path not found: {path}" |
| File not supported | ERROR | "Cannot mount \"{filename}\"\nFile type \"{ext}\" is not a supported archive format.\nSupported formats: {list from IFormatRegistry.SupportedExtensions}\n\nTip: Drag a ZIP or RAR file, or a folder containing them." |
| Single file mounted | TIP | "You mounted a single archive file.\nDrag a FOLDER onto ZipDrive.exe to mount all archives inside it at once!" |
| Empty directory | WARNING | "No supported archives found in \"{dirName}\"\nSupported formats: {list}\nThe drive is mounted but empty. Add ZIP or RAR files and they will appear automatically." |

### 6. Volume label for single-file mode

**Decision**: `Path.GetFileNameWithoutExtension(filePath)`, truncated to 32 chars (NTFS limit), same truncation logic as existing folder-name label.

**Rationale**: `game.zip` → volume label `game`. Natural and expected.

## Risks / Trade-offs

- **`ArchiveDirectory` naming confusion** → Acceptable. The config key works for both files and directories. Renaming would be a breaking change. The error messages now mention "file or folder" to guide users.
- **Spectre.Console in Infrastructure layer** → Slightly unusual (Infrastructure usually doesn't have presentation concerns). Mitigated by isolating it in a single `UserNotice` static class. If this becomes a concern later, it can move to a shared project.
- **No dynamic reload for single file** → If the user modifies the archive while mounted, stale data is served. Acceptable — the user can restart ZipDrive. This matches the stated non-goal.
