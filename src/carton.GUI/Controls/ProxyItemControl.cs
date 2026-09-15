using System;
using System.Collections.Generic;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.VisualTree;
using carton.ViewModels;

namespace carton.GUI.Controls;

public sealed class ProxyItemControl : Control
{
    private const double CardHeight = 62;
    private const double CornerRadius = 12;
    private const double HorizontalPadding = 14;
    private const double TopPadding = 12;
    private const double TypeTopGap = 10;
    private const double TestButtonIconWidth = 44;
    private const double TestButtonTextWidth = 64;
    private const double TestButtonHeight = 24;
    private const double TestButtonRight = 14;
    private const double TestButtonBottom = 8;
    private const double BorderThickness = 1;
    private const double ToolTipMinWidth = 72;
    private const double ToolTipMaxWidth = 280;
    private const double ToolTipAverageCharWidth = 7;
    private const double ToolTipHorizontalPadding = 24;
    private const double ToolTipOffset = 12;

    private static readonly FontFamily TextFontFamily = FontFamily.Default;
    private static readonly FontFamily EmojiFontFamily = new("avares://carton/Assets/Fonts#Twemoji COLRv0,Segoe UI Emoji");
    private static readonly Typeface TitleTypeface = new(TextFontFamily, FontStyle.Normal, FontWeight.SemiBold, FontStretch.Normal);
    private static readonly Typeface EmojiTypeface = new(EmojiFontFamily);
    private static readonly Typeface MetadataTypeface = new(TextFontFamily);
    private static readonly Typeface DelayTypeface = new(TextFontFamily, FontStyle.Normal, FontWeight.SemiBold, FontStretch.Normal);
    private static readonly Geometry TestIconGeometry = Geometry.Parse("M13 2L5 13H11L10 22L18 11H12L13 2Z");
    private static readonly SolidColorBrush LowLatencyBrush = new(Color.Parse("#16A34A"));
    private static readonly SolidColorBrush MediumLatencyBrush = new(Color.Parse("#CA8A04"));
    private static readonly SolidColorBrush HighLatencyBrush = new(Color.Parse("#DC2626"));
    private static readonly SolidColorBrush TimeoutLatencyBrush = new(Color.Parse("#DB2777"));
    private static readonly SolidColorBrush EmptyLatencyBrush = new(Colors.Gray);
    private static readonly Pen TransparentBorderPen = new(Brushes.Transparent, 1);
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    private readonly EmojiTextSlot _titleText = new();
    private readonly TextSlot _typeText = new();
    private readonly TextSlot _delayText = new();
    private OutboundItemViewModel? _item;
    private SolidColorBrush? _selectedOverlayBrush;
    private Pen? _selectedBorderPen;
    private Color? _selectedOverlayColor;
    private bool _isPointerOver;
    private bool _isTestButtonHot;
    private bool _toolTipInitialized;

