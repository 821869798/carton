using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using carton.Core.Services;
using carton.Core.Models;
using carton.Core.Utilities;
using carton.GUI.Models;

namespace carton.ViewModels;

public partial class ConnectionsViewModel : PageViewModelBase, IDisposable
{
    private readonly ISingBoxManager? _singBoxManager;
    private IReadOnlyList<ConnectionInfo> _allConnections = Array.Empty<ConnectionInfo>();
    private readonly object _refreshGate = new();
    private ConnectionsSnapshot? _pendingSnapshot;
    private EventHandler<ConnectionsSnapshot>? _streamHandler;
    private long _refreshGeneration;
    private bool _refreshQueued;
    private bool _disposed;
    private readonly Dictionary<string, ConnectionItemViewModel> _connectionItemCache = new(StringComparer.Ordinal);
    private readonly HashSet<string> _activeConnectionIds = new(StringComparer.Ordinal);
    private readonly List<string> _staleConnectionIds = new();

    public override NavigationPage PageType => NavigationPage.Connections;

    [ObservableProperty]
    private ObservableCollection<ConnectionItemViewModel> _connections = new();

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private int _connectionCount;

    [ObservableProperty]
    private int _visibleConnectionCount;

    [ObservableProperty]
    private ConnectionItemViewModel? _selectedConnection;

    private bool _isOnPage;
    private bool _isWindowVisible = true;
    private int _pendingFilterRefresh;
    private bool _subscribedToStreams;

    public ConnectionsViewModel()
    {
        InitializePageMetadata("Connections", "Navigation.Connections", "Connections");
    }

    public ConnectionsViewModel(ISingBoxManager singBoxManager) : this()
    {
        _singBoxManager = singBoxManager;
    }

    /// <summary>
    /// Called when the user navigates to the Connections page.
    /// </summary>
    public override void OnNavigatedTo()
    {
        _isOnPage = true;
        UpdateRefreshState();
        RequestApplyFilters();
    }

    private void QueueConnections(ConnectionsSnapshot snapshot, long generation)
    {
        lock (_refreshGate)
        {
            if (_disposed || !_subscribedToStreams || generation != _refreshGeneration)
            {
                return;
            }

            // Complete, copy-on-write manager snapshots, never incremental events.
            _pendingSnapshot = snapshot;
            if (_refreshQueued)
            {
                return;
            }
            _refreshQueued = true;
        }

        Dispatcher.UIThread.Post(() => FlushConnections(generation), DispatcherPriority.Background);
    }

    private void FlushConnections(long generation)
    {
        ConnectionsSnapshot? snapshot;
        lock (_refreshGate)
        {
            if (_disposed || !_subscribedToStreams || generation != _refreshGeneration)
            {
                return;
            }
            snapshot = _pendingSnapshot;
            _pendingSnapshot = null;
            _refreshQueued = false;
        }

        if (snapshot == null || !_isOnPage || !_isWindowVisible)
        {
            return;
        }

        _allConnections = snapshot.ActiveConnections;
        ConnectionCount = _allConnections.Count;
        PruneConnectionItemCache();
        ApplyFilters();
    }

    public void SetWindowVisible(bool isVisible)
    {
        _isWindowVisible = isVisible;
        UpdateRefreshState();
    }

    private void UpdateRefreshState()
    {
        if (!_disposed && _singBoxManager is { IsRunning: true } && _isOnPage && _isWindowVisible)
        {
            lock (_refreshGate)
            {
                if (!_subscribedToStreams)
                {
                    var generation = ++_refreshGeneration;
                    _streamHandler = (_, snapshot) => QueueConnections(snapshot, generation);
                    _subscribedToStreams = true;
                    _singBoxManager.ConnectionsUpdated += _streamHandler;
                }

                // Seed while holding the gate so a newer arriving snapshot cannot be
                // replaced by this initial read. Manager events are raised outside its lock.
                QueueConnections(_singBoxManager.CurrentConnections, _refreshGeneration);
            }
            return;
        }

        StopRefresh();
    }

    private void StopRefresh()
    {
        EventHandler<ConnectionsSnapshot>? handler;
        lock (_refreshGate)
        {
            ++_refreshGeneration;
            _pendingSnapshot = null;
            _refreshQueued = false;
            _subscribedToStreams = false;
            handler = _streamHandler;
            _streamHandler = null;
        }
        if (handler != null && _singBoxManager != null)
        {
            _singBoxManager.ConnectionsUpdated -= handler;
        }
    }

