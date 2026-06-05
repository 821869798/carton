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
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Threading;
using Avalonia;

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
    private readonly AppUpdateCoordinator _appUpdateCoordinator;
    private readonly LogStore _logStore;
    private readonly DispatcherTimer _transientPageUnloadTimer;
    private readonly DispatcherTimer _sessionDurationTimer;
    private AppPreferences _currentPreferences = new();
    private bool _isShuttingDown;
    private bool _autoStartOnLaunch;
    private bool _isWindowVisible = true;
    private bool _isSessionDurationRefreshActive;
    private bool _suppressPreferenceUpdates;
    private DownloadMirror? _latestKernelVersionMirror;
    private int _sessionStartTimeMeasureHourDigits = 2;
    private bool _isInteractionBlocked;
    private string _interactionBlockedMessage = string.Empty;

    [ObservableProperty]
    private PageViewModelBase _currentPage;

    [ObservableProperty]
    private NavigationPage _selectedPage = NavigationPage.Dashboard;

    [ObservableProperty]
    private bool _isPaneOpen = true;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private ServiceStatus _serviceStatus = ServiceStatus.Stopped;

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

    [ObservableProperty]
    private DownloadMirror _selectedKernelDownloadMirror = DownloadMirror.GitHub;

    [ObservableProperty]
    private bool _hasKernelDownloadFailed;

    [ObservableProperty]
    private string _sessionStartTimeText = "--";

    [ObservableProperty]
    private string _sessionStartTimeMeasureText = "88:88:88";

    public ObservableCollection<ToastNotificationViewModel> Toasts { get; } = new();
    public ObservableCollection<NavigationItem> NavigationItems { get; }

    public MainViewModel? KernelDialogHost => ShowKernelDialog ? this : null;
    public AppUpdateCoordinator AppUpdate => _appUpdateCoordinator;
    public AppUpdateCoordinator? AppUpdateDialogHost => !ShowKernelDialog && AppUpdate.ShowStartupUpdateDialog ? AppUpdate : null;
    public bool IsInteractionBlocked => _isInteractionBlocked;
    public string InteractionBlockedMessage => _interactionBlockedMessage;

    partial void OnShowKernelDialogChanged(bool value)
    {
        OnPropertyChanged(nameof(KernelDialogHost));
        OnPropertyChanged(nameof(AppUpdateDialogHost));
    }

    partial void OnSelectedKernelDownloadMirrorChanged(DownloadMirror value)
    {
        if (_suppressPreferenceUpdates)
        {
            return;
        }

        _latestKernelVersionMirror = null;
        _currentPreferences.KernelDownloadMirror = value;
        _preferencesService.Save(_currentPreferences);
        _ = RefreshLatestKernelVersionAsync();
    }

    public ObservableCollection<DownloadMirror> KernelDownloadMirrors { get; } = new(
    [
        DownloadMirror.GitHub,
        DownloadMirror.GitHubPreRelease,
        DownloadMirror.GhProxy,
        DownloadMirror.GhProxyPreRelease,
        DownloadMirror.Ref1ndStable,
        DownloadMirror.Ref1ndTest
    ]);
    public string KernelPrimaryActionText => HasKernelDownloadFailed ? GetLocalizedRetryLabel() : _localizationService["MainWindow.KernelDialog.Button.Download"];

    public DashboardViewModel DashboardViewModel { get; }

    private readonly Lazy<GroupsViewModel> _lazyGroupsViewModel;
    private ProfilesViewModel? _profilesViewModel;
    private ConnectionsViewModel? _connectionsViewModel;
    private LogsViewModel? _logsViewModel;
    private SettingsViewModel? _settingsViewModel;
    private GroupsViewModel? _activeGroupsViewModel;
    private DateTime? _groupsInactiveAtUtc;
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
        _preferencesService = App.PreferencesService;
        var githubUpdateCheckStrategyProvider = new PreferencesGitHubUpdateCheckStrategyProvider(_preferencesService);
        _kernelManager = new KernelManager(appDataPath, githubUpdateCheckStrategyProvider);
        _localizationService = LocalizationService.Instance;
        _localizationService.LanguageChanged += OnLanguageChanged;
        NavigationItems = CreateNavigationItems();
        RefreshNavigationItemTitles();
        _themeService = ThemeService.Instance;

        _kernelManager.DownloadProgressChanged += OnDownloadProgress;
        _kernelManager.StatusChanged += OnKernelStatusChanged;
        _kernelManager.InstalledKernelChanged += OnInstalledKernelChanged;

        var singBoxPath = _kernelManager.KernelPath;
        _singBoxManager = new SingBoxManager(singBoxPath, workingDirectory);

        _logStore = new LogStore();

        _singBoxManager.StatusChanged += OnStatusChanged;
        _singBoxManager.ManagerLogReceived += OnManagerLogReceived;
        _singBoxManager.LogReceived += OnLogReceived;

        DashboardViewModel = new DashboardViewModel(_singBoxManager, _kernelManager, _profileManager, _configManager, _preferencesService, ShowToast, _logStore.AddLog);
        _lazyGroupsViewModel = new Lazy<GroupsViewModel>(() => new GroupsViewModel(_singBoxManager, _preferencesService));
        _appUpdateService = new AppUpdateService("https://github.com/821869798/carton", null, _logStore.AddLog, githubUpdateCheckStrategyProvider);
        _appUpdateCoordinator = new AppUpdateCoordinator(_appUpdateService, _localizationService);
        _appUpdateCoordinator.PropertyChanged += OnAppUpdateCoordinatorPropertyChanged;
        _transientPageUnloadTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(30)
        };
        _transientPageUnloadTimer.Tick += OnTransientPageUnloadTimerTick;
        _transientPageUnloadTimer.Start();
        _sessionDurationTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _sessionDurationTimer.Tick += (_, _) => UpdateSessionStartTime();

        _currentPage = DashboardViewModel;
        _logStore.AddLog("[INFO] Log pipeline initialized");
        ConnectionStatus = _localizationService["Status.Disconnected"];

        _ = InitializeAsync();
    }

    private void OnLanguageChanged(object? sender, AppLanguage e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            RefreshNavigationItemTitles();
            OnPropertyChanged(nameof(KernelPrimaryActionText));
            _appUpdateCoordinator.RefreshLocalizedTexts();
        });
    }

    private static ObservableCollection<NavigationItem> CreateNavigationItems()
    {
        return new ObservableCollection<NavigationItem>
        {
            new(NavigationPage.Dashboard, "Navigation.Dashboard", "Dashboard", "M12 3 L3 10 V21 H9 V15 H15 V21 H21 V10 Z"),
            new(NavigationPage.Groups, "Navigation.Groups", "Groups", "M8 4 A3 3 0 1 1 8 10 A3 3 0 1 1 8 4 Z M3 18 A5 5 0 0 1 13 18 V20 H3 Z M17 5.5 A2.5 2.5 0 1 1 17 10.5 A2.5 2.5 0 1 1 17 5.5 Z M13 18 A4 4 0 0 1 21 18 V20 H13 Z"),
            new(NavigationPage.Profiles, "Navigation.Profiles", "Profiles", "M3 7.5 A1.5 1.5 0 0 1 4.5 6 H8.88 A1.5 1.5 0 0 1 9.94 6.44 L11.62 8.12 A1.5 1.5 0 0 0 12.68 8.56 H19.5 A1.5 1.5 0 0 1 21 10.06 V18.5 A1.5 1.5 0 0 1 19.5 20 H4.5 A1.5 1.5 0 0 1 3 18.5 Z"),
            new(NavigationPage.Connections, "Navigation.Connections", "Connections", "M7 6 A3 3 0 1 1 7 12 A3 3 0 1 1 7 6 Z M17 12 A3 3 0 1 1 17 18 A3 3 0 1 1 17 12 Z M9.5 10.5 L10.5 9.5 L14.5 13.5 L13.5 14.5 Z"),
            new(NavigationPage.Logs, "Navigation.Logs", "Logs", "M6 3 H18 A2 2 0 0 1 20 5 V19 A2 2 0 0 1 18 21 H6 A2 2 0 0 1 4 19 V5 A2 2 0 0 1 6 3 Z M8 8 H16 V10 H8 Z M8 12 H16 V14 H8 Z M8 16 H13 V18 H8 Z"),
            new(NavigationPage.Settings, "Navigation.Settings", "Settings", "M10.71 2.98 C10.91 2.39 11.42 2 12 2 C12.58 2 13.09 2.39 13.29 2.98 L13.6 3.93 C13.91 4.02 14.2 4.15 14.47 4.31 L15.39 3.9 C15.96 3.65 16.63 3.77 17.08 4.22 C17.53 4.67 17.65 5.34 17.4 5.91 L16.99 6.83 C17.15 7.1 17.28 7.39 17.37 7.7 L18.32 8.01 C18.91 8.21 19.3 8.72 19.3 9.3 V10.7 C19.3 11.28 18.91 11.79 18.32 11.99 L17.37 12.3 C17.28 12.61 17.15 12.9 16.99 13.17 L17.4 14.09 C17.65 14.66 17.53 15.33 17.08 15.78 C16.63 16.23 15.96 16.35 15.39 16.1 L14.47 15.69 C14.2 15.85 13.91 15.98 13.6 16.07 L13.29 17.02 C13.09 17.61 12.58 18 12 18 C11.42 18 10.91 17.61 10.71 17.02 L10.4 16.07 C10.09 15.98 9.8 15.85 9.53 15.69 L8.61 16.1 C8.04 16.35 7.37 16.23 6.92 15.78 C6.47 15.33 6.35 14.66 6.6 14.09 L7.01 13.17 C6.85 12.9 6.72 12.61 6.63 12.3 L5.68 11.99 C5.09 11.79 4.7 11.28 4.7 10.7 V9.3 C4.7 8.72 5.09 8.21 5.68 8.01 L6.63 7.7 C6.72 7.39 6.85 7.1 7.01 6.83 L6.6 5.91 C6.35 5.34 6.47 4.67 6.92 4.22 C7.37 3.77 8.04 3.65 8.61 3.9 L9.53 4.31 C9.8 4.15 10.09 4.02 10.4 3.93 Z M12 7.5 A2.5 2.5 0 1 0 12 12.5 A2.5 2.5 0 1 0 12 7.5 Z")
        };
    }

    private void RefreshNavigationItemTitles()
    {
        foreach (var item in NavigationItems)
        {
            var title = _localizationService.GetString(item.TitleResourceKey);
            item.Title = string.Equals(title, item.TitleResourceKey, StringComparison.Ordinal)
                ? item.FallbackTitle
                : title;
        }
    }

    private async Task InitializeAsync()
    {
        _currentPreferences = _preferencesService.Load();
        _suppressPreferenceUpdates = true;
        SelectedKernelDownloadMirror = _currentPreferences.KernelDownloadMirror;
        _suppressPreferenceUpdates = false;
        _appUpdateCoordinator.Configure(UpdateChannelToString(_currentPreferences.UpdateChannel));

        _localizationService.SetLanguage(_currentPreferences.Language);
        RefreshNavigationItemTitles();
        _themeService.ApplyTheme(_currentPreferences.Theme);
        _themeService.ApplyAccent(_currentPreferences.UseSystemThemeAccent, _currentPreferences.ThemeAccent);
        _autoStartOnLaunch = _currentPreferences.AutoStartOnLaunch;

        var kernelInfo = await _kernelManager.GetInstalledKernelInfoAsync();
        IsKernelInstalled = kernelInfo != null;

        if (!IsKernelInstalled)
        {
            var mirror = SelectedKernelDownloadMirror;
            var latestVersion = await _kernelManager.GetLatestVersionAsync(mirror);
            ApplyLatestKernelVersion(mirror, latestVersion);
            ShowKernelDialog = true;
        }
        else
        {
            ApplyInstalledKernelInfo(kernelInfo);
        }

        await _singBoxManager.SyncRunningStateAsync();
        Dispatcher.UIThread.Post(UpdateSessionDurationRefreshState);
        await RecoverStaleSystemProxyAsync();

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

        _ = _appUpdateCoordinator.RunStartupCheckAsync(_currentPreferences.AutoCheckAppUpdates);
    }

    private async Task RecoverStaleSystemProxyAsync()
    {
        if (_singBoxManager.IsRunning)
        {
            return;
        }

        try
        {
            var selectedId = await _profileManager.GetSelectedProfileIdAsync();
            if (selectedId <= 0)
            {
                return;
            }

            var runtimeOptions = await _profileManager.GetRuntimeOptionsAsync(selectedId);
            var port = runtimeOptions.InboundPort is >= 1 and <= 65535
                ? runtimeOptions.InboundPort
                : 2028;

            if (SystemProxyHelper.TryRecoverStaleSystemProxy(port))
            {
                _logStore.AddLog($"[INFO] Cleared stale system proxy left by a previous carton session on port {port}");
            }
        }
        catch (Exception ex)
        {
            _logStore.AddLog($"[WARN] Failed to recover stale system proxy: {ex.Message}");
        }
    }

    private void OnDownloadProgress(object? sender, DownloadProgress e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            HasKernelDownloadFailed = false;
            OnPropertyChanged(nameof(KernelPrimaryActionText));
            DownloadProgress = e.Progress;
            DownloadStatus = DownloadUiHelper.FormatStatus(
                e.Status,
                e.BytesReceived,
                e.TotalBytes,
                GetString("Common.Unknown", "unknown"));
        });
    }

    private void OnKernelStatusChanged(object? sender, string status)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            KernelStatus = status;
            DownloadStatus = status;
        });
    }

    private void OnInstalledKernelChanged(object? sender, KernelInfo? kernelInfo)
    {
        Dispatcher.UIThread.Post(() => ApplyInstalledKernelInfo(kernelInfo));
    }

    private void OnStatusChanged(object? sender, ServiceStatus status)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            ServiceStatus = status;
            IsConnected = status == ServiceStatus.Running;
            ConnectionStatus = status switch
            {
                ServiceStatus.Running => _localizationService["Status.Connected"],
                ServiceStatus.Starting => _localizationService["Status.Starting"],
                ServiceStatus.Stopping => _localizationService["Status.Stopping"],
                ServiceStatus.Error => _localizationService["Status.Error"],
                _ => _localizationService["Status.Disconnected"]
            };
            if (status == ServiceStatus.Running)
            {
                UpdateSessionStartTime();
            }
            else
            {
                ResetSessionStartTimeDisplay();
            }

            UpdateSessionDurationRefreshState();
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
        UpdateSessionDurationRefreshState();
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
        _profilesViewModel ??= new ProfilesViewModel(
            _profileManager,
            _configManager,
            _singBoxManager,
            _preferencesService,
            DashboardViewModel.LoadProfilesAsync,
            ShowToast);
        return _profilesViewModel;
    }

    public void ShowToast(string message, int durationMilliseconds = 2400)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        _ = ShowToastAsync(message, durationMilliseconds);
    }

    private async Task ShowToastAsync(string message, int durationMilliseconds)
    {
        ToastNotificationViewModel? toast = null;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            while (Toasts.Count >= 3)
            {
                Toasts.RemoveAt(0);
            }

            toast = new ToastNotificationViewModel
            {
                Message = message,
                Opacity = 0,
                OffsetMargin = new Thickness(0, 18, 0, -18)
            };
            Toasts.Add(toast);
        });

        if (toast == null)
        {
            return;
        }

        await Task.Delay(16);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            toast.Opacity = 1;
            toast.OffsetMargin = new Thickness(0);
        });

        await Task.Delay(durationMilliseconds);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            toast.Opacity = 0;
            toast.OffsetMargin = new Thickness(0, -18, 0, 18);
        });

        await Task.Delay(220);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Toasts.Remove(toast);
        });
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
        if (_settingsViewModel == null)
        {
            _settingsViewModel = new SettingsViewModel(
                _configManager,
                _profileManager,
                _kernelManager,
                _singBoxManager,
                _preferencesService,
                _localizationService,
                _themeService,
                new StartupService(),
                _appUpdateCoordinator,
                ShowToast);
            _settingsViewModel.PropertyChanged += OnSettingsViewModelPropertyChanged;
            UpdateInteractionBlockState();
        }

        return _settingsViewModel;
    }

    private void OnSettingsViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.PropertyName) ||
            e.PropertyName == nameof(SettingsViewModel.IsBlockingUi) ||
            e.PropertyName == nameof(SettingsViewModel.BlockingUiMessage))
        {
            UpdateInteractionBlockState();
        }
    }

    private void UpdateInteractionBlockState()
    {
        if (_appUpdateCoordinator.IsApplyingAppUpdate)
        {
            _isInteractionBlocked = true;
            _interactionBlockedMessage = _appUpdateCoordinator.AppUpdateStatus;
        }
        else
        {
            _isInteractionBlocked = _settingsViewModel?.IsBlockingUi == true;
            _interactionBlockedMessage = _settingsViewModel?.BlockingUiMessage ?? string.Empty;
        }

        OnPropertyChanged(nameof(IsInteractionBlocked));
        OnPropertyChanged(nameof(InteractionBlockedMessage));
    }

    private void OnAppUpdateCoordinatorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.PropertyName) ||
            e.PropertyName == nameof(AppUpdateCoordinator.ShowStartupUpdateDialog))
        {
            OnPropertyChanged(nameof(AppUpdateDialogHost));
        }

        if (string.IsNullOrWhiteSpace(e.PropertyName) ||
            e.PropertyName == nameof(AppUpdateCoordinator.IsApplyingAppUpdate) ||
            e.PropertyName == nameof(AppUpdateCoordinator.AppUpdateStatus))
        {
            UpdateInteractionBlockState();
        }
    }

    private void OnTransientPageUnloadTimerTick(object? sender, EventArgs e)
    {
        TryUnloadInactiveTransientPages();
    }

    private void UpdateSessionStartTime()
    {
        var startTime = _singBoxManager.State.StartTime;
        if (!startTime.HasValue)
        {
            ResetSessionStartTimeDisplay();
            return;
        }

        var elapsed = DateTime.Now - startTime.Value;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        var totalHours = (int)elapsed.TotalHours;
        SessionStartTimeText = $"{totalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        UpdateSessionStartTimeMeasureText(totalHours);
    }

    private void ResetSessionStartTimeDisplay()
    {
        SessionStartTimeText = "--";
        ResetSessionStartTimeMeasureText();
    }

    private void ResetSessionStartTimeMeasureText()
    {
        if (_sessionStartTimeMeasureHourDigits == 2)
        {
            return;
        }

        _sessionStartTimeMeasureHourDigits = 2;
        SessionStartTimeMeasureText = "88:88:88";
    }

    private void UpdateSessionStartTimeMeasureText(int totalHours)
    {
        var hourDigits = GetHourDigitCount(totalHours);
        if (hourDigits == _sessionStartTimeMeasureHourDigits)
        {
            return;
        }

        _sessionStartTimeMeasureHourDigits = hourDigits;
        SessionStartTimeMeasureText = $"{new string('8', hourDigits)}:88:88";
    }

    private static int GetHourDigitCount(int totalHours)
    {
        if (totalHours < 100)
        {
            return 2;
        }

        var digits = 0;
        do
        {
            digits++;
            totalHours /= 10;
        }
        while (totalHours > 0);

        return digits;
    }

    private void StartSessionDurationTimer()
    {
        if (!_sessionDurationTimer.IsEnabled)
        {
            _sessionDurationTimer.Start();
        }
    }

    private void StopSessionDurationTimer()
    {
        if (_sessionDurationTimer.IsEnabled)
        {
            _sessionDurationTimer.Stop();
        }
    }

    private void UpdateSessionDurationRefreshState()
    {
        var shouldRefresh = _singBoxManager is { IsRunning: true } && _isWindowVisible;
        if (_isSessionDurationRefreshActive == shouldRefresh)
        {
            return;
        }

        _isSessionDurationRefreshActive = shouldRefresh;
        if (shouldRefresh)
        {
            UpdateSessionStartTime();
            StartSessionDurationTimer();
            return;
        }

        StopSessionDurationTimer();
    }

    private void TryUnloadInactiveTransientPages()
    {
        var now = DateTime.UtcNow;
        TryUnloadGroupsPage(now);
        TryUnloadTransientPage(NavigationPage.Profiles, _profilesInactiveAtUtc, _profilesViewModel, disposable => _profilesViewModel = null, now);
        TryUnloadTransientPage(NavigationPage.Connections, _connectionsInactiveAtUtc, _connectionsViewModel, disposable => _connectionsViewModel = null, now);
        TryUnloadTransientPage(NavigationPage.Logs, _logsInactiveAtUtc, _logsViewModel, disposable => _logsViewModel = null, now);
        TryUnloadTransientPage(NavigationPage.Settings, _settingsInactiveAtUtc, _settingsViewModel, disposable => _settingsViewModel = null, now);
    }

    private void TryUnloadGroupsPage(DateTime now)
    {
        if (SelectedPage == NavigationPage.Groups || _groupsInactiveAtUtc == null || _activeGroupsViewModel == null)
        {
            return;
        }

        if (now - _groupsInactiveAtUtc.Value < TransientPageUnloadDelay)
        {
            return;
        }

        _activeGroupsViewModel.TrimInactiveUi();
        _groupsInactiveAtUtc = null;
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
            case NavigationPage.Groups:
                _groupsInactiveAtUtc = inactiveAt;
                break;
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
            case NavigationPage.Groups:
                _groupsInactiveAtUtc = null;
                break;
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
            var mirror = SelectedKernelDownloadMirror;
            var latestVersion = await _kernelManager.GetLatestVersionAsync(mirror);
            ApplyLatestKernelVersion(mirror, latestVersion);
            ConnectionStatus = GetMissingKernelStartMessage();
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
                ConnectionStatus = BuildStartFailureStatus();
            }
        }
    }

    private async Task RefreshLatestKernelVersionAsync()
    {
        if (IsKernelInstalled)
        {
            return;
        }

        var mirror = SelectedKernelDownloadMirror;
        try
        {
            var latestVersion = await _kernelManager.GetLatestVersionAsync(mirror);
            if (mirror != SelectedKernelDownloadMirror)
            {
                return;
            }

            ApplyLatestKernelVersion(mirror, latestVersion);
        }
        catch
        {
            if (mirror == SelectedKernelDownloadMirror)
            {
                _latestKernelVersionMirror = null;
                LatestVersion = "unknown";
            }
        }
    }

    private void ApplyLatestKernelVersion(DownloadMirror mirror, string? latestVersion)
    {
        _latestKernelVersionMirror = string.IsNullOrWhiteSpace(latestVersion) ? null : mirror;
        LatestVersion = latestVersion ?? "unknown";
    }

    private string? GetKnownLatestVersionForSelectedKernelMirror()
    {
        if (_latestKernelVersionMirror != SelectedKernelDownloadMirror)
        {
            return null;
        }

        var version = LatestVersion.Trim();
        return string.IsNullOrWhiteSpace(version) ||
               string.Equals(version, "unknown", StringComparison.OrdinalIgnoreCase)
            ? null
            : version;
    }

    private void ApplyInstalledKernelInfo(KernelInfo? kernelInfo)
    {
        _singBoxManager.UpdateKernelPath(kernelInfo?.Path ?? _kernelManager.KernelPath);
        IsKernelInstalled = kernelInfo != null;
        KernelStatus = kernelInfo == null
            ? string.Empty
            : CartonApplicationInfo.FormatSingBoxStatus(kernelInfo.KernelVersion);
    }

    private async Task<string?> EnsureProfileConfigPathForStartAsync(Profile profile)
    {
        var configPath = await _configManager.GetConfigPathAsync(profile.Id, profile.Type);
        var hasLocalConfig = !string.IsNullOrWhiteSpace(configPath) && File.Exists(configPath);
        if (hasLocalConfig && !ShouldRefreshRemoteProfileOnStart(profile))
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

        var loadingMessage = hasLocalConfig
            ? GetString("Status.RemoteConfigRefreshing", "Remote config due for update, refreshing...")
            : GetString("Status.RemoteConfigDownloading", "Remote config missing, downloading...");
        ConnectionStatus = loadingMessage;
        _logStore.AddLog($"[INFO] {loadingMessage}: {profile.Name} ({profile.Id})");
        try
        {
            var client = HttpClientFactory.External;
            var content = await HttpDownloadHelper.DownloadTextAsync(
                client,
                profile.Url,
                (bytesReceived, totalBytes) =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        ConnectionStatus = DownloadUiHelper.FormatStatus(
                            loadingMessage,
                            bytesReceived,
                            totalBytes,
                            GetString("Common.Unknown", "unknown"));
                    });
                });
            if (string.IsNullOrWhiteSpace(content))
            {
                var message = GetString("Status.RemoteConfigEmpty", "Downloaded remote config is empty");
                return HandleRemoteConfigRefreshFailure(profile, configPath, hasLocalConfig, message);
            }

            await _configManager.SaveConfigAsync(profile.Id, content, ProfileType.Remote);
            profile.LastUpdated = DateTime.Now;
            await _profileManager.UpdateAsync(profile);
            var downloadedPath = await _configManager.GetConfigPathAsync(profile.Id, ProfileType.Remote);
            if (string.IsNullOrWhiteSpace(downloadedPath) || !File.Exists(downloadedPath))
            {
                var message = GetString("Status.RemoteConfigFileMissing", "Remote config download succeeded but file missing");
                return HandleRemoteConfigRefreshFailure(profile, configPath, hasLocalConfig, message);
            }

            var completedMessage = hasLocalConfig
                ? GetString("Status.RemoteConfigRefreshed", "Remote config refreshed")
                : GetString("Status.RemoteConfigDownloaded", "Remote config downloaded");
            _logStore.AddLog($"[INFO] {completedMessage}: {profile.Name} ({profile.Id})");
            ConnectionStatus = completedMessage;
            return downloadedPath;
        }
        catch (Exception ex)
        {
            var message = GetString("Status.RemoteConfigDownloadFailed", "Failed to download remote config");
            return HandleRemoteConfigRefreshFailure(profile, configPath, hasLocalConfig, $"{message}: {ex.Message}");
        }
    }

    private string? HandleRemoteConfigRefreshFailure(Profile profile, string? existingConfigPath, bool hasLocalConfig, string errorMessage)
    {
        if (hasLocalConfig && !string.IsNullOrWhiteSpace(existingConfigPath) && File.Exists(existingConfigPath))
        {
            var warning = GetString("Status.RemoteConfigRefreshFailedUsingLocal", "Remote config refresh failed, using local cached config");
            _logStore.AddLog($"[WARN] {errorMessage}: {profile.Name} ({profile.Id}); {warning}");
            ConnectionStatus = $"{warning}: {errorMessage}";
            return existingConfigPath;
        }

        _logStore.AddLog($"[ERROR] {errorMessage}: {profile.Name} ({profile.Id})");
        ConnectionStatus = errorMessage;
        return null;
    }

    private static bool ShouldRefreshRemoteProfileOnStart(Profile profile)
    {
        if (profile.Type != ProfileType.Remote || !profile.AutoUpdate)
        {
            return false;
        }

        if (profile.UpdateInterval <= 0 || profile.LastUpdated == null)
        {
            return true;
        }

        return DateTime.Now - profile.LastUpdated.Value >= TimeSpan.FromMinutes(profile.UpdateInterval);
    }

    private string GetString(string key, string fallback)
    {
        var value = _localizationService.GetString(key);
        return string.Equals(value, key, StringComparison.Ordinal) ? fallback : value;
    }

    [RelayCommand]
    private async Task DownloadKernel()
    {
        DownloadProgress = 0;
        HasKernelDownloadFailed = false;
        OnPropertyChanged(nameof(KernelPrimaryActionText));
        IsDownloadingKernel = true;
        DownloadStatus = _localizationService["Status.KernelDownloading"];
        var hadInstalledKernel = _kernelManager.IsKernelInstalled;

        var success = await _kernelManager.DownloadAndInstallAsync(GetKnownLatestVersionForSelectedKernelMirror(), SelectedKernelDownloadMirror);

        IsDownloadingKernel = false;

        if (success)
        {
            HasKernelDownloadFailed = false;
            var installedChannel = KernelCacheCleanupService.GetInstallChannel(SelectedKernelDownloadMirror);
            if (KernelCacheCleanupService.ShouldClearCache(_currentPreferences, installedChannel, hadInstalledKernel))
            {
                ClearKernelCacheFile();
            }

            KernelCacheCleanupService.RecordInstalledChannel(_currentPreferences, installedChannel);
            _preferencesService.Save(_currentPreferences);
            IsKernelInstalled = true;
            ShowKernelDialog = false;
            var kernelInfo = await _kernelManager.GetInstalledKernelInfoAsync();
            ApplyInstalledKernelInfo(kernelInfo);
        }
        else
        {
            HasKernelDownloadFailed = true;
            DownloadStatus = BuildKernelDownloadFailureMessage();
        }

        OnPropertyChanged(nameof(KernelPrimaryActionText));
    }

    [RelayCommand]
    private void CloseKernelDialog()
    {
        ShowKernelDialog = false;
    }

    private string BuildStartFailureStatus()
    {
        if (!IsKernelInstalled || !_kernelManager.IsKernelInstalled)
        {
            return GetMissingKernelStartMessage();
        }

        var fallback = _localizationService["Status.FailedStart"];
        var detail = _singBoxManager.State.ErrorMessage;
        if (!string.IsNullOrWhiteSpace(detail) &&
            detail.Contains("sing-box binary not found", StringComparison.OrdinalIgnoreCase))
        {
            return GetMissingKernelStartMessage();
        }

        return string.IsNullOrWhiteSpace(detail) ? fallback : $"{fallback}: {detail}";
    }

    private string GetMissingKernelStartMessage()
    {
        return GetString(
            "Status.KernelMissingStartFailed",
            "Start failed. sing-box kernel is missing. Please install it from Settings.");
    }

    private string BuildKernelDownloadFailureMessage()
    {
        var detail = KernelStatus;
        var hint = _localizationService.CurrentLanguage == AppLanguage.SimplifiedChinese
            ? "可切换镜像后继续下载，或稍后下载。"
            : "Switch mirrors to continue downloading, or download later.";

        return string.IsNullOrWhiteSpace(detail) ? hint : $"{detail} {hint}";
    }

    private string GetLocalizedRetryLabel()
    {
        return _localizationService.CurrentLanguage == AppLanguage.SimplifiedChinese
            ? "继续下载"
            : "Continue";
    }

    private void ClearKernelCacheFile()
    {
        try
        {
            var baseDirectory = carton.Core.Utilities.PathHelper.GetAppDataPath();
            var cacheDbPath = Path.Combine(baseDirectory, "cache.db");
            if (File.Exists(cacheDbPath))
            {
                File.Delete(cacheDbPath);
            }
        }
        catch (Exception ex)
        {
            DownloadStatus = $"{KernelStatus} Failed to clear cache.db: {ex.Message}";
        }
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
        StopSessionDurationTimer();
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
            if (_settingsViewModel != null)
            {
                _settingsViewModel.PropertyChanged -= OnSettingsViewModelPropertyChanged;
            }
            _settingsViewModel = null;
            _kernelManager.DownloadProgressChanged -= OnDownloadProgress;
            _kernelManager.StatusChanged -= OnKernelStatusChanged;
            _kernelManager.InstalledKernelChanged -= OnInstalledKernelChanged;
            _appUpdateCoordinator.PropertyChanged -= OnAppUpdateCoordinatorPropertyChanged;

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

    private static string UpdateChannelToString(AppUpdateChannel channel)
        => channel == AppUpdateChannel.Beta ? "beta" : "release";

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

public partial class ToastNotificationViewModel : ObservableObject
{
    [ObservableProperty]
    private string _message = string.Empty;

    [ObservableProperty]
    private double _opacity;

    [ObservableProperty]
    private Thickness _offsetMargin;
}
