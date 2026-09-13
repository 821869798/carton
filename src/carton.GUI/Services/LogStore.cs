using System;
using System.Collections.Generic;
using System.Threading;
using Avalonia.Threading;
using carton.Core.Models;
using carton.GUI.Models;

namespace carton.GUI.Services;

public sealed class LogStore
{
    private const int MaxEntries = 800;
    private const int MaxMessageLength = 2048;
    private readonly LogRingBuffer _entries = new(MaxEntries);
    private readonly object _syncRoot = new();
    private readonly object _timeSyncRoot = new();
    private int _pendingEntriesChanged;
    private long _nextSequence;
    private long _resetEpoch;
    private long _cachedTimeSecond = -1;
    private string _cachedTimeText = string.Empty;

    public event EventHandler? EntriesChanged;

    public void AddLog(string message)
    {
        AddLog(message, LogSource.Carton);
    }

    public void AddLog(string message, LogSource source)
    {
        var entry = CreateEntry(message, source);
        AddEntry(entry);
    }

    /// <summary>
    /// Structured carton entry: severity comes as an enum - no "[WARN] " prefix
    /// parsing, no per-entry regex/StartsWith work on the hot logging path.
    /// </summary>
    public void AddLog(CartonLogEntry entry)
    {
        var message = entry.Message;
        if (message.Length > MaxMessageLength)
        {
            message = message[..MaxMessageLength] + "...";
        }

        // Enum.ToString() boxes and allocates per entry; the severity set is tiny and
        // fixed, so map it through constants instead (hot logging path). The enum's
        // numeric order IS the filter rank (Debug = 0 ... Error = 3; "Fatal" = 4 only
        // exists as a sing-box string level), so the rank casts straight through.
        var levelText = entry.Level switch
        {
            CartonLogLevel.Debug => "Debug",
            CartonLogLevel.Warn => "Warn",
            CartonLogLevel.Error => "Error",
            _ => "Info"
        };

        // The cast above relies on CartonLogLevel's numeric order matching
        // GetLevelRank's string ranks. Assert it once (Debug builds only) instead of
        // paying a per-entry switch on the hot path; if the enum ever changes order,
        // the debug assert fails immediately instead of silently filtering wrong.
        System.Diagnostics.Debug.Assert(
            (byte)entry.Level == GetLevelRank(levelText),
            "CartonLogLevel numeric values must match GetLevelRank's string ranks");

        AddEntry(new LogEntryRecord(0, GetCurrentTimeText(), LogSource.Carton, levelText, (byte)entry.Level, message));
    }

    public void AddSingBoxLog(KernelLogEntry log)
    {
        var entry = CreateSingBoxEntry(log);
        AddEntry(entry);
    }

    private void AddEntry(LogEntryRecord entry)
    {
        lock (_syncRoot)
        {
            entry = entry with { Sequence = ++_nextSequence };
            _entries.Add(entry);
        }

        RaiseEntriesChanged();
    }

    public void CopySnapshotTo(List<LogEntryRecord> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);

