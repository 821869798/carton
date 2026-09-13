using System.Runtime.CompilerServices;
using carton.Core.Models;
using Daemon;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;

namespace carton.Core.Services.SingBoxApi;

internal sealed class SingBoxGrpcApiClient : ISingBoxApiClient, IDisposable
{
    /// <summary>
    /// Interval unit for <c>SubscribeStatusRequest.Interval</c> / <c>SubscribeConnectionsRequest.Interval</c>
    /// is nanoseconds (Go <c>time.Duration</c>) on the sing-box side; sing-box defaults to one second when &lt;= 0.
    /// Matches the official sing-box dashboard's <c>SUBSCRIPTION_INTERVAL = 1_000_000_000n</c>.
    /// </summary>
    internal const long SubscribeIntervalNanoseconds = 1_000_000_000;

    /// <summary>
    /// Returns true when the item's URL test result is fresh, i.e. its UrlTestTime
    /// (unix seconds, updated on success) is newer than the recorded baseline or its
    /// delay changed (a failed test clears the cached history).
    /// </summary>
    internal static bool IsFreshUrlTestResult(long baselineTime, long baselineDelay, long itemTime, int itemDelay)
        => itemTime > baselineTime || itemDelay != baselineDelay;

    /// <summary>
    /// Merges one kernel groups snapshot into an in-flight delay test. A tag leaves
    /// the remaining set the first time it observes a FRESH result (UrlTestTime
    /// advanced / delay changed) and never re-enters on later snapshots: completion
    /// is monotonic because the baseline values are captured once, before any result
    /// mutation - later full-snapshot pushes repeat either the fresh value (already
    /// done) or the failed state (delay cleared to 0, also already done).
    /// Returns the baseline tags still awaiting their first fresh result.
    /// </summary>
    internal static IReadOnlySet<string> MergeFreshDelayResults(
        IReadOnlyDictionary<string, long> baselineUrlTestTimes,
        IReadOnlyDictionary<string, int> baselineDelays,
        IEnumerable<KeyValuePair<string, (long UrlTestTime, int UrlTestDelay)>> snapshotItems)
    {
        var remaining = new HashSet<string>(baselineUrlTestTimes.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var (tag, (itemTime, itemDelay)) in snapshotItems)
        {
            if (remaining.Remove(tag) &&
                baselineUrlTestTimes.TryGetValue(tag, out var baselineTime))
            {
                // Remove first (monotonic), then decide: if this is NOT fresh, put the tag
                // back - it is still awaiting its first fresh result.
                if (!IsFreshUrlTestResult(baselineTime, baselineDelays.GetValueOrDefault(tag), itemTime, itemDelay))
                {
                    remaining.Add(tag);
                }
            }
        }

        return remaining;
    }


    private readonly Action<string>? _log;
    private readonly object _channelLock = new();
    private GrpcChannel? _channel;
    private StartedService.StartedServiceClient? _client;
    private string _cachedAddress = string.Empty;

    public SingBoxGrpcApiClient(Action<string>? log = null)
    {
        _log = log;
    }

    /// <summary>
    /// Everything one RPC needs. Address is the endpoint the returned client is bound
    /// to, captured under the channel lock so diagnostics never report a stale value.
    /// </summary>
    private readonly record struct ApiCallContext(
        StartedService.StartedServiceClient Client,
        Metadata Headers,
        string Address)
    {
        // Most call sites only care about the client and headers.
        public void Deconstruct(out StartedService.StartedServiceClient client, out Metadata headers)
        {
            client = Client;
            headers = Headers;
        }
    }

