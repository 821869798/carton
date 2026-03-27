using Avalonia;
using System;
using carton.Core.Services;
using Velopack;
using carton.GUI.Services;

namespace carton;

sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build()
            .SetArgs(args)
            .Run();

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