    /// <summary>
    /// Called when the user navigates away from the Connections page.
    /// </summary>
    public override void OnNavigatedFrom()
    {
        _isOnPage = false;
        UpdateRefreshState();
    }

    /// <summary>
    /// Called when sing-box status changes. Streaming monitors in the manager stop
    /// with the kernel; just clear the local view when it stops.
    /// </summary>
    public void OnServiceStatusChanged(bool isRunning)
    {
        if (isRunning)
        {
            UpdateRefreshState();
            return;
        }

        // MainViewModel forwards status changes on the UI thread. Invalidate queued
        // work before clearing, so an old stream cannot repopulate a stopped session.
        StopRefresh();
        _allConnections = Array.Empty<ConnectionInfo>();
        _connectionItemCache.Clear();
        _activeConnectionIds.Clear();
        _staleConnectionIds.Clear();
        Connections.Clear();
        SelectedConnection = null;
        ConnectionCount = 0;
        VisibleConnectionCount = 0;
    }

    partial void OnSearchTextChanged(string value)
    {
        RequestApplyFilters();
    }

    [RelayCommand]
    private async Task CloseAll()
    {
        if (_singBoxManager == null)
        {
            return;
        }

        try
        {
            await _singBoxManager.CloseAllConnectionsAsync();
            // The kernel pushes CLOSED events for each connection through the stream,
            // so the list refreshes itself; nothing else to poll.
        }
        catch (Exception ex)
        {
            // No status bar on this page: surface the failure through the shared log
            // channel so "clicked and nothing happened" stays diagnosable.
            LogCloseFailure("close all connections", ex);
        }
    }

    [RelayCommand]
    private async Task CloseConnection(ConnectionItemViewModel? connection)
    {
        if (_singBoxManager == null || connection == null) return;

        try
        {
            await _singBoxManager.CloseConnectionAsync(connection.Id);
            connection.MarkClosed();
            // CLOSED event arrives via the stream and removes the row.
        }
        catch (Exception ex)
        {
            LogCloseFailure($"close connection {connection.Id}", ex);
        }
    }

    private void LogCloseFailure(string operation, Exception ex)
    {
        // Best-effort diagnostics without a status bar: keep the failure observable
        // (debug log + property) so "clicked and nothing happened" stays diagnosable.
        CloseOperationError = $"Failed to {operation}: {ex.Message}";
        System.Diagnostics.Debug.WriteLine($"[Connections] {CloseOperationError}");
    }

    [ObservableProperty]
    private string? _closeOperationError;

    [RelayCommand]
    private void ClearSelectedConnection()
    {
        SelectedConnection = null;
    }

    private static string FormatText(string? value, string? fallback = null)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        if (!string.IsNullOrWhiteSpace(fallback))
        {
            return fallback;
        }

