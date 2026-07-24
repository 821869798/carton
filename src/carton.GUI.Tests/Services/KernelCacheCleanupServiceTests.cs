using carton.Core.Models;
using carton.GUI.Services;
using Xunit;

namespace carton.GUI.Tests.Services;

public sealed class KernelCacheCleanupServiceTests
{
    [Theory]
    [InlineData(DownloadMirror.GitHub, KernelInstallChannel.Official)]
    [InlineData(DownloadMirror.GhProxy, KernelInstallChannel.Official)]
    [InlineData(DownloadMirror.Ref1ndStable, KernelInstallChannel.Ref1ndStable)]
    [InlineData(DownloadMirror.Ref1ndTest, KernelInstallChannel.Ref1ndTest)]
    [InlineData(DownloadMirror.GitHubPreRelease, KernelInstallChannel.OfficialPreRelease)]
    [InlineData(DownloadMirror.GhProxyPreRelease, KernelInstallChannel.OfficialPreRelease)]
    public void GetInstallChannel_MapsMirror(DownloadMirror mirror, KernelInstallChannel expected)
    {
        Assert.Equal(expected, KernelCacheCleanupService.GetInstallChannel(mirror));
    }

    [Fact]
    public void ShouldClearCache_NeverPolicy_ReturnsFalse()
    {
        var preferences = new AppPreferences
        {
            KernelCacheCleanupPolicy = KernelCacheCleanupPolicy.Never,
            LastInstalledKernelChannel = KernelInstallChannel.Official
        };

        Assert.False(KernelCacheCleanupService.ShouldClearCache(
            preferences,
            KernelInstallChannel.Ref1ndStable,
            hadInstalledKernel: true));
    }

    [Fact]
    public void ShouldClearCache_CustomChannel_ReturnsTrue()
    {
        var preferences = new AppPreferences
        {
            KernelCacheCleanupPolicy = KernelCacheCleanupPolicy.ClearOnChannelChange,
            LastInstalledKernelChannel = KernelInstallChannel.Official
        };

        Assert.True(KernelCacheCleanupService.ShouldClearCache(
            preferences,
            KernelInstallChannel.Custom,
            hadInstalledKernel: false));
    }

    [Fact]
    public void ShouldClearCache_NoInstalledKernel_ReturnsFalse()
    {
        var preferences = new AppPreferences
        {
            KernelCacheCleanupPolicy = KernelCacheCleanupPolicy.ClearOnChannelChange,
            LastInstalledKernelChannel = null
        };

        Assert.False(KernelCacheCleanupService.ShouldClearCache(
            preferences,
            KernelInstallChannel.Official,
            hadInstalledKernel: false));
    }

    [Fact]
    public void ShouldClearCache_ChannelChanged_ReturnsTrue()
    {
        var preferences = new AppPreferences
        {
            KernelCacheCleanupPolicy = KernelCacheCleanupPolicy.ClearOnChannelChange,
            LastInstalledKernelChannel = KernelInstallChannel.Official
        };

        Assert.True(KernelCacheCleanupService.ShouldClearCache(
            preferences,
            KernelInstallChannel.Ref1ndStable,
            hadInstalledKernel: true));
    }

    [Fact]
    public void ShouldClearCache_SameChannel_ReturnsFalse()
    {
        var preferences = new AppPreferences
        {
            KernelCacheCleanupPolicy = KernelCacheCleanupPolicy.ClearOnChannelChange,
            LastInstalledKernelChannel = KernelInstallChannel.Official
        };

        Assert.False(KernelCacheCleanupService.ShouldClearCache(
            preferences,
            KernelInstallChannel.Official,
            hadInstalledKernel: true));
    }

    [Fact]
    public void ShouldClearCache_MissingLastChannel_ReturnsTrue()
    {
        var preferences = new AppPreferences
        {
            KernelCacheCleanupPolicy = KernelCacheCleanupPolicy.ClearOnChannelChange,
            LastInstalledKernelChannel = null
        };

        Assert.True(KernelCacheCleanupService.ShouldClearCache(
            preferences,
            KernelInstallChannel.Official,
            hadInstalledKernel: true));
    }

    [Fact]
    public void RecordInstalledChannel_UpdatesPreference()
    {
        var preferences = new AppPreferences();
        KernelCacheCleanupService.RecordInstalledChannel(preferences, KernelInstallChannel.Ref1ndTest);
        Assert.Equal(KernelInstallChannel.Ref1ndTest, preferences.LastInstalledKernelChannel);
    }
}
