using Daemon;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using carton.Core.Models;
using carton.Core.Services.SingBoxApi;

namespace carton.Core.Services;

public partial class SingBoxManager
{
    /// <summary>
    /// Current outbound mode config: the mode list is fetched once (it only changes with
    /// the config) and the current mode comes from the push stream cache so repeated
    /// calls do not hit the API at all.
    /// </summary>
    public async Task<ApiModeConfigSnapshot?> GetModeConfigAsync()
    {
        var modeList = CurrentModeList;
        if (modeList == null)
        {
            var snapshot = await CreateApiClient().GetModeConfigAsync();
            if (snapshot != null)
            {
                lock (_snapshotSyncRoot)
                {
                    _currentModeList = snapshot.ModeList;
                    if (!string.IsNullOrWhiteSpace(snapshot.Mode))
                    {
                        _currentMode = snapshot.Mode;
                    }
                }

                return snapshot;
            }

            return null;
        }

        return new ApiModeConfigSnapshot
        {
            Mode = CurrentMode,
            ModeList = modeList
        };
    }

    public async Task<bool> SetModeAsync(string mode)
    {
        var success = await CreateApiClient().SetModeAsync(mode);
        if (success)
        {
            // Optimistic local update; the push stream will confirm/refresh it.
            lock (_snapshotSyncRoot)
            {
                _currentMode = mode;
            }

            ModeChanged?.Invoke(this, mode);
        }

        return success;
    }

    public async Task<List<OutboundGroup>> GetOutboundGroupsAsync()
    {
        return await CreateApiClient().GetOutboundGroupsAsync();
    }

    public async Task SelectOutboundAsync(string groupTag, string outboundTag)
    {
        await CreateApiClient().SelectOutboundAsync(groupTag, outboundTag);
    }

    public Task SetGroupExpandAsync(string groupTag, bool isExpand)
        => CreateApiClient().SetGroupExpandAsync(groupTag, isExpand);

    public async Task<IReadOnlyList<DeprecatedConfigWarning>> GetDeprecatedWarningsAsync()
    {
        var client = CreateApiClient();
        var warnings = await client.GetDeprecatedWarningsAsync();
        if (warnings == null || warnings.Warnings.Count == 0)
        {
            return Array.Empty<DeprecatedConfigWarning>();
        }

        var result = new List<DeprecatedConfigWarning>(warnings.Warnings.Count);
        foreach (var warning in warnings.Warnings)
        {
            result.Add(new DeprecatedConfigWarning
            {
                Message = warning.Message,
                Description = warning.Description,
                DeprecatedVersion = warning.DeprecatedVersion,
                ScheduledVersion = warning.ScheduledVersion,
                MigrationLink = warning.MigrationLink,
                Impending = warning.Impending
            });
        }

        return result;
    }

    /// <summary>Current daemon apiVersion (0 when unknown); refreshed by SyncRunningStateAsync.</summary>
    public int ApiVersion
    {
        get
        {
            var client = SingBoxApiClientFactory.Peek();
            return client?.ApiVersion ?? 0;
        }
    }

    /// <summary>
    /// Feature gate mirroring the official dashboard capabilities.ts table.
    /// </summary>
    public bool SupportsApiFeature(string feature) => feature switch
    {
        // MIN_API_VERSION from the official sing-box dashboard
        "usbip" => ApiVersion >= 2,
        "openVpnAndOpenConnect" => ApiVersion >= 3,
        "taildrop" => ApiVersion >= 4,
        _ => false
    };

