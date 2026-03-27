using System.Buffers;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using carton.Core.Models;
using carton.Core.Utilities;

namespace carton.Core.Services;

public partial class SingBoxManager
{
    private static readonly string[] TrafficUplinkPropertyNames = ["uplink", "up", "upload"];
    private static readonly string[] TrafficDownlinkPropertyNames = ["downlink", "down", "download"];
    private static readonly string[] MemoryPropertyNames = ["inuse", "inUse", "memory", "value"];
    private const int MessageBufferTrimThreshold = 64 * 1024;
    private const int MessageBufferInitialCapacity = 4 * 1024;
    private const int MaxMonitorMessageBytes = 64 * 1024;

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

    private async Task StartTrafficMonitorAsync()
    {
        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        var stream = new MemoryStream();
        ClientWebSocket? webSocket = null;
        var skippingOversizedMessage = false;
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
                        ResetMessageBuffer(stream);
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
                        if (skippingOversizedMessage)
                        {
                            if (result.EndOfMessage)
                            {
                                skippingOversizedMessage = false;
                                ResetMessageBuffer(stream);
                            }

                            continue;
                        }

                        if (stream.Length + result.Count > MaxMonitorMessageBytes)
                        {
                            LogManager($"[WARN] Traffic monitor payload exceeded {MaxMonitorMessageBytes} bytes and was discarded");
                            skippingOversizedMessage = !result.EndOfMessage;
                            ResetMessageBuffer(stream);
                            continue;
                        }

                        stream.Write(buffer, 0, result.Count);
                    }

                    if (!result.EndOfMessage)
                    {
                        continue;
                    }

                    if (result.MessageType != WebSocketMessageType.Text)
                    {
                        ResetMessageBuffer(stream);
                        continue;
                    }

                    if (stream.Length == 0)
                    {
                        continue;
                    }

                    var payload = Encoding.UTF8.GetString(stream.GetBuffer().AsSpan(0, (int)stream.Length));
                    ResetMessageBuffer(stream);
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

                    ResetMessageBuffer(stream);
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

    private async Task StartMemoryMonitorAsync()
    {
        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        var stream = new MemoryStream();
        ClientWebSocket? webSocket = null;
        var skippingOversizedMessage = false;
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
                        ResetMessageBuffer(stream);
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
                        if (skippingOversizedMessage)
                        {
                            if (result.EndOfMessage)
                            {
                                skippingOversizedMessage = false;
                                ResetMessageBuffer(stream);
                            }

                            continue;
                        }

                        if (stream.Length + result.Count > MaxMonitorMessageBytes)
                        {
                            LogManager($"[WARN] Memory monitor payload exceeded {MaxMonitorMessageBytes} bytes and was discarded");
                            skippingOversizedMessage = !result.EndOfMessage;
                            ResetMessageBuffer(stream);
                            continue;
                        }

                        stream.Write(buffer, 0, result.Count);
                    }

                    if (!result.EndOfMessage)
                    {
                        continue;
                    }

                    if (result.MessageType != WebSocketMessageType.Text)
                    {
                        ResetMessageBuffer(stream);
                        continue;
                    }

                    if (stream.Length == 0)
                    {
                        continue;
                    }

                    var payload = Encoding.UTF8.GetString(stream.GetBuffer().AsSpan(0, (int)stream.Length));
                    ResetMessageBuffer(stream);
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

                    ResetMessageBuffer(stream);
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
            var uplink = ReadTrafficValue(root, TrafficUplinkPropertyNames);
            var downlink = ReadTrafficValue(root, TrafficDownlinkPropertyNames);
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
            if (TryReadTrafficValue(root, MemoryPropertyNames, out var value))
            {
                return value;
            }

            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("now", out var nowElement) &&
                TryReadTrafficValue(nowElement, MemoryPropertyNames, out value))
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

    private static long ReadTrafficValue(JsonElement root, IReadOnlyList<string> propertyNames)
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

    private static void ResetMessageBuffer(MemoryStream stream)
    {
        stream.SetLength(0);
        if (stream.Capacity <= MessageBufferTrimThreshold)
        {
            return;
        }

        stream.Capacity = MessageBufferInitialCapacity;
    }
}
