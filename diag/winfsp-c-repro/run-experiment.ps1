# run-experiment.ps1 — mount the C repro with a UNIQUE net prefix, run the probe
# N times, then guarantee cleanup (kill proc + verify no residual volume).
#
# Usage:
#   .\run-experiment.ps1 -Label "blocking-nocache" -ReproArgs @('--tailDelayMs=3000') -Runs 4
param(
    [string]$Label = "run",
    [string[]]$ReproArgs = @('--tailDelayMs=3000'),
    [int]$Runs = 4
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $here
$fsptool = 'C:\Program Files (x86)\WinFsp\bin\fsptool-x64.exe'

# Unique prefix per experiment invocation to dodge redirector negative-cache.
$tag = "crepro" + ([guid]::NewGuid().ToString('N').Substring(0,6))
$prefix = "\$tag\share"
$root   = "\\$tag\share"

$allArgs = @("--prefix=$prefix") + $ReproArgs
Write-Host "=================================================================="
Write-Host "EXPERIMENT: $Label"
Write-Host "  repro args : $($allArgs -join ' ')"
Write-Host "  root       : $root"
Write-Host "  runs       : $Runs"
Write-Host "=================================================================="

$p = Start-Process -FilePath '.\winfsp-c-repro.exe' -ArgumentList $allArgs -PassThru `
        -RedirectStandardOutput "$here\_mount.out" -RedirectStandardError "$here\_mount.err"
try
{
    # Wait (bounded) for the mount to be reachable.
    $ok = $false
    for ($i = 0; $i -lt 30; $i++)
    {
        Start-Sleep -Milliseconds 300
        if ($p.HasExited) { throw "repro exited early (code $($p.ExitCode)). See _mount.err" }
        if (Test-Path "$root\video.bin") { $ok = $true; break }
    }
    if (-not $ok) { throw "mount not reachable within timeout. _mount.err:`n$(Get-Content "$here\_mount.err" -Raw)" }

    Write-Host "mount.out:"; Get-Content "$here\_mount.out" | ForEach-Object { "  $_" }
    Write-Host ""

    for ($r = 1; $r -le $Runs; $r++)
    {
        Write-Host "--- run $r/$Runs ---"
        $job = Start-Job -ScriptBlock {
            param($dll, $root)
            & dotnet $dll $root 2>&1
        } -ArgumentList "$here\bin\Release\net10.0\probe.dll", $root

        if (Wait-Job $job -Timeout 30)
        {
            Receive-Job $job | ForEach-Object { "  $_" }
        }
        else
        {
            Write-Host "  PROBE TIMED OUT (>30s) — killing job"
            Stop-Job $job
        }
        Remove-Job $job -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 500
    }
}
finally
{
    Write-Host ""
    Write-Host "cleanup: killing repro pid $($p.Id)"
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    $vols = & $fsptool lsvol 2>&1
    $residual = $vols | Where-Object { $_ -match [regex]::Escape($tag) }
    if ($residual) { Write-Host "WARNING: residual volume: $residual" }
    else { Write-Host "cleanup OK: no residual '$tag' volume" }
}
