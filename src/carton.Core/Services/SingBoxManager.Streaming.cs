using Grpc.Core;
using carton.Core.Models;
using carton.Core.Services.SingBoxApi;

namespace carton.Core.Services;

/// <summary>
/// Long-lived streaming subscriptions over the sing-box gRPC daemon API.
///
/// Two persistent streams are maintained while the kernel runs (alongside the
/// existing log/traffic/memory monitors):
///  - <see cref="StartConnectionsMonitorAsync"/>: SubscribeConnections, merging
///    NEW/UPDATE/CLOSED deltas into a <see cref="ConnectionsSnapshot"/> following the
///    same rules as the official sing-box dashboard (see its src/api/daemon.ts).
///  - <see cref="StartGroupsMonitorAsync"/>: SubscribeGroups, republishing full group
///    snapshots that the kernel pushes on every url test history / selection change.
///
/// ViewModels consume the snapshots through <see cref="ISingBoxManager"/> events instead
/// of polling <c>GetConnectionsAsync</c>/<c>GetOutboundGroupsAsync</c> on a timer.
/// </summary>
public partial class SingBoxManager
{
    /// <summary>
    /// gRPC status codes that mean retrying the SAME subscription can never succeed
    /// (mirrors the official dashboard's isTerminalCode). For these the monitor loop
    /// stops instead of hammering the kernel with pointless reconnects; the user
    /// restarted flows pick it up when the state changes.
    /// </summary>
    private static bool IsTerminalRpcFailure(StatusCode code)
        => code is StatusCode.Unimplemented or StatusCode.NotFound
            or StatusCode.Unauthenticated or StatusCode.PermissionDenied;
    private Task? _connectionsMonitorTask;
    private CancellationTokenSource? _connectionsMonitorCts;
    private EventHandler<ConnectionsSnapshot>? _connectionsUpdated;
    private Task? _groupsMonitorTask;
    private Task? _modeMonitorTask;
    private ConnectionsSnapshot _connectionsSnapshot = ConnectionsSnapshot.Empty;
    private GroupsSnapshot _groupsSnapshot = GroupsSnapshot.Empty;
    private Dictionary<string, ConnectionSnapshotRow> _connectionRows = new(StringComparer.Ordinal);
    private readonly object _snapshotSyncRoot = new();
    private readonly object _groupsReconcileGate = new();
    private int _groupsReconcileGeneration;
    private bool _groupsReconcileScheduled;

    /// <summary>
    /// Live reconciliation workers. The scheduled flag alone cannot distinguish
    /// "worker alive" from "worker exited": a worker's finally block can run
    /// after the next push has already scheduled a new worker, and unconditionally
    /// clearing the flag there lets a third push start a second worker while the
    /// second is still running. Counting registrations instead keeps the flag set
    /// while any worker is alive and never wedges on the exception path. The
    /// counter is maintained inside the same lock as the scheduler's flag so a
    /// push can never interleave between a worker's exit and its flag clear.
    /// </summary>
    private int _groupsReconcileWorkers;
    private static readonly TimeSpan GroupsReconcileQuietPeriod = TimeSpan.FromMilliseconds(300);

    /// <summary>Last merged connection list (active connections only).</summary>
    public ConnectionsSnapshot CurrentConnections
    {
        get
        {
            lock (_snapshotSyncRoot)
            {
                return _connectionsSnapshot;
            }
        }
    }

    /// <summary>Last groups snapshot pushed by the kernel.</summary>
    public GroupsSnapshot CurrentGroups
    {
        get
        {
            lock (_snapshotSyncRoot)
            {
                return _groupsSnapshot;
            }
        }
    }