    public ProxyItemControl()
    {
        MinHeight = CardHeight;
        ClipToBounds = true;
        Focusable = false;
        Cursor = HandCursor;
        ToolTip.SetShowDelay(this, 1500);
        ToolTip.SetBetweenShowDelay(this, -1);
        ToolTip.SetPlacement(this, PlacementMode.Right);
        ToolTip.SetHorizontalOffset(this, ToolTipOffset);
        ActualThemeVariantChanged += OnActualThemeVariantChanged;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var desiredWidth = double.IsInfinity(availableSize.Width) ? 220 : availableSize.Width;
        return new Size(desiredWidth, CardHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        return new Size(finalSize.Width, Math.Max(CardHeight, finalSize.Height));
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        SetItem(DataContext as OutboundItemViewModel);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        SetItem(DataContext as OutboundItemViewModel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ClearItem();
        ClearToolTip();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        _isPointerOver = true;
        UpdateTestButtonHot(e.GetPosition(this));
        EnsureToolTip();
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _isPointerOver = false;
        _isTestButtonHot = false;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        UpdateTestButtonHot(e.GetPosition(this));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (_item == null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (GetTestButtonRect(_item).Contains(e.GetPosition(this)))
        {
            ExecuteTestDelay();
        }
        else
        {
            ExecuteSelectOutbound();
        }

        e.Handled = true;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var item = _item;
        if (item == null || Bounds.Width <= 0)
        {
            return;
        }

        var cardRect = new Rect(0, 0, Bounds.Width, Math.Max(CardHeight, Bounds.Height));
        var contentRect = cardRect.Deflate(BorderThickness);
        var borderRect = cardRect.Deflate(BorderThickness / 2);
        var background = ResolveCardBackground(item);

        context.DrawRectangle(background, null, cardRect, CornerRadius, CornerRadius);

        if (item.IsSelected)
        {
            context.DrawRectangle(
                GetSelectedOverlayBrush(),
                null,
                contentRect,
                CornerRadius,
                CornerRadius);
            context.DrawRectangle(
                null,
                GetSelectedBorderPen(),
                borderRect,
                CornerRadius,
                CornerRadius);
        }
        else
        {
            context.DrawRectangle(null, TransparentBorderPen, borderRect, CornerRadius, CornerRadius);
        }

        var delayBaseline = DrawDelayButton(context, item);
        DrawContent(context, item, cardRect, delayBaseline);
    }

    private void DrawContent(DrawingContext context, OutboundItemViewModel item, Rect cardRect, double? delayBaseline)
    {
        var textBrush = GetBrush("CartonControlForegroundBaseHighBrush", Brushes.Black);
        var metadataBrush = GetBrush("CartonControlForegroundBaseMediumBrush", Brushes.Gray);
        var textWidth = Math.Max(0, cardRect.Width - HorizontalPadding * 2);

        var titleHeight = _titleText.Draw(context, item.Tag, 13, textBrush, textWidth, new Point(HorizontalPadding, TopPadding));

        var typeLayout = _typeText.Get(item.Type, MetadataTypeface, 10, metadataBrush, textWidth);
        var typeY = delayBaseline.HasValue
            ? delayBaseline.Value - typeLayout.Baseline
            : TopPadding + titleHeight + TypeTopGap;
        var typeOrigin = new Point(HorizontalPadding, typeY);
        typeLayout.Draw(context, typeOrigin);
    }

    private double? DrawDelayButton(DrawingContext context, OutboundItemViewModel item)
    {
        var rect = GetTestButtonRect(item);
        if (_isTestButtonHot)
        {
            context.DrawRectangle(GetBrush("CartonControlBackgroundBaseLowBrush", Brushes.Transparent), null, rect, 8, 8);
        }

        if (item.ShowDelayText)
        {
            // Colour comes from the SAME state as the text (DelayState), never from an
            // independent if-chain: IsDelayTimeout is set while a node's own test is
            // still running, and reading it here used to paint the "..." dots pink.
            var delayBrush = item.DelayState switch
            {
                DelayState.Latency => ResolveLatencyBrush(item.Delay),
                DelayState.Timeout => TimeoutLatencyBrush,
                _ => GetBrush("CartonControlForegroundBaseMediumBrush", Brushes.Gray),
            };
            var layout = _delayText.Get(item.DelayDisplay, DelayTypeface, 11, delayBrush, Math.Max(0, rect.Width - 10));
            var point = new Point(
                rect.X + Math.Max(0, (rect.Width - layout.Width) / 2),
                rect.Y + Math.Max(0, (rect.Height - layout.Height) / 2));
            layout.Draw(context, point);
            return point.Y + layout.Baseline;
        }

        var iconBrush = GetBrush("CartonControlForegroundBaseMediumBrush", Brushes.Gray);
        var iconBounds = TestIconGeometry.Bounds;
        var scale = Math.Min(12 / iconBounds.Width, 12 / iconBounds.Height);
        var center = rect.Center;

        var offsetX = center.X - iconBounds.Center.X * scale;
        var offsetY = center.Y - iconBounds.Center.Y * scale;

        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offsetX, offsetY)))
        {
            context.DrawGeometry(iconBrush, null, TestIconGeometry);
        }

        return null;
    }