    public async Task<Dictionary<string, int>> RunGroupDelayTestAsync(string groupTag, string? testUrl = null, int timeoutMs = 5000)
    {
        if (_state.Status != carton.Core.Models.ServiceStatus.Running || string.IsNullOrWhiteSpace(groupTag))
        {
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        var apiClient = CreateApiClient();
        // Reuse the long-lived groups stream: the monitor already feeds GroupsUpdated
        // snapshots on every url test history change, so a delay test is just a trigger
        // plus waiting for the next fresh snapshots - no extra subscription is opened.
        // The baseline is filtered to the requested group so the result only contains
        // its items and completion is tracked for those tags alone.
        // A stale snapshot (stream reconnecting) must not seed the baseline: its
        // timestamps are from before the disconnect and would misjudge freshness.
        var groupsSnapshot = CurrentGroups;
        var baseline = CollectBaseline(
            groupsSnapshot,
            group => string.Equals(group.Tag, groupTag, StringComparison.OrdinalIgnoreCase));
        if (!baseline.FoundAny || !groupsSnapshot.IsFresh(TimeSpan.FromSeconds(10)))
        {
            // Groups snapshot not arrived yet (monitor still warming up) or stale:
            // fall back to the self-contained implementation which subscribes on demand.
            return await apiClient.RunGroupDelayTestAsync(groupTag, testUrl, timeoutMs);
        }

        var result = new Dictionary<string, int>(baseline.Delays, StringComparer.OrdinalIgnoreCase);
        var fresh = await WaitForFreshDelaysAsync(
            baseline,
            result,
            timeoutMs,
            () => apiClient.URLTestAsync(groupTag));
        return fresh;
    }

    public async Task<Dictionary<string, int>> RunOutboundDelayTestsAsync(IEnumerable<string> outboundTags, string? testUrl = null, int timeoutMs = 5000)
    {
        var tags = outboundTags
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (tags.Count == 0 || _state.Status != carton.Core.Models.ServiceStatus.Running)
        {
            return result;
        }

        var apiClient = CreateApiClient();
        var groupsSnapshot = CurrentGroups;
        var baseline = CollectBaseline(groupsSnapshot, _ => true, tags);
        if (!baseline.FoundAny || !groupsSnapshot.IsFresh(TimeSpan.FromSeconds(10)))
        {
            // No fresh groups snapshot yet: fall back to the on-demand subscription variant.
            return await apiClient.RunOutboundDelayTestsAsync(tags, testUrl, timeoutMs);
        }

        foreach (var (tag, delay) in baseline.Delays)
        {
            result[tag] = delay;
        }

        // URLTest is fire-and-forget on the kernel side. The daemon handles ALL tag
        // kinds natively (verified against sing-box daemon/started_service.go URLTest):
        // URLTest groups -> CheckOutbounds, other groups -> batch test of leaf members,
        // leaf outbounds -> single async test of that node. So the requested tags are
        // passed through UNCHANGED - matching the official dashboard, which calls
        // urlTest(item.tag) directly for a single node. NO leaf->group mapping here:
        // that would re-test the whole parent group for every single-node request.
        var triggerTags = tags;

        var fresh = await WaitForFreshDelaysAsync(
            baseline,
            result,
            timeoutMs,
            async () =>
            {
                var triggerTasks = triggerTags
                    .Select(apiClient.URLTestAsync)
                    .ToList();
                try
                {
                    await Task.WhenAll(triggerTasks);
                }
                catch
                {
                    // Individual failures leave the stale values in place.
                }
            });
        return fresh;
    }

    /// <summary>Baseline state for a delay test: last url test time + last delay per tag.</summary>
    private sealed record DelayTestBaseline(
        Dictionary<string, long> UrlTestTimes,
        Dictionary<string, int> Delays,
        bool FoundAny);

    private static DelayTestBaseline CollectBaseline(
        GroupsSnapshot snapshot,
        Func<OutboundGroup, bool> groupFilter,
        IReadOnlyCollection<string>? tagFilter = null)
    {
        var times = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var delays = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in snapshot.Groups)
        {
            if (!groupFilter(group))
            {
                continue;
            }

            foreach (var item in group.Items)
            {
                if (tagFilter != null && !tagFilter.Contains(item.Tag, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                times[item.Tag] = item.UrlTestTime;
                delays[item.Tag] = item.UrlTestDelay;
            }
        }

        return new DelayTestBaseline(times, delays, FoundAny: times.Count > 0);
    }

    /// <summary>
    /// Waits for fresh url test results on the long-lived GroupsUpdated stream until
    /// every baseline tag refreshed (newer UrlTestTime / changed delay) or the budget
    /// runs out, then returns a THREAD-SAFE COPY of the collected results.
    /// The trigger delegate runs AFTER the handler is attached, closing the race where
    /// the kernel pushes the refreshed snapshot between triggering and subscribing.
    /// </summary>
    /// <remarks>
    /// Copy semantics matter: the raw dictionary keeps being mutated by the handler
    /// while the wait ends (timeout fires / completion races a trailing snapshot), and
    /// unsubscribing cannot abort a handler already executing. Returning a copy taken
    /// under the same lock the handler uses hands the caller an immutable result.
    /// </remarks>
    private async Task<Dictionary<string, int>> WaitForFreshDelaysAsync(
        DelayTestBaseline baseline,
        Dictionary<string, int> result,
        int timeoutMs,
        Func<Task> triggerTestsAsync)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(Math.Max(timeoutMs, 1000)));
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // All handler mutations of `result` run under this lock; the final copy below is
        // taken under the same lock so the caller can never observe a mid-write state.
        // (GroupsUpdated itself is raised from a single monitor thread, so the lock
        // does not serialize handler vs handler - it fences handler vs CALLER.)
        var gate = new object();
        EventHandler<GroupsSnapshot> handler = (_, snapshot) =>
        {
            lock (gate)
            {
                var remaining = SingBoxGrpcApiClient.MergeFreshDelayResults(
                    baseline.UrlTestTimes,
                    baseline.Delays,
                    snapshot.Groups.SelectMany(group => group.Items)
                        .Select(item => new KeyValuePair<string, (long, int)>(item.Tag, (item.UrlTestTime, item.UrlTestDelay))));

                // Apply fresh values as they arrive; a tag only leaves "remaining" when
                // its result is actually fresh (see MergeFreshDelayResults - unchanged
                // full-snapshot pushes must not complete the wait early).
                foreach (var group in snapshot.Groups)
                {
                    foreach (var item in group.Items)
                    {
                        if (baseline.UrlTestTimes.TryGetValue(item.Tag, out var baselineTime) &&
                            SingBoxGrpcApiClient.IsFreshUrlTestResult(baselineTime, baseline.Delays.GetValueOrDefault(item.Tag), item.UrlTestTime, item.UrlTestDelay))
                        {
                            result[item.Tag] = item.UrlTestDelay;
                        }
                    }
                }

                if (remaining.Count == 0)
                {
                    completion.TrySetResult(true);
                }
            }
        };

        // Attach the handler BEFORE the tests are triggered: the push stream may
        // deliver the refreshed snapshot at any moment and a late subscription would
        // miss it and burn the whole budget.
        GroupsUpdated += handler;
        try
        {
            await triggerTestsAsync();
            await completion.Task.WaitAsync(cts.Token);
        }
        catch (TimeoutException)
        {
            // Budget exhausted: return whatever has been collected so far.
        }
        catch (OperationCanceledException)
        {
            // Budget exhausted: return whatever has been collected so far.
        }
        finally
        {
            GroupsUpdated -= handler;
        }

        // The unsubscribe does not abort a handler that is already executing, so the
        // final copy must be taken under the handler's lock: this is the point where
        // the mutating dictionary becomes the caller's immutable result.
        lock (gate)
        {
            return new Dictionary<string, int>(result, StringComparer.OrdinalIgnoreCase);
        }
    }

