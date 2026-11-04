using Avalonia.Media;
using carton.GUI.Services;
using Xunit;

namespace carton.GUI.Tests.Services;

public class CartonAccentPaletteTests
{
    // Captured from FluentAvaloniaUI 2.5.1: the 19 application presets plus black/white.
    [Theory]
    [InlineData("#FFB900", "#ffd99d00", "#ffb28200", "#ff8c6600", "#ffffc426", "#ffffce4c", "#ffffd973")]
    [InlineData("#FF8C00", "#ffd97700", "#ffb26200", "#ff8c4d00", "#ffff9d26", "#ffffae4c", "#ffffc073")]
    [InlineData("#DA3B01", "#ffb93201", "#ff992901", "#ff782001", "#fffb4401", "#fffe5a1f", "#fffe723f")]
    [InlineData("#D13438", "#ffb5292d", "#ff952225", "#ff751b1d", "#ffd85457", "#ffdf7477", "#ffe79496")]
    [InlineData("#E81123", "#ffc50e1e", "#ffa20c19", "#ff800913", "#fff02f3f", "#fff2515f", "#fff5747f")]
    [InlineData("#EA005E", "#ffc70050", "#ffa40042", "#ff810034", "#ffff0e6f", "#ffff3184", "#ffff5499")]
    [InlineData("#E3008C", "#ffc10077", "#ff9f0062", "#ff7d004d", "#ffff06a0", "#ffff28ad", "#ffff4aba")]
    [InlineData("#C239B3", "#ffa53098", "#ff88287d", "#ff6b1f62", "#ffcd54c0", "#ffd571ca", "#ffde8ed5")]
    [InlineData("#0078D4", "#ff0066b4", "#ff005494", "#ff004275", "#ff008af4", "#ff1599ff", "#ff34a7ff")]
    [InlineData("#0063B1", "#ff005496", "#ff00457c", "#ff003661", "#ff0072cc", "#ff0081e6", "#ff028fff")]
    [InlineData("#8E8CD8", "#ff6764ca", "#ff423fba", "#ff343292", "#ffb5b4e6", "#ffdcdcf3", "White")]
    [InlineData("#8764B8", "#ff714ca6", "#ff5d3e88", "#ff49316b", "#ff9e81c5", "#ffb49ed3", "#ffcbbce0")]
    [InlineData("#0099BC", "#ff0082a0", "#ff006b84", "#ff005467", "#ff00b0d8", "#ff00c7f4", "#ff12d3ff")]
    [InlineData("#00B7C3", "#ff009ca6", "#ff008088", "#ff00656b", "#ff00d2e0", "#ff00eefe", "#ff1cf1ff")]
    [InlineData("#00B294", "#ff00977e", "#ff007d68", "#ff006251", "#ff00cdaa", "#ff00e7c0", "#ff03ffd5")]
    [InlineData("#00CC6A", "#ff00ad5a", "#ff008f4a", "#ff00703a", "#ff00eb7a", "#ff0aff89", "#ff29ff98")]
    [InlineData("#10893E", "#ff0e7435", "#ff0b602b", "#ff094b22", "#ff129e47", "#ff15b251", "#ff17c75a")]
    [InlineData("#767676", "#ff646464", "#ff535353", "#ff414141", "#ff888888", "#ff999999", "#ffababab")]
    [InlineData("#68768A", "#ff586475", "#ff495361", "#ff39414c", "#ff7b889b", "#ff909bab", "#ffa4adbb")]
    [InlineData("#000000", "Black", "Black", "Black", "#ff262626", "#ff4c4c4c", "#ff737373")]
    [InlineData("#FFFFFF", "#ffd9d9d9", "#ffb2b2b2", "#ff8c8c8c", "White", "White", "White")]
    public void ShadesMatchPreUpgradeTheme(string accent, string d1, string d2, string d3, string l1, string l2, string l3)
    {
        var actual = CartonAccentPalette.CalculateShades(Color.Parse(accent));
        Assert.Equal(
            new[] { d1, d2, d3, l1, l2, l3 }.Select(Color.Parse),
            new[] { actual.d1, actual.d2, actual.d3, actual.l1, actual.l2, actual.l3 });
    }
}
