using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using carton.Core.Models;
using carton.Core.Services.SingBoxApi;
using carton.Core.Utilities;

namespace carton.Core.Services;

public interface ISingBoxManager
{
    event EventHandler<ServiceStatus>? StatusChanged;
    event EventHandler<TrafficInfo>? TrafficUpdated;
    event EventHandler<long>? MemoryUpdated;
    event EventHandler<CartonLogEntry>? ManagerLogEntryReceived;
    event EventHandler<KernelLogEntry>? LogReceived;

    /// <summary>
    /// Raised when the sing-box kernel resets its log buffer; consumers should clear
    /// local kernel log buffers to avoid duplicated history replays.
    /// </summary>
    event EventHandler? KernelLogsReset;

    /// <summary>Current merged connection list from the SubscribeConnections stream.</summary>
    ConnectionsSnapshot CurrentConnections { get; }

    /// <summary>Latest groups snapshot from the SubscribeGroups stream.</summary>
    GroupsSnapshot CurrentGroups { get; }

    /// <summary>Raised after a new connection snapshot was merged from the kernel stream.</summary>
    event EventHandler<ConnectionsSnapshot>? ConnectionsUpdated;

    /// <summary>Raised after the kernel pushed a new groups snapshot.</summary>
    event EventHandler<GroupsSnapshot>? GroupsUpdated;

    /// <summary>Raised after the kernel pushed a fresh outbound mode (including other clients' changes).</summary>
    event EventHandler<string>? ModeChanged;

    /// <summary>Latest outbound mode pushed by the kernel (null before the first snapshot).</summary>
    string? CurrentMode { get; }

    /// <summary>
    /// Raised when the active kernel was rejected by the version gate (older than
    /// KernelVersionGuard.MinimumRequiredVersion). Consumers should show a blocking
    /// dialog directing the user to install a supported kernel.
    /// </summary>
    event EventHandler<string>? KernelVersionRejected;

    /// <summary>
    /// Current daemon apiVersion (0 when unknown). Optional gRPC features are gated on
    /// this value, mirroring the official dashboard's MIN_API_VERSION table:
    /// taildrop >= 4, OpenVPN/OpenConnect >= 3.
    /// </summary>
    int ApiVersion { get; }

    /// <summary>Whether the daemon API supports the given feature at its current apiVersion.</summary>
    bool SupportsApiFeature(string feature);

    /// <summary>
    /// Configuration deprecation warnings reported by the running kernel
    /// (empty when none or unavailable).
    /// </summary>
    Task<IReadOnlyList<DeprecatedConfigWarning>> GetDeprecatedWarningsAsync();

    ServiceState State { get; }
    bool IsRunning { get; }

    Task<bool> SyncRunningStateAsync();
    Task<bool> StartAsync(string configPath);
    Task<(bool Success, string Message)> CheckConfigAsync(string configContent);
    Task StopAsync();
    Task<ApiModeConfigSnapshot?> GetModeConfigAsync();
    Task<bool> SetModeAsync(string mode);
    Task<List<OutboundGroup>> GetOutboundGroupsAsync();
    Task SelectOutboundAsync(string groupTag, string outboundTag);
    Task SetGroupExpandAsync(string groupTag, bool isExpand);
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
    private const int MaxErrorOutputLines = 80;
    private const int MaxKernelOutputSummaryLines = 5;
    /// <summary>
    /// API readiness budget for a plain start. A TUN config gets extra headroom on
    /// every start path (elevated, or setuid on Linux) because device setup makes the
    /// kernel cold start slower.
    /// </summary>
    private static readonly TimeSpan ApiReadyTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TunApiReadyTimeout = TimeSpan.FromSeconds(35);
    private int _startupLogCaptureSession;
    private bool _captureStartupOutputForUi;
    private long _windowsStartupLogSequence;
    private bool _reportedWindowsStartupLogGap;
    private Task? _logMonitorTask;
    private Task? _statusMonitorTask;
    private CancellationTokenSource? _monitorCancellation;
    private IntPtr _windowsJobHandle = IntPtr.Zero;
    private string? _windowsElevatedHelperToken;
    private int? _windowsElevatedHelperPid;
    private string? _lastStartupWaitFailureReason;
    private const string LogMonitorFallbackLevel = "info";
    private string _logMonitorLevel = LogMonitorFallbackLevel;
    /// <summary>Whether the current/last session had system-proxy enabled.</summary>
    private bool _systemProxyEnabled;

