using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using carton.Core.Models;
using carton.Core.Services;
using carton.Core.Utilities;
using carton.GUI.Services;
using carton.ViewModels;
using carton.Views;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;

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
        HttpClientFactory.Initialize(CartonApplicationInfo.Version);
        var preferences = LoadOrCreatePreferences();
        LocalizationService.Instance.Initialize(preferences.Language);
        ThemeService.Instance.Initialize(preferences.Theme, preferences.UseSystemThemeAccent, preferences.ThemeAccent);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        GlobalExceptionHandler.RegisterDispatcher();

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
            if (Program.LaunchOptions.StartHidden)
            {
                mainWindow.StartHiddenToTray();
            }
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

        var reservedWidth = 0d;
        if (parameter is string parameterText &&
            double.TryParse(parameterText, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedReservedWidth) &&
            parsedReservedWidth > 0)
        {
            reservedWidth = parsedReservedWidth;
        }

        availableWidth = Math.Max(0, availableWidth - reservedWidth);

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

public class KernelDownloadMirrorDisplayConverter : Avalonia.Data.Converters.IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not DownloadMirror mirror)
        {
            return value?.ToString() ?? string.Empty;
        }

        var localization = LocalizationService.Instance;
        return mirror switch
        {
            DownloadMirror.GitHub => localization["Settings.Kernel.Mirror.GitHub"],
            DownloadMirror.GitHubPreRelease => localization["Settings.Kernel.Mirror.GitHubPreRelease"],
            DownloadMirror.GhProxy => localization["Settings.Kernel.Mirror.GhProxy"],
            DownloadMirror.GhProxyPreRelease => localization["Settings.Kernel.Mirror.GhProxyPreRelease"],
            DownloadMirror.Ref1ndStable => localization["Settings.Kernel.Mirror.Ref1ndStable"],
            DownloadMirror.Ref1ndTest => localization["Settings.Kernel.Mirror.Ref1ndTest"],
            _ => mirror.ToString()
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class KernelCacheCleanupPolicyDisplayConverter : Avalonia.Data.Converters.IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not KernelCacheCleanupPolicy policy)
        {
            return value?.ToString() ?? string.Empty;
        }

        var localization = LocalizationService.Instance;
        return policy switch
        {
            KernelCacheCleanupPolicy.ClearOnChannelChange => localization["Settings.Kernel.CacheCleanupPolicy.ClearOnChannelChange"],
            KernelCacheCleanupPolicy.Never => localization["Settings.Kernel.CacheCleanupPolicy.Never"],
            _ => policy.ToString()
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class GitHubUpdateCheckStrategyDisplayConverter : Avalonia.Data.Converters.IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not GitHubUpdateCheckStrategy strategy)
        {
            return value?.ToString() ?? string.Empty;
        }

        var localization = LocalizationService.Instance;
        return strategy switch
        {
            GitHubUpdateCheckStrategy.ApiOnly => localization["Settings.General.GitHubUpdateCheckStrategy.ApiOnly"],
            GitHubUpdateCheckStrategy.ApiThenAtom => localization["Settings.General.GitHubUpdateCheckStrategy.ApiThenAtom"],
            _ => strategy.ToString()
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
