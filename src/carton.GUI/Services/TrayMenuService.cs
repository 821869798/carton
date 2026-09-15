using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;
using carton.Core.Models;
using carton.ViewModels;
using carton.Views;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

namespace carton.GUI.Services;

public sealed class TrayMenuService : IDisposable
{
    private readonly ILocalizationService _localizationService;
    private Application? _application;
    private IClassicDesktopStyleApplicationLifetime? _desktopLifetime;
    private MainWindow? _mainWindow;
    private MainViewModel? _mainViewModel;
    private DashboardViewModel? _dashboardViewModel;
    private GroupsViewModel? _groupsViewModel;
    private TrayIcon? _trayIcon;
    private NativeMenuItem? _openWindowItem;
    private NativeMenuItem? _startStopItem;
    private NativeMenuItem? _profilesMenuItem;
    private NativeMenuItem? _groupsMenuItem;
    private NativeMenuItem? _profilesEmptyMenuItem;
    private readonly Dictionary<DashboardProfileItemViewModel, NativeMenuItem> _profileMenuItems = new();
    private int _profilesRefreshPending;
    private int _groupsRefreshPending;
    private int _groupsMenuHash;
    private bool _hasGroupsMenuHash;
    private bool _isInitialized;
    private Timer? _linuxTrayWatchdog;
    private int _trayReRegisterPending;

    public TrayMenuService()
    {
        _localizationService = LocalizationService.Instance;
    }

    public void Initialize(Application application,
        IClassicDesktopStyleApplicationLifetime desktopLifetime,
        MainWindow window,
        MainViewModel mainViewModel)
    {
        if (_isInitialized)
        {
            return;
        }

        _application = application ?? throw new ArgumentNullException(nameof(application));
        _desktopLifetime = desktopLifetime ?? throw new ArgumentNullException(nameof(desktopLifetime));
        _mainWindow = window ?? throw new ArgumentNullException(nameof(window));
        _mainViewModel = mainViewModel ?? throw new ArgumentNullException(nameof(mainViewModel));
        _dashboardViewModel = mainViewModel.DashboardViewModel;

        _localizationService.LanguageChanged += OnLanguageChanged;
        _mainViewModel.PropertyChanged += OnMainViewModelPropertyChanged;
        _dashboardViewModel.AvailableProfiles.CollectionChanged += OnProfilesCollectionChanged;
        SubscribeToProfileItems(_dashboardViewModel.AvailableProfiles);

        CreateTrayIcon();
        _isInitialized = true;

        // Linux only: keep the StatusNotifierItem registration alive (see ReRegisterTrayIcon).
        if (OperatingSystem.IsLinux())
        {
            _linuxTrayWatchdog = new Timer(
                static state => Dispatcher.UIThread.Post(() => ((TrayMenuService)state!).ReRegisterTrayIcon("watchdog")),
                this,
                LinuxTrayRegisterInterval,
                LinuxTrayRegisterInterval);
        }
    }

    /// <summary>
    /// Avalonia 11.3 only re-registers the StatusNotifierItem when the *watcher's name owner*
    /// changes, so a restarted tray host leaves the item unlisted while carton keeps running:
    /// icon gone, process alive, proxy fine. Confirmed in the wild on Caelestia/Quickshell
    /// (the watcher restarts, "org.kde.StatusNotifierItem-{pid}-0" still exists on the bus but
    /// is no longer in the watcher's RegisteredStatusNotifierItems) and tracked upstream as
    /// AvaloniaUI/Avalonia#13130; AvaloniaUI/Avalonia#21980 fixes duplicate icons and a crash
    /// on exit but explicitly not this. Upstream fix needs Avalonia 12.1+, so carton nudges
    /// Avalonia into re-registering itself on a slow timer: setting IsVisible false/true makes
    /// DBusTrayIconImpl run DestroyTrayIcon + CreateTrayIcon, which calls
    /// RegisterStatusNotifierItem again - no menu rebuild, no restart, and the item reappears
    /// with a fresh name within milliseconds.
    /// </summary>
    private static readonly TimeSpan LinuxTrayRegisterInterval = TimeSpan.FromMinutes(5);

