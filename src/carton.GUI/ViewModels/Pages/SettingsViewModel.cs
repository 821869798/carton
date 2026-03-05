using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using carton.Core.Services;
using carton.Core.Models;
using carton.GUI.Models;
using carton.GUI.Services;

namespace carton.ViewModels;

public partial class SettingsViewModel : PageViewModelBase
{
    private readonly IConfigManager? _configManager;
    private readonly IProfileManager? _profileManager;
    private readonly IKernelManager? _kernelManager;
    private readonly IPreferencesService? _preferencesService;
    private readonly ILocalizationService? _localizationService;
    private readonly IThemeService? _themeService;
    private readonly IStartupService? _startupService;
    private readonly IAppUpdateService? _appUpdateService;
    private AppUpdateResult? _pendingAppUpdate;
    private GitHubReleaseInfo? _latestReleaseInfo;
    private bool _requiresManualAppUpdate;
    private AppPreferences _currentPreferences = new();
    private bool _suppressPreferenceUpdates;

    public override NavigationPage PageType => NavigationPage.Settings;

    [ObservableProperty]
    private bool _startAtLogin;

    [ObservableProperty]
    private bool _autoStartOnLaunch;

    [ObservableProperty]
    private AppTheme _selectedTheme = AppTheme.System;

    [ObservableProperty]
    private string _selectedUpdateChannel = "release";

    [ObservableProperty]
    private bool _autoCheckAppUpdates = true;

    [ObservableProperty]
    private LanguageOptionViewModel? _selectedLanguage;

    [ObservableProperty]
    private DownloadMirror _selectedKernelDownloadMirror = DownloadMirror.GitHub;

    partial void OnStartAtLoginChanged(bool value)
    {
        _startupService?.ApplyStartAtLoginPreference(value);
        UpdatePreference(p => p.StartAtLogin = value);
    }

    partial void OnAutoStartOnLaunchChanged(bool value) => UpdatePreference(p => p.AutoStartOnLaunch = value);
    partial void OnSelectedThemeChanged(AppTheme value) => OnThemeChanged(value);
    partial void OnSelectedLanguageChanged(LanguageOptionViewModel? value) => OnLanguageOptionChanged(value);
    partial void OnSelectedUpdateChannelChanged(string value) => OnUpdateChannelChanged(value);
    partial void OnAutoCheckAppUpdatesChanged(bool value) => UpdatePreference(p => p.AutoCheckAppUpdates = value);
    partial void OnSelectedKernelDownloadMirrorChanged(DownloadMirror value) => UpdatePreference(p => p.KernelDownloadMirror = value);

    [ObservableProperty]
    private ObservableCollection<ProfileViewModel> _profiles = new();

    [ObservableProperty]
    private ProfileViewModel? _selectedProfile;

    [ObservableProperty]
    private string _kernelVersion = "Not installed";

    [ObservableProperty]
    private string _kernelPath = string.Empty;

    [ObservableProperty]
    private bool _isKernelInstalled;

    [ObservableProperty]
    private bool _isUpdatingKernel;

    [ObservableProperty]
    private double _updateProgress;

    [ObservableProperty]
    private string _updateStatus = string.Empty;

    [ObservableProperty]
    private string _latestVersion = string.Empty;

    [ObservableProperty]
    private bool _isDataOperationInProgress;

    [ObservableProperty]
    private string _dataOperationStatus = string.Empty;

    [ObservableProperty]
    private bool _isCheckingAppUpdate;

    [ObservableProperty]
    private bool _isDownloadingAppUpdate;

    [ObservableProperty]
    private bool _isAppUpdateAvailable;

    [ObservableProperty]
    private bool _isAppUpdateReadyToInstall;

    [ObservableProperty]
    private double _appUpdateProgress;

    [ObservableProperty]
    private string _appUpdateStatus = string.Empty;

    [ObservableProperty]
    private string _latestAvailableVersion = string.Empty;

    [ObservableProperty]
    private string _currentAppVersion = string.Empty;

