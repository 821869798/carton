namespace carton.ViewModels;

/// <summary>
/// What the delay cell is currently showing. Text AND colour must both come from
/// this one state - keeping two independent if-chains is exactly how the cell ended
/// up rendering "..." in the pink Timeout colour.
/// </summary>
public enum DelayState
{
    None,
    Testing,
    Timeout,
    Latency,
}

/// <summary>
/// Delay-cell text for a proxy row. Kept pure (no Avalonia types) so carton.GUI.Tests
/// can link this file, the same way LogParser / AppLaunchOptions are linked.
/// </summary>
public static class DelayText
{
    /// <summary>
    /// An already-arrived delay wins over every other state. The kernel pushes each
    /// node's own result the moment its test finishes, but a group test stays "in
    /// flight" until EVERY member reported - or until the wait budget expires, because
    /// a failing node that never had a history emits no event at all. Letting the
    /// testing dots win for that whole window is what made a finished test look like
    /// it returned nothing (the official dashboard / Windows client just shows
    /// whatever the groups stream last delivered).
    ///
    /// "Testing" also outranks "Timeout": the merge path marks a zero-delay node as
    /// timed out as soon as it is being tested, so a node can carry IsDelayTimeout
    /// the whole time its own test is still running (see ApplyUrlTestTimeoutState).
    /// </summary>
    public static DelayState State(int delayMs, bool isTesting, bool isTimeout)
        => delayMs > 0 ? DelayState.Latency
            : isTesting ? DelayState.Testing
            : isTimeout ? DelayState.Timeout
            : DelayState.None;

    public static string Text(DelayState state, int delayMs)
        => state switch
        {
            DelayState.Latency => $"{delayMs}ms",
            DelayState.Testing => "...",
            DelayState.Timeout => "Timeout",
            _ => string.Empty,
        };

    public static bool ShowText(DelayState state) => state != DelayState.None;
}
