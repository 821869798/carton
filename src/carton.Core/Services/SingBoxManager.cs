using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using carton.Core.Models;
using carton.Core.Utilities;

namespace carton.Core.Services;

public interface ISingBoxManager
{
    event EventHandler<ServiceStatus>? StatusChanged;
    event EventHandler<TrafficInfo>? TrafficUpdated;
    event EventHandler<long>? MemoryUpdated;
    event EventHandler<string>? ManagerLogReceived;
    event EventHandler<string>? LogReceived;

    ServiceState State { get; }
    bool IsRunning { get; }

    Task<bool> SyncRunningStateAsync();
    Task<bool> StartAsync(string configPath);
    Task<(bool Success, string Message)> CheckConfigAsync(string configContent);
    Task StopAsync();
    Task ReloadAsync();
    Task<List<OutboundGroup>> GetOutboundGroupsAsync();
    Task SelectOutboundAsync(string groupTag, string outboundTag);
    Task<Dictionary<string, int>> RunGroupDelayTestAsync(string groupTag, string? testUrl = null, int timeoutMs = 5000);
    Task<Dictionary<string, int>> RunOutboundDelayTestsAsync(IEnumerable<string> outboundTags, string? testUrl = null, int timeoutMs = 5000);
    long? GetRunningProcessMemoryBytes();
    Task<List<ConnectionInfo>> GetConnectionsAsync();
    Task CloseConnectionAsync(string connectionId);
    Task CloseAllConnectionsAsync();

    Task<bool> IsLinuxCoreAuthorizedAsync();
    Task<(bool Success, string? Error)> AuthorizeCoreOnLinuxAsync(string password);
    void UpdateKernelPath(string singBoxPath);

    /// <summary>
    /// Notifies the manager whether a system proxy was configured for the current
    /// sing-box session. When <paramref name="enabled"/> is <see langword="true"/>
    /// the system proxy will be cleared automatically on the next Stop.
    /// </summary>
    void NotifySystemProxyEnabled(bool enabled);
}

public partial class SingBoxManager : ISingBoxManager, IDisposable
{
    private string _singBoxPath;
    private readonly string _workingDirectory;
    private Process? _process;
    private readonly ServiceState _state = new();
    private HttpClient _httpClient => HttpClientFactory.LocalApi;
    private string _apiAddress => HttpClientFactory.LocalApiAddress;
    private int _apiPort => HttpClientFactory.LocalApiPort;
    private bool _disposed;
    private readonly ConcurrentQueue<string> _errorOutput = new();
    private int? _elevatedPid;
    private string? _elevatedLogPath;
    private CancellationTokenSource? _elevatedLogCts;
    private Task? _elevatedLogTask;
    private Task? _trafficMonitorTask;
    private Task? _memoryMonitorTask;
    private IntPtr _windowsJobHandle = IntPtr.Zero;
    private string? _windowsElevatedHelperToken;
    private int? _windowsElevatedHelperPid;
    private string? _lastStartupWaitFailureReason;
    /// <summary>Whether the current/last session had system-proxy enabled.</summary>
    private bool _systemProxyEnabled;

    public event EventHandler<ServiceStatus>? StatusChanged;
    public event EventHandler<TrafficInfo>? TrafficUpdated;
    public event EventHandler<long>? MemoryUpdated;
    public event EventHandler<string>? ManagerLogReceived;
    public event EventHandler<string>? LogReceived;

    public ServiceState State => _state;
    public bool IsRunning => _state.Status == ServiceStatus.Running;

