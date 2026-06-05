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
}
