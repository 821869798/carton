using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using carton.Core.Models;
using carton.Core.Services;
using carton.GUI.Models;
using carton.GUI.Services;
using carton.ViewModels;
using carton.Views;
using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace carton;

public partial class App : Application
{
    private TrayMenuService? _trayMenuService;
    private static IPreferencesService? _preferencesService;

    public static IPreferencesService PreferencesService =>
        _preferencesService ?? throw new InvalidOperationException("Preferences service has not been initialized.");

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        var appVersion = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
            ?? "1.0";
        // strip build metadata (e.g. "+sha")
        var plusIndex = appVersion.IndexOf('+');
        if (plusIndex >= 0) appVersion = appVersion[..plusIndex];
        HttpClientFactory.Initialize(appVersion);
        var preferences = LoadOrCreatePreferences();
        LocalizationService.Instance.Initialize(preferences.Language);
        ThemeService.Instance.Initialize(preferences.Theme);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            DisableAvaloniaDataAnnotationValidation();

            var viewModel = new MainViewModel();
            var mainWindow = new MainWindow
            {
                DataContext = viewModel,
            };
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.MainWindow = mainWindow;
            SingleInstanceService.RegisterMainWindow(mainWindow);
            _trayMenuService = new TrayMenuService();
            _trayMenuService.Initialize(this, desktop, mainWindow, viewModel);
            desktop.Exit += OnDesktopExit;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return;
        }

        _trayMenuService?.Dispose();
        _trayMenuService = null;

        if (desktop.MainWindow?.DataContext is MainViewModel viewModel)
        {
            viewModel.ShutdownAsync().GetAwaiter().GetResult();
        }

        SingleInstanceService.Dispose();

        Environment.Exit(0);
    }

    private void DisableAvaloniaDataAnnotationValidation()
    {
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

        foreach (var plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }

    private static AppPreferences LoadOrCreatePreferences()
    {
        if (_preferencesService == null)
        {
            var workingDirectory = ResolveWorkingDirectory();
            _preferencesService = new PreferencesService(workingDirectory);
        }

        return _preferencesService.Load();
    }

    private static string ResolveWorkingDirectory()
    {
        var appDataPath = carton.Core.Utilities.PathHelper.GetAppDataPath();
        var workingDirectory = Path.Combine(appDataPath, "data");
        Directory.CreateDirectory(workingDirectory);
        return workingDirectory;
    }
}

public class ConnectionStatusConverter : Avalonia.Data.Converters.IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is bool isConnected)
        {
            return isConnected ? Brushes.Green : Brushes.Red;
        }
        return Brushes.Gray;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class NavigationIconConverter : Avalonia.Data.Converters.IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is NavigationPage page)
        {
            return page switch
            {
                NavigationPage.Dashboard => "M3 12l2-2m0 0l7-7 7 7M5 10v10a1 1 0 001 1h3m10-11l2 2m-2-2v10a1 1 0 01-1 1h-3m-6 0a1 1 0 001-1v-4a1 1 0 011-1h2a1 1 0 011 1v4a1 1 0 001 1m-6 0h6",
                NavigationPage.Profiles => "M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z M12 14l9-5-9-5-9 5 9 5z M12 14l6.16-3.422a12.083 12.083 0 01.665 6.479A11.952 11.952 0 0012 20.055a11.952 11.952 0 00-6.824-2.998 12.078 12.078 0 01.665-6.479L12 14z",
                NavigationPage.Groups => "M17 20h5v-2a3 3 0 00-5.356-1.857M17 20H7m10 0v-2c0-.656-.126-1.283-.356-1.857M7 20H2v-2a3 3 0 015.356-1.857M7 20v-2c0-.656.126-1.283.356-1.857m0 0a5.002 5.002 0 019.288 0M15 7a3 3 0 11-6 0 3 3 0 016 0zm6 3a2 2 0 11-4 0 2 2 0 014 0zM7 10a2 2 0 11-4 0 2 2 0 014 0z",
                NavigationPage.Connections => "M13.828 10.172a4 4 0 00-5.656 0l-4 4a4 4 0 105.656 5.656l1.102-1.101m-.758-4.899a4 4 0 005.656 0l4-4a4 4 0 00-5.656-5.656l-1.1 1.1",
                NavigationPage.Logs => "M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z",
                NavigationPage.Settings => "M10.325 4.317c.426-1.756 2.924-1.756 3.35 0a1.724 1.724 0 002.573 1.066c1.543-.94 3.31.826 2.37 2.37a1.724 1.724 0 001.065 2.572c1.756.426 1.756 2.924 0 3.35a1.724 1.724 0 00-1.066 2.573c.94 1.543-.826 3.31-2.37 2.37a1.724 1.724 0 00-2.572 1.065c-.426 1.756-2.924 1.756-3.35 0a1.724 1.724 0 00-2.573-1.066c-1.543.94-3.31-.826-2.37-2.37a1.724 1.724 0 00-1.065-2.572c-1.756-.426-1.756-2.924 0-3.35a1.724 1.724 0 001.066-2.573c-.94-1.543.826-3.31 2.37-2.37.996.608 2.296.07 2.572-1.065z M15 12a3 3 0 11-6 0 3 3 0 016 0z",
                _ => ""
            };
        }
        return "";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class NavigationTitleConverter : Avalonia.Data.Converters.IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is NavigationPage page)
        {
            return page.GetTitle();
        }
        return "";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
