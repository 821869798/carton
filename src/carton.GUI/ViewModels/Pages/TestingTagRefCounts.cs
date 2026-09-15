using System;
using System.Collections.Generic;

namespace carton.ViewModels;

/// <summary>
/// Ref-counted "this outbound tag is being tested" set, kept pure (no Avalonia types) so
/// carton.GUI.Tests can link this file the way LogParser / AppLaunchOptions / DelayText are.
///
/// A tag is shared often: two policy groups can contain the same node, and the node's own
/// delay test can run while a group test is still in flight. A plain set cannot express
/// "another test still owns this" - the first finisher would clear the state and the node
/// would drop its "..." (and get marked Timeout by the merge path) while its test is still
/// running. Both <see cref="Acquire"/> and <see cref="Release"/> report whether the visible
/// 0 &lt;-&gt; 1 transition happened, which is the only time the UI needs touching.
/// </summary>
internal sealed class TestingTagRefCounts
{
    private readonly Dictionary<string, int> _counts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Number of distinct tags currently owned by at least one test.</summary>
    public int Count => _counts.Count;

    public bool Contains(string tag) => _counts.TryGetValue(tag, out var count) && count > 0;

    /// <summary>Adds one owner. Returns true when this was the first owner (0 -&gt; 1).</summary>
    public bool Acquire(string tag)
    {
        var count = _counts.GetValueOrDefault(tag) + 1;
        _counts[tag] = count;
        return count == 1;
    }

    public void Clear() => _counts.Clear();

    /// <summary>Removes one owner. Returns true when the last owner left (1 -&gt; 0).</summary>
    public bool Release(string tag)
    {
        if (!_counts.TryGetValue(tag, out var count))
        {
            return false;
        }

        if (count > 1)
        {
            _counts[tag] = count - 1;
            return false;
        }

        _counts.Remove(tag);
        return true;
    }
}
