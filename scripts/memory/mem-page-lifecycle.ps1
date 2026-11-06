param(
    [string]$Root = "$PSScriptRoot/../../artifacts/memory-lifecycle",
    [string[]]$Variants = @('before', 'after'),
    [int]$Runs = 3,
    [int]$Cycles = 6,
    [int]$ProfileCount = 0,
    [int]$NaturalIdleSeconds = 0,
    [string]$ResultsDirectory = ''
)
$ErrorActionPreference = 'Stop'
$Root = [IO.Path]::GetFullPath($Root)
$artifacts = [IO.Path]::GetFullPath("$PSScriptRoot/../../artifacts") + [IO.Path]::DirectorySeparatorChar
if (!$Root.StartsWith($artifacts, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Use disposable publish directories under this repository's artifacts folder."
}
if (!$ResultsDirectory) { $ResultsDirectory = Join-Path $Root 'measurements' }
New-Item -ItemType Directory -Force $ResultsDirectory | Out-Null
if (Get-Process carton -ErrorAction SilentlyContinue) { throw 'Close the existing carton yourself; this probe never stops a user process.' }
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class LifecycleNative {
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int command);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
}
'@
function Find-ById($element, [string]$id) {
    $element.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::AutomationIdProperty), $id))
}
foreach ($round in 1..$Runs) {
    $order = if ($round % 2 -eq 1) { $Variants } else { @($Variants[($Variants.Count - 1)..0]) }
    foreach ($variant in $order) {
        $directory = Join-Path $Root $variant
        $exe = Join-Path $directory 'carton.exe'
        if (!(Test-Path $exe)) { throw "Missing isolated AOT build: $exe" }
        New-Item -ItemType File -Force "$directory/.carton_portable_data" | Out-Null
        New-Item -ItemType Directory -Force "$directory/data" | Out-Null
        @{ autoStartOnLaunch = $false; autoCheckAppUpdates = $false; language = 0; theme = 1;
           windowMaterial = 0; useSystemThemeAccent = $false; themeAccent = '#FF0078D4' } |
            ConvertTo-Json | Set-Content "$directory/data/preferences.json" -Encoding UTF8
        $profiles = @()
        New-Item -ItemType Directory -Force "$directory/data/configs/local" | Out-Null
        for ($index = 1; $index -le $ProfileCount; $index++) {
            $profiles += @{
                id = $index; name = "Test profile $index"; type = 0; path = ''; autoUpdate = $false
                createdAt = '2026-01-01T00:00:00'; updateInterval = 1440
                runtimeOptions = @{ inboundPort = 2028; allowLanConnections = $false; logLevel = 'info'; initialized = $true; logLevelInitialized = $true }
            }
            '{"log":{"level":"info"},"outbounds":[{"type":"direct","tag":"direct"}]}' |
                Set-Content "$directory/data/configs/local/profile_$index.json" -Encoding UTF8
        }
        @{ selectedProfileId = $(if ($ProfileCount -gt 0) { 1 } else { 0 }); profiles = $profiles } |
            ConvertTo-Json -Depth 5 | Set-Content "$directory/data/sing-box-data.json" -Encoding UTF8
        $sha = (Get-FileHash $exe -Algorithm SHA256).Hash
        $rows = [Collections.Generic.List[object]]::new()
        $resultPath = Join-Path $ResultsDirectory "$variant-$round.json"
        $clock = [Diagnostics.Stopwatch]::StartNew()
        if (Get-Process carton -ErrorAction SilentlyContinue) { throw 'Another carton instance appeared; refusing to attach to or stop it.' }
        $process = Start-Process $exe -PassThru
        try {
            $handle = [IntPtr]::Zero
            do {
                Start-Sleep -Milliseconds 100
                $process.Refresh()
                if ($process.HasExited) { throw "$variant exited before opening its window (exit $($process.ExitCode))." }
                $handle = $process.MainWindowHandle
            } while ($handle -eq [IntPtr]::Zero -and $clock.Elapsed.TotalSeconds -lt 30)
            if ($handle -eq [IntPtr]::Zero) { throw 'No window within 30 seconds.' }
            [LifecycleNative]::ShowWindow($handle, 9) | Out-Null
            [LifecycleNative]::SetWindowPos($handle, [IntPtr]::Zero, 80, 60, 1000, 720, 0x0040) | Out-Null
            Start-Sleep -Seconds 10
            $ui = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
            $later = $ui.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                (New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::NameProperty), 'Later'))
            if ($later) { $later.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
            $nav = Find-ById $ui 'NavListBox'
            if (!$nav) { throw 'Navigation was not found.' }
            $items = $nav.FindAll([System.Windows.Automation.TreeScope]::Children,
                (New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ControlTypeProperty), ([System.Windows.Automation.ControlType]::ListItem)))
            if ($items.Count -ne 6) { throw "Expected 6 navigation items, found $($items.Count)." }
            function Select-Page([int]$index) {
                $process.Refresh()
                $cpuBefore = $process.TotalProcessorTime.TotalMilliseconds
                $navigationClock = [Diagnostics.Stopwatch]::StartNew()
                $selection = $items[$index].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
                $selection.Select()
                Start-Sleep -Milliseconds 400
                if (!$selection.Current.IsSelected) { throw "Page $index did not become selected." }
                $process.Refresh()
                $script:NavigationCpuMs = $process.TotalProcessorTime.TotalMilliseconds - $cpuBefore
                $script:NavigationElapsedMs = $navigationClock.Elapsed.TotalMilliseconds
            }
            function Sample([string]$stage) {
                $process.Refresh()
                $cpu = $process.TotalProcessorTime.TotalMilliseconds
                $sampleClock = [Diagnostics.Stopwatch]::StartNew()
                Start-Sleep -Seconds 1
                $process.Refresh()
                if ($process.HasExited) { throw 'Application exited during measurement.' }
                $cpuPercent = 100 * ($process.TotalProcessorTime.TotalMilliseconds - $cpu) / $sampleClock.Elapsed.TotalMilliseconds
                $counters = Get-CimInstance Win32_PerfFormattedData_PerfProc_Process -Filter "IDProcess=$($process.Id)"
                if (!$counters) { throw 'Private working-set counter is unavailable.' }
                $visible = [LifecycleNative]::IsWindowVisible($handle)
                $minimized = [LifecycleNative]::IsIconic($handle)
                if (!$visible -or $minimized) { throw 'Do not compare trimmed/minimized samples with visible windows.' }
                $row = [pscustomobject]@{
                    Variant = $variant; Round = $round; Stage = $stage; Sha256 = $sha; ProfileCount = $ProfileCount
                    ElapsedSeconds = [math]::Round($clock.Elapsed.TotalSeconds, 2)
                    WorkingSetMiB = [math]::Round($process.WorkingSet64 / 1MB, 3)
                    PrivateWorkingSetMiB = [math]::Round($counters.WorkingSetPrivate / 1MB, 3)
                    PrivateBytesMiB = [math]::Round($process.PrivateMemorySize64 / 1MB, 3)
                    CpuOneCorePercent = [math]::Round($cpuPercent, 3)
                    NavigationCpuMs = [math]::Round($script:NavigationCpuMs, 3)
                    NavigationElapsedMs = [math]::Round($script:NavigationElapsedMs, 3)
                    Handles = $process.HandleCount; Visible = $visible; Minimized = $minimized
                }
                $rows.Add($row)
                $rows.ToArray() | ConvertTo-Json | Set-Content $resultPath -Encoding UTF8
                $row | ConvertTo-Json -Compress | Write-Output
            }
            Select-Page 0
            Sample 'startup-dashboard'
            foreach ($cycle in 1..$Cycles) {
                Select-Page 5
                Sample "settings-$cycle"
                Select-Page 2
                Sample "profiles-$cycle"
                foreach ($index in @(3, 4, 1, 0)) { Select-Page $index }
                Sample "dashboard-hot-$cycle"
                if ($cycle -eq 1 -and $NaturalIdleSeconds -gt 0) {
                    Start-Sleep -Seconds $NaturalIdleSeconds
                    Sample 'dashboard-natural-unload'
                }
                # Exercise the EXISTING background unload path; do not add GC/trim calls.
                # Only sample after restoring and repainting, never while minimized.
                [LifecycleNative]::ShowWindow($handle, 6) | Out-Null
                Start-Sleep -Milliseconds 1500
                [LifecycleNative]::ShowWindow($handle, 9) | Out-Null
                Start-Sleep -Seconds 3
                Sample "dashboard-restored-$cycle"
            }
        }
        finally {
            try {
                if (!$process.HasExited) { Stop-Process -Id $process.Id }
                # TerminateProcess is asynchronous; don't race the next instance against
                # the previous process's still-live global single-instance mutex.
                if (!$process.WaitForExit(10000)) { throw "Test process $($process.Id) did not finish terminating." }
            }
            finally { $process.Dispose() }
        }
    }
}
