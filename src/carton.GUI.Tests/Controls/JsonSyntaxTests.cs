using System.Collections.Generic;
using carton.GUI.Controls;
using Xunit;

namespace carton.GUI.Tests.Controls;

public class JsonSyntaxTests
{
    private static List<JsonLine> Lines(string text)
    {
        var lines = new List<JsonLine>();
        JsonSyntax.BuildLines(text, lines, out _);
        return lines;
    }

    private static List<JsonToken> Tokens(string text)
    {
        var tokens = new List<JsonToken>();
        JsonSyntax.Tokenize(text, tokens);
        return tokens;
    }

    // 模拟绘制时对每行所做的 token 裁剪：复刻 DrawText 整行路径，
    // 任何越界都会在这里以 ArgumentOutOfRangeException 暴露出来。
    private static void AssertTokensFitEveryLine(string text)
    {
        var lines = Lines(text);
        var tokens = Tokens(text);
        var firstTokenIndex = new List<int>();
        JsonSyntax.BuildLineTokenIndex(lines, tokens, firstTokenIndex);

        for (var li = 0; li < lines.Count; li++)
        {
            var line = lines[li];
            var lineLength = line.EndOffset - line.StartOffset;
            for (var i = firstTokenIndex[li]; i < tokens.Count; i++)
            {
                var token = tokens[i];
                if (token.Start >= line.EndOffset) break;

                var paintStart = System.Math.Max(token.Start, line.StartOffset) - line.StartOffset;
                var paintEnd = System.Math.Min(token.Start + token.Length, line.EndOffset) - line.StartOffset;
                if (paintEnd <= paintStart) continue;

                Assert.True(paintStart >= 0, $"paintStart<0 on line {li}");
                Assert.True(paintEnd <= lineLength, $"paintEnd>{lineLength} on line {li}");
            }
        }
    }

    // ---- 回归：删除闭合引号导致字符串 token 跨多行，曾触发渲染崩溃 ----

    [Fact]
    public void UnterminatedString_TokenStaysWithinEveryLine()
    {
        // 第一行字符串缺少闭合引号，词法会把它一直读到下一行的引号。
        var text = "{\n  \"name: \"value\",\n  \"port\": 1080\n}";
        AssertTokensFitEveryLine(text);
    }

    [Fact]
    public void TrailingOpenQuote_DoesNotOverflowLastLine()
    {
        var text = "{\n  \"key\": \"";
        AssertTokensFitEveryLine(text);
    }

    [Fact]
    public void DeletingQuotesProgressively_NeverOverflows()
    {
        // 模拟用户逐字符退格删除，确保中间每个状态都不越界。
        var full = "{\n  \"server\": \"127.0.0.1\",\n  \"port\": 1080\n}";
        for (var cut = full.Length; cut > 0; cut--)
        {
            AssertTokensFitEveryLine(full[..cut]);
        }
    }

    [Fact]
    public void EmptyText_ProducesSingleLineNoTokens()
    {
        Assert.Single(Lines(string.Empty));
        Assert.Empty(Tokens(string.Empty));
    }

    // ---- 词法着色 ----

    [Fact]
    public void PropertyName_DetectedByTrailingColon()
    {
        var tokens = Tokens("{ \"port\": 1080 }");
        Assert.Contains(tokens, t => t.Kind == JsonTokenKind.Property);
    }

    [Fact]
    public void StringValue_NotTreatedAsProperty()
    {
        var tokens = Tokens("{ \"host\": \"localhost\" }");
        Assert.Contains(tokens, t => t.Kind == JsonTokenKind.String);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("null")]
    public void Keywords_AreRecognized(string keyword)
    {
        var tokens = Tokens($"{{ \"k\": {keyword} }}");
        Assert.Contains(tokens, t => t.Kind == JsonTokenKind.Keyword && t.Length == keyword.Length);
    }

