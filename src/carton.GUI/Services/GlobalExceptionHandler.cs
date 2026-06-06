using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Threading;
using carton.Core.Utilities;

namespace carton.GUI.Services;

/// <summary>
/// Process-wide last-resort exception handlers.
/// <list type="number">
/// <item>Record stack traces for exception paths that would otherwise be hard
/// to diagnose. Some covered paths are recoverable, while others such as
/// <see cref="AppDomain.UnhandledException"/> are diagnostic-only and do not
/// prevent process termination.</item>
/// <item>Reduce escalation for exception sources we can safely handle here,
/// such as unobserved task exceptions and Avalonia dispatcher exceptions.</item>
/// <item>Persist the details to <c>data/logs/crash.log</c> so crash evidence
/// lives alongside the rest of the application's logs.</item>
/// </list>
/// </summary>
internal static class GlobalExceptionHandler
{
    private static readonly object LogGate = new();
    private static bool _registered;
    private static bool _dispatcherRegistered;

    /// <summary>
    /// Registers the AppDomain and TaskScheduler handlers. Safe to call once,
    /// as early as possible in process startup (before the UI is built).
    /// AppDomain unhandled exceptions are logged here, but remain fatal.
    /// </summary>
    public static void Register()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log("AppDomain.UnhandledException", e.ExceptionObject as Exception, e.IsTerminating);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log("TaskScheduler.UnobservedTaskException", e.Exception, isTerminating: false);
            e.SetObserved();
        };
    }

    /// <summary>
    /// Registers the Avalonia UI-thread handler. Must run after Avalonia has
    /// initialised the dispatcher.
    /// </summary>
    public static void RegisterDispatcher()
    {
        if (_dispatcherRegistered)
        {
            return;
        }

        _dispatcherRegistered = true;

        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Log("Dispatcher.UIThread.UnhandledException", e.Exception, isTerminating: false);
            e.Handled = true;
        };
    }

    private static void Log(string source, Exception? exception, bool isTerminating)
    {
        try
        {
            var builder = new StringBuilder();
            builder.Append('[').Append(DateTimeOffset.Now.ToString("O")).Append("] ").Append(source);
            if (isTerminating)
            {
                builder.Append(" (terminating)");
            }

            builder.AppendLine();
            builder.AppendLine(exception?.ToString() ?? "<no exception object>");
            builder.AppendLine(new string('-', 80));

            var logPath = GetCrashLogPath();
            lock (LogGate)
            {
                File.AppendAllText(logPath, builder.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // A crash logger that throws is worse than useless. Stay silent.
        }
    }

    private static string GetCrashLogPath()
    {
        var logDirectory = Path.Combine(PathHelper.GetAppDataPath(), "data", "logs");
        Directory.CreateDirectory(logDirectory);
        return Path.Combine(logDirectory, "crash.log");
    }
}
