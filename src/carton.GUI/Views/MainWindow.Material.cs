using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.Styling;
using carton.Core.Models;
using carton.GUI.Services;

namespace carton.Views;

public partial class MainWindow
{
    private bool _applyingMaterial;
    private IDisposable? _headerMaterialOverride;
    private IDisposable? _sidebarMaterialOverride;
    private void InitializeMaterial()
    {
        WindowMaterialService.Instance.Apply(App.PreferencesService.Load());
        WindowMaterialService.Instance.Changed += OnMaterialChanged;
        PropertyChanged += OnMaterialWindowPropertyChanged;
        Closed += (_, _) =>
        {
            WindowMaterialService.Instance.Changed -= OnMaterialChanged;
            PropertyChanged -= OnMaterialWindowPropertyChanged;
            _headerMaterialOverride?.Dispose();
            _sidebarMaterialOverride?.Dispose();
        };
        ApplyMaterial();
    }

    private void OnMaterialChanged(object? sender, EventArgs e) => ApplyMaterial();

    private void OnMaterialWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ActualTransparencyLevelProperty || e.Property == ActualThemeVariantProperty)
            ApplyMaterial();
    }

    private void ApplyMaterial()
    {
        if (_applyingMaterial) return;
        _applyingMaterial = true;
        try
        {
            var state = WindowMaterialService.Instance;
            var requested = state.Material switch
            {
                WindowMaterial.Mica => WindowTransparencyLevel.Mica,
                _ => WindowTransparencyLevel.None
            };
            if (TransparencyLevelHint.Count == 0 || TransparencyLevelHint[0] != requested)
                TransparencyLevelHint = requested == WindowTransparencyLevel.None
                    ? new[] { WindowTransparencyLevel.None }
                    : new[] { requested, WindowTransparencyLevel.None };

            var supported = requested != WindowTransparencyLevel.None && ActualTransparencyLevel == requested;
            state.SetFallback(requested != WindowTransparencyLevel.None && !supported);
            var dark = ActualThemeVariant == ThemeVariant.Dark;
            var fallback = dark ? Color.Parse("#FF202020") : Color.Parse("#FFF3F3F3");
            TransparencyBackgroundFallback = new SolidColorBrush(fallback);
            _headerMaterialOverride?.Dispose();
            _sidebarMaterialOverride?.Dispose();
            _headerMaterialOverride = null;
            _sidebarMaterialOverride = null;
            if (!supported)
            {
                ClearValue(BackgroundProperty);
                return;
            }

            Background = Brushes.Transparent;
            // Override the resource binding without replacing it: clearing this priority
            // restores the original opaque shell when switching back to Solid.
            _headerMaterialOverride = ShellHeader.SetValue(Border.BackgroundProperty, Brushes.Transparent, BindingPriority.Animation);
            _sidebarMaterialOverride = ShellSidebar.SetValue(Border.BackgroundProperty, Brushes.Transparent, BindingPriority.Animation);
        }
        finally { _applyingMaterial = false; }
    }
}