    /// <summary>Raised after a new <see cref="ConnectionsSnapshot"/> was merged.</summary>
    public event EventHandler<ConnectionsSnapshot>? ConnectionsUpdated
    {
        add
        {
            lock (_snapshotSyncRoot)
            {
                _connectionsUpdated += value;
                UpdateConnectionsMonitorStateLocked();
            }
        }
        remove
        {
            lock (_snapshotSyncRoot)
            {
                _connectionsUpdated -= value;
                UpdateConnectionsMonitorStateLocked();
            }
        }
    }

    /// <summary>Raised after the kernel pushed a new groups snapshot.</summary>
    public event EventHandler<GroupsSnapshot>? GroupsUpdated;

    /// <summary>Raised after the kernel pushed a fresh outbound mode.</summary>
    public event EventHandler<string>? ModeChanged;

    /// <summary>Latest outbound mode pushed by the kernel (null before the first snapshot).</summary>
    public string? CurrentMode
    {
        get
        {
            lock (_snapshotSyncRoot)
            {
                return _currentMode;
            }
        }
    }

    private string? _currentMode;

    /// <summary>Mode list snapshot; null until GetModeConfigAsync succeeded once.</summary>
    public List<string>? CurrentModeList
    {
        get
        {
            lock (_snapshotSyncRoot)
            {
                return _currentModeList;
            }
        }
    }

    private List<string>? _currentModeList;

    /// <inheritdoc />
    public event EventHandler<string>? KernelVersionRejected;

    internal void StopConnectionsMonitor()
    {
        lock (_snapshotSyncRoot)
        {
            StopConnectionsMonitorLocked();
        }
    }

    private void UpdateConnectionsMonitorStateLocked()
    {
        if (_state.Status == ServiceStatus.Running && _connectionsUpdated != null)
        {
            StartConnectionsMonitorLocked();
        }
        else
        {
            StopConnectionsMonitorLocked();
        }
    }

    private void StartConnectionsMonitorLocked()
    {
        if (_state.Status != ServiceStatus.Running)
        {
            return;
        }

        // A live token plus a running loop is exactly one subscription - do not add another.
        if (_connectionsMonitorTask is { IsCompleted: false } &&
            _connectionsMonitorCts is { IsCancellationRequested: false })
        {
            return;
        }

        // Stop() only cancels: the old loop still needs a moment to observe the token and
        // leave its stream. Starting the replacement in the same breath let two
        // subscriptions merge into _connectionRows at once (duplicate/stale rows while the
        // kernel restarts or the connections page is left and re-entered). Chain instead:
        // the new loop opens its stream only once the previous one has fully exited.
        var previous = _connectionsMonitorTask;
        var cts = new CancellationTokenSource();
        _connectionsMonitorCts = cts;
        _connectionsMonitorTask = Task.Run(async () =>
        {
            try
            {
                if (previous is { IsCompleted: false })
                {
                    try
                    {
                        await previous;
                    }
                    catch
                    {
                        // The previous loop reports its own failures; nothing to add here.
                    }
                }

                lock (_snapshotSyncRoot)
                {
                    // Superseded while waiting, or the kernel went away again: opening a
                    // stream now would defeat the chaining above.
                    if (!ReferenceEquals(_connectionsMonitorCts, cts) ||
                        _state.Status != ServiceStatus.Running)
                    {
                        return;
                    }
                }

                await StartConnectionsMonitorAsync(cts);
            }
            finally
            {
                // Give the field back only if this loop is still the current one: a newer
                // start may have taken it over while this loop was draining. Clearing it
                // here keeps "a non-null _connectionsMonitorCts is never disposed" true, so
                // the check in StartConnectionsMonitorLocked can read it safely.
                lock (_snapshotSyncRoot)
                {
                    if (ReferenceEquals(_connectionsMonitorCts, cts))
                    {
                        _connectionsMonitorCts = null;
                    }
                }

                cts.Dispose();
            }
        });
    }

