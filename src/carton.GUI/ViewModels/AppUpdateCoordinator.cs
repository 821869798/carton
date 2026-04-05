using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using carton.Core.Utilities;
using carton.GUI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace carton.ViewModels;

public partial class AppUpdateCoordinator : ObservableObject
{
    private readonly IAppUpdateService? _appUpdateService;
    private readonly ILocalizationService? _localizationService;
    private AppUpdateResult? _pendingAppUpdate;
    private bool _requiresManualAppUpdate;
    private bool _suppressChannelNormalization;
    private bool _isCompletionPromptVisible;

    [ObservableProperty]
    private string _selectedUpdateChannel = "release";

    [ObservableProperty]
    private bool _isCheckingAppUpdate;

    [ObservableProperty]
    private bool _isDownloadingAppUpdate;

    [ObservableProperty]
    private bool _isAppUpdateAvailable;

    [ObservableProperty]
    private bool _isAppUpdateReadyToInstall;

    [ObservableProperty]
    private bool _isPortableApp;

    [ObservableProperty]
    private double _appUpdateProgress;

    [ObservableProperty]
    private long _appUpdateBytesReceived;

    [ObservableProperty]
    private long _appUpdateTotalBytes;

    [ObservableProperty]
    private string _appUpdateStatus = string.Empty;

    [ObservableProperty]
    private string _latestAvailableVersion = string.Empty;

    [ObservableProperty]
    private string _currentAppVersion = string.Empty;

    [ObservableProperty]
    private bool _showStartupUpdateDialog;

    public string AppUpdateProgressDetail => AppUpdateTotalBytes > 0
        ? $"{FormatHelper.FormatBytes(AppUpdateBytesReceived)} / {FormatHelper.FormatBytes(AppUpdateTotalBytes)}"
        : string.Empty;

    public AppUpdateCoordinator()
    {
        InitializeState();
    }

    public AppUpdateCoordinator(
        IAppUpdateService appUpdateService,
        ILocalizationService localizationService)
    {
        _appUpdateService = appUpdateService;
        _localizationService = localizationService;
        InitializeState();
    }

    partial void OnAppUpdateBytesReceivedChanged(long value) => OnPropertyChanged(nameof(AppUpdateProgressDetail));
    partial void OnAppUpdateTotalBytesChanged(long value) => OnPropertyChanged(nameof(AppUpdateProgressDetail));

    partial void OnSelectedUpdateChannelChanged(string value)
    {
        var normalized = NormalizeUpdateChannel(value);
        if (!string.Equals(value, normalized, StringComparison.Ordinal))
        {
            _suppressChannelNormalization = true;
            SelectedUpdateChannel = normalized;
            _suppressChannelNormalization = false;
            return;
        }

        if (_suppressChannelNormalization)
        {
            return;
        }

        ResetAvailableStateForChannelChange();
    }

    public void Configure(string channel)
    {
        _suppressChannelNormalization = true;
        SelectedUpdateChannel = NormalizeUpdateChannel(channel);
        _suppressChannelNormalization = false;
    }

    public Task RunStartupCheckAsync(bool enabled)
    {
        if (!enabled)
        {
            return Task.CompletedTask;
        }

        return CheckForUpdatesCoreAsync(showStartupDialogWhenAvailable: true, showManualPrompt: true);
    }

    [RelayCommand]
    private Task CheckAppUpdate()
        => CheckForUpdatesCoreAsync(showStartupDialogWhenAvailable: false, showManualPrompt: true);

    [RelayCommand]
    private Task DownloadAppUpdate()
        => DownloadAppUpdateCoreAsync(silentDownload: false);

    [RelayCommand]
    private Task SilentDownloadAppUpdate()
    {
        if (IsDownloadingAppUpdate)
        {
            ShowStartupUpdateDialog = false;
            return Task.CompletedTask;
        }

        return DownloadAppUpdateCoreAsync(silentDownload: true);
    }

    [RelayCommand]
    private Task ApplyAppUpdate()
        => ApplyAppUpdateCoreAsync();

    [RelayCommand]
    private void CloseStartupUpdateDialog()
    {
        ShowStartupUpdateDialog = false;
    }

