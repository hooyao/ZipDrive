# ZipDrive WinFsp hang — root-cause analysis

> Standalone analysis for the diagnostic session. No prior context required.
> Companion docs: [`CAPTURE.md`](CAPTURE.md) (how to reproduce + capture), [`ANALYZE.md`](ANALYZE.md)
> (how to read the dumps), [`PROPOSED-FIX.md`](PROPOSED-FIX.md).

## Symptom

After commit `7b5bceb` migrated the presentation adapter from **DokanNet → WinFsp**
(`ZipDrive.Infrastructure.FileSystem`), a hang appears:

1. Mount a ZIP containing a mix of **images and videos** (archive on a **NAS over SMB**).
2. Open the mounted folder in Explorer; switch to large icons → Windows generates
   thumbnails (images **and** videos).
3. Double-click an image that **already has a thumbnail** (its bytes are already in the
   memory cache). **Windows 11 Photos opens and displays the image fine** — then the Photos
   window **hangs / becomes unresponsive** for a long time, eventually recovering.
4. On **SMB** the hang is severe (many seconds); on a local **NVMe** copy the same steps
   cause only a mild 2–3 s stall.
5. Under the **old Dokan** adapter the identical workload was **perfectly smooth** ("丝毫不卡").

The cache / extraction / VFS code is **shared** between the Dokan and WinFsp builds — the
migration only swapped the presentation adapter (plus a zero-copy `Memory<byte>` read
signature). Because the shared code was smooth under Dokan, **the regression lives in the
adapter/host threading model, not in the cache internals.**

## TL;DR root cause

**A thread-pool starvation regression introduced by the Dokan→WinFsp migration.** Work that
Dokan ran on its **large, on-demand-growing native dispatcher pool** now runs on **two small,
fixed, never-grown pools**, both of which saturate under SMB round-trip latency:

| Pool | Who uses it now | Size | Grows? |
|---|---|---|---|
| **WinFsp dispatcher threads** | **All metadata callbacks** (`Open`/`GetFileInfo`/`GetSecurityByName`/`GetDirInfoByName`/`Flush`) block it synchronously via `GetAwaiter().GetResult()` | `[4,16]` (CPU count, clamped) | **No — fixed at mount** |
| **.NET ThreadPool** | All `STATUS_PENDING` read completions, `Task.Run` background chunk extraction, deflate (CPU), chunk-ready continuations | min = CPU count | only ~1 thread / 0.5–1 s (hill-climb) |

On NVMe each backing op completes in microseconds, so threads free almost instantly and
neither pool saturates. On SMB each backing op is a network round-trip (ms–seconds under
load), so threads are held 100×–1000× longer and the small pools fill up. Dokan never
touched either pool and grew its own native threads on demand, so it stayed smooth.

## Why Photos hangs **after** the image is already displayed

This is the key detail. The double-clicked image's own open+read **succeeds** (memory-cache
hit → synchronous → fast). The hang is in what Photos does **next**:

Windows Photos, after displaying an image, immediately **enumerates the sibling files in the
same folder and pre-reads/decodes the neighbours** (filmstrip + next/previous navigation).
Those sibling operations hit the mounted filesystem — and any sibling that is **not yet
cached** (notably the **video** still being thumbnailed, which is large → disk-tier chunked
extraction from SMB) triggers slow extraction. With the .NET ThreadPool already occupied by
that video's background extraction (and the WinFsp dispatcher threads blocked on metadata),
Photos' file operations stall, and its UI thread — which is waiting on them — goes
unresponsive. When the backlog finally drains, Photos recovers.

So the **victim** moved from "the cached image's read" to "Photos' post-display sibling
prefetch," but the **cause** is the same starvation.

### The cross-pool coupling (why it bites even though Explorer thumbnailing is single-threaded)

Windows Explorer thumbnail generation is **sequential / single-threaded** (confirmed —
see Sources), so this is *not* "dozens of concurrent requests flood the pool." It only takes
**one** in-flight video extraction:

- The video extraction (`Task.Run` → `DeflateStream` inflate + per-chunk SMB writes/flush)
  occupies .NET ThreadPool threads for the whole multi-second SMB extraction.
- Each WinFsp **metadata callback blocks a dispatcher thread** in `GetResult()` waiting for an
  `async` VFS call whose **continuation needs a .NET ThreadPool thread** to finish — but the
  pool is occupied by the video. So the dispatcher thread stays blocked longer, and the pool
  grows only ~1 thread/sec → the backlog compounds across both pools.

## Evidence (file:line)

Verified by reading the source (ZipDrive repo + the vendored `WinFsp.Native` at the path the
`WinFsp.Native` package is built from):

**The threading flip (the regression):**
- OLD `DokanFileSystemAdapter : IDokanOperations2` — synchronous callbacks, blocked
  `.GetAwaiter().GetResult()` on **native Dokan threads** (a large, on-demand pool).
  (`git show 7b5bceb^:src/ZipDrive.Infrastructure.FileSystem/DokanFileSystemAdapter.cs`)
- NEW `WinFspFileSystemAdapter` — `SynchronousIo => false`, fully `async ValueTask`
  (`WinFspFileSystemAdapter.cs:44,95,122,144,210,276`).

