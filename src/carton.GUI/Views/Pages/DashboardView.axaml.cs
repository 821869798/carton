using Avalonia.Controls;
using Avalonia.Interactivity;
using carton.ViewModels;

namespace carton.Views.Pages;

public partial class DashboardView : UserControl
{
    public DashboardView()
    {
        InitializeComponent();
    }

    private void InboundPortTextBox_OnLostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is DashboardViewModel viewModel)
        {
            viewModel.CommitInboundPortEdit();
        }
    }
}
