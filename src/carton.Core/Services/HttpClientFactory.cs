using System.Net.Http.Headers;

namespace carton.Core.Services;

/// <summary>
/// Shared HttpClient instances to avoid socket exhaustion and reduce memory overhead.
/// </summary>
public static class HttpClientFactory
{
    private static HttpClient _localApi = null!;
    public static string LocalApiAddress { get; private set; } = string.Empty;
    public static int LocalApiPort { get; private set; }
    public static string? LocalApiSecret { get; private set; }

    static HttpClientFactory()
    {
        UpdateLocalApi("127.0.0.1", 9090, null);
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
    /// </summary>
    public static HttpClient External { get; } = CreateExternalClient();

    private static HttpClient CreateExternalClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Carton/1.0");
        return client;
    }
}
