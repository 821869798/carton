using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using carton.Core.Models;
using carton.GUI.Models;
using carton.GUI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace carton.ViewModels;

public partial class LogsViewModel : PageViewModelBase, IDisposable
{
    private bool _isOnPage;
    private bool _isWindowVisible = true;
    private bool _hasPendingVisibleRefresh;
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
        ApplyFilters();
    }

    partial void OnSelectedLevelChanged(string value)
    {
        ApplyFilters();
    }

    partial void OnSelectedSourceFilterChanged(LogSourceFilterOptionViewModel? value)
    {
        ApplyFilters();
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
            ApplyFilters();
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
            ApplyFilters();
        }
    }

    private void ReleaseVisibleLogs()
    {
        Logs.Clear();
        SelectedLog = null;
    }

    private void ApplyFilters()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(ApplyFilters);
            return;
        }

        var filtered = _logStore
            .GetSnapshot()
            .Where(MatchesFilter)
            .Select(entry => new LogEntryViewModel
            {
                Time = entry.Time,
                Source = entry.Source,
                SourceDisplayName = GetSourceDisplayName(entry.Source),
                Level = entry.Level,
                Message = entry.Message
            })
            .ToList();

        Logs.Clear();
        foreach (var log in filtered)
        {
            Logs.Add(log);
        }

        if (SelectedLog != null &&
            !Logs.Any(log =>
                log.Time == SelectedLog.Time &&
                log.Source == SelectedLog.Source &&
                log.Level == SelectedLog.Level &&
                log.Message == SelectedLog.Message))
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
        ApplyFilters();
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