        lock (_syncRoot)
        {
            _entries.CopyTo(destination);
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

    /// <summary>
    /// Removes all buffered entries from the given source, e.g. when the sing-box
    /// kernel resets its log buffer and replays its saved history.
    /// </summary>
    public void RemoveSource(LogSource source)
    {
        lock (_syncRoot)
        {
            _entries.RemoveAll(entry => entry.Source == source);
            // Bump the reset epoch: sequence-based incremental consumers see a new
            // epoch and rebuild their view from the snapshot instead of blindly
            // appending the replayed history on top of stale rows.
            _resetEpoch++;
        }

        RaiseEntriesChanged();
    }

    /// <summary>
    /// Incremented every time the buffer is structurally reset (RemoveSource);
    /// consumers doing sequence-based incremental updates must fully rebuild when
    /// this changes. The global sequence itself never rewinds (post-reset entries
    // keep getting higher numbers than anything applied before).
    /// </summary>
    public long ResetEpoch
    {
        get
        {
            lock (_syncRoot)
            {
                return _resetEpoch;
            }
        }
    }

    private LogEntryRecord CreateEntry(string message, LogSource source)
    {
        var now = GetCurrentTimeText();
        var (time, level, parsedMessage) = source switch
        {
            LogSource.Carton => LogParser.ParseCartonLog(message, now),
            LogSource.SingBox => LogParser.ParseSingBoxLog(message, now),
            _ => (now, "Info", message)
        };

        if (parsedMessage.Length > MaxMessageLength)
        {
            parsedMessage = parsedMessage[..MaxMessageLength] + "...";
        }

        return new LogEntryRecord(0, time, source, level, GetLevelRank(level), parsedMessage);
    }

    private LogEntryRecord CreateSingBoxEntry(KernelLogEntry log)
    {
        if (string.IsNullOrWhiteSpace(log.Level))
        {
            return CreateEntry(log.Message, LogSource.SingBox);
        }

        var message = log.Message;
        if (message.Length > MaxMessageLength)
        {
            message = message[..MaxMessageLength] + "...";
        }

        return new LogEntryRecord(
            0,
            GetCurrentTimeText(),
            LogSource.SingBox,
            log.Level,
            GetLevelRank(log.Level),
            message);
    }

    private string GetCurrentTimeText()
    {
        var now = DateTime.Now;
        var second = now.Ticks / TimeSpan.TicksPerSecond;
        if (Volatile.Read(ref _cachedTimeSecond) == second)
        {
            return Volatile.Read(ref _cachedTimeText);
        }

        lock (_timeSyncRoot)
        {
            if (_cachedTimeSecond == second)
            {
                return _cachedTimeText;
            }

            _cachedTimeText = now.ToString("HH:mm:ss");
            Volatile.Write(ref _cachedTimeSecond, second);
            return _cachedTimeText;
        }
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

    /// <summary>
    /// Filter rank for a normalized level name (Debug = 0 ... Fatal = 4; "Trace" is
    /// the most verbose and ranks 0). Levels are normalized to capitalized names at
    /// parse time (LogParser / MapLogLevel / the carton enum switch), so an Ordinal
    /// switch suffices. Used when records are created (rank cached on the record so
    /// the hot filter path is a pure integer compare) and when the logs filter's
    /// selected level changes.
    /// </summary>
    internal static byte GetLevelRank(string? level)
    {
        return level switch
        {
            "Debug" => 0,
            "Trace" => 0,
            "Info" => 1,
            "Warn" => 2,
            "Error" => 3,
            "Fatal" => 4,
            _ => 1
        };
    }
}

public readonly record struct LogEntryRecord(long Sequence, string Time, LogSource Source, string Level, byte LevelRank, string Message);

internal sealed class LogRingBuffer
{
    private readonly LogEntryRecord[] _buffer;
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

    public void CopyTo(List<LogEntryRecord> destination)
    {
        destination.Clear();
        destination.EnsureCapacity(_count);
        for (var i = 0; i < _count; i++)
        {
            destination.Add(_buffer[(_start + i) % _buffer.Length]);
        }
    }

    public void RemoveAll(Predicate<LogEntryRecord> match)
    {
        var kept = new List<LogEntryRecord>(Math.Max(1, _count));
        for (var i = 0; i < _count; i++)
        {
            var entry = _buffer[(_start + i) % _buffer.Length];
            if (!match(entry))
            {
                kept.Add(entry);
            }
        }

        _start = 0;
        _count = 0;
        for (var i = 0; i < kept.Count && i < _buffer.Length; i++)
        {
            _buffer[i] = kept[i];
            _count++;
        }
    }

    public void Clear()
    {
        Array.Clear(_buffer, 0, _buffer.Length);
        _start = 0;
        _count = 0;
    }
}
