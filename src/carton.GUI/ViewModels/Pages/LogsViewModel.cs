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

    public ObservableCollection<LogEntryViewModel> SelectedLogs { get; } = new();

    private bool CanCopySelectedLog => SelectedLogs.Count > 0 || SelectedLog != null;

    [ObservableProperty]
    private bool _isAutoScrollToLatest = true;

    public ObservableCollection<string> LogLevels { get; } = new() { "All", "Debug", "Info", "Warn", "Error" };
    public ObservableCollection<LogSourceFilterOptionViewModel> LogSourceFilters { get; } = new();

    public LogsViewModel(LogStore logStore)
    {
        InitializePageMetadata("Logs", "Navigation.Logs", "Logs");
        _logStore = logStore;
        _localizationService = LocalizationService.Instance;
        InitializeSourceFilters();
        _localizationService.LanguageChanged += OnLanguageChanged;
        _logStore.EntriesChanged += OnEntriesChanged;
        SelectedLogs.CollectionChanged += (_, _) => CopySelectedLogCommand.NotifyCanExecuteChanged();
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
        return Logs.Where(selectedSet.Contains).ToArray();
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
        SelectedLogs.Clear();
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
        var selectedLevel = SelectedLevel;
        var selectedFilter = SelectedSourceFilter?.Filter ?? LogSourceFilter.All;
        var searchText = SearchText;
        var hasSearchText = !string.IsNullOrWhiteSpace(searchText);
        var cartonSourceDisplayName = _localizationService["Logs.Source.Carton"];
        var singBoxSourceDisplayName = _localizationService["Logs.Source.SingBox"];

        var writeIndex = 0;
        var selectedLog = SelectedLog;
        var selectedExists = selectedLog == null;
        var selectedTime = selectedLog?.Time;
        var selectedSource = selectedLog?.Source;
        var selectedLevelText = selectedLog?.Level;
        var selectedMessage = selectedLog?.Message;
        LogEntryViewModel? matchedSelectedLog = null;

        for (var i = 0; i < snapshot.Count; i++)
        {
            var entry = snapshot[i];
            if (!MatchesFilter(entry, selectedLevel, selectedFilter, searchText, hasSearchText, cartonSourceDisplayName, singBoxSourceDisplayName))
            {
                continue;
            }

            var sourceDisplayName = GetSourceDisplayName(entry.Source, cartonSourceDisplayName, singBoxSourceDisplayName);
            LogEntryViewModel log;
            if (writeIndex < Logs.Count)
            {
                log = Logs[writeIndex];
                UpdateLog(log, entry, sourceDisplayName);
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

                Logs.Add(log);
            }

            writeIndex++;

            if (!selectedExists &&
                log.Time == selectedTime &&
                log.Source == selectedSource &&
                log.Level == selectedLevelText &&
                log.Message == selectedMessage)
            {
                selectedExists = true;
                matchedSelectedLog = log;
            }
        }

        for (var i = Logs.Count - 1; i >= writeIndex; i--)
        {
            Logs.RemoveAt(i);
        }

        if (!selectedExists)
        {
            SelectedLogs.Clear();
            SelectedLog = null;
        }
        else if (matchedSelectedLog != null && !ReferenceEquals(SelectedLog, matchedSelectedLog))
        {
            SelectedLog = matchedSelectedLog;
        }
    }

    private static void UpdateLog(LogEntryViewModel log, LogEntryRecord entry, string sourceDisplayName)
    {
        if (!string.Equals(log.Time, entry.Time, StringComparison.Ordinal))
        {
            log.Time = entry.Time;
        }

        if (log.Source != entry.Source)
        {
            log.Source = entry.Source;
        }

        if (!string.Equals(log.SourceDisplayName, sourceDisplayName, StringComparison.Ordinal))
        {
            log.SourceDisplayName = sourceDisplayName;
        }

        if (!string.Equals(log.Level, entry.Level, StringComparison.Ordinal))
        {
            log.Level = entry.Level;
        }

        if (!string.Equals(log.Message, entry.Message, StringComparison.Ordinal))
        {
            log.Message = entry.Message;
        }
    }

    private static bool MatchesFilter(
        LogEntryRecord log,
        string selectedLevel,
        LogSourceFilter selectedFilter,
        string searchText,
        bool hasSearchText,
        string cartonSourceDisplayName,
        string singBoxSourceDisplayName)
    {
        var levelMatched = selectedLevel == "All" ||
                           string.Equals(log.Level, selectedLevel, StringComparison.OrdinalIgnoreCase);
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
