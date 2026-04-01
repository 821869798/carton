using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using carton.Core.Services;
using carton.Core.Models;
using carton.Core.Utilities;
using carton.GUI.Models;
using carton.GUI.Services;

namespace carton.ViewModels;

public partial class ProfilesViewModel : PageViewModelBase, IDisposable
{
    private const string DefaultUpdateIntervalMinutes = "1440";

    private readonly IProfileManager? _profileManager;
    private readonly IConfigManager? _configManager;
    private readonly ISingBoxManager? _singBoxManager;
    private readonly RemoteConfigUpdateService? _remoteConfigUpdateService;
    private readonly Action<string, int>? _toastWriter;
    private readonly ILocalizationService _localizationService;
    private string _neverLabel = "Never";

    public override NavigationPage PageType => NavigationPage.Profiles;

    [ObservableProperty]
    private ObservableCollection<ProfileItemViewModel> _profiles = new();

    [ObservableProperty]
    private ProfileItemViewModel? _selectedProfile;

    [ObservableProperty]
    private bool _isImporting;

    [ObservableProperty]
    private string _importStatus = string.Empty;

    [ObservableProperty]
    private string _configContent = string.Empty;

    [ObservableProperty]
    private bool _isContentEditorVisible;

    [ObservableProperty]
    private bool _isEditingMode;

    private ProfileItemViewModel? _editingProfile;

    [ObservableProperty]
    private bool _isCreatingMode;

    [ObservableProperty]
    private string _newProfileName = string.Empty;

    [ObservableProperty]
    private int _newProfileType = 0;

    [ObservableProperty]
    private int _newLocalMode = 0;

    [ObservableProperty]
    private string _newLocalFilePath = string.Empty;

    [ObservableProperty]
    private string _newProfileUrl = string.Empty;

    [ObservableProperty]
    private bool _newAutoUpdate = true;

    [ObservableProperty]
    private string _newUpdateIntervalMinutes = DefaultUpdateIntervalMinutes;

    [ObservableProperty]
    private string _newProfileStatus = string.Empty;

    [ObservableProperty]
    private bool _isCreating;

    [ObservableProperty]
    private bool _isFormChanged;

    [ObservableProperty]
    private string _editLastUpdated = string.Empty;

    private string _initialName = string.Empty;
    private string _initialUrl = string.Empty;
    private bool _initialAutoUpdate = true;
    private string _initialUpdateIntervalMinutes = DefaultUpdateIntervalMinutes;
    private string _initialConfigContent = string.Empty;

    public ObservableCollection<string> ProfileTypes { get; } = new();
    public ObservableCollection<string> LocalModes { get; } = new();
    public ObservableCollection<string> UpdateIntervals { get; } = new() { "No Update", "Every Hour", "Every 6 Hours", "Every 12 Hours", "Every Day" };

    public bool IsLocalProfile => NewProfileType == 0;
    public bool IsRemoteProfile => NewProfileType == 1;
    public bool IsProfileFormMode => IsCreatingMode || IsEditingMode;
    public bool IsTypeEditable => !IsEditingMode;
    public bool IsLocalModeEditable => !IsEditingMode;
    public bool IsLocalImportMode => IsLocalProfile && NewLocalMode == 1;
    public bool ShowLocalFileModeSelector => IsCreatingMode && IsLocalProfile;
    public bool ShowLocalImportFilePicker => IsCreatingMode && IsLocalImportMode;
    public bool ShowCreateLocalContentEditor => IsCreatingMode && IsLocalProfile && NewLocalMode == 0;
    public bool ShowContentAction => IsEditingMode && IsRemoteProfile;
    public bool IsRemoteEditStatusVisible => IsEditingMode && IsRemoteProfile;
    public bool CanEditContent => IsLocalProfile;
    public string ContentActionText => IsContentEditorVisible
        ? GetString("Profiles.Form.Content.Hide", "Hide Content")
        : GetString("Profiles.Form.Content.Show", "View Content");
    public bool ShowConfigEditor =>
        ShowCreateLocalContentEditor ||
        (IsEditingMode && IsLocalProfile) ||
        (IsEditingMode && IsRemoteProfile && IsContentEditorVisible);
    public bool IsConfigReadOnly => IsEditingMode && IsRemoteProfile;
    public bool CanUpdateSelected => SelectedProfile?.IsRemoteType == true;
    public bool CanSaveProfile => IsEditingMode && IsFormChanged && !IsCreating;
    public string ProfileFormTitle => IsEditingMode
        ? GetString("Profiles.Form.Title.Edit", "Edit Profile")
        : GetString("Profiles.Form.Title.New", "New Profile");

