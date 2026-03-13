using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using carton.GUI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using carton.GUI.Models;

namespace carton.ViewModels;

public partial class LogsViewModel : PageViewModelBase
{
    private bool _isOnPage;
    private bool _isWindowVisible = true;
    private bool _hasPendingVisibleRefresh;
    private readonly ILocalizationService _localizationService;
    private static readonly Regex AnsiEscapeRegex = new(@"\e\[[0-9;]*[a-zA-Z]");

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

    public ObservableCollection<string> LogLevels { get; } = new() { "All", "Debug", "Info", "Warn", "Error" };
    public ObservableCollection<LogSourceFilterOptionViewModel> LogSourceFilters { get; } = new();
    private readonly FixedLogBuffer _allLogs = new(MaxVisibleLogEntries);

    // Keep a smaller rolling window when hidden so background log bursts do not keep growing carton memory.
    private const int MaxVisibleLogEntries = 800;
    private const int MaxHiddenLogEntries = 300;
    private const int MaxMessageLength = 2048;

    public LogsViewModel()
    {
        Title = "Logs";
        Icon = "Logs";
        _localizationService = LocalizationService.Instance;
        InitializeSourceFilters();
        _localizationService.LanguageChanged += (_, _) => RefreshLocalizedText();
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

    public void AddLog(string message)
    {
        AddLog(message, LogSource.Carton);
    }

    public void AddLog(string message, LogSource source)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => AddLog(message, source));
            return;
        }

        var (time, level, msg) = source switch
        {
            LogSource.Carton => ParseCartonLog(message),
            LogSource.SingBox => ParseSingBoxLog(message),
            _ => (DateTime.Now.ToString("HH:mm:ss"), "Info", message)
        };

        if (msg.Length > MaxMessageLength)
        {
            msg = msg[..MaxMessageLength] + "...";
        }

        var entry = new LogEntryViewModel
        {
            Time = time,
            Source = source,
            SourceDisplayName = GetSourceDisplayName(source),
            Level = level,
            Message = msg
        };

        var removed = _allLogs.Add(entry);
        if (removed != null)
        {
            if (Logs.Contains(removed))
            {
                Logs.Remove(removed);
            }
            else
            {
                _hasPendingVisibleRefresh = true;
            }
        }

        if (_isOnPage && _isWindowVisible && MatchesFilter(entry))
        {
            Logs.Add(entry);
        }
        else if (!_isOnPage || !_isWindowVisible)
        {
            _hasPendingVisibleRefresh = true;
        }

        TrimRetainedLogs();
    }

    private static (string Time, string Level, string Message) ParseCartonLog(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return (DateTime.Now.ToString("HH:mm:ss"), "Info", string.Empty);
        }

        if (message.StartsWith("[ERROR] ", StringComparison.OrdinalIgnoreCase))
        {
            return (DateTime.Now.ToString("HH:mm:ss"), "Error", message["[ERROR] ".Length..]);
        }

        if (message.StartsWith("[WARN] ", StringComparison.OrdinalIgnoreCase) ||
            message.StartsWith("[WARNING] ", StringComparison.OrdinalIgnoreCase))
        {
            var prefixLength = message.StartsWith("[WARNING] ", StringComparison.OrdinalIgnoreCase)
                ? "[WARNING] ".Length
                : "[WARN] ".Length;
            return (DateTime.Now.ToString("HH:mm:ss"), "Warn", message[prefixLength..]);
        }

        if (message.StartsWith("[DEBUG] ", StringComparison.OrdinalIgnoreCase))
        {
            return (DateTime.Now.ToString("HH:mm:ss"), "Debug", message["[DEBUG] ".Length..]);
        }

        if (message.StartsWith("[INFO] ", StringComparison.OrdinalIgnoreCase))
        {
            return (DateTime.Now.ToString("HH:mm:ss"), "Info", message["[INFO] ".Length..]);
        }

        return (DateTime.Now.ToString("HH:mm:ss"), "Info", message);
    }

    private static (string Time, string Level, string Message) ParseSingBoxLog(string message)
    {
        var msg = AnsiEscapeRegex.Replace(message, "");
        var parts = msg.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4)
        {
            return (DateTime.Now.ToString("HH:mm:ss"), "Info", msg);
        }

        var time = parts[2].Length >= 8 ? parts[2][..8] : DateTime.Now.ToString("HH:mm:ss");
        var remainder = parts[3];
        var levelSplit = remainder.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (levelSplit.Length == 0)
        {
            return (time, "Info", string.Empty);
        }

        var level = NormalizeSingBoxLevel(levelSplit[0]);
        msg = levelSplit.Length > 1 ? levelSplit[1] : string.Empty;

        if (msg.Length > 0 && msg[0] == '[')
        {
            var endIndex = msg.IndexOf(']');
            if (endIndex > 0)
            {
                msg = msg[(endIndex + 1)..].TrimStart();
            }
        }

        return (time, level, msg);
    }

    private static string NormalizeSingBoxLevel(string value)
    {
        return value.ToUpperInvariant() switch
        {
            "DEBUG" => "Debug",
            "WARN" or "WARNING" => "Warn",
            "ERROR" or "FATAL" => "Error",
            _ => "Info"
        };
    }

    private void TrimRetainedLogs()
    {
        var limit = _isOnPage && _isWindowVisible ? MaxVisibleLogEntries : MaxHiddenLogEntries;
        if (_allLogs.Capacity != limit)
        {
            _allLogs.Resize(limit);
            _hasPendingVisibleRefresh = true;
        }

        if (_isOnPage && _isWindowVisible)
        {
            ApplyFilters();
        }
    }

    [RelayCommand]
    private void ClearLogs()
    {
        _allLogs.Clear();
        Logs.Clear();
        SelectedLog = null;
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

    private static async Task CopyTextToClipboardAsync(string text)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop ||
            desktop.MainWindow?.Clipboard == null)
        {
            return;
        }

        await desktop.MainWindow.Clipboard.SetTextAsync(text);
    }

    private void ApplyFilters()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(ApplyFilters);
            return;
        }

        var filtered = _allLogs.Where(MatchesFilter).ToList();

        Logs.Clear();
        foreach (var log in filtered)
        {
            Logs.Add(log);
        }

        if (SelectedLog != null && !Logs.Contains(SelectedLog))
        {
            SelectedLog = null;
        }
    }

    private bool MatchesFilter(LogEntryViewModel log)
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

        return log.Message.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
               log.SourceDisplayName.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
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

    private void RefreshLocalizedText()
    {
        UpdateSourceFilterDisplayNames();
        foreach (var log in _allLogs)
        {
            log.SourceDisplayName = GetSourceDisplayName(log.Source);
        }
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
}

