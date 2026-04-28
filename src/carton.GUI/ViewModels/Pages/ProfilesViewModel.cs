using System;
using System.Buffers;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using carton.Core.Services;
using carton.Core.Models;
using carton.Core.Utilities;
using carton.GUI.Models;
using carton.GUI.Services;
using Avalonia.Threading;

namespace carton.ViewModels;

public partial class ProfilesViewModel : PageViewModelBase, IDisposable
{
    private const string DefaultUpdateIntervalMinutes = "1440";

    private readonly IProfileManager? _profileManager;
    private readonly IConfigManager? _configManager;
    private readonly ISingBoxManager? _singBoxManager;
    private readonly RemoteConfigUpdateService? _remoteConfigUpdateService;
    private readonly Action<string, int>? _toastWriter;
    private readonly Func<Task>? _profilesChangedCallback;
    private readonly ILocalizationService _localizationService;
    private string _neverLabel = "Never";
    private int? _runningProfileId;

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
    private bool _isConfigContentLoaded;

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
    public bool ShowContentAction => IsEditingMode;
    public bool IsRemoteEditStatusVisible => IsEditingMode && IsRemoteProfile;
    public bool CanEditContent => IsLocalProfile;
    public bool ShowExternalEditorAction => IsEditingMode && IsLocalProfile;
    public string FormatJsonText => GetString("Profiles.Form.FormatJson", "Format");
    public string ValidateJsonText => GetString("Profiles.Form.ValidateJson", "Validate JSON");
    public string UpdateConfigText => GetString("Profiles.Form.UpdateConfig", "更新配置");
    public string ContentActionText => IsContentEditorVisible
        ? GetString("Profiles.Form.Content.Hide", "Hide Content")
        : GetString("Profiles.Form.Content.Show", "View Content");
    public string OpenExternalText => GetString("Profiles.Form.OpenExternal", "通过外部编辑器打开");
    public string CopyAsLocalText => GetString("Profiles.Form.CopyAsLocal", "复制为本地配置");
    public bool ShowInlineConfigEditor => ShowCreateLocalContentEditor;
    public bool ShowConfigFullscreenView => IsEditingMode && IsContentEditorVisible;
    public bool IsConfigReadOnly => IsEditingMode && IsRemoteProfile;
    public bool CanUpdateSelected => SelectedProfile?.IsRemoteType == true;
    public bool CanSaveProfile => IsEditingMode && !IsCreating && HasProfileMetadataChanges();
    public bool CanSaveConfigInEditor => IsEditingMode && IsLocalProfile && !IsCreating && HasUnsavedChanges();
    public string ProfileFormTitle => IsEditingMode
        ? GetString("Profiles.Form.Title.Edit", "Edit Profile")
        : GetString("Profiles.Form.Title.New", "New Profile");
    public bool HasLoadedConfigContent => _isConfigContentLoaded;

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
        OnPropertyChanged(nameof(ShowInlineConfigEditor));
        OnPropertyChanged(nameof(ShowConfigFullscreenView));
        OnPropertyChanged(nameof(ShowExternalEditorAction));
        OnPropertyChanged(nameof(CanSaveConfigInEditor));
    }

    partial void OnNewLocalModeChanged(int value)
    {
        OnPropertyChanged(nameof(IsLocalImportMode));
        OnPropertyChanged(nameof(ShowLocalImportFilePicker));
        OnPropertyChanged(nameof(ShowCreateLocalContentEditor));
        OnPropertyChanged(nameof(ShowInlineConfigEditor));
        OnPropertyChanged(nameof(ShowConfigFullscreenView));
        OnPropertyChanged(nameof(CanSaveConfigInEditor));
    }

    partial void OnIsCreatingModeChanged(bool value)
    {
        OnPropertyChanged(nameof(IsProfileFormMode));
        OnPropertyChanged(nameof(ShowLocalFileModeSelector));
        OnPropertyChanged(nameof(ShowLocalImportFilePicker));
        OnPropertyChanged(nameof(ShowCreateLocalContentEditor));
        OnPropertyChanged(nameof(ShowInlineConfigEditor));
        OnPropertyChanged(nameof(ShowConfigFullscreenView));
        OnPropertyChanged(nameof(CanSaveConfigInEditor));
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
        OnPropertyChanged(nameof(ShowInlineConfigEditor));
        OnPropertyChanged(nameof(ShowConfigFullscreenView));
        OnPropertyChanged(nameof(ProfileFormTitle));
        OnPropertyChanged(nameof(ShowExternalEditorAction));
        OnPropertyChanged(nameof(CanSaveConfigInEditor));
    }

    partial void OnSelectedProfileChanged(ProfileItemViewModel? value)
    {
        OnPropertyChanged(nameof(CanUpdateSelected));
    }

    partial void OnIsCreatingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSaveProfile));
        OnPropertyChanged(nameof(CanSaveConfigInEditor));
    }

    partial void OnIsContentEditorVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(ContentActionText));
        OnPropertyChanged(nameof(ShowConfigFullscreenView));
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
        InitializePageMetadata("Profiles", "Navigation.Profiles", "Profiles");
        UpdateLocalizedTexts();
    }

    public ProfilesViewModel(
        IProfileManager profileManager,
        IConfigManager configManager,
        ISingBoxManager singBoxManager,
        IPreferencesService preferencesService,
        Func<Task>? profilesChangedCallback = null,
        Action<string, int>? toastWriter = null) : this()
    {
        _profileManager = profileManager;
        _configManager = configManager;
        _singBoxManager = singBoxManager;
        _remoteConfigUpdateService = new RemoteConfigUpdateService(configManager, profileManager, preferencesService);
        _profilesChangedCallback = profilesChangedCallback;
        _toastWriter = toastWriter;
        _singBoxManager.StatusChanged += OnSingBoxStatusChanged;
        _ = LoadProfilesAsync();
        _ = RefreshRunningProfileIdAsync();
    }

    [RelayCommand]
    private async Task GoBack()
    {
        if (ShowConfigFullscreenView)
        {
            if (!await ConfirmDiscardOrSaveChangesAsync())
            {
                return;
            }

            IsContentEditorVisible = false;
            ClearLoadedConfigContent();
            return;
        }

        if (!await ConfirmDiscardOrSaveChangesAsync())
        {
            return;
        }

        ResetEditingState();
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
                    IsRemoteType = profile.Type == ProfileType.Remote,
                    CanDelete = _runningProfileId != profile.Id
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
        _isConfigContentLoaded = true;
        IsContentEditorVisible = false;
        _editingProfile = null;
        IsEditingMode = false;
        IsFormChanged = false;
        NewProfileStatus = string.Empty;
        IsCreatingMode = true;
    }

    [RelayCommand]
    private async Task CancelCreate()
    {
        if (!await ConfirmDiscardOrSaveChangesAsync())
        {
            return;
        }

        ResetEditingState();
    }

    private void ResetEditingState()
    {
        IsCreatingMode = false;
        IsEditingMode = false;
        IsFormChanged = false;
        _editingProfile = null;
        IsContentEditorVisible = false;
        ClearLoadedConfigContent();
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
            await NotifyProfilesChangedAsync();

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
            _isConfigContentLoaded = true;
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

        if (_runningProfileId == target.Id)
        {
            ImportStatus = GetString("Profiles.Status.CannotDeleteRunning", "Cannot delete the profile that is currently running");
            _toastWriter?.Invoke(ImportStatus, 2600);
            return;
        }

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
        await NotifyProfilesChangedAsync();
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

        await NotifyProfilesChangedAsync();
    }

    [RelayCommand]
    private async Task EditProfile(ProfileItemViewModel? profile = null)
    {
        if (_configManager == null || _profileManager == null) return;

        var target = profile ?? SelectedProfile;
        if (target == null) return;

        var profileModel = await _profileManager.GetAsync(target.Id);
        if (profileModel == null) return;

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
        ClearLoadedConfigContent();
        _initialName = NewProfileName;
        _initialUrl = NewProfileUrl;
        _initialAutoUpdate = NewAutoUpdate;
        _initialUpdateIntervalMinutes = NewUpdateIntervalMinutes;
        _initialConfigContent = string.Empty;
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
        _isConfigContentLoaded = true;
        if (string.IsNullOrWhiteSpace(NewProfileName) || NewProfileName.StartsWith("New Profile ", StringComparison.Ordinal))
        {
            NewProfileName = Path.GetFileNameWithoutExtension(NewLocalFilePath);
        }
    }

    [RelayCommand]
    private async Task ToggleContentEditor()
    {
        if (IsContentEditorVisible)
        {
            if (!await ConfirmDiscardOrSaveChangesAsync())
            {
                return;
            }

            IsContentEditorVisible = false;
            ClearLoadedConfigContent();
            return;
        }

        if (_editingProfile == null || _configManager == null)
        {
            return;
        }

        if (IsRemoteProfile)
        {
            var configPath = await _configManager.GetConfigPathAsync(_editingProfile.Id, ProfileType.Remote);
            if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
            {
                _toastWriter?.Invoke(GetString("Profiles.Toast.RemoteConfigNotFound", "错误：本地不存在此远程配置，请更新配置"), 3000);
                return;
            }
        }

        await LoadConfigContentForEditorAsync();
        IsContentEditorVisible = true;
    }

    [RelayCommand]
    private void FormatConfigJson()
    {
        try
        {
            using var document = JsonDocument.Parse(ConfigContent);
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(
                buffer,
                new JsonWriterOptions
                {
                    Indented = true,
                    Encoder = UnicodeJsonEncoder.Instance
                }))
            {
                document.RootElement.WriteTo(writer);
                writer.Flush();
            }

            ConfigContent = Encoding.UTF8.GetString(buffer.WrittenSpan);
            _toastWriter?.Invoke(GetString("Profiles.Toast.JsonFormatSuccess", "JSON formatted"), 1800);
        }
        catch (JsonException ex)
        {
            _toastWriter?.Invoke(FormatJsonError(ex), 3200);
        }
    }

    [RelayCommand]
    private async Task ValidateConfigJson()
    {
        if (_singBoxManager == null)
        {
            try
            {
                using var _ = JsonDocument.Parse(ConfigContent);
                _toastWriter?.Invoke(GetString("Profiles.Toast.JsonValid", "JSON syntax is valid"), 1800);
            }
            catch (JsonException ex)
            {
                _toastWriter?.Invoke(FormatJsonError(ex), 3600);
            }

            return;
        }

        var (success, message) = await _singBoxManager.CheckConfigAsync(ConfigContent);
        if (success)
        {
            _toastWriter?.Invoke(GetString("Profiles.Toast.JsonValid", "JSON syntax is valid"), 1800);
            return;
        }

        await ShowValidationErrorDialogAsync(message);
    }

    [RelayCommand]
    private async Task OpenInExternalEditor()
    {
        if (_configManager == null || _editingProfile == null) return;

        var configPath = await _configManager.GetConfigPathAsync(_editingProfile.Id, ProfileType.Local);
        if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
        {
            _toastWriter?.Invoke(GetString("Profiles.Toast.LocalConfigNotFound", "找不到配置文件"), 3000);
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = configPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _toastWriter?.Invoke($"{GetString("Profiles.Toast.OpenFailed", "无法打开文件")}: {ex.Message}", 3000);
        }
    }

    [RelayCommand]
    private async Task CopyAsLocal()
    {
        if (_configManager == null || _profileManager == null || _editingProfile == null) return;

        var configPath = await _configManager.GetConfigPathAsync(_editingProfile.Id, ProfileType.Remote);
        if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
        {
            _toastWriter?.Invoke(GetString("Profiles.Toast.RemoteConfigNotFound", "错误：本地不存在此远程配置，请更新配置"), 3000);
            return;
        }

        try
        {
            var content = await File.ReadAllTextAsync(configPath);
            string baseName = _editingProfile.Name + " Copy";
            string newName = baseName;
            int counter = 1;

            var existingProfiles = await _profileManager.ListAsync();
            while (existingProfiles.Any(p => string.Equals(p.Name, newName, StringComparison.OrdinalIgnoreCase)))
            {
                newName = $"{baseName} {counter++}";
            }

            var profile = await _profileManager.CreateAsync(new Core.Models.Profile
            {
                Name = newName,
                Type = ProfileType.Local,
                LastUpdated = DateTime.Now,
                UpdateInterval = 0,
                AutoUpdate = false
            }, content);

            await LoadProfilesAsync();
            await NotifyProfilesChangedAsync();
            _toastWriter?.Invoke($"{GetString("Profiles.Toast.CopySuccess", "已复制为")} {newName}", 3000);
        }
        catch (Exception ex)
        {
            _toastWriter?.Invoke($"{GetString("Profiles.Toast.CopyFailed", "复制失败")}: {ex.Message}", 3000);
        }
    }

    [RelayCommand]
    private async Task SaveProfile()
    {
        await SaveProfileCoreAsync(exitEditMode: true);
    }

    [RelayCommand]
    private async Task SaveProfileInEditor()
    {
        await SaveProfileCoreAsync(exitEditMode: false);
    }

    private async Task<bool> SaveProfileCoreAsync(bool exitEditMode)
    {
        if (_configManager == null || _profileManager == null || _editingProfile == null) return false;

        if (string.IsNullOrWhiteSpace(NewProfileName))
        {
            NewProfileStatus = "Profile name cannot be empty";
            return false;
        }

        if (IsRemoteProfile && string.IsNullOrWhiteSpace(NewProfileUrl))
        {
            NewProfileStatus = "Remote profile URL cannot be empty";
            return false;
        }

        IsCreating = true;
        NewProfileStatus = "Saving...";

        try
        {
            if (IsLocalProfile && HasLoadedConfigContent)
            {
                await _configManager.SaveConfigAsync(
                    _editingProfile.Id,
                    ConfigContent,
                    ProfileType.Local);
            }

            var profile = await _profileManager.GetAsync(_editingProfile.Id);
            if (profile != null)
            {
                profile.Name = NewProfileName;
                profile.LastUpdated = DateTime.Now;
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
            await NotifyProfilesChangedAsync();

            _initialName = NewProfileName;
            _initialUrl = NewProfileUrl;
            _initialAutoUpdate = NewAutoUpdate;
            _initialUpdateIntervalMinutes = NewUpdateIntervalMinutes;
            _initialConfigContent = ConfigContent;
            IsFormChanged = false;
            NewProfileStatus = string.Empty;
            _toastWriter?.Invoke(GetString("Profiles.Toast.SaveSuccess", "Profile saved"), 1800);

            if (profile != null)
            {
                EditLastUpdated = profile.LastUpdated?.ToString("yyyy-MM-dd HH:mm:ss") ?? _neverLabel;
            }

            if (exitEditMode)
            {
                IsEditingMode = false;
                IsContentEditorVisible = false;
                ClearLoadedConfigContent();
                _editingProfile = null;
            }
            else
            {
                _editingProfile = Profiles.FirstOrDefault(p => p.Id == _editingProfile.Id) ?? _editingProfile;
            }

            return true;
        }
        catch (Exception ex)
        {
            NewProfileStatus = $"Save failed: {ex.Message}";
            return false;
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
    private async Task UpdateEditingProfile()
    {
        if (_editingProfile != null)
        {
            await UpdateProfile(_editingProfile);

            if (_configManager != null)
            {
                ClearLoadedConfigContent();
                await LoadConfigContentForEditorAsync();
                var profile = _profileManager != null ? await _profileManager.GetAsync(_editingProfile.Id) : null;
                if (profile != null)
                {
                    EditLastUpdated = profile.LastUpdated?.ToString("yyyy-MM-dd HH:mm:ss") ?? _neverLabel;
                }
                RecalculateFormChanged();
            }
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

    private bool HasProfileMetadataChanges()
    {
        if (!IsEditingMode)
        {
            return false;
        }

        return
            !string.Equals(NewProfileName, _initialName, StringComparison.Ordinal) ||
            !string.Equals(NewProfileUrl, _initialUrl, StringComparison.Ordinal) ||
            NewAutoUpdate != _initialAutoUpdate ||
            !string.Equals(NewUpdateIntervalMinutes, _initialUpdateIntervalMinutes, StringComparison.Ordinal);
    }

    private bool HasConfigContentChanges()
    {
        return IsEditingMode &&
               IsLocalProfile &&
               HasLoadedConfigContent &&
               !string.Equals(ConfigContent, _initialConfigContent, StringComparison.Ordinal);
    }

    private bool HasUnsavedChanges()
    {
        return HasProfileMetadataChanges() || HasConfigContentChanges();
    }

    private async Task<bool> ConfirmDiscardOrSaveChangesAsync()
    {
        if (!IsEditingMode || !HasUnsavedChanges())
        {
            return true;
        }

        var shouldSave = await ShowUnsavedChangesDialogAsync();
        if (!shouldSave.HasValue)
        {
            return false;
        }

        if (!shouldSave.Value)
        {
            return true;
        }

        return await SaveProfileCoreAsync(exitEditMode: false);
    }

    private async Task LoadConfigContentForEditorAsync()
    {
        if (_editingProfile == null || _configManager == null || HasLoadedConfigContent)
        {
            return;
        }

        var profileType = _editingProfile.IsRemoteType ? ProfileType.Remote : ProfileType.Local;
        var content = await _configManager.LoadConfigAsync(_editingProfile.Id, profileType);
        _initialConfigContent = content ?? "{}";
        ConfigContent = _initialConfigContent;
        _isConfigContentLoaded = true;
        RecalculateFormChanged();
    }

    private void ClearLoadedConfigContent()
    {
        _isConfigContentLoaded = false;
        _initialConfigContent = string.Empty;
        ConfigContent = string.Empty;
    }

    private async Task<bool?> ShowUnsavedChangesDialogAsync()
    {
        var desktop = Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
        var owner = desktop?.MainWindow;
        if (owner == null)
        {
            return true;
        }

        var dialog = new Avalonia.Controls.Window
        {
            Width = 460,
            Height = 210,
            CanResize = false,
            WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.CenterOwner,
            Title = GetString("Profiles.UnsavedDialog.Title", "Unsaved Changes")
        };

        var message = new Avalonia.Controls.TextBlock
        {
            Text = GetString("Profiles.UnsavedDialog.Message", "You have unsaved changes. Save before going back?"),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(0, 0, 0, 16)
        };

        var saveButton = new Avalonia.Controls.Button
        {
            Content = GetString("Profiles.UnsavedDialog.SaveButton", "Save"),
            MinWidth = 90
        };
        saveButton.Click += (_, _) => dialog.Close(true);

        var discardButton = new Avalonia.Controls.Button
        {
            Content = GetString("Profiles.UnsavedDialog.DiscardButton", "Don't Save"),
            MinWidth = 90
        };
        discardButton.Click += (_, _) => dialog.Close(false);

        var cancelButton = new Avalonia.Controls.Button
        {
            Content = GetString("Profiles.Form.CancelButton", "Cancel"),
            MinWidth = 90
        };
        cancelButton.Click += (_, _) => dialog.Close(null);

        dialog.Content = new Avalonia.Controls.StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Children =
            {
                message,
                new Avalonia.Controls.StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Spacing = 8,
                    Children =
                    {
                        cancelButton,
                        discardButton,
                        saveButton
                    }
                }
            }
        };

        return await dialog.ShowDialog<bool?>(owner);
    }

    private async Task ShowValidationErrorDialogAsync(string logText)
    {
        var content = string.IsNullOrWhiteSpace(logText)
            ? GetString("Profiles.ValidateDialog.EmptyLog", "No error output from sing-box check.")
            : logText.Trim();

        var desktop = Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
        var owner = desktop?.MainWindow;
        if (owner == null)
        {
            _toastWriter?.Invoke($"{GetString("Profiles.Toast.JsonInvalid", "Invalid JSON")}: {content}", 4200);
            return;
        }

        var dialog = new Avalonia.Controls.Window
        {
            Width = 760,
            Height = 520,
            CanResize = true,
            WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.CenterOwner,
            Title = GetString("Profiles.ValidateDialog.Title", "sing-box Check Failed")
        };

        var message = new Avalonia.Controls.TextBlock
        {
            Text = GetString("Profiles.ValidateDialog.Message", "sing-box check failed. Review the log below:"),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(0, 0, 0, 12)
        };

        var logBox = new Avalonia.Controls.TextBox
        {
            Text = content,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Height = 390
        };
        Avalonia.Controls.ScrollViewer.SetHorizontalScrollBarVisibility(
            logBox,
            Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        Avalonia.Controls.ScrollViewer.SetVerticalScrollBarVisibility(
            logBox,
            Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);

        var copyButton = new Avalonia.Controls.Button
        {
            Content = GetString("Profiles.ValidateDialog.CopyButton", "Copy Log"),
            MinWidth = 100
        };
        copyButton.Click += async (_, _) =>
        {
            if (owner.Clipboard == null)
            {
                return;
            }

            await owner.Clipboard.SetTextAsync(content);
            _toastWriter?.Invoke(GetString("Dashboard.Status.CommandCopied", "Command copied to clipboard"), 1800);
        };

        var closeButton = new Avalonia.Controls.Button
        {
            Content = GetString("Profiles.ValidateDialog.CloseButton", "Close"),
            MinWidth = 90
        };
        closeButton.Click += (_, _) => dialog.Close();

        dialog.Content = new Avalonia.Controls.StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 8,
            Children =
            {
                message,
                logBox,
                new Avalonia.Controls.StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Spacing = 8,
                    Children =
                    {
                        copyButton,
                        closeButton
                    }
                }
            }
        };

        await dialog.ShowDialog(owner);
    }

    private void RecalculateFormChanged()
    {
        if (!IsEditingMode)
        {
            IsFormChanged = false;
            OnPropertyChanged(nameof(CanSaveProfile));
            OnPropertyChanged(nameof(CanSaveConfigInEditor));
            return;
        }

        var changed = HasUnsavedChanges();

        IsFormChanged = changed;
        OnPropertyChanged(nameof(CanSaveProfile));
        OnPropertyChanged(nameof(CanSaveConfigInEditor));
    }

    private void OnLanguageChanged(object? sender, AppLanguage e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(UpdateLocalizedTexts);
    }

    private void OnSingBoxStatusChanged(object? sender, ServiceStatus status)
    {
        _ = status == ServiceStatus.Running
            ? RefreshRunningProfileIdAsync()
            : ClearRunningProfileIdAsync();
    }

    private async Task RefreshRunningProfileIdAsync()
    {
        if (_singBoxManager?.IsRunning != true || _profileManager == null)
        {
            await ClearRunningProfileIdAsync();
            return;
        }

        _runningProfileId = await _profileManager.GetSelectedProfileIdAsync();
        await Dispatcher.UIThread.InvokeAsync(UpdateProfileDeleteAvailability);
    }

    private async Task ClearRunningProfileIdAsync()
    {
        _runningProfileId = null;
        await Dispatcher.UIThread.InvokeAsync(UpdateProfileDeleteAvailability);
    }

    private void UpdateProfileDeleteAvailability()
    {
        foreach (var profile in Profiles)
        {
            profile.CanDelete = _runningProfileId != profile.Id;
        }
    }

    public void Dispose()
    {
        if (_singBoxManager != null)
        {
            _singBoxManager.StatusChanged -= OnSingBoxStatusChanged;
        }

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

    private async Task NotifyProfilesChangedAsync()
    {
        if (_profilesChangedCallback != null)
        {
            await _profilesChangedCallback();
        }
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
        OnPropertyChanged(nameof(OpenExternalText));
        OnPropertyChanged(nameof(CopyAsLocalText));
        OnPropertyChanged(nameof(UpdateConfigText));
        OnPropertyChanged(nameof(FormatJsonText));
        OnPropertyChanged(nameof(ValidateJsonText));
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

    private string FormatJsonError(JsonException ex)
    {
        var prefix = GetString("Profiles.Toast.JsonInvalid", "Invalid JSON");
        if (ex.LineNumber.HasValue && ex.BytePositionInLine.HasValue)
        {
            return $"{prefix}: line {ex.LineNumber.Value + 1}, column {ex.BytePositionInLine.Value + 1} - {ex.Message}";
        }

        return $"{prefix}: {ex.Message}";
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

    [ObservableProperty]
    private bool _canDelete = true;

    partial void OnNameChanged(string value)
    {
        OnPropertyChanged(nameof(DisplayName));
    }

    partial void OnIdChanged(int value)
    {
        OnPropertyChanged(nameof(DisplayName));
    }
}