    partial void OnNewProfileTypeChanged(int value)
    {
        OnPropertyChanged(nameof(IsLocalProfile));
        OnPropertyChanged(nameof(IsRemoteProfile));
        OnPropertyChanged(nameof(IsLocalImportMode));
        OnPropertyChanged(nameof(ShowContentAction));
        OnPropertyChanged(nameof(ShowLocalFileModeSelector));
        OnPropertyChanged(nameof(ShowLocalImportFilePicker));
        OnPropertyChanged(nameof(ShowCreateLocalContentEditor));
        OnPropertyChanged(nameof(IsRemoteEditStatusVisible));
        OnPropertyChanged(nameof(CanEditContent));
        OnPropertyChanged(nameof(ContentActionText));
        OnPropertyChanged(nameof(IsConfigReadOnly));
        OnPropertyChanged(nameof(ShowConfigEditor));
    }

    partial void OnNewLocalModeChanged(int value)
    {
        OnPropertyChanged(nameof(IsLocalImportMode));
        OnPropertyChanged(nameof(ShowLocalImportFilePicker));
        OnPropertyChanged(nameof(ShowCreateLocalContentEditor));
        OnPropertyChanged(nameof(ShowConfigEditor));
    }

    partial void OnIsCreatingModeChanged(bool value)
    {
        OnPropertyChanged(nameof(IsProfileFormMode));
        OnPropertyChanged(nameof(ShowLocalFileModeSelector));
        OnPropertyChanged(nameof(ShowLocalImportFilePicker));
        OnPropertyChanged(nameof(ShowCreateLocalContentEditor));
        OnPropertyChanged(nameof(ShowConfigEditor));
    }

    partial void OnIsEditingModeChanged(bool value)
    {
        OnPropertyChanged(nameof(IsProfileFormMode));
        OnPropertyChanged(nameof(IsTypeEditable));
        OnPropertyChanged(nameof(IsLocalModeEditable));
        OnPropertyChanged(nameof(ShowContentAction));
        OnPropertyChanged(nameof(IsRemoteEditStatusVisible));
        OnPropertyChanged(nameof(IsConfigReadOnly));
        OnPropertyChanged(nameof(ShowLocalFileModeSelector));
        OnPropertyChanged(nameof(ShowLocalImportFilePicker));
        OnPropertyChanged(nameof(ShowCreateLocalContentEditor));
        OnPropertyChanged(nameof(CanSaveProfile));
        OnPropertyChanged(nameof(ShowConfigEditor));
        OnPropertyChanged(nameof(ProfileFormTitle));
    }

    partial void OnSelectedProfileChanged(ProfileItemViewModel? value)
    {
        OnPropertyChanged(nameof(CanUpdateSelected));
    }

