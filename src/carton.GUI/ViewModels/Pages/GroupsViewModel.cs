using Avalonia.Threading;
using carton.Core.Models;
using carton.Core.Services;
using carton.GUI.Models;
using carton.GUI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace carton.ViewModels;

public partial class GroupsViewModel : PageViewModelBase
{
    private const string WaitingForSingBoxResourceKey = "Groups.Status.WaitingForSingBoxStart";
    private static readonly TimeSpan CacheExpirationInterval = TimeSpan.FromMinutes(1);
    private readonly ISingBoxManager? _singBoxManager;
    private readonly IPreferencesService? _preferencesService;
    private readonly SemaphoreSlim _loadSemaphore = new(1, 1);
    private readonly ClashConfigCacheService _clashConfigCache;
    private readonly ObservableCollection<OutboundItemViewModel> _expandedProxyItems = new();
    private readonly HashSet<string> _testingOutboundTags = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _urlTestRefreshTimer;
    private IReadOnlyList<GroupCacheSnapshot> _cachedGroups = Array.Empty<GroupCacheSnapshot>();
    private DateTimeOffset? _lastCacheRefreshAt;
    private string? _expandedGroupName;
    private bool _isRefreshingUrlTestGroups;
    private bool _isPageActive;
    private bool _isWindowVisible = true;

    public override NavigationPage PageType => NavigationPage.Groups;

    [ObservableProperty]
    private ObservableCollection<GroupItemViewModel> _groups = new();

    [ObservableProperty]
    private GroupItemViewModel? _selectedGroup;

    [ObservableProperty]
    private IReadOnlyList<GroupMenuSnapshot> _trayGroups = Array.Empty<GroupMenuSnapshot>();

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isTestingGroup;

