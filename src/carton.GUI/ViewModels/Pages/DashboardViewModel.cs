using Avalonia.Threading;
using carton.Core.Models;
using carton.Core.Services;
using carton.Core.Utilities;
using carton.GUI.Models;
using carton.GUI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace carton.ViewModels;

public partial class DashboardViewModel : PageViewModelBase
{
    private const string TerminalProxyTypeCmd = "cmd";
    private const string TerminalProxyTypePowerShell = "ps";
    private const string TerminalProxyTypeLinux = "linux";
    private const int TrafficSparklineSampleCount = 60;
    private static readonly TimeSpan ModeCacheDuration = TimeSpan.FromSeconds(2);
    private static readonly IReadOnlyList<DashboardSiteStatusDefinition> ConnectivityTargets =
    [
        new("Baidu", "https://apps.bdimg.com/favicon.ico"),
        new("Google", "https://www.google.com/generate_204")
    ];
    private readonly ISingBoxManager? _singBoxManager;
    private readonly IKernelManager? _kernelManager;
    private readonly IProfileManager? _profileManager;
    private readonly IConfigManager? _configManager;
    private readonly RemoteConfigUpdateService? _remoteConfigUpdateService;
    private readonly IPreferencesService? _preferencesService;
    private readonly Action<string, int>? _toastWriter;
    private readonly Action<string>? _logWriter;
    private readonly Func<Task>? _profilesChangedCallback;
    private readonly ILocalizationService _localizationService;
    private readonly ProxyModeCacheService _proxyModeCache;
    private string? _currentMode;
    private bool _suppressSelectedModeOptionChange;
    private ProfileRuntimeOptions _runtimeOptions = new();
    private bool _suppressRuntimeOptionUpdates;
    private bool _suppressSystemProxyApply;
    private DashboardRuntimeOperation _runtimeOperation = DashboardRuntimeOperation.None;
    private bool _isOnPage = true;
    private bool _isWindowVisible = true;
    private bool _isLiveRefreshActive;
    private int _pendingTrafficRefresh;
    private int _pendingMemoryRefresh;
    private int? _runningProfileId;
    private string? _runningSourceConfigPath;
    private long _lastUploadSpeed;
    private long _lastDownloadSpeed;
    private static readonly ObservableCollection<string> SupportedLogLevels = new(SingBoxLogLevelHelper.Levels);
    public override NavigationPage PageType => NavigationPage.Dashboard;

    [ObservableProperty]
    private ServiceStatus _kernelStatus = ServiceStatus.Stopped;

    public bool IsConnected => KernelStatus == ServiceStatus.Running;
    public string StatusText => KernelStatus switch
    {
        ServiceStatus.Running => _localizationService["Status.Connected"],
        ServiceStatus.Starting => _localizationService["Status.Starting"],
        ServiceStatus.Stopping => _localizationService["Status.Stopping"],
        ServiceStatus.Error => _localizationService["Status.Error"],
        _ => _localizationService["Status.Disconnected"]
    };

    [ObservableProperty]
    private string _uploadSpeed = "0 B/s";

    [ObservableProperty]
    private string _downloadSpeed = "0 B/s";

    [ObservableProperty]
    private string _totalUpload = "0 B";

    [ObservableProperty]
    private string _totalDownload = "0 B";

    [ObservableProperty]
    private string _currentProfile = "No Profile Selected";

    [ObservableProperty]
    private ObservableCollection<DashboardProfileItemViewModel> _availableProfiles = new();

    public ObservableCollection<DashboardModeOptionViewModel> ModeOptions { get; } = new();
    public ObservableCollection<long> UploadTrafficSamples { get; } = new();
    public ObservableCollection<long> DownloadTrafficSamples { get; } = new();
    public ObservableCollection<DashboardSiteStatusItemViewModel> ConnectivityItems { get; } = new();

    public int ModeColumnCount => Math.Max(1, ModeOptions.Count);
    public bool UseModeDropdown => ModeOptions.Count > 5;
    public bool ShowModeSegments => ModeOptions.Count > 0 && !UseModeDropdown;

    [ObservableProperty]
    private DashboardModeOptionViewModel? _selectedModeOption;

    partial void OnSelectedModeOptionChanged(DashboardModeOptionViewModel? value)
    {
        if (_suppressSelectedModeOptionChange || value == null)
        {
            return;
        }

        _ = ChangeMode(value);
    }

    [ObservableProperty]
    private DashboardProfileItemViewModel? _selectedStartupProfile;

    public bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    public bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
    public bool ShowTerminalProxyButtons => IsConnected;

    [ObservableProperty]
    private string _kernelVersion = "unknown";

    [ObservableProperty]
    private string _memoryUsage = "0 B";

    [ObservableProperty]
    private string _goroutineCount = "--";

    [ObservableProperty]
    private string _connectionsIn = "--";

    [ObservableProperty]
    private string _connectionsOut = "--";

    [NotifyCanExecuteChangedFor(nameof(RefreshConnectivityCommand))]
    [ObservableProperty]
    private bool _isRefreshingConnectivity;
    // Session-level "already auto-tested" flag: the auto-refresh path (page entry /
    // window visibility) must not re-run the full retry loop on every navigation when
    // a site (e.g. Google) is unreachable - otherwise up to ~16.5s of retries keep the
    // refresh button disabled on each dashboard visit. Manual refresh (force) and a
    // kernel restart (ResetConnectivityDisplay) clear it.
    private bool _connectivityAutoTestedThisSession;
    private int _connectivitySessionId;

    [RelayCommand]
    private Task CopyCmdTerminalProxy() => CopyTerminalProxyAsync(TerminalProxyTypeCmd);

    [RelayCommand]
    private Task CopyPsTerminalProxy() => CopyTerminalProxyAsync(TerminalProxyTypePowerShell);

    [RelayCommand]
    private Task CopyLinuxTerminalProxy() => CopyTerminalProxyAsync(TerminalProxyTypeLinux);

    [RelayCommand]
    private void OpenCmdTerminalProxy() => OpenTerminalProxy(TerminalProxyTypeCmd);

    [RelayCommand]
    private void OpenPsTerminalProxy() => OpenTerminalProxy(TerminalProxyTypePowerShell);

    [RelayCommand(CanExecute = nameof(CanRefreshConnectivity))]
    private Task RefreshConnectivity() => RefreshConnectivityCoreAsync(force: true);

    [RelayCommand]
    private async Task EditInboundPortAsync()
    {
        var result = await ShowInboundPortDialogAsync();
        if (result == null)
        {
            return;
        }

        InboundPortText = result;
        CommitInboundPortEdit();
    }

    [RelayCommand]
    private void SelectLocalOnly() => SetInboundAccessScope(allowLan: false);

    [RelayCommand]
    private void SelectLanAccess() => SetInboundAccessScope(allowLan: true);

    [RelayCommand]
    private async Task ResetRuntimeOptionsToConfig()
    {
        if (_profileManager == null)
        {
            var message = GetString("Dashboard.Status.ResetRuntimeOptionsFailed", "Failed to reset options to config values");
            StartupStatus = message;
            LogError(message);
            return;
        }

        var target = SelectedStartupProfile ?? GetFirstAvailableProfile();
        if (target == null)
        {
            var message = GetString("Dashboard.Startup.NoProfileAvailable", "No profile available");
            StartupStatus = message;
            LogError(message);
            return;
        }

        try
        {
            var options = await _profileManager.ResetRuntimeOptionsToConfigAsync(target.Id);
            if (options == null)
            {
                var message = GetString("Dashboard.Status.ResetRuntimeOptionsUnavailable", "Config values are unavailable for reset");
                StartupStatus = message;
                LogError($"{message}: {target.Name} ({target.Id})");
                return;
            }

            ApplyRuntimeOptions(options);
            StartupStatus = GetString("Dashboard.Status.RuntimeOptionsResetToConfig", "Options reset to config values");
            LogInfo($"{StartupStatus}: {target.Name} ({target.Id})");
        }
        catch (Exception ex)
        {
            var message = GetString("Dashboard.Status.ResetRuntimeOptionsFailed", "Failed to reset options to config values");
            StartupStatus = $"{message}: {ex.Message}";
            LogError($"{message}: {target.Name} ({target.Id}): {ex.Message}");
        }
    }

    private async Task CopyTerminalProxyAsync(string type)
    {
        if (!TryBuildTerminalProxyCommand(type, out var command, out var error))
        {
            StartupStatus = error;
            LogError(error);
            return;
        }

        var desktop = Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
        if (desktop?.MainWindow?.Clipboard is not { } clipboard)
        {
            var message = GetString("Dashboard.Status.ClipboardUnavailable", "Clipboard is not available");
            StartupStatus = message;
            LogError(message);
            return;
        }

        await clipboard.SetTextAsync(command);
        var copiedMessage = GetString("Dashboard.Status.CommandCopied", "Command copied to clipboard");
        ShowTransientStatus(copiedMessage);
        _toastWriter?.Invoke(copiedMessage, 2200);
        LogInfo($"Copied {type} proxy command");
    }

    private void OpenTerminalProxy(string type)
    {
        if (!TryCreateTerminalLaunch(type, out var startInfo, out var error))
        {
            StartupStatus = error;
            LogError(error);
            return;
        }

        try
        {
            Process.Start(startInfo);
            ShowTransientStatus(GetString("Dashboard.Status.ProxyToolOpened", "Proxy tool opened"));
            LogInfo($"Opened {type} proxy tool");
        }
        catch (Exception ex)
        {
            var message = GetString("Dashboard.Status.OpenProxyToolFailed", "Failed to open proxy tool");
            StartupStatus = $"{message}: {ex.Message}";
            LogError($"{message}: {type}: {ex.Message}");
        }
    }

    private void SetInboundAccessScope(bool allowLan)
    {
        if (!ShowStartupSelector || AllowLanConnections == allowLan)
        {
            return;
        }

        AllowLanConnections = allowLan;
        var message = allowLan
            ? GetString("Dashboard.Port.Scope.EnabledStatus", "LAN access enabled")
            : GetString("Dashboard.Port.Scope.DisabledStatus", "Switched to local only");
        ShowTransientStatus(message);
        LogInfo($"Inbound access scope changed: lan={AllowLanConnections}");
    }

    private string BuildInboundAccessOptionToolTip(bool allowLan)
    {
        var description = allowLan
            ? GetString("Dashboard.Port.Scope.Lan.ToolTip", "Listening on 0.0.0.0. Devices on your LAN can connect.")
            : GetString("Dashboard.Port.Scope.LocalOnly.ToolTip", "Listening on 127.0.0.1. Only this device can connect.");

        if (!ShowStartupSelector)
        {
            return $"{description}{Environment.NewLine}{GetString("Dashboard.Port.Scope.Locked.ToolTip", "Stop the kernel before changing the listening scope.")}";
        }

        if (AllowLanConnections == allowLan)
        {
            return description;
        }

        var action = allowLan
            ? GetString("Dashboard.Port.Scope.ToggleToLan.ToolTip", "Click to allow devices on your LAN.")
            : GetString("Dashboard.Port.Scope.ToggleToLocal.ToolTip", "Click to switch to local only.");
        return $"{description}{Environment.NewLine}{action}";
    }

