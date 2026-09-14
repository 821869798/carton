using System.Net;
using System.Net.Sockets;
using carton.Core.Services;
using carton.Core.Services.SingBoxApi;
using Daemon;
using Xunit;

namespace carton.GUI.Tests.Services;

/// <summary>
/// Unit tests for the pure helpers of the gRPC API client. These encode the
/// sing-box daemon wire contract that the implementation must respect:
/// subscription intervals are nanoseconds (Go time.Duration), and URL test
/// results are only fresh when the kernel pushed an updated history entry.
/// </summary>
[Collection(SingBoxApiGlobalStateCollection.Name)]
public sealed class SingBoxGrpcApiClientTests
{
    [Fact]
    public void IsFreshUrlTestResult_TrueWhenTimestampAdvanced()
    {
        Assert.True(SingBoxGrpcApiClient.IsFreshUrlTestResult(
            baselineTime: 1000, baselineDelay: 120, itemTime: 1001, itemDelay: 120));
    }

    [Fact]
    public void IsFreshUrlTestResult_TrueWhenDelayChangedWithoutTimestamp()
    {
        // Some snapshots may not refresh UrlTestTime before the delay is stored;
        // a changed delay still proves the value is fresh.
        Assert.True(SingBoxGrpcApiClient.IsFreshUrlTestResult(
            baselineTime: 1000, baselineDelay: 120, itemTime: 1000, itemDelay: 95));
    }

    [Fact]
    public void IsFreshUrlTestResult_FalseWhenNothingChanged()
    {
        // Previously cached delays (persisted in cache.db) must never be mistaken
        // for the results of a freshly triggered URL test.
        Assert.False(SingBoxGrpcApiClient.IsFreshUrlTestResult(
            baselineTime: 1000, baselineDelay: 120, itemTime: 1000, itemDelay: 120));
    }

    [Fact]
    public void IsFreshUrlTestResult_TrueWhenHistoryClearedByFailedTest()
    {
        // A failed test removes the history entry: delay drops to 0 while the
        // timestamp may stay behind. That still counts as a fresh result.
        Assert.True(SingBoxGrpcApiClient.IsFreshUrlTestResult(
            baselineTime: 1000, baselineDelay: 120, itemTime: 1000, itemDelay: 0));
    }

    [Fact]
    public void MergeFreshDelayResults_UnchangedSnapshotPushDoesNotCompleteTheWait()
    {
        // Regression (pi-review finding): kernel groups snapshots are FULL states, so
        // an unchanged repeat push (same UrlTestTime, same delay) must NOT count as a
        // refreshed result - the wait must keep running until values actually change.
        var baseline = new Dictionary<string, long>
        {
            ["a"] = 1000,
            ["b"] = 1000
        };
        var current = new Dictionary<string, int> { ["a"] = 999, ["b"] = 999 };
        var unchangedSnapshot = new List<KeyValuePair<string, (long, int)>>
        {
            new("a", (1000, 999)),
            new("b", (1000, 999))
        };

        var remaining = SingBoxGrpcApiClient.MergeFreshDelayResults(baseline, current, unchangedSnapshot);

        Assert.Equal(2, remaining.Count);
        Assert.Contains("a", remaining);
        Assert.Contains("b", remaining);
    }

    [Fact]
    public void MergeFreshDelayResults_OnlyFreshTagsLeaveRemaining()
    {
        var baseline = new Dictionary<string, long>
        {
            ["a"] = 1000,
            ["b"] = 1000,
            ["c"] = 1000
        };
        var current = new Dictionary<string, int> { ["a"] = 999, ["b"] = 999, ["c"] = 999 };
        var snapshot = new List<KeyValuePair<string, (long, int)>>
        {
            new("a", (2000, 55)),   // fresh: timestamp advanced
            new("b", (1000, 999)),  // unchanged: still stale
            new("c", (1000, 0))     // fresh: failed test cleared the history
        };

        var remaining = SingBoxGrpcApiClient.MergeFreshDelayResults(baseline, current, snapshot);

        var stale = Assert.Single(remaining);
        Assert.Equal("b", stale);
    }

    [Fact]
    public void MergeFreshDelayResults_IgnoresTagsOutsideTheBaseline()
    {
        // Regression (pi-review finding): a group delay test must only track its own
        // group's items; outbounds from unrelated groups never affect completion.
        var baseline = new Dictionary<string, long> { ["a"] = 1000 };
        var current = new Dictionary<string, int> { ["a"] = 999 };
        var snapshot = new List<KeyValuePair<string, (long, int)>>
        {
            new("unrelated-1", (3000, 77)),
            new("unrelated-2", (3000, 88))
        };

        var remaining = SingBoxGrpcApiClient.MergeFreshDelayResults(baseline, current, snapshot);

        Assert.Single(remaining);
        Assert.Contains("a", remaining);
    }

    [Fact]
    public void MergeFreshDelayResults_EmptySnapshotKeepsEverythingRemaining()
    {
        var baseline = new Dictionary<string, long> { ["a"] = 1000, ["b"] = 1000 };

        var remaining = SingBoxGrpcApiClient.MergeFreshDelayResults(baseline, new Dictionary<string, int>(), Array.Empty<KeyValuePair<string, (long, int)>>());

        Assert.Equal(2, remaining.Count);
    }

    [Theory]
    [InlineData("warn", LogLevel.Warn)]
    [InlineData("warning", LogLevel.Warn)]
    [InlineData("debug", LogLevel.Debug)]
    [InlineData("trace", LogLevel.Trace)]
    [InlineData("invalid", LogLevel.Warn)]
    public void ParseLogThreshold_NormalizesRuntimeLevel(string level, LogLevel expected)
    {
        Assert.Equal(expected, SingBoxGrpcApiClient.ParseLogThreshold(level));
    }

    [Fact]
    public void CreateSocketsHttpHandler_DisablesProxyAndKeepsASingleHttp2Connection()
    {
        using var handler = SingBoxGrpcApiClient.CreateSocketsHttpHandler();
        Assert.False(handler.UseProxy);
        // Local sing-box is one HTTP/2 server. Extra connections only duplicate windows
        // and ping timers; multiplexing already happens inside the single connection.
        Assert.False(handler.EnableMultipleHttp2Connections);
    }

    [Fact]
    public async Task IsReachableAsync_WhenUnreachable_LogsTargetAddress()
    {
        // Hold a loopback port for the whole probe so no other test can grab it, and
        // drop every connection on accept: the client fails before any HTTP/2 frame
        // arrives instead of waiting for the 2s deadline.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var dropLoop = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    (await listener.AcceptSocketAsync()).Dispose();
                }
            }
            catch (Exception)
            {
                // Listener stopped: the test is over.
            }
        });

        string? loggedMessage = null;
        HttpClientFactory.UpdateLocalNativeApi("127.0.0.1", port, null);
        try
        {
            using var client = new SingBoxGrpcApiClient(msg => loggedMessage = msg);
            var reachable = await client.IsReachableAsync();
            Assert.False(reachable);
            Assert.NotNull(loggedMessage);
            Assert.Contains($"http://127.0.0.1:{port}", loggedMessage);
        }
        finally
        {
            HttpClientFactory.ClearLocalNativeApi();
            listener.Stop();
            await dropLoop;
        }
    }
}
