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
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", $"carton/{_appVersion} (sing-box 1.13.0; sing-box/1.13.0)");
        return client;
    }
}
