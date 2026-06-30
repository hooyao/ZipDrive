# Capture playbook — reproduce the hang and grab dumps

> Run this on the machine that can reach the NAS over SMB. Goal: capture ZipDrive process
> dumps **while Windows 11 Photos is unresponsive**, so the dumps in `diag\dumps\` can be
> analyzed (see [`ANALYZE.md`](ANALYZE.md)) to confirm the root cause in [`ANALYSIS.md`](ANALYSIS.md).

## 0. Prerequisites (one-time on the test machine)

- **.NET 10 SDK** — any `10.0.x` (repo `global.json` rolls forward by feature band).
- **WinFsp** installed — <https://winfsp.dev/rel/>
- This branch checked out.
- Diagnostic tools:
  ```powershell
  .\diag\install-tools.ps1
  ```
  (Installs `dotnet-dump`, `dotnet-counters`, `dotnet-trace`, `dotnet-gcdump` as global tools.)

## 1. Build the diagnostic (non-AOT, analyzable) build

```powershell
.\diag\build-diag.ps1
```
Produces `publish-jit\ZipDrive.exe` (JIT + full PDB symbols). **This build deliberately has no
fix — it must reproduce the hang.** If it prints `coreclr.dll present = JIT, NOT AOT` you're good.

## 2. Launch ZipDrive on the SMB archive

Open a **dedicated terminal** and run (fill in the real NAS path):

```powershell
.\publish-jit\ZipDrive.exe `
  --Mount:ArchiveDirectory="\\NAS\share\path\to\mixed-images-and-videos.zip" `
  --Mount:MountPoint="R:\" `
  --Serilog:MinimumLevel:Default=Information
```

- `Information` logging prints `Read (miss): <archive>:<file>` lines. The **last miss before
  the hang tells us which sibling Photos was pre-reading** (expected: the video or an uncached
  neighbour). Keep this terminal visible.
- Leave it running. Confirm `R:\` is mounted and the archive shows as a folder.

## 3. (Recommended) Start counters collecting in a 2nd terminal

```powershell
.\diag\collect-counters.ps1
```
Writes `diag\out\counters-*.csv`. Leave it running across the whole repro; Ctrl+C at the end.
The starvation tell is `threadpool-queue-length` climbing while `threadpool-thread-count`
barely moves and `cpu-usage` stays low.

## 4. Reproduce

1. In Explorer open `R:\…\mixed.zip\` and switch to **Large/Extra-large icons** so Windows
   starts generating thumbnails (images **and** videos).
2. Let a few thumbnails render (this starts the slow SMB video extraction in the background).
3. Double-click an image **that already shows a thumbnail**. Photos opens and **displays** it…
4. …then the Photos window **hangs / goes unresponsive** (title bar shows "Not Responding").

## 5. Capture dumps DURING the hang (interval capture)

The moment Photos is unresponsive, in a 3rd terminal run:

```powershell
.\diag\collect-dump.ps1
```
- Default: **5 `Heap` dumps, 3 s apart** (~12 s window) → `diag\dumps\`.
- For a longer freeze: `.\diag\collect-dump.ps1 -Count 10 -IntervalSec 2`.
- Spanning the freeze **and** the recovery is ideal: identical stacks across dumps = a hard
  block; stacks that shift = a slow drain. Either way we see what's holding the threads.

Note the **last `Read (miss)` line** in the ZipDrive terminal at the moment of the hang — copy
it into `diag\dumps\NOTES.txt` (which file Photos was stuck on).

## 6. Wind down

- Wait for Photos to recover, then Ctrl+C the counters terminal (step 3).
- Optionally also grab a timeline trace on a second repro: `.\diag\collect-trace.ps1 -DurationSec 30`
  (trigger the hang inside the 30 s window) → `diag\out\*.nettrace` (open in PerfView/VS).
- Stop ZipDrive with **Ctrl+C** in its terminal (this unmounts `R:\` cleanly).

## 7. Analyze

On this same machine (the dumps must be analyzed where they were captured — matching runtime):

```powershell
.\diag\analyze-dump.ps1        # analyzes every dump in diag\dumps -> diag\out\*.analysis.txt
```

Then hand `diag\out\*.analysis.txt`, `diag\dumps\NOTES.txt`, the counters CSV, and
[`ANALYZE.md`](ANALYZE.md) + [`ANALYSIS.md`](ANALYSIS.md) to **Claude Code on this machine** and
ask it to follow `ANALYZE.md`. (Claude Code can also run `dotnet-dump analyze` itself.)

## Artifacts to collect for the analyzer
- `diag\dumps\*.dmp` (the dumps)
- `diag\out\*.analysis.txt` (pre-rendered SOS reports)
- `diag\out\counters-*.csv` (ThreadPool/runtime timeline)
- `diag\dumps\NOTES.txt` (the last `Read (miss)` file + rough timing)
- The ZipDrive console log (copy/paste or redirect to a file)
