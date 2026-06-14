param(
    [int]$DurationSeconds = 180,
    [int]$RequestDelayMilliseconds = 100,
    [string]$HelperPath = "",
    [int]$Port = 0
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$repoRoot = (Resolve-Path "$scriptDir\..").Path

if ([string]::IsNullOrWhiteSpace($HelperPath)) {
    $HelperPath = Join-Path $repoRoot "src\carton.Helper\target\x86_64-pc-windows-msvc\release\carton-helper.exe"
}

$helperPathResolved = (Resolve-Path $HelperPath).Path
if (-not (Test-Path -LiteralPath $helperPathResolved)) {
    throw "carton-helper executable not found: $HelperPath"
}

if ($DurationSeconds -le 0) {
    throw "DurationSeconds must be greater than zero."
}

if ($RequestDelayMilliseconds -lt 0) {
    throw "RequestDelayMilliseconds cannot be negative."
}

if ($Port -le 0) {
    $Port = Get-Random -Minimum 48000 -Maximum 59999
}

$token = "carton-helper-stress-" + [Guid]::NewGuid().ToString("N")
$baseUrl = "http://127.0.0.1:$Port"
$headers = @{ "X-Carton-Helper-Token" = $token }
$helperProcess = $null
$samples = New-Object System.Collections.Generic.List[object]
$requestCount = 0
$failureCount = 0
$lastSampleAt = [DateTimeOffset]::MinValue

function Get-HelperSample {
    param([int]$ProcessId)

    $process = Get-Process -Id $ProcessId -ErrorAction Stop
    [pscustomobject]@{
        TimeUtc = [DateTimeOffset]::UtcNow
        WorkingSetBytes = [int64]$process.WorkingSet64
        PrivateBytes = [int64]$process.PrivateMemorySize64
        HandleCount = [int]$process.HandleCount
        CpuSeconds = [double]$process.TotalProcessorTime.TotalSeconds
    }
}

function Convert-ToMiB {
    param([int64]$Bytes)
    [math]::Round($Bytes / 1MB, 2)
}

try {
    $arguments = @(
        "--carton-elevated-helper",
        "--port",
        [string]$Port,
        "--token",
        $token,
        "--parent-pid",
        [string]$PID
    )

    $helperProcess = Start-Process `
        -FilePath $helperPathResolved `
        -ArgumentList $arguments `
        -WindowStyle Hidden `
        -PassThru

    $ready = $false
    for ($i = 0; $i -lt 100; $i++) {
        try {
            $ping = Invoke-RestMethod -Uri "$baseUrl/ping" -Headers $headers -TimeoutSec 1
            if ([string]$ping -eq $token) {
                $ready = $true
                break
            }
        }
        catch {
            Start-Sleep -Milliseconds 100
        }
    }

    if (-not $ready) {
        throw "carton-helper did not become ready on port $Port."
    }

    $samples.Add((Get-HelperSample -ProcessId $helperProcess.Id))
    $startedAt = [DateTimeOffset]::UtcNow
    $deadline = $startedAt.AddSeconds($DurationSeconds)

    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        try {
            Invoke-RestMethod `
                -Uri "$baseUrl/status?afterStartupLogSequence=0" `
                -Headers $headers `
                -TimeoutSec 2 | Out-Null
            $requestCount++
        }
        catch {
            $failureCount++
        }

        $now = [DateTimeOffset]::UtcNow
        if (($now - $lastSampleAt).TotalSeconds -ge 1) {
            $samples.Add((Get-HelperSample -ProcessId $helperProcess.Id))
            $lastSampleAt = $now
        }

        if ($RequestDelayMilliseconds -gt 0) {
            Start-Sleep -Milliseconds $RequestDelayMilliseconds
        }
    }

    $samples.Add((Get-HelperSample -ProcessId $helperProcess.Id))

    try {
        Invoke-RestMethod -Uri "$baseUrl/shutdown" -Headers $headers -TimeoutSec 2 | Out-Null
        Wait-Process -Id $helperProcess.Id -Timeout 5 -ErrorAction Stop
    }
    catch {
        if (Get-Process -Id $helperProcess.Id -ErrorAction SilentlyContinue) {
            Stop-Process -Id $helperProcess.Id -Force
        }
    }

    $first = $samples[0]
    $last = $samples[$samples.Count - 1]
    $maxWorkingSet = ($samples | Measure-Object -Property WorkingSetBytes -Maximum).Maximum
    $maxPrivate = ($samples | Measure-Object -Property PrivateBytes -Maximum).Maximum
    $maxHandles = ($samples | Measure-Object -Property HandleCount -Maximum).Maximum
    $elapsedSeconds = ([DateTimeOffset]::UtcNow - $startedAt).TotalSeconds

    [pscustomobject]@{
        Result = if ($failureCount -eq 0) { "OK" } else { "CompletedWithRequestFailures" }
        HelperPid = $helperProcess.Id
        Port = $Port
        DurationSeconds = [math]::Round($elapsedSeconds, 1)
        Requests = $requestCount
        Failures = $failureCount
        RequestsPerSecond = [math]::Round($requestCount / [math]::Max($elapsedSeconds, 1), 2)
        Samples = $samples.Count
        InitialWorkingSetMiB = Convert-ToMiB $first.WorkingSetBytes
        MaxWorkingSetMiB = Convert-ToMiB ([int64]$maxWorkingSet)
        FinalWorkingSetMiB = Convert-ToMiB $last.WorkingSetBytes
        WorkingSetDeltaMiB = Convert-ToMiB ($last.WorkingSetBytes - $first.WorkingSetBytes)
        InitialPrivateMiB = Convert-ToMiB $first.PrivateBytes
        MaxPrivateMiB = Convert-ToMiB ([int64]$maxPrivate)
        FinalPrivateMiB = Convert-ToMiB $last.PrivateBytes
        PrivateDeltaMiB = Convert-ToMiB ($last.PrivateBytes - $first.PrivateBytes)
        InitialHandles = $first.HandleCount
        MaxHandles = [int]$maxHandles
        FinalHandles = $last.HandleCount
        HandleDelta = $last.HandleCount - $first.HandleCount
        CpuSeconds = [math]::Round($last.CpuSeconds - $first.CpuSeconds, 2)
    }
}
finally {
    if ($helperProcess -and (Get-Process -Id $helperProcess.Id -ErrorAction SilentlyContinue)) {
        Stop-Process -Id $helperProcess.Id -Force
    }
}