    [RelayCommand]
    private void OpenSingBoxWebUi()
    {
        LaunchWebUi(BuildSingBoxWebUiUrl, "sing-box API WebUI");
    }

    private void LaunchWebUi(Func<string> urlFactory, string target)
    {
        try
        {
            var url = urlFactory();
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            var message = GetString("Dashboard.Status.OpenWebUiFailed", "Failed to open WebUI");
            StartupStatus = $"{message}: {ex.Message}";
            LogError($"Failed to open {target}: {ex.Message}");
        }
    }

    [ObservableProperty]
    private string _startupStatus = string.Empty;

    [ObservableProperty]
    private string _inboundPortText = "2028";

    [ObservableProperty]
    private bool _allowLanConnections;

    public bool IsLocalOnlySelected => !AllowLanConnections;
    public bool IsLanAccessSelected => AllowLanConnections;
    public string LocalOnlyAccessToolTip => BuildInboundAccessOptionToolTip(allowLan: false);
    public string LanAccessToolTip => BuildInboundAccessOptionToolTip(allowLan: true);

    [ObservableProperty]
    private bool _enableSystemProxy;

    [ObservableProperty]
    private bool _enableTunInbound;

    [ObservableProperty]
    private string _selectedLogLevel = SingBoxLogLevelHelper.DefaultLevel;

    partial void OnAllowLanConnectionsChanged(bool value)
    {
        UpdateRuntimeOptions(options => options.AllowLanConnections = value);
        OnPropertyChanged(nameof(IsLocalOnlySelected));
        OnPropertyChanged(nameof(IsLanAccessSelected));
        OnPropertyChanged(nameof(LocalOnlyAccessToolTip));
        OnPropertyChanged(nameof(LanAccessToolTip));
    }
    partial void OnEnableSystemProxyChanged(bool value)
    {
        UpdateRuntimeOptions(options => options.EnableSystemProxy = value);

        if (_suppressSystemProxyApply || !IsConnected)
        {
            return;
        }

        ApplyRunningSystemProxy(value);
    }
    partial void OnEnableTunInboundChanged(bool oldValue, bool newValue)
    {
        UpdateRuntimeOptions(options => options.EnableTunInbound = newValue);

        if (_suppressRuntimeOptionUpdates || !IsConnected)
        {
            return;
        }

        _ = RestartForTunToggleAsync(newValue, previousValue: oldValue);
    }
    partial void OnSelectedLogLevelChanged(string value)
    {
        UpdateRuntimeOptions(options => options.LogLevel = NormalizeLogLevel(value));
        OnPropertyChanged(nameof(ShowVerboseLogLevelHint));
    }

    private const int DefaultSingBoxApiPort = 9091;

    public bool ShowStartupSelector => !IsConnected;
    public bool ShowDashboardMetrics => IsConnected;
    public DashboardViewModel? DashboardMetricsContent => ShowDashboardMetrics ? this : null;
    public bool CanUseDashboardControls =>
        _singBoxManager == null ||
        (_runtimeOperation == DashboardRuntimeOperation.None &&
         KernelStatus is not (ServiceStatus.Starting or ServiceStatus.Stopping));
    public bool CanToggleTunInbound => CanUseDashboardControls && (ShowStartupSelector || IsConnected);
    public ObservableCollection<string> LogLevelOptions => SupportedLogLevels;
    public bool ShowVerboseLogLevelHint => SingBoxLogLevelHelper.IsVerbose(SelectedLogLevel);
    public bool HasAvailableProfiles => AvailableProfiles.Count > 0;
    public bool HasSelectedProfile => SelectedStartupProfile != null;
    public bool CanToggleConnection => IsConnected || HasSelectedProfile;
    public string ConnectionToggleToolTip => IsConnected
        ? GetString("MainWindow.Button.StopKernel", "Stop Kernel")
        : GetString("MainWindow.Button.StartKernel", "Start Kernel");

