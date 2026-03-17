using carton.Core.Models;
using carton.Core.Services;
using carton.Core.Utilities;
using carton.GUI.Models;
using carton.GUI.Serialization;
using carton.GUI.Services;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace carton.ViewModels;

public partial class DashboardViewModel : PageViewModelBase
{
    private readonly ISingBoxManager? _singBoxManager;
    private readonly IKernelManager? _kernelManager;
    private readonly IProfileManager? _profileManager;
    private readonly IConfigManager? _configManager;
    private readonly Action<string>? _logWriter;
    private readonly ILocalizationService _localizationService;
    private readonly ClashConfigCacheService _clashConfigCache;
    private HttpClient _clashHttpClient => HttpClientFactory.LocalApi;
    private string? _currentClashMode;
    private ProfileRuntimeOptions _runtimeOptions = new();
    private bool _suppressRuntimeOptionUpdates;
    private bool _suppressSystemProxyApply;
    private readonly DispatcherTimer _sessionDurationTimer;
    private bool _isOnPage = true;
    private bool _isWindowVisible = true;
    private bool _isLiveRefreshActive;
    private int _pendingTrafficRefresh;
    private int _pendingMemoryRefresh;
    private static readonly ObservableCollection<string> SupportedLogLevels = new(SingBoxLogLevelHelper.Levels);
    public override NavigationPage PageType => NavigationPage.Dashboard;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string _statusText = "Disconnected";

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

    public ObservableCollection<DashboardClashModeOptionViewModel> ClashModeOptions { get; } = new();

    [ObservableProperty]
    private DashboardProfileItemViewModel? _selectedStartupProfile;

    public bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    public bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
    public bool ShowTerminalProxyButtons => IsConnected;

    [ObservableProperty]
    private string _kernelVersion = "unknown";

    [ObservableProperty]
    private string _memoryUsage = "0 B";

    [RelayCommand]
    private Task CopyCmdTerminalProxy() => CopyTerminalProxyAsync("cmd");

    [RelayCommand]
    private Task CopyPsTerminalProxy() => CopyTerminalProxyAsync("ps");

    [RelayCommand]
    private Task CopyLinuxTerminalProxy() => CopyTerminalProxyAsync("linux");

    private async Task CopyTerminalProxyAsync(string type)
    {
        if (!int.TryParse(InboundPortText, out var port)) port = 2028;
        var host = "127.0.0.1";
        var proxyUrl = $"http://{host}:{port}";

        string command = type switch
        {
            "cmd" => $"set http_proxy={proxyUrl} & set https_proxy={proxyUrl}",
            "ps" => $"$Env:http_proxy=\"{proxyUrl}\"; $Env:https_proxy=\"{proxyUrl}\"",
            "linux" => $"export http_proxy={proxyUrl} https_proxy={proxyUrl}",
            _ => string.Empty
        };

        if (!string.IsNullOrEmpty(command))
        {
            var desktop = Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
            if (desktop?.MainWindow?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(command);
                StartupStatus = GetString("Dashboard.Status.CommandCopied", "Command copied to clipboard");
                _ = Task.Delay(2000).ContinueWith(_ => Avalonia.Threading.Dispatcher.UIThread.Post(() => StartupStatus = string.Empty));
            }
        }
    }

