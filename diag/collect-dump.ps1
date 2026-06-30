<#
.SYNOPSIS
  Capture a series of process dumps of ZipDrive at a fixed interval. Designed for a
  TRANSIENT hang that recovers on its own: start this the moment the image viewer goes
  unresponsive, and it grabs several snapshots spanning the freeze + the recovery so the
  analyzer can see exactly which threads were blocked and on what.

.DESCRIPTION
  Default: 5 dumps, 3 seconds apart, Heap type (full managed heap + thread stacks — enough
  for `dumpasync`, `pstacks`, `clrstack`, `threadpool`; much smaller than a Full dump).

  Start it RIGHT WHEN Photos hangs. If the hang is longer, raise -Count. If you want a
  longer window, raise -Count and/or -IntervalSec.

.EXAMPLE
  .\diag\collect-dump.ps1
  # 5 Heap dumps, 3s apart (≈12s window)

.EXAMPLE
  .\diag\collect-dump.ps1 -Count 10 -IntervalSec 2
  # 10 dumps, 2s apart (≈18s window) — for a longer hang

.EXAMPLE
  .\diag\collect-dump.ps1 -DumpType Full
  # Full dumps (larger, ~hundreds of MB each) if Heap analysis is insufficient
#>
param(
  [string]$ProcessName = 'ZipDrive',
  [int]$ProcId = 0,
  [int]$Count = 5,
  [int]$IntervalSec = 3,
  [ValidateSet('Heap','Full','Mini','Triage')][string]$DumpType = 'Heap',
  [string]$OutDir = "$PSScriptRoot\dumps"
)

$ErrorActionPreference = 'Stop'

function Resolve-DotnetTool([string]$name) {
  $cmd = Get-Command $name -ErrorAction SilentlyContinue
  if ($cmd) { return $cmd.Source }
  $fallback = Join-Path $env:USERPROFILE ".dotnet\tools\$name.exe"
  if (Test-Path $fallback) { return $fallback }
  throw "$name not found. Install with: dotnet tool install -g $name  (see diag\install-tools.ps1)"
}

$dotnetDump = Resolve-DotnetTool 'dotnet-dump'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

if ($ProcId -le 0) {
  $procs = @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue)
  if ($procs.Count -eq 0) { throw "Process '$ProcessName' not found. Start ZipDrive first, or pass -ProcId <pid>." }
  if ($procs.Count -gt 1) { Write-Warning "Multiple '$ProcessName' processes; using PID $($procs[0].Id). Pass -ProcId to choose." }
  $ProcId = $procs[0].Id
}

$session = Get-Date -Format 'yyyyMMdd-HHmmss'
Write-Host "Capturing $Count '$DumpType' dump(s) of PID $ProcId every $IntervalSec s -> $OutDir" -ForegroundColor Cyan

for ($i = 1; $i -le $Count; $i++) {
  $stamp = Get-Date -Format 'HHmmss'
  $out = Join-Path $OutDir "zipdrive-$session-$('{0:D2}' -f $i)-$stamp.dmp"
  Write-Host "[$i/$Count] $(Get-Date -Format 'HH:mm:ss')  collecting -> $(Split-Path $out -Leaf)" -ForegroundColor Yellow
  & $dotnetDump collect -p $ProcId --type $DumpType -o $out
  if ($i -lt $Count) { Start-Sleep -Seconds $IntervalSec }
}

Write-Host "Done. $Count dump(s) in $OutDir" -ForegroundColor Green
Write-Host "Analyze with:  .\diag\analyze-dump.ps1        (analyzes every dump in diag\dumps)" -ForegroundColor Green
Write-Host "Or hand this folder + diag\ANALYZE.md to Claude Code on this machine." -ForegroundColor Green
