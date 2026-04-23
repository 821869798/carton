using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using carton.ViewModels;
using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;

namespace carton.Views.Pages;

public partial class LogsView : UserControl
{
    private const double BottomThreshold = 4;
    private static readonly TimeSpan LogRefreshInterval = TimeSpan.FromMilliseconds(500);

    private ListBox? _logsListBox;
    private ScrollViewer? _scrollViewer;
    private LogsViewModel? _viewModel;
    private bool _autoScrollToBottom = true;
    private bool _pendingScrollToBottom;
    private bool _suppressScrollTracking;
    private readonly DispatcherTimer _logRefreshTimer;
    private bool _hasPendingLogRefresh;
    private bool _isViewActive;

    public LogsView()
    {
        _logRefreshTimer = new DispatcherTimer(LogRefreshInterval, DispatcherPriority.Background, OnLogRefreshTimerTick);
        InitializeComponent();
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        DataContextChanged += OnDataContextChanged;
        PropertyChanged += OnControlPropertyChanged;
        LayoutUpdated += OnLayoutUpdated;
        AddHandler(InputElement.PointerWheelChangedEvent, OnPointerWheelChanged, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _logsListBox ??= this.FindControl<ListBox>("LogsListBox");
        if (_logsListBox != null)
        {
            _logsListBox.SelectionChanged -= OnLogsListBoxSelectionChanged;
            _logsListBox.SelectionChanged += OnLogsListBoxSelectionChanged;
        }

        EnsureScrollViewerHooked();
        UpdateActiveState();
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _isViewActive = false;
        DetachViewModel();
        if (_logsListBox != null)
        {
            _logsListBox.SelectionChanged -= OnLogsListBoxSelectionChanged;
        }

        if (_scrollViewer != null)
        {
            _scrollViewer.PropertyChanged -= OnScrollViewerPropertyChanged;
        }

        _scrollViewer = null;
        _pendingScrollToBottom = false;
        _hasPendingLogRefresh = false;
        _logRefreshTimer.Stop();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_isViewActive)
        {
            AttachViewModel(DataContext as LogsViewModel);
        }
    }

