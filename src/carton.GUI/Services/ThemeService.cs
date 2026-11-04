using System;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Themes.Fluent2;
using Avalonia.Threading;
using carton.Core.Models;

namespace carton.GUI.Services;

public interface IThemeService
{
    AppTheme CurrentTheme { get; }
    bool UseSystemThemeAccent { get; }
    string ThemeAccent { get; }
    event EventHandler<AppTheme>? ThemeChanged;
    void Initialize(AppTheme theme, bool useSystemThemeAccent, string themeAccent);
    void ApplyTheme(AppTheme theme);
    void ApplyAccent(bool useSystemThemeAccent, string themeAccent);
}

public sealed class ThemeService : IThemeService
{
    private static readonly Lazy<ThemeService> _instance = new(() => new ThemeService());
    public static ThemeService Instance => _instance.Value;

    private bool _initialized;
    private bool _platformSettingsHooked;
    private int _accentUpdateVersion;

    public AppTheme CurrentTheme { get; private set; } = AppTheme.System;
    public bool UseSystemThemeAccent { get; private set; }
    public string ThemeAccent { get; private set; } = "#FF0078D7";

    public event EventHandler<AppTheme>? ThemeChanged;

    private ThemeService()
    {
    }

    public void Initialize(AppTheme theme, bool useSystemThemeAccent, string themeAccent)
    {
        if (_initialized)
        {
            ApplyTheme(theme);
            ApplyAccent(useSystemThemeAccent, themeAccent);
            return;
        }

        ApplyTheme(theme);
        ApplyAccent(useSystemThemeAccent, themeAccent);
        _initialized = true;
    }

    public void ApplyTheme(AppTheme theme)
    {
        CurrentTheme = theme;

        var app = Application.Current ?? throw new InvalidOperationException("Application is not ready");
        app.RequestedThemeVariant = theme switch
        {
            AppTheme.Light => ThemeVariant.Light,
            AppTheme.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };

        ThemeChanged?.Invoke(this, theme);
    }

    public void ApplyAccent(bool useSystemThemeAccent, string themeAccent)
    {
        UseSystemThemeAccent = useSystemThemeAccent;

        var normalizedAccent = NormalizeAccent(themeAccent);
        ThemeAccent = normalizedAccent;

        var app = Application.Current ?? throw new InvalidOperationException("Application is not ready");
        EnsurePlatformSettingsHooked(app);

        var fluent2Theme = app.Styles.OfType<Fluent2Theme>().FirstOrDefault();
        var accentUpdateVersion = ++_accentUpdateVersion;

        if (useSystemThemeAccent)
        {
            if (fluent2Theme != null)
            {
                fluent2Theme.Palettes.Remove(ThemeVariant.Light);
                fluent2Theme.Palettes.Remove(ThemeVariant.Dark);
            }

            // 跟随系统色:系统 AccentColor 变化时可能稍后就绪，读取当前并排队一次后台刷新。
            UpdateCartonAccentBrush(app, ResolveCurrentAccentColor(app, normalizedAccent));
            Dispatcher.UIThread.Post(
                () =>
                {
                    if (!UseSystemThemeAccent || accentUpdateVersion != _accentUpdateVersion)
                    {
                        return;
                    }

                    UpdateCartonAccentBrush(app, ResolveCurrentAccentColor(app, normalizedAccent));
                },
                DispatcherPriority.Background);
        }
        else
        {
            var customColor = Color.Parse(normalizedAccent);
            if (fluent2Theme != null)
            {
                if (!fluent2Theme.Palettes.TryGetValue(ThemeVariant.Light, out var lightPalette) || lightPalette is not CartonAccentPalette)
                {
                    lightPalette = new CartonAccentPalette();
                    fluent2Theme.Palettes[ThemeVariant.Light] = lightPalette;
                }
                lightPalette.Accent = customColor;

                if (!fluent2Theme.Palettes.TryGetValue(ThemeVariant.Dark, out var darkPalette) || darkPalette is not CartonAccentPalette)
                {
                    darkPalette = new CartonAccentPalette();
                    fluent2Theme.Palettes[ThemeVariant.Dark] = darkPalette;
                }
                darkPalette.Accent = customColor;
            }

            // 自定义色:目标色已知,更新 Fluent2Theme.Palettes 及应用级画刷即可即时生效。
            UpdateCartonAccentBrush(app, customColor);
        }
    }

