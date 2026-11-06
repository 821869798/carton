using System.Collections.Generic;

namespace carton.GUI.Controls;

/// <summary>编辑历史中需要随文本一起恢复的光标/视口状态。</summary>
public readonly record struct CaretState(int Caret, int Anchor, double HOffset, double VOffset);

/// <summary>
/// 一次可逆文本编辑：在 <see cref="Offset"/> 处删除 <see cref="Removed"/>、插入 <see cref="Inserted"/>。
/// <see cref="Restore"/> 是「沿该方向应用后应恢复到的光标状态」。
/// </summary>
public readonly record struct TextEdit(int Offset, string Removed, string Inserted, CaretState Restore);

/// <summary>
/// 借鉴 AvaloniaEdit 的 UndoStack 思路：撤销栈只存变更增量（offset+删的文本+插的文本），
/// 而不是每次按键存一份全文副本。内存/时间与「改动量」成正比，而非与文档大小成正比。
/// 本类是纯逻辑（不依赖控件/Avalonia），可单元测试。
/// </summary>
public sealed class JsonEditHistory
{
    private readonly Stack<TextEdit> _undo = new();
    private readonly Stack<TextEdit> _redo = new();
    private readonly int _maxEntries;
    private readonly long _maxChars;

    public JsonEditHistory(int maxEntries, long maxChars)
    {
        _maxEntries = maxEntries;
        _maxChars = maxChars;
    }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }

    /// <summary>记录一次正向编辑。<paramref name="edit"/>.Restore 应为编辑前的光标状态（供撤销恢复）。</summary>
    public void Record(TextEdit edit)
    {
        _undo.Push(edit);
        _redo.Clear();
        Trim(_undo);
    }

    /// <summary>
    /// 撤销：对 <paramref name="currentText"/> 反向应用栈顶编辑，输出新文本与应恢复的光标状态。
    /// <paramref name="currentState"/> 是撤销前（即原编辑之后）的状态，用于构造重做项。
    /// </summary>
    public bool TryUndo(string currentText, CaretState currentState, out string newText, out CaretState newState)
    {
        if (_undo.Count == 0)
        {
            newText = currentText;
            newState = currentState;
            return false;
        }

        var e = _undo.Pop();
        // 反向：删掉当初插入的文本，补回当初删除的文本。
        newText = currentText.Remove(e.Offset, e.Inserted.Length).Insert(e.Offset, e.Removed);
        newState = e.Restore;
        // 重做项保留同一正向编辑，恢复状态记为「原编辑之后」= 撤销前的当前状态。
        _redo.Push(new TextEdit(e.Offset, e.Removed, e.Inserted, currentState));
        return true;
    }

    /// <summary>重做：对 <paramref name="currentText"/> 正向应用栈顶编辑。</summary>
    public bool TryRedo(string currentText, CaretState currentState, out string newText, out CaretState newState)
    {
        if (_redo.Count == 0)
        {
            newText = currentText;
            newState = currentState;
            return false;
        }

        var e = _redo.Pop();
        // 正向：删掉当初删除的文本，重新插入当初插入的文本。
        newText = currentText.Remove(e.Offset, e.Removed.Length).Insert(e.Offset, e.Inserted);
        newState = e.Restore;
        // 撤销项恢复状态记为「编辑之前」= 重做前的当前状态（重做前正处于撤销后的旧状态）。
        _undo.Push(new TextEdit(e.Offset, e.Removed, e.Inserted, currentState));
        return true;
    }

    public bool TryUndo(JsonTextDocument document, CaretState currentState, out CaretState newState)
    {
        newState = currentState;
        if (_undo.Count == 0) return false;
        var edit = _undo.Peek();
        document.Replace(edit.Offset, edit.Inserted.Length, edit.Removed);
        _undo.Pop();
        newState = edit.Restore;
        _redo.Push(edit with { Restore = currentState });
        return true;
    }

    public bool TryRedo(JsonTextDocument document, CaretState currentState, out CaretState newState)
    {
        newState = currentState;
        if (_redo.Count == 0) return false;
        var edit = _redo.Peek();
        document.Replace(edit.Offset, edit.Removed.Length, edit.Inserted);
        _redo.Pop();
        newState = edit.Restore;
        _undo.Push(edit with { Restore = currentState });
        return true;
    }

    // 按条数与「增量字符总量」双上限裁剪，保留最新的若干条。
    private void Trim(Stack<TextEdit> stack)
    {
        if (stack.Count <= _maxEntries)
        {
            var totalChars = 0L;
            foreach (var e in stack)
            {
                totalChars += e.Removed.Length + e.Inserted.Length;
            }

            if (totalChars <= _maxChars)
            {
                return;
            }
        }

        var entries = stack.ToArray(); // [0] 为栈顶（最新）
        stack.Clear();
        var keptChars = 0L;
        var kept = 0;
        for (var i = 0; i < entries.Length && kept < _maxEntries; i++)
        {
            keptChars += entries[i].Removed.Length + entries[i].Inserted.Length;
            if (kept > 0 && keptChars > _maxChars)
            {
                break;
            }

            kept++;
        }

        for (var i = kept - 1; i >= 0; i--)
        {
            stack.Push(entries[i]);
        }
    }
}
