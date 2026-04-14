using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using carton.Core.Models;

namespace carton.Core.Services;

public interface IKernelManager
{
    event EventHandler<DownloadProgress>? DownloadProgressChanged;
    event EventHandler<string>? StatusChanged;

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
    private readonly string _binDirectory;
    private readonly string _kernelPath;
    private readonly HttpClient _httpClient = HttpClientFactory.External;
    private KernelInfo? _installedKernel;

    public event EventHandler<DownloadProgress>? DownloadProgressChanged;
    public event EventHandler<string>? StatusChanged;

    public KernelInfo? InstalledKernel => _installedKernel;
    public bool IsKernelInstalled => File.Exists(_kernelPath);
    public string KernelPath => _kernelPath;

    private const string GitHubApiUrl = "https://api.github.com/repos/SagerNet/sing-box/releases/latest";
    private const string GitHubDownloadUrl = "https://github.com/SagerNet/sing-box/releases/download";
    private const string GitHubReleasesApiUrl = "https://api.github.com/repos/SagerNet/sing-box/releases?per_page=20";

    private const string Ref1ndReleasesApiUrl = "https://api.github.com/repos/reF1nd/sing-box-releases/releases?per_page=20";

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
            _installedKernel = null;
            return null;
        }

        try
        {
            var version = await GetInstalledVersionAsync();
            _installedKernel = new KernelInfo
            {
                KernelVersion = version ?? "unknown",
                Path = _kernelPath,
                InstallTime = File.GetCreationTime(_kernelPath),
                Platform = PlatformInfo.Current
            };
            return _installedKernel;
        }
        catch
        {
            return null;
        }
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
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

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
                var releaseAsset = await GetRef1ndReleaseAssetAsync(platform, mirror);
                if (releaseAsset == null)
                {
                    StatusChanged?.Invoke(this, $"ref1nd channel does not support platform: {platform.OS}-{platform.Arch}");
                    return null;
                }

                var channelLabel = GetRef1ndChannelLabel(mirror);
                StatusChanged?.Invoke(this, $"Downloading ref1nd sing-box {releaseAsset.TagName} ({channelLabel})...");

                var tempExt = GetPackageExtension(releaseAsset.AssetName);
                var tempFile = Path.Combine(Path.GetTempPath(), $"sing-box-ref1nd-{Guid.NewGuid():N}{tempExt}");
                await DownloadFileAsync(releaseAsset.DownloadUrl, tempFile);
                StatusChanged?.Invoke(this, $"Downloaded ref1nd sing-box {releaseAsset.TagName} ({channelLabel})");

                return new KernelPackageDownloadResult
                {
                    TempFilePath = tempFile,
                    VersionLabel = releaseAsset.TagName,
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
    {
        try
        {
            var response = await _httpClient.GetStringAsync(GitHubApiUrl);
            using var document = JsonDocument.Parse(response);
            if (document.RootElement.TryGetProperty("tag_name", out var tagElement) &&
                tagElement.ValueKind == JsonValueKind.String)
            {
                return tagElement.GetString();
            }
        }
        catch
        {
            // Fallback: follow the /releases/latest redirect to get the tag.
            using var request = new HttpRequestMessage(HttpMethod.Head, "https://github.com/SagerNet/sing-box/releases/latest");
            using var response = await _httpClient.SendAsync(request);
            var finalUrl = response.RequestMessage?.RequestUri?.ToString();

            if (finalUrl != null && finalUrl.Contains("/releases/tag/"))
            {
                return finalUrl[(finalUrl.LastIndexOf('/') + 1)..];
            }
        }

        return null;
    }

    private async Task<string?> GetLatestOfficialPreReleaseVersionAsync()
    {
        var releases = await GetGitHubReleaseListAsync(GitHubReleasesApiUrl);
        return releases.FirstOrDefault(item => item.Prerelease)?.TagName;
    }

    private async Task<string?> GetLatestRef1ndVersionAsync(DownloadMirror mirror)
    {
        var releases = await GetGitHubReleaseListAsync(Ref1ndReleasesApiUrl);
        var wantPrerelease = mirror == DownloadMirror.Ref1ndTest;
        return releases.FirstOrDefault(item => item.Prerelease == wantPrerelease)?.TagName;
    }

    private async Task<List<GitHubReleaseInfo>> GetGitHubReleaseListAsync(string apiUrl)
    {
        var response = await _httpClient.GetStringAsync(apiUrl);
        using var document = JsonDocument.Parse(response);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var releases = new List<GitHubReleaseInfo>();
        foreach (var releaseElement in document.RootElement.EnumerateArray())
        {
            if (releaseElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var release = new GitHubReleaseInfo
            {
                TagName = TryGetStringProperty(releaseElement, "tag_name"),
                Prerelease = TryGetBooleanProperty(releaseElement, "prerelease")
            };

            if (releaseElement.TryGetProperty("assets", out var assetsElement) &&
                assetsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var assetElement in assetsElement.EnumerateArray())
                {
                    if (assetElement.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    release.Assets.Add(new GitHubReleaseAssetInfo
                    {
                        Name = TryGetStringProperty(assetElement, "name"),
                        BrowserDownloadUrl = TryGetStringProperty(assetElement, "browser_download_url")
                    });
                }
            }

            releases.Add(release);
        }

        return releases;
    }

    private static string TryGetStringProperty(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;

    private static bool TryGetBooleanProperty(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) &&
           property.ValueKind is JsonValueKind.True or JsonValueKind.False &&
           property.GetBoolean();

    private async Task<Ref1ndReleaseAssetInfo?> GetRef1ndReleaseAssetAsync(PlatformInfo platform, DownloadMirror mirror)
    {
        var releases = await GetGitHubReleaseListAsync(Ref1ndReleasesApiUrl);
        if (releases.Count == 0)
        {
            return null;
        }

        var wantPrerelease = mirror == DownloadMirror.Ref1ndTest;
        var release = releases.FirstOrDefault(item => item.Prerelease == wantPrerelease);
        if (release == null || string.IsNullOrWhiteSpace(release.TagName))
        {
            return null;
        }

        var version = release.TagName.TrimStart('v');
        foreach (var candidate in GetRef1ndAssetCandidates(platform, version))
        {
            var asset = release.Assets.FirstOrDefault(item =>
                string.Equals(item.Name, candidate, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(item.BrowserDownloadUrl));

            if (asset != null)
            {
                return new Ref1ndReleaseAssetInfo(release.TagName, asset.Name, asset.BrowserDownloadUrl);
            }
        }

        return null;
    }

    private static string[] GetRef1ndAssetCandidates(PlatformInfo platform, string version)
    {
        var preferV3 = SupportsX64V3();
        var preferredLinuxLibc = IsLikelyMuslLinux() ? "musl" : "glibc";
        var alternateLinuxLibc = preferredLinuxLibc == "glibc" ? "musl" : "glibc";

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
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.EndsWith("sing-box.exe"))
                {
                    entry.ExtractToFile(Path.Combine(destination, "sing-box.exe"), true);
                    return;
                }
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
                File.Copy(singBoxFile, Path.Combine(destination, "sing-box"), true);
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

    private sealed record Ref1ndReleaseAssetInfo(string TagName, string AssetName, string DownloadUrl);

    private sealed class GitHubReleaseInfo
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = string.Empty;

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubReleaseAssetInfo> Assets { get; set; } = [];
    }

    private sealed class GitHubReleaseAssetInfo
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = string.Empty;
    }

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

            _installedKernel = null;
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
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };

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