    private void SetItem(OutboundItemViewModel? item)
    {
        if (ReferenceEquals(_item, item))
        {
            return;
        }

        if (_item != null)
        {
            _item.PropertyChanged -= OnItemPropertyChanged;
        }

        _item = item;
        if (_item != null)
        {
            _item.PropertyChanged += OnItemPropertyChanged;
        }

        ClearTextSlots();
        ClearToolTip();
        InvalidateVisual();
    }

    private void ClearItem()
    {
        if (_item != null)
        {
            _item.PropertyChanged -= OnItemPropertyChanged;
        }

        _item = null;
        _isPointerOver = false;
        _isTestButtonHot = false;
        ClearTextSlots();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OutboundItemViewModel.Tag) or nameof(OutboundItemViewModel.Type))
        {
            ClearTextSlots();
            ClearToolTip();
        }
        else if (e.PropertyName is nameof(OutboundItemViewModel.Delay) or nameof(OutboundItemViewModel.IsTesting) or nameof(OutboundItemViewModel.IsDelayTimeout))
        {
            _delayText.Clear();
        }

        InvalidateVisual();
    }

    private void ExecuteSelectOutbound()
    {
        var item = _item;
        if (item?.SelectOutboundCommand == null || string.IsNullOrWhiteSpace(item.Tag))
        {
            return;
        }

        if (item.SelectOutboundCommand.CanExecute(item.Tag))
        {
            _ = item.SelectOutboundCommand.ExecuteAsync(item.Tag);
        }
    }

    private void ExecuteTestDelay()
    {
        var item = _item;
        if (item?.TestDelayCommand != null && item.TestDelayCommand.CanExecute(null))
        {
            item.TestDelayCommand.Execute(null);
        }
    }

    private void UpdateTestButtonHot(Point point)
    {
        var isHot = GetTestButtonRect(_item).Contains(point);
        if (_isTestButtonHot == isHot)
        {
            return;
        }

        _isTestButtonHot = isHot;
        InvalidateVisual();
    }

    private Rect GetTestButtonRect(OutboundItemViewModel? item)
    {
        var height = Math.Max(CardHeight, Bounds.Height);
        var width = item?.ShowDelayText == true ? TestButtonTextWidth : TestButtonIconWidth;
        return new Rect(
            Math.Max(0, Bounds.Width - TestButtonRight - width),
            Math.Max(0, height - TestButtonBottom - TestButtonHeight),
            width,
            TestButtonHeight);
    }

    private void EnsureToolTip()
    {
        if (_toolTipInitialized || _item == null || string.IsNullOrWhiteSpace(_item.Tag))
        {
            return;
        }

        UpdateToolTipPlacement();
        ToolTip.SetTip(this, new TextBlock
        {
            Text = _item.Tag,
            MaxWidth = ToolTipMaxWidth,
            TextWrapping = TextWrapping.Wrap,
            IsHitTestVisible = false
        });
        _toolTipInitialized = true;
    }

    private void ClearToolTip()
    {
        if (!_toolTipInitialized)
        {
            return;
        }

        ToolTip.SetTip(this, null);
        _toolTipInitialized = false;
    }

    private void UpdateToolTipPlacement()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        var topLeft = topLevel == null ? null : this.TranslatePoint(new Point(0, 0), topLevel);
        if (topLevel == null || topLeft == null || _item == null)
        {
            return;
        }

        var preferredWidth = EstimateToolTipWidth(_item.Tag);
        var availableRight = topLevel.Bounds.Width - (topLeft.Value.X + Bounds.Width);
        var availableLeft = topLeft.Value.X;
        var requiredWidth = preferredWidth + ToolTipOffset;
        var placeLeft = availableRight < requiredWidth && availableLeft > availableRight;

        ToolTip.SetPlacement(this, placeLeft ? PlacementMode.Left : PlacementMode.Right);
        ToolTip.SetHorizontalOffset(this, placeLeft ? -ToolTipOffset : ToolTipOffset);
    }

    private IBrush ResolveCardBackground(OutboundItemViewModel item)
    {
        if (item.IsSelected)
        {
            return GetBrush("CartonControlBackgroundChromeHighBrush", Brushes.White);
        }

        if (_isPointerOver)
        {
            return GetBrush("CartonControlBackgroundBaseLowBrush", Brushes.Transparent);
        }

        return GetBrush("CartonControlBackgroundAltMediumHighBrush", Brushes.Transparent);
    }

    private IBrush ResolveLatencyBrush(int delay)
    {
        if (delay <= 0)
        {
            return EmptyLatencyBrush;
        }

        if (delay < 400)
        {
            return LowLatencyBrush;
        }

        return delay < 800 ? MediumLatencyBrush : HighLatencyBrush;
    }

    private IBrush GetBrush(string key, IBrush fallback)
    {
        return this.TryFindResource(key, ActualThemeVariant, out var resource) && resource is IBrush brush
            ? brush
            : fallback;
    }

    private IBrush GetSelectedOverlayBrush()
    {
        var source = GetBrush("CartonAccentBrush", EmptyLatencyBrush);
        if (source is not ISolidColorBrush solidBrush)
        {
            return source;
        }

        var color = solidBrush.Color;
        if (_selectedOverlayBrush != null && _selectedOverlayColor == color)
        {
            return _selectedOverlayBrush;
        }

        _selectedOverlayColor = color;
        _selectedOverlayBrush = new SolidColorBrush(Color.FromArgb(31, color.R, color.G, color.B));
        return _selectedOverlayBrush;
    }

    private Pen GetSelectedBorderPen()
    {
        if (_selectedBorderPen != null)
        {
            return _selectedBorderPen;
        }

        var brush = GetBrush("CartonAccentBorderBrush", GetBrush("CartonAccentBrush", EmptyLatencyBrush));
        _selectedBorderPen = new Pen(brush, BorderThickness);
        return _selectedBorderPen;
    }

    private static double EstimateToolTipWidth(string text)
    {
        var estimatedWidth = text.Length * ToolTipAverageCharWidth + ToolTipHorizontalPadding;
        return Math.Clamp(estimatedWidth, ToolTipMinWidth, ToolTipMaxWidth);
    }

    private void OnActualThemeVariantChanged(object? sender, EventArgs e)
    {
        _selectedOverlayBrush = null;
        _selectedBorderPen = null;
        _selectedOverlayColor = null;
        ClearTextSlots();
        InvalidateVisual();
    }

    private void ClearTextSlots()
    {
        _titleText.Clear();
        _typeText.Clear();
        _delayText.Clear();
    }

    private sealed class TextSlot
    {
        private string? _text;
        private Typeface _typeface;
        private double _fontSize;
        private double _maxWidth;
        private IBrush? _brush;
        private TextLayout? _layout;

        public TextLayout Get(string? text, Typeface typeface, double fontSize, IBrush brush, double maxWidth)
        {
            text ??= string.Empty;
            maxWidth = Math.Max(0, maxWidth);

            if (_layout != null &&
                _text == text &&
                _typeface == typeface &&
                Math.Abs(_fontSize - fontSize) < 0.001 &&
                Math.Abs(_maxWidth - maxWidth) < 0.5 &&
                ReferenceEquals(_brush, brush))
            {
                return _layout;
            }

            _text = text;
            _typeface = typeface;
            _fontSize = fontSize;
            _maxWidth = maxWidth;
            _brush = brush;

            var oldLayout = _layout;
            _layout = null;
            oldLayout?.Dispose();
            _layout = new TextLayout(
                text,
                typeface,
                fontSize,
                brush,
                TextAlignment.Left,
                TextWrapping.NoWrap,
                TextTrimming.CharacterEllipsis,
                null,
                FlowDirection.LeftToRight,
                maxWidth,
                CardHeight,
                double.NaN,
                0,
                1);
            return _layout;
        }

        public void Clear()
        {
            _text = null;
            _brush = null;
            var oldLayout = _layout;
            _layout = null;
            oldLayout?.Dispose();
        }
    }

    private sealed class EmojiTextSlot
    {
        private readonly List<Segment> _segments = new();
        private string? _text;
        private double _fontSize;
        private double _maxWidth;
        private IBrush? _brush;
        private TextLayout? _plainLayout;
        private double _height;
        private double _baseline;

        public double Draw(DrawingContext context, string? text, double fontSize, IBrush brush, double maxWidth, Point origin)
        {
            Ensure(text, fontSize, brush, maxWidth);
            var clip = new Rect(origin.X, origin.Y, Math.Max(0, maxWidth), CardHeight);

            using (context.PushClip(clip))
            {
                if (_plainLayout != null)
                {
                    _plainLayout.Draw(context, origin);
                    return _height;
                }

                var x = origin.X;
                var maxX = origin.X + maxWidth;
                for (var i = 0; i < _segments.Count; i++)
                {
                    var segment = _segments[i];
                    if (x >= maxX)
                    {
                        break;
                    }

                    segment.Layout.Draw(context, new Point(x, origin.Y + Math.Max(0, _baseline - segment.Baseline)));
                    x += segment.Layout.Width;
                }
            }

            return _height;
        }

        public void Clear()
        {
            _text = null;
            _brush = null;
            _height = 0;
            _baseline = 0;
            var oldLayout = _plainLayout;
            _plainLayout = null;
            oldLayout?.Dispose();

            for (var i = 0; i < _segments.Count; i++)
            {
                _segments[i].Layout.Dispose();
            }

            _segments.Clear();
        }

        private void Ensure(string? text, double fontSize, IBrush brush, double maxWidth)
        {
            text ??= string.Empty;
            maxWidth = Math.Max(0, maxWidth);

            if (_text == text &&
                Math.Abs(_fontSize - fontSize) < 0.001 &&
                Math.Abs(_maxWidth - maxWidth) < 0.5 &&
                ReferenceEquals(_brush, brush))
            {
                return;
            }

            Clear();
            _text = text;
            _fontSize = fontSize;
            _maxWidth = maxWidth;
            _brush = brush;

            if (!ContainsEmojiCandidate(text))
            {
                _plainLayout = CreateLayout(text, TitleTypeface, fontSize, brush, maxWidth, TextTrimming.CharacterEllipsis);
                _height = _plainLayout.Height;
                _baseline = _plainLayout.Baseline;
                return;
            }

            BuildSegments(text, fontSize, brush, maxWidth);
            if (_segments.Count == 0)
            {
                _plainLayout = CreateLayout(text, TitleTypeface, fontSize, brush, maxWidth, TextTrimming.CharacterEllipsis);
                _height = _plainLayout.Height;
                _baseline = _plainLayout.Baseline;
            }
        }

        private void BuildSegments(string text, double fontSize, IBrush brush, double maxWidth)
        {
            var segmentStart = 0;
            var index = 0;
            while (index < text.Length)
            {
                if (!TryGetEmojiSequenceLength(text, index, out var emojiLength))
                {
                    index++;
                    continue;
                }

                AddSegment(text, segmentStart, index - segmentStart, isEmoji: false, fontSize, brush, maxWidth);
                AddSegment(text, index, emojiLength, isEmoji: true, fontSize, brush, maxWidth);
                index += emojiLength;
                segmentStart = index;
            }

            AddSegment(text, segmentStart, text.Length - segmentStart, isEmoji: false, fontSize, brush, maxWidth);
        }

        private void AddSegment(
            string text,
            int start,
            int length,
            bool isEmoji,
            double fontSize,
            IBrush brush,
            double maxWidth)
        {
            if (length <= 0)
            {
                return;
            }

            var segmentText = text.Substring(start, length);
            var typeface = isEmoji ? EmojiTypeface : TitleTypeface;
            var layout = CreateLayout(segmentText, typeface, fontSize, brush, maxWidth, TextTrimming.None);
            _height = Math.Max(_height, layout.Height);
            _baseline = Math.Max(_baseline, layout.Baseline);
            _segments.Add(new Segment(layout, layout.Baseline));
        }

        private static TextLayout CreateLayout(
            string text,
            Typeface typeface,
            double fontSize,
            IBrush brush,
            double maxWidth,
            TextTrimming trimming)
        {
            return new TextLayout(
                text,
                typeface,
                fontSize,
                brush,
                TextAlignment.Left,
                TextWrapping.NoWrap,
                trimming,
                null,
                FlowDirection.LeftToRight,
                maxWidth,
                CardHeight,
                double.NaN,
                0,
                1);
        }

        private static bool ContainsEmojiCandidate(string text)
        {
            for (var i = 0; i < text.Length; i++)
            {
                var ch = text[i];
                if (char.IsHighSurrogate(ch) || IsBmpEmoji(ch))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryGetEmojiSequenceLength(string text, int start, out int length)
        {
            length = 0;
            if (!TryGetBaseEmojiLength(text, start, out var baseLength))
            {
                return false;
            }

            var index = ConsumeEmojiModifiers(text, start + baseLength);
            while (index < text.Length && text[index] == '\u200D')
            {
                var nextStart = index + 1;
                if (!TryGetBaseEmojiLength(text, nextStart, out var nextLength))
                {
                    break;
                }

                index = ConsumeEmojiModifiers(text, nextStart + nextLength);
            }

            length = index - start;
            return true;
        }

        private static bool TryGetBaseEmojiLength(string text, int start, out int length)
        {
            length = 0;
            if (start >= text.Length)
            {
                return false;
            }

            var ch = text[start];
            if (IsBmpEmoji(ch))
            {
                length = 1;
                return true;
            }

            if (!char.IsHighSurrogate(ch) || start + 1 >= text.Length || !char.IsLowSurrogate(text[start + 1]))
            {
                return false;
            }

            var codePoint = char.ConvertToUtf32(ch, text[start + 1]);
            if (IsRegionalIndicator(codePoint) &&
                start + 3 < text.Length &&
                char.IsHighSurrogate(text[start + 2]) &&
                char.IsLowSurrogate(text[start + 3]) &&
                IsRegionalIndicator(char.ConvertToUtf32(text[start + 2], text[start + 3])))
            {
                length = 4;
                return true;
            }

            if (IsSupplementaryEmoji(codePoint))
            {
                length = 2;
                return true;
            }

            return false;
        }

        private static int ConsumeEmojiModifiers(string text, int index)
        {
            while (index < text.Length)
            {
                if (text[index] == '\uFE0F')
                {
                    index++;
                    continue;
                }

                if (index + 1 < text.Length &&
                    char.IsHighSurrogate(text[index]) &&
                    char.IsLowSurrogate(text[index + 1]))
                {
                    var codePoint = char.ConvertToUtf32(text[index], text[index + 1]);
                    if (codePoint is >= 0x1F3FB and <= 0x1F3FF)
                    {
                        index += 2;
                        continue;
                    }
                }

                break;
            }

            return index;
        }

        private static bool IsBmpEmoji(char ch)
        {
            return ch is >= '\u2600' and <= '\u27BF';
        }

        private static bool IsRegionalIndicator(int codePoint)
        {
            return codePoint is >= 0x1F1E6 and <= 0x1F1FF;
        }

        private static bool IsSupplementaryEmoji(int codePoint)
        {
            if (codePoint is >= 0x1F100 and <= 0x1F16F)
            {
                return false;
            }

            return codePoint is >= 0x1F000 and <= 0x1FAFF;
        }

        private sealed class Segment
        {
            public Segment(TextLayout layout, double baseline)
            {
                Layout = layout;
                Baseline = baseline;
            }

            public TextLayout Layout { get; }

            public double Baseline { get; }
        }
    }
}
