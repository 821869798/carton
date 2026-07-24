using carton.Core.Services;
using Xunit;

namespace carton.GUI.Tests.Services;

public sealed class GitHubReleaseLookupTests
{
    [Theory]
    [InlineData("v1.2.0", "1.2.0")]
    [InlineData("V1.2.0", "1.2.0")]
    [InlineData("1.2.0", "1.2.0")]
    [InlineData("refs/tags/v1.2.0", "1.2.0")]
    [InlineData("refs/tags/1.2.0-beta.1", "1.2.0-beta.1")]
    [InlineData("  v1.2.0  ", "1.2.0")]
    [InlineData("", "")]
    public void NormalizeVersion_StripsCommonPrefixes(string tag, string expected)
    {
        Assert.Equal(expected, GitHubReleaseLookup.NormalizeVersion(tag));
    }

    [Theory]
    [InlineData("1.2.0-beta.1", true)]
    [InlineData("v1.2.0-rc.2", true)]
    [InlineData("1.2.0-alpha", true)]
    [InlineData("1.2.0-preview.3", true)]
    [InlineData("1.2.0-pre", true)]
    [InlineData("beta", true)]
    [InlineData("1.2.0", false)]
    [InlineData("v1.2.0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("   ", false)]
    public void IsLikelyPrereleaseTag_DetectsPrereleaseMarkers(string? value, bool expected)
    {
        Assert.Equal(expected, GitHubReleaseLookup.IsLikelyPrereleaseTag(value));
    }
}
