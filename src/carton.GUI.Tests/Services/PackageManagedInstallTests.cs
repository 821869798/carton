using carton.Core.Utilities;
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

    [Theory]
    // The format comes from the stamp, the manager from the distribution the user actually runs.
    [InlineData(InstallKind.Deb, "ID=ubuntu\nID_LIKE=debian\n", "apt", null)]
    [InlineData(InstallKind.Deb, "ID=fedora\n", "dnf", null)]
    [InlineData(InstallKind.Rpm, "ID=opensuse-leap\n", "zypper", null)]
    [InlineData(InstallKind.Aur, "ID=arch\n", "AUR", "yay -Syu")]
    // No readable /etc/os-release: the stamped format names the family's default manager.
    [InlineData(InstallKind.Deb, null, "apt", null)]
    [InlineData(InstallKind.Rpm, null, "dnf", null)]
    [InlineData(InstallKind.Aur, null, "AUR", "yay -Syu")]
    public void StampedPackagesResolve(
        InstallKind stamp,
        string? osRelease,
        string manager,
        string? command)
    {
        var hint = PackageManagedInstall.ResolveHint(stamp, osRelease, isSystemDirectory: false);

        Assert.NotNull(hint);
        Assert.Equal(manager, hint!.Value.Manager);
        Assert.Equal(command, hint.Value.UpgradeCommand);
    }

    [Theory]
    // Installs that predate the stamp: same /usr/ + os-release detection as before, so nothing
    // regresses for packages already out in the wild.
    [InlineData(InstallKind.Unknown, true, "ID=ubuntu\n", "apt")]
    [InlineData(InstallKind.Unknown, true, null, null)]
    [InlineData(InstallKind.Unknown, false, "ID=ubuntu\n", null)]
    // A stamp saying "not package-managed" wins over the directory heuristic: an AppImage
    // unpacked somewhere under /usr must not be told to run apt.
    [InlineData(InstallKind.AppImage, true, "ID=ubuntu\n", null)]
    [InlineData(InstallKind.PortableTar, true, "ID=arch\n", null)]
    [InlineData(InstallKind.WindowsSetup, true, "ID=ubuntu\n", null)]
    public void StampWinsOverTheDirectoryHeuristic(
        InstallKind stamp,
        bool isSystemDirectory,
        string? osRelease,
        string? expectedManager)
    {
        var hint = PackageManagedInstall.ResolveHint(stamp, osRelease, isSystemDirectory);

        Assert.Equal(expectedManager, hint?.Manager);
    }
}
