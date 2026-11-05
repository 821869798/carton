using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace carton.GUI.Helpers;

/// <summary>
/// 为 ScrollViewer 提供丝滑滚轮阻尼/平滑滚动的附加行为。
/// 基于 TopLevel.RequestAnimationFrame 驱动，与屏幕高刷新率（60/120/144Hz）严格垂直同步，
/// 采用帧率无关的指数衰减阻尼算法，并支持滚轮动量连续叠加。
/// </summary>
public static class SmoothScrollBehavior
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, bool>(
            "IsEnabled", typeof(SmoothScrollBehavior), defaultValue: false);

    public static readonly AttachedProperty<double> ScrollStepProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, double>(
            "ScrollStep", typeof(SmoothScrollBehavior), defaultValue: 90.0);

    public static readonly AttachedProperty<double> DecayRateProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, double>(
            "DecayRate", typeof(SmoothScrollBehavior), defaultValue: 18.0);

    public static bool GetIsEnabled(ScrollViewer element) => element.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(ScrollViewer element, bool value) => element.SetValue(IsEnabledProperty, value);

    public static double GetScrollStep(ScrollViewer element) => element.GetValue(ScrollStepProperty);
    public static void SetScrollStep(ScrollViewer element, double value) => element.SetValue(ScrollStepProperty, value);

    public static double GetDecayRate(ScrollViewer element) => element.GetValue(DecayRateProperty);
    public static void SetDecayRate(ScrollViewer element, double value) => element.SetValue(DecayRateProperty, value);

    private static readonly AttachedProperty<ScrollAnimationState?> AnimationStateProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, ScrollAnimationState?>(
            "AnimationState", typeof(SmoothScrollBehavior));

    static SmoothScrollBehavior()
    {
        IsEnabledProperty.Changed.AddClassHandler<ScrollViewer>(OnIsEnabledChanged);
    }

    private static void OnIsEnabledChanged(ScrollViewer sv, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            sv.AddHandler(InputElement.PointerWheelChangedEvent, OnPointerWheel, RoutingStrategies.Tunnel);
            sv.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
            sv.DetachedFromVisualTree += OnDetachedFromVisualTree;
        }
        else
        {
            sv.RemoveHandler(InputElement.PointerWheelChangedEvent, OnPointerWheel);
            sv.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
            sv.DetachedFromVisualTree -= OnDetachedFromVisualTree;
            StopAnimation(sv);
            sv.ClearValue(AnimationStateProperty);
        }
    }

    private static void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is ScrollViewer sv)
        {
            StopAnimation(sv);
        }
    }

    private static void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // 用户按下鼠标时（例如拖动滚动条或点击内容交互），立即停掉滚轮阻尼动画
        if (sender is ScrollViewer sv)
        {
            StopAnimation(sv);
        }
    }

    private static void StopAnimation(ScrollViewer sv)
    {
        if (sv.GetValue(AnimationStateProperty) is { } state)
        {
            state.IsRunning = false;
            state.Generation++;
            state.LastFrameTime = null;
        }
    }

    private static bool CanMove(ScrollViewer viewer, Vector delta)
    {
        var offset = viewer.GetValue(AnimationStateProperty) is { IsRunning: true } animation
            ? animation.TargetOffset : viewer.Offset;
        var maxX = Math.Max(0, viewer.Extent.Width - viewer.Viewport.Width);
        var maxY = Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height);
        return (delta.Y < 0 && offset.Y < maxY - 0.01) ||
               (delta.Y > 0 && offset.Y > 0.01) ||
               (delta.X < 0 && offset.X < maxX - 0.01) ||
               (delta.X > 0 && offset.X > 0.01);
    }

    private static void OnPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (sender is not ScrollViewer sv || e.Handled) return;

        // Tunnel events reach outer viewers first. Let the nearest scrollable
        // descendant consume the wheel, including native text-editor viewers.
        for (var visual = e.Source as Visual; visual != null && visual != sv; visual = visual.GetVisualParent())
        {
            if (visual is ScrollViewer inner && CanMove(inner, e.Delta)) return;
        }
        if (!CanMove(sv, e.Delta)) return;

        // 如果按下了 Ctrl 键（通常是缩放界面或字体快捷键），不拦截
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            return;
        }

        // 计算当前可滚动区域
        double maxX = Math.Max(0, sv.Extent.Width - sv.Viewport.Width);
        double maxY = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);

        bool canScrollX = maxX > 0 && Math.Abs(e.Delta.X) > 0.001;
        bool canScrollY = maxY > 0 && Math.Abs(e.Delta.Y) > 0.001;

        if (!canScrollX && !canScrollY)
        {
            // 当前方向无法滚动，放行事件让外层容器处理
            return;
        }

        // 笔记本精密触控板（Precision Touchpad）往往发送高频、微小连续浮点 Delta (< 0.1)
        // 触控板直接走系统原生通道最贴合手指，不加额外机械滚轮物理模拟
        if (Math.Abs(e.Delta.Y) < 0.1 && Math.Abs(e.Delta.X) < 0.1)
        {
            return;
        }

        var state = sv.GetValue(AnimationStateProperty);
        if (state == null)
        {
            state = new ScrollAnimationState();
            sv.SetValue(AnimationStateProperty, state);
        }

        double step = GetScrollStep(sv);

        // 如果之前没有在跑动画，则以当前控件的真实 Offset 为基准
        if (!state.IsRunning)
        {
            state.TargetOffset = sv.Offset;
            state.LastFrameTime = null;
        }

        // 动量累加：连续滚动时累加目标值，并用 Clamp 限制在有效视口内
        // 滚轮向下滚动时 e.Delta.Y 为负数，内容上移，Offset 增加
        double targetX = canScrollX
            ? Math.Clamp(state.TargetOffset.X - (e.Delta.X * step), 0, maxX)
            : sv.Offset.X;

        double targetY = canScrollY
            ? Math.Clamp(state.TargetOffset.Y - (e.Delta.Y * step), 0, maxY)
            : sv.Offset.Y;

        state.TargetOffset = new Vector(targetX, targetY);

        // 标记已处理，防止 ScrollViewer 发生原生离散瞬移跳跃
        e.Handled = true;

        if (!state.IsRunning)
        {
            state.IsRunning = true;
            state.Generation++;
            RequestNextFrame(sv, state);
        }
    }

    private static void RequestNextFrame(ScrollViewer sv, ScrollAnimationState state)
    {
        var topLevel = TopLevel.GetTopLevel(sv);
        if (topLevel == null)
        {
            sv.Offset = state.TargetOffset;
            state.IsRunning = false;
            return;
        }

        var generation = state.Generation;
        topLevel.RequestAnimationFrame(time =>
        {
            if (!state.IsRunning || generation != state.Generation) return;

            // 计算与上一帧的时间差 dt
            double dt = 1.0 / 60.0;
            if (state.LastFrameTime.HasValue)
            {
                dt = (time - state.LastFrameTime.Value).TotalSeconds;
                // 限制异常的大 dt（例如进程卡顿、后台挂起），防止突跳
                dt = Math.Clamp(dt, 0.001, 0.05);
            }
            state.LastFrameTime = time;

            var current = sv.Offset;
            // Filtering/removing rows can shrink the extent while a wheel animation runs.
            state.TargetOffset = new Vector(
                Math.Clamp(state.TargetOffset.X, 0, Math.Max(0, sv.Extent.Width - sv.Viewport.Width)),
                Math.Clamp(state.TargetOffset.Y, 0, Math.Max(0, sv.Extent.Height - sv.Viewport.Height)));
            var target = state.TargetOffset;
            var diff = target - current;

            // 差距小于 0.5 像素直接吸附并结束动画
            if (Math.Abs(diff.X) < 0.5 && Math.Abs(diff.Y) < 0.5)
            {
                sv.Offset = target;
                state.IsRunning = false;
                state.LastFrameTime = null;
                return;
            }

            // 帧率无关指数衰减阻尼：factor = 1 - e^(-decay * dt)
            double decay = GetDecayRate(sv);
            double factor = 1.0 - Math.Exp(-decay * dt);

            sv.Offset = new Vector(
                current.X + (diff.X * factor),
                current.Y + (diff.Y * factor));

            // 请求下一帧
            RequestNextFrame(sv, state);
        });
    }

    private sealed class ScrollAnimationState
    {
        public Vector TargetOffset;
        public TimeSpan? LastFrameTime;
        public bool IsRunning;
        public long Generation;
    }
}
