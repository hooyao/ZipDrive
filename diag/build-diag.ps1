<#
.SYNOPSIS
  Build the DIAGNOSTIC build of ZipDrive: a non-AOT, JIT, self-contained, symbol-rich
  executable that dotnet-dump / dotnet-trace / dotnet-counters can fully analyze.

.WHY
  The release build is Native AOT, which gives degraded/incomplete managed stacks under
  the diagnostics tools. This build keeps the EXACT same behavior (so it reproduces the
  hang) but runs on CoreCLR/JIT with full PDB symbols, so dumps show real method names,
  async state machines, and thread stacks.

  This build intentionally does NOT contain any fix — it must reproduce the bug.

.OUTPUT
  publish-jit\ZipDrive.exe  (+ runtime DLLs + ZipDrive.pdb + appsettings.jsonc)

.PREREQS
  - .NET 10 SDK (this repo's global.json pins 10.0.103 with rollForward latestFeature,
    so any installed 10.0.x SDK works).
  - WinFsp installed on the machine that RUNS it: https://winfsp.dev/rel/
#>
param(
  [ValidateSet('win-x64','win-arm64')][string]$Rid = 'win-x64',
  [string]$OutDir = "$PSScriptRoot\..\publish-jit"
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path "$PSScriptRoot\.."
$csproj = Join-Path $repo 'src\ZipDrive.Cli\ZipDrive.Cli.csproj'

Write-Host "Building diagnostic (non-AOT, JIT, self-contained) ZipDrive [$Rid] -> $OutDir" -ForegroundColor Cyan

# -p:PublishAot=false overrides the csproj default (which is true for release).
dotnet publish $csproj `
  -c Release -r $Rid --self-contained true `
  -p:PublishAot=false -p:PublishSingleFile=false -p:PublishTrimmed=false `
  -p:DebugType=portable -p:DebugSymbols=true `
  -o $OutDir

if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)" }

$exe = Join-Path $OutDir 'ZipDrive.exe'
$pdb = Join-Path $OutDir 'ZipDrive.pdb'
$coreclr = Join-Path $OutDir 'coreclr.dll'
Write-Host ""
Write-Host "Build OK." -ForegroundColor Green
Write-Host ("  exe     : {0}" -f $exe)
Write-Host ("  symbols : {0}  (present = analyzable)" -f $pdb)
Write-Host ("  coreclr : {0}  (present = JIT, NOT AOT — good)" -f $coreclr)
if (-not (Test-Path $coreclr)) {
  Write-Warning "coreclr.dll missing — this may be an AOT build. Diagnostics will be degraded."
}
