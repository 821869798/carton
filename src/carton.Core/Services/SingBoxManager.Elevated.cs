using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using carton.Core.Models;

namespace carton.Core.Services;

public partial class SingBoxManager
{
    private static readonly TimeSpan LocalApiProbeTimeout = TimeSpan.FromSeconds(1);

    private bool RequiresElevatedPrivileges(string configPath)
    {
        try
        {
            var content = File.ReadAllText(configPath);
            using var document = JsonDocument.Parse(content);
            if (!document.RootElement.TryGetProperty("inbounds", out var inbounds) ||
                inbounds.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var inbound in inbounds.EnumerateArray())
            {
                if (inbound.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (string.Equals(ReadJsonString(inbound, "type"), "tun", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            LogWarn($"Failed to inspect config for TUN: {ex.Message}");
        }

        return false;
    }

    private async Task<bool> StartElevatedAsync(string configPath)
    {
        var timing = Stopwatch.StartNew();
        LogTiming("start_elevated.begin");
        var startupLogSession = 0;
        try
        {
#if DEBUG
            var logFileName = $"sing-box.elevated.{DateTime.Now:yyyyMMdd-HHmmss-fff}.log";
            var elevatedLogPath = Path.Combine(_workingDirectory, "logs", logFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(elevatedLogPath)!);
#else
            var elevatedLogPath = string.Empty;
#endif

            startupLogSession = BeginStartupLogCapture();
            var startProcessTiming = Stopwatch.StartNew();
            var result = await StartElevatedProcessForCurrentPlatformAsync(configPath, elevatedLogPath);
            LogTiming(
                result.Success ? "start_elevated.process_request_success" : "start_elevated.process_request_failed",
                startProcessTiming.Elapsed);
            if (!result.Success)
            {
                var msg = string.IsNullOrWhiteSpace(result.ErrorMessage) ? "Elevated start failed" : result.ErrorMessage;
                var recentLog = string.IsNullOrWhiteSpace(elevatedLogPath)
                    ? string.Empty
                    : await ReadRecentLogLinesAsync(elevatedLogPath, 20);
                if (!string.IsNullOrWhiteSpace(recentLog))
                {
                    msg = $"{msg}: {recentLog}";
                }
                // When helper startup output has already been emitted through the sing-box
                // log channel, keep the detailed reason there. The UI will add the single
                // Carton "start failed" summary from State.ErrorMessage.
                if (Interlocked.Read(ref _windowsStartupLogSequence) <= 0)
                {
                    LogError($"{msg}");
                }
                StopStartupLogCapture(startupLogSession);
                SetError(msg);
                return false;
            }

            int? pid = result.Pid;
            if (pid.HasValue && pid.Value > 0)
            {
                _elevatedPid = pid.Value;
                TryAttachProcessIdToWindowsJob(pid.Value);
                LogDebug($"Elevated process PID: {pid.Value}");
            }
            else
            {
                LogDebug("No PID from helper response, will discover via API port");
            }

            var readyTiming = Stopwatch.StartNew();
            var ready = await WaitForApiReadyAsync(
                pid,
                TunApiReadyTimeout);
            LogTiming(ready ? "start_elevated.api_ready" : "start_elevated.api_not_ready", readyTiming.Elapsed);
            if (!ready)
            {
                var recentLog = string.IsNullOrWhiteSpace(elevatedLogPath)
                    ? string.Empty
                    : await ReadRecentLogLinesAsync(elevatedLogPath, 20);
                var msg = string.IsNullOrWhiteSpace(_lastStartupWaitFailureReason)
                    ? "sing-box API did not become reachable in time"
                    : _lastStartupWaitFailureReason;
                if (!string.IsNullOrWhiteSpace(recentLog))
                {
                    msg = $"{msg}: {recentLog}";
                }
                LogError($"{msg}");
                StopStartupLogCapture(startupLogSession);
                await CleanupFailedStartAttemptAsync();
                SetError(msg);
                return false;
            }

            _errorOutput.Clear();
            _state.StartTime = DateTime.Now;
            UpdateStatus(ServiceStatus.Running);
            LogInfo($"sing-box started successfully (elevated, pid={_elevatedPid})");
            EnsureRuntimeMonitorsRunning();
            LogTiming("start_elevated.end_success", timing.Elapsed);
            return true;
        }
        catch (Exception ex)
        {
            if (startupLogSession != 0)
            {
                StopStartupLogCapture(startupLogSession);
            }

            var error = $"Failed to start sing-box with administrator privileges: {ex.Message}";
            LogError($"{error}");
            await CleanupFailedStartAttemptAsync();
            SetError(error);
            LogTiming("start_elevated.end_exception", timing.Elapsed);
            return false;
        }
    }

    private async Task<bool> StopElevatedAsync()
    {
        var timing = Stopwatch.StartNew();
        LogTiming("stop_elevated.begin");
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && !_elevatedPid.HasValue)
        {
            var helperStopTiming = Stopwatch.StartNew();
            var helperStopResult = await TryStopViaWindowsElevatedHelperAsync(force: false);
            if (helperStopResult == WindowsHelperStopResult.Success)
            {
                LogTiming("stop_elevated.helper_stop_without_pid", helperStopTiming.Elapsed);
                await Task.Delay(500);
                LogTiming("stop_elevated.end_success", timing.Elapsed);
                return true;
            }
            LogTiming(
                helperStopResult == WindowsHelperStopResult.EndpointUnavailable
                    ? "stop_elevated.helper_unavailable_without_pid"
                    : "stop_elevated.helper_stop_failed_without_pid",
                helperStopTiming.Elapsed);

            WriteStopSignalFile();
            if (helperStopResult == WindowsHelperStopResult.EndpointUnavailable)
            {
                InvalidateWindowsElevatedHelperSession();
            }
            await Task.Delay(1500);
            LogTiming("stop_elevated.signal_file_without_pid", timing.Elapsed);
            return true;
        }

        if (!_elevatedPid.HasValue)
        {
            LogTiming("stop_elevated.no_pid", timing.Elapsed);
            return true;
        }

        var pid = _elevatedPid.Value;
        var usedWindowsHelperEndpoint = false;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var helperStopTiming = Stopwatch.StartNew();
            var helperStopResult = await TryStopViaWindowsElevatedHelperAsync(force: true);
            if (helperStopResult == WindowsHelperStopResult.Success)
            {
                usedWindowsHelperEndpoint = true;
                LogTiming("stop_elevated.helper_force_stop_returned", helperStopTiming.Elapsed);
                await Task.Delay(250);
                if (!IsProcessAlive(pid))
                {
                    LogTiming("stop_elevated.end_success", timing.Elapsed);
                    return true;
                }

                LogWarn($"Elevated helper stop returned before sing-box process {pid} exited");
            }
            else
            {
                LogTiming(
                    helperStopResult == WindowsHelperStopResult.EndpointUnavailable
                        ? "stop_elevated.helper_unavailable"
                        : "stop_elevated.helper_stop_failed",
                    helperStopTiming.Elapsed);
            }

            // Fallback to file-based signal if the helper endpoint is unavailable.
            WriteStopSignalFile();
            if (helperStopResult == WindowsHelperStopResult.EndpointUnavailable)
            {
                InvalidateWindowsElevatedHelperSession();
                var freshHelperStopTiming = Stopwatch.StartNew();
                if (await TryStopViaFreshWindowsElevatedHelperAsync(pid, force: true))
                {
                    LogTiming("stop_elevated.fresh_helper_force_stop_returned", freshHelperStopTiming.Elapsed);
                    usedWindowsHelperEndpoint = true;
                    await Task.Delay(250);
                    if (!IsProcessAlive(pid))
                    {
                        LogTiming("stop_elevated.end_success", timing.Elapsed);
                        return true;
                    }
                }
                else
                {
                    LogTiming("stop_elevated.fresh_helper_stop_failed", freshHelperStopTiming.Elapsed);
                }
            }
        }
        else
        {
            await RunElevatedStopCommandAsync(pid, force: false);
        }

        // Wait for process to exit
        var waitAttempts = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? 8 : 20;
        var waitDelay = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? TimeSpan.FromMilliseconds(250)
            : TimeSpan.FromMilliseconds(500);
        for (var i = 0; i < waitAttempts; i++)
        {
            await Task.Delay(waitDelay);
            if (!IsProcessAlive(pid))
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
                    !usedWindowsHelperEndpoint &&
                    string.IsNullOrWhiteSpace(_windowsElevatedHelperToken))
                {
                    InvalidateWindowsElevatedHelperSession();
                }

                LogTiming("stop_elevated.process_exit_wait", timing.Elapsed);
                return true;
            }
        }

        LogWarn($"sing-box process {pid} did not exit, trying force kill...");

        // Last resort: try taskkill via UAC.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var taskkillArgs = $"/PID {pid} /T /F";
            await RunWindowsUacCommandAsync(
                "$p = Start-Process -FilePath 'taskkill.exe' -ArgumentList " +
                $"'{EscapeForPowerShellSingleQuoted(taskkillArgs)}' " +
                "-Verb RunAs -WindowStyle Hidden -Wait -PassThru; " +
                "if ($p) { $p.ExitCode }");
            await Task.Delay(500);
        }
        else
        {
            await RunElevatedStopCommandAsync(pid, force: true);
            await Task.Delay(500);
        }

