using Avalonia.Controls;

namespace carton.Views.Pages;

public partial class DashboardView : UserControl
{
    private bool _stackRuntimeOptions;

    public DashboardView()
    {
        InitializeComponent();
    }

    private void OnRuntimeOptionsSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        var stack = e.NewSize.Width < 620;
        if (stack == _stackRuntimeOptions)
        {
            return;
        }

        _stackRuntimeOptions = stack;
        RuntimeOptionsGrid.ColumnDefinitions = new ColumnDefinitions(stack ? "*" : "*,Auto");
        RuntimeOptionsGrid.RowDefinitions = new RowDefinitions(stack ? "Auto,Auto" : "Auto");
        RuntimeOptionsGrid.RowSpacing = stack ? 16 : 0;
        Grid.SetColumn(RuntimeSwitchesCard, stack ? 0 : 1);
        Grid.SetRow(RuntimeSwitchesCard, stack ? 1 : 0);
    }
}
