<#
.SYNOPSIS
  Live TUI of the counters that matter for the dispatcher/ThreadPool starvation hang.
  Use this to *watch in real time* while you click around in Explorer.

.NOTES
  KEY SIGNALS during the hang:
    threadpool-queue-length     -> climbs and stays high  = work queued, pool can't keep up
    threadpool-thread-count     -> creeps up ~1/sec       = hill-climb fighting starvation
    monitor-lock-contention     -> rising                 = lock contention
    cpu-usage                   -> LOW while hung          = threads blocked, not busy (starvation, not CPU)
#>
param(
  [string]$ProcessName = 'ZipDrive',
  [int]$ProcId = 0,
  [int]$RefreshSec = 1
)

$ErrorActionPreference = 'Stop'
$counters = @(
  'System.Runtime[cpu-usage,threadpool-thread-count,threadpool-queue-length,threadpool-completed-items-count,monitor-lock-contention-count]'
  'ZipDrive.WinFsp'
  'ZipDrive.Caching'
) -join ','

$target = if ($ProcId -gt 0) { @('-p', $ProcId) } else { @('-n', $ProcessName) }
dotnet-counters monitor @target --refresh-interval $RefreshSec --counters $counters
