using System;
using System.Collections.Generic;

namespace carton.GUI.Controls;

internal static class JsonIndentation
{
    // A newline must not inspect the whole document, including pathological single-line JSON.
    internal const int ContextLines = 32;
    internal const int MaxLineScan = 4096;
    private const int MaxIndent = 256;
    internal readonly record struct NewLineEdit(string Text, int CaretOffset, int RemoveLength);

    internal static NewLineEdit Create(JsonTextSource text, int start, int length, IReadOnlyList<JsonLine> lines, int lineIndex)
    {
        var line = lines[lineIndex];
        var indentLength = IndentLength(text, line);
        var indent = text.Substring(line.StartOffset, Math.Min(indentLength, start - line.StartOffset));
        var newline = Environment.NewLine;
        // Reuse indexed line boundaries rather than searching backwards through text.
        var newlineLine = lineIndex > 0 ? lines[lineIndex - 1] : line;
        if (newlineLine.EndOffset < text.Length)
            newline = text[newlineLine.EndOffset] == '\r' ? "\r\n" : "\n";

        var plain = new NewLineEdit(newline + indent, newline.Length + indent.Length, length);
        if (start - line.StartOffset > MaxLineScan || indentLength == MaxIndent) return plain;

        // JSON strings cannot legally span physical lines. Inspect only the current
        // line; incomplete/very long lines retain indentation without guessing braces.
        var quoted = false;
        var escaped = false;
        for (var i = line.StartOffset; i < start; i++)
        {
            var c = text[i];
            if (quoted && escaped) { escaped = false; continue; }
            if (quoted && c == '\\') { escaped = true; continue; }
            if (c == '"') quoted = !quoted;
        }
        var previous = start - 1;
        while (previous >= line.StartOffset && IsIndent(text[previous])) previous--;
        if (quoted || previous < line.StartOffset || (text[previous] != '{' && text[previous] != '[')) return plain;

        var insertion = newline + indent + InferUnit(text, lines, lineIndex);
        var next = start + length;
        var whitespaceEnd = Math.Min(text.Length, next + MaxIndent);
        while (next < whitespaceEnd && IsIndent(text[next])) next++;
        var closing = text[previous] == '{' ? '}' : ']';
        if (next < text.Length && text[next] == closing)
            return new(insertion + newline + indent, insertion.Length, next - start);
        return new(insertion, insertion.Length, length);
    }

    private static string InferUnit(JsonTextSource text, IReadOnlyList<JsonLine> lines, int current)
    {
        var previousStart = -1;
        var previousLength = 0;
        var bestStart = -1;
        var bestLength = 0;
        var bestDistance = int.MaxValue;
        var last = Math.Min(lines.Count - 1, current + ContextLines);
        for (var index = Math.Max(0, current - ContextLines); index <= last; index++)
        {
            var line = lines[index];
            var length = IndentLength(text, line);
            if (length == MaxIndent || line.StartOffset + length == line.EndOffset) continue;
            if (previousStart >= 0 && previousLength != length)
            {
                var shorterStart = length < previousLength ? line.StartOffset : previousStart;
                var longerStart = length > previousLength ? line.StartOffset : previousStart;
                var shorterLength = Math.Min(length, previousLength);
                var longerLength = Math.Max(length, previousLength);
                var distance = Math.Abs(index - current);
                if (distance < bestDistance && text.RangeEquals(longerStart, shorterStart, shorterLength))
                {
                    bestStart = longerStart + shorterLength;
                    bestLength = longerLength - shorterLength;
                    bestDistance = distance;
                }
            }
            previousStart = line.StartOffset;
            previousLength = length;
        }
        return bestStart >= 0 ? text.Substring(bestStart, bestLength) : "  ";
    }

    private static int IndentLength(JsonTextSource text, JsonLine line)
    {
        var end = Math.Min(line.EndOffset, line.StartOffset + MaxIndent);
        var i = line.StartOffset;
        while (i < end && IsIndent(text[i])) i++;
        return i - line.StartOffset;
    }

    private static bool IsIndent(char c) => c is ' ' or '\t';
}
