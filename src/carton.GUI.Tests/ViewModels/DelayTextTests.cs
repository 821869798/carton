using carton.ViewModels;
using Xunit;

namespace carton.GUI.Tests.ViewModels;

/// <summary>
/// Regression guards for the delay cell. It has to answer two questions from ONE
/// state - the text (<see cref="DelayText.Text"/>) and the colour
/// (<see cref="DelayState"/>) - because independent if-chains already produced both
/// "dots hide an arrived value" and "dots drawn in the pink Timeout colour".
/// </summary>
public class DelayTextTests
{
    private static DelayState State(int delay, bool isTesting, bool isTimeout)
        => DelayText.State(delay, isTesting, isTimeout);

    private static string Display(int delay, bool isTesting, bool isTimeout)
        => DelayText.Text(State(delay, isTesting, isTimeout), delay);

    [Fact]
    public void ArrivedDelayWinsOverTestingDots()
    {
        Assert.Equal(DelayState.Latency, State(274, isTesting: true, isTimeout: false));
        Assert.Equal("274ms", Display(274, isTesting: true, isTimeout: false));
        Assert.True(DelayText.ShowText(State(274, isTesting: true, isTimeout: false)));
    }

    [Fact]
    public void TestingWithoutValueShowsGreyDots()
    {
        Assert.Equal(DelayState.Testing, State(0, isTesting: true, isTimeout: false));
        Assert.Equal("...", Display(0, isTesting: true, isTimeout: false));
        Assert.True(DelayText.ShowText(State(0, isTesting: true, isTimeout: false)));
    }

    /// <summary>
    /// The merge path marks a zero-delay node as timed out the moment it is being
    /// tested (ApplyUrlTestTimeoutState), so this combination is the normal one
    /// during a test - and it must read as dots, not as a pink "Timeout".
    /// </summary>
    [Fact]
    public void TestingOutranksTimeoutMark()
    {
        Assert.Equal(DelayState.Testing, State(0, isTesting: true, isTimeout: true));
        Assert.Equal("...", Display(0, isTesting: true, isTimeout: true));
    }

    [Fact]
    public void TimeoutShownOnlyAfterTestingEnded()
    {
        Assert.Equal(DelayState.Timeout, State(0, isTesting: false, isTimeout: true));
        Assert.Equal("Timeout", Display(0, isTesting: false, isTimeout: true));
        Assert.True(DelayText.ShowText(State(0, isTesting: false, isTimeout: true)));
    }

    [Fact]
    public void IdleWithoutValueShowsNothing()
    {
        Assert.Equal(DelayState.None, State(0, isTesting: false, isTimeout: false));
        Assert.Equal(string.Empty, Display(0, isTesting: false, isTimeout: false));
        Assert.False(DelayText.ShowText(State(0, isTesting: false, isTimeout: false)));
    }
}