    public GroupsViewModel()
    {
        Title = "Groups";
        Icon = "Group";
        _clashConfigCache = ClashConfigCacheService.Instance;
        _urlTestRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2.5f)
        };
        _urlTestRefreshTimer.Tick += OnUrlTestRefreshTimerTick;
    }

    public GroupsViewModel(ISingBoxManager singBoxManager) : this()
    {
        _singBoxManager = singBoxManager;
        _singBoxManager.StatusChanged += OnServiceStatusChanged;
    }

    public GroupsViewModel(ISingBoxManager singBoxManager, IPreferencesService preferencesService) : this(singBoxManager)
    {
        _preferencesService = preferencesService;
    }

    public void OnNavigatedTo()
    {
        _isPageActive = true;
        UpdateUrlTestRefreshState();
        var isCacheExpired = IsCacheExpired(DateTimeOffset.UtcNow);

        if (_singBoxManager == null)
        {
            StatusMessage = "sing-box manager unavailable";
            return;
        }

        if (_singBoxManager.IsRunning)
        {
            if (_clashConfigCache.IsDirty || _cachedGroups.Count == 0 || isCacheExpired)
            {
                _ = LoadGroupsAsync();
            }
            else if (Groups.Count == 0)
            {
                RestoreViewGroupsFromCache();
                StatusMessage = CreateLoadedGroupsStatusMessage(Groups.Count);
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

    public void TrimInactiveUi()
    {
        if (_isPageActive)
        {
            return;
        }

        ReleaseViewGroups(clearStatusMessage: false);
    }

    public void EnsureLoadedInBackground()
    {
        if (_singBoxManager?.IsRunning != true)
        {
            return;
        }

        if (_cachedGroups.Count == 0 || _clashConfigCache.IsDirty)
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
                    ReleaseViewGroups(clearStatusMessage: false);
                    _expandedGroupName = null;
                    _cachedGroups = Array.Empty<GroupCacheSnapshot>();
                    TrayGroups = Array.Empty<GroupMenuSnapshot>();
                    StatusMessage = LocalizationService.Instance[WaitingForSingBoxResourceKey];
                });
                return;
            }

            var groups = await _singBoxManager.GetOutboundGroupsAsync();
            var clashConfig = _clashConfigCache.Current;
            var filteredGroups = new List<OutboundGroup>(groups.Count);
            for (var i = 0; i < groups.Count; i++)
            {
                var group = groups[i];
                if (ShouldDisplayGroup(group, clashConfig))
                {
                    filteredGroups.Add(group);
                }
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _cachedGroups = BuildCachedGroupsFromOutboundGroups(filteredGroups);
                _lastCacheRefreshAt = DateTimeOffset.UtcNow;
                UpdateTrayGroupsFromCache();

                if (!_isPageActive)
                {
                    ReleaseViewGroups(clearStatusMessage: false);
                    StatusMessage = CreateLoadedGroupsStatusMessage(CountDisplayableGroups(_cachedGroups));
                    return;
                }

                RestoreViewGroupsFromCache();
                StatusMessage = CreateLoadedGroupsStatusMessage(Groups.Count);
            });
            _clashConfigCache.MarkClean();

            Dispatcher.UIThread.Post(UpdateSelectOutboundCommandStates);
            Dispatcher.UIThread.Post(UpdateTestDelayCommandStates);
            Dispatcher.UIThread.Post(() => TestCurrentGroupCommand.NotifyCanExecuteChanged());
            Dispatcher.UIThread.Post(() => TestGroupCardCommand.NotifyCanExecuteChanged());
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
        Dispatcher.UIThread.Post(() => TestGroupCardCommand.NotifyCanExecuteChanged());

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
                ReleaseViewGroups(clearStatusMessage: false);
                _expandedGroupName = null;
                _cachedGroups = Array.Empty<GroupCacheSnapshot>();
                TrayGroups = Array.Empty<GroupMenuSnapshot>();
                StatusMessage = status == ServiceStatus.Error
                    ? "sing-box failed to start"
                    : "sing-box is not running";
            });
            UpdateUrlTestRefreshState();
        }
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
        foreach (var item in _expandedProxyItems)
        {
            if (item.TestDelayCommand is AsyncRelayCommand asyncCommand)
            {
                asyncCommand.NotifyCanExecuteChanged();
            }
        }
    }

    private void RecalculateEffectiveDelays()
    {
        if (_cachedGroups.Count == 0)
        {
            SyncExpandedProxyItemsFromCache();
            UpdateTrayGroupsFromCache();
            return;
        }

        var selectedOutboundByGroup = new Dictionary<string, string>(_cachedGroups.Count, StringComparer.OrdinalIgnoreCase);
        var rawDelayByTag = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in _cachedGroups)
        {
            selectedOutboundByGroup[group.Name] = group.SelectedOutbound;
            foreach (var item in group.Items)
            {
                if (item.RawDelay > 0 && !rawDelayByTag.ContainsKey(item.Tag))
                {
                    rawDelayByTag[item.Tag] = item.RawDelay;
                }
            }
        }

        var resolvedDelayLookup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var updatedGroups = new List<GroupCacheSnapshot>(_cachedGroups.Count);

        foreach (var group in _cachedGroups)
        {
            var updatedItems = new List<OutboundCacheSnapshot>(group.Items.Count);
            foreach (var item in group.Items)
            {
                var delay = ResolveEffectiveDelay(
                    item.Tag,
                    selectedOutboundByGroup,
                    rawDelayByTag,
                    resolvedDelayLookup,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase));

                updatedItems.Add(item with
                {
                    Delay = delay,
                    IsSelected = string.Equals(item.Tag, group.SelectedOutbound, StringComparison.OrdinalIgnoreCase)
                });
            }

            updatedGroups.Add(group with { Items = updatedItems });
        }

        _cachedGroups = updatedGroups;
        SyncViewGroupsFromCache();
        SyncExpandedProxyItemsFromCache();
        UpdateTrayGroupsFromCache();
    }

    private static int ResolveEffectiveDelay(
        string tag,
        IReadOnlyDictionary<string, string> selectedOutboundByGroup,
        IReadOnlyDictionary<string, int> rawDelayByTag,
        Dictionary<string, int> resolvedDelayLookup,
        HashSet<string> visitedTags)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return 0;
        }

        if (resolvedDelayLookup.TryGetValue(tag, out var cachedDelay))
        {
            return cachedDelay;
        }

        if (!visitedTags.Add(tag))
        {
            return 0;
        }

        if (selectedOutboundByGroup.TryGetValue(tag, out var selectedOutbound) &&
            !string.IsNullOrWhiteSpace(selectedOutbound) &&
            !string.Equals(selectedOutbound, tag, StringComparison.OrdinalIgnoreCase))
        {
            var resolvedDelay = ResolveEffectiveDelay(
                selectedOutbound,
                selectedOutboundByGroup,
                rawDelayByTag,
                resolvedDelayLookup,
                visitedTags);
            resolvedDelayLookup[tag] = resolvedDelay;
            return resolvedDelay;
        }

        var delay = rawDelayByTag.TryGetValue(tag, out var rawDelay) ? rawDelay : 0;
        resolvedDelayLookup[tag] = delay;
        return delay;
    }

    private static bool ShouldDisplayGroup(OutboundGroup group, ClashConfigSnapshot? clashConfig)
    {
        if (!string.Equals(group.Tag, "GLOBAL", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var modeCount = 0;
        var modeList = clashConfig?.ModeList;
        if (modeList != null)
        {
            for (var i = 0; i < modeList.Count; i++)
            {
                if (!string.IsNullOrWhiteSpace(modeList[i]))
                {
                    modeCount++;
                }
            }
        }

        if (modeCount <= 1)
        {
            return false;
        }

        return string.Equals(clashConfig?.Mode, "global", StringComparison.OrdinalIgnoreCase);
    }

    private static int CountDisplayableGroups(IReadOnlyList<GroupCacheSnapshot> groups)
    {
        var count = 0;
        for (var i = 0; i < groups.Count; i++)
        {
            if (groups[i].Items.Count > 0)
            {
                count++;
            }
        }

        return count;
    }

    private static string CreateLoadedGroupsStatusMessage(int groupCount)
    {
        return groupCount > 0
            ? $"Loaded {groupCount} groups"
            : "No groups available";
    }

    private async Task SelectOutboundAsync(string groupTag, string? outboundTag)
    {
        if (_singBoxManager == null || string.IsNullOrEmpty(outboundTag))
        {
            return;
        }

        var group = await Dispatcher.UIThread.InvokeAsync(() => FindGroupByName(groupTag));
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
                    SelectedGroup = group;
                    group.SelectedOutbound = outboundTag;
                }

                UpdateCachedGroupSelection(groupTag, outboundTag);
                RecalculateEffectiveDelays();
                StatusMessage = $"Selected {outboundTag} for {groupTag}";
            });

            StartDisconnectAffectedConnectionsInBackground(groupTag);
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                StatusMessage = $"Failed to select outbound: {ex.Message}";
            });
        }
    }

    private void StartDisconnectAffectedConnectionsInBackground(string groupTag)
    {
        if (_singBoxManager == null ||
            string.IsNullOrWhiteSpace(groupTag) ||
            !ShouldAutoDisconnectConnectionsOnNodeSwitch())
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await DisconnectAffectedConnectionsAsync(groupTag);
            }
            catch
            {
            }
        });
    }

    private async Task DisconnectAffectedConnectionsAsync(string groupTag)
    {
        if (_singBoxManager == null ||
            string.IsNullOrWhiteSpace(groupTag) ||
            !ShouldAutoDisconnectConnectionsOnNodeSwitch())
        {
            return;
        }

        var connections = await _singBoxManager.GetConnectionsAsync();
        var affectedConnections = connections
            .Where(connection =>
                connection.Chains.Any(chain => string.Equals(chain, groupTag, StringComparison.OrdinalIgnoreCase)) ||
                string.Equals(connection.Outbound, groupTag, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (affectedConnections.Length == 0)
        {
            return;
        }

        var disconnectTasks = new Task[affectedConnections.Length];
        for (var i = 0; i < affectedConnections.Length; i++)
        {
            disconnectTasks[i] = _singBoxManager.CloseConnectionAsync(affectedConnections[i].Id);
        }

        await Task.WhenAll(disconnectTasks);
    }

    private bool ShouldAutoDisconnectConnectionsOnNodeSwitch()
    {
        return _preferencesService?.Load().AutoDisconnectConnectionsOnNodeSwitch ?? true;
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

        var tagSet = CreateTagSet(groupTags);
        if (tagSet.Count == 0)
        {
            return;
        }

        var groups = await _singBoxManager.GetOutboundGroupsAsync();
        var groupLookup = new Dictionary<string, OutboundGroup>(groups.Count, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            groupLookup[group.Tag] = group;
        }

        if (!HasMatchingGroup(groupLookup))
        {
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            MergeUpdatedGroupsIntoCache(groupLookup, tagSet);
            RecalculateEffectiveDelays();
            _lastCacheRefreshAt = DateTimeOffset.UtcNow;
        });
    }

    private void MergeUpdatedGroupsIntoCache(
        IReadOnlyDictionary<string, OutboundGroup> groupLookup,
        IReadOnlySet<string> updatedGroupTags)
    {
        if (_cachedGroups.Count == 0)
        {
            return;
        }

        var mergedGroups = new List<GroupCacheSnapshot>(_cachedGroups.Count);
        var hasChanges = false;

        foreach (var existingGroup in _cachedGroups)
        {
            if (!updatedGroupTags.Contains(existingGroup.Name) ||
                !groupLookup.TryGetValue(existingGroup.Name, out var updatedGroup))
            {
                mergedGroups.Add(existingGroup);
                continue;
            }

            var existingItemLookup = new Dictionary<string, OutboundCacheSnapshot>(existingGroup.Items.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var existingItem in existingGroup.Items)
            {
                existingItemLookup[existingItem.Tag] = existingItem;
            }

            var mergedItems = new List<OutboundCacheSnapshot>(updatedGroup.Items.Count);
            foreach (var updatedItem in updatedGroup.Items)
            {
                existingItemLookup.TryGetValue(updatedItem.Tag, out var existingItem);

                var mergedItem = new OutboundCacheSnapshot(
                    updatedItem.Tag,
                    updatedItem.Type,
                    existingItem?.Delay ?? 0,
                    updatedItem.UrlTestDelay,
                    string.Equals(updatedItem.Tag, updatedGroup.Selected, StringComparison.OrdinalIgnoreCase));

                mergedItems.Add(mergedItem);
                hasChanges |= mergedItem != existingItem;
            }

            var mergedGroup = existingGroup with
            {
                Type = updatedGroup.Type,
                SelectedOutbound = updatedGroup.Selected,
                IsSelectable = !string.Equals(updatedGroup.Type, "URLTest", StringComparison.OrdinalIgnoreCase),
                Items = mergedItems
            };

            mergedGroups.Add(mergedGroup);
            hasChanges |= mergedGroup != existingGroup;
        }

        if (hasChanges)
        {
            _cachedGroups = mergedGroups;
        }
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
            var urlTestGroupNames = await Dispatcher.UIThread.InvokeAsync(GetUrlTestGroupNames);
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

        SetOutboundTestingState(item.Tag, true);

        StatusMessage = $"Testing {item.Tag}...";

        try
        {
            var delays = await _singBoxManager.RunOutboundDelayTestsAsync(new[] { item.Tag });
            var delay = delays.TryGetValue(item.Tag, out var value) && value >= 0 ? value : 0;
            UpdateCachedRawDelay(item.Tag, delay);
            RecalculateEffectiveDelays();
            var resolvedDelay = GetCachedEffectiveDelay(item.Tag);
            StatusMessage = resolvedDelay > 0
                ? $"{item.Tag}: {resolvedDelay}ms"
                : $"{item.Tag}: timeout";
        }
        catch (Exception ex)
        {
            UpdateCachedRawDelay(item.Tag, 0);
            RecalculateEffectiveDelays();
            StatusMessage = $"Failed to test {item.Tag}: {ex.Message}";
        }
        finally
        {
            SetOutboundTestingState(item.Tag, false);
        }
    }

    partial void OnSelectedGroupChanged(GroupItemViewModel? value)
    {
        TestCurrentGroupCommand.NotifyCanExecuteChanged();
        TestGroupCardCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsTestingGroupChanged(bool value)
    {
        TestCurrentGroupCommand.NotifyCanExecuteChanged();
        TestGroupCardCommand.NotifyCanExecuteChanged();
    }

    private bool CanTestCurrentGroup()
    {
        return _singBoxManager?.IsRunning == true && SelectedGroup != null && !IsTestingGroup;
    }

    private bool CanTestGroupCard(GroupItemViewModel? group)
    {
        return _singBoxManager?.IsRunning == true && group != null && !IsTestingGroup;
    }

    [RelayCommand]
    private void ToggleGroupExpansion(GroupItemViewModel? group)
    {
        if (group == null)
        {
            return;
        }

        SelectedGroup = group;
        if (string.Equals(_expandedGroupName, group.Name, StringComparison.OrdinalIgnoreCase))
        {
            CollapseExpandedGroup();
            return;
        }

        ExpandGroup(group);
    }

    private void ExpandGroup(GroupItemViewModel group)
    {
        var cachedGroup = FindCachedGroup(group.Name);
        if (cachedGroup == null)
        {
            return;
        }

        CollapseExpandedGroup();
        SyncExpandedProxyItems(cachedGroup, group.SelectOutboundCommand);
        group.Items = _expandedProxyItems;
        group.IsExpanded = true;
        _expandedGroupName = group.Name;
    }

    private void CollapseExpandedGroup()
    {
        CollapseExpandedGroup(clearExpandedGroupName: true);
    }

    private void CollapseExpandedGroup(bool clearExpandedGroupName)
    {
        if (!string.IsNullOrWhiteSpace(_expandedGroupName))
        {
            var expandedGroup = FindGroupByName(_expandedGroupName);
            if (expandedGroup != null)
            {
                expandedGroup.IsExpanded = false;
                expandedGroup.Items = Array.Empty<OutboundItemViewModel>();
            }
        }

        _expandedProxyItems.Clear();
        if (clearExpandedGroupName)
        {
            _expandedGroupName = null;
        }
    }

    private void PopulateExpandedProxyItems(
        GroupCacheSnapshot cachedGroup,
        IAsyncRelayCommand<string>? selectOutboundCommand)
    {
        _expandedProxyItems.Clear();

        foreach (var item in cachedGroup.Items)
        {
            _expandedProxyItems.Add(CreateExpandedProxyItemViewModel(item, selectOutboundCommand));
        }
    }

    private IReadOnlyList<OutboundItemViewModel> GetExpandedOutboundItems(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag) || _expandedProxyItems.Count == 0)
        {
            return Array.Empty<OutboundItemViewModel>();
        }

        var items = new List<OutboundItemViewModel>();
        foreach (var item in _expandedProxyItems)
        {
            if (string.Equals(item.Tag, tag, StringComparison.OrdinalIgnoreCase))
            {
                items.Add(item);
            }
        }

        return items;
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

        var cachedGroup = FindCachedGroup(group.Name);
        if (cachedGroup == null)
        {
            return;
        }

        var targets = BuildUniqueOutboundTargets(cachedGroup.Items, onlyTestMissingDelay);

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
                SetOutboundTestingState(item.Tag, true);
            }
        });

        try
        {
            var completedCounter = new int[1];
            var totalCount = targets.Count;
            var testTasks = new Task[targets.Count];
            for (var i = 0; i < targets.Count; i++)
            {
                var item = targets[i];
                testTasks[i] = TestGroupTargetAsync(group, item, updateTestingState, totalCount, completedCounter);
            }

            await Task.WhenAll(testTasks);
            if (string.Equals(group.Type, "URLTest", StringComparison.OrdinalIgnoreCase))
            {
                await RefreshGroupSelectionAsync(group.Name);
            }

            if (updateTestingState)
            {
                UpdateTrayGroupsFromCache();
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
                    SetOutboundTestingState(item.Tag, false);
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

        var cachedGroup = FindCachedGroup(group.Name);
        if (cachedGroup == null)
        {
            return;
        }

        var targets = BuildUniqueOutboundTargets(cachedGroup.Items, onlyTestMissingDelay: false);

        if (targets.Count == 0)
        {
            return;
        }

        if (onlyTestMissingDelay && AllTargetsHaveSharedDelay(targets))
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
                SetOutboundTestingState(item.Tag, true);
            }
        });

        try
        {
            var delays = await _singBoxManager.RunGroupDelayTestAsync(group.Name);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var item in targets)
                {
                    var delay = delays.TryGetValue(item.Tag, out var value) && value > 0 ? value : 0;
                    UpdateCachedRawDelay(item.Tag, delay);
                }

                RecalculateEffectiveDelays();
            });

            await RefreshGroupSelectionAsync(group.Name);

            if (updateTestingState)
            {
                UpdateTrayGroupsFromCache();
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
                    SetOutboundTestingState(item.Tag, false);
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

    [RelayCommand(CanExecute = nameof(CanTestGroupCard))]
    private async Task TestGroupCardAsync(GroupItemViewModel? group)
    {
        if (group == null)
        {
            return;
        }

        SelectedGroup = group;
        await TestGroupAsync(group, updateTestingState: true, onlyTestMissingDelay: false);
    }

    public Task SelectOutboundFromTrayAsync(string groupTag, string outboundTag)
    {
        return SelectOutboundAsync(groupTag, outboundTag);
    }

    private GroupItemViewModel? FindGroupByName(string groupName)
    {
        for (var i = 0; i < Groups.Count; i++)
        {
            var group = Groups[i];
            if (string.Equals(group.Name, groupName, StringComparison.OrdinalIgnoreCase))
            {
                return group;
            }
        }

        return null;
    }

    private static HashSet<string> CreateTagSet(IEnumerable<string> groupTags)
    {
        var tagSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in groupTags)
        {
            if (!string.IsNullOrWhiteSpace(tag))
            {
                tagSet.Add(tag);
            }
        }

        return tagSet;
    }

    private bool HasMatchingGroup(Dictionary<string, OutboundGroup> groupLookup)
    {
        for (var i = 0; i < Groups.Count; i++)
        {
            if (groupLookup.ContainsKey(Groups[i].Name))
            {
                return true;
            }
        }

        return false;
    }

    private static Dictionary<string, OutboundItem> CreateOutboundItemLookup(OutboundGroup group)
    {
        var itemLookup = new Dictionary<string, OutboundItem>(group.Items.Count, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < group.Items.Count; i++)
        {
            var item = group.Items[i];
            itemLookup[item.Tag] = item;
        }

        return itemLookup;
    }

    private List<string> GetUrlTestGroupNames()
    {
        var result = new List<string>();
        for (var i = 0; i < _cachedGroups.Count; i++)
        {
            var group = _cachedGroups[i];
            if (string.Equals(group.Type, "URLTest", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(group.Name);
            }
        }

        return result;
    }

    private static Dictionary<string, int> CreateResolvedDelayLookup(IReadOnlyList<OutboundGroup> groups)
    {
        var selectedOutboundByGroup = new Dictionary<string, string>(groups.Count, StringComparer.OrdinalIgnoreCase);
        var rawDelayByTag = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            selectedOutboundByGroup[group.Tag] = group.Selected;
            for (var j = 0; j < group.Items.Count; j++)
            {
                var item = group.Items[j];
                if (item.UrlTestDelay > 0 && !rawDelayByTag.ContainsKey(item.Tag))
                {
                    rawDelayByTag[item.Tag] = item.UrlTestDelay;
                }
            }
        }

        var resolvedDelayLookup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in rawDelayByTag)
        {
            ResolveEffectiveDelay(
                entry.Key,
                selectedOutboundByGroup,
                rawDelayByTag,
                resolvedDelayLookup,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        return resolvedDelayLookup;
    }

    private void RestoreViewGroupsFromCache()
    {
        if (_cachedGroups.Count == 0)
        {
            ReleaseViewGroups(clearStatusMessage: false);
            return;
        }

        var previousSelection = SelectedGroup?.Name;
        var preferGlobalGroup = string.Equals(_clashConfigCache.Current?.Mode, "global", StringComparison.OrdinalIgnoreCase);
        ReleaseViewGroups(clearStatusMessage: false);
        GroupItemViewModel? selected = null;
        GroupItemViewModel? globalGroup = null;
        GroupItemViewModel? firstGroup = null;

        foreach (var cachedGroup in _cachedGroups)
        {
            if (cachedGroup.Items.Count == 0)
            {
                continue;
            }

            var groupVm = new GroupItemViewModel
            {
                Name = cachedGroup.Name,
                Type = cachedGroup.Type,
                SelectedOutbound = cachedGroup.SelectedOutbound,
                ItemCount = cachedGroup.Items.Count,
                CollapsedPreviewItems = CreateCollapsedPreviewItems(cachedGroup.Items),
                Items = Array.Empty<OutboundItemViewModel>(),
                IsExpanded = false
            };

            groupVm.SelectOutboundCommand = new AsyncRelayCommand<string>(
                outboundTag => SelectOutboundAsync(cachedGroup.Name, outboundTag),
                _ => cachedGroup.IsSelectable && _singBoxManager?.IsRunning == true);

            groupVm.UpdateItemSelection();

            if (string.Equals(groupVm.Name, previousSelection, StringComparison.OrdinalIgnoreCase))
            {
                selected = groupVm;
            }

            if (string.Equals(groupVm.Name, "GLOBAL", StringComparison.OrdinalIgnoreCase))
            {
                globalGroup = groupVm;
            }

            firstGroup ??= groupVm;
            Groups.Add(groupVm);
        }

        if (!string.IsNullOrWhiteSpace(_expandedGroupName))
        {
            var expandedGroup = FindGroupByName(_expandedGroupName);
            if (expandedGroup != null)
            {
                ExpandGroup(expandedGroup);
            }
        }

        SelectedGroup = preferGlobalGroup
            ? globalGroup ?? selected ?? firstGroup
            : selected ?? firstGroup;
    }

    private static IReadOnlyList<GroupCacheSnapshot> BuildCachedGroupsFromOutboundGroups(IReadOnlyList<OutboundGroup> groups)
    {
        var resolvedDelayLookup = CreateResolvedDelayLookup(groups);
        var cachedGroups = new List<GroupCacheSnapshot>(groups.Count);
        for (var i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            var cachedItems = new List<OutboundCacheSnapshot>(group.Items.Count);
            for (var j = 0; j < group.Items.Count; j++)
            {
                var item = group.Items[j];
                cachedItems.Add(new OutboundCacheSnapshot(
                    item.Tag,
                    item.Type,
                    resolvedDelayLookup.TryGetValue(item.Tag, out var delay) ? delay : 0,
                    item.UrlTestDelay,
                    string.Equals(item.Tag, group.Selected, StringComparison.OrdinalIgnoreCase)));
            }

            cachedGroups.Add(new GroupCacheSnapshot(
                group.Tag,
                group.Type,
                group.Selected,
                !string.Equals(group.Type, "URLTest", StringComparison.OrdinalIgnoreCase),
                cachedItems));
        }

        return cachedGroups;
    }

    private void SyncViewGroupsFromCache()
    {
        if (Groups.Count == 0)
        {
            return;
        }

        foreach (var group in Groups)
        {
            var cachedGroup = FindCachedGroup(group.Name);
            if (cachedGroup == null)
            {
                continue;
            }

            group.Type = cachedGroup.Type;
            group.SelectedOutbound = cachedGroup.SelectedOutbound;
            group.ItemCount = cachedGroup.Items.Count;
            group.CollapsedPreviewItems = CreateCollapsedPreviewItems(cachedGroup.Items);
        }
    }

    private void UpdateCachedGroupSelection(string groupTag, string outboundTag)
    {
        if (_cachedGroups.Count == 0)
        {
            return;
        }

        var updatedGroups = new List<GroupCacheSnapshot>(_cachedGroups.Count);
        var hasChanges = false;

        foreach (var group in _cachedGroups)
        {
            if (!string.Equals(group.Name, groupTag, StringComparison.OrdinalIgnoreCase))
            {
                updatedGroups.Add(group);
                continue;
            }

            updatedGroups.Add(group with { SelectedOutbound = outboundTag });
            hasChanges = true;
        }

        if (hasChanges)
        {
            _cachedGroups = updatedGroups;
            SyncViewGroupsFromCache();
        }
    }

    private void UpdateTrayGroupsFromCache()
    {
        if (_cachedGroups.Count == 0)
        {
            TrayGroups = Array.Empty<GroupMenuSnapshot>();
            return;
        }

        var trayGroups = new List<GroupMenuSnapshot>(_cachedGroups.Count);
        foreach (var group in _cachedGroups)
        {
            if (group.Items.Count == 0)
            {
                continue;
            }

            var trayItems = new List<OutboundMenuSnapshot>(group.Items.Count);
            foreach (var item in group.Items)
            {
                trayItems.Add(new OutboundMenuSnapshot(item.Tag, item.Delay, item.IsSelected));
            }

            trayGroups.Add(new GroupMenuSnapshot(
                group.Name,
                group.Type,
                group.IsSelectable,
                trayItems));
        }

        TrayGroups = trayGroups;
    }

    private void ReleaseViewGroups(bool clearStatusMessage)
    {
        CollapseExpandedGroup(clearExpandedGroupName: false);
        Groups.Clear();
        SelectedGroup = null;
        IsTestingGroup = false;

        if (clearStatusMessage)
        {
            StatusMessage = string.Empty;
        }
    }

    private bool IsCacheExpired(DateTimeOffset now)
    {
        return _lastCacheRefreshAt.HasValue && now - _lastCacheRefreshAt.Value > CacheExpirationInterval;
    }

    private List<OutboundItemViewModel> BuildUniqueOutboundTargets(
        IReadOnlyList<OutboundCacheSnapshot> items,
        bool onlyTestMissingDelay)
    {
        var result = new List<OutboundItemViewModel>(items.Count);
        var seenTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var positiveRawDelayTags = onlyTestMissingDelay ? CreatePositiveRawDelayTagSet() : null;

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (string.IsNullOrWhiteSpace(item.Tag) || !seenTags.Add(item.Tag))
            {
                continue;
            }

            if (positiveRawDelayTags?.Contains(item.Tag) == true)
            {
                continue;
            }

            result.Add(new OutboundItemViewModel
            {
                Tag = item.Tag,
                Type = item.Type,
                Delay = item.Delay,
                RawDelay = item.RawDelay,
                IsSelected = item.IsSelected
            });
        }

        return result;
    }

    private HashSet<string> CreatePositiveRawDelayTagSet()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in _cachedGroups)
        {
            foreach (var item in group.Items)
            {
                if (item.RawDelay > 0)
                {
                    result.Add(item.Tag);
                }
            }
        }

        return result;
    }

    private bool AllTargetsHaveSharedDelay(List<OutboundItemViewModel> targets)
    {
        var positiveRawDelayTags = CreatePositiveRawDelayTagSet();
        for (var i = 0; i < targets.Count; i++)
        {
            if (!positiveRawDelayTags.Contains(targets[i].Tag))
            {
                return false;
            }
        }

        return true;
    }

    private async Task TestGroupTargetAsync(
        GroupItemViewModel group,
        OutboundItemViewModel item,
        bool updateTestingState,
        int totalCount,
        int[] completedCounter)
    {
        if (_singBoxManager == null)
        {
            return;
        }

        var delays = await _singBoxManager.RunOutboundDelayTestsAsync(new[] { item.Tag });
        var delay = delays.TryGetValue(item.Tag, out var value) && value >= 0 ? value : 0;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            UpdateCachedRawDelay(item.Tag, delay);
            RecalculateEffectiveDelays();
            SetOutboundTestingState(item.Tag, false);

            completedCounter[0]++;
            if (updateTestingState)
            {
                StatusMessage = delay > 0
                    ? $"{group.Name}: {item.Tag} {delay}ms ({completedCounter[0]}/{totalCount})"
                    : $"{group.Name}: {item.Tag} timeout ({completedCounter[0]}/{totalCount})";
            }
        });
    }

    private GroupCacheSnapshot? FindCachedGroup(string groupName)
    {
        for (var i = 0; i < _cachedGroups.Count; i++)
        {
            var group = _cachedGroups[i];
            if (string.Equals(group.Name, groupName, StringComparison.OrdinalIgnoreCase))
            {
                return group;
            }
        }

        return null;
    }

    private void SyncExpandedProxyItemsFromCache()
    {
        if (string.IsNullOrWhiteSpace(_expandedGroupName))
        {
            _expandedProxyItems.Clear();
            return;
        }

        var expandedGroup = FindGroupByName(_expandedGroupName);
        var cachedGroup = FindCachedGroup(_expandedGroupName);
        if (expandedGroup == null || cachedGroup == null)
        {
            CollapseExpandedGroup();
            return;
        }

        var selectCommand = expandedGroup.SelectOutboundCommand;
        SyncExpandedProxyItems(cachedGroup, selectCommand);
        if (!ReferenceEquals(expandedGroup.Items, _expandedProxyItems))
        {
            expandedGroup.Items = _expandedProxyItems;
        }
        expandedGroup.UpdateItemSelection();
    }

    private void SyncExpandedProxyItems(
        GroupCacheSnapshot cachedGroup,
        IAsyncRelayCommand<string>? selectOutboundCommand)
    {
        if (_expandedProxyItems.Count == 0)
        {
            PopulateExpandedProxyItems(cachedGroup, selectOutboundCommand);
            return;
        }

        var existingItemLookup = new Dictionary<string, Queue<OutboundItemViewModel>>(StringComparer.OrdinalIgnoreCase);
        foreach (var existingItem in _expandedProxyItems)
        {
            if (!existingItemLookup.TryGetValue(existingItem.Tag, out var queue))
            {
                queue = new Queue<OutboundItemViewModel>();
                existingItemLookup[existingItem.Tag] = queue;
            }

            queue.Enqueue(existingItem);
        }

        var desiredItems = new List<OutboundItemViewModel>(cachedGroup.Items.Count);
        foreach (var item in cachedGroup.Items)
        {
            OutboundItemViewModel? viewModel = null;
            if (existingItemLookup.TryGetValue(item.Tag, out var queue) && queue.Count > 0)
            {
                viewModel = queue.Dequeue();
            }

            viewModel ??= CreateExpandedProxyItemViewModel(item, selectOutboundCommand);
            UpdateExpandedProxyItemViewModel(viewModel, item, selectOutboundCommand);
            desiredItems.Add(viewModel);
        }

        for (var i = 0; i < desiredItems.Count; i++)
        {
            var desiredItem = desiredItems[i];
            if (i >= _expandedProxyItems.Count)
            {
                _expandedProxyItems.Add(desiredItem);
                continue;
            }

            if (ReferenceEquals(_expandedProxyItems[i], desiredItem))
            {
                continue;
            }

            var existingIndex = _expandedProxyItems.IndexOf(desiredItem);
            if (existingIndex >= 0)
            {
                _expandedProxyItems.Move(existingIndex, i);
                continue;
            }

            _expandedProxyItems.Insert(i, desiredItem);
        }

        while (_expandedProxyItems.Count > desiredItems.Count)
        {
            _expandedProxyItems.RemoveAt(_expandedProxyItems.Count - 1);
        }
    }

    private OutboundItemViewModel CreateExpandedProxyItemViewModel(
        OutboundCacheSnapshot item,
        IAsyncRelayCommand<string>? selectOutboundCommand)
    {
        var viewModel = new OutboundItemViewModel();
        UpdateExpandedProxyItemViewModel(viewModel, item, selectOutboundCommand);
        viewModel.TestDelayCommand = new AsyncRelayCommand(
            () => TestOutboundAsync(viewModel),
            () => _singBoxManager?.IsRunning == true && !viewModel.IsTesting);
        return viewModel;
    }

    private void UpdateExpandedProxyItemViewModel(
        OutboundItemViewModel viewModel,
        OutboundCacheSnapshot item,
        IAsyncRelayCommand<string>? selectOutboundCommand)
    {
        viewModel.Tag = item.Tag;
        viewModel.Type = item.Type;
        viewModel.Delay = item.Delay;
        viewModel.RawDelay = item.RawDelay;
        viewModel.IsSelected = item.IsSelected;
        viewModel.IsTesting = _testingOutboundTags.Contains(item.Tag);
        viewModel.SelectOutboundCommand = selectOutboundCommand;

        if (viewModel.TestDelayCommand is AsyncRelayCommand asyncCommand)
        {
            asyncCommand.NotifyCanExecuteChanged();
        }
    }

    private void UpdateCachedRawDelay(string tag, int delay)
    {
        if (_cachedGroups.Count == 0 || string.IsNullOrWhiteSpace(tag))
        {
            return;
        }

        var updatedGroups = new List<GroupCacheSnapshot>(_cachedGroups.Count);
        var hasChanges = false;

        foreach (var group in _cachedGroups)
        {
            var updatedItems = new List<OutboundCacheSnapshot>(group.Items.Count);
            var groupChanged = false;

            foreach (var item in group.Items)
            {
                if (string.Equals(item.Tag, tag, StringComparison.OrdinalIgnoreCase))
                {
                    var updatedItem = item with { RawDelay = delay };
                    updatedItems.Add(updatedItem);
                    groupChanged |= updatedItem != item;
                }
                else
                {
                    updatedItems.Add(item);
                }
            }

            updatedGroups.Add(groupChanged ? group with { Items = updatedItems } : group);
            hasChanges |= groupChanged;
        }

        if (hasChanges)
        {
            _cachedGroups = updatedGroups;
        }
    }

    private void SetOutboundTestingState(string tag, bool isTesting)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return;
        }

        if (isTesting)
        {
            _testingOutboundTags.Add(tag);
        }
        else
        {
            _testingOutboundTags.Remove(tag);
        }

        foreach (var item in GetExpandedOutboundItems(tag))
        {
            item.IsTesting = isTesting;
            if (item.TestDelayCommand is AsyncRelayCommand asyncCommand)
            {
                asyncCommand.NotifyCanExecuteChanged();
            }
        }
    }

    private int GetCachedEffectiveDelay(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return 0;
        }

        foreach (var group in _cachedGroups)
        {
            foreach (var item in group.Items)
            {
                if (string.Equals(item.Tag, tag, StringComparison.OrdinalIgnoreCase))
                {
                    return item.Delay;
                }
            }
        }

        return 0;
    }

    private static IReadOnlyList<OutboundCacheSnapshot> CreateCollapsedPreviewItems(IReadOnlyList<OutboundCacheSnapshot> items)
    {
        const int collapsedPreviewItemLimit = 24;
        if (items.Count <= collapsedPreviewItemLimit)
        {
            return items;
        }

        var previewItems = new List<OutboundCacheSnapshot>(collapsedPreviewItemLimit);
        for (var i = 0; i < collapsedPreviewItemLimit; i++)
        {
            previewItems.Add(items[i]);
        }

        return previewItems;
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
    private int _itemCount;

    [ObservableProperty]
    private IReadOnlyList<OutboundCacheSnapshot> _collapsedPreviewItems = Array.Empty<OutboundCacheSnapshot>();

    [ObservableProperty]
    private IReadOnlyList<OutboundItemViewModel> _items = Array.Empty<OutboundItemViewModel>();

    [ObservableProperty]
    private bool _isExpanded;

    public IAsyncRelayCommand<string>? SelectOutboundCommand { get; set; }

    public bool IsCollapsed => !IsExpanded;

    partial void OnSelectedOutboundChanged(string value)
    {
        UpdateItemSelection();
    }

    partial void OnItemsChanged(IReadOnlyList<OutboundItemViewModel> value)
    {
        UpdateItemSelection();
    }

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsCollapsed));
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

    [ObservableProperty]
    private int _rawDelay;

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

public sealed record OutboundMenuSnapshot(string Tag, int Delay, bool IsSelected);

public sealed record GroupMenuSnapshot(
    string Name,
    string Type,
    bool IsSelectable,
    IReadOnlyList<OutboundMenuSnapshot> Items);

public sealed record OutboundCacheSnapshot(
    string Tag,
    string Type,
    int Delay,
    int RawDelay,
    bool IsSelected);

public sealed record GroupCacheSnapshot(
    string Name,
    string Type,
    string SelectedOutbound,
    bool IsSelectable,
    IReadOnlyList<OutboundCacheSnapshot> Items);
