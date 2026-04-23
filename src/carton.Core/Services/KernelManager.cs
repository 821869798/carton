using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using carton.Core.Models;
using carton.Core.Utilities;

namespace carton.Core.Services;

public interface IKernelManager
{
    event EventHandler<DownloadProgress>? DownloadProgressChanged;
    event EventHandler<string>? StatusChanged;
    event EventHandler<KernelInfo?>? InstalledKernelChanged;

    KernelInfo? InstalledKernel { get; }
    bool IsKernelInstalled { get; }
    string KernelPath { get; }

    Task<KernelInfo?> GetInstalledKernelInfoAsync();
    Task<string?> GetLatestVersionAsync(DownloadMirror mirror = DownloadMirror.GitHub);
    Task<KernelPackageDownloadResult?> DownloadPackageAsync(string? version = null, DownloadMirror mirror = DownloadMirror.GitHub);
    Task<bool> InstallPackageAsync(KernelPackageDownloadResult package);
    Task<bool> DownloadAndInstallAsync(string? version = null, DownloadMirror mirror = DownloadMirror.GitHub);
    Task<bool> InstallCustomKernelAsync(string sourcePath);
    Task<bool> UninstallAsync();
    Task<bool> CheckKernelAsync();
}

public class DownloadProgress
{
    public long BytesReceived { get; set; }
    public long TotalBytes { get; set; }
    public double Progress => TotalBytes > 0 ? (double)BytesReceived / TotalBytes * 100 : 0;
    public string Status { get; set; } = string.Empty;
}

public sealed class KernelPackageDownloadResult
{
    public string TempFilePath { get; init; } = string.Empty;
    public string VersionLabel { get; init; } = string.Empty;
    public KernelInstallChannel SourceChannel { get; init; } = KernelInstallChannel.Official;
}

public class KernelManager : IKernelManager
{
    private const string WindowsNaiveProxyRuntimeDll = "libcronet.dll";
    private readonly string _binDirectory;
    private readonly string _kernelPath;
    private readonly HttpClient _httpClient = HttpClientFactory.External;
    private KernelInfo? _installedKernel;

    public event EventHandler<DownloadProgress>? DownloadProgressChanged;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<KernelInfo?>? InstalledKernelChanged;

    public KernelInfo? InstalledKernel => _installedKernel;
    public bool IsKernelInstalled => File.Exists(_kernelPath);
    public string KernelPath => _kernelPath;

    private static readonly TimeSpan GitHubLookupTimeout = TimeSpan.FromSeconds(6);
    private const string GitHubDownloadUrl = "https://github.com/SagerNet/sing-box/releases/download";
    private const string GitHubReleasesPageUrl = "https://github.com/SagerNet/sing-box/releases";
    private const string GitHubReleasesAtomUrl = "https://github.com/SagerNet/sing-box/releases.atom";

    private const string Ref1ndDownloadUrl = "https://github.com/reF1nd/sing-box-releases/releases/download";
    private const string Ref1ndReleasesPageUrl = "https://github.com/reF1nd/sing-box-releases/releases";
    private const string Ref1ndReleasesAtomUrl = "https://github.com/reF1nd/sing-box-releases/releases.atom";

    public KernelManager(string baseDirectory)
    {
        _binDirectory = Path.Combine(baseDirectory, "bin");
        var platform = PlatformInfo.Current;
        var fileName = $"sing-box{platform.Suffix}";
        _kernelPath = Path.Combine(_binDirectory, fileName);

        Directory.CreateDirectory(_binDirectory);
    }



    public async Task<KernelInfo?> GetInstalledKernelInfoAsync()
    {
        if (!File.Exists(_kernelPath))
        {
            return SetInstalledKernel(null, null);
        }

        try
        {
            var version = await GetInstalledVersionAsync();
            var kernelInfo = new KernelInfo
            {
                KernelVersion = CartonApplicationInfo.FormatSingBoxVersion(version),
                Path = _kernelPath,
                InstallTime = File.GetCreationTime(_kernelPath),
                Platform = PlatformInfo.Current
            };

            return SetInstalledKernel(kernelInfo, version);
        }
        catch
        {
            return null;
        }
    }

