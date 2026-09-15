using System.IO;
using carton.Core.Utilities;
using Xunit;

namespace carton.GUI.Tests.Utilities;

/// <summary>
/// Guards the packager stamp. A .deb, a .rpm and an AUR package install to the same directory
/// with the same payload, so this file is the only exact statement of which one is running - and
/// it is written by release scripts, so parsing has to survive whatever they produce.
/// </summary>
public class InstallStampTests
{
    [Theory]
    [InlineData("deb", InstallKind.Deb)]
    [InlineData("deb\n", InstallKind.Deb)]
    [InlineData("rpm\n", InstallKind.Rpm)]
    [InlineData("  AUR  ", InstallKind.Aur)]
    [InlineData("appimage\n# written by the AppImage build\n", InstallKind.AppImage)]
    [InlineData("portable-tar", InstallKind.PortableTar)]
    [InlineData("portable-zip", InstallKind.PortableZip)]
    [InlineData("win-setup", InstallKind.WindowsSetup)]
    [InlineData("win-portable", InstallKind.WindowsPortable)]
    [InlineData("", InstallKind.Unknown)]
    [InlineData("   \n\n", InstallKind.Unknown)]
    [InlineData(null, InstallKind.Unknown)]
    [InlineData("banana", InstallKind.Unknown)]
    public void ParsesTheStampedFormat(string? content, InstallKind expected)
        => Assert.Equal(expected, InstallStamp.Parse(content));

    [Theory]
    [InlineData(InstallKind.Deb, "deb")]
    [InlineData(InstallKind.Rpm, "rpm")]
    [InlineData(InstallKind.Aur, "aur")]
    [InlineData(InstallKind.AppImage, "appimage")]
    [InlineData(InstallKind.PortableTar, "portable-tar")]
    [InlineData(InstallKind.PortableZip, "portable-zip")]
    [InlineData(InstallKind.WindowsSetup, "win-setup")]
    [InlineData(InstallKind.WindowsPortable, "win-portable")]
    [InlineData(InstallKind.Unknown, "unknown")]
    public void SlugIsExactlyWhatParseAccepts(InstallKind kind, string slug)
    {
        // The build scripts write Slug(); if the two ever drift the stamp silently reads Unknown.
        Assert.Equal(slug, InstallStamp.Slug(kind));
        Assert.Equal(kind, InstallStamp.Parse(slug));
    }

    [Fact]
    public void DetectsTheStampSittingNextToTheExecutable()
    {
        var directory = Directory.CreateTempSubdirectory("carton-stamp-");
        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, InstallStamp.FileName), "rpm\n");

            Assert.Equal(InstallKind.Rpm, InstallStamp.Detect(directory.FullName));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void MissingStampOrDirectoryIsUnknown()
    {
        var directory = Directory.CreateTempSubdirectory("carton-nostamp-");
        try
        {
            // No stamp: older packages and third-party repackaging, must fall back to detection.
            Assert.Equal(InstallKind.Unknown, InstallStamp.Detect(directory.FullName));
            Assert.Equal(InstallKind.Unknown, InstallStamp.Detect("/definitely/not/a/directory"));
            Assert.Equal(InstallKind.Unknown, InstallStamp.Detect(null));
            Assert.Equal(InstallKind.Unknown, InstallStamp.Detect(""));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
