# Proposed fix

> **Documented, not applied.** The build in this branch must reproduce the bug so we can
> capture the dumps. Apply these on a separate branch after the dumps confirm the cause
> ([`ANALYSIS.md`](ANALYSIS.md), [`ANALYZE.md`](ANALYZE.md)).

The fix has two layers: **quick mitigations** (one-liners, prove the diagnosis immediately and
relieve the hang) and **proper fixes** (remove the design flaw). Recommended order: ship the
mitigations, confirm the hang is gone, then land the proper fix.

## Quick mitigations (low risk, do first — they also confirm the diagnosis)

### M1 — Raise the .NET ThreadPool floor  *(addresses cause #1/#3)*
At the very top of `Main` in `src/ZipDrive.Cli/Program.cs`, before the host is built:
```csharp
int min = Math.Max(64, Environment.ProcessorCount * 4);
ThreadPool.SetMinThreads(min, min);
```
Removes the ~1-thread/sec hill-climb delay so read continuations, chunk-ready signals, and
`Task.Run` extraction get threads immediately. **If this alone makes the hang disappear, cause
#1 (ThreadPool starvation) is confirmed.**

> Zero-rebuild A/B variant: instead of editing code you can add to `publish-jit\ZipDrive.runtimeconfig.json`
> under `configProperties`: `"System.Threading.ThreadPool.MinThreads": 256`, relaunch, and compare.

### M2 — Raise the WinFsp dispatcher thread count  *(addresses cause #2)*
In `src/ZipDrive.Infrastructure.FileSystem/WinFspHostedService.cs:157`, change
`_host.Mount(mountManagerPoint)` to use the existing `MountEx` overload with a generous count:
```csharp
int result = _host.MountEx(mountManagerPoint, threadCount: 64);
```
(and the DefineDosDevice fallback at `:173` likewise). Gives many more dispatcher threads so an
SMB metadata burst can't pin them all. **If M1+M2 together remove the hang, the starvation
class is confirmed.**

## Proper fixes (durable)

### P0 — Make the metadata callbacks non-blocking (`STATUS_PENDING`)  *(root fix for cause #2)*
In the `WinFsp.Native` host (`FileSystemHost.cs`), give `OnOpen` (`:402`), `OnGetFileInfo`
(`:707`), `OnGetSecurityByName` (`:366`), `OnGetDirInfoByName` (`:866`), `OnCreate` (`:387`),
and `OnFlush` (`:692`) the **same `STATUS_PENDING` + `ContinueWith(... FspFileSystemSendResponse)`**
pattern already used by `OnRead`/`OnWrite`/`OnReadDirectory` (`:488-676`). The adapter already
returns `ValueTask` for all of them, so no adapter change is needed. This releases the dispatcher
thread at the first incomplete `await`, so a cold SMB call can never pin a dispatcher thread.
*(This change lives in the `WinFsp.Native` package source; coordinate with that repo.)*

### P1 — Don't let extraction starve the shared pool  *(root fix for cause #1)*
- Bound concurrent **disk-tier extractions** and run them on a dedicated, bounded scheduler
  instead of the shared `ThreadPool` (`ChunkedDiskStorageStrategy.cs:116` `Task.Run`), so a few
  slow SMB video extractions can't drain the pool that read completions depend on.
- For **RAR** (latent; not the ZIP repro): `RarEntryExtractor.cs:46-48` and
  `RarStructureBuilder.cs:68,86` do blocking synchronous SMB reads on ThreadPool threads —
  gate them behind a small semaphore (max 2–4 concurrent) or a dedicated thread set.

### P1 — Drop `ExecuteSynchronously` on the send-response continuation  *(reduces tail latency)*
`FileSystemHost.cs:550,618,671` run `FspFileSystemSendResponse` inline under
`TaskContinuationOptions.ExecuteSynchronously`, hijacking the completing IOCP/ThreadPool thread
for the native syscall before it can pick up the next chunk/read. Consider removing it (or
posting to a dedicated responder). Small win, but stops stealing the "next-chunk" thread.

### P2 — Pre-warm structure / harden the dormant blocks
- Pre-build `ArchiveStructure` at discovery/mount so the first metadata call over SMB doesn't
  pay the cold Central-Directory parse on a dispatcher thread.
- `ChunkedStream.cs:106-128` — the synchronous `Read`/`Read(Span)` overloads do
  `.GetAwaiter().GetResult()` (dormant today because `SynchronousIo=false`). Throw
  `NotSupportedException` or make them non-blocking so a future sync consumer can't reintroduce
  the block.
- `ChunkedFileEntry.cs:240` — `ExtractionTask.Wait(5s)` in `Dispose()` blocks the
  eviction/cleanup thread up to 5 s; make disposal async.

## Why this restores Dokan's smoothness

Under Dokan the same blocking I/O ran on Dokan's large, on-demand-growing **native** dispatcher
pool and never touched the .NET ThreadPool, so it merely felt slow under SMB latency rather than
hanging. The fixes either (a) move the work back onto adequately-sized pools (M2, P0), or (b)
stop the shared .NET ThreadPool from being drained/hill-climb-starved (M1, P1). Together they
reproduce Dokan's behavior while keeping WinFsp's Native-AOT benefit.

## Key files (quick index)
- `src/ZipDrive.Cli/Program.cs` — M1 (`SetMinThreads`)
- `src/ZipDrive.Infrastructure.FileSystem/WinFspHostedService.cs:157,173` — M2 (`MountEx threadCount`)
- `WinFsp.Native/FileSystemHost.cs:366,387,402,692,707,866` (blocking) vs `:488-676` (the
  `STATUS_PENDING` template) — P0
- `src/ZipDrive.Infrastructure.Caching/ChunkedDiskStorageStrategy.cs:116`,
  `ChunkedFileEntry.cs:52,240`, `ChunkedStream.cs:106-128` — P1/P2
- `src/ZipDrive.Infrastructure.Archives.Rar/RarEntryExtractor.cs:46-48`,
  `RarStructureBuilder.cs:68,86` — P1 (RAR)
