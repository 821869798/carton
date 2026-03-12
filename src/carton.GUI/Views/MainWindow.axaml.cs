using Avalonia;
using Avalonia.Controls;
using carton.ViewModels;
namespace carton.Views;

public partial class MainWindow : Window
{
    private bool _allowClose;

    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
        Opened += OnOpened;
        PropertyChanged += OnWindowPropertyChanged;
    }

    public void AllowClose()
    {
        _allowClose = true;
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        NotifyWindowVisible(false);
        e.Cancel = true;
        Hide();
    }

    private void OnOpened(object? sender, System.EventArgs e)
    {
        NotifyWindowVisible(IsVisible && WindowState != WindowState.Minimized);
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty || e.Property == WindowStateProperty)
        {
            NotifyWindowVisible(IsVisible && WindowState != WindowState.Minimized);
        }
    }

    private void NotifyWindowVisible(bool isVisible)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.SetWindowVisible(isVisible);
        }
    }
}