**WinFsp metadata callbacks block the dispatcher thread (no `STATUS_PENDING` path):**
- `FileSystemHost.cs` `OnOpen:402`, `OnGetFileInfo:707`, `OnGetSecurityByName:366`,
  `OnGetDirInfoByName:866`, `OnFlush:692`, `OnCreate:387` — all
  `task.AsTask().GetAwaiter().GetResult()`.
- Only `OnRead:488-555`, `OnWrite:557-624`, `OnReadDirectory:628-676` use the non-blocking
  `STATUS_PENDING` + `ContinueWith(... FspFileSystemSendResponse, ExecuteSynchronously)` pattern.

**WinFsp dispatcher pool is small and fixed:**
- `WinFspHostedService.cs:157` `_host.Mount(point)` → `FileSystemHost.Mount → MountEx(…, threadCount: 0, …)`
  → `FspFileSystemStartDispatcher(fs, 0)`.
- WinFsp's `0`-default = process CPU count, **clamped to `[4,16]`**, and **fixed for the mount's
  lifetime** (no on-demand growth) — confirmed in winfsp `src/dll/fs.c`
  (`FspFileSystemDispatcherDefaultThreadCountMin=4`, `…Max=16`).

**.NET ThreadPool is never raised:**
- Repo-wide: **no** `ThreadPool.SetMinThreads` / `SetMaxThreads` anywhere (`Program.cs` only
  swaps `DokanHostedService → WinFspHostedService`). Min worker/IOCP = CPU count, hill-climbs
  ~1 thread/sec.

**The shared cache is NOT the bottleneck (refutes a cache-lock theory):**
- Memory-tier (small image) HIT completes **synchronously**, no I/O, no lock:
  `GenericCache.BorrowAsync` Layer-1 (`GenericCache.cs:125-164`) returns without awaiting;
  `MemoryStorageStrategy.Retrieve` returns a `MemoryStream` over a `byte[]`.
- Structure-cache HIT is a pure dictionary lookup, lock-free; cold first-build is coalesced
  per-archive by `Lazy<Task>` (`GenericCache.cs:182-185`) so a burst collapses to **one** SMB
  Central-Directory parse.
- `ArchiveGuard`/`ArchiveNode` are lock-free (`Interlocked` only).

**Large-file (video) extraction is the .NET ThreadPool occupant:**
- Disk-tier extraction is `Task.Run(...)` (`ChunkedDiskStorageStrategy.cs:116`) running
  `ChunkedFileEntry.ExtractAsync` (`:149-199`): `DeflateStream` inflate (CPU) + per-chunk
  `fs.WriteAsync`/`FlushAsync`. Reads await chunk readiness via a TCS created
  `RunContinuationsAsynchronously` (`ChunkedFileEntry.cs:52`) → reader continuations are also
  ThreadPool-dispatched. ZIP **source** reads are async (`ZipReader.cs:78` `useAsync:true` +
  IOCP), so SMB stalls park on IOCP rather than pinning a worker — but the deflate CPU + chunk
  plumbing still consume ThreadPool threads.

**(RAR only — not this ZIP repro, but a latent multiplier):** SharpCompress is synchronous;
`RarEntryExtractor.cs:46-48` `CopyToAsync` over a non-async stream runs blocking `Read` on a
ThreadPool thread; `RarStructureBuilder.cs:68,86` wrap synchronous opens in `Task.Run`.

## Ranked root causes

1. **(HIGH) .NET ThreadPool starvation** — the video's `Task.Run` extraction + deflate + chunk
   plumbing occupy the never-grown pool; Photos' post-display sibling prefetch (and read
   completions generally) can't get threads. Primary suspect for *this* (ZIP, Photos-after-
   display) symptom.
2. **(HIGH) WinFsp dispatcher-thread starvation** — metadata callbacks block the fixed `[4,16]`
   pool via `GetResult()`; under SMB the cold work behind them (or the cross-pool wait on the
   starved ThreadPool) holds dispatcher threads so new IRPs can't be served.
3. **(MED) Cross-pool coupling** — (2) waits on (1): blocked dispatcher threads need the
   starved ThreadPool to complete their awaited work, so the two amplify each other.
4. **(LOW/MED) Disk-tier chunk-wait coupling** — sibling reads of the large video block on
   sequential, per-chunk-flushed extraction; if `Cache:TempDirectory` is on SMB, every chunk
   write+flush is a round-trip.

The **dump settles which of (1)/(2) dominates** — see [`ANALYZE.md`](ANALYZE.md).

## Confirmed external facts

- **WinFsp default dispatcher thread count** when `ThreadCount=0`: process CPU count clamped to
  `[4,16]`, **fixed at mount, no on-demand growth** (winfsp `src/dll/fs.c`,
  `FspFileSystemStartDispatcher`).
- **Windows Explorer thumbnail generation is sequential / single-threaded** (lazy, one at a
  time) — so the hang is a *small-pool + blocking-work* problem, not a *request-volume* problem.

### Sources
- Does Windows File Explorer parallelize thumbnail extraction? (no documented parallelism;
  sequential/lazy): <https://learn.microsoft.com/en-us/answers/questions/2142023/does-the-windows-file-explorer-use-parallelization>
- File Explorer renders thumbnails sequentially:
  <https://www.xda-developers.com/fix-slow-windows-file-thumbnails-with-this-lightweight-tool/>
- WinFsp dispatcher source (`FspFileSystemStartDispatcher`):
  <https://github.com/winfsp/winfsp/blob/master/src/dll/fs.c>
