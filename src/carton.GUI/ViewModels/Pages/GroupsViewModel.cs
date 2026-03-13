using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using carton.Core.Models;
using carton.Core.Services;
using carton.GUI.Models;
using carton.GUI.Services;

namespace carton.ViewModels;

public partial class GroupsViewModel : PageViewModelBase
{
    private const string WaitingForSingBoxResourceKey = "Groups.Status.WaitingForSingBoxStart";
    private readonly ISingBoxManager? _singBoxManager;
    private readonly SemaphoreSlim _loadSemaphore = new(1, 1);
    private readonly ClashConfigCacheService _clashConfigCache;
    private readonly Dictionary<string, List<OutboundItemViewModel>> _outboundItemsByTag = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _urlTestRefreshTimer;
    private bool _isRefreshingUrlTestGroups;
    private bool _isPageActive;
    private bool _isWindowVisible = true;

    public override NavigationPage PageType => NavigationPage.Groups;

    [ObservableProperty]
    private ObservableCollection<GroupItemViewModel> _groups = new();

    [ObservableProperty]
    private GroupItemViewModel? _selectedGroup;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isTestingGroup;

    [ObservableProperty]
    private bool _showGroupTabs;

    public GroupsViewModel()
    {
        Title = "Groups";
        Icon = "Group";
        _clashConfigCache = ClashConfigCacheService.Instance;
        _urlTestRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _urlTestRefreshTimer.Tick += OnUrlTestRefreshTimerTick;
        Groups.CollectionChanged += OnGroupsCollectionChanged;
        UpdateGroupTabsVisibility();
    }

    public GroupsViewModel(ISingBoxManager singBoxManager) : this()
    {
        _singBoxManager = singBoxManager;
        _singBoxManager.StatusChanged += OnServiceStatusChanged;
    }

    public void OnNavigatedTo()
    {
        _isPageActive = true;
        UpdateUrlTestRefreshState();

        if (_singBoxManager == null)
        {
            StatusMessage = "sing-box manager unavailable";
            return;
        }

        if (_singBoxManager.IsRunning)
        {
            if (Groups.Count == 0 || _clashConfigCache.IsDirty)
            {
                _ = LoadGroupsAsync();
            }
        }
        else if (Groups.Count == 0)
        {
            StatusMessage = LocalizationService.Instance[WaitingForSingBoxResourceKey];
        }
    }

    public void OnNavigatedFrom()
    {
        _isPageActive = false;
        UpdateUrlTestRefreshState();
    }

    public void EnsureLoadedInBackground()
    {
        if (_singBoxManager?.IsRunning != true)
        {
            return;
        }

        if (Groups.Count == 0 || _clashConfigCache.IsDirty)
        {
            _ = LoadGroupsAsync();
        }
    }

    public void SetWindowVisible(bool isVisible)
    {
        _isWindowVisible = isVisible;
        UpdateUrlTestRefreshState();
    }

