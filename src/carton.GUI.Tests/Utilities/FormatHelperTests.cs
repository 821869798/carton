using carton.Core.Utilities;
using Xunit;

namespace carton.GUI.Tests.Utilities;

public sealed class FormatHelperTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(-1, "0 B")]
    [InlineData(1, "1 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048576, "1 MB")]
    [InlineData(1073741824, "1 GB")]
    public void FormatBytes_ReturnsExpectedUnits(long bytes, string expected)
    {
        Assert.Equal(expected, FormatHelper.FormatBytes(bytes));
    }

    [Theory]
    [InlineData(0, "0 B/s")]
    [InlineData(-5, "0 B/s")]
    [InlineData(1024, "1 KB/s")]
    public void FormatBytesPerSecond_AppendsSuffix(long bytesPerSecond, string expected)
    {
        Assert.Equal(expected, FormatHelper.FormatBytesPerSecond(bytesPerSecond));
    }

    [Fact]
    public void FormatByteProgress_UsesTotalWhenKnown()
    {
        Assert.Equal("1 KB / 2 KB", FormatHelper.FormatByteProgress(1024, 2048));
    }

    [Fact]
    public void FormatByteProgress_UsesUnknownLabelWhenTotalMissing()
    {
        Assert.Equal("1 KB / unknown", FormatHelper.FormatByteProgress(1024, 0));
        Assert.Equal("1 KB / n/a", FormatHelper.FormatByteProgress(1024, -1, "n/a"));
    }
}
