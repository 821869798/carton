using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using carton.Core.Models;
using carton.GUI.Models;
using carton.GUI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace carton.ViewModels;

public partial class LogsViewModel : PageViewModelBase, IDisposable
{
    private static readonly TimeSpan LogRefreshInterval = TimeSpan.FromMilliseconds(100);
    private bool _isOnPage;
    private bool _isWindowVisible = true;
    private bool _hasPendingVisibleRefresh;
    private int _pendingFilterRefresh;
    private readonly ILocalizationService _localizationService;
    private readonly LogStore _logStore;
    private readonly DispatcherTimer _filterRefreshTimer;
    private readonly List<LogEntryRecord> _snapshotBuffer = new(1024);
    private bool _hasAppliedFilterState;
    private long _lastAppliedSequence;
    private long _lastAppliedEpoch;
    private string _appliedSelectedLevel = "All";
    private LogSourceFilter _appliedSourceFilter = LogSourceFilter.All;
    private string _appliedSearchText = string.Empty;
    private string _appliedCartonSourceDisplayName = string.Empty;
    private string _appliedSingBoxSourceDisplayName = string.Empty;

    public override NavigationPage PageType => NavigationPage.Logs;
    public event EventHandler? VisibleLogsRefreshed;

    [ObservableProperty]
    private ObservableCollection<LogEntryViewModel> _logs = new();

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _selectedLevel = "All";

    [ObservableProperty]
    private LogSourceFilterOptionViewModel? _selectedSourceFilter;

    [ObservableProperty]
    private LogEntryViewModel? _selectedLog;

    public ObservableCollection<LogEntryViewModel> SelectedLogs { get; } = new();

    private bool CanCopySelectedLog => SelectedLogs.Count > 0 || SelectedLog != null;

    [ObservableProperty]
    private bool _isAutoScrollToLatest = true;

    public ObservableCollection<string> LogLevels { get; } = new() { "All", "Debug", "Info", "Warn", "Error", "Fatal" };
    public ObservableCollection<LogSourceFilterOptionViewModel> LogSourceFilters { get; } = new();

    public LogsViewModel(LogStore logStore)
    {
        InitializePageMetadata("Logs", "Navigation.Logs", "Logs");
        _logStore = logStore;
        _localizationService = LocalizationService.Instance;
        _filterRefreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = LogRefreshInterval
        };
        _filterRefreshTimer.Tick += OnFilterRefreshTimerTick;
        InitializeSourceFilters();
        _localizationService.LanguageChanged += OnLanguageChanged;
        _logStore.EntriesChanged += OnEntriesChanged;
        SelectedLogs.CollectionChanged += (_, _) => CopySelectedLogCommand.NotifyCanExecuteChanged();