    public void Dispose()
    {
        if (!_isInitialized)
        {
            return;
        }

        _isInitialized = false;
        _linuxTrayWatchdog?.Dispose();
        _linuxTrayWatchdog = null;
        _localizationService.LanguageChanged -= OnLanguageChanged;

        if (_mainViewModel != null)
        {
            _mainViewModel.PropertyChanged -= OnMainViewModelPropertyChanged;
        }

        if (_dashboardViewModel != null)
        {
            _dashboardViewModel.AvailableProfiles.CollectionChanged -= OnProfilesCollectionChanged;
            foreach (var profile in _dashboardViewModel.AvailableProfiles)
            {
                profile.PropertyChanged -= OnProfilePropertyChanged;
            }
        }

        if (_groupsViewModel != null)
        {
            _groupsViewModel.PropertyChanged -= OnGroupsViewModelPropertyChanged;
        }

        DestroyTrayIcon();

        _profileMenuItems.Clear();
        _profilesEmptyMenuItem = null;
    }

    /// <summary>Releases the current tray icon (used by Dispose).</summary>
    private void DestroyTrayIcon()
    {
        if (_trayIcon != null)
        {
            _trayIcon.Clicked -= OnTrayIconClicked;
            _trayIcon.Menu = null;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        if (_application != null)
        {
            TrayIcon.SetIcons(_application, new TrayIcons());
        }
    }

    /// <summary>
    /// Re-registers the tray item with the current host. Called by the Linux watchdog (on the UI
    /// thread, via Dispatcher.Post). Must be called on the UI thread: it touches TrayIcon state.
    /// Never throws; a no-op unless the icon is actually visible, and never runs twice at once.
    /// </summary>
    internal void ReRegisterTrayIcon(string reason)
    {
        var icon = _trayIcon;
        if (!_isInitialized || icon is not { IsVisible: true })
        {
            return;
        }

        if (Interlocked.Exchange(ref _trayReRegisterPending, 1) == 1)
        {
            // Someone else owns the flag; it will clear it when done.
            return;
        }

        try
        {
            icon.IsVisible = false;
            icon.IsVisible = true;
            _mainViewModel?.Log($"[INFO] Re-registered tray icon ({reason})");
        }
        catch (Exception ex)
        {
            _mainViewModel?.Log($"[WARN] Tray re-register failed: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _trayReRegisterPending, 0);
        }
    }

    private void CreateTrayIcon()
    {
        var menu = BuildInitialMenu();
        var assemblyName = _application?.GetType().Assembly.GetName().Name ?? "carton";
        var iconUri = new Uri($"avares://{assemblyName}/Assets/carton_icon.png");
        var icon = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(iconUri)),
            ToolTipText = _localizationService["App.Name"],
            Menu = menu,
            // Explicit rather than inherited from Visual.IsVisible (default true): the Linux
            // watchdog only re-registers a visible icon, so this must never be false by accident.
            IsVisible = true
        };

        icon.Clicked += OnTrayIconClicked;
        TrayIcon.SetIcons(_application!, new TrayIcons { icon });
        _trayIcon = icon;
        UpdateMenuState();
    }

