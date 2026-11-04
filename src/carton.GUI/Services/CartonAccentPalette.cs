using System;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent2;

namespace carton.GUI.Services;

/// <summary>Keeps Carton's accent shades consistent across Fluent2 theme updates.</summary>
internal sealed class CartonAccentPalette : ColorPaletteResources
{
    private Color _cachedAccent;
    private (Color d1, Color d2, Color d3, Color l1, Color l2, Color l3) _shades;

    public override bool TryGetResource(object key, ThemeVariant? theme, out object? value)
    {
        if (Accent == default)
        {
            return base.TryGetResource(key, theme, out value);
        }

        if (_cachedAccent != Accent)
        {
            _cachedAccent = Accent;
            _shades = CalculateShades(Accent);
        }

        value = key switch
        {
            "SystemAccentColorDark1" => _shades.d1,
            "SystemAccentColorDark2" => _shades.d2,
            "SystemAccentColorDark3" => _shades.d3,
            "SystemAccentColorLight1" => _shades.l1,
            "SystemAccentColorLight2" => _shades.l2,
            "SystemAccentColorLight3" => _shades.l3,
            _ => null
        };
        return value != null || base.TryGetResource(key, theme, out value);
    }

    internal static (Color d1, Color d2, Color d3, Color l1, Color l2, Color l3) CalculateShades(Color accent)
    {
        // Match FluentAvalonia Color2.LightenPercent, including its float arithmetic
        // and black-color fallback. Double-based HSL conversion changes some RGB bytes.
        var r = accent.R / 255f;
        var g = accent.G / 255f;
        var b = accent.B / 255f;
        var min = MathF.Min(r, MathF.Min(g, b));
        var max = MathF.Max(r, MathF.Max(g, b));
        var delta = max - min;
        var lightness = (max + min) * 0.5f;
        var saturation = 0f;
        var hue = 0f;
        if (delta > 0.001f)
        {
            saturation = lightness < 0.5f ? delta / (max + min) : delta / (2f - max - min);
            hue = MathF.Abs(r - max) < 0.001f ? (g - b) / delta
                : MathF.Abs(g - max) < 0.001f ? 2f + (b - r) / delta
                : 4f + (r - g) / delta;
            hue *= 60f;
            if (hue < 0f) hue += 360f;
            else if (hue >= 360f) hue -= 360f;
        }
        hue /= 360f;

        Color Shade(float percent)
        {
            var l = Math.Clamp(lightness < 0.001f ? percent : lightness + lightness * percent, 0f, 1f);
            var red = l;
            var green = l;
            var blue = l;
            if (saturation > 0.001f)
            {
                var high = l < 0.5f ? l * (1f + saturation) : l + saturation - saturation * l;
                var low = 2f * l - high;
                red = HueChannel(low, high, hue + 1f / 3f);
                green = HueChannel(low, high, hue);
                blue = HueChannel(low, high, hue - 1f / 3f);
            }
            return Color.FromArgb(accent.A, ToByte(red), ToByte(green), ToByte(blue));
        }
        return (Shade(-0.15f), Shade(-0.30f), Shade(-0.45f), Shade(0.15f), Shade(0.30f), Shade(0.45f));
    }

    private static byte ToByte(float channel) => (byte)Math.Round(channel * 255f);

    private static float HueChannel(float low, float high, float hue)
    {
        if (hue < 0f) hue++;
        if (hue > 1f) hue--;
        if (6f * hue < 1f) return low + (high - low) * 6f * hue;
        if (2f * hue < 1f) return high;
        if (3f * hue < 2f) return low + (high - low) * (2f / 3f - hue) * 6f;
        return low;
    }
}
