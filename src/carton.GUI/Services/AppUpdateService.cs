using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using carton.Core.Utilities;
using NuGet.Versioning;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace carton.GUI.Services;

public interface IAppUpdateService
{
    event EventHandler? GitHubApiFallbackOccurred;

    string CurrentVersion { get; }

    bool IsUpdatePendingRestart { get; }

    string? PendingRestartVersion { get; }

    bool SupportsInAppUpdates { get; }

    string ReleasesPageUrl { get; }

    Task<AppUpdateResult?> CheckForUpdatesAsync(
        string channel,
        CancellationToken cancellationToken = default);

    Task<GitHubReleaseInfo?> GetLatestReleaseInfoAsync(
        string channel,
        CancellationToken cancellationToken = default);

    Task DownloadUpdateAsync(
        AppUpdateResult update,
        string channel,
        IProgress<AppUpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task RestartToApplyDownloadedUpdateAsync(bool silentRestart = false);
}

public sealed record AppUpdateResult(
    string Version,
    string? ReleaseNotesMarkdown,
    string Channel,
    UpdateInfo UpdateInfo,
    GitHubReleaseInfo ReleaseInfo);

public sealed record GitHubReleaseInfo(
    string Tag,
    string Version,
    bool IsPrerelease,
    string Name,
    string Body,
    IReadOnlyList<GitHubAssetInfo> Assets,
    DateTimeOffset PublishedAt);

public sealed record GitHubAssetInfo(
    string Name,
    string DownloadUrl,
    long Size);

public sealed record AppUpdateDownloadProgress(
    int Percent,
    long BytesReceived,
    long TotalBytes);

public sealed class AppUpdateService : IAppUpdateService
{
    private const string ForceGitHubApiFailEnvironmentVariable = "CARTON_FORCE_GITHUB_API_FAIL";
    private static readonly TimeSpan GitHubLookupTimeout = TimeSpan.FromSeconds(6);
    private readonly string _repositoryUrl;
    private readonly string? _token;
    private readonly Action<string>? _log;
    private readonly Lazy<IVelopackLocator> _locator;
    private readonly HttpClient _httpClient;
    private readonly string _repoOwner;
    private readonly string _repoName;
    private readonly bool _supportsInAppUpdates;

    private VelopackAsset? _stagedRelease;
    private string? _stagedChannel;

    public event EventHandler? GitHubApiFallbackOccurred;

    public AppUpdateService(string repositoryUrl, string? token = null, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(repositoryUrl))
        {
            throw new ArgumentException("Repository URL must be provided", nameof(repositoryUrl));
        }

