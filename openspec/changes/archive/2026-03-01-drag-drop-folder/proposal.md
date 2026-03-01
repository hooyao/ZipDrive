## Why

Users currently must launch ZipDrive from a terminal with `--Mount:ArchiveDirectory="..."`. Drag-and-drop onto `ZipDrive.exe` is the standard Windows UX for "open this folder with this app" — supporting it makes the app usable without any CLI knowledge. When dragged, Windows passes the folder path as a bare positional argument, which the current config-only parser ignores.

## What Changes

- Detect bare (non-`--` prefixed) positional arguments in `args` before host builder initialization and rewrite them to `--Mount:ArchiveDirectory=<path>` so the standard config pipeline handles them.
- Add directory existence validation in `DokanHostedService`: if `ArchiveDirectory` is set but is not a valid directory, print a clean error and wait for a keypress before exiting (so the auto-created console window stays visible).
- The existing "ArchiveDirectory is required" error path also gets the press-any-key treatment.

## Capabilities

### New Capabilities
- `drag-drop-launch`: Bare positional argument rewriting and error UX for drag-and-drop scenarios

### Modified Capabilities
- `cli-application`: Program.cs gains pre-host arg rewriting
- `dokan-hosted-service`: Validation adds directory existence check and press-any-key on error

## Impact

- `src/ZipDrive.Cli/Program.cs` — arg rewriting before `CreateDefaultBuilder`
- `src/ZipDrive.Infrastructure.FileSystem/DokanHostedService.cs` — validation + console UX
- No new dependencies, no breaking changes