    [RelayCommand]
    private void OpenWebUi()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = BuildWebUiUrl(),
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            var message = GetString("Dashboard.Status.OpenWebUiFailed", "Failed to open WebUI");
            StartupStatus = $"{message}: {ex.Message}";
            LogError($"Failed to open WebUI: {ex.Message}");
        }
    }

    [ObservableProperty]
    private string _startupStatus = string.Empty;

    [ObservableProperty]
    private string _sessionStartTimeText = "--";

    [ObservableProperty]
    private string _inboundPortText = "2028";

    [ObservableProperty]
    private bool _allowLanConnections;

    [ObservableProperty]
    private bool _enableSystemProxy;

    [ObservableProperty]
    private bool _enableTunInbound;

    [ObservableProperty]
    private string _selectedLogLevel = SingBoxLogLevelHelper.DefaultLevel;

    partial void OnAllowLanConnectionsChanged(bool value) => UpdateRuntimeOptions(options => options.AllowLanConnections = value);
    partial void OnEnableSystemProxyChanged(bool value)
    {
        UpdateRuntimeOptions(options => options.EnableSystemProxy = value);

        if (_suppressSystemProxyApply || !IsConnected)
        {
            return;
        }

        ApplyRunningSystemProxy(value);
    }
    partial void OnEnableTunInboundChanged(bool value) => UpdateRuntimeOptions(options => options.EnableTunInbound = value);
    partial void OnSelectedLogLevelChanged(string value)
    {
        UpdateRuntimeOptions(options => options.LogLevel = NormalizeLogLevel(value));
        OnPropertyChanged(nameof(ShowVerboseLogLevelHint));
    }

    private const int DefaultClashApiPort = 9090;

    public bool ShowStartupSelector => !IsConnected;
    public bool ShowDashboardMetrics => IsConnected;
    public ObservableCollection<string> LogLevelOptions => SupportedLogLevels;
    public bool ShowVerboseLogLevelHint => SingBoxLogLevelHelper.IsVerbose(SelectedLogLevel);

    partial void OnIsConnectedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowStartupSelector));
        OnPropertyChanged(nameof(ShowDashboardMetrics));
    }

    public DashboardViewModel()
    {
        Title = "Dashboard";
        Icon = "Home";
        _localizationService = LocalizationService.Instance;
        _clashConfigCache = ClashConfigCacheService.Instance;
        _sessionDurationTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _sessionDurationTimer.Tick += (_, _) => UpdateSessionStartTime();
        _localizationService.LanguageChanged += (_, _) =>
        {
            UpdateSessionStartTime();
            _ = RefreshKernelVersionAsync();
        };
        _ = RefreshKernelVersionAsync();
    }

    public DashboardViewModel(ISingBoxManager singBoxManager, IKernelManager kernelManager, IProfileManager profileManager, IConfigManager configManager, Action<string>? logWriter = null) : this()
    {
        _singBoxManager = singBoxManager;
        _kernelManager = kernelManager;
        _profileManager = profileManager;
        _configManager = configManager;
        _logWriter = logWriter;
        _singBoxManager.StatusChanged += OnStatusChanged;
        _singBoxManager.TrafficUpdated += OnTrafficUpdated;
        _singBoxManager.MemoryUpdated += OnMemoryUpdated;
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
            IsConnected = status == ServiceStatus.Running;
            StatusText = status switch
            {
                ServiceStatus.Running => _localizationService["Status.Connected"],
                ServiceStatus.Starting => _localizationService["Status.Starting"],
                ServiceStatus.Stopping => _localizationService["Status.Stopping"],
                ServiceStatus.Error => _localizationService["Status.Error"],
                _ => _localizationService["Status.Disconnected"]
            };
            OnPropertyChanged(nameof(ShowTerminalProxyButtons));
            UpdateSessionStartTime();

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
        }
        else
        {
            StopSessionDurationTimer();
            Dispatcher.UIThread.Post(() =>
            {
                UpdateLiveRefreshState();
                _clashConfigCache.Clear();
                UpdateClashModeSelection(null);
                ResetTrafficDisplay();
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
        });
    }

    private void InitializeMemoryMetrics()
    {
        ApplyMemoryUsage(_singBoxManager?.State.MemoryInUse ?? 0);
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
                    CurrentProfile = vm.Name;
                }
            }

            if (SelectedStartupProfile == null && AvailableProfiles.Count > 0)
            {
                SelectedStartupProfile = AvailableProfiles[0];
                SelectedStartupProfile.IsSelected = true;
            }
        });

        if (SelectedStartupProfile != null)
        {
            await LoadRuntimeOptionsAsync(SelectedStartupProfile.Id);
        }
    }

    [RelayCommand]
    private async Task SelectStartupProfile(DashboardProfileItemViewModel? profile)
    {
        if (_profileManager == null || profile == null) return;

        foreach (var item in AvailableProfiles)
        {
            item.IsSelected = item.Id == profile.Id;
        }

        SelectedStartupProfile = profile;
        CurrentProfile = profile.Name;
        await _profileManager.SetSelectedProfileIdAsync(profile.Id);
        await LoadRuntimeOptionsAsync(profile.Id);
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

        var target = SelectedStartupProfile ?? AvailableProfiles.FirstOrDefault();
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

        var configPath = await EnsureProfileConfigPathForStartAsync(profile, target.Name) ?? string.Empty;
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

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && EnableTunInbound
            && !await _singBoxManager.IsLinuxCoreAuthorizedAsync())
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
                StartupStatus = GetString("Dashboard.Auth.Failed", "Failed to authorize kernel");
                LogError($"Linux kernel authorization failed: {authError}");
                return;
            }
        }

        var success = await _singBoxManager.StartAsync(runtimeConfigPath);
        if (success)
        {
            ApplyRunningSystemProxy(EnableSystemProxy);
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
        StartupStatus = string.Empty;
    }

    [RelayCommand]
    private async Task ChangeClashMode(DashboardClashModeOptionViewModel? option)
    {
        if (option == null || string.IsNullOrWhiteSpace(option.Mode))
        {
            return;
        }

        if (string.Equals(_currentClashMode, option.Mode, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var success = await SetClashModeAsync(option.Mode);
        if (success)
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_clashConfigCache.Current is { } current)
                {
                    _clashConfigCache.Update(new ClashConfigSnapshot
                    {
                        Mode = option.Mode,
                        ModeList = current.ModeList
                    }, isDirty: true);
                }

                UpdateClashModeSelection(option.Mode);
            });
        }
        else
        {
            await RefreshClashModeAsync();
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
        _logWriter?.Invoke($"[INFO] {message}");
    }

    private void LogError(string message)
    {
        _logWriter?.Invoke($"[ERROR] {message}");
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

    private async Task<string?> EnsureProfileConfigPathForStartAsync(Profile profile, string profileName)
    {
        var configPath = await _configManager!.GetConfigPathAsync(profile.Id, profile.Type);
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
            StartupStatus = GetString("Status.RemoteProfileUrlEmpty", "Remote profile URL is empty");
            LogError($"{StartupStatus}: {profileName} ({profile.Id})");
            return null;
        }

        var loadingMessage = hasLocalConfig
            ? GetString("Status.RemoteConfigRefreshing", "Remote config due for update, refreshing...")
            : GetString("Status.RemoteConfigDownloading", "Remote config missing, downloading...");
        StartupStatus = loadingMessage;
        LogInfo($"{loadingMessage}: {profileName} ({profile.Id}) URL: {profile.Url}");

        try
        {
            var client = HttpClientFactory.External;
            var content = await client.GetStringAsync(profile.Url);
            if (string.IsNullOrWhiteSpace(content))
            {
                StartupStatus = GetString("Status.RemoteConfigEmpty", "Downloaded remote config is empty");
                LogError($"{StartupStatus}: {profileName} ({profile.Id})");
                return null;
            }

            await _configManager.SaveConfigAsync(profile.Id, content, ProfileType.Remote);
            profile.LastUpdated = DateTime.Now;
            await _profileManager!.UpdateAsync(profile);
            var downloadedPath = await _configManager.GetConfigPathAsync(profile.Id, ProfileType.Remote);
            if (string.IsNullOrWhiteSpace(downloadedPath) || !File.Exists(downloadedPath))
            {
                StartupStatus = GetString("Status.RemoteConfigFileMissing", "Remote config download succeeded but file missing");
                LogError($"{StartupStatus}: {profileName} ({profile.Id})");
                return null;
            }

            var completedMessage = hasLocalConfig
                ? GetString("Status.RemoteConfigRefreshed", "Remote config refreshed")
                : GetString("Status.RemoteConfigDownloaded", "Remote config downloaded");
            StartupStatus = completedMessage;
            LogInfo($"{completedMessage}: {profileName} ({profile.Id})");
            return downloadedPath;
        }
        catch (Exception ex)
        {
            var message = GetString("Status.RemoteConfigDownloadFailed", "Failed to download remote config");
            StartupStatus = $"{message}: {ex.Message}";
            LogError($"{message}: {profileName} ({profile.Id}) - {ex.Message}");
            return null;
        }
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
                var tunAddresses = new JsonArray((JsonNode)"172.18.0.1/30");
                if (Socket.OSSupportsIPv6)
                {
                    tunAddresses.Add((JsonNode)"fdfe:dcba:9876::1/126");
                }

                // 找到已有的 tun inbound，只更新需要的字段
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
                tunInbound["address"] = tunAddresses;
                tunInbound["auto_route"] = true;
                tunInbound["strict_route"] = true;
                tunInbound["route_exclude_address"] = new JsonArray(
                    "10.0.0.0/8",
                    "192.168.0.0/16",
                    "fe80::/10");
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

            var experimental = root["experimental"] as JsonObject ?? new JsonObject();
            root["experimental"] = experimental;

            var clashApi = experimental["clash_api"] as JsonObject ?? new JsonObject();

            var apiPort = DefaultClashApiPort;
            var secret = string.Empty;
            var hasExtController = false;

            if (clashApi.TryGetPropertyValue("external_controller", out var extControllerNode) && extControllerNode?.GetValue<string>() is string extController && !string.IsNullOrWhiteSpace(extController))
            {
                hasExtController = true;
                var portPos = extController.LastIndexOf(':');
                if (portPos >= 0 && portPos < extController.Length - 1)
                {
                    if (int.TryParse(extController.Substring(portPos + 1), out var p))
                    {
                        apiPort = p;
                    }
                }
            }

            if (clashApi.TryGetPropertyValue("secret", out var secretNode) && secretNode?.GetValue<string>() is string sec && !string.IsNullOrWhiteSpace(sec))
            {
                secret = sec;
            }

            if (!hasExtController)
            {
                clashApi["external_controller"] = $"127.0.0.1:{apiPort}";
            }
            clashApi["external_ui"] = "dashboard";
            experimental["clash_api"] = clashApi;

            HttpClientFactory.UpdateLocalApi("127.0.0.1", apiPort, secret);

            var cacheFile = experimental["cache_file"] as JsonObject ?? new JsonObject();
            cacheFile["enabled"] = true;
            cacheFile["path"] = "cache.db";
            cacheFile["store_fakeip"] = true;
            experimental["cache_file"] = cacheFile;

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

    private async Task RefreshClashModeAsync()
    {
        var config = await GetClashConfigFromApiAsync();
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            ApplyClashModeOptions(config?.ModeList, config?.Mode);
            UpdateClashModeSelection(config?.Mode);
        });
    }

    private void ResetTrafficDisplay()
    {
        ApplyTrafficMetrics(0, 0, 0, 0);
        ApplyMemoryUsage(0);
        SessionStartTimeText = "--";
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
            KernelVersion = info?.KernelVersion ?? GetString("Common.Unknown", "unknown");
        }
        catch
        {
            KernelVersion = GetString("Common.Unknown", "unknown");
        }
    }

    private void UpdateSessionStartTime()
    {
        var startTime = _singBoxManager?.State.StartTime;
        if (!startTime.HasValue)
        {
            SessionStartTimeText = "--";
            return;
        }

        var elapsed = DateTime.Now - startTime.Value;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        var totalHours = (int)elapsed.TotalHours;
        SessionStartTimeText = $"{totalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
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
            StartSessionDurationTimer();
            RefreshVisibleDashboardData();
            return;
        }

        StopSessionDurationTimer();
    }

    private void RefreshVisibleDashboardData()
    {
        if (!CanRefreshLiveMetrics())
        {
            return;
        }

        UpdateSessionStartTime();
        InitializeTrafficMetrics();
        InitializeMemoryMetrics();
        _ = RefreshClashModeAsync();
    }

    private void ApplyTrafficMetrics(long uploadSpeed, long downloadSpeed, long totalUpload, long totalDownload)
    {
        UploadSpeed = FormatBytes(uploadSpeed) + "/s";
        DownloadSpeed = FormatBytes(downloadSpeed) + "/s";
        TotalUpload = FormatBytes(totalUpload);
        TotalDownload = FormatBytes(totalDownload);
    }

    private void ApplyMemoryUsage(long memoryInUse)
    {
        MemoryUsage = FormatBytes(memoryInUse);
    }

    private async Task<ClashConfigSnapshot?> GetClashConfigFromApiAsync()
    {
        try
        {
            var response = await _clashHttpClient.GetAsync("configs");
            if (!response.IsSuccessStatusCode)
            {
                LogError($"Failed to fetch Clash config: {response.StatusCode}");
                return null;
            }

            var payload = await response.Content.ReadAsStringAsync();
            var config = JsonSerializer.Deserialize(payload, CartonGuiJsonContext.Default.ClashConfigSnapshot);
            _clashConfigCache.Update(config);
            return config;
        }
        catch (Exception ex)
        {
            LogError($"Failed to fetch Clash config: {ex.Message}");
            return null;
        }
    }

    private async Task<bool> SetClashModeAsync(string mode)
    {
        try
        {
            var body = new JsonObject
            {
                ["mode"] = mode
            };
            var request = new HttpRequestMessage(new HttpMethod("PATCH"), "configs")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
            };

            var response = await _clashHttpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                LogError($"Failed to change Clash mode: {response.StatusCode}");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            LogError($"Failed to change Clash mode: {ex.Message}");
            return false;
        }
    }

    private void UpdateClashModeSelection(string? mode)
    {
        _currentClashMode = string.IsNullOrWhiteSpace(mode) ? null : mode;
        foreach (var option in ClashModeOptions)
        {
            option.IsSelected = !string.IsNullOrWhiteSpace(mode) &&
                string.Equals(option.Mode, mode, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void ApplyClashModeOptions(IReadOnlyList<string>? modeList, string? currentMode)
    {
        var modes = (modeList ?? Array.Empty<string>())
            .Where(mode => !string.IsNullOrWhiteSpace(mode))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (modes.Count == 0 && !string.IsNullOrWhiteSpace(currentMode))
        {
            modes.Add(currentMode);
        }

        ClashModeOptions.Clear();
        foreach (var mode in modes)
        {
            ClashModeOptions.Add(new DashboardClashModeOptionViewModel
            {
                Mode = mode,
                DisplayName = mode
            });
        }
    }

    private static string FormatBytes(long bytes) => FormatHelper.FormatBytes(bytes);

    private static string BuildWebUiUrl()
    {
        var port = HttpClientFactory.LocalApiPort > 0 ? HttpClientFactory.LocalApiPort : DefaultClashApiPort;
        var queryParts = new List<string>
        {
            "hostname=127.0.0.1",
            $"port={port}"
        };

        if (!string.IsNullOrWhiteSpace(HttpClientFactory.LocalApiSecret))
        {
            queryParts.Add($"secret={Uri.EscapeDataString(HttpClientFactory.LocalApiSecret)}");
        }

        return $"http://127.0.0.1:{port}/ui/?{string.Join("&", queryParts)}";
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

public partial class DashboardClashModeOptionViewModel : ObservableObject
{
    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private string _mode = string.Empty;

    [ObservableProperty]
    private bool _isSelected;
}
