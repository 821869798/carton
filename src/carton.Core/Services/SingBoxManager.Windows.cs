using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using carton.Core.Models;
using carton.Core.Serialization;
using carton.Core.Utilities;

namespace carton.Core.Services;

public partial class SingBoxManager
{
    private const int WindowsElevatedHelperPort = 47891;
    private const string WindowsElevatedHelperTokenHeader = "X-Carton-Helper-Token";

    private enum WindowsHelperStopResult
    {
        Success,
        ActionFailed,
        EndpointUnavailable
    }

    [SupportedOSPlatform("windows")]
    private async Task<ElevatedStartResult> StartElevatedOnWindowsAsync(string configPath, string logPath)
    {
        if (!await EnsureWindowsElevatedHelperRunningAsync())
        {
            return new ElevatedStartResult
            {
                Success = false,
                ErrorMessage = "Failed to start elevated helper"
            };
        }

        try
        {
            var resultFilePath = GetWindowsHelperStartResultFilePath("result");
            var request = new WindowsHelperStartRequest
            {
                SingBoxPath = _singBoxPath,
                ConfigPath = configPath,
                WorkingDirectory = _workingDirectory,
                LogPath = logPath,
                ResultFilePath = resultFilePath,
                ApiAddress = _apiAddress,
                ApiSecret = HttpClientFactory.LocalApiSecret
            };

            var json = JsonSerializer.Serialize(request, CartonCoreJsonContext.Default.WindowsHelperStartRequest);

            LogManager("[INFO] Sending start request to elevated helper...");
            using var sendClient = HttpClientFactory.CreateLoopbackClient(TimeSpan.FromSeconds(5));
            using var message = new HttpRequestMessage(HttpMethod.Post, GetWindowsHelperUri("start"))
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            message.Headers.Add(WindowsElevatedHelperTokenHeader, _windowsElevatedHelperToken!);

            try
            {
                var sendTask = sendClient.SendAsync(message);

                var resultFromFile = await WaitForWindowsHelperStartResultAsync(
                    resultFilePath,
                    TimeSpan.FromMilliseconds(1200));
                if (resultFromFile != null)
                {
                    return new ElevatedStartResult
                    {
                        Success = resultFromFile.Success,
                        Pid = resultFromFile.Pid,
                        ErrorMessage = resultFromFile.Error
                    };
                }

                if (await Task.WhenAny(sendTask, Task.Delay(300)) == sendTask)
                {
                    using var response = await sendTask;
                    var payload = (await response.Content.ReadAsStringAsync()).Trim();
                    if (!response.IsSuccessStatusCode)
                    {
                        return new ElevatedStartResult
                        {
                            Success = false,
                            ErrorMessage = string.IsNullOrWhiteSpace(payload)
                                ? $"Elevated helper start request failed with status {(int)response.StatusCode}"
                                : payload
                        };
                    }

                    if (!string.IsNullOrWhiteSpace(payload))
                    {
                        try
                        {
                            var result = JsonSerializer.Deserialize(
                                payload,
                                CartonCoreJsonContext.Default.WindowsHelperActionResponse);
                            if (result != null)
                            {
                                return new ElevatedStartResult
                                {
                                    Success = result.Success,
                                    Pid = result.Pid,
                                    ErrorMessage = result.Error
                                };
                            }
                        }
                        catch (Exception ex)
                        {
                            LogManager($"[WARN] Failed to parse elevated helper response: {ex.Message}");
                        }
                    }
                }

                LogManager("[INFO] Elevated helper did not send an immediate start confirmation, proceeding with API readiness check");
            }
            catch (TaskCanceledException)
            {
                LogManager("[WARN] Timed out waiting for elevated helper start response, proceeding with API readiness check");
            }
            catch (Exception ex)
            {
                LogManager($"[WARN] Failed to read immediate elevated helper start response: {ex.Message}");
            }

            return new ElevatedStartResult
            {
                Success = true
            };
        }
        catch (Exception ex)
        {
            return new ElevatedStartResult
            {
                Success = false,
                ErrorMessage = $"Failed to start sing-box via elevated helper: {ex.Message}"
            };
        }
    }

