<#
.SYNOPSIS
  Run the key SOS analyses on hang dump(s) non-interactively and write a text report per
  dump. With no -Dump argument it analyzes EVERY .dmp in diag\dumps.

.WHAT-TO-LOOK-FOR  (full decision table in diag\ANALYZE.md)
  * threadpool            -> worker "Running" near max, "Idle" 0, a non-empty work queue,
                             CPU utilization LOW = ThreadPool starvation (not CPU-bound).
  * pstacks / clrstack -all -> WinFsp dispatcher threads parked in GetAwaiter().GetResult()
                             under FileSystemHost.OnOpen / OnGetFileInfo / OnGetSecurityByName
                             / OnGetDirInfoByName  == dispatcher-thread starvation.
                          -> ThreadPool threads in ChunkedFileEntry.ExtractAsync / DeflateStream
                             inflate / WaitForChunkAsync == extraction occupying the pool.
  * dumpasync --stats     -> many pending ReadFileAsync / GetOrBuildAsync / chunk-wait state
                             machines = completed/awaiting work that can't get a thread.
  * syncblk               -> managed-lock contention (expected to be ~empty here; its absence
                             plus GetResult()-parked threads confirms sync-over-async, not a lock).

.EXAMPLE
  .\diag\analyze-dump.ps1                 # analyze all dumps in diag\dumps
  .\diag\analyze-dump.ps1 -Dump .\diag\dumps\zipdrive-...-03-XXXX.dmp
#>
param(
  [string]$Dump,
  [string]$DumpDir = "$PSScriptRoot\dumps",
  [string]$OutDir = "$PSScriptRoot\out"
)

$ErrorActionPreference = 'Stop'

function Resolve-DotnetTool([string]$name) {
  $cmd = Get-Command $name -ErrorAction SilentlyContinue
  if ($cmd) { return $cmd.Source }
  $fallback = Join-Path $env:USERPROFILE ".dotnet\tools\$name.exe"
  if (Test-Path $fallback) { return $fallback }
  throw "$name not found. Run diag\install-tools.ps1"
}
$dotnetDump = Resolve-DotnetTool 'dotnet-dump'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$dumps = if ($Dump) { @($Dump) } else {
  @(Get-ChildItem -Path $DumpDir -Filter *.dmp -ErrorAction SilentlyContinue | Sort-Object Name | ForEach-Object FullName)
}
if ($dumps.Count -eq 0) { throw "No dumps found. Looked in: $DumpDir (or pass -Dump <file>)." }

# Ordered: cheap overview first, verbose per-thread stacks last.
$cmds = @('clrthreads', 'threadpool', 'dumpasync --stats', 'pstacks', 'syncblk', 'clrstack -all', 'exit')

foreach ($d in $dumps) {
  $base = [IO.Path]::GetFileNameWithoutExtension($d)
  $report = Join-Path $OutDir "$base.analysis.txt"
  Write-Host "Analyzing $(Split-Path $d -Leaf) -> $(Split-Path $report -Leaf)" -ForegroundColor Cyan

  $args = @($d)
  foreach ($c in $cmds) { $args += @('-c', $c) }
  & $dotnetDump analyze @args *>&1 | Tee-Object -FilePath $report | Out-Null
}

Write-Host "Reports written to $OutDir" -ForegroundColor Green
