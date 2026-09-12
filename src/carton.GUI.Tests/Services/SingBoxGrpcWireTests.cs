using carton.Core.Services;
using carton.Core.Services.SingBoxApi;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace carton.GUI.Tests.Services;

/// <summary>
/// Wire-level integration tests: the real <see cref="SingBoxGrpcApiClient"/> against an
/// in-process mock of the sing-box daemon gRPC API (see <see cref="MockSingBoxServer"/>).
/// These verify the full chain - protobuf serialization, h2c transport, Bearer auth,
/// streaming semantics and the interval unit contract - without a real kernel.
/// </summary>
[Collection(SingBoxApiGlobalStateCollection.Name)]
public sealed class SingBoxGrpcWireTests
{
    private sealed class TestHarness : IAsyncDisposable
    {
        public MockSingBoxServer Server { get; } = new();
        public WebApplication GrpcServer { get; }
        public int Port { get; }
        public SingBoxGrpcApiClient Client { get; }

        public TestHarness()
        {
            (Port, GrpcServer) = MockSingBoxServer.Start(Server);
            HttpClientFactory.UpdateLocalApi("127.0.0.1", Port, MockSingBoxServer.Secret);
            HttpClientFactory.UpdateLocalNativeApi("127.0.0.1", Port, MockSingBoxServer.Secret);
            Client = new SingBoxGrpcApiClient();
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await GrpcServer.StopAsync();
            SingBoxApiClientFactory.Reset();
        }
    }

    private static async Task WithHarnessAsync(Func<TestHarness, Task> test)
    {
        await using var harness = new TestHarness();
        await test(harness);
    }

    [Fact]
    public async Task GetServerVersion_ReturnsVersionAndApiVersion_OverTheWire()
    {
        await WithHarnessAsync(async harness =>
        {
            var info = await harness.Client.GetServerVersionAsync();

            Assert.NotNull(info);
            Assert.Equal(MockSingBoxServer.ServerVersion, info!.Value.Version);
            Assert.Equal(MockSingBoxServer.ServerApiVersion, info.Value.ApiVersion);
            Assert.Equal(MockSingBoxServer.ServerApiVersion, harness.Client.ApiVersion);
        });
    }

    [Fact]
    public async Task IsReachable_True_WhenApiResponds()
    {
        await WithHarnessAsync(async harness =>
        {
            Assert.True(await harness.Client.IsReachableAsync());
        });
    }

    [Fact]
    public async Task GetModeConfig_ReturnsModesOverTheWire()
    {
        await WithHarnessAsync(async harness =>
        {
            var snapshot = await harness.Client.GetModeConfigAsync();

            Assert.NotNull(snapshot);
            Assert.Equal("rule", snapshot!.Mode);
            Assert.Contains("global", snapshot.ModeList!);
        });
    }

    [Fact]
    public async Task GetOutboundGroups_ReturnsInitialSnapshot()
    {
        await WithHarnessAsync(async harness =>
        {
            harness.Server.AddGroup("proxy", "Selector", "auto", ("auto", "URLTest", 40), ("direct", "Direct", 5));

            var groups = await harness.Client.GetOutboundGroupsAsync();

            var group = Assert.Single(groups);
            Assert.Equal("proxy", group.Tag);
            Assert.Equal("auto", group.Selected);
            Assert.Equal(2, group.Items.Count);
        });
    }

    [Fact]
    public async Task SelectOutbound_SendsRequestWithSecret()
    {
        await WithHarnessAsync(async harness =>
        {
            harness.Server.AddGroup("proxy", "Selector", "auto", ("auto", "URLTest", 40), ("direct", "Direct", 5));

            await harness.Client.SelectOutboundAsync("proxy", "direct");

            var call = Assert.Single(harness.Server.SelectCalls);
            Assert.Equal(("proxy", "direct"), call);
        });
    }

    [Fact]
    public async Task SetGroupExpand_SendsRequestOverTheWire()
    {
        await WithHarnessAsync(async harness =>
        {
            await harness.Client.SetGroupExpandAsync("proxy", true);

            var call = Assert.Single(harness.Server.GroupExpandCalls);
            Assert.Equal(("proxy", true), call);
        });
    }