        _repositoryUrl = repositoryUrl;
        var repo = ParseRepository(repositoryUrl);
        _repoOwner = repo.owner;
        _repoName = repo.repo;
        _repositoryUrl = $"https://github.com/{_repoOwner}/{_repoName}";
        _token = token;
        _log = log;
        _locator = new Lazy<IVelopackLocator>(() =>
            VelopackLocator.Current ?? VelopackLocator.CreateDefaultForPlatform());
        _httpClient = new HttpClient
        {
            Timeout = GitHubLookupTimeout
        };
        _httpClient.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("carton", CartonApplicationInfo.Version));
        if (!string.IsNullOrWhiteSpace(_token))
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        }

        CurrentVersion = CartonApplicationInfo.Version;
        _supportsInAppUpdates = DetermineSupportsInAppUpdates();
    }

    public string CurrentVersion { get; }

    public bool SupportsInAppUpdates => _supportsInAppUpdates;

    public string ReleasesPageUrl => $"{_repositoryUrl}/releases";

    public string? PendingRestartVersion
    {
        get
        {
            var release = GetPendingRestartRelease();
            if (release?.Version == null)
            {
                return null;
            }

            return NormalizeVersion(release.Version.ToString());
        }
    }

    public bool IsUpdatePendingRestart
    {
        get
        {
            return GetPendingRestartRelease() != null;
        }
    }

    public async Task<AppUpdateResult?> CheckForUpdatesAsync(
        string channel,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var releaseInfo = await GetLatestReleaseInfoAsync(channel, cancellationToken).ConfigureAwait(false);
        if (releaseInfo == null)
        {
            Log($"No release found for channel={channel}");
            return null;
        }

        if (!IsRemoteVersionDifferent(releaseInfo.Version))
        {
            Log($"Current version ({CurrentVersion}) is up to date for channel={channel}");
            return null;
        }

        if (ShouldForceGitHubApiFailure())
        {
            Log($"Skipping Velopack GitHub source check because {ForceGitHubApiFailEnvironmentVariable}=1");
            return new AppUpdateResult(releaseInfo.Version, releaseInfo.Body, channel, null!, releaseInfo);
        }

        var manager = CreateManager(channel, allowVersionDowngrade: true);
        try
        {
            Log($"Checking Velopack feed for updates (channel={channel}, allowVersionDowngrade={true})");

            var info = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (info?.TargetFullRelease == null)
            {
                _stagedRelease = manager.UpdatePendingRestart;
                Log("Velopack feed returned no updates.");
                return null;
            }

            var version = info.TargetFullRelease.Version?.ToString() ?? releaseInfo.Version;
            return new AppUpdateResult(version, releaseInfo.Body, channel, info, releaseInfo);
        }
        finally
        {
            DisposeManager(manager);
        }
    }

    public async Task<GitHubReleaseInfo?> GetLatestReleaseInfoAsync(
        string channel,
        CancellationToken cancellationToken = default)
    {
        var wantsPrerelease = IsPrereleaseChannel(channel);
        const int pageSize = 50;
        const int maxPages = 5;
        try
        {
            for (var page = 1; page <= maxPages; page++)
            {
                var apiUrl = $"https://api.github.com/repos/{_repoOwner}/{_repoName}/releases?per_page={pageSize}&page={page}";
                using var response = await SendGitHubApiRequestAsync(HttpMethod.Get, apiUrl, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    var message = $"GitHub releases fetch failed: {(int)response.StatusCode} {response.ReasonPhrase}";
                    Log(message);
                    throw new HttpRequestException(message, null, response.StatusCode);
                }

                await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var document = await JsonDocument.ParseAsync(contentStream, cancellationToken: cancellationToken).ConfigureAwait(false);
                var releases = document.RootElement;
                if (releases.ValueKind != JsonValueKind.Array || releases.GetArrayLength() == 0)
                {
                    return null;
                }

                foreach (var releaseElement in releases.EnumerateArray())
                {
                    var isDraft = releaseElement.TryGetProperty("draft", out var draftProp) && draftProp.GetBoolean();
                    if (isDraft)
                    {
                        continue;
                    }

                    var tag = releaseElement.TryGetProperty("tag_name", out var tagProp)
                        ? tagProp.GetString() ?? string.Empty
                        : string.Empty;
                    if (string.IsNullOrWhiteSpace(tag))
                    {
                        continue;
                    }

                    var isPrerelease = releaseElement.TryGetProperty("prerelease", out var prereleaseProp) &&
                                       prereleaseProp.GetBoolean();
                    var name = releaseElement.TryGetProperty("name", out var nameProp)
                        ? nameProp.GetString() ?? tag
                        : tag;
                    var resolvedPrerelease = IsLikelyPrerelease(isPrerelease, tag, name);
                    if (wantsPrerelease != resolvedPrerelease)
                    {
                        continue;
                    }

                    var version = NormalizeVersion(tag);
                    var body = releaseElement.TryGetProperty("body", out var bodyProp)
                        ? bodyProp.GetString() ?? string.Empty
                        : string.Empty;
                    var publishedAt = releaseElement.TryGetProperty("published_at", out var publishedProp)
                        ? ParseDateTime(publishedProp)
                        : DateTimeOffset.MinValue;

                    var assets = new List<GitHubAssetInfo>();
                    if (releaseElement.TryGetProperty("assets", out var assetsElement) && assetsElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var asset in assetsElement.EnumerateArray())
                        {
                            var downloadUrl = asset.TryGetProperty("browser_download_url", out var urlProp)
                                ? urlProp.GetString() ?? string.Empty
                                : string.Empty;
                            if (string.IsNullOrWhiteSpace(downloadUrl))
                            {
                                continue;
                            }

                            assets.Add(new GitHubAssetInfo(
                                asset.TryGetProperty("name", out var assetNameProp) ? assetNameProp.GetString() ?? string.Empty : string.Empty,
                                downloadUrl,
                                asset.TryGetProperty("size", out var sizeProp) ? sizeProp.GetInt64() : 0));
                        }
                    }

                    return new GitHubReleaseInfo(tag, version, resolvedPrerelease, name, body, assets, publishedAt);
                }

                if (releases.GetArrayLength() < pageSize)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Log($"GitHub API release lookup failed, falling back to releases page: {ex.Message}");
            GitHubApiFallbackOccurred?.Invoke(this, EventArgs.Empty);
        }

        var fallbackRelease = await GetLatestReleaseInfoFromReleasesPageAsync(wantsPrerelease, cancellationToken).ConfigureAwait(false);
        if (fallbackRelease != null)
        {
            return fallbackRelease;
        }

        Log($"No matching GitHub release found for channel={channel}");
        return null;
    }

    public async Task DownloadUpdateAsync(
        AppUpdateResult update,
        string channel,
        IProgress<AppUpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (update == null)
        {
            throw new ArgumentNullException(nameof(update));
        }

        if (ShouldForceGitHubApiFailure())
        {
            throw new InvalidOperationException(
                $"Download is disabled while {ForceGitHubApiFailEnvironmentVariable}=1 because the check is running in simulated fallback mode.");
        }

        var totalBytes = ResolveDownloadSize(update);
        var manager = CreateManager(channel);
        try
        {
            Log($"Downloading update {update.Version} (channel={channel})");

            await manager.DownloadUpdatesAsync(
                update.UpdateInfo,
                percent =>
                {
                    var normalizedPercent = Math.Clamp(percent, 0, 100);
                    var bytesReceived = totalBytes <= 0
                        ? 0
                        : (long)Math.Round(totalBytes * (normalizedPercent / 100d), MidpointRounding.AwayFromZero);
                    progress?.Report(new AppUpdateDownloadProgress(normalizedPercent, bytesReceived, totalBytes));
                },
                cancellationToken).ConfigureAwait(false);

            _stagedRelease = update.UpdateInfo.TargetFullRelease;
            _stagedChannel = channel;
        }
        finally
        {
            DisposeManager(manager);
        }
    }

    public async Task RestartToApplyDownloadedUpdateAsync(bool silentRestart = false)
    {
        if (_stagedRelease == null)
        {
            _stagedRelease = GetPendingRestartRelease();
        }

        if (_stagedRelease == null)
        {
            throw new InvalidOperationException("No downloaded update is ready to apply.");
        }

        Log($"Applying update {_stagedRelease.Version} (restart={true})");
        var updater = CreateManager(_stagedChannel);
        try
        {
            updater.ApplyUpdatesAndRestart(
                _stagedRelease,
                Array.Empty<string>());
            await Task.CompletedTask.ConfigureAwait(false);
        }
        finally
        {
            DisposeManager(updater);
        }
    }

    private UpdateManager CreateManager(string? channel, bool allowVersionDowngrade = false)
    {
        var normalizedChannel = ResolveVelopackChannel(channel);

        var options = new UpdateOptions
        {
            ExplicitChannel = normalizedChannel,
            AllowVersionDowngrade = allowVersionDowngrade,
            MaximumDeltasBeforeFallback = 2
        };

        var source = new GithubSource(_repositoryUrl, _token ?? string.Empty, IsPrereleaseChannel(channel), null);
        return new UpdateManager(source, options, _locator.Value);
    }

    private static void DisposeManager(UpdateManager? manager)
    {
        if (manager == null)
        {
            return;
        }

        if (manager is IAsyncDisposable asyncDisposable)
        {
            asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
            return;
        }

        if (manager is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private void Log(string message)
    {
        _log?.Invoke(message);
    }

    private static long ResolveDownloadSize(AppUpdateResult update)
    {
        var deltaPackages = update.UpdateInfo.DeltasToTarget;
        if (deltaPackages is { Length: > 0 })
        {
            var deltaBytes = deltaPackages
                .Where(asset => asset != null)
                .Sum(asset => Math.Max(0, asset.Size));
            if (deltaBytes > 0)
            {
                return deltaBytes;
            }
        }

        var fullRelease = update.UpdateInfo.TargetFullRelease;
        if (fullRelease != null && fullRelease.Size > 0)
        {
            return fullRelease.Size;
        }

        var fileName = fullRelease?.FileName;
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            var matchedAsset = update.ReleaseInfo.Assets.FirstOrDefault(asset =>
                string.Equals(asset.Name, fileName, StringComparison.OrdinalIgnoreCase));
            if (matchedAsset != null && matchedAsset.Size > 0)
            {
                return matchedAsset.Size;
            }
        }

        return 0;
    }

    private VelopackAsset? GetPendingRestartRelease()
    {
        if (_stagedRelease != null)
        {
            return _stagedRelease;
        }

        var manager = CreateManager(_stagedChannel);
        try
        {
            _stagedRelease = manager.UpdatePendingRestart;
            return _stagedRelease;
        }
        finally
        {
            DisposeManager(manager);
        }
    }

    private bool DetermineSupportsInAppUpdates()
    {
        try
        {
            var locator = _locator.Value;
            if (locator == null)
            {
                return false;
            }

            if (locator.IsPortable)
            {
                return false;
            }

            if (OperatingSystem.IsWindows())
            {
                var updateExePath = locator.UpdateExePath;
                if (string.IsNullOrWhiteSpace(updateExePath) || !File.Exists(updateExePath))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            Log($"Failed to determine update capability: {ex.Message}");
            return false;
        }
    }

    private bool IsRemoteVersionDifferent(string remoteVersion)
    {
        return !string.Equals(remoteVersion, CurrentVersion, StringComparison.OrdinalIgnoreCase);
    }

    private static (string owner, string repo) ParseRepository(string repositoryUrl)
    {
        var uri = new Uri(repositoryUrl);
        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
        {
            throw new ArgumentException("Repository URL must be in the form https://github.com/<owner>/<repo>", nameof(repositoryUrl));
        }

        return (segments[0], segments[1].Replace(".git", string.Empty, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeVersion(string tag)
    {
        var normalized = tag.Trim();
        if (normalized.StartsWith("refs/tags/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["refs/tags/".Length..];
        }

        if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[1..];
        }

        return normalized;
    }

    private static bool IsPrereleaseChannel(string? channel)
        => string.Equals(channel, "beta", StringComparison.OrdinalIgnoreCase);

    private static bool IsLikelyPrerelease(bool isPrerelease, string? tag, string? name)
        => isPrerelease || IsLikelyPrereleaseTag(tag) || IsLikelyPrereleaseTag(name);

    private static bool IsLikelyPrereleaseTag(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return Regex.IsMatch(
            value,
            @"(?:^|[.\-_])(?:alpha|beta|preview|pre|rc)(?:[.\-_]?\d+)?(?:$|[.\-_])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool ShouldForceGitHubApiFailure()
        => string.Equals(
            Environment.GetEnvironmentVariable(ForceGitHubApiFailEnvironmentVariable),
            "1",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsGitHubApiUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
           string.Equals(uri.Host, "api.github.com", StringComparison.OrdinalIgnoreCase);

    private async Task<HttpResponseMessage> SendGitHubApiRequestAsync(
        HttpMethod method,
        string url,
        CancellationToken cancellationToken)
    {
        if (ShouldForceGitHubApiFailure() && IsGitHubApiUrl(url))
        {
            throw new HttpRequestException(
                $"Simulated GitHub API failure via {ForceGitHubApiFailEnvironmentVariable}",
                null,
                System.Net.HttpStatusCode.Forbidden);
        }

        var request = new HttpRequestMessage(method, url);
        return await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GitHubReleaseInfo?> GetLatestReleaseInfoFromReleasesPageAsync(
        bool wantsPrerelease,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var response = await _httpClient.GetAsync(ReleasesPageUrl, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var matches = Regex.Matches(
            html,
            @"/" + Regex.Escape(_repoOwner) + "/" + Regex.Escape(_repoName) + @"/releases/tag/(?<tag>[^""#?&/]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (matches.Count == 0)
        {
            return null;
        }

        var seenTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in matches)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var tag = Uri.UnescapeDataString(match.Groups["tag"].Value);
            if (string.IsNullOrWhiteSpace(tag) || !seenTags.Add(tag))
            {
                continue;
            }

            var isPrerelease = IsLikelyPrereleaseTag(tag);
            if (wantsPrerelease != isPrerelease)
            {
                continue;
            }

            return new GitHubReleaseInfo(
                tag,
                NormalizeVersion(tag),
                isPrerelease,
                tag,
                string.Empty,
                [],
                DateTimeOffset.MinValue);
        }

        return null;
    }

    private static string ResolveVelopackChannel(string? channel)
    {
        var channelSuffix = IsPrereleaseChannel(channel) ? "beta" : "release";
        return $"{GetPlatformRidPrefix()}-{channelSuffix}";
    }

    private static string GetPlatformRidPrefix()
    {
        if (OperatingSystem.IsWindows())
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => "win-arm64",
                Architecture.X64 => "win-x64",
                _ => "win"
            };
        }

        if (OperatingSystem.IsLinux())
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => "linux-arm64",
                Architecture.X64 => "linux-x64",
                _ => "linux"
            };
        }

        if (OperatingSystem.IsMacOS())
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => "osx-arm64",
                Architecture.X64 => "osx-x64",
                _ => "osx"
            };
        }

        return "unknown";
    }

    private static DateTimeOffset ParseDateTime(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(element.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto))
        {
            return dto;
        }

        return DateTimeOffset.MinValue;
    }
}
