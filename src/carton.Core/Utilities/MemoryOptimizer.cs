using System;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace carton.Core.Utilities;

/// <summary>
/// Proactive memory optimization utility: compacts the managed heaps and returns the
/// unreferenced physical working set to the operating system.
/// </summary>
/// <remarks>
/// <para>
/// A compacting gen2 collection is expensive (tens of milliseconds on a loaded heap) and
/// <see cref="GCCollectionMode.Aggressive"/> is only legal when <c>blocking</c> and
/// <c>compacting</c> are both <see langword="true"/> - passing <c>blocking: false</c>
/// throws <see cref="ArgumentException"/> and silently defeats the whole optimization.
/// Everything here therefore runs on a thread-pool thread and is rate limited, so the
/// calling thread returns immediately instead of waiting for the collection.
/// </para>
/// <para>
/// Off the caller's thread is not the same as free, though: a blocking collection
/// suspends every managed thread in the process - the UI thread included - for the length
/// of the pause, and the working-set trim makes the next paint or gRPC call fault pages
/// back in. Both costs are knowingly traded for a smaller idle footprint, which is the
/// whole point of trimming a background tray application.
/// </para>
/// </remarks>
public static partial class MemoryOptimizer
{
    /// <summary>Minimum wall-clock gap between two real trims.</summary>
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(20);

    private static long _lastTrimTicks = long.MinValue;
    private static int _trimInFlight;
    /// <summary>Set when a request arrived while a trim was already running.</summary>
    private static int _trimPending;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessWorkingSetSize(IntPtr proc, IntPtr min, IntPtr max);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();

    /// <summary>
    /// Schedules a compacting collection plus an OS working-set trim on a background thread.
    /// Returns immediately; safe to call from the UI thread and from hot paths.
    /// Repeated calls inside <see cref="MinimumInterval"/> collapse into a single trim.
    /// </summary>
    public static void CompactAndTrim() => Schedule(force: false);

    /// <summary>
    /// Same as <see cref="CompactAndTrim"/> but bypasses the rate limiter. Use only for
    /// genuinely rare, high-value moments: the kernel was stopped or restarted, the window
    /// was just hidden to the tray or minimized, or startup has settled.
    /// </summary>
    public static void CompactAndTrimNow() => Schedule(force: true);

    private static void Schedule(bool force)
    {
        if (!force && !TryReserveSlot())
        {
            return;
        }

        if (force)
        {
            Volatile.Write(ref _lastTrimTicks, DateTime.UtcNow.Ticks);
        }

        // Only ever one trim in flight: a queue of blocking gen2 collections would be
        // strictly worse than not trimming at all. A request that arrives mid-flight is
        // remembered (coalesced) rather than dropped, so a high-value trim - e.g. the
        // kernel stopping while the startup trim is still running - is never lost.
        if (Interlocked.CompareExchange(ref _trimInFlight, 1, 0) != 0)
        {
            Volatile.Write(ref _trimPending, 1);
            return;
        }

        _ = Task.Run(static () =>
        {
            try
            {
                do
                {
                    Volatile.Write(ref _trimPending, 0);
                    Execute();
                }
                while (Interlocked.Exchange(ref _trimPending, 0) == 1);
            }
            finally
            {
                // Narrow, deliberately accepted race: a request landing between the loop's
                // final Exchange and this write sets _trimPending on a worker that is about
                // to exit, so that one request is dropped. Trims are opportunistic (the
                // rate limiter would likely have suppressed a follow-up this close anyway),
                // and the alternative - holding a lock across a blocking gen2 collection -
                // is materially worse. Correctness never depends on a trim happening.
                Volatile.Write(ref _trimInFlight, 0);
            }
        });
    }

    private static bool TryReserveSlot()
    {
        var now = DateTime.UtcNow.Ticks;
        var last = Volatile.Read(ref _lastTrimTicks);
        if (last != long.MinValue && now - last < MinimumInterval.Ticks)
        {
            return false;
        }

        return Interlocked.CompareExchange(ref _lastTrimTicks, now, last) == last;
    }

    /// <summary>
    /// Performs the actual compaction + trim. Runs on a thread-pool thread.
    /// </summary>
    private static void Execute()
    {
        try
        {
            // Compact the LOH too: Skia/gRPC byte buffers and JSON payloads land there and
            // otherwise leave permanently fragmented, never-returned segments behind.
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;

            // Aggressive REQUIRES blocking + compacting; anything else throws.
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();

            // Second pass: objects resurrected/released by finalizers are only reclaimable now.
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        }
        catch
        {
            // Best effort; a failed collection must never interrupt the application.
        }

        if (OperatingSystem.IsWindows())
        {
            TrimWorkingSetWindows();
        }
    }

    private static void TrimWorkingSetWindows()
    {
        try
        {
            // GetCurrentProcess() returns a pseudo-handle that needs no Process object and
            // no handle close - avoids the SafeHandle churn of Process.GetCurrentProcess().
            SetProcessWorkingSetSize(GetCurrentProcess(), (IntPtr)(-1), (IntPtr)(-1));
        }
        catch
        {
        }
    }

    /// <summary>
    /// Current managed heap size and process working set, for diagnostics/telemetry.
    /// </summary>
    public static (long ManagedBytes, long WorkingSetBytes) Snapshot()
    {
        long workingSet;
        try
        {
            using var process = Process.GetCurrentProcess();
            workingSet = process.WorkingSet64;
        }
        catch
        {
            workingSet = 0;
        }

        return (GC.GetTotalMemory(forceFullCollection: false), workingSet);
    }
}
