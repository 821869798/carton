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
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
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

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "This Avalonia validation plugin removal path has been verified in published AOT builds.")]
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
                NavigationPage.Dashboard => "M12 3 L3 10 V21 H9 V15 H15 V21 H21 V10 Z",
                NavigationPage.Profiles => "M3 7.5 A1.5 1.5 0 0 1 4.5 6 H8.88 A1.5 1.5 0 0 1 9.94 6.44 L11.62 8.12 A1.5 1.5 0 0 0 12.68 8.56 H19.5 A1.5 1.5 0 0 1 21 10.06 V18.5 A1.5 1.5 0 0 1 19.5 20 H4.5 A1.5 1.5 0 0 1 3 18.5 Z",
                NavigationPage.Groups => "M8 4 A3 3 0 1 1 8 10 A3 3 0 1 1 8 4 Z M3 18 A5 5 0 0 1 13 18 V20 H3 Z M17 5.5 A2.5 2.5 0 1 1 17 10.5 A2.5 2.5 0 1 1 17 5.5 Z M13 18 A4 4 0 0 1 21 18 V20 H13 Z",
                NavigationPage.Connections => "M7 6 A3 3 0 1 1 7 12 A3 3 0 1 1 7 6 Z M17 12 A3 3 0 1 1 17 18 A3 3 0 1 1 17 12 Z M9.5 10.5 L10.5 9.5 L14.5 13.5 L13.5 14.5 Z",
                NavigationPage.Logs => "M6 3 H18 A2 2 0 0 1 20 5 V19 A2 2 0 0 1 18 21 H6 A2 2 0 0 1 4 19 V5 A2 2 0 0 1 6 3 Z M8 8 H16 V10 H8 Z M8 12 H16 V14 H8 Z M8 16 H13 V18 H8 Z",
                NavigationPage.Settings => "M14.25 3 A4.75 4.75 0 0 0 10.02 9.91 L3.44 16.5 A2.25 2.25 0 1 0 6.62 19.68 L13.21 13.09 A4.75 4.75 0 0 0 19.5 6.75 L16.25 10 L13 6.75 L16.25 3.5 A4.73 4.73 0 0 0 14.25 3 Z",
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

public class NavigationIndicatorMarginConverter : Avalonia.Data.Converters.IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double offset && !double.IsNaN(offset) && !double.IsInfinity(offset))
        {
            return new Thickness(0, offset, 0, 0);
        }

        return new Thickness(0);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class ResponsiveItemWidthConverter : Avalonia.Data.Converters.IValueConverter
{
    private const double MinCardWidth = 200;
    private const double CardGap = 8;
    private const int MinColumns = 2;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double availableWidth || double.IsNaN(availableWidth) || availableWidth <= 0)
        {
            return 220d;
        }

        var columns = Math.Max(
            MinColumns,
            (int)Math.Floor((availableWidth + CardGap) / (MinCardWidth + CardGap)));

        return Math.Floor((availableWidth - (columns - 1) * CardGap) / columns);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class LatencyForegroundConverter : Avalonia.Data.Converters.IValueConverter
{
    private static readonly IBrush LowLatencyBrush = new SolidColorBrush(Color.Parse("#16A34A"));
    private static readonly IBrush MediumLatencyBrush = new SolidColorBrush(Color.Parse("#CA8A04"));
    private static readonly IBrush HighLatencyBrush = new SolidColorBrush(Color.Parse("#DC2626"));
    private static readonly IBrush EmptyLatencyBrush = Brushes.Gray;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not int latency || latency <= 0)
        {
            return EmptyLatencyBrush;
        }

        if (latency < 400)
        {
            return LowLatencyBrush;
        }

        if (latency < 800)
        {
            return MediumLatencyBrush;
        }

        return HighLatencyBrush;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
