#requires -Version 5.1
<#
.SYNOPSIS
  A/B memory benchmark for carton: launches the app under each rendering mode,
  waits for it to settle, and reports working set / private bytes / threads /
  GPU-module footprint.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts/memory/mem-benchmark.ps1
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts/memory/mem-benchmark.ps1 -SettleSeconds 45
#>
param(
    [string]$ExePath = "$PSScriptRoot\..\..\src\carton.GUI\bin\Release\net10.0\carton.exe",
    [int]$SettleSeconds = 30,
    [string[]]$Modes = @('gpu', 'software')
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $ExePath)) {
    Write-Error "carton.exe not found at $ExePath - build Release first."
    exit 1
}

function Stop-Carton {
    Get-Process carton -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
}

$gpuModulePattern = 'nvwgf2umx|nvgpucomp|nvldumdx|NvMemMap|nvppex|nvspcap|av_libGLESv2|d3dcompiler|d3d11|dxgi|atio|amdvlk|igd|opengl32|vulkan'

$results = @()

foreach ($mode in $Modes) {
    Stop-Carton

    $env:CARTON_RENDERING_MODE = $mode
    $proc = Start-Process -FilePath $ExePath -PassThru
    Write-Host "[$mode] launched pid=$($proc.Id), settling ${SettleSeconds}s..."
    Start-Sleep -Seconds $SettleSeconds

    $p = Get-Process -Id $proc.Id -ErrorAction SilentlyContinue
    if (-not $p) {
        Write-Warning "[$mode] process exited early"
        continue
    }
    $p.Refresh()

    $gpuBytes = 0
    $gpuModules = @()
    foreach ($m in $p.Modules) {
        if ($m.ModuleName -match $gpuModulePattern) {
            $gpuBytes += $m.ModuleMemorySize
            $gpuModules += $m.ModuleName
        }
    }

    $results += [pscustomobject]@{
        Mode          = $mode
        WorkingSetMB  = [math]::Round($p.WorkingSet64 / 1MB, 1)
        PrivateMB     = [math]::Round($p.PrivateMemorySize64 / 1MB, 1)
        Threads       = $p.Threads.Count
        Handles       = $p.HandleCount
        Modules       = $p.Modules.Count
        GpuModuleMB   = [math]::Round($gpuBytes / 1MB, 1)
        GpuModules    = $gpuModules.Count
    }

    Stop-Carton
}

Remove-Item Env:\CARTON_RENDERING_MODE -ErrorAction SilentlyContinue

Write-Host ''
Write-Host '=== carton memory benchmark ==='
$results | Format-Table -AutoSize | Out-String -Width 140 | Write-Host

if ($results.Count -ge 2) {
    $gpu = $results | Where-Object Mode -eq 'gpu'
    $sw  = $results | Where-Object Mode -eq 'software'
    if ($gpu -and $sw) {
        $wsDelta = $gpu.WorkingSetMB - $sw.WorkingSetMB
        $pvDelta = $gpu.PrivateMB - $sw.PrivateMB
        $wsPct = if ($gpu.WorkingSetMB -gt 0) { [math]::Round(100 * $wsDelta / $gpu.WorkingSetMB, 1) } else { 0 }
        $pvPct = if ($gpu.PrivateMB -gt 0) { [math]::Round(100 * $pvDelta / $gpu.PrivateMB, 1) } else { 0 }
        Write-Host "Working set saved: $wsDelta MB ($wsPct%)"
        Write-Host "Private bytes saved: $pvDelta MB ($pvPct%)"
        Write-Host "Threads saved: $($gpu.Threads - $sw.Threads)"
    }
}
