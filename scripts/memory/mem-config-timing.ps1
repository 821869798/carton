#requires -Version 5.1
<#
.SYNOPSIS
  Seeds the real carton data directory with N profiles whose config files are `SizeKB` each,
  launches carton, and reports the Task Manager "Memory" column (Working Set - Private).

.PURPOSE
  Answers the question: does opening carton read the config JSON into memory?
  If it does, 8 x 2 MB of config must show up as ~16 MB of extra private working set.

  Seeded configs are deliberately padded with a large, valid JSON string value so the file
  is big but still parses. The padding lives in a field carton never displays.

.WARNING
  This overwrites the real data directory. Back it up first.
#>
param(
    [int]$ProfileCount = 8,
    [int]$SizeKB = 2048,
    [string]$DataDir = "$env:APPDATA\Carton\data",
    [string]$ExePath = "$PSScriptRoot\..\..\src\carton.GUI\bin\Release\net10.0\carton.exe",
    [int]$SettleSeconds = 25,
    [string]$Label = ''
)

$ErrorActionPreference = 'Stop'

function Stop-Carton {
    Get-Process carton -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
}

Stop-Carton

$localDir = Join-Path $DataDir 'configs\local'
$remoteDir = Join-Path $DataDir 'configs\remote'
New-Item -ItemType Directory -Force -Path $localDir, $remoteDir | Out-Null

# Remove existing profile configs so only the seeded ones exist.
Get-ChildItem -Path $localDir, $remoteDir -Filter 'profile_*.json' -ErrorAction SilentlyContinue | Remove-Item -Force

$padding = 'x' * ([Math]::Max(0, $SizeKB * 1024 - 400))

$profiles = @()
for ($i = 1; $i -le $ProfileCount; $i++) {
    $json = @"
{
  "log": { "level": "info" },
  "inbounds": [
    { "type": "mixed", "listen": "127.0.0.1", "listen_port": $(2000 + $i) }
  ],
  "outbounds": [ { "type": "direct", "tag": "direct" } ],
  "_memtest_padding": "$padding"
}
"@
    Set-Content -Path (Join-Path $localDir "profile_$i.json") -Value $json -Encoding UTF8 -NoNewline

    $profiles += [ordered]@{
        id                = $i
        name              = "memtest-$i"
        type              = 0
        path              = ''
        url               = $null
        lastUpdated       = $null
        updateInterval    = 1440
        autoUpdate        = $false
        createdAt         = (Get-Date).ToString('o')
        runtimeOptions    = [ordered]@{
            inboundPort          = (2000 + $i)
            allowLanConnections  = $false
            enableSystemProxy    = $false
            enableTunInbound     = $false
            logLevel             = 'info'
            logLevelInitialized  = $true
            initialized          = $true
        }
    }
}

$data = [ordered]@{
    selectedProfileId = if ($ProfileCount -gt 0) { 1 } else { 0 }
    profiles          = $profiles
}
$data | ConvertTo-Json -Depth 10 | Set-Content -Path (Join-Path $DataDir 'sing-box-data.json') -Encoding UTF8

$totalKB = 0
Get-ChildItem -Path $localDir -Filter 'profile_*.json' -ErrorAction SilentlyContinue | ForEach-Object { $totalKB += $_.Length / 1KB }
Write-Host "seeded $ProfileCount profiles, config bytes on disk = $([Math]::Round($totalKB)) KB"

$proc = Start-Process -FilePath $ExePath -PassThru
Start-Sleep -Seconds $SettleSeconds

$p = Get-Process -Id $proc.Id -ErrorAction SilentlyContinue
if (-not $p) { Write-Warning 'carton exited early'; exit 1 }
$p.Refresh()

$privWs = (Get-Counter "\Process(carton)\Working Set - Private").CounterSamples[0].CookedValue

Write-Host ''
Write-Host "=== $Label ($ProfileCount profiles, $([Math]::Round($totalKB)) KB config on disk) ==="
Write-Host ("Task Manager 'Memory' (Working Set - Private): {0} MB" -f [math]::Round($privWs / 1MB, 2))
Write-Host ("Working Set:      {0} MB" -f [math]::Round($p.WorkingSet64 / 1MB, 2))
Write-Host ("Commit (Private Bytes): {0} MB" -f [math]::Round($p.PrivateMemorySize64 / 1MB, 2))
Write-Host ("Threads: {0}  Handles: {1}" -f $p.Threads.Count, $p.HandleCount)

Stop-Carton
