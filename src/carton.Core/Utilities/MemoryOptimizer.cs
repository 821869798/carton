using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace carton.Core.Utilities;

/// <summary>
/// Proactive memory optimization utility to compact managed heaps and trim
/// the unreferenced physical working set back to the operating system.
/// </summary>
public static partial class MemoryOptimizer
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessWorkingSetSize(IntPtr proc, IntPtr min, IntPtr max);

    /// <summary>
    /// Performs a compacting GC collection and trims the process working set on Windows.
    /// Safe to call on all platforms (no-op for OS trim on Linux).
    /// </summary>
    public static void CompactAndTrim()
    {
        try
        {
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: false, compacting: true);

            if (OperatingSystem.IsWindows())
            {
                TrimWorkingSetWindows();
            }
        }
        catch
        {
            // Best effort; failures must never interrupt the application.
        }
    }

    private static void TrimWorkingSetWindows()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            SetProcessWorkingSetSize(process.Handle, (IntPtr)(-1), (IntPtr)(-1));
        }
        catch
        {
        }
    }
}