    private async Task LoadGroupsAsync()
    {
        if (_singBoxManager == null)
        {
            return;
        }

        await _loadSemaphore.WaitAsync();
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsLoading = true;
                StatusMessage = "Loading groups...";
            });

            if (!_singBoxManager.IsRunning)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _outboundItemsByTag.Clear();
                    Groups.Clear();
                    SelectedGroup = null;
                    StatusMessage = LocalizationService.Instance[WaitingForSingBoxResourceKey];
                    UpdateGroupTabsVisibility();
                });
                return;
            }

            var groups = await _singBoxManager.GetOutboundGroupsAsync();
            var clashConfig = _clashConfigCache.Current;
            var filteredGroups = groups
                .Where(group => ShouldDisplayGroup(group, clashConfig))
                .ToList();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var previousSelection = SelectedGroup?.Name;
                var preferGlobalGroup = string.Equals(clashConfig?.Mode, "global", StringComparison.OrdinalIgnoreCase);
                _outboundItemsByTag.Clear();
                Groups.Clear();
                GroupItemViewModel? selected = null;
                GroupItemViewModel? globalGroup = null;

                foreach (var group in filteredGroups)
                {
                    var isSelectableGroup = !string.Equals(group.Type, "URLTest", StringComparison.OrdinalIgnoreCase);
                    var outboundItems = group.Items
                        .Select(item => new OutboundItemViewModel
                        {
                            Tag = item.Tag,
                            Type = item.Type,
                            Delay = item.UrlTestDelay
                        })
                        .ToList();

                    if (outboundItems.Count == 0)
                    {
                        continue;
                    }

                    var groupVm = new GroupItemViewModel
                    {
                        Name = group.Tag,
                        Type = group.Type,
                        SelectedOutbound = group.Selected,
                        Items = new ObservableCollection<OutboundItemViewModel>(outboundItems)
                    };

                    groupVm.SelectOutboundCommand = new AsyncRelayCommand<string>(
                        outboundTag => SelectOutboundAsync(group.Tag, outboundTag),
                        _ => isSelectableGroup && _singBoxManager?.IsRunning == true);

                    foreach (var item in groupVm.Items)
                    {
                        RegisterOutboundItem(item);
                        item.SelectOutboundCommand = groupVm.SelectOutboundCommand;
                        item.TestDelayCommand = new AsyncRelayCommand(
                            () => TestOutboundAsync(item),
                            () => _singBoxManager?.IsRunning == true && !item.IsTesting);
                    }

                    groupVm.UpdateItemSelection();

                    if (groupVm.Name == previousSelection)
                    {
                        selected = groupVm;
                    }

                    if (string.Equals(groupVm.Name, "GLOBAL", StringComparison.OrdinalIgnoreCase))
                    {
                        globalGroup = groupVm;
                    }

                    Groups.Add(groupVm);
                }

                SelectedGroup = preferGlobalGroup
                    ? globalGroup ?? selected ?? Groups.FirstOrDefault()
                    : selected ?? Groups.FirstOrDefault();
                StatusMessage = Groups.Count > 0
                    ? $"Loaded {Groups.Count} groups"
                    : "No groups available";
            });
            _clashConfigCache.MarkClean();

            Dispatcher.UIThread.Post(UpdateSelectOutboundCommandStates);
            Dispatcher.UIThread.Post(UpdateTestDelayCommandStates);
            Dispatcher.UIThread.Post(() => TestCurrentGroupCommand.NotifyCanExecuteChanged());
            UpdateUrlTestRefreshState();
            _ = RefreshUrlTestGroupsAsync();
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                StatusMessage = $"Failed to load groups: {ex.Message}";
            });
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsLoading = false);
            _loadSemaphore.Release();
        }
    }

    private void OnServiceStatusChanged(object? sender, ServiceStatus status)
    {
        Dispatcher.UIThread.Post(UpdateSelectOutboundCommandStates);
        Dispatcher.UIThread.Post(UpdateTestDelayCommandStates);
        Dispatcher.UIThread.Post(() => TestCurrentGroupCommand.NotifyCanExecuteChanged());

        if (status == ServiceStatus.Running)
        {
            UpdateUrlTestRefreshState();
            _ = LoadGroupsAsync();
            return;
        }

        if (status is ServiceStatus.Stopped or ServiceStatus.Error)
        {
            Dispatcher.UIThread.Post(() =>
            {
                _outboundItemsByTag.Clear();
                Groups.Clear();
                SelectedGroup = null;
                UpdateGroupTabsVisibility();
                StatusMessage = status == ServiceStatus.Error
                    ? "sing-box failed to start"
                    : "sing-box is not running";
            });
            UpdateUrlTestRefreshState();
        }
    }

    private void OnGroupsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateGroupTabsVisibility();
    }

    private void UpdateGroupTabsVisibility()
    {
        ShowGroupTabs = Groups.Count > 1;
    }

    private void UpdateSelectOutboundCommandStates()
    {
        foreach (var group in Groups)
        {
            if (group.SelectOutboundCommand is AsyncRelayCommand<string> asyncCommand)
            {
                asyncCommand.NotifyCanExecuteChanged();
            }
        }
    }

    private void UpdateTestDelayCommandStates()
    {
        foreach (var group in Groups)
        {
            foreach (var item in group.Items)
            {
                if (item.TestDelayCommand is AsyncRelayCommand asyncCommand)
                {
                    asyncCommand.NotifyCanExecuteChanged();
                }
            }
        }
    }

    private static bool ShouldDisplayGroup(OutboundGroup group, ClashConfigSnapshot? clashConfig)
    {
        if (!string.Equals(group.Tag, "GLOBAL", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var modeCount = clashConfig?.ModeList?
            .Count(mode => !string.IsNullOrWhiteSpace(mode)) ?? 0;
        if (modeCount <= 1)
        {
            return false;
        }

        return string.Equals(clashConfig?.Mode, "global", StringComparison.OrdinalIgnoreCase);
    }

    private async Task SelectOutboundAsync(string groupTag, string? outboundTag)
    {
        if (_singBoxManager == null || string.IsNullOrEmpty(outboundTag))
        {
            return;
        }

        var group = await Dispatcher.UIThread.InvokeAsync(() => Groups.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, groupTag, StringComparison.OrdinalIgnoreCase)));
        if (group != null && string.Equals(group.Type, "URLTest", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            await _singBoxManager.SelectOutboundAsync(groupTag, outboundTag);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (group != null)
                {
                    group.SelectedOutbound = outboundTag;
                }

                StatusMessage = $"Selected {outboundTag} for {groupTag}";
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                StatusMessage = $"Failed to select outbound: {ex.Message}";
            });
        }
    }

    private async Task RefreshGroupSelectionAsync(string groupTag)
    {
        if (_singBoxManager == null || string.IsNullOrWhiteSpace(groupTag) || !_singBoxManager.IsRunning)
        {
            return;
        }

        try
        {
            await RefreshGroupsSnapshotAsync(new[] { groupTag });
        }
        catch
        {
        }
    }

    private async Task RefreshGroupsSnapshotAsync(IEnumerable<string> groupTags)
    {
        if (_singBoxManager == null || !_singBoxManager.IsRunning)
        {
            return;
        }

        var tagSet = groupTags
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (tagSet.Count == 0)
        {
            return;
        }

        var groups = await _singBoxManager.GetOutboundGroupsAsync();
        var updatedGroups = groups
            .Where(group => tagSet.Contains(group.Tag))
            .ToList();
        if (updatedGroups.Count == 0)
        {
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            foreach (var updatedGroup in updatedGroups)
            {
                var existingGroup = Groups.FirstOrDefault(group =>
                    string.Equals(group.Name, updatedGroup.Tag, StringComparison.OrdinalIgnoreCase));
                if (existingGroup == null)
                {
                    continue;
                }

                existingGroup.SelectedOutbound = updatedGroup.Selected;

                foreach (var updatedItem in updatedGroup.Items)
                {
                    ApplySharedDelay(updatedItem.Tag, updatedItem.UrlTestDelay);
                }
            }
        });
    }

    private void OnUrlTestRefreshTimerTick(object? sender, EventArgs e)
    {
        _ = RefreshUrlTestGroupsAsync();
    }

    private void UpdateUrlTestRefreshState()
    {
        if (_singBoxManager?.IsRunning == true && _isPageActive && _isWindowVisible)
        {
            _urlTestRefreshTimer.Start();
            return;
        }

        _urlTestRefreshTimer.Stop();
    }

    private async Task RefreshUrlTestGroupsAsync()
    {
        if (_singBoxManager?.IsRunning != true || !_isPageActive || !_isWindowVisible || _isRefreshingUrlTestGroups)
        {
            return;
        }

        _isRefreshingUrlTestGroups = true;
        try
        {
            var urlTestGroupNames = await Dispatcher.UIThread.InvokeAsync(() => Groups
                .Where(group => string.Equals(group.Type, "URLTest", StringComparison.OrdinalIgnoreCase))
                .Select(group => group.Name)
                .ToList());
            if (urlTestGroupNames.Count == 0)
            {
                return;
            }

            try
            {
                await RefreshGroupsSnapshotAsync(urlTestGroupNames);
            }
            catch
            {
            }
        }
        finally
        {
            _isRefreshingUrlTestGroups = false;
        }
    }

    private async Task TestOutboundAsync(OutboundItemViewModel item)
    {
        if (_singBoxManager == null || string.IsNullOrWhiteSpace(item.Tag))
        {
            return;
        }

        item.IsTesting = true;
        if (item.TestDelayCommand is AsyncRelayCommand asyncCommand)
        {
            asyncCommand.NotifyCanExecuteChanged();
        }

        StatusMessage = $"Testing {item.Tag}...";

        try
        {
            var delays = await _singBoxManager.RunOutboundDelayTestsAsync(new[] { item.Tag });
            var delay = delays.TryGetValue(item.Tag, out var value) && value >= 0 ? value : 0;
            ApplySharedDelay(item.Tag, delay);
            StatusMessage = item.Delay > 0
                ? $"{item.Tag}: {item.Delay}ms"
                : $"{item.Tag}: timeout";
        }
        catch (Exception ex)
        {
            ApplySharedDelay(item.Tag, 0);
            StatusMessage = $"Failed to test {item.Tag}: {ex.Message}";
        }
        finally
        {
            foreach (var sharedItem in GetSharedOutboundItems(item.Tag))
            {
                sharedItem.IsTesting = false;
                if (sharedItem.TestDelayCommand is AsyncRelayCommand updatedCommand)
                {
                    updatedCommand.NotifyCanExecuteChanged();
                }
            }
        }
    }

    partial void OnSelectedGroupChanged(GroupItemViewModel? value)
    {
        TestCurrentGroupCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsTestingGroupChanged(bool value)
    {
        TestCurrentGroupCommand.NotifyCanExecuteChanged();
    }

    private bool CanTestCurrentGroup()
    {
        return _singBoxManager?.IsRunning == true && SelectedGroup != null && !IsTestingGroup;
    }

    private void RegisterOutboundItem(OutboundItemViewModel item)
    {
        if (string.IsNullOrWhiteSpace(item.Tag))
        {
            return;
        }

        if (!_outboundItemsByTag.TryGetValue(item.Tag, out var items))
        {
            items = new List<OutboundItemViewModel>();
            _outboundItemsByTag[item.Tag] = items;
        }

        items.Add(item);
        var sharedDelay = items
            .Select(candidate => candidate.Delay)
            .FirstOrDefault(delay => delay > 0);
        if (sharedDelay > 0 && item.Delay != sharedDelay)
        {
            item.Delay = sharedDelay;
        }
    }

    private IReadOnlyList<OutboundItemViewModel> GetSharedOutboundItems(string tag)
    {
        return _outboundItemsByTag.TryGetValue(tag, out var items)
            ? items
            : Array.Empty<OutboundItemViewModel>();
    }

    private void ApplySharedDelay(string tag, int delay)
    {
        foreach (var item in GetSharedOutboundItems(tag))
        {
            item.Delay = delay;
        }
    }

    private async Task TestGroupAsync(
        GroupItemViewModel group,
        bool updateTestingState,
        bool onlyTestMissingDelay)
    {
        if (_singBoxManager == null)
        {
            return;
        }

        if (string.Equals(group.Type, "URLTest", StringComparison.OrdinalIgnoreCase))
        {
            await TestUrlTestGroupAsync(group, updateTestingState, onlyTestMissingDelay);
            return;
        }

        var targets = group.Items
            .Where(item => !string.IsNullOrWhiteSpace(item.Tag))
            .GroupBy(item => item.Tag, StringComparer.OrdinalIgnoreCase)
            .Select(items => items.First())
            .Where(item => !onlyTestMissingDelay || GetSharedOutboundItems(item.Tag).All(sharedItem => sharedItem.Delay <= 0))
            .ToList();

        if (targets.Count == 0)
        {
            return;
        }

        if (updateTestingState)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsTestingGroup = true;
                StatusMessage = $"Testing {group.Name}...";
            });
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            foreach (var item in targets)
            {
                foreach (var sharedItem in GetSharedOutboundItems(item.Tag))
                {
                    sharedItem.IsTesting = true;
                    if (sharedItem.TestDelayCommand is AsyncRelayCommand asyncCommand)
                    {
                        asyncCommand.NotifyCanExecuteChanged();
                    }
                }
            }
        });

        try
        {
            var completedCount = 0;
            var totalCount = targets.Count;
            var testTasks = targets.Select(async item =>
            {
                var delays = await _singBoxManager.RunOutboundDelayTestsAsync(new[] { item.Tag });
                var delay = delays.TryGetValue(item.Tag, out var value) && value >= 0 ? value : 0;

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    ApplySharedDelay(item.Tag, delay);
                    foreach (var sharedItem in GetSharedOutboundItems(item.Tag))
                    {
                        sharedItem.IsTesting = false;
                        if (sharedItem.TestDelayCommand is AsyncRelayCommand asyncCommand)
                        {
                            asyncCommand.NotifyCanExecuteChanged();
                        }
                    }

                    completedCount++;
                    if (updateTestingState)
                    {
                        StatusMessage = delay > 0
                            ? $"{group.Name}: {item.Tag} {delay}ms ({completedCount}/{totalCount})"
                            : $"{group.Name}: {item.Tag} timeout ({completedCount}/{totalCount})";
                    }
                });
            });

            await Task.WhenAll(testTasks);
            if (string.Equals(group.Type, "URLTest", StringComparison.OrdinalIgnoreCase))
            {
                await RefreshGroupSelectionAsync(group.Name);
            }

            if (updateTestingState)
            {
                StatusMessage = $"Test completed: {group.Name}";
            }
        }
        catch (Exception ex)
        {
            if (updateTestingState)
            {
                StatusMessage = $"Test failed: {ex.Message}";
            }
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var item in targets)
                {
                    foreach (var sharedItem in GetSharedOutboundItems(item.Tag))
                    {
                        sharedItem.IsTesting = false;
                        if (sharedItem.TestDelayCommand is AsyncRelayCommand asyncCommand)
                        {
                            asyncCommand.NotifyCanExecuteChanged();
                        }
                    }
                }

                if (updateTestingState)
                {
                    IsTestingGroup = false;
                }
            });
        }
    }

    private async Task TestUrlTestGroupAsync(
        GroupItemViewModel group,
        bool updateTestingState,
        bool onlyTestMissingDelay)
    {
        if (_singBoxManager == null)
        {
            return;
        }

        var targets = group.Items
            .Where(item => !string.IsNullOrWhiteSpace(item.Tag))
            .GroupBy(item => item.Tag, StringComparer.OrdinalIgnoreCase)
            .Select(items => items.First())
            .ToList();

        if (targets.Count == 0)
        {
            return;
        }

        if (onlyTestMissingDelay &&
            targets.All(item => GetSharedOutboundItems(item.Tag).Any(sharedItem => sharedItem.Delay > 0)))
        {
            await RefreshGroupSelectionAsync(group.Name);
            return;
        }

        if (updateTestingState)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsTestingGroup = true;
                StatusMessage = $"Testing {group.Name}...";
            });
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            foreach (var item in targets)
            {
                foreach (var sharedItem in GetSharedOutboundItems(item.Tag))
                {
                    sharedItem.IsTesting = true;
                    if (sharedItem.TestDelayCommand is AsyncRelayCommand asyncCommand)
                    {
                        asyncCommand.NotifyCanExecuteChanged();
                    }
                }
            }
        });

        try
        {
            await _singBoxManager.RunGroupDelayTestAsync(group.Name);

            var testTasks = targets.Select(async item =>
            {
                var delays = await _singBoxManager.RunOutboundDelayTestsAsync(new[] { item.Tag });
                var delay = delays.TryGetValue(item.Tag, out var value) && value >= 0 ? value : 0;

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    ApplySharedDelay(item.Tag, delay);
                });
            });

            await Task.WhenAll(testTasks);
            await RefreshGroupSelectionAsync(group.Name);

            if (updateTestingState)
            {
                StatusMessage = $"Test completed: {group.Name}";
            }
        }
        catch (Exception ex)
        {
            if (updateTestingState)
            {
                StatusMessage = $"Test failed: {ex.Message}";
            }
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var item in targets)
                {
                    foreach (var sharedItem in GetSharedOutboundItems(item.Tag))
                    {
                        sharedItem.IsTesting = false;
                        if (sharedItem.TestDelayCommand is AsyncRelayCommand asyncCommand)
                        {
                            asyncCommand.NotifyCanExecuteChanged();
                        }
                    }
                }

                if (updateTestingState)
                {
                    IsTestingGroup = false;
                }
            });
        }
    }

    [RelayCommand(CanExecute = nameof(CanTestCurrentGroup))]
    private async Task TestCurrentGroupAsync()
    {
        if (SelectedGroup == null)
        {
            return;
        }

        await TestGroupAsync(SelectedGroup, updateTestingState: true, onlyTestMissingDelay: false);
    }
}