    private KernelInfo? SetInstalledKernel(KernelInfo? kernelInfo, string? version)
    {
        _installedKernel = kernelInfo;
        CartonApplicationInfo.SetSingBoxVersion(version);
        InstalledKernelChanged?.Invoke(this, kernelInfo);
        return kernelInfo;
    }

    private string GetKernelWorkingDirectory()
        => Path.GetDirectoryName(_kernelPath) ?? _binDirectory;

    private void ApplyLinuxLibrarySearchPath(ProcessStartInfo startInfo)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return;
        }

        var current = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
        startInfo.Environment["LD_LIBRARY_PATH"] = string.IsNullOrWhiteSpace(current)
            ? _binDirectory
            : $"{_binDirectory}:{current}";
    }

    private async Task<string?> GetInstalledVersionAsync()
    {
        if (!File.Exists(_kernelPath)) return null;

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _kernelPath,
                    Arguments = "version",
                    WorkingDirectory = GetKernelWorkingDirectory(),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            ApplyLinuxLibrarySearchPath(process.StartInfo);

            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await Task.WhenAll(stdoutTask, stderrTask, process.WaitForExitAsync());

            return ParseInstalledVersion(stdoutTask.Result, stderrTask.Result);
        }
        catch
        {
        }

        return null;
    }

    private static string? ParseInstalledVersion(params string?[] outputs)
    {
        foreach (var output in outputs)
        {
            foreach (var line in SplitLines(output))
            {
                var version = TryExtractVersion(line, requireSingBoxPrefix: true);
                if (!string.IsNullOrWhiteSpace(version))
                {
                    return version;
                }
            }
        }

        foreach (var output in outputs)
        {
            foreach (var line in SplitLines(output))
            {
                var version = TryExtractVersion(line, requireSingBoxPrefix: false);
                if (!string.IsNullOrWhiteSpace(version))
                {
                    return version;
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> SplitLines(string? output) =>
        string.IsNullOrWhiteSpace(output)
            ? []
            : output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? TryExtractVersion(string line, bool requireSingBoxPrefix)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        if (requireSingBoxPrefix && !line.Contains("sing-box", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var prefixedMatch = Regex.Match(
            line,
            @"sing-box(?:\s+version)?\s+([^\s\(\[]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (prefixedMatch.Success)
        {
            return prefixedMatch.Groups[1].Value.Trim();
        }

        if (!requireSingBoxPrefix)
        {
            var fallbackMatch = Regex.Match(
                line,
                @"\bv?(?:\d+\.){1,}\d+(?:[-+][^\s\)\]]+)?\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (fallbackMatch.Success)
            {
                return fallbackMatch.Value.Trim();
            }
        }

        return null;
    }

    public async Task<string?> GetLatestVersionAsync(DownloadMirror mirror = DownloadMirror.GitHub)
    {
        try
        {
            if (mirror is DownloadMirror.Ref1ndStable or DownloadMirror.Ref1ndTest)
            {
                return await GetLatestRef1ndVersionAsync(mirror);
            }

            var wantPrerelease = IsOfficialPreReleaseMirror(mirror);
            if (wantPrerelease)
            {
                return await GetLatestOfficialPreReleaseVersionAsync();
            }

            return await GetLatestOfficialReleaseVersionAsync();
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> DownloadAndInstallAsync(string? version = null, DownloadMirror mirror = DownloadMirror.GitHub)
    {
        var package = await DownloadPackageAsync(version, mirror);
        if (package == null)
        {
            return false;
        }

        return await InstallPackageAsync(package);
    }

    private async Task<bool> DownloadAndInstallFromRef1ndAsync(DownloadMirror mirror)
    {
        var package = await DownloadPackageAsync(null, mirror);
        if (package == null)
        {
            return false;
        }

        return await InstallPackageAsync(package);
    }

    public async Task<KernelPackageDownloadResult?> DownloadPackageAsync(string? version = null, DownloadMirror mirror = DownloadMirror.GitHub)
    {
        try
        {
            if (mirror is DownloadMirror.Ref1ndStable or DownloadMirror.Ref1ndTest)
            {
                var platform = PlatformInfo.Current;
                var ref1ndTag = await GetLatestRef1ndVersionAsync(mirror);
                if (string.IsNullOrWhiteSpace(ref1ndTag))
                {
                    StatusChanged?.Invoke(this, "Failed to get latest version");
                    return null;
                }

                var channelLabel = GetRef1ndChannelLabel(mirror);
                var tempFile = await DownloadRef1ndPackageAsync(platform, ref1ndTag, mirror, channelLabel);
                if (string.IsNullOrWhiteSpace(tempFile))
                {
                    StatusChanged?.Invoke(this, $"ref1nd channel does not support platform: {platform.OS}-{platform.Arch}");
                    return null;
                }

                StatusChanged?.Invoke(this, $"Downloaded ref1nd sing-box {ref1ndTag} ({channelLabel})");

                return new KernelPackageDownloadResult
                {
                    TempFilePath = tempFile,
                    VersionLabel = ref1ndTag,
                    SourceChannel = mirror == DownloadMirror.Ref1ndTest
                        ? KernelInstallChannel.Ref1ndTest
                        : KernelInstallChannel.Ref1ndStable
                };
            }

            version ??= await GetLatestVersionAsync(mirror);
            if (string.IsNullOrEmpty(version))
            {
                StatusChanged?.Invoke(this, "Failed to get latest version");
                return null;
            }

            StatusChanged?.Invoke(this, $"Downloading sing-box {version}...");

            var currentPlatform = PlatformInfo.Current;
            var assetName = $"sing-box-{version.TrimStart('v')}-{currentPlatform.OS}-{currentPlatform.Arch}";
            var archiveExt = currentPlatform.OS == "windows" ? ".zip" : ".tar.gz";
            var originalUrl = $"{GitHubDownloadUrl}/{version}/{assetName}{archiveExt}";

            var downloadUrlForMirror = mirror switch
            {
                DownloadMirror.GhProxy or DownloadMirror.GhProxyPreRelease => $"https://gh-proxy.com/{originalUrl}",
                _ => originalUrl
            };

            var archiveFile = Path.Combine(Path.GetTempPath(), $"sing-box-{Guid.NewGuid():N}{archiveExt}");
            await DownloadFileAsync(downloadUrlForMirror, archiveFile);
            StatusChanged?.Invoke(this, $"Downloaded sing-box {version}");

            return new KernelPackageDownloadResult
            {
                TempFilePath = archiveFile,
                VersionLabel = version,
                SourceChannel = IsOfficialPreReleaseMirror(mirror)
                    ? KernelInstallChannel.OfficialPreRelease
                    : KernelInstallChannel.Official
            };
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, $"Failed to download: {ex.Message}");
            return null;
        }
    }

    public async Task<bool> InstallPackageAsync(KernelPackageDownloadResult package)
    {
        try
        {
            if (package == null || string.IsNullOrWhiteSpace(package.TempFilePath) || !File.Exists(package.TempFilePath))
            {
                StatusChanged?.Invoke(this, "Downloaded package not found");
                return false;
            }

            var platform = PlatformInfo.Current;
            var versionLabel = string.IsNullOrWhiteSpace(package.VersionLabel) ? "package" : package.VersionLabel;
            var isDirectExecutable = platform.OS == "windows" &&
                                     string.Equals(Path.GetExtension(package.TempFilePath), ".exe", StringComparison.OrdinalIgnoreCase);

            if (isDirectExecutable)
            {
                StatusChanged?.Invoke(this, "Installing...");
                await KillRunningKernelAsync();
                CleanupWindowsRuntimeSidecars();
                var destination = Path.Combine(_binDirectory, "sing-box.exe");
                File.Move(package.TempFilePath, destination, overwrite: true);
            }
            else
            {
                StatusChanged?.Invoke(this, "Extracting...");
                await ExtractArchiveAsync(package.TempFilePath, _binDirectory);
                TryDeleteFile(package.TempFilePath);

                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    var chmodPath = Path.Combine(_binDirectory, "sing-box");
                    if (File.Exists(chmodPath))
                    {
                        Process.Start("chmod", $"+x \"{chmodPath}\"")?.WaitForExit();
                    }
                }
            }

            await GetInstalledKernelInfoAsync();
            StatusChanged?.Invoke(this, $"Successfully installed sing-box {versionLabel}");
            return true;
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, $"Failed to install: {ex.Message}");
            return false;
        }
    }

    private static string GetRef1ndChannelLabel(DownloadMirror mirror)
        => mirror == DownloadMirror.Ref1ndTest ? "test" : "stable";

    private static bool IsOfficialPreReleaseMirror(DownloadMirror mirror)
        => mirror is DownloadMirror.GitHubPreRelease or DownloadMirror.GhProxyPreRelease;

    private async Task<string?> GetLatestOfficialReleaseVersionAsync()
        => await GetLatestVersionFromReleaseFeedAsync(
            GitHubReleasesAtomUrl,
            GitHubReleasesPageUrl,
            wantPrerelease: false);

    private async Task<string?> GetLatestOfficialPreReleaseVersionAsync()
        => await GetLatestVersionFromReleaseFeedAsync(
            GitHubReleasesAtomUrl,
            GitHubReleasesPageUrl,
            wantPrerelease: true);

    private async Task<string?> GetLatestRef1ndVersionAsync(DownloadMirror mirror)
    {
        var wantPrerelease = mirror == DownloadMirror.Ref1ndTest;

        return await GetLatestVersionFromReleaseFeedAsync(
            Ref1ndReleasesAtomUrl,
            Ref1ndReleasesPageUrl,
            wantPrerelease);
    }

    private async Task<string?> DownloadRef1ndPackageAsync(PlatformInfo platform, string tagName, DownloadMirror mirror, string channelLabel)
    {
        foreach (var candidate in GetRef1ndAssetCandidates(platform, tagName))
        {
            var tempExt = GetPackageExtension(candidate);
            var tempFile = Path.Combine(Path.GetTempPath(), $"sing-box-ref1nd-{Guid.NewGuid():N}{tempExt}");
            var downloadUrl = $"{Ref1ndDownloadUrl}/{tagName}/{candidate}";

            try
            {
                StatusChanged?.Invoke(this, $"Downloading ref1nd sing-box {tagName} ({channelLabel})...");
                await DownloadFileAsync(downloadUrl, tempFile);
                return tempFile;
            }
            catch
            {
                TryDeleteFile(tempFile);
            }
        }

        return null;
    }

    private static string[] GetRef1ndAssetCandidates(PlatformInfo platform, string tagName)
    {
        var preferV3 = SupportsX64V3();
        var preferredLinuxLibc = IsLikelyMuslLinux() ? "musl" : "glibc";
        var alternateLinuxLibc = preferredLinuxLibc == "glibc" ? "musl" : "glibc";
        var version = tagName.TrimStart('v');
        return (platform.OS, platform.Arch) switch
        {
            ("windows", "amd64") => preferV3
                ? [$"sing-box-{version}-windows-amd64v3.zip", $"sing-box-{version}-windows-amd64.zip"]
                : [$"sing-box-{version}-windows-amd64.zip", $"sing-box-{version}-windows-amd64v3.zip"],
            ("windows", "arm64") => [$"sing-box-{version}-windows-arm64.zip"],
            ("linux", "amd64") => BuildLinuxAssetCandidates(version, preferV3 ? "amd64v3" : "amd64", preferV3 ? "amd64" : "amd64v3", preferredLinuxLibc, alternateLinuxLibc),
            ("linux", "arm64") => BuildLinuxAssetCandidates(version, "arm64", null, preferredLinuxLibc, alternateLinuxLibc),
            ("darwin", "amd64") => preferV3
                ? [$"sing-box-{version}-darwin-amd64v3.tar.gz", $"sing-box-{version}-darwin-amd64.tar.gz"]
                : [$"sing-box-{version}-darwin-amd64.tar.gz", $"sing-box-{version}-darwin-amd64v3.tar.gz"],
            ("darwin", "arm64") => [$"sing-box-{version}-darwin-arm64.tar.gz"],
            _ => []
        };
    }

    private static string[] BuildLinuxAssetCandidates(string version, string primaryArch, string? fallbackArch, string preferredLibc, string alternateLibc)
    {
        var candidates = new List<string>
        {
            $"sing-box-{version}-linux-{primaryArch}-{preferredLibc}.tar.gz",
            $"sing-box-{version}-linux-{primaryArch}-purego.tar.gz",
            $"sing-box-{version}-linux-{primaryArch}-{alternateLibc}.tar.gz"
        };

        if (!string.IsNullOrWhiteSpace(fallbackArch))
        {
            candidates.Add($"sing-box-{version}-linux-{fallbackArch}-{preferredLibc}.tar.gz");
            candidates.Add($"sing-box-{version}-linux-{fallbackArch}-purego.tar.gz");
            candidates.Add($"sing-box-{version}-linux-{fallbackArch}-{alternateLibc}.tar.gz");
        }

        return candidates.ToArray();
    }

    private static bool IsLikelyMuslLinux()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return false;
        }

        return File.Exists("/etc/alpine-release") ||
               File.Exists("/lib/ld-musl-x86_64.so.1") ||
               File.Exists("/lib/ld-musl-aarch64.so.1");
    }

    private static bool SupportsX64V3()
        => Avx2.IsSupported && Bmi1.IsSupported && Bmi2.IsSupported && Fma.IsSupported && Lzcnt.IsSupported;

    private static string GetPackageExtension(string assetName)
    {
        if (assetName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            return ".tar.gz";
        }

        return Path.GetExtension(assetName);
    }

    private async Task<string?> GetLatestVersionFromReleasesPageAsync(string releasesPageUrl, bool wantPrerelease)
    {
        var html = await GetStringWithGitHubLookupTimeoutAsync(releasesPageUrl);
        var matches = Regex.Matches(
            html,
            @"/releases/tag/(?<tag>[^""#?&/]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (matches.Count == 0)
        {
            return null;
        }

        var seenTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in matches)
        {
            var tag = Uri.UnescapeDataString(match.Groups["tag"].Value);
            if (string.IsNullOrWhiteSpace(tag) || !seenTags.Add(tag))
            {
                continue;
            }

            if (wantPrerelease == IsLikelyPrereleaseTag(tag))
            {
                return tag;
            }
        }

        return null;
    }

    private async Task<string?> GetLatestVersionFromReleaseFeedAsync(
        string atomFeedUrl,
        string releasesPageUrl,
        bool wantPrerelease)
    {
        try
        {
            var atom = await GetStringWithGitHubLookupTimeoutAsync(atomFeedUrl);
            foreach (var release in ParseReleaseAtomFeed(atom))
            {
                if (string.IsNullOrWhiteSpace(release.Tag))
                {
                    continue;
                }

                if (MatchesPrereleasePreference(release, wantPrerelease))
                {
                    return release.Tag;
                }
            }
        }
        catch
        {
        }

        return await GetLatestVersionFromReleasesPageAsync(releasesPageUrl, wantPrerelease);
    }

    private static IEnumerable<GitHubAtomRelease> ParseReleaseAtomFeed(string atom)
    {
        var document = XDocument.Parse(atom);
        XNamespace atomNamespace = "http://www.w3.org/2005/Atom";

        foreach (var entry in document.Descendants(atomNamespace + "entry"))
        {
            var tag = TryExtractReleaseTag(entry.Element(atomNamespace + "id")?.Value);
            foreach (var link in entry.Elements(atomNamespace + "link"))
            {
                var href = link.Attribute("href")?.Value;
                tag ??= TryExtractReleaseTag(href);
            }

            if (string.IsNullOrWhiteSpace(tag))
            {
                continue;
            }

            yield return new GitHubAtomRelease(
                tag,
                entry.Element(atomNamespace + "title")?.Value ?? string.Empty);
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
            @"/releases/tag/(?<tag>[^#?&/]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success
            ? Uri.UnescapeDataString(match.Groups["tag"].Value)
            : null;
    }

    private static bool MatchesPrereleasePreference(GitHubAtomRelease release, bool wantPrerelease)
    {
        var isPrerelease = IsLikelyPrereleaseTag(release.Tag) || IsLikelyPrereleaseTag(release.Title);
        return wantPrerelease == isPrerelease;
    }

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

    private async Task<HttpResponseMessage> SendWithGitHubLookupTimeoutAsync(HttpRequestMessage request)
    {
        using var timeoutCts = new CancellationTokenSource(GitHubLookupTimeout);
        return await _httpClient.SendAsync(request, timeoutCts.Token);
    }

    private async Task<string> GetStringWithGitHubLookupTimeoutAsync(string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await SendWithGitHubLookupTimeoutAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>
    /// Kills any running sing-box processes that match our managed binary path.
    /// </summary>
    private async Task KillRunningKernelAsync()
    {
        try
        {
            var targetExe = _kernelPath;
            var processes = Process.GetProcessesByName("sing-box");
            foreach (var p in processes)
            {
                try
                {
                    if (string.Equals(p.MainModule?.FileName, targetExe, StringComparison.OrdinalIgnoreCase))
                    {
                        p.Kill();
                        await p.WaitForExitAsync();
                    }
                }
                catch { }
            }

            if (File.Exists(targetExe))
                File.Delete(targetExe);
        }
        catch { }
    }

    private async Task ExtractArchiveAsync(string archivePath, string destination)
    {
        var platform = PlatformInfo.Current;

        try
        {
            var targetExe = Path.Combine(destination, platform.OS == "windows" ? "sing-box.exe" : "sing-box");
            var processes = Process.GetProcessesByName("sing-box");
            foreach (var p in processes)
            {
                try
                {
                    if (string.Equals(p.MainModule?.FileName, targetExe, StringComparison.OrdinalIgnoreCase))
                    {
                        p.Kill();
                        await p.WaitForExitAsync();
                    }
                }
                catch { } // Ignore access denied
            }

            // Also try to rename/delete existing if locked (Windows allows renaming running exe)
            if (File.Exists(targetExe))
            {
                File.Delete(targetExe);
            }
        }
        catch { }

        if (platform.OS == "windows")
        {
            using var archive = ZipFile.OpenRead(archivePath);
            var singBoxEntry = archive.Entries.FirstOrDefault(entry =>
                !string.IsNullOrWhiteSpace(entry.Name) &&
                entry.FullName.EndsWith("sing-box.exe", StringComparison.OrdinalIgnoreCase));

            if (singBoxEntry == null)
            {
                throw new FileNotFoundException("sing-box.exe was not found in the archive.");
            }

            var runtimeDirectory = GetArchiveEntryDirectory(singBoxEntry.FullName);
            var runtimeEntries = archive.Entries.Where(entry =>
                    !string.IsNullOrWhiteSpace(entry.Name) &&
                    string.Equals(GetArchiveEntryDirectory(entry.FullName), runtimeDirectory, StringComparison.OrdinalIgnoreCase) &&
                    IsWindowsRuntimeCompanion(entry.Name))
                .ToList();

            if (runtimeEntries.Count == 0)
            {
                throw new FileNotFoundException("No Windows runtime files were found in the archive.");
            }

            CleanupWindowsRuntimeSidecars();

            foreach (var entry in runtimeEntries)
            {
                entry.ExtractToFile(Path.Combine(destination, entry.Name), true);
            }
        }
        else
        {
            var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "tar",
                    Arguments = $"-xzf \"{archivePath}\" -C \"{tempDir}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            await process.WaitForExitAsync();

            var singBoxFile = Directory.GetFiles(tempDir, "sing-box", SearchOption.AllDirectories).FirstOrDefault();
            if (singBoxFile != null)
            {
                var sourceDirectory = Path.GetDirectoryName(singBoxFile);
                File.Copy(singBoxFile, Path.Combine(destination, "sing-box"), true);

                if (!string.IsNullOrWhiteSpace(sourceDirectory))
                {
                    foreach (var soFile in Directory.EnumerateFiles(sourceDirectory, "*.so*", SearchOption.TopDirectoryOnly))
                    {
                        File.Copy(soFile, Path.Combine(destination, Path.GetFileName(soFile)), true);
                    }
                }
            }

            Directory.Delete(tempDir, true);
        }
    }

    private async Task DownloadFileAsync(string downloadUrl, string tempFile)
    {
        using var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? 0;
        var buffer = new byte[8192];
        var bytesRead = 0L;

        await using var contentStream = await response.Content.ReadAsStreamAsync();
        await using var fileStream = File.Create(tempFile);

        int read;
        while ((read = await contentStream.ReadAsync(buffer)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read));
            bytesRead += read;

            DownloadProgressChanged?.Invoke(this, new DownloadProgress
            {
                BytesReceived = bytesRead,
                TotalBytes = totalBytes,
                Status = "Downloading..."
            });
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private void CleanupWindowsRuntimeSidecars()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        TryDeleteFile(Path.Combine(_binDirectory, "sing-box.exe"));

        foreach (var dllPath in Directory.EnumerateFiles(_binDirectory, "*.dll", SearchOption.TopDirectoryOnly))
        {
            TryDeleteFile(dllPath);
        }
    }

    private static string GetArchiveEntryDirectory(string fullName)
    {
        var normalized = fullName.Replace('\\', '/').Trim('/');
        var lastSlash = normalized.LastIndexOf('/');
        return lastSlash >= 0 ? normalized[..lastSlash] : string.Empty;
    }

    private static bool IsWindowsRuntimeCompanion(string fileName)
        => fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
           fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

    private sealed record GitHubAtomRelease(string Tag, string Title);

    public async Task<bool> InstallCustomKernelAsync(string sourcePath)
    {
        try
        {
            if (!File.Exists(sourcePath))
            {
                StatusChanged?.Invoke(this, "Selected file does not exist.");
                return false;
            }

            StatusChanged?.Invoke(this, "Installing custom kernel...");

            var platform = PlatformInfo.Current;
            var targetExe = _kernelPath;
            var targetDirectory = Path.GetDirectoryName(targetExe) ?? _binDirectory;
            var sourceDirectory = Path.GetDirectoryName(sourcePath);
            var sourceNaiveProxyRuntime = string.IsNullOrWhiteSpace(sourceDirectory)
                ? null
                : Path.Combine(sourceDirectory, WindowsNaiveProxyRuntimeDll);
            var targetNaiveProxyRuntime = Path.Combine(targetDirectory, WindowsNaiveProxyRuntimeDll);

            // Kill running processes if any
            try
            {
                var processes = Process.GetProcessesByName("sing-box");
                foreach (var p in processes)
                {
                    if (string.Equals(p.MainModule?.FileName, targetExe, StringComparison.OrdinalIgnoreCase))
                    {
                        p.Kill();
                        await p.WaitForExitAsync();
                    }
                }

                if (File.Exists(targetExe))
                {
                    File.Delete(targetExe);
                }
            }
            catch { }

            File.Copy(sourcePath, targetExe, true);

            if (OperatingSystem.IsWindows())
            {
                if (!string.IsNullOrWhiteSpace(sourceNaiveProxyRuntime) && File.Exists(sourceNaiveProxyRuntime))
                {
                    TryDeleteFile(targetNaiveProxyRuntime);
                    File.Copy(sourceNaiveProxyRuntime, targetNaiveProxyRuntime, true);
                }
            }

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start("chmod", $"+x \"{targetExe}\"")?.WaitForExit();
            }

            await GetInstalledKernelInfoAsync();
            StatusChanged?.Invoke(this, "Successfully installed custom kernel");

            return true;
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, $"Failed to install custom kernel: {ex.Message}");
            return false;
        }
    }

    public Task<bool> UninstallAsync()
    {
        try
        {
            if (File.Exists(_kernelPath))
            {
                File.Delete(_kernelPath);
            }

            SetInstalledKernel(null, null);
            StatusChanged?.Invoke(this, "Kernel uninstalled");
            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public async Task<bool> CheckKernelAsync()
    {
        if (!File.Exists(_kernelPath))
        {
            return false;
        }

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _kernelPath,
                    Arguments = "version",
                    WorkingDirectory = GetKernelWorkingDirectory(),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };
            ApplyLinuxLibrarySearchPath(process.StartInfo);

            process.Start();
            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
