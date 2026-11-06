using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using carton.Core.Models;
using carton.GUI.Services;
using carton.ViewModels;

// This is an object-lifetime regression probe, NOT a working-set benchmark.
// Forced collection is confined to this test, so a surviving WeakReference proves retention.
internal static class Program
{
    private const int Iterations = 20;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--profiles")) return ProfileListProbe.Run(args.Contains("--verify"));

        AppBuilder.Configure<Application>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions())
            .SetupWithoutStarting();
        LocalizationService.Instance.Initialize(AppLanguage.English);
        var logStore = new LogStore();
        var cases = new (string Name, Func<PageViewModelBase> Create)[]
        {
            ("Connections", static () => new ConnectionsViewModel()),
            ("Profiles", static () => new ProfilesViewModel()),
            ("Logs", () => new LogsViewModel(logStore)),
            ("Settings", static () => new SettingsViewModel())
        };
        var retained = 0;
        foreach (var (name, create) in cases)
        {
            CheckActiveLocalization(create);
            Collect();
            var heapBefore = GC.GetTotalMemory(false);
            var subscriptionsBefore = SubscriptionCount();
            var pages = Enumerable.Range(0, Iterations).Select(_ => CreateDisposedPage(create)).ToArray();
            Collect();
            var alive = pages.Count(page => page.IsAlive);
            retained += alive;
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                page = name,
                created = Iterations,
                retainedAfterDispose = alive,
                addedLocalizationSubscriptions = SubscriptionCount() - subscriptionsBefore,
                retainedManagedBytes = GC.GetTotalMemory(false) - heapBefore,
                localizationTargets = DescribeSubscriptions()
            }));
        }
        GC.KeepAlive(logStore);
        if (args.Contains("--verify") && retained != 0)
        {
            Console.Error.WriteLine($"FAIL: {retained} disposed page view models are still rooted.");
            return 1;
        }
        Console.WriteLine($"Disposed pages retained: {retained}/{Iterations * cases.Length}");
        return 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateDisposedPage(Func<PageViewModelBase> create)
    {
        var page = create();
        page.OnNavigatedTo();
        Dispatcher.UIThread.RunJobs();
        page.OnNavigatedFrom();
        var reference = new WeakReference(page);
        ((IDisposable)page).Dispose();
        ((IDisposable)page).Dispose(); // Cleanup must also be safe when repeated.
        return reference;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CheckActiveLocalization(Func<PageViewModelBase> create)
    {
        var page = create();
        try
        {
            var english = page.Title;
            LocalizationService.Instance.SetLanguage(AppLanguage.SimplifiedChinese);
            Dispatcher.UIThread.RunJobs();
            if (page.Title == english) throw new InvalidOperationException("Active page title did not change language.");
            LocalizationService.Instance.SetLanguage(AppLanguage.English);
            Dispatcher.UIThread.RunJobs();
            if (page.Title != english) throw new InvalidOperationException("Active page title did not restore English.");
        }
        finally { ((IDisposable)page).Dispose(); }
    }

    private static void Collect()
    {
        Dispatcher.UIThread.RunJobs();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static Delegate[] Subscriptions() =>
        ((Delegate?)typeof(LocalizationService).GetField("LanguageChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(LocalizationService.Instance))?.GetInvocationList() ?? [];

    private static int SubscriptionCount() => Subscriptions().Length;

    private static Dictionary<string, int> DescribeSubscriptions() => Subscriptions()
        .GroupBy(handler => $"{handler.Target?.GetType().Name}.{handler.Method.Name}")
        .ToDictionary(group => group.Key, group => group.Count());
}
