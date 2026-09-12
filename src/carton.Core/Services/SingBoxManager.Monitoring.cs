using System.Diagnostics;
using System.Text.Json;
using Grpc.Core;
using carton.Core.Models;
using carton.Core.Utilities;

namespace carton.Core.Services;

public partial class SingBoxManager
{
    public long? GetRunningProcessMemoryBytes()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Refresh();
                return _process.WorkingSet64;
            }

            if (_elevatedPid.HasValue && _elevatedPid.Value > 0)
            {
                using var process = Process.GetProcessById(_elevatedPid.Value);
                process.Refresh();
                return process.WorkingSet64;
            }
        }
        catch
        {
        }

        return null;
    }

    private void EnsureRuntimeMonitorsRunning()
    {
        var replacingCanceledMonitors = _monitorCancellation?.IsCancellationRequested == true;
        var cancellationToken = EnsureRuntimeMonitorCancellationToken();
        if (replacingCanceledMonitors || _logMonitorTask is not { IsCompleted: false })
        {
            _logMonitorTask = Task.Run(() => StartLogMonitorAsync(cancellationToken));
        }

        if (replacingCanceledMonitors || _statusMonitorTask is not { IsCompleted: false })
        {
            _statusMonitorTask = Task.Run(() => StartStatusMonitorAsync(cancellationToken));
        }

        if (replacingCanceledMonitors ||
            _connectionsMonitorTask is not { IsCompleted: false } ||
            _groupsMonitorTask is not { IsCompleted: false } ||
            _modeMonitorTask is not { IsCompleted: false })
        {
            StartStreamingMonitors();
        }
    }

    private CancellationToken EnsureRuntimeMonitorCancellationToken()
    {
        if (_monitorCancellation is { IsCancellationRequested: false } current)
        {
            return current.Token;
        }

        _monitorCancellation = new CancellationTokenSource();
        return _monitorCancellation.Token;
    }

    private void CancelRuntimeMonitors()
    {
        try
        {
            _monitorCancellation?.Cancel();
        }
        catch
        {
        }
    }

    private async Task StartLogMonitorAsync(CancellationToken cancellationToken)
    {
        var consecutiveFailures = 0;
        var monitorLevel = _logMonitorLevel;
        LogDebug($"Log monitor subscribed at level: {monitorLevel}");

        while (_state.Status == ServiceStatus.Running && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                var apiClient = CreateApiClient();
                EventHandler? resetHandler = null;
                resetHandler = (_, _) =>
                {
                    // Kernel reset its log buffer: drop buffered diagnostics so the
                    // history replay below is not duplicated.
                    ClearKernelErrorOutput();
                    KernelLogsReset?.Invoke(this, EventArgs.Empty);
                };
                apiClient.LogsReset += resetHandler;
                try
                {
                    await foreach (var entry in apiClient.SubscribeLogsAsync(monitorLevel, cancellationToken))
                    {
                        if (_state.Status != ServiceStatus.Running)
                        {
                            break;
                        }

                        StopStartupLogCapture();
                        consecutiveFailures = 0;
                        LogKernel(entry);
                    }
                }
                finally
                {
                    apiClient.LogsReset -= resetHandler;
                }

                if (_state.Status == ServiceStatus.Running)
                {
                    await DelaySafelyAsync(TimeSpan.FromMilliseconds(500), cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                consecutiveFailures++;
                if (consecutiveFailures == 1 || consecutiveFailures % 10 == 0)
                {
                    LogWarn($"Log monitor error: {e.Message}");
                }

                await DelaySafelyAsync(TimeSpan.FromSeconds(Math.Min(5, Math.Max(1, consecutiveFailures))), cancellationToken);
            }
        }
    }

    /// <summary>
    /// Single multiplexed SubscribeStatus stream feeding both traffic and memory
    /// updates. Totals come from the kernel's uplinkTotal/downlinkTotal counters
    /// (exact even across monitor reconnects); the client-side accumulation is only
    /// a fallback for kernels that do not report traffic availability.
    /// </summary>
    private async Task StartStatusMonitorAsync(CancellationToken cancellationToken)
    {
        var consecutiveFailures = 0;
        var fallbackTotalUplink = 0L;
        var fallbackTotalDownlink = 0L;

        while (_state.Status == ServiceStatus.Running && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                var apiClient = CreateApiClient();
                await foreach (var status in apiClient.SubscribeAggregatedStatusAsync(cancellationToken))
                {
                    if (_state.Status != ServiceStatus.Running)
                    {
                        break;
                    }

                    consecutiveFailures = 0;
                    _state.UploadSpeed = status.Uplink;
                    _state.DownloadSpeed = status.Downlink;
                    _state.Goroutines = status.Goroutines;
                    _state.ConnectionsIn = status.ConnectionsIn;
                    _state.ConnectionsOut = status.ConnectionsOut;
                    if (status.TrafficAvailable)
                    {
                        _state.TotalUpload = status.UplinkTotal;
                        _state.TotalDownload = status.DownlinkTotal;
                    }
                    else
                    {
                        // No kernel-tracked totals: accumulate per-interval deltas locally.
                        fallbackTotalUplink += status.Uplink;
                        fallbackTotalDownlink += status.Downlink;
                        _state.TotalUpload = fallbackTotalUplink;
                        _state.TotalDownload = fallbackTotalDownlink;
                    }

                    TrafficUpdated?.Invoke(this, new TrafficInfo
                    {
                        Uplink = status.Uplink,
                        Downlink = status.Downlink,
                        Goroutines = status.Goroutines,
                        ConnectionsIn = status.ConnectionsIn,
                        ConnectionsOut = status.ConnectionsOut
                    });

                    if (_state.MemoryInUse != (long)status.Memory)
                    {
                        // Only fire on actual changes; a constant memory read stays quiet.
                        _state.MemoryInUse = (long)status.Memory;
                        MemoryUpdated?.Invoke(this, (long)status.Memory);
                    }
                }

                if (_state.Status == ServiceStatus.Running)
                {
                    await DelaySafelyAsync(TimeSpan.FromMilliseconds(500), cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (RpcException e) when (cancellationToken.IsCancellationRequested)
            {
                // Deliberate stop/restart: the monitors are being torn down, the
                // cancelled streams are expected and are not user-facing failures.
                break;
            }
            catch (RpcException e)
            {
                consecutiveFailures++;
                if (consecutiveFailures == 1 || consecutiveFailures % 10 == 0)
                {
                    LogWarn($"Status monitor RPC error: {e.StatusCode} {e.Message}");
                }

                await DelaySafelyAsync(TimeSpan.FromSeconds(Math.Min(5, consecutiveFailures)), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                consecutiveFailures++;
                if (consecutiveFailures == 1 || consecutiveFailures % 10 == 0)
                {
                    LogWarn($"Status monitor error: {e.Message}");
                }

                await DelaySafelyAsync(TimeSpan.FromSeconds(Math.Min(5, Math.Max(1, consecutiveFailures))), cancellationToken);
            }
        }
    }

    private string ReadLogMonitorLevel(string configPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(configPath));
            if (document.RootElement.TryGetProperty("log", out var logElement) &&
                logElement.ValueKind == JsonValueKind.Object &&
                logElement.TryGetProperty("level", out var levelElement) &&
                levelElement.ValueKind == JsonValueKind.String)
            {
                return SingBoxLogLevelHelper.Normalize(levelElement.GetString());
            }
        }
        catch (Exception ex)
        {
            LogWarn($"Failed to inspect config log level: {ex.Message}");
        }

        return LogMonitorFallbackLevel;
    }
}
