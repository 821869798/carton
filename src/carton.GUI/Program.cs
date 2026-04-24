using System;
using Avalonia;
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
            .LogToTrace();
}
