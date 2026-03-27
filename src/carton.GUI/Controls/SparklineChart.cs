using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace carton.Controls;

public class SparklineChart : Control
{
    public static readonly StyledProperty<IList<long>?> SamplesProperty =
        AvaloniaProperty.Register<SparklineChart, IList<long>?>(nameof(Samples));

    public static readonly StyledProperty<IBrush?> LineBrushProperty =
        AvaloniaProperty.Register<SparklineChart, IBrush?>(nameof(LineBrush));

    public static readonly StyledProperty<IBrush?> FillBrushProperty =
        AvaloniaProperty.Register<SparklineChart, IBrush?>(nameof(FillBrush));

    public static readonly StyledProperty<IBrush?> GridBrushProperty =
        AvaloniaProperty.Register<SparklineChart, IBrush?>(nameof(GridBrush));

    public static readonly StyledProperty<double> LineThicknessProperty =
        AvaloniaProperty.Register<SparklineChart, double>(nameof(LineThickness), 1.75d);

    public static readonly StyledProperty<int> GridLineCountProperty =
        AvaloniaProperty.Register<SparklineChart, int>(nameof(GridLineCount), 4);

    public static readonly StyledProperty<double> ChartPaddingProperty =
        AvaloniaProperty.Register<SparklineChart, double>(nameof(ChartPadding), 4d);

    public static readonly StyledProperty<double> FillOpacityProperty =
        AvaloniaProperty.Register<SparklineChart, double>(nameof(FillOpacity), 0.12d);

    private INotifyCollectionChanged? _observableSamples;
    private Geometry? _lineGeometry;
    private Geometry? _fillGeometry;
    private Pen? _linePen;
    private Pen? _gridPen;
    private Size _cachedSize;
    private bool _isGeometryDirty = true;
    private static readonly ConditionalWeakTable<ISolidColorBrush, Dictionary<int, SolidColorBrush>> FillBrushCache = new();
    private static readonly object FillBrushCacheLock = new();

