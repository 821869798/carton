using System.Diagnostics;
using System.Runtime.InteropServices;
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
    private const string WindowsElevatedHelperArg = "--carton-elevated-helper";
    private const string WindowsElevatedHelperTokenHeader = "X-Carton-Helper-Token";

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
            var request = new WindowsHelperStartRequest
            {
                SingBoxPath = _singBoxPath,
                ConfigPath = configPath,
                WorkingDirectory = _workingDirectory,
                LogPath = logPath
            };

            var json = JsonSerializer.Serialize(request, CartonCoreJsonContext.Default.WindowsHelperStartRequest);

            int? pid = null;
            try
            {
                LogManager("[INFO] Sending start request to elevated helper...");
                using var sendClient = new HttpClient();
                using var message = new HttpRequestMessage(HttpMethod.Post, GetWindowsHelperUri("start"))
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                message.Headers.Add(WindowsElevatedHelperTokenHeader, _windowsElevatedHelperToken!);

                // Fire the request - don't wait for response (TUN will likely block it)
                _ = sendClient.SendAsync(message);
                // Brief delay to let the helper receive and process the request
                await Task.Delay(1000);
            }
            catch (Exception ex)
            {
                LogManager($"[WARN] Failed to send start request: {ex.Message}");
            }

            return new ElevatedStartResult
            {
                Success = true,
                Pid = pid
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

    private async Task<bool> EnsureWindowsElevatedHelperRunningAsync()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(_windowsElevatedHelperToken) &&
            await PingWindowsElevatedHelperAsync(_windowsElevatedHelperToken))
        {
            return true;
        }

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            LogManager("[ERROR] Unable to resolve current executable path for elevated helper");
            return false;
        }

        var token = Guid.NewGuid().ToString("N");
        var parentPid = Environment.ProcessId;
        var helperArgs =
            $"{WindowsElevatedHelperArg} --port {WindowsElevatedHelperPort} --token {token} --parent-pid {parentPid}";
        var script =
            "$p = Start-Process -FilePath " +
            $"'{EscapeForPowerShellSingleQuoted(executablePath)}' " +
            "-ArgumentList " +
            $"'{EscapeForPowerShellSingleQuoted(helperArgs)}' " +
            $"-WorkingDirectory '{EscapeForPowerShellSingleQuoted(_workingDirectory)}' " +
            "-Verb RunAs -WindowStyle Hidden -PassThru; " +
            "$p.Id";

        var result = await RunWindowsUacCommandAsync(script);
        if (!result.Success)
        {
            LogManager($"[ERROR] {result.ErrorMessage ?? "Failed to start elevated helper"}");
            return false;
        }

        if (int.TryParse(result.RawOutput, out var helperPid) && helperPid > 0)
        {
            _windowsElevatedHelperPid = helperPid;
        }

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
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var message = new HttpRequestMessage(HttpMethod.Get, GetWindowsHelperUri("ping"));
            message.Headers.Add(WindowsElevatedHelperTokenHeader, token);
            using var response = await _httpClient.SendAsync(message, cts.Token);
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

    private async Task<bool> TryStopViaWindowsElevatedHelperAsync(bool force)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
            string.IsNullOrWhiteSpace(_windowsElevatedHelperToken))
        {
            return false;
        }

        try
        {
            using var client = new HttpClient();
            using var message = new HttpRequestMessage(
                HttpMethod.Post,
                GetWindowsHelperUri(force ? "stop?force=1" : "stop"));
            message.Headers.Add(WindowsElevatedHelperTokenHeader, _windowsElevatedHelperToken);

            var sendTask = client.SendAsync(message);
            var completed = await Task.WhenAny(sendTask, Task.Delay(3000));

            if (completed != sendTask || !sendTask.IsCompletedSuccessfully)
            {
                return false;
            }

            using var response = sendTask.Result;
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var payload = (await response.Content.ReadAsStringAsync()).Trim();
            if (string.IsNullOrWhiteSpace(payload))
            {
                return true;
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

            return result == null || result.Success;
        }
        catch
        {
            return false;
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