    private void StopConnectionsMonitorLocked()
    {
        try
        {
            _connectionsMonitorCts?.Cancel();
        }
        catch
        {
        }

        _connectionsMonitorCts = null;
        // The task reference is deliberately kept: StartConnectionsMonitorLocked uses it to
        // wait for this loop to finish before opening the next subscription. The token
        // source is disposed by that loop's own wrapper once it exits.
        _connectionRows.Clear();
        _connectionsSnapshot = ConnectionsSnapshot.Empty;
    }

    private void StartStreamingMonitors()
    {
        var cancellationToken = EnsureRuntimeMonitorCancellationToken();

        if (_groupsMonitorTask is not { IsCompleted: false })
        {
            _groupsMonitorTask = Task.Run(() => StartGroupsMonitorAsync(cancellationToken));
        }

        if (_modeMonitorTask is not { IsCompleted: false })
        {
            _modeMonitorTask = Task.Run(() => StartModeMonitorAsync(cancellationToken));
        }

        lock (_snapshotSyncRoot)
        {
            UpdateConnectionsMonitorStateLocked();
        }
    }

    private async Task StartConnectionsMonitorAsync(CancellationTokenSource cts)
    {
        var cancellationToken = cts.Token;
        var consecutiveFailures = 0;

        while (_state.Status == ServiceStatus.Running && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                var apiClient = CreateApiClient();
                await foreach (var message in apiClient.SubscribeConnectionEventsAsync(cancellationToken))
                {
                    if (_state.Status != ServiceStatus.Running)
                    {
                        break;
                    }

                    consecutiveFailures = 0;
                    if (!TryMergeConnectionEvents(message, cts))
                    {
                        // Superseded while this message was in flight: the snapshot now
                        // belongs to the replacement monitor - or to nobody, if the page was
                        // left - so this loop must stop writing to it.
                        break;
                    }
                }

                if (_state.Status == ServiceStatus.Running)
                {
                    await DelaySafelyAsync(TimeSpan.FromMilliseconds(500), cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (RpcException) when (cancellationToken.IsCancellationRequested)
            {
                // Deliberate stop/restart: cancelled streams are expected, not failures.
                break;
            }
            catch (RpcException e)
            {
                if (IsTerminalRpcFailure(e.StatusCode))
                {
                    LogWarn($"Connections monitor terminal error, stopping: {e.StatusCode} {e.Message}");
                    break;
                }

                // Stream broke (e.g. kernel reload / restart): reconnect with backoff.
                consecutiveFailures++;
                if (consecutiveFailures == 1 || consecutiveFailures % 10 == 0)
                {
                    LogWarn($"Connections monitor RPC error: {e.StatusCode} {e.Message}");
                }

                await DelaySafelyAsync(TimeSpan.FromSeconds(Math.Min(5, consecutiveFailures)), cancellationToken);
            }
            catch (Exception e)
            {
                consecutiveFailures++;
                if (consecutiveFailures == 1 || consecutiveFailures % 10 == 0)
                {
                    LogWarn($"Connections monitor error: {e.Message}");
                }

                await DelaySafelyAsync(TimeSpan.FromSeconds(Math.Min(5, Math.Max(1, consecutiveFailures))), cancellationToken);
            }
        }
    }

    private async Task StartGroupsMonitorAsync(CancellationToken cancellationToken)
    {
        var consecutiveFailures = 0;

        while (_state.Status == ServiceStatus.Running && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                var apiClient = CreateApiClient();
                await foreach (var snapshot in apiClient.SubscribeGroupSnapshotsAsync(cancellationToken))
                {
                    if (_state.Status != ServiceStatus.Running)
                    {
                        break;
                    }

                    consecutiveFailures = 0;
                    PublishGroupsSnapshot(snapshot);
                    ScheduleGroupsFinalStateReconciliation(cancellationToken);
                }

                if (_state.Status == ServiceStatus.Running)
                {
                    await DelaySafelyAsync(TimeSpan.FromMilliseconds(500), cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (RpcException) when (cancellationToken.IsCancellationRequested)
            {
                // Deliberate stop/restart: cancelled streams are expected, not failures.
                break;
            }
            catch (RpcException e)
            {
                if (IsTerminalRpcFailure(e.StatusCode))
                {
                    LogWarn($"Groups monitor terminal error, stopping: {e.StatusCode} {e.Message}");
                    break;
                }

                consecutiveFailures++;
                if (consecutiveFailures == 1 || consecutiveFailures % 10 == 0)
                {
                    LogWarn($"Groups monitor RPC error: {e.StatusCode} {e.Message}");
                }

                await DelaySafelyAsync(TimeSpan.FromSeconds(Math.Min(5, consecutiveFailures)), cancellationToken);
            }
            catch (Exception e)
            {
                consecutiveFailures++;
                if (consecutiveFailures == 1 || consecutiveFailures % 10 == 0)
                {
                    LogWarn($"Groups monitor error: {e.Message}");
                }

                await DelaySafelyAsync(TimeSpan.FromSeconds(Math.Min(5, Math.Max(1, consecutiveFailures))), cancellationToken);
            }
        }
    }

    private async Task StartModeMonitorAsync(CancellationToken cancellationToken)
    {
        var consecutiveFailures = 0;

        while (_state.Status == ServiceStatus.Running && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                var apiClient = CreateApiClient();
                await foreach (var message in apiClient.SubscribeModeStreamAsync(cancellationToken))
                {
                    if (_state.Status != ServiceStatus.Running)
                    {
                        break;
                    }

                    if (string.IsNullOrWhiteSpace(message.Mode))
                    {
                        // The kernel may push an empty mode right after a reload before
                        // the clash server reports the next one; wait for the next push.
                        continue;
                    }

                    lock (_snapshotSyncRoot)
                    {
                        _currentMode = message.Mode;
                    }

                    ModeChanged?.Invoke(this, message.Mode);
                }

                if (_state.Status == ServiceStatus.Running)
                {
                    await DelaySafelyAsync(TimeSpan.FromMilliseconds(500), cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (RpcException) when (cancellationToken.IsCancellationRequested)
            {
                // Deliberate stop/restart: cancelled streams are expected, not failures.
                break;
            }
            catch (RpcException e)
            {
                if (IsTerminalRpcFailure(e.StatusCode))
                {
                    LogWarn($"Outbound mode monitor terminal error, stopping: {e.StatusCode} {e.Message}");
                    break;
                }

                // Stream broke (kernel reload / restart, or no clash server configured):
                // reconnect with backoff.
                consecutiveFailures++;
                if (consecutiveFailures == 1 || consecutiveFailures % 10 == 0)
                {
                    LogWarn($"Outbound mode monitor RPC error: {e.StatusCode} {e.Message}");
                }

                await DelaySafelyAsync(TimeSpan.FromSeconds(Math.Min(5, consecutiveFailures)), cancellationToken);
            }
            catch (Exception e)
            {
                consecutiveFailures++;
                if (consecutiveFailures == 1 || consecutiveFailures % 10 == 0)
                {
                    LogWarn($"Outbound mode monitor error: {e.Message}");
                }

                await DelaySafelyAsync(TimeSpan.FromSeconds(Math.Min(5, Math.Max(1, consecutiveFailures))), cancellationToken);
            }
        }
    }

    /// <summary>
    /// Applies one connection-events message, unless <paramref name="owner"/> has been
    /// superseded in the meantime.
    /// </summary>
    /// <remarks>
    /// Stop() resets the rows and the published snapshot under <see cref="_snapshotSyncRoot"/>,
    /// so the ownership check has to sit in the same critical section that mutates them.
    /// Without it, the last message dequeued by a loop that is already leaving would merge
    /// after the reset and leave a stale, never-again-cleared snapshot behind - the
    /// connections page is gone yet <see cref="CurrentConnections"/> is non-empty, which in
    /// turn makes the groups page skip its GetConnectionsAsync fallback (its condition is
    /// "snapshot is empty") for the rest of the kernel session.
    /// </remarks>
    /// <returns><see langword="false"/> when the message was dropped because another
    /// monitor - or a stop - now owns the snapshot.</returns>
    private bool TryMergeConnectionEvents(Daemon.ConnectionEvents message, CancellationTokenSource owner)
    {
        ConnectionsSnapshot snapshot;
        // ApplyConnectionEvents is copy-on-write and returns a fresh dictionary: the
        // local reference is immutable from this thread's perspective, so the O(n)
        // row->info conversion can run OUTSIDE the lock without any extra
        // synchronization - UI readers of CurrentConnections are never blocked by it.
        Dictionary<string, ConnectionSnapshotRow> rows;
        lock (_snapshotSyncRoot)
        {
            if (!ReferenceEquals(_connectionsMonitorCts, owner))
            {
                return false;
            }

            rows = ApplyConnectionEvents(_connectionRows, message);
            _connectionRows = rows;
        }

        snapshot = BuildConnectionsSnapshot(rows);

        // Publish under the lock (readers must see the new snapshot + counters
        // together); the expensive conversion has already happened above.
        lock (_snapshotSyncRoot)
        {
            // Re-check: a stop may have reset the snapshot while this one was being built,
            // in which case publishing it would resurrect rows that were just dropped.
            if (!ReferenceEquals(_connectionsMonitorCts, owner))
            {
                return false;
            }

            _state.ConnectionCount = snapshot.ActiveCount;
            _connectionsSnapshot = snapshot;
        }

        // Events are raised OUTSIDE the lock: handlers may read CurrentConnections or
        // re-enter manager methods that take the same lock.
        _connectionsUpdated?.Invoke(this, snapshot);
        return true;
    }

    /// <summary>
    /// Pure merge of one connection-events message into the row map, following the
    /// official dashboard rules (src/api/daemon.ts): reset=true rebuilds from scratch
    /// (kernel start / reload / reconnect), NEW upserts (skipping closed connections
    /// reported by the initial snapshot), UPDATE accumulates deltas, CLOSED drops.
    /// Extracted as pure static logic so tests exercise the production algorithm
    /// directly instead of a hand-copied replica.
    /// </summary>
    internal static Dictionary<string, ConnectionSnapshotRow> ApplyConnectionEvents(
        Dictionary<string, ConnectionSnapshotRow> previousRows,
        Daemon.ConnectionEvents message)
    {
        var rows = message.Reset
            ? new Dictionary<string, ConnectionSnapshotRow>(StringComparer.Ordinal)
            : new Dictionary<string, ConnectionSnapshotRow>(previousRows);

        foreach (var evt in message.Events)
        {
            switch (evt.Type)
            {
                case Daemon.ConnectionEventType.ConnectionEventNew:
                    if (evt.Connection is { } newConnection && newConnection.ClosedAt == 0)
                    {
                        // The initial snapshot reports recently closed connections as NEW
                        // events with ClosedAt set - skip them so only live ones remain.
                        rows[evt.Id] = ConnectionSnapshotRow.FromProto(newConnection);
                    }
                    break;

                case Daemon.ConnectionEventType.ConnectionEventUpdate:
                    if (rows.TryGetValue(evt.Id, out var row))
                    {
                        rows[evt.Id] = row.WithDelta(evt.UplinkDelta, evt.DownlinkDelta);
                    }
                    break;

                case Daemon.ConnectionEventType.ConnectionEventClosed:
                    rows.Remove(evt.Id);
                    break;
            }
        }

        return rows;
    }

    private static ConnectionsSnapshot BuildConnectionsSnapshot(Dictionary<string, ConnectionSnapshotRow> rows)
    {
        var active = new List<ConnectionInfo>(rows.Count);
        long totalUplink = 0;
        long totalDownlink = 0;
        foreach (var row in rows.Values)
        {
            var info = row.ToConnectionInfo();
            active.Add(info);
            totalUplink += row.UplinkTotal;
            totalDownlink += row.DownlinkTotal;
        }

        return new ConnectionsSnapshot(active, active.Count, totalUplink, totalDownlink);
    }

    private void PublishGroupsSnapshot(Daemon.Groups snapshot)
    {
        var groups = new List<OutboundGroup>(snapshot.Group.Count);
        foreach (var g in snapshot.Group)
        {
            var group = new OutboundGroup
            {
                Tag = g.Tag,
                Type = g.Type,
                Selected = g.Selected
            };

            foreach (var item in g.Items)
            {
                group.Items.Add(new OutboundItem
                {
                    Tag = item.Tag,
                    Type = item.Type,
                    UrlTestTime = item.UrlTestTime,
                    UrlTestDelay = item.UrlTestDelay
                });
            }

            groups.Add(group);
        }

        PublishGroupsSnapshot(groups);
    }

    private void PublishGroupsSnapshot(IReadOnlyList<OutboundGroup> groups)
    {
        var groupsSnapshot = new GroupsSnapshot(groups);
        lock (_snapshotSyncRoot)
        {
            _groupsSnapshot = groupsSnapshot;
        }

        // Raised outside the lock, same discipline as ConnectionsUpdated.
        GroupsUpdated?.Invoke(this, groupsSnapshot);
    }

    /// <summary>
    /// sing-box 1.14's URLTest update order is:
    /// StoreURLTestHistory (emits SubscribeGroups) -> batch.Wait -> performUpdateCheck
    /// (sets selectedOutboundTCP/UDP). The final selected tag therefore has no stream
    /// notification of its own. After the history burst goes quiet, read one fresh
    /// groups snapshot and publish it so automatic and manual URLTest selections reach
    /// CurrentGroups/UI without requiring a page change. Each new stream event resets
    /// this timer; no polling loop is introduced.
    /// </summary>
    private void ScheduleGroupsFinalStateReconciliation(CancellationToken monitorToken)
    {
        lock (_groupsReconcileGate)
        {
            _groupsReconcileGeneration++;
            if (_groupsReconcileScheduled)
            {
                return;
            }

            _groupsReconcileScheduled = true;
            _groupsReconcileWorkers++;
        }

        // One worker per monitor lifetime, regardless of how many per-node history
        // pushes arrive. New pushes only increment a generation integer: no per-event
        // CTS/Task allocation and cancellation storm during large URLTest groups.
        _ = ReconcileGroupsFinalStateAsync(monitorToken);
    }

    private async Task ReconcileGroupsFinalStateAsync(CancellationToken monitorToken)
    {
        try
        {
            while (!monitorToken.IsCancellationRequested)
            {
                int generation;
                lock (_groupsReconcileGate)
                {
                    generation = _groupsReconcileGeneration;
                }

                await Task.Delay(GroupsReconcileQuietPeriod, monitorToken);

                lock (_groupsReconcileGate)
                {
                    if (generation != _groupsReconcileGeneration)
                    {
                        continue;
                    }
                }

                if (_state.Status != ServiceStatus.Running)
                {
                    return;
                }

                var groups = await CreateApiClient().GetOutboundGroupsAsync();
                if (groups.Count > 0 && !monitorToken.IsCancellationRequested && _state.Status == ServiceStatus.Running)
                {
                    PublishGroupsSnapshot(groups);
                }

                lock (_groupsReconcileGate)
                {
                    if (generation == _groupsReconcileGeneration)
                    {
                        _groupsReconcileScheduled = false;
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LogDebug($"Groups final-state reconciliation failed: {ex.Message}");
        }
        finally
        {
            // Unregister and clear inside the same lock the scheduler uses, so a
            // push that schedules a new worker can never interleave between this
            // worker's decrement and its flag clear (which would leave the new
            // worker unsupervised and allow overlapping GetOutboundGroupsAsync).
            lock (_groupsReconcileGate)
            {
                _groupsReconcileWorkers--;

                // Only the last exiting worker clears the flag: an exception path
                // still unregisters (the flag cannot wedge true), while a worker
                // scheduled by a newer push keeps it set (no overlapping
                // GetOutboundGroupsAsync calls).
                if (_groupsReconcileWorkers == 0)
                {
                    _groupsReconcileScheduled = false;
                }
            }
        }
    }

    /// <summary>
    /// Backoff delay that swallows the cancellation that fires while sleeping in a
    /// catch block - a raw Task.Delay(..., ct) there throws again and faults the
    /// monitor task, which nobody awaits (silent death).
    /// </summary>
    private static async Task DelaySafelyAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ResetStreamingSnapshots()
    {
        lock (_snapshotSyncRoot)
        {
            _connectionRows = new Dictionary<string, ConnectionSnapshotRow>(StringComparer.Ordinal);
            _connectionsSnapshot = ConnectionsSnapshot.Empty;
            _groupsSnapshot = GroupsSnapshot.Empty;
            _currentMode = null;
            _currentModeList = null;
        }
    }

    /// <summary>
    /// Internal mutable row for merging connection deltas; converted to
    /// <see cref="ConnectionInfo"/> for publishing.
    /// </summary>
    internal sealed class ConnectionSnapshotRow
    {
        public required string Id { get; init; }
        public required string Inbound { get; init; }
        public required string InboundType { get; init; }
        public required string Network { get; init; }
        public required string Source { get; init; }
        public required string Destination { get; init; }
        public required string Domain { get; init; }
        public required string Protocol { get; init; }
        public required string Outbound { get; init; }
        public required List<string> Chains { get; init; }
        public required string Process { get; init; }
        public required long CreatedAt { get; init; }
        public long UplinkTotal { get; private set; }
        public long DownlinkTotal { get; private set; }

        public static ConnectionSnapshotRow FromProto(Daemon.Connection c) => new()
        {
            Id = c.Id,
            Inbound = c.Inbound,
            InboundType = c.InboundType,
            Network = c.Network,
            Source = c.Source,
            Destination = string.IsNullOrWhiteSpace(c.Domain) ? c.Destination : $"{c.Domain} ({c.Destination})",
            Domain = c.Domain,
            Protocol = c.Protocol,
            Outbound = c.Outbound,
            Chains = c.ChainList.ToList(),
            Process = c.ProcessInfo?.ProcessPath ?? string.Empty,
            CreatedAt = c.CreatedAt,
            UplinkTotal = c.UplinkTotal,
            DownlinkTotal = c.DownlinkTotal
        };

        public ConnectionSnapshotRow WithDelta(long uplinkDelta, long downlinkDelta) => new()
        {
            Id = Id,
            Inbound = Inbound,
            InboundType = InboundType,
            Network = Network,
            Source = Source,
            Destination = Destination,
            Domain = Domain,
            Protocol = Protocol,
            Outbound = Outbound,
            Chains = Chains,
            Process = Process,
            CreatedAt = CreatedAt,
            UplinkTotal = UplinkTotal + uplinkDelta,
            DownlinkTotal = DownlinkTotal + downlinkDelta
        };

        public ConnectionInfo ToConnectionInfo() => new()
        {
            Id = Id,
            StartTime = CreatedAt > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(CreatedAt).LocalDateTime : DateTime.Now,
            Inbound = Inbound,
            InboundType = InboundType,
            Process = Process,
            Ip = Source,
            Source = Source,
            Destination = Destination,
            Domain = Domain,
            Network = Network,
            Protocol = Protocol,
            Outbound = Outbound,
            Chains = Chains,
            Upload = UplinkTotal,
            Download = DownlinkTotal
        };
    }
}
