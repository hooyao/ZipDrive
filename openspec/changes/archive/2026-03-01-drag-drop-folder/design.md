## Context

ZipDrive.exe currently requires `--Mount:ArchiveDirectory="..."` via command line. When a user drags a folder onto the exe in Windows Explorer, the OS launches `ZipDrive.exe "D:\my-zips"` — a bare positional argument that the .NET config pipeline ignores. The app then fails with "ArchiveDirectory is required" and the console window closes instantly, leaving the user confused.

## Goals / Non-Goals

**Goals:**
- Accept a bare folder path as `args[0]` and map it to `Mount:ArchiveDirectory`
- Validate that `ArchiveDirectory` points to an existing directory
- Keep the console window open on validation errors so drag-and-drop users can read the message

**Non-Goals:**
- Auto-detecting a free mount drive letter (users edit `appsettings.jsonc`)
- Accepting bare file paths (e.g., a single `.zip` dragged onto the exe)
- Positional argument for mount point

## Decisions

### 1. Prepend rewritten arg so named args win

The bare path is rewritten to `--Mount:ArchiveDirectory=<path>` and **prepended** to the args array. Because .NET configuration uses last-wins semantics, an explicit `--Mount:ArchiveDirectory=X` later in the array overrides the rewritten value. This matches user expectation: explicit flags are more intentional than drag-and-drop.

**Alternative considered:** Append instead of prepend. Rejected because it would make the bare arg override an explicit named arg, which is surprising.

### 2. Detection heuristic: first arg, not starting with `--`

A bare positional arg is detected when `args.Length > 0 && !args[0].StartsWith("--")`. This is simple and sufficient — the .NET config binder only consumes `--key=value` pairs, so any non-`--` arg is otherwise ignored.

**Alternative considered:** Check `Directory.Exists()` at detection time. Rejected because validation belongs in `DokanHostedService`, not in arg rewriting. Separation of concerns: rewrite is mechanical, validation is semantic.

### 3. Press-any-key on all validation errors

On any `ArchiveDirectory` validation failure (empty, not a directory, doesn't exist), the service prints the error and calls `Console.ReadKey()` before stopping the host. This keeps the auto-created console window open for drag-and-drop users. For terminal users, it's a minor inconvenience (one extra keypress) but provides a consistent experience.

**Alternative considered:** Detect whether the console is auto-created (no parent console process) and only wait in that case. Rejected as over-engineered for the benefit — the extra keypress is harmless from a terminal.

## Risks / Trade-offs

- **[Minor UX friction]** Terminal users must press a key after validation errors. → Acceptable; errors are rare and the message is informative.
- **[Bare arg ambiguity]** A non-path string without `--` prefix would be rewritten to `--Mount:ArchiveDirectory=<garbage>`, then caught by directory validation. → Correct behavior; the validation error message will make the issue clear.
