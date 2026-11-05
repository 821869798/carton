using System;
using carton.Core.Models;

namespace carton.GUI.Services;

/// <summary>Appearance state shared by the settings page and the main window.</summary>
public sealed class WindowMaterialService
{
    public static WindowMaterialService Instance { get; } = new();
    public event EventHandler? Changed;
    public event EventHandler? AvailabilityChanged;
    public WindowMaterial Material { get; private set; }
    public bool IsFallback { get; private set; }

    public void Apply(AppPreferences preferences)
    {
        var material = preferences.WindowMaterial;
        if (Material == material) return;
        Material = material;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetFallback(bool fallback)
    {
        if (IsFallback == fallback) return;
        IsFallback = fallback;
        AvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }
}
