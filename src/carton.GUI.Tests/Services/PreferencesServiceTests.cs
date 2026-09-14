using carton.Core.Models;
using carton.Core.Services;
using Xunit;

namespace carton.GUI.Tests.Services;

/// <summary>
/// Covers the app-level switches the dashboard reads and writes. They used to be per-profile
/// runtime options; this pins where they live now.
/// </summary>
public sealed class PreferencesServiceTests
{
    [Fact]
    public void FreshInstall_DefaultsBothSwitchesToOff()
    {
        var baseDirectory = CreateTempDirectory();
        try
        {
            var preferences = new PreferencesService(baseDirectory).Load();

            Assert.False(preferences.SystemProxyEnabled);
            Assert.False(preferences.TunInboundEnabled);
        }
        finally
        {
            DeleteTempDirectory(baseDirectory);
        }
    }

    [Fact]
    public void BothSwitches_RoundTripThroughThePreferencesFile()
    {
        var baseDirectory = CreateTempDirectory();
        try
        {
            var service = new PreferencesService(baseDirectory);
            var preferences = service.Load();
            preferences.SystemProxyEnabled = true;
            preferences.TunInboundEnabled = true;
            service.Save(preferences);

            // A fresh instance (i.e. a restart) must read the same values back.
            var reloaded = new PreferencesService(baseDirectory).Load();

            Assert.True(reloaded.SystemProxyEnabled);
            Assert.True(reloaded.TunInboundEnabled);

            // And they are stored under the camel-case keys upgrades round-trip on.
            var raw = File.ReadAllText(Path.Combine(baseDirectory, "preferences.json"));
            Assert.Contains("\"systemProxyEnabled\": true", raw);
            Assert.Contains("\"tunInboundEnabled\": true", raw);
        }
        finally
        {
            DeleteTempDirectory(baseDirectory);
        }
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "carton-preferences-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteTempDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
