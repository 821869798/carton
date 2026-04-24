using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
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

public sealed class JsonConfigEditor : Grid
{
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<JsonConfigEditor, string>(
            nameof(Text),
            string.Empty,
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<bool> IsReadOnlyProperty =
        AvaloniaProperty.Register<JsonConfigEditor, bool>(nameof(IsReadOnly));

    private const int MaxHistoryEntries = 200;

    private readonly EditorSurface _surface;
    private readonly ScrollBar _horizontalScrollBar;
    private readonly ScrollBar _verticalScrollBar;
    private readonly Stack<EditorSnapshot> _undoStack = new();
    private readonly Stack<EditorSnapshot> _redoStack = new();
    private readonly List<SearchMatch> _searchMatches = new();

    private string _searchQuery = string.Empty;
    private int _currentSearchMatchIndex = -1;
    private bool _isInternalTextMutation;
    private bool _searchCaseSensitive;
    private bool _searchWholeWord;
    private bool _searchUseRegex;
    private bool _searchPatternValid = true;
    private bool _isSearchOpen;

    public JsonConfigEditor()
    {
        RowDefinitions = new RowDefinitions("*,Auto");
        ColumnDefinitions = new ColumnDefinitions("*,Auto");

        _surface = new EditorSurface(this);
        ActualThemeVariantChanged += (_, _) => _surface.InvalidateVisual();
        SetRow(_surface, 0);
        Children.Add(_surface);

        _horizontalScrollBar = new ScrollBar
        {
            Orientation = Orientation.Horizontal,
            Height = 12
        };
        _horizontalScrollBar.ValueChanged += OnHorizontalScrollChanged;
        SetRow(_horizontalScrollBar, 1);
        Children.Add(_horizontalScrollBar);

        _verticalScrollBar = new ScrollBar
        {
            Orientation = Orientation.Vertical,
            Width = 12
        };
        _verticalScrollBar.ValueChanged += OnVerticalScrollChanged;
        SetRow(_verticalScrollBar, 0);
        SetColumn(_verticalScrollBar, 1);
        Children.Add(_verticalScrollBar);
    }

    public event EventHandler? EditorStateChanged;

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public bool IsReadOnly
    {
        get => GetValue(IsReadOnlyProperty);
        set => SetValue(IsReadOnlyProperty, value);
    }

    public bool CanUndo => _undoStack.Count > 0;

    public bool CanRedo => _redoStack.Count > 0;

    public bool IsSearchOpen => _isSearchOpen;

    public bool SearchCaseSensitive => _searchCaseSensitive;

    public bool SearchWholeWord => _searchWholeWord;

    public bool SearchUseRegex => _searchUseRegex;

    public bool HasSearchMatches => _searchMatches.Count > 0 && _currentSearchMatchIndex >= 0;

    public bool IsSearchPatternValid => _searchPatternValid;

    public string SearchStatusText => !_searchPatternValid
        ? "ERR"
        : HasSearchMatches
            ? $"{_currentSearchMatchIndex + 1}/{_searchMatches.Count}"
            : "0/0";

    public void Undo()
    {
        if (!CanUndo)
        {
            return;
        }

        var current = _surface.CaptureSnapshot();
        var target = _undoStack.Pop();
        _redoStack.Push(current);
        TrimHistory(_redoStack);
        _surface.ApplySnapshot(target);
        UpdateSearchResults(selectCurrentMatch: false);
        UpdateScrollBars();
        RaiseEditorStateChanged();
    }

    public void Redo()
    {
        if (!CanRedo)
        {
            return;
        }

        var current = _surface.CaptureSnapshot();
        var target = _redoStack.Pop();
        _undoStack.Push(current);
        TrimHistory(_undoStack);
        _surface.ApplySnapshot(target);
        UpdateSearchResults(selectCurrentMatch: false);
        UpdateScrollBars();
        RaiseEditorStateChanged();
    }

    public void OpenSearch()
    {
        _isSearchOpen = true;
        RaiseEditorStateChanged();
    }

    public void CloseSearch()
    {
        _isSearchOpen = false;
        _searchQuery = string.Empty;
        _searchMatches.Clear();
        _currentSearchMatchIndex = -1;
        _searchPatternValid = true;
        _surface.InvalidateVisual();
        Dispatcher.UIThread.Post(() => _surface.Focus());
        RaiseEditorStateChanged();
    }

    public void SetSearchQuery(string query)
    {
        _searchQuery = query ?? string.Empty;
        UpdateSearchResults(selectCurrentMatch: true);
        RaiseEditorStateChanged();
    }

    public void FindNext()
    {
        if (_searchMatches.Count == 0)
        {
            return;
        }

        _currentSearchMatchIndex = (_currentSearchMatchIndex + 1 + _searchMatches.Count) % _searchMatches.Count;
        SelectCurrentSearchMatch();
    }

    public void FindPrevious()
    {
        if (_searchMatches.Count == 0)
        {
            return;
        }

        _currentSearchMatchIndex = (_currentSearchMatchIndex - 1 + _searchMatches.Count) % _searchMatches.Count;
        SelectCurrentSearchMatch();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
        {
            if (!_isInternalTextMutation)
            {
                ClearHistory();
                ResetSearchForNewText();
            }

            _surface.OnTextChanged();
            UpdateSearchResults(selectCurrentMatch: false);
            UpdateScrollBars();
            RaiseEditorStateChanged();
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var result = base.ArrangeOverride(finalSize);
        UpdateScrollBars();
        return result;
    }

    private void OnHorizontalScrollChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        _surface.HorizontalOffset = e.NewValue;
    }

    private void OnVerticalScrollChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        _surface.VerticalOffset = e.NewValue;
    }

