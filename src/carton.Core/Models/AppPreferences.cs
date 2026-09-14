namespace carton.Core.Models;

public class AppPreferences
{
    public double JsonEditorFontSize { get; set; } = 13;
    public bool StartAtLogin { get; set; }
    public bool StartHiddenAtLogin { get; set; }
    public bool AutoStartOnLaunch { get; set; }
    public bool AutoDisconnectConnectionsOnNodeSwitch { get; set; } = false;

    /// <summary>
    /// Whether carton arms the system proxy while the kernel runs.
    /// </summary>
    /// <remarks>
    /// App-level, not per profile: the dashboard switch describes how this machine runs the
    /// proxy, so it must survive a profile switch and must not be reachable by "reset to
    /// config values" (which only restores what the profile's config file genuinely owns:
    /// port, LAN scope, log level).
    /// </remarks>
    public bool SystemProxyEnabled { get; set; }

    /// <summary>
    /// Whether carton injects a TUN inbound into the generated runtime config.
    /// </summary>
    /// <remarks>App-level for the same reason as <see cref="SystemProxyEnabled"/>. An inbound
    /// that already exists in the user's config is reused as-is - carton only decides whether
    /// TUN is on, never the parameters inside the node.</remarks>
    public bool TunInboundEnabled { get; set; }
    public bool SaveWindowPlacement { get; set; }
    public bool UseProxyForRemoteConfigUpdates { get; set; }
    public string CustomUserAgent { get; set; } = string.Empty;
    public GitHubUpdateCheckStrategy GitHubUpdateCheckStrategy { get; set; } = GitHubUpdateCheckStrategy.ApiThenAtom;
    public KernelCacheCleanupPolicy KernelCacheCleanupPolicy { get; set; } = KernelCacheCleanupPolicy.ClearOnChannelChange;
    public KernelInstallChannel? LastInstalledKernelChannel { get; set; }
    public AppTheme Theme { get; set; } = AppTheme.System;
    public bool UseSystemThemeAccent { get; set; } = true;
    public string ThemeAccent { get; set; } = "#FF0078D7";
    public AppLanguage Language { get; set; } = AppLanguage.English;
    public AppUpdateChannel UpdateChannel { get; set; } = AppUpdateChannel.Release;
    public DownloadMirror KernelDownloadMirror { get; set; } = DownloadMirror.GitHub;
    public bool AutoCheckAppUpdates { get; set; } = true;

    /// <summary>
    /// Loopback port of the last sing-box native API service. Restored on startup so
    /// carton can re-attach to an externally still-running kernel (e.g. after a crash
    /// with TUN active) instead of probing the static default port.
    /// </summary>
    public int LastNativeApiPort { get; set; }

    /// <summary>Secret of the last sing-box native API service (empty when none).</summary>
    public string LastNativeApiSecret { get; set; } = string.Empty;
}

public enum KernelCacheCleanupPolicy
{
    ClearOnChannelChange = 0,
    Never = 1
}

public enum GitHubUpdateCheckStrategy
{
    ApiThenAtom = 0,
    ApiOnly = 1
}

public enum KernelInstallChannel
{
    Official = 0,
    Ref1ndStable = 1,
    Ref1ndTest = 2,
    Custom = 3,
    OfficialPreRelease = 4
}

public enum DownloadMirror
{
    GitHub = 0,
    GhProxy = 1,
    Ref1ndStable = 2,
    Ref1ndTest = 3,
    GitHubPreRelease = 4,
    GhProxyPreRelease = 5
}

public enum AppTheme
{
    System,
    Light,
    Dark
}

public enum AppLanguage
{
    English,
    SimplifiedChinese
}

public enum AppUpdateChannel
{
    Release,
    Beta
}
