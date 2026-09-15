using carton.ViewModels;
using Xunit;

namespace carton.GUI.Tests.ViewModels;

/// <summary>
/// Regression guard for the shared-node case: with the group re-entry guard removed, two
/// tests can own the same tag at once, and only the LAST finisher may clear its state.
/// </summary>
public class TestingTagRefCountsTests
{
    [Fact]
    public void SecondOwnerKeepsTheTagFlagged()
    {
        var tags = new TestingTagRefCounts();

        Assert.True(tags.Acquire("node-a"));            // 0 -> 1: UI shows "..."
        Assert.False(tags.Acquire("node-a"));           // second in-flight test, no UI change
        Assert.True(tags.Contains("node-a"));

        Assert.False(tags.Release("node-a"));           // first test finished, other still running
        Assert.True(tags.Contains("node-a"));
        Assert.Equal(1, tags.Count);

        Assert.True(tags.Release("node-a"));            // last one: UI clears
        Assert.False(tags.Contains("node-a"));
        Assert.Equal(0, tags.Count);
    }

    [Fact]
    public void ReleasingSomethingNotOwnedIsANoOp()
    {
        var tags = new TestingTagRefCounts();

        Assert.False(tags.Release("node-b"));
        Assert.Equal(0, tags.Count);
    }

    [Fact]
    public void CountTracksDistinctTags()
    {
        var tags = new TestingTagRefCounts();

        tags.Acquire("node-a");
        tags.Acquire("node-a");
        tags.Acquire("node-b");

        Assert.Equal(2, tags.Count);
    }

    [Fact]
    public void ClearDropsEveryOwner()
    {
        var tags = new TestingTagRefCounts();
        tags.Acquire("node-a");
        tags.Acquire("node-a");
        tags.Acquire("node-b");

        tags.Clear();

        Assert.Equal(0, tags.Count);
        Assert.False(tags.Contains("node-a"));
        Assert.False(tags.Release("node-a"));
    }

    [Fact]
    public void TagMatchingIgnoresCase()
    {
        var tags = new TestingTagRefCounts();

        Assert.True(tags.Acquire("Node-A"));      // 0 -> 1
        Assert.True(tags.Contains("node-a"));
        Assert.True(tags.Release("NODE-A"));      // 1 -> 0: no double counting across cases
        Assert.False(tags.Contains("node-a"));
        Assert.Equal(0, tags.Count);
    }
}