        return "-";
    }

    private void RequestApplyFilters()
    {
        if (!_isOnPage || !_isWindowVisible)
        {
            return;
        }

        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(RequestApplyFilters);
            return;
        }

        if (Interlocked.Exchange(ref _pendingFilterRefresh, 1) == 1)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _pendingFilterRefresh, 0);
            ApplyFilters();
        }, DispatcherPriority.Background);
    }

    private void ApplyFilters()
    {
        if (_disposed || !_isOnPage || !_isWindowVisible || !Dispatcher.UIThread.CheckAccess())
        {
            return;
        }

        var searchText = SearchText.Trim();
        var writeIndex = 0;
        var selectedConnection = SelectedConnection;
        var selectedConnectionExistsInSnapshots = false;
        for (var i = 0; i < _allConnections.Count; i++)
        {
            var snapshot = _allConnections[i];
            if (selectedConnection != null && string.Equals(snapshot.Id, selectedConnection.Id, StringComparison.Ordinal))
            {
                selectedConnectionExistsInSnapshots = true;
            }

            if (!MatchesSearch(snapshot, searchText))
            {
                continue;
            }

            ConnectionItemViewModel connection;
            if (_connectionItemCache.TryGetValue(snapshot.Id, out var cachedConnection))
            {
                connection = cachedConnection;
                connection.Update(snapshot);
            }
            else
            {
                connection = ConnectionItemViewModel.FromSnapshot(snapshot);
                _connectionItemCache[snapshot.Id] = connection;
            }

            if (writeIndex < Connections.Count)
            {
                if (!ReferenceEquals(Connections[writeIndex], connection))
                {
                    Connections[writeIndex] = connection;
                }
            }
            else
            {
                Connections.Add(connection);
            }

            writeIndex++;
            if (ReferenceEquals(selectedConnection, connection))
            {
                selectedConnectionExistsInSnapshots = true;
            }
        }

        for (var i = Connections.Count - 1; i >= writeIndex; i--)
        {
            Connections.RemoveAt(i);
        }

        if (selectedConnection != null && !selectedConnectionExistsInSnapshots)
        {
            selectedConnection.MarkClosed();
        }

        VisibleConnectionCount = writeIndex;

    }

    partial void OnSelectedConnectionChanged(ConnectionItemViewModel? oldValue, ConnectionItemViewModel? newValue)
    {
        if (oldValue != null)
        {
            oldValue.IsSelected = false;
        }

        if (newValue != null)
        {
            newValue.IsSelected = true;
        }
    }

    private static bool MatchesSearch(ConnectionInfo connection, string searchText)
    {
        return string.IsNullOrWhiteSpace(searchText) ||
               FormatText(connection.Process, connection.Inbound).Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
               FormatText(connection.Source, connection.Ip).Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
               FormatText(connection.Destination, connection.Domain).Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
               connection.Network.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
               connection.Protocol.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
               FormatText(connection.Outbound).Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
               connection.Inbound.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
               connection.InboundType.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
               connection.Process.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
               connection.Ip.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
               connection.Source.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
               connection.Domain.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
               connection.Destination.Contains(searchText, StringComparison.OrdinalIgnoreCase);
    }

    private void PruneConnectionItemCache()
    {
        _activeConnectionIds.Clear();
        for (var i = 0; i < _allConnections.Count; i++)
        {
            _activeConnectionIds.Add(_allConnections[i].Id);
        }

        _staleConnectionIds.Clear();
        foreach (var id in _connectionItemCache.Keys)
        {
            if (!_activeConnectionIds.Contains(id))
            {
                _staleConnectionIds.Add(id);
            }
        }

        for (var i = 0; i < _staleConnectionIds.Count; i++)
        {
            _connectionItemCache.Remove(_staleConnectionIds[i]);
        }
    }

    public void Dispose()
    {
        lock (_refreshGate)
        {
            _disposed = true;
        }
        StopRefresh();
        ReleasePageMetadata();

        _allConnections = Array.Empty<ConnectionInfo>();
        _connectionItemCache.Clear();
        _activeConnectionIds.Clear();
        _staleConnectionIds.Clear();
        Connections.Clear();
        SelectedConnection = null;
        ConnectionCount = 0;
        VisibleConnectionCount = 0;
    }
}

public partial class ConnectionItemViewModel : ObservableObject
{
    private ConnectionInfo? _lastSnapshot;
    private CultureInfo? _lastCulture;

    internal static ConnectionItemViewModel FromSnapshot(ConnectionInfo snapshot)
    {
        var item = new ConnectionItemViewModel();
        item.Update(snapshot);
        return item;
    }

    internal void Update(ConnectionInfo snapshot)
    {
        var previous = _lastSnapshot;
        var culture = CultureInfo.CurrentCulture;
        var cultureChanged = !ReferenceEquals(_lastCulture, culture);
        Id = snapshot.Id;
        Process = FormatTextWithFallback(snapshot.Process, snapshot.Inbound);
        Source = FormatTextWithFallback(snapshot.Source, snapshot.Ip);
        Destination = FormatTextWithFallback(snapshot.Destination, snapshot.Domain);
        if (previous == null || previous.Network != snapshot.Network)
        {
            NetworkDisplay = FormatNetworkDisplay(snapshot.Network);
        }
        Network = snapshot.Network;
        Protocol = snapshot.Protocol;
        Outbound = FormatText(snapshot.Outbound);
        if (previous == null || previous.Upload != snapshot.Upload || cultureChanged)
        {
            Upload = UploadTotal = FormatHelper.FormatBytes(snapshot.Upload);
        }
        if (previous == null || previous.Download != snapshot.Download || cultureChanged)
        {
            Download = DownloadTotal = FormatHelper.FormatBytes(snapshot.Download);
        }
        if (previous == null || previous.Inbound != snapshot.Inbound || previous.InboundType != snapshot.InboundType)
        {
            Inbound = FormatInbound(snapshot.InboundType, snapshot.Inbound);
            InboundSummary = Inbound;
        }
        RawProcess = FormatText(snapshot.Process);
        Ip = FormatText(snapshot.Ip);
        RawSource = FormatText(snapshot.Source);
        Domain = FormatText(snapshot.Domain);
        RawDestination = FormatText(snapshot.Destination);
        if (previous == null || previous.StartTime != snapshot.StartTime || cultureChanged)
        {
            StartedAt = snapshot.StartTime.ToString("yyyy-MM-dd HH:mm:ss", culture);
        }
        if (previous == null || previous.Outbound != snapshot.Outbound || !SameChain(previous.Chains, snapshot.Chains))
        {
            Chain = FormatChain(snapshot.Chains, Outbound);
            Route = FormatActiveRoute(snapshot.Chains, Outbound);
        }
        Status = "Active";
        ProcessDisplay = string.IsNullOrWhiteSpace(snapshot.Process) ? string.Empty : snapshot.Process;
        IsClosed = false;
        _lastSnapshot = snapshot;
        _lastCulture = culture;
    }

