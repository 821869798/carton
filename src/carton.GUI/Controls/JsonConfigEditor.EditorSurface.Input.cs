using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace carton.GUI.Controls;

public sealed partial class JsonConfigEditor
{
    private sealed partial class EditorSurface
    {
        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            Focus();
            var index = GetIndexFromPoint(e.GetPosition(this));
            _caretIndex = index;
            _selectionAnchor = e.KeyModifiers.HasFlag(KeyModifiers.Shift)
                ? (_selectionAnchor >= 0 ? _selectionAnchor : _caretIndex)
                : index;
            _pointerSelecting = true;
            InvalidateVisual();
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (!_pointerSelecting)
            {
                return;
            }

            _caretIndex = GetIndexFromPoint(e.GetPosition(this));
            EnsureCaretVisible();
            InvalidateVisual();
            e.Handled = true;
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            _pointerSelecting = false;
            e.Handled = true;
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                if (Math.Abs(e.Delta.Y) > double.Epsilon)
                {
                    AdjustFontSize(Math.Sign(e.Delta.Y) * FontSizeStep);
                }

                e.Handled = true;
                return;
            }

            EnsureMetrics();
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                // 鼠标滚轮只产生竖直 delta，按住 Shift 时将其映射为水平滚动。
                var delta = Math.Abs(e.Delta.X) > double.Epsilon ? e.Delta.X : e.Delta.Y;
                _owner.ScrollSurfaceBy(new Vector(-delta * _charWidth * 3, 0));
                e.Handled = true;
                return;
            }

            _owner.ScrollSurfaceBy(new Vector(
                -e.Delta.X * _charWidth * 3,
                -e.Delta.Y * _lineHeight * 3));
            e.Handled = true;
        }

        protected override void OnTextInput(TextInputEventArgs e)
        {
            base.OnTextInput(e);
            if (_owner.IsReadOnly || string.IsNullOrEmpty(e.Text))
            {
                return;
            }

            InsertText(e.Text);
            e.Handled = true;
        }

        protected override async void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            if (HandleEditorShortcut(e))
            {
                e.Handled = true;
                return;
            }

            if (HandleClipboardShortcutAsync(e) is { } task)
            {
                await task;
                e.Handled = true;
                return;
            }

            if (HandleNavigation(e) || HandleEditing(e))
            {
                e.Handled = true;
            }
        }

        private string Text => _owner.Text ?? string.Empty;

        private bool HandleEditorShortcut(KeyEventArgs e)
        {
            if (e.Key == Key.F3)
            {
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                {
                    _owner.FindPrevious();
                }
                else
                {
                    _owner.FindNext();
                }

                return true;
            }

            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                return false;
            }

            switch (e.Key)
            {
                case Key.F:
                    _owner.OpenSearch();
                    return true;
                case Key.Z when !_owner.IsReadOnly && e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                    _owner.Redo();
                    return true;
                case Key.Z when !_owner.IsReadOnly:
                    _owner.Undo();
                    return true;
                case Key.Y when !_owner.IsReadOnly:
                    _owner.Redo();
                    return true;
                default:
                    return false;
            }
        }

        private Task? HandleClipboardShortcutAsync(KeyEventArgs e)
        {
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                return null;
            }

            return e.Key switch
            {
                Key.A => Task.Run(SelectAllOnUiThread),
                Key.C => CopySelectionAsync(),
                Key.X when !_owner.IsReadOnly => CutSelectionAsync(),
                Key.V when !_owner.IsReadOnly => PasteSelectionAsync(),
                _ => null
            };
        }

        private void SelectAllOnUiThread()
        {
            Dispatcher.UIThread.Post(() =>
            {
                _selectionAnchor = 0;
                _caretIndex = Text.Length;
                EnsureCaretVisible();
                InvalidateVisual();
            });
        }

        private async Task CopySelectionAsync()
        {
            var selected = GetSelectedText();
            if (string.IsNullOrEmpty(selected))
            {
                return;
            }

            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
                desktop.MainWindow?.Clipboard != null)
            {
                await desktop.MainWindow.Clipboard.SetTextAsync(selected);
            }
        }

        private async Task CutSelectionAsync()
        {
            var selected = GetSelectedText();
            if (string.IsNullOrEmpty(selected))
            {
                return;
            }

            await CopySelectionAsync();
            DeleteSelectionOrCharacter(backspace: true);
        }

        private async Task PasteSelectionAsync()
        {
            if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop ||
                desktop.MainWindow?.Clipboard == null)
            {
                return;
            }

            var text = await desktop.MainWindow.Clipboard.TryGetTextAsync();
            if (!string.IsNullOrEmpty(text))
            {
                InsertText(text);
            }
        }

        private bool HandleNavigation(KeyEventArgs e)
        {
            var keepSelection = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            switch (e.Key)
            {
                case Key.Left:
                    MoveCaret(PrevCharIndex(_caretIndex), keepSelection);
                    return true;
                case Key.Right:
                    MoveCaret(NextCharIndex(_caretIndex), keepSelection);
                    return true;
                case Key.Up:
                    MoveVertical(-1, keepSelection);
                    return true;
                case Key.Down:
                    MoveVertical(1, keepSelection);
                    return true;
                case Key.Home:
                    MoveToLineBoundary(toEnd: false, keepSelection);
                    return true;
                case Key.End:
                    MoveToLineBoundary(toEnd: true, keepSelection);
                    return true;
                default:
                    return false;
            }
        }

        private bool HandleEditing(KeyEventArgs e)
        {
            if (_owner.IsReadOnly)
            {
                return false;
            }

            switch (e.Key)
            {
                case Key.Back:
                    DeleteSelectionOrCharacter(backspace: true);
                    return true;
                case Key.Delete:
                    DeleteSelectionOrCharacter(backspace: false);
                    return true;
                case Key.Enter:
                    InsertText(Environment.NewLine);
                    return true;
                case Key.Tab:
                    InsertText("  ");
                    return true;
                default:
                    return false;
            }
        }

        private void MoveCaret(int newIndex, bool keepSelection)
        {
            if (!keepSelection)
            {
                _selectionAnchor = newIndex;
            }

            _caretIndex = newIndex;
            EnsureCaretVisible();
            InvalidateVisual();
        }

        private void MoveVertical(int delta, bool keepSelection)
        {
            // 借鉴 AvaloniaEdit：垂直移动以「显示列」为中介坐标，
            // 否则源行含 CJK 时按字符列定位会与 caret 的实际 X 偏离。
            var lineIndex = GetLineIndexForOffset(_caretIndex);
            var displayColumn = OffsetToDisplayColumn(_lines[lineIndex], _caretIndex);
            var targetLineIndex = Math.Clamp(lineIndex + delta, 0, _lines.Count - 1);
            var targetIndex = DisplayColumnToOffset(_lines[targetLineIndex], displayColumn);
            MoveCaret(targetIndex, keepSelection);
        }

        private void MoveToLineBoundary(bool toEnd, bool keepSelection)
        {
            var lineIndex = GetLineIndexForOffset(_caretIndex);
            var line = _lines[lineIndex];
            MoveCaret(toEnd ? line.EndOffset : line.StartOffset, keepSelection);
        }

    }
}