public partial class GroupItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _type = string.Empty;

    [ObservableProperty]
    private string _selectedOutbound = string.Empty;

    [ObservableProperty]
    private ObservableCollection<OutboundItemViewModel> _items = new();

    public IAsyncRelayCommand<string>? SelectOutboundCommand { get; set; }

    partial void OnSelectedOutboundChanged(string value)
    {
        UpdateItemSelection();
    }

    partial void OnItemsChanged(ObservableCollection<OutboundItemViewModel> value)
    {
        UpdateItemSelection();
    }

    public void UpdateItemSelection()
    {
        if (Items == null)
        {
            return;
        }

        foreach (var item in Items)
        {
            item.IsSelected = string.Equals(item.Tag, SelectedOutbound, StringComparison.OrdinalIgnoreCase);
        }
    }
}

public partial class OutboundItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string _tag = string.Empty;

    [ObservableProperty]
    private string _type = string.Empty;

    [ObservableProperty]
    private int _delay;

    public IAsyncRelayCommand<string>? SelectOutboundCommand { get; set; }

    public IAsyncRelayCommand? TestDelayCommand { get; set; }

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isTesting;

    [ObservableProperty]
    private bool _isHovered;

    public string DelayDisplay => IsTesting ? "..." : Delay > 0 ? $"{Delay}ms" : string.Empty;

    partial void OnDelayChanged(int value)
    {
        OnPropertyChanged(nameof(DelayDisplay));
    }

    partial void OnIsTestingChanged(bool value)
    {
        OnPropertyChanged(nameof(DelayDisplay));
    }
}
