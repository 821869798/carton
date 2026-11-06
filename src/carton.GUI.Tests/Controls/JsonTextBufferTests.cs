using carton.GUI.Controls;
using Xunit;

namespace carton.GUI.Tests.Controls;

public class JsonTextBufferTests
{
    [Theory]
    [InlineData(7)]
    [InlineData(73)]
    [InlineData(7531)]
    public void RandomEditsAndSlicesMatchImmutableString(int seed)
    {
        var random = new Random(seed);
        var expected = "{\r\n\t\"名字\": \"😀\"\r\n}";
        var buffer = new JsonTextBuffer(expected);
        for (var step = 0; step < 5000; step++)
        {
            var offset = random.Next(expected.Length + 1);
            var count = random.Next(Math.Min(100, expected.Length - offset) + 1);
            var inserted = step % 100 == 0 ? new string('x', 3000) : new[] { "", "x", "\n", "\r\n", "中文", "😀", "\t{}" }[random.Next(7)];
            var next = expected.Remove(offset, count).Insert(offset, inserted);
            var version = buffer.Version;
            Assert.Equal(next != expected, buffer.Replace(offset, count, inserted));
            Assert.Equal(version + (next == expected ? 0 : 1), buffer.Version);
            expected = next;
            Assert.Equal(expected.Length, buffer.Length);
            Assert.Equal(expected, buffer.Snapshot());
            Assert.Same(buffer.Snapshot(), buffer.Snapshot());
            var sliceStart = random.Next(expected.Length + 1);
            var sliceLength = random.Next(expected.Length - sliceStart + 1);
            Assert.Equal(expected.Substring(sliceStart, sliceLength), buffer.Slice(sliceStart, sliceLength));
            if (expected.Length > 0)
            {
                var index = random.Next(expected.Length);
                Assert.Equal(expected[index], buffer[index]);
            }
        }
    }

    [Fact]
    public void LocalTypingDoesNotAllocateFullDocumentCopies()
    {
        var buffer = new JsonTextBuffer(new string('x', 3_600_000));
        buffer.Replace(buffer.Length, 0, "a"); // Warm method paths.
        var start = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 500; i++) buffer.Replace(buffer.Length, 0, "b");
        var allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.True(allocated < 1024, $"Local typing allocated {allocated} bytes");
        Assert.Equal(3_600_501, buffer.Length);
    }

    [Fact]
    public void ViewingSharesOriginalUntilFirstEdit()
    {
        var text = new string('x', 3_600_000);
        _ = new JsonTextBuffer("warmup");
        var start = GC.GetAllocatedBytesForCurrentThread();
        var buffer = new JsonTextBuffer(text);
        Assert.Equal('x', buffer[text.Length - 1]);
        Assert.Same(text, buffer.Snapshot());
        var allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.True(allocated < 1024, $"Viewing copied text: {allocated} bytes");
        buffer.Replace(text.Length, 0, "y");
        Assert.Equal('x', text[^1]);
        Assert.Equal('y', buffer[buffer.Length - 1]);
    }

    [Fact]
    public void BadRangesLeaveContentUntouched()
    {
        var buffer = new JsonTextBuffer("abc");
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.Replace(2, 2, "x"));
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.Replace(-1, 0, "x"));
        Assert.Equal("abc", buffer.Snapshot());
        Assert.Equal(0, buffer.Version);
    }
}
