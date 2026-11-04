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

namespace carton.Views.Pages;

public partial class LogsView : UserControl
{
    private const double BottomThreshold = 4;
    private const int MaxScrollToBottomAttempts = 8;
    private static readonly TimeSpan LogRefreshInterval = TimeSpan.FromMilliseconds(500);

    private ListBox? _logsListBox;
    private ScrollViewer? _scrollViewer;
    private LogsViewModel? _viewModel;
    private INotifyCollectionChanged? _logsCollection;
    private bool _autoScrollToBottom = true;
    private bool _pendingScrollToBottom;
    private int _pendingScrollToBottomAttempts;
    private bool _suppressScrollTracking;
    private bool _isScrollingLastLogIntoView;
    private bool _scrollLastLogIntoViewQueued;
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
        _scrollLastLogIntoViewQueued = false;
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
        var isActive = TopLevel.GetTopLevel(this) != null && IsVisible;
        if (_isViewActive == isActive)
        {
            return;
        }

        _isViewActive = isActive;
        if (_isViewActive)
        {
            AttachViewModel(DataContext as LogsViewModel);
            RequestScrollToBottom();
            return;
        }

        DetachViewModel();
        _hasPendingLogRefresh = false;
        _pendingScrollToBottom = false;
        _scrollLastLogIntoViewQueued = false;
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
            AttachLogsCollection(_viewModel.Logs);
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.VisibleLogsRefreshed += OnVisibleLogsRefreshed;
            _autoScrollToBottom = _viewModel.IsAutoScrollToLatest;
            SyncSelectionToViewModel();
            if (_autoScrollToBottom && _viewModel.Logs.Count > 0)
            {
                RequestScrollToBottom();
            }
        }
    }

    private void DetachViewModel()
    {
        if (_viewModel != null)
        {
            DetachLogsCollection();
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.VisibleLogsRefreshed -= OnVisibleLogsRefreshed;
            _viewModel = null;
        }
    }

    private void AttachLogsCollection(INotifyCollectionChanged logs)
    {
        if (ReferenceEquals(_logsCollection, logs))
        {
            return;
        }

        DetachLogsCollection();
        _logsCollection = logs;
        _logsCollection.CollectionChanged += OnLogsCollectionChanged;
    }

    private void DetachLogsCollection()
    {
        if (_logsCollection == null)
        {
            return;
        }

        _logsCollection.CollectionChanged -= OnLogsCollectionChanged;
        _logsCollection = null;
    }

    private void OnVisibleLogsRefreshed(object? sender, EventArgs e)
    {
        if (_isViewActive && _autoScrollToBottom)
        {
            RequestScrollToBottom();
        }
    }

    private void OnLogsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_autoScrollToBottom)
        {
            _hasPendingLogRefresh = true;
            RequestScrollToBottom();
            if (_isViewActive && !_logRefreshTimer.IsEnabled)
            {
                _logRefreshTimer.Start();
            }
        }
    }

    private void OnLogRefreshTimerTick(object? sender, EventArgs e)
    {
        if (!_hasPendingLogRefresh)
        {
            _logRefreshTimer.Stop();
            return;
        }

        _hasPendingLogRefresh = false;
        if (_autoScrollToBottom)
        {
            RequestScrollToBottom();
        }

        if (!_hasPendingLogRefresh)
        {
            _logRefreshTimer.Stop();
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

        if (e.PropertyName == nameof(LogsViewModel.Logs))
        {
            AttachLogsCollection(_viewModel.Logs);
            if (_autoScrollToBottom && _viewModel.Logs.Count > 0)
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
            _pendingScrollToBottomAttempts = 0;
            return;
        }

        var maxOffsetY = Math.Max(0, _scrollViewer.Extent.Height - _scrollViewer.Viewport.Height);
        if (Math.Abs(maxOffsetY - _scrollViewer.Offset.Y) <= BottomThreshold)
        {
            if (_pendingScrollToBottomAttempts > 0 || GetLogCount() == 0)
            {
                _pendingScrollToBottom = false;
                _pendingScrollToBottomAttempts = 0;
                return;
            }
        }

        _suppressScrollTracking = true;
        try
        {
            _scrollViewer.Offset = new Vector(_scrollViewer.Offset.X, maxOffsetY);
        }
        finally
        {
            _suppressScrollTracking = false;
        }

        if (++_pendingScrollToBottomAttempts >= MaxScrollToBottomAttempts)
        {
            _pendingScrollToBottom = false;
            _pendingScrollToBottomAttempts = 0;
            return;
        }

        Dispatcher.UIThread.Post(() => { }, DispatcherPriority.Background);
    }

    private void OnScrollViewerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != ScrollViewer.OffsetProperty ||
            _suppressScrollTracking ||
            _pendingScrollToBottom ||
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
        if (!_pendingScrollToBottom)
        {
            _pendingScrollToBottom = true;
            _pendingScrollToBottomAttempts = 0;
        }

        QueueScrollLastLogIntoView();
    }

    private void QueueScrollLastLogIntoView()
    {
        if (_scrollLastLogIntoViewQueued)
        {
            return;
        }

        _scrollLastLogIntoViewQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _scrollLastLogIntoViewQueued = false;
            TryScrollLastLogIntoView();
        }, DispatcherPriority.Background);
    }

    private void TryScrollLastLogIntoView()
    {
        if (!_autoScrollToBottom || _isScrollingLastLogIntoView)
        {
            return;
        }

        _logsListBox ??= this.FindControl<ListBox>("LogsListBox");
        if (_logsListBox == null ||
            _viewModel == null ||
            _viewModel.Logs.Count == 0)
        {
            return;
        }

        _isScrollingLastLogIntoView = true;
        _suppressScrollTracking = true;
        try
        {
            _logsListBox.ScrollIntoView(_viewModel.Logs[^1]);
        }
        finally
        {
            _suppressScrollTracking = false;
            _isScrollingLastLogIntoView = false;
        }
    }

    private int GetLogCount()
    {
        return _viewModel?.Logs.Count ?? 0;
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
            foreach (var selectedItem in _logsListBox.SelectedItems)
            {
                if (selectedItem is LogEntryViewModel log)
                {
                    _viewModel.SelectedLogs.Add(log);
                }
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
        var scrollViewer = FindScrollViewer(_logsListBox);
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

    private static ScrollViewer? FindScrollViewer(Visual? visual)
    {
        if (visual == null)
        {
            return null;
        }

        if (visual is ScrollViewer scrollViewer)
        {
            return scrollViewer;
        }

        foreach (var child in visual.GetVisualChildren())
        {
            var match = FindScrollViewer(child);
            if (match != null)
            {
                return match;
            }
        }

        return null;
    }
}