    private ApiCallContext GetClient()
    {
        var address = HttpClientFactory.LocalNativeApiPort > 0
            ? HttpClientFactory.LocalNativeApiAddress
            : HttpClientFactory.LocalApiAddress;

        if (string.IsNullOrWhiteSpace(address))
        {
            var port = HttpClientFactory.LocalNativeApiPort > 0
                ? HttpClientFactory.LocalNativeApiPort
                : (HttpClientFactory.LocalApiPort > 0 ? HttpClientFactory.LocalApiPort : 9090);
            address = $"http://127.0.0.1:{port}";
        }

        var secret = HttpClientFactory.LocalNativeApiPort > 0
            ? HttpClientFactory.LocalNativeApiSecret
            : HttpClientFactory.LocalApiSecret;

        lock (_channelLock)
        {
            if (_channel == null || _cachedAddress != address)
            {
                _channel?.Dispose();
                _channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
                {
                    HttpHandler = CreateSocketsHttpHandler(),
                    DisposeHttpClient = true
                });
                _client = new StartedService.StartedServiceClient(_channel);
                _cachedAddress = address;
            }

            var headers = new Metadata();
            if (!string.IsNullOrWhiteSpace(secret))
            {
                headers.Add("authorization", $"Bearer {secret}");
            }

            return new ApiCallContext(_client!, headers, address);
        }
    }

    internal static SocketsHttpHandler CreateSocketsHttpHandler() => new()
    {
        UseProxy = false,
        EnableMultipleHttp2Connections = true,
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
        KeepAlivePingDelay = TimeSpan.FromSeconds(15),
        KeepAlivePingTimeout = TimeSpan.FromSeconds(5),
    };

    /// <summary>
    /// Returns the (version, apiVersion) pair reported by the running kernel, or null
    /// when the API is unreachable. apiVersion gates optional RPCs the same way the
    /// official sing-box dashboard does (see its capabilities.ts: taildrop >= 4,
    /// OpenVPN/OpenConnect >= 3).
    /// </summary>
    public async Task<(string Version, int ApiVersion)?> GetServerVersionAsync()
    {
        try
        {
            var (client, headers) = GetClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var version = await client.GetVersionAsync(
                new Empty(),
                headers,
                deadline: DateTime.UtcNow.AddSeconds(2),
                cancellationToken: cts.Token);
            if (version == null)
            {
                return null;
            }

            _apiVersion = version.ApiVersion;
            return (version.Version_, version.ApiVersion);
        }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.Unauthenticated or StatusCode.PermissionDenied)
        {
            // Reachable but the secret was rejected: the API version is unknown.
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The kernel instance's CURRENT log level via the GetDefaultLogLevel RPC, or
    /// null when unavailable. The daemon answers from the LIVE instance factory
    /// (instance.instance.LogFactory().Level()), so this reflects reloads and any
    /// level changes the manager did not observe - unlike a config-file snapshot.
    /// </summary>
    public async Task<LogLevel?> GetRuntimeLogLevelAsync()
    {
        try
        {
            var (client, headers) = GetClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var result = await client.GetDefaultLogLevelAsync(
                new Empty(),
                headers,
                deadline: DateTime.UtcNow.AddSeconds(2),
                cancellationToken: cts.Token);
            return result?.Level;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Last known daemon apiVersion (0 when unknown); used for capability gating.</summary>
    public int ApiVersion => Volatile.Read(ref _apiVersion);

    private int _apiVersion;

    public async Task<bool> IsReachableAsync()
    {
        var target = string.Empty;
        try
        {
            var (client, headers, address) = GetClient();
            target = address;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var version = await client.GetVersionAsync(
                new Empty(),
                headers,
                deadline: DateTime.UtcNow.AddSeconds(2),
                cancellationToken: cts.Token);
            return version != null;
        }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.Unauthenticated or StatusCode.PermissionDenied)
        {
            // The sing-box gRPC API is reachable and responding
            return true;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[DEBUG] gRPC IsReachable check failed ({target}): {ex.Message}");
            return false;
        }
    }

    public async Task<ApiModeConfigSnapshot?> GetModeConfigAsync()
    {
        try
        {
            var (client, headers) = GetClient();
            var status = await client.GetClashModeStatusAsync(new Empty(), headers);
            return new ApiModeConfigSnapshot
            {
                Mode = status.CurrentMode,
                ModeList = status.ModeList.ToList()
            };
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[WARN] Failed to fetch mode status via gRPC: {ex.Message}");
            return null;
        }
    }

    public async Task<bool> SetModeAsync(string mode)
    {
        try
        {
            var (client, headers) = GetClient();
            await client.SetClashModeAsync(new Daemon.ClashMode { Mode = mode }, headers);
            return true;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[WARN] Failed to set mode via gRPC: {ex.Message}");
            return false;
        }
    }

    public async Task<List<OutboundGroup>> GetOutboundGroupsAsync()
    {
        try
        {
            var (client, headers) = GetClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var call = client.SubscribeGroups(new Empty(), headers, cancellationToken: cts.Token);

            if (await call.ResponseStream.MoveNext(cts.Token))
            {
                var groupsMsg = call.ResponseStream.Current;
                var groups = new List<OutboundGroup>(groupsMsg.Group.Count);

                foreach (var g in groupsMsg.Group)
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

                return groups;
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[WARN] Failed to get outbound groups via gRPC: {ex.Message}");
        }

        return new List<OutboundGroup>();
    }

    public async Task SelectOutboundAsync(string groupTag, string outboundTag)
    {
        var (client, headers) = GetClient();
        await client.SelectOutboundAsync(new SelectOutboundRequest
        {
            GroupTag = groupTag,
            OutboundTag = outboundTag
        }, headers);
    }

    public async Task URLTestAsync(string outboundTag)
    {
        var (client, headers) = GetClient();
        await client.URLTestAsync(new URLTestRequest { OutboundTag = outboundTag }, headers);
    }

    public async IAsyncEnumerable<Daemon.ClashMode> SubscribeModeStreamAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var (client, headers) = GetClient();
        using var call = client.SubscribeClashMode(new Empty(), headers, cancellationToken: cancellationToken);

        while (!cancellationToken.IsCancellationRequested && await call.ResponseStream.MoveNext(cancellationToken))
        {
            yield return call.ResponseStream.Current;
        }
    }

    public async Task SetGroupExpandAsync(string groupTag, bool isExpand)
    {
        try
        {
            var (client, headers) = GetClient();
            // Persisted by the kernel into cache.db so the expansion state survives
            // restarts and syncs to other clients (official sing-box dashboard behavior).
            await client.SetGroupExpandAsync(new SetGroupExpandRequest
            {
                GroupTag = groupTag,
                IsExpand = isExpand
            }, headers);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[WARN] Failed to persist group expand state: {ex.Message}");
        }
    }

    public async Task<Dictionary<string, int>> RunGroupDelayTestAsync(string groupTag, string? testUrl = null, int timeoutMs = 5000)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(groupTag))
        {
            return result;
        }

        try
        {
            var (client, headers) = GetClient();
            // Subscribe first so the baseline snapshot is captured before the URL test is
            // triggered. Every URL test history update is pushed as a fresh Groups snapshot
            // (urltest history update hook), so we can wait for results with fresh timestamps.
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(Math.Max(timeoutMs, 1000)));
            using var call = client.SubscribeGroups(new Empty(), headers, cancellationToken: cts.Token);

            // Baseline: record the last url test time of every item in the target group so
            // previously cached delays (persisted in cache.db) are never mistaken for fresh results.
            var baseline = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var baselineDelays = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Group? targetGroup = null;
            while (targetGroup == null && await call.ResponseStream.MoveNext(cts.Token))
            {
                var snapshot = call.ResponseStream.Current;
                targetGroup = snapshot.Group.FirstOrDefault(g => string.Equals(g.Tag, groupTag, StringComparison.OrdinalIgnoreCase));
                if (targetGroup != null)
                {
                    foreach (var item in targetGroup.Items)
                    {
                        baseline[item.Tag] = item.UrlTestTime;
                        baselineDelays[item.Tag] = item.UrlTestDelay;
                        result[item.Tag] = item.UrlTestDelay;
                    }
                }
            }

            if (targetGroup == null)
            {
                return result;
            }

            // Fire-and-forget URL test: the kernel runs the tests asynchronously and pushes
            // updated Groups snapshots through the subscription as each node completes.
            await client.URLTestAsync(new URLTestRequest { OutboundTag = groupTag }, headers);

            // Wait until every item has a fresh result (UrlTestTime newer than baseline),
            // a failed refresh (delay cleared to 0) or the timeout budget is exhausted.
            while (await call.ResponseStream.MoveNext(cts.Token))
            {
                var groups = call.ResponseStream.Current;
                var group = groups.Group.FirstOrDefault(g => string.Equals(g.Tag, groupTag, StringComparison.OrdinalIgnoreCase));
                if (group == null)
                {
                    continue;
                }

                var groupItems = group.Items
                    .Select(item => new KeyValuePair<string, (long, int)>(item.Tag, (item.UrlTestTime, item.UrlTestDelay)))
                    .ToList();
                // The baseline delays are the comparison anchor (NOT the mutating result
                // dictionary): completion stays monotonic across snapshot repeats.
                var remaining = MergeFreshDelayResults(baseline, baselineDelays, groupItems);
                foreach (var item in group.Items)
                {
                    if (!baseline.TryGetValue(item.Tag, out var baselineTime) ||
                        IsFreshUrlTestResult(baselineTime, baselineDelays.GetValueOrDefault(item.Tag), item.UrlTestTime, item.UrlTestDelay))
                    {
                        // UrlTestTime (unix seconds) updates on success; a failed test clears
                        // the cached delay (history removed) which also counts as refreshed.
                        result[item.Tag] = item.UrlTestDelay;
                    }
                }

                if (remaining.Count == 0)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Timeout: return whatever has been collected so far.
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[WARN] RunGroupDelayTest error: {ex.Message}");
        }

        return result;
    }

    public async Task<Dictionary<string, int>> RunOutboundDelayTestsAsync(IEnumerable<string> outboundTags, string? testUrl = null, int timeoutMs = 5000)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var tags = outboundTags.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (tags.Count == 0)
        {
            return result;
        }

        try
        {
            var (client, headers) = GetClient();
            // Subscribe first so the baseline snapshot is captured before the URL tests are
            // triggered; the kernel pushes fresh Groups snapshots as each test completes.
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(Math.Max(timeoutMs, 1000)));
            using var call = client.SubscribeGroups(new Empty(), headers, cancellationToken: cts.Token);

            // Baseline: last url test time per requested tag (also collected from nested groups).
            var baseline = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var stale = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            while (await call.ResponseStream.MoveNext(cts.Token))
            {
                var snapshot = call.ResponseStream.Current;
                foreach (var g in snapshot.Group)
                {
                    foreach (var item in g.Items)
                    {
                        if (tags.Contains(item.Tag, StringComparer.OrdinalIgnoreCase))
                        {
                            baseline[item.Tag] = item.UrlTestTime;
                            stale[item.Tag] = item.UrlTestDelay;
                        }
                    }
                }

                // Keep the requested tags as the trigger: the daemon URLTest RPC handles
                // all tag kinds natively (group -> batch, leaf -> single async test),
                // matching the official dashboard's urlTest(item.tag) usage.

                // The first message is the kernel's FULL current state: whatever it
                // does not contain does not exist (unknown tags would otherwise burn
                // the whole budget below and return an empty result).
                break;
            }

            // Trigger the tests concurrently: URLTest is fire-and-forget on the kernel
            // side (it spawns the test and returns), so awaiting them one-by-one would
            // just serialize N needless RPC round-trips. Failures fall through to the
            // stale cached values below.
            var triggerTasks = new List<Task>(tags.Count);
            foreach (var tag in tags)
            {
                triggerTasks.Add(client.URLTestAsync(new URLTestRequest { OutboundTag = tag }, headers).ResponseAsync);
            }

            try
            {
                await Task.WhenAll(triggerTasks);
            }
            catch
            {
                // Individual failures are fine; the subscription still yields fresh values.
            }

            // Wait until every requested tag has a fresh result or the budget is exhausted.
            foreach (var (tag, value) in stale)
            {
                result[tag] = value;
            }

            while (await call.ResponseStream.MoveNext(cts.Token))
            {
                var snapshot = call.ResponseStream.Current;
                var snapshotItems = snapshot.Group
                    .SelectMany(g => g.Items)
                    .Select(item => new KeyValuePair<string, (long, int)>(item.Tag, (item.UrlTestTime, item.UrlTestDelay)))
                    .ToList();

                // The stale values are the comparison anchor (baseline delays captured
                // before the tests), not the mutating result dictionary: completion is
                // monotonic across unchanged snapshot repeats.
                var remaining = MergeFreshDelayResults(baseline, stale, snapshotItems);
                foreach (var g in snapshot.Group)
                {
                    foreach (var item in g.Items)
                    {
                        if (baseline.TryGetValue(item.Tag, out var baselineTime) &&
                            IsFreshUrlTestResult(baselineTime, stale.GetValueOrDefault(item.Tag), item.UrlTestTime, item.UrlTestDelay))
                        {
                            result[item.Tag] = item.UrlTestDelay;
                        }
                    }
                }

                if (remaining.Count == 0)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Timeout: return whatever has been collected so far.
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[WARN] RunOutboundDelayTests error: {ex.Message}");
        }

        return result;
    }

    public async Task<List<ConnectionInfo>> GetConnectionsAsync()
    {
        var connections = new List<ConnectionInfo>();
        try
        {
            var (client, headers) = GetClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var call = client.SubscribeConnections(new SubscribeConnectionsRequest { Interval = SubscribeIntervalNanoseconds }, headers, cancellationToken: cts.Token);

            if (await call.ResponseStream.MoveNext(cts.Token))
            {
                var events = call.ResponseStream.Current;
                foreach (var evt in events.Events)
                {
                    // The initial subscription snapshot reports every known connection as a
                    // NEW event, including recently closed ones (sing-box pushes closed
                    // connections with ClosedAt set). Skip them so only active connections are
                    // returned - matching the semantics the connections page has always relied on.
                    if (evt.Connection is not { } c || c.ClosedAt > 0)
                    {
                        continue;
                    }

                    connections.Add(new ConnectionInfo
                    {
                        Id = c.Id,
                        StartTime = c.CreatedAt > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(c.CreatedAt).LocalDateTime : DateTime.Now,
                        Inbound = c.Inbound,
                        InboundType = c.InboundType,
                        Process = c.ProcessInfo?.ProcessPath ?? string.Empty,
                        Ip = c.Source,
                        Source = c.Source,
                        Destination = string.IsNullOrWhiteSpace(c.Domain) ? c.Destination : $"{c.Domain} ({c.Destination})",
                        Domain = c.Domain,
                        Network = c.Network,
                        Protocol = c.Protocol,
                        Outbound = c.Outbound,
                        Chains = c.ChainList.ToList(),
                        Upload = c.UplinkTotal,
                        Download = c.DownlinkTotal
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[WARN] Failed to fetch connections via gRPC: {ex.Message}");
        }

        return connections;
    }

    public async Task CloseConnectionAsync(string connectionId)
    {
        try
        {
            var (client, headers) = GetClient();
            await client.CloseConnectionAsync(new CloseConnectionRequest { Id = connectionId }, headers);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[WARN] Failed to close connection {connectionId} via gRPC: {ex.Message}");
        }
    }

    public async Task CloseAllConnectionsAsync()
    {
        try
        {
            var (client, headers) = GetClient();
            await client.CloseAllConnectionsAsync(new Empty(), headers);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[WARN] Failed to close all connections via gRPC: {ex.Message}");
        }
    }

    public async IAsyncEnumerable<KernelLogEntry> SubscribeLogsAsync(
        string level,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var (client, headers) = GetClient();
        using var call = client.SubscribeLog(new Empty(), headers, cancellationToken: cancellationToken);
        var threshold = ParseLogThreshold(level);

        while (!cancellationToken.IsCancellationRequested && await call.ResponseStream.MoveNext(cancellationToken))
        {
            var logMsg = call.ResponseStream.Current;
            if (logMsg.Reset)
            {
                // The kernel signals a log buffer reset (subscription start / reload): the
                // following batch replays the full saved history, so let the UI drop the
                // stale buffer first instead of appending duplicates on every reconnect.
                LogsReset?.Invoke(this, EventArgs.Empty);
            }

            foreach (var msg in logMsg.Messages)
            {
                // SubscribeLog has an Empty request and always streams every buffered
                // level. Honor the runtime config client-side before constructing UI
                // records: WARN must not display or allocate DEBUG/INFO entries.
                // Protobuf enum order is severity ascending (PANIC=0 ... TRACE=6).
                if (msg.Level > threshold)
                {
                    continue;
                }

                var levelStr = MapLogLevel(msg.Level);
                // Strip ANSI at the source: sing-box colors every channel, and this
                // entry's non-empty level means the UI store will pass the message
                // through VERBATIM (only level-less entries go through ParseSingBoxLog).
                yield return new KernelLogEntry(levelStr, carton.Core.Services.KernelLogCleaner.StripAnsi(msg.Message_));
            }
        }
    }

    /// <summary>
    /// Raised when the kernel resets its log buffer (subscription start, kernel reload or
    /// ClearLogs). Consumers should clear their local log buffer to avoid duplicates.
    /// </summary>
    public event EventHandler? LogsReset;

    public async Task<DeprecatedWarnings?> GetDeprecatedWarningsAsync()
    {
        try
        {
            var (client, headers) = GetClient();
            return await client.GetDeprecatedWarningsAsync(new Empty(), headers);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[DEBUG] GetDeprecatedWarningsAsync error: {ex.Message}");
            return null;
        }
    }

    public async IAsyncEnumerable<Daemon.Status> SubscribeAggregatedStatusAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var (client, headers) = GetClient();
        using var call = client.SubscribeStatus(new SubscribeStatusRequest { Interval = SubscribeIntervalNanoseconds }, headers, cancellationToken: cancellationToken);

        while (!cancellationToken.IsCancellationRequested && await call.ResponseStream.MoveNext(cancellationToken))
        {
            yield return call.ResponseStream.Current;
        }
    }

    public async IAsyncEnumerable<ConnectionEvents> SubscribeConnectionEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var (client, headers) = GetClient();
        using var call = client.SubscribeConnections(
            new SubscribeConnectionsRequest { Interval = SubscribeIntervalNanoseconds },
            headers,
            cancellationToken: cancellationToken);

        while (!cancellationToken.IsCancellationRequested && await call.ResponseStream.MoveNext(cancellationToken))
        {
            yield return call.ResponseStream.Current;
        }
    }

    public async IAsyncEnumerable<Daemon.Groups> SubscribeGroupSnapshotsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var (client, headers) = GetClient();
        using var call = client.SubscribeGroups(new Empty(), headers, cancellationToken: cancellationToken);

        while (!cancellationToken.IsCancellationRequested && await call.ResponseStream.MoveNext(cancellationToken))
        {
            yield return call.ResponseStream.Current;
        }
    }

    public async IAsyncEnumerable<NetworkQualityTestProgress> StartNetworkQualityTestAsync(
        NetworkQualityTestRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var (client, headers) = GetClient();
        using var call = client.StartNetworkQualityTest(request, headers, cancellationToken: cancellationToken);

        while (!cancellationToken.IsCancellationRequested && await call.ResponseStream.MoveNext(cancellationToken))
        {
            yield return call.ResponseStream.Current;
        }
    }

    public async IAsyncEnumerable<STUNTestProgress> StartSTUNTestAsync(
        string server,
        string outboundTag,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var (client, headers) = GetClient();
        var request = new STUNTestRequest
        {
            Server = server,
            OutboundTag = outboundTag
        };
        using var call = client.StartSTUNTest(request, headers, cancellationToken: cancellationToken);

        while (!cancellationToken.IsCancellationRequested && await call.ResponseStream.MoveNext(cancellationToken))
        {
            yield return call.ResponseStream.Current;
        }
    }

    internal static LogLevel ParseLogThreshold(string? level)
        => (level ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "panic" => LogLevel.Panic,
            "fatal" => LogLevel.Fatal,
            "error" => LogLevel.Error,
            "warn" or "warning" => LogLevel.Warn,
            "info" => LogLevel.Info,
            "debug" => LogLevel.Debug,
            "trace" => LogLevel.Trace,
            _ => LogLevel.Warn
        };

    private static string MapLogLevel(LogLevel level) => level switch
    {
        LogLevel.Trace => "Trace",
        LogLevel.Debug => "Debug",
        LogLevel.Info => "Info",
        LogLevel.Warn => "Warn",
        LogLevel.Error => "Error",
        LogLevel.Fatal or LogLevel.Panic => "Fatal",
        _ => "Info"
    };

    public void Dispose()
    {
        lock (_channelLock)
        {
            _channel?.Dispose();
            _channel = null;
            _client = null;
        }
    }
}