    private async Task CheckForUpdatesCoreAsync(bool showStartupDialogWhenAvailable, bool showManualPrompt)
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
        ShowStartupUpdateDialog = false;
        AppUpdateStatus = GetString("Settings.Update.Status.Checking", "Checking for updates...");
        try
        {
            if (_requiresManualAppUpdate)
            {
                var latestRelease = await _appUpdateService.GetLatestReleaseInfoAsync(SelectedUpdateChannel);
                if (latestRelease == null)
                {
                    ClearAvailableUpdateState();
                    AppUpdateStatus =
                        $"{GetString("Settings.Update.Status.Error", "Update failed")}: no release found for channel '{SelectedUpdateChannel}'";
                    return;
                }

                LatestAvailableVersion = latestRelease.Version;
                if (IsRemoteVersionDifferent(latestRelease.Version, _appUpdateService.CurrentVersion))
                {
                    _pendingAppUpdate = null;
                    IsAppUpdateAvailable = true;
                    IsAppUpdateReadyToInstall = false;
                    ResetDownloadProgress();
                    AppUpdateStatus = GetString("Settings.Update.Status.ManualRequired", "New version available. Download it from the releases page.");
                    if (showManualPrompt)
                    {
                        await ShowManualUpdatePromptAsync();
                    }
                }
                else
                {
                    ClearAvailableUpdateState();
                    AppUpdateStatus = GetString("Settings.Update.Status.Latest", "Already up to date");
                }

                return;
            }

            var result = await _appUpdateService.CheckForUpdatesAsync(SelectedUpdateChannel);
            if (result == null)
            {
                _pendingAppUpdate = null;
                IsAppUpdateAvailable = false;
                ResetDownloadProgress();
                IsAppUpdateReadyToInstall = _appUpdateService.IsUpdatePendingRestart;
                if (IsAppUpdateReadyToInstall)
                {
                    LatestAvailableVersion = _appUpdateService.PendingRestartVersion ?? LatestAvailableVersion;
                }
                else if (!IsDownloadingAppUpdate)
                {
                    LatestAvailableVersion = string.Empty;
                }

                AppUpdateStatus = IsAppUpdateReadyToInstall
                    ? GetString("Settings.Update.Status.Ready", "Update downloaded. Restart to apply.")
                    : GetString("Settings.Update.Status.Latest", "Already up to date");
                return;
            }

            _pendingAppUpdate = result;
            LatestAvailableVersion = result.Version;
            IsAppUpdateAvailable = true;
            IsAppUpdateReadyToInstall = false;
            ResetDownloadProgress();
            AppUpdateStatus = GetString("Settings.Update.Status.Available", "New version available");
            if (showStartupDialogWhenAvailable)
            {
                ShowStartupUpdateDialog = true;
            }
        }
        catch (Exception ex)
        {
            ClearAvailableUpdateState();
            IsAppUpdateReadyToInstall = false;
            ShowStartupUpdateDialog = false;
            AppUpdateStatus = $"{GetString("Settings.Update.Status.Error", "Update failed")}: {ex.Message}";
        }
        finally
        {
            IsCheckingAppUpdate = false;
        }
    }

    private async Task DownloadAppUpdateCoreAsync(bool silentDownload)
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
            await CheckForUpdatesCoreAsync(showStartupDialogWhenAvailable: false, showManualPrompt: false);
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
        if (silentDownload)
        {
            ShowStartupUpdateDialog = false;
        }

        var initialTotalBytes = ResolveExpectedDownloadSize(_pendingAppUpdate);
        AppUpdateProgress = 0;
        AppUpdateBytesReceived = 0;
        AppUpdateTotalBytes = initialTotalBytes;
        AppUpdateStatus = BuildDownloadStatus();

        var progress = new Progress<AppUpdateDownloadProgress>(UpdateDownloadProgress);
        try
        {
            await _appUpdateService.DownloadUpdateAsync(_pendingAppUpdate, SelectedUpdateChannel, progress);
            _pendingAppUpdate = null;
            IsAppUpdateAvailable = false;
            IsAppUpdateReadyToInstall = true;
            ShowStartupUpdateDialog = false;
            AppUpdateProgress = 100;
            AppUpdateBytesReceived = AppUpdateTotalBytes > 0 ? AppUpdateTotalBytes : AppUpdateBytesReceived;
            AppUpdateStatus = GetString("Settings.Update.Status.Ready", "Update downloaded. Restart to apply.");
            await ShowApplyDownloadedUpdatePromptAsync();
        }
        catch (Exception ex)
        {
            IsAppUpdateReadyToInstall = false;
            AppUpdateStatus = $"{GetString("Settings.Update.Status.Error", "Update failed")}: {ex.Message}";
            if (silentDownload && LatestAvailableVersion.Length > 0)
            {
                ShowStartupUpdateDialog = true;
            }
        }
        finally
        {
            IsDownloadingAppUpdate = false;
        }
    }

    private async Task ApplyAppUpdateCoreAsync()
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

    private async Task ShowApplyDownloadedUpdatePromptAsync()
    {
        if (_isCompletionPromptVisible)
        {
            return;
        }

        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop ||
            desktop.MainWindow == null)
        {
            return;
        }

        _isCompletionPromptVisible = true;
        try
        {
            var owner = desktop.MainWindow;
            var versionLabel = string.IsNullOrWhiteSpace(LatestAvailableVersion)
                ? GetString("Common.Unknown", "unknown")
                : LatestAvailableVersion;

            var dialog = new Window
            {
                Width = 460,
                Height = 200,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Title = GetString("Settings.Update.ApplyPrompt.Title", "Update ready to install")
            };

            var message = new TextBlock
            {
                Text = string.Format(
                    GetString(
                        "Settings.Update.ApplyPrompt.Message",
                        "Version {0} has been downloaded. Restart now to apply it?"),
                    versionLabel),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 16)
            };

            var restartButton = new Button
            {
                Content = GetString("Settings.Update.ApplyPrompt.RestartButton", "Restart Now"),
                MinWidth = 110
            };
            restartButton.Click += (_, _) => dialog.Close(true);

            var laterButton = new Button
            {
                Content = GetString("Settings.Update.ApplyPrompt.LaterButton", "Later"),
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
                    restartButton
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

            var shouldRestart = await dialog.ShowDialog<bool>(owner);
            if (shouldRestart)
            {
                await ApplyAppUpdateCoreAsync();
            }
        }
        finally
        {
            _isCompletionPromptVisible = false;
        }
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

        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop ||
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

    private void InitializeState()
    {
        CurrentAppVersion = _appUpdateService?.CurrentVersion ?? GetString("Common.Unknown", "unknown");
        _requiresManualAppUpdate = _appUpdateService != null && !_appUpdateService.SupportsInAppUpdates;
        IsPortableApp = _requiresManualAppUpdate;
        LatestAvailableVersion = _appUpdateService?.PendingRestartVersion ?? string.Empty;
        IsAppUpdateAvailable = false;
        IsAppUpdateReadyToInstall = _appUpdateService?.IsUpdatePendingRestart == true;
        ResetDownloadProgress();
        AppUpdateStatus = IsAppUpdateReadyToInstall
            ? GetString("Settings.Update.Status.Ready", "Update downloaded. Restart to apply.")
            : string.Empty;
    }

    private void ClearAvailableUpdateState()
    {
        _pendingAppUpdate = null;
        IsAppUpdateAvailable = false;
        if (!IsAppUpdateReadyToInstall)
        {
            LatestAvailableVersion = string.Empty;
        }

        ResetDownloadProgress();
    }

    private void ResetAvailableStateForChannelChange()
    {
        ShowStartupUpdateDialog = false;
        if (IsDownloadingAppUpdate || IsAppUpdateReadyToInstall)
        {
            return;
        }

        ClearAvailableUpdateState();
        AppUpdateStatus = string.Empty;
    }

    private void ResetDownloadProgress()
    {
        AppUpdateProgress = 0;
        AppUpdateBytesReceived = 0;
        AppUpdateTotalBytes = 0;
    }

    private void UpdateDownloadProgress(AppUpdateDownloadProgress progress)
    {
        AppUpdateProgress = progress.Percent;
        AppUpdateBytesReceived = progress.BytesReceived;
        AppUpdateTotalBytes = progress.TotalBytes;
        AppUpdateStatus = BuildDownloadStatus();
    }

    private string BuildDownloadStatus()
    {
        var baseStatus = GetString("Settings.Update.Status.Downloading", "Downloading update...");
        return AppUpdateTotalBytes > 0
            ? $"{baseStatus} {AppUpdateProgressDetail}"
            : baseStatus;
    }

    private long ResolveExpectedDownloadSize(AppUpdateResult update)
    {
        var deltaPackages = update.UpdateInfo.DeltasToTarget;
        if (deltaPackages is { Length: > 0 })
        {
            long deltaBytes = 0;
            foreach (var asset in deltaPackages)
            {
                deltaBytes += Math.Max(0, asset?.Size ?? 0);
            }

            if (deltaBytes > 0)
            {
                return deltaBytes;
            }
        }

        return Math.Max(0, update.UpdateInfo.TargetFullRelease?.Size ?? 0);
    }

    private string GetString(string key, string fallback)
    {
        if (_localizationService == null)
        {
            return fallback;
        }

        var value = _localizationService.GetString(key);
        return string.Equals(value, key, StringComparison.Ordinal) ? fallback : value;
    }

    private static string NormalizeUpdateChannel(string? channel)
    {
        if (string.Equals(channel, "beta", StringComparison.OrdinalIgnoreCase))
        {
            return "beta";
        }

        return "release";
    }

    private static bool IsRemoteVersionDifferent(string remoteVersion, string currentVersion)
    {
        return !string.Equals(remoteVersion, currentVersion, StringComparison.OrdinalIgnoreCase);
    }
}
