using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using carton.Core.Services;
using carton.GUI.Services;
using Velopack;

namespace carton;

sealed class Program
{
    public static AppLaunchOptions LaunchOptions { get; private set; } = AppLaunchOptions.Default;

    [STAThread]
    public static void Main(string[] args)
    {
        GlobalExceptionHandler.Register();

        LaunchOptions = AppLaunchOptions.Parse(args);

        var velopackApp = VelopackApp.Build()
            .SetArgs(args);

        velopackApp.Run();

        if (WindowsElevatedHelperHost.TryRunFromArgs(args))
        {
            return;
        }

        const string instanceKey = "carton-app";
        if (!SingleInstanceService.TryClaim(instanceKey))
        {
            SingleInstanceService.NotifyExistingInstance();
            return;
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            SingleInstanceService.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            // Pin the default UI font to the embedded Inter family so Latin text
            // renders consistently across platforms. Without this, Linux lacks the
            // Windows fonts referenced in XAML and substitutes CJK faces whose Latin
            // glyphs look wrong; CJK text still falls back to the platform default.
            .With(new FontManagerOptions
            {
                DefaultFamilyName = "avares://Avalonia.Fonts.Inter/Assets#Inter",
                FontFallbacks = new[]
                {
                    new FontFallback { FontFamily = FontFamily.Default },
                },
            })
            .LogToTrace();
}
