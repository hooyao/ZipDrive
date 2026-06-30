# ZipDrive WinFsp hang — diagnostic kit

Self-contained kit to diagnose the post-WinFsp-migration hang where **Windows 11 Photos opens a
cached image fine, then goes unresponsive** while browsing a NAS-hosted ZIP (smooth under the old
Dokan build). Everything here is standalone — usable on a different test machine with no prior
context.

## Read in this order
1. **[`ANALYSIS.md`](ANALYSIS.md)** — what's going wrong and why (root cause, evidence, sources).
2. **[`CAPTURE.md`](CAPTURE.md)** — build the diagnostic exe, reproduce, capture dumps.
3. **[`ANALYZE.md`](ANALYZE.md)** — how to read the dumps (signature → root-cause table). Hand this
   to Claude Code on the test machine.
4. **[`PROPOSED-FIX.md`](PROPOSED-FIX.md)** — the fix (documented, not applied — this build must
   reproduce the bug).

## TL;DR root cause
The Dokan→WinFsp migration moved filesystem-callback work from Dokan's **large, on-demand-growing
native thread pool** onto two **small, fixed, never-grown** pools — the WinFsp dispatcher pool
(`[4,16]`, blocked synchronously by every metadata callback) and the **.NET ThreadPool** (never
raised via `SetMinThreads`; runs all read completions + `Task.Run` video extraction). On
high-latency SMB these saturate and the filesystem stalls; on NVMe it clears in 2–3 s. Photos
hangs *after* displaying the image because it then pre-reads the sibling files (incl. the video
still extracting from SMB), which hits the starved filesystem.

## Scripts (PowerShell)
| Script | Purpose |
|---|---|
| `install-tools.ps1` | Install `dotnet-dump/counters/trace/gcdump` global tools |
| `build-diag.ps1` | Build the non-AOT, JIT, symbol-rich `publish-jit\ZipDrive.exe` |
| `collect-counters.ps1` | Collect runtime/ThreadPool counters to CSV across the repro |
| `watch-counters.ps1` | Live TUI of the same counters |
| `collect-dump.ps1` | **Interval dump capture** during the hang (default 5×, 3 s apart) |
| `analyze-dump.ps1` | Run SOS analyses on every dump → `out\*.analysis.txt` |
| `collect-trace.ps1` | Optional EventPipe timeline trace (PerfView/VS) |

## Quick run
```powershell
.\diag\install-tools.ps1
.\diag\build-diag.ps1
# launch (fill in NAS path):
.\publish-jit\ZipDrive.exe --Mount:ArchiveDirectory="\\NAS\...\mixed.zip" --Mount:MountPoint="R:\" --Serilog:MinimumLevel:Default=Information
# in another terminal, start counters; reproduce in Explorer/Photos; the moment Photos hangs:
.\diag\collect-dump.ps1
# after recovery:
.\diag\analyze-dump.ps1
```

Outputs: dumps → `diag\dumps\`, reports/CSV/traces → `diag\out\` (both git-ignored).
