<#
.SYNOPSIS
  Collect live runtime + ZipDrive counters to a CSV while you reproduce the hang.
  The decisive signal for the starvation hypothesis is `threadpool-queue-length`
  climbing while `threadpool-thread-count` rises only slowly (hill-climb), and
  `monitor-lock-contention-count` ticking up.

.EXAMPLE
  # Start this BEFORE you trigger the hang, let it run across the repro, Ctrl+C when done.
  .\diag\collect-counters.ps1

.EXAMPLE
  .\diag\collect-counters.ps1 -ProcId 12345 -RefreshSec 1
#>
param(
  [string]$ProcessName = 'ZipDrive',
  [int]$ProcId = 0,
  [int]$RefreshSec = 1,
  [string]$OutDir = "$PSScriptRoot\out"
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$csv = Join-Path $OutDir "counters-$stamp.csv"

$counters = @(
  'System.Runtime[cpu-usage,threadpool-thread-count,threadpool-queue-length,threadpool-completed-items-count,monitor-lock-contention-count,gc-heap-size,active-timer-count,exception-count]'
  'ZipDrive.Caching'
  'ZipDrive.WinFsp'
  'ZipDrive.Zip'
) -join ','

$target = if ($ProcId -gt 0) { @('-p', $ProcId) } else { @('-n', $ProcessName) }

Write-Host "Collecting counters to $csv  (Ctrl+C to stop)" -ForegroundColor Cyan
Write-Host "Watch: threadpool-queue-length (should spike during the hang)" -ForegroundColor Yellow

dotnet-counters collect @target `
  --refresh-interval $RefreshSec `
  --format csv `
  -o $csv `
  --counters $counters
