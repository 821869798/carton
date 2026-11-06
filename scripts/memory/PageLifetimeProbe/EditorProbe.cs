using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using carton.GUI.Controls;

internal static class EditorProbe
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    internal static int Run()
    {
        AppBuilder.Configure<Application>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
        var editor = new JsonConfigEditor { Text = "abcdef" };
        var surface = typeof(JsonConfigEditor).GetField("_surface", Private)!.GetValue(editor)!;
        void Select(int start, int length) => surface.GetType().GetMethod("SelectRange")!.Invoke(surface, new object[] { start, length });
        object? Call(string name, params object[] args) => surface.GetType().GetMethod(name, Private)!.Invoke(surface, args);
        void Expect(string value, string check) { if (editor.Text != value) throw new Exception(check + ": " + editor.Text); Console.WriteLine("PASS " + check); }
        Select(1, 3);
        ((Task)Call("CutSelectionAsync")!).GetAwaiter().GetResult();
        Expect("abcdef", "unavailable clipboard preserves text");
        Call("InsertText", "XYZ"); Expect("aXYZef", "replace selection");
        editor.Undo(); Expect("abcdef", "undo replacement");
        editor.Redo(); Expect("aXYZef", "redo replacement");
        editor.IsReadOnly = true; editor.Undo(); Expect("aXYZef", "public undo respects read-only");
        editor.IsReadOnly = false; editor.Undo(); editor.IsReadOnly = true; editor.Redo(); Expect("abcdef", "public redo respects read-only");
        editor.IsReadOnly = false;
        Call("SelectAllAsync"); Call("InsertText", "replacement"); Expect("replacement", "select all immediately precedes typing");
        editor.Text = new string('a', 4000) + "!";
        typeof(JsonConfigEditor).GetField("_searchQuery", Private)!.SetValue(editor, "(a+)+$");
        typeof(JsonConfigEditor).GetField("_searchUseRegex", Private)!.SetValue(editor, true);
        var clock = Stopwatch.StartNew();
        typeof(JsonConfigEditor).GetMethod("UpdateSearchResults", Private)!.Invoke(editor, new object[] { false });
        if (editor.IsSearchPatternValid || editor.HasSearchMatches || clock.Elapsed > TimeSpan.FromSeconds(3)) throw new Exception("Pathological regex was not bounded/cleared");
        Console.WriteLine($"PASS pathological regex returns ERR in {clock.ElapsedMilliseconds}ms");
        typeof(JsonConfigEditor).GetField("_searchQuery", Private)!.SetValue(editor, "a+");
        typeof(JsonConfigEditor).GetMethod("UpdateSearchResults", Private)!.Invoke(editor, new object[] { false });
        if (!editor.IsSearchPatternValid || !editor.HasSearchMatches) throw new Exception("Search did not recover after timeout");
        Console.WriteLine("PASS valid search recovers after timeout");
        editor.Text = "one one\nONE";
        Select(0, 0);
        typeof(JsonConfigEditor).GetField("_searchQuery", Private)!.SetValue(editor, "one");
        typeof(JsonConfigEditor).GetField("_searchUseRegex", Private)!.SetValue(editor, false);
        typeof(JsonConfigEditor).GetMethod("UpdateSearchResults", Private)!.Invoke(editor, new object[] { true });
        if (editor.SearchStatusText != "1/3") throw new Exception("Literal search count/selection failed");
        editor.FindPrevious(); if (editor.SearchStatusText != "3/3") throw new Exception("Previous wrap failed");
        editor.FindNext(); if (editor.SearchStatusText != "1/3") throw new Exception("Next wrap failed");
        editor.CloseSearch(); if (editor.HasSearchMatches) throw new Exception("Close search retained matches");
        Console.WriteLine("PASS literal search navigation/wrapping/close");
        editor.Text = "a😀中b"; Select(3, 0);
        Call("DeleteSelectionOrCharacter", true); Expect("a中b", "backspace removes whole emoji");
        editor.Undo(); Expect("a😀中b", "undo Unicode deletion");
        editor.Redo(); Expect("a中b", "redo Unicode deletion");
        editor.Text = "{\n    \"nested\": {}\n}";
        var newlineStart = editor.Text.IndexOf("{}", StringComparison.Ordinal) + 1;
        var beforeNewline = editor.Text;
        Select(newlineStart, 0);
        Call("HandleEditing", new Avalonia.Input.KeyEventArgs { Key = Avalonia.Input.Key.Enter });
        Expect("{\n    \"nested\": {\n        \n    }\n}", "context-aware Enter expands pair");
        var caretProperty = surface.GetType().GetProperty("CaretIndex")!;
        var innerCaret = (int)caretProperty.GetValue(surface)!;
        if (innerCaret != newlineStart + 9) throw new Exception("Enter caret is not on inner indented line");
        editor.Undo(); Expect(beforeNewline, "single undo restores Enter");
        editor.Redo();
        if ((int)caretProperty.GetValue(surface)! != innerCaret) throw new Exception("Redo lost indented caret");
        Call("InsertText", "\"x\": 1");
        Expect("{\n    \"nested\": {\n        \"x\": 1\n    }\n}", "typing follows indented caret after redo");
        editor.IsReadOnly = true;
        var readonlyText = editor.Text;
        Call("HandleEditing", new Avalonia.Input.KeyEventArgs { Key = Avalonia.Input.Key.Enter });
        Expect(readonlyText, "read-only Enter leaves text unchanged");
        editor.IsReadOnly = false;
        JsonValidationProbe.Run();
        ClipboardProbe.Run();
        DocumentAttachmentProbe.Run();
        return 0;
    }
}
