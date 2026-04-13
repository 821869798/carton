namespace carton.Core.Models;

public class AppPreferences
{
    public bool StartAtLogin { get; set; }
    public bool AutoStartOnLaunch { get; set; }
    public bool AutoDisconnectConnectionsOnNodeSwitch { get; set; } = false;
    public bool UseProxyForRemoteConfigUpdates { get; set; }
    public KernelCacheCleanupPolicy KernelCacheCleanupPolicy { get; set; } = KernelCacheCleanupPolicy.ClearOnChannelChange;
    public KernelInstallChannel? LastInstalledKernelChannel { get; set; }
    public AppTheme Theme { get; set; } = AppTheme.System;
    public AppLanguage Language { get; set; } = AppLanguage.English;
    public AppUpdateChannel UpdateChannel { get; set; } = AppUpdateChannel.Release;
    public DownloadMirror KernelDownloadMirror { get; set; } = DownloadMirror.GitHub;
    public bool AutoCheckAppUpdates { get; set; } = true;
}

public enum KernelCacheCleanupPolicy
{
    ClearOnChannelChange = 0,
    Never = 1
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
