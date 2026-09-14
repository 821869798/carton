#requires -Version 5.1
<#
.SYNOPSIS
  Repeated-sample A/B for a single rendering mode, so small deltas (GC flags, globalization)
  can be judged against run-to-run variance instead of a single noisy sample.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts/memory/mem-repeat.ps1 -Mode software -Runs 5
#>
param(
    [string]$ExePath = "$PSScriptRoot\..\..\src\carton.GUI\bin\Release\net10.0\carton.exe",
    [string]$Mode = 'software',
    [int]$Runs = 5,
    [int]$SettleSeconds = 25,
    [string]$Label = ''
)

$ErrorActionPreference = 'Stop'

function Stop-Carton {
    Get-Process carton -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
}

$samples = @()

for ($i = 1; $i -le $Runs; $i++) {
    Stop-Carton
    $env:CARTON_RENDERING_MODE = $Mode
    $proc = Start-Process -FilePath $ExePath -PassThru
    Start-Sleep -Seconds $SettleSeconds

    $p = Get-Process -Id $proc.Id -ErrorAction SilentlyContinue
    if (-not $p) { Write-Warning "run $i exited early"; continue }
    $p.Refresh()

    $samples += [pscustomobject]@{
        Run          = $i
        WorkingSetMB = [math]::Round($p.WorkingSet64 / 1MB, 2)
        PrivateMB    = [math]::Round($p.PrivateMemorySize64 / 1MB, 2)
        Threads      = $p.Threads.Count
    }
    Write-Host ("  run {0}: WS={1} MB  Priv={2} MB  threads={3}" -f $i, $samples[-1].WorkingSetMB, $samples[-1].PrivateMB, $samples[-1].Threads)
}

Stop-Carton
Remove-Item Env:\CARTON_RENDERING_MODE -ErrorAction SilentlyContinue

$ws = $samples | Measure-Object -Property WorkingSetMB -Average -Minimum -Maximum
$pv = $samples | Measure-Object -Property PrivateMB -Average -Minimum -Maximum

Write-Host ''
Write-Host "=== $Mode $Label ($($samples.Count) runs) ==="
Write-Host ("WorkingSet  avg={0} MB  min={1}  max={2}" -f [math]::Round($ws.Average,2), $ws.Minimum, $ws.Maximum)
Write-Host ("PrivateBytes avg={0} MB  min={1}  max={2}" -f [math]::Round($pv.Average,2), $pv.Minimum, $pv.Maximum)
Write-Host ("Threads      avg={0}" -f [math]::Round((($samples | Measure-Object -Property Threads -Average).Average),1))