    private void ResetSearchForNewText()
    {
        _searchMatches.Clear();
        _currentSearchMatchIndex = -1;
        _searchQuery = string.Empty;
        _searchPatternValid = true;
        _isSearchOpen = false;
    }

    private void UpdateSearchResults(bool selectCurrentMatch)
    {
        _searchMatches.Clear();
        _currentSearchMatchIndex = -1;
        _searchPatternValid = true;

        if (!string.IsNullOrWhiteSpace(_searchQuery) && !string.IsNullOrEmpty(Text))
        {
            try
            {
                if (_searchUseRegex)
                {
                    var regexPattern = _searchWholeWord ? $@"\b(?:{_searchQuery})\b" : _searchQuery;
                    var regexOptions = RegexOptions.Multiline | RegexOptions.CultureInvariant;
                    if (!_searchCaseSensitive)
                    {
                        regexOptions |= RegexOptions.IgnoreCase;
                    }

                    foreach (Match match in Regex.Matches(Text, regexPattern, regexOptions))
                    {
                        if (!match.Success || match.Length <= 0)
                        {
                            continue;
                        }

                        _searchMatches.Add(new SearchMatch(match.Index, match.Length));
                    }
                }
                else
                {
                    var searchStart = 0;
                    var comparison = _searchCaseSensitive
                        ? StringComparison.Ordinal
                        : StringComparison.OrdinalIgnoreCase;

                    while (searchStart < Text.Length)
                    {
                        var matchIndex = Text.IndexOf(_searchQuery, searchStart, comparison);
                        if (matchIndex < 0)
                        {
                            break;
                        }

                        if (!_searchWholeWord || IsWholeWordMatch(Text, matchIndex, _searchQuery.Length))
                        {
                            _searchMatches.Add(new SearchMatch(matchIndex, _searchQuery.Length));
                        }

                        searchStart = matchIndex + Math.Max(1, _searchQuery.Length);
                    }
                }
            }
            catch (ArgumentException)
            {
                _searchPatternValid = false;
            }

            if (_searchMatches.Count > 0)
            {
                _currentSearchMatchIndex = FindNearestSearchMatchIndex(_surface.CaretIndex);
                if (selectCurrentMatch)
                {
                    SelectCurrentSearchMatch();
                    return;
                }
            }
        }

        _surface.InvalidateVisual();
    }

    private int FindNearestSearchMatchIndex(int caretIndex)
    {
        for (var i = 0; i < _searchMatches.Count; i++)
        {
            if (_searchMatches[i].Start >= caretIndex)
            {
                return i;
            }
        }

        return 0;
    }

    private void SelectCurrentSearchMatch()
    {
        if (_currentSearchMatchIndex < 0 || _currentSearchMatchIndex >= _searchMatches.Count)
        {
            _surface.InvalidateVisual();
            RaiseEditorStateChanged();
            return;
        }

        var match = _searchMatches[_currentSearchMatchIndex];
        _surface.SelectRange(match.Start, match.Length);
        _surface.CenterRangeInView(match.Start, match.Length);
        _surface.InvalidateVisual();
        RaiseEditorStateChanged();
    }

    private void PushUndoSnapshot(EditorSnapshot snapshot)
    {
        _undoStack.Push(snapshot);
        TrimHistory(_undoStack);
        _redoStack.Clear();
        RaiseEditorStateChanged();
    }

    private static void TrimHistory(Stack<EditorSnapshot> stack)
    {
        if (stack.Count <= MaxHistoryEntries)
        {
            return;
        }

        var snapshots = stack.ToArray();
        stack.Clear();
        for (var i = MaxHistoryEntries - 1; i >= 0; i--)
        {
            stack.Push(snapshots[i]);
        }
    }