    public async Task<List<ConnectionInfo>> GetConnectionsAsync()
    {
        return await CreateApiClient().GetConnectionsAsync();
    }

    public async Task CloseConnectionAsync(string connectionId)
    {
        await CreateApiClient().CloseConnectionAsync(connectionId);
    }

    public async Task CloseAllConnectionsAsync()
    {
        await CreateApiClient().CloseAllConnectionsAsync();
    }

    private static async Task<bool> IsLocalPortListeningAsync(int port)
    {
        if (port is <= 0 or > 65535)
        {
            return false;
        }

        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            await socket.ConnectAsync(IPAddress.Loopback, port, cts.Token);
            return socket.Connected;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> IsApiReachableAsync()
    {
        var port = HttpClientFactory.LocalNativeApiPort > 0
            ? HttpClientFactory.LocalNativeApiPort
            : (HttpClientFactory.LocalApiPort > 0 ? HttpClientFactory.LocalApiPort : 9090);

        if (!await IsLocalPortListeningAsync(port))
        {
            return false;
        }

        return await CreateApiClient().IsReachableAsync();
    }

    private async Task<int?> TryFindProcessPidByApiPortAsync()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "netstat",
                        Arguments = "-ano -p tcp",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                var output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();

                foreach (var raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var line = raw.Trim();
                    if (!line.Contains($":{_apiPort}", StringComparison.Ordinal) ||
                        !line.Contains("LISTEN", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 0 && int.TryParse(parts[^1], out var pid) && pid > 0)
                    {
                        return pid;
                    }
                }

                return null;
            }

            using var unixProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "lsof",
                    Arguments = $"-nP -iTCP:{_apiPort} -sTCP:LISTEN -t",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            unixProcess.Start();
            var pidOutput = (await unixProcess.StandardOutput.ReadToEndAsync()).Trim();
            await unixProcess.WaitForExitAsync();

            var firstLine = pidOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (int.TryParse(firstLine, out var unixPid) && unixPid > 0)
            {
                return unixPid;
            }
        }
        catch
        {
        }

        return null;
    }
}
