<#
.SYNOPSIS
  Optional EventPipe trace covering the repro window. Complements the dump:
  the dump shows WHERE threads are blocked at one instant; the trace shows the
  timeline (ThreadPool starvation events, contention, task scheduling, GC).

  Open the resulting .nettrace in PerfView or Visual Studio. The
  'Microsoft-DotNETCore-SampleProfiler' gives CPU stacks; the runtime keyword
  0x10000 (Threading) + Contention surface the ThreadPool starvation events.

.EXAMPLE
  .\diag\collect-trace.ps1 -DurationSec 30
  # trigger the hang within the 30s window
#>
param(
  [string]$ProcessName = 'ZipDrive',
  [int]$ProcId = 0,
  [int]$DurationSec = 30,
  [string]$OutDir = "$PSScriptRoot\out"
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$out = Join-Path $OutDir "zipdrive-$stamp.nettrace"

$target = if ($ProcId -gt 0) { @('-p', $ProcId) } else { @('-n', $ProcessName) }

# cpu-sampling profile + Threading/Contention/Tasks keywords.
#   Microsoft-Windows-DotNETRuntime  0x14000  = Threading(0x10000) | Contention(0x4000)
#   System.Threading.Tasks.TplEventSource 0x1 = TaskTransfer/parallelism
$providers = 'Microsoft-Windows-DotNETRuntime:0x14000:4,System.Threading.Tasks.TplEventSource:0x1:4'

Write-Host "Tracing $DurationSec s -> $out  (trigger the hang now)" -ForegroundColor Cyan
dotnet-trace collect @target `
  --profile cpu-sampling `
  --duration ("00:00:{0:D2}" -f $DurationSec) `
  --providers $providers `
  -o $out
Write-Host "Trace written: $out  (open in PerfView / Visual Studio)" -ForegroundColor Green
