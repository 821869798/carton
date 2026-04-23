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

    /// <summary>
    /// Notifies the manager whether a system proxy was configured for the current
    /// sing-box session. When <paramref name="enabled"/> is <see langword="true"/>
    /// the system proxy will be cleared automatically on the next Stop.
    /// </summary>
    void NotifySystemProxyEnabled(bool enabled);
}

public partial class SingBoxManager : ISingBoxManager, IDisposable
{
    private readonly string _singBoxPath;
    private readonly string _workingDirectory;
    private Process? _process;
    private readonly ServiceState _state = new();
    private HttpClient _httpClient => HttpClientFactory.LocalApi;
    private string _apiAddress => HttpClientFactory.LocalApiAddress;
    private int _apiPort => HttpClientFactory.LocalApiPort;
    private bool _disposed;
    private readonly List<string> _errorOutput = new();
    private int? _elevatedPid;
    private string? _elevatedLogPath;
    private CancellationTokenSource? _elevatedLogCts;
    private Task? _elevatedLogTask;
    private Task? _trafficMonitorTask;
    private Task? _memoryMonitorTask;
    private IntPtr _windowsJobHandle = IntPtr.Zero;
    private string? _windowsElevatedHelperToken;
    private int? _windowsElevatedHelperPid;
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
        if (_state.Status == ServiceStatus.Running)
        {
            return true;
        }

        if (HasCleanupCandidate())
        {
            LogManager("[WARN] Cleaning up leftover sing-box process before starting a new session");
            await StopAsync();
            if (await HasLeftoverSingBoxProcessAsync())
            {
                const string error = "Failed to clean up previous sing-box process before start";
                LogManager($"[ERROR] {error}");
                SetError(error);
                return false;
            }
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
            UpdateStatus(ServiceStatus.Starting);
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
                    return await StartElevatedAsync(configPath);
                }
            }

            _process = new Process
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
            ApplyLinuxLibrarySearchPath(_process.StartInfo);

            _process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    LogKernel(e.Data);
                }
            };

            _process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    _errorOutput.Add(e.Data);
                    LogKernel($"[ERROR] {e.Data}");
                }
            };

            _process.Exited += (_, _) =>
            {
                var exitCode = _process.ExitCode;
                if (_state.Status == ServiceStatus.Running || _state.Status == ServiceStatus.Starting)
                {
                    var errorMsg = $"sing-box exited with code {exitCode}";
                    if (_errorOutput.Count > 0)
                    {
                        errorMsg += $": {string.Join("\n", _errorOutput)}";
                    }
                    LogManager($"[ERROR] {errorMsg}");
                    SetError(errorMsg);
                }
            };

            _process.EnableRaisingEvents = true;

            LogManager("[INFO] Starting process...");
            _process.Start();
            TryAttachProcessToWindowsJob(_process);
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            var ready = await WaitForApiReadyAsync(null, TimeSpan.FromSeconds(25));
            if (!ready)
            {
                if (_process.HasExited)
                {
                    var exitCode = _process.ExitCode;
                    var errorMsg = $"sing-box process exited unexpectedly with code {exitCode}";
                    if (_errorOutput.Count > 0)
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

            return true;
        }
        catch (Exception ex)
        {
            var error = $"Failed to start sing-box: {ex.Message}";
            LogManager($"[ERROR] {error}");
            await CleanupFailedStartAttemptAsync();
            SetError(error);
            return false;
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
        var canUseElevatedStop = CanUseElevatedStop();
        var hasTargetProcess = _process != null || canUseElevatedStop;
        if (_process == null && !canUseElevatedStop)
        {
            _elevatedPid = await TryFindProcessPidByApiPortAsync();
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
                await StopManagedProcessAsync(_process);
                _process.Dispose();
                _process = null;
            }
            else if (canUseElevatedStop)
            {
                stopped = await StopElevatedAsync();
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
                SystemProxyHelper.ClearSystemProxy();
                _systemProxyEnabled = false;
            }

            _state.StartTime = null;
            UpdateStatus(ServiceStatus.Stopped);
            LogManager("[INFO] sing-box stopped");
        }
        catch (Exception ex)
        {
            var error = $"Failed to stop sing-box: {ex.Message}";
            LogManager($"[ERROR] {error}");
            SetError(error);
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