    public ObservableCollection<AppTheme> Themes { get; } = new(Enum.GetValues<AppTheme>());
    public ObservableCollection<DownloadMirror> KernelDownloadMirrors { get; } = new(Enum.GetValues<DownloadMirror>());
    public ObservableCollection<LanguageOptionViewModel> Languages { get; } = new();
    public ObservableCollection<UpdateChannelOptionViewModel> UpdateChannels { get; } = new();

    public SettingsViewModel()
    {
        Title = "Settings";
        Icon = "Settings";
        InitializeUpdateChannels();
    }

    public SettingsViewModel(
        IConfigManager configManager,
        IProfileManager profileManager,
        IKernelManager kernelManager,
        IPreferencesService preferencesService,
        ILocalizationService localizationService,
        IThemeService themeService,
        IStartupService startupService,
        IAppUpdateService appUpdateService) : this()
    {
        _configManager = configManager;
        _profileManager = profileManager;
        _kernelManager = kernelManager;
        _preferencesService = preferencesService;
        _localizationService = localizationService;
        _themeService = themeService;
        _startupService = startupService;
        _appUpdateService = appUpdateService;

        _kernelManager.DownloadProgressChanged += OnDownloadProgress;
        _kernelManager.StatusChanged += OnKernelStatusChanged;
        InitializeLanguages();
        InitializeUpdateChannels();
        UpdateLocalizedTexts();
        if (_localizationService != null)
        {
            _localizationService.LanguageChanged += (_, _) => UpdateLocalizedTexts();
        }

        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        LoadPreferences();
        await LoadProfilesAsync();
        await RefreshKernelInfoAsync();
        InitializeAppUpdateState();
        if (_currentPreferences.AutoCheckAppUpdates)
        {
            _ = CheckAppUpdate();
        }
    }

    private void InitializeLanguages()
    {
        Languages.Clear();
        if (_localizationService == null)
        {
            return;
        }

        foreach (var language in Enum.GetValues<AppLanguage>())
        {
            Languages.Add(new LanguageOptionViewModel(language));
        }
    }

    private void InitializeUpdateChannels()
    {
        UpdateChannels.Clear();
        UpdateChannels.Add(new UpdateChannelOptionViewModel("release", GetUpdateChannelDisplayName("release")));
        UpdateChannels.Add(new UpdateChannelOptionViewModel("beta", GetUpdateChannelDisplayName("beta")));
    }

    private void RefreshUpdateChannelDisplayNames()
    {
        if (UpdateChannels.Count == 0)
        {
            InitializeUpdateChannels();
            return;
        }

        foreach (var option in UpdateChannels)
        {
            option.DisplayName = GetUpdateChannelDisplayName(option.Channel);
        }
    }

    private void InitializeAppUpdateState()
    {
        CurrentAppVersion = _appUpdateService?.CurrentVersion ?? GetString("Common.Unknown", "unknown");
        _requiresManualAppUpdate = _appUpdateService != null && !_appUpdateService.SupportsInAppUpdates;
        LatestAvailableVersion = string.Empty;
        AppUpdateStatus = string.Empty;
        SelectedUpdateChannel = UpdateChannelToString(_currentPreferences.UpdateChannel);
    }

    private void UpdateLocalizedTexts()
    {
        if (_localizationService == null)
        {
            return;
        }

        Title = _localizationService["Navigation.Settings"];

        if (!IsKernelInstalled)
        {
            KernelVersion = GetString("Settings.Kernel.NotInstalled", "Not installed");
        }

        RefreshUpdateChannelDisplayNames();
    }

