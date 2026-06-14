using System;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using carton.Core.Models;
using carton.Core.Services;
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

    bool SupportsDirectInstallerUpdates { get; }

    bool SupportsDirectPortableUpdates { get; }

    string ReleasesPageUrl { get; }

    long ResolveExpectedDownloadSize(AppUpdateResult update);

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
    UpdateInfo? UpdateInfo,
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
    private static readonly TimeSpan GitHubApiPreferredWait = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan GitHubLookupTimeout = TimeSpan.FromSeconds(6);
    private const string WindowsPortableHelperExecutableName = "carton-helper.exe";
    private const string UnixPortableHelperExecutableName = "carton-helper";
    private const string DefaultWindowsMainExecutableName = "carton.exe";
    private const string DefaultUnixMainExecutableName = "carton";
    private const string PendingDirectUpdateStateFileName = ".carton_pending_update";
    private const string DirectUpdateKindInstaller = "installer";
    private const string DirectUpdateKindPortable = "portable";
    private readonly string _repositoryUrl;
    private readonly Action<string>? _log;
    private readonly Lazy<IVelopackLocator> _locator;
    private readonly HttpClient _httpClient;
    private readonly string _repoOwner;
    private readonly string _repoName;
    private readonly IGitHubUpdateCheckStrategyProvider _githubUpdateCheckStrategyProvider;
    private readonly bool _supportsInAppUpdates;
    private readonly bool _supportsDirectInstallerUpdates;
    private readonly bool _supportsDirectPortableUpdates;

    private VelopackAsset? _stagedRelease;
    private string? _stagedChannel;
    private string? _downloadedInstallerPath;
    private string? _downloadedInstallerVersion;
    private string? _downloadedPortableArchivePath;
    private string? _downloadedPortableArchiveVersion;

    public AppUpdateService(
        string repositoryUrl,
        string? token = null,
        Action<string>? log = null,
        IGitHubUpdateCheckStrategyProvider? githubUpdateCheckStrategyProvider = null)
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
        _githubUpdateCheckStrategyProvider = githubUpdateCheckStrategyProvider ??
                                             new StaticGitHubUpdateCheckStrategyProvider(GitHubUpdateCheckStrategy.ApiThenAtom);
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
        _supportsDirectInstallerUpdates = DetermineSupportsDirectInstallerUpdates();
        _supportsDirectPortableUpdates = DetermineSupportsDirectPortableUpdates();
        RestorePendingDirectUpdateState();
    }

    public string CurrentVersion { get; }

    public bool SupportsInAppUpdates => _supportsInAppUpdates;

    public bool SupportsDirectInstallerUpdates => _supportsDirectInstallerUpdates;

    public bool SupportsDirectPortableUpdates => _supportsDirectPortableUpdates;

    public string ReleasesPageUrl => $"{_repositoryUrl}/releases";

    public string? PendingRestartVersion
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_downloadedInstallerVersion))
            {
                return _downloadedInstallerVersion;
            }

            if (!string.IsNullOrWhiteSpace(_downloadedPortableArchiveVersion))
            {
                return _downloadedPortableArchiveVersion;
            }

            if (!SupportsInAppUpdates)
            {
                return null;
            }

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
            return !string.IsNullOrWhiteSpace(_downloadedInstallerPath) ||
                   !string.IsNullOrWhiteSpace(_downloadedPortableArchivePath) ||
                   (SupportsInAppUpdates && GetPendingRestartRelease() != null);
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

        if (!IsRemoteVersionNewerThanCurrent(releaseInfo.Version))
        {
            Log($"Current version ({CurrentVersion}) is up to date for channel={channel}");
            return null;
        }

        if (!SupportsInAppUpdates)
        {
            return new AppUpdateResult(releaseInfo.Version, releaseInfo.Body, channel, null, releaseInfo);
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
        var releaseSource = CreateGitHubReleaseSource();
        var release = await releaseSource.GetLatestReleaseAsync(wantsPrerelease, cancellationToken).ConfigureAwait(false);
        if (release == null)
        {
            Log($"No matching GitHub release found for channel={channel}");
            return null;
        }

        var releaseInfo = ToGitHubReleaseInfo(release);
        if (RequiresDirectReleaseAssets() && releaseInfo.Assets.Count == 0)
        {
            releaseInfo = await HydrateReleaseDetailsAsync(releaseInfo, cancellationToken).ConfigureAwait(false);
        }

        return releaseInfo;
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

        if (!SupportsInAppUpdates)
        {
            if (SupportsDirectInstallerUpdates)
            {
                await DownloadInstallerUpdateAsync(update, progress, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (SupportsDirectPortableUpdates)
            {
                await DownloadPortableUpdateAsync(update, progress, cancellationToken).ConfigureAwait(false);
                return;
            }

            throw new InvalidOperationException("Direct updates are not supported for this build.");
        }

        var totalBytes = ResolveExpectedDownloadSize(update);
        var manager = CreateManager(channel, update.ReleaseInfo);
        try
        {
            Log($"Downloading update {update.Version} (channel={channel})");

            await manager.DownloadUpdatesAsync(
                update.UpdateInfo!,
                percent =>
                {
                    var normalizedPercent = Math.Clamp(percent, 0, 100);
                    var bytesReceived = totalBytes <= 0
                        ? 0
                        : (long)Math.Round(totalBytes * (normalizedPercent / 100d), MidpointRounding.AwayFromZero);
                    progress?.Report(new AppUpdateDownloadProgress(normalizedPercent, bytesReceived, totalBytes));
                },
                cancellationToken).ConfigureAwait(false);

            _stagedRelease = update.UpdateInfo!.TargetFullRelease;
            _stagedChannel = channel;
        }
        finally
        {
            DisposeManager(manager);
        }
    }

    public async Task RestartToApplyDownloadedUpdateAsync(bool silentRestart = false)
    {
        if (!SupportsInAppUpdates)
        {
            if (!string.IsNullOrWhiteSpace(_downloadedInstallerPath) && File.Exists(_downloadedInstallerPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = _downloadedInstallerPath,
                    UseShellExecute = true
                });
                ClearPendingDirectUpdateState();
                Environment.Exit(0);
                return;
            }

            if (!string.IsNullOrWhiteSpace(_downloadedPortableArchivePath) && File.Exists(_downloadedPortableArchivePath))
            {
                StartPortableUpdater(_downloadedPortableArchivePath);
                ClearPendingDirectUpdateState();
                Environment.Exit(0);
                return;
            }

            if (SupportsDirectPortableUpdates)
            {
                throw new InvalidOperationException("No downloaded portable package is ready to apply.");
            }

            throw new InvalidOperationException("No downloaded installer is ready to apply.");
        }

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

        var downloader = new VelopackAcceleratedFileDownloader(Log);
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

    public long ResolveExpectedDownloadSize(AppUpdateResult update)
    {
        if (update.UpdateInfo == null)
        {
            var directAsset = SupportsDirectPortableUpdates
                ? ResolvePreferredPortableAsset(update.ReleaseInfo)
                : ResolvePreferredInstallerAsset(update.ReleaseInfo);
            return directAsset?.Size ?? 0;
        }

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
#if INSTALLER_BUILD
        if (OperatingSystem.IsWindows())
        {
            // Windows installer builds apply updates via the downloaded NSIS setup.
            return false;
        }
#endif

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

            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                return locator.CurrentlyInstalledVersion != null;
            }

            return true;
        }
        catch (Exception ex)
        {
            Log($"Failed to determine update capability: {ex.Message}");
            return false;
        }
    }

    private static bool DetermineSupportsDirectInstallerUpdates()
    {
#if INSTALLER_BUILD
        return OperatingSystem.IsWindows();
#else
        return false;
#endif
    }

    private static bool DetermineSupportsDirectPortableUpdates()
    {
#if INSTALLER_BUILD
        return false;
#else
        if (!SupportsPortableUpdaterPlatform())
        {
            return false;
        }

        var appDirectory = AppDomain.CurrentDomain.BaseDirectory;
        var updaterPath = Path.Combine(appDirectory, GetPortableHelperExecutableName());
        return File.Exists(updaterPath);
#endif
    }

    private static bool SupportsPortableUpdaterPlatform()
        => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

    private static string GetPortableHelperExecutableName()
        => OperatingSystem.IsWindows()
            ? WindowsPortableHelperExecutableName
            : UnixPortableHelperExecutableName;

    private static string GetDefaultMainExecutableName()
        => OperatingSystem.IsWindows()
            ? DefaultWindowsMainExecutableName
            : DefaultUnixMainExecutableName;

    private bool IsRemoteVersionNewerThanCurrent(string remoteVersion)
        => IsVersionNewerThanCurrent(remoteVersion);

    private bool IsPendingDirectUpdateNewerThanCurrent(string updateVersion)
        => IsVersionNewerThanCurrent(updateVersion);

    private bool IsVersionNewerThanCurrent(string version)
    {
        var normalizedUpdateVersion = NormalizeVersion(version);
        var normalizedCurrentVersion = NormalizeVersion(CurrentVersion);
        if (!NuGetVersion.TryParse(normalizedUpdateVersion, out var pendingVersion) ||
            !NuGetVersion.TryParse(normalizedCurrentVersion, out var currentVersion))
        {
            Log($"Failed to compare update version '{version}' with current version '{CurrentVersion}'.");
            return false;
        }

        return pendingVersion.CompareTo(currentVersion) > 0;
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
        => GitHubReleaseLookup.NormalizeVersion(tag);

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

    private PreferredGitHubReleaseSource CreateGitHubReleaseSource()
        => new(
            new GitHubApiReleaseSource(_httpClient, _repoOwner, _repoName),
            new GitHubAtomReleaseSource(_httpClient, GetReleasesAtomUrl()),
            GitHubApiPreferredWait,
            GitHubLookupTimeout,
            _githubUpdateCheckStrategyProvider,
            Log);

    private bool RequiresDirectReleaseAssets()
        => !SupportsInAppUpdates && (SupportsDirectInstallerUpdates || SupportsDirectPortableUpdates);

    private static GitHubReleaseInfo ToGitHubReleaseInfo(
        GitHubReleaseLookupResult release,
        GitHubReleaseInfo? fallback = null)
    {
        var assets = release.Assets.Count > 0
            ? release.Assets
                .Select(asset => new GitHubAssetInfo(asset.Name, asset.DownloadUrl, asset.Size))
                .ToArray()
            : fallback?.Assets ?? [];

        return new GitHubReleaseInfo(
            release.Tag,
            release.Version,
            release.IsPrerelease,
            string.IsNullOrWhiteSpace(release.Name) ? fallback?.Name ?? release.Tag : release.Name,
            string.IsNullOrWhiteSpace(release.Body) ? fallback?.Body ?? string.Empty : release.Body,
            assets,
            release.PublishedAt == DateTimeOffset.MinValue ? fallback?.PublishedAt ?? DateTimeOffset.MinValue : release.PublishedAt);
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

    private async Task<GitHubReleaseInfo> HydrateReleaseDetailsAsync(
        GitHubReleaseInfo fallback,
        CancellationToken cancellationToken)
    {
        try
        {
            var release = await new GitHubApiReleaseSource(_httpClient, _repoOwner, _repoName)
                .GetReleaseByTagAsync(fallback.Tag, cancellationToken)
                .ConfigureAwait(false);
            return release == null ? fallback : ToGitHubReleaseInfo(release, fallback);
        }
        catch
        {
            return fallback;
        }
    }

    private async Task DownloadInstallerUpdateAsync(
        AppUpdateResult update,
        IProgress<AppUpdateDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var asset = ResolvePreferredInstallerAsset(update.ReleaseInfo);
        if (asset == null)
        {
            throw new InvalidOperationException("No Windows installer asset was found for this release.");
        }

        var fileName = Path.GetFileName(asset.Name);
        var tempPath = Path.Combine(Path.GetTempPath(), fileName);
        var partialPath = tempPath + ".partial";
        ClearDownloadedDirectUpdateState();
        TryDeleteFile(partialPath);
        using var httpClient = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        httpClient.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("carton", CartonApplicationInfo.Version));
        var downloader = new AcceleratedFileDownloader(httpClient, Log);
        try
        {
            await downloader.DownloadFileAsync(
                asset.DownloadUrl,
                partialPath,
                new Progress<FileDownloadProgress>(downloadProgress =>
                {
                    var totalBytes = downloadProgress.TotalBytes > 0 ? downloadProgress.TotalBytes : asset.Size;
                    var percent = totalBytes > 0
                        ? (int)Math.Clamp(downloadProgress.BytesReceived * 100 / totalBytes, 0, 100)
                        : 0;
                    progress?.Report(new AppUpdateDownloadProgress(percent, downloadProgress.BytesReceived, totalBytes));
                }),
                cancellationToken).ConfigureAwait(false);

            ValidateDownloadedDirectUpdateFile(partialPath, asset.Size);
            File.Move(partialPath, tempPath, overwrite: true);
        }
        catch
        {
            TryDeleteFile(partialPath);
            throw;
        }

        _downloadedInstallerPath = tempPath;
        _downloadedInstallerVersion = update.Version;
        PersistPendingDirectUpdateState(DirectUpdateKindInstaller, update.Version, tempPath);
    }

    private async Task DownloadPortableUpdateAsync(
        AppUpdateResult update,
        IProgress<AppUpdateDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var asset = ResolvePreferredPortableAsset(update.ReleaseInfo);
        if (asset == null)
        {
            throw new InvalidOperationException("No Windows portable asset was found for this release.");
        }

        var fileName = Path.GetFileName(asset.Name);
        var tempPath = Path.Combine(Path.GetTempPath(), fileName);
        var partialPath = tempPath + ".partial";
        ClearDownloadedDirectUpdateState();
        TryDeleteFile(partialPath);
        using var httpClient = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        httpClient.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("carton", CartonApplicationInfo.Version));
        var downloader = new AcceleratedFileDownloader(httpClient, Log);
        try
        {
            await downloader.DownloadFileAsync(
                asset.DownloadUrl,
                partialPath,
                new Progress<FileDownloadProgress>(downloadProgress =>
                {
                    var totalBytes = downloadProgress.TotalBytes > 0 ? downloadProgress.TotalBytes : asset.Size;
                    var percent = totalBytes > 0
                        ? (int)Math.Clamp(downloadProgress.BytesReceived * 100 / totalBytes, 0, 100)
                        : 0;
                    progress?.Report(new AppUpdateDownloadProgress(percent, downloadProgress.BytesReceived, totalBytes));
                }),
                cancellationToken).ConfigureAwait(false);

            ValidateDownloadedDirectUpdateFile(partialPath, asset.Size);
            File.Move(partialPath, tempPath, overwrite: true);
        }
        catch
        {
            TryDeleteFile(partialPath);
            throw;
        }

        _downloadedPortableArchivePath = tempPath;
        _downloadedPortableArchiveVersion = update.Version;
        PersistPendingDirectUpdateState(DirectUpdateKindPortable, update.Version, tempPath);
    }

    private void RestorePendingDirectUpdateState()
    {
        var statePath = GetPendingDirectUpdateStatePath();
        if (!File.Exists(statePath))
        {
            return;
        }

        try
        {
            var lines = File.ReadAllLines(statePath);
            if (lines.Length < 3)
            {
                ClearPendingDirectUpdateState();
                return;
            }

            var kind = lines[0].Trim();
            var version = lines[1].Trim();
            var updatePath = lines[2].Trim();
            if (string.IsNullOrWhiteSpace(kind) ||
                string.IsNullOrWhiteSpace(version) ||
                string.IsNullOrWhiteSpace(updatePath) ||
                !File.Exists(updatePath) ||
                !IsPendingDirectUpdateNewerThanCurrent(version))
            {
                ClearPendingDirectUpdateState();
                return;
            }

            if (string.Equals(kind, DirectUpdateKindInstaller, StringComparison.OrdinalIgnoreCase) &&
                SupportsDirectInstallerUpdates)
            {
                _downloadedInstallerPath = updatePath;
                _downloadedInstallerVersion = version;
                return;
            }

            if (string.Equals(kind, DirectUpdateKindPortable, StringComparison.OrdinalIgnoreCase) &&
                SupportsDirectPortableUpdates)
            {
                _downloadedPortableArchivePath = updatePath;
                _downloadedPortableArchiveVersion = version;
                return;
            }

            ClearPendingDirectUpdateState();
        }
        catch (Exception ex)
        {
            Log($"Failed to restore pending direct update state: {ex.Message}");
            ClearPendingDirectUpdateState();
        }
    }

    private void PersistPendingDirectUpdateState(string kind, string version, string updatePath)
    {
        try
        {
            var appDataPath = PathHelper.GetAppDataPath();
            Directory.CreateDirectory(appDataPath);
            File.WriteAllLines(
                GetPendingDirectUpdateStatePath(),
                [kind, version, updatePath]);
        }
        catch (Exception ex)
        {
            Log($"Failed to persist pending direct update state: {ex.Message}");
        }
    }

    private void ClearDownloadedDirectUpdateState()
    {
        _downloadedInstallerPath = null;
        _downloadedInstallerVersion = null;
        _downloadedPortableArchivePath = null;
        _downloadedPortableArchiveVersion = null;
        ClearPendingDirectUpdateState();
    }

    private static void ValidateDownloadedDirectUpdateFile(string path, long expectedSize)
    {
        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException("Downloaded update file was not found.", path);
        }

        if (fileInfo.Length <= 0)
        {
            throw new IOException($"Downloaded update file is empty: {path}");
        }

        if (expectedSize > 0 && fileInfo.Length != expectedSize)
        {
            throw new IOException(
                $"Downloaded update file size mismatch: expected {expectedSize} bytes, got {fileInfo.Length} bytes.");
        }
    }

    private void ClearPendingDirectUpdateState()
    {
        try
        {
            var statePath = GetPendingDirectUpdateStatePath();
            if (File.Exists(statePath))
            {
                File.Delete(statePath);
            }
        }
        catch (Exception ex)
        {
            Log($"Failed to clear pending direct update state: {ex.Message}");
        }
    }

    private static string GetPendingDirectUpdateStatePath()
        => Path.Combine(PathHelper.GetAppDataPath(), PendingDirectUpdateStateFileName);

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Log($"Failed to delete file '{path}': {ex.Message}");
        }
    }

    private static void StartPortableUpdater(string archivePath)
    {
        var appDirectory = AppDomain.CurrentDomain.BaseDirectory;
        var updaterName = GetPortableHelperExecutableName();
        var updaterPath = Path.Combine(appDirectory, updaterName);
        if (!File.Exists(updaterPath))
        {
            throw new FileNotFoundException("Portable updater executable was not found.", updaterPath);
        }

        var tempUpdaterDirectory = Path.Combine(
            Path.GetTempPath(),
            "carton-helper-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempUpdaterDirectory);

        var tempUpdaterPath = Path.Combine(tempUpdaterDirectory, updaterName);
        File.Copy(updaterPath, tempUpdaterPath, overwrite: true);

        var restartExecutable = Path.GetFileName(Environment.ProcessPath);
        if (string.IsNullOrWhiteSpace(restartExecutable))
        {
            restartExecutable = GetDefaultMainExecutableName();
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = tempUpdaterPath,
            WorkingDirectory = tempUpdaterDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--carton-update");
        startInfo.ArgumentList.Add("--pid");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--archive");
        startInfo.ArgumentList.Add(archivePath);
        startInfo.ArgumentList.Add("--target");
        startInfo.ArgumentList.Add(appDirectory);
        startInfo.ArgumentList.Add("--restart");
        startInfo.ArgumentList.Add(restartExecutable);

        Process.Start(startInfo);
    }

    private static GitHubAssetInfo? ResolvePreferredInstallerAsset(GitHubReleaseInfo release)
    {
        if (release.Assets.Count == 0)
        {
            return null;
        }

        var preferArm64 = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

        return release.Assets
            .Where(asset => asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                            asset.Name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(asset => asset.Name.Contains("InnoSetup", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(asset => asset.Name.Contains("Setup", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(asset =>
                preferArm64
                    ? asset.Name.Contains("win-arm64", StringComparison.OrdinalIgnoreCase)
                    : asset.Name.Contains("win-x64", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(asset =>
                preferArm64
                    ? asset.Name.Contains("arm64", StringComparison.OrdinalIgnoreCase)
                    : asset.Name.Contains("x64", StringComparison.OrdinalIgnoreCase))
            .ThenBy(asset => asset.Name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
    }

    private static GitHubAssetInfo? ResolvePreferredPortableAsset(GitHubReleaseInfo release)
    {
        if (release.Assets.Count == 0)
        {
            return null;
        }

        var preferArm64 = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        var expectedExtension = OperatingSystem.IsLinux() ? ".tar.gz" : ".zip";
        var ridPrefix = GetPlatformRidPrefix();

        return release.Assets
            .Where(asset => asset.Name.EndsWith(expectedExtension, StringComparison.OrdinalIgnoreCase) &&
                            asset.Name.Contains("portable", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(asset => asset.Name.Contains(ridPrefix, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(asset =>
                OperatingSystem.IsLinux()
                    ? asset.Name.Contains("linux", StringComparison.OrdinalIgnoreCase)
                    : asset.Name.Contains("win", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(asset =>
                preferArm64
                    ? asset.Name.Contains("arm64", StringComparison.OrdinalIgnoreCase)
                    : asset.Name.Contains("x64", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
    }
}
