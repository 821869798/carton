using carton.GUI.Controls;
using Xunit;

namespace carton.GUI.Tests.Controls;

public class JsonIndentationTests
{
    private static JsonIndentation.NewLineEdit Create(string text, int start, int length)
    {
        var lines = new List<JsonLine>();
        JsonSyntax.BuildLines(text, lines, out _);
        var index = lines.FindLastIndex(line => line.StartOffset <= start);
        return JsonIndentation.Create(text, start, length, lines, index);
    }

    [Fact]
    public void HugeCurrentLineFallsBackWithoutScanningItsPrefix()
    {
        var text = new string('x', 2_000_000) + "{}";
        var lines = new[] { new JsonLine(0, text.Length) };
        var edit = JsonIndentation.Create(text, text.Length - 1, 0, lines, 0);
        Assert.Equal(Environment.NewLine, edit.Text);
    }

    [Fact]
    public void HugeDocumentReadsOnlyNearbyLineEntries()
    {
        var text = "{\n    \"a\": {}\n}";
        var lines = new List<JsonLine>();
        JsonSyntax.BuildLines(text, lines, out _);
        var indexed = new CountingLines(lines, 1_000_000);
        var edit = JsonIndentation.Create(text, text.IndexOf("{}") + 1, 0, indexed, 500_000);
        Assert.Equal("\n        \n    ", edit.Text);
        Assert.True(indexed.Reads <= 70, $"Read {indexed.Reads} line entries");
    }

    private sealed class CountingLines(List<JsonLine> source, int count) : IReadOnlyList<JsonLine>
    {
        public int Reads;
        public int Count => count;
        public JsonLine this[int index] { get { Reads++; return source[index == 500_000 ? 1 : 0]; } }
        public IEnumerator<JsonLine> GetEnumerator() => throw new InvalidOperationException("Must not enumerate document");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Theory]
    [InlineData("{\n    \"a\": 1,|\n}", "{\n    \"a\": 1,\n    |\n}")]
    [InlineData("{\n\t\"a\": 1,|\n}", "{\n\t\"a\": 1,\n\t|\n}")]
    [InlineData("{\n    \"a\": {|}\n}", "{\n    \"a\": {\n        |\n    }\n}")]
    [InlineData("{\n\t\"a\": [|]\n}", "{\n\t\"a\": [\n\t\t|\n\t]\n}")]
    [InlineData("{\n  \"a\": {|  }\n}", "{\n  \"a\": {\n    |\n  }\n}")]
    [InlineData("{\r\n    \"a\": {|}\r\n}", "{\r\n    \"a\": {\r\n        |\r\n    }\r\n}")]
    [InlineData("{\n  \"a\": \"{|x\"\n}", "{\n  \"a\": \"{\n  |x\"\n}")]
    [InlineData("{\n  \"a\": \"\\\"{|x\"\n}", "{\n  \"a\": \"\\\"{\n  |x\"\n}")]
    [InlineData("|    {}\n", "\n|    {}\n")]
    [InlineData("{\n    |\"a\": 1\n}", "{\n    \n    |\"a\": 1\n}")]
    public void NewlineUsesSurroundingWhitespace(string input, string expected)
    {
        var caret = input.IndexOf('|');
        var text = input.Remove(caret, 1);
        var edit = Create(text, caret, 0);
        var actual = text.Remove(caret, edit.RemoveLength).Insert(caret, edit.Text);
        Assert.Equal(expected, actual.Insert(caret + edit.CaretOffset, "|"));
    }

    [Fact]
    public void EmptyDocumentUsesPlatformNewline()
    {
        var edit = Create("", 0, 0);
        Assert.Equal(Environment.NewLine, edit.Text);
    }

    [Fact]
    public void ReplacesSelectionAsOneEdit()
    {
        var text = "{\n    \"a\": {remove}\n}";
        var start = text.IndexOf("remove", StringComparison.Ordinal);
        var edit = Create(text, start, 6);
        Assert.Equal("{\n    \"a\": {\n        |\n    }\n}",
            text.Remove(start, edit.RemoveLength).Insert(start, edit.Text).Insert(start + edit.CaretOffset, "|"));
    }
}
