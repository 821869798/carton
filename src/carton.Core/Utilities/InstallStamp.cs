using System;
using System.IO;

namespace carton.Core.Utilities;

/// <summary>
/// Which distribution format the running install came from.
/// </summary>
public enum InstallKind
{
    Unknown,
    Deb,
    Rpm,
    Aur,
    AppImage,
    PortableTar,
    PortableZip,
    WindowsSetup,
    WindowsPortable
}

/// <summary>
/// Reads the single-line stamp a packager drops next to the executable, so the app can tell
/// where it was installed from.
///
/// Why a file instead of a compile-time macro: the Linux package formats differ only in how they
/// are installed, not in how the app behaves, and one NativeAOT publish currently feeds the
/// AppImage, the .deb, the .rpm and the AUR tarball. A macro per format would force one publish
/// per format, and the AUR package is repackaged by makepkg from a released tarball, so it could
/// never carry one. A stamp is also something downstream repackagers can add themselves.
///
/// The stamp is advisory: nothing depends on it for correctness. A missing or unreadable stamp
/// falls back to the detection that was in place before stamping (see PackageManagedInstall), so
/// older packages and third-party repackaging keep working.
/// </summary>
public static class InstallStamp
{
    public const string FileName = ".carton_package";

    /// <summary>
    /// Parses the stamp body. Tolerates surrounding whitespace, mixed case and trailing lines.
    /// </summary>
    public static InstallKind Parse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return InstallKind.Unknown;
        }

        foreach (var line in content.Split('\n'))
        {
            var token = line.Trim();
            if (token.Length == 0)
            {
                continue;
            }

            return token.ToLowerInvariant() switch
            {
                "deb" => InstallKind.Deb,
                "rpm" => InstallKind.Rpm,
                "aur" => InstallKind.Aur,
                "appimage" => InstallKind.AppImage,
                "portable-tar" => InstallKind.PortableTar,
                "portable-zip" => InstallKind.PortableZip,
                "win-setup" => InstallKind.WindowsSetup,
                "win-portable" => InstallKind.WindowsPortable,
                _ => InstallKind.Unknown
            };
        }

        return InstallKind.Unknown;
    }

    /// <summary>
    /// Reads the stamp sitting next to the executable. Never throws: a missing file, a directory
    /// without read permission or a truncated write all mean <see cref="InstallKind.Unknown"/>.
    /// </summary>
    public static InstallKind Detect(string? appDirectory)
    {
        if (string.IsNullOrWhiteSpace(appDirectory))
        {
            return InstallKind.Unknown;
        }

        try
        {
            var stampPath = Path.Combine(appDirectory, FileName);
            return File.Exists(stampPath)
                ? Parse(File.ReadAllText(stampPath))
                : InstallKind.Unknown;
        }
        catch (Exception)
        {
            return InstallKind.Unknown;
        }
    }

    /// <summary>
    /// The exact token a packager has to write into <see cref="FileName"/>, e.g. "deb" for the
    /// .deb staging step. Kept next to <see cref="Parse"/> so the two cannot drift apart.
    /// </summary>
    public static string Slug(InstallKind kind) => kind switch
    {
        InstallKind.Deb => "deb",
        InstallKind.Rpm => "rpm",
        InstallKind.Aur => "aur",
        InstallKind.AppImage => "appimage",
        InstallKind.PortableTar => "portable-tar",
        InstallKind.PortableZip => "portable-zip",
        InstallKind.WindowsSetup => "win-setup",
        InstallKind.WindowsPortable => "win-portable",
        _ => "unknown"
    };
}
