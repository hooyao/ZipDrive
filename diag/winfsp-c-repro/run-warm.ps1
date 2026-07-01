# run-warm.ps1 — warm-kernel-cache experiments (scenarios A and B).
# Mounts with --infinite-cache (FileInfoTimeout=FspTimeoutInfinity32) + --debug so we
# can count actual Read-callback dispatches from the repro's stderr.
#
# Usage:
#   .\run-warm.ps1 -Scenario A
#   .\run-warm.ps1 -Scenario B -Runs 3
#   .\run-warm.ps1 -Scenario B -Runs 3 -Finite   # control: finite timeout (no kernel cache)
param(
    [ValidateSet('A','B')] [string]$Scenario = 'B',
    [int]$Runs = 3,
    [switch]$Finite,       # use --timeout=1000 instead of --infinite-cache (control)
    [switch]$SlowAll       # scenario C flavor: all video reads slow
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $here
$fsptool = 'C:\Program Files (x86)\WinFsp\bin\fsptool-x64.exe'

$tag = "crepro" + ([guid]::NewGuid().ToString('N').Substring(0,6))
$prefix = "\$tag\share"
$root   = "\\$tag\share"

$cacheArgs = if ($Finite) { @('--timeout=1000') } else { @('--infinite-cache') }
$slowArgs  = if ($SlowAll) { @('--slowAll') } else { @() }
$allArgs = @("--prefix=$prefix",'--tailDelayMs=3000','--debug') + $cacheArgs + $slowArgs
$errFile = "$here\_warm.err"

Write-Host "=================================================================="
Write-Host "WARM EXPERIMENT: scenario $Scenario  cache=$(if($Finite){'FINITE(1000)'}else{'INFINITE'})  slowAll=$([bool]$SlowAll)"
Write-Host "  repro args : $($allArgs -join ' ')"
Write-Host "  root       : $root"
Write-Host "=================================================================="

$p = Start-Process -FilePath '.\winfsp-c-repro.exe' -ArgumentList $allArgs -PassThru `
        -RedirectStandardOutput "$here\_warm.out" -RedirectStandardError $errFile
try
{
    $ok = $false
    for ($i = 0; $i -lt 30; $i++)
    {
        Start-Sleep -Milliseconds 300
        if ($p.HasExited) { throw "repro exited early (code $($p.ExitCode)). See _warm.err" }
        if (Test-Path "$root\video.bin") { $ok = $true; break }
    }
    if (-not $ok) { throw "mount not reachable. _warm.err:`n$(Get-Content $errFile -Raw)" }

    Write-Host "mount.out:"; Get-Content "$here\_warm.out" | ForEach-Object { "  $_" }
    Write-Host ""

    $probeDll = if ($Scenario -eq 'A') { "$here\bin\Release\net10.0\probe-warm.dll" } else { "$here\bin\Release\net10.0\probe-warm.dll" }

    for ($r = 1; $r -le $Runs; $r++)
    {
        Write-Host "--- run $r/$Runs ---"
        # mark stderr position so we can count NEW Read-callbacks for THIS run
        $preLines = if (Test-Path $errFile) { (Get-Content $errFile).Count } else { 0 }

        # Unique tail offset per run (>= TAIL_START=32MB, page aligned): under infinite
        # cache this guarantees every run's tail read is a genuine cache MISS.
        $tailOffset = 33554432 + ($r * 1048576)   # 32MB + r*1MB

        $job = Start-Job -ScriptBlock {
            param($dll, $scen, $root, $off)
            & dotnet $dll $scen $root $off 2>&1
        } -ArgumentList $probeDll, $Scenario, $root, $tailOffset

        if (Wait-Job $job -Timeout 30) { Receive-Job $job | ForEach-Object { "  $_" } }
        else { Write-Host "  PROBE TIMED OUT (>30s)"; Stop-Job $job }
        Remove-Job $job -Force -ErrorAction SilentlyContinue

        Start-Sleep -Milliseconds 300
        # Count Read-callback dispatches that happened during this run (video.bin only).
        $post = if (Test-Path $errFile) { Get-Content $errFile } else { @() }
        $newLines = $post | Select-Object -Skip $preLines
        $readEnters = @($newLines | Where-Object { $_ -match 'Read ENTER video\.bin' })
        $headReads  = @($readEnters | Where-Object { $_ -match 'offset=0 ' })
        $slowReads  = @($readEnters | Where-Object { $_ -match 'SLOW' })
        Write-Host "  [FSD] video.bin Read-callback dispatches this run: total=$($readEnters.Count) head(offset0)=$($headReads.Count) slow=$($slowReads.Count)"
        Start-Sleep -Milliseconds 300
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
