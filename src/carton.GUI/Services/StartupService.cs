using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace carton.GUI.Services;

public interface IStartupService
{
    void ApplyStartAtLoginPreference(bool enabled, bool startHidden = false);
    bool IsStartAtLoginEnabled();
}

public sealed class StartupService : IStartupService
{
    private const string WindowsRunKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    private const string AppName = "Carton";
    private const string LinuxDesktopFileName = "carton.desktop";

    public void ApplyStartAtLoginPreference(bool enabled, bool startHidden = false)
    {
        if (OperatingSystem.IsWindows())
        {
            ApplyWindowsStartAtLoginPreference(enabled, startHidden);
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            ApplyLinuxStartAtLoginPreference(enabled, startHidden);
        }
    }

    public bool IsStartAtLoginEnabled()
    {
        if (OperatingSystem.IsWindows())
        {
            return IsWindowsStartAtLoginEnabled();
        }

        if (OperatingSystem.IsLinux())
        {
            return IsLinuxStartAtLoginEnabled();
        }

        return false;
    }

    [SupportedOSPlatform("windows")]
    private static void ApplyWindowsStartAtLoginPreference(bool enabled, bool startHidden)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(WindowsRunKey, writable: true) ??
                            Registry.CurrentUser.CreateSubKey(WindowsRunKey);
            if (key == null)
            {
                return;
            }

            if (enabled)
            {
                var executablePath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(executablePath))
                {
                    return;
                }

                var command = $"\"{executablePath}\"";
                if (startHidden)
                {
                    command += $" {AppLaunchOptions.BackgroundArgument}";
                }

                key.SetValue(AppName, command);
            }
            else
            {
                key.DeleteValue(AppName, false);
            }
        }
        catch
        {
            // Ignore registry failures because startup control is best-effort.
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool IsWindowsStartAtLoginEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(WindowsRunKey, writable: false);
            if (key == null)
            {
                return false;
            }

            return key.GetValue(AppName) is string value && !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            return false;
        }
    }

    [SupportedOSPlatform("linux")]
    private static void ApplyLinuxStartAtLoginPreference(bool enabled, bool startHidden)
    {
        try
        {
            var desktopFilePath = GetLinuxAutostartDesktopFilePath();
            if (string.IsNullOrWhiteSpace(desktopFilePath))
            {
                return;
            }

            if (!enabled)
            {
                if (File.Exists(desktopFilePath))
                {
                    File.Delete(desktopFilePath);
                }

                return;
            }

            var executablePath = GetLinuxStartupExecutablePath();
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                return;
            }

            var autostartDirectory = Path.GetDirectoryName(desktopFilePath);
            if (string.IsNullOrWhiteSpace(autostartDirectory))
            {
                return;
            }

            Directory.CreateDirectory(autostartDirectory);
            File.WriteAllText(
                desktopFilePath,
                BuildLinuxDesktopEntry(executablePath, startHidden),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch
        {
            // Ignore startup control failures because this is best-effort.
        }
    }

    [SupportedOSPlatform("linux")]
    private static bool IsLinuxStartAtLoginEnabled()
    {
        try
        {
            var desktopFilePath = GetLinuxAutostartDesktopFilePath();
            if (string.IsNullOrWhiteSpace(desktopFilePath) || !File.Exists(desktopFilePath))
            {
                return false;
            }

            var content = File.ReadAllText(desktopFilePath);
            return !content.Contains("Hidden=true", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    [SupportedOSPlatform("linux")]
    private static string? GetLinuxAutostartDesktopFilePath()
    {
        var configRoot = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configRoot))
        {
            configRoot = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        }

        if (string.IsNullOrWhiteSpace(configRoot))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(home))
            {
                return null;
            }

            configRoot = Path.Combine(home, ".config");
        }

        return Path.Combine(configRoot, "autostart", LinuxDesktopFileName);
    }

    [SupportedOSPlatform("linux")]
    private static string? GetLinuxStartupExecutablePath()
    {
        // AppImage runs from a transient mount point, so autostart must use the original file path.
        var appImagePath = Environment.GetEnvironmentVariable("APPIMAGE");
        if (IsValidLinuxStartupExecutable(appImagePath))
        {
            return appImagePath;
        }

        var executablePath = Environment.ProcessPath;
        if (IsValidLinuxStartupExecutable(executablePath))
        {
            return executablePath;
        }

        executablePath = Process.GetCurrentProcess().MainModule?.FileName;
        return IsValidLinuxStartupExecutable(executablePath)
            ? executablePath
            : null;
    }

    [SupportedOSPlatform("linux")]
    private static bool IsValidLinuxStartupExecutable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            var mode = File.GetUnixFileMode(path);
            return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildLinuxDesktopEntry(string executablePath, bool startHidden)
    {
        var builder = new StringBuilder();
        builder.AppendLine("[Desktop Entry]");
        builder.AppendLine("Type=Application");
        builder.AppendLine("Version=1.0");
        builder.AppendLine($"Name={AppName}");
        builder.AppendLine($"Exec={BuildLinuxDesktopExecCommand(executablePath, startHidden)}");
        builder.AppendLine("Terminal=false");
        builder.AppendLine("StartupNotify=false");
        builder.AppendLine("X-GNOME-Autostart-enabled=true");
        return builder.ToString();
    }

    private static string BuildLinuxDesktopExecCommand(string executablePath, bool startHidden)
    {
        var arguments = new[]
        {
            executablePath,
            startHidden ? AppLaunchOptions.BackgroundArgument : null
        };

        return string.Join(
            " ",
            arguments
                .Where(static argument => !string.IsNullOrWhiteSpace(argument))
                .Select(static argument => EscapeDesktopEntryArgument(argument!)));
    }

    private static string EscapeDesktopEntryArgument(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');

        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    builder.Append(ch);
                    break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }
}
