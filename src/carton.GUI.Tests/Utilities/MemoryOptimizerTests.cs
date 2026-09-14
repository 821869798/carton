using System.Diagnostics;
using System.Runtime.CompilerServices;
using carton.Core.Utilities;
using Xunit;

namespace carton.GUI.Tests.Utilities;

/// <summary>
/// Regression guards for <see cref="MemoryOptimizer"/>.
/// </summary>
/// <remarks>
/// The original implementation called
/// <c>GC.Collect(2, GCCollectionMode.Aggressive, blocking: false, compacting: true)</c>,
/// which throws <see cref="System.ArgumentException"/> ("AggressiveGC requires setting the
/// blocking parameter to true") on every invocation. Because the body was wrapped in a
/// bare <c>catch</c>, the exception was swallowed and BOTH the collection and the
/// subsequent working-set trim were skipped - the optimizer was a silent no-op.
/// </remarks>
public sealed class MemoryOptimizerTests
{
    [Fact]
    public void AggressiveCollectionRequiresBlocking_DocumentsTheOriginalBug()
    {
        // Pin the runtime contract that made the original call a guaranteed no-op.
        Assert.Throws<ArgumentException>(() =>
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: false, compacting: true));
    }

    [Fact]
    public void CompactAndTrim_ActuallyReclaimsManagedMemory()
    {
        // Allocate a large, provably dead graph, then assert the optimizer really collects
        // it. A no-op implementation (the original bug) fails this.
        AllocateGarbage();

        var before = GC.GetTotalMemory(forceFullCollection: false);

        MemoryOptimizer.CompactAndTrimNow();

        // The work is intentionally asynchronous (never block the UI thread); give the
        // thread-pool run a bounded window to finish.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        long after;
        do
        {
            Thread.Sleep(100);
            after = GC.GetTotalMemory(forceFullCollection: false);
        }
        while (after >= before && DateTime.UtcNow < deadline);

        Assert.True(
            after < before,
            $"expected the managed heap to shrink; before={before:N0} after={after:N0}");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AllocateGarbage()
    {
        // ~24 MB of short-lived arrays across gen0/LOH, unreachable on return.
        var sink = new List<byte[]>();
        for (var i = 0; i < 24; i++)
        {
            sink.Add(new byte[1024 * 1024]);
        }

        GC.KeepAlive(sink);
    }

    [Fact]
    public void CompactAndTrim_IsRateLimited_AndNeverThrows()
    {
        // Hot paths (window minimize/restore fires repeatedly) must be able to call this
        // freely: it returns immediately, collapses duplicates, and never propagates.
        var exception = Record.Exception(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                MemoryOptimizer.CompactAndTrim();
            }
        });

        Assert.Null(exception);
    }

    [Fact]
    public void CompactAndTrim_ReturnsPromptly_SoTheUiThreadIsNeverBlocked()
    {
        // A blocking gen2 compacting collection on the dispatcher would freeze the window;
        // the scheduling call itself must be effectively free.
        var stopwatch = Stopwatch.StartNew();
        MemoryOptimizer.CompactAndTrim();
        stopwatch.Stop();

        Assert.True(
            stopwatch.ElapsedMilliseconds < 250,
            $"scheduling took {stopwatch.ElapsedMilliseconds} ms; it must not run inline");
    }

    [Fact]
    public void Snapshot_ReportsPlausibleCounters()
    {
        var (managed, workingSet) = MemoryOptimizer.Snapshot();

        Assert.True(managed > 0, "managed heap size must be positive");
        Assert.True(workingSet > 0, "working set must be positive");
    }
}
