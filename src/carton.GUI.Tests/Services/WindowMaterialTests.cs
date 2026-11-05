using System.Text.Json;
using carton.Core.Models;
using carton.Core.Services;
using carton.GUI.Services;
using Xunit;

namespace carton.GUI.Tests.Services;

public class WindowMaterialTests
{
    [Fact]
    public void DefaultsAreSolidAndRepeatedSelectionDoesNotNotify()
    {
        var service = new WindowMaterialService();
        var changes = 0;
        service.Changed += (_, _) => changes++;
        service.Apply(new AppPreferences());
        Assert.Equal(WindowMaterial.Solid, service.Material);
        Assert.Equal(0, changes);
        var preferences = new AppPreferences { WindowMaterial = WindowMaterial.Mica };
        service.Apply(preferences);
        service.Apply(preferences);
        Assert.Equal(WindowMaterial.Mica, service.Material);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void FallbackNotifiesOnlyOnChange()
    {
        var service = new WindowMaterialService();
        var changes = 0;
        service.AvailabilityChanged += (_, _) => changes++;
        service.SetFallback(true);
        service.SetFallback(true);
        service.SetFallback(false);
        Assert.Equal(2, changes);
    }

    [Theory]
    [InlineData(WindowMaterial.Solid, 0)]
    [InlineData(WindowMaterial.Mica, 1)]
    public void PreferencesPersistMaterialAsInteger(WindowMaterial material, int expected)
    {
        var path = Path.Combine(Path.GetTempPath(), "carton-material-test-" + Guid.NewGuid());
        try
        {
            var service = new PreferencesService(path);
            var prefs = service.Load();
            prefs.WindowMaterial = material;
            service.Save(prefs);
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(path, "preferences.json")));
            Assert.Equal(JsonValueKind.Number, document.RootElement.GetProperty("windowMaterial").ValueKind);
            Assert.Equal(expected, document.RootElement.GetProperty("windowMaterial").GetInt32());
            Assert.DoesNotContain(document.RootElement.EnumerateObject(), p => p.Name.StartsWith("acrylic", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(material, new PreferencesService(path).Load().WindowMaterial);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }
}
