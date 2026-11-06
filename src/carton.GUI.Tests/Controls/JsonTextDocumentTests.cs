using carton.GUI.Controls;
using Xunit;

namespace carton.GUI.Tests.Controls;

public class JsonTextDocumentTests
{
    [Theory]
    [InlineData(17)]
    [InlineData(53)]
    [InlineData(197)]
    public void IncrementalLinesAndDirtyStateMatchFullReference(int seed)
    {
        var random = new Random(seed);
        var expected = "{\r\n  \"a\": \"😀中文\"\n}";
        var saved = expected;
        var document = new JsonTextDocument(); document.Load(expected);
        var lines = new List<JsonLine>();
        for (var step = 0; step < 4000; step++)
        {
            var start = random.Next(expected.Length + 1);
            var count = random.Next(Math.Min(12, expected.Length - start) + 1);
            var insertion = new[] { "", "a", "\r", "\n", "\r\n", "\n\r", "\t", "中", "😀", "{}", "\n\n\n" }[random.Next(11)];
            document.Replace(start, count, insertion);
            expected = expected.Remove(start, count).Insert(start, insertion);
            Assert.Equal(expected, document.GetText());
            JsonSyntax.BuildLines(expected, lines, out var width);
            Assert.Equal(lines, document.Lines);
            Assert.Equal(width, document.LongestLineColumns);
            Assert.Equal(expected != saved, document.IsModified);
            if (step % 97 == 0) { document.MarkSaved(); saved = expected; Assert.False(document.IsModified); }
        }
    }

    [Fact]
    public void ValidationMarkerIsTransientAndDoesNotDirtyDocument()
    {
        var document = new JsonTextDocument(); document.Load("a\nb");
        document.SetErrorLine(1);
        Assert.Equal(1, document.ErrorLine);
        Assert.False(document.IsModified);
        document.Replace(0, 1, "x");
        Assert.Null(document.ErrorLine);
        document.SetErrorLine(0);
        document.Load("new");
        Assert.Null(document.ErrorLine);
        document.SetErrorLine(999);
        Assert.Null(document.ErrorLine);
    }

    [Fact]
    public void RevertingAnEditRestoresCleanState()
    {
        var document = new JsonTextDocument(); document.Load("abc\ndef");
        document.Replace(1, 1, "x"); Assert.True(document.IsModified);
        document.Replace(1, 1, "b"); Assert.False(document.IsModified);
        document.Replace(3, 0, "\r"); Assert.True(document.IsModified);
        document.Replace(3, 1, ""); Assert.False(document.IsModified);
    }
}
