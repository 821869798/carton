using System;

namespace carton.GUI.Controls;

/// <summary>
/// Mutable UTF-16 storage with a gap at the most recent edit. Local typing allocates
/// no document-sized strings. Moving far away may copy characters inside the buffer;
/// exporting a snapshot is explicit and cached until the next change.
/// </summary>
internal sealed class JsonTextBuffer
{
    private char[] _buffer = Array.Empty<char>();
    private string? _original;
    private int _gapStart;
    private int _gapEnd;
    private string? _snapshot;

    internal JsonTextBuffer(string text)
    {
        _original = text;
        _snapshot = text;
    }

    internal int Length => _original?.Length ?? (_buffer.Length - (_gapEnd - _gapStart));
    internal long Version { get; private set; }
    internal char this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Length) throw new ArgumentOutOfRangeException(nameof(index));
            if (_original != null) return _original[index];
            return _buffer[index < _gapStart ? index : index + _gapEnd - _gapStart];
        }
    }

    internal bool Replace(int offset, int count, string inserted)
    {
        ValidateRange(offset, count);
        ArgumentNullException.ThrowIfNull(inserted);
        if (count == inserted.Length)
        {
            var same = true;
            for (var i = 0; i < count; i++)
                if (this[offset + i] != inserted[i]) { same = false; break; }
            if (same) return false;
        }
        // Reserve before mutating so allocation failure cannot silently discard text.
        EnsureMutable();
        EnsureGap(Math.Max(0, inserted.Length - count));
        MoveGap(offset);
        _gapEnd += count;
        inserted.CopyTo(0, _buffer, _gapStart, inserted.Length);
        _gapStart += inserted.Length;
        _snapshot = null;
        Version++;
        return true;
    }

    internal string Slice(int offset, int count)
    {
        ValidateRange(offset, count);
        if (_original != null) return _original.Substring(offset, count);
        return string.Create(count, (Buffer: this, Offset: offset), static (destination, state) =>
            state.Buffer.CopyTo(state.Offset, destination));
    }

    internal string Snapshot() => _snapshot ??= Slice(0, Length);

    private void CopyTo(int offset, Span<char> destination)
    {
        var before = Math.Min(destination.Length, Math.Max(0, _gapStart - offset));
        if (before > 0) _buffer.AsSpan(offset, before).CopyTo(destination);
        if (before < destination.Length)
            _buffer.AsSpan(offset + before + _gapEnd - _gapStart, destination.Length - before).CopyTo(destination[before..]);
    }

    private void EnsureMutable()
    {
        if (_original == null) return;
        var buffer = new char[checked(_original.Length + 1024)];
        _original.CopyTo(0, buffer, 0, _original.Length);
        _gapStart = _original.Length;
        _gapEnd = buffer.Length;
        _buffer = buffer;
        _original = null;
    }

    private void MoveGap(int offset)
    {
        if (offset < _gapStart)
        {
            var count = _gapStart - offset;
            Array.Copy(_buffer, offset, _buffer, _gapEnd - count, count);
            _gapEnd -= count;
        }
        else if (offset > _gapStart)
        {
            var count = offset - _gapStart;
            Array.Copy(_buffer, _gapEnd, _buffer, _gapStart, count);
            _gapEnd += count;
        }
        _gapStart = offset;
    }

    private void EnsureGap(int needed)
    {
        if (_gapEnd - _gapStart >= needed) return;
        var capacity = checked(Length + Math.Max(needed, Math.Max(1024, Length / 4)));
        var buffer = new char[capacity];
        Array.Copy(_buffer, 0, buffer, 0, _gapStart);
        var tail = _buffer.Length - _gapEnd;
        Array.Copy(_buffer, _gapEnd, buffer, capacity - tail, tail);
        _buffer = buffer;
        _gapEnd = capacity - tail;
    }

    private void ValidateRange(int offset, int count)
    {
        if (offset < 0 || count < 0 || offset > Length || count > Length - offset)
            throw new ArgumentOutOfRangeException(nameof(offset));
    }
}
