using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
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
    Task<string?> GetLatestVersionAsync();
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

    private const string Ref1ndBaseUrl = "https://github.com/DustinWin/proxy-tools/releases/download/sing-box";

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

    public async Task<string?> GetLatestVersionAsync()
    {
        try
        {
            // Try API first
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
                // Fallback: follow the /releases/latest redirect to get the tag
                using var request = new HttpRequestMessage(HttpMethod.Head, "https://github.com/SagerNet/sing-box/releases/latest");
                using var response = await _httpClient.SendAsync(request);
                var finalUrl = response.RequestMessage?.RequestUri?.ToString();

                if (finalUrl != null && finalUrl.Contains("/releases/tag/"))
                {
                    var version = finalUrl.Substring(finalUrl.LastIndexOf('/') + 1);
                    return version;
                }
            }

            return null;
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

    /// <summary>
    /// Downloads and installs sing-box from the ref1nd release channel (DustinWin/proxy-tools).
    /// Files are published at a fixed tag (sing-box) and use a custom naming scheme:
    ///   Windows amd64  → sing-box-ref1nd-stable-windows-amd64-v3.exe  (direct exe, no archive)
    ///   Windows arm64  → sing-box-ref1nd-stable-windows-arm64.exe      (direct exe, no archive)
    ///   Linux   amd64  → sing-box-ref1nd-stable-linux-amd64-v3.tar.gz
    ///   Linux   arm64  → sing-box-ref1nd-stable-linux-arm64.tar.gz
    /// </summary>
    private async Task<bool> DownloadAndInstallFromRef1ndAsync(DownloadMirror mirror)
    {
        try
        {
            var platform = PlatformInfo.Current;
            var fileName = GetRef1ndFileName(platform, mirror);
            if (fileName == null)
            {
                StatusChanged?.Invoke(this, $"ref1nd channel does not support platform: {platform.OS}-{platform.Arch}");
                return false;
            }

            var downloadUrl = $"{Ref1ndBaseUrl}/{fileName}";
            StatusChanged?.Invoke(this, $"Downloading ref1nd sing-box ({GetRef1ndChannelLabel(mirror)})...");

            var isWindowsDirect = platform.OS == "windows";
            var tempExt = isWindowsDirect ? ".exe" : ".tar.gz";
            var tempFile = Path.Combine(Path.GetTempPath(), $"sing-box-dustinwin{tempExt}");

            using (var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
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

            if (isWindowsDirect)
            {
                // Windows builds are distributed as a standalone .exe — install directly.
                StatusChanged?.Invoke(this, "Installing...");
                await KillRunningKernelAsync();
                var dest = Path.Combine(_binDirectory, "sing-box.exe");
                File.Move(tempFile, dest, overwrite: true);
            }
            else
            {
                StatusChanged?.Invoke(this, "Extracting...");
                await ExtractArchiveAsync(tempFile, _binDirectory);
                File.Delete(tempFile);

                var chmodPath = Path.Combine(_binDirectory, "sing-box");
                if (File.Exists(chmodPath))
                    Process.Start("chmod", $"+x \"{chmodPath}\"")?.WaitForExit();
            }

            await GetInstalledKernelInfoAsync();
            StatusChanged?.Invoke(this, $"Successfully installed sing-box (ref1nd {GetRef1ndChannelLabel(mirror)})");
            return true;
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, $"Failed to install: {ex.Message}");
            return false;
        }
    }

    public async Task<KernelPackageDownloadResult?> DownloadPackageAsync(string? version = null, DownloadMirror mirror = DownloadMirror.GitHub)
    {
        try
        {
            if (mirror is DownloadMirror.Ref1ndStable or DownloadMirror.Ref1ndTest)
            {
                var platform = PlatformInfo.Current;
                var fileName = GetRef1ndFileName(platform, mirror);
                if (fileName == null)
                {
                    StatusChanged?.Invoke(this, $"ref1nd channel does not support platform: {platform.OS}-{platform.Arch}");
                    return null;
                }

                var downloadUrl = $"{Ref1ndBaseUrl}/{fileName}";
                var channelLabel = GetRef1ndChannelLabel(mirror);
                StatusChanged?.Invoke(this, $"Downloading ref1nd sing-box ({channelLabel})...");

                var tempExt = platform.OS == "windows" ? ".exe" : ".tar.gz";
                var tempFile = Path.Combine(Path.GetTempPath(), $"sing-box-ref1nd-{Guid.NewGuid():N}{tempExt}");
                await DownloadFileAsync(downloadUrl, tempFile);
                StatusChanged?.Invoke(this, $"Downloaded ref1nd sing-box ({channelLabel})");

                return new KernelPackageDownloadResult
                {
                    TempFilePath = tempFile,
                    VersionLabel = $"(ref1nd {channelLabel})",
                    SourceChannel = mirror == DownloadMirror.Ref1ndTest
                        ? KernelInstallChannel.Ref1ndTest
                        : KernelInstallChannel.Ref1ndStable
                };
            }

            version ??= await GetLatestVersionAsync();
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
                DownloadMirror.GhProxy => $"https://gh-proxy.com/{originalUrl}",
                _ => originalUrl
            };

            var archiveFile = Path.Combine(Path.GetTempPath(), $"sing-box-{Guid.NewGuid():N}{archiveExt}");
            await DownloadFileAsync(downloadUrlForMirror, archiveFile);
            StatusChanged?.Invoke(this, $"Downloaded sing-box {version}");

            return new KernelPackageDownloadResult
            {
                TempFilePath = archiveFile,
                VersionLabel = version,
                SourceChannel = KernelInstallChannel.Official
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

    /// <summary>
    /// Returns the ref1nd asset filename for the current platform, or null if unsupported.
    /// </summary>
    private static string? GetRef1ndFileName(PlatformInfo platform, DownloadMirror mirror)
    {
        var channelLabel = GetRef1ndChannelLabel(mirror);
        return (platform.OS, platform.Arch) switch
        {
            ("windows", "amd64") => $"sing-box-ref1nd-{channelLabel}-windows-amd64-v3.exe",
            ("windows", "arm64") => $"sing-box-ref1nd-{channelLabel}-windows-arm64.exe",
            ("linux", "amd64") => $"sing-box-ref1nd-{channelLabel}-linux-amd64-v3.tar.gz",
            ("linux", "arm64") => $"sing-box-ref1nd-{channelLabel}-linux-arm64.tar.gz",
            _ => null
        };
    }

    private static string GetRef1ndChannelLabel(DownloadMirror mirror)
        => mirror == DownloadMirror.Ref1ndTest ? "test" : "stable";

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