internal sealed class FixedLogBuffer : IEnumerable<LogEntryViewModel>
{
    private LogEntryViewModel[] _buffer;
    private int _start;
    private int _count;

    public FixedLogBuffer(int capacity)
    {
        _buffer = new LogEntryViewModel[Math.Max(1, capacity)];
    }

    public int Count => _count;
    public int Capacity => _buffer.Length;

    public LogEntryViewModel? Add(LogEntryViewModel entry)
    {
        if (_count < _buffer.Length)
        {
            _buffer[(_start + _count) % _buffer.Length] = entry;
            _count++;
            return null;
        }

        var removed = _buffer[_start];
        _buffer[_start] = entry;
        _start = (_start + 1) % _buffer.Length;
        return removed;
    }

    public void Clear()
    {
        Array.Clear(_buffer, 0, _buffer.Length);
        _start = 0;
        _count = 0;
    }

    public void Resize(int capacity)
    {
        var newCapacity = Math.Max(1, capacity);
        if (newCapacity == _buffer.Length)
        {
            return;
        }

        var newBuffer = new LogEntryViewModel[newCapacity];
        var copyCount = Math.Min(_count, newCapacity);
        var skip = _count - copyCount;
        for (var i = 0; i < copyCount; i++)
        {
            newBuffer[i] = this[skip + i];
        }

        _buffer = newBuffer;
        _start = 0;
        _count = copyCount;
    }

    private LogEntryViewModel this[int index] => _buffer[(_start + index) % _buffer.Length];

    public IEnumerator<LogEntryViewModel> GetEnumerator()
    {
        for (var i = 0; i < _count; i++)
        {
            yield return this[i];
        }
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
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
