using carton.Core.Utilities;
using Xunit;

namespace carton.GUI.Tests.Utilities;

public sealed class SingBoxLogLevelHelperTests
{
    [Theory]
    [InlineData(null, "warn")]
    [InlineData("", "warn")]
    [InlineData("  ", "warn")]
    [InlineData("TRACE", "trace")]
    [InlineData("Debug", "debug")]
    [InlineData("info", "info")]
    [InlineData("warn", "warn")]
    [InlineData("warning", "warn")]
    [InlineData("ERROR", "error")]
    [InlineData("fatal", "fatal")]
    [InlineData("panic", "panic")]
    [InlineData("nope", "warn")]
    public void Normalize_MapsAliasesAndDefaults(string? level, string expected)
    {
        Assert.Equal(expected, SingBoxLogLevelHelper.Normalize(level));
    }

    [Theory]
    [InlineData("trace", true)]
    [InlineData("debug", true)]
    [InlineData("info", true)]
    [InlineData("warn", false)]
    [InlineData("error", false)]
    [InlineData(null, false)]
    public void IsVerbose_ReturnsExpectedResult(string? level, bool expected)
    {
        Assert.Equal(expected, SingBoxLogLevelHelper.IsVerbose(level));
    }
}
