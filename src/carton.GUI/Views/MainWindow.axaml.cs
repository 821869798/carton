using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using carton.Core.Models;
using carton.GUI.Models;
using carton.GUI.Services;
using carton.ViewModels;
using FluentAvalonia.UI.Controls;
using System;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace carton.Views;

public partial class MainWindow : Window
{
    private const double DefaultWindowWidth = 910;
    private const double DefaultWindowHeight = 640;
    private bool _allowClose;
    private bool _hideOnFirstOpen;
    private bool _restoredWindowPlacement;
    private bool _hasNormalWindowState;
    private bool _suppressNormalWindowCapture;
    private int _normalWindowCaptureVersion;
    private int _deferredWindowPlacementSaveVersion;
    private MainViewModel? _viewModel;
    private MainWindowState? _lastNormalWindowState;
    private readonly IWindowStateService _windowStateService = new WindowStateService();
    private long _windowPlacementSaveSequence;
    private bool _syncingNavigationSelection;
    private int _navigationRailAnimationVersion;
    private const double NavigationRailLeft = 10;
    private const double NavigationRailHeight = 18;

    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
        Opened += OnOpened;
        PositionChanged += OnPositionChanged;
        Resized += OnResized;
        PropertyChanged += OnWindowPropertyChanged;
        RestoreWindowPlacement();
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
            SaveWindowPlacement();
            return;
        }

        NotifyWindowVisible(false);
        SaveWindowPlacement();
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

        if (e.Property == WindowStateProperty)
        {
            QueueNormalWindowStateCapture();
            QueueDeferredWindowPlacementSave();
        }
    }

    private void OnPositionChanged(object? sender, PixelPointEventArgs e)
    {
        QueueNormalWindowStateCapture();
    }

    private void OnResized(object? sender, WindowResizedEventArgs e)
    {
        // Some backends report user-driven resizes as Unspecified; only Layout/Move
        // resizes (content- or position-driven) are not worth capturing here.
        if (e.Reason != WindowResizeReason.User &&
            e.Reason != WindowResizeReason.Application &&
            e.Reason != WindowResizeReason.Unspecified)
        {
            return;
        }

        QueueNormalWindowStateCapture(e.ClientSize);
    }

    private void RestoreWindowPlacement()
    {
        var preferences = App.PreferencesService.Load();
        if (!preferences.SaveWindowPlacement)
        {
            return;
        }

        var state = _windowStateService.LoadMainWindowState();
        if (!IsValidWindowState(state))
        {
            ResetToDefaultWindowPlacement();
            return;
        }

        _restoredWindowPlacement = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = Math.Max(MinWidth, state.Width);
        Height = Math.Max(MinHeight, state.Height);
        Position = new PixelPoint((int)Math.Round(state.X), (int)Math.Round(state.Y));

        _lastNormalWindowState = new MainWindowState
        {
            X = state.X,
            Y = state.Y,
            Width = Math.Max(MinWidth, state.Width),
            Height = Math.Max(MinHeight, state.Height),
            State = MainWindowSavedState.Normal
        };
        _hasNormalWindowState = true;

        if (state.State == MainWindowSavedState.Maximized)
        {
            _suppressNormalWindowCapture = true;
            try
            {
                WindowState = WindowState.Maximized;
            }
            finally
            {
                _suppressNormalWindowCapture = false;
            }
        }
    }

    private void ResetToDefaultWindowPlacement()
    {
        Width = DefaultWindowWidth;
        Height = DefaultWindowHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }

    private void SaveWindowPlacement()
    {
        var preferences = App.PreferencesService.Load();
        if (!preferences.SaveWindowPlacement)
        {
            return;
        }

        var state = CreateWindowStateForSave();
        if (state == null)
        {
            return;
        }

        // Closing/hide path: write synchronously so the final placement is guaranteed to land
        // before the process exits. The sequence makes the service reject any slower background
        // save that finishes after this one, so a stale write can never overwrite it.
        _windowStateService.SaveMainWindowState(state, NextWindowPlacementSequence());
    }

    private void DeleteWindowPlacement()
    {
        _deferredWindowPlacementSaveVersion++;
        _windowStateService.DeleteMainWindowState(NextWindowPlacementSequence());
    }

    // Monotonic, UI-thread-only counter that orders saves; never returns 0 (which means
    // "unordered" to the service). Both the deferred background save and the synchronous
    // close save stamp their writes with it so a slower older write is dropped.
    private long NextWindowPlacementSequence() => ++_windowPlacementSaveSequence;

    private MainWindowState? CreateWindowStateForSave()
    {
        if (WindowState == WindowState.Minimized)
        {
            return _hasNormalWindowState ? _lastNormalWindowState : null;
        }

        if (WindowState == WindowState.Maximized && !_hasNormalWindowState)
        {
            return null;
        }

        var state = WindowState == WindowState.Maximized && _hasNormalWindowState
            ? CloneNormalWindowState(_lastNormalWindowState!)
            : CreateNormalWindowState(ClientSize);

        state.State = WindowState == WindowState.Maximized
            ? MainWindowSavedState.Maximized
            : MainWindowSavedState.Normal;

        return IsValidWindowState(state) ? state : null;
    }

    private void QueueNormalWindowStateCapture(Size? clientSize = null)
    {
        var version = ++_normalWindowCaptureVersion;
        Dispatcher.UIThread.Post(
            () =>
            {
                if (version == _normalWindowCaptureVersion)
                {
                    CaptureNormalWindowState(clientSize);
                }
            },
            DispatcherPriority.Background);
    }

    private void CaptureNormalWindowState(Size? clientSize = null)
    {
        if (!_restoredWindowPlacement && !IsVisible)
        {
            return;
        }

        if (_suppressNormalWindowCapture || WindowState != WindowState.Normal)
        {
            return;
        }

        var size = clientSize ?? ClientSize;
        if (IsScreenSizedWindow(size))
        {
            return;
        }

        var state = CreateNormalWindowState(size);
        if (!IsValidWindowState(state))
        {
            return;
        }

        _lastNormalWindowState = state;
        _hasNormalWindowState = true;
        QueueDeferredWindowPlacementSave();
    }

    private async void QueueDeferredWindowPlacementSave()
    {
        try
        {
            var version = ++_deferredWindowPlacementSaveVersion;
            await Task.Delay(TimeSpan.FromSeconds(1));

            if (version != _deferredWindowPlacementSaveVersion)
            {
                return;
            }

            // Snapshot the placement on the UI thread (touches Position/ClientSize/Screens),
            // then hand the disk write off to a background thread so a slow/contended disk
            // never stalls rendering.
            var sequence = NextWindowPlacementSequence();
            var state = await Dispatcher.UIThread.InvokeAsync(() =>
                version == _deferredWindowPlacementSaveVersion ? CreatePlacementForDeferredSave() : null);

            if (state != null)
            {
                if (version != _deferredWindowPlacementSaveVersion)
                {
                    return;
                }

                // Write off the UI thread; the sequence makes the service drop this save if a
                // newer one (background or the synchronous close save) has already landed, so a
                // slow older write can never overwrite a newer one regardless of finish order.
                await Task.Run(() => _windowStateService.SaveMainWindowState(state, sequence));
            }
        }
        catch (Exception)
        {
            // Deferred window placement persistence is best-effort.
        }
    }

    private MainWindowState? CreatePlacementForDeferredSave()
    {
        var preferences = App.PreferencesService.Load();
        return preferences.SaveWindowPlacement ? CreateWindowStateForSave() : null;
    }

    private MainWindowState CreateNormalWindowState(Size clientSize)
        => new()
        {
            X = Position.X,
            Y = Position.Y,
            Width = clientSize.Width,
            Height = clientSize.Height,
            State = MainWindowSavedState.Normal
        };

    private static MainWindowState CloneNormalWindowState(MainWindowState state)
        => new()
        {
            X = state.X,
            Y = state.Y,
            Width = state.Width,
            Height = state.Height,
            State = MainWindowSavedState.Normal
        };

    private bool IsValidWindowState([NotNullWhen(true)] MainWindowState? state)
    {
        if (state == null)
        {
            return false;
        }

        if (!IsFinitePositive(state.Width) ||
            !IsFinitePositive(state.Height) ||
            !double.IsFinite(state.X) ||
            !double.IsFinite(state.Y))
        {
            return false;
        }

        var width = Math.Max(MinWidth, state.Width);
        var height = Math.Max(MinHeight, state.Height);

        foreach (var screen in Screens.All)
        {
            var scaling = GetSafeScreenScaling(screen);
            var bounds = new PixelRect(
                (int)Math.Round(state.X),
                (int)Math.Round(state.Y),
                Math.Max(1, (int)Math.Round(width * scaling)),
                Math.Max(1, (int)Math.Round(height * scaling)));

            if (IsMostlyVisible(bounds, screen.WorkingArea))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsMostlyVisible(PixelRect windowBounds, PixelRect workingArea)
    {
        var visible = windowBounds.Intersect(workingArea);
        if (visible.Width <= 0 || visible.Height <= 0)
        {
            return false;
        }

        const int MinVisiblePixels = 80;
        return visible.Width >= Math.Min(MinVisiblePixels, windowBounds.Width) &&
               visible.Height >= Math.Min(MinVisiblePixels, windowBounds.Height);
    }

    private static bool IsFinitePositive(double value)
        => double.IsFinite(value) && value > 0;

    private bool IsScreenSizedWindow(Size size)
    {
        foreach (var screen in Screens.All)
        {
            var scaling = GetSafeScreenScaling(screen);
            var pixelWidth = Math.Max(1, (int)Math.Round(size.Width * scaling));
            var pixelHeight = Math.Max(1, (int)Math.Round(size.Height * scaling));

            if (Math.Abs(screen.WorkingArea.Width - pixelWidth) <= 2 &&
                Math.Abs(screen.WorkingArea.Height - pixelHeight) <= 2)
            {
                return true;
            }
        }

        return false;
    }

    private static double GetSafeScreenScaling(Screen screen)
    {
        var scaling = screen.Scaling;
        return scaling <= 0 || double.IsNaN(scaling) || double.IsInfinity(scaling)
            ? 1
            : scaling;
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

        if (e.PropertyName == nameof(MainViewModel.SaveWindowPlacementEnabled))
        {
            if (_viewModel?.SaveWindowPlacementEnabled == true)
            {
                SaveWindowPlacement();
            }
            else
            {
                DeleteWindowPlacement();
            }
        }
    }

    private void SyncNavigationSelection(bool force = false)
    {
        if (_viewModel == null)
        {
            return;
        }

        var item = FindNavigationItem(_viewModel.SelectedPage);

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
        UpdateNavigationItemSelectionStates(RootNavigationView, selectedItem);

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
        var index = 0;
        if (_viewModel != null)
        {
            for (var i = 0; i < _viewModel.NavigationItems.Count; i++)
            {
                if (ReferenceEquals(_viewModel.NavigationItems[i], selectedItem))
                {
                    index = i;
                    break;
                }
            }
        }

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
        if (_viewModel == null)
        {
            return null;
        }

        for (var i = 0; i < _viewModel.NavigationItems.Count; i++)
        {
            var item = _viewModel.NavigationItems[i];
            if (item.Page == page)
            {
                return item;
            }
        }

        return null;
    }

    private NavigationViewItem? FindNavigationItemContainer(NavigationItem item)
    {
        return FindNavigationItemContainer(RootNavigationView, item);
    }

    private static NavigationViewItem? FindNavigationItemContainer(Visual visual, NavigationItem item)
    {
        if (visual is NavigationViewItem container && IsContainerForNavigationItem(container, item))
        {
            return container;
        }

        foreach (var child in visual.GetVisualChildren())
        {
            var match = FindNavigationItemContainer(child, item);
            if (match != null)
            {
                return match;
            }
        }

        return null;
    }

    private static void UpdateNavigationItemSelectionStates(Visual visual, NavigationItem selectedItem)
    {
        if (visual is NavigationViewItem container &&
            (container.DataContext is NavigationItem || container.Content is NavigationItem || container.Tag is NavigationPage))
        {
            container.IsSelected = IsContainerForNavigationItem(container, selectedItem);
        }

        foreach (var child in visual.GetVisualChildren())
        {
            UpdateNavigationItemSelectionStates(child, selectedItem);
        }
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