    private void ClearHistory()
    {
        if (_undoStack.Count == 0 && _redoStack.Count == 0)
        {
            return;
        }

        _undoStack.Clear();
        _redoStack.Clear();
        RaiseEditorStateChanged();
    }

    private void RaiseEditorStateChanged()
    {
        EditorStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateScrollBars()
    {
        if (_surface.Bounds.Width <= 0 || _surface.Bounds.Height <= 0)
        {
            return;
        }

        var extent = _surface.GetExtent();
        var horizontalMaximum = Math.Max(0, extent.Width - _surface.Bounds.Width);
        var verticalMaximum = Math.Max(0, extent.Height - _surface.Bounds.Height);

        _horizontalScrollBar.Maximum = horizontalMaximum;
        _horizontalScrollBar.ViewportSize = _surface.Bounds.Width;
        _horizontalScrollBar.IsVisible = horizontalMaximum > 1;
        _horizontalScrollBar.Value = Math.Clamp(_horizontalScrollBar.Value, 0, horizontalMaximum);

        _verticalScrollBar.Maximum = verticalMaximum;
        _verticalScrollBar.ViewportSize = _surface.Bounds.Height;
        _verticalScrollBar.IsVisible = verticalMaximum > 1;
        _verticalScrollBar.Value = Math.Clamp(_verticalScrollBar.Value, 0, verticalMaximum);

        _surface.HorizontalOffset = _horizontalScrollBar.Value;
        _surface.VerticalOffset = _verticalScrollBar.Value;
    }

    private void ScrollSurfaceBy(Vector delta)
    {
        _horizontalScrollBar.Value = Math.Clamp(_horizontalScrollBar.Value + delta.X, 0, _horizontalScrollBar.Maximum);
        _verticalScrollBar.Value = Math.Clamp(_verticalScrollBar.Value + delta.Y, 0, _verticalScrollBar.Maximum);
    }

    public void ToggleCaseSensitive()
    {
        _searchCaseSensitive = !_searchCaseSensitive;
        UpdateSearchResults(selectCurrentMatch: true);
        RaiseEditorStateChanged();
    }

    public void ToggleWholeWord()
    {
        _searchWholeWord = !_searchWholeWord;
        UpdateSearchResults(selectCurrentMatch: true);
        RaiseEditorStateChanged();
    }

    public void ToggleRegex()
    {
        _searchUseRegex = !_searchUseRegex;
        UpdateSearchResults(selectCurrentMatch: true);
        RaiseEditorStateChanged();
    }

    private static bool IsWholeWordMatch(string text, int index, int length)
    {
        var beforeIsWord = index > 0 && IsWordCharacter(text[index - 1]);
        var afterIndex = index + length;
        var afterIsWord = afterIndex < text.Length && IsWordCharacter(text[afterIndex]);
        return !beforeIsWord && !afterIsWord;
    }

    private static bool IsWordCharacter(char ch)
    {
        return char.IsLetterOrDigit(ch) || ch == '_';
    }

    private readonly record struct EditorSnapshot(
        string Text,
        int CaretIndex,
        int SelectionAnchor,
        double HorizontalOffset,
        double VerticalOffset);

    private readonly record struct SearchMatch(int Start, int Length);

    private sealed class EditorSurface : Control
    {
        private static readonly Typeface EditorTypeface = new("Consolas, Cascadia Mono, monospace");
        private static readonly IBrush DarkBackgroundBrush = new SolidColorBrush(Color.FromRgb(30, 30, 30));
        private static readonly IBrush DarkLineNumberBackgroundBrush = new SolidColorBrush(Color.FromArgb(34, 255, 255, 255));
        private static readonly IBrush DarkTextBrush = new SolidColorBrush(Color.FromRgb(212, 212, 212));
        private static readonly IBrush DarkLineNumberBrush = new SolidColorBrush(Color.FromRgb(120, 127, 136));
        private static readonly IBrush DarkStringBrush = new SolidColorBrush(Color.FromRgb(206, 145, 120));
        private static readonly IBrush DarkPropertyBrush = new SolidColorBrush(Color.FromRgb(156, 220, 254));
        private static readonly IBrush DarkNumberBrush = new SolidColorBrush(Color.FromRgb(181, 206, 168));
        private static readonly IBrush DarkKeywordBrush = new SolidColorBrush(Color.FromRgb(86, 156, 214));
        private static readonly IBrush DarkPunctuationBrush = new SolidColorBrush(Color.FromRgb(215, 186, 125));
        private static readonly IBrush DarkSelectionBrush = new SolidColorBrush(Color.FromArgb(90, 80, 140, 220));
        private static readonly IBrush DarkSearchBrush = new SolidColorBrush(Color.FromArgb(110, 255, 201, 40));
        private static readonly IBrush DarkCurrentSearchBrush = new SolidColorBrush(Color.FromArgb(150, 255, 145, 0));
        private static readonly IBrush DarkCaretBrush = new SolidColorBrush(Color.FromRgb(245, 245, 245));
        private static readonly IBrush LightBackgroundBrush = new SolidColorBrush(Color.FromRgb(248, 249, 251));
        private static readonly IBrush LightLineNumberBackgroundBrush = new SolidColorBrush(Color.FromRgb(240, 242, 245));
        private static readonly IBrush LightTextBrush = new SolidColorBrush(Color.FromRgb(36, 41, 46));
        private static readonly IBrush LightLineNumberBrush = new SolidColorBrush(Color.FromRgb(118, 126, 136));
        private static readonly IBrush LightStringBrush = new SolidColorBrush(Color.FromRgb(163, 21, 21));
        private static readonly IBrush LightPropertyBrush = new SolidColorBrush(Color.FromRgb(0, 92, 197));
        private static readonly IBrush LightNumberBrush = new SolidColorBrush(Color.FromRgb(9, 134, 88));
        private static readonly IBrush LightKeywordBrush = new SolidColorBrush(Color.FromRgb(0, 98, 177));
        private static readonly IBrush LightPunctuationBrush = new SolidColorBrush(Color.FromRgb(97, 99, 104));
        private static readonly IBrush LightSelectionBrush = new SolidColorBrush(Color.FromArgb(96, 173, 214, 255));
        private static readonly IBrush LightSearchBrush = new SolidColorBrush(Color.FromArgb(120, 255, 230, 120));
        private static readonly IBrush LightCurrentSearchBrush = new SolidColorBrush(Color.FromArgb(160, 255, 190, 60));
        private static readonly IBrush LightCaretBrush = new SolidColorBrush(Color.FromRgb(36, 41, 46));
        private const double HorizontalPadding = 8;
        private const double VerticalPadding = 8;
        private const double LineNumberGap = 8;

        private readonly JsonConfigEditor _owner;
        private readonly List<LineInfo> _lines = new();
        private readonly List<Token> _tokens = new();
        private FormattedText? _sampleFormattedText;
        private double _charWidth = 8;
        private double _lineHeight = 18;
        private int _caretIndex;
        private int _selectionAnchor = -1;
        private bool _pointerSelecting;
        private bool _internalTextUpdate;
        private double _horizontalOffset;
        private double _verticalOffset;

        public EditorSurface(JsonConfigEditor owner)
        {
            _owner = owner;
            Focusable = true;
            Cursor = new Cursor(StandardCursorType.Ibeam);
            ActualThemeVariantChanged += (_, _) =>
            {
                _sampleFormattedText = null;
                InvalidateVisual();
            };
            RebuildDocumentState();
        }

        public int CaretIndex => _caretIndex;

        public double HorizontalOffset
        {
            get => _horizontalOffset;
            set
            {
                _horizontalOffset = Math.Max(0, value);
                InvalidateVisual();
            }
        }

        public double VerticalOffset
        {
            get => _verticalOffset;
            set
            {
                _verticalOffset = Math.Max(0, value);
                InvalidateVisual();
            }
        }

        public void OnTextChanged()
        {
            if (!_internalTextUpdate)
            {
                _caretIndex = Math.Clamp(_caretIndex, 0, Text.Length);
                if (_selectionAnchor >= 0)
                {
                    _selectionAnchor = Math.Clamp(_selectionAnchor, 0, Text.Length);
                }
            }

            RebuildDocumentState();
            InvalidateVisual();
        }

        public JsonConfigEditor.EditorSnapshot CaptureSnapshot()
            => new(Text, _caretIndex, _selectionAnchor, HorizontalOffset, VerticalOffset);

        public void ApplySnapshot(JsonConfigEditor.EditorSnapshot snapshot)
        {
            _internalTextUpdate = true;
            _owner._isInternalTextMutation = true;
            _owner.Text = snapshot.Text;
            _owner._isInternalTextMutation = false;
            _internalTextUpdate = false;
            _caretIndex = Math.Clamp(snapshot.CaretIndex, 0, Text.Length);
            _selectionAnchor = Math.Clamp(snapshot.SelectionAnchor, -1, Text.Length);
            _owner._horizontalScrollBar.Value = Math.Clamp(snapshot.HorizontalOffset, 0, _owner._horizontalScrollBar.Maximum);
            _owner._verticalScrollBar.Value = Math.Clamp(snapshot.VerticalOffset, 0, _owner._verticalScrollBar.Maximum);
            EnsureCaretVisible();
            InvalidateVisual();
        }

        public void SelectRange(int start, int length)
        {
            _selectionAnchor = Math.Clamp(start, 0, Text.Length);
            _caretIndex = Math.Clamp(start + length, 0, Text.Length);
            EnsureCaretVisible();
            InvalidateVisual();
        }

        public void CenterRangeInView(int start, int length)
        {
            EnsureMetrics();
            var targetIndex = Math.Clamp(start + Math.Max(0, length / 2), 0, Text.Length);
            var (lineIndex, column) = GetLineAndColumn(targetIndex);
            var lineNumberWidth = GetLineNumberColumnWidth();
            var targetX = lineNumberWidth + HorizontalPadding + column * _charWidth;
            var targetY = VerticalPadding + lineIndex * _lineHeight;

            var horizontalTarget = Math.Max(0, targetX - Bounds.Width / 2);
            var verticalTarget = Math.Max(0, targetY - Bounds.Height / 2 + _lineHeight / 2);

            _owner._horizontalScrollBar.Value = Math.Clamp(horizontalTarget, 0, _owner._horizontalScrollBar.Maximum);
            _owner._verticalScrollBar.Value = Math.Clamp(verticalTarget, 0, _owner._verticalScrollBar.Maximum);
            HorizontalOffset = _owner._horizontalScrollBar.Value;
            VerticalOffset = _owner._verticalScrollBar.Value;
        }

        public Size GetExtent()
        {
            EnsureMetrics();
            return new Size(
                GetLineNumberColumnWidth() + HorizontalPadding * 2 + GetLongestLineLength() * _charWidth,
                VerticalPadding * 2 + _lines.Count * _lineHeight);
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            EnsureMetrics();

            var bounds = Bounds;
            var lineNumberWidth = GetLineNumberColumnWidth();
            context.FillRectangle(GetBackgroundBrush(), bounds);
            context.FillRectangle(GetLineNumberBackgroundBrush(), new Rect(0, 0, lineNumberWidth, bounds.Height));

            DrawSearchMatches(context, lineNumberWidth);
            DrawSelection(context, lineNumberWidth);
            DrawText(context, lineNumberWidth);
            DrawLineNumbers(context, lineNumberWidth);
            if (IsFocused)
            {
                DrawCaret(context, lineNumberWidth);
            }
        }

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
            EnsureMetrics();
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
                    MoveCaret(Math.Max(0, _caretIndex - 1), keepSelection);
                    return true;
                case Key.Right:
                    MoveCaret(Math.Min(Text.Length, _caretIndex + 1), keepSelection);
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
            var (lineIndex, column) = GetLineAndColumn(_caretIndex);
            var targetLineIndex = Math.Clamp(lineIndex + delta, 0, _lines.Count - 1);
            var targetLine = _lines[targetLineIndex];
            var targetIndex = Math.Min(targetLine.EndOffset, targetLine.StartOffset + column);
            MoveCaret(targetIndex, keepSelection);
        }

        private void MoveToLineBoundary(bool toEnd, bool keepSelection)
        {
            var (lineIndex, _) = GetLineAndColumn(_caretIndex);
            var line = _lines[lineIndex];
            MoveCaret(toEnd ? line.EndOffset : line.StartOffset, keepSelection);
        }

        private void InsertText(string text)
        {
            ReplaceSelection(text);
        }

        private void DeleteSelectionOrCharacter(bool backspace)
        {
            if (HasSelection)
            {
                ReplaceSelection(string.Empty);
                return;
            }

            if (backspace && _caretIndex > 0)
            {
                SetTextInternal(Text.Remove(_caretIndex - 1, 1), _caretIndex - 1);
            }
            else if (!backspace && _caretIndex < Text.Length)
            {
                SetTextInternal(Text.Remove(_caretIndex, 1), _caretIndex);
            }
        }

        private void ReplaceSelection(string replacement)
        {
            var (start, length) = GetSelectionRange();
            var newText = Text.Remove(start, length).Insert(start, replacement);
            SetTextInternal(newText, start + replacement.Length);
        }

        private void SetTextInternal(string newText, int newCaretIndex)
        {
            if (string.Equals(newText, Text, StringComparison.Ordinal))
            {
                _caretIndex = Math.Clamp(newCaretIndex, 0, Text.Length);
                _selectionAnchor = _caretIndex;
                EnsureCaretVisible();
                InvalidateVisual();
                return;
            }

            _owner.PushUndoSnapshot(CaptureSnapshot());
            _internalTextUpdate = true;
            _owner._isInternalTextMutation = true;
            _owner.Text = newText;
            _owner._isInternalTextMutation = false;
            _internalTextUpdate = false;
            _caretIndex = Math.Clamp(newCaretIndex, 0, Text.Length);
            _selectionAnchor = _caretIndex;
            EnsureCaretVisible();
            _owner.UpdateScrollBars();
            _owner.UpdateSearchResults(selectCurrentMatch: false);
            InvalidateVisual();
        }

        private bool HasSelection => _selectionAnchor >= 0 && _selectionAnchor != _caretIndex;

        private (int Start, int Length) GetSelectionRange()
        {
            if (!HasSelection)
            {
                return (_caretIndex, 0);
            }

            var start = Math.Min(_selectionAnchor, _caretIndex);
            var end = Math.Max(_selectionAnchor, _caretIndex);
            return (start, end - start);
        }

        private string GetSelectedText()
        {
            var (start, length) = GetSelectionRange();
            return length == 0 ? string.Empty : Text.Substring(start, length);
        }

        private void EnsureMetrics()
        {
            if (_sampleFormattedText != null)
            {
                return;
            }

            _sampleFormattedText = new FormattedText(
                "0",
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                EditorTypeface,
                12,
                GetTextBrush());
            _charWidth = Math.Max(1, _sampleFormattedText.WidthIncludingTrailingWhitespace);
            _lineHeight = Math.Max(1, _sampleFormattedText.Height + 2);
        }

        private void RebuildDocumentState()
        {
            EnsureMetrics();
            BuildLines();
            BuildTokens();
            _selectionAnchor = Math.Clamp(_selectionAnchor < 0 ? _caretIndex : _selectionAnchor, 0, Text.Length);
            _caretIndex = Math.Clamp(_caretIndex, 0, Text.Length);
        }

        private void BuildLines()
        {
            _lines.Clear();
            var text = Text;
            var start = 0;
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    var lineEnd = i > start && text[i - 1] == '\r' ? i - 1 : i;
                    _lines.Add(new LineInfo(start, lineEnd));
                    start = i + 1;
                }
            }

