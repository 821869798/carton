using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Fonts;
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
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            // Inter has no CJK. FontFamily.Default is Inter itself here, so every
            // dashboard TextBlock (标题 / 系统代理 / 虚拟网卡 / 仅本机…) needs a real CJK family
            // or Skia substitutes Yu Gothic / a synthetic bold. Sidebar looks fine
            // because Fluent pins Segoe, which already falls back to YaHei.
            .With(new FontManagerOptions
            {
                DefaultFamilyName = "avares://Avalonia.Fonts.Inter/Assets#Inter",
                FontFallbacks = CjkFallbacks(),
            });

#if DEBUG
        // LogToTrace installs a global Avalonia log sink that formats every framework
        // diagnostic into strings. In release that is pure allocation for output nobody
        // reads, so it is debug-only.
        builder = builder.LogToTrace();
#endif

        return builder;
    }

    private static FontFallback[] CjkFallbacks()
    {
        // One FontFallback per family: a comma list is not a reliable cascade.
        // Han + kana + fullwidth only. Hangul is left to the system (Malgun Gothic
        // on Windows) instead of failing through these SC faces first.
        var cjk = UnicodeRange.Parse(
            "2E80-A4CF,F900-FAFF,FE10-FE1F,FE30-FE4F,FF00-FFEF,20000-2FA1F");
        string[] families = OperatingSystem.IsWindows()
            ? ["Microsoft YaHei UI", "Microsoft YaHei"]
            : OperatingSystem.IsMacOS()
                ? ["PingFang SC", "Hiragino Sans GB"]
                :
                [
                    "Noto Sans CJK SC",
                    "Noto Sans SC",
                    "Source Han Sans SC",
                    "Source Han Sans CN",
                    "WenQuanYi Micro Hei",
                    "WenQuanYi Zen Hei",
                    "Droid Sans Fallback",
                ];

        return Array.ConvertAll(families, family => new FontFallback
        {
            FontFamily = new FontFamily(family),
            UnicodeRange = cjk,
        });
    }
}
