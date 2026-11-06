using System;

namespace carton.GUI.Controls;

/// <summary>Read-only character access without materializing a mutable document.</summary>
public readonly struct JsonTextSource
{
    private readonly string? _text;
    private readonly JsonTextBuffer? _buffer;
    public JsonTextSource(string text) { _text = text; _buffer = null; }
    internal JsonTextSource(JsonTextBuffer buffer) { _text = null; _buffer = buffer; }
    public static implicit operator JsonTextSource(string text) => new(text);
    public int Length => _buffer?.Length ?? _text?.Length ?? 0;
    public char this[int index] => _buffer != null ? _buffer[index] : _text![index];
    public string Substring(int start, int length) => _buffer != null ? _buffer.Slice(start, length) : _text!.Substring(start, length);
    public bool Matches(int start, string value)
    {
        if (start < 0 || value.Length > Length - start) return false;
        for (var i = 0; i < value.Length; i++) if (this[start + i] != value[i]) return false;
        return true;
    }
    public bool RangeEquals(int first, int second, int count)
    {
        for (var i = 0; i < count; i++) if (this[first + i] != this[second + i]) return false;
        return true;
    }
    public override string ToString() => _buffer?.Snapshot() ?? _text ?? string.Empty;
}
