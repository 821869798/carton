using carton.Core.Models;
using carton.Core.Services;
using carton.Core.Utilities;
using carton.GUI.Models;
using carton.GUI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace carton.ViewModels;

public partial class DashboardViewModel : PageViewModelBase
{
    private readonly ISingBoxManager? _singBoxManager;
    private readonly IProfileManager? _profileManager;
    private readonly IConfigManager? _configManager;
    private readonly Action<string>? _logWriter;
    private readonly ILocalizationService _localizationService;
    private HttpClient _clashHttpClient => HttpClientFactory.LocalApi;
    private string? _currentClashMode;
    private ProfileRuntimeOptions _runtimeOptions = new();
    private bool _suppressRuntimeOptionUpdates;

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

    [RelayCommand]
    public async Task CopyTerminalProxyCommand(string type)
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

    [ObservableProperty]
    private string _startupStatus = string.Empty;

    [ObservableProperty]
    private string _inboundPortText = "2028";

    [ObservableProperty]
    private bool _isPortEditing;

    [ObservableProperty]
    private bool _allowLanConnections;

    [ObservableProperty]
    private bool _enableSystemProxy;

    [ObservableProperty]
    private bool _enableTunInbound;

    partial void OnAllowLanConnectionsChanged(bool value) => UpdateRuntimeOptions(options => options.AllowLanConnections = value);
    partial void OnEnableSystemProxyChanged(bool value) => UpdateRuntimeOptions(options => options.EnableSystemProxy = value);
    partial void OnEnableTunInboundChanged(bool value) => UpdateRuntimeOptions(options => options.EnableTunInbound = value);

    private const int DefaultClashApiPort = 9090;

    public bool ShowStartupSelector => !IsConnected;
    public bool ShowDashboardMetrics => IsConnected;
    public bool IsPortInputReadOnly => !IsPortEditing;
    public string PortEditButtonText => IsPortEditing
        ? GetString("Dashboard.Port.Save", "Save Port")
        : GetString("Dashboard.Port.Edit", "Edit Port");

