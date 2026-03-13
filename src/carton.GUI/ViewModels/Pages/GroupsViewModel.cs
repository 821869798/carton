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
                Groups.Clear();
                GroupItemViewModel? selected = null;
                GroupItemViewModel? globalGroup = null;

                foreach (var group in filteredGroups)
                {
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
                        _ => _singBoxManager?.IsRunning == true);

                    foreach (var item in groupVm.Items)
                    {
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
            _ = LoadGroupsAsync();
            return;
        }

        if (status is ServiceStatus.Stopped or ServiceStatus.Error)
        {
            Dispatcher.UIThread.Post(() =>
            {
                Groups.Clear();
                SelectedGroup = null;
                UpdateGroupTabsVisibility();
                StatusMessage = status == ServiceStatus.Error
                    ? "sing-box failed to start"
                    : "sing-box is not running";
            });
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

        try
        {
            await _singBoxManager.SelectOutboundAsync(groupTag, outboundTag);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var group = Groups.FirstOrDefault(candidate => candidate.Name == groupTag);
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
            item.Delay = delays.TryGetValue(item.Tag, out var value) && value >= 0 ? value : 0;
            StatusMessage = item.Delay > 0
                ? $"{item.Tag}: {item.Delay}ms"
                : $"{item.Tag}: timeout";
        }
        catch (Exception ex)
        {
            item.Delay = 0;
            StatusMessage = $"Failed to test {item.Tag}: {ex.Message}";
        }
        finally
        {
            item.IsTesting = false;
            if (item.TestDelayCommand is AsyncRelayCommand updatedCommand)
            {
                updatedCommand.NotifyCanExecuteChanged();
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

    [RelayCommand(CanExecute = nameof(CanTestCurrentGroup))]
    private async Task TestCurrentGroupAsync()
    {
        if (_singBoxManager == null || SelectedGroup == null)
        {
            return;
        }

        var targets = SelectedGroup.Items
            .Select(item => item.Tag)
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .ToList();

        if (targets.Count == 0)
        {
            StatusMessage = "No testable proxies in the current group";
            return;
        }

        IsTestingGroup = true;
        StatusMessage = $"Testing {SelectedGroup.Name}...";

        try
        {
            var delays = await _singBoxManager.RunOutboundDelayTestsAsync(targets);
            foreach (var item in SelectedGroup.Items)
            {
                item.Delay = delays.TryGetValue(item.Tag, out var value) && value >= 0 ? value : 0;
            }

            StatusMessage = $"Test completed: {SelectedGroup.Name}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Test failed: {ex.Message}";
        }
        finally
        {
            IsTestingGroup = false;
        }
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
