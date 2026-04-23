using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using carton.Core.Utilities;
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
    private static readonly TimeSpan GitHubLookupTimeout = TimeSpan.FromSeconds(6);
    private readonly string _repositoryUrl;
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
        _log = log;
        _locator = new Lazy<IVelopackLocator>(() =>
            VelopackLocator.Current ?? VelopackLocator.CreateDefaultForPlatform());
        _httpClient = new HttpClient
        {
            Timeout = GitHubLookupTimeout
        };
        _httpClient.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("carton", CartonApplicationInfo.Version));

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

        var manager = CreateManager(channel, releaseInfo, allowVersionDowngrade: true);
        try
        {
            Log($"Checking Velopack feed from release assets (channel={channel}, tag={releaseInfo.Tag}, allowVersionDowngrade={true})");

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
        try
        {
            var atomRelease = await GetLatestReleaseInfoFromAtomFeedAsync(wantsPrerelease, cancellationToken).ConfigureAwait(false);
            if (atomRelease != null)
            {
                return atomRelease;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            Log($"GitHub releases Atom lookup failed, falling back to releases page: {ex.Message}");
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

        var totalBytes = ResolveDownloadSize(update);
        var manager = CreateManager(channel, update.ReleaseInfo);
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

    private UpdateManager CreateManager(
        string? channel,
        GitHubReleaseInfo? releaseInfo = null,
        bool allowVersionDowngrade = false)
    {
        var normalizedChannel = ResolveVelopackChannel(channel);

        var options = new UpdateOptions
        {
            ExplicitChannel = normalizedChannel,
            AllowVersionDowngrade = allowVersionDowngrade,
            MaximumDeltasBeforeFallback = 2
        };

        var downloader = new HttpClientFileDownloader();
        var source = new SimpleWebSource(
            GetReleaseDownloadBaseUrl(releaseInfo?.Tag),
            downloader,
            GitHubLookupTimeout.TotalMinutes);
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

    private string GetReleasesAtomUrl()
        => $"{_repositoryUrl}/releases.atom";

    private string GetReleaseDownloadBaseUrl(string? tag)
    {
        var resolvedTag = string.IsNullOrWhiteSpace(tag)
            ? GetDefaultReleaseTag()
            : tag.Trim();
        return $"{_repositoryUrl}/releases/download/{Uri.EscapeDataString(resolvedTag)}";
    }

    private string GetDefaultReleaseTag()
        => CurrentVersion.StartsWith("v", StringComparison.OrdinalIgnoreCase)
            ? CurrentVersion
            : $"v{CurrentVersion}";

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

    private async Task<GitHubReleaseInfo?> GetLatestReleaseInfoFromAtomFeedAsync(
        bool wantsPrerelease,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var response = await _httpClient.GetAsync(GetReleasesAtomUrl(), cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var atom = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        foreach (var release in ParseReleaseAtomFeed(atom))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (wantsPrerelease != release.IsPrerelease)
            {
                continue;
            }

            return new GitHubReleaseInfo(
                release.Tag,
                NormalizeVersion(release.Tag),
                release.IsPrerelease,
                string.IsNullOrWhiteSpace(release.Title) ? release.Tag : release.Title,
                release.Body,
                [],
                release.PublishedAt);
        }

        return null;
    }

    private static IEnumerable<GitHubAtomReleaseInfo> ParseReleaseAtomFeed(string atom)
    {
        var document = XDocument.Parse(atom);
        XNamespace atomNamespace = "http://www.w3.org/2005/Atom";

        foreach (var entry in document.Descendants(atomNamespace + "entry"))
        {
            var title = entry.Element(atomNamespace + "title")?.Value?.Trim() ?? string.Empty;
            var tag = TryExtractReleaseTag(entry.Element(atomNamespace + "id")?.Value);
            foreach (var link in entry.Elements(atomNamespace + "link"))
            {
                tag ??= TryExtractReleaseTag(link.Attribute("href")?.Value);
            }

            if (string.IsNullOrWhiteSpace(tag))
            {
                continue;
            }

            var body = NormalizeReleaseBody(
                entry.Element(atomNamespace + "content")?.Value,
                entry.Element(atomNamespace + "summary")?.Value);

            yield return new GitHubAtomReleaseInfo(
                tag,
                title,
                body,
                IsLikelyPrerelease(false, tag, title),
                ParseAtomDateTime(
                    entry.Element(atomNamespace + "published")?.Value,
                    entry.Element(atomNamespace + "updated")?.Value));
        }
    }

    private static string? TryExtractReleaseTag(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = Regex.Match(
            value,
            @"/releases/tag/(?<tag>[^""#?&/]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success
            ? Uri.UnescapeDataString(match.Groups["tag"].Value)
            : null;
    }

    private static string NormalizeReleaseBody(string? htmlContent, string? summary)
    {
        var content = string.IsNullOrWhiteSpace(htmlContent) ? summary : htmlContent;
        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        var normalized = Regex.Replace(content, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"</p\s*>", "\n\n", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"<[^>]+>", string.Empty, RegexOptions.CultureInvariant);
        return WebUtility.HtmlDecode(normalized).Trim();
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

    private static DateTimeOffset ParseAtomDateTime(params string?[] values)
    {
        foreach (var value in values)
        {
            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto))
            {
                return dto;
            }
        }

        return DateTimeOffset.MinValue;
    }

    private sealed record GitHubAtomReleaseInfo(
        string Tag,
        string Title,
        string Body,
        bool IsPrerelease,
        DateTimeOffset PublishedAt);
}
