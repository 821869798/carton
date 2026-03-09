using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace carton.Core.Utilities;

/// <summary>
/// Provides helpers to clear the system proxy settings.
/// Supports Windows (registry + WinINet), GNOME (gsettings), and KDE (kwriteconfig5/6).
/// All calls are best-effort: errors are silently swallowed.
/// </summary>
public static class SystemProxyHelper
{
    // ─── Windows ──────────────────────────────────────────────────────────────

    private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
    private const int INTERNET_OPTION_REFRESH = 37;

    private const string InternetSettingsKey =
        @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    [SupportedOSPlatform("windows")]
    [DllImport("wininet.dll", SetLastError = true, EntryPoint = "InternetSetOptionW")]
    private static extern bool InternetSetOptionW(
        IntPtr hInternet,
        int dwOption,
        IntPtr lpBuffer,
        int dwBufferLength);

    // ─── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Clears the system proxy (reverts to direct/no-proxy).
    /// Dispatches to the correct implementation based on the current OS.
    /// Safe to call on unsupported platforms — returns immediately.
    /// </summary>
    public static void ClearSystemProxy()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            ClearWindowsProxy();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            ClearLinuxProxy();
        }
        // macOS: sing-box with set_system_proxy on macOS uses networksetup.
        // Add macOS support here if needed in the future.
    }

    // ─── Windows implementation ───────────────────────────────────────────────

    [SupportedOSPlatform("windows")]
    private static void ClearWindowsProxy()
    {
        // Step 1: Write ProxyEnable=0 directly to the registry.
        // This is the same location sing-box reads/writes on Windows.
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, writable: true);
            if (key != null)
            {
                key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                key.SetValue("ProxyServer", string.Empty, RegistryValueKind.String);
            }
        }
        catch
        {
            // Best-effort.
        }

        // Step 2: Notify WinINet so all open processes pick up the change immediately.
        try
        {
            InternetSetOptionW(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
            InternetSetOptionW(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
        }
        catch
        {
            // Best-effort.
        }
    }

    // ─── Linux implementation ─────────────────────────────────────────────────

    private static void ClearLinuxProxy()
    {
        // GNOME / any gsettings-capable DE
        TryClearGnomeProxy();

        // KDE Plasma (try both kwriteconfig5 and kwriteconfig6)
        TryClearKdeProxy();
    }

    /// <summary>
    /// Clears the GNOME system proxy via <c>gsettings</c>.
    /// Equivalent to: gsettings set org.gnome.system.proxy mode 'none'
    /// </summary>
    private static void TryClearGnomeProxy()
    {
        try
        {
            RunCommand("gsettings", "set org.gnome.system.proxy mode none");
        }
        catch
        {
            // gsettings not available or command failed — ignore.
        }
    }

    /// <summary>
    /// Clears the KDE system proxy by setting ProxyType=0 (no proxy) via
    /// <c>kwriteconfig5</c> or <c>kwriteconfig6</c>, then reloads KIO slaves.
    /// </summary>
    private static void TryClearKdeProxy()
    {
        // Try kwriteconfig6 first (KDE Plasma 6), fall back to kwriteconfig5 (Plasma 5).
        string? tool = FindExecutable("kwriteconfig6") ?? FindExecutable("kwriteconfig5");
        if (tool == null)
            return;

        try
        {
            // ProxyType=0 means "no proxy" in kioslaverc
            RunCommand(tool, "--file kioslaverc --group \"Proxy Settings\" --key ProxyType 0");

            // Ask KIO to pick up the new settings without needing a logout.
            // krunner / kded / kioclient accept a dbus call, but a simple
            // config reload is sufficient for most scenarios.
            RunCommandIgnoreResult("kbuildsycoca6");
            RunCommandIgnoreResult("kbuildsycoca5");
        }
        catch
        {
            // kwriteconfig not available or command failed — ignore.
        }
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs <paramref name="executable"/> with <paramref name="arguments"/>
    /// and waits up to 3 seconds for it to finish.
    /// </summary>
    private static void RunCommand(string executable, string arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.Start();
        process.WaitForExit(3000);
    }

    private static void RunCommandIgnoreResult(string executable)
    {
        try { RunCommand(executable, string.Empty); } catch { }
    }

    /// <summary>
    /// Returns the full path of <paramref name="name"/> if it can be found on
    /// PATH, otherwise <see langword="null"/>.
    /// </summary>
    private static string? FindExecutable(string name)
    {
        try
        {
            using var which = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "which",
                    Arguments = name,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            which.Start();
            var output = which.StandardOutput.ReadToEnd().Trim();
            which.WaitForExit(2000);
            return which.ExitCode == 0 && !string.IsNullOrWhiteSpace(output) ? output : null;
        }
        catch
        {
            return null;
        }
    }
}
