using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using carton.Core.Models;

namespace carton.Core.Services;

public partial class SingBoxManager
{
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

                if (string.Equals(ReadString(inbound, "type"), "tun", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            LogManager($"[WARN] Failed to inspect config for TUN: {ex.Message}");
        }

        return false;
    }

    private async Task<bool> StartElevatedAsync(string configPath)
    {
        try
        {
            await StopElevatedLogTailAsync();

            var logFileName = $"sing-box.elevated.{DateTime.Now:yyyyMMdd-HHmmss-fff}.log";
            var elevatedLogPath = Path.Combine(_workingDirectory, "logs", logFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(elevatedLogPath)!);

            var result = await StartElevatedProcessForCurrentPlatformAsync(configPath, elevatedLogPath);
            if (!result.Success)
            {
                var msg = string.IsNullOrWhiteSpace(result.ErrorMessage) ? "Elevated start failed" : result.ErrorMessage;
                LogManager($"[ERROR] {msg}");
                SetError(msg);
                return false;
            }

            int? pid = result.Pid;
            if (pid.HasValue && pid.Value > 0)
            {
                _elevatedPid = pid.Value;
                TryAttachProcessIdToWindowsJob(pid.Value);
                LogManager($"[INFO] Elevated process PID: {pid.Value}");
            }
            else
            {
                LogManager("[INFO] No PID from helper response, will discover via API port");
            }

            _elevatedLogPath = elevatedLogPath;
            StartElevatedLogTail(elevatedLogPath);

            var ready = await WaitForApiReadyAsync(pid, TimeSpan.FromSeconds(30));
            if (!ready)
            {
                var recentLog = await ReadRecentLogLinesAsync(elevatedLogPath, 20);
                var msg = "sing-box API did not become reachable in time" +
                          (string.IsNullOrWhiteSpace(recentLog) ? string.Empty : $": {recentLog}");
                LogManager($"[ERROR] {msg}");
                await CleanupFailedStartAttemptAsync();
                SetError(msg);
                return false;
            }

            _state.StartTime = DateTime.Now;
            UpdateStatus(ServiceStatus.Running);
            LogManager($"[INFO] sing-box started successfully (elevated, pid={_elevatedPid})");
            EnsureRuntimeMonitorsRunning();
            return true;
        }
        catch (Exception ex)
        {
            var error = $"Failed to start sing-box with administrator privileges: {ex.Message}";
            LogManager($"[ERROR] {error}");
            await CleanupFailedStartAttemptAsync();
            SetError(error);
            return false;
        }
    }

    private async Task<bool> StopElevatedAsync()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && !_elevatedPid.HasValue)
        {
            if (await TryStopViaWindowsElevatedHelperAsync(force: false))
            {
                await Task.Delay(500);
                return true;
            }

            WriteStopSignalFile();
            await Task.Delay(1500);
            return true;
        }

        if (!_elevatedPid.HasValue)
        {
            return true;
        }

        var pid = _elevatedPid.Value;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Use file-based signal to stop (bypasses TUN)
            WriteStopSignalFile();
        }
        else
        {
            await RunElevatedStopCommandAsync(pid, force: false);
        }

        // Wait for process to exit
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(500);
            if (!IsProcessAlive(pid))
            {
                return true;
            }
        }

        LogManager($"[WARN] sing-box process {pid} did not exit, trying force kill...");

        // Fallback: try taskkill via UAC
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
            LogManager($"[ERROR] sing-box process {pid} is still running after stop attempts");
            return false;
        }

        return true;
    }

    private async Task<bool> WaitForApiReadyAsync(int? elevatedPid, TimeSpan timeout)
    {
        var start = DateTime.UtcNow;
        LogManager($"[INFO] Waiting for sing-box API to become ready (timeout={timeout.TotalSeconds}s)...");
        var attempt = 0;
        while (DateTime.UtcNow - start < timeout)
        {
            attempt++;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                using var response = await client.GetAsync($"{_apiAddress}/version", cts.Token);
                LogManager($"[INFO] API responded with status {(int)response.StatusCode}");

                // API is reachable, discover PID if we don't have one
                if (!_elevatedPid.HasValue || _elevatedPid.Value <= 0)
                {
                    var discoveredPid = await TryFindProcessPidByApiPortAsync();
                    if (discoveredPid.HasValue && discoveredPid.Value > 0)
                    {
                        _elevatedPid = discoveredPid.Value;
                    }
                }

                return true;
            }
            catch
            {
                // API not ready yet
            }

            // For non-elevated mode, check if process crashed
            if (_process != null && _process.HasExited)
            {
                LogManager("[WARN] Process exited while waiting for API");
                return false;
            }

            // For elevated mode, check if process is still alive (only if we have a PID)
            if (elevatedPid.HasValue && elevatedPid.Value > 0)
            {
                try
                {
                    using var proc = Process.GetProcessById(elevatedPid.Value);
                    if (proc.HasExited)
                    {
                        LogManager($"[WARN] Elevated process {elevatedPid.Value} exited while waiting for API");
                        return false;
                    }
                }
                catch
                {
                    LogManager($"[WARN] Elevated process {elevatedPid.Value} not found while waiting for API");
                    return false;
                }
            }

            await Task.Delay(500);
        }

        LogManager($"[WARN] API did not become ready within {timeout.TotalSeconds}s");
        return false;
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
            if (await TryStopViaWindowsElevatedHelperAsync(force))
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

    private void StartElevatedLogTail(string logPath)
    {
        _elevatedLogCts?.Cancel();
        _elevatedLogCts?.Dispose();
        _elevatedLogCts = new CancellationTokenSource();
        _elevatedLogTask = Task.Run(() => TailLogAsync(logPath, _elevatedLogCts.Token));
    }

    private async Task StopElevatedLogTailAsync()
    {
        if (_elevatedLogCts == null)
        {
            return;
        }

        _elevatedLogCts.Cancel();
        if (_elevatedLogTask != null)
        {
            try
            {
                await _elevatedLogTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _elevatedLogTask = null;
        _elevatedLogCts.Dispose();
        _elevatedLogCts = null;
    }

    private async Task TailLogAsync(string logPath, CancellationToken cancellationToken)
    {
        try
        {
            using var stream = new FileStream(logPath, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line == null)
                {
                    await Task.Delay(100, cancellationToken);
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(line))
                {
                    LogKernel(line);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LogManager($"[WARN] Elevated log tail stopped: {ex.Message}");
        }
    }

    private sealed class ElevatedStartResult
    {
        public bool Success { get; init; }
        public int? Pid { get; init; }
        public string? ErrorMessage { get; init; }
        public string RawOutput { get; init; } = string.Empty;
    }
}
