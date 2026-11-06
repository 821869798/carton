using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Threading;
using carton.GUI.Controls;

internal static class DocumentAttachmentProbe
{
    internal static void Run()
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var document = new JsonTextDocument(); document.Load("one two one");
        var editor = new JsonConfigEditor { Document = document };
        var window = new Window { Content = editor, Width = 600, Height = 400 };
        window.Show();
        var surface = typeof(JsonConfigEditor).GetField("_surface", flags)!.GetValue(editor)!;
        var select = surface.GetType().GetMethod("SelectRange")!;
        var insert = surface.GetType().GetMethod("InsertText", flags)!;
        editor.SetSearchQuery("one"); Pump(250);
        if (!editor.HasSearchMatches) throw new Exception("Debounced search did not complete");
        select.Invoke(surface, new object[] { 0, 0 });
        insert.Invoke(surface, new object[] { " " });
        if (editor.HasSearchMatches) throw new Exception("Stale search highlights were retained during edit");
        Pump(250);
        if (!editor.HasSearchMatches || (int)surface.GetType().GetProperty("CaretIndex")!.GetValue(surface)! != 1)
            throw new Exception("Search refresh lost matches or moved typing caret");
        window.Content = null;
        document.Load("new document");
        window.Content = editor;
        if (editor.CanUndo || editor.Text != "new document") throw new Exception("Detached document reset retained stale history");
        var replacement = new JsonTextDocument(); replacement.Load("replacement");
        editor.Document = replacement;
        document.Replace(0, 0, "ignored");
        if (editor.Text != "replacement" || editor.CanUndo) throw new Exception("Document switch retained old data/history");
        window.Close();
        var retainedDocument = new JsonTextDocument();
        var weak = CreateClosedEditor(retainedDocument);
        Pump(600); GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        if (weak.IsAlive) throw new Exception("Retained document roots closed editor");
        GC.KeepAlive(retainedDocument);
        Console.WriteLine("PASS debounced search preserves caret, detached reload, document switch and closed-editor collection");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateClosedEditor(JsonTextDocument document)
    {
        var editor = new JsonConfigEditor { Document = document };
        var window = new Window { Content = editor }; window.Show();
        var weak = new WeakReference(editor);
        window.Close(); window.Content = null;
        return weak;
    }
    private static void Pump(int milliseconds)
    {
        using var stop = new CancellationTokenSource(milliseconds);
        Dispatcher.UIThread.MainLoop(stop.Token);
    }
}