    partial void OnSelectedStartupProfileChanged(DashboardProfileItemViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelectedProfile));
        OnPropertyChanged(nameof(CanToggleConnection));
    }

    partial void OnKernelStatusChanged(ServiceStatus value)
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ShowStartupSelector));
        OnPropertyChanged(nameof(ShowDashboardMetrics));
        OnPropertyChanged(nameof(DashboardMetricsContent));
        OnPropertyChanged(nameof(HasSelectedProfile));
        OnPropertyChanged(nameof(CanToggleConnection));
        OnPropertyChanged(nameof(ConnectionToggleToolTip));
        OnPropertyChanged(nameof(CanUseDashboardControls));
        OnPropertyChanged(nameof(CanToggleTunInbound));
        OnPropertyChanged(nameof(LocalOnlyAccessToolTip));
        OnPropertyChanged(nameof(LanAccessToolTip));
        RefreshConnectivityCommand.NotifyCanExecuteChanged();
    }

    public DashboardViewModel()
    {
        InitializePageMetadata("Home", "Navigation.Dashboard", "Dashboard");
        _localizationService = LocalizationService.Instance;
        _proxyModeCache = ProxyModeCacheService.Instance;
        InitializeConnectivityItems();
        AvailableProfiles.CollectionChanged += OnAvailableProfilesCollectionChanged;
        _localizationService.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(ConnectionToggleToolTip));
            OnPropertyChanged(nameof(LocalOnlyAccessToolTip));
            OnPropertyChanged(nameof(LanAccessToolTip));
            _ = RefreshKernelVersionAsync();
        };
        _ = RefreshKernelVersionAsync();
    }

    private void OnAvailableProfilesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasAvailableProfiles));
        OnPropertyChanged(nameof(HasSelectedProfile));
        OnPropertyChanged(nameof(CanToggleConnection));
    }

    public DashboardViewModel(
        ISingBoxManager singBoxManager,
        IKernelManager kernelManager,
        IProfileManager profileManager,
        IConfigManager configManager,
        IPreferencesService preferencesService,
        Action<string, int>? toastWriter = null,
        Action<string>? logWriter = null,
        Func<Task>? profilesChangedCallback = null) : this()
    {
        _singBoxManager = singBoxManager;
        _kernelManager = kernelManager;
        _profileManager = profileManager;
        _configManager = configManager;
        _remoteConfigUpdateService = new RemoteConfigUpdateService(configManager, profileManager, preferencesService);
        _preferencesService = preferencesService;
        _toastWriter = toastWriter;
        _logWriter = logWriter;
        _profilesChangedCallback = profilesChangedCallback;
        _kernelManager.InstalledKernelChanged += OnInstalledKernelChanged;
        _singBoxManager.StatusChanged += OnStatusChanged;
        _singBoxManager.TrafficUpdated += OnTrafficUpdated;
        _singBoxManager.MemoryUpdated += OnMemoryUpdated;
        _singBoxManager.ModeChanged += OnModeChanged;
        KernelStatus = _singBoxManager.State.Status;
        _ = LoadProfilesAsync();
        _ = RefreshKernelVersionAsync();
        if (_singBoxManager.IsRunning)
        {
            UpdateLiveRefreshState();
            _suppressSystemProxyApply = true;
            EnableSystemProxy = _runtimeOptions.EnableSystemProxy;
            _suppressSystemProxyApply = false;
        }
    }

    public void OnNavigatedTo()
    {
        _isOnPage = true;
        UpdateLiveRefreshState();
    }

    public void OnNavigatedFrom()
    {
        _isOnPage = false;
        UpdateLiveRefreshState();
    }

    public void SetWindowVisible(bool isVisible)
    {
        if (_isWindowVisible == isVisible)
        {
            return;
        }

        _isWindowVisible = isVisible;
        UpdateLiveRefreshState();
    }

    private void OnStatusChanged(object? sender, ServiceStatus status)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            KernelStatus = status;
            OnPropertyChanged(nameof(CanUseDashboardControls));
            OnPropertyChanged(nameof(CanToggleTunInbound));
            OnPropertyChanged(nameof(ShowTerminalProxyButtons));
    
            if (status == ServiceStatus.Error)
            {
                StartupStatus = BuildStartFailureStatus();
            }

            _suppressSystemProxyApply = true;
            if (status == ServiceStatus.Running)
            {
                EnableSystemProxy = _runtimeOptions.EnableSystemProxy;
            }
            _suppressSystemProxyApply = false;
        });

        if (status == ServiceStatus.Running)
        {
            Dispatcher.UIThread.Post(UpdateLiveRefreshState);
            _ = ReportDeprecatedWarningsAsync();
        }
        else
        {
            Dispatcher.UIThread.Post(() =>
            {
                UpdateLiveRefreshState();
                _proxyModeCache.Clear();
                UpdateModeSelection(null);
                ResetTrafficDisplay();
                ResetConnectivityDisplay();
                ResetKernelStatusDisplay();
            });
        }
    }

    private void InitializeTrafficMetrics()
    {
        if (_singBoxManager == null)
        {
            ApplyTrafficMetrics(0, 0, 0, 0);
            return;
        }

        var state = _singBoxManager.State;
        ApplyTrafficMetrics(state.UploadSpeed, state.DownloadSpeed, state.TotalUpload, state.TotalDownload);
    }

    private void OnTrafficUpdated(object? sender, TrafficInfo traffic)
    {
        if (_singBoxManager == null || !CanRefreshLiveMetrics())
        {
            return;
        }

        if (Interlocked.Exchange(ref _pendingTrafficRefresh, 1) == 1)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _pendingTrafficRefresh, 0);
            if (_singBoxManager == null || !CanRefreshLiveMetrics())
            {
                return;
            }

            var state = _singBoxManager.State;
            ApplyTrafficMetrics(state.UploadSpeed, state.DownloadSpeed, state.TotalUpload, state.TotalDownload);
            ApplyKernelStatusMetrics(state.Goroutines, state.ConnectionsIn, state.ConnectionsOut);
        });
    }

    private void ApplyKernelStatusMetrics(int goroutines, int connectionsIn, int connectionsOut)
    {
        // "--" means "no data" (kernel not pushing the metric yet), NOT zero.
        // ConnectionsIn/Out are legitimately 0 when the kernel is idle, so gate on
        // IsConnected instead of the value; Goroutines can never be 0 while running.
        var running = IsConnected;
        GoroutineCount = running && goroutines > 0 ? goroutines.ToString() : "--";
        ConnectionsIn = running ? connectionsIn.ToString() : "--";
        ConnectionsOut = running ? connectionsOut.ToString() : "--";
    }

    private void InitializeMemoryMetrics()
    {
        ApplyMemoryUsage(_singBoxManager?.State.MemoryInUse ?? 0);
    }

    private void OnModeChanged(object? sender, string mode)
    {
        // Push-driven mode updates: fires on our own SetModeAsync (optimistic) and on
        // changes made by other control clients (official sing-box dashboard).
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            // Mode changes can flip GLOBAL group visibility on the Groups page: mark the
            // cache dirty so navigation there reloads instead of rendering stale groups.
            // Preserve the known mode list - overwriting it with null (before the first
            // GetModeConfigAsync filled it) collapses the selector to a single button.
            var previous = _proxyModeCache.Current;
            var newModeList = previous?.ModeList;
            if (newModeList == null && _singBoxManager is { IsRunning: true })
            {
                // List not fetched yet: request it once so the selector keeps its options.
                _ = RefreshModeAsync();
            }

            _proxyModeCache.Update(new ApiModeConfigSnapshot
            {
                Mode = mode,
                ModeList = newModeList
            }, isDirty: true);
            UpdateModeSelection(mode);
        });
    }

    private void OnMemoryUpdated(object? sender, long memoryInUse)
    {
        if (!CanRefreshLiveMetrics())
        {
            return;
        }

        if (Interlocked.Exchange(ref _pendingMemoryRefresh, 1) == 1)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _pendingMemoryRefresh, 0);
            if (!CanRefreshLiveMetrics())
            {
                return;
            }

            ApplyMemoryUsage(_singBoxManager?.State.MemoryInUse ?? memoryInUse);
        });
    }

    public async Task LoadProfilesAsync()
    {
        if (_profileManager == null) return;

        var profiles = await _profileManager.ListAsync();
        var selectedId = await _profileManager.GetSelectedProfileIdAsync();
        var currentProfileName = GetString("Dashboard.Startup.NoProfileAvailable", "No profile available");

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            AvailableProfiles.Clear();
            SelectedStartupProfile = null;

            foreach (var profile in profiles)
            {
                var vm = new DashboardProfileItemViewModel
                {
                    Id = profile.Id,
                    Name = string.IsNullOrWhiteSpace(profile.Name) ? $"Profile {profile.Id}" : profile.Name,
                    IsSelected = profile.Id == selectedId
                };
                AvailableProfiles.Add(vm);

                if (vm.IsSelected)
                {
                    SelectedStartupProfile = vm;
                    currentProfileName = vm.Name;
                }
            }

            if (SelectedStartupProfile == null && AvailableProfiles.Count > 0)
            {
                SelectedStartupProfile = AvailableProfiles[0];
                SelectedStartupProfile.IsSelected = true;
                currentProfileName = SelectedStartupProfile.Name;
            }

            CurrentProfile = currentProfileName;
        });

        if (SelectedStartupProfile != null)
        {
            await LoadRuntimeOptionsAsync(SelectedStartupProfile.Id);
        }
    }

    [RelayCommand]
    private async Task SelectStartupProfile(DashboardProfileItemViewModel? profile)
    {
        if (_profileManager == null ||
            profile == null ||
            _singBoxManager?.State.Status is not (ServiceStatus.Stopped or ServiceStatus.Error))
        {
            return;
        }

        foreach (var item in AvailableProfiles)
        {
            item.IsSelected = item.Id == profile.Id;
        }

        SelectedStartupProfile = profile;
        CurrentProfile = profile.Name;
        await _profileManager.SetSelectedProfileIdAsync(profile.Id);
        await LoadRuntimeOptionsAsync(profile.Id);
    }

    private DashboardProfileItemViewModel? GetFirstAvailableProfile()
    {
        return AvailableProfiles.Count > 0 ? AvailableProfiles[0] : null;
    }

    [RelayCommand]
    private async Task StartWithSelectedProfile()
    {
        if (_profileManager == null || _configManager == null || _singBoxManager == null)
        {
            var message = GetString("Dashboard.Startup.ServiceUnavailable", "Service unavailable");
            StartupStatus = message;
            LogError(message);
            return;
        }

        if (_kernelManager?.IsKernelInstalled != true)
        {
            StartupStatus = GetMissingKernelStartMessage();
            LogError(StartupStatus);
            return;
        }

        var target = SelectedStartupProfile ?? GetFirstAvailableProfile();
        if (target == null)
        {
            var message = GetString("Dashboard.Startup.NoProfileAvailable", "No profile available");
            StartupStatus = message;
            LogError(message);
            return;
        }

        await _profileManager.SetSelectedProfileIdAsync(target.Id);

        var profile = await _profileManager.GetAsync(target.Id);
        if (profile == null)
        {
            var message = GetString("Dashboard.Startup.ProfileNotFound", "Profile not found");
            StartupStatus = message;
            LogError($"{message}: {target.Id}");
            return;
        }

        var (configPath, deferredRefresh) = await EnsureProfileConfigPathForStartAsync(profile, target.Name);
        configPath ??= string.Empty;
        if (string.IsNullOrWhiteSpace(configPath))
        {
            StartupStatus = profile.Type == ProfileType.Remote
                ? GetString("Status.RemoteConfigUnavailable", "Remote config unavailable")
                : _localizationService["Status.ConfigMissing"];
            LogError($"{StartupStatus}: {target.Name} ({target.Id})");
            return;
        }

        if (!File.Exists(configPath))
        {
            StartupStatus = _localizationService["Status.ConfigMissing"];
            LogError($"Profile config not found: {configPath}");
            return;
        }

        CommitInboundPortEdit();
        if (!TryGetValidatedPort(out var port, out var validationError))
        {
            StartupStatus = validationError;
            LogError(validationError);
            return;
        }

        PersistRuntimePort(port);

        var runtimeConfigPath = await BuildRuntimeConfigAsync(configPath, target.Id, port);
        if (string.IsNullOrWhiteSpace(runtimeConfigPath))
        {
            return;
        }

        StartupStatus = _localizationService["Status.Starting"];
        LogInfo($"Starting with profile: {target.Name} ({target.Id})");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && EnableTunInbound)
        {
            // Only the AppImage payload is mounted read-only with nosuid: its bundled kernel
            // must be promoted to the writable data directory before setuid can be applied.
            // A portable install already sits on a writable filesystem and is authorized in place.
            if (IsLinuxAppImageRuntime() && !await PrepareLinuxKernelForTunAsync())
            {
                StartupStatus = GetString("Dashboard.Kernel.PrepareFailed", "Failed to prepare kernel for authorization");
                LogError("Linux kernel authorization failed: unable to copy the built-in kernel to the writable data directory");
                return;
            }

            if (!await _singBoxManager.IsLinuxCoreAuthorizedAsync())
            {
                var password = await ShowLinuxPasswordDialogAsync();
                if (password == null)
                {
                    StartupStatus = string.Empty;
                    return;
                }

                var (authSuccess, authError) = await _singBoxManager.AuthorizeCoreOnLinuxAsync(password);
                if (!authSuccess)
                {
                    StartupStatus = BuildLinuxAuthFailureStatus(authError);
                    LogError($"Linux kernel authorization failed: {authError}");
                    return;
                }
            }
        }

        var success = await _singBoxManager.StartAsync(runtimeConfigPath);
        if (success)
        {
            ApplyRunningSystemProxy(EnableSystemProxy);
            _runningProfileId = target.Id;
            _runningSourceConfigPath = configPath;
            if (deferredRefresh)
            {
                _ = RefreshRemoteProfileAfterStartAsync(profile, target.Name, port);
            }
        }
        StartupStatus = success ? string.Empty : BuildStartFailureStatus();
        if (!success)
        {
            LogError(StartupStatus);
        }
    }

    [RelayCommand]
    private async Task StopConnection()
    {
        if (_singBoxManager == null) return;

        StartupStatus = _localizationService["Status.Stopping"];
        LogInfo("Stopping sing-box");
        await _singBoxManager.StopAsync();
        ClearRunningProfileState();
        StartupStatus = string.Empty;
    }

    private async Task RestartForTunToggleAsync(bool targetValue, bool previousValue)
    {
        var timing = Stopwatch.StartNew();
        LogTiming($"tun_restart.begin tun={targetValue}");
        if (_singBoxManager == null ||
            _kernelManager == null ||
            _profileManager == null ||
            _configManager == null ||
            _runtimeOperation != DashboardRuntimeOperation.None)
        {
            LogTiming($"tun_restart.skipped {timing.Elapsed.TotalMilliseconds:F0}ms");
            return;
        }

        SetRuntimeOperation(DashboardRuntimeOperation.RestartingForTunChange);
        try
        {
            var profileId = _runningProfileId ?? SelectedStartupProfile?.Id;
            if (profileId == null)
            {
                await RevertTunToggleAsync(previousValue);
                StartupStatus = GetString("Dashboard.Startup.NoProfileAvailable", "No profile available");
                LogError("Failed to restart for TUN toggle: no running profile");
                LogTiming($"tun_restart.failed_no_profile {timing.Elapsed.TotalMilliseconds:F0}ms");
                return;
            }

            var prepareTiming = Stopwatch.StartNew();
            CommitInboundPortEdit();
            if (!TryGetValidatedPort(out var port, out var validationError))
            {
                await RevertTunToggleAsync(previousValue);
                StartupStatus = validationError;
                LogError(validationError);
                LogTiming($"tun_restart.failed_invalid_port {timing.Elapsed.TotalMilliseconds:F0}ms");
                return;
            }

            PersistRuntimePort(port);

            var profile = await _profileManager.GetAsync(profileId.Value);
            if (profile == null)
            {
                await RevertTunToggleAsync(previousValue);
                StartupStatus = GetString("Dashboard.Startup.ProfileNotFound", "Profile not found");
                LogError($"Failed to restart for TUN toggle: profile not found: {profileId.Value}");
                LogTiming($"tun_restart.failed_profile_not_found {timing.Elapsed.TotalMilliseconds:F0}ms");
                return;
            }

            var sourceConfigPath = _runningSourceConfigPath;
            if (string.IsNullOrWhiteSpace(sourceConfigPath) || !File.Exists(sourceConfigPath))
            {
                sourceConfigPath = await _configManager.GetConfigPathAsync(profile.Id, profile.Type);
            }

            if (string.IsNullOrWhiteSpace(sourceConfigPath) || !File.Exists(sourceConfigPath))
            {
                await RevertTunToggleAsync(previousValue);
                StartupStatus = _localizationService["Status.ConfigMissing"];
                LogError($"Failed to restart for TUN toggle: config not found for profile {profile.Id}");
                LogTiming($"tun_restart.failed_config_missing {timing.Elapsed.TotalMilliseconds:F0}ms");
                return;
            }
            LogTiming($"tun_restart.prepare {prepareTiming.Elapsed.TotalMilliseconds:F0}ms");

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && targetValue)
            {
                // Only the AppImage payload needs its kernel promoted to a writable location
                // before setuid can be applied; portable installs are authorized in place.
                if (IsLinuxAppImageRuntime() && !await PrepareLinuxKernelForTunAsync())
                {
                    await RevertTunToggleAsync(previousValue);
                    StartupStatus = GetString("Dashboard.Kernel.PrepareFailed", "Failed to prepare kernel for authorization");
                    LogError("Linux kernel authorization failed during TUN toggle: unable to copy the built-in kernel to the writable data directory");
                    LogTiming($"tun_restart.failed_kernel_prepare {timing.Elapsed.TotalMilliseconds:F0}ms");
                    return;
                }

                if (!await _singBoxManager.IsLinuxCoreAuthorizedAsync())
                {
                    var password = await ShowLinuxPasswordDialogAsync();
                    if (password == null)
                    {
                        await RevertTunToggleAsync(previousValue);
                        StartupStatus = string.Empty;
                        return;
                    }

                    var (authSuccess, authError) = await _singBoxManager.AuthorizeCoreOnLinuxAsync(password);
                    if (!authSuccess)
                    {
                        await RevertTunToggleAsync(previousValue);
                        StartupStatus = BuildLinuxAuthFailureStatus(authError);
                        LogError($"Linux kernel authorization failed during TUN toggle: {authError}");
                        return;
                    }
                }
            }

            var runtimeConfigPath = await BuildRuntimeConfigAsync(sourceConfigPath, profile.Id, port);
            LogTiming($"tun_restart.build_runtime_config {timing.Elapsed.TotalMilliseconds:F0}ms");
            if (string.IsNullOrWhiteSpace(runtimeConfigPath))
            {
                await RevertTunToggleAsync(previousValue);
                return;
            }

            StartupStatus = GetString("Dashboard.Status.RestartingForTun", "Restarting kernel to apply TUN change...");
            LogInfo($"Restarting sing-box to apply TUN change: tun={targetValue}");

            var stopTiming = Stopwatch.StartNew();
            await _singBoxManager.StopAsync();
            LogTiming($"tun_restart.stop {stopTiming.Elapsed.TotalMilliseconds:F0}ms");
            var startTiming = Stopwatch.StartNew();
            var success = await _singBoxManager.StartAsync(runtimeConfigPath);
            LogTiming($"tun_restart.start {startTiming.Elapsed.TotalMilliseconds:F0}ms");
            if (success)
            {
                ApplyRunningSystemProxy(EnableSystemProxy);
                _runningProfileId = profile.Id;
                _runningSourceConfigPath = sourceConfigPath;
                StartupStatus = string.Empty;
                LogTiming($"tun_restart.end_success {timing.Elapsed.TotalMilliseconds:F0}ms");
                return;
            }

            LogError(BuildStartFailureStatus());
            await RestoreTunToggleAfterRestartFailureAsync(previousValue, sourceConfigPath, profile, port);
            LogTiming($"tun_restart.end_restore_attempted {timing.Elapsed.TotalMilliseconds:F0}ms");
        }
        finally
        {
            SetRuntimeOperation(DashboardRuntimeOperation.None);
        }
    }

    private async Task RestoreTunToggleAfterRestartFailureAsync(bool previousValue, string sourceConfigPath, Profile profile, int port)
    {
        await RevertTunToggleAsync(previousValue);
        var failedStatus = BuildStartFailureStatus();
        LogWarning($"TUN toggle restart failed, attempting to restore previous state: {failedStatus}");

        var fallbackConfigPath = await BuildRuntimeConfigAsync(sourceConfigPath, profile.Id, port);
        if (string.IsNullOrWhiteSpace(fallbackConfigPath))
        {
            StartupStatus = failedStatus;
            return;
        }

        var restored = await _singBoxManager!.StartAsync(fallbackConfigPath);
        if (restored)
        {
            ApplyRunningSystemProxy(EnableSystemProxy);
            _runningProfileId = profile.Id;
            _runningSourceConfigPath = sourceConfigPath;
            StartupStatus = GetString("Dashboard.Status.TunRestartFailedRestored", "Failed to apply TUN change; restored previous running state.");
            LogWarning(StartupStatus);
            _toastWriter?.Invoke(StartupStatus, 3200);
            return;
        }

        ClearRunningProfileState();
        StartupStatus = failedStatus;
    }

    private async Task RevertTunToggleAsync(bool previousValue)
    {
        _suppressRuntimeOptionUpdates = true;
        _runtimeOptions.EnableTunInbound = previousValue;
        EnableTunInbound = previousValue;
        _suppressRuntimeOptionUpdates = false;

        if (_profileManager != null && SelectedStartupProfile != null)
        {
            var snapshot = CopyRuntimeOptions(_runtimeOptions);
            await _profileManager.SaveRuntimeOptionsAsync(SelectedStartupProfile.Id, snapshot);
        }
    }

    private void SetRuntimeOperation(DashboardRuntimeOperation operation)
    {
        if (_runtimeOperation == operation)
        {
            return;
        }

        _runtimeOperation = operation;
        OnPropertyChanged(nameof(CanUseDashboardControls));
        OnPropertyChanged(nameof(CanToggleTunInbound));
    }

    [Conditional("DEBUG")]
    private void LogTiming(string message)
    {
        LogInfo($"[TIMING] {message}");
    }

    private void ClearRunningProfileState()
    {
        _runningProfileId = null;
        _runningSourceConfigPath = null;
    }

    [RelayCommand]
    private async Task ToggleConnection()
    {
        if (IsConnected)
        {
            await StopConnection();
            return;
        }

        await StartWithSelectedProfile();
    }

    [RelayCommand]
    private async Task ChangeMode(DashboardModeOptionViewModel? option)
    {
        if (option == null || string.IsNullOrWhiteSpace(option.Mode))
        {
            return;
        }

        if (string.Equals(_currentMode, option.Mode, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var success = await SetModeAsync(option.Mode);
        if (success)
        {
            // The manager updates its push-stream cache and raises ModeChanged,
            // which refreshes the UI selection - no hand-built cache snapshot needed.
        }
        else
        {
            await RefreshModeAsync();
        }
    }

    public void CommitInboundPortEdit()
    {
        if (!TryGetValidatedPort(out var port, out var error))
        {
            StartupStatus = error;
            InboundPortText = _runtimeOptions.InboundPort.ToString();
            return;
        }

        InboundPortText = port.ToString();
        PersistRuntimePort(port);
        StartupStatus = string.Empty;
    }

    private void LogInfo(string message)
    {
        _logWriter?.Invoke($"[DEBUG] {message}");
    }

    private void LogError(string message)
    {
        _logWriter?.Invoke($"[ERROR] {message}");
    }

    private async Task ReportDeprecatedWarningsAsync()
    {
        if (_singBoxManager == null)
        {
            return;
        }

        try
        {
            var warnings = await _singBoxManager.GetDeprecatedWarningsAsync();
            if (warnings.Count == 0)
            {
                return;
            }

            foreach (var warning in warnings)
            {
                // Surface kernel config deprecations (e.g. legacy clash_api fields) once
                // per running session so users can migrate before the removal version.
                var suffix = string.IsNullOrWhiteSpace(warning.MigrationLink)
                    ? string.Empty
                    : $" (migration: {warning.MigrationLink})";
                LogWarning($"[sing-box] Deprecated config: {warning.Message}{suffix}");
            }
        }
        catch
        {
            // Deprecation reporting is best-effort; never disturb the startup flow.
        }
    }

    private void LogWarning(string message)
    {
        _logWriter?.Invoke($"[WARN] {message}");
    }

    private void ShowTransientStatus(string message, int durationMilliseconds = 2000)
    {
        StartupStatus = message;
        _ = Task.Delay(durationMilliseconds).ContinueWith(_ =>
            Dispatcher.UIThread.Post(() =>
            {
                if (StartupStatus == message)
                {
                    StartupStatus = string.Empty;
                }
            }));
    }

    /// <summary>
    /// Detects the standard AppImage runtime environment, which exports the absolute path of
    /// the running .AppImage file via the APPIMAGE variable. Some environments (e.g. AppImageLauncher)
    /// also export it, but a nonexistent path is treated as not running from an AppImage.
    /// </summary>
    private static bool IsLinuxAppImageRuntime()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return false;
        }

        var appImagePath = Environment.GetEnvironmentVariable("APPIMAGE");
        return !string.IsNullOrWhiteSpace(appImagePath) && File.Exists(appImagePath);
    }

    /// <summary>
    /// Points the manager at a kernel that can actually be authorized for TUN. On an AppImage
    /// the bundled kernel sits on a read-only, nosuid mount, so it is promoted to the writable
    /// data directory first. The running kernel is left alone: promotion only ever renames a
    /// fresh copy into place, so it stays valid to call before stopping.
    /// </summary>
    private async Task<bool> PrepareLinuxKernelForTunAsync()
    {
        if (_kernelManager == null || _singBoxManager == null)
        {
            return false;
        }

        if (!await _kernelManager.EnsureWritableKernelAsync())
        {
            return false;
        }

        _singBoxManager.UpdateKernelPath(_kernelManager.KernelPath);

        // Only refreshes the version shown in the UI, so it is kept off the startup path.
        _ = _kernelManager.GetInstalledKernelInfoAsync();
        return true;
    }

    /// <summary>
    /// Keeps the reason on screen instead of only in the log. "Failed to authorize kernel" on
    /// its own gives no way to tell a rejected password from sudo being unavailable.
    /// </summary>
    private string BuildLinuxAuthFailureStatus(string? authError)
    {
        var status = GetString("Dashboard.Auth.Failed", "Failed to authorize kernel");
        return string.IsNullOrWhiteSpace(authError)
            ? status
            : $"{status}: {authError.Trim().ReplaceLineEndings(" ")}";
    }

    private async Task<string?> ShowLinuxPasswordDialogAsync()
    {
        var desktop = Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
        var window = desktop?.MainWindow;
        if (window == null) return null;

        var dialog = new Avalonia.Controls.Window
        {
            Width = 400,
            Height = 190,
            CanResize = false,
            WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.CenterOwner,
            Title = GetString("Dashboard.Auth.PasswordTitle", "Authorization Required")
        };

        var prompt = new Avalonia.Controls.TextBlock
        {
            Text = GetString("Dashboard.Auth.PasswordPrompt",
                "Enter your password to authorize TUN mode (setuid will be set on sing-box)"),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(0, 0, 0, 8)
        };

        var passwordBox = new Avalonia.Controls.TextBox
        {
            PasswordChar = '\u2022',
            Watermark = "Password",
            Margin = new Avalonia.Thickness(0, 0, 0, 16)
        };

        var okBtn = new Avalonia.Controls.Button
        {
            Content = GetString("Settings.Data.StoreInAppDir.ConfirmButton", "Confirm"),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            MinWidth = 110
        };
        okBtn.Click += (_, _) => dialog.Close(passwordBox.Text);

        var cancelBtn = new Avalonia.Controls.Button
        {
            Content = GetString("Profiles.Form.CancelButton", "Cancel"),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            MinWidth = 110
        };
        cancelBtn.Click += (_, _) => dialog.Close(null);

        var buttons = new Avalonia.Controls.StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right
        };
        buttons.Children.Add(cancelBtn);
        buttons.Children.Add(okBtn);

        dialog.Content = new Avalonia.Controls.StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Children = { prompt, passwordBox, buttons }
        };

        return await dialog.ShowDialog<string?>(window);
    }

    private async Task<string?> ShowInboundPortDialogAsync()
    {
        var desktop = Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
        var window = desktop?.MainWindow;
        if (window == null)
        {
            return null;
        }

        var dialog = new Avalonia.Controls.Window
        {
            Width = 380,
            Height = 180,
            CanResize = false,
            WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.CenterOwner,
            Title = GetString("Dashboard.Port.Edit", "Edit Port")
        };

        var prompt = new Avalonia.Controls.TextBlock
        {
            Text = GetString("Dashboard.Field.Port", "Mixed Port"),
            Margin = new Avalonia.Thickness(0, 0, 0, 8)
        };

        var portBox = new Avalonia.Controls.TextBox
        {
            Text = InboundPortText,
            Watermark = GetString("Dashboard.Field.Port", "Mixed Port"),
            Margin = new Avalonia.Thickness(0, 0, 0, 16)
        };

        var okBtn = new Avalonia.Controls.Button
        {
            Content = GetString("Settings.Data.StoreInAppDir.ConfirmButton", "Confirm"),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            MinWidth = 110
        };

        var cancelBtn = new Avalonia.Controls.Button
        {
            Content = GetString("Profiles.Form.CancelButton", "Cancel"),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            MinWidth = 110
        };

        cancelBtn.Click += (_, _) => dialog.Close(null);
        okBtn.Click += (_, _) =>
        {
            var candidate = portBox.Text?.Trim() ?? string.Empty;
            if (!int.TryParse(candidate, out var port) || port is < 1 or > 65535)
            {
                StartupStatus = GetString("Dashboard.Startup.PortOutOfRange", "Port must be between 1 and 65535");
                return;
            }

            dialog.Close(port.ToString());
        };

        var buttons = new Avalonia.Controls.StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right
        };
        buttons.Children.Add(cancelBtn);
        buttons.Children.Add(okBtn);

        dialog.Content = new Avalonia.Controls.StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 8,
            Children =
            {
                prompt,
                portBox,
                buttons
            }
        };

        return await dialog.ShowDialog<string?>(window);
    }

    private async Task<(string? ConfigPath, bool DeferredRefresh)> EnsureProfileConfigPathForStartAsync(Profile profile, string profileName)
    {
        var configPath = await _configManager!.GetConfigPathAsync(profile.Id, profile.Type);
        var hasLocalConfig = !string.IsNullOrWhiteSpace(configPath) && File.Exists(configPath);
        if (hasLocalConfig && !RemoteConfigUpdateService.ShouldRefreshOnStart(profile))
        {
            return (configPath, false);
        }

        if (profile.Type != ProfileType.Remote)
        {
            return (null, false);
        }

        if (string.IsNullOrWhiteSpace(profile.Url))
        {
            StartupStatus = GetString("Status.RemoteProfileUrlEmpty", "Remote profile URL is empty");
            LogError($"{StartupStatus}: {profileName} ({profile.Id})");
            return (null, false);
        }

        if (hasLocalConfig && _remoteConfigUpdateService?.ShouldDeferRefreshUntilStarted(profile, hasLocalConfig) == true)
        {
            StartupStatus = GetString(
                "Status.RemoteConfigRefreshDeferred",
                "Remote config is due for update. Starting with the local cached config first and refreshing after sing-box starts.");
            LogInfo($"{StartupStatus}: {profileName} ({profile.Id})");
            return (configPath, true);
        }

        var loadingMessage = hasLocalConfig
            ? GetString("Status.RemoteConfigRefreshing", "Remote config due for update, refreshing...")
            : GetString("Status.RemoteConfigDownloading", "Remote config missing, downloading...");
        StartupStatus = loadingMessage;
        LogInfo($"{loadingMessage}: {profileName} ({profile.Id}) URL: {profile.Url}");

        try
        {
            var result = await _remoteConfigUpdateService!.UpdateAsync(
                profile,
                progress: (bytesReceived, totalBytes) =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        StartupStatus = DownloadUiHelper.FormatStatus(
                            loadingMessage,
                            bytesReceived,
                            totalBytes,
                            GetString("Common.Unknown", "unknown"));
                    });
                });
            if (!result.Success || string.IsNullOrWhiteSpace(result.ConfigPath))
            {
                var message = GetString("Status.RemoteConfigDownloadFailed", "Failed to download remote config");
                return (HandleRemoteConfigRefreshFailure(profile, profileName, configPath, hasLocalConfig, $"{message}: {result.ErrorMessage}"), false);
            }

            var completedMessage = hasLocalConfig
                ? GetString("Status.RemoteConfigRefreshed", "Remote config refreshed")
                : GetString("Status.RemoteConfigDownloaded", "Remote config downloaded");
            StartupStatus = completedMessage;
            LogInfo($"{completedMessage}: {profileName} ({profile.Id})");
            await NotifyProfilesChangedAsync();
            return (result.ConfigPath, false);
        }
        catch (Exception ex)
        {
            var message = GetString("Status.RemoteConfigDownloadFailed", "Failed to download remote config");
            return (HandleRemoteConfigRefreshFailure(
                profile,
                profileName,
                configPath,
                hasLocalConfig,
                $"{message}: {ex.Message}"), false);
        }
    }

    private string? HandleRemoteConfigRefreshFailure(
        Profile profile,
        string profileName,
        string? existingConfigPath,
        bool hasLocalConfig,
        string errorMessage)
    {
        if (hasLocalConfig && !string.IsNullOrWhiteSpace(existingConfigPath) && File.Exists(existingConfigPath))
        {
            var warning = GetString("Status.RemoteConfigRefreshFailedUsingLocal", "Remote config refresh failed, using local cached config");
            StartupStatus = $"{warning}: {errorMessage}";
            LogWarning($"{errorMessage}: {profileName} ({profile.Id}); {warning}");
            return existingConfigPath;
        }

        StartupStatus = errorMessage;
        LogError($"{errorMessage}: {profileName} ({profile.Id})");
        return null;
    }

    private async Task RefreshRemoteProfileAfterStartAsync(Profile profile, string profileName, int mixedPort)
    {
        if (_remoteConfigUpdateService == null)
        {
            return;
        }

        var loadingMessage = GetString(
            "Status.RemoteConfigRefreshingViaProxy",
            "Refreshing remote config via the local mixed proxy...");
        StartupStatus = loadingMessage;
        LogInfo($"{loadingMessage}: {profileName} ({profile.Id})");
        _toastWriter?.Invoke(loadingMessage, 1800);

        var latestProfile = await _profileManager!.GetAsync(profile.Id) ?? profile;
        var result = await _remoteConfigUpdateService.UpdateAsync(
            latestProfile,
            mixedPort,
            (bytesReceived, totalBytes) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    StartupStatus = DownloadUiHelper.FormatStatus(
                        loadingMessage,
                        bytesReceived,
                        totalBytes,
                        GetString("Common.Unknown", "unknown"));
                });
            });
        if (result.Success)
        {
            await LoadProfilesAsync();
            await NotifyProfilesChangedAsync();
            var completedMessage = GetString(
                "Status.RemoteConfigRefreshedViaProxy",
                "Remote config refreshed via the local mixed proxy.");
            LogInfo($"{completedMessage}: {profileName} ({profile.Id})");
            ShowTransientStatus(completedMessage, 3000);
            _toastWriter?.Invoke(completedMessage, 2600);
            return;
        }

        var failedMessage = $"{GetString("Status.RemoteConfigRefreshViaProxyFailed", "Failed to refresh remote config via the local mixed proxy")}: {result.ErrorMessage}";
        StartupStatus = failedMessage;
        LogWarning($"{failedMessage}: {profileName} ({profile.Id})");
        _toastWriter?.Invoke(failedMessage, 3200);
    }

    private async Task LoadRuntimeOptionsAsync(int profileId)
    {
        if (_profileManager == null)
        {
            ApplyRuntimeOptions(new ProfileRuntimeOptions());
            return;
        }

        try
        {
            var options = await _profileManager.GetRuntimeOptionsAsync(profileId);
            ApplyRuntimeOptions(options ?? new ProfileRuntimeOptions());
        }
        catch
        {
            ApplyRuntimeOptions(new ProfileRuntimeOptions());
        }
    }

    private void ApplyRuntimeOptions(ProfileRuntimeOptions options)
    {
        _suppressRuntimeOptionUpdates = true;
        _runtimeOptions = options ?? new ProfileRuntimeOptions();
        InboundPortText = _runtimeOptions.InboundPort.ToString();
        AllowLanConnections = _runtimeOptions.AllowLanConnections;
        EnableSystemProxy = _runtimeOptions.EnableSystemProxy;
        EnableTunInbound = _runtimeOptions.EnableTunInbound;
        SelectedLogLevel = NormalizeLogLevel(_runtimeOptions.LogLevel);
        _suppressRuntimeOptionUpdates = false;
    }

    private void UpdateRuntimeOptions(Action<ProfileRuntimeOptions> updater)
    {
        updater(_runtimeOptions);

        if (_suppressRuntimeOptionUpdates || _profileManager == null || SelectedStartupProfile == null)
        {
            return;
        }

        var snapshot = CopyRuntimeOptions(_runtimeOptions);
        _ = _profileManager.SaveRuntimeOptionsAsync(SelectedStartupProfile.Id, snapshot);
    }

    private void PersistRuntimePort(int port)
    {
        UpdateRuntimeOptions(options => options.InboundPort = port);
    }

    private void ApplyRunningSystemProxy(bool enabled)
    {
        if (_singBoxManager == null)
        {
            return;
        }

        try
        {
            if (enabled)
            {
                var port = _runtimeOptions.InboundPort is >= 1 and <= 65535
                    ? _runtimeOptions.InboundPort
                    : 2028;
                SystemProxyHelper.SetSystemProxy("127.0.0.1", port);
            }
            else
            {
                SystemProxyHelper.ClearSystemProxy();
            }

            _singBoxManager.NotifySystemProxyEnabled(enabled);
        }
        catch (Exception ex)
        {
            StartupStatus = $"{GetString("Dashboard.Status.SystemProxyToggleFailed", "Failed to toggle system proxy")}: {ex.Message}";
            LogError($"Failed to toggle system proxy: {ex.Message}");

            _suppressSystemProxyApply = true;
            EnableSystemProxy = !enabled;
            _suppressSystemProxyApply = false;
        }
    }

    private static ProfileRuntimeOptions CopyRuntimeOptions(ProfileRuntimeOptions options)
    {
        return new ProfileRuntimeOptions
        {
            InboundPort = options.InboundPort,
            AllowLanConnections = options.AllowLanConnections,
            EnableSystemProxy = options.EnableSystemProxy,
            EnableTunInbound = options.EnableTunInbound,
            LogLevel = NormalizeLogLevel(options.LogLevel),
            LogLevelInitialized = true,
            Initialized = true
        };
    }

    private async Task<string?> BuildRuntimeConfigAsync(string sourceConfigPath, int profileId, int port)
    {
        try
        {
            var parsed = JsonNode.Parse(await File.ReadAllTextAsync(sourceConfigPath));
            if (parsed is not JsonObject root)
            {
                StartupStatus = GetString("Dashboard.Startup.InvalidConfigJson", "Invalid profile config JSON");
                LogError("Invalid profile config JSON");
                return null;
            }

            var inbounds = root["inbounds"] as JsonArray ?? new JsonArray();

            // 找到已有的 mixed inbound，只更新需要的字段
            JsonObject? mixedInbound = null;
            foreach (var node in inbounds)
            {
                if (node is JsonObject obj && obj["type"]?.GetValue<string>() == "mixed")
                {
                    mixedInbound = obj;
                    break;
                }
            }
            if (mixedInbound == null)
            {
                mixedInbound = new JsonObject { ["type"] = "mixed", ["tag"] = "mixed-in" };
                inbounds.Add((JsonNode)mixedInbound);
            }
            mixedInbound["listen"] = AllowLanConnections ? "0.0.0.0" : "127.0.0.1";
            mixedInbound["listen_port"] = port;
            // On Linux, a setuid sing-box runs with AT_SECURE and cannot safely
            // invoke gsettings / kwriteconfig helpers, so carton must manage the
            // desktop proxy itself after startup. Keep the original behavior on
            // other platforms.
            mixedInbound["set_system_proxy"] = RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                ? false
                : EnableSystemProxy;

            if (EnableTunInbound)
            {
                // 找到已有的 tun inbound，优先使用配置中的 address
                JsonObject? tunInbound = null;
                foreach (var node in inbounds)
                {
                    if (node is JsonObject obj && obj["type"]?.GetValue<string>() == "tun")
                    {
                        tunInbound = obj;
                        break;
                    }
                }
                if (tunInbound == null)
                {
                    tunInbound = new JsonObject { ["type"] = "tun", ["tag"] = "tun-in" };
                    inbounds.Add((JsonNode)tunInbound);
                }
                TunInboundDefaults.Apply(tunInbound, Socket.OSSupportsIPv6);
            }
            else
            {
                // 如果禁用 tun，移除已有的 tun inbound
                for (var i = inbounds.Count - 1; i >= 0; i--)
                {
                    if (inbounds[i] is JsonObject o && o["type"]?.GetValue<string>() == "tun")
                    {
                        inbounds.RemoveAt(i);
                        break;
                    }
                }
            }

            root["inbounds"] = inbounds;
            ApplyRuntimeLogLevel(root, NormalizeLogLevel(_runtimeOptions.LogLevel));

            var services = root["services"] as JsonArray ?? new JsonArray();
            root["services"] = services;

            var apiService = GetOrCreateApiService(services);
            var nativeApiPort = 0;
            var hasConfiguredNativeApiPort = false;
            if (TryReadJsonInt(apiService, "listen_port", out var servicePort) &&
                IsValidPort(servicePort))
            {
                hasConfiguredNativeApiPort = true;
                nativeApiPort = servicePort;
            }

            var nativeApiSecret = string.Empty;
            if (TryReadJsonString(apiService, "secret", out var serviceSecret))
            {
                nativeApiSecret = serviceSecret;
            }

            // Cross-inherit secrets like the legacy path did: an explicit clash_api secret
            // authorizes the native API too, and vice versa - both fronts should agree.
            var experimentalForSecrets = root["experimental"] as JsonObject;
            if (string.IsNullOrWhiteSpace(nativeApiSecret) &&
                experimentalForSecrets?["clash_api"] is JsonObject existingModeBackend &&
                TryReadJsonString(existingModeBackend, "secret", out var existingModeSecret))
            {
                nativeApiSecret = existingModeSecret;
            }

            var portPlan = ApiPortPlanner.Resolve(
                DefaultSingBoxApiPort,
                SingBoxDashboardBootstrapService.PreferredPort,
                hasConfiguredNativeApiPort,
                nativeApiPort);
            nativeApiPort = portPlan.NativeApiPort;

            apiService["type"] = "api";

            // tag/listen: only defaulted on carton's OWN block (user blocks keep theirs).
            if (!TryReadJsonString(apiService, "tag", out _))
            {
                apiService["tag"] = "carton-api";
            }

            if (!TryReadJsonString(apiService, "listen", out var nativeApiListen) ||
                string.IsNullOrWhiteSpace(nativeApiListen))
            {
                apiService["listen"] = "127.0.0.1";
            }

            // listen_port: always written back, but with the user's own value whenever
            // their block has one (carton needs it to connect); carton allocates a free
            // port (9091+) only when absent.
            apiService["listen_port"] = nativeApiPort;

            // secret: NEVER injected, not even into carton's own block. sing-box runs
            // fine unauthenticated (secret == "" disables the auth check). The only
            // secret carton ever knows is one READ from the user's own config
            // (services.api.secret / clash_api.secret, whichever exists); when the user
            // configures none, carton stores an empty value and connects anonymously.

            // CORS allow-list: missing entries are complemented everywhere (it only
            // enables the built-in dashboard/WebUI button from the browser - a
            // functional dependency with no security impact). User-configured lists
            // are preserved verbatim.
            // CORS allow-list, by TYPE (arrays throw on GetValue<string>, so never
            // use TryReadJsonString here): a user-configured value - array OR single
            // string (badoption.Listable[string] accepts both) - is preserved verbatim
            // with a warning when it cannot serve the built-in dashboard; only a
            // missing/invalid entry receives carton's default list.
            // Configure() runs unconditionally first: it is a singleton whose side
            // effect (UpdateServer) keeps the dashboard bootstrap pointing at the
            // CURRENT api port/secret even when the user's own list is kept.
            var dashboardBootstrap = SingBoxDashboardBootstrapService.Configure(
                nativeApiPort,
                nativeApiSecret,
                LogWarning,
                nativeApiPort,
                nativeApiPort);

            if (apiService["access_control_allow_origin"] is JsonArray userAllowList)
            {
                var servesDashboard = false;
                foreach (var origin in userAllowList)
                {
                    if (origin is not JsonValue originValue ||
                        !originValue.TryGetValue<string>(out var originText))
                    {
                        continue;
                    }

                    if (originText.Contains("sing-box-dashboard.sagernet.org", StringComparison.OrdinalIgnoreCase) ||
                        originText.Contains("dash.sing-box.app", StringComparison.OrdinalIgnoreCase))
                    {
                        servesDashboard = true;
                        break;
                    }
                }

                if (!servesDashboard)
                {
                    LogWarning("User api service access_control_allow_origin does not include the official dashboard origins; the WebUI button may not be able to reach the API");
                }
            }
            else if (apiService["access_control_allow_origin"] is JsonValue userAllowOrigin &&
                     userAllowOrigin.TryGetValue<string>(out var userOriginText))
            {
                // Single-string form (badoption.Listable[string]): same sovereignty, same warning.
                if (!userOriginText.Contains("sing-box-dashboard.sagernet.org", StringComparison.OrdinalIgnoreCase) &&
                    !userOriginText.Contains("dash.sing-box.app", StringComparison.OrdinalIgnoreCase))
                {
                    LogWarning("User api service access_control_allow_origin does not include the official dashboard origins; the WebUI button may not be able to reach the API");
                }
            }
            else
            {
                apiService["access_control_allow_origin"] = new JsonArray(
                    (JsonNode)"http://sing-box-dashboard.sagernet.org",
                    (JsonNode)"https://sing-box-dashboard.sagernet.org",
                    (JsonNode)"http://dash.sing-box.app",
                    (JsonNode)"https://dash.sing-box.app",
                    (JsonNode)dashboardBootstrap.Origin);
            }

            if (!apiService.ContainsKey("access_control_allow_private_network"))
            {
                apiService["access_control_allow_private_network"] = true;
            }

            var experimental = root["experimental"] as JsonObject ?? new JsonObject();
            root["experimental"] = experimental;

            // Minimal-change principle: a user-provided clash_api block is passed through
            // UNTOUCHED - no field is added, removed or overwritten, even if fields are
            // missing (the kernel defaults apply on its own). Only when the block does
            // not exist at all does carton create one, with the mode-backend minimum.
            // external_controller is "" as an EXPLICIT statement of intent: v1.14 treats
            // absent and "" identically (bool = value != "" -> no REST listener either
            // way), so this is a documentation pin, not a necessity. No secret is written
            // even here: sing-box runs fine unauthenticated, and a secret is never
            // forced onto the config.
            if (experimental.ContainsKey("clash_api") &&
                experimental["clash_api"] is JsonObject userModeBackend &&
                userModeBackend["external_controller"] is JsonValue modeController &&
                modeController.TryGetValue<string>(out var modeControllerText) &&
                !string.IsNullOrWhiteSpace(modeControllerText))
            {
                // Sovereignty honored, but a non-empty external_controller in the user's
                // block means the kernel WILL open a REST listener there (the user asked
                // for it) - log it so "why is 9090 open" is answerable from the log.
                LogWarning($"User clash_api.external_controller is set; the kernel will open a REST listener at {modeControllerText}");
            }

            if (!experimental.ContainsKey("clash_api") ||
                experimental["clash_api"] is not JsonObject)
            {
                var modeBackend = new JsonObject
                {
                    ["external_controller"] = "",
                    ["default_mode"] = "rule"
                };
                experimental["clash_api"] = modeBackend;
            }
            // else: user block kept verbatim - see the minimal-change principle above.

            HttpClientFactory.UpdateLocalApi(
                "127.0.0.1",
                nativeApiPort,
                nativeApiSecret);
            HttpClientFactory.UpdateLocalNativeApi("127.0.0.1", nativeApiPort, nativeApiSecret);
            OnPropertyChanged(nameof(CanOpenSingBoxWebUi));
            // Persist the endpoint so a restarted carton can re-attach to this kernel
            // (see MainViewModel.InitializeAsync restore).
            if (_preferencesService != null)
            {
                var preferences = _preferencesService.Load();
                preferences.LastNativeApiPort = nativeApiPort;
                // If DPAPI ever fails, keep the previous stored value rather than writing
                // the secret to disk in plaintext.
                if (carton.Core.Services.SecretProtector.TryProtect(nativeApiSecret, out var protectedSecret))
                {
                    preferences.LastNativeApiSecret = protectedSecret;
                }
                _preferencesService.Save(preferences);
            }

    
            // Same minimal-change principle as clash_api above: a user-provided
            // cache_file block passes through untouched. Only when absent does carton
            // create its standard block (fakeip persistence + store_dns, the latter
            // being a 1.14 field - never written into a user's own block).
            if (!experimental.ContainsKey("cache_file") ||
                experimental["cache_file"] is not JsonObject)
            {
                experimental["cache_file"] = new JsonObject
                {
                    ["enabled"] = true,
                    ["path"] = "cache.db",
                    ["store_fakeip"] = true,
                    ["store_dns"] = true
                };
            }
            else if (experimental["cache_file"] is JsonObject userCacheFile &&
                     userCacheFile["enabled"] is JsonValue cacheEnabled &&
                     cacheEnabled.TryGetValue<bool>(out var cacheEnabledValue) &&
                     !cacheEnabledValue)
            {
                // Sovereignty honored, but a disabled cache file degrades carton
                // features silently (selected nodes / fakeip no longer persist across
                // restarts) - surface it once at startup.
                LogWarning("User cache_file is disabled; selected nodes and fakeip mappings will not persist across kernel restarts");
            }
            // else: user cache_file kept verbatim.

            var runtimeDirectory = _configManager!.RuntimeConfigDirectory;
            Directory.CreateDirectory(runtimeDirectory);
            var runtimeConfigPath = Path.Combine(runtimeDirectory, $"profile_{profileId}.runtime.json");


            var json = root.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = carton.Core.Utilities.UnicodeJsonEncoder.Instance
            });
            await File.WriteAllTextAsync(runtimeConfigPath, json);
            LogInfo($"Runtime inbounds prepared: port={port}, lan={AllowLanConnections}, systemProxy={EnableSystemProxy}, tun={EnableTunInbound}, logLevel={NormalizeLogLevel(_runtimeOptions.LogLevel)}");
            return runtimeConfigPath;
        }
        catch (Exception ex)
        {
            var message = GetString("Dashboard.Startup.UpdateInboundsFailed", "Failed to update inbounds");
            StartupStatus = $"{message}: {ex.Message}";
            LogError($"Failed to update inbounds: {ex.Message}");
            return null;
        }
    }

    private int _modeRefreshInFlight;

    private async Task RefreshModeAsync()
    {
        // Coalesce concurrent refreshes: rapid mode pushes (e.g. before the mode list
        // lands) must not fan out into parallel API round-trips.
        if (Interlocked.CompareExchange(ref _modeRefreshInFlight, 1, 0) != 0)
        {
            return;
        }

        try
        {
            if (!_proxyModeCache.TryGetFresh(ModeCacheDuration, out var config))
            {
                config = await GetModeConfigFromApiAsync();
            }

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                ApplyModeOptions(config?.ModeList, config?.Mode);
                UpdateModeSelection(config?.Mode);
            });
        }
        finally
        {
            Volatile.Write(ref _modeRefreshInFlight, 0);
        }
    }

    private void ResetTrafficDisplay()
    {
        ResetTrafficHistory();
        ApplyTrafficMetrics(0, 0, 0, 0, false);
        ApplyMemoryUsage(0);
    }

    private void ResetKernelStatusDisplay()
    {
        GoroutineCount = "--";
        ConnectionsIn = "--";
        ConnectionsOut = "--";
    }

    private async Task RefreshKernelVersionAsync()
    {
        if (_kernelManager == null)
        {
            return;
        }

        try
        {
            var info = await _kernelManager.GetInstalledKernelInfoAsync();
            ApplyInstalledKernelInfo(info);
        }
        catch
        {
            ApplyInstalledKernelInfo(null);
        }
    }

    private void OnInstalledKernelChanged(object? sender, KernelInfo? kernelInfo)
    {
        Dispatcher.UIThread.Post(() => ApplyInstalledKernelInfo(kernelInfo));
    }

    private void ApplyInstalledKernelInfo(KernelInfo? kernelInfo)
    {
        KernelVersion = kernelInfo?.KernelVersion ?? GetString("Common.Unknown", CartonApplicationInfo.UnknownSingBoxVersion);
    }

    private bool CanRefreshLiveMetrics()
    {
        return _singBoxManager is { IsRunning: true } && _isOnPage && _isWindowVisible;
    }

    private void UpdateLiveRefreshState()
    {
        var shouldRefresh = CanRefreshLiveMetrics();
        if (_isLiveRefreshActive == shouldRefresh)
        {
            return;
        }

        _isLiveRefreshActive = shouldRefresh;
        if (shouldRefresh)
        {
            RefreshVisibleDashboardData();
        }
    }

    private void RefreshVisibleDashboardData()
    {
        if (!CanRefreshLiveMetrics())
        {
            return;
        }

        InitializeTrafficMetrics();
        InitializeMemoryMetrics();
        var state = _singBoxManager?.State;
        ApplyKernelStatusMetrics(
            state?.Goroutines ?? 0,
            state?.ConnectionsIn ?? 0,
            state?.ConnectionsOut ?? 0);
        _ = RefreshModeAsync();
        _ = RefreshConnectivityCoreAsync(force: false);
    }

    private void ApplyTrafficMetrics(long uploadSpeed, long downloadSpeed, long totalUpload, long totalDownload, bool updateHistory = true)
    {
        UploadSpeed = FormatBytesPerSecond(uploadSpeed);
        DownloadSpeed = FormatBytesPerSecond(downloadSpeed);
        TotalUpload = FormatBytes(totalUpload);
        TotalDownload = FormatBytes(totalDownload);

        // Skip sparkline updates when traffic is idle (sustained zero). The sparkline
        // chart is a 60-sample rolling window; if we keep appending zeros every second
        // we burn CPU, allocate/notify on every ObservableCollection mutation, and the
        // UI redraws a flat line that looks identical to a static flat line. This also
        // reduces GC pressure from the constant churn of small int[] arrays backing the
        // samples. When traffic resumes, the sparkline starts moving again immediately.
        if (updateHistory && !(uploadSpeed == 0 && downloadSpeed == 0 && _lastUploadSpeed == 0 && _lastDownloadSpeed == 0))
        {
            UpdateTrafficHistory(uploadSpeed, downloadSpeed);
        }

        _lastUploadSpeed = uploadSpeed;
        _lastDownloadSpeed = downloadSpeed;
    }

    private void UpdateTrafficHistory(long uploadSpeed, long downloadSpeed)
    {
        AppendTrafficSample(UploadTrafficSamples, uploadSpeed);
        AppendTrafficSample(DownloadTrafficSamples, downloadSpeed);
    }

    private void ApplyMemoryUsage(long memoryInUse)
    {
        MemoryUsage = FormatBytes(memoryInUse);
    }

    private void ResetTrafficHistory()
    {
        UploadTrafficSamples.Clear();
        DownloadTrafficSamples.Clear();
    }

    private static void AppendTrafficSample(ObservableCollection<long> samples, long value)
    {
        samples.Add(Math.Max(0, value));
        if (samples.Count > TrafficSparklineSampleCount)
        {
            samples.RemoveAt(0);
        }
    }

    private async Task<ApiModeConfigSnapshot?> GetModeConfigFromApiAsync()
    {
        if (_singBoxManager == null)
        {
            return null;
        }

        var config = await _singBoxManager.GetModeConfigAsync();
        _proxyModeCache.Update(config);
        return config;
    }

    private async Task<bool> SetModeAsync(string mode)
    {
        if (_singBoxManager == null)
        {
            return false;
        }

        return await _singBoxManager.SetModeAsync(mode);
    }

    private void UpdateModeSelection(string? mode)
    {
        _currentMode = string.IsNullOrWhiteSpace(mode) ? null : mode;
        DashboardModeOptionViewModel? selectedOption = null;
        foreach (var option in ModeOptions)
        {
            option.IsSelected = !string.IsNullOrWhiteSpace(mode) &&
                string.Equals(option.Mode, mode, StringComparison.OrdinalIgnoreCase);

            if (option.IsSelected)
            {
                selectedOption = option;
            }
        }

        _suppressSelectedModeOptionChange = true;
        try
        {
            SelectedModeOption = selectedOption;
        }
        finally
        {
            _suppressSelectedModeOptionChange = false;
        }
    }

    private void ApplyModeOptions(IReadOnlyList<string>? modeList, string? currentMode)
    {
        var modes = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var source = modeList ?? Array.Empty<string>();
        for (int i = 0; i < source.Count; i++)
        {
            var mode = source[i];
            if (!string.IsNullOrWhiteSpace(mode) && seen.Add(mode))
            {
                modes.Add(mode);
            }
        }

        if (modes.Count == 0 && !string.IsNullOrWhiteSpace(currentMode))
        {
            modes.Add(currentMode);
        }

        if (AreModeOptionsEqual(modes))
        {
            return;
        }

        ModeOptions.Clear();
        for (var i = 0; i < modes.Count; i++)
        {
            ModeOptions.Add(new DashboardModeOptionViewModel
            {
                Mode = modes[i],
                DisplayName = modes[i]
            });
        }

        OnPropertyChanged(nameof(ModeColumnCount));
        OnPropertyChanged(nameof(UseModeDropdown));
        OnPropertyChanged(nameof(ShowModeSegments));
    }

    private bool AreModeOptionsEqual(IReadOnlyList<string> modes)
    {
        if (ModeOptions.Count != modes.Count)
        {
            return false;
        }

        for (var i = 0; i < modes.Count; i++)
        {
            if (!string.Equals(ModeOptions[i].Mode, modes[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static string FormatBytes(long bytes) => FormatHelper.FormatBytes(bytes);
    private static string FormatBytesPerSecond(long bytesPerSecond) => FormatHelper.FormatBytesPerSecond(bytesPerSecond);

    private static string BuildSingBoxWebUiUrl()
    {
        var port = HttpClientFactory.LocalNativeApiPort > 0 ? HttpClientFactory.LocalNativeApiPort : DefaultSingBoxApiPort;
        return SingBoxDashboardBootstrapService.Configure(port, HttpClientFactory.LocalNativeApiSecret).Url;
    }

    /// <summary>
    /// Whether the WebUI button can work at all: the running kernel must expose the
    /// native gRPC API (a version-gated 1.14 feature) on a discovered endpoint.
    /// Disabled otherwise instead of opening a panel that cannot reach anything.
    /// </summary>
    public bool CanOpenSingBoxWebUi =>
        HttpClientFactory.LocalNativeApiPort > 0 &&
        CartonApplicationInfo.SupportsNativeApi(CartonApplicationInfo.EffectiveSingBoxVersion);

    private bool TryBuildTerminalProxyCommand(string type, out string command, out string error)
    {
        command = string.Empty;
        if (!TryGetTerminalProxyUrl(out var proxyUrl, out error))
        {
            return false;
        }

        command = type switch
        {
            TerminalProxyTypeCmd => BuildCmdProxyCommand(proxyUrl),
            TerminalProxyTypePowerShell => BuildPowerShellProxyCommand(proxyUrl),
            TerminalProxyTypeLinux => BuildLinuxProxyCommand(proxyUrl),
            _ => string.Empty
        };

        if (!string.IsNullOrWhiteSpace(command))
        {
            return true;
        }

        error = GetString("Dashboard.Status.OpenProxyToolFailed", "Failed to open proxy tool");
        return false;
    }

    private bool TryCreateTerminalLaunch(string type, out ProcessStartInfo startInfo, out string error)
    {
        startInfo = new ProcessStartInfo();
        if (!TryGetTerminalProxyUrl(out var proxyUrl, out error))
        {
            return false;
        }

        var userHomeDirectory = GetUserHomeDirectory();

        switch (type)
        {
            case TerminalProxyTypeCmd:
                startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/K {BuildCmdProxyCommand(proxyUrl)}",
                    UseShellExecute = true,
                    WorkingDirectory = userHomeDirectory
                };
                return true;
            case TerminalProxyTypePowerShell:
                startInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoExit -EncodedCommand {BuildEncodedPowerShellCommand(proxyUrl)}",
                    UseShellExecute = true,
                    WorkingDirectory = userHomeDirectory
                };
                return true;
            default:
                error = GetString("Dashboard.Status.OpenProxyToolFailed", "Failed to open proxy tool");
                return false;
        }
    }

    private bool TryGetTerminalProxyUrl(out string proxyUrl, out string error)
    {
        proxyUrl = string.Empty;
        if (!TryGetValidatedPort(out var port, out error))
        {
            return false;
        }

        proxyUrl = $"http://127.0.0.1:{port}";
        error = string.Empty;
        return true;
    }

    private static string BuildCmdProxyCommand(string proxyUrl)
    {
        return $"set \"http_proxy={proxyUrl}\" && set \"https_proxy={proxyUrl}\" && set \"HTTP_PROXY={proxyUrl}\" && set \"HTTPS_PROXY={proxyUrl}\"";
    }

    private static string BuildPowerShellProxyCommand(string proxyUrl)
    {
        var escapedProxyUrl = EscapeForPowerShellSingleQuoted(proxyUrl);
        return $"$Env:http_proxy='{escapedProxyUrl}'; $Env:https_proxy='{escapedProxyUrl}'; $Env:HTTP_PROXY='{escapedProxyUrl}'; $Env:HTTPS_PROXY='{escapedProxyUrl}'";
    }

    private static string BuildLinuxProxyCommand(string proxyUrl)
    {
        var escapedProxyUrl = EscapeForShellSingleQuoted(proxyUrl);
        return $"export http_proxy='{escapedProxyUrl}' https_proxy='{escapedProxyUrl}' HTTP_PROXY='{escapedProxyUrl}' HTTPS_PROXY='{escapedProxyUrl}'";
    }

    private static string BuildEncodedPowerShellCommand(string proxyUrl)
    {
        var command = BuildPowerShellProxyCommand(proxyUrl);
        return Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
    }

    private static string GetUserHomeDirectory()
    {
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private static string EscapeForPowerShellSingleQuoted(string value)
    {
        return value.Replace("'", "''");
    }

    private static string EscapeForShellSingleQuoted(string value)
    {
        return value.Replace("'", "'\"'\"'");
    }

    private bool TryGetValidatedPort(out int port, out string error)
    {
        if (!int.TryParse(InboundPortText, out port) || port is < 1 or > 65535)
        {
            error = GetString("Dashboard.Startup.PortOutOfRange", "Port must be between 1 and 65535");
            return false;
        }

        error = string.Empty;
        return true;
    }

    private bool CanRefreshConnectivity()
    {
        return ShowDashboardMetrics && !IsRefreshingConnectivity;
    }

    private void InitializeConnectivityItems()
    {
        if (ConnectivityItems.Count > 0)
        {
            return;
        }

        foreach (var target in ConnectivityTargets)
        {
            ConnectivityItems.Add(new DashboardSiteStatusItemViewModel
            {
                Name = target.Name,
                Url = target.Url
            });
        }
    }

    private bool AllConnectivityItemsHaveLatency()
    {
        if (ConnectivityItems.Count == 0)
        {
            return false;
        }

        for (var i = 0; i < ConnectivityItems.Count; i++)
        {
            if (ConnectivityItems[i].Latency <= 0)
            {
                return false;
            }
        }

        return true;
    }

    private void ResetConnectivityDisplay()
    {
        // Kernel stopped/restarted: a new session begins, so auto-test may run again.
        _connectivitySessionId++;
        _connectivityAutoTestedThisSession = false;
        foreach (var item in ConnectivityItems)
        {
            item.ResetMeasurement();
        }
    }

    private async Task RefreshConnectivityCoreAsync(bool force)
    {
        if (IsRefreshingConnectivity || !ShowDashboardMetrics)
        {
            return;
        }

        if (!force && (_connectivityAutoTestedThisSession || AllConnectivityItemsHaveLatency()))
        {
            return;
        }

        var sessionId = _connectivitySessionId;
        IsRefreshingConnectivity = true;
        try
        {
            if (!await WaitForMixedProxyAsync())
            {
                await Dispatcher.UIThread.InvokeAsync(ResetConnectivityDisplay);
                return;
            }

            using var client = CreateConnectivityProxyClient();
            if (client == null)
            {
                await Dispatcher.UIThread.InvokeAsync(ResetConnectivityDisplay);
                return;
            }

            const int maxAttempts = 3;
            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                if (attempt > 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt));
                    if (!ShowDashboardMetrics || sessionId != _connectivitySessionId)
                    {
                        return;
                    }
                }

                var pending = new List<DashboardSiteStatusItemViewModel>();
                for (var i = 0; i < ConnectivityItems.Count; i++)
                {
                    if (force || ConnectivityItems[i].Latency <= 0)
                    {
                        pending.Add(ConnectivityItems[i]);
                    }
                }

                if (pending.Count == 0)
                {
                    return;
                }

                var tasks = new Task[pending.Count];
                for (var i = 0; i < pending.Count; i++)
                {
                    tasks[i] = RefreshConnectivityItemAsync(client, pending[i]);
                }

                await Task.WhenAll(tasks);

                if (!force && AllConnectivityItemsHaveLatency())
                {
                    return;
                }

                // Manual refresh always takes a single pass; auto-start retries
                // sites that still have no latency because mixed/routing may not
                // be ready the instant the gRPC API reports Running.
                if (force)
                {
                    return;
                }
            }
        }
        finally
        {
            IsRefreshingConnectivity = false;
            // Auto path: mark the session as tested whether or not every site
            // answered, so re-entering the dashboard never re-runs the retry loop.
            // The flag survives kernel restarts only via ResetConnectivityDisplay.
            if (!force)
            {
                _connectivityAutoTestedThisSession = true;
            }
        }
    }

    private async Task RefreshConnectivityItemAsync(HttpClient client, DashboardSiteStatusItemViewModel item)
    {
        var latency = await MeasureConnectivityAsync(client, item.Url);
        await Dispatcher.UIThread.InvokeAsync(() => item.SetMeasuredLatency(latency));
    }

    private HttpClient? CreateConnectivityProxyClient()
    {
        if (!TryGetValidatedPort(out var port, out _))
        {
            return null;
        }

        return HttpClientFactory.CreateExternalProxyClient("127.0.0.1", port);
    }

    private async Task<bool> WaitForMixedProxyAsync()
    {
        if (!TryGetValidatedPort(out var port, out _))
        {
            return false;
        }

        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (!ShowDashboardMetrics)
            {
                return false;
            }

            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
                await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), cts.Token);
                return true;
            }
            catch (OperationCanceledException)
            {
            }
            catch (SocketException)
            {
            }

            await Task.Delay(200);
        }

        return false;
    }

    private static async Task<int?> MeasureConnectivityAsync(HttpClient client, string url)
    {
        var requestUri = AppendConnectivityCacheBuster(url);
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };
        request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cts.Token);
            stopwatch.Stop();

            // Any HTTP response through the mixed inbound proves the path works.
            // Favicon endpoints often return 403/404 to a non-browser User-Agent.
            // This is only sound because the probe goes through SOCKS5 (a SOCKS
            // failure surfaces as an exception below, never as a fake HTTP status);
            // if the transport ever goes back to an HTTP proxy, proxy-generated
            // 502/407 responses would be miscounted as "connected" here.
            return Math.Max(1, (int)Math.Round(stopwatch.Elapsed.TotalMilliseconds));
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (SocketException)
        {
            // SOCKS handshake / connection refused from the SocketsHttpHandler proxy.
            return null;
        }
    }

    private static string AppendConnectivityCacheBuster(string url)
    {
        var separator = url.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return $"{url}{separator}_={Environment.TickCount64}";
    }

    private static JsonObject GetOrCreateApiService(JsonArray services)
    {
        foreach (var service in services)
        {
            if (service is JsonObject serviceObject &&
                TryReadJsonString(serviceObject, "type", out var type) &&
                string.Equals(type, "api", StringComparison.OrdinalIgnoreCase))
            {
                return serviceObject;
            }
        }

        var apiService = new JsonObject
        {
            ["type"] = "api",
            ["tag"] = "carton-api"
        };
        services.Add((JsonNode)apiService);
        return apiService;
    }

    private static bool IsValidPort(int port)
    {
        return port is > 0 and <= 65535;
    }

    private static bool TryReadJsonString(JsonObject obj, string propertyName, out string value)
    {
        value = string.Empty;
        if (!obj.TryGetPropertyValue(propertyName, out var node) || node == null)
        {
            return false;
        }

        try
        {
            value = node.GetValue<string>() ?? string.Empty;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadJsonInt(JsonObject obj, string propertyName, out int value)
    {
        value = 0;
        if (!obj.TryGetPropertyValue(propertyName, out var node) || node == null)
        {
            return false;
        }

        try
        {
            if (node.GetValueKind() == JsonValueKind.Number)
            {
                value = node.GetValue<int>();
                return true;
            }

            if (node.GetValueKind() == JsonValueKind.String &&
                int.TryParse(node.GetValue<string>(), out value))
            {
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private static void ApplyRuntimeLogLevel(JsonObject root, string logLevel)
    {
        if (root["log"] is not JsonObject logConfig)
        {
            root["log"] = new JsonObject
            {
                ["level"] = NormalizeLogLevel(logLevel),
                ["disabled"] = false,
                ["timestamp"] = true
            };
            return;
        }

        logConfig["level"] = NormalizeLogLevel(logLevel);
    }

    private string GetString(string key, string fallback)
    {
        var value = _localizationService.GetString(key);
        return string.Equals(value, key, StringComparison.Ordinal) ? fallback : value;
    }

    private string BuildStartFailureStatus()
    {
        if (_kernelManager?.IsKernelInstalled != true)
        {
            return GetMissingKernelStartMessage();
        }

        var fallback = _localizationService["Status.FailedStart"];
        var detail = _singBoxManager?.State.ErrorMessage;
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

    private static string NormalizeLogLevel(string? level)
    {
        return SingBoxLogLevelHelper.Normalize(level);
    }

    private async Task NotifyProfilesChangedAsync()
    {
        if (_profilesChangedCallback != null)
        {
            await _profilesChangedCallback();
        }
    }

}

public partial class DashboardProfileItemViewModel : ObservableObject
{
    [ObservableProperty]
    private int _id;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private bool _isSelected;
}

public partial class DashboardModeOptionViewModel : ObservableObject
{
    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private string _mode = string.Empty;

    [ObservableProperty]
    private bool _isSelected;
}

internal sealed record DashboardSiteStatusDefinition(string Name, string Url);

internal enum DashboardRuntimeOperation
{
    None,
    RestartingForTunChange
}

public partial class DashboardSiteStatusItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private int _latency;

    [ObservableProperty]
    private string _latencyText = "--";

    [ObservableProperty]
    private bool _hasMeasured;

    public string Url { get; init; } = string.Empty;

    public void SetMeasuredLatency(int? latency)
    {
        Latency = latency ?? 0;
        LatencyText = latency.HasValue ? $"{latency.Value} ms" : "--";
        HasMeasured = true;
    }

    public void ResetMeasurement()
    {
        Latency = 0;
        LatencyText = "--";
        HasMeasured = false;
    }
}