    private static string FormatTextWithFallback(string value, string fallback)
        => !string.IsNullOrWhiteSpace(value) ? value : FormatText(fallback);

    private static bool SameChain(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count != right.Count) return false;
        for (var i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i], right[i], StringComparison.Ordinal)) return false;
        }
        return true;
    }

    internal void MarkClosed()
    {
        Status = "Closed";
        IsClosed = true;
    }

    private static string FormatText(string? value)
        => string.IsNullOrWhiteSpace(value) ? "-" : value;

    private static string FormatOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value;

    private static string FormatInbound(string? type, string? tag)
    {
        var normalizedType = FormatOptional(type);
        var normalizedTag = FormatOptional(tag);
        if (normalizedType.Length > 0 && normalizedTag.Length > 0)
        {
            return string.Concat(normalizedType, "/", normalizedTag);
        }

        return FormatText(normalizedTag.Length > 0 ? normalizedTag : normalizedType);
    }

    private static string FormatNetworkDisplay(string value)
    {
        if (value.Length == 0)
        {
            return string.Empty;
        }

        var hasLower = false;
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsLower(value[i]))
            {
                hasLower = true;
                break;
            }
        }

        return hasLower ? value.ToUpperInvariant() : value;
    }

    private static string FormatActiveRoute(IReadOnlyList<string> chains, string fallback)
    {
        if (chains.Count > 0)
        {
            return FormatText(chains[^1]);
        }

        return FormatText(fallback);
    }

    private static string FormatChain(IReadOnlyList<string> chains, string fallback)
    {
        if (chains.Count == 0)
        {
            return FormatText(fallback);
        }

        var values = new string[chains.Count];
        for (var i = 0; i < chains.Count; i++)
        {
            values[i] = chains[chains.Count - 1 - i];
        }

        return string.Join(" / ", values);
    }

    [ObservableProperty]
    private string _id = string.Empty;

    [ObservableProperty]
    private string _process = string.Empty;

    [ObservableProperty]
    private string _source = string.Empty;

    [ObservableProperty]
    private string _destination = string.Empty;

    [ObservableProperty]
    private string _protocol = string.Empty;

    [ObservableProperty]
    private string _outbound = string.Empty;

    [ObservableProperty]
    private string _upload = string.Empty;

    [ObservableProperty]
    private string _download = string.Empty;

    [ObservableProperty]
    private string _uploadTotal = string.Empty;

    [ObservableProperty]
    private string _downloadTotal = string.Empty;

    [ObservableProperty]
    private string _inbound = string.Empty;

    [ObservableProperty]
    private string _rawProcess = string.Empty;

    [ObservableProperty]
    private string _ip = string.Empty;

    [ObservableProperty]
    private string _rawSource = string.Empty;

    [ObservableProperty]
    private string _domain = string.Empty;

    [ObservableProperty]
    private string _rawDestination = string.Empty;

    [ObservableProperty]
    private string _startedAt = string.Empty;

    [ObservableProperty]
    private string _chain = string.Empty;

    [ObservableProperty]
    private string _route = string.Empty;

    [ObservableProperty]
    private string _inboundSummary = string.Empty;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private string _network = string.Empty;

    [ObservableProperty]
    private string _networkDisplay = string.Empty;

    [ObservableProperty]
    private string _processDisplay = string.Empty;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isClosed;
}
