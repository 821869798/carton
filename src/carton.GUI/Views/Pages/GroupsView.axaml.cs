using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using carton.ViewModels;

namespace carton.Views.Pages;

public partial class GroupsView : UserControl
{
    public GroupsView()
    {
        InitializeComponent();
    }

    private void OnGroupItemPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: GroupItemViewModel group } ||
            DataContext is not GroupsViewModel viewModel)
        {
            return;
        }

        if (e.Source is Visual sourceVisual && HasToggleExclusionAncestor(sourceVisual, sender as Visual))
        {
            return;
        }

        if (!viewModel.ToggleGroupExpansionCommand.CanExecute(group))
        {
            return;
        }

        viewModel.ToggleGroupExpansionCommand.Execute(group);
        e.Handled = true;
    }

    private static bool HasToggleExclusionAncestor(Visual sourceVisual, Visual? groupRoot)
    {
        for (Visual? current = sourceVisual; current != null && !ReferenceEquals(current, groupRoot); current = current.GetVisualParent() as Visual)
        {
            if (current is Button || current is Control { DataContext: OutboundItemViewModel })
            {
                return true;
            }
        }

        return false;
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
