using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using carton.Core.Models;
using carton.Core.Serialization;
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
    Task RunGroupDelayTestAsync(string groupTag, string? testUrl = null, int timeoutMs = 5000);
    Task<Dictionary<string, int>> RunOutboundDelayTestsAsync(IEnumerable<string> outboundTags, string? testUrl = null, int timeoutMs = 5000);
    long? GetRunningProcessMemoryBytes();
    Task<List<ConnectionInfo>> GetConnectionsAsync();
    Task CloseConnectionAsync(string connectionId);
    Task CloseAllConnectionsAsync();

    /// <summary>
    /// Notifies the manager whether a system proxy was configured for the current
    /// sing-box session. When <paramref name="enabled"/> is <see langword="true"/>
    /// the system proxy will be cleared automatically on the next Stop.
    /// </summary>
    void NotifySystemProxyEnabled(bool enabled);
}

public class SingBoxManager : ISingBoxManager, IDisposable
{
    private const string MacTunPermissionPrompt = "Carton requires administrator permission to start or stop the TUN interface (sing-box).";
    private const string DefaultDelayTestUrl = "https://www.gstatic.com/generate_204";

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
    private const int WindowsElevatedHelperPort = 47891;
    private const string WindowsElevatedHelperArg = "--carton-elevated-helper";
    private const string WindowsElevatedHelperTokenHeader = "X-Carton-Helper-Token";
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

            LogManager($"[INFO] Starting sing-box with config: {configPath}");
            LogManager($"[INFO] Binary path: {_singBoxPath}");

            if (RequiresElevatedPrivileges(configPath))
            {
                LogManager("[INFO] TUN inbound detected, requesting elevated privileges...");
                return await StartElevatedAsync(configPath);
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
                _process.Kill(true);
                await _process.WaitForExitAsync();
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

    public async Task<List<OutboundGroup>> GetOutboundGroupsAsync()
    {
        try
        {
            var response = await _httpClient.GetStringAsync($"{_apiAddress}/proxies");
            using var document = JsonDocument.Parse(response);
            var groups = new List<OutboundGroup>();

            if (document.RootElement.TryGetProperty("proxies", out var proxiesElement) &&
                proxiesElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var proxyProperty in proxiesElement.EnumerateObject())
                {
                    if (proxyProperty.Value.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var proxy = proxyProperty.Value;
                    var group = new OutboundGroup
                    {
                        Tag = proxyProperty.Name,
                        Type = ReadString(proxy, "type"),
                        Selected = ReadString(proxy, "now")
                    };

                     if (proxy.TryGetProperty("all", out var allElement) &&
                         allElement.ValueKind == JsonValueKind.Array)
                     {
                         foreach (var item in allElement.EnumerateArray())
                         {
                              if (item.ValueKind == JsonValueKind.String)
                              {
                                 var itemTag = item.GetString() ?? string.Empty;
                                 var itemType = string.Empty;
                                 JsonElement itemProxy = default;

                                 if (!string.IsNullOrWhiteSpace(itemTag) &&
                                     proxiesElement.TryGetProperty(itemTag, out var itemProxyElement) &&
                                     itemProxyElement.ValueKind == JsonValueKind.Object)
                                 {
                                     itemProxy = itemProxyElement;
                                     itemType = ReadString(itemProxy, "type");
                                 }

                                group.Items.Add(new OutboundItem
                                {
                                    Tag = itemTag,
                                    Type = itemType,
                                    UrlTestDelay = ReadLatestDelay(itemProxy)
                                });
                              }
                          }
                     }

                    groups.Add(group);
                }
            }

            return groups;
        }
        catch
        {
            return new List<OutboundGroup>();
        }
    }

    public async Task SelectOutboundAsync(string groupTag, string outboundTag)
    {
        try
        {
            var request = new OutboundSelectionRequest { Name = outboundTag };
            var payload = JsonSerializer.Serialize(
                request,
                CartonCoreJsonContext.Default.OutboundSelectionRequest);
            var content = new StringContent(payload, Encoding.UTF8, "application/json");
            await _httpClient.PutAsync($"{_apiAddress}/proxies/{Uri.EscapeDataString(groupTag)}", content);
        }
        catch
        {
        }
    }

    public async Task RunGroupDelayTestAsync(string groupTag, string? testUrl = null, int timeoutMs = 5000)
    {
        if (string.IsNullOrWhiteSpace(groupTag))
        {
            return;
        }

        var urlParam = Uri.EscapeDataString(string.IsNullOrWhiteSpace(testUrl) ? DefaultDelayTestUrl : testUrl);

        try
        {
            var endpoint = $"{_apiAddress}/group/{Uri.EscapeDataString(groupTag)}/delay?timeout={timeoutMs}&url={urlParam}";
            using var response = await _httpClient.GetAsync(endpoint);
            _ = response.IsSuccessStatusCode;
        }
        catch
        {
        }
    }

    public async Task<Dictionary<string, int>> RunOutboundDelayTestsAsync(IEnumerable<string> outboundTags, string? testUrl = null, int timeoutMs = 5000)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (outboundTags == null)
        {
            return result;
        }

        var targets = new List<string>();
        var seenTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var outboundTag in outboundTags)
        {
            if (!string.IsNullOrWhiteSpace(outboundTag) && seenTags.Add(outboundTag))
            {
                targets.Add(outboundTag);
            }
        }