    partial void OnIsContentEditorVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(ContentActionText));
        OnPropertyChanged(nameof(ShowConfigEditor));
    }

    partial void OnNewProfileNameChanged(string value) => RecalculateFormChanged();
    partial void OnNewProfileUrlChanged(string value) => RecalculateFormChanged();
    partial void OnNewAutoUpdateChanged(bool value) => RecalculateFormChanged();
    partial void OnNewUpdateIntervalMinutesChanged(string value) => RecalculateFormChanged();
    partial void OnConfigContentChanged(string value) => RecalculateFormChanged();

    public ProfilesViewModel()
    {
        _localizationService = LocalizationService.Instance;
        _localizationService.LanguageChanged += OnLanguageChanged;
        Icon = "Profiles";
        UpdateLocalizedTexts();
    }

    public ProfilesViewModel(
        IProfileManager profileManager,
        IConfigManager configManager,
        ISingBoxManager singBoxManager,
        IPreferencesService preferencesService,
        Action<string, int>? toastWriter = null) : this()
    {
        _profileManager = profileManager;
        _configManager = configManager;
        _singBoxManager = singBoxManager;
        _remoteConfigUpdateService = new RemoteConfigUpdateService(configManager, profileManager, preferencesService);
        _toastWriter = toastWriter;
        _ = LoadProfilesAsync();
    }

    public async Task RefreshAsync()
    {
        await LoadProfilesAsync();
    }

    private async Task LoadProfilesAsync()
    {
        if (_profileManager == null) return;

        var profiles = await _profileManager.ListAsync();
        var selectedId = await _profileManager.GetSelectedProfileIdAsync();

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            Profiles.Clear();
            SelectedProfile = null;
            foreach (var profile in profiles)
            {
                var vm = new ProfileItemViewModel
                {
                    Id = profile.Id,
                    Name = profile.Name,
                    Type = GetProfileTypeDisplay(profile.Type),
                    LastUpdated = profile.LastUpdated?.ToString("yyyy-MM-dd HH:mm") ?? _neverLabel,
                    Url = profile.Url ?? string.Empty,
                    IsSelected = profile.Id == selectedId,
                    UpdateInterval = profile.UpdateInterval,
                    AutoUpdate = profile.AutoUpdate,
                    IsRemoteType = profile.Type == ProfileType.Remote
                };
                Profiles.Add(vm);

                if (profile.Id == selectedId)
                {
                    SelectedProfile = vm;
                }
            }

            if (SelectedProfile == null && Profiles.Count > 0)
            {
                SelectedProfile = Profiles[0];
            }
        });
    }

    [RelayCommand]
    private void ShowCreateProfileDialog()
    {
        NewProfileName = $"New Profile {DateTime.Now:yyyyMMddHHmmss}";
        NewProfileType = 1;
        NewLocalMode = 0;
        NewLocalFilePath = string.Empty;
        NewProfileUrl = string.Empty;
        NewAutoUpdate = true;
        NewUpdateIntervalMinutes = DefaultUpdateIntervalMinutes;
        ConfigContent = "{}";
        IsContentEditorVisible = false;
        _editingProfile = null;
        IsEditingMode = false;
        IsFormChanged = false;
        NewProfileStatus = string.Empty;
        IsCreatingMode = true;
    }

    [RelayCommand]
    private void CancelCreate()
    {
        IsCreatingMode = false;
        IsEditingMode = false;
        IsFormChanged = false;
        _editingProfile = null;
        IsContentEditorVisible = false;
        NewProfileStatus = string.Empty;
    }

    [RelayCommand]
    private async Task CreateProfile()
    {
        if (_profileManager == null || _configManager == null) return;

        if (string.IsNullOrWhiteSpace(NewProfileName))
        {
            NewProfileStatus = "Please enter a profile name";
            return;
        }

        IsCreating = true;
        NewProfileStatus = "Creating...";

        try
        {
            var normalizedName = NewProfileName.Trim();
            var profileType = NewProfileType == 0 ? ProfileType.Local : ProfileType.Remote;
            string? configContent = null;

            if (profileType == ProfileType.Local)
            {
                if (NewLocalMode == 0)
                {
                    configContent = string.IsNullOrWhiteSpace(ConfigContent) ? "{}" : ConfigContent;
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(NewLocalFilePath) || !File.Exists(NewLocalFilePath))
                    {
                        NewProfileStatus = "Please choose a local file";
                        IsCreating = false;
                        return;
                    }

                    configContent = string.IsNullOrWhiteSpace(ConfigContent)
                        ? await File.ReadAllTextAsync(NewLocalFilePath)
                        : ConfigContent;
                }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(NewProfileUrl))
                {
                    NewProfileStatus = "Please enter a URL";
                    IsCreating = false;
                    return;
                }

                NewProfileStatus = "Downloading from URL...";

                var httpClient = HttpClientFactory.External;
                configContent = await httpClient.GetStringAsync(NewProfileUrl);
            }

            var updateInterval = ParseUpdateIntervalMinutes();
            var autoUpdate = profileType == ProfileType.Remote && NewAutoUpdate && updateInterval > 0;

            var profile = await _profileManager.CreateAsync(new Core.Models.Profile
            {
                Name = normalizedName,
                Type = profileType,
                Url = profileType == ProfileType.Remote ? NewProfileUrl : null,
                LastUpdated = DateTime.Now,
                UpdateInterval = autoUpdate ? updateInterval : 0,
                AutoUpdate = autoUpdate
            }, configContent);

            await _profileManager.SetSelectedProfileIdAsync(profile.Id);
            await LoadProfilesAsync();

            IsCreatingMode = false;
            NewProfileStatus = "Profile created successfully!";
        }
        catch (Exception ex)
        {
            NewProfileStatus = $"Failed to create: {ex.Message}";
        }
        finally
        {
            IsCreating = false;
        }
    }

    [RelayCommand]
    private async Task ImportFromFile()
    {
        try
        {
            var storageProvider = Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
            var window = storageProvider?.MainWindow;

            if (window == null) return;

            var storage = window.StorageProvider;
            var files = await storage.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = "Select Profile File",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new Avalonia.Platform.Storage.FilePickerFileType("JSON Files") { Patterns = new[] { "*.json" } },
                    new Avalonia.Platform.Storage.FilePickerFileType("All Files") { Patterns = new[] { "*.*" } }
                }
            });

            var file = files.FirstOrDefault();
            if (file == null) return;

            NewLocalFilePath = file.Path.LocalPath;
            NewLocalMode = 1;
            NewProfileType = 0;
            NewProfileName = Path.GetFileNameWithoutExtension(NewLocalFilePath);
            NewProfileUrl = string.Empty;
            NewAutoUpdate = false;
            NewUpdateIntervalMinutes = DefaultUpdateIntervalMinutes;
            ConfigContent = "{}";
            IsContentEditorVisible = false;
            _editingProfile = null;
            IsEditingMode = false;
            IsCreatingMode = true;
            NewProfileStatus = string.Empty;
        }
        catch (Exception ex)
        {
            NewProfileStatus = $"Import failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task DeleteProfile(ProfileItemViewModel? profile = null)
    {
        if (_profileManager == null) return;

        var target = profile;
        if (target == null) return;

        await _profileManager.DeleteAsync(target.Id);

        if (target.IsSelected)
        {
            var remaining = await _profileManager.ListAsync();
            await _profileManager.SetSelectedProfileIdAsync(remaining.FirstOrDefault()?.Id ?? 0);
        }

        await LoadProfilesAsync();
        if (SelectedProfile?.Id == target.Id)
        {
            SelectedProfile = null;
        }
    }

    [RelayCommand]
    private async Task SelectProfile(ProfileItemViewModel? profile = null)
    {
        if (_profileManager == null) return;

        var target = profile ?? SelectedProfile;
        if (target == null) return;

        await _profileManager.SetSelectedProfileIdAsync(target.Id);
        SelectedProfile = target;

        foreach (var p in Profiles)
        {
            p.IsSelected = p.Id == target.Id;
        }
    }

    [RelayCommand]
    private async Task EditProfile(ProfileItemViewModel? profile = null)
    {
        if (_configManager == null || _profileManager == null) return;

        var target = profile ?? SelectedProfile;
        if (target == null) return;

        var profileModel = await _profileManager.GetAsync(target.Id);
        if (profileModel == null) return;

        var content = await _configManager.LoadConfigAsync(target.Id);
        ConfigContent = content ?? "{}";
        NewProfileName = string.IsNullOrWhiteSpace(profileModel.Name) ? target.DisplayName : profileModel.Name;
        NewProfileType = profileModel.Type == ProfileType.Local ? 0 : 1;
        NewLocalMode = 0;
        NewProfileUrl = profileModel.Url ?? string.Empty;
        NewLocalFilePath = string.Empty;
        NewAutoUpdate = profileModel.AutoUpdate;
        NewUpdateIntervalMinutes = profileModel.UpdateInterval > 0 ? profileModel.UpdateInterval.ToString() : DefaultUpdateIntervalMinutes;
        EditLastUpdated = profileModel.LastUpdated?.ToString("yyyy-MM-dd HH:mm:ss") ?? _neverLabel;
        NewProfileStatus = string.Empty;
        IsContentEditorVisible = false;
        _editingProfile = target;
        _initialName = NewProfileName;
        _initialUrl = NewProfileUrl;
        _initialAutoUpdate = NewAutoUpdate;
        _initialUpdateIntervalMinutes = NewUpdateIntervalMinutes;
        _initialConfigContent = ConfigContent;
        IsFormChanged = false;
        IsCreatingMode = false;
        IsEditingMode = true;
    }

    [RelayCommand]
    private async Task BrowseLocalFile()
    {
        var storageProvider = Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
        var window = storageProvider?.MainWindow;

        if (window == null)
        {
            NewProfileStatus = "Cannot open file dialog";
            return;
        }

        var storage = window.StorageProvider;
        var files = await storage.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Select Profile File",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new Avalonia.Platform.Storage.FilePickerFileType("JSON Files") { Patterns = new[] { "*.json" } },
                new Avalonia.Platform.Storage.FilePickerFileType("All Files") { Patterns = new[] { "*.*" } }
            }
        });

        var file = files.FirstOrDefault();
        if (file == null) return;

        NewLocalFilePath = file.Path.LocalPath;
        ConfigContent = await File.ReadAllTextAsync(NewLocalFilePath);
        if (string.IsNullOrWhiteSpace(NewProfileName) || NewProfileName.StartsWith("New Profile ", StringComparison.Ordinal))
        {
            NewProfileName = Path.GetFileNameWithoutExtension(NewLocalFilePath);
        }
    }

    [RelayCommand]
    private void ToggleContentEditor()
    {
        if (string.IsNullOrWhiteSpace(ConfigContent))
        {
            ConfigContent = "{}";
        }
        IsContentEditorVisible = !IsContentEditorVisible;
    }

    [RelayCommand]
    private async Task SaveProfile()
    {
        if (_configManager == null || _profileManager == null || _editingProfile == null) return;

        if (string.IsNullOrWhiteSpace(NewProfileName))
        {
            NewProfileStatus = "Profile name cannot be empty";
            return;
        }

        if (IsRemoteProfile && string.IsNullOrWhiteSpace(NewProfileUrl))
        {
            NewProfileStatus = "Remote profile URL cannot be empty";
            return;
        }

        IsCreating = true;
        NewProfileStatus = "Saving...";

        try
        {
            await _configManager.SaveConfigAsync(
                _editingProfile.Id,
                ConfigContent,
                _editingProfile.IsRemoteType ? ProfileType.Remote : ProfileType.Local);

            var profile = await _profileManager.GetAsync(_editingProfile.Id);
            if (profile != null)
            {
                profile.Name = NewProfileName;
                if (profile.Type == ProfileType.Remote)
                {
                    var updateInterval = ParseUpdateIntervalMinutes();
                    profile.Url = NewProfileUrl;
                    profile.AutoUpdate = NewAutoUpdate && updateInterval > 0;
                    profile.UpdateInterval = profile.AutoUpdate ? updateInterval : 0;
                }
                await _profileManager.UpdateAsync(profile);
            }

            await LoadProfilesAsync();
            IsEditingMode = false;
            IsFormChanged = false;
            _editingProfile = null;
            NewProfileStatus = string.Empty;
        }
        catch (Exception ex)
        {
            NewProfileStatus = $"Save failed: {ex.Message}";
        }
        finally
        {
            IsCreating = false;
        }
    }

    [RelayCommand]
    private async Task UpdateProfile(ProfileItemViewModel? profile = null)
    {
        if (_configManager == null || _profileManager == null) return;

        var target = profile ?? SelectedProfile;
        if (target == null || string.IsNullOrWhiteSpace(target.Url)) return;

        ImportStatus = GetString("Profiles.Status.Updating", "Updating...");
        IsImporting = true;

        try
        {
            var profileModel = await _profileManager.GetAsync(target.Id);
            if (profileModel == null)
            {
                ImportStatus = $"{GetString("Profiles.Status.UpdateFailed", "Update failed")}: {GetString("Profiles.Status.ProfileNotFound", "profile not found")} ({target.Id})";
                return;
            }

            var mixedPort = await ResolveActiveMixedPortAsync();
            var result = await _remoteConfigUpdateService!.UpdateAsync(profileModel, mixedPort);
            if (!result.Success)
            {
                ImportStatus = $"{GetString("Profiles.Status.UpdateFailed", "Update failed")}: {result.ErrorMessage}";
                return;
            }

            await LoadProfilesAsync();
            ImportStatus = result.UsedProxy
                ? $"{GetString("Profiles.Status.UpdateSucceededViaProxy", "Update successful via mixed proxy")}: {target.Name}"
                : $"{GetString("Profiles.Status.UpdateSucceeded", "Update successful")}: {target.Name}";
            _toastWriter?.Invoke(ImportStatus, 2200);
        }
        catch (Exception ex)
        {
            ImportStatus = $"{GetString("Profiles.Status.UpdateFailed", "Update failed")}: {ex.Message}";
            _toastWriter?.Invoke(ImportStatus, 3200);
        }
        finally
        {
            IsImporting = false;
        }
    }

    [RelayCommand]
    private void ShowQrCode(ProfileItemViewModel? profile = null)
    {
        var target = profile ?? SelectedProfile;
        if (target == null) return;

        if (!target.IsRemoteType || string.IsNullOrWhiteSpace(target.Url))
        {
            ImportStatus = "This profile has no URL QR content";
            return;
        }

        ImportStatus = $"QR URL: {target.Url}";
    }

    [RelayCommand]
    private async Task ShareProfile(ProfileItemViewModel? profile = null)
    {
        if (_configManager == null) return;

        var target = profile ?? SelectedProfile;
        if (target == null) return;

        var shareText = target.IsRemoteType && !string.IsNullOrWhiteSpace(target.Url)
            ? target.Url
            : await _configManager.LoadConfigAsync(target.Id) ?? "{}";

        var desktop = Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
        var window = desktop?.MainWindow;

        if (window?.Clipboard == null)
        {
            ImportStatus = "Clipboard is not available";
            return;
        }

        await window.Clipboard.SetTextAsync(shareText);
        ImportStatus = $"Copied: {target.Name}";
    }

    private int ParseUpdateIntervalMinutes()
    {
        if (int.TryParse(NewUpdateIntervalMinutes, out var minutes) && minutes > 0)
        {
            return minutes;
        }
        return int.Parse(DefaultUpdateIntervalMinutes);
    }

    private void RecalculateFormChanged()
    {
        if (!IsEditingMode)
        {
            IsFormChanged = false;
            OnPropertyChanged(nameof(CanSaveProfile));
            return;
        }

        var changed =
            !string.Equals(NewProfileName, _initialName, StringComparison.Ordinal) ||
            !string.Equals(NewProfileUrl, _initialUrl, StringComparison.Ordinal) ||
            NewAutoUpdate != _initialAutoUpdate ||
            !string.Equals(NewUpdateIntervalMinutes, _initialUpdateIntervalMinutes, StringComparison.Ordinal) ||
            !string.Equals(ConfigContent, _initialConfigContent, StringComparison.Ordinal);

        IsFormChanged = changed;
        OnPropertyChanged(nameof(CanSaveProfile));
    }

    private void OnLanguageChanged(object? sender, AppLanguage e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(UpdateLocalizedTexts);
    }

    public void Dispose()
    {
        _localizationService.LanguageChanged -= OnLanguageChanged;
    }

    private async Task<int?> ResolveActiveMixedPortAsync()
    {
        if (_singBoxManager?.IsRunning != true || _profileManager == null)
        {
            return null;
        }

        var selectedId = await _profileManager.GetSelectedProfileIdAsync();
        if (selectedId <= 0)
        {
            return 2028;
        }

        var runtimeOptions = await _profileManager.GetRuntimeOptionsAsync(selectedId);
        return runtimeOptions.InboundPort is >= 1 and <= 65535
            ? runtimeOptions.InboundPort
            : 2028;
    }

    private void UpdateLocalizedTexts()
    {
        Title = GetString("Navigation.Profiles", "Profiles");
        _neverLabel = GetString("Profiles.List.Never", "Never");
        if (string.IsNullOrWhiteSpace(EditLastUpdated))
        {
            EditLastUpdated = _neverLabel;
        }

        ProfileTypes.Clear();
        ProfileTypes.Add(GetString("Profiles.Form.Type.Local", "Local"));
        ProfileTypes.Add(GetString("Profiles.Form.Type.Remote", "Remote"));

        LocalModes.Clear();
        LocalModes.Add(GetString("Profiles.Form.LocalMode.Create", "Create New"));
        LocalModes.Add(GetString("Profiles.Form.LocalMode.Import", "Import"));

        OnPropertyChanged(nameof(ProfileFormTitle));
        OnPropertyChanged(nameof(ContentActionText));
    }

    private string GetString(string key, string fallback)
    {
        var value = _localizationService[key];
        return string.IsNullOrWhiteSpace(value) || value == key ? fallback : value;
    }

    private string GetProfileTypeDisplay(ProfileType type)
    {
        return type == ProfileType.Local
            ? GetString("Profiles.Form.Type.Local", "Local")
            : GetString("Profiles.Form.Type.Remote", "Remote");
    }
}

public partial class ProfileItemViewModel : ObservableObject
{
    [ObservableProperty]
    private int _id;

    [ObservableProperty]
    private string _name = string.Empty;

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"Profile {Id}" : Name;

    [ObservableProperty]
    private string _type = string.Empty;

    [ObservableProperty]
    private string _lastUpdated = string.Empty;

    [ObservableProperty]
    private string _url = string.Empty;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private int _updateInterval;

    [ObservableProperty]
    private bool _autoUpdate;

    [ObservableProperty]
    private bool _isRemoteType;

    partial void OnNameChanged(string value)
    {
        OnPropertyChanged(nameof(DisplayName));
    }

    partial void OnIdChanged(int value)
    {
        OnPropertyChanged(nameof(DisplayName));
    }
}