    private NativeMenu BuildInitialMenu()
    {
        _openWindowItem = new NativeMenuItem
        {
            Header = _localizationService["Tray.ShowWindow"]
        };
        _openWindowItem.Click += (_, _) => ShowMainWindow();

        _startStopItem = new NativeMenuItem();
        _startStopItem.Click += async (_, _) => await ToggleServiceAsync();

        _profilesMenuItem = new NativeMenuItem
        {
            Header = _localizationService["Tray.Profiles"],
            Menu = new NativeMenu()
        };

        _groupsMenuItem = new NativeMenuItem
        {
            Header = _localizationService["Tray.Groups"],
            Menu = new NativeMenu()
        };

        var quitItem = new NativeMenuItem
        {
            Header = _localizationService["Tray.Quit"]
        };
        quitItem.Click += (_, _) => ExitApplication();

        var menu = new NativeMenu();
        menu.Items.Add(_openWindowItem);
        menu.Items.Add(_startStopItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(_profilesMenuItem);
        menu.Items.Add(_groupsMenuItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(quitItem);
        return menu;
    }

    private void ExitApplication()
    {
        RunOnUiThread(() =>
        {
            _mainWindow?.AllowClose();
            _mainWindow?.Close();
            //_desktopLifetime?.Shutdown();
            SingleInstanceService.Dispose();
            Environment.Exit(0);
        });
    }

    private void UpdateMenuState()
    {
        UpdateStartStopItem();
        RefreshProfilesMenu();
        RefreshGroupsMenu();
    }

    private bool EnsureGroupsViewModelSubscribed()
    {
        if (_groupsViewModel != null)
        {
            return true;
        }

        if (_mainViewModel?.IsConnected != true)
        {
            return false;
        }

        _groupsViewModel = _mainViewModel.EnsureGroupsViewModel();
        _groupsViewModel.PropertyChanged += OnGroupsViewModelPropertyChanged;

        return true;
    }

    private void ReleaseGroupsMenuState()
    {
        if (_groupsViewModel != null)
        {
            _groupsViewModel.PropertyChanged -= OnGroupsViewModelPropertyChanged;
            _groupsViewModel = null;
        }

        if (_groupsMenuItem != null)
        {
            var unavailableHash = ComputeUnavailableGroupsMenuHash();
            if (_hasGroupsMenuHash && _groupsMenuHash == unavailableHash)
            {
                return;
            }

            var menu = new NativeMenu();
            menu.Items.Add(new NativeMenuItem
            {
                Header = _localizationService["Tray.Groups.Unavailable"],
                IsEnabled = false
            });
            _groupsMenuItem.Menu = menu;
            _groupsMenuItem.IsEnabled = false;
            _groupsMenuHash = unavailableHash;
            _hasGroupsMenuHash = true;
        }
    }

    private void UpdateStartStopItem()
    {
        if (_startStopItem == null || _mainViewModel == null)
        {
            return;
        }

        var isRunning = _mainViewModel.IsConnected;
        _startStopItem.Header = _localizationService[isRunning ? "Tray.Stop" : "Tray.Start"];
        _startStopItem.IsEnabled = true;
    }

    private void RefreshProfilesMenu()
    {
        if (_profilesMenuItem == null || _dashboardViewModel == null)
        {
            return;
        }

        if (Interlocked.Exchange(ref _profilesRefreshPending, 1) == 1)
        {
            return;
        }

        void Update()
        {
            Interlocked.Exchange(ref _profilesRefreshPending, 0);
            UpdateProfilesMenuItems();
        }

        RunOnUiThread(Update);
    }

    private void RefreshGroupsMenu()
    {
        if (_groupsMenuItem == null || _mainViewModel == null)
        {
            return;
        }

        if (Interlocked.Exchange(ref _groupsRefreshPending, 1) == 1)
        {
            return;
        }

        if (!EnsureGroupsViewModelSubscribed())
        {
            Interlocked.Exchange(ref _groupsRefreshPending, 0);
            ReleaseGroupsMenuState();
            return;
        }

        var groupsViewModel = _groupsViewModel;
        if (groupsViewModel == null)
        {
            Interlocked.Exchange(ref _groupsRefreshPending, 0);
            return;
        }

        void Update()
        {
            Interlocked.Exchange(ref _groupsRefreshPending, 0);
            var menu = new NativeMenu();
            var isConnected = _mainViewModel?.IsConnected == true;
            var trayGroups = groupsViewModel.TrayGroups;
            var hasGroups = trayGroups.Count > 0 && isConnected;
            var menuHash = hasGroups
                ? ComputeGroupsMenuHash(trayGroups, true)
                : ComputeGroupsStateHash(isConnected, isEmptyState: isConnected);

            if (_hasGroupsMenuHash && _groupsMenuHash == menuHash)
            {
                return;
            }

            if (!hasGroups)
            {
                menu.Items.Add(new NativeMenuItem
                {
                    Header = _localizationService[isConnected
                        ? "Tray.Groups.Empty"
                        : "Tray.Groups.Unavailable"],
                    IsEnabled = false
                });
                _groupsMenuItem.Menu = menu;
                _groupsMenuItem.IsEnabled = isConnected;
                _groupsMenuHash = menuHash;
                _hasGroupsMenuHash = true;
                return;
            }

            foreach (var group in trayGroups)
            {
                var groupMenu = new NativeMenu();
                foreach (var outbound in group.Items)
                {
                    var outboundHeader = string.IsNullOrWhiteSpace(outbound.Tag)
                        ? _localizationService["Common.Unknown"]
                        : outbound.Tag;
                    var delaySuffix = outbound.Delay > 0 ? $" ({outbound.Delay}ms)" : string.Empty;
                    var outboundItem = new NativeMenuItem
                    {
                        Header = outboundHeader + delaySuffix,
                        IsChecked = outbound.IsSelected,
                        ToggleType = NativeMenuItemToggleType.CheckBox,
                        IsEnabled = group.IsSelectable
                    };
                    outboundItem.Click += (_, _) => SelectOutbound(group.Name, outbound.Tag);
                    groupMenu.Items.Add(outboundItem);
                }

                if (groupMenu.Items.Count == 0)
                {
                    groupMenu.Items.Add(new NativeMenuItem
                    {
                        Header = _localizationService["Tray.Groups.Empty"],
                        IsEnabled = false
                    });
                }

                var groupHeader = string.IsNullOrWhiteSpace(group.Name)
                    ? _localizationService["Common.Unknown"]
                    : group.Name;

                menu.Items.Add(new NativeMenuItem
                {
                    Header = groupHeader,
                    Menu = groupMenu
                });
            }

            _groupsMenuItem.Menu = menu;
            _groupsMenuItem.IsEnabled = true;
            _groupsMenuHash = menuHash;
            _hasGroupsMenuHash = true;
        }

        RunOnUiThread(Update);
    }

    private void SelectProfile(DashboardProfileItemViewModel profile)
    {
        if (_dashboardViewModel?.SelectStartupProfileCommand == null ||
            !CanSwitchProfiles())
        {
            return;
        }

        RunOnUiThread(() => _ = _dashboardViewModel.SelectStartupProfileCommand.ExecuteAsync(profile));
    }

    private void SelectOutbound(string groupTag, string? outboundTag)
    {
        if (string.IsNullOrWhiteSpace(outboundTag) || _groupsViewModel == null)
        {
            return;
        }

        RunOnUiThread(() => _ = _groupsViewModel.SelectOutboundFromTrayAsync(groupTag, outboundTag));
    }

    private async Task ToggleServiceAsync()
    {
        if (_dashboardViewModel == null)
        {
            return;
        }

        var startCommand = _dashboardViewModel.StartWithSelectedProfileCommand;
        var stopCommand = _dashboardViewModel.StopConnectionCommand;

        if (_mainViewModel?.IsConnected == true)
        {
            if (stopCommand != null)
            {
                RunOnUiThread(() => _ = stopCommand.ExecuteAsync(null));
            }
        }
        else if (startCommand != null)
        {
            RunOnUiThread(() => _ = startCommand.ExecuteAsync(null));
        }

        await Task.CompletedTask;
    }

    private void OnMainViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsConnected))
        {
            UpdateStartStopItem();
            if (_mainViewModel?.IsConnected == true)
            {
                if (EnsureGroupsViewModelSubscribed())
                {
                    _groupsViewModel?.EnsureLoadedInBackground();
                }
            }
            else
            {
                ReleaseGroupsMenuState();
            }
            RefreshGroupsMenu();
        }
        else if (e.PropertyName == nameof(MainViewModel.ServiceStatus))
        {
            RefreshProfilesMenu();
        }
        else if (e.PropertyName == nameof(MainViewModel.CurrentProfileName))
        {
            RefreshProfilesMenu();
        }
    }

    private void OnProfilesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
        {
            foreach (DashboardProfileItemViewModel profile in e.OldItems)
            {
                profile.PropertyChanged -= OnProfilePropertyChanged;
            }
        }

        if (e.NewItems != null)
        {
            foreach (DashboardProfileItemViewModel profile in e.NewItems)
            {
                profile.PropertyChanged += OnProfilePropertyChanged;
            }
        }

        RefreshProfilesMenu();
    }

    private void SubscribeToProfileItems(IEnumerable<DashboardProfileItemViewModel> profiles)
    {
        foreach (var profile in profiles)
        {
            profile.PropertyChanged += OnProfilePropertyChanged;
        }
    }

    private void OnProfilePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DashboardProfileItemViewModel.IsSelected) ||
            e.PropertyName == nameof(DashboardProfileItemViewModel.Name))
        {
            RefreshProfilesMenu();
        }
    }

    private void OnGroupsViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GroupsViewModel.TrayGroups))
        {
            RefreshGroupsMenu();
        }
    }

    private void OnLanguageChanged(object? sender, AppLanguage e)
    {
        _hasGroupsMenuHash = false;

        RunOnUiThread(() =>
        {
            if (_trayIcon != null)
            {
                _trayIcon.ToolTipText = _localizationService["App.Name"];
            }

            if (_openWindowItem != null)
            {
                _openWindowItem.Header = _localizationService["Tray.ShowWindow"];
            }

            if (_profilesMenuItem != null)
            {
                _profilesMenuItem.Header = _localizationService["Tray.Profiles"];
            }

            if (_groupsMenuItem != null)
            {
                _groupsMenuItem.Header = _localizationService["Tray.Groups"];
            }

            UpdateStartStopItem();
            RefreshProfilesMenu();
            RefreshGroupsMenu();
        });
    }

    private void OnTrayIconClicked(object? sender, EventArgs e)
    {
        ShowMainWindow();
    }

    private void ShowMainWindow()
    {
        if (_mainWindow == null)
        {
            return;
        }

        RunOnUiThread(() =>
        {
            if (!_mainWindow.IsVisible)
            {
                _mainWindow.Show();
            }

            if (_mainWindow.WindowState == WindowState.Minimized)
            {
                _mainWindow.WindowState = WindowState.Normal;
            }

            _mainWindow.Activate();
            _mainWindow.Topmost = true;
            _mainWindow.Topmost = false;
        });
    }

    private void RunOnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }

    private void UpdateProfilesMenuItems()
    {
        if (_profilesMenuItem == null || _dashboardViewModel == null)
        {
            return;
        }

        var menu = _profilesMenuItem.Menu ?? new NativeMenu();
        _profilesMenuItem.Menu = menu;
        var profiles = _dashboardViewModel.AvailableProfiles;
        var canSwitchProfiles = CanSwitchProfiles();

        if (profiles.Count == 0)
        {
            _profileMenuItems.Clear();

            if (_profilesEmptyMenuItem == null)
            {
                _profilesEmptyMenuItem = new NativeMenuItem
                {
                    IsEnabled = false
                };
            }

            _profilesEmptyMenuItem.Header = _localizationService["Tray.Profiles.Empty"];

            if (menu.Items.Count != 1 || !ReferenceEquals(menu.Items[0], _profilesEmptyMenuItem))
            {
                menu.Items.Clear();
                menu.Items.Add(_profilesEmptyMenuItem);
            }

            return;
        }

        _profilesEmptyMenuItem = null;

        for (var i = menu.Items.Count - 1; i >= 0; i--)
        {
            if (menu.Items[i] is NativeMenuItem item &&
                !_profileMenuItems.ContainsValue(item))
            {
                menu.Items.RemoveAt(i);
            }
        }

        var activeProfiles = new HashSet<DashboardProfileItemViewModel>(profiles);
        foreach (var entry in new List<KeyValuePair<DashboardProfileItemViewModel, NativeMenuItem>>(_profileMenuItems))
        {
            if (activeProfiles.Contains(entry.Key))
            {
                continue;
            }

            menu.Items.Remove(entry.Value);
            _profileMenuItems.Remove(entry.Key);
        }

        for (var i = 0; i < profiles.Count; i++)
        {
            var profile = profiles[i];
            if (!_profileMenuItems.TryGetValue(profile, out var item))
            {
                item = CreateProfileMenuItem(profile);
                _profileMenuItems[profile] = item;
            }

            UpdateProfileMenuItem(item, profile, canSwitchProfiles);
            MoveProfileMenuItem(menu, item, i);
        }
    }

    private NativeMenuItem CreateProfileMenuItem(DashboardProfileItemViewModel profile)
    {
        var item = new NativeMenuItem
        {
            ToggleType = NativeMenuItemToggleType.Radio
        };
        item.Click += (_, _) => SelectProfile(profile);
        return item;
    }

    private static void UpdateProfileMenuItem(
        NativeMenuItem item,
        DashboardProfileItemViewModel profile,
        bool canSwitchProfiles)
    {
        item.Header = profile.Name;
        item.IsChecked = profile.IsSelected;
        item.IsEnabled = canSwitchProfiles;
    }

    private static void MoveProfileMenuItem(NativeMenu menu, NativeMenuItem item, int targetIndex)
    {
        var currentIndex = menu.Items.IndexOf(item);
        if (currentIndex == targetIndex)
        {
            return;
        }

        if (currentIndex >= 0)
        {
            menu.Items.RemoveAt(currentIndex);
        }

        menu.Items.Insert(targetIndex, item);
    }

    private bool CanSwitchProfiles()
    {
        var serviceStatus = _mainViewModel?.ServiceStatus;
        return serviceStatus is null or ServiceStatus.Stopped or ServiceStatus.Error;
    }

    private int ComputeGroupsMenuHash(IReadOnlyList<GroupMenuSnapshot> groups, bool isConnected)
    {
        var hash = new HashCode();
        hash.Add(isConnected);
        hash.Add(groups.Count);
        hash.Add(_localizationService["Tray.Groups.Empty"]);
        hash.Add(_localizationService["Tray.Groups.Unavailable"]);
        hash.Add(_localizationService["Common.Unknown"]);

        foreach (var group in groups)
        {
            hash.Add(group.Name, StringComparer.Ordinal);
            hash.Add(group.Type, StringComparer.OrdinalIgnoreCase);
            hash.Add(group.Items.Count);

            foreach (var outbound in group.Items)
            {
                hash.Add(outbound.Tag, StringComparer.Ordinal);
                hash.Add(outbound.Delay);
                hash.Add(outbound.IsSelected);
            }
        }

        return hash.ToHashCode();
    }

    private int ComputeUnavailableGroupsMenuHash()
    {
        return ComputeGroupsStateHash(isConnected: false, isEmptyState: false);
    }

    private int ComputeGroupsStateHash(bool isConnected, bool isEmptyState)
    {
        var hash = new HashCode();
        hash.Add(isConnected);
        hash.Add(isEmptyState);
        hash.Add(_localizationService[isConnected ? "Tray.Groups.Empty" : "Tray.Groups.Unavailable"]);
        return hash.ToHashCode();
    }
}
