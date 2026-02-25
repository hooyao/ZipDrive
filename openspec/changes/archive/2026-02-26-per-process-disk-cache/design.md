## Context

`DiskStorageStrategy` currently receives a `tempDirectory` string (or null for system temp), ensures the directory exists, and writes cache files directly into it as `{Guid}.zip2vd.cache`. On shutdown, `CacheMaintenanceService.Clear()` calls `GenericCache.Clear()` which disposes individual cache files (deleting each temp file). The directory itself is never cleaned up.

When multiple ZipDrive processes run concurrently (mounting different drive letters), all processes share the same flat directory. Crashed processes leave orphaned cache files with no way to identify which process created them.

**Constraints:**
- `CacheMaintenanceService` already handles final cleanup via `_fileCache.Clear()`.
- The existing `IStorageStrategy<T>` interface and `GenericCache<T>` must not change — this is internal to `DiskStorageStrategy`.

## Goals / Non-Goals

**Goals:**
- Isolate disk cache files per process using `ZipDrive-{pid}` subdirectory
- Clean up the entire subdirectory on graceful shutdown
- Keep it simple — no orphan cleanup, no lock files

**Non-Goals:**
- Automatic orphan cleanup on startup (user can clean manually)
- Cross-process cache sharing or coordination
- Changes to `IStorageStrategy<T>` interface or `GenericCache<T>`

## Decisions

### Decision 1: Subdirectory naming — `ZipDrive-{pid}`

**Choice:** Create subdirectory named `ZipDrive-{pid}` under the base temp directory. Example: `Z:\Temp\ZipDrive-12345\`.

The PID comes from `Environment.ProcessId`.

**Alternatives considered:**
- `ZipDrive-{guid}`: Not human-readable, can't correlate to running process.
- `ZipDrive-{pid}-{mountLetter}`: Adds unnecessary complexity. PID alone uniquely identifies the process, and adding mount letter would require threading `MountSettings` through the caching infrastructure.

**Rationale:** Human-readable, debuggable. `tasklist | findstr 12345` immediately tells you if the process is alive. Simple — no dependency on `MountSettings`.

### Decision 2: DiskStorageStrategy owns subdirectory creation

**Choice:** `DiskStorageStrategy` always creates a `ZipDrive-{pid}` subdirectory in its constructor. No external parameters needed — the strategy computes the name internally using `Environment.ProcessId`.

**Alternatives considered:**
- Pass subdirectory name from `DualTierFileCache`: Leaks an implementation detail upward.
- Pass `IOptions<MountSettings>` to compute mount letter: Adds config dependency to a low-level component for no practical benefit.

**Rationale:** `DiskStorageStrategy` is the right owner — it knows about its temp directory and should manage its own subdirectory structure. The constructor computes `Path.Combine(baseDir, $"ZipDrive-{Environment.ProcessId}")` and creates the directory.

### Decision 3: Cleanup chain — Clear() then DeleteCacheDirectory()

**Choice:** On shutdown:
1. `CacheMaintenanceService` calls `_fileCache.Clear()` (existing) — deletes individual cache files
2. `CacheMaintenanceService` calls a new `_fileCache.DeleteCacheDirectory()` method — removes the now-empty subdirectory

The directory delete is best-effort (logged warning on failure). Even if individual file cleanup fails, `Directory.Delete(recursive: true)` will catch remaining files.

**Rationale:** Two-phase cleanup is defensive. If `Clear()` successfully removes all files, the directory delete is trivial. If some files linger (MMF still held by OS), the recursive delete catches them. If both fail, the orphan directory stays — acceptable per non-goal.

## Risks / Trade-offs

**[PID reuse after crash]** — A crashed process's orphan directory (e.g., `ZipDrive-12345`) could collide with a new process that happens to get PID 12345. This is extremely unlikely and harmless (the new process would just reuse the directory and clean it up on exit). No mitigation needed.

**[Directory.Delete may fail if files are locked by OS]** — Memory-mapped files can keep file handles open briefly after `MemoryMappedFile.Dispose()`. If `DeleteCacheDirectory` runs before the OS fully releases handles, it may fail. Mitigation: `Clear()` already disposes all MMFs first, and the delete is best-effort with warning log.
