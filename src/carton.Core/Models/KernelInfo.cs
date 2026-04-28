using System.Runtime.InteropServices;

namespace carton.Core.Models;

public class KernelInfo
{
    public string KernelVersion { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public DateTime? InstallTime { get; set; }
    public bool IsBuiltin { get; set; }
    public PlatformInfo Platform { get; set; } = new();
}

public class PlatformInfo
{
    public string OS { get; set; } = string.Empty;
    public string Arch { get; set; } = string.Empty;
    public string Suffix { get; set; } = string.Empty;

    public static PlatformInfo Current => new()
    {
        OS = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows" :
             RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "darwin" :
             RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux" : "unknown",
        Arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "amd64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "386",
            Architecture.Arm => "arm",
            _ => "unknown"
        },
        Suffix = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : ""
    };

    public string GetDownloadFileName(string version) => 
        $"sing-box-{version}-{OS}-{Arch}{(OS == "windows" ? ".zip" : ".tar.gz")}";

    public string GetGitHubReleaseAsset(string version) => 
        $"sing-box-{version}-{OS}-{Arch}{(OS == "windows" ? ".zip" : ".tar.gz")}";
}
