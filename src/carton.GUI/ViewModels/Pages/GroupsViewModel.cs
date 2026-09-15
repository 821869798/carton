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
using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace carton.ViewModels;

public partial class GroupsViewModel : PageViewModelBase
{
    private static readonly TimeSpan CacheExpirationInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan NavigationApiRefreshInterval = TimeSpan.FromSeconds(10);
    private readonly ISingBoxManager? _singBoxManager;
    private readonly IPreferencesService? _preferencesService;
    private readonly SemaphoreSlim _loadSemaphore = new(1, 1);
    private readonly ProxyModeCacheService _proxyModeCache;
    private readonly ObservableCollection<OutboundItemViewModel> _expandedProxyItems = new();
    private readonly TestingTagRefCounts _testingOutboundRefCounts = new();
    // Groups whose test window is still open, so a rebuilt GroupItemViewModel can restore its spinner.
    private readonly HashSet<string> _groupsWithTestInFlight = new(StringComparer.OrdinalIgnoreCase);
    // Bumped when the kernel stops so an in-flight wait from the previous run cannot
    // release tags / restore spinners onto the next session.
    // Plain int on purpose: every access is on the UI thread (the stop handler goes through
    // Dispatcher.UIThread.Post, and the captures/compares live in VM methods that run on the UI
    // thread), so Interlocked/volatile would add nothing. Revisit if any of those move off it.
    private int _testSession;
    private readonly Dictionary<string, (int Version, IReadOnlyList<OutboundCacheSnapshot> Items)> _collapsedPreviewCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GroupMenuSnapshot> _trayGroupLookupBuffer = new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<GroupCacheSnapshot> _cachedGroups = Array.Empty<GroupCacheSnapshot>();
    // Reused by the hot incremental snapshot path. Rebuilding these dictionaries for
    // every URLTest history push was avoidable allocation and CPU work.
    private readonly Dictionary<string, string> _selectedOutboundByGroupCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _rawDelayByTagCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _resolvedDelayLookupBuffer = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _visitedTagsBuffer = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _chainTagsBuffer = new();
    private readonly Dictionary<string, GroupItemViewModel> _groupViewModelLookupBuffer = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset? _lastCacheRefreshAt;
    private DateTimeOffset? _lastNavigationApiRefreshAt;

    // --- Groups snapshot coalescing (mirrors the official dashboard's StreamStore) ---
    // A group test holds NO group-level state and no re-entry guard: the daemon RPC
    // only accepts the test (~ms) and a second click is allowed, exactly like the
    // official dashboard/Windows client. Only the members that still have no value
    // show "..." (per-tag set below), which is what the tray suppression keys on.
    // The kernel pushes one full snapshot per url-test history change: a 200-node
    // group test yields up to 200 pushes in a few seconds. Coalescing them into
    // ONE UI application per window keeps the UI thread free and stops the visible
    // number-flicker (each item still renders its own value; it just no longer
    // repaints the whole list once per remote node result).
    private readonly object _snapshotCoalesceGate = new();
    private GroupsSnapshot? _pendingSnapshot;
    private CancellationTokenSource? _snapshotFlushCts;
    private static readonly TimeSpan DefaultSnapshotCoalesceWindow = TimeSpan.FromMilliseconds(150);
    /// <summary>Adaptive: widened for huge groups (see ApplyGroupsSnapshotAsync).</summary>
    private TimeSpan _snapshotCoalesceWindow = DefaultSnapshotCoalesceWindow;
    private string? _expandedGroupName;
    private bool _isRefreshingGroupsOnNavigation;
    private bool _isPageActive;
    private bool _isWindowVisible = true;

    public override NavigationPage PageType => NavigationPage.Groups;

    /// <summary>
    /// Empty-state switches for the group list (rendered by GroupsView): the kernel
    /// simply is not up yet, versus up but the loaded profile has no outbound groups.
    /// </summary>
    public bool ShowKernelStoppedState => !IsLoading && _singBoxManager?.IsRunning != true;

    public bool ShowNoGroupsState => !IsLoading && _singBoxManager?.IsRunning == true && Groups.Count == 0;

    public bool ShowEmptyState => ShowKernelStoppedState || ShowNoGroupsState;

    [ObservableProperty]
    private ObservableCollection<GroupItemViewModel> _groups = new();

    [ObservableProperty]
    private GroupItemViewModel? _selectedGroup;

    [ObservableProperty]
    private IReadOnlyList<GroupMenuSnapshot> _trayGroups = Array.Empty<GroupMenuSnapshot>();

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>
    /// Latest test generation per group. A repeated click on the group test is allowed
    /// (the official dashboard / Windows client have no guard at all) and supersedes the
    /// previous window instead of racing it: the newest click owns the spinner and the
    /// per-node dots, and an older window that ends later must not clear them.
    /// </summary>
    private readonly Dictionary<string, int> _groupTestGenerations = new(StringComparer.OrdinalIgnoreCase);

    public GroupsViewModel()
    {
        InitializePageMetadata("Group", "Navigation.Groups", "Groups");
        _proxyModeCache = ProxyModeCacheService.Instance;
        _groups.CollectionChanged += OnGroupsCollectionChanged;
    }