    private static async Task<ElevatedStartResult> RunWindowsUacCommandAsync(string script)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encoded}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        process.Start();
        var output = (await process.StandardOutput.ReadToEndAsync()).Trim();
        var error = (await process.StandardError.ReadToEndAsync()).Trim();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            return new ElevatedStartResult
            {
                Success = false,
                ErrorMessage = string.IsNullOrWhiteSpace(error) ? "UAC denied or failed" : error
            };
        }

        return new ElevatedStartResult { Success = true, RawOutput = output };
    }

    private string GetWindowsHelperUri(string path)
    {
        return $"http://127.0.0.1:{WindowsElevatedHelperPort}/{path}";
    }

    [SupportedOSPlatform("windows")]
    private async Task<bool> EnsureWindowsElevatedHelperRunningAsync()
    {
        var timing = Stopwatch.StartNew();
        LogTiming("windows_helper.ensure.begin");
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            LogTiming("windows_helper.ensure.not_windows", timing.Elapsed);
            return false;
        }

        if (!string.IsNullOrWhiteSpace(_windowsElevatedHelperToken) &&
            await PingWindowsElevatedHelperAsync(_windowsElevatedHelperToken))
        {
            LogTiming("windows_helper.ensure.existing_ready", timing.Elapsed);
            return true;
        }

        var executablePath = WindowsElevatedHelperTaskUtility.ResolveHelperExecutablePath(Environment.ProcessPath);
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            LogManager("[ERROR] Unable to resolve current executable path for elevated helper");
            LogTiming("windows_helper.ensure.no_executable", timing.Elapsed);
            return false;
        }

        var token = Guid.NewGuid().ToString("N");
        var parentPid = Environment.ProcessId;
        var taskTiming = Stopwatch.StartNew();
        if (await TryStartWindowsElevatedHelperViaScheduledTaskAsync(executablePath, token, parentPid))
        {
            LogTiming("windows_helper.ensure.scheduled_task_ready", taskTiming.Elapsed);
            LogTiming("windows_helper.ensure.end_success", timing.Elapsed);
            return true;
        }

        LogTiming("windows_helper.ensure.scheduled_task_failed", taskTiming.Elapsed);
        var runAsTiming = Stopwatch.StartNew();
        var result = await StartWindowsElevatedHelperViaRunAsAsync(executablePath, token, parentPid);
        if (!result.Success)
        {
            LogManager($"[ERROR] {result.ErrorMessage ?? "Failed to start elevated helper"}");
            LogTiming("windows_helper.ensure.runas_failed", runAsTiming.Elapsed);
            LogTiming("windows_helper.ensure.end_failed", timing.Elapsed);
            return false;
        }
        LogTiming("windows_helper.ensure.runas_started", runAsTiming.Elapsed);

        if (int.TryParse(result.RawOutput, out var helperPid) && helperPid > 0)
        {
            _windowsElevatedHelperPid = helperPid;
        }

        var ready = await WaitForWindowsElevatedHelperAsync(token);
        LogTiming(ready ? "windows_helper.ensure.runas_ready" : "windows_helper.ensure.runas_not_ready", timing.Elapsed);
        return ready;
    }

    [SupportedOSPlatform("windows")]
    private async Task<ElevatedStartResult> StartWindowsElevatedHelperViaRunAsAsync(
        string executablePath,
        string token,
        int parentPid)
    {
        var helperArgs =
            $"{WindowsElevatedHelperTaskUtility.HelperArg} --port {WindowsElevatedHelperPort} --token {token} --parent-pid {parentPid}";
        var script =
            "$p = Start-Process -FilePath " +
            $"'{EscapeForPowerShellSingleQuoted(executablePath)}' " +
            "-ArgumentList " +
            $"'{EscapeForPowerShellSingleQuoted(helperArgs)}' " +
            $"-WorkingDirectory '{EscapeForPowerShellSingleQuoted(_workingDirectory)}' " +
            "-Verb RunAs -WindowStyle Hidden -PassThru; " +
            "$p.Id";

        return await RunWindowsUacCommandAsync(script);
    }

    [SupportedOSPlatform("windows")]
    private async Task<bool> TryStartWindowsElevatedHelperViaScheduledTaskAsync(
        string executablePath,
        string token,
        int parentPid)
    {
        try
        {
            var requestFilePath = WindowsElevatedHelperTaskUtility.GetRequestFilePath(_workingDirectory);
            var hasCurrentRegistration = await WindowsElevatedHelperTaskUtility.IsRegistrationCurrentAsync(
                _workingDirectory,
                executablePath);
            if (!hasCurrentRegistration)
            {
                LogManager("[INFO] Elevated helper scheduled task is missing or stale, repairing registration");
                var registrationResult = await WindowsElevatedHelperTaskUtility.EnsureRegisteredAsync(
                    _workingDirectory,
                    executablePath);
                if (!registrationResult.Success)
                {
                    if (registrationResult.Cancelled)
                    {
                        LogManager("[INFO] Elevated helper scheduled task registration was canceled");
                    }
                    else
                    {
                        LogManager(
                            $"[WARN] Failed to repair elevated helper scheduled task: {registrationResult.ErrorMessage}");
                    }
                    TryDeleteFile(requestFilePath);
                    return false;
                }
            }

            if (!await WriteWindowsElevatedHelperLaunchRequestAsync(requestFilePath, token, parentPid))
            {
                return false;
            }

            if (await WindowsElevatedHelperTaskUtility.RunTaskAsync() &&
                await WaitForWindowsElevatedHelperAsync(token))
            {
                return true;
            }

            LogManager("[WARN] Scheduled task launch did not start a ready helper, cleaning stale helper and retrying");
            TryKillWindowsHelperProcess();
            token = Guid.NewGuid().ToString("N");

            if (!await WriteWindowsElevatedHelperLaunchRequestAsync(requestFilePath, token, parentPid))
            {
                return false;
            }

            if (await WindowsElevatedHelperTaskUtility.RunTaskAsync() &&
                await WaitForWindowsElevatedHelperAsync(token))
            {
                return true;
            }

            LogManager("[WARN] Scheduled task launch did not start a ready helper after cleanup, re-registering task and retrying");
            var retryRegistrationResult = await WindowsElevatedHelperTaskUtility.EnsureRegisteredAsync(
                _workingDirectory,
                executablePath);
            if (!retryRegistrationResult.Success)
            {
                if (retryRegistrationResult.Cancelled)
                {
                    LogManager("[INFO] Elevated helper scheduled task re-registration was canceled");
                }
                else
                {
                    LogManager(
                        $"[WARN] Failed to re-register elevated helper scheduled task: {retryRegistrationResult.ErrorMessage}");
                }
                TryDeleteFile(requestFilePath);
                return false;
            }

            token = Guid.NewGuid().ToString("N");
            if (!await WriteWindowsElevatedHelperLaunchRequestAsync(requestFilePath, token, parentPid))
            {
                return false;
            }

            if (await WindowsElevatedHelperTaskUtility.RunTaskAsync() &&
                await WaitForWindowsElevatedHelperAsync(token))
            {
                return true;
            }

            LogManager("[WARN] Scheduled task launched but elevated helper did not become ready after re-registration");
            TryDeleteFile(requestFilePath);
            return false;
        }
        catch (Exception ex)
        {
            LogManager($"[WARN] Failed to start elevated helper via scheduled task: {ex.Message}");
            return false;
        }
    }

    private string GetWindowsHelperStartResultFilePath(string kind)
    {
        return Path.Combine(
            _workingDirectory,
            "cache",
            $"windows-elevated-helper-start-{kind}-{Guid.NewGuid():N}.json");
    }

    private async Task<WindowsHelperActionResponse?> WaitForWindowsHelperStartResultAsync(
        string resultFilePath,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (File.Exists(resultFilePath))
                {
                    var payload = await File.ReadAllTextAsync(resultFilePath);
                    TryDeleteFile(resultFilePath);
                    if (string.IsNullOrWhiteSpace(payload))
                    {
                        return null;
                    }

                    return JsonSerializer.Deserialize(
                        payload,
                        CartonCoreJsonContext.Default.WindowsHelperActionResponse);
                }
            }
            catch (Exception ex)
            {
                LogManager($"[WARN] Failed to read elevated helper start result: {ex.Message}");
                break;
            }

            await Task.Delay(50);
        }

        TryDeleteFile(resultFilePath);
        return null;
    }

    private async Task<bool> WriteWindowsElevatedHelperLaunchRequestAsync(string requestFilePath, string token, int parentPid)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(requestFilePath)!);
            var launchRequest = new WindowsHelperLaunchRequest
            {
                Port = WindowsElevatedHelperPort,
                Token = token,
                ParentPid = parentPid,
                RequestedAtUtc = DateTimeOffset.UtcNow,
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(2)
            };

            var payload = JsonSerializer.Serialize(
                launchRequest,
                CartonCoreJsonContext.Default.WindowsHelperLaunchRequest);
            await File.WriteAllTextAsync(requestFilePath, payload, new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex)
        {
            LogManager($"[WARN] Failed to write elevated helper request file: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> WaitForWindowsElevatedHelperAsync(string token)
    {
        for (var i = 0; i < 30; i++)
        {
            if (await PingWindowsElevatedHelperAsync(token))
            {
                _windowsElevatedHelperToken = token;
                LogManager("[INFO] Elevated helper ready");
                return true;
            }

            await Task.Delay(200);
        }

        LogManager("[ERROR] Elevated helper did not become ready in time");
        return false;
    }

    private async Task<bool> PingWindowsElevatedHelperAsync(string token)
    {
        try
        {
            using var cts = new CancellationTokenSource(LocalApiProbeTimeout);
            using var client = HttpClientFactory.CreateLoopbackClient(LocalApiProbeTimeout);
            using var message = new HttpRequestMessage(HttpMethod.Get, GetWindowsHelperUri("ping"));
            message.Headers.Add(WindowsElevatedHelperTokenHeader, token);
            using var response = await client.SendAsync(message, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var payload = (await response.Content.ReadAsStringAsync()).Trim();
            return string.Equals(payload, token, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private async Task<WindowsHelperStopResult> TryStopViaWindowsElevatedHelperAsync(bool force, int? pid = null)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
            string.IsNullOrWhiteSpace(_windowsElevatedHelperToken))
        {
            return WindowsHelperStopResult.EndpointUnavailable;
        }

        try
        {
            var timeout = TimeSpan.FromSeconds(5);
            using var client = HttpClientFactory.CreateLoopbackClient(timeout);
            var stopPath = force ? "stop?force=1" : "stop";
            if (pid is > 0)
            {
                stopPath += force ? $"&pid={pid.Value}" : $"?pid={pid.Value}";
            }

            using var message = new HttpRequestMessage(
                HttpMethod.Post,
                GetWindowsHelperUri(stopPath));
            message.Headers.Add(WindowsElevatedHelperTokenHeader, _windowsElevatedHelperToken);

            var sendTask = client.SendAsync(message);
            var completed = await Task.WhenAny(sendTask, Task.Delay(timeout));

            if (completed != sendTask || !sendTask.IsCompletedSuccessfully)
            {
                return WindowsHelperStopResult.EndpointUnavailable;
            }

            using var response = sendTask.Result;
            if (!response.IsSuccessStatusCode)
            {
                return WindowsHelperStopResult.EndpointUnavailable;
            }

            var payload = (await response.Content.ReadAsStringAsync()).Trim();
            if (string.IsNullOrWhiteSpace(payload))
            {
                return WindowsHelperStopResult.Success;
            }

            WindowsHelperActionResponse? result = null;
            try
            {
                result = JsonSerializer.Deserialize(
                    payload,
                    CartonCoreJsonContext.Default.WindowsHelperActionResponse);
            }
            catch
            {
            }

            return result == null || result.Success
                ? WindowsHelperStopResult.Success
                : WindowsHelperStopResult.ActionFailed;
        }
        catch
        {
            return WindowsHelperStopResult.EndpointUnavailable;
        }
    }

    private async Task<bool> TryStopViaFreshWindowsElevatedHelperAsync(int pid, bool force)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return false;
        }

        var executablePath = WindowsElevatedHelperTaskUtility.ResolveHelperExecutablePath(Environment.ProcessPath);
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        var previousToken = _windowsElevatedHelperToken;
        var previousHelperPid = _windowsElevatedHelperPid;
        _windowsElevatedHelperToken = null;
        _windowsElevatedHelperPid = null;

        try
        {
            if (!await TryStartWindowsElevatedHelperViaScheduledTaskAsync(
                    executablePath,
                    Guid.NewGuid().ToString("N"),
                    Environment.ProcessId))
            {
                return false;
            }

            return await TryStopViaWindowsElevatedHelperAsync(force, pid) == WindowsHelperStopResult.Success;
        }
        finally
        {
            if (string.IsNullOrWhiteSpace(_windowsElevatedHelperToken))
            {
                _windowsElevatedHelperToken = previousToken;
                _windowsElevatedHelperPid = previousHelperPid;
            }
        }
    }

    private async Task TryShutdownWindowsElevatedHelperAsync()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var shutdownSucceeded = false;
        var token = _windowsElevatedHelperToken;
        if (!string.IsNullOrWhiteSpace(token))
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                using var message = new HttpRequestMessage(HttpMethod.Post, GetWindowsHelperUri("shutdown"));
                message.Headers.Add(WindowsElevatedHelperTokenHeader, token);
                using var response = await _httpClient.SendAsync(message, cts.Token);
                shutdownSucceeded = response.IsSuccessStatusCode;
            }
            catch
            {
            }
        }

        if (!shutdownSucceeded && _windowsElevatedHelperPid.HasValue)
        {
            try
            {
                using var process = Process.GetProcessById(_windowsElevatedHelperPid.Value);
                if (!process.HasExited)
                {
                    process.Kill(true);
                    process.WaitForExit(2000);
                }

                shutdownSucceeded = true;
            }
            catch
            {
            }
        }

        if (shutdownSucceeded)
        {
            _windowsElevatedHelperToken = null;
            _windowsElevatedHelperPid = null;
        }
    }

    private void TryKillWindowsHelperProcess()
    {
        try
        {
            if (_windowsElevatedHelperPid.HasValue)
            {
                using var process = Process.GetProcessById(_windowsElevatedHelperPid.Value);
                if (!process.HasExited)
                {
                    process.Kill(true);
                }
            }
        }
        catch
        {
        }

        _windowsElevatedHelperToken = null;
        _windowsElevatedHelperPid = null;
    }

    private void TryShutdownWindowsHelperWithoutThrow()
    {
        TryKillWindowsHelperProcess();
    }

    private void InvalidateWindowsElevatedHelperSession()
    {
        _windowsElevatedHelperToken = null;
        _windowsElevatedHelperPid = null;
    }

    private void WriteStopSignalFile()
    {
        try
        {
            var signalPath = Path.Combine(
                Path.GetDirectoryName(Environment.ProcessPath) ?? ".", ".carton-stop-signal");
            File.WriteAllText(signalPath, "stop");
        }
        catch
        {
        }
    }

    private static string EscapeForPowerShellSingleQuoted(string value)
    {
        return value.Replace("'", "''");
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private async Task<WindowsHelperProcessStatusResponse?> TryGetWindowsHelperProcessStatusAsync(
        bool includeStartupLogs = false)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
            string.IsNullOrWhiteSpace(_windowsElevatedHelperToken))
        {
            return null;
        }

        try
        {
            using var cts = new CancellationTokenSource(LocalApiProbeTimeout);
            using var client = HttpClientFactory.CreateLoopbackClient(LocalApiProbeTimeout);
            var statusPath = includeStartupLogs
                ? $"status?afterStartupLogSequence={Interlocked.Read(ref _windowsStartupLogSequence)}"
                : "status";
            using var message = new HttpRequestMessage(HttpMethod.Get, GetWindowsHelperUri(statusPath));
            message.Headers.Add(WindowsElevatedHelperTokenHeader, _windowsElevatedHelperToken);
            using var response = await client.SendAsync(message, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            return await JsonSerializer.DeserializeAsync(
                stream,
                CartonCoreJsonContext.Default.WindowsHelperProcessStatusResponse,
                cts.Token);
        }
        catch
        {
            return null;
        }
    }

    private void EmitWindowsHelperStartupLogs(WindowsHelperProcessStatusResponse status)
    {
        if (!IsStartupLogCaptureActive())
        {
            return;
        }

        if (status.StartupLogGap && !Volatile.Read(ref _reportedWindowsStartupLogGap))
        {
            Volatile.Write(ref _reportedWindowsStartupLogGap, true);
            LogManager("[WARN] Some early sing-box startup logs were dropped because the startup log buffer overflowed");
        }

        var lines = status.StartupLogs;
        if (lines == null || lines.Count == 0)
        {
            return;
        }

        foreach (var line in lines)
        {
            if (line.Sequence <= Interlocked.Read(ref _windowsStartupLogSequence) ||
                string.IsNullOrEmpty(line.Message))
            {
                continue;
            }

            LogStartupKernel(line.Message);
            Interlocked.Exchange(ref _windowsStartupLogSequence, line.Sequence);
        }
    }

    private void TryInitializeWindowsProcessJob()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        try
        {
            _windowsJobHandle = CreateJobObject(IntPtr.Zero, null);
            if (_windowsJobHandle == IntPtr.Zero)
            {
                var code = Marshal.GetLastWin32Error();
                LogManager($"[WARN] Failed to create Windows job object: {code}");
                return;
            }

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JobObjectLimitKillOnJobClose
                }
            };

            var length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            var ptr = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                var success = SetInformationJobObject(
                    _windowsJobHandle,
                    JobObjectInfoType.ExtendedLimitInformation,
                    ptr,
                    (uint)length);
                if (!success)
                {
                    var code = Marshal.GetLastWin32Error();
                    LogManager($"[WARN] Failed to configure Windows job object: {code}");
                    CloseHandle(_windowsJobHandle);
                    _windowsJobHandle = IntPtr.Zero;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
        catch (Exception ex)
        {
            LogManager($"[WARN] Failed to initialize Windows job object: {ex.Message}");
            if (_windowsJobHandle != IntPtr.Zero)
            {
                CloseHandle(_windowsJobHandle);
                _windowsJobHandle = IntPtr.Zero;
            }
        }
    }

    private void TryAttachProcessToWindowsJob(Process process)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
            _windowsJobHandle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var success = AssignProcessToJobObject(_windowsJobHandle, process.Handle);
            if (!success)
            {
                var code = Marshal.GetLastWin32Error();
                LogManager($"[WARN] Failed to attach sing-box to Windows job object: {code}");
            }
        }
        catch (Exception ex)
        {
            LogManager($"[WARN] Failed to attach sing-box to Windows job object: {ex.Message}");
        }
    }

    private void TryAttachProcessIdToWindowsJob(int pid)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
            _windowsJobHandle == IntPtr.Zero ||
            pid <= 0)
        {
            return;
        }

        if (!IsCurrentProcessElevatedOnWindows())
        {
            return;
        }

        IntPtr processHandle = IntPtr.Zero;
        try
        {
            processHandle = OpenProcess(ProcessSetQuota | ProcessTerminate, false, pid);
            if (processHandle == IntPtr.Zero)
            {
                var openCode = Marshal.GetLastWin32Error();
                if (openCode == 5)
                {
                    return;
                }
                LogManager($"[WARN] Failed to open sing-box process {pid} for job object attach: {openCode}");
                return;
            }

            var success = AssignProcessToJobObject(_windowsJobHandle, processHandle);
            if (!success)
            {
                var code = Marshal.GetLastWin32Error();
                LogManager($"[WARN] Failed to attach sing-box process {pid} to Windows job object: {code}");
            }
        }
        catch (Exception ex)
        {
            LogManager($"[WARN] Failed to attach sing-box process {pid} to Windows job object: {ex.Message}");
        }
        finally
        {
            if (processHandle != IntPtr.Zero)
            {
                CloseHandle(processHandle);
            }
        }
    }

    private static bool IsCurrentProcessElevatedOnWindows()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return false;
        }

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private const uint JobObjectLimitKillOnJobClose = 0x00002000;

    private enum JobObjectInfoType
    {
        ExtendedLimitInformation = 9
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public IntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob,
        JobObjectInfoType infoType,
        IntPtr lpJobObjectInfo,
        uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint ProcessTerminate = 0x0001;
    private const uint ProcessSetQuota = 0x0100;
}
