using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using carton.GUI.Controls;

internal static class EditorMemoryProbe
{
    internal static int Run()
    {
        AppBuilder.Configure<Application>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
        var editor = new JsonConfigEditor();
        var window = new Window { Content = editor, Width = 800, Height = 600 }; window.Show();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var surface = typeof(JsonConfigEditor).GetField("_surface", flags)!.GetValue(editor)!;
        var select = surface.GetType().GetMethod("SelectRange")!;
        var insert = surface.GetType().GetMethod("InsertText", flags)!;
        var text = "[\n" + string.Join(",\n", Enumerable.Repeat("  {\"tag\":\"direct\",\"type\":\"direct\"}", 100000)) + "\n]";
        editor.Text = "{}"; Layout();
        var initial = Heap();
        editor.Text = text; Layout();
        var loaded = Heap();
        select.Invoke(surface, new object[] { text.Length - 2, 0 });
        insert.Invoke(surface, new object[] { " " }); Layout();
        var edited = Heap();
        for (var i = 0; i < 500; i++) insert.Invoke(surface, new object[] { " " });
        Layout(); var steady = Heap();
        editor.Text = "{}"; Layout(); var released = Heap();
        Console.WriteLine(JsonSerializer.Serialize(new { characters = text.Length, loadedExtraMiB = (loaded-initial)/1048576d, firstEditExtraMiB = (edited-initial)/1048576d, after501EditsExtraMiB = (steady-initial)/1048576d, releasedExtraMiB = (released-initial)/1048576d }));
        // Keep original immutable content alive in both versions, as the profile's saved baseline does.
        GC.KeepAlive(text); window.Close(); return 0;
        void Layout() { window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2); Dispatcher.UIThread.RunJobs(); }
        static long Heap() { GC.Collect(); GC.WaitForPendingFinalizers(); return GC.GetTotalMemory(true); }
    }
}
