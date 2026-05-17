using Avalonia;
using Avalonia.Controls;
using carton.GUI.Models;
using carton.ViewModels;
using FluentAvalonia.UI.Controls;
using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace carton.Views;

public partial class MainWindow : Window
{
    private bool _allowClose;
    private bool _hideOnFirstOpen;
    private MainViewModel? _viewModel;
    private bool _syncingNavigationSelection;
    private int _navigationRailAnimationVersion;
    private const double NavigationRailLeft = 10;
    private const double NavigationRailHeight = 18;

    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
        Opened += OnOpened;
        PropertyChanged += OnWindowPropertyChanged;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_viewModel != null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        base.OnDataContextChanged(e);

        _viewModel = DataContext as MainViewModel;
        if (_viewModel != null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            SyncNavigationSelection();
        }
    }

    public void AllowClose()
    {
        _allowClose = true;
    }

    public void StartHiddenToTray()
    {
        _hideOnFirstOpen = true;
        if (IsVisible)
        {
            HideOnFirstOpen();
        }
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
        if (_hideOnFirstOpen)
        {
            HideOnFirstOpen();
            return;
        }

        NotifyWindowVisible(IsVisible && WindowState != WindowState.Minimized);
        SyncNavigationSelection(force: true);
    }

    private void OnRootNavigationViewLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        SyncNavigationSelection(force: true);
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

    private void OnNavigationViewItemInvoked(object? sender, NavigationViewItemInvokedEventArgs e)
    {
        if (_syncingNavigationSelection || DataContext is not MainViewModel viewModel)
        {
            return;
        }

        var navigationItem = GetNavigationItemFromInvokedEvent(e);
        if (navigationItem == null)
        {
            return;
        }

        if (viewModel.SelectedPage != navigationItem.Page)
        {
            viewModel.SelectedPage = navigationItem.Page;
        }

        RootNavigationView.SelectedItem = navigationItem;
        SetNavigationItemSelectionStates(navigationItem);
    }

    private void OnNavigationViewSelectionChanged(object? sender, NavigationViewSelectionChangedEventArgs e)
    {
        if (_syncingNavigationSelection || DataContext is not MainViewModel viewModel)
        {
            return;
        }

        var navigationItem = GetNavigationItemFromSelectionEvent(e);
        if (navigationItem != null && viewModel.SelectedPage != navigationItem.Page)
        {
            viewModel.SelectedPage = navigationItem.Page;
        }

        if (navigationItem != null)
        {
            SetNavigationItemSelectionStates(navigationItem);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedPage))
        {
            SyncNavigationSelection();
        }
    }

    private void SyncNavigationSelection(bool force = false)
    {
        if (_viewModel == null)
        {
            return;
        }

        var item = _viewModel.NavigationItems
            .FirstOrDefault(x => x.Page == _viewModel.SelectedPage);

        if (item == null || (!force && ReferenceEquals(RootNavigationView.SelectedItem, item)))
        {
            if (item != null && !NavigationSelectionRail.IsVisible)
            {
                SetNavigationItemSelectionStates(item);
            }

            return;
        }

        _syncingNavigationSelection = true;
        try
        {
            if (force)
            {
                RootNavigationView.SelectedItem = null;
            }

            RootNavigationView.SelectedItem = item;
            SetNavigationItemSelectionStates(item);
        }
        finally
        {
            _syncingNavigationSelection = false;
        }
    }

    private void SetNavigationItemSelectionStates(NavigationItem selectedItem)
    {
        foreach (var item in GetNavigationItemContainers())
        {
            item.IsSelected = IsContainerForNavigationItem(item, selectedItem);
        }

        MoveNavigationSelectionRail(selectedItem);
        Dispatcher.UIThread.Post(
            () => MoveNavigationSelectionRail(selectedItem, retryIfMissing: true),
            DispatcherPriority.Render);
    }

    private void MoveNavigationSelectionRail(NavigationItem selectedItem, bool retryIfMissing = false)
    {
        var fallbackTop = GetFallbackNavigationRailTop(selectedItem);
        var selectedContainer = FindNavigationItemContainer(selectedItem);
        var center = selectedContainer?.Bounds.Height > 0
            ? selectedContainer.TranslatePoint(new Point(0, selectedContainer.Bounds.Height / 2), RootNavigationView)
            : null;

        if (center == null)
        {
            SetNavigationSelectionRailTop(fallbackTop);
            if (retryIfMissing)
            {
                Dispatcher.UIThread.Post(
                    () => MoveNavigationSelectionRail(selectedItem),
                    DispatcherPriority.Loaded);
            }
            return;
        }

        var measuredTop = center.Value.Y - NavigationSelectionRail.Height / 2;
        SetNavigationSelectionRailTop(measuredTop > 80 ? measuredTop : fallbackTop);
    }

    private double GetFallbackNavigationRailTop(NavigationItem selectedItem)
    {
        var index = _viewModel?.NavigationItems
            .Select((item, itemIndex) => new { item, itemIndex })
            .FirstOrDefault(x => ReferenceEquals(x.item, selectedItem))
            ?.itemIndex ?? 0;

        return 188 + index * 46;
    }

    private void SetNavigationSelectionRailTop(double top)
    {
        AnimateNavigationSelectionRailToTop(Math.Max(0, top));
    }

    private async void AnimateNavigationSelectionRailToTop(double targetTop)
    {
        var version = ++_navigationRailAnimationVersion;
        var currentTop = NavigationSelectionRail.Margin.Top;

        if (!NavigationSelectionRail.IsVisible || Math.Abs(currentTop - targetTop) < 0.5)
        {
            NavigationSelectionRail.Height = NavigationRailHeight;
            NavigationSelectionRail.Margin = new Thickness(NavigationRailLeft, targetTop, 0, 0);
            NavigationSelectionRail.IsVisible = true;
            return;
        }

        var stretchTop = Math.Min(currentTop, targetTop);
        var stretchHeight = Math.Abs(targetTop - currentTop) + NavigationRailHeight;

        NavigationSelectionRail.Margin = new Thickness(NavigationRailLeft, stretchTop, 0, 0);
        NavigationSelectionRail.Height = stretchHeight;
        NavigationSelectionRail.IsVisible = true;

        await Task.Delay(140);

        if (version != _navigationRailAnimationVersion)
        {
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (version != _navigationRailAnimationVersion)
            {
                return;
            }

            NavigationSelectionRail.Margin = new Thickness(NavigationRailLeft, targetTop, 0, 0);
            NavigationSelectionRail.Height = NavigationRailHeight;
        });
    }

    private NavigationItem? GetNavigationItemFromInvokedEvent(NavigationViewItemInvokedEventArgs e)
    {
        if (e.InvokedItemContainer is Control { DataContext: NavigationItem containerItem })
        {
            return containerItem;
        }

        if (e.InvokedItem is NavigationItem invokedItem)
        {
            return invokedItem;
        }

        if (e.InvokedItemContainer is NavigationViewItem { Tag: NavigationPage pageFromTag })
        {
            return FindNavigationItem(pageFromTag);
        }

        return null;
    }

    private NavigationItem? GetNavigationItemFromSelectionEvent(NavigationViewSelectionChangedEventArgs e)
    {
        if (e.SelectedItem is NavigationItem selectedItem)
        {
            return selectedItem;
        }

        if (e.SelectedItemContainer is Control { DataContext: NavigationItem containerItem })
        {
            return containerItem;
        }

        if (e.SelectedItemContainer is NavigationViewItem { Tag: NavigationPage pageFromContainer })
        {
            return FindNavigationItem(pageFromContainer);
        }

        if (e.SelectedItem is NavigationViewItem { Tag: NavigationPage pageFromItem })
        {
            return FindNavigationItem(pageFromItem);
        }

        return null;
    }

    private NavigationItem? FindNavigationItem(NavigationPage page)
    {
        return _viewModel?.NavigationItems.FirstOrDefault(x => x.Page == page);
    }

    private NavigationViewItem? FindNavigationItemContainer(NavigationItem item)
    {
        return GetNavigationItemContainers()
            .FirstOrDefault(container => IsContainerForNavigationItem(container, item));
    }

    private NavigationViewItem[] GetNavigationItemContainers()
    {
        return RootNavigationView.GetVisualDescendants()
            .OfType<NavigationViewItem>()
            .Where(item => item.DataContext is NavigationItem || item.Content is NavigationItem || item.Tag is NavigationPage)
            .ToArray();
    }

    private static bool IsContainerForNavigationItem(NavigationViewItem container, NavigationItem item)
    {
        return ReferenceEquals(container.DataContext, item)
            || ReferenceEquals(container.Content, item)
            || (container.Tag is NavigationPage page && page == item.Page);
    }

    private void HideOnFirstOpen()
    {
        _hideOnFirstOpen = false;
        NotifyWindowVisible(false);
        Hide();
    }
}