            _lines.Add(new LineInfo(start, text.Length));
        }

        private void BuildTokens()
        {
            _tokens.Clear();
            var text = Text;
            var index = 0;
            while (index < text.Length)
            {
                var ch = text[index];
                if (ch == '"')
                {
                    var start = index;
                    index = ReadJsonString(text, index + 1);
                    _tokens.Add(new Token(start, index - start, IsLikelyPropertyName(text, index) ? TokenKind.Property : TokenKind.String));
                    continue;
                }

                if (char.IsDigit(ch) || ch == '-')
                {
                    var start = index++;
                    while (index < text.Length && IsNumberChar(text[index]))
                    {
                        index++;
                    }

                    _tokens.Add(new Token(start, index - start, TokenKind.Number));
                    continue;
                }

                if (TryReadKeyword(text, index, out var keywordLength))
                {
                    _tokens.Add(new Token(index, keywordLength, TokenKind.Keyword));
                    index += keywordLength;
                    continue;
                }

                if (ch is '{' or '}' or '[' or ']' or ':' or ',')
                {
                    _tokens.Add(new Token(index, 1, TokenKind.Punctuation));
                }

                index++;
            }
        }

        private void DrawText(DrawingContext context, double lineNumberWidth)
        {
            var lineStartX = lineNumberWidth + HorizontalPadding - HorizontalOffset;
            var firstVisibleLine = Math.Max(0, (int)(VerticalOffset / _lineHeight));
            var visibleLineCount = (int)Math.Ceiling(Bounds.Height / _lineHeight) + 1;
            var lastVisibleLine = Math.Min(_lines.Count - 1, firstVisibleLine + visibleLineCount);

            for (var lineIndex = firstVisibleLine; lineIndex <= lastVisibleLine; lineIndex++)
            {
                var line = _lines[lineIndex];
                var y = VerticalPadding + lineIndex * _lineHeight - VerticalOffset;
                var lineText = Text[line.StartOffset..line.EndOffset];
                if (lineText.Length == 0)
                {
                    continue;
                }

                var formatted = new FormattedText(
                    lineText,
                    CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    EditorTypeface,
                    12,
                    GetTextBrush());

                foreach (var token in GetLineTokens(line.StartOffset, line.EndOffset))
                {
                    formatted.SetForegroundBrush(GetBrush(token.Kind), token.Start - line.StartOffset, token.Length);
                }

                context.DrawText(formatted, new Point(lineStartX, y));
            }
        }

        private void DrawLineNumbers(DrawingContext context, double lineNumberWidth)
        {
            var firstVisibleLine = Math.Max(0, (int)(VerticalOffset / _lineHeight));
            var visibleLineCount = (int)Math.Ceiling(Bounds.Height / _lineHeight) + 1;
            var lastVisibleLine = Math.Min(_lines.Count - 1, firstVisibleLine + visibleLineCount);
            var digits = _lines.Count.ToString().Length;

            for (var lineIndex = firstVisibleLine; lineIndex <= lastVisibleLine; lineIndex++)
            {
                var y = VerticalPadding + lineIndex * _lineHeight - VerticalOffset;
                var lineText = (lineIndex + 1).ToString().PadLeft(digits);
                var formatted = new FormattedText(
                    lineText,
                    CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    EditorTypeface,
                    12,
                    GetLineNumberBrush());
                context.DrawText(formatted, new Point(lineNumberWidth - formatted.Width - LineNumberGap, y));
            }
        }

        private void DrawSearchMatches(DrawingContext context, double lineNumberWidth)
        {
            for (var i = 0; i < _owner._searchMatches.Count; i++)
            {
                var match = _owner._searchMatches[i];
                var isCurrent = i == _owner._currentSearchMatchIndex;
                var brush = isCurrent ? GetCurrentSearchBrush() : GetSearchBrush();
                DrawTextRangeHighlight(
                    context,
                    lineNumberWidth,
                    match.Start,
                    match.Start + match.Length,
                    brush,
                    isCurrent ? 2 : 0,
                    isCurrent ? GetCurrentSearchOutlineBrush() : null);
            }
        }

        private void DrawSelection(DrawingContext context, double lineNumberWidth)
        {
            if (!HasSelection)
            {
                return;
            }

            var (start, length) = GetSelectionRange();
            DrawTextRangeHighlight(context, lineNumberWidth, start, start + length, GetSelectionBrush(), 0, null);
        }

        private void DrawTextRangeHighlight(
            DrawingContext context,
            double lineNumberWidth,
            int start,
            int end,
            IBrush brush,
            double inflate,
            IPen? pen)
        {
            var lineStartX = lineNumberWidth + HorizontalPadding - HorizontalOffset;

            for (var lineIndex = 0; lineIndex < _lines.Count; lineIndex++)
            {
                var line = _lines[lineIndex];
                var segmentStart = Math.Max(start, line.StartOffset);
                var segmentEnd = Math.Min(end, line.EndOffset);
                if (segmentEnd < segmentStart)
                {
                    continue;
                }

                var startColumn = segmentStart - line.StartOffset;
                var endColumn = segmentEnd - line.StartOffset;
                if (segmentStart == segmentEnd && segmentEnd != end)
                {
                    endColumn++;
                }

                var rect = new Rect(
                    lineStartX + startColumn * _charWidth,
                    VerticalPadding + lineIndex * _lineHeight - VerticalOffset,
                    Math.Max(2, (endColumn - startColumn) * _charWidth),
                    _lineHeight).Inflate(inflate);
                context.FillRectangle(brush, rect);
                if (pen != null)
                {
                    context.DrawRectangle(pen, rect);
                }
            }
        }

        private void DrawCaret(DrawingContext context, double lineNumberWidth)
        {
            var (lineIndex, column) = GetLineAndColumn(_caretIndex);
            var x = lineNumberWidth + HorizontalPadding + column * _charWidth - HorizontalOffset;
            var y = VerticalPadding + lineIndex * _lineHeight - VerticalOffset;
            context.FillRectangle(GetCaretBrush(), new Rect(x, y, 1.5, _lineHeight));
        }

        private void EnsureCaretVisible()
        {
            var lineNumberWidth = GetLineNumberColumnWidth();
            var (lineIndex, column) = GetLineAndColumn(_caretIndex);
            var caretX = lineNumberWidth + HorizontalPadding + column * _charWidth;
            var caretY = VerticalPadding + lineIndex * _lineHeight;

            if (caretX - HorizontalOffset > Bounds.Width - 20)
            {
                _owner._horizontalScrollBar.Value = caretX - Bounds.Width + 20;
            }
            else if (caretX - HorizontalOffset < lineNumberWidth + 4)
            {
                _owner._horizontalScrollBar.Value = Math.Max(0, caretX - lineNumberWidth - 4);
            }

            if (caretY - VerticalOffset > Bounds.Height - _lineHeight)
            {
                _owner._verticalScrollBar.Value = caretY - Bounds.Height + _lineHeight;
            }
            else if (caretY - VerticalOffset < 0)
            {
                _owner._verticalScrollBar.Value = Math.Max(0, caretY);
            }
        }

        private int GetIndexFromPoint(Point point)
        {
            var lineNumberWidth = GetLineNumberColumnWidth();
            var x = Math.Max(0, point.X + HorizontalOffset - lineNumberWidth - HorizontalPadding);
            var y = Math.Max(0, point.Y + VerticalOffset - VerticalPadding);
            var lineIndex = Math.Clamp((int)(y / _lineHeight), 0, _lines.Count - 1);
            var column = Math.Max(0, (int)Math.Round(x / _charWidth, MidpointRounding.AwayFromZero));
            var line = _lines[lineIndex];
            return Math.Min(line.EndOffset, line.StartOffset + column);
        }

        private (int LineIndex, int Column) GetLineAndColumn(int index)
        {
            for (var i = 0; i < _lines.Count; i++)
            {
                var line = _lines[i];
                if (index <= line.EndOffset)
                {
                    return (i, index - line.StartOffset);
                }
            }

            var last = _lines[^1];
            return (_lines.Count - 1, Math.Max(0, last.EndOffset - last.StartOffset));
        }

        private IEnumerable<Token> GetLineTokens(int start, int end)
        {
            foreach (var token in _tokens)
            {
                var tokenEnd = token.Start + token.Length;
                if (tokenEnd <= start)
                {
                    continue;
                }

                if (token.Start >= end)
                {
                    yield break;
                }

                yield return token;
            }
        }

        private double GetLineNumberColumnWidth()
        {
            EnsureMetrics();
            var digits = Math.Max(2, _lines.Count.ToString().Length);
            return 8 + digits * _charWidth + LineNumberGap + 8;
        }

        private int GetLongestLineLength()
        {
            var max = 1;
            foreach (var line in _lines)
            {
                max = Math.Max(max, line.EndOffset - line.StartOffset);
            }

            return max;
        }

        private static int ReadJsonString(string text, int index)
        {
            var escaped = false;
            while (index < text.Length)
            {
                var ch = text[index++];
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (ch == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (ch == '"')
                {
                    break;
                }
            }

            return index;
        }

        private static bool IsLikelyPropertyName(string text, int index)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index]))
            {
                index++;
            }

            return index < text.Length && text[index] == ':';
        }

        private static bool IsNumberChar(char ch)
        {
            return char.IsDigit(ch) || ch is '.' or 'e' or 'E' or '+' or '-';
        }

        private static bool TryReadKeyword(string text, int index, out int length)
        {
            foreach (var keyword in new[] { "true", "false", "null" })
            {
                if (text.AsSpan(index).StartsWith(keyword, StringComparison.Ordinal) &&
                    (index + keyword.Length == text.Length || !char.IsLetterOrDigit(text[index + keyword.Length])))
                {
                    length = keyword.Length;
                    return true;
                }
            }

            length = 0;
            return false;
        }

        private bool IsLightTheme => ActualThemeVariant == ThemeVariant.Light;

        private IBrush GetBackgroundBrush() => IsLightTheme ? LightBackgroundBrush : DarkBackgroundBrush;

        private IBrush GetLineNumberBackgroundBrush() => IsLightTheme ? LightLineNumberBackgroundBrush : DarkLineNumberBackgroundBrush;

        private IBrush GetTextBrush() => IsLightTheme ? LightTextBrush : DarkTextBrush;

        private IBrush GetLineNumberBrush() => IsLightTheme ? LightLineNumberBrush : DarkLineNumberBrush;

        private IBrush GetSelectionBrush() => IsLightTheme ? LightSelectionBrush : DarkSelectionBrush;

        private IBrush GetSearchBrush() => IsLightTheme ? LightSearchBrush : DarkSearchBrush;

        private IBrush GetCurrentSearchBrush() => IsLightTheme ? LightCurrentSearchBrush : DarkCurrentSearchBrush;

        private IPen GetCurrentSearchOutlineBrush()
            => new Pen(IsLightTheme
                ? new SolidColorBrush(Color.FromRgb(191, 101, 0))
                : new SolidColorBrush(Color.FromRgb(255, 214, 102)));

        private IBrush GetCaretBrush() => IsLightTheme ? LightCaretBrush : DarkCaretBrush;

        private IBrush GetBrush(TokenKind kind) => kind switch
        {
            TokenKind.String => IsLightTheme ? LightStringBrush : DarkStringBrush,
            TokenKind.Property => IsLightTheme ? LightPropertyBrush : DarkPropertyBrush,
            TokenKind.Number => IsLightTheme ? LightNumberBrush : DarkNumberBrush,
            TokenKind.Keyword => IsLightTheme ? LightKeywordBrush : DarkKeywordBrush,
            TokenKind.Punctuation => IsLightTheme ? LightPunctuationBrush : DarkPunctuationBrush,
            _ => GetTextBrush()
        };

        private readonly record struct LineInfo(int StartOffset, int EndOffset);
        private readonly record struct Token(int Start, int Length, TokenKind Kind);

        private enum TokenKind
        {
            Plain,
            String,
            Property,
            Number,
            Keyword,
            Punctuation
        }
    }
}