    private void EnsurePlatformSettingsHooked(Application app)
    {
        if (_platformSettingsHooked || app.PlatformSettings == null)
        {
            return;
        }

        app.PlatformSettings.ColorValuesChanged += OnPlatformColorValuesChanged;
        _platformSettingsHooked = true;
    }

    private void OnPlatformColorValuesChanged(object? sender, PlatformColorValues e)
    {
        if (!UseSystemThemeAccent)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (Application.Current is { } app && UseSystemThemeAccent)
            {
                UpdateCartonAccentBrush(app, ResolveCurrentAccentColor(app, ThemeAccent));
            }
        });
    }

    private static string NormalizeAccent(string? themeAccent)
    {
        if (!string.IsNullOrWhiteSpace(themeAccent) && Color.TryParse(themeAccent, out var color))
        {
            return FormatColor(color);
        }

        return "#FF0078D7";
    }

    private static void UpdateCartonAccentBrush(Application app, Color color)
    {
        SetOrUpdateBrush(app, "CartonAccentBrush", color);

        // 选中/展开态边框用弱化的强调色(70% alpha),需跟随当前强调色一起更新
        var borderColor = new Color(0xB3, color.R, color.G, color.B);
        SetOrUpdateBrush(app, "CartonAccentBorderBrush", borderColor);

        // 同步更新色阶画刷,使 Settings 预览色条即时生效
        var shades = CartonAccentPalette.CalculateShades(color);
        Color ResolveShade(string key, Color fallback) =>
            app.TryGetResource(key, app.ActualThemeVariant, out var resource) && TryConvertColor(resource, out var shade)
                ? shade
                : fallback;
        SetOrUpdateBrush(app, "CartonAccentLight1Brush", ResolveShade("SystemAccentColorLight1", shades.l1));
        SetOrUpdateBrush(app, "CartonAccentLight2Brush", ResolveShade("SystemAccentColorLight2", shades.l2));
        SetOrUpdateBrush(app, "CartonAccentLight3Brush", ResolveShade("SystemAccentColorLight3", shades.l3));
        SetOrUpdateBrush(app, "CartonAccentDark1Brush", ResolveShade("SystemAccentColorDark1", shades.d1));
        SetOrUpdateBrush(app, "CartonAccentDark2Brush", ResolveShade("SystemAccentColorDark2", shades.d2));
        SetOrUpdateBrush(app, "CartonAccentDark3Brush", ResolveShade("SystemAccentColorDark3", shades.d3));
    }

    private static void SetOrUpdateBrush(Application app, string key, Color color)
    {
        if (app.Resources.TryGetResource(key, null, out var resource) &&
            resource is SolidColorBrush brush)
        {
            brush.Color = color;
        }
        else
        {
            app.Resources[key] = new SolidColorBrush(color);
        }
    }

    private static Color ResolveCurrentAccentColor(Application app, string fallbackAccent)
    {
        if (app.PlatformSettings?.GetColorValues().AccentColor1 is { } platformAccent)
        {
            return platformAccent;
        }

        var variant = app.ActualThemeVariant;
        if (app.TryGetResource("SystemAccentColor", variant, out var accentResource) &&
            TryConvertColor(accentResource, out var resColor))
        {
            return resColor;
        }

        return Color.Parse(fallbackAccent);
    }

    private static bool TryConvertColor(object? resource, out Color color)
    {
        switch (resource)
        {
            case Color c:
                color = c;
                return true;
            case SolidColorBrush b:
                color = b.Color;
                return true;
            default:
                color = default;
                return false;
        }
    }

    private static string FormatColor(Color color)
        => $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
}
