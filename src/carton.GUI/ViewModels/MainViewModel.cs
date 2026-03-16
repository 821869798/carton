using Avalonia.Controls.ApplicationLifetimes;
using carton.Core.Models;
using carton.Core.Services;
using carton.Core.Utilities;
using carton.GUI.Models;
using carton.GUI.Services;
using carton.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace carton.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private static readonly TimeSpan TransientPageUnloadDelay = TimeSpan.FromMinutes(1);
    private readonly ISingBoxManager _singBoxManager;
    private readonly IProfileManager _profileManager;
    private readonly IConfigManager _configManager;
    private readonly IKernelManager _kernelManager;
    private readonly IPreferencesService _preferencesService;
    private readonly ILocalizationService _localizationService;
    private readonly IThemeService _themeService;
    private readonly IAppUpdateService _appUpdateService;
    private readonly LogStore _logStore;
    private readonly DispatcherTimer _transientPageUnloadTimer;
    private bool _isShuttingDown;
    private bool _autoStartOnLaunch;
    private bool _isWindowVisible = true;

    [ObservableProperty]
    private PageViewModelBase _currentPage;

    [ObservableProperty]
    private NavigationPage _selectedPage = NavigationPage.Dashboard;

    [ObservableProperty]
    private bool _isPaneOpen = true;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string _connectionStatus = string.Empty;

    [ObservableProperty]
    private string _uploadSpeed = "0 B/s";

    [ObservableProperty]
    private string _downloadSpeed = "0 B/s";

    [ObservableProperty]
    private string _currentProfileName = "No Profile";

    [ObservableProperty]
    private bool _isKernelInstalled;

    [ObservableProperty]
    private string _kernelStatus = string.Empty;

    [ObservableProperty]
    private bool _isDownloadingKernel;

    [ObservableProperty]
    private double _downloadProgress;

    [ObservableProperty]
    private string _downloadStatus = string.Empty;

    [ObservableProperty]
    private bool _showKernelDialog;

    [ObservableProperty]
    private string _latestVersion = string.Empty;

    public ObservableCollection<NavigationPage> NavigationPages { get; } = new()
    {
        NavigationPage.Dashboard,
        NavigationPage.Groups,
        NavigationPage.Profiles,
        NavigationPage.Connections,
        NavigationPage.Logs,
        NavigationPage.Settings
    };

    public DashboardViewModel DashboardViewModel { get; }

    private readonly Lazy<GroupsViewModel> _lazyGroupsViewModel;
    private ProfilesViewModel? _profilesViewModel;
    private ConnectionsViewModel? _connectionsViewModel;
    private LogsViewModel? _logsViewModel;
    private SettingsViewModel? _settingsViewModel;
    private GroupsViewModel? _activeGroupsViewModel;
    private DateTime? _profilesInactiveAtUtc;
    private DateTime? _connectionsInactiveAtUtc;
    private DateTime? _logsInactiveAtUtc;
    private DateTime? _settingsInactiveAtUtc;

    public GroupsViewModel? ActiveGroupsViewModel => _activeGroupsViewModel;
    public ILocalizationService Localization => _localizationService;

    public bool ShowGlobalStartStop => false;
    public bool ShowStartButton => false;
    public bool ShowStopButton => false;
    public bool IsDashboardPage => SelectedPage == NavigationPage.Dashboard;
    public bool IsProfilesPage => SelectedPage == NavigationPage.Profiles;
    public bool IsGroupsPage => SelectedPage == NavigationPage.Groups;
    public bool IsConnectionsPage => SelectedPage == NavigationPage.Connections;
    public bool IsLogsPage => SelectedPage == NavigationPage.Logs;
    public bool IsSettingsPage => SelectedPage == NavigationPage.Settings;
    public bool IsTransientPage => SelectedPage is NavigationPage.Profiles or NavigationPage.Connections or NavigationPage.Logs or NavigationPage.Settings;
    public PageViewModelBase? ActiveTransientPage => IsTransientPage ? CurrentPage : null;

    public MainViewModel()
    {
        var appDataPath = Path.Combine(carton.Core.Utilities.PathHelper.GetAppDataPath());

        var workingDirectory = Path.Combine(appDataPath, "data");
        _configManager = new ConfigManager(workingDirectory);
        _profileManager = new ProfileManager(workingDirectory, _configManager);
        _kernelManager = new KernelManager(appDataPath);
        _preferencesService = App.PreferencesService;
        _localizationService = LocalizationService.Instance;
        _localizationService.LanguageChanged += OnLanguageChanged;
        _themeService = ThemeService.Instance;

        _kernelManager.DownloadProgressChanged += OnDownloadProgress;
        _kernelManager.StatusChanged += OnKernelStatusChanged;

        var singBoxPath = _kernelManager.KernelPath;
        _singBoxManager = new SingBoxManager(singBoxPath, workingDirectory);

        _logStore = new LogStore();

        _singBoxManager.StatusChanged += OnStatusChanged;
        _singBoxManager.ManagerLogReceived += OnManagerLogReceived;
        _singBoxManager.LogReceived += OnLogReceived;

        DashboardViewModel = new DashboardViewModel(_singBoxManager, _kernelManager, _profileManager, _configManager, _logStore.AddLog);
        _lazyGroupsViewModel = new Lazy<GroupsViewModel>(() => new GroupsViewModel(_singBoxManager));
        _appUpdateService = new AppUpdateService("https://github.com/821869798/carton", null, _logStore.AddLog);
        _transientPageUnloadTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(30)
        };
        _transientPageUnloadTimer.Tick += OnTransientPageUnloadTimerTick;
        _transientPageUnloadTimer.Start();

        _currentPage = DashboardViewModel;
        _logStore.AddLog("[INFO] Log pipeline initialized");
        ConnectionStatus = _localizationService["Status.Disconnected"];

        _ = InitializeAsync();
    }

    private void OnLanguageChanged(object? sender, AppLanguage e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            for (var i = 0; i < NavigationPages.Count; i++)
            {
                var page = NavigationPages[i];
                NavigationPages[i] = page;
            }
        });
    }

    private async Task InitializeAsync()
    {
        var preferences = _preferencesService.Load();
        _localizationService.SetLanguage(preferences.Language);
        _themeService.ApplyTheme(preferences.Theme);
        _autoStartOnLaunch = preferences.AutoStartOnLaunch;

        var kernelInfo = await _kernelManager.GetInstalledKernelInfoAsync();
        IsKernelInstalled = kernelInfo != null;

        if (!IsKernelInstalled)
        {
            var latestVersion = await _kernelManager.GetLatestVersionAsync();
            LatestVersion = latestVersion ?? "unknown";
            ShowKernelDialog = true;
        }
        else
        {
            KernelStatus = $"sing-box {kernelInfo!.KernelVersion}";
        }

        await _singBoxManager.SyncRunningStateAsync();

        if (_autoStartOnLaunch && !_singBoxManager.IsRunning)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = TryAutoStartAsync());
        }

        var selectedId = await _profileManager.GetSelectedProfileIdAsync();
        if (selectedId > 0)
        {
            var profile = await _profileManager.GetAsync(selectedId);
            if (profile != null)
            {
                CurrentProfileName = profile.Name;
            }
        }
    }

    private void OnDownloadProgress(object? sender, DownloadProgress e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            DownloadProgress = e.Progress;
            DownloadStatus = $"{e.Status} {e.BytesReceived / 1024 / 1024:F1}MB / {e.TotalBytes / 1024 / 1024:F1}MB";
        });
    }

    private void OnKernelStatusChanged(object? sender, string status)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            KernelStatus = status;
        });
    }

    private void OnStatusChanged(object? sender, ServiceStatus status)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            IsConnected = status == ServiceStatus.Running;
            ConnectionStatus = status switch
            {
                ServiceStatus.Running => _localizationService["Status.Connected"],
                ServiceStatus.Starting => _localizationService["Status.Starting"],
                ServiceStatus.Stopping => _localizationService["Status.Stopping"],
                ServiceStatus.Error => _localizationService["Status.Error"],
                _ => _localizationService["Status.Disconnected"]
            };
            OnPropertyChanged(nameof(ShowStartButton));
            OnPropertyChanged(nameof(ShowStopButton));
            if (_connectionsViewModel != null)
            {
                _connectionsViewModel.OnServiceStatusChanged(status == ServiceStatus.Running);
            }
        });
    }

    private void OnLogReceived(object? sender, string log)
    {
        _logStore.AddLog(log, LogSource.SingBox);
    }

    private void OnManagerLogReceived(object? sender, string log)
    {
        _logStore.AddLog(log, LogSource.Carton);
    }

    partial void OnSelectedPageChanged(NavigationPage value)
    {
        var previousPage = CurrentPage;

        if (previousPage == _logsViewModel)
        {
            _logsViewModel?.OnNavigatedFrom();
        }
        else if (previousPage == _activeGroupsViewModel)
        {
            _activeGroupsViewModel?.OnNavigatedFrom();
        }
        else if (previousPage == _connectionsViewModel)
        {
            _connectionsViewModel?.OnNavigatedFrom();
        }
        else if (previousPage == DashboardViewModel)
        {
            DashboardViewModel.OnNavigatedFrom();
        }

        MarkTransientPageInactive(previousPage?.PageType);

        CurrentPage = value switch
        {
            NavigationPage.Dashboard => DashboardViewModel,
            NavigationPage.Profiles => EnsureProfilesViewModel(),
            NavigationPage.Groups => EnsureGroupsViewModel(),
            NavigationPage.Connections => EnsureConnectionsViewModel(),
            NavigationPage.Logs => EnsureLogsViewModel(),
            NavigationPage.Settings => EnsureSettingsViewModel(),
            _ => DashboardViewModel
        };

        MarkTransientPageActive(value);

        if (value == NavigationPage.Groups)
        {
            EnsureGroupsViewModel().OnNavigatedTo();
        }

        if (value == NavigationPage.Connections)
        {
            _connectionsViewModel?.OnNavigatedTo();
        }

        if (value == NavigationPage.Dashboard)
        {
            DashboardViewModel.OnNavigatedTo();
            _ = DashboardViewModel.LoadProfilesAsync();
        }

        if (value == NavigationPage.Logs)
        {
            _logsViewModel?.OnNavigatedTo();
        }

        OnPropertyChanged(nameof(ShowGlobalStartStop));
        OnPropertyChanged(nameof(ShowStartButton));
        OnPropertyChanged(nameof(ShowStopButton));
        OnPropertyChanged(nameof(IsDashboardPage));
        OnPropertyChanged(nameof(IsProfilesPage));
        OnPropertyChanged(nameof(IsGroupsPage));
        OnPropertyChanged(nameof(IsConnectionsPage));
        OnPropertyChanged(nameof(IsLogsPage));
        OnPropertyChanged(nameof(IsSettingsPage));
        OnPropertyChanged(nameof(IsTransientPage));
        OnPropertyChanged(nameof(ActiveTransientPage));
    }

    public void SetWindowVisible(bool isVisible)
    {
        if (_isWindowVisible == isVisible)
        {
            return;
        }

        _isWindowVisible = isVisible;
        DashboardViewModel.SetWindowVisible(isVisible);
        if (_activeGroupsViewModel != null)
        {
            _activeGroupsViewModel.SetWindowVisible(isVisible);
        }
        if (_connectionsViewModel != null)
        {
            _connectionsViewModel.SetWindowVisible(isVisible);
        }

        _logsViewModel?.SetWindowVisible(isVisible);
    }

    public bool IsGroupsViewModelCreated => _lazyGroupsViewModel.IsValueCreated;

    public GroupsViewModel EnsureGroupsViewModel()
    {
        var groupsViewModel = _lazyGroupsViewModel.Value;
        if (!ReferenceEquals(_activeGroupsViewModel, groupsViewModel))
        {
            _activeGroupsViewModel = groupsViewModel;
            OnPropertyChanged(nameof(ActiveGroupsViewModel));
        }

        groupsViewModel.SetWindowVisible(_isWindowVisible);

        return groupsViewModel;
    }

    public ProfilesViewModel EnsureProfilesViewModel()
    {
        _profilesInactiveAtUtc = null;
        _profilesViewModel ??= new ProfilesViewModel(_profileManager, _configManager, _singBoxManager);
        return _profilesViewModel;
    }

    public ConnectionsViewModel EnsureConnectionsViewModel()
    {
        _connectionsInactiveAtUtc = null;
        _connectionsViewModel ??= new ConnectionsViewModel(_singBoxManager);
        _connectionsViewModel.SetWindowVisible(_isWindowVisible);
        return _connectionsViewModel;
    }

    public LogsViewModel EnsureLogsViewModel()
    {
        _logsInactiveAtUtc = null;
        _logsViewModel ??= new LogsViewModel(_logStore);
        _logsViewModel.SetWindowVisible(_isWindowVisible);
        return _logsViewModel;
    }

    public SettingsViewModel EnsureSettingsViewModel()
    {
        _settingsInactiveAtUtc = null;
        _settingsViewModel ??= new SettingsViewModel(
            _configManager,
            _profileManager,
            _kernelManager,
            _preferencesService,
            _localizationService,
            _themeService,
            new StartupService(),
            _appUpdateService);
        return _settingsViewModel;
    }

    private void OnTransientPageUnloadTimerTick(object? sender, EventArgs e)
    {
        TryUnloadInactiveTransientPages();
    }

    private void TryUnloadInactiveTransientPages()
    {
        var now = DateTime.UtcNow;
        TryUnloadTransientPage(NavigationPage.Profiles, _profilesInactiveAtUtc, _profilesViewModel, disposable => _profilesViewModel = null, now);
        TryUnloadTransientPage(NavigationPage.Connections, _connectionsInactiveAtUtc, _connectionsViewModel, disposable => _connectionsViewModel = null, now);
        TryUnloadTransientPage(NavigationPage.Logs, _logsInactiveAtUtc, _logsViewModel, disposable => _logsViewModel = null, now);
        TryUnloadTransientPage(NavigationPage.Settings, _settingsInactiveAtUtc, _settingsViewModel, disposable => _settingsViewModel = null, now);
    }

    private void TryUnloadTransientPage(NavigationPage page, DateTime? inactiveAtUtc, IDisposable? viewModel, Action<IDisposable> clearReference, DateTime now)
    {
        if (SelectedPage == page || inactiveAtUtc == null || viewModel == null)
        {
            return;
        }

        if (now - inactiveAtUtc.Value < TransientPageUnloadDelay)
        {
            return;
        }

        clearReference(viewModel);
        viewModel.Dispose();
        if (CurrentPage.PageType == page)
        {
            OnPropertyChanged(nameof(ActiveTransientPage));
        }
    }

    private void MarkTransientPageInactive(NavigationPage? page)
    {
        var inactiveAt = DateTime.UtcNow;
        switch (page)
        {
            case NavigationPage.Profiles:
                _profilesInactiveAtUtc = inactiveAt;
                break;
            case NavigationPage.Connections:
                _connectionsInactiveAtUtc = inactiveAt;
                break;
            case NavigationPage.Logs:
                _logsInactiveAtUtc = inactiveAt;
                break;
            case NavigationPage.Settings:
                _settingsInactiveAtUtc = inactiveAt;
                break;
        }
    }

    private void MarkTransientPageActive(NavigationPage page)
    {
        switch (page)
        {
            case NavigationPage.Profiles:
                _profilesInactiveAtUtc = null;
                break;
            case NavigationPage.Connections:
                _connectionsInactiveAtUtc = null;
                break;
            case NavigationPage.Logs:
                _logsInactiveAtUtc = null;
                break;
            case NavigationPage.Settings:
                _settingsInactiveAtUtc = null;
                break;
        }
    }

    [RelayCommand]
    private void TogglePane()
    {
        IsPaneOpen = !IsPaneOpen;
    }

    [RelayCommand]
    private async Task ToggleConnection()
    {
        if (!IsKernelInstalled)
        {
            var latestVersion = await _kernelManager.GetLatestVersionAsync();
            LatestVersion = latestVersion ?? "unknown";
            ShowKernelDialog = true;
            return;
        }

        if (_singBoxManager.IsRunning)
        {
            await _singBoxManager.StopAsync();
        }
        else
        {
            var selectedId = await _profileManager.GetSelectedProfileIdAsync();
            string configPath;

            if (selectedId > 0)
            {
                var profile = await _profileManager.GetAsync(selectedId);
                if (profile == null)
                {
                    _logStore.AddLog("[ERROR] Selected profile not found");
                    ConnectionStatus = _localizationService["Status.ConfigMissing"];
                    return;
                }

                configPath = await EnsureProfileConfigPathForStartAsync(profile) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(configPath))
                {
                    ConnectionStatus = profile.Type == ProfileType.Remote
                        ? GetString("Status.RemoteConfigUnavailable", "Remote config unavailable")
                        : _localizationService["Status.ConfigMissing"];
                    return;
                }

                if (!File.Exists(configPath))
                {
                    _logStore.AddLog("[ERROR] Selected profile config file not found");
                    ConnectionStatus = _localizationService["Status.ConfigMissing"];
                    return;
                }
            }
            else
            {
                _logStore.AddLog("[INFO] No profile selected, please select a profile first");
                SelectedPage = NavigationPage.Profiles;
                return;
            }

            var success = await _singBoxManager.StartAsync(configPath);
            if (!success)
            {
                ConnectionStatus = _localizationService["Status.FailedStart"];
            }
        }
    }

    private async Task<string?> EnsureProfileConfigPathForStartAsync(Profile profile)
    {
        var configPath = await _configManager.GetConfigPathAsync(profile.Id, profile.Type);
        if (!string.IsNullOrWhiteSpace(configPath) && File.Exists(configPath))
        {
            return configPath;
        }

        if (profile.Type != ProfileType.Remote)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(profile.Url))
        {
            var message = GetString("Status.RemoteProfileUrlEmpty", "Remote profile URL is empty");
            _logStore.AddLog($"[ERROR] {message}: {profile.Name} ({profile.Id})");
            ConnectionStatus = message;
            return null;
        }

        var downloadingMessage = GetString("Status.RemoteConfigDownloading", "Remote config missing, downloading...");
        ConnectionStatus = downloadingMessage;
        _logStore.AddLog($"[INFO] {downloadingMessage}: {profile.Name} ({profile.Id})");
        try
        {
            var client = HttpClientFactory.External;
            var content = await client.GetStringAsync(profile.Url);
            if (string.IsNullOrWhiteSpace(content))
            {
                var message = GetString("Status.RemoteConfigEmpty", "Downloaded remote config is empty");
                _logStore.AddLog($"[ERROR] {message}: {profile.Name} ({profile.Id})");
                ConnectionStatus = message;
                return null;
            }

            await _configManager.SaveConfigAsync(profile.Id, content, ProfileType.Remote);
            var downloadedPath = await _configManager.GetConfigPathAsync(profile.Id, ProfileType.Remote);
            if (string.IsNullOrWhiteSpace(downloadedPath) || !File.Exists(downloadedPath))
            {
                var message = GetString("Status.RemoteConfigFileMissing", "Remote config download succeeded but file missing");
                _logStore.AddLog($"[ERROR] {message}: {profile.Name} ({profile.Id})");
                ConnectionStatus = message;
                return null;
            }

            var downloadedMessage = GetString("Status.RemoteConfigDownloaded", "Remote config downloaded");
            _logStore.AddLog($"[INFO] {downloadedMessage}: {profile.Name} ({profile.Id})");
            ConnectionStatus = downloadedMessage;
            return downloadedPath;
        }
        catch (Exception ex)
        {
            var message = GetString("Status.RemoteConfigDownloadFailed", "Failed to download remote config");
            _logStore.AddLog($"[ERROR] {message}: {profile.Name} ({profile.Id}) - {ex.Message}");
            ConnectionStatus = $"{message}: {ex.Message}";
            return null;
        }
    }

    private string GetString(string key, string fallback)
    {
        var value = _localizationService.GetString(key);
        return string.Equals(value, key, StringComparison.Ordinal) ? fallback : value;
    }

    [RelayCommand]
    private async Task DownloadKernel()
    {
        IsDownloadingKernel = true;
        DownloadStatus = _localizationService["Status.KernelDownloading"];

        var success = await _kernelManager.DownloadAndInstallAsync();

        IsDownloadingKernel = false;

        if (success)
        {
            IsKernelInstalled = true;
            ShowKernelDialog = false;
            var kernelInfo = await _kernelManager.GetInstalledKernelInfoAsync();
            KernelStatus = $"sing-box {kernelInfo?.KernelVersion ?? "installed"}";
        }
    }

    [RelayCommand]
    private void CloseKernelDialog()
    {
        ShowKernelDialog = false;
    }

    [RelayCommand]
    private void Quit()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            ShutdownAsync().GetAwaiter().GetResult();
            if (desktop.MainWindow is MainWindow window)
            {
                window.AllowClose();
            }
            desktop.Shutdown();
        }
    }

    public async Task ShutdownAsync()
    {
        if (_isShuttingDown)
        {
            return;
        }

        _isShuttingDown = true;
        _transientPageUnloadTimer.Stop();
        _transientPageUnloadTimer.Tick -= OnTransientPageUnloadTimerTick;
        try
        {
            await _singBoxManager.StopAsync();
        }
        catch
        {
            // Best-effort shutdown path.
        }
        finally
        {
            _logsViewModel?.Dispose();
            _logsViewModel = null;
            _profilesViewModel?.Dispose();
            _profilesViewModel = null;
            _connectionsViewModel?.Dispose();
            _connectionsViewModel = null;
            _settingsViewModel?.Dispose();
            _settingsViewModel = null;

            if (_singBoxManager is IDisposable disposable)
            {
                try
                {
                    disposable.Dispose();
                }
                catch
                {
                    // Best-effort shutdown path.
                }
            }
        }
    }

    private static string FormatBytes(long bytes) => FormatHelper.FormatBytes(bytes);

    private async Task TryAutoStartAsync()
    {
        try
        {
            if (DashboardViewModel.AvailableProfiles.Count == 0)
            {
                await DashboardViewModel.LoadProfilesAsync();
            }

            var command = DashboardViewModel.StartWithSelectedProfileCommand;
            if (command != null)
            {
                await command.ExecuteAsync(null);
            }
        }
        catch (Exception ex)
        {
            _logStore.AddLog($"[ERROR] Failed to auto start: {ex.Message}");
        }
    }
}