        // Release builds default the level filter to Info (lowest-level semantics:
        // Info and everything more severe stays visible while Debug chatter stays
        // quiet); Debug builds show everything so diagnosis keeps all detail.
#if DEBUG
        _selectedLevel = "All";
        _appliedSelectedLevel = "All";
#else
        _selectedLevel = "Info";
        _appliedSelectedLevel = "Info";
#endif
    }

    public void OnNavigatedTo()
    {
        _isOnPage = true;
        _hasPendingVisibleRefresh = true;
        RefreshVisibleLogsIfNeeded();
    }

    public void SetWindowVisible(bool isVisible)
    {
        _isWindowVisible = isVisible;
        if (isVisible)
        {
            RefreshVisibleLogsIfNeeded();
        }
        else
        {
            ReleaseVisibleLogs();
        }
    }

    public void OnNavigatedFrom()
    {
        _isOnPage = false;
        ReleaseVisibleLogs();
    }

    public void Dispose()
    {
        _localizationService.LanguageChanged -= OnLanguageChanged;
        _logStore.EntriesChanged -= OnEntriesChanged;
        _filterRefreshTimer.Tick -= OnFilterRefreshTimerTick;
        _filterRefreshTimer.Stop();
        ReleaseVisibleLogs();
    }

    partial void OnSearchTextChanged(string value)
    {
        RequestApplyFilters();
    }

    partial void OnSelectedLevelChanged(string value)
    {
        RequestApplyFilters();
    }

    partial void OnSelectedSourceFilterChanged(LogSourceFilterOptionViewModel? value)
    {
        RequestApplyFilters();
    }

    [RelayCommand]
    private void ClearLogs()
    {
        _logStore.Clear();
    }

    partial void OnSelectedLogChanged(LogEntryViewModel? value)
    {
        CopySelectedLogCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanCopySelectedLog))]
    private async Task CopySelectedLog()
    {
        var selectedLogs = GetSelectedLogsInDisplayOrder();
        if (selectedLogs.Length == 0)
        {
            return;
        }

        var sb = new StringBuilder();
        foreach (var log in selectedLogs)
        {
            sb.AppendLine(FormatLogLine(log));
        }

        await CopyTextToClipboardAsync(sb.ToString().TrimEnd('\r', '\n'));
    }

    private LogEntryViewModel[] GetSelectedLogsInDisplayOrder()
    {
        if (SelectedLogs.Count == 0)
        {
            return SelectedLog != null
                ? new[] { SelectedLog }
                : Array.Empty<LogEntryViewModel>();
        }

        var selectedSet = SelectedLogs.ToHashSet();
        var result = new List<LogEntryViewModel>(selectedSet.Count);
        for (int i = 0; i < Logs.Count; i++)
        {
            if (selectedSet.Contains(Logs[i]))
            {
                result.Add(Logs[i]);
            }
        }
        return result.ToArray();
    }

    [RelayCommand]
    private async Task CopyAllLogs()
    {
        if (Logs.Count == 0) return;

        var sb = new StringBuilder();
        foreach (var log in Logs)
        {
            sb.AppendLine(FormatLogLine(log));
        }

        await CopyTextToClipboardAsync(sb.ToString().TrimEnd('\r', '\n'));
    }

    private void OnEntriesChanged(object? sender, EventArgs e)
    {
        if (_isOnPage && _isWindowVisible)
        {
            RequestApplyFilters();
        }
        else
        {
            _hasPendingVisibleRefresh = true;
        }
    }

    private void RefreshVisibleLogsIfNeeded()
    {
        if (_isOnPage && _isWindowVisible && _hasPendingVisibleRefresh)
        {
            _hasPendingVisibleRefresh = false;
            RequestApplyFilters();
        }
    }

    private void ReleaseVisibleLogs()
    {
        StopPendingFilterRefresh();
        SelectedLogs.Clear();
        Logs = new ObservableCollection<LogEntryViewModel>();
        SelectedLog = null;
        _snapshotBuffer.Clear();
        _snapshotBuffer.TrimExcess();
        _hasAppliedFilterState = false;
    }

    private void RequestApplyFilters()
    {
        if (!_isOnPage || !_isWindowVisible)
        {
            _hasPendingVisibleRefresh = true;
            return;
        }

        if (Interlocked.Exchange(ref _pendingFilterRefresh, 1) == 1)
        {
            return;
        }

        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(ScheduleApplyFilters, DispatcherPriority.Background);
            return;
        }

        ScheduleApplyFilters();
    }

    private void ScheduleApplyFilters()
    {
        if (!Dispatcher.UIThread.CheckAccess() || !_isOnPage || !_isWindowVisible)
        {
            _hasPendingVisibleRefresh = true;
            Interlocked.Exchange(ref _pendingFilterRefresh, 0);
            return;
        }

        _filterRefreshTimer.Stop();
        _filterRefreshTimer.Start();
    }

    private void OnFilterRefreshTimerTick(object? sender, EventArgs e)
    {
        _filterRefreshTimer.Stop();
        Interlocked.Exchange(ref _pendingFilterRefresh, 0);
        if (!_isOnPage || !_isWindowVisible)
        {
            _hasPendingVisibleRefresh = true;
            return;
        }

        ApplyFilters();
    }

    private void StopPendingFilterRefresh()
    {
        _filterRefreshTimer.Stop();
        Interlocked.Exchange(ref _pendingFilterRefresh, 0);
    }

    private void ApplyFilters()
    {
        if (!Dispatcher.UIThread.CheckAccess() || !_isOnPage || !_isWindowVisible)
        {
            _hasPendingVisibleRefresh = true;
            return;
        }

        _logStore.CopySnapshotTo(_snapshotBuffer);
        // Structural resets (kernel log replay) renumber/reorder the buffer: detect the
        // epoch bump and force a full rebuild so replayed history is not appended on
        // top of stale rows.
        var resetEpoch = _logStore.ResetEpoch;
        var epochChanged = resetEpoch != _lastAppliedEpoch;
        _lastAppliedEpoch = resetEpoch;
        var selectedLevel = SelectedLevel;
        var selectedFilter = SelectedSourceFilter?.Filter ?? LogSourceFilter.All;
        var searchText = SearchText;
        var hasSearchText = !string.IsNullOrWhiteSpace(searchText);
        var cartonSourceDisplayName = _localizationService["Logs.Source.Carton"];
        var singBoxSourceDisplayName = _localizationService["Logs.Source.SingBox"];
        var latestSequence = _snapshotBuffer.Count == 0
            ? _lastAppliedSequence
            : _snapshotBuffer[^1].Sequence;

        var filterStateChanged =
            !_hasAppliedFilterState ||
            epochChanged ||
            latestSequence < _lastAppliedSequence ||
            !string.Equals(_appliedSelectedLevel, selectedLevel, StringComparison.Ordinal) ||
            _appliedSourceFilter != selectedFilter ||
            !string.Equals(_appliedSearchText, searchText, StringComparison.Ordinal) ||
            !string.Equals(_appliedCartonSourceDisplayName, cartonSourceDisplayName, StringComparison.Ordinal) ||
            !string.Equals(_appliedSingBoxSourceDisplayName, singBoxSourceDisplayName, StringComparison.Ordinal);

        var visibleLogsChanged = filterStateChanged
            ? RebuildVisibleLogs(
                _snapshotBuffer,
                selectedLevel,
                selectedFilter,
                searchText,
                hasSearchText,
                cartonSourceDisplayName,
                singBoxSourceDisplayName)
            : AppendVisibleLogChanges(
                _snapshotBuffer,
                selectedLevel,
                selectedFilter,
                searchText,
                hasSearchText,
                cartonSourceDisplayName,
                singBoxSourceDisplayName);

        _lastAppliedSequence = latestSequence;
        _appliedSelectedLevel = selectedLevel;
        _appliedSourceFilter = selectedFilter;
        _appliedSearchText = searchText;
        _appliedCartonSourceDisplayName = cartonSourceDisplayName;
        _appliedSingBoxSourceDisplayName = singBoxSourceDisplayName;
        _hasAppliedFilterState = true;

        if (visibleLogsChanged)
        {
            VisibleLogsRefreshed?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool RebuildVisibleLogs(
        IReadOnlyList<LogEntryRecord> snapshot,
        string selectedLevel,
        LogSourceFilter selectedFilter,
        string searchText,
        bool hasSearchText,
        string cartonSourceDisplayName,
        string singBoxSourceDisplayName)
    {
        var selectedLog = SelectedLog;
        var selectedSequence = selectedLog?.Sequence;
        LogEntryViewModel? matchedSelectedLog = null;
        HashSet<long>? selectedSequences = null;
        if (SelectedLogs.Count > 0)
        {
            selectedSequences = new HashSet<long>(SelectedLogs.Count);
            foreach (var log in SelectedLogs)
            {
                selectedSequences.Add(log.Sequence);
            }
        }

        SelectedLogs.Clear();
        Logs.Clear();

        // The selected level never changes inside this loop: compute its rank once
        // instead of re-parsing it per entry.
        var selectedRank = LogStore.GetLevelRank(selectedLevel);

        for (var i = 0; i < snapshot.Count; i++)
        {
            var entry = snapshot[i];
            if (!MatchesFilter(entry, selectedLevel, selectedRank, selectedFilter, searchText, hasSearchText, cartonSourceDisplayName, singBoxSourceDisplayName))
            {
                continue;
            }

            var sourceDisplayName = GetSourceDisplayName(entry.Source, cartonSourceDisplayName, singBoxSourceDisplayName);
            var log = CreateLogViewModel(entry, sourceDisplayName);
            Logs.Add(log);

            if (selectedSequence == entry.Sequence)
            {
                matchedSelectedLog = log;
            }

            if (selectedSequences?.Contains(entry.Sequence) == true)
            {
                SelectedLogs.Add(log);
            }
        }

        SelectedLog = matchedSelectedLog;
        return true;
    }

    private bool AppendVisibleLogChanges(
        IReadOnlyList<LogEntryRecord> snapshot,
        string selectedLevel,
        LogSourceFilter selectedFilter,
        string searchText,
        bool hasSearchText,
        string cartonSourceDisplayName,
        string singBoxSourceDisplayName)
    {
        if (snapshot.Count == 0)
        {
            var hadLogs = Logs.Count > 0 || SelectedLogs.Count > 0 || SelectedLog != null;
            SelectedLogs.Clear();
            Logs.Clear();
            SelectedLog = null;
            return hadLogs;
        }

        var removedAny = false;
        var addedAny = false;
        var oldestSequence = snapshot[0].Sequence;
        while (Logs.Count > 0 && Logs[0].Sequence < oldestSequence)
        {
            Logs.RemoveAt(0);
            removedAny = true;
        }

        // The selected level never changes inside this loop: compute its rank once
        // instead of re-parsing it per entry.
        var selectedRank = LogStore.GetLevelRank(selectedLevel);

        for (var i = 0; i < snapshot.Count; i++)
        {
            var entry = snapshot[i];
            if (entry.Sequence <= _lastAppliedSequence)
            {
                continue;
            }

            if (!MatchesFilter(entry, selectedLevel, selectedRank, selectedFilter, searchText, hasSearchText, cartonSourceDisplayName, singBoxSourceDisplayName))
            {
                continue;
            }

            var sourceDisplayName = GetSourceDisplayName(entry.Source, cartonSourceDisplayName, singBoxSourceDisplayName);
            Logs.Add(CreateLogViewModel(entry, sourceDisplayName));
            addedAny = true;
        }

        if (removedAny)
        {
            PruneSelection();
        }

        return removedAny || addedAny;
    }

    private void PruneSelection()
    {
        if (SelectedLog != null && !Logs.Contains(SelectedLog))
        {
            SelectedLog = null;
        }

        for (var i = SelectedLogs.Count - 1; i >= 0; i--)
        {
            if (!Logs.Contains(SelectedLogs[i]))
            {
                SelectedLogs.RemoveAt(i);
            }
        }
    }

    private static LogEntryViewModel CreateLogViewModel(LogEntryRecord entry, string sourceDisplayName)
    {
        return new LogEntryViewModel
        {
            Sequence = entry.Sequence,
            Time = entry.Time,
            Source = entry.Source,
            SourceDisplayName = sourceDisplayName,
            Level = entry.Level,
            Message = entry.Message
        };
    }

    private static bool MatchesFilter(
        LogEntryRecord log,
        string selectedLevel,
        int selectedRank,
        LogSourceFilter selectedFilter,
        string searchText,
        bool hasSearchText,
        string cartonSourceDisplayName,
        string singBoxSourceDisplayName)
    {
        // Rank is cached on the record at creation time: a pure integer compare,
        // no per-entry string matching on the hot filter path.
        var levelMatched = selectedLevel == "All" ||
                           log.LevelRank >= selectedRank;
        if (!levelMatched)
        {
            return false;
        }

        var sourceMatched = selectedFilter switch
        {
            LogSourceFilter.All => true,
            LogSourceFilter.Carton => log.Source == LogSource.Carton,
            LogSourceFilter.SingBox => log.Source == LogSource.SingBox,
            _ => true
        };
        if (!sourceMatched)
        {
            return false;
        }

        if (!hasSearchText)
        {
            return true;
        }

        var sourceDisplayName = GetSourceDisplayName(log.Source, cartonSourceDisplayName, singBoxSourceDisplayName);
        return log.Message.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
               sourceDisplayName.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
               log.Level.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
               log.Time.Contains(searchText, StringComparison.OrdinalIgnoreCase);
    }

    private void InitializeSourceFilters()
    {
        LogSourceFilters.Clear();
        LogSourceFilters.Add(new LogSourceFilterOptionViewModel { Filter = LogSourceFilter.All });
        LogSourceFilters.Add(new LogSourceFilterOptionViewModel { Filter = LogSourceFilter.Carton });
        LogSourceFilters.Add(new LogSourceFilterOptionViewModel { Filter = LogSourceFilter.SingBox });
        UpdateSourceFilterDisplayNames();
        SelectedSourceFilter = LogSourceFilters[0];
    }

    private void OnLanguageChanged(object? sender, AppLanguage e)
    {
        UpdateSourceFilterDisplayNames();
        RequestApplyFilters();
    }

    private void UpdateSourceFilterDisplayNames()
    {
        foreach (var option in LogSourceFilters)
        {
            option.DisplayName = option.Filter switch
            {
                LogSourceFilter.All => _localizationService["Logs.Source.All"],
                LogSourceFilter.Carton => _localizationService["Logs.Source.Carton"],
                LogSourceFilter.SingBox => _localizationService["Logs.Source.SingBox"],
                _ => option.Filter.ToString()
            };
        }
    }

    private string GetSourceDisplayName(LogSource source)
    {
        return source switch
        {
            LogSource.Carton => _localizationService["Logs.Source.Carton"],
            LogSource.SingBox => _localizationService["Logs.Source.SingBox"],
            _ => source.ToString()
        };
    }

    private static string GetSourceDisplayName(LogSource source, string cartonSourceDisplayName, string singBoxSourceDisplayName)
    {
        return source switch
        {
            LogSource.Carton => cartonSourceDisplayName,
            LogSource.SingBox => singBoxSourceDisplayName,
            _ => source.ToString()
        };
    }

    private static string FormatLogLine(LogEntryViewModel log)
    {
        return string.IsNullOrWhiteSpace(log.SourceDisplayName)
            ? $"[{log.Time}] [{log.Level}] {log.Message}"
            : $"[{log.Time}] [{log.SourceDisplayName}] [{log.Level}] {log.Message}";
    }

    private static async Task CopyTextToClipboardAsync(string text)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop ||
            desktop.MainWindow?.Clipboard == null)
        {
            return;
        }

        await desktop.MainWindow.Clipboard.SetTextAsync(text);
    }
}

public sealed class LogEntryViewModel
{
    public long Sequence { get; init; }

    public string Time { get; init; } = string.Empty;

    public LogSource Source { get; init; }

    public string SourceDisplayName { get; init; } = string.Empty;

    public string Level { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;
}

public partial class LogSourceFilterOptionViewModel : ObservableObject
{
    [ObservableProperty]
    private LogSourceFilter _filter;

    [ObservableProperty]
    private string _displayName = string.Empty;
}