    [Fact]
    public async Task SubscribeStatus_SendsIntervalInNanoseconds()
    {
        await WithHarnessAsync(async harness =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var received = 0;
            await foreach (var _ in harness.Client.SubscribeAggregatedStatusAsync(cts.Token))
            {
                received++;
                if (received >= 2)
                {
                    break;
                }
            }

            var interval = Assert.Single(harness.Server.StatusIntervalsNs);
            // The wire contract: interval is nanoseconds (Go time.Duration). One second
            // must be sent as 1_000_000_000, not 1000 (which would be one microsecond).
            Assert.Equal(1_000_000_000L, interval);
        });
    }

    [Fact]
    public async Task SubscribeConnections_SendsIntervalInNanoseconds_AndFiltersClosedInSnapshot()
    {
        await WithHarnessAsync(async harness =>
        {
            harness.Server.AddConnection("a", uplinkTotal: 100);
            harness.Server.CloseConnectionLocally("a", uplinkTotal: 100);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var connections = await harness.Client.GetConnectionsAsync();

            var interval = Assert.Single(harness.Server.ConnectionIntervalsNs);
            Assert.Equal(1_000_000_000L, interval);

            // The initial snapshot reports closed connections as NEW events with
            // ClosedAt set - they must be filtered out (legacy REST semantics).
            Assert.Empty(connections);
        });
    }

    [Fact]
    public async Task GetConnections_StreamsActiveConnections()
    {
        await WithHarnessAsync(async harness =>
        {
            harness.Server.AddConnection("a", uplinkTotal: 100, downlinkTotal: 200);
            harness.Server.AddConnection("b", uplinkTotal: 300, downlinkTotal: 400);

            var connections = await harness.Client.GetConnectionsAsync();

            Assert.Equal(2, connections.Count);
            Assert.Contains(connections, c => c.Id == "a" && c.Upload == 100);
            Assert.Contains(connections, c => c.Id == "b" && c.Upload == 300);
        });
    }

    [Fact]
    public async Task CloseConnection_RemovesItFromTheKernel()
    {
        await WithHarnessAsync(async harness =>
        {
            harness.Server.AddConnection("a");
            harness.Server.AddConnection("b");

            await harness.Client.CloseConnectionAsync("a");
            var remaining = await harness.Client.GetConnectionsAsync();

            var connection = Assert.Single(remaining);
            Assert.Equal("b", connection.Id);
        });
    }

    [Fact]
    public async Task RunOutboundDelayTests_WaitsForFreshResultsOverTheWire()
    {
        await WithHarnessAsync(async harness =>
        {
            harness.Server.AddGroup("proxy", "Selector", "auto", ("node-a", "VMess", 999));

            // The daemon URLTest RPC handles leaf tags natively (single-node test);
            // the request must pass the ORIGINAL tag through, matching the official
            // dashboard's urlTest(item.tag).
            var delays = await harness.Client.RunOutboundDelayTestsAsync(new[] { "node-a" }, timeoutMs: 5000);

            Assert.True(delays.TryGetValue("node-a", out var delay));
            Assert.NotEqual(999, delay);
            Assert.True(delay > 0);
            Assert.Contains("node-a", harness.Server.UrlTestCalls);
        });
    }

    [Fact]
    public async Task RunGroupDelayTest_WaitsForFreshResultsOverTheWire()
    {
        await WithHarnessAsync(async harness =>
        {
            harness.Server.AddGroup("proxy", "Selector", "auto",
                ("node-a", "VMess", 999), ("node-b", "Trojan", 999));

            var delays = await harness.Client.RunGroupDelayTestAsync("proxy", timeoutMs: 5000);

            Assert.Equal(2, delays.Count);
            Assert.All(delays.Values, delay => Assert.True(delay > 0 && delay != 999));
        });
    }

    [Fact]
    public async Task GetDeprecatedWarnings_MapsWarningModelOverTheWire()
    {
        await WithHarnessAsync(async harness =>
        {
            var client = (ISingBoxApiClient)harness.Client;
            var warnings = await client.GetDeprecatedWarningsAsync();
            Assert.NotNull(warnings);

            var warning = Assert.Single(warnings!.Warnings);
            Assert.True(warning.Impending);
            Assert.Equal("1.16.0", warning.ScheduledVersion);
        });
    }

    [Fact]
    public async Task SubscribeLogs_ReplaysHistoryWithResetFlag()
    {
        await WithHarnessAsync(async harness =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var resetSeen = false;
            harness.Client.LogsReset += (_, _) => resetSeen = true;

            var entries = new List<string>();
            await foreach (var entry in harness.Client.SubscribeLogsAsync("info", cts.Token))
            {
                entries.Add(entry.Message);
                if (entries.Count >= 1)
                {
                    break;
                }
            }

            Assert.True(resetSeen, "the first SubscribeLog message must carry reset=true");
        });
    }

    [Fact]
    public async Task SubscribeClashMode_PushesInitialModeOverTheWire()
    {
        await WithHarnessAsync(async harness =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var modes = new List<string>();
            await foreach (var mode in harness.Client.SubscribeModeStreamAsync(cts.Token))
            {
                modes.Add(mode.Mode);
                break;
            }

            // The first message carries the current mode; later pushes reflect changes
            // (including SetClashMode from other clients).
            var first = Assert.Single(modes);
            Assert.Equal("rule", first);
        });
    }

    [Fact]
    public async Task SetMode_UpdatesThenPushesNewMode()
    {
        await WithHarnessAsync(async harness =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var received = new List<string>();
            var streamTask = Task.Run(async () =>
            {
                await foreach (var mode in harness.Client.SubscribeModeStreamAsync(cts.Token))
                {
                    lock (received)
                    {
                        received.Add(mode.Mode);
                    }

                    if (received.Count >= 2)
                    {
                        break;
                    }
                }
            });

            await Task.Delay(200);
            Assert.True(await harness.Client.SetModeAsync("global"));
            await streamTask;

            lock (received)
            {
                Assert.Contains("global", received);
            }
        });
    }

    [Fact]
    public async Task UnauthenticatedSecret_IsRejectedByServer()
    {
        await WithHarnessAsync(async harness =>
        {
            // Point the client at the server with a WRONG secret.
            HttpClientFactory.UpdateLocalNativeApi("127.0.0.1", harness.Port, "wrong-secret");
            var client = new SingBoxGrpcApiClient();
            try
            {
                var info = await client.GetServerVersionAsync();

                // Server rejects the RPC; version stays unknown.
                Assert.Null(info);
                Assert.Equal(0, client.ApiVersion);

                // The RPC must have REACHED the server (not failed to connect) and been
                // rejected there: a connection-level failure would also produce null,
                // so assert the server actually saw - and rejected - the request.
                var requests = harness.Server.VersionRequests;
                Assert.NotEmpty(requests);
                Assert.All(requests, r => Assert.Equal(("unauthenticated", 0), r));
            }
            finally
            {
                client.Dispose();
            }
        });
    }
}
