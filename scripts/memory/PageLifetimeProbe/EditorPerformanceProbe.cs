using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using carton.GUI.Controls;

internal static class EditorPerformanceProbe
{
    internal static int Run()
    {
        AppBuilder.Configure<Application>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var editor = new JsonConfigEditor();
        var window = new Window { Content = editor, Width = 800, Height = 600 };
        window.Show();
        var surface = typeof(JsonConfigEditor).GetField("_surface", flags)!.GetValue(editor)!;
        var draw = surface.GetType().GetMethod("DrawSearchMatches", flags)!;
        var search = typeof(JsonConfigEditor).GetMethod("UpdateSearchResults", flags)!;
        var select = surface.GetType().GetMethod("SelectRange")!;
        var insert = surface.GetType().GetMethod("InsertText", flags)!;
        var editing = surface.GetType().GetMethod("HandleEditing", flags)!;
        foreach (var lineCount in new[] { 1000, 100000 })
        {
            var original = "[\n" + string.Join(",\n", Enumerable.Repeat("  {\"tag\":\"direct\",\"type\":\"direct\"}", lineCount)) + "\n]";
            editor.Text = original;
            window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(1); Dispatcher.UIThread.RunJobs();
            typeof(JsonConfigEditor).GetField("_searchQuery", flags)!.SetValue(editor, "direct");
            search.Invoke(editor, new object[] { false });
            select.Invoke(surface, new object[] { original.Length - 5, 0 });
            var rounds = new List<double>();
            for (var batch = 0; batch < 6; batch++)
            {
                var sw = Stopwatch.StartNew();
                for (var i = 0; i < 100; i++)
                {
                    var group = new DrawingGroup();
                    using var context = group.Open();
                    draw.Invoke(surface, new object[] { context, 48d });
                }
                if (batch > 0) rounds.Add(sw.Elapsed.TotalMilliseconds / 100);
            }
            rounds.Sort();
            Console.WriteLine(JsonSerializer.Serialize(new { lineCount, stage = "highlight", msPerPass = rounds[2] }));
            foreach (var position in new[] { "start", "middle", "end" })
            foreach (var action in new[] { "type", "enter", "active-search" })
            {
                var times = new List<double>(); var allocations = new List<long>();
                var firstTimes = new List<double>(); var firstAllocations = new List<long>();
                for (var batch = 0; batch < 4; batch++)
                {
                    editor.CloseSearch(); editor.Text = original;
                    var offset = position == "start" ? 2 : position == "middle" ? original.Length / 2 : original.Length - 2;
                    select.Invoke(surface, new object[] { offset, 0 });
                    if (action == "active-search")
                    {
                        typeof(JsonConfigEditor).GetField("_searchQuery", flags)!.SetValue(editor, "direct");
                        search.Invoke(editor, new object[] { false });
                    }
                    void Edit()
                    {
                        if (action == "enter") editing.Invoke(surface, new object[] { new Avalonia.Input.KeyEventArgs { Key = Avalonia.Input.Key.Enter } });
                        else insert.Invoke(surface, new object[] { " " });
                    }
                    var firstBytes = GC.GetAllocatedBytesForCurrentThread();
                    var firstWatch = Stopwatch.StartNew(); Edit(); firstWatch.Stop();
                    var firstAllocated = GC.GetAllocatedBytesForCurrentThread() - firstBytes;
                    var bytes = GC.GetAllocatedBytesForCurrentThread();
                    var typing = Stopwatch.StartNew();
                    for (var i = 0; i < 20; i++) Edit();
                    typing.Stop();
                    // Capture before any explicit Text getter/export used for validation/reporting.
                    var allocation = GC.GetAllocatedBytesForCurrentThread() - bytes;
                    if (batch > 0)
                    {
                        times.Add(typing.Elapsed.TotalMilliseconds / 20); allocations.Add(allocation / 20);
                        firstTimes.Add(firstWatch.Elapsed.TotalMilliseconds); firstAllocations.Add(firstAllocated);
                    }
                    if (editor.Text.Length <= original.Length) throw new Exception("Benchmark did not edit text");
                }
                times.Sort(); allocations.Sort(); firstTimes.Sort(); firstAllocations.Sort();
                Console.WriteLine(JsonSerializer.Serialize(new { lineCount, position, action, msPerKey = times[1], allocatedBytesPerKey = allocations[1], firstKeyMs = firstTimes[1], firstKeyAllocatedBytes = firstAllocations[1] }));
            }
        }
        editor.CloseSearch(); window.Close();
        return 0;
    }
}