    private void OnLogsListBoxSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        SyncSelectionToViewModel();
    }

    private void OnControlPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty)
        {
            UpdateActiveState();
        }
    }

    private void UpdateActiveState()
    {
        var isActive = this.GetVisualRoot() != null && IsVisible;
        if (_isViewActive == isActive)
        {
            return;
        }

        _isViewActive = isActive;
        if (_isViewActive)
        {
            AttachViewModel(DataContext as LogsViewModel);
            _logRefreshTimer.Start();
            RequestScrollToBottom();
            return;
        }

        DetachViewModel();
        _hasPendingLogRefresh = false;
        _pendingScrollToBottom = false;
        _logRefreshTimer.Stop();
    }

    private void AttachViewModel(LogsViewModel? viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel))
        {
            return;
        }

        DetachViewModel();
        _viewModel = viewModel;
        if (_viewModel != null)
        {
            _viewModel.Logs.CollectionChanged += OnLogsCollectionChanged;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _autoScrollToBottom = _viewModel.IsAutoScrollToLatest;
            SyncSelectionToViewModel();
        }
    }

    private void DetachViewModel()
    {
        if (_viewModel != null)
        {
            _viewModel.Logs.CollectionChanged -= OnLogsCollectionChanged;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel = null;
        }
    }

    private void OnLogsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_autoScrollToBottom)
        {
            _hasPendingLogRefresh = true;
        }
    }

    private void OnLogRefreshTimerTick(object? sender, EventArgs e)
    {
        if (!_hasPendingLogRefresh)
        {
            return;
        }

        _hasPendingLogRefresh = false;
        if (_autoScrollToBottom)
        {
            RequestScrollToBottom();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_viewModel == null)
        {
            return;
        }

        if (e.PropertyName == nameof(LogsViewModel.IsAutoScrollToLatest))
        {
            _autoScrollToBottom = _viewModel.IsAutoScrollToLatest;
            if (_autoScrollToBottom)
            {
                RequestScrollToBottom();
            }
        }
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (!_pendingScrollToBottom)
        {
            return;
        }

        EnsureScrollViewerHooked();
        if (_scrollViewer == null)
        {
            return;
        }

        if (!_autoScrollToBottom)
        {
            _pendingScrollToBottom = false;
            return;
        }

        var maxOffsetY = Math.Max(0, _scrollViewer.Extent.Height - _scrollViewer.Viewport.Height);
        if (Math.Abs(maxOffsetY - _scrollViewer.Offset.Y) <= BottomThreshold)
        {
            _pendingScrollToBottom = false;
            return;
        }

        _suppressScrollTracking = true;
        _scrollViewer.Offset = new Vector(_scrollViewer.Offset.X, maxOffsetY);
        _suppressScrollTracking = false;
        _pendingScrollToBottom = false;
    }

    private void OnScrollViewerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != ScrollViewer.OffsetProperty ||
            _suppressScrollTracking ||
            sender is not ScrollViewer scrollViewer)
        {
            return;
        }

        var isAtBottom = IsAtBottom(scrollViewer);
        if (_autoScrollToBottom == isAtBottom)
        {
            return;
        }

        _autoScrollToBottom = isAtBottom;
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (_viewModel == null || !_viewModel.IsAutoScrollToLatest || _scrollViewer == null)
        {
            return;
        }

        if (e.Source is Visual sourceVisual && IsDescendantOf(sourceVisual, _scrollViewer))
        {
            _viewModel.IsAutoScrollToLatest = false;
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_viewModel == null || !_viewModel.IsAutoScrollToLatest)
        {
            return;
        }

        if (e.Source is Visual sourceVisual && HasScrollInteractionAncestor(sourceVisual))
        {
            _viewModel.IsAutoScrollToLatest = false;
        }
    }

    private void RequestScrollToBottom()
    {
        if (_pendingScrollToBottom)
        {
            return;
        }

        _pendingScrollToBottom = true;
        Dispatcher.UIThread.Post(() => { }, DispatcherPriority.Background);
    }

    private static bool IsDescendantOf(Visual sourceVisual, Visual ancestor)
    {
        for (Visual? current = sourceVisual; current != null; current = current.GetVisualParent() as Visual)
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasScrollInteractionAncestor(Visual sourceVisual)
    {
        for (Visual? current = sourceVisual; current != null; current = current.GetVisualParent() as Visual)
        {
            if (current is ScrollBar or Thumb or Track)
            {
                return true;
            }
        }

        return false;
    }

    private void SyncSelectionToViewModel()
    {
        if (_viewModel == null || _logsListBox == null)
        {
            return;
        }

        _viewModel.SelectedLogs.Clear();
        if (_logsListBox.SelectedItems != null)
        {
            foreach (var log in _logsListBox.SelectedItems.OfType<LogEntryViewModel>())
            {
                _viewModel.SelectedLogs.Add(log);
            }
        }

        _viewModel.SelectedLog = _logsListBox.SelectedItem as LogEntryViewModel;
    }

    private static bool IsAtBottom(ScrollViewer scrollViewer)
    {
        var maxOffsetY = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        return maxOffsetY - scrollViewer.Offset.Y <= BottomThreshold;
    }

    private void EnsureScrollViewerHooked()
    {
        _logsListBox ??= this.FindControl<ListBox>("LogsListBox");
        var scrollViewer = _logsListBox?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (ReferenceEquals(_scrollViewer, scrollViewer))
        {
            return;
        }

        if (_scrollViewer != null)
        {
            _scrollViewer.PropertyChanged -= OnScrollViewerPropertyChanged;
        }

        _scrollViewer = scrollViewer;
        if (_scrollViewer != null)
        {
            _scrollViewer.PropertyChanged += OnScrollViewerPropertyChanged;
        }
    }
}
