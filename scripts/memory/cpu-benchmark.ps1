#requires -Version 5.1
<#
.SYNOPSIS
  Measures idle CPU cost of each rendering mode, so the memory win can be traded off
  honestly against CPU. Software rasterization moves drawing from the GPU to the CPU;
  this quantifies how much that actually costs for carton's static UI.
#>
param(
    [string]$ExePath = "$PSScriptRoot\..\..\src\carton.GUI\bin\Release\net10.0\carton.exe",
    [int]$WarmupSeconds = 20,
    [int]$SampleSeconds = 30
)

$ErrorActionPreference = 'Stop'

function Stop-Carton {
    Get-Process carton -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
}

$cpuCount = [Environment]::ProcessorCount
$results = @()

foreach ($mode in @('gpu', 'software')) {
    Stop-Carton
    $env:CARTON_RENDERING_MODE = $mode
    $proc = Start-Process -FilePath $ExePath -PassThru
    Start-Sleep -Seconds $WarmupSeconds

    $p = Get-Process -Id $proc.Id -ErrorAction SilentlyContinue
    if (-not $p) { Write-Warning "[$mode] exited early"; continue }

    $cpuStart = $p.TotalProcessorTime
    $wallStart = Get-Date
    Start-Sleep -Seconds $SampleSeconds
    $p.Refresh()
    $cpuEnd = $p.TotalProcessorTime
    $wallEnd = Get-Date

    $cpuMs = ($cpuEnd - $cpuStart).TotalMilliseconds
    $wallMs = ($wallEnd - $wallStart).TotalMilliseconds
    # Percent of ONE core, and of the whole machine.
    $pctOneCore = [math]::Round(100 * $cpuMs / $wallMs, 2)
    $pctMachine = [math]::Round($pctOneCore / $cpuCount, 3)

    $results += [pscustomobject]@{
        Mode           = $mode
        CpuMsPerSample = [math]::Round($cpuMs, 1)
        PctOfOneCore   = $pctOneCore
        PctOfMachine   = $pctMachine
        WorkingSetMB   = [math]::Round($p.WorkingSet64 / 1MB, 1)
        PrivateMB      = [math]::Round($p.PrivateMemorySize64 / 1MB, 1)
    }

    Stop-Carton
}

Remove-Item Env:\CARTON_RENDERING_MODE -ErrorAction SilentlyContinue

Write-Host ''
Write-Host "=== idle CPU cost by rendering mode (${SampleSeconds}s sample, $cpuCount logical cores) ==="
$results | Format-Table -AutoSize | Out-String -Width 140 | Write-Host