        if (IsProcessAlive(pid))
        {
            LogError($"sing-box process {pid} is still running after stop attempts");
            LogTiming("stop_elevated.end_failed", timing.Elapsed);
            return false;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
            !usedWindowsHelperEndpoint &&
            string.IsNullOrWhiteSpace(_windowsElevatedHelperToken))
        {
            InvalidateWindowsElevatedHelperSession();
        }

        LogTiming("stop_elevated.end_success_after_force", timing.Elapsed);
        return true;
    }

    private async Task<bool> WaitForApiReadyAsync(
        int? elevatedPid,
        TimeSpan timeout)
    {
        _lastStartupWaitFailureReason = null;
        var timing = Stopwatch.StartNew();
        var start = DateTime.UtcNow;
        var noProcessStatusCount = 0;
        var shouldQueryHelperStatus =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
            !string.IsNullOrWhiteSpace(_windowsElevatedHelperToken) &&
            (_process == null || elevatedPid.HasValue || _elevatedPid.HasValue);
        LogDebug($"Waiting for sing-box API to become ready (timeout={timeout.TotalSeconds}s)...");
        var attempt = 0;
        while (DateTime.UtcNow - start < timeout)
        {
            attempt++;

            var helperProcessStatus = shouldQueryHelperStatus
                ? await TryGetWindowsHelperProcessStatusAsync(includeStartupLogs: IsStartupLogCaptureActive())
                : null;
            if (helperProcessStatus != null)
            {
                EmitWindowsHelperStartupLogs(helperProcessStatus);

                if ((!elevatedPid.HasValue || elevatedPid.Value <= 0) && helperProcessStatus.Pid is > 0)
                {
                    elevatedPid = helperProcessStatus.Pid;
                    _elevatedPid = helperProcessStatus.Pid;
                    LogDebug($"Learned elevated process PID from helper status endpoint: {helperProcessStatus.Pid}");
                }

                if (helperProcessStatus.HasProcess && !helperProcessStatus.IsRunning)
                {
                    _lastStartupWaitFailureReason = GetWindowsHelperExitReason(helperProcessStatus);
                    return false;
                }

                if (helperProcessStatus.ApiReady)
                {
                    LogTiming("api_ready.helper_status", timing.Elapsed);
                    return true;
                }

                if (!helperProcessStatus.HasProcess)
                {
                    noProcessStatusCount++;
                    if (DateTime.UtcNow - start > TimeSpan.FromSeconds(3) && noProcessStatusCount >= 3)
                    {
                        _lastStartupWaitFailureReason = "Elevated helper has no active sing-box process after startup request";
                        LogWarn($"{_lastStartupWaitFailureReason}");
                        return false;
                    }
                }
                else
                {
                    noProcessStatusCount = 0;
                }
            }

            if (await IsApiReachableAsync())
            {
                LogDebug("API probe succeeded");
                if (!_elevatedPid.HasValue || _elevatedPid.Value <= 0)
                {
                    var discoveredPid = await TryFindProcessPidByApiPortAsync();
                    if (discoveredPid.HasValue && discoveredPid.Value > 0)
                    {
                        _elevatedPid = discoveredPid.Value;
                    }
                }

                LogTiming("api_ready.protocol_probe", timing.Elapsed);
                return true;
            }

            // For non-elevated mode, check if process crashed
            if (_process != null && _process.HasExited)
            {
                LogWarn("Process exited while waiting for API");
                _lastStartupWaitFailureReason = "sing-box exited while waiting for API";
                return false;
            }

            // For elevated mode, check if process is still alive (only if we have a PID)
            if (elevatedPid.HasValue && elevatedPid.Value > 0)
            {
                var processUnavailable = false;
                string? processCheckError = null;
                try
                {
                    using var proc = Process.GetProcessById(elevatedPid.Value);
                    if (proc.HasExited)
                    {
                        processUnavailable = true;
                    }
                }
                catch (Exception ex)
                {
                    processUnavailable = true;
                    processCheckError = ex.Message;
                }

                if (processUnavailable)
                {
                    // The helper owns the elevated process and captures its stdout/stderr. Query it
                    // once more before falling back to a generic PID error: failures such as an
                    // occupied inbound port often occur while the direct API probe is in flight.
                    var latestHelperStatus = shouldQueryHelperStatus
                        ? await TryGetWindowsHelperProcessStatusAsync(
                            includeStartupLogs: IsStartupLogCaptureActive())
                        : null;
                    if (latestHelperStatus != null)
                    {
                        EmitWindowsHelperStartupLogs(latestHelperStatus);
                        if (latestHelperStatus.HasProcess && !latestHelperStatus.IsRunning)
                        {
                            _lastStartupWaitFailureReason = GetWindowsHelperExitReason(latestHelperStatus);
                            return false;
                        }

                        if (latestHelperStatus.IsRunning)
                        {
                            LogWarn(
                                $"Direct process check for {elevatedPid.Value} failed, but elevated helper reports it is still running");
                            await Task.Delay(500);
                            continue;
                        }
                    }

                    var detail = string.IsNullOrWhiteSpace(processCheckError)
                        ? string.Empty
                        : $": {processCheckError}";
                    LogWarn(
                        $"Elevated process {elevatedPid.Value} was unavailable while waiting for API{detail}");
                    _lastStartupWaitFailureReason =
                        $"sing-box process {elevatedPid.Value} exited before API became ready";
                    return false;
                }
            }

            await Task.Delay(500);
        }

        LogWarn($"API did not become ready within {timeout.TotalSeconds}s");
        _lastStartupWaitFailureReason = "sing-box API did not become reachable in time";
        LogTiming("api_ready.timeout", timing.Elapsed);
        return false;
    }

    private static string GetWindowsHelperExitReason(WindowsHelperProcessStatusResponse status)
    {
        if (!string.IsNullOrWhiteSpace(status.Error))
        {
            return status.Error;
        }

        return status.ExitCode.HasValue
            ? $"sing-box exited with code {status.ExitCode.Value} before API became ready"
            : "sing-box exited before API became ready";
    }

    private async Task<ElevatedStartResult> StartElevatedProcessForCurrentPlatformAsync(string configPath, string logPath)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return await StartElevatedOnMacAsync(configPath, logPath);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return await StartElevatedOnLinuxAsync(configPath, logPath);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return await StartElevatedOnWindowsAsync(configPath, logPath);
        }

        return new ElevatedStartResult
        {
            Success = false,
            ErrorMessage = "Unsupported OS for elevated TUN startup"
        };
    }

    private async Task RunElevatedStopCommandAsync(int pid, bool force)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var signal = force ? "KILL" : "TERM";
            await RunAppleScriptAdminCommandAsync($"kill -{signal} {pid}", MacTunPermissionPrompt);
            return;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var signal = force ? "-KILL" : "-TERM";
            await RunPkexecCommandAsync($"kill {signal} {pid}");
            return;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (await TryStopViaWindowsElevatedHelperAsync(force) == WindowsHelperStopResult.Success)
            {
                return;
            }

            var taskkillArgs = force ? $"/PID {pid} /T /F" : $"/PID {pid} /T";
            await RunWindowsUacCommandAsync(
                "$p = Start-Process -FilePath 'taskkill.exe' -ArgumentList " +
                $"'{EscapeForPowerShellSingleQuoted(taskkillArgs)}' " +
                "-Verb RunAs -WindowStyle Hidden -Wait -PassThru; " +
                "if ($p) { $p.ExitCode }");
        }
    }

    private sealed class ElevatedStartResult
    {
        public bool Success { get; init; }
        public int? Pid { get; init; }
        public string? ErrorMessage { get; init; }
        public string RawOutput { get; init; } = string.Empty;
    }

    private static string ReadJsonString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property))
        {
            return string.Empty;
        }

        return property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : property.ToString();
    }
}
