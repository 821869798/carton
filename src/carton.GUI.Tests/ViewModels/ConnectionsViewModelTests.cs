using System.Reflection;
using System.Globalization;
using Avalonia.Headless;
using Avalonia.Threading;
using carton.Core.Models;
using carton.Core.Services;
using carton.Core.Utilities;
using carton.ViewModels;
using Xunit;

namespace carton.GUI.Tests.ViewModels;

[Collection("Avalonia UI")]
public class ConnectionsViewModelTests
{
    private static Task OnUI(Action action)
        => OnUI(() => { action(); return Task.CompletedTask; });

    private static async Task OnUI(Func<Task> action)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));
        await session.Dispatch(action, CancellationToken.None);
    }

    private static (ConnectionsViewModel Vm, ManagerProxy Manager) Create()
    {
        var manager = DispatchProxy.Create<ISingBoxManager, ManagerProxy>();
        var proxy = (ManagerProxy)(object)manager;
        var vm = new ConnectionsViewModel(manager);
        vm.OnNavigatedTo();
        Pump();
        return (vm, proxy);
    }

    private static void Pump() => Dispatcher.UIThread.RunJobs();

    internal static ConnectionInfo Row(string id, long bytes = 1024) => new()
    {
        Id = id, Process = "browser", Source = "127.0.0.1:1234", Destination = "example.com:443",
        Network = "tcp", Protocol = "tls", Outbound = "proxy", Inbound = "mixed-in", InboundType = "mixed",
        Ip = "1.2.3.4", Domain = "example.com", StartTime = new DateTime(2026, 1, 2, 3, 4, 5),
        Upload = bytes, Download = bytes * 2, Chains = ["proxy", "region"]
    };
    internal static ConnectionsSnapshot Snapshot(params ConnectionInfo[] rows) => new(rows, rows.Length, 0, 0);

    [Fact]
    public Task BurstOnlyPublishesLatestFullStateAndKeepsAllRows() => OnUI(BurstScenario);

    private void BurstScenario()
    {
        var (vm, manager) = Create();
        using (vm)
        {
            var changes = 0;
            vm.Connections.CollectionChanged += (_, _) => changes++;
            for (var i = 0; i < 100; i++) manager.Emit(Snapshot(Row("obsolete-" + i)));
            var rows = Enumerable.Range(0, 5000).Select(i => Row(i.ToString())).ToArray();
            manager.Emit(Snapshot(rows));
            Assert.Empty(vm.Connections);
            Pump();
            Assert.Equal(5000, changes);
            Assert.Equal(rows.Select(r => r.Id), vm.Connections.Select(r => r.Id));
            Assert.Equal(5000, vm.ConnectionCount);
            Assert.Equal(5000, vm.VisibleConnectionCount);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task LeavingOrHidingInvalidatesQueuedAndInFlightCallbacks(bool hide) => OnUI(() => VisibilityScenario(hide));

    private void VisibilityScenario(bool hide)
    {
        var (vm, manager) = Create();
        using (vm)
        {
            var old = manager.Handler!;
            manager.Emit(Snapshot(Row("old")));
            if (hide) vm.SetWindowVisible(false); else vm.OnNavigatedFrom();
            Assert.Null(manager.Handler);
            old(manager, Snapshot(Row("late")));
            Pump();
            Assert.Empty(vm.Connections);
            manager.Current = Snapshot(Row("fresh"));
            if (hide) vm.SetWindowVisible(true); else vm.OnNavigatedTo();
            old(manager, Snapshot(Row("late-again")));
            Pump();
            Assert.Equal("fresh", Assert.Single(vm.Connections).Id);
        }
    }

    [Fact]
    public Task StopRestartDoesNotResurrectOldRowsOrClearNewRows() => OnUI(RestartScenario);

    private void RestartScenario()
    {
        var (vm, manager) = Create();
        using (vm)
        {
            manager.Emit(Snapshot(Row("first")));
            Pump();
            vm.SelectedConnection = vm.Connections[0];
            var stale = manager.Handler!;
            manager.Emit(Snapshot(Row("queued")));
            manager.Running = false;
            vm.OnServiceStatusChanged(false);
            Assert.Empty(vm.Connections);
            Assert.Null(vm.SelectedConnection);
            Assert.Equal(0, vm.ConnectionCount);
            manager.Current = Snapshot(Row("new-session"));
            manager.Running = true;
            vm.OnServiceStatusChanged(true);
            stale(manager, Snapshot(Row("old-session")));
            Pump();
            Assert.Equal("new-session", Assert.Single(vm.Connections).Id);
        }
    }

    [Fact]
    public Task CloseSelectionAndSearchKeepExistingSemantics() => OnUI(SearchScenario);

    private void SearchScenario()
    {
        var (vm, manager) = Create();
        using (vm)
        {
            manager.Emit(Snapshot(Row("one"), Row("two")));
            Pump();
            var selected = vm.Connections[0];
            vm.SelectedConnection = selected;
            vm.SearchText = "does-not-match";
            Pump();
            Assert.Empty(vm.Connections);
            Assert.False(selected.IsClosed);
            manager.Emit(Snapshot(Row("two")));
            Pump();
            Assert.True(selected.IsClosed);
            Assert.Same(selected, vm.SelectedConnection);
            vm.SearchText = "EXAMPLE.COM";
            Pump();
            Assert.Equal("two", Assert.Single(vm.Connections).Id);
            Assert.Equal(1, vm.ConnectionCount);
        }
    }

    [Fact]
    public Task ChangedMetadataAndTrafficUpdateWithoutReplacingSelectedRow() => OnUI(MetadataScenario);

    private void MetadataScenario()
    {
        var (vm, manager) = Create();
        using (vm)
        {
            manager.Emit(Snapshot(Row("one")));
            Pump();
            var item = vm.Connections[0];
            vm.SelectedConnection = item;
            var identical = Row("one");
            var text = item.StartedAt;
            var chain = item.Chain;
            var upload = item.Upload;
            manager.Emit(Snapshot(identical));
            Pump();
            Assert.Same(text, item.StartedAt);
            Assert.Same(chain, item.Chain);
            Assert.Same(upload, item.Upload);
            var changed = Row("one", 4096);
            changed.Network = "udp";
            changed.Inbound = "new-in";
            changed.InboundType = "tun";
            changed.Chains = ["new-proxy", "new-region"];
            changed.StartTime = changed.StartTime.AddHours(1);
            changed.Process = "updated-process";
            manager.Emit(Snapshot(changed));
            Pump();
            Assert.Same(item, Assert.Single(vm.Connections));
            Assert.Same(item, vm.SelectedConnection);
            Assert.Equal("UDP", item.NetworkDisplay);
            Assert.Equal("tun/new-in", item.Inbound);
            Assert.Equal("new-region / new-proxy", item.Chain);
            Assert.Equal("new-region", item.Route);
            Assert.Equal("updated-process", item.Process);
            Assert.Equal(FormatHelper.FormatBytes(4096), item.Upload);
            Assert.NotEqual(text, item.StartedAt);
        }
    }

    [Fact]
    public Task BackgroundPublisherDuringUiDrainEventuallyShowsLatestState() => OnUI(PublisherScenario);

    private void PublisherScenario()
    {
        var (vm, manager) = Create();
        using (vm)
        {
            var producer = Task.Run(() =>
            {
                for (var i = 0; i < 500; i++) manager.Emit(Snapshot(Row(i.ToString())));
            });
            while (!producer.IsCompleted) Pump();
            producer.GetAwaiter().GetResult();
            Pump();
            Assert.Equal("499", Assert.Single(vm.Connections).Id);
        }
    }

    [Fact]
    public Task DisposeRejectsQueuedCallbacksAndFutureReactivation() => OnUI(DisposeScenario);

    private void DisposeScenario()
    {
        var (vm, manager) = Create();
        var stale = manager.Handler!;
        manager.Emit(Snapshot(Row("queued")));
        vm.Dispose();
        stale(manager, Snapshot(Row("late")));
        vm.OnNavigatedTo();
        vm.SetWindowVisible(true);
        Pump();
        Assert.Null(manager.Handler);
        Assert.Empty(vm.Connections);
        Assert.Equal(0, vm.ConnectionCount);
    }

    [Fact]
    public Task CloseCommandsStillReachManagerAndFailureDoesNotRemoveRow() => OnUI(CloseScenario);

    private async Task CloseScenario()
    {
        var (vm, manager) = Create();
        using (vm)
        {
            manager.Emit(Snapshot(Row("one")));
            Pump();
            var item = vm.Connections[0];
            manager.FailClose = true;
            await vm.CloseConnectionCommand.ExecuteAsync(item);
            Assert.False(item.IsClosed);
            Assert.NotNull(vm.CloseOperationError);
            manager.FailClose = false;
            await vm.CloseConnectionCommand.ExecuteAsync(item);
            Assert.Equal("one", manager.ClosedId);
            Assert.True(item.IsClosed);
            await vm.CloseAllCommand.ExecuteAsync(null);
            Assert.True(manager.ClosedAll);
            manager.Emit(ConnectionsSnapshot.Empty);
            Pump();
            Assert.Empty(vm.Connections);
        }
    }

    [Fact]
    public Task DisposedPageIsNotRetainedByLocalizationOrQueuedWork() => OnUI(() =>
    {
        var reference = CreateDisposedPage();
        Pump();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(reference.IsAlive);
    });

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference CreateDisposedPage()
    {
        var (vm, manager) = Create();
        manager.Emit(Snapshot(Row("queued")));
        var reference = new WeakReference(vm);
        vm.Dispose();
        return reference;
    }

    [Fact]
    public Task RepeatedActivationDoesNotDuplicateSubscriptions() => OnUI(() =>
    {
        var (vm, manager) = Create();
        using (vm)
        {
            for (var i = 0; i < 5; i++)
            {
                vm.OnNavigatedTo();
                vm.SetWindowVisible(true);
                vm.OnServiceStatusChanged(true);
            }
            Assert.Single(manager.Handler!.GetInvocationList());
            manager.Emit(Snapshot(Row("one")));
            Pump();
            Assert.Single(vm.Connections);
        }
    });

    [Fact]
    public Task FormattingTracksCultureChangesEvenWhenCountersAreUnchanged() => OnUI(() =>
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            var item = new ConnectionItemViewModel();
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            item.Update(Row("one", 1536));
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            item.Update(Row("one", 1536));
            Assert.Equal(FormatHelper.FormatBytes(1536), item.Upload);
            Assert.Equal(Row("one").StartTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture), item.StartedAt);
        }
        finally { CultureInfo.CurrentCulture = saved; }
    });

    [Fact]
    public Task MissingFieldsKeepFallbacksAndAreSearchable() => OnUI(FallbackScenario);

    private void FallbackScenario()
    {
        var (vm, manager) = Create();
        using (vm)
        {
            manager.Emit(Snapshot(new ConnectionInfo { Id = "fallback", Inbound = "in", Ip = "ip", Domain = "domain" }));
            Pump();
            var item = Assert.Single(vm.Connections);
            Assert.Equal("in", item.Process);
            Assert.Equal("ip", item.Source);
            Assert.Equal("domain", item.Destination);
            Assert.Equal("-", item.Outbound);
            foreach (var term in new[] { "in", "IP", "domain", "-" })
            {
                vm.SearchText = term;
                Pump();
                Assert.Single(vm.Connections);
            }
        }
    }
}

public class ManagerProxy : DispatchProxy
{
    public bool Running = true;
    public ConnectionsSnapshot Current = ConnectionsSnapshot.Empty;
    public EventHandler<ConnectionsSnapshot>? Handler;
    public string? ClosedId;
    public bool ClosedAll;
    public bool FailClose;
    public void Emit(ConnectionsSnapshot snapshot)
    {
        Current = snapshot;
        Handler?.Invoke(this, snapshot);
    }
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        switch (method!.Name)
        {
            case "get_IsRunning": return Running;
            case "get_CurrentConnections": return Current;
            case "add_ConnectionsUpdated": Handler += (EventHandler<ConnectionsSnapshot>)args![0]!; return null;
            case "remove_ConnectionsUpdated": Handler -= (EventHandler<ConnectionsSnapshot>)args![0]!; return null;
            case "CloseConnectionAsync":
                if (FailClose) return Task.FromException(new InvalidOperationException("test failure"));
                ClosedId = (string)args![0]!; return Task.CompletedTask;
            case "CloseAllConnectionsAsync": ClosedAll = true; return Task.CompletedTask;
            default: throw new NotSupportedException(method.Name);
        }
    }
}