        if (targets.Count == 0)
        {
            return result;
        }

        var tasks = targets.Select(async tag =>
        {
            var delay = await RunOutboundDelayTestAsync(tag, testUrl, timeoutMs);
            return (tag, delay);
        });

        foreach (var (tag, delay) in await Task.WhenAll(tasks))
        {
            result[tag] = delay;
        }

        return result;
    }

    private async Task<int> RunOutboundDelayTestAsync(string tag, string? testUrl = null, int timeoutMs = 5000)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return -1;
        }

        var urlParam = Uri.EscapeDataString(string.IsNullOrWhiteSpace(testUrl) ? DefaultDelayTestUrl : testUrl);

        try
        {
            var endpoint = $"{_apiAddress}/proxies/{Uri.EscapeDataString(tag)}/delay?timeout={timeoutMs}&url={urlParam}";
            using var response = await _httpClient.GetAsync(endpoint);
            if (!response.IsSuccessStatusCode)
            {
                return -1;
            }

            var payload = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.TryGetProperty("delay", out var delayElement) &&
                delayElement.ValueKind == JsonValueKind.Number &&
                delayElement.TryGetInt32(out var delay))
            {
                return delay;
            }
        }
        catch
        {
        }

        return -1;
    }

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
    public async Task<List<ConnectionInfo>> GetConnectionsAsync()
    {
        try
        {
            var response = await _httpClient.GetStringAsync($"{_apiAddress}/connections");
            var connections = new List<ConnectionInfo>();
            using var document = JsonDocument.Parse(response);

            if (!document.RootElement.TryGetProperty("connections", out var connectionsElement) ||
                connectionsElement.ValueKind != JsonValueKind.Array)
            {
                return connections;
            }

            foreach (var conn in connectionsElement.EnumerateArray())
            {
                if (conn.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var metadata = conn.TryGetProperty("metadata", out var metadataElement) &&
                               metadataElement.ValueKind == JsonValueKind.Object
                    ? metadataElement
                    : default;
                var hasMetadata = metadata.ValueKind == JsonValueKind.Object;

                var chains = conn.TryGetProperty("chains", out var chainsElement) ? chainsElement : default;

                var sourceIp = hasMetadata ? ReadString(metadata, "sourceIP") : string.Empty;
                var sourcePort = hasMetadata ? ReadString(metadata, "sourcePort") : string.Empty;
                var destinationIp = hasMetadata ? ReadString(metadata, "destinationIP") : string.Empty;
                var destinationPort = hasMetadata ? ReadString(metadata, "destinationPort") : string.Empty;
                var host = hasMetadata ? ReadString(metadata, "host") : string.Empty;

                connections.Add(new ConnectionInfo
                {
                    Id = ReadString(conn, "id"),
                    StartTime = ReadDateTime(conn, "start"),
                    Inbound = ReadString(conn, "inbound"),
                    Process = hasMetadata ? ReadString(metadata, "process") : string.Empty,
                    Ip = sourceIp,
                    Source = ComposeEndpoint(sourceIp, sourcePort),
                    Destination = ComposeDestination(host, destinationIp, destinationPort),
                    Domain = host,
                    Protocol = ResolveProtocol(conn, metadata),
                    Chains = ReadChains(chains),
                    Outbound = ResolveOutbound(conn, chains),
                    Upload = ReadInt64(conn, "upload"),
                    Download = ReadInt64(conn, "download")
                });
            }

            return connections;
        }
        catch
        {
            return new List<ConnectionInfo>();
        }
    }

    public async Task CloseConnectionAsync(string connectionId)
    {
        try
        {
            await _httpClient.DeleteAsync($"{_apiAddress}/connections/{Uri.EscapeDataString(connectionId)}");
        }
        catch
        {
        }
    }

    private static string ResolveProtocol(JsonElement connection, JsonElement metadata)
    {
        var protocol = ReadString(connection, "protocol");
        return string.IsNullOrWhiteSpace(protocol) ? ReadString(metadata, "network") : protocol;
    }

    private static string ResolveOutbound(JsonElement connection, JsonElement chains)
    {
        var outbound = ReadString(connection, "outbound");
        if (!string.IsNullOrWhiteSpace(outbound))
        {
            return outbound;
        }

        var tags = ReadChains(chains);
        if (tags.Count > 0)
        {
            return string.Join(" -> ", tags);
        }

        return string.Empty;
    }

    private static List<string> ReadChains(JsonElement chains)
    {
        var tags = new List<string>();
        if (chains.ValueKind != JsonValueKind.Array)
        {
            return tags;
        }

        foreach (var chain in chains.EnumerateArray())
        {
            if (chain.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var tag = chain.GetString();
            if (!string.IsNullOrWhiteSpace(tag))
            {
                tags.Add(tag);
            }
        }

        return tags;
    }

    private static string ComposeEndpoint(string address, string port)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return string.Empty;
        }

        return string.IsNullOrWhiteSpace(port) ? address : $"{address}:{port}";
    }

    private static string ComposeDestination(string host, string destinationIp, string port)
    {
        var target = string.IsNullOrWhiteSpace(host) ? destinationIp : host;
        if (string.IsNullOrWhiteSpace(target))
        {
            return string.Empty;
        }

        return string.IsNullOrWhiteSpace(port) ? target : $"{target}:{port}";
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property))
        {
            return string.Empty;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString() ?? string.Empty,
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => property.ToString(),
            JsonValueKind.Object when property.TryGetProperty("name", out var nameProperty) &&
                                     nameProperty.ValueKind == JsonValueKind.String
                => nameProperty.GetString() ?? string.Empty,
            _ => string.Empty
        };
    }

    private static int ReadLatestDelay(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("history", out var historyElement) ||
            historyElement.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        var latestDelay = 0;
        foreach (var historyItem in historyElement.EnumerateArray())
        {
            if (historyItem.ValueKind != JsonValueKind.Object ||
                !historyItem.TryGetProperty("delay", out var delayElement) ||
                delayElement.ValueKind != JsonValueKind.Number ||
                !delayElement.TryGetInt32(out var delay))
            {
                continue;
            }

            latestDelay = delay > 0 ? delay : 0;
        }

        return latestDelay;
    }

    private static long ReadInt64(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property))
        {
            return 0;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var integerValue))
        {
            return integerValue;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var floatingValue))
        {
            return (long)floatingValue;
        }

        if (property.ValueKind == JsonValueKind.String &&
            long.TryParse(property.GetString(), out var parsedValue))
        {
            return parsedValue;
        }

        return 0;
    }

    private static DateTime ReadDateTime(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property))
        {
            return DateTime.Now;
        }

        if (property.ValueKind == JsonValueKind.String &&
            DateTime.TryParse(property.GetString(), out var timestamp))
        {
            return timestamp;
        }

        if (property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt64(out var unixMilliseconds))
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds).LocalDateTime;
        }

        return DateTime.Now;
    }

    public async Task CloseAllConnectionsAsync()
    {
        try
        {
            await _httpClient.DeleteAsync($"{_apiAddress}/connections");
        }
        catch
        {
        }
    }

    private async Task StartTrafficMonitorAsync()
    {
        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        var stream = new MemoryStream();
        ClientWebSocket? webSocket = null;
        var wsUri = BuildWebSocketUri("traffic");

        try
        {
            while (_state.Status == ServiceStatus.Running)
            {
                try
                {
                    if (webSocket == null || webSocket.State != WebSocketState.Open)
                    {
                        webSocket?.Dispose();
                        webSocket = new ClientWebSocket();
                        webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
                        var wsSecret = HttpClientFactory.LocalApiSecret;
                        if (!string.IsNullOrWhiteSpace(wsSecret))
                        {
                            webSocket.Options.SetRequestHeader("Authorization", $"Bearer {wsSecret}");
                        }
                        using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        await webSocket.ConnectAsync(wsUri, connectCts.Token);
                        stream.SetLength(0);
                    }

                    var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await CloseSocketSilentlyAsync(webSocket, "Carton traffic monitor reconnect");
                        webSocket.Dispose();
                        webSocket = null;
                        await Task.Delay(500);
                        continue;
                    }

                    if (result.Count > 0)
                    {
                        stream.Write(buffer, 0, result.Count);
                    }

                    if (!result.EndOfMessage)
                    {
                        continue;
                    }

                    if (result.MessageType != WebSocketMessageType.Text)
                    {
                        stream.SetLength(0);
                        continue;
                    }

                    if (stream.Length == 0)
                    {
                        continue;
                    }

                    var payload = Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
                    stream.SetLength(0);
                    var traffic = TryParseTrafficSnapshot(payload);
                    if (traffic == null)
                    {
                        continue;
                    }

                    _state.UploadSpeed = traffic.Uplink;
                    _state.DownloadSpeed = traffic.Downlink;
                    _state.TotalUpload += traffic.Uplink;
                    _state.TotalDownload += traffic.Downlink;
                    TrafficUpdated?.Invoke(this, traffic);
                }
                catch (Exception e)
                {
                    LogManager($"[WARN] Traffic monitor error: {e.Message}");
                    if (webSocket != null)
                    {
                        await CloseSocketSilentlyAsync(webSocket, "Carton traffic monitor error");
                        webSocket.Dispose();
                        webSocket = null;
                    }

                    stream.SetLength(0);
                    await Task.Delay(1000);
                }
            }
        }
        finally
        {
            if (webSocket != null)
            {
                await CloseSocketSilentlyAsync(webSocket, "Carton traffic monitor stopped");
                webSocket.Dispose();
            }

            stream.Dispose();
            ArrayPool<byte>.Shared.Return(buffer);
            _trafficMonitorTask = null;
        }
    }

    private void EnsureRuntimeMonitorsRunning()
    {
        if (_trafficMonitorTask is { IsCompleted: false })
        {
        }
        else
        {
            _trafficMonitorTask = Task.Run(StartTrafficMonitorAsync);
        }

        if (_memoryMonitorTask is { IsCompleted: false })
        {
            return;
        }

        _memoryMonitorTask = Task.Run(StartMemoryMonitorAsync);
    }

    private async Task StartMemoryMonitorAsync()
    {
        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        var stream = new MemoryStream();
        ClientWebSocket? webSocket = null;
        var wsUri = BuildWebSocketUri("memory");

        try
        {
            while (_state.Status == ServiceStatus.Running)
            {
                try
                {
                    if (webSocket == null || webSocket.State != WebSocketState.Open)
                    {
                        webSocket?.Dispose();
                        webSocket = new ClientWebSocket();
                        webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
                        var wsSecret = HttpClientFactory.LocalApiSecret;
                        if (!string.IsNullOrWhiteSpace(wsSecret))
                        {
                            webSocket.Options.SetRequestHeader("Authorization", $"Bearer {wsSecret}");
                        }

                        using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        await webSocket.ConnectAsync(wsUri, connectCts.Token);
                        stream.SetLength(0);
                    }

                    var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await CloseSocketSilentlyAsync(webSocket, "Carton memory monitor reconnect");
                        webSocket.Dispose();
                        webSocket = null;
                        await Task.Delay(500);
                        continue;
                    }

                    if (result.Count > 0)
                    {
                        stream.Write(buffer, 0, result.Count);
                    }

                    if (!result.EndOfMessage)
                    {
                        continue;
                    }

                    if (result.MessageType != WebSocketMessageType.Text)
                    {
                        stream.SetLength(0);
                        continue;
                    }

                    if (stream.Length == 0)
                    {
                        continue;
                    }

                    var payload = Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
                    stream.SetLength(0);
                    var memoryInUse = TryParseMemorySnapshot(payload);
                    if (!memoryInUse.HasValue)
                    {
                        continue;
                    }

                    _state.MemoryInUse = memoryInUse.Value;
                    MemoryUpdated?.Invoke(this, memoryInUse.Value);
                }
                catch (Exception e)
                {
                    LogManager($"[WARN] Memory monitor error: {e.Message}");
                    if (webSocket != null)
                    {
                        await CloseSocketSilentlyAsync(webSocket, "Carton memory monitor error");
                        webSocket.Dispose();
                        webSocket = null;
                    }

                    stream.SetLength(0);
                    await Task.Delay(1000);
                }
            }
        }
        finally
        {
            if (webSocket != null)
            {
                await CloseSocketSilentlyAsync(webSocket, "Carton memory monitor stopped");
                webSocket.Dispose();
            }

            stream.Dispose();
            ArrayPool<byte>.Shared.Return(buffer);
            _memoryMonitorTask = null;
        }
    }

    private TrafficInfo? TryParseTrafficSnapshot(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var uplink = ReadTrafficValue(root, "uplink", "up", "upload");
            var downlink = ReadTrafficValue(root, "downlink", "down", "download");
            return new TrafficInfo
            {
                Uplink = uplink,
                Downlink = downlink
            };
        }
        catch (JsonException ex)
        {
            LogManager($"[WARN] Failed to parse traffic snapshot: {ex.Message}");
            return null;
        }
    }

    private long? TryParseMemorySnapshot(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (TryReadTrafficValue(root, new[] { "inuse", "inUse", "memory", "value" }, out var value))
            {
                return value;
            }

            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("now", out var nowElement) &&
                TryReadTrafficValue(nowElement, new[] { "inuse", "inUse", "memory", "value" }, out value))
            {
                return value;
            }

            return null;
        }
        catch (JsonException ex)
        {
            LogManager($"[WARN] Failed to parse memory snapshot: {ex.Message}");
            return null;
        }
    }

    private static long ReadTrafficValue(JsonElement root, params string[] propertyNames)
    {
        if (TryReadTrafficValue(root, propertyNames, out var value))
        {
            return value;
        }

        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("now", out var nowElement) &&
            nowElement.ValueKind == JsonValueKind.Object &&
            TryReadTrafficValue(nowElement, propertyNames, out value))
        {
            return value;
        }

        return 0;
    }

    private static async Task CloseSocketSilentlyAsync(ClientWebSocket socket, string reason)
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, CancellationToken.None);
            }
        }
        catch
        {
        }
    }

    private static bool TryReadTrafficValue(JsonElement element, IReadOnlyList<string> propertyNames, out long value)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            value = 0;
            return false;
        }

        foreach (var name in propertyNames)
        {
            if (!element.TryGetProperty(name, out var property))
            {
                continue;
            }

            switch (property.ValueKind)
            {
                case JsonValueKind.Number when property.TryGetInt64(out var longValue):
                    value = longValue;
                    return true;
                case JsonValueKind.Number when property.TryGetDouble(out var doubleValue):
                    value = (long)doubleValue;
                    return true;
                case JsonValueKind.String when long.TryParse(property.GetString(), out var parsed):
                    value = parsed;
                    return true;
            }
        }

        value = 0;
        return false;
    }

    private Uri BuildWebSocketUri(string relativePath)
    {
        var builder = new UriBuilder(_apiAddress)
        {
            Scheme = _apiAddress.StartsWith("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws",
            Path = relativePath.TrimStart('/'),
            Query = string.Empty
        };
        return builder.Uri;
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

    private async Task<bool> IsApiReachableAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var response = await _httpClient.GetAsync($"{_apiAddress}/version", cts.Token);
            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            return response.StatusCode is System.Net.HttpStatusCode.NotFound
                or System.Net.HttpStatusCode.Unauthorized
                or System.Net.HttpStatusCode.Forbidden
                or System.Net.HttpStatusCode.MethodNotAllowed;
        }
        catch
        {
            return false;
        }
    }

    private async Task<int?> TryFindProcessPidByApiPortAsync()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "netstat",
                        Arguments = "-ano -p tcp",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                var output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();

                foreach (var raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var line = raw.Trim();
                    if (!line.Contains($":{_apiPort}", StringComparison.Ordinal) ||
                        !line.Contains("LISTEN", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 0 && int.TryParse(parts[^1], out var pid) && pid > 0)
                    {
                        return pid;
                    }
                }

                return null;
            }

            using var unixProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "lsof",
                    Arguments = $"-nP -iTCP:{_apiPort} -sTCP:LISTEN -t",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            unixProcess.Start();
            var pidOutput = (await unixProcess.StandardOutput.ReadToEndAsync()).Trim();
            await unixProcess.WaitForExitAsync();

            var firstLine = pidOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (int.TryParse(firstLine, out var unixPid) && unixPid > 0)
            {
                return unixPid;
            }
        }
        catch
        {
        }

        return null;
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

    private static string QuoteShellArg(string value)
    {
        return $"'{value.Replace("'", "'\"'\"'")}'";
    }

    private static string EscapeForPowerShellSingleQuoted(string value)
    {
        return value.Replace("'", "''");
    }

    private static string EscapeForAppleScript(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private async Task<ElevatedStartResult> StartElevatedOnMacAsync(string configPath, string logPath)
    {
        var shellCommand =
            $"{QuoteShellArg(_singBoxPath)} run -c {QuoteShellArg(configPath)} < /dev/null >> {QuoteShellArg(logPath)} 2>&1 & echo $!";
        var appleScript =
            $"do shell script \"{EscapeForAppleScript(shellCommand)}\" with prompt \"{EscapeForAppleScript(MacTunPermissionPrompt)}\" with administrator privileges";

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "osascript",
                Arguments = $"-e \"{EscapeForAppleScript(appleScript)}\"",
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
                ErrorMessage = string.IsNullOrWhiteSpace(error) ? "Administrator permission denied or failed" : error
            };
        }

        if (!int.TryParse(output, out var pid))
        {
            return new ElevatedStartResult
            {
                Success = false,
                ErrorMessage = $"Failed to parse elevated PID: '{output}'"
            };
        }

        return new ElevatedStartResult { Success = true, Pid = pid };
    }

    private async Task<ElevatedStartResult> StartElevatedOnLinuxAsync(string configPath, string logPath)
    {
        var shellCommand =
            $"{QuoteShellArg(_singBoxPath)} run -c {QuoteShellArg(configPath)} < /dev/null >> {QuoteShellArg(logPath)} 2>&1 & echo $!";

        var startInfo = new ProcessStartInfo
        {
            FileName = "pkexec",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("/bin/sh");
        startInfo.ArgumentList.Add("-lc");
        startInfo.ArgumentList.Add(shellCommand);

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new ElevatedStartResult
            {
                Success = false,
                ErrorMessage = $"pkexec unavailable: {ex.Message}"
            };
        }

        var output = (await process.StandardOutput.ReadToEndAsync()).Trim();
        var error = (await process.StandardError.ReadToEndAsync()).Trim();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            return new ElevatedStartResult
            {
                Success = false,
                ErrorMessage = string.IsNullOrWhiteSpace(error)
                    ? "Root permission denied or pkexec failed"
                    : error
            };
        }

        if (!int.TryParse(output, out var pid))
        {
            return new ElevatedStartResult
            {
                Success = false,
                ErrorMessage = $"Failed to parse elevated PID: '{output}'"
            };
        }

        return new ElevatedStartResult { Success = true, Pid = pid };
    }

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

    private static async Task RunAppleScriptAdminCommandAsync(string shellCommand, string? prompt = null)
    {
        var promptClause = string.IsNullOrWhiteSpace(prompt)
            ? string.Empty
            : $" with prompt \"{EscapeForAppleScript(prompt)}\"";
        var appleScript = $"do shell script \"{EscapeForAppleScript(shellCommand)}\"{promptClause} with administrator privileges";
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "osascript",
                Arguments = $"-e \"{EscapeForAppleScript(appleScript)}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        process.Start();
        await process.WaitForExitAsync();
    }

    private static async Task RunPkexecCommandAsync(string shellCommand)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "pkexec",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("/bin/sh");
        startInfo.ArgumentList.Add("-lc");
        startInfo.ArgumentList.Add(shellCommand);

        using var process = new Process { StartInfo = startInfo };

        process.Start();
        await process.WaitForExitAsync();
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

    private sealed class ElevatedStartResult
    {
        public bool Success { get; init; }
        public int? Pid { get; init; }
        public string? ErrorMessage { get; init; }
        public string RawOutput { get; init; } = string.Empty;
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



