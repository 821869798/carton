using carton.Core.Services;
using Xunit;

namespace carton.GUI.Tests.Services;

/// <summary>
/// Guards the Linux TUN authorization command. The regression this covers: chown and chmod used
/// to run as two separate "sudo -S" calls while only one password line was written to the shared
/// stdin, so the second sudo authenticated with an empty password ("auth could not identify
/// password"). The kernel ended up root-owned without the setuid bit, TUN failed, and the file
/// was no longer writable by the user. Distros whose sudo credential cache happened to cover the
/// second call (Ubuntu) hid the bug; ones that re-prompt (CachyOS) did not.
/// </summary>
public sealed class LinuxKernelAuthorizationTests
{
    private const string KernelPath = "/home/u/.config/Carton/bin/sing-box";

    /// <summary>
    /// The argv layer is where the bug lived, so the shape is asserted end to end: one sudo (the
    /// process name), -S to read the single password line from stdin, and the privileged command
    /// behind "--" so no second sudo can appear anywhere.
    /// </summary>
    [Fact]
    public void BuildLinuxAuthorizationArguments_PassesOneSudoReadingStdinOnce()
    {
        var arguments = SingBoxManager.BuildLinuxAuthorizationArguments(KernelPath);

        Assert.Equal(
            ["-S", "-p", "", "--", "/bin/sh", "-c", SingBoxManager.BuildLinuxAuthorizationCommand(KernelPath)],
            arguments);
        Assert.DoesNotContain(arguments, argument => argument.Contains("sudo", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildLinuxAuthorizationCommand_ChainsChownThenChmodOnTheSamePath()
    {
        var command = SingBoxManager.BuildLinuxAuthorizationCommand(KernelPath);

        // 6755 is "-rwsr-sr-x": a symbolic "+sx" depends on the mode the copy landed with and on
        // the umask, so the setuid bit could silently go missing.
        Assert.Equal($"chown root:root '{KernelPath}' && chmod 6755 '{KernelPath}'", command);
    }

    [Theory]
    [InlineData("/home/user name/.config/Carton/bin/sing-box", "'/home/user name/.config/Carton/bin/sing-box'")]
    [InlineData("/home/u/it's/sing-box", "'/home/u/it'\"'\"'s/sing-box'")]
    public void BuildLinuxAuthorizationCommand_QuotesPathsTheShellWouldSplit(string kernelPath, string expectedQuoted)
    {
        var command = SingBoxManager.BuildLinuxAuthorizationCommand(kernelPath);

        Assert.Equal($"chown root:root {expectedQuoted} && chmod 6755 {expectedQuoted}", command);
    }

    [Fact]
    public void FormatSudoFailure_KeepsSudoMessageAndDropsPrompt()
    {
        var message = SingBoxManager.FormatSudoFailure(1, "[sudo] password for u: \nsudo: 1 incorrect password attempt");

        Assert.Contains("sudo: 1 incorrect password attempt", message);
        Assert.DoesNotContain("[sudo]", message);
    }

    [Fact]
    public void FormatSudoFailure_FallsBackToExitCodeWhenSilent()
    {
        var message = SingBoxManager.FormatSudoFailure(1, "   \n \n");

        Assert.Contains("1", message);
        Assert.False(string.IsNullOrWhiteSpace(message));
    }
}
