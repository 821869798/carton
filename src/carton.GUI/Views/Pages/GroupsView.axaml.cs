using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia;
using System;
using System.Linq;
using carton.ViewModels;

namespace carton.Views.Pages;

public partial class GroupsView : UserControl
{
    private Point? _groupTabsDragStartPoint;
    private Vector _groupTabsDragStartOffset;
    private bool _isDraggingGroupTabs;

    public GroupsView()
    {
        InitializeComponent();

        var groupTabsListBox = this.FindControl<ListBox>("GroupTabsListBox");
        if (groupTabsListBox != null)
        {
            groupTabsListBox.AddHandler(InputElement.PointerPressedEvent, OnGroupTabsPointerPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
            groupTabsListBox.AddHandler(InputElement.PointerMovedEvent, OnGroupTabsPointerMoved, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
            groupTabsListBox.AddHandler(InputElement.PointerReleasedEvent, OnGroupTabsPointerReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
            groupTabsListBox.AddHandler(InputElement.PointerCaptureLostEvent, OnGroupTabsPointerCaptureLost, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        }
    }

    private void OnGroupTabsPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not ListBox listBox || !e.GetCurrentPoint(listBox).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.Source is Visual sourceVisual &&
            sourceVisual.GetSelfAndVisualAncestors().Any(visual =>
                visual is ScrollBar or Thumb or Track))
        {
            return;
        }

        var scrollViewer = listBox.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (scrollViewer == null)
        {
            return;
        }

        _groupTabsDragStartPoint = e.GetPosition(listBox);
        _groupTabsDragStartOffset = scrollViewer.Offset;
        _isDraggingGroupTabs = false;
        e.Pointer.Capture(listBox);
    }

    private void OnGroupTabsPointerMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not ListBox listBox || _groupTabsDragStartPoint is null || !Equals(e.Pointer.Captured, listBox))
        {
            return;
        }

        var scrollViewer = listBox.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (scrollViewer == null)
        {
            return;
        }

        var currentPoint = e.GetPosition(listBox);
        var delta = currentPoint - _groupTabsDragStartPoint.Value;
        if (!_isDraggingGroupTabs && Math.Abs(delta.X) < 4)
        {
            return;
        }

        _isDraggingGroupTabs = true;
        var maxOffsetX = Math.Max(0, scrollViewer.Extent.Width - scrollViewer.Viewport.Width);
        var targetOffsetX = Math.Clamp(_groupTabsDragStartOffset.X - delta.X, 0, maxOffsetX);
        scrollViewer.Offset = new Vector(targetOffsetX, scrollViewer.Offset.Y);
        e.Handled = true;
    }

    private void OnGroupTabsPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not ListBox listBox)
        {
            return;
        }

        if (Equals(e.Pointer.Captured, listBox))
        {
            e.Pointer.Capture(null);
        }

        _groupTabsDragStartPoint = null;
        _groupTabsDragStartOffset = default;
        _isDraggingGroupTabs = false;
    }

    private void OnGroupTabsPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _groupTabsDragStartPoint = null;
        _groupTabsDragStartOffset = default;
        _isDraggingGroupTabs = false;
    }

    private void OnProxySelectPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: OutboundItemViewModel item })
        {
            return;
        }

        if (item.SelectOutboundCommand == null || string.IsNullOrWhiteSpace(item.Tag))
        {
            return;
        }

        if (!item.SelectOutboundCommand.CanExecute(item.Tag))
        {
            e.Handled = true;
            return;
        }

        _ = item.SelectOutboundCommand.ExecuteAsync(item.Tag);
        e.Handled = true;
    }

    private void OnProxyPointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is Control { DataContext: OutboundItemViewModel item })
        {
            item.IsHovered = true;
        }
    }

    private void OnProxyPointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is Control { DataContext: OutboundItemViewModel item })
        {
            item.IsHovered = false;
        }
    }
}
