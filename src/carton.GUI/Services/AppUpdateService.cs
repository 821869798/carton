using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Versioning;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace carton.GUI.Services;

public interface IAppUpdateService
{
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
        IProgress<int>? progress = null,
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

public sealed class AppUpdateService : IAppUpdateService
{
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
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("carton", Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0"));
        if (!string.IsNullOrWhiteSpace(_token))
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        }

        CurrentVersion = ResolveCurrentVersion() ?? "0.0.0";
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

        if (!IsRemoteVersionNewer(releaseInfo.Version))
        {
            Log($"Current version ({CurrentVersion}) is up to date for channel={channel}");
            return null;
        }

        var manager = CreateManager(channel);
        try
        {
            Log($"Checking Velopack feed for updates (channel={channel})");

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

        for (var page = 1; page <= maxPages; page++)
        {
            var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://api.github.com/repos/{_repoOwner}/{_repoName}/releases?per_page={pageSize}&page={page}");

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Log($"GitHub releases fetch failed: {(int)response.StatusCode} {response.ReasonPhrase}");
                return null;
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

                var isPrerelease = releaseElement.TryGetProperty("prerelease", out var prereleaseProp) &&
                                   prereleaseProp.GetBoolean();
                if (wantsPrerelease != isPrerelease)
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

                var version = NormalizeVersion(tag);
                var name = releaseElement.TryGetProperty("name", out var nameProp)
                    ? nameProp.GetString() ?? tag
                    : tag;
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

                return new GitHubReleaseInfo(tag, version, isPrerelease, name, body, assets, publishedAt);
            }

            if (releases.GetArrayLength() < pageSize)
            {
                break;
            }
        }

        return null;
    }

    public async Task DownloadUpdateAsync(
        AppUpdateResult update,
        string channel,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (update == null)
        {
            throw new ArgumentNullException(nameof(update));
        }

        var manager = CreateManager(channel);
        try
        {
            Log($"Downloading update {update.Version} (channel={channel})");

            await manager.DownloadUpdatesAsync(
                update.UpdateInfo,
                percent => progress?.Report(percent),
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
            await updater.WaitExitThenApplyUpdatesAsync(
                _stagedRelease,
                true,
                silentRestart,
                Array.Empty<string>()).ConfigureAwait(false);
        }
        finally
        {
            DisposeManager(updater);
        }
    }

    private UpdateManager CreateManager(string? channel)
    {
        var normalizedChannel = string.IsNullOrWhiteSpace(channel)
            ? null
            : channel.Trim();

        var options = new UpdateOptions
        {
            ExplicitChannel = normalizedChannel,
            AllowVersionDowngrade = false,
            MaximumDeltasBeforeFallback = 2
        };

        var source = new GithubSource(_repositoryUrl, _token ?? string.Empty, normalizedChannel == "beta", null);
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

    private bool IsRemoteVersionNewer(string remoteVersion)
    {
        if (SemanticVersion.TryParse(remoteVersion, out var remote) &&
            SemanticVersion.TryParse(CurrentVersion, out var local))
        {
            return remote > local;
        }

        if (Version.TryParse(remoteVersion, out var remoteVersionParsed) &&
            Version.TryParse(CurrentVersion, out var localVersionParsed))
        {
            return remoteVersionParsed > localVersionParsed;
        }

        return !string.Equals(remoteVersion, CurrentVersion, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveCurrentVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        if (assembly == null)
        {
            return null;
        }

        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            return StripBuildMetadata(informational);
        }

        return assembly.GetName().Version?.ToString();
    }

    private static string StripBuildMetadata(string version)
    {
        var plusIndex = version.IndexOf('+');
        return plusIndex >= 0 ? version[..plusIndex] : version;
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
