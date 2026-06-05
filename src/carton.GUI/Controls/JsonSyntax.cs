using System;
using System.Collections.Generic;
using System.Threading;

namespace carton.GUI.Controls;

public enum JsonTokenKind
{
    Plain,
    String,
    Property,
    Number,
    Keyword,
    Punctuation
}

public readonly record struct JsonToken(int Start, int Length, JsonTokenKind Kind);

public readonly record struct JsonLine(int StartOffset, int EndOffset);

/// <summary>
/// JSON 编辑器的纯文本算法：分行、词法着色、宽字符列宽换算。
/// 不依赖渲染/控件状态，方便单元测试覆盖（编辑器的崩溃多源于此处的边界处理）。
/// </summary>
public static class JsonSyntax
{
    private static readonly string[] Keywords = { "true", "false", "null" };

    // CJK 及常见全角字符在等宽字体下约占两个西文字符宽度。
    public static bool IsWideChar(char ch)
    {
        return ch >= 0x1100 &&
               (ch <= 0x115F ||                               // Hangul Jamo
                ch is >= (char)0x2E80 and <= (char)0xA4CF ||  // CJK 部首、假名、CJK 统一表意文字等
                ch is >= (char)0xAC00 and <= (char)0xD7A3 ||  // Hangul 音节
                ch is >= (char)0xF900 and <= (char)0xFAFF ||  // CJK 兼容表意
                ch is >= (char)0xFE30 and <= (char)0xFE4F ||  // CJK 兼容形式
                ch is >= (char)0xFF00 and <= (char)0xFF60 ||  // 全角 ASCII
                ch is >= (char)0xFFE0 and <= (char)0xFFE6);   // 全角符号
    }

    public static int DisplayWidth(char ch) => IsWideChar(ch) ? 2 : 1;

    public static bool IsNumberChar(char ch)
    {
        return char.IsDigit(ch) || ch is '.' or 'e' or 'E' or '+' or '-';
    }

    public static bool TryReadKeyword(string text, int index, out int length)
    {
        foreach (var keyword in Keywords)
        {
            if (text.AsSpan(index).StartsWith(keyword, StringComparison.Ordinal) &&
                (index + keyword.Length == text.Length || !char.IsLetterOrDigit(text[index + keyword.Length])))
            {
                length = keyword.Length;
                return true;
            }
        }

        length = 0;
        return false;
    }

    // 从开引号后的位置开始扫描，返回闭引号之后的下标；字符串未闭合时返回 text.Length。
    public static int ReadJsonString(string text, int index)
    {
        var escaped = false;
        while (index < text.Length)
        {
            var ch = text[index++];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (ch == '\\')
            {
                escaped = true;
                continue;
            }

            if (ch == '"')
            {
                break;
            }
        }

        return index;
    }

    public static bool IsLikelyPropertyName(string text, int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            index++;
        }

        return index < text.Length && text[index] == ':';
    }

    /// <summary>按行切分文本（兼容 \n 与 \r\n），并返回最长行的显示列宽（至少 1）。</summary>
    public static void BuildLines(string text, List<JsonLine> lines, out int longestColumns)
    {
        lines.Clear();
        var start = 0;
        var longest = 1;
        var lineColumns = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '\n')
            {
                var lineEnd = i > start && text[i - 1] == '\r' ? i - 1 : i;
                lines.Add(new JsonLine(start, lineEnd));
                if (lineColumns > longest)
                {
                    longest = lineColumns;
                }
                start = i + 1;
                lineColumns = 0;
            }
            else if (ch != '\r')
            {
                lineColumns += DisplayWidth(ch);
            }
        }

        lines.Add(new JsonLine(start, text.Length));
        if (lineColumns > longest)
        {
            longest = lineColumns;
        }

        longestColumns = longest;
    }

    /// <summary>
    /// 对 JSON 文本做轻量词法着色。<paramref name="cancellation"/> 用于后台任务的协作取消，
    /// 同步调用传 default 即可（每 64K 字符才检查一次，开销可忽略）。
    /// </summary>
    public static void Tokenize(string text, List<JsonToken> tokens, CancellationToken cancellation = default)
    {
        tokens.Clear();
        var index = 0;
        var checkpoint = 0;
        while (index < text.Length)
        {
            if (index - checkpoint >= 65_536)
            {
                if (cancellation.IsCancellationRequested) return;
                checkpoint = index;
            }

            var ch = text[index];
            if (ch == '"')
            {
                var start = index;
                index = ReadJsonString(text, index + 1);
                tokens.Add(new JsonToken(start, index - start, IsLikelyPropertyName(text, index) ? JsonTokenKind.Property : JsonTokenKind.String));
                continue;
            }

            if (char.IsDigit(ch) || ch == '-')
            {
                var start = index++;
                while (index < text.Length && IsNumberChar(text[index]))
                {
                    index++;
                }

                tokens.Add(new JsonToken(start, index - start, JsonTokenKind.Number));
                continue;
            }

            if (TryReadKeyword(text, index, out var keywordLength))
            {
                tokens.Add(new JsonToken(index, keywordLength, JsonTokenKind.Keyword));
                index += keywordLength;
                continue;
            }

            if (ch is '{' or '}' or '[' or ']' or ':' or ',')
            {
                tokens.Add(new JsonToken(index, 1, JsonTokenKind.Punctuation));
            }

            index++;
        }
    }

    /// <summary>
    /// 为每一行预计算「第一个可能与该行相交的 token 下标」，供绘制时从该处线性扫描。
    /// </summary>
    public static void BuildLineTokenIndex(
        IReadOnlyList<JsonLine> lines,
        IReadOnlyList<JsonToken> tokens,
        List<int> firstTokenIndex)
    {
        firstTokenIndex.Clear();
        if (firstTokenIndex.Capacity < lines.Count)
        {
            firstTokenIndex.Capacity = lines.Count;
        }

        var tokenIdx = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            while (tokenIdx < tokens.Count && tokens[tokenIdx].Start + tokens[tokenIdx].Length <= line.StartOffset)
            {
                tokenIdx++;
            }
            firstTokenIndex.Add(tokenIdx);
        }
    }

    // 将字符偏移换算成显示列（CJK/全角记 2 列），与渲染、extent 的列宽口径一致。
    public static int OffsetToDisplayColumn(string text, JsonLine line, int offset)
    {
        var end = Math.Clamp(offset, line.StartOffset, line.EndOffset);
        var columns = 0;
        for (var i = line.StartOffset; i < end; i++)
        {
            columns += DisplayWidth(text[i]);
        }

        return columns;
    }

    // 将显示列换算回字符偏移，落在宽字符中间时就近吸附到字符边界。
    public static int DisplayColumnToOffset(string text, JsonLine line, int targetColumn)
    {
        var columns = 0;
        for (var i = line.StartOffset; i < line.EndOffset; i++)
        {
            var w = DisplayWidth(text[i]);
            if (columns + w > targetColumn)
            {
                var distToStart = targetColumn - columns;
                var distToEnd = columns + w - targetColumn;
                return distToEnd < distToStart ? i + 1 : i;
            }

            columns += w;
        }

        return line.EndOffset;
    }
}
