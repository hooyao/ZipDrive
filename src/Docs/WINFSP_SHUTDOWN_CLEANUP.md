# WinFsp Shutdown Cleanup (Ctrl+C Temp-File Leak)

## Problem

When the WinFsp build of ZipDrive was stopped with **Ctrl+C** while any large file had been
routed to the disk tier, it left temp files behind:

```
%TEMP%\ZipDrive-{pid}\*.chunked
```

Each disk-tier extraction writes its decompressed bytes to a sparse backing file under a
per-process temp directory (`ZipDrive-{pid}`). On shutdown those files — and the directory — were
never removed.

## Root cause

The WinFsp migration used the **standard .NET Generic Host lifecycle** (`await host.RunAsync()`),
so `ConsoleLifetime` intercepted Ctrl+C correctly and ran each hosted service's `StopAsync`.
Ctrl+C handling was never the issue.

The issue was simply that **`WinFspHostedService.StopAsync` omitted the disk-cache cleanup step**.
It unmounted the WinFsp host and the VFS, but it never:

- injected `IFileContentCache`, and
- called `_fileCache.Clear()` / `_fileCache.DeleteCacheDirectory()`.

So the `ChunkedFileEntry` objects were never disposed and the per-process temp directory was never
deleted — the backing files leaked on every shutdown.

For contrast, the Dokan build never leaked, but not because of anything subtle: its hosted service
already performed cache cleanup on stop. (Their callback threading also differs — see below — but
that difference is not what caused or fixed this leak.)

## Fix

### 1. `WinFspHostedService.StopAsync` — add cleanup, in the correct order

`StopAsync` now runs, in order:

1. **`_host.Dispose()`** — WinFsp canonical unmount. `FspFileSystemStopDispatcher` stops
   dispatching new `ReadFile` callbacks and blocks until every in-flight callback drains. This
   MUST run first: until it returns, WinFsp worker threads may still be reading from the cache and
   its streams, so freeing that state earlier could let an in-flight read touch disposed objects.
2. `StopWatcher()` — stop the `FileSystemWatcher` + consolidator (fully synchronous disposal, no
   async continuations).
3. `await _vfs.UnmountAsync(...)`.
4. **`_fileCache.Clear()` + `_fileCache.DeleteCacheDirectory()`** — the step whose absence caused
   the leak. `Clear()` disposes every cached `ChunkedFileEntry`; `DeleteCacheDirectory()` removes
   the `ZipDrive-{pid}` temp directory. Safe here because no read is in flight after step 1.
5. `await base.StopAsync(...)` — last, so it observes a fully drained dispatcher.

### 2. `ChunkedFileEntry` — RAII ownership of the backing file

`Clear()` only helps if disposing an entry reliably deletes its file. A background extraction may
still be mid-write when shutdown fires, and `DeflateStream.ReadAsync` barely honors its
cancellation token — so waiting for the extraction task to unwind could stall multiple seconds per
file (or not release the file at all). `ChunkedFileEntry.Dispose()` therefore does **not** wait for
the task; it takes ownership directly:

1. `ExtractionCts.Cancel()` — best-effort signal to the extraction task.
2. **Close the writer handle directly** — `Interlocked.Exchange(ref _writerFs, null)?.Dispose()`.
   The extraction task publishes its writer `FileStream` via `Volatile.Write`; closing it here
   releases the OS file lock immediately. If extraction is mid-`WriteAsync`, it observes
   `ObjectDisposedException` and terminates (handled in `ExtractAsync`). `FileStream.Dispose` is
   idempotent, so racing with the task's own `finally` is safe.
3. `CancelPendingChunks()` — wake any readers blocked on un-extracted chunks.
4. `DeleteBackingFileWithRetry()` — delete the file (the writer handle is closed in step 2; brief
   retry tolerates a reader handle that is still closing).

### 3. `ChunkedDiskStorageStrategy` — single owner of deletion

`Dispose(StoredEntry)` now just calls `entry.Dispose()`. Backing-file deletion lives entirely in
the entry (RAII), so every disposal site behaves identically and there is no duplicate delete path
to keep in sync.

### 4. `ArchiveChangeConsolidator` — synchronous `Dispose()`

`StopWatcher` runs on the shutdown path and disposes the consolidator. A synchronous `Dispose()`
was added that uses only blocking primitives — `Timer.Dispose(WaitHandle)` (blocks until any
in-flight timer callback finishes) and a bounded `Task.Wait` on the in-flight flush — so shutdown
does not depend on a `ValueTask` continuation being scheduled while the host is tearing down.

## Dokan vs WinFsp callback threading (context, not the cause)

The two adapters have different callback models, which is worth knowing when reasoning about
shutdown but did **not** cause this leak:

- **Dokan** — `ReadFile` is a **synchronous** `NtStatus` callback executed on **Dokan's own native
  worker threads** (`_vfs.ReadFileAsync(...).GetAwaiter().GetResult()` runs on those threads, not
  the .NET thread pool).
- **WinFsp** (this binding) — `ReadFile` is an **async** `ValueTask` callback and `SynchronousIo`
  is `false`; a read that doesn't complete synchronously is completed via a continuation and the
  native thread is released (`STATUS_PENDING`). A read blocked on chunk extraction is a suspended
  state machine that holds no thread.

The unmount-before-cleanup ordering in `StopAsync` is the WinFsp-canonical requirement (drain
callbacks before freeing what they touch) and is correct regardless of these threading details.

## Verification

- **Unit / integration** (`tests/ZipDrive.Infrastructure.Caching.Tests`):
  - `ChunkedFileEntryTests.Dispose_WhileWriterHandleOpen_ClosesHandleAndDeletesFile` — Dispose
    deletes the backing file (and returns quickly) while the extraction writer handle is open.
  - `ChunkedExtractionIntegrationTests.Dispose_DuringActiveExtraction_ReturnsFastAndDeletesFile` —
    same guarantee through the strategy, with a ~10 s extraction proving Dispose does not wait for
    it.
  - `ChunkedFileEntryTests.Dispose_DeletesBackingFile` / `Dispose_WhenBackingFileAlreadyGone_*` —
    the RAII delete contract and its shutdown-race tolerance.
- **Manual (AOT)** — `dotnet publish` the CLI, mount a directory of large archives, drive reads to
  force disk-tier extraction, press Ctrl+C. The process exits promptly and
  `%TEMP%\ZipDrive-{pid}\` is gone with zero residual `.chunked` files.
