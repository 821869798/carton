using System;
using System.Collections.Generic;
using System.Globalization;
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
        private void DrawText(DrawingContext context, double lineNumberWidth)
        {
            var text = Text;
            var (firstVisibleLine, lastVisibleLine) = GetVisibleLineRange();
            var viewportStartX = lineNumberWidth + HorizontalPadding;

            // 可见列范围：绘制按列网格定位（与光标/点击/选区同一套坐标），
            // 只画落在视口内的列，避免逐字符遍历整条超长行。
            var firstVisibleCol = (int)Math.Max(0, HorizontalOffset / _charWidth) - 1;
            if (firstVisibleCol < 0) firstVisibleCol = 0;
            var lastVisibleCol = firstVisibleCol + (int)Math.Ceiling(Bounds.Width / _charWidth) + 2;
            var textBrush = GetTextBrush();

            for (var lineIndex = firstVisibleLine; lineIndex <= lastVisibleLine; lineIndex++)
            {
                var line = _lines[lineIndex];
                if (line.EndOffset <= line.StartOffset)
                {
                    continue;
                }

                var y = VerticalPadding + lineIndex * _lineHeight - VerticalOffset;
                DrawLineGlyphs(context, lineIndex, line, text, viewportStartX, y, firstVisibleCol, lastVisibleCol, textBrush);
            }
        }

        // 逐字符按列网格绘制：每个字符落在 col*_charWidth 处，CJK 占两列。
        // 渲染坐标与 OffsetToDisplayColumn / 光标 / 命中测试完全一致，从根源消除偏移与重叠。
        private void DrawLineGlyphs(
            DrawingContext context,
            int lineIndex,
            JsonLine line,
            string text,
            double viewportStartX,
            double y,
            int firstVisibleCol,
            int lastVisibleCol,
            IBrush textBrush)
        {
            // 先用行 token 索引建立「字符偏移 -> 颜色」的快速判定。
            using var tokenEnumerator = GetLineTokens(lineIndex).GetEnumerator();
            var hasToken = tokenEnumerator.MoveNext();

            var col = 0;
            var i = line.StartOffset;
            while (i < line.EndOffset)
            {
                var ch = text[i];
                var (w, charCount) = JsonSyntax.DisplayWidthAt(text, i);

                if (col >= lastVisibleCol)
                {
                    break;
                }

                if (col + w <= firstVisibleCol || ch == ' ' || ch == '\t')
                {
                    col += w;
                    i += charCount;
                    continue;
                }

                // 推进 token 游标到覆盖当前偏移的 token（token 已按 Start 升序）。
                while (hasToken)
                {
                    var t = tokenEnumerator.Current;
                    if (t.Start + t.Length <= i)
                    {
                        hasToken = tokenEnumerator.MoveNext();
                        continue;
                    }
                    break;
                }

                var brush = textBrush;
                if (hasToken)
                {
                    var t = tokenEnumerator.Current;
                    if (i >= t.Start && i < t.Start + t.Length)
                    {
                        brush = GetBrush(t.Kind);
                    }
                }

                // 整个码点（含代理对的两个 char）作为一个字形绘制，否则 emoji 会裂成乱码。
                var glyphText = text.Substring(i, charCount);
                var glyph = GetGlyph(glyphText, brush);
                var x = viewportStartX + col * _charWidth - HorizontalOffset;
                // 在该字符的列格内水平居中（西文 1 格、CJK/emoji 2 格），观感更稳。
                var cellWidth = w * _charWidth;
                var offsetX = Math.Max(0, (cellWidth - glyph.Width) / 2);
                context.DrawText(glyph, new Point(x + offsetX, y + _baseline - glyph.Baseline));

                col += w;
                i += charCount;
            }
        }

        // 单字形（码点字符串 + 颜色）缓存：JSON 文本字符高度重复，缓存命中率极高，
        // 避免每帧为每个字符重新创建 FormattedText。字号或主题变更时清空。
        private readonly Dictionary<(string Glyph, IBrush Brush), FormattedText> _glyphCache = new();

        private FormattedText GetGlyph(string glyphText, IBrush brush)
        {
            var key = (glyphText, brush);
            if (_glyphCache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var glyph = new FormattedText(
                glyphText,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                EditorTypeface,
                _fontSize,
                brush);
            _glyphCache[key] = glyph;
            return glyph;
        }

        private void DrawLineNumbers(DrawingContext context, double lineNumberWidth)
        {
            var (firstVisibleLine, lastVisibleLine) = GetVisibleLineRange();
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
                    _fontSize,
                    GetLineNumberBrush());
                context.DrawText(formatted, new Point(lineNumberWidth - formatted.Width - LineNumberGap, y));
            }
        }

        private void AdjustFontSize(double delta)
        {
            SetFontSize(_fontSize + delta);
        }

        private void DrawSearchMatches(DrawingContext context, double lineNumberWidth)
        {
            if (_owner._searchMatches.Count == 0 || _lines.Count == 0)
            {
                return;
            }

            var (firstVisibleLine, lastVisibleLine) = GetVisibleLineRange();
            if (lastVisibleLine < firstVisibleLine)
            {
                return;
            }

            var visibleStart = _lines[firstVisibleLine].StartOffset;
            var visibleEnd = _lines[lastVisibleLine].EndOffset;

            for (var i = 0; i < _owner._searchMatches.Count; i++)
            {
                var match = _owner._searchMatches[i];
                if (match.Start >= visibleEnd)
                {
                    break;
                }
                if (match.Start + match.Length <= visibleStart)
                {
                    continue;
                }

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
            var (firstVisibleLine, lastVisibleLine) = GetVisibleLineRange();

            for (var lineIndex = firstVisibleLine; lineIndex <= lastVisibleLine; lineIndex++)
            {
                var line = _lines[lineIndex];
                if (line.StartOffset >= end)
                {
                    break;
                }
                if (line.EndOffset < start)
                {
                    continue;
                }

                var segmentStart = Math.Max(start, line.StartOffset);
                var segmentEnd = Math.Min(end, line.EndOffset);
                if (segmentEnd < segmentStart)
                {
                    continue;
                }

                var startColumn = OffsetToDisplayColumn(line, segmentStart);
                var endColumn = OffsetToDisplayColumn(line, segmentEnd);
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
            var lineIndex = GetLineIndexForOffset(_caretIndex);
            var column = OffsetToDisplayColumn(_lines[lineIndex], _caretIndex);
            var x = lineNumberWidth + HorizontalPadding + column * _charWidth - HorizontalOffset;
            var y = VerticalPadding + lineIndex * _lineHeight - VerticalOffset;
            context.FillRectangle(GetCaretBrush(), new Rect(x, y, 1.5, _lineHeight));
        }

        private void EnsureCaretVisible()
        {
            var lineNumberWidth = GetLineNumberColumnWidth();
            var lineIndex = GetLineIndexForOffset(_caretIndex);
            var column = OffsetToDisplayColumn(_lines[lineIndex], _caretIndex);
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
            var targetColumn = Math.Max(0, (int)Math.Round(x / _charWidth, MidpointRounding.AwayFromZero));
            return DisplayColumnToOffset(_lines[lineIndex], targetColumn);
        }

        // 返回字符偏移所在的行号。列定位一律走显示列（OffsetToDisplayColumn），
        // 不再暴露字符列，避免与渲染口径混用导致 CJK 行的光标偏移。
        // _lines 的 EndOffset 升序，用二分找首个 EndOffset >= index 的行（大文件下 O(log n)）。
        private int GetLineIndexForOffset(int index)
        {
            var lo = 0;
            var hi = _lines.Count - 1;
            while (lo < hi)
            {
                var mid = (lo + hi) >> 1;
                if (_lines[mid].EndOffset < index)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            return lo;
        }

        // 将字符偏移换算成显示列（CJK/全角记 2 列），与渲染、extent 的列宽口径一致。
        private int OffsetToDisplayColumn(JsonLine line, int offset)
            => JsonSyntax.OffsetToDisplayColumn(Text, line, offset);

        // 将显示列换算回字符偏移，落在宽字符中间时就近吸附到字符边界。
        private int DisplayColumnToOffset(JsonLine line, int targetColumn)
            => JsonSyntax.DisplayColumnToOffset(Text, line, targetColumn);

        // 按需对单行着色到复用缓冲区。只对可见行调用（每帧约数十行），
        // 故无需跨帧缓存或全文 token 表。标准 JSON token 不跨行，按行着色与全文等价。
        private readonly List<JsonToken> _lineTokenBuffer = new();

        private List<JsonToken> GetLineTokens(int lineIndex)
        {
            JsonSyntax.TokenizeLine(Text, _lines[lineIndex], _lineTokenBuffer);
            return _lineTokenBuffer;
        }

        private double GetLineNumberColumnWidth()
        {
            EnsureMetrics();
            var digits = Math.Max(2, _lines.Count.ToString().Length);
            return 8 + digits * _charWidth + LineNumberGap + 8;
        }

        private int GetLongestLineLength()
        {
            return _longestLineLength;
        }

        private (int FirstVisibleLine, int LastVisibleLine) GetVisibleLineRange()
        {
            if (_lines.Count == 0)
            {
                return (0, -1);
            }

            var firstVisibleLine = Math.Max(0, (int)(VerticalOffset / _lineHeight));
            var visibleLineCount = (int)Math.Ceiling(Bounds.Height / _lineHeight) + 1;
            var lastVisibleLine = Math.Min(_lines.Count - 1, firstVisibleLine + visibleLineCount);
            return (firstVisibleLine, lastVisibleLine);
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

        private IBrush GetBrush(JsonTokenKind kind) => kind switch
        {
            JsonTokenKind.String => IsLightTheme ? LightStringBrush : DarkStringBrush,
            JsonTokenKind.Property => IsLightTheme ? LightPropertyBrush : DarkPropertyBrush,
            JsonTokenKind.Number => IsLightTheme ? LightNumberBrush : DarkNumberBrush,
            JsonTokenKind.Keyword => IsLightTheme ? LightKeywordBrush : DarkKeywordBrush,
            JsonTokenKind.Punctuation => IsLightTheme ? LightPunctuationBrush : DarkPunctuationBrush,
            _ => GetTextBrush()
        };
    }
}
