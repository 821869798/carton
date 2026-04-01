using System.Net;
using System.Net.Http.Headers;

namespace carton.Core.Services;

/// <summary>
/// Shared HttpClient instances to avoid socket exhaustion and reduce memory overhead.
/// </summary>
public static class HttpClientFactory
{
    private static HttpClient _localApi = null!;
    private static string _appVersion = "1.0";
    public static string LocalApiAddress { get; private set; } = string.Empty;
    public static int LocalApiPort { get; private set; }
    public static string? LocalApiSecret { get; private set; }

    static HttpClientFactory()
    {
        UpdateLocalApi("127.0.0.1", 9090, null);
    }

    /// <summary>
    /// Call once at application startup to set the app version used in User-Agent.
    /// Must be called before External is first accessed.
    /// </summary>
    public static void Initialize(string appVersion)
    {
        if (!string.IsNullOrWhiteSpace(appVersion))
        {
            _appVersion = appVersion;
        }
    }

    /// <summary>
    /// Client for the local sing-box / Clash API.
    /// </summary>
    public static HttpClient LocalApi => _localApi;

    public static void UpdateLocalApi(string host, int port, string? secret)
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri($"http://{host}:{port}/"),
            Timeout = TimeSpan.FromSeconds(5)
        };

        if (!string.IsNullOrWhiteSpace(secret))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        }

        _localApi = client;
        LocalApiAddress = $"http://{host}:{port}";
        LocalApiPort = port;
        LocalApiSecret = string.IsNullOrWhiteSpace(secret) ? null : secret;
    }

    /// <summary>
    /// Client for external requests (GitHub, remote config downloads, etc.).
    /// Lazy-created so that Initialize(appVersion) takes effect before first use.
    /// </summary>
    private static HttpClient? _external;
    public static HttpClient External => _external ??= CreateExternalClient();

    private static HttpClient CreateExternalClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        ConfigureExternalClient(client);
        return client;
    }

    public static HttpClient CreateExternalProxyClient(string host, int port)
    {
        // This is the proxy endpoint scheme, not the destination scheme:
        // HTTPS URLs still work here because HttpClient tunnels them via CONNECT
        // through the local mixed-port HTTP proxy.
        var handler = new HttpClientHandler
        {
            UseProxy = true,
            Proxy = new WebProxy($"http://{host}:{port}")
        };

        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        ConfigureExternalClient(client);
        return client;
    }

    private static void ConfigureExternalClient(HttpClient client)
    {
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", $"carton/{_appVersion} (sing-box 1.13.0; sing-box/1.13.0)");
    }
}
