## Context

`DiskStorageStrategy` currently receives a `tempDirectory` string (or null for system temp), ensures the directory exists, and writes cache files directly into it as `{Guid}.zip2vd.cache`. On shutdown, `CacheMaintenanceService.Clear()` calls `GenericCache.Clear()` which disposes individual cache files (deleting each temp file). The directory itself is never cleaned up.

When multiple ZipDrive processes run concurrently (mounting different drive letters), all processes share the same flat directory. Crashed processes leave orphaned cache files with no way to identify which process created them.

**Constraints:**
- `DiskStorageStrategy` is constructed inside `DualTierFileCache` which receives `IOptions<CacheOptions>` and has no access to `MountSettings`.
- `CacheMaintenanceService` already handles final cleanup via `_fileCache.Clear()`.
- The existing `IStorageStrategy<T>` interface and `GenericCache<T>` must not change — this is internal to `DiskStorageStrategy`.

## Goals / Non-Goals

**Goals:**
- Isolate disk cache files per process using `ZipDrive-{pid}-{mountLetter}` subdirectory
- Clean up the entire subdirectory on graceful shutdown
- Keep it simple — no orphan cleanup, no lock files

**Non-Goals:**
- Automatic orphan cleanup on startup (user can clean manually)
- Cross-process cache sharing or coordination
- Changes to `IStorageStrategy<T>` interface or `GenericCache<T>`

## Decisions

### Decision 1: Subdirectory naming — `ZipDrive-{pid}-{mountLetter}`

**Choice:** Create subdirectory named `ZipDrive-{pid}-{mountLetter}` under the base temp directory. Example: `Z:\Temp\ZipDrive-12345-R\`.

The mount letter is extracted from `MountSettings.MountPoint` (first character, e.g., `R` from `R:\`). The PID comes from `Environment.ProcessId`.

**Alternatives considered:**
- `ZipDrive-{guid}`: Not human-readable, can't correlate to running process.
- `ZipDrive-{pid}` only: Less descriptive when debugging; can't tell which mount point a cache dir belongs to.

**Rationale:** Human-readable, debuggable. `tasklist | findstr 12345` immediately tells you if the process is alive. The mount letter distinguishes cache directories when a single machine runs multiple ZipDrive instances.

### Decision 2: Pass mount letter through DualTierFileCache

**Choice:** Add `IOptions<MountSettings>` as a constructor parameter of `DualTierFileCache`. Extract the drive letter and pass it to `DiskStorageStrategy` as a new `string processSubdirectory` parameter.

`DualTierFileCache` computes: `$"ZipDrive-{Environment.ProcessId}-{mountSettings.MountPoint[0]}"` and passes the full subdirectory name.

**Alternatives considered:**
- Inject `MountSettings` directly into `DiskStorageStrategy`: Strategy is a low-level component; adding config dependency feels wrong.
- Compute in `Program.cs` and override `CacheOptions.TempDirectory`: Would work but loses the semantic separation (user's configured base dir vs computed process dir).

**Rationale:** `DualTierFileCache` already receives `IOptions<CacheOptions>`, making it the natural place to also receive `IOptions<MountSettings>`. It already owns the construction of `DiskStorageStrategy`. The strategy stays config-agnostic — it just receives a directory string.

### Decision 3: DiskStorageStrategy creates and owns the process subdirectory

**Choice:** `DiskStorageStrategy` constructor receives the full resolved directory path (base + process subdirectory), creates it if it doesn't exist (existing behavior), and stores files there (existing behavior). A new `DeleteDirectory()` method deletes the entire directory recursively.

```
Constructor:
  _tempDirectory = Path.Combine(baseDir, processSubdirectory)
  Directory.CreateDirectory(_tempDirectory)
  // Files go into _tempDirectory as before

DeleteDirectory():
  Directory.Delete(_tempDirectory, recursive: true)
```

**Rationale:** Minimal change to `DiskStorageStrategy`. The constructor already creates directories. The only new behavior is directory-level cleanup (previously files were deleted individually by `Dispose(StoredEntry)`, the directory was never touched).

### Decision 4: Cleanup chain — Clear() then DeleteDirectory()

**Choice:** On shutdown:
1. `CacheMaintenanceService` calls `_fileCache.Clear()` (existing) — deletes individual cache files
2. `CacheMaintenanceService` calls a new `_fileCache.DeleteCacheDirectory()` method — removes the now-empty subdirectory

The directory delete is best-effort (logged warning on failure). Even if individual file cleanup fails, `Directory.Delete(recursive: true)` will catch remaining files.

**Rationale:** Two-phase cleanup is defensive. If `Clear()` successfully removes all files, the directory delete is trivial. If some files linger (MMF still held by OS), the recursive delete catches them. If both fail, the orphan directory stays — acceptable per non-goal.

## Risks / Trade-offs

**[Mount letter extraction assumes standard drive letter format]** — `MountSettings.MountPoint[0]` assumes the mount point starts with a drive letter (e.g., `R:\`). Dokan also supports UNC mount paths (`\\mount\point`), but ZipDrive's current config only uses drive letters. If UNC support is added later, the extraction logic would need updating. Low risk — UNC mount is not in scope.

**[PID reuse after crash]** — A crashed process's orphan directory (e.g., `ZipDrive-12345-R`) could collide with a new process that happens to get PID 12345 and mounts the same letter. This is extremely unlikely (PID reuse + same letter + same base dir) and harmless (the new process would just reuse the directory and clean it up on exit). No mitigation needed.

**[Directory.Delete may fail if files are locked by OS]** — Memory-mapped files can keep file handles open briefly after `MemoryMappedFile.Dispose()`. If `DeleteCacheDirectory` runs before the OS fully releases handles, it may fail. Mitigation: `Clear()` already disposes all MMFs first, and the delete is best-effort with warning log.
