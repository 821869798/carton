using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
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

    public KernelManager(string baseDirectory)
    {
        _binDirectory = Path.Combine(baseDirectory, "bin");
        _kernelPath = GetKernelExecutablePath();


        Directory.CreateDirectory(_binDirectory);
    }

    private static string GetKernelExecutablePath()
    {
        var platform = PlatformInfo.Current;
        var fileName = $"sing-box{platform.Suffix}";
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Carton", "bin", fileName)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Carton", "bin", fileName);
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
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            var firstLine = output.Split('\n').FirstOrDefault();
            if (firstLine != null && firstLine.Contains("sing-box"))
            {
                var parts = firstLine.Split(' ');
                return parts.LastOrDefault()?.Trim();
            }
        }
        catch
        {
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
        try
        {
            version ??= await GetLatestVersionAsync();
            if (string.IsNullOrEmpty(version))
            {
                StatusChanged?.Invoke(this, "Failed to get latest version");
                return false;
            }

            StatusChanged?.Invoke(this, $"Downloading sing-box {version}...");

            var platform = PlatformInfo.Current;
            var assetName = $"sing-box-{version.TrimStart('v')}-{platform.OS}-{platform.Arch}";
            var archiveExt = platform.OS == "windows" ? ".zip" : ".tar.gz";
            var originalUrl = $"{GitHubDownloadUrl}/{version}/{assetName}{archiveExt}";

            var downloadUrl = mirror switch
            {
                DownloadMirror.GhProxy => $"https://gh-proxy.com/{originalUrl}",
                _ => originalUrl
            };

            var tempFile = Path.Combine(Path.GetTempPath(), $"sing-box{archiveExt}");

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

            StatusChanged?.Invoke(this, "Extracting...");

            await ExtractArchiveAsync(tempFile, _binDirectory);

            File.Delete(tempFile);

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var chmodPath = Path.Combine(_binDirectory, "sing-box");
                if (File.Exists(chmodPath))
                {
                    Process.Start("chmod", $"+x \"{chmodPath}\"")?.WaitForExit();
                }
            }

            await GetInstalledKernelInfoAsync();
            StatusChanged?.Invoke(this, $"Successfully installed sing-box {version}");

            return true;
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, $"Failed to install: {ex.Message}");
            return false;
        }
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