    public SingBoxManager(string singBoxPath, string workingDirectory, int apiPort = 9090)
    {
        _singBoxPath = singBoxPath;
        _workingDirectory = workingDirectory;


        Directory.CreateDirectory(_workingDirectory);
        Directory.CreateDirectory(Path.Combine(_workingDirectory, "logs"));
        Directory.CreateDirectory(Path.Combine(_workingDirectory, "cache"));

        AppDomain.CurrentDomain.ProcessExit += OnCurrentProcessExit;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            TryInitializeWindowsProcessJob();
        }
    }

    public void UpdateKernelPath(string singBoxPath)
    {
        if (string.IsNullOrWhiteSpace(singBoxPath))
        {
            return;
        }

        _singBoxPath = singBoxPath;
    }

    public async Task<bool> SyncRunningStateAsync()
    {
        if (await IsApiReachableAsync())
        {
            if (_state.Status != ServiceStatus.Running)
            {
                _state.StartTime ??= DateTime.Now;
                UpdateStatus(ServiceStatus.Running);
                LogManager("[INFO] Detected existing sing-box instance, synchronized running state");
            }

            if (!_elevatedPid.HasValue)
            {
                _elevatedPid = await TryFindProcessPidByApiPortAsync();
            }

            EnsureRuntimeMonitorsRunning();
            return true;
        }

        if (_state.Status == ServiceStatus.Running)
        {
            _state.StartTime = null;
            UpdateStatus(ServiceStatus.Stopped);
        }

        return false;
    }

    public async Task<bool> StartAsync(string configPath)
    {
        var timing = Stopwatch.StartNew();
        LogTiming("start.begin");
        if (_state.Status == ServiceStatus.Running)
        {
            LogTiming("start.skip_already_running", timing.Elapsed);
            return true;
        }

        UpdateStatus(ServiceStatus.Starting);

        var leftoverTiming = Stopwatch.StartNew();
        if (await HasLeftoverSingBoxProcessAsync())
        {
            LogTiming("start.leftover_detected", leftoverTiming.Elapsed);
            LogManager("[WARN] Cleaning up leftover sing-box process before starting a new session");
            await StopAsync();
            leftoverTiming.Restart();
            if (await HasLeftoverSingBoxProcessAsync())
            {
                LogTiming("start.leftover_cleanup_failed", leftoverTiming.Elapsed);
                const string error = "Failed to clean up previous sing-box process before start";
                LogManager($"[ERROR] {error}");
                SetError(error);
                return false;
            }

            LogTiming("start.leftover_cleanup_complete", leftoverTiming.Elapsed);
            UpdateStatus(ServiceStatus.Starting);
        }
        else
        {
            LogTiming("start.leftover_check", leftoverTiming.Elapsed);
        }

        if (!File.Exists(configPath))
        {
            var error = $"Configuration file not found: {configPath}";
            LogManager($"[ERROR] {error}");
            SetError(error);
            return false;
        }

        if (!File.Exists(_singBoxPath))
        {
            var error = $"sing-box binary not found at: {_singBoxPath}";
            LogManager($"[ERROR] {error}");
            SetError(error);
            return false;
        }

        try
        {
            _errorOutput.Clear();
            ResetSessionMetrics();

            LogManager($"[INFO] Starting sing-box with config: {configPath}");
            LogManager($"[INFO] Binary path: {_singBoxPath}");

            if (RequiresElevatedPrivileges(configPath))
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && await IsLinuxCoreAuthorizedAsync())
                {
                    LogManager("[INFO] TUN inbound detected, sing-box has setuid bit — using normal start path");
                }
                else
                {
                    LogManager("[INFO] TUN inbound detected, requesting elevated privileges...");
                    var elevatedResult = await StartElevatedAsync(configPath);
                    LogTiming(elevatedResult ? "start.end_success_elevated" : "start.end_failed_elevated", timing.Elapsed);
                    return elevatedResult;
                }
            }

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _singBoxPath,
                    Arguments = $"run -c \"{configPath}\"",
                    WorkingDirectory = _workingDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                }
            };
            _process = process;
            ApplyLinuxLibrarySearchPath(process.StartInfo);

            process.OutputDataReceived += (_, e) =>
            {
                // Process redirection / exit callbacks run on thread-pool threads.
                // Any exception escaping here would terminate the whole app via
                // fail-fast (STATUS_STACK_BUFFER_OVERRUN / 0xC0000409) — the dialog
                // users see as "stack-based buffer overrun". Never let one escape.
                try
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        LogKernel(e.Data);
                    }
                }
                catch (Exception ex)
                {
                    TryLogCallbackFailure("OutputDataReceived", ex);
                }
            };

            process.ErrorDataReceived += (_, e) =>
            {
                try
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        _errorOutput.Enqueue(e.Data);
                        LogKernel($"[ERROR] {e.Data}");
                    }
                }
                catch (Exception ex)
                {
                    TryLogCallbackFailure("ErrorDataReceived", ex);
                }
            };

            process.Exited += (_, _) =>
            {
                try
                {
                    var exitCode = process.ExitCode;
                    if (_state.Status == ServiceStatus.Running || _state.Status == ServiceStatus.Starting)
                    {
                        var errorMsg = $"sing-box exited with code {exitCode}";
                        if (!_errorOutput.IsEmpty)
                        {
                            errorMsg += $": {string.Join("\n", _errorOutput)}";
                        }
                        LogManager($"[ERROR] {errorMsg}");
                        SetError(errorMsg);
                    }
                }
                catch (Exception ex)
                {
                    // Swallow everything — including ObjectDisposedException /
                    // InvalidOperationException raised when a concurrent Stop()
                    // disposed the process (e.g. sing-box exiting on a Wi-Fi
                    // switch while the user/app is also stopping it).
                    TryLogCallbackFailure("Process.Exited", ex);
                }
            };

            process.EnableRaisingEvents = true;

            LogManager("[INFO] Starting process...");
            var processStartTiming = Stopwatch.StartNew();
            process.Start();
            TryAttachProcessToWindowsJob(process);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            LogTiming("start.process_started", processStartTiming.Elapsed);

            var readyTiming = Stopwatch.StartNew();
            var ready = await WaitForApiReadyAsync(null, TimeSpan.FromSeconds(25));
            LogTiming(ready ? "start.api_ready" : "start.api_not_ready", readyTiming.Elapsed);
            if (!ready)
            {
                if (_process.HasExited)
                {
                    var exitCode = _process.ExitCode;
                    var errorMsg = $"sing-box process exited unexpectedly with code {exitCode}";
                    if (!_errorOutput.IsEmpty)
                    {
                        errorMsg += $"\n{string.Join("\n", _errorOutput)}";
                    }
                    LogManager($"[ERROR] {errorMsg}");
                    await CleanupFailedStartAttemptAsync();
                    SetError(errorMsg);
                    return false;
                }

                var msg = "sing-box API did not become reachable in time";
                LogManager($"[ERROR] {msg}");
                await CleanupFailedStartAttemptAsync();
                SetError(msg);
                return false;
            }

            _state.StartTime = DateTime.Now;
            UpdateStatus(ServiceStatus.Running);
            LogManager("[INFO] sing-box started successfully");

            EnsureRuntimeMonitorsRunning();

            LogTiming("start.end_success", timing.Elapsed);
            return true;
        }
        catch (Exception ex)
        {
            var error = $"Failed to start sing-box: {ex.Message}";
            LogManager($"[ERROR] {error}");
            await CleanupFailedStartAttemptAsync();
            SetError(error);
            LogTiming("start.end_exception", timing.Elapsed);
            return false;
        }
    }

    public async Task<(bool Success, string Message)> CheckConfigAsync(string configContent)
    {
        if (string.IsNullOrWhiteSpace(configContent))
        {
            return (false, "Configuration content is empty.");
        }

        if (!File.Exists(_singBoxPath))
        {
            return (false, $"sing-box binary not found at: {_singBoxPath}");
        }

        var tempConfigPath = Path.Combine(Path.GetTempPath(), $"carton-check-{Guid.NewGuid():N}.json");

        try
        {
            await File.WriteAllTextAsync(tempConfigPath, configContent, new UTF8Encoding(false));

            var startInfo = new ProcessStartInfo
            {
                FileName = _singBoxPath,
                Arguments = $"check -c \"{tempConfigPath}\"",
                WorkingDirectory = _workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            ApplyLinuxLibrarySearchPath(startInfo);

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return (false, "Failed to start sing-box check process.");
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            await Task.WhenAll(stdoutTask, stderrTask, process.WaitForExitAsync());

            var stdout = (await stdoutTask).Trim();
            var stderr = (await stderrTask).Trim();

            var output = string.Join(
                Environment.NewLine + Environment.NewLine,
                new[] { stdout, stderr }.Where(s => !string.IsNullOrWhiteSpace(s)));

            if (string.IsNullOrWhiteSpace(output))
            {
                output = process.ExitCode == 0
                    ? "sing-box check succeeded."
                    : $"sing-box check failed with exit code {process.ExitCode}.";
            }

            return (process.ExitCode == 0, output);
        }
        catch (Exception ex)
        {
            return (false, $"Failed to run sing-box check: {ex.Message}");
        }
        finally
        {
            try
            {
                if (File.Exists(tempConfigPath))
                {
                    File.Delete(tempConfigPath);
                }
            }
            catch
            {
                // Ignore temp cleanup errors.
            }
        }
    }

    private void ApplyLinuxLibrarySearchPath(ProcessStartInfo startInfo)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return;
        }

        var libraryDirectory = Path.GetDirectoryName(_singBoxPath);
        if (string.IsNullOrWhiteSpace(libraryDirectory))
        {
            return;
        }

        var current = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
        startInfo.Environment["LD_LIBRARY_PATH"] = string.IsNullOrWhiteSpace(current)
            ? libraryDirectory
            : $"{libraryDirectory}:{current}";
    }

    private string BuildLinuxLibrarySearchPathPrefix()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return string.Empty;
        }

        var libraryDirectory = Path.GetDirectoryName(_singBoxPath);
        if (string.IsNullOrWhiteSpace(libraryDirectory))
        {
            return string.Empty;
        }

        return $"export LD_LIBRARY_PATH={QuoteShellArg(libraryDirectory)}${{LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}}; ";
    }

    public async Task StopAsync()
    {
        var timing = Stopwatch.StartNew();
        LogTiming("stop.begin");
        var canUseElevatedStop = CanUseElevatedStop();
        var hasTargetProcess = _process != null || canUseElevatedStop;
        if (_process == null && !canUseElevatedStop)
        {
            var discoverTiming = Stopwatch.StartNew();
            _elevatedPid = await TryFindProcessPidByApiPortAsync();
            LogTiming("stop.discover_pid_by_api_port", discoverTiming.Elapsed);
            canUseElevatedStop = CanUseElevatedStop();
            hasTargetProcess = canUseElevatedStop;
        }

        try
        {
            LogManager("[INFO] Stopping sing-box...");
            UpdateStatus(ServiceStatus.Stopping);
            var stopped = true;

            if (_process != null)
            {
                var managedStopTiming = Stopwatch.StartNew();
                await StopManagedProcessAsync(_process);
                LogTiming("stop.managed_process_stopped", managedStopTiming.Elapsed);
                _process.Dispose();
                _process = null;
            }
            else if (canUseElevatedStop)
            {
                var elevatedStopTiming = Stopwatch.StartNew();
                stopped = await StopElevatedAsync();
                LogTiming(stopped ? "stop.elevated_stopped" : "stop.elevated_failed", elevatedStopTiming.Elapsed);
            }

            if (!stopped && hasTargetProcess)
            {
                var error = "Failed to stop sing-box: elevated process is still running";
                LogManager($"[ERROR] {error}");
                SetError(error);
                return;
            }

            await StopElevatedLogTailAsync();
            _elevatedPid = null;
            _elevatedLogPath = null;
            ResetSessionMetrics();

            // Ensure the system proxy is cleared even if sing-box did not
            // have a chance to clean it up itself (e.g. process was killed).
            if (_systemProxyEnabled)
            {
                var proxyTiming = Stopwatch.StartNew();
                SystemProxyHelper.ClearSystemProxy();
                LogTiming("stop.system_proxy_cleared", proxyTiming.Elapsed);
                _systemProxyEnabled = false;
            }

            _state.StartTime = null;
            UpdateStatus(ServiceStatus.Stopped);
            LogManager("[INFO] sing-box stopped");
            LogTiming("stop.end_success", timing.Elapsed);
        }
        catch (Exception ex)
        {
            var error = $"Failed to stop sing-box: {ex.Message}";
            LogManager($"[ERROR] {error}");
            SetError(error);
            LogTiming("stop.end_exception", timing.Elapsed);
        }
    }

    /// <inheritdoc />
    public void NotifySystemProxyEnabled(bool enabled)
    {
        _systemProxyEnabled = enabled;
    }

    public async Task ReloadAsync()
    {
        if (_state.Status != ServiceStatus.Running)
        {
            return;
        }

        try
        {
            var response = await _httpClient.PutAsync($"{_apiAddress}/configs", new StringContent(""));
            response.EnsureSuccessStatusCode();
        }
        catch
        {
        }

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                TryShutdownWindowsHelperWithoutThrow();
            }
        }
        catch
        {
        }
    }

    private void UpdateStatus(ServiceStatus status)
    {
        _state.Status = status;
        _state.ErrorMessage = null;
        StatusChanged?.Invoke(this, status);
    }

    private void LogManager(string message)
    {
        ManagerLogReceived?.Invoke(this, message);
    }

    /// <summary>
    /// Reports an exception that was swallowed inside a thread-pool callback
    /// (process events) so it never escapes and fail-fasts the process.
    /// The logging itself is guarded because event subscribers may throw too.
    /// </summary>
    private void TryLogCallbackFailure(string source, Exception ex)
    {
        try
        {
            ManagerLogReceived?.Invoke(this, $"[WARN] Ignored exception in {source} callback to avoid crashing the process: {ex.Message}");
        }
        catch
        {
            // Logging must never crash the process from a thread-pool callback.
        }
    }

    [Conditional("DEBUG")]
    private void LogTiming(string stage, TimeSpan? elapsed = null)
    {
        var elapsedText = elapsed.HasValue ? $" {elapsed.Value.TotalMilliseconds:F0}ms" : string.Empty;
        var message = $"[TIMING] {DateTimeOffset.Now:O} {stage}{elapsedText}";
        LogManager(message);

        try
        {
            var timingLogPath = Path.Combine(_workingDirectory, "logs", "timing.log");
            Directory.CreateDirectory(Path.GetDirectoryName(timingLogPath)!);
            File.AppendAllText(timingLogPath, message + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {
            // Timing logs must never affect core lifecycle operations.
        }
    }

    private void LogKernel(string message)
    {
        LogReceived?.Invoke(this, message);
    }

    private void SetError(string message)
    {
        _state.Status = ServiceStatus.Error;
        _state.ErrorMessage = message;
        StatusChanged?.Invoke(this, ServiceStatus.Error);
    }

    private bool HasCleanupCandidate()
    {
        return _process != null || CanUseElevatedStop();
    }

    private async Task<bool> HasLeftoverSingBoxProcessAsync()
    {
        if (_process != null)
        {
            return true;
        }

        if (_elevatedPid.HasValue && IsProcessAlive(_elevatedPid.Value))
        {
            return true;
        }

        var helperStatus = await TryGetWindowsHelperProcessStatusAsync();
        if (helperStatus is { IsRunning: true })
        {
            if (helperStatus.Pid is > 0)
            {
                _elevatedPid = helperStatus.Pid.Value;
            }

            return true;
        }

        var discoveredPid = await TryFindProcessPidByApiPortAsync();
        if (discoveredPid.HasValue && discoveredPid.Value > 0)
        {
            _elevatedPid = discoveredPid.Value;
            return true;
        }

        return false;
    }

    private bool CanUseElevatedStop()
    {
        return _elevatedPid.HasValue ||
               (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
                !string.IsNullOrWhiteSpace(_windowsElevatedHelperToken));
    }

    private async Task CleanupFailedStartAttemptAsync()
    {
        if (!HasCleanupCandidate())
        {
            return;
        }

        try
        {
            await StopAsync();
        }
        catch (Exception ex)
        {
            LogManager($"[WARN] Failed to clean up sing-box after a start failure: {ex.Message}");
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            return !proc.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> IsProcessRunningAsync(int pid)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "tasklist",
                    Arguments = $"/FI \"PID eq {pid}\" /FO CSV /NH",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            return process.ExitCode == 0 &&
                   output.Contains($"\"{pid}\"", StringComparison.OrdinalIgnoreCase);
        }

        using var unixProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "ps",
                Arguments = $"-p {pid} -o pid=",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        unixProcess.Start();
        var unixOutput = await unixProcess.StandardOutput.ReadToEndAsync();
        await unixProcess.WaitForExitAsync();
        return unixProcess.ExitCode == 0 && !string.IsNullOrWhiteSpace(unixOutput);
    }

    private void ResetSessionMetrics()
    {
        _state.UploadSpeed = 0;
        _state.DownloadSpeed = 0;
        _state.TotalUpload = 0;
        _state.TotalDownload = 0;
        _state.ConnectionCount = 0;
        _state.MemoryInUse = 0;
    }

    private async Task StopManagedProcessAsync(Process process)
    {
        if (process.HasExited)
        {
            return;
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                using var signalProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "kill",
                        Arguments = $"-TERM {process.Id}",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };

                signalProcess.Start();
                await signalProcess.WaitForExitAsync();
            }
            catch (Exception ex)
            {
                LogManager($"[WARN] Failed to send SIGTERM to sing-box process {process.Id}: {ex.Message}");
            }

            for (var i = 0; i < 20; i++)
            {
                if (process.HasExited)
                {
                    return;
                }

                await Task.Delay(250);
            }

            LogManager($"[WARN] sing-box process {process.Id} did not exit after SIGTERM, forcing termination");
        }

        if (!process.HasExited)
        {
            process.Kill(true);
            await process.WaitForExitAsync();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        AppDomain.CurrentDomain.ProcessExit -= OnCurrentProcessExit;
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Write signal file so helper knows to stop sing-box,
            // then let helper self-terminate via parent death detection
            WriteStopSignalFile();
        }

        _process?.Kill(true);
        _process?.Dispose();
        _elevatedLogCts?.Cancel();
        _elevatedLogCts?.Dispose();


        if (_windowsJobHandle != IntPtr.Zero)
        {
            CloseHandle(_windowsJobHandle);
            _windowsJobHandle = IntPtr.Zero;
        }
    }

    private void OnCurrentProcessExit(object? sender, EventArgs e)
    {
        TryForceStopWithoutThrow();
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        TryForceStopWithoutThrow();
    }

    private void TryForceStopWithoutThrow()
    {
        try
        {
            _process?.Kill(true);
        }
        catch
        {
        }

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Write signal file, DON'T kill helper - let it detect
                // the signal or parent death and clean up sing-box itself
                WriteStopSignalFile();
            }
        }
        catch
        {
        }

        try
        {
            if (_elevatedPid.HasValue && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "taskkill",
                        Arguments = $"/PID {_elevatedPid.Value} /T /F",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                process.WaitForExit(3000);
            }
        }
        catch
        {
        }

        // Clear system proxy if it was enabled, so it is not left active
        // after a forced/unexpected process exit.
        try
        {
            if (_systemProxyEnabled)
            {
                SystemProxyHelper.ClearSystemProxy();
                _systemProxyEnabled = false;
            }
        }
        catch
        {
        }
    }

    private static string QuoteShellArg(string value)
    {
        return $"'{value.Replace("'", "'\"'\"'")}'";
    }

    private static async Task<string> ReadRecentLogLinesAsync(string path, int maxLines)
    {
        try
        {
            if (!File.Exists(path))
            {
                return string.Empty;
            }

            var lines = await File.ReadAllLinesAsync(path);
            if (lines.Length == 0)
            {
                return string.Empty;
            }

            return string.Join(" | ", lines.TakeLast(Math.Max(1, maxLines)));
        }
        catch
        {
            return string.Empty;
        }
    }
}