    [Fact]
    public void KeywordPrefix_InsideLargerWordIsNotKeyword()
    {
        // "nullable" 不应被识别为 null 关键字。
        var tokens = Tokens("nullable");
        Assert.DoesNotContain(tokens, t => t.Kind == JsonTokenKind.Keyword);
    }

    [Theory]
    [InlineData("1080")]
    [InlineData("-12")]
    [InlineData("3.14")]
    [InlineData("1e10")]
    public void Numbers_AreRecognized(string number)
    {
        var tokens = Tokens($"[{number}]");
        Assert.Contains(tokens, t => t.Kind == JsonTokenKind.Number);
    }

    [Fact]
    public void EscapedQuoteInsideString_DoesNotTerminateEarly()
    {
        // "a\"b" 是一个完整字符串，转义引号不应提前结束。
        var text = "\"a\\\"b\"";
        var tokens = Tokens(text);
        Assert.Single(tokens);
        Assert.Equal(text.Length, tokens[0].Length);
    }

    [Fact]
    public void Punctuation_EachBraceIsOneToken()
    {
        var tokens = Tokens("{}[],:");
        Assert.Equal(6, tokens.Count);
        Assert.All(tokens, t => Assert.Equal(JsonTokenKind.Punctuation, t.Kind));
    }

    // ---- 分行 ----

    [Fact]
    public void CrlfAndLf_BothSplitAndStripCarriageReturn()
    {
        var lines = Lines("a\r\nb\nc");
        Assert.Equal(3, lines.Count);
        // 第一行的 EndOffset 应落在 \r 之前。
        Assert.Equal(1, lines[0].EndOffset);
    }

    [Fact]
    public void TrailingNewline_ProducesEmptyFinalLine()
    {
        var lines = Lines("a\n");
        Assert.Equal(2, lines.Count);
        Assert.Equal(lines[1].StartOffset, lines[1].EndOffset);
    }

    [Fact]
    public void LongestColumns_CountsWideCharsAsTwo()
    {
        // "中文" = 4 列，"ab" = 2 列。
        JsonSyntax.BuildLines("中文\nab", new List<JsonLine>(), out var longest);
        Assert.Equal(4, longest);
    }

    // ---- 宽字符列宽换算 ----

    [Fact]
    public void WideChar_CountsAsTwoColumns()
    {
        Assert.True(JsonSyntax.IsWideChar('中'));
        Assert.Equal(2, JsonSyntax.DisplayWidth('中'));
        Assert.Equal(1, JsonSyntax.DisplayWidth('a'));
    }

    [Fact]
    public void OffsetToColumn_RoundTripsThroughColumnToOffset()
    {
        var text = "a中b文c";
        var line = new JsonLine(0, text.Length);
        for (var offset = 0; offset <= text.Length; offset++)
        {
            var col = JsonSyntax.OffsetToDisplayColumn(text, line, offset);
            var back = JsonSyntax.DisplayColumnToOffset(text, line, col);
            Assert.Equal(offset, back);
        }
    }

    [Fact]
    public void ColumnToOffset_SnapsToNearestCharBoundary()
    {
        var text = "中"; // 占 0..2 两列
        var line = new JsonLine(0, text.Length);
        // 落在宽字符正中（第 1 列）时，平局就近吸附到起始边界（offset 0）。
        Assert.Equal(0, JsonSyntax.DisplayColumnToOffset(text, line, 1));
        // 第 2 列正好是末尾边界。
        Assert.Equal(1, JsonSyntax.DisplayColumnToOffset(text, line, 2));
    }

    [Fact]
    public void OffsetToColumn_ClampsOutOfRangeOffset()
    {
        var text = "abc";
        var line = new JsonLine(0, text.Length);
        Assert.Equal(3, JsonSyntax.OffsetToDisplayColumn(text, line, 999));
        Assert.Equal(0, JsonSyntax.OffsetToDisplayColumn(text, line, -5));
    }

    // ---- 行 token 索引 ----

