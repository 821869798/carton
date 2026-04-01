using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
    private readonly List<ConnectionSnapshot> _allConnections = new();

    public override NavigationPage PageType => NavigationPage.Connections;

    [ObservableProperty]
    private ObservableCollection<ConnectionItemViewModel> _connections = new();

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private int _connectionCount;

    [ObservableProperty]
    private int _visibleConnectionCount;

    private readonly DispatcherTimer? _refreshTimer;
    private bool _isRefreshing;
    private bool _isOnPage;
    private bool _isWindowVisible = true;
    private int _pendingFilterRefresh;

    public ConnectionsViewModel()
    {
        InitializePageMetadata("Connections", "Navigation.Connections", "Connections");
    }

    public ConnectionsViewModel(ISingBoxManager singBoxManager) : this()
    {
        _singBoxManager = singBoxManager;
        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _refreshTimer.Tick += OnRefreshTimerTick;
        // Timer is NOT started here — it starts when user navigates to this page
    }

    /// <summary>
    /// Called when the user navigates to the Connections page.
    /// </summary>
    public void OnNavigatedTo()
    {
        _isOnPage = true;
        UpdateRefreshState();
        RequestApplyFilters();
    }

    private async void OnRefreshTimerTick(object? sender, EventArgs e)
    {
        await RefreshAsync();
    }

    public void SetWindowVisible(bool isVisible)
    {
        _isWindowVisible = isVisible;
        UpdateRefreshState();
    }

    private void UpdateRefreshState()
    {
        if (_singBoxManager is { IsRunning: true } && _isOnPage && _isWindowVisible)
        {
            _refreshTimer?.Start();
            _ = RefreshAsync();
            return;
        }

        _refreshTimer?.Stop();
    }

    /// <summary>
    /// Called when the user navigates away from the Connections page.
    /// </summary>
    public void OnNavigatedFrom()
    {
        _isOnPage = false;
        UpdateRefreshState();
    }

    /// <summary>
    /// Called when sing-box status changes. Starts/stops polling accordingly.
    /// </summary>
    public void OnServiceStatusChanged(bool isRunning)
    {
        if (isRunning)
        {
            UpdateRefreshState();
        }
        else
        {
            _refreshTimer?.Stop();
            if (!isRunning)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    _allConnections.Clear();
                    Connections.Clear();
                    ConnectionCount = 0;
                    VisibleConnectionCount = 0;
                });
            }
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        RequestApplyFilters();
    }

    [RelayCommand]
    private async Task CloseAll()
    {
        if (_singBoxManager != null)
        {
            await _singBoxManager.CloseAllConnectionsAsync();
            await RefreshAsync();
        }
    }

    [RelayCommand]
    private async Task CloseConnection(ConnectionItemViewModel? connection)
    {
        if (_singBoxManager == null || connection == null) return;
        await _singBoxManager.CloseConnectionAsync(connection.Id);
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_singBoxManager == null || _isRefreshing || !_isOnPage || !_isWindowVisible) return;
        _isRefreshing = true;

        try
        {
            var connections = await _singBoxManager.GetConnectionsAsync();
            var snapshots = new List<ConnectionSnapshot>(connections.Count);
            foreach (var conn in connections)
            {
                var process = FormatText(conn.Process, conn.Inbound);
                var source = FormatText(conn.Source, conn.Ip);
                var destination = FormatText(conn.Destination, conn.Domain);
                var protocol = FormatText(conn.Protocol);
                var outbound = FormatText(conn.Outbound);

                snapshots.Add(new ConnectionSnapshot(
                    conn.Id,
                    process,
                    source,
                    destination,
                    protocol,
                    outbound,
                    FormatBytes(conn.Upload),
                    FormatBytes(conn.Download),
                    BuildSearchableText(process, source, destination, protocol, outbound, conn.Inbound, conn.Process, conn.Ip, conn.Source, conn.Domain, conn.Destination)));
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!_isOnPage || !_isWindowVisible)
                {
                    return;
                }

                _allConnections.Clear();
                _allConnections.AddRange(snapshots);
                ConnectionCount = snapshots.Count;
                RequestApplyFilters();
            }, DispatcherPriority.Background);
        }
        catch
        {
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private static string FormatBytes(long bytes) => FormatHelper.FormatBytes(bytes);

    private static string FormatText(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value!;
            }
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
        if (!Dispatcher.UIThread.CheckAccess())
        {
            return;
        }

        var searchText = SearchText.Trim();
        var reusableConnections = new Dictionary<string, Queue<ConnectionItemViewModel>>(Connections.Count, StringComparer.Ordinal);
        for (var i = 0; i < Connections.Count; i++)
        {
            var existing = Connections[i];
            if (!reusableConnections.TryGetValue(existing.Id, out var queue))
            {
                queue = new Queue<ConnectionItemViewModel>();
                reusableConnections[existing.Id] = queue;
            }

            queue.Enqueue(existing);
        }

        var filteredConnections = new List<ConnectionItemViewModel>(_allConnections.Count);
        for (var i = 0; i < _allConnections.Count; i++)
        {
            var snapshot = _allConnections[i];
            if (!MatchesSearch(snapshot, searchText))
            {
                continue;
            }

            ConnectionItemViewModel connection;
            if (reusableConnections.TryGetValue(snapshot.Id, out var queue) && queue.Count > 0)
            {
                connection = queue.Dequeue();
                connection.Update(snapshot);
            }
            else
            {
                connection = ConnectionItemViewModel.FromSnapshot(snapshot);
            }

            filteredConnections.Add(connection);
        }

        var commonCount = Math.Min(Connections.Count, filteredConnections.Count);
        for (var i = 0; i < commonCount; i++)
        {
            if (!ReferenceEquals(Connections[i], filteredConnections[i]))
            {
                Connections[i] = filteredConnections[i];
            }
        }

        for (var i = Connections.Count - 1; i >= filteredConnections.Count; i--)
        {
            Connections.RemoveAt(i);
        }

        for (var i = Connections.Count; i < filteredConnections.Count; i++)
        {
            Connections.Add(filteredConnections[i]);
        }

        VisibleConnectionCount = filteredConnections.Count;
    }

    private static bool MatchesSearch(ConnectionSnapshot connection, string searchText)
    {
        return string.IsNullOrWhiteSpace(searchText) ||
               connection.SearchableText.Contains(searchText, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildSearchableText(params string?[] values)
    {
        return string.Join('\n', values);
    }

    public void Dispose()
    {
        if (_refreshTimer == null)
        {
            return;
        }

        _refreshTimer.Stop();
        _refreshTimer.Tick -= OnRefreshTimerTick;
        _allConnections.Clear();
        Connections.Clear();
        VisibleConnectionCount = 0;
    }
}

public partial class ConnectionItemViewModel : ObservableObject
{
    internal static ConnectionItemViewModel FromSnapshot(ConnectionSnapshot snapshot)
    {
        var item = new ConnectionItemViewModel();
        item.Update(snapshot);
        return item;
    }

    internal void Update(ConnectionSnapshot snapshot)
    {
        Id = snapshot.Id;
        Process = snapshot.Process;
        Source = snapshot.Source;
        Destination = snapshot.Destination;
        Protocol = snapshot.Protocol;
        Outbound = snapshot.Outbound;
        Upload = snapshot.Upload;
        Download = snapshot.Download;
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
}

internal sealed class ConnectionSnapshot
{
    public ConnectionSnapshot(
        string id,
        string process,
        string source,
        string destination,
        string protocol,
        string outbound,
        string upload,
        string download,
        string searchableText)
    {
        Id = id;
        Process = process;
        Source = source;
        Destination = destination;
        Protocol = protocol;
        Outbound = outbound;
        Upload = upload;
        Download = download;
        SearchableText = searchableText;
    }

    public string Id { get; }

    public string Process { get; }

    public string Source { get; }

    public string Destination { get; }

    public string Protocol { get; }

    public string Outbound { get; }

    public string Upload { get; }

    public string Download { get; }

    public string SearchableText { get; }
}