    public event EventHandler<ServiceStatus>? StatusChanged;
    public event EventHandler<TrafficInfo>? TrafficUpdated;
    public event EventHandler<long>? MemoryUpdated;
    public event EventHandler<CartonLogEntry>? ManagerLogEntryReceived;
    public event EventHandler<KernelLogEntry>? LogReceived;

    /// <summary>
    /// Raised when the kernel resets its log buffer (gRPC SubscribeLog reset flag).
    /// Consumers should clear local kernel log buffers to avoid duplicated history replays.
    /// </summary>
    public event EventHandler? KernelLogsReset;

    public ServiceState State => _state;
    public bool IsRunning => _state.Status == ServiceStatus.Running;

    private ISingBoxApiClient CreateApiClient()
        => SingBoxApiClientFactory.Create(LogFromClient);

    /// <summary>
    /// Bridge for the gRPC client's internal diagnostics: those messages carry their
    /// own "[DEBUG] "/"[WARN] " prefixes. Only a handful of messages per session use
    /// this path, so the prefix check here is not a hot-path concern.
    /// </summary>
    private void LogFromClient(string message)
    {
        if (message.StartsWith("[DEBUG] ", StringComparison.OrdinalIgnoreCase))
        {
            LogDebug(message[8..]);
        }
        else if (message.StartsWith("[WARN] ", StringComparison.OrdinalIgnoreCase))
        {
            LogWarn(message[7..]);
        }
        else if (message.StartsWith("[ERROR] ", StringComparison.OrdinalIgnoreCase))
        {
            LogError(message[8..]);
        }
        else if (message.StartsWith("[INFO] ", StringComparison.OrdinalIgnoreCase))
        {
            LogInfo(message[7..]);
        }
        else
        {
            LogDebug(message);
        }
    }

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
                LogDebug("Detected existing sing-box instance, synchronized running state");
            }

            // Refresh the runtime kernel version + apiVersion from the daemon API:
            // enables capability gating (SupportsApiFeature) even for externally
            // started kernels whose binary path we never probed.
            if (CreateApiClient() is SingBoxGrpcApiClient { } grpcClient)
            {
                var serverVersion = await grpcClient.GetServerVersionAsync();
                if (serverVersion is { } info && !string.IsNullOrWhiteSpace(info.Version))
                {
                    CartonApplicationInfo.SetSingBoxVersion(info.Version);
                }
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
            LogWarn("Cleaning up leftover sing-box process before starting a new session");
            await StopAsync();
            leftoverTiming.Restart();
            if (await HasLeftoverSingBoxProcessAsync())
            {
                LogTiming("start.leftover_cleanup_failed", leftoverTiming.Elapsed);
                const string error = "Failed to clean up previous sing-box process before start";
                LogError($"{error}");
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
            LogError($"{error}");
            SetError(error);
            return false;
        }

        if (!File.Exists(_singBoxPath))
        {
            var error = $"sing-box binary not found at: {_singBoxPath}";
            LogError($"{error}");
            SetError(error);
            return false;
        }

        // The gRPC API client hard-requires sing-box >= 1.14.0: the runtime config now
        // injects `services: [{type: api}]` and `cache_file.store_dns`, which older
        // kernels reject with a FATAL "unknown field" at startup. Fail fast with a
        // clear message instead of a cryptic config decode error.
        if (!await IsKernelSupportedAsync())
        {
            var rawVersion = await GetKernelVersionAsync();
            var error = KernelVersionGuard.BuildUnsupportedMessage(rawVersion);
            LogError($"{error}");
            KernelVersionRejected?.Invoke(this, error);
            SetError(error);
            return false;
        }

        _logMonitorLevel = ReadLogMonitorLevel(configPath);

        var startupLogSession = 0;
        try
        {
            _errorOutput.Clear();
            ResetSessionMetrics();

            LogDebug($"Starting sing-box with config: {configPath}");
            LogDebug($"Binary path: {_singBoxPath}");

            // "Config has a TUN inbound" — a Linux setuid kernel still takes the plain
            // start path below, so this is not the same as "we are about to elevate".
            var hasTunInbound = RequiresElevatedPrivileges(configPath);
            if (hasTunInbound)
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && await IsLinuxCoreAuthorizedAsync())
                {
                    LogDebug("TUN inbound detected, sing-box has setuid bit — using normal start path");
                }
                else
                {
                    LogInfo("TUN inbound detected, requesting elevated privileges...");
                    var elevatedResult = await StartElevatedAsync(configPath);
                    LogTiming(elevatedResult ? "start.end_success_elevated" : "start.end_failed_elevated", timing.Elapsed);
                    return elevatedResult;
                }
            }

            startupLogSession = BeginStartupLogCapture();
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
                try
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        if (IsStartupLogCaptureActive(startupLogSession))
                        {
                            EnqueueErrorOutput(e.Data);
                            LogStartupKernel(startupLogSession, e.Data);
                        }
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
                        if (IsStartupLogCaptureActive(startupLogSession))
                        {
                            EnqueueErrorOutput(e.Data);
                            LogStartupKernel(startupLogSession, e.Data);
                        }
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
                        var summary = BuildKernelOutputSummary();
                        if (summary.Length > 0)
                        {
                            errorMsg += $": {summary}";
                        }
                        LogError($"{errorMsg}");
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

            LogDebug("Starting process...");
            var processStartTiming = Stopwatch.StartNew();
            process.Start();
            TryAttachProcessToWindowsJob(process);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            LogTiming("start.process_started", processStartTiming.Elapsed);

            var readyTiming = Stopwatch.StartNew();
            var ready = await WaitForApiReadyAsync(null, hasTunInbound ? TunApiReadyTimeout : ApiReadyTimeout);
            LogTiming(ready ? "start.api_ready" : "start.api_not_ready", readyTiming.Elapsed);
            if (!ready)
            {
                StopStartupLogCapture(startupLogSession);
                if (_process.HasExited)
                {
                    var exitCode = _process.ExitCode;
                    var errorMsg = $"sing-box process exited unexpectedly with code {exitCode}";
                    var exitSummary = BuildKernelOutputSummary();
                    if (exitSummary.Length > 0)
                    {
                        errorMsg += $"\n{exitSummary}";
                    }
                    LogError($"{errorMsg}");
                    await CleanupFailedStartAttemptAsync();
                    SetError(errorMsg);
                    return false;
                }

                var msg = "sing-box API did not become reachable in time";
                // The full capture is already on the kernel log channel; the status
                // line only gets the diagnostic tail so the UI is not flooded.
                var summary = BuildKernelOutputSummary();
                if (summary.Length > 0)
                {
                    msg += $":\n{summary}";
                }
                LogError($"{msg}");
                await CleanupFailedStartAttemptAsync();
                SetError(msg);
                return false;
            }

            _errorOutput.Clear();
            _state.StartTime = DateTime.Now;
            UpdateStatus(ServiceStatus.Running);
            LogInfo("sing-box started successfully");

            EnsureRuntimeMonitorsRunning();

            _ = Task.Delay(1500).ContinueWith(_ =>
            {
                MemoryOptimizer.CompactAndTrim();
            }, TaskScheduler.Default);

            LogTiming("start.end_success", timing.Elapsed);
            return true;
        }
        catch (Exception ex)
        {
            if (startupLogSession != 0)
            {
                StopStartupLogCapture(startupLogSession);
            }

            var error = $"Failed to start sing-box: {ex.Message}";
            LogError($"{error}");
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
        StopStartupLogCapture();
        CancelRuntimeMonitors();
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
            LogInfo("Stopping sing-box...");
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
                LogError($"{error}");
                SetError(error);
                return;
            }
            _elevatedPid = null;
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
            LogInfo("sing-box stopped");
            LogTiming("stop.end_success", timing.Elapsed);
            SingBoxApiClientFactory.Reset();
            _ = Task.Delay(500).ContinueWith(_ =>
            {
                MemoryOptimizer.CompactAndTrim();
            }, TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            var error = $"Failed to stop sing-box: {ex.Message}";
            LogError($"{error}");
            SetError(error);
            LogTiming("stop.end_exception", timing.Elapsed);
        }
    }

    /// <inheritdoc />
    public void NotifySystemProxyEnabled(bool enabled)
    {
        _systemProxyEnabled = enabled;
    }

    private async Task<bool> IsKernelSupportedAsync()
    {
        try
        {
            var version = await GetKernelVersionAsync();
            if (string.IsNullOrWhiteSpace(version))
            {
                // Version probe failed; assume supported so we do not block the user
                // behind a false negative (the kernel will fail loudly if it is too old).
                return true;
            }

            CartonApplicationInfo.SetSingBoxVersion(version);
            return KernelVersionGuard.IsSupported(version);
        }
        catch
        {
            return true;
        }
    }

    private async Task<string?> GetKernelVersionAsync()
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _singBoxPath,
                Arguments = "version",
                WorkingDirectory = _workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };
        ApplyLinuxLibrarySearchPath(process.StartInfo);

        if (!process.Start())
        {
            return null;
        }

        var stdout = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return stdout;
    }

    private void UpdateStatus(ServiceStatus status)
    {
        if (status != ServiceStatus.Running)
        {
            CancelRuntimeMonitors();
            ResetStreamingSnapshots();
        }

        _state.Status = status;
        _state.ErrorMessage = null;
        StatusChanged?.Invoke(this, status);
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
            LogWarn($"Ignored exception in {source} callback to avoid crashing the process: {ex.Message}");
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
        // Debug-only instrumentation: the Conditional attribute strips this method
        // (CALL SITE included - no dead-call overhead) AND the timing.log file sink
        // below entirely in Release builds; Release diagnostics rely on the manager
        // log stream instead.
        var message = $"[TIMING] {DateTimeOffset.Now:O} {stage}{elapsedText}";
        LogDebug(message);

        try
        {
            var timingLogPath = Path.Combine(_workingDirectory, "logs", "timing.log");
            Directory.CreateDirectory(Path.GetDirectoryName(timingLogPath)!);
            File.AppendAllText(timingLogPath, $"[DEBUG] {message}" + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {
            // Timing logs must never affect core lifecycle operations.
        }
    }

    private void LogKernel(string message)
    {
        LogReceived?.Invoke(this, new KernelLogEntry(string.Empty, message));
    }

    /// <summary>
    /// Structured manager logging: severity is an enum, not a parsed "[WARN] " prefix.
    /// A single structured event per log call (ManagerLogEntryReceived). Kernel
    /// stdout/stderr capture and the gRPC log stream reach the UI through the
    /// separate KernelLogEntry-based LogReceived event instead.
    /// </summary>
    public void Log(CartonLogLevel level, string message)
    {
        // Single structured event only. Firing the legacy string event too made
        // every manager log appear twice in the logs page.
        ManagerLogEntryReceived?.Invoke(this, new CartonLogEntry(level, message));
    }

    public void LogDebug(string message) => Log(CartonLogLevel.Debug, message);
    public void LogInfo(string message) => Log(CartonLogLevel.Info, message);
    public void LogWarn(string message) => Log(CartonLogLevel.Warn, message);
    public void LogError(string message) => Log(CartonLogLevel.Error, message);

    private void LogKernel(KernelLogEntry entry)
    {
        if (IsRuntimeDiagnosticLevel(entry.Level))
        {
            EnqueueErrorOutput(entry.Message);
        }

        LogReceived?.Invoke(this, entry);
    }

    private int BeginStartupLogCapture()
    {
        var session = Interlocked.Increment(ref _startupLogCaptureSession);
        Interlocked.Exchange(ref _windowsStartupLogSequence, 0);
        Volatile.Write(ref _reportedWindowsStartupLogGap, false);
        Volatile.Write(ref _captureStartupOutputForUi, true);
        return session;
    }

    private void StopStartupLogCapture()
    {
        Volatile.Write(ref _captureStartupOutputForUi, false);
    }

    private void StopStartupLogCapture(int session)
    {
        if (Volatile.Read(ref _startupLogCaptureSession) == session)
        {
            StopStartupLogCapture();
        }
    }

    private bool IsStartupLogCaptureActive()
    {
        return Volatile.Read(ref _captureStartupOutputForUi);
    }

    private bool IsStartupLogCaptureActive(int session)
    {
        return IsStartupLogCaptureActive() &&
               Volatile.Read(ref _startupLogCaptureSession) == session;
    }

    private void LogStartupKernel(int session, string message)
    {
        if (!string.IsNullOrEmpty(message) && IsStartupLogCaptureActive(session))
        {
            LogKernel(message);
        }
    }

    private void LogStartupKernel(string message)
    {
        if (!string.IsNullOrEmpty(message) && IsStartupLogCaptureActive())
        {
            LogKernel(message);
        }
    }

    private void EnqueueErrorOutput(string message)
    {
        _errorOutput.Enqueue(message);
        while (_errorOutput.Count > MaxErrorOutputLines && _errorOutput.TryDequeue(out _))
        {
        }
    }

    /// <summary>
    /// Condenses the buffered kernel output for the status line: the last few
    /// warn/error/fatal lines when there are any, otherwise the last few lines as-is.
    /// The full capture is on the kernel log channel already.
    /// </summary>
    private string BuildKernelOutputSummary()
    {
        return SummarizeKernelOutput(_errorOutput.ToArray(), MaxKernelOutputSummaryLines);
    }

    internal static string SummarizeKernelOutput(IReadOnlyList<string> lines, int maxLines)
    {
        if (lines.Count == 0)
        {
            return string.Empty;
        }

        var diagnostics = lines.Where(IsKernelDiagnosticLine).ToList();
        IEnumerable<string> source = diagnostics.Count > 0 ? diagnostics : lines;
        return string.Join("\n", source.TakeLast(maxLines));
    }

    /// <summary>
    /// True when a raw kernel console line carries a warn-or-worse level. sing-box
    /// prints the upper-case level first ("ERROR ...", "ERROR[0042] ...") or right
    /// after the optional "-0700 2006-01-02 15:04:05" timestamp, and Go panics start
    /// with "panic:". Only the first recognised level token decides, so a level word
    /// inside the message (node names, URLs) does not turn an INFO line into a hit.
    /// </summary>
    internal static bool IsKernelDiagnosticLine(string line)
    {
        var remaining = line.AsSpan();
        for (var i = 0; i < 4 && !remaining.IsEmpty; i++)
        {
            remaining = remaining.TrimStart();
            var end = remaining.IndexOf(' ');
            var token = end < 0 ? remaining : remaining[..end];
            remaining = end < 0 ? ReadOnlySpan<char>.Empty : remaining[(end + 1)..];

            var cut = token.IndexOfAny('[', ':');
            if (cut >= 0)
            {
                token = token[..cut];
            }

            if (IsKernelLevelToken(token))
            {
                return IsRuntimeDiagnosticLevel(token.ToString());
            }
        }

        return false;
    }

    private static bool IsKernelLevelToken(ReadOnlySpan<char> token)
    {
        return token.Equals("TRACE", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("DEBUG", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("INFO", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("WARN", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("WARNING", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("ERROR", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("FATAL", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("PANIC", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Drops buffered kernel diagnostic lines. Called when the kernel resets its
    /// log buffer and replays its saved history, so diagnostics are not duplicated.
    /// </summary>
    internal void ClearKernelErrorOutput()
    {
        _errorOutput.Clear();
    }

    private static bool IsRuntimeDiagnosticLevel(string level)
    {
        return string.Equals(level, "Warn", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(level, "Warning", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(level, "Error", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(level, "Fatal", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(level, "Panic", StringComparison.OrdinalIgnoreCase);
    }

    private void SetError(string message)
    {
        CancelRuntimeMonitors();
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
            LogWarn($"Failed to clean up sing-box after a start failure: {ex.Message}");
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
        _state.Goroutines = 0;
        _state.ConnectionsIn = 0;
        _state.ConnectionsOut = 0;
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
                LogWarn($"Failed to send SIGTERM to sing-box process {process.Id}: {ex.Message}");
            }

            for (var i = 0; i < 20; i++)
            {
                if (process.HasExited)
                {
                    return;
                }

                await Task.Delay(250);
            }

            LogWarn($"sing-box process {process.Id} did not exit after SIGTERM, forcing termination");
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
        CancelRuntimeMonitors();
        _monitorCancellation?.Dispose();
        _monitorCancellation = null;
        // Release the shared gRPC client (and its HTTP/2 channel) once the manager goes away.
        SingBoxApiClientFactory.Reset();


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
