using System.Diagnostics;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using carton.Core.Models;
using carton.Core.Serialization;
using carton.Core.Utilities;

namespace carton.Core.Services;

public partial class SingBoxManager
{
    private const string DefaultDelayTestUrl = "https://www.gstatic.com/generate_204";

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
                    if (!proxy.TryGetProperty("all", out var allElement) ||
                        allElement.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    var group = new OutboundGroup
                    {
                        Tag = proxyProperty.Name,
                        Type = ReadString(proxy, "type"),
                        Selected = ReadString(proxy, "now")
                    };

                    foreach (var item in allElement.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.String)
                        {
                            continue;
                        }

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

                    if (group.Items.Count == 0)
                    {
                        continue;
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
        var request = new OutboundSelectionRequest { Name = outboundTag };
        var payload = JsonSerializer.Serialize(
            request,
            CartonCoreJsonContext.Default.OutboundSelectionRequest);
        var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PutAsync($"{_apiAddress}/proxies/{Uri.EscapeDataString(groupTag)}", content);
        response.EnsureSuccessStatusCode();
    }

    public async Task<Dictionary<string, int>> RunGroupDelayTestAsync(string groupTag, string? testUrl = null, int timeoutMs = 5000)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(groupTag))
        {
            return result;
        }

        var urlParam = Uri.EscapeDataString(string.IsNullOrWhiteSpace(testUrl) ? DefaultDelayTestUrl : testUrl);

        try
        {
            var endpoint = $"{_apiAddress}/group/{Uri.EscapeDataString(groupTag)}/delay?timeout={timeoutMs}&url={urlParam}";
            using var response = await _httpClient.GetAsync(endpoint);
            if (!response.IsSuccessStatusCode)
            {
                return result;
            }

            var payload = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(payload);

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Number &&
                    property.Value.TryGetInt32(out var delay))
                {
                    result[property.Name] = delay;
                }
            }
        }
        catch
        {
        }

        return result;
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
}
