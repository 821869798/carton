using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
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
    private LogEntryRecord[] _snapshotCache = Array.Empty<LogEntryRecord>();
    private bool _snapshotDirty = true;
    private int _pendingEntriesChanged;

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
            _snapshotDirty = true;
        }

        RaiseEntriesChanged();
    }

    public IReadOnlyList<LogEntryRecord> GetSnapshot()
    {
        lock (_syncRoot)
        {
            if (_snapshotDirty)
            {
                _snapshotCache = _entries.ToArray();
                _snapshotDirty = false;
            }

            return _snapshotCache;
        }
    }

    public void Clear()
    {
        lock (_syncRoot)
        {
            _entries.Clear();
            _snapshotDirty = true;
            _snapshotCache = Array.Empty<LogEntryRecord>();
        }

        RaiseEntriesChanged();
    }

    private static LogEntryRecord CreateEntry(string message, LogSource source)
    {
        var now = DateTime.Now.ToString("HH:mm:ss");
        var (time, level, parsedMessage) = source switch
        {
            LogSource.Carton => ParseCartonLog(message, now),
            LogSource.SingBox => ParseSingBoxLog(message, now),
            _ => (now, "Info", message)
        };

        if (parsedMessage.Length > MaxMessageLength)
        {
            parsedMessage = parsedMessage[..MaxMessageLength] + "...";
        }

        return new LogEntryRecord(time, source, level, parsedMessage);
    }

    private void RaiseEntriesChanged()
    {
        if (EntriesChanged == null)
        {
            return;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            EntriesChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (Interlocked.Exchange(ref _pendingEntriesChanged, 1) == 1)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _pendingEntriesChanged, 0);
            EntriesChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    private static (string Time, string Level, string Message) ParseCartonLog(string message, string currentTime)
    {
        if (string.IsNullOrEmpty(message))
        {
            return (currentTime, "Info", string.Empty);
        }

        if (message.StartsWith("[ERROR] ", StringComparison.OrdinalIgnoreCase))
        {
            return (currentTime, "Error", message["[ERROR] ".Length..]);
        }

        if (message.StartsWith("[WARN] ", StringComparison.OrdinalIgnoreCase) ||
            message.StartsWith("[WARNING] ", StringComparison.OrdinalIgnoreCase))
        {
            var prefixLength = message.StartsWith("[WARNING] ", StringComparison.OrdinalIgnoreCase)
                ? "[WARNING] ".Length
                : "[WARN] ".Length;
            return (currentTime, "Warn", message[prefixLength..]);
        }

        if (message.StartsWith("[DEBUG] ", StringComparison.OrdinalIgnoreCase))
        {
            return (currentTime, "Debug", message["[DEBUG] ".Length..]);
        }

        if (message.StartsWith("[INFO] ", StringComparison.OrdinalIgnoreCase))
        {
            return (currentTime, "Info", message["[INFO] ".Length..]);
        }

        return (currentTime, "Info", message);
    }

    private static (string Time, string Level, string Message) ParseSingBoxLog(string message, string currentTime)
    {
        var msg = message.IndexOf('\u001b') >= 0
            ? AnsiEscapeRegex.Replace(message, "")
            : message;

        var span = msg.AsSpan().TrimStart();
        var prefixedLevel = TryStripPrefixedLevel(ref span);
        var tokenIndex = 0;
        var position = 0;
        var timeTokenStart = -1;
        var timeTokenLength = 0;
        var payloadStart = -1;

        while (position < span.Length)
        {
            while (position < span.Length && span[position] == ' ')
            {
                position++;
            }

            if (position >= span.Length)
            {
                break;
            }

            var start = position;
            while (position < span.Length && span[position] != ' ')
            {
                position++;
            }

            var length = position - start;
            if (tokenIndex == 2)
            {
                timeTokenStart = start;
                timeTokenLength = length;
            }
            else if (tokenIndex == 3)
            {
                payloadStart = start;
                break;
            }

            tokenIndex++;
        }

        if (payloadStart < 0)
        {
            return (currentTime, prefixedLevel ?? "Info", span.ToString());
        }

        var time = ExtractTime(span, timeTokenStart, timeTokenLength, currentTime);

        var payload = span[payloadStart..].TrimStart();
        if (payload.Length == 0)
        {
            return (time, prefixedLevel ?? "Info", string.Empty);
        }

        var separatorIndex = payload.IndexOf(' ');
        ReadOnlySpan<char> levelToken;
        ReadOnlySpan<char> messagePart;
        if (separatorIndex < 0)
        {
            levelToken = payload;
            messagePart = ReadOnlySpan<char>.Empty;
        }
        else
        {
            levelToken = payload[..separatorIndex];
            messagePart = payload[(separatorIndex + 1)..].TrimStart();
        }

        var level = TryNormalizeSingBoxLevel(levelToken, out var parsedLevel)
            ? parsedLevel
            : prefixedLevel ?? "Info";

        if (messagePart.Length > 0 && messagePart[0] == '[')
        {
            var endIndex = messagePart.IndexOf(']');
            if (endIndex > 0)
            {
                messagePart = messagePart[(endIndex + 1)..].TrimStart();
            }
        }

        return (time, level, messagePart.ToString());
    }

    private static string? TryStripPrefixedLevel(ref ReadOnlySpan<char> span)
    {
        if (span.Length < 3 || span[0] != '[')
        {
            return null;
        }

        var endIndex = span.IndexOf(']');
        if (endIndex <= 1)
        {
            return null;
        }

        if (!TryNormalizeSingBoxLevel(span[1..endIndex], out var level))
        {
            return null;
        }

        span = span[(endIndex + 1)..].TrimStart();
        return level;
    }

    private static string ExtractTime(ReadOnlySpan<char> span, int start, int length, string fallback)
    {
        if (length < 8)
        {
            return fallback;
        }

        var token = span.Slice(start, length);

        // If token contains 'T' (e.g. "2026-03-25T14:30:45+08:00"), extract time after 'T'
        var tIndex = token.IndexOf('T');
        if (tIndex >= 0 && tIndex + 9 <= length)
        {
            return new string(token.Slice(tIndex + 1, 8));
        }

        // If first 8 chars look like a date (contain '-'), use fallback
        var first8 = token[..8];
        if (first8.IndexOf('-') >= 0)
        {
            return fallback;
        }

        return new string(first8);
    }

    private static bool TryNormalizeSingBoxLevel(ReadOnlySpan<char> value, out string level)
    {
        if (value.Equals("DEBUG", StringComparison.OrdinalIgnoreCase))
        {
            level = "Debug";
            return true;
        }

        if (value.Equals("WARN", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("WARNING", StringComparison.OrdinalIgnoreCase))
        {
            level = "Warn";
            return true;
        }

        if (value.Equals("ERROR", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("FATAL", StringComparison.OrdinalIgnoreCase))
        {
            level = "Error";
            return true;
        }

        if (value.Equals("INFO", StringComparison.OrdinalIgnoreCase))
        {
            level = "Info";
            return true;
        }

        level = "Info";
        return false;
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
