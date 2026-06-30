# Dump analysis playbook — for Claude Code on the test machine

> You are analyzing process dumps captured from **ZipDrive**, a WinFsp-backed virtual
> filesystem, while **Windows 11 Photos hung** browsing a ZIP mounted from a NAS over SMB.
> **Read [`ANALYSIS.md`](ANALYSIS.md) first** for the hypothesis. Your job: confirm WHICH
> starvation dominates, using the dumps in `diag\dumps\`.

## How to run the analysis

Option A (scripted — renders all reports):
```powershell
.\diag\analyze-dump.ps1     # writes diag\out\<dump>.analysis.txt for every dump
```
Option B (interactive, per dump):
```powershell
dotnet-dump analyze .\diag\dumps\<one>.dmp
# then at the > prompt run, in order:
#   clrthreads
#   threadpool
#   dumpasync --stats
#   pstacks
#   syncblk
#   clrstack -all
```
Analyze **the dump(s) taken mid-hang** (the middle ones in the series). Compare an early-hang
dump with a late/recovering one: stacks identical across dumps ⇒ hard block; shifting ⇒ slow drain.

> SOS command names: `pstacks` = parallel stacks (grouped). If unavailable in your SOS build,
> use `clrstack -all` and group by hand. `dumpasync --stats` may also be `dumpasync -stats`.

## Decision table — signature ⇒ root cause

Work top to bottom; more than one can be true (they compound).

### A. .NET ThreadPool starvation  (ANALYSIS.md cause #1 — primary suspect)
- `threadpool` shows **Worker `Running` ≈ max, `Idle` 0, a non-empty work-item queue, CPU
  utilization LOW**.
- `counters-*.csv` (if collected): `threadpool-queue-length` rises and stays high while
  `threadpool-thread-count` is flat / climbs ~1/s, `cpu-usage` low.
- `pstacks` / `clrstack -all`: ThreadPool threads sitting in
  `ChunkedFileEntry.ExtractAsync` → `DeflateStream` inflate, or `fs.WriteAsync`/`FlushAsync`,
  or parked in `WaitForChunkAsync` / `EnsureChunkReadyAsync`.
- `dumpasync --stats`: many pending `ReadFileAsync` / `FileContentCache.ReadAsync` /
  chunk-wait state machines (work awaiting a thread that never comes).
- **Distinguish from CPU-bound:** if CPU utilization is ~100% with all cores in `DeflateStream`
  inflate, it's CPU saturation, not thread starvation — note that explicitly (different fix).

### B. WinFsp dispatcher-thread starvation  (cause #2)
- `pstacks` / `clrstack -all`: **N threads (N ≈ 4–16, the dispatcher count)** all parked in
  `WaitHandle.WaitOne` / `ManualResetEventSlim.Wait` reached from
  `…GetAwaiter().GetResult()` whose frames include
  `WinFsp.Native.FileSystemHost.OnOpen` / `OnGetFileInfo` / `OnGetSecurityByName` /
  `OnGetDirInfoByName` (these are reverse-P/Invoke `[UnmanagedCallersOnly]` thunks — you'll
  see the native→managed transition at the top of the stack).
- Below the `GetResult()` you'll see `ArchiveVirtualFileSystem.GetFileInfoAsync` →
  `ArchiveStructureCache.GetOrBuildAsync` (cold) or just an in-memory continuation waiting on
  the ThreadPool (cross-pool coupling — confirms B is gated by A).
- **Proof of B as the visible hang:** the count of such parked dispatcher threads ≈ the
  dispatcher pool size AND Photos' pending operation isn't running anywhere (its IRP can't be
  dispatched).

### C. Cross-pool coupling  (cause #3)
- Both A and B present, AND the dispatcher threads in B are blocked in `GetResult()` whose
  underlying task's continuation is in the ThreadPool queue (A). This is the expected combined
  picture; report it as "B is waiting on A."

### D. Disk-tier chunk-wait  (cause #4)
- `dumpasync`: tasks awaiting `ChunkedFileEntry.WaitForChunkAsync` /
  `ChunkedStream.EnsureChunkReadyAsync`, with the matching `ChunkedFileEntry.ExtractAsync`
  either **queued/not-running** (⇒ that's A) or blocked in `fs.WriteAsync`/`FlushAsync` on SMB.
- A thread parked in `ChunkedFileEntry.Dispose` → `ExtractionTask.Wait(5s)` confirms the
  cleanup-path 5-second block.

### Rule-outs (should be ABSENT — note if present, it changes the story)
- `syncblk`: heavy managed-monitor contention is **not** expected (the hot path uses
  `Lazy<Task>` / `Interlocked`, not `lock`). Its absence + GetResult()-parked threads confirms
  sync-over-async rather than a lock.
- Threads in `RarEntryExtractor` / `RarStructureBuilder` / `SharpCompress` doing synchronous
  `FileStream.Read` ⇒ a **RAR** archive is involved (not expected for the ZIP repro; note it).

## What to report back

1. **Verdict:** which cause(s) (A/B/C/D) the dumps confirm, with confidence.
2. **The smoking-gun stacks:** paste the representative parked stacks (dispatcher `GetResult()`
   frames; ThreadPool extraction/chunk frames), with the thread counts.
3. **`threadpool` line** (Running / Idle / queue / CPU%) and the counters CSV trend.
4. **The file Photos was stuck on** (from `diag\dumps\NOTES.txt` — the last `Read (miss)`).
5. **CPU-bound vs starvation** call (from CPU utilization).
6. Whether the recommended fixes in [`PROPOSED-FIX.md`](PROPOSED-FIX.md) match what you saw, and
   which one to apply first.
