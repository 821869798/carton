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
    /// <summary>Writes a line to the in-app log store (Logs page).</summary>
    public void Log(string message)
    {
        _logStore.AddLog(message);
    }

    /// <summary>
    /// One line describing where this build came from, so a bug report states the package format
    /// and the data directory instead of leaving both to guesswork.
    /// </summary>
    private static string BuildInstallSummary()
    {
        var appDirectory = AppContext.BaseDirectory;
        var kind = InstallStamp.Slug(InstallStamp.Detect(appDirectory));
        var portableData = File.Exists(Path.Combine(appDirectory, PathHelper.PortableMarkerFileName));
        return $"[INFO] install: {kind}, data: {PathHelper.GetAppDataPath()} "
             + $"({(portableData ? "portable" : "non-portable")})";
    }

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
    private DateTime? _groupsInactiveAtUtc;
    private DateTime? _profilesInactiveAtUtc;
    private DateTime? _connectionsInactiveAtUtc;
    private DateTime? _logsInactiveAtUtc;
    private DateTime? _settingsInactiveAtUtc;

    public ILocalizationService Localization => _localizationService;

    public bool IsDashboardPage => SelectedPage == NavigationPage.Dashboard;
    public bool IsTransientPage => SelectedPage != NavigationPage.Dashboard;
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
        _kernelManager.StatusChanged += OnKernelProcessMessage;
        _kernelManager.InstalledKernelChanged += OnInstalledKernelChanged;

        var singBoxPath = _kernelManager.KernelPath;
        _singBoxManager = new SingBoxManager(singBoxPath, workingDirectory);

        _logStore = new LogStore();

        _singBoxManager.StatusChanged += OnStatusChanged;
        _singBoxManager.ManagerLogEntryReceived += OnManagerLogEntryReceived;
        _singBoxManager.LogReceived += OnLogReceived;
        _singBoxManager.KernelLogsReset += OnKernelLogsReset;
        _singBoxManager.KernelVersionRejected += OnKernelVersionRejected;

        DashboardViewModel = new DashboardViewModel(
            _singBoxManager,
            _kernelManager,
            _profileManager,
            _configManager,
            _preferencesService,
            ShowToast,
            _logStore.AddLog,
            RefreshProfilesPageAsync);
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
        _logStore.AddLog("[DEBUG] Log pipeline initialized");
        _logStore.AddLog(BuildInstallSummary());
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

        // Restore the last native API endpoint so a restarted carton can re-attach to
        // an externally still-running kernel (e.g. carton was killed while TUN was
        // active) instead of probing the static default port and reporting "not running".
        if (_currentPreferences.LastNativeApiPort > 0)
        {
            var storedSecret = carton.Core.Services.SecretProtector.Unprotect(_currentPreferences.LastNativeApiSecret);
            HttpClientFactory.UpdateLocalApi(
                "127.0.0.1",
                _currentPreferences.LastNativeApiPort,
                storedSecret);
            HttpClientFactory.UpdateLocalNativeApi(
                "127.0.0.1",
                _currentPreferences.LastNativeApiPort,
                storedSecret);

            // Upgrade hygiene: a pre-DPAPI release stored this secret as plain text.
            // Encrypt it in place right now instead of waiting for the next successful
            // kernel start (which may never come) to overwrite the file.
            // Gate on the INPUT form (IsProtected), NOT on comparing ciphertexts:
            // DPAPI encryption is non-deterministic, so a re-protected value differs
            // from the stored blob on every launch and would rewrite the file forever.
            if (!string.IsNullOrWhiteSpace(storedSecret) &&
                !carton.Core.Services.SecretProtector.IsProtected(_currentPreferences.LastNativeApiSecret))
            {
                var reProtected = carton.Core.Services.SecretProtector.TryProtect(storedSecret, out var protectedSecret)
                    ? protectedSecret
                    : null;
                if (reProtected != null &&
                    !string.Equals(reProtected, _currentPreferences.LastNativeApiSecret, StringComparison.Ordinal))
                {
                    _currentPreferences.LastNativeApiSecret = reProtected;
                    _preferencesService.Save(_currentPreferences);
                }
            }
        }

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
                _logStore.AddLog($"[DEBUG] Cleared stale system proxy left by a previous carton session on port {port}");
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

    private void OnKernelProcessMessage(object? sender, string status)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            // Kernel process messages (download/verify/install progress) belong to the
            // settings download area (DownloadStatus) only. The persistent title-bar
            // version label (KernelStatus) must ONLY change when the installed kernel
            // actually changes (see ApplyInstalledKernelInfo / OnInstalledKernelChanged).
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
            if (_connectionsViewModel != null)
            {
                _connectionsViewModel.OnServiceStatusChanged(status == ServiceStatus.Running);
            }
        });
    }

    private void OnLogReceived(object? sender, KernelLogEntry log)
    {
        _logStore.AddSingBoxLog(log);
    }

    private void OnKernelLogsReset(object? sender, EventArgs e)
    {
        // The kernel reset its log buffer and will replay its saved history next:
        // drop stale kernel lines so the replay does not duplicate them.
        _logStore.RemoveSource(LogSource.SingBox);
    }

    private void OnKernelVersionRejected(object? sender, string message)
    {
        // Hard version gate tripped at startup: blocking dialog directing the user
        // to install a supported kernel (>= 1.14.0). The rejection typically fires
        // during auto-start with the window still hidden in the tray, so the dialog
        // must bring the main window up first - ShowDialog on a hidden owner throws.
        Dispatcher.UIThread.Post(async () =>
        {
            var desktop = Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            var owner = desktop?.MainWindow;
            if (owner is { IsVisible: false })
            {
                owner.Show();
                if (owner.WindowState == Avalonia.Controls.WindowState.Minimized)
                {
                    owner.WindowState = Avalonia.Controls.WindowState.Normal;
                }
            }

            await ShowKernelVersionRejectedDialogAsync(message);
        });
    }

    private async Task ShowKernelVersionRejectedDialogAsync(string message)
    {
        var desktop = Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        var owner = desktop?.MainWindow;
        if (owner == null)
        {
            ShowToast(message);
            return;
        }

        var dialog = new Avalonia.Controls.Window
        {
            Width = 460,
            SizeToContent = Avalonia.Controls.SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.CenterOwner,
            Title = GetString("Kernel.Unsupported.Title", "Unsupported sing-box Version"),
            ShowInTaskbar = false
        };

        var messageBlock = new Avalonia.Controls.TextBlock
        {
            Text = message,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(0, 0, 0, 16)
        };

        var okButton = new Avalonia.Controls.Button
        {
            Content = GetString("Common.Ok", "OK"),
            MinWidth = 110,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right
        };
        okButton.Click += (_, _) => dialog.Close();

        dialog.Content = new Avalonia.Controls.StackPanel
        {
            Margin = new Avalonia.Thickness(24),
            Children = { messageBlock, okButton }
        };

        await dialog.ShowDialog(owner);
    }

    private void OnManagerLogEntryReceived(object? sender, CartonLogEntry entry)
    {
        _logStore.AddLog(entry);
    }

    partial void OnSelectedPageChanged(NavigationPage value)
    {
        var previousPage = CurrentPage;
        previousPage.OnNavigatedFrom();
        MarkTransientPageInactive(previousPage.PageType);

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
        CurrentPage.OnNavigatedTo();
        if (value == NavigationPage.Dashboard)
        {
            _ = DashboardViewModel.LoadProfilesAsync();
        }

        OnPropertyChanged(nameof(IsDashboardPage));
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
        if (_lazyGroupsViewModel.IsValueCreated)
        {
            _lazyGroupsViewModel.Value.SetWindowVisible(isVisible);
        }
        if (_connectionsViewModel != null)
        {
            _connectionsViewModel.SetWindowVisible(isVisible);
        }

        _logsViewModel?.SetWindowVisible(isVisible);

        if (!isVisible)
        {
            ForceUnloadInactiveTransientPagesForBackground();
        }
    }

    public GroupsViewModel EnsureGroupsViewModel()
    {
        var groupsViewModel = _lazyGroupsViewModel.Value;
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

    private async Task RefreshProfilesPageAsync()
    {
        if (_profilesViewModel == null)
        {
            return;
        }

        await _profilesViewModel.RefreshAsync();
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
        if (e.PropertyName == nameof(SettingsViewModel.SaveWindowPlacementEnabled))
        {
            OnPropertyChanged(nameof(SaveWindowPlacementEnabled));
        }

        if (string.IsNullOrWhiteSpace(e.PropertyName) ||
            e.PropertyName == nameof(SettingsViewModel.IsBlockingUi) ||
            e.PropertyName == nameof(SettingsViewModel.BlockingUiMessage))
        {
            UpdateInteractionBlockState();
        }
    }

    public bool SaveWindowPlacementEnabled => _settingsViewModel?.SaveWindowPlacementEnabled == true;

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
        var unloaded = TryUnloadInactiveTransientPages();
        if (unloaded)
        {
            // Disposing a page frees its view models and lets the visual tree go, but the
            // pages stay in the GC heap and in the working set until something collects
            // and trims. Without this, navigating around leaves the working set elevated
            // indefinitely (measured: 19 MB -> 65 MB after navigation churn, with no
            // recovery). CompactAndTrim is async and rate limited, so this is cheap.
            MemoryOptimizer.CompactAndTrim();
        }
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

    /// <summary>
    /// Unloads transient pages whose inactivity delay has elapsed.
    /// </summary>
    /// <returns>True when at least one page was actually disposed.</returns>
    private bool TryUnloadInactiveTransientPages()
    {
        var now = DateTime.UtcNow;
        var unloaded = TryUnloadGroupsPage(now);
        unloaded |= TryUnloadTransientPage(NavigationPage.Profiles, _profilesInactiveAtUtc, _profilesViewModel, disposable => _profilesViewModel = null, now);
        unloaded |= TryUnloadTransientPage(NavigationPage.Connections, _connectionsInactiveAtUtc, _connectionsViewModel, disposable => _connectionsViewModel = null, now);
        unloaded |= TryUnloadTransientPage(NavigationPage.Logs, _logsInactiveAtUtc, _logsViewModel, disposable => _logsViewModel = null, now);
        unloaded |= TryUnloadTransientPage(NavigationPage.Settings, _settingsInactiveAtUtc, _settingsViewModel, disposable => _settingsViewModel = null, now);
        return unloaded;
    }

    /// <summary>
    /// When the window goes to the tray we no longer need the visual trees of the
    /// transient pages the user is not currently viewing (Connections grid, up to
    /// 800 log rows, profile list, the JSON editor). Backdate their inactivity so
    /// the existing unload path frees them immediately. The selected page is never
    /// unloaded, so re-showing has no extra cost and pages re-create lazily on next
    /// navigation as they already do.
    ///
    /// Dashboard stays resident. Groups keeps its ViewModel (tray menus + snapshot
    /// cache) but the view is already gone with ActiveTransientPage; trim the row VMs.
    /// </summary>
    private void ForceUnloadInactiveTransientPagesForBackground()
    {
        var past = DateTime.UtcNow - TransientPageUnloadDelay - TimeSpan.FromSeconds(1);
        if (_groupsInactiveAtUtc != null) _groupsInactiveAtUtc = past;
        if (_profilesInactiveAtUtc != null) _profilesInactiveAtUtc = past;
        if (_connectionsInactiveAtUtc != null) _connectionsInactiveAtUtc = past;
        if (_logsInactiveAtUtc != null) _logsInactiveAtUtc = past;
        if (_settingsInactiveAtUtc != null) _settingsInactiveAtUtc = past;
        TryUnloadInactiveTransientPages();
    }

    private bool TryUnloadGroupsPage(DateTime now)
    {
        if (SelectedPage == NavigationPage.Groups || _groupsInactiveAtUtc == null || !_lazyGroupsViewModel.IsValueCreated)
        {
            return false;
        }

        if (now - _groupsInactiveAtUtc.Value < TransientPageUnloadDelay)
        {
            return false;
        }

        _lazyGroupsViewModel.Value.TrimInactiveUi();
        _groupsInactiveAtUtc = null;
        return true;
    }

    /// <summary>
    /// Disposes one transient page if it is not the selected page and its inactivity
    /// delay has elapsed.
    /// </summary>
    /// <returns>True when the page was disposed by this call.</returns>
    private bool TryUnloadTransientPage(NavigationPage page, DateTime? inactiveAtUtc, IDisposable? viewModel, Action<IDisposable> clearReference, DateTime now)
    {
        if (SelectedPage == page || inactiveAtUtc == null || viewModel == null)
        {
            return false;
        }

        if (now - inactiveAtUtc.Value < TransientPageUnloadDelay)
        {
            return false;
        }

        clearReference(viewModel);
        viewModel.Dispose();
        if (CurrentPage.PageType == page)
        {
            OnPropertyChanged(nameof(ActiveTransientPage));
        }

        return true;
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
            // DownloadStatus normally still holds the real failure message pushed by
            // OnKernelProcessMessage (e.g. "Failed to download: ...") because every
            // failure path inside KernelManager goes through a network await before
            // invoking StatusChanged, so the Post'd update lands before this
            // continuation resumes. That ordering is an observation about the current
            // call chain, NOT a language guarantee (a cached/synchronously-completed
            // lookup would inline the continuation). Guard against the edge case: if
            // DownloadStatus is still the initial "starting download" text, no real
            // error ever arrived - fall back to the generic hint instead of showing
            // a stale "Starting download..."-prefixed message.
            var startingText = _localizationService["Status.KernelDownloading"];
            var failureDetail = DownloadStatus == startingText ? null : DownloadStatus;
            DownloadStatus = BuildKernelDownloadFailureMessage(failureDetail);
        }

        OnPropertyChanged(nameof(KernelPrimaryActionText));
    }

    [RelayCommand]
    private void CloseKernelDialog()
    {
        ShowKernelDialog = false;
    }

    private string BuildKernelDownloadFailureMessage(string? detail)
    {
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
            DownloadStatus = $"Failed to clear cache.db: {ex.Message}";
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
            _kernelManager.StatusChanged -= OnKernelProcessMessage;
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
