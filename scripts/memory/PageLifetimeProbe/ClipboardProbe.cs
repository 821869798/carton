using System.Reflection;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform;
using Avalonia.Threading;
using carton.GUI.Controls;

internal static class ClipboardProbe
{
    internal static void Run()
    {
        var editor = new JsonConfigEditor();
        var window = new Window { Content = editor, Width = 600, Height = 400 };
        window.Show();
        var original = window.PlatformImpl!;
        var platform = DispatchProxy.Create<ITopLevelImpl, PlatformProxy>();
        var clipboard = DispatchProxy.Create<IClipboard, ClipboardProxy>();
        var fake = (ClipboardProxy)(object)clipboard;
        ((PlatformProxy)(object)platform).Original = original;
        ((PlatformProxy)(object)platform).Clipboard = clipboard;
        typeof(TopLevel).GetProperty("PlatformImpl")!.SetValue(window, platform);
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var surface = typeof(JsonConfigEditor).GetField("_surface", flags)!.GetValue(editor)!;
        void Select(int start, int length) => surface.GetType().GetMethod("SelectRange")!.Invoke(surface, new object[] { start, length });
        Task Invoke(string name) => (Task)surface.GetType().GetMethod(name, flags)!.Invoke(surface, null)!;
        void Finish(Task task)
        {
            for (var i = 0; i < 100 && !task.IsCompleted; i++) Dispatcher.UIThread.RunJobs();
            if (!task.IsCompleted) throw new Exception("Clipboard continuation did not finish");
            task.GetAwaiter().GetResult();
        }
        try
        {
            foreach (var operation in new[] { "CutSelectionAsync", "PasteSelectionAsync" })
            foreach (var change in new[] { "none", "selection", "document", "readonly", "detach", "failure" })
            {
                editor.IsReadOnly = false; editor.Text = "abcdef"; Select(1, 3);
                fake.Gate = null;
                clipboard.SetTextAsync("PASTE").GetAwaiter().GetResult();
                fake.Gate = new TaskCompletionSource();
                var pending = Invoke(operation);
                if (pending.IsCompleted) throw new Exception("Expected delayed clipboard operation");
                switch (change)
                {
                    case "selection": Select(0, 1); break;
                    case "document": editor.Text = "new document"; break;
                    case "readonly": editor.IsReadOnly = true; break;
                    case "detach": window.Content = null; break;
                }
                if (change == "failure") fake.Gate.SetException(new IOException("Clipboard busy"));
                else fake.Gate.SetResult();
                try { Finish(pending); if (change == "failure") throw new Exception("Expected clipboard failure"); }
                catch (IOException) when (change == "failure") { }
                var expected = change == "document" ? "new document" : change == "none"
                    ? operation == "CutSelectionAsync" ? "aef" : "aPASTEef" : "abcdef";
                if (editor.Text != expected) throw new Exception($"{operation}/{change}: {editor.Text}, expected {expected}");
                window.Content = editor;
                Console.WriteLine($"PASS delayed {operation}/{change}");
            }
        }
        finally
        {
            typeof(TopLevel).GetProperty("PlatformImpl")!.SetValue(window, original);
            window.Close();
        }
    }

    public class PlatformProxy : DispatchProxy
    {
        public ITopLevelImpl Original = null!;
        public IClipboard Clipboard = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == "TryGetFeature")
            {
                var type = (Type)args![0]!;
                if (type == typeof(IClipboard)) return Clipboard;
                if (type.Name == "IPlatformClipboardManagerImpl") return null;
            }
            return method.Invoke(Original, args);
        }
    }
    public class ClipboardProxy : DispatchProxy
    {
        public TaskCompletionSource? Gate;
        private IAsyncDataTransfer? _data;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            "SetDataAsync" => Set((IAsyncDataTransfer?)args![0]),
            "TryGetDataAsync" => Get(),
            "TryGetInProcessDataAsync" => Task.FromResult(_data),
            "ClearAsync" or "FlushAsync" => Task.CompletedTask,
            _ => throw new NotSupportedException(method.Name)
        };
        private async Task Set(IAsyncDataTransfer? data) { if (Gate != null) await Gate.Task; _data = data; }
        private async Task<IAsyncDataTransfer?> Get() { if (Gate != null) await Gate.Task; return _data; }
    }
}
