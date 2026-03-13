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
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace carton.ViewModels;

public partial class LogsViewModel : PageViewModelBase, IDisposable
{
    private bool _isOnPage;
    private bool _isWindowVisible = true;
    private bool _hasPendingVisibleRefresh;
    private int _pendingFilterRefresh;
    private readonly ILocalizationService _localizationService;
    private readonly LogStore _logStore;

    public override NavigationPage PageType => NavigationPage.Logs;

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

    [ObservableProperty]
    private bool _isAutoScrollToLatest = true;

    public ObservableCollection<string> LogLevels { get; } = new() { "All", "Debug", "Info", "Warn", "Error" };
    public ObservableCollection<LogSourceFilterOptionViewModel> LogSourceFilters { get; } = new();

    public LogsViewModel(LogStore logStore)
    {
        Title = "Logs";
        Icon = "Logs";
        _logStore = logStore;
        _localizationService = LocalizationService.Instance;
        InitializeSourceFilters();
        _localizationService.LanguageChanged += OnLanguageChanged;
        _logStore.EntriesChanged += OnEntriesChanged;
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

    [RelayCommand]
    private async Task CopySelectedLog()
    {
        if (SelectedLog == null) return;

        var line = $"[{SelectedLog.Time}] [{SelectedLog.Level}] {SelectedLog.Message}";
        if (!string.IsNullOrWhiteSpace(SelectedLog.SourceDisplayName))
        {
            line = $"[{SelectedLog.Time}] [{SelectedLog.SourceDisplayName}] [{SelectedLog.Level}] {SelectedLog.Message}";
        }

        await CopyTextToClipboardAsync(line);
    }

    [RelayCommand]
    private async Task CopyAllLogs()
    {
        if (Logs.Count == 0) return;

        var sb = new StringBuilder();
        foreach (var log in Logs)
        {
            sb.Append('[').Append(log.Time).Append("] [").Append(log.SourceDisplayName).Append("] [").Append(log.Level).Append("] ").Append(log.Message).AppendLine();
        }

        await CopyTextToClipboardAsync(sb.ToString());
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
        Logs.Clear();
        SelectedLog = null;
    }

    private void RequestApplyFilters()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(RequestApplyFilters);
            return;
        }

        if (Interlocked.Exchange(ref _pendingFilterRefresh, 1) == 1)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _pendingFilterRefresh, 0);
            ApplyFilters();
        }, DispatcherPriority.Background);
    }

    private void ApplyFilters()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            return;
        }

        var snapshot = _logStore.GetSnapshot();
        var reusableLogs = new Dictionary<(string Time, LogSource Source, string Level, string Message), Queue<LogEntryViewModel>>(Logs.Count);
        for (var i = 0; i < Logs.Count; i++)
        {
            var existing = Logs[i];
            var key = (existing.Time, existing.Source, existing.Level, existing.Message);
            if (!reusableLogs.TryGetValue(key, out var queue))
            {
                queue = new Queue<LogEntryViewModel>();
                reusableLogs[key] = queue;
            }

            queue.Enqueue(existing);
        }

        var filtered = new List<LogEntryViewModel>(snapshot.Count);
        var selectedLog = SelectedLog;
        var selectedExists = selectedLog == null;

        for (var i = 0; i < snapshot.Count; i++)
        {
            var entry = snapshot[i];
            if (!MatchesFilter(entry))
            {
                continue;
            }

            var key = (entry.Time, entry.Source, entry.Level, entry.Message);
            LogEntryViewModel log;
            if (reusableLogs.TryGetValue(key, out var queue) && queue.Count > 0)
            {
                log = queue.Dequeue();
                var sourceDisplayName = GetSourceDisplayName(entry.Source);
                if (!string.Equals(log.SourceDisplayName, sourceDisplayName, StringComparison.Ordinal))
                {
                    log.SourceDisplayName = sourceDisplayName;
                }
            }
            else
            {
                log = new LogEntryViewModel
                {
                    Time = entry.Time,
                    Source = entry.Source,
                    SourceDisplayName = GetSourceDisplayName(entry.Source),
                    Level = entry.Level,
                    Message = entry.Message
                };
            }

            filtered.Add(log);

            if (!selectedExists &&
                log.Time == selectedLog!.Time &&
                log.Source == selectedLog.Source &&
                log.Level == selectedLog.Level &&
                log.Message == selectedLog.Message)
            {
                selectedExists = true;
            }
        }

        var commonCount = Math.Min(Logs.Count, filtered.Count);
        for (var i = 0; i < commonCount; i++)
        {
            if (!ReferenceEquals(Logs[i], filtered[i]))
            {
                Logs[i] = filtered[i];
            }
        }

        for (var i = Logs.Count - 1; i >= filtered.Count; i--)
        {
            Logs.RemoveAt(i);
        }

        for (var i = Logs.Count; i < filtered.Count; i++)
        {
            Logs.Add(filtered[i]);
        }

        if (!selectedExists)
        {
            SelectedLog = null;
        }
    }

    private bool MatchesFilter(LogEntryRecord log)
    {
        var levelMatched = SelectedLevel == "All" ||
                           string.Equals(log.Level, SelectedLevel, StringComparison.OrdinalIgnoreCase);
        if (!levelMatched)
        {
            return false;
        }

        var selectedFilter = SelectedSourceFilter?.Filter ?? LogSourceFilter.All;
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

        if (string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        var sourceDisplayName = GetSourceDisplayName(log.Source);
        return log.Message.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
               sourceDisplayName.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
               log.Level.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
               log.Time.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
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

public partial class LogEntryViewModel : ObservableObject
{
    [ObservableProperty]
    private string _time = string.Empty;

    [ObservableProperty]
    private LogSource _source;

    [ObservableProperty]
    private string _sourceDisplayName = string.Empty;

    [ObservableProperty]
    private string _level = string.Empty;

    [ObservableProperty]
    private string _message = string.Empty;
}

public partial class LogSourceFilterOptionViewModel : ObservableObject
{
    [ObservableProperty]
    private LogSourceFilter _filter;

    [ObservableProperty]
    private string _displayName = string.Empty;
}