    public IList<long>? Samples
    {
        get => GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    public IBrush? LineBrush
    {
        get => GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    public IBrush? FillBrush
    {
        get => GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    public IBrush? GridBrush
    {
        get => GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    public double LineThickness
    {
        get => GetValue(LineThicknessProperty);
        set => SetValue(LineThicknessProperty, value);
    }

    public int GridLineCount
    {
        get => GetValue(GridLineCountProperty);
        set => SetValue(GridLineCountProperty, value);
    }

    public double ChartPadding
    {
        get => GetValue(ChartPaddingProperty);
        set => SetValue(ChartPaddingProperty, value);
    }

    public double FillOpacity
    {
        get => GetValue(FillOpacityProperty);
        set => SetValue(FillOpacityProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SamplesProperty)
        {
            if (_observableSamples != null)
            {
                _observableSamples.CollectionChanged -= OnSamplesCollectionChanged;
            }

            _observableSamples = change.NewValue is INotifyCollectionChanged collection ? collection : null;
            if (_observableSamples != null)
            {
                _observableSamples.CollectionChanged += OnSamplesCollectionChanged;
            }

            InvalidateGeometry();
            return;
        }

        if (change.Property == LineBrushProperty ||
            change.Property == FillBrushProperty ||
            change.Property == GridBrushProperty ||
            change.Property == LineThicknessProperty ||
            change.Property == GridLineCountProperty ||
            change.Property == ChartPaddingProperty ||
            change.Property == FillOpacityProperty)
        {
            if (change.Property == LineBrushProperty || change.Property == LineThicknessProperty)
            {
                _linePen = null;
            }

            if (change.Property == GridBrushProperty)
            {
                _gridPen = null;
            }

            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        DrawGrid(context, bounds.Size);
        EnsureGeometry(bounds.Size);

        if (_fillGeometry != null && FillBrush != null)
        {
            var fillBrush = GetAdjustedFillBrush(FillBrush, FillOpacity);
            context.DrawGeometry(fillBrush, null, _fillGeometry);
        }

        if (_lineGeometry != null && LineBrush != null)
        {
            context.DrawGeometry(null, GetOrCreateLinePen(LineBrush, LineThickness), _lineGeometry);
        }
    }

    private void DrawGrid(DrawingContext context, Size size)
    {
        if (GridBrush == null)
        {
            return;
        }

        var lineCount = Math.Max(2, GridLineCount);
        var stepY = size.Height / (lineCount - 1);
        var gridPen = GetOrCreateGridPen(GridBrush);
        for (var i = 0; i < lineCount; i++)
        {
            var y = i * stepY;
            context.DrawLine(gridPen, new Point(0, y), new Point(size.Width, y));
        }
    }

    private void EnsureGeometry(Size size)
    {
        if (!_isGeometryDirty && _cachedSize == size)
        {
            return;
        }

        _cachedSize = size;
        _isGeometryDirty = false;
        BuildGeometry(size);
    }

    private void BuildGeometry(Size size)
    {
        var samples = Samples;
        if (samples == null || samples.Count == 0)
        {
            _lineGeometry = null;
            _fillGeometry = null;
            return;
        }

        long maxValue = 0;
        for (var i = 0; i < samples.Count; i++)
        {
            maxValue = Math.Max(maxValue, samples[i]);
        }

        var padding = Math.Max(0, ChartPadding);
        var width = size.Width;
        var height = size.Height;
        var baseline = height - padding;
        var drawableHeight = Math.Max(1, height - (padding * 2));
        var stepX = samples.Count == 1 ? 0 : width / (samples.Count - 1);

        _lineGeometry = new StreamGeometry();
        _fillGeometry = new StreamGeometry();

        using (var lineContext = ((StreamGeometry)_lineGeometry).Open())
        using (var fillContext = ((StreamGeometry)_fillGeometry).Open())
        {
            var startPoint = new Point(0, CalculateY(samples[0], maxValue, baseline, drawableHeight));
            lineContext.BeginFigure(startPoint, false);
            fillContext.BeginFigure(new Point(0, baseline), true);
            fillContext.LineTo(startPoint);

            for (var i = 1; i < samples.Count; i++)
            {
                var point = new Point(stepX * i, CalculateY(samples[i], maxValue, baseline, drawableHeight));
                lineContext.LineTo(point);
                fillContext.LineTo(point);
            }

            fillContext.LineTo(new Point(width, baseline));
            fillContext.EndFigure(true);
        }
    }

    private static double CalculateY(long value, long maxValue, double baseline, double drawableHeight)
    {
        if (maxValue <= 0)
        {
            return baseline;
        }

        var normalized = Math.Clamp(value / (double)maxValue, 0d, 1d);
        return baseline - (normalized * drawableHeight);
    }

    private void OnSamplesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        InvalidateGeometry();
    }

    private void InvalidateGeometry()
    {
        _isGeometryDirty = true;
        InvalidateVisual();
    }

    private static IBrush GetAdjustedFillBrush(IBrush brush, double opacity)
    {
        if (brush is ISolidColorBrush solidBrush)
        {
            var normalizedOpacity = Math.Clamp(opacity, 0d, 1d);
            if (Math.Abs(solidBrush.Opacity - normalizedOpacity) < 0.0001d)
            {
                return brush;
            }

            var opacityKey = (int)Math.Round(normalizedOpacity * 1000d);

            lock (FillBrushCacheLock)
            {
                var cache = FillBrushCache.GetOrCreateValue(solidBrush);
                if (!cache.TryGetValue(opacityKey, out var cachedBrush))
                {
                    cachedBrush = new SolidColorBrush(solidBrush.Color, normalizedOpacity);
                    cache[opacityKey] = cachedBrush;
                }

                return cachedBrush;
            }
        }

        return brush;
    }

    private Pen GetOrCreateLinePen(IBrush lineBrush, double thickness)
    {
        if (_linePen == null)
        {
            _linePen = new Pen(lineBrush, thickness);
        }

        return _linePen;
    }

    private Pen GetOrCreateGridPen(IBrush gridBrush)
    {
        if (_gridPen == null)
        {
            _gridPen = new Pen(gridBrush, 1);
        }

        return _gridPen;
    }
}
