<#
.SYNOPSIS
  Install the .NET diagnostic global tools needed to capture/analyze ZipDrive dumps.
  Safe to re-run (skips already-installed tools).
#>
$ErrorActionPreference = 'Stop'

$tools = 'dotnet-dump', 'dotnet-counters', 'dotnet-trace', 'dotnet-gcdump'
foreach ($t in $tools) {
  $installed = (& dotnet tool list --global 2>$null) -match "^\s*$t\s"
  if ($installed) {
    Write-Host "$t already installed." -ForegroundColor DarkGray
  } else {
    Write-Host "Installing $t ..." -ForegroundColor Cyan
    dotnet tool install -g $t
  }
}

Write-Host ""
Write-Host "Make sure the global tools dir is on PATH: $env:USERPROFILE\.dotnet\tools" -ForegroundColor Yellow
Write-Host "(The scripts fall back to that path automatically if it isn't.)" -ForegroundColor DarkGray
