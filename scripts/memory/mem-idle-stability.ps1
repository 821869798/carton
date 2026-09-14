#requires -Version 5.1
<#
.SYNOPSIS
  Long-run idle stability check: does carton's memory creep while simply sitting in the
  tray/window? Samples at a fixed interval and reports the trend.

.NOTES
  Deliberately does NOT touch UI Automation.

  An earlier version of this script drove navigation through UIA SelectionItemPattern
  and appeared to show the working set growing 19 MB -> 65 MB with no recovery. That was
  a measurement artifact: Avalonia's NavigationView does not respond to UIA Select(), so
  no navigation ever occurred, and the growth came from the UIA provider tree being
  materialized inside the target process by the repeated FindAll/pattern queries.

  A control run with no UIA access at all shows the opposite: working set settles around
  26 MB and private bytes DECLINE (40.3 -> 38.7 MB over ~2 minutes).

  Conclusion: measure with process counters only. Any in-process instrumentation
  (UIA, debuggers, profilers with allocation tracking) inflates the very numbers being
  measured. To exercise real page churn, a human must click through the pages, or the
  app needs a test hook.
#>
param(
    [string]$ExePath = "$PSScriptRoot\..\..\src\carton.GUI\bin\Release\net10.0\carton.exe",
    [string]$Mode = 'software',
    [int]$Minutes = 10,
    [int]$IntervalSeconds = 30,
    [string]$Label = ''
)

$ErrorActionPreference = 'Stop'

function Stop-Carton {
    Get-Process carton -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
}

Stop-Carton
$env:CARTON_RENDERING_MODE = $Mode
$proc = Start-Process -FilePath $ExePath -PassThru
Write-Host "launched pid=$($proc.Id) mode=$Mode; sampling every ${IntervalSeconds}s for ${Minutes}min"

$samples = @()
$deadline = (Get-Date).AddMinutes($Minutes)
$start = Get-Date

while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds $IntervalSeconds
    $p = Get-Process -Id $proc.Id -ErrorAction SilentlyContinue
    if (-not $p) { Write-Warning 'process exited'; break }
    $p.Refresh()

    $elapsed = [math]::Round(((Get-Date) - $start).TotalSeconds)
    $row = [pscustomobject]@{
        ElapsedS     = $elapsed
        WorkingSetMB = [math]::Round($p.WorkingSet64 / 1MB, 2)
        PrivateMB    = [math]::Round($p.PrivateMemorySize64 / 1MB, 2)
        Threads      = $p.Threads.Count
        Handles      = $p.HandleCount
    }
    $samples += $row
    Write-Host ("  t={0,5}s  WS={1,7} MB  Priv={2,7} MB  thr={3}  hnd={4}" -f $row.ElapsedS, $row.WorkingSetMB, $row.PrivateMB, $row.Threads, $row.Handles)
}

Stop-Carton
Remove-Item Env:\CARTON_RENDERING_MODE -ErrorAction SilentlyContinue

if ($samples.Count -lt 2) { Write-Warning 'not enough samples'; exit 0 }

$first = $samples[0]
$last = $samples[-1]

Write-Host ''
Write-Host "=== idle stability: $Mode $Label ($($samples.Count) samples) ==="
Write-Host ("WorkingSet  {0} -> {1} MB  (delta {2})" -f $first.WorkingSetMB, $last.WorkingSetMB, [math]::Round($last.WorkingSetMB - $first.WorkingSetMB, 2))
Write-Host ("PrivateBytes {0} -> {1} MB  (delta {2})" -f $first.PrivateMB, $last.PrivateMB, [math]::Round($last.PrivateMB - $first.PrivateMB, 2))
Write-Host ("Handles      {0} -> {1}       (delta {2})" -f $first.Handles, $last.Handles, ($last.Handles - $first.Handles))
Write-Host ("Threads      {0} -> {1}" -f $first.Threads, $last.Threads)

$pvMax = ($samples | Measure-Object -Property PrivateMB -Maximum).Maximum
Write-Host ("PrivateBytes peak {0} MB" -f $pvMax)