    [Fact]
    public void LineTokenIndex_HasOneEntryPerLine()
    {
        var text = "{\n  \"a\": 1,\n  \"b\": 2\n}";
        var lines = Lines(text);
        var firstTokenIndex = new List<int>();
        JsonSyntax.BuildLineTokenIndex(lines, Tokens(text), firstTokenIndex);
        Assert.Equal(lines.Count, firstTokenIndex.Count);
    }

    // ---- 垂直移动（上下方向键）以显示列为中介，跨含 CJK 的行保持列对齐 ----

    [Fact]
    public void VerticalMove_PreservesDisplayColumnAcrossWideCharLine()
    {
        // 第 0 行含 CJK，第 1 行纯 ASCII。光标在第 0 行 "中" 之后（显示列 2），
        // 下移到第 1 行应落在显示列 2 处（offset 2），而非按字符列得到的 offset 1。
        var text = "中x\nabcd";
        var lines = Lines(text);

        var caret = 1; // "中" 之后
        var displayColumn = JsonSyntax.OffsetToDisplayColumn(text, lines[0], caret);
        Assert.Equal(2, displayColumn);

        var target = JsonSyntax.DisplayColumnToOffset(text, lines[1], displayColumn);
        // lines[1] 从绝对 offset 3 开始（"中x\n"），显示列 2 → 'c' 之前 = 3 + 2 = 5。
        Assert.Equal(5, target);
        Assert.Equal('c', text[target]); // 与上方 "中" 右边缘视觉对齐
    }

    // ---- emoji / 代理对（星平面码点）按码点测量 ----

    [Fact]
    public void Emoji_CountsAsTwoColumns()
    {
        // 😀 = U+1F600，UTF-16 是代理对（2 个 char），显示占 2 列。
        var text = "😀";
        Assert.Equal(2, text.Length); // 确认是代理对
        var (w, charCount) = JsonSyntax.DisplayWidthAt(text, 0);
        Assert.Equal(2, w);
        Assert.Equal(2, charCount);
    }

    [Fact]
    public void Emoji_LongestColumnsCountsAsTwo()
    {
        JsonSyntax.BuildLines("😀\nab", new List<JsonLine>(), out var longest);
        Assert.Equal(2, longest);
    }

    [Fact]
    public void OffsetToColumn_SkipsBothSurrogateChars()
    {
        // "a😀b"：a=列0..1，😀=列1..3，b=列3..4。
        var text = "a😀b";
        var line = new JsonLine(0, text.Length);
        Assert.Equal(0, JsonSyntax.OffsetToDisplayColumn(text, line, 0)); // a 前
        Assert.Equal(1, JsonSyntax.OffsetToDisplayColumn(text, line, 1)); // 😀 前
        Assert.Equal(3, JsonSyntax.OffsetToDisplayColumn(text, line, 3)); // b 前（跳过 2 个 char）
        Assert.Equal(4, JsonSyntax.OffsetToDisplayColumn(text, line, 4)); // 行尾
    }

    [Fact]
    public void ColumnToOffset_SnapsToCodePointBoundaryNotSurrogateHalf()
    {
        var text = "a😀b";
        var line = new JsonLine(0, text.Length);
        // 落在 😀 占据的第 2 列正中（列 2）时，平局吸附到起始边界 offset 1（不会切到代理对中间）。
        Assert.Equal(1, JsonSyntax.DisplayColumnToOffset(text, line, 2));
        // 列 3 正好是 😀 之后、b 之前 = offset 3。
        Assert.Equal(3, JsonSyntax.DisplayColumnToOffset(text, line, 3));
    }

    [Fact]
    public void DisplayColumnToOffset_NeverReturnsLowSurrogateIndex()
    {
        // 任何目标列还原出的偏移都不能落在代理对的低位（index 2）。
        var text = "a😀b";
        var line = new JsonLine(0, text.Length);
        for (var col = 0; col <= 5; col++)
        {
            var offset = JsonSyntax.DisplayColumnToOffset(text, line, col);
            if (offset < text.Length)
            {
                Assert.False(char.IsLowSurrogate(text[offset]), $"col {col} 落在代理对低位");
            }
        }
    }

