using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using carton.GUI.Models;

namespace carton.GUI.Services;

public sealed class LogStore
{
    private const int MaxEntries = 800;
    private const int MaxMessageLength = 2048;
    private static readonly Regex AnsiEscapeRegex = new(@"\e\[[0-9;]*[a-zA-Z]");
    private readonly LogRingBuffer _entries = new(MaxEntries);
    private readonly object _syncRoot = new();

    public event EventHandler? EntriesChanged;

    public void AddLog(string message)
    {
        AddLog(message, LogSource.Carton);
    }

    public void AddLog(string message, LogSource source)
    {
        var entry = CreateEntry(message, source);

        lock (_syncRoot)
        {
            _entries.Add(entry);
        }

        RaiseEntriesChanged();
    }

    public IReadOnlyList<LogEntryRecord> GetSnapshot()
    {
        lock (_syncRoot)
        {
            return _entries.ToArray();
        }
    }

    public void Clear()
    {
        lock (_syncRoot)
        {
            _entries.Clear();
        }

        RaiseEntriesChanged();
    }

    private static LogEntryRecord CreateEntry(string message, LogSource source)
    {
        var (time, level, parsedMessage) = source switch
        {
            LogSource.Carton => ParseCartonLog(message),
            LogSource.SingBox => ParseSingBoxLog(message),
            _ => (DateTime.Now.ToString("HH:mm:ss"), "Info", message)
        };

        if (parsedMessage.Length > MaxMessageLength)
        {
            parsedMessage = parsedMessage[..MaxMessageLength] + "...";
        }

        return new LogEntryRecord(time, source, level, parsedMessage);
    }

    private void RaiseEntriesChanged()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            EntriesChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        Dispatcher.UIThread.Post(() => EntriesChanged?.Invoke(this, EventArgs.Empty));
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
}

public readonly record struct LogEntryRecord(string Time, LogSource Source, string Level, string Message);

internal sealed class LogRingBuffer
{
    private LogEntryRecord[] _buffer;
    private int _start;
    private int _count;

    public LogRingBuffer(int capacity)
    {
        _buffer = new LogEntryRecord[Math.Max(1, capacity)];
    }

    public void Add(LogEntryRecord entry)
    {
        if (_count < _buffer.Length)
        {
            _buffer[(_start + _count) % _buffer.Length] = entry;
            _count++;
            return;
        }

        _buffer[_start] = entry;
        _start = (_start + 1) % _buffer.Length;
    }

    public LogEntryRecord[] ToArray()
    {
        var result = new LogEntryRecord[_count];
        for (var i = 0; i < _count; i++)
        {
            result[i] = _buffer[(_start + i) % _buffer.Length];
        }

        return result;
    }

    public void Clear()
    {
        Array.Clear(_buffer, 0, _buffer.Length);
        _start = 0;
        _count = 0;
    }
}
