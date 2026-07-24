using carton.GUI.Services;
using Xunit;

namespace carton.GUI.Tests.Services;

public sealed class AppLaunchOptionsTests
{
    [Fact]
    public void Parse_EmptyArgs_DoesNotStartHidden()
    {
        var options = AppLaunchOptions.Parse([]);
        Assert.False(options.StartHidden);
    }

    [Theory]
    [InlineData("--background")]
    [InlineData("--BACKGROUND")]
    [InlineData("--Background")]
    public void Parse_BackgroundFlag_StartsHidden(string flag)
    {
        var options = AppLaunchOptions.Parse([flag]);
        Assert.True(options.StartHidden);
    }

    [Fact]
    public void Parse_UnknownArgs_AreIgnored()
    {
        var options = AppLaunchOptions.Parse(["--foo", "bar"]);
        Assert.False(options.StartHidden);
    }

    [Fact]
    public void Parse_BackgroundAmongOtherArgs_StartsHidden()
    {
        var options = AppLaunchOptions.Parse(["--foo", "--background", "bar"]);
        Assert.True(options.StartHidden);
    }
}
