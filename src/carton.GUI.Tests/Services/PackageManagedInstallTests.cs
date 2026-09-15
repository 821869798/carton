using carton.GUI.Services;
using Xunit;

namespace carton.GUI.Tests.Services;

/// <summary>
/// Guards the wording for package-managed installs. Those packages deliberately ship without
/// carton-helper (so Carton cannot update itself over the package manager), which means the
/// update hint must name the package manager instead of pointing at a download the user
/// cannot apply.
///
/// It also pins down the difference the distributions need: Carton publishes no apt/dnf
/// repository, so only the Arch/AUR case may offer a command that upgrades the package on
/// its own - suggesting `apt upgrade carton` would just fail on a machine that installed the
/// .deb by hand.
/// </summary>
public class PackageManagedInstallTests
{
    [Fact]
    public void RecognisesPackageManagerInstallDirectories()
    {
        Assert.True(PackageManagedInstall.IsSystemInstall("/usr/lib/carton/"));
        Assert.True(PackageManagedInstall.IsSystemInstall("/usr/lib/carton"));
        Assert.False(PackageManagedInstall.IsSystemInstall("/home/me/Apps/carton/"));
        Assert.False(PackageManagedInstall.IsSystemInstall("/opt/carton/"));
        Assert.False(PackageManagedInstall.IsSystemInstall(null));
    }

    [Theory]
    [InlineData("ID=debian\n", "apt", null)]
    [InlineData("ID=ubuntu\nID_LIKE=debian\n", "apt", null)]
    [InlineData("ID=linuxmint\nID_LIKE=\"ubuntu debian\"\n", "apt", null)]
    [InlineData("ID=arch\n", "AUR", "yay -Syu")]
    [InlineData("ID=cachyos\nID_LIKE=\"arch\"\n", "AUR", "yay -Syu")]
    [InlineData("ID=\"opensuse-tumbleweed\"\nID_LIKE=\"opensuse suse\"\n", "zypper", null)]
    [InlineData("ID=fedora\n", "dnf", null)]
    [InlineData("ID=almalinux\nID_LIKE=\"rhel centos fedora\"\n", "dnf", null)]
    public void MapsDistributionsToTheirPackageManager(string osRelease, string manager, string? command)
    {
        var hint = PackageManagedInstall.ResolveHint(osRelease);

        Assert.NotNull(hint);
        Assert.Equal(manager, hint!.Value.Manager);
        Assert.Equal(command, hint.Value.UpgradeCommand);
    }

    [Theory]
    [InlineData("ID=alpine\n")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("# only comments\nSOMETHING_ELSE=1\n")]
    public void UnknownDistributionsFallBackToTheGenericWording(string? osRelease)
        => Assert.Null(PackageManagedInstall.ResolveHint(osRelease));
}
