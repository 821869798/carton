using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using carton.GUI.Models;

namespace carton.ViewModels;

public partial class LogsViewModel : PageViewModelBase
{
    private bool _isOnPage;
    private bool _isWindowVisible = true;
    private bool _hasPendingVisibleRefresh;

    public override NavigationPage PageType => NavigationPage.Logs;

    [ObservableProperty]
    private ObservableCollection<LogEntryViewModel> _logs = new();

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _selectedLevel = "All";

    [ObservableProperty]
    private LogEntryViewModel? _selectedLog;

    public ObservableCollection<string> LogLevels { get; } = new() { "All", "Debug", "Info", "Warn", "Error" };
    private readonly List<LogEntryViewModel> _allLogs = new();

    // Keep a large rolling window for troubleshooting while preventing unbounded growth.
    private const int MaxLogEntries = 2000;

    public LogsViewModel()
    {
        Title = "Logs";
        Icon = "Logs";
    }

    public void OnNavigatedTo()
    {
        _isOnPage = true;
        RefreshVisibleLogsIfNeeded();
    }

    public void SetWindowVisible(bool isVisible)
    {
        _isWindowVisible = isVisible;
        if (isVisible)
        {
            RefreshVisibleLogsIfNeeded();
        }
    }

    public void OnNavigatedFrom()
    {
        _isOnPage = false;
    }

    private void RefreshVisibleLogsIfNeeded()
    {
        if (_isOnPage && _isWindowVisible && _hasPendingVisibleRefresh)
        {
            _hasPendingVisibleRefresh = false;
            ApplyFilters();
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        ApplyFilters();
    }

    partial void OnSelectedLevelChanged(string value)
    {
        ApplyFilters();
    }

    public void AddLog(string message)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => AddLog(message));
            return;
        }

        // Strip ANSI escape sequences
        var msg = System.Text.RegularExpressions.Regex.Replace(message, @"\e\[[0-9;]*[a-zA-Z]", "");

        // Remove sing-box timestamp: "+0800 2026-03-09 10:46:36 " or "2024-03-15T12:00:00.000Z "
        var tsRegex = new System.Text.RegularExpressions.Regex(@"^([+-]\d{4}\s+)?\d{4}-\d{2}-\d{2}[T\s]\d{2}:\d{2}:\d{2}(\.\d+[Zz]?)?\s+");
        msg = tsRegex.Replace(msg, "");

        var level = "Info";

        // Extract and remove level
        var levelRegex = new System.Text.RegularExpressions.Regex(@"^\[?(DEBUG|INFO|WARN|WARNING|ERROR|FATAL|debug|info|warn|warning|error|fatal)\]?[\s:]+");
        var levelMatch = levelRegex.Match(msg);
        if (levelMatch.Success)
        {
            var l = levelMatch.Groups[1].Value;
            if (l.Equals("WARNING", StringComparison.OrdinalIgnoreCase)) l = "Warn";
            level = l.Length > 0 ? char.ToUpper(l[0]) + l.Substring(1).ToLower() : level;
            msg = msg.Substring(levelMatch.Length);
        }
        else
        {
            // Fallback content scan
            if (msg.Contains("ERROR", StringComparison.OrdinalIgnoreCase) || msg.Contains("[error]", StringComparison.OrdinalIgnoreCase))
            {
                level = "Error";
            }
            else if (msg.Contains("WARN", StringComparison.OrdinalIgnoreCase) || msg.Contains("[warn]", StringComparison.OrdinalIgnoreCase))
            {
                level = "Warn";
            }
            else if (msg.Contains("DEBUG", StringComparison.OrdinalIgnoreCase) || msg.Contains("[debug]", StringComparison.OrdinalIgnoreCase))
            {
                level = "Debug";
            }
        }

        // Extract and remove connection context ID, e.g., "[1651110515 5.0s] " or "[1234] "
        var contextRegex = new System.Text.RegularExpressions.Regex(@"^\[\d+(?:\s+[^\]]+)?\]\s*");
        msg = contextRegex.Replace(msg, "");

        var entry = new LogEntryViewModel
        {
            Time = DateTime.Now.ToString("HH:mm:ss"),
            Level = level,
            Message = msg
        };

        _allLogs.Add(entry);
        if (_isOnPage && _isWindowVisible && MatchesFilter(entry))
        {
            Logs.Add(entry);
        }
        else if (!_isOnPage || !_isWindowVisible)
        {
            _hasPendingVisibleRefresh = true;
        }

        while (_allLogs.Count > MaxLogEntries)
        {
            var removed = _allLogs[0];
            _allLogs.RemoveAt(0);
            if (_isOnPage && _isWindowVisible)
            {
                Logs.Remove(removed);
            }
            else
            {
                _hasPendingVisibleRefresh = true;
            }
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
        await CopyTextToClipboardAsync(line);
    }

    [RelayCommand]
    private async Task CopyAllLogs()
    {
        if (Logs.Count == 0) return;

        var sb = new StringBuilder();
        foreach (var log in Logs)
        {
            sb.Append('[').Append(log.Time).Append("] [").Append(log.Level).Append("] ").Append(log.Message).AppendLine();
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

        if (string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        return log.Message.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
               log.Level.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
               log.Time.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
    }
}

public partial class LogEntryViewModel : ObservableObject
{
    [ObservableProperty]
    private string _time = string.Empty;

    [ObservableProperty]
    private string _level = string.Empty;

    [ObservableProperty]
    private string _message = string.Empty;
}
