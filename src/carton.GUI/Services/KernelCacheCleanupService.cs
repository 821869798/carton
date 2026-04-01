using carton.Core.Models;

namespace carton.GUI.Services;

public static class KernelCacheCleanupService
{
    public static KernelInstallChannel GetInstallChannel(DownloadMirror mirror)
        => mirror switch
        {
            DownloadMirror.Ref1ndStable => KernelInstallChannel.Ref1ndStable,
            DownloadMirror.Ref1ndTest => KernelInstallChannel.Ref1ndTest,
            _ => KernelInstallChannel.Official
        };

    public static bool ShouldClearCache(
        AppPreferences preferences,
        KernelInstallChannel nextChannel,
        bool hadInstalledKernel)
    {
        if (preferences.KernelCacheCleanupPolicy == KernelCacheCleanupPolicy.Never)
        {
            return false;
        }

        if (nextChannel == KernelInstallChannel.Custom)
        {
            return true;
        }

        if (!hadInstalledKernel)
        {
            return false;
        }

        return preferences.LastInstalledKernelChannel == null ||
               preferences.LastInstalledKernelChannel != nextChannel;
    }

    public static void RecordInstalledChannel(AppPreferences preferences, KernelInstallChannel? channel)
    {
        preferences.LastInstalledKernelChannel = channel;
    }
}