    private void LoadPreferences()
    {
        if (_preferencesService == null)
        {
            return;
        }

        var preferences = _preferencesService.Load();
        _currentPreferences = preferences ?? new AppPreferences();

        _suppressPreferenceUpdates = true;
        StartAtLogin = _currentPreferences.StartAtLogin;
        AutoStartOnLaunch = _currentPreferences.AutoStartOnLaunch;
        SelectedTheme = _currentPreferences.Theme;
        SelectedLanguage = Languages.FirstOrDefault(l => l.Language == _currentPreferences.Language) ?? Languages.FirstOrDefault();
        SelectedUpdateChannel = UpdateChannelToString(_currentPreferences.UpdateChannel);
        AutoCheckAppUpdates = _currentPreferences.AutoCheckAppUpdates;
        SelectedKernelDownloadMirror = _currentPreferences.KernelDownloadMirror;
        _suppressPreferenceUpdates = false;
        _localizationService?.SetLanguage(_currentPreferences.Language);
        _startupService?.ApplyStartAtLoginPreference(StartAtLogin);
    }

    private void OnDownloadProgress(object? sender, DownloadProgress e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            UpdateProgress = e.Progress;
            UpdateStatus = $"{e.Status} {e.BytesReceived / 1024 / 1024:F1}MB / {e.TotalBytes / 1024 / 1024:F1}MB";
        });
    }

    private void OnKernelStatusChanged(object? sender, string status)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            UpdateStatus = status;
        });
    }

    public async Task RefreshKernelInfoAsync()
    {
        if (_kernelManager == null) return;

        var kernelInfo = await _kernelManager.GetInstalledKernelInfoAsync();
        IsKernelInstalled = kernelInfo != null;

        if (kernelInfo != null)
        {
            KernelVersion = kernelInfo.KernelVersion;
            KernelPath = kernelInfo.Path;
        }
        else
        {
            KernelVersion = GetString("Settings.Kernel.NotInstalled", "Not installed");
            KernelPath = string.Empty;
        }

        var latest = await _kernelManager.GetLatestVersionAsync();
        LatestVersion = latest ?? GetString("Common.Unknown", "unknown");
    }

    [RelayCommand]
    private async Task CheckUpdate()
    {
        await RefreshKernelInfoAsync();
    }

    [RelayCommand]
    private async Task UpdateKernel()
    {
        if (_kernelManager == null || IsUpdatingKernel) return;

        IsUpdatingKernel = true;
        UpdateStatus = GetString("Settings.Kernel.StartingDownload", "Starting download...");

        var success = await _kernelManager.DownloadAndInstallAsync(null, SelectedKernelDownloadMirror);

        IsUpdatingKernel = false;

        if (success)
        {
            await RefreshKernelInfoAsync();
        }
    }

    [RelayCommand]
    private async Task InstallCustomKernel()
    {
        if (_kernelManager == null || IsUpdatingKernel) return;

        var desktop = Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
        var window = desktop?.MainWindow;
        if (window == null) return;

        var files = await window.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = GetString("Settings.Kernel.SelectCustomExe", "Select Custom Kernel Executable"),
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new Avalonia.Platform.Storage.FilePickerFileType("Executable Files")
                {
                    Patterns = new[] { RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "*.exe" : "*" }
                }
            }
        });

        var file = files.FirstOrDefault();
        if (file == null) return;

        IsUpdatingKernel = true;
        UpdateStatus = GetString("Settings.Kernel.InstallingCustom", "Installing custom kernel...");

        var success = await _kernelManager.InstallCustomKernelAsync(file.Path.LocalPath);

        IsUpdatingKernel = false;

        if (success)
        {
            await RefreshKernelInfoAsync();
        }
    }

    [RelayCommand]
    private async Task UninstallKernel()
    {
        if (_kernelManager == null) return;

        await _kernelManager.UninstallAsync();
        await RefreshKernelInfoAsync();
    }

    [RelayCommand]
    private async Task OpenDataFolder()
    {
        if (_configManager != null)
        {
            await Task.Run(() =>
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = _configManager.ConfigDirectory,
                    UseShellExecute = true,
                    Verb = "open"
                });
            });
        }
    }

    [RelayCommand]
    private async Task ExportBackup()
    {
        if (_configManager == null)
        {
            DataOperationStatus = GetString("Settings.Data.Backup.Export.Failed", "Export backup failed");
            return;
        }

        var desktop = Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
        var window = desktop?.MainWindow;
        if (window == null)
        {
            DataOperationStatus = GetString("Settings.Data.Operation.WindowUnavailable", "Main window unavailable");
            return;
        }

        var file = await window.StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = GetString("Settings.Data.Backup.Export.Title", "Export Backup"),
            SuggestedFileName = $"carton-backup-{DateTime.Now:yyyyMMddHHmmss}",
            DefaultExtension = "zip",
            FileTypeChoices = new[]
            {
                new Avalonia.Platform.Storage.FilePickerFileType("Zip Archive")
                {
                    Patterns = new[] { "*.zip" }
                }
            }
        });

        if (file == null)
        {
            return;
        }

        IsDataOperationInProgress = true;
        DataOperationStatus = GetString("Settings.Data.Backup.Export.InProgress", "Exporting backup...");
        try
        {
            await ExportBackupAsync(file.Path.LocalPath);
            DataOperationStatus = GetString("Settings.Data.Backup.Export.Success", "Backup exported successfully");
        }
        catch (Exception ex)
        {
            DataOperationStatus = $"{GetString("Settings.Data.Backup.Export.Failed", "Export backup failed")}: {ex.Message}";
        }
        finally
        {
            IsDataOperationInProgress = false;
        }
    }

    [RelayCommand]
    private async Task ImportBackup()
    {
        if (_configManager == null)
        {
            DataOperationStatus = GetString("Settings.Data.Backup.Import.Failed", "Import backup failed");
            return;
        }

        var desktop = Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
        var window = desktop?.MainWindow;
        if (window == null)
        {
            DataOperationStatus = GetString("Settings.Data.Operation.WindowUnavailable", "Main window unavailable");
            return;
        }

        var files = await window.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = GetString("Settings.Data.Backup.Import.Title", "Import Backup"),
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new Avalonia.Platform.Storage.FilePickerFileType("Zip Archive")
                {
                    Patterns = new[] { "*.zip" }
                }
            }
        });

        var file = files.FirstOrDefault();
        if (file == null)
        {
            return;
        }

        IsDataOperationInProgress = true;
        DataOperationStatus = GetString("Settings.Data.Backup.Import.InProgress", "Importing backup...");
        try
        {
            await ImportBackupAsync(file.Path.LocalPath);
            DataOperationStatus = GetString("Settings.Data.Backup.Import.Success", "Backup imported successfully");
            await ShowRestartRequiredDialogAndRestartAsync(window);
        }
        catch (Exception ex)
        {
            DataOperationStatus = $"{GetString("Settings.Data.Backup.Import.Failed", "Import backup failed")}: {ex.Message}";
        }
        finally
        {
            IsDataOperationInProgress = false;
        }
    }

    [RelayCommand]
    private void ClearLogs()
    {
    }

    [RelayCommand]
    private Task ResetSettings()
    {
        _currentPreferences = new AppPreferences();
        _suppressPreferenceUpdates = true;
        StartAtLogin = _currentPreferences.StartAtLogin;
        AutoStartOnLaunch = _currentPreferences.AutoStartOnLaunch;
        SelectedTheme = _currentPreferences.Theme;
        SelectedLanguage = Languages.FirstOrDefault(l => l.Language == _currentPreferences.Language) ?? Languages.FirstOrDefault();
        SelectedUpdateChannel = UpdateChannelToString(_currentPreferences.UpdateChannel);
        AutoCheckAppUpdates = _currentPreferences.AutoCheckAppUpdates;
        SelectedKernelDownloadMirror = _currentPreferences.KernelDownloadMirror;
        _suppressPreferenceUpdates = false;
        _localizationService?.SetLanguage(_currentPreferences.Language);
        PersistPreferences();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task CheckAppUpdate()
    {
        if (_appUpdateService == null)
        {
            AppUpdateStatus = GetString("Settings.Update.Status.Unsupported", "Update service unavailable");
            return;
        }

        if (IsCheckingAppUpdate)
        {
            return;
        }

        IsCheckingAppUpdate = true;
        AppUpdateStatus = GetString("Settings.Update.Status.Checking", "Checking for updates...");
        try
        {
            var latestRelease = await _appUpdateService.GetLatestReleaseInfoAsync(SelectedUpdateChannel);
            if (latestRelease != null)
            {
                _latestReleaseInfo = latestRelease;
                LatestAvailableVersion = latestRelease.Version;
            }

            var result = await _appUpdateService.CheckForUpdatesAsync(SelectedUpdateChannel);
            if (result == null)
            {
                _pendingAppUpdate = null;
                IsAppUpdateAvailable = false;
                IsAppUpdateReadyToInstall = false;
                AppUpdateProgress = 0;
                AppUpdateStatus = GetString("Settings.Update.Status.Latest", "Already up to date");
                return;
            }

            _pendingAppUpdate = result;
            _latestReleaseInfo = result.ReleaseInfo;
            LatestAvailableVersion = result.Version;
            IsAppUpdateAvailable = true;
            IsAppUpdateReadyToInstall = false;
            AppUpdateProgress = 0;
            if (_requiresManualAppUpdate)
            {
                AppUpdateStatus = GetString("Settings.Update.Status.ManualRequired", "New version available. Download it from the releases page.");
                await ShowManualUpdatePromptAsync();
            }
            else
            {
                AppUpdateStatus = GetString("Settings.Update.Status.Available", "New version available");
            }
        }
        catch (Exception ex)
        {
            _pendingAppUpdate = null;
            IsAppUpdateAvailable = false;
            IsAppUpdateReadyToInstall = false;
            AppUpdateStatus = $"{GetString("Settings.Update.Status.Error", "Update failed")}: {ex.Message}";
        }
        finally
        {
            IsCheckingAppUpdate = false;
        }
    }

    [RelayCommand]
    private async Task DownloadAppUpdate()
    {
        if (_appUpdateService == null)
        {
            AppUpdateStatus = GetString("Settings.Update.Status.Unsupported", "Update service unavailable");
            return;
        }

        if (_requiresManualAppUpdate)
        {
            AppUpdateStatus = GetString("Settings.Update.Status.ManualRequired", "New version available. Download it from the releases page.");
            await ShowManualUpdatePromptAsync();
            return;
        }

        if (_pendingAppUpdate == null)
        {
            await CheckAppUpdate();
            if (_pendingAppUpdate == null)
            {
                return;
            }
        }

        if (IsDownloadingAppUpdate)
        {
            return;
        }

        IsDownloadingAppUpdate = true;
        IsAppUpdateReadyToInstall = false;
        AppUpdateStatus = GetString("Settings.Update.Status.Downloading", "Downloading update...");
        var progress = new Progress<int>(value => AppUpdateProgress = value);

        try
        {
            await _appUpdateService.DownloadUpdateAsync(_pendingAppUpdate, SelectedUpdateChannel, progress);
            IsAppUpdateReadyToInstall = true;
            AppUpdateStatus = GetString("Settings.Update.Status.Ready", "Update downloaded. Restart to apply.");
        }
        catch (Exception ex)
        {
            IsAppUpdateReadyToInstall = false;
            AppUpdateStatus = $"{GetString("Settings.Update.Status.Error", "Update failed")}: {ex.Message}";
        }
        finally
        {
            IsDownloadingAppUpdate = false;
        }
    }

    [RelayCommand]
    private async Task ApplyAppUpdate()
    {
        if (_appUpdateService == null)
        {
            AppUpdateStatus = GetString("Settings.Update.Status.Unsupported", "Update service unavailable");
            return;
        }

        if (_requiresManualAppUpdate)
        {
            AppUpdateStatus = GetString("Settings.Update.Status.ManualRequired", "New version available. Download it from the releases page.");
            await ShowManualUpdatePromptAsync();
            return;
        }

        if (!IsAppUpdateReadyToInstall)
        {
            AppUpdateStatus = GetString("Settings.Update.Status.DownloadFirst", "Download the update first.");
            return;
        }

        try
        {
            AppUpdateStatus = GetString("Settings.Update.Status.Applying", "Restarting to apply update...");
            await _appUpdateService.RestartToApplyDownloadedUpdateAsync();
        }
        catch (Exception ex)
        {
            AppUpdateStatus = $"{GetString("Settings.Update.Status.Error", "Update failed")}: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task AddProfile()
    {
        if (_profileManager != null)
        {
            var profile = await _profileManager.CreateAsync(new Core.Models.Profile
            {
                Name = $"Profile {Profiles.Count + 1}",
                Type = Core.Models.ProfileType.Local
            });

            Profiles.Add(new ProfileViewModel
            {
                Id = profile.Id,
                Name = profile.Name,
                Type = profile.Type.ToString()
            });
        }
    }

    [RelayCommand]
    private async Task DeleteProfile()
    {
        if (_profileManager != null && SelectedProfile != null)
        {
            await _profileManager.DeleteAsync(SelectedProfile.Id);
            Profiles.Remove(SelectedProfile);
            SelectedProfile = null;
        }
    }

    private void UpdatePreference(Action<AppPreferences> updater)
    {
        updater(_currentPreferences);

        if (_suppressPreferenceUpdates)
        {
            return;
        }

        PersistPreferences();
    }

    private void PersistPreferences()
    {
        if (_preferencesService == null)
        {
            return;
        }

        _preferencesService.Save(_currentPreferences);
    }

    private void OnLanguageOptionChanged(LanguageOptionViewModel? value)
    {
        if (value == null)
        {
            return;
        }

        if (_suppressPreferenceUpdates)
        {
            _currentPreferences.Language = value.Language;
            return;
        }

        _localizationService?.SetLanguage(value.Language);
        UpdatePreference(p => p.Language = value.Language);
    }

    private void OnUpdateChannelChanged(string value)
    {
        var normalized = NormalizeUpdateChannel(value);
        var parsed = ParseUpdateChannel(normalized);
        if (_suppressPreferenceUpdates)
        {
            _currentPreferences.UpdateChannel = parsed;
            return;
        }

        UpdatePreference(p => p.UpdateChannel = parsed);
    }

    private void OnThemeChanged(AppTheme value)
    {
        var appliedTheme = value;
        if (_themeService != null)
        {
            _themeService.ApplyTheme(value);
            appliedTheme = _themeService.CurrentTheme;
        }

        UpdatePreference(p => p.Theme = appliedTheme);
    }

    private string GetString(string key, string fallback)
    {
        if (_localizationService == null)
        {
            return fallback;
        }

        var value = _localizationService[key];
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private string GetUpdateChannelDisplayName(string channel)
    {
        return string.Equals(channel, "beta", StringComparison.OrdinalIgnoreCase)
            ? GetString("Settings.Update.Channel.BetaLabel", "Beta (preview)")
            : GetString("Settings.Update.Channel.ReleaseLabel", "Release (stable)");
    }

    private static string NormalizeUpdateChannel(string? channel)
    {
        if (string.Equals(channel, "beta", StringComparison.OrdinalIgnoreCase))
        {
            return "beta";
        }

        return "release";
    }

    private static AppUpdateChannel ParseUpdateChannel(string? channel)
    {
        return string.Equals(channel, "beta", StringComparison.OrdinalIgnoreCase)
            ? AppUpdateChannel.Beta
            : AppUpdateChannel.Release;
    }

    private static string UpdateChannelToString(AppUpdateChannel channel)
        => channel == AppUpdateChannel.Beta ? "beta" : "release";

    private async Task ExportBackupAsync(string zipPath)
    {
        if (_configManager == null)
        {
            return;
        }

        var baseDirectory = ResolveBaseDirectory(_configManager);
        var singBoxDataPath = Path.Combine(baseDirectory, "sing-box-data.json");
        var preferencesPath = Path.Combine(baseDirectory, "preferences.json");
        var localConfigDirectory = _configManager.LocalConfigDirectory;

        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }

        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        AddFileIfExists(archive, singBoxDataPath, "sing-box-data.json");
        AddFileIfExists(archive, preferencesPath, "preferences.json");

        if (Directory.Exists(localConfigDirectory))
        {
            foreach (var file in Directory.GetFiles(localConfigDirectory, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(localConfigDirectory, file).Replace('\\', '/');
                var entryName = $"configs/local/{relative}";
                archive.CreateEntryFromFile(file, entryName);
            }
        }

        await Task.CompletedTask;
    }

    private async Task ImportBackupAsync(string zipPath)
    {
        if (_configManager == null)
        {
            return;
        }

        var baseDirectory = ResolveBaseDirectory(_configManager);
        var singBoxDataPath = Path.Combine(baseDirectory, "sing-box-data.json");
        var preferencesPath = Path.Combine(baseDirectory, "preferences.json");
        var localConfigDirectory = _configManager.LocalConfigDirectory;

        using var archive = ZipFile.OpenRead(zipPath);
        var localPrefix = "configs/local/";
        var hasLocalEntries = archive.Entries.Any(entry =>
        {
            var entryName = entry.FullName.Replace('\\', '/').TrimStart('/');
            return entryName.StartsWith(localPrefix, StringComparison.OrdinalIgnoreCase) &&
                   !entryName.EndsWith("/", StringComparison.Ordinal);
        });

        if (hasLocalEntries)
        {
            ResetDirectory(localConfigDirectory);
        }

        foreach (var entry in archive.Entries)
        {
            var entryName = entry.FullName.Replace('\\', '/').TrimStart('/');
            if (string.IsNullOrWhiteSpace(entryName) || entryName.EndsWith("/", StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(entryName, "sing-box-data.json", StringComparison.OrdinalIgnoreCase))
            {
                ExtractEntryToFile(entry, singBoxDataPath);
                continue;
            }

            if (string.Equals(entryName, "preferences.json", StringComparison.OrdinalIgnoreCase))
            {
                ExtractEntryToFile(entry, preferencesPath);
                continue;
            }

            if (entryName.StartsWith(localPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var relative = entryName[localPrefix.Length..];
                var destination = ResolveSafePath(localConfigDirectory, relative);
                ExtractEntryToFile(entry, destination);
            }
        }

        await Task.CompletedTask;
    }

    private static string ResolveBaseDirectory(IConfigManager configManager)
    {
        var parent = Directory.GetParent(configManager.ConfigDirectory)?.FullName;
        if (string.IsNullOrWhiteSpace(parent))
        {
            throw new InvalidOperationException("Unable to resolve data directory");
        }

        return parent;
    }

    private static void AddFileIfExists(ZipArchive archive, string sourcePath, string entryName)
    {
        if (File.Exists(sourcePath))
        {
            archive.CreateEntryFromFile(sourcePath, entryName);
        }
    }

    private static void ResetDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }

        Directory.CreateDirectory(path);
    }

    private static string ResolveSafePath(string rootDirectory, string relativePath)
    {
        var normalizedRoot = Path.GetFullPath(rootDirectory);
        var destinationPath = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var rootPrefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;

        if (!destinationPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Invalid backup entry path");
        }

        return destinationPath;
    }

    private static void ExtractEntryToFile(ZipArchiveEntry entry, string destinationPath)
    {
        var destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(destinationDirectory))
        {
            Directory.CreateDirectory(destinationDirectory);
        }

        using var source = entry.Open();
        using var target = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        source.CopyTo(target);
    }

    private async Task ShowRestartRequiredDialogAndRestartAsync(Window owner)
    {
        var dialog = new Window
        {
            Width = 420,
            Height = 180,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Title = GetString("Settings.Data.Backup.Import.Restart.Title", "Restart Required")
        };

        var message = new TextBlock
        {
            Text = GetString("Settings.Data.Backup.Import.Restart.Message", "Backup import completed. The app needs to restart to apply changes."),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        };

        var okButton = new Button
        {
            Content = GetString("Settings.Data.Backup.Import.Restart.Button", "Restart Now"),
            HorizontalAlignment = HorizontalAlignment.Right,
            MinWidth = 110
        };
        okButton.Click += (_, _) => dialog.Close(true);

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Children =
            {
                message,
                okButton
            }
        };

        await dialog.ShowDialog<bool>(owner);
        await RestartApplicationAsync(owner);
    }

    private async Task RestartApplicationAsync(Window owner)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            return;
        }

        if (owner.DataContext is MainViewModel mainViewModel)
        {
            await mainViewModel.ShutdownAsync();
        }

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            executablePath = Process.GetCurrentProcess().MainModule?.FileName;
        }

        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(executablePath) ?? Environment.CurrentDirectory
            });
        }

        desktop.Shutdown();
    }

    private async Task ShowManualUpdatePromptAsync()
    {
        if (_appUpdateService == null)
        {
            return;
        }

        var releaseUrl = _appUpdateService.ReleasesPageUrl;
        if (string.IsNullOrWhiteSpace(releaseUrl))
        {
            return;
        }

        if (Avalonia.Application.Current?.ApplicationLifetime is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop ||
            desktop.MainWindow == null)
        {
            OpenReleasesPage(releaseUrl);
            return;
        }

        var owner = desktop.MainWindow;
        var versionLabel = string.IsNullOrWhiteSpace(LatestAvailableVersion)
            ? GetString("Common.Unknown", "unknown")
            : LatestAvailableVersion;

        var dialog = new Window
        {
            Width = 480,
            Height = 220,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Title = GetString("Settings.Update.ManualDialog.Title", "Manual update required")
        };

        var message = new TextBlock
        {
            Text = string.Format(
                GetString(
                    "Settings.Update.ManualDialog.Message",
                    "Portable builds cannot update automatically. Open the releases page to download version {0}?"),
                versionLabel),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        };

        var openButton = new Button
        {
            Content = GetString("Settings.Update.ManualDialog.OpenButton", "Open Releases"),
            MinWidth = 120
        };
        openButton.Click += (_, _) => dialog.Close(true);

        var laterButton = new Button
        {
            Content = GetString("Settings.Update.ManualDialog.LaterButton", "Later"),
            MinWidth = 90
        };
        laterButton.Click += (_, _) => dialog.Close(false);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children =
            {
                laterButton,
                openButton
            }
        };

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Children =
            {
                message,
                buttons
            }
        };

        var shouldOpen = await dialog.ShowDialog<bool>(owner);
        if (shouldOpen)
        {
            OpenReleasesPage(releaseUrl);
        }
    }

    private void OpenReleasesPage(string releaseUrl)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = releaseUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppUpdateStatus = $"{GetString("Settings.Update.Status.OpenReleasesFailed", "Failed to open releases page")}: {ex.Message}";
        }
    }

    private async Task LoadProfilesAsync()
    {
        if (_profileManager == null) return;

        var profiles = await _profileManager.ListAsync();
        var selectedId = await _profileManager.GetSelectedProfileIdAsync();

        Profiles.Clear();
        foreach (var profile in profiles)
        {
            var vm = new ProfileViewModel
            {
                Id = profile.Id,
                Name = profile.Name,
                Type = profile.Type.ToString(),
                LastUpdated = profile.LastUpdated?.ToString("yyyy-MM-dd HH:mm") ?? ""
            };
            Profiles.Add(vm);

            if (profile.Id == selectedId)
            {
                SelectedProfile = vm;
            }
        }
    }
}

public partial class ProfileViewModel : ObservableObject
{
    [ObservableProperty]
    private int _id;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _type = string.Empty;

    [ObservableProperty]
    private string _lastUpdated = string.Empty;
}

public class LanguageOptionViewModel : ObservableObject
{
    public AppLanguage Language { get; }

    public string DisplayName => Language switch
    {
        AppLanguage.SimplifiedChinese => "简体中文",
        _ => "English"
    };

    public LanguageOptionViewModel(AppLanguage language)
    {
        Language = language;
    }

    public override string ToString() => DisplayName;
}

public partial class UpdateChannelOptionViewModel : ObservableObject
{
    public string Channel { get; }

    [ObservableProperty]
    private string _displayName;

    public UpdateChannelOptionViewModel(string channel, string displayName)
    {
        Channel = channel;
        _displayName = displayName;
    }
}
