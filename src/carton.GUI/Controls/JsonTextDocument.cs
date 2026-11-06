using System;
using System.Collections.Generic;

namespace carton.GUI.Controls;

/// <summary>Mutable editor document. Only explicit consumers request a full string snapshot.</summary>
public sealed class JsonTextDocument
{
    private JsonTextBuffer _buffer = new(string.Empty);
    private readonly List<JsonLine> _lines = new() { new(0, 0) };
    private readonly List<int> _widths = new() { 0 };
    private readonly List<JsonLine> _replacementLines = new();
    private readonly List<int> _replacementWidths = new();
    private string _saved = string.Empty;
    private int _differences;
    public event EventHandler? Changed;
    public event EventHandler? Reset;
    public event EventHandler? ValidationChanged;
    public int? ErrorLine { get; private set; }

    public void SetErrorLine(long? line)
    {
        var next = line.HasValue && line.Value >= 0 && line.Value < _lines.Count ? (int?)line.Value : null;
        if (ErrorLine == next) return;
        ErrorLine = next;
        ValidationChanged?.Invoke(this, EventArgs.Empty);
    }
    public JsonTextSource Text => new(_buffer);
    public IReadOnlyList<JsonLine> Lines => _lines;
    public int Length => _buffer.Length;
    public int LongestLineColumns { get; private set; } = 1;
    public long Version { get; private set; }
    public string GetText() => _buffer.Snapshot();

    public void Load(string text, string? savedText = null)
    {
        _buffer = new(text);
        _lines.Clear(); _widths.Clear();
        ParseLines(0, Length, _lines, _widths);
        UpdateLongest();
        _saved = savedText ?? text;
        _differences = string.Equals(text, _saved, StringComparison.Ordinal) ? 0 : -1;
        Version++;
        SetErrorLine(null);
        Reset?.Invoke(this, EventArgs.Empty);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void MarkSaved()
    {
        _saved = GetText();
        _differences = 0;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool IsModified
    {
        get
        {
            if (Length != _saved.Length) return true;
            if (_differences < 0)
            {
                _differences = 0;
                for (var i = 0; i < Length; i++) if (Text[i] != _saved[i]) _differences++;
            }
            return _differences != 0;
        }
    }

    public int FindLine(int offset)
    {
        if (offset < 0 || offset > Length) throw new ArgumentOutOfRangeException(nameof(offset));
        var lo = 0; var hi = _lines.Count - 1;
        while (lo < hi)
        {
            var mid = lo + (hi - lo + 1) / 2;
            if (_lines[mid].StartOffset <= offset) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    public bool Replace(int offset, int count, string inserted)
    {
        if (offset < 0 || count < 0 || offset > Length || count > Length - offset)
            throw new ArgumentOutOfRangeException(nameof(offset));
        ArgumentNullException.ThrowIfNull(inserted);
        if (count == inserted.Length && Text.Matches(offset, inserted)) return false;
        // Include adjacent characters/lines so edits that join/split CRLF are indexed correctly.
        var first = FindLine(Math.Max(0, offset - 1));
        var last = FindLine(Math.Min(Length, offset + count + 1));
        var start = _lines[first].StartOffset;
        var end = last + 1 < _lines.Count ? _lines[last + 1].StartOffset : Length;
        var delta = inserted.Length - count;
        if (_differences >= 0 && Length == _saved.Length && delta == 0)
        {
            for (var i = 0; i < count; i++)
            {
                if (Text[offset + i] != _saved[offset + i]) _differences--;
                if (inserted[i] != _saved[offset + i]) _differences++;
            }
        }
        else _differences = -1;
        _buffer.Replace(offset, count, inserted);
        _replacementLines.Clear(); _replacementWidths.Clear();
        ParseLines(start, end + delta, _replacementLines, _replacementWidths);
        // A suffix owns its final empty boundary line, so don't duplicate it.
        if (last + 1 < _lines.Count && _replacementLines.Count > 0 && _replacementLines[^1].StartOffset == end + delta)
        {
            _replacementLines.RemoveAt(_replacementLines.Count - 1);
            _replacementWidths.RemoveAt(_replacementWidths.Count - 1);
        }
        var removedMaximum = false;
        for (var i = first; i <= last; i++) if (_widths[i] == LongestLineColumns) removedMaximum = true;
        if (delta != 0)
        {
            for (var i = last + 1; i < _lines.Count; i++)
            {
                var line = _lines[i];
                _lines[i] = new(line.StartOffset + delta, line.EndOffset + delta);
            }
        }
        _lines.RemoveRange(first, last - first + 1);
        _widths.RemoveRange(first, last - first + 1);
        _lines.InsertRange(first, _replacementLines);
        _widths.InsertRange(first, _replacementWidths);
        var newMaximum = 1;
        foreach (var width in _replacementWidths) newMaximum = Math.Max(newMaximum, width);
        if (removedMaximum && newMaximum < LongestLineColumns) UpdateLongest();
        else LongestLineColumns = Math.Max(LongestLineColumns, newMaximum);
        Version++;
        SetErrorLine(null);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void ParseLines(int start, int end, List<JsonLine> lines, List<int> widths)
    {
        var lineStart = start;
        var columns = 0;
        var text = Text;
        for (var i = start; i < end; i++)
        {
            if (text[i] == '\n')
            {
                lines.Add(new(lineStart, i > lineStart && text[i - 1] == '\r' ? i - 1 : i));
                widths.Add(columns); columns = 0; lineStart = i + 1;
            }
            else if (text[i] != '\r')
            {
                var (width, chars) = JsonSyntax.DisplayWidthAt(text, i);
                columns += width; i += chars - 1;
            }
        }
        lines.Add(new(lineStart, end)); widths.Add(columns);
    }

    private void UpdateLongest()
    {
        LongestLineColumns = 1;
        foreach (var width in _widths) LongestLineColumns = Math.Max(LongestLineColumns, width);
    }
}
