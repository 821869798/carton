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

public sealed partial class JsonConfigEditor : Grid
{
    public const double DefaultEditorFontSize = 13;

    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<JsonConfigEditor, string>(
            nameof(Text),
            string.Empty,
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<bool> IsReadOnlyProperty =
        AvaloniaProperty.Register<JsonConfigEditor, bool>(nameof(IsReadOnly));

    private const int MaxHistoryEntries = 200;

    // 撤销/重做快照保存全文副本。除条数上限外再加字符总量上限，
    // 防止大配置反复编辑时累积数百 MB 字符串触发 GC 卡顿。
    private const long MaxHistoryChars = 32L * 1024 * 1024;

    private readonly EditorSurface _surface;
    private readonly ScrollBar _horizontalScrollBar;
    private readonly ScrollBar _verticalScrollBar;
    private readonly JsonEditHistory _history = new(MaxHistoryEntries, MaxHistoryChars);
    private readonly List<SearchMatch> _searchMatches = new();

    private string _searchQuery = string.Empty;
    private int _currentSearchMatchIndex = -1;
    private bool _isInternalTextMutation;
    private bool _searchCaseSensitive;
    private bool _searchWholeWord;
    private bool _searchUseRegex;
    private bool _searchPatternValid = true;
    private bool _isSearchOpen;
    private CancellationTokenSource? _searchDebounceTokenSource;

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

    public bool CanUndo => _history.CanUndo;

    public bool CanRedo => _history.CanRedo;

    public bool IsSearchOpen => _isSearchOpen;

    public bool SearchCaseSensitive => _searchCaseSensitive;

    public bool SearchWholeWord => _searchWholeWord;

    public bool SearchUseRegex => _searchUseRegex;

    public bool HasSearchMatches => _searchMatches.Count > 0 && _currentSearchMatchIndex >= 0;

    public bool IsSearchPatternValid => _searchPatternValid;

    public double EditorFontSize
    {
        get => _surface.FontSize;
        set
        {
            _surface.SetFontSize(value);
            UpdateScrollBars();
        }
    }

    public string SearchStatusText => !_searchPatternValid
        ? "ERR"
        : HasSearchMatches
            ? $"{_currentSearchMatchIndex + 1}/{_searchMatches.Count}"
            : "0/0";

    public void Undo()
    {
        if (_history.TryUndo(_surface.TextValue, _surface.CaretState, out var newText, out var newState))
        {
            _surface.ApplyHistoryResult(newText, newState);
            UpdateSearchResults(selectCurrentMatch: false);
            UpdateScrollBars();
            RaiseEditorStateChanged();
        }
    }

    public void Redo()
    {
        if (_history.TryRedo(_surface.TextValue, _surface.CaretState, out var newText, out var newState))
        {
            _surface.ApplyHistoryResult(newText, newState);
            UpdateSearchResults(selectCurrentMatch: false);
            UpdateScrollBars();
            RaiseEditorStateChanged();
        }
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
        _searchDebounceTokenSource?.Cancel();
        _searchDebounceTokenSource?.Dispose();
        _searchDebounceTokenSource = new CancellationTokenSource();
        var token = _searchDebounceTokenSource.Token;
        _ = DebounceSearchAsync(token);
    }

    private async Task DebounceSearchAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(150, token);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        if (!token.IsCancellationRequested)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (token.IsCancellationRequested) return;
                UpdateSearchResults(selectCurrentMatch: true);
                RaiseEditorStateChanged();
            });
        }
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
            var isExternalChange = !_isInternalTextMutation;
            if (isExternalChange)
            {
                ClearHistory();
                ResetSearchForNewText();
            }

            _surface.OnTextChanged();

            // 内部编辑/撤销/重做由各自的调用方（ApplyEdit、Undo、Redo）刷新搜索与滚动条，
            // 这里只处理外部赋值（如加载配置），避免每次按键重复全文扫描。
            if (isExternalChange)
            {
                UpdateSearchResults(selectCurrentMatch: false);
                UpdateScrollBars();
                RaiseEditorStateChanged();
            }
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

    private void RecordEdit(TextEdit edit)
    {
        _history.Record(edit);
        RaiseEditorStateChanged();
    }

    private void ClearHistory()
    {
        if (!_history.CanUndo && !_history.CanRedo)
        {
            return;
        }

        _history.Clear();
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

    private readonly record struct SearchMatch(int Start, int Length);

    private sealed partial class EditorSurface : Control
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
        private const double DefaultFontSize = DefaultEditorFontSize;
        private const double MinFontSize = 10;
        private const double MaxFontSize = 24;
        private const double FontSizeStep = 1;

        private readonly JsonConfigEditor _owner;
        private readonly List<JsonLine> _lines = new();
        private FormattedText? _sampleFormattedText;
        private double _charWidth = 8;
        private double _lineHeight = 18;
        private double _baseline = 14;
        private int _caretIndex;
        private int _selectionAnchor = -1;
        private bool _pointerSelecting;
        private bool _internalTextUpdate;
        private double _horizontalOffset;
        private double _verticalOffset;
        private double _fontSize = DefaultFontSize;
        private int _longestLineLength = 1;

        public EditorSurface(JsonConfigEditor owner)
        {
            _owner = owner;
            Focusable = true;
            Cursor = new Cursor(StandardCursorType.Ibeam);
            ActualThemeVariantChanged += (_, _) =>
            {
                _sampleFormattedText = null;
                _glyphCache.Clear();
                InvalidateVisual();
            };
            RebuildDocumentState();
        }

        public int CaretIndex => _caretIndex;

        public double FontSize => _fontSize;

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

        public string TextValue => Text;

        public CaretState CaretState
            => new(_caretIndex, _selectionAnchor, HorizontalOffset, VerticalOffset);

        // 应用撤销/重做得到的新文本与恢复的光标状态。文本变更不再走历史记录。
        public void ApplyHistoryResult(string newText, CaretState state)
        {
            _internalTextUpdate = true;
            _owner._isInternalTextMutation = true;
            _owner.Text = newText;
            _owner._isInternalTextMutation = false;
            _internalTextUpdate = false;
            _caretIndex = Math.Clamp(state.Caret, 0, Text.Length);
            _selectionAnchor = Math.Clamp(state.Anchor, -1, Text.Length);
            _owner._horizontalScrollBar.Value = Math.Clamp(state.HOffset, 0, _owner._horizontalScrollBar.Maximum);
            _owner._verticalScrollBar.Value = Math.Clamp(state.VOffset, 0, _owner._verticalScrollBar.Maximum);
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
            var lineIndex = GetLineIndexForOffset(targetIndex);
            var column = OffsetToDisplayColumn(_lines[lineIndex], targetIndex);
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

            var contentRect = new Rect(
                lineNumberWidth,
                0,
                Math.Max(0, bounds.Width - lineNumberWidth),
                bounds.Height);
            using (context.PushClip(contentRect))
            {
                DrawSearchMatches(context, lineNumberWidth);
                DrawSelection(context, lineNumberWidth);
                DrawText(context, lineNumberWidth);
                if (IsFocused)
                {
                    DrawCaret(context, lineNumberWidth);
                }
            }

            context.FillRectangle(GetLineNumberBackgroundBrush(), new Rect(0, 0, lineNumberWidth, bounds.Height));
            DrawLineNumbers(context, lineNumberWidth);
        }

        public void SetFontSize(double fontSize)
        {
            var newFontSize = Math.Clamp(fontSize, MinFontSize, MaxFontSize);
            if (Math.Abs(newFontSize - _fontSize) < double.Epsilon)
            {
                return;
            }

            _fontSize = newFontSize;
            _sampleFormattedText = null;
            _glyphCache.Clear();
            RebuildDocumentState();
            EnsureCaretVisible();
            _owner.UpdateScrollBars();
            InvalidateVisual();
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

            // 按码点删除：代理对（emoji 等）一次删除两个 char，避免切出半个非法字符。
            if (backspace && _caretIndex > 0)
            {
                var prev = PrevCharIndex(_caretIndex);
                ApplyEdit(prev, _caretIndex - prev, string.Empty);
            }
            else if (!backspace && _caretIndex < Text.Length)
            {
                var next = NextCharIndex(_caretIndex);
                ApplyEdit(_caretIndex, next - _caretIndex, string.Empty);
            }
        }

        // 以 UTF-16 偏移为坐标，按码点前后移动一个位置（代理对算一步）。
        private int NextCharIndex(int index)
        {
            var text = Text;
            if (index < text.Length && char.IsHighSurrogate(text[index]) &&
                index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
            {
                return index + 2;
            }

            return Math.Min(text.Length, index + 1);
        }

        private int PrevCharIndex(int index)
        {
            var text = Text;
            if (index >= 2 && char.IsLowSurrogate(text[index - 1]) && char.IsHighSurrogate(text[index - 2]))
            {
                return index - 2;
            }

            return Math.Max(0, index - 1);
        }

        private void ReplaceSelection(string replacement)
        {
            var (start, length) = GetSelectionRange();
            ApplyEdit(start, length, replacement);
        }

        // 统一编辑入口：在 offset 处删除 removeLength 个字符并插入 insertText。
        // 捕获 delta 记入历史栈（不再存全文快照），再以增量方式重建文本。
        private void ApplyEdit(int offset, int removeLength, string insertText)
        {
            var current = Text;
            var removed = removeLength > 0 ? current.Substring(offset, removeLength) : string.Empty;
            // 文本无实际变化（含「删除内容与插入内容相同」的替换）时不记历史、不清重做栈，
            // 仅折叠选区并把光标移到插入末尾，与旧 SetTextInternal 的 string.Equals 守卫一致。
            if (string.Equals(removed, insertText, StringComparison.Ordinal))
            {
                _caretIndex = Math.Clamp(offset + insertText.Length, 0, current.Length);
                _selectionAnchor = _caretIndex;
                EnsureCaretVisible();
                InvalidateVisual();
                return;
            }

            var newText = current.Remove(offset, removeLength).Insert(offset, insertText);
            // Restore 记录「编辑前」的光标/视口，供撤销恢复到本次编辑发生前的状态。
            var restore = new CaretState(_caretIndex, _selectionAnchor, HorizontalOffset, VerticalOffset);
            _owner.RecordEdit(new TextEdit(offset, removed, insertText, restore));
            CommitText(newText, offset + insertText.Length);
        }

        // 写入新文本并把光标置于指定位置（折叠选区）。不记历史——历史由调用方决定。
        private void CommitText(string newText, int newCaretIndex)
        {
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
            _owner.RaiseEditorStateChanged();
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
                _fontSize,
                GetTextBrush());
            _charWidth = Math.Max(1, _sampleFormattedText.WidthIncludingTrailingWhitespace);
            _lineHeight = Math.Max(1, _sampleFormattedText.Height + 2);
            _baseline = _sampleFormattedText.Baseline;
        }

        private void RebuildDocumentState()
        {
            EnsureMetrics();
            BuildLines();
            // 着色按可见行惰性进行（见 GetLineTokens），不再在此预扫全文 token，
            // 故大文件编辑/加载时这里只是 O(n) 切行，无全文着色开销。
            _selectionAnchor = Math.Clamp(_selectionAnchor < 0 ? _caretIndex : _selectionAnchor, 0, Text.Length);
            _caretIndex = Math.Clamp(_caretIndex, 0, Text.Length);
        }

        private void BuildLines()
        {
            JsonSyntax.BuildLines(Text, _lines, out _longestLineLength);
        }
    }
}