    // ---- 按行着色 TokenizeLine（可见行惰性着色路径）----

    private static List<JsonToken> LineTokens(string text, int lineIndex)
    {
        var lines = Lines(text);
        var buffer = new List<JsonToken>();
        JsonSyntax.TokenizeLine(text, lines[lineIndex], buffer);
        return buffer;
    }

    [Fact]
    public void TokenizeLine_EmitsAbsoluteOffsets()
    {
        var text = "{\n  \"port\": 1080\n}";
        var tokens = LineTokens(text, 1); // 第 1 行: '  "port": 1080'
        // 属性名 token 的起点应是该行内 '"' 的绝对偏移。
        var prop = tokens.Find(t => t.Kind == JsonTokenKind.Property);
        Assert.Equal('"', text[prop.Start]);
        Assert.Contains(tokens, t => t.Kind == JsonTokenKind.Number);
    }

    [Fact]
    public void TokenizeLine_TokensStayWithinLine()
    {
        var text = "{\n  \"a\": 1,\n  \"b\": 2\n}";
        var lines = Lines(text);
        var buffer = new List<JsonToken>();
        for (var li = 0; li < lines.Count; li++)
        {
            JsonSyntax.TokenizeLine(text, lines[li], buffer);
            foreach (var t in buffer)
            {
                Assert.True(t.Start >= lines[li].StartOffset);
                Assert.True(t.Start + t.Length <= lines[li].EndOffset);
            }
        }
    }

    [Fact]
    public void TokenizeLine_UnterminatedString_StopsAtLineEnd()
    {
        // 未闭合字符串只染到本行末，不溢出到下一行。
        var text = "\"abc\ndef";
        var lines = Lines(text);
        var buffer = new List<JsonToken>();
        JsonSyntax.TokenizeLine(text, lines[0], buffer);
        var str = buffer.Find(t => t.Kind is JsonTokenKind.String or JsonTokenKind.Property);
        Assert.Equal(0, str.Start);
        Assert.Equal(lines[0].EndOffset, str.Start + str.Length); // 止于行末
    }

    [Fact]
    public void TokenizeLine_MatchesWholeDocTokenizePerLine()
    {
        // 标准 JSON 下，按行着色应与全文着色的结果逐 token 等价。
        var text = "{\n  \"host\": \"x\",\n  \"n\": -3.5\n}";
        var lines = Lines(text);
        var whole = Tokens(text);
        var perLine = new List<JsonToken>();
        var buffer = new List<JsonToken>();
        foreach (var line in lines)
        {
            JsonSyntax.TokenizeLine(text, line, buffer);
            perLine.AddRange(buffer);
        }

        Assert.Equal(whole.Count, perLine.Count);
        for (var i = 0; i < whole.Count; i++)
        {
            Assert.Equal(whole[i], perLine[i]);
        }
    }

    [Fact]
    public void TokenizeLine_UnterminatedString_NotMisclassifiedByNextLineColon()
    {
        // 第 0 行是未闭合字符串，下一行以 ':' 开头。按行着色不得越过行尾去看那个 ':'，
        // 否则会把字符串误判成属性名（Property）。应保持 String。
        var text = "\"abc\n: 1";
        var lines = Lines(text);
        var buffer = new List<JsonToken>();
        JsonSyntax.TokenizeLine(text, lines[0], buffer);
        var str = buffer.Find(t => t.Kind is JsonTokenKind.String or JsonTokenKind.Property);
        Assert.Equal(JsonTokenKind.String, str.Kind);
    }

    [Fact]
    public void TokenizeLine_PropertyWithinLine_StillDetected()
    {
        // 同一行内闭合字符串后随 ':'，仍应判为 Property（确认行内判定不受上界限制误伤）。
        var text = "\"port\": 1";
        var lines = Lines(text);
        var buffer = new List<JsonToken>();
        JsonSyntax.TokenizeLine(text, lines[0], buffer);
        Assert.Contains(buffer, t => t.Kind == JsonTokenKind.Property);
    }
}