    partial void OnIsConnectedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowStartupSelector));
        OnPropertyChanged(nameof(ShowDashboardMetrics));
    }

    partial void OnIsPortEditingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsPortInputReadOnly));
        OnPropertyChanged(nameof(PortEditButtonText));
    }

    public DashboardViewModel()
    {
        Title = "Dashboard";
        Icon = "Home";
        _localizationService = LocalizationService.Instance;
        InitializeClashModeOptions();
        _localizationService.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(PortEditButtonText));
            UpdateClashModeOptionDisplayNames();
        };
    }

    public DashboardViewModel(ISingBoxManager singBoxManager, IProfileManager profileManager, IConfigManager configManager, Action<string>? logWriter = null) : this()
    {
        _singBoxManager = singBoxManager;
        _profileManager = profileManager;
        _configManager = configManager;
        _logWriter = logWriter;
        _singBoxManager.StatusChanged += OnStatusChanged;
        _singBoxManager.TrafficUpdated += OnTrafficUpdated;
        _ = LoadProfilesAsync();
        if (_singBoxManager.IsRunning)
        {
            _ = RefreshClashModeAsync();
            InitializeTrafficMetrics();
        }
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
        });

        if (status == ServiceStatus.Running)
        {
            _ = RefreshClashModeAsync();
            Avalonia.Threading.Dispatcher.UIThread.Post(InitializeTrafficMetrics);
        }
        else
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                UpdateClashModeSelection(null);
                ResetTrafficDisplay();
            });
        }
    }

    private void InitializeTrafficMetrics()
    {
        if (_singBoxManager == null)
        {
            ResetTrafficDisplay();
            return;
        }

        var state = _singBoxManager.State;
        UploadSpeed = FormatBytes(state.UploadSpeed) + "/s";
        DownloadSpeed = FormatBytes(state.DownloadSpeed) + "/s";
        TotalUpload = FormatBytes(state.TotalUpload);
        TotalDownload = FormatBytes(state.TotalDownload);
    }

    private void OnTrafficUpdated(object? sender, TrafficInfo traffic)
    {
        if (_singBoxManager == null)
        {
            return;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            UploadSpeed = FormatBytes(traffic.Uplink) + "/s";
            DownloadSpeed = FormatBytes(traffic.Downlink) + "/s";
            TotalUpload = FormatBytes(_singBoxManager.State.TotalUpload);
            TotalDownload = FormatBytes(_singBoxManager.State.TotalDownload);
        });
    }

    private void InitializeClashModeOptions()
    {
        ClashModeOptions.Clear();
        ClashModeOptions.Add(new DashboardClashModeOptionViewModel { Mode = "global" });
        ClashModeOptions.Add(new DashboardClashModeOptionViewModel { Mode = "rule" });
        ClashModeOptions.Add(new DashboardClashModeOptionViewModel { Mode = "direct" });
        UpdateClashModeOptionDisplayNames();
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
        var success = await _singBoxManager.StartAsync(runtimeConfigPath);
        if (success)
        {
            // Let the manager know whether to clear the system proxy on stop.
            _singBoxManager.NotifySystemProxyEnabled(EnableSystemProxy);
        }
        StartupStatus = success ? string.Empty : _localizationService["Status.FailedStart"];
        if (!success)
        {
            LogError("Failed to start sing-box");
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
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => UpdateClashModeSelection(option.Mode));
        }
        else
        {
            await RefreshClashModeAsync();
        }
    }

    [RelayCommand]
    private void TogglePortEdit()
    {
        if (!IsPortEditing)
        {
            IsPortEditing = true;
            return;
        }

        if (!TryGetValidatedPort(out var port, out var error))
        {
            StartupStatus = error;
            return;
        }

        InboundPortText = port.ToString();
        PersistRuntimePort(port);
        IsPortEditing = false;
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

    private async Task<string?> EnsureProfileConfigPathForStartAsync(Profile profile, string profileName)
    {
        var configPath = await _configManager!.GetConfigPathAsync(profile.Id, profile.Type);
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
            StartupStatus = GetString("Status.RemoteProfileUrlEmpty", "Remote profile URL is empty");
            LogError($"{StartupStatus}: {profileName} ({profile.Id})");
            return null;
        }

        var downloadingMessage = GetString("Status.RemoteConfigDownloading", "Remote config missing, downloading...");
        StartupStatus = downloadingMessage;
        LogInfo($"{downloadingMessage} URL: {profile.Url}");

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
            var downloadedPath = await _configManager.GetConfigPathAsync(profile.Id, ProfileType.Remote);
            if (string.IsNullOrWhiteSpace(downloadedPath) || !File.Exists(downloadedPath))
            {
                StartupStatus = GetString("Status.RemoteConfigFileMissing", "Remote config download succeeded but file missing");
                LogError($"{StartupStatus}: {profileName} ({profile.Id})");
                return null;
            }

            var downloadedMessage = GetString("Status.RemoteConfigDownloaded", "Remote config downloaded");
            StartupStatus = downloadedMessage;
            LogInfo($"{downloadedMessage}: {profileName} ({profile.Id})");
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
        IsPortEditing = false;
        AllowLanConnections = _runtimeOptions.AllowLanConnections;
        EnableSystemProxy = _runtimeOptions.EnableSystemProxy;
        EnableTunInbound = _runtimeOptions.EnableTunInbound;
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

    private static ProfileRuntimeOptions CopyRuntimeOptions(ProfileRuntimeOptions options)
    {
        return new ProfileRuntimeOptions
        {
            InboundPort = options.InboundPort,
            AllowLanConnections = options.AllowLanConnections,
            EnableSystemProxy = options.EnableSystemProxy,
            EnableTunInbound = options.EnableTunInbound,
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
                inbounds.Add(mixedInbound);
            }
            mixedInbound["listen"] = AllowLanConnections ? "0.0.0.0" : "127.0.0.1";
            mixedInbound["listen_port"] = port;
            mixedInbound["set_system_proxy"] = EnableSystemProxy;

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
                    inbounds.Add(tunInbound);
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
            IsPortEditing = false;
            LogInfo($"Runtime inbounds prepared: port={port}, lan={AllowLanConnections}, systemProxy={EnableSystemProxy}, tun={EnableTunInbound}");
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
        var mode = await GetClashModeFromApiAsync();
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => UpdateClashModeSelection(mode));
    }

    private void ResetTrafficDisplay()
    {
        UploadSpeed = "0 B/s";
        DownloadSpeed = "0 B/s";
        TotalUpload = "0 B";
        TotalDownload = "0 B";
    }

    private async Task<string?> GetClashModeFromApiAsync()
    {
        try
        {
            var response = await _clashHttpClient.GetAsync("configs");
            if (!response.IsSuccessStatusCode)
            {
                LogError($"Failed to fetch Clash mode: {response.StatusCode}");
                return null;
            }

            var payload = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.TryGetProperty("mode", out var modeElement) &&
                modeElement.ValueKind == JsonValueKind.String)
            {
                return modeElement.GetString();
            }

            return null;
        }
        catch (Exception ex)
        {
            LogError($"Failed to fetch Clash mode: {ex.Message}");
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

    private void UpdateClashModeOptionDisplayNames()
    {
        foreach (var option in ClashModeOptions)
        {
            option.DisplayName = option.Mode switch
            {
                "global" => GetString("Dashboard.ClashMode.Global", "Global"),
                "rule" => GetString("Dashboard.ClashMode.Rule", "Rule"),
                "direct" => GetString("Dashboard.ClashMode.Direct", "Direct"),
                _ => option.Mode ?? string.Empty
            };
        }
    }

    private static string FormatBytes(long bytes) => FormatHelper.FormatBytes(bytes);

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

    private string GetString(string key, string fallback)
    {
        var value = _localizationService.GetString(key);
        return string.Equals(value, key, StringComparison.Ordinal) ? fallback : value;
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