    private void OnGroupsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        NotifyEmptyStateChanged();
    }

    partial void OnIsLoadingChanged(bool value)
    {
        NotifyEmptyStateChanged();
    }

    private void NotifyEmptyStateChanged()
    {
        OnPropertyChanged(nameof(ShowKernelStoppedState));
        OnPropertyChanged(nameof(ShowNoGroupsState));
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    public GroupsViewModel(ISingBoxManager singBoxManager) : this()
    {
        _singBoxManager = singBoxManager;
        _singBoxManager.StatusChanged += OnServiceStatusChanged;
        // Event driven: the manager keeps a long-lived SubscribeGroups stream and the
        // kernel pushes a fresh snapshot on every url test history / selection change -
        // the 2.5s polling timer is no longer needed.
        _singBoxManager.GroupsUpdated += OnGroupsSnapshotUpdated;
    }

    public GroupsViewModel(ISingBoxManager singBoxManager, IPreferencesService preferencesService) : this(singBoxManager)
    {
        _preferencesService = preferencesService;
    }

    public override void OnNavigatedTo()
    {
        _isPageActive = true;
        UpdateUrlTestRefreshState();
        var isCacheExpired = IsCacheExpired(DateTimeOffset.UtcNow);

        if (_singBoxManager == null)
        {
            return;
        }

        if (_singBoxManager.IsRunning)
        {
            // A group test may have finished while the page was hidden: the newest
            // stream snapshot waits in _pendingSnapshot. Apply it first so delays
            // and selections are fresh immediately on return, instead of waiting
            // for the next push or the cache expiry window.
            GroupsSnapshot? pendingSnapshot;
            lock (_snapshotCoalesceGate)
            {
                pendingSnapshot = _pendingSnapshot;
                _pendingSnapshot = null;
            }

            var now = DateTimeOffset.UtcNow;
            if (pendingSnapshot != null)
            {
                _ = ApplyGroupsSnapshotAsync(pendingSnapshot);
            }

            // Not mutually exclusive with the pending snapshot: a page created but
            // never entered can hold a pending snapshot while the cache is still
            // empty (the incremental merge returns early on an empty cache), and
            // TrimInactiveUi releases the view VMs while keeping the cache. Both
            // states still need the load/restore paths below.
            var shouldLoadGroups = _proxyModeCache.IsDirty || _cachedGroups.Count == 0 || isCacheExpired;
            if (shouldLoadGroups)
            {
                _lastNavigationApiRefreshAt = now;
                _ = LoadGroupsAsync();
            }
            else if (Groups.Count == 0)
            {
                RestoreViewGroupsFromCache();
                RefreshGroupsFromApiOnNavigationIfDue(now);
            }
            else
            {
                RefreshGroupsFromApiOnNavigationIfDue(now);
            }
        }
        else
        {
            _lastNavigationApiRefreshAt = null;
        }
    }

    public override void OnNavigatedFrom()
    {
        _isPageActive = false;
        UpdateUrlTestRefreshState();

        // Cancel any coalescing timer still pending: nothing should flush while
        // the page is hidden. The latest snapshot stays in _pendingSnapshot so a
        // group test finishing while the user is away still reaches the UI on
        // return (OnNavigatedTo consumes it).
        lock (_snapshotCoalesceGate)
        {
            _snapshotFlushCts?.Cancel();
            _snapshotFlushCts?.Dispose();
            _snapshotFlushCts = null;
        }
    }

    public void TrimInactiveUi()
    {
        if (_isPageActive)
        {
            return;
        }

        ReleaseViewGroups();
    }

    public void EnsureLoadedInBackground()
    {
        if (_singBoxManager?.IsRunning != true)
        {
            return;
        }

        if (_cachedGroups.Count == 0 || _proxyModeCache.IsDirty)
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
            });

            if (!_singBoxManager.IsRunning)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    ReleaseViewGroups();
                    _expandedGroupName = null;
                    _cachedGroups = Array.Empty<GroupCacheSnapshot>();
                    RebuildDelayCaches();
                    _collapsedPreviewCache.Clear();
                    TrayGroups = Array.Empty<GroupMenuSnapshot>();
                });
                return;
            }

            var groups = await GetGroupsForReadAsync();
            var modeConfig = _proxyModeCache.Current;
            var filteredGroups = new List<OutboundGroup>(groups.Count);
            for (var i = 0; i < groups.Count; i++)
            {
                var group = groups[i];
                if (ShouldDisplayGroup(group, modeConfig))
                {
                    filteredGroups.Add(group);
                }
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _cachedGroups = BuildCachedGroupsFromOutboundGroups(filteredGroups);
                RebuildDelayCaches();
                _lastCacheRefreshAt = DateTimeOffset.UtcNow;
                _lastNavigationApiRefreshAt = _lastCacheRefreshAt;
                UpdateTrayGroupsFromCache();

                if (!_isPageActive)
                {
                    ReleaseViewGroups();
                    return;
                }

                RestoreViewGroupsFromCache();
            });
            _proxyModeCache.MarkClean();

            Dispatcher.UIThread.Post(UpdateSelectOutboundCommandStates);
            Dispatcher.UIThread.Post(UpdateTestDelayCommandStates);
            Dispatcher.UIThread.Post(() => TestCurrentGroupCommand.NotifyCanExecuteChanged());
            Dispatcher.UIThread.Post(() => TestGroupCardCommand.NotifyCanExecuteChanged());
            UpdateUrlTestRefreshState();
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
        Dispatcher.UIThread.Post(NotifyEmptyStateChanged);

        if (status == ServiceStatus.Running)
        {
            UpdateUrlTestRefreshState();
            _ = LoadGroupsAsync();
            return;
        }

        if (status is ServiceStatus.Stopped or ServiceStatus.Error)
        {
            _lastNavigationApiRefreshAt = null;
            Dispatcher.UIThread.Post(() =>
            {
                _testSession++;
                _groupsWithTestInFlight.Clear();
                _testingOutboundRefCounts.Clear();
                lock (_groupTestGenerations)
                {
                    _groupTestGenerations.Clear();
                }

                ReleaseViewGroups();
                _expandedGroupName = null;
                _cachedGroups = Array.Empty<GroupCacheSnapshot>();
                RebuildDelayCaches();
                _collapsedPreviewCache.Clear();
                TrayGroups = Array.Empty<GroupMenuSnapshot>();
                // A pending snapshot captured before the stop is stale: applying it
                // on the next navigation would repopulate the just-cleared cache
                // with data from a dead kernel run.
                lock (_snapshotCoalesceGate)
                {
                    _pendingSnapshot = null;
                }
            });
            UpdateUrlTestRefreshState();
        }
    }

    private void RefreshGroupsFromApiOnNavigationIfDue(DateTimeOffset now)
    {
        if (_singBoxManager?.IsRunning != true || _isRefreshingGroupsOnNavigation)
        {
            return;
        }

        if (_lastNavigationApiRefreshAt.HasValue &&
            now - _lastNavigationApiRefreshAt.Value < NavigationApiRefreshInterval)
        {
            return;
        }

        _lastNavigationApiRefreshAt = now;
        _ = RefreshGroupsFromApiOnNavigationAsync();
    }

    private async Task RefreshGroupsFromApiOnNavigationAsync()
    {
        if (_singBoxManager?.IsRunning != true)
        {
            _lastNavigationApiRefreshAt = null;
            return;
        }

        _isRefreshingGroupsOnNavigation = true;
        try
        {
            var groupNames = await Dispatcher.UIThread.InvokeAsync(GetCachedGroupNames);
            if (groupNames.Count == 0)
            {
                return;
            }

            var selections = await GetGroupSelectionSnapshotAsync(groupNames);
            if (selections.Count == 0)
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ApplyGroupSelectionSnapshot(selections);
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to refresh groups on navigation: {ex}");
        }
        finally
        {
            _isRefreshingGroupsOnNavigation = false;
        }
    }

    /// <summary>
    /// Reads the requested groups from the live groups snapshot (fed by the
    /// SubscribeGroups stream) - falls back to a one-shot API fetch while the
    /// stream has not delivered a snapshot yet, or when the cached snapshot went
    /// stale (stream reconnecting): serving hours-old rows silently is worse than
    /// a single on-demand fetch.
    /// </summary>
    private async Task<List<OutboundGroup>> GetGroupsForReadAsync()
    {
        var snapshot = _singBoxManager?.CurrentGroups;
        if (snapshot is { Groups.Count: > 0 } fresh && fresh.IsFresh(TimeSpan.FromSeconds(10)))
        {
            return fresh.Groups.ToList();
        }

        return _singBoxManager == null
            ? new List<OutboundGroup>()
            : await _singBoxManager.GetOutboundGroupsAsync();
    }

    private async Task<Dictionary<string, string>> GetGroupSelectionSnapshotAsync(IReadOnlyList<string> groupNames)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_singBoxManager?.IsRunning != true || groupNames.Count == 0)
        {
            return result;
        }

        var groupNameSet = CreateTagSet(groupNames);
        var groups = await GetGroupsForReadAsync();
        for (var i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            if (string.IsNullOrWhiteSpace(group.Tag) || !groupNameSet.Contains(group.Tag))
            {
                continue;
            }

            result[group.Tag] = group.Selected;
        }

        return result;
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

    private void RebuildDelayCaches()
    {
        _selectedOutboundByGroupCache.Clear();
        _rawDelayByTagCache.Clear();

        foreach (var group in _cachedGroups)
        {
            _selectedOutboundByGroupCache[group.Name] = group.SelectedOutbound;
            foreach (var item in group.Items)
            {
                if (item.RawDelay > 0)
                {
                    _rawDelayByTagCache.TryAdd(item.Tag, item.RawDelay);
                }
            }
        }
    }

    private void RecalculateEffectiveDelays(bool forceUiSync = false)
    {
        if (_cachedGroups.Count == 0)
        {
            SyncExpandedProxyItemsFromCache();
            UpdateTrayGroupsFromCache();
            return;
        }

        RebuildDelayCaches();
        var selectedOutboundByGroup = _selectedOutboundByGroupCache;
        var rawDelayByTag = _rawDelayByTagCache;

        var resolvedDelayLookup = _resolvedDelayLookupBuffer;
        var visitedTags = _visitedTagsBuffer;
        var chainTags = _chainTagsBuffer;
        resolvedDelayLookup.Clear();
        visitedTags.Clear();
        chainTags.Clear();
        var hasChanges = false;

        for (var groupIndex = 0; groupIndex < _cachedGroups.Count; groupIndex++)
        {
            var group = _cachedGroups[groupIndex];
            for (var i = 0; i < group.Items.Count; i++)
            {
                var item = group.Items[i];
                if (!resolvedDelayLookup.TryGetValue(item.Tag, out var delay))
                {
                    delay = ResolveEffectiveDelay(
                        item.Tag,
                        selectedOutboundByGroup,
                        rawDelayByTag,
                        resolvedDelayLookup,
                        visitedTags,
                        chainTags);
                }

                var isSelected = string.Equals(item.Tag, group.SelectedOutbound, StringComparison.OrdinalIgnoreCase);
                if (item.Delay != delay)
                {
                    item.Delay = delay;
                    hasChanges = true;
                }

                if (item.IsSelected != isSelected)
                {
                    item.IsSelected = isSelected;
                    hasChanges = true;
                }
            }
        }

        // Structural changes (items/groups added or removed, type changed) can leave
        // hasChanges false: added/removed nodes may all have zero delay and keep the
        // selection, so the value-only hasChanges check would skip the UI syncs and
        // the view would still list deleted nodes. Callers on the structural path
        // force the syncs; value-only hot paths keep the early return.
        if (!hasChanges && !forceUiSync)
        {
            return;
        }

        SyncViewGroupsFromCache();
        SyncExpandedProxyItemsFromCache();

        // Tray menu rebuild is expensive (full snapshot per group). While any delay
        // test is in flight the intermediate snapshots would rebuild it dozens of
        // times for values that are about to be replaced anyway; the final refresh
        // after the test completes catches it up.
        if (_testingOutboundRefCounts.Count == 0)
        {
            UpdateTrayGroupsFromCache();
        }
    }

    private static int ResolveEffectiveDelay(
        string tag,
        IReadOnlyDictionary<string, string> selectedOutboundByGroup,
        IReadOnlyDictionary<string, int> rawDelayByTag,
        Dictionary<string, int> resolvedDelayLookup,
        HashSet<string> visitedTags,
        List<string> chainTags)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return 0;
        }

        if (resolvedDelayLookup.TryGetValue(tag, out var cachedDelay))
        {
            return cachedDelay;
        }

        visitedTags.Clear();
        chainTags.Clear();

        var currentTag = tag;
        while (true)
        {
            if (string.IsNullOrWhiteSpace(currentTag))
            {
                break;
            }

            if (resolvedDelayLookup.TryGetValue(currentTag, out var resolvedDelay))
            {
                for (var i = 0; i < chainTags.Count; i++)
                {
                    resolvedDelayLookup[chainTags[i]] = resolvedDelay;
                }

                return resolvedDelay;
            }

            if (!visitedTags.Add(currentTag))
            {
                break;
            }

            chainTags.Add(currentTag);
            if (selectedOutboundByGroup.TryGetValue(currentTag, out var nextTag) &&
                !string.IsNullOrWhiteSpace(nextTag) &&
                !string.Equals(nextTag, currentTag, StringComparison.OrdinalIgnoreCase))
            {
                currentTag = nextTag;
                continue;
            }

            break;
        }

        var delay = rawDelayByTag.TryGetValue(currentTag, out var rawDelay) ? rawDelay : 0;
        for (var i = 0; i < chainTags.Count; i++)
        {
            resolvedDelayLookup[chainTags[i]] = delay;
        }

        return delay;
    }

    private static bool ShouldDisplayGroup(OutboundGroup group, ApiModeConfigSnapshot? modeConfig)
    {
        if (!string.Equals(group.Tag, "GLOBAL", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var modeCount = 0;
        var modeList = modeConfig?.ModeList;
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

        return string.Equals(modeConfig?.Mode, "global", StringComparison.OrdinalIgnoreCase);
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
            });

            StartDisconnectAffectedConnectionsInBackground(groupTag);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to select outbound {outboundTag} for {groupTag}: {ex.Message}");
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

        var connections = _singBoxManager.CurrentConnections.ActiveConnections;
        if (connections.Count == 0 && _singBoxManager.IsRunning)
        {
            try
            {
                connections = await _singBoxManager.GetConnectionsAsync();
            }
            catch
            {
            }
        }

        var affectedConnections = new List<ConnectionInfo>();
        for (int i = 0; i < connections.Count; i++)
        {
            var connection = connections[i];
            bool hasMatchingChain = false;
            for (int j = 0; j < connection.Chains.Count; j++)
            {
                if (string.Equals(connection.Chains[j], groupTag, StringComparison.OrdinalIgnoreCase))
                {
                    hasMatchingChain = true;
                    break;
                }
            }
            if (hasMatchingChain || string.Equals(connection.Outbound, groupTag, StringComparison.OrdinalIgnoreCase))
            {
                affectedConnections.Add(connection);
            }
        }

        if (affectedConnections.Count == 0)
        {
            return;
        }

        var disconnectTasks = new Task[affectedConnections.Count];
        for (var i = 0; i < affectedConnections.Count; i++)
        {
            disconnectTasks[i] = _singBoxManager.CloseConnectionAsync(affectedConnections[i].Id);
        }

        await Task.WhenAll(disconnectTasks);
    }

    private bool ShouldAutoDisconnectConnectionsOnNodeSwitch()
    {
        return _preferencesService?.Load().AutoDisconnectConnectionsOnNodeSwitch ?? false;
    }

    // Clash-API-era note: the old "post-test snapshot pull"
    // (RefreshGroupSelectionAsync / RefreshGroupsSnapshotAsync) was removed - the
    // gRPC groups stream pushes fresh values (including the kernel's own URLTest
    // group reselection) automatically after every url test.

    /// <summary>
    /// Merges pushed group updates into the cache.
    /// Returns (StructuralChanged, AnyValueChanged): the caller uses this to pick
    /// between the cheap incremental UI path (delay/selection numbers only) and the
    /// full rebuild path (items added/removed/type changed).
    /// </summary>
    private (bool StructuralChanged, bool AnyValueChanged) MergeUpdatedGroupsIntoCache(
        IReadOnlyDictionary<string, OutboundGroup> groupLookup)
    {
        if (_cachedGroups.Count == 0)
        {
            return (false, false);
        }

        var structuralChanged = false;
        var anyValueChanged = false;

        for (var groupIndex = 0; groupIndex < _cachedGroups.Count; groupIndex++)
        {
            var existingGroup = _cachedGroups[groupIndex];
            if (!groupLookup.TryGetValue(existingGroup.Name, out var updatedGroup))
            {
                continue;
            }

            if (!string.Equals(existingGroup.Type, updatedGroup.Type, StringComparison.Ordinal))
            {
                existingGroup.Type = updatedGroup.Type;
                structuralChanged = true;
            }

            if (!string.Equals(existingGroup.SelectedOutbound, updatedGroup.Selected, StringComparison.OrdinalIgnoreCase))
            {
                existingGroup.SelectedOutbound = updatedGroup.Selected;
                _selectedOutboundByGroupCache[existingGroup.Name] = updatedGroup.Selected;
                anyValueChanged = true;
            }

            var isSelectable = !string.Equals(updatedGroup.Type, "URLTest", StringComparison.OrdinalIgnoreCase);
            if (existingGroup.IsSelectable != isSelectable)
            {
                existingGroup.IsSelectable = isSelectable;
                structuralChanged = true;
            }

            // Hot path for normal URLTest/history pushes: group structure and item
            // order are stable, so update values in place with ZERO merge dictionary /
            // list allocation. Only subscription reloads or real structure changes
            // fall through to the slower structural merge below.
            var sameShape = existingGroup.Items.Count == updatedGroup.Items.Count;
            if (sameShape)
            {
                for (var i = 0; i < updatedGroup.Items.Count; i++)
                {
                    if (!string.Equals(existingGroup.Items[i].Tag, updatedGroup.Items[i].Tag, StringComparison.OrdinalIgnoreCase))
                    {
                        sameShape = false;
                        break;
                    }
                }
            }

            if (sameShape)
            {
                for (var i = 0; i < updatedGroup.Items.Count; i++)
                {
                    var existingItem = existingGroup.Items[i];
                    var updatedItem = updatedGroup.Items[i];
                    if (!string.Equals(existingItem.Type, updatedItem.Type, StringComparison.Ordinal))
                    {
                        existingItem.Type = updatedItem.Type;
                        structuralChanged = true;
                    }

                    // Timeout flag BEFORE the RawDelay update (it reads the previous
                    // delay) and independent of the delay delta: a 0 -> 0 node failing
                    // inside a group test still gets marked, a positive result clears.
                    if (ApplyUrlTestTimeoutState(existingItem, updatedItem.UrlTestDelay))
                    {
                        anyValueChanged = true;
                    }

                    if (existingItem.RawDelay != updatedItem.UrlTestDelay)
                    {
                        existingItem.RawDelay = updatedItem.UrlTestDelay;
                        if (updatedItem.UrlTestDelay > 0)
                        {
                            _rawDelayByTagCache[updatedItem.Tag] = updatedItem.UrlTestDelay;
                        }
                        else
                        {
                            _rawDelayByTagCache.Remove(updatedItem.Tag);
                        }

                        anyValueChanged = true;
                    }

                    var isSelected = string.Equals(updatedItem.Tag, updatedGroup.Selected, StringComparison.OrdinalIgnoreCase);
                    if (existingItem.IsSelected != isSelected)
                    {
                        existingItem.IsSelected = isSelected;
                        anyValueChanged = true;
                    }
                }

                continue;
            }

            structuralChanged = true;
            var existingItemLookup = new Dictionary<string, OutboundCacheSnapshot>(existingGroup.Items.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var existingItem in existingGroup.Items)
            {
                existingItemLookup[existingItem.Tag] = existingItem;
            }

            var mergedItems = new List<OutboundCacheSnapshot>(updatedGroup.Items.Count);
            var groupItemsChanged = true;
            foreach (var updatedItem in updatedGroup.Items)
            {
                existingItemLookup.TryGetValue(updatedItem.Tag, out var existingItem);

                if (existingItem == null)
                {
                    mergedItems.Add(new OutboundCacheSnapshot(
                        updatedItem.Tag,
                        updatedItem.Type,
                        0,
                        updatedItem.UrlTestDelay,
                        string.Equals(updatedItem.Tag, updatedGroup.Selected, StringComparison.OrdinalIgnoreCase)));
                    groupItemsChanged = true;
                    continue;
                }

                if (!string.Equals(existingItem.Type, updatedItem.Type, StringComparison.Ordinal))
                {
                    existingItem.Type = updatedItem.Type;
                    groupItemsChanged = true;
                    structuralChanged = true;
                }

                // Timeout flag BEFORE the RawDelay update (it reads the previous
                // delay) and independent of the delay delta: a 0 -> 0 node failing
                // inside a group test still gets marked, a positive result clears.
                if (ApplyUrlTestTimeoutState(existingItem, updatedItem.UrlTestDelay))
                {
                    groupItemsChanged = true;
                    anyValueChanged = true;
                }

                if (existingItem.RawDelay != updatedItem.UrlTestDelay)
                {
                    existingItem.RawDelay = updatedItem.UrlTestDelay;
                    if (updatedItem.UrlTestDelay > 0)
                    {
                        _rawDelayByTagCache[updatedItem.Tag] = updatedItem.UrlTestDelay;
                    }
                    else
                    {
                        _rawDelayByTagCache.Remove(updatedItem.Tag);
                    }

                    groupItemsChanged = true;
                    anyValueChanged = true;
                }

                var isSelected = string.Equals(updatedItem.Tag, updatedGroup.Selected, StringComparison.OrdinalIgnoreCase);
                if (existingItem.IsSelected != isSelected)
                {
                    existingItem.IsSelected = isSelected;
                    groupItemsChanged = true;
                    anyValueChanged = true;
                }

                mergedItems.Add(existingItem);
            }

            if (groupItemsChanged)
            {
                existingGroup.Items = mergedItems;
            }
        }

        return (structuralChanged, anyValueChanged);
    }

    /// <summary>
    /// Kernel pushed a fresh groups snapshot (url test history / selection change).
    /// Consecutive pushes are coalesced: only the newest snapshot within the window is
    /// applied once. While the page is hidden Core keeps CurrentGroups fresh and this
    /// ViewModel does no work; switching back rebuilds from that latest snapshot -
    /// mirroring the official dashboard's stream-backed store.
    /// </summary>
    private void OnGroupsSnapshotUpdated(object? sender, GroupsSnapshot snapshot)
    {
        if (_singBoxManager?.IsRunning != true)
        {
            return;
        }

        // When the page is inactive, Core still keeps CurrentGroups fresh from the
        // long-lived stream. Do ZERO ViewModel/UI work here: keep only the latest
        // snapshot reference (no flush timer, no merge) so OnNavigatedTo can apply
        // it when the user returns.
        if (!_isPageActive)
        {
            lock (_snapshotCoalesceGate)
            {
                _pendingSnapshot = snapshot;
            }

            return;
        }

        CancellationTokenSource? flushCts = null;
        lock (_snapshotCoalesceGate)
        {
            _pendingSnapshot = snapshot;
            if (_snapshotFlushCts == null)
            {
                // First push in the window: schedule the single flush and remember
                // the token so later pushes within the window reuse the same timer.
                _snapshotFlushCts = new CancellationTokenSource();
                flushCts = _snapshotFlushCts;
            }
        }

        if (flushCts != null)
        {
            _ = FlushPendingSnapshotAsync(flushCts);
        }
    }

    private async Task FlushPendingSnapshotAsync(CancellationTokenSource flushCts)
    {
        try
        {
            await Task.Delay(_snapshotCoalesceWindow, flushCts.Token);
        }
        catch (OperationCanceledException)
        {
            flushCts.Dispose();
            return;
        }

        GroupsSnapshot? snapshot;
        lock (_snapshotCoalesceGate)
        {
            snapshot = _pendingSnapshot;
            _pendingSnapshot = null;
            // Guard against a newer flush cycle: leaving the page and returning can
            // schedule a NEW pending push (with its own CTS) while this older timer
            // fires; nulling unconditionally would drop that reference (the newer
            // flush still runs, just via a stale-field miss - harmless, but unclear).
            if (ReferenceEquals(_snapshotFlushCts, flushCts))
            {
                _snapshotFlushCts = null;
            }
        }

        flushCts.Dispose();
        if (snapshot != null)
        {
            await ApplyGroupsSnapshotAsync(snapshot);
        }
    }

    private async Task ApplyGroupsSnapshotAsync(GroupsSnapshot snapshot)
    {
        var modeConfig = _proxyModeCache.Current;
        var groupLookup = new Dictionary<string, OutboundGroup>(snapshot.Groups.Count, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < snapshot.Groups.Count; i++)
        {
            var group = snapshot.Groups[i];
            if (ShouldDisplayGroup(group, modeConfig))
            {
                groupLookup[group.Tag] = group;
            }
        }

        if (groupLookup.Count == 0)
        {
            return;
        }

        // Adaptive coalescing window: the flush already ran, but the NEXT window is
        // widened for large group sets so a burst of per-node pushes (200+ node
        // group under test) cannot queue up back-to-back full UI applications.
        var totalVisibleItems = 0;
        foreach (var g in snapshot.Groups)
        {
            if (groupLookup.ContainsKey(g.Tag))
            {
                totalVisibleItems += g.Items.Count;
            }
        }

        _snapshotCoalesceWindow = totalVisibleItems > 1000
            ? TimeSpan.FromMilliseconds(500)
            : totalVisibleItems > 500
                ? TimeSpan.FromMilliseconds(300)
                : DefaultSnapshotCoalesceWindow;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var (structuralChanged, anyValueChanged) = MergeUpdatedGroupsIntoCache(groupLookup);
            _lastCacheRefreshAt = DateTimeOffset.UtcNow;

            if (!anyValueChanged && !structuralChanged)
            {
                return;
            }

            if (structuralChanged)
            {
                // Items/groups added/removed or type changed: the full rebuild path is
                // the only correct one. Force the UI syncs: a pure structural change
                // (added/removed zero-delay nodes, unchanged selection) leaves the
                // value-only hasChanges false and would otherwise skip them.
                RecalculateEffectiveDelays(forceUiSync: true);
                return;
            }

            // Value-only update (a kernel url test finished for some nodes): update
            // just the affected view models - O(changed items), no full-list sync.
            ApplyIncrementalDelayUpdates();
        });
    }


    /// <summary>
    /// Value-only push (delays/selection numbers changed, same items). Walks the
    /// cached groups once and updates only the view models whose effective delay
    /// actually changed - this is the hot path during a 200+ node group test,
    /// so it must stay allocation-light and never touch the full-list syncs.
    /// </summary>
    private void ApplyIncrementalDelayUpdates()
    {
        if (_cachedGroups.Count == 0)
        {
            return;
        }

        var selectedOutboundByGroup = _selectedOutboundByGroupCache;
        var rawDelayByTag = _rawDelayByTagCache;

        var resolvedDelayLookup = _resolvedDelayLookupBuffer;
        var visitedTags = _visitedTagsBuffer;
        var chainTags = _chainTagsBuffer;
        resolvedDelayLookup.Clear();
        visitedTags.Clear();
        chainTags.Clear();

        // Raw delay per tag is maintained incrementally by the cache merge above.
        var cachedRawByTag = rawDelayByTag;

        // Keep the collapsed Group cards in sync as well. The first incremental
        // implementation only touched _expandedProxyItems, which left URLTest's
        // kernel-selected tag blank in the card header while its preview dots already
        // showed fresh delays. Update the cached group rows and their existing VMs in
        // the same O(total cached items) pass.
        var groupVmLookup = _groupViewModelLookupBuffer;
        groupVmLookup.Clear();
        foreach (var groupVm in Groups)
        {
            groupVmLookup[groupVm.Name] = groupVm;
        }

        foreach (var cachedGroup in _cachedGroups)
        {
            if (!groupVmLookup.TryGetValue(cachedGroup.Name, out var groupVm))
            {
                continue;
            }

            if (!string.Equals(groupVm.SelectedOutbound, cachedGroup.SelectedOutbound, StringComparison.OrdinalIgnoreCase))
            {
                groupVm.SelectedOutbound = cachedGroup.SelectedOutbound;
            }

            groupVm.ItemCount = cachedGroup.Items.Count;
            groupVm.UpdateItemSelection();
        }

        // Write effective delays back to the cached rows. Collapsed preview dots
        // and the tray menu both bind cachedGroup.Items[].Delay; without this
        // write-back they kept showing pre-test values while the expanded list
        // looked fresh. Shares the memo lookup above, so this stays O(items).
        for (var groupIndex = 0; groupIndex < _cachedGroups.Count; groupIndex++)
        {
            var cachedItems = _cachedGroups[groupIndex].Items;
            for (var i = 0; i < cachedItems.Count; i++)
            {
                var cachedItem = cachedItems[i];
                if (!resolvedDelayLookup.TryGetValue(cachedItem.Tag, out var delay))
                {
                    delay = ResolveEffectiveDelay(
                        cachedItem.Tag,
                        selectedOutboundByGroup,
                        cachedRawByTag,
                        resolvedDelayLookup,
                        visitedTags,
                        chainTags);
                }

                if (cachedItem.Delay != delay)
                {
                    cachedItem.Delay = delay;
                }
            }
        }

        // Update the expanded-group VM items in place.
        for (var i = 0; i < _expandedProxyItems.Count; i++)
        {
            var item = _expandedProxyItems[i];
            var delay = ResolveEffectiveDelay(
                item.Tag,
                selectedOutboundByGroup,
                cachedRawByTag,
                resolvedDelayLookup,
                visitedTags,
                chainTags);
            if (item.Delay != delay)
            {
                item.Delay = delay;
            }

            var raw = cachedRawByTag.GetValueOrDefault(item.Tag);
            if (item.RawDelay != raw)
            {
                item.RawDelay = raw;
            }
        }

        // Tray catch-up follows the same suppression rule as the full path.
        if (_testingOutboundRefCounts.Count == 0)
        {
            UpdateTrayGroupsFromCache();
        }
    }

    private void UpdateUrlTestRefreshState()
    {
        // Streaming pushes replace the polling timer; nothing to start or stop here.
    }

    private async Task TestOutboundAsync(OutboundItemViewModel item)
    {
        if (_singBoxManager == null || string.IsNullOrWhiteSpace(item.Tag))
        {
            return;
        }

        var session = _testSession;
        SetOutboundTestingState(item.Tag, true);

        try
        {
            var delays = await _singBoxManager.RunOutboundDelayTestsAsync(new[] { item.Tag });
            if (session != _testSession)
            {
                return;
            }

            var delay = delays.TryGetValue(item.Tag, out var value) && value > 0 ? value : 0;
            UpdateCachedRawDelay(item.Tag, delay, delay <= 0);
            RecalculateEffectiveDelays();
        }
        catch (Exception ex)
        {
            if (session != _testSession)
            {
                return;
            }

            Debug.WriteLine($"Failed to test {item.Tag}: {ex.Message}");
            UpdateCachedRawDelay(item.Tag, 0);
            RecalculateEffectiveDelays();
        }
        finally
        {
            // No `return` from a finally (CS0157): guard the cleanup instead. A kernel stop
            // already cleared this session's counts, so there is nothing left to release.
            if (session == _testSession)
            {
                SetOutboundTestingState(item.Tag, false);
                // Delay-test suppression above skipped tray rebuilds while this node was
                // testing; catch the tray up now that the final value is in cache.
                UpdateTrayGroupsFromCache();
            }
        }
    }

    partial void OnSelectedGroupChanged(GroupItemViewModel? value)
    {
        TestCurrentGroupCommand.NotifyCanExecuteChanged();
        TestGroupCardCommand.NotifyCanExecuteChanged();
    }

    private bool CanTestCurrentGroup()
    {
        return _singBoxManager?.IsRunning == true && SelectedGroup != null;
    }

    private bool CanTestGroupCard(GroupItemViewModel? group)
    {
        return _singBoxManager?.IsRunning == true && group != null;
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

        // Persist server-side so the expansion survives restarts and syncs to
        // other control clients (sing-box SetGroupExpand, stored in cache.db).
        if (_singBoxManager is { IsRunning: true })
        {
            _ = _singBoxManager.SetGroupExpandAsync(group.Name, true);
        }
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
            var collapsedGroupName = _expandedGroupName;
            _expandedGroupName = null;

            if (!string.IsNullOrWhiteSpace(collapsedGroupName) && _singBoxManager is { IsRunning: true })
            {
                _ = _singBoxManager.SetGroupExpandAsync(collapsedGroupName, false);
            }
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
        GroupItemViewModel group)
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

        var targets = BuildUniqueOutboundTargets(cachedGroup.Items);

        if (targets.Count == 0)
        {
            return;
        }

        // The kernel runs a selector's members in batches of 10 sequentially
        // (daemon/started_service.go URLTest -> protocol/group/urltest.go
        // urlTestBatch.test). That batch RECURSES into nested OutboundGroup members,
        // and a nested URLTest group's own history only lands AFTER all of its
        // nested nodes finish (b.Wait() precedes the group-history loop). The
        // visible member list (GLOBAL = 8 direct members) can therefore hide
        // hundreds of nested nodes (perf-urltest-200 = 200) - count the EXPANDED
        // unique tags, not the direct members, or the budget times out while the
        // kernel is still testing and "Test completed" shows early.
        var expandedTargetCount = CountExpandedTestTargets(cachedGroup.Name);

        // Per-batch allowance is the kernel's worst-case single test time (15s TCP
        // timeout, constant/timeout.go C.TCPTimeout) - failed nodes never produce a
        // fresh result, so the budget is what ends the wait for them. Normal tests
        // finish far below this bound.
        var waitBudgetMs = Math.Max(5000, ((expandedTargetCount + 9) / 10) * 15000);

        // One generation per click: repeated clicks are allowed (the official clients
        // have no guard at all) and each click supersedes the previous test window.
        // Capture session + bump generation on the same UI turn as Acquire so a kernel
        // stop in between cannot pair a new-session acquire with an old-session finish.
        int generation = 0;
        var session = 0;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            session = _testSession;
            lock (_groupTestGenerations)
            {
                generation = _groupTestGenerations[group.Name] = _groupTestGenerations.GetValueOrDefault(group.Name) + 1;
                _groupsWithTestInFlight.Add(group.Name);
            }

            var currentGroup = FindGroupByName(group.Name) ?? group;
            currentGroup.IsTesting = true;
            foreach (var item in targets)
            {
                SetOutboundTestingState(item.Tag, true);
            }
        });

        // The daemon URLTest RPC only ACCEPTS a test - every branch of
        // daemon/started_service.go's URLTest is `go ...` and answers in ~ms - and
        // the per-node results then arrive through the groups stream. So this
        // method returns as soon as the RPC is on the wire: no group-level spinner,
        // no re-entry guard held for the result wait. Holding them (the full budget,
        // or the old 90s soft cap) is what made a second click on the group test do
        // nothing and look stuck; the official dashboard and Windows client hold no
        // state at all. Only members that still have no value keep their "..." (see
        // DelayText), and the wait below decides their final "Timeout".
        var waitTask = _singBoxManager.RunGroupDelayTestAsync(group.Name, timeoutMs: waitBudgetMs);
        _ = FinishGroupTestAsync(group, targets, waitTask, generation, session);
    }

    /// <summary>
    /// Completes a group test started by <see cref="TestGroupAsync"/>: waits (bounded
    /// by the kernel's worst case) for the members to report, then clears their
    /// testing state and refreshes the tray. Detached on purpose - the command that
    /// triggered the test returns as soon as the RPC is on the wire.
    /// </summary>
    private async Task FinishGroupTestAsync(
        GroupItemViewModel group,
        List<OutboundItemViewModel> targets,
        Task<Dictionary<string, int>> waitTask,
        int generation,
        int session)
    {
        try
        {
            await waitTask;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Group delay test failed for {group.Name}: {ex.Message}");
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (session != _testSession)
                {
                    // Kernel stopped (and possibly restarted) while this window was waiting:
                    // OnServiceStatusChanged already cleared the session's counts.
                    return;
                }

                // Release the references THIS window acquired, unconditionally: the ref count is
                // what decides whether a node may stop showing "..." (another in-flight test may
                // still own it). Gating the release on the generation below would leak a
                // reference on every repeated click and leave timed-out nodes stuck on "..."
                // (and the tray suppressed) forever.
                foreach (var item in targets)
                {
                    SetOutboundTestingState(item.Tag, false);
                }

                // A newer click on the same group owns the group-level state now: this older
                // window must not clear its spinner or refresh the tray.
                lock (_groupTestGenerations)
                {
                    if (_groupTestGenerations.GetValueOrDefault(group.Name) != generation)
                    {
                        return;
                    }

                    _groupsWithTestInFlight.Remove(group.Name);
                }

                // Resolve the group through the CURRENT view list: a cache refresh/navigation
                // may have replaced the GroupItemViewModel instance this task started with.
                var currentGroup = FindGroupByName(group.Name);
                if (currentGroup != null)
                {
                    currentGroup.IsTesting = false;
                }

                // Delay-test suppression above skipped tray rebuilds while members
                // were testing; catch the tray up now that the final values are in.
                UpdateTrayGroupsFromCache();
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

        await TestGroupAsync(SelectedGroup);
    }

    [RelayCommand(CanExecute = nameof(CanTestGroupCard))]
    private async Task TestGroupCardAsync(GroupItemViewModel? group)
    {
        if (group == null)
        {
            return;
        }

        SelectedGroup = group;
        await TestGroupAsync(group);
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

    private IReadOnlyList<string> GetCachedGroupNames()
    {
        if (_cachedGroups.Count == 0)
        {
            return Array.Empty<string>();
        }

        var names = new List<string>(_cachedGroups.Count);
        for (var i = 0; i < _cachedGroups.Count; i++)
        {
            var group = _cachedGroups[i];
            if (group.Items.Count > 0 && !string.IsNullOrWhiteSpace(group.Name))
            {
                names.Add(group.Name);
            }
        }

        return names;
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
                if (item.UrlTestDelay > 0)
                {
                    rawDelayByTag.TryAdd(item.Tag, item.UrlTestDelay);
                }
            }
        }

        var resolvedDelayLookup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var visitedTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var chainTags = new List<string>();
        foreach (var entry in rawDelayByTag)
        {
            ResolveEffectiveDelay(
                entry.Key,
                selectedOutboundByGroup,
                rawDelayByTag,
                resolvedDelayLookup,
                visitedTags,
                chainTags);
        }

        return resolvedDelayLookup;
    }

    private void RestoreViewGroupsFromCache()
    {
        if (_cachedGroups.Count == 0)
        {
            ReleaseViewGroups();
            return;
        }

        var previousSelection = SelectedGroup?.Name;
        var preferGlobalGroup = string.Equals(_proxyModeCache.Current?.Mode, "global", StringComparison.OrdinalIgnoreCase);
        ReleaseViewGroups();
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
                // A test window can outlive a cache refresh (1 min expiry vs a multi-minute
                // budget): keep showing its spinner on the rebuilt card.
                IsTesting = _groupsWithTestInFlight.Contains(cachedGroup.Name),
                CollapsedPreviewItems = GetOrCreateCollapsedPreviewItems(cachedGroup.Name, cachedGroup.Items),
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

            if (!string.Equals(group.Type, cachedGroup.Type, StringComparison.Ordinal))
            {
                group.Type = cachedGroup.Type;
            }

            if (!string.Equals(group.SelectedOutbound, cachedGroup.SelectedOutbound, StringComparison.OrdinalIgnoreCase))
            {
                group.SelectedOutbound = cachedGroup.SelectedOutbound;
            }

            if (group.ItemCount != cachedGroup.Items.Count)
            {
                group.ItemCount = cachedGroup.Items.Count;
            }

            var collapsedPreviewItems = GetOrCreateCollapsedPreviewItems(cachedGroup.Name, cachedGroup.Items);
            if (!ReferenceEquals(group.CollapsedPreviewItems, collapsedPreviewItems))
            {
                group.CollapsedPreviewItems = collapsedPreviewItems;
            }
        }
    }

    private void UpdateCachedGroupSelection(string groupTag, string outboundTag)
    {
        if (_cachedGroups.Count == 0)
        {
            return;
        }

        for (var i = 0; i < _cachedGroups.Count; i++)
        {
            var group = _cachedGroups[i];
            if (!string.Equals(group.Name, groupTag, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(group.SelectedOutbound, outboundTag, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            group.SelectedOutbound = outboundTag;
            SyncViewGroupsFromCache();
            break;
        }
    }

    private bool ApplyGroupSelectionSnapshot(IReadOnlyDictionary<string, string> selectedOutboundByGroup)
    {
        if (_cachedGroups.Count == 0 || selectedOutboundByGroup.Count == 0)
        {
            return false;
        }

        var hasChanges = false;
        for (var i = 0; i < _cachedGroups.Count; i++)
        {
            var group = _cachedGroups[i];
            if (!selectedOutboundByGroup.TryGetValue(group.Name, out var selectedOutbound) ||
                string.Equals(group.SelectedOutbound, selectedOutbound, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            group.SelectedOutbound = selectedOutbound;
            UpdateCacheItemSelection(group, selectedOutbound);
            UpdateViewGroupSelection(group.Name, selectedOutbound);
            hasChanges = true;
        }

        if (hasChanges)
        {
            SyncExpandedProxyItemsFromSelectionOnly();
            RecalculateEffectiveDelays();
            UpdateTrayGroupsFromCache();
        }

        return hasChanges;
    }

    private static void UpdateCacheItemSelection(GroupCacheSnapshot group, string selectedOutbound)
    {
        for (var i = 0; i < group.Items.Count; i++)
        {
            var item = group.Items[i];
            var isSelected = string.Equals(item.Tag, selectedOutbound, StringComparison.OrdinalIgnoreCase);
            if (item.IsSelected != isSelected)
            {
                item.IsSelected = isSelected;
            }
        }
    }

    private void UpdateViewGroupSelection(string groupName, string selectedOutbound)
    {
        var group = FindGroupByName(groupName);
        if (group == null)
        {
            return;
        }

        if (!string.Equals(group.SelectedOutbound, selectedOutbound, StringComparison.OrdinalIgnoreCase))
        {
            group.SelectedOutbound = selectedOutbound;
        }
    }

    private void SyncExpandedProxyItemsFromSelectionOnly()
    {
        if (string.IsNullOrWhiteSpace(_expandedGroupName) || _expandedProxyItems.Count == 0)
        {
            return;
        }

        var cachedGroup = FindCachedGroup(_expandedGroupName);
        if (cachedGroup == null)
        {
            return;
        }

        for (var i = 0; i < _expandedProxyItems.Count; i++)
        {
            var item = _expandedProxyItems[i];
            var isSelected = string.Equals(item.Tag, cachedGroup.SelectedOutbound, StringComparison.OrdinalIgnoreCase);
            if (item.IsSelected != isSelected)
            {
                item.IsSelected = isSelected;
            }
        }
    }

    private void UpdateTrayGroupsFromCache()
    {
        if (_cachedGroups.Count == 0)
        {
            if (TrayGroups.Count > 0)
            {
                TrayGroups = Array.Empty<GroupMenuSnapshot>();
            }

            return;
        }

        var currentTrayGroups = TrayGroups;
        var existingByName = _trayGroupLookupBuffer;
        existingByName.Clear();
        for (var i = 0; i < currentTrayGroups.Count; i++)
        {
            existingByName[currentTrayGroups[i].Name] = currentTrayGroups[i];
        }

        List<GroupMenuSnapshot>? updatedTrayGroups = null;
        var index = 0;

        foreach (var group in _cachedGroups)
        {
            if (group.Items.Count == 0)
            {
                continue;
            }

            GroupMenuSnapshot targetGroup;
            if (index < currentTrayGroups.Count && IsTrayGroupEquivalent(currentTrayGroups[index], group))
            {
                targetGroup = currentTrayGroups[index];
            }
            else if (existingByName.TryGetValue(group.Name, out var existingGroup) && IsTrayGroupEquivalent(existingGroup, group))
            {
                targetGroup = existingGroup;
            }
            else
            {
                targetGroup = CreateTrayGroupSnapshot(group);
            }

            if (updatedTrayGroups == null)
            {
                var samePosition = index < currentTrayGroups.Count && ReferenceEquals(currentTrayGroups[index], targetGroup);
                if (!samePosition)
                {
                    updatedTrayGroups = new List<GroupMenuSnapshot>(Math.Max(_cachedGroups.Count, currentTrayGroups.Count));
                    for (var i = 0; i < index && i < currentTrayGroups.Count; i++)
                    {
                        updatedTrayGroups.Add(currentTrayGroups[i]);
                    }

                    updatedTrayGroups.Add(targetGroup);
                }
            }
            else
            {
                updatedTrayGroups.Add(targetGroup);
            }

            index++;
        }

        if (updatedTrayGroups == null)
        {
            if (index == currentTrayGroups.Count)
            {
                return;
            }

            updatedTrayGroups = new List<GroupMenuSnapshot>(index);
            for (var i = 0; i < index && i < currentTrayGroups.Count; i++)
            {
                updatedTrayGroups.Add(currentTrayGroups[i]);
            }
        }

        TrayGroups = updatedTrayGroups;
    }

    private static GroupMenuSnapshot CreateTrayGroupSnapshot(GroupCacheSnapshot group)
    {
        var trayItems = new List<OutboundMenuSnapshot>(group.Items.Count);
        for (var i = 0; i < group.Items.Count; i++)
        {
            var item = group.Items[i];
            trayItems.Add(new OutboundMenuSnapshot(item.Tag, item.Delay, item.IsSelected));
        }

        return new GroupMenuSnapshot(group.Name, group.Type, group.IsSelectable, trayItems);
    }

    private static bool IsTrayGroupEquivalent(GroupMenuSnapshot trayGroup, GroupCacheSnapshot cacheGroup)
    {
        if (!string.Equals(trayGroup.Name, cacheGroup.Name, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(trayGroup.Type, cacheGroup.Type, StringComparison.OrdinalIgnoreCase) ||
            trayGroup.IsSelectable != cacheGroup.IsSelectable ||
            trayGroup.Items.Count != cacheGroup.Items.Count)
        {
            return false;
        }

        for (var i = 0; i < cacheGroup.Items.Count; i++)
        {
            var trayItem = trayGroup.Items[i];
            var cacheItem = cacheGroup.Items[i];
            if (!string.Equals(trayItem.Tag, cacheItem.Tag, StringComparison.OrdinalIgnoreCase) ||
                trayItem.Delay != cacheItem.Delay ||
                trayItem.IsSelected != cacheItem.IsSelected)
            {
                return false;
            }
        }

        return true;
    }

    private void ReleaseViewGroups()
    {
        CollapseExpandedGroup(clearExpandedGroupName: false);
        Groups.Clear();
        SelectedGroup = null;
    }

    private bool IsCacheExpired(DateTimeOffset now)
    {
        return _lastCacheRefreshAt.HasValue && now - _lastCacheRefreshAt.Value > CacheExpirationInterval;
    }

    /// <summary>
    /// Counts the unique outbounds the kernel will actually test for a group tag,
    /// expanding nested groups: urlTestBatch.test recurses into every
    /// OutboundGroup member (protocol/group/urltest.go), so GLOBAL containing a
    /// 200-node urltest subgroup must count ~200, not its ~8 direct members. Uses
    /// the cached groups for the group->members lookup; a nested group MISSING
    /// from the cache counts as 1 (UNDERCOUNTS, never overcounts) - the budget
    /// then runs short and "Test completed" can still show early, so keep the
    /// cache fresh rather than relaxing this.
    /// </summary>
    private int CountExpandedTestTargets(string groupTag)
    {
        if (_cachedGroups.Count == 0)
        {
            return 0;
        }

        var seenTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var countedTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Group lookup by tag for the recursion; built once per call (cold path,
        // group tests are user-triggered).
        var groupsByName = new Dictionary<string, GroupCacheSnapshot>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < _cachedGroups.Count; i++)
        {
            groupsByName[_cachedGroups[i].Name] = _cachedGroups[i];
        }

        if (!groupsByName.TryGetValue(groupTag, out var root))
        {
            return 0;
        }

        CountExpanded(root, groupsByName, seenTags, countedTags);
        return countedTags.Count;

        static void CountExpanded(
            GroupCacheSnapshot group,
            Dictionary<string, GroupCacheSnapshot> groupsByName,
            HashSet<string> seenTags,
            HashSet<string> countedTags)
        {
            if (!seenTags.Add(group.Name))
            {
                // Cyclic group references (selector chains) must not loop forever.
                return;
            }

            for (var i = 0; i < group.Items.Count; i++)
            {
                var item = group.Items[i];
                if (groupsByName.TryGetValue(item.Tag, out var nested))
                {
                    // A member that is itself a group: recurse, mirroring the
                    // kernel's urlTestBatch.test expansion.
                    CountExpanded(nested, groupsByName, seenTags, countedTags);
                }
                else
                {
                    // Leaf outbound (proxy node, DIRECT, REJECT...): it gets a test.
                    countedTags.Add(item.Tag);
                }
            }
        }
    }

    private List<OutboundItemViewModel> BuildUniqueOutboundTargets(
        IReadOnlyList<OutboundCacheSnapshot> items)
    {
        var result = new List<OutboundItemViewModel>(items.Count);
        var seenTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (string.IsNullOrWhiteSpace(item.Tag) || !seenTags.Add(item.Tag))
            {
                continue;
            }

            result.Add(new OutboundItemViewModel
            {
                Tag = item.Tag,
                Type = item.Type,
                Delay = item.Delay,
                RawDelay = item.RawDelay,
                IsDelayTimeout = item.IsDelayTimeout,
                IsSelected = item.IsSelected
            });
        }

        return result;
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

        // Position lookup for the reorder pass: reference identity -> current index.
        // Built once per sync; ObservableCollection.IndexOf is O(n) per call and the
        // reorder loop is O(n^2) with it on large groups.
        var positionLookup = new Dictionary<OutboundItemViewModel, int>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < _expandedProxyItems.Count; i++)
        {
            positionLookup[_expandedProxyItems[i]] = i;
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

            if (positionLookup.TryGetValue(desiredItem, out var existingIndex) &&
                existingIndex < _expandedProxyItems.Count &&
                ReferenceEquals(_expandedProxyItems[existingIndex], desiredItem))
            {
                _expandedProxyItems.Move(existingIndex, i);
                // Positions shifted by the move: rebuild affected indexes cheaply by
                // walking the moved range (contiguous shift of 1 in either direction).
                if (existingIndex > i)
                {
                    for (var j = i; j <= existingIndex; j++)
                    {
                        positionLookup[_expandedProxyItems[j]] = j;
                    }
                }
                else
                {
                    for (var j = existingIndex; j <= i; j++)
                    {
                        positionLookup[_expandedProxyItems[j]] = j;
                    }
                }
                continue;
            }

            _expandedProxyItems.Insert(i, desiredItem);
            for (var j = i; j < _expandedProxyItems.Count; j++)
            {
                positionLookup[_expandedProxyItems[j]] = j;
            }
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
        if (!string.Equals(viewModel.Tag, item.Tag, StringComparison.Ordinal))
        {
            viewModel.Tag = item.Tag;
        }

        if (!string.Equals(viewModel.Type, item.Type, StringComparison.Ordinal))
        {
            viewModel.Type = item.Type;
        }

        if (viewModel.Delay != item.Delay)
        {
            viewModel.Delay = item.Delay;
        }

        if (viewModel.RawDelay != item.RawDelay)
        {
            viewModel.RawDelay = item.RawDelay;
        }

        if (viewModel.IsDelayTimeout != item.IsDelayTimeout)
        {
            viewModel.IsDelayTimeout = item.IsDelayTimeout;
        }

        if (viewModel.IsSelected != item.IsSelected)
        {
            viewModel.IsSelected = item.IsSelected;
        }

        var isTesting = IsOutboundTesting(item.Tag);
        var testingStateChanged = viewModel.IsTesting != isTesting;
        if (testingStateChanged)
        {
            viewModel.IsTesting = isTesting;
        }

        if (!ReferenceEquals(viewModel.SelectOutboundCommand, selectOutboundCommand))
        {
            viewModel.SelectOutboundCommand = selectOutboundCommand;
        }

        if (testingStateChanged && viewModel.TestDelayCommand is AsyncRelayCommand asyncCommand)
        {
            asyncCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// Keeps the expanded-group VM rows in sync with a cached row's timeout flag
    /// (collapsed preview dots bind the cached row directly; expanded rows are
    /// separate view models). Runs on the UI thread inside the snapshot merge.
    /// </summary>
    private void SyncExpandedItemTimeout(string tag, bool isDelayTimeout)
    {
        var items = _expandedProxyItems;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (string.Equals(item.Tag, tag, StringComparison.OrdinalIgnoreCase) &&
                item.IsDelayTimeout != isDelayTimeout)
            {
                item.IsDelayTimeout = isDelayTimeout;
            }
        }
    }

    /// <summary>
    /// Applies the URLTest timeout flag independent of the RawDelay delta: a
    /// node that never had a delay (0 -> 0 on a failed group test) must still be
    /// marked, and a positive delay always clears it. Must run BEFORE the
    /// RawDelay update so it can read the previous value. A zero-delay node that
    /// never had a delay OUTSIDE a running group test stays "never measured"
    /// (the single-node path marks timeouts via UpdateCachedRawDelay instead).
    /// </summary>
    private bool ApplyUrlTestTimeoutState(OutboundCacheSnapshot existingItem, int urlTestDelay)
    {
        if (urlTestDelay > 0)
        {
            if (existingItem.IsDelayTimeout)
            {
                existingItem.IsDelayTimeout = false;
                SyncExpandedItemTimeout(existingItem.Tag, false);
                return true;
            }

            return false;
        }

        if (existingItem.IsDelayTimeout)
        {
            return false;
        }

        // Zero delay: mark when the node previously had a delay (the kernel
        // cleared its saved history) or THIS node is currently being tested. The
        // per-tag set matters here: the merge
        // scans every cached group, so a proxy-group test must not mark never-measured
        // nodes in auto/netflix as timed out just because some group is testing.
        // A tag can appear in several groups; if it is being tested it is being
        // tested everywhere it appears.
        if (existingItem.RawDelay != 0 || IsOutboundTesting(existingItem.Tag))
        {
            existingItem.IsDelayTimeout = true;
            SyncExpandedItemTimeout(existingItem.Tag, true);
            return true;
        }

        return false;
    }

    private void UpdateCachedRawDelay(string tag, int delay, bool isTimeout = false)
    {
        if (_cachedGroups.Count == 0 || string.IsNullOrWhiteSpace(tag))
        {
            return;
        }

        for (var groupIndex = 0; groupIndex < _cachedGroups.Count; groupIndex++)
        {
            var group = _cachedGroups[groupIndex];
            for (var i = 0; i < group.Items.Count; i++)
            {
                var item = group.Items[i];
                if (string.Equals(item.Tag, tag, StringComparison.OrdinalIgnoreCase))
                {
                    if (item.RawDelay != delay)
                    {
                        item.RawDelay = delay;
                    }

                    if (item.IsDelayTimeout != isTimeout)
                    {
                        item.IsDelayTimeout = isTimeout;
                    }
                }
            }
        }

        for (var i = 0; i < _expandedProxyItems.Count; i++)
        {
            var item = _expandedProxyItems[i];
            if (string.Equals(item.Tag, tag, StringComparison.OrdinalIgnoreCase) &&
                item.IsDelayTimeout != isTimeout)
            {
                item.IsDelayTimeout = isTimeout;
            }
        }
    }

    /// <summary>True while any in-flight test owns <paramref name="tag"/>.</summary>
    private bool IsOutboundTesting(string tag)
    {
        return _testingOutboundRefCounts.Contains(tag);
    }

    /// <summary>
    /// Adds/removes one owner of the per-node test state. Ref-counted (see TestingTagRefCounts) so
    /// overlapping tests - two groups sharing a node, or a group test plus that node's own test -
    /// keep the "..." / IsTesting state until the last one finishes; only the 0 &lt;-&gt; 1
    /// transitions touch the UI.
    /// </summary>
    private void SetOutboundTestingState(string tag, bool isTesting)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return;
        }

        var visibleStateChanged = isTesting
            ? _testingOutboundRefCounts.Acquire(tag)
            : _testingOutboundRefCounts.Release(tag);
        if (!visibleStateChanged)
        {
            // Already flagged by another in-flight test, or released something we never owned.
            return;
        }

        for (var i = 0; i < _expandedProxyItems.Count; i++)
        {
            var item = _expandedProxyItems[i];
            if (!string.Equals(item.Tag, tag, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            item.IsTesting = isTesting;
            if (item.TestDelayCommand is AsyncRelayCommand asyncCommand)
            {
                asyncCommand.NotifyCanExecuteChanged();
            }
        }
    }

    private IReadOnlyList<OutboundCacheSnapshot> GetOrCreateCollapsedPreviewItems(
        string groupName,
        IReadOnlyList<OutboundCacheSnapshot> items)
    {
        var version = CreateCollapsedPreviewVersion(items);
        if (_collapsedPreviewCache.TryGetValue(groupName, out var cacheEntry) && cacheEntry.Version == version)
        {
            return cacheEntry.Items;
        }

        var collapsedPreviewItems = CreateCollapsedPreviewItems(items);
        _collapsedPreviewCache[groupName] = (version, collapsedPreviewItems);
        return collapsedPreviewItems;
    }

    private static int CreateCollapsedPreviewVersion(IReadOnlyList<OutboundCacheSnapshot> items)
    {
        return HashCode.Combine(items.Count, RuntimeHelpers.GetHashCode(items));
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
    private const int DefaultExpandedProxyColumns = 3;
    private int _expandedProxyColumns = DefaultExpandedProxyColumns;

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
    private IReadOnlyList<OutboundItemRowViewModel> _itemRows = Array.Empty<OutboundItemRowViewModel>();

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isTesting;

    public IAsyncRelayCommand<string>? SelectOutboundCommand { get; set; }

    public bool IsCollapsed => !IsExpanded;

    partial void OnSelectedOutboundChanged(string value)
    {
        UpdateItemSelection();
    }

    partial void OnItemsChanged(IReadOnlyList<OutboundItemViewModel> value)
    {
        RebuildItemRows();
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

    private void RebuildItemRows()
    {
        if (Items == null || Items.Count == 0)
        {
            ItemRows = Array.Empty<OutboundItemRowViewModel>();
            return;
        }

        var columns = Math.Max(1, _expandedProxyColumns);
        var rows = new List<OutboundItemRowViewModel>((Items.Count + columns - 1) / columns);
        for (var i = 0; i < Items.Count; i += columns)
        {
            var count = Math.Min(columns, Items.Count - i);
            var rowItems = new List<OutboundItemViewModel>(count);
            for (var j = 0; j < count; j++)
            {
                rowItems.Add(Items[i + j]);
            }

            rows.Add(new OutboundItemRowViewModel(rowItems));
        }

        ItemRows = rows;
    }

    public void SetExpandedProxyColumns(int columns)
    {
        var normalizedColumns = Math.Max(1, columns);
        if (_expandedProxyColumns == normalizedColumns)
        {
            return;
        }

        _expandedProxyColumns = normalizedColumns;
        RebuildItemRows();
    }
}

public sealed class OutboundItemRowViewModel
{
    public OutboundItemRowViewModel(IReadOnlyList<OutboundItemViewModel> items)
    {
        Items = items;
    }

    public IReadOnlyList<OutboundItemViewModel> Items { get; }
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
    private bool _isDelayTimeout;

    /// <summary>
    /// Single source of truth for both the text and the colour of the delay cell.
    /// </summary>
    public DelayState DelayState => DelayText.State(Delay, IsTesting, IsDelayTimeout);
    public string DelayDisplay => DelayText.Text(DelayState, Delay);
    public bool ShowDelayText => DelayText.ShowText(DelayState);
    public bool ShowDelayIcon => DelayState == DelayState.None;

    partial void OnDelayChanged(int value)
    {
        NotifyDelayStateChanged();
    }

    partial void OnIsTestingChanged(bool value)
    {
        NotifyDelayStateChanged();
    }

    partial void OnIsDelayTimeoutChanged(bool value)
    {
        NotifyDelayStateChanged();
    }

    private void NotifyDelayStateChanged()
    {
        OnPropertyChanged(nameof(DelayState));
        OnPropertyChanged(nameof(DelayDisplay));
        OnPropertyChanged(nameof(ShowDelayText));
        OnPropertyChanged(nameof(ShowDelayIcon));
    }
}

public sealed record OutboundMenuSnapshot(string Tag, int Delay, bool IsSelected);

public sealed record GroupMenuSnapshot(
    string Name,
    string Type,
    bool IsSelectable,
    IReadOnlyList<OutboundMenuSnapshot> Items);

public sealed partial class OutboundCacheSnapshot : ObservableObject
{
    public OutboundCacheSnapshot(string tag, string type, int delay, int rawDelay, bool isSelected, bool isDelayTimeout = false)
    {
        _tag = tag;
        _type = type;
        _delay = delay;
        _rawDelay = rawDelay;
        _isSelected = isSelected;
        _isDelayTimeout = isDelayTimeout;
    }

    [ObservableProperty]
    private string _tag;

    [ObservableProperty]
    private string _type;

    [ObservableProperty]
    private int _delay;

    [ObservableProperty]
    private int _rawDelay;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isDelayTimeout;
}

public sealed class GroupCacheSnapshot
{
    public GroupCacheSnapshot(string name, string type, string selectedOutbound, bool isSelectable, IReadOnlyList<OutboundCacheSnapshot> items)
    {
        Name = name;
        Type = type;
        SelectedOutbound = selectedOutbound;
        IsSelectable = isSelectable;
        Items = items;
    }

    public string Name { get; set; }

    public string Type { get; set; }

    public string SelectedOutbound { get; set; }

    public bool IsSelectable { get; set; }

    public IReadOnlyList<OutboundCacheSnapshot> Items { get; set; }
}
