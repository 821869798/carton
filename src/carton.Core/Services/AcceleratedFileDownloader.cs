using System.Net;
using System.Net.Http.Headers;

namespace carton.Core.Services;

public sealed class FileDownloadProgress
{
    public long BytesReceived { get; init; }
    public long TotalBytes { get; init; }
    public int Percent => TotalBytes > 0 ? (int)Math.Clamp(BytesReceived * 100 / TotalBytes, 0, 100) : 0;
}

public sealed class AcceleratedFileDownloader
{
    private const int SequentialDownloadBufferSize = 128 * 1024;
    private const int ParallelDownloadBufferSize = 512 * 1024;
    private const int DefaultMaxSegments = 16;
    private const long ParallelDownloadMinFileSizeBytes = 8L * 1024 * 1024;
    private const long ParallelDownloadMinSegmentSizeBytes = 1L * 1024 * 1024;

    private readonly HttpClient _httpClient;
    private readonly Action<string>? _log;

    public AcceleratedFileDownloader(HttpClient httpClient, Action<string>? log = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _log = log;
    }

    public async Task DownloadFileAsync(
        string downloadUrl,
        string targetFile,
        IProgress<FileDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        TryDeleteFile(targetFile);

        var probe = await ProbeDownloadAsync(downloadUrl, cancellationToken).ConfigureAwait(false);
        if (probe.SupportsRange && probe.TotalBytes > 0)
        {
            var segmentCount = GetParallelSegmentCount(probe.TotalBytes);
            try
            {
                _log?.Invoke($"Downloading with {segmentCount} connections...");
                await DownloadFileInParallelAsync(downloadUrl, targetFile, probe.TotalBytes, segmentCount, progress, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                TryDeleteFile(targetFile);
                _log?.Invoke($"Multi-connection download unavailable, retrying with single connection: {ex.Message}");
            }
        }
        else if (probe.TotalBytes > 0)
        {
            _log?.Invoke("Server does not support range download, using single connection...");
        }

        await DownloadFileSequentialAsync(downloadUrl, targetFile, progress, cancellationToken).ConfigureAwait(false);
    }

    private async Task<DownloadProbeResult> ProbeDownloadAsync(string downloadUrl, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
        request.Headers.Range = new RangeHeaderValue(0, 0);

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var rangedTotalBytes = response.Content.Headers.ContentRange?.Length ?? 0;
        if (response.StatusCode == HttpStatusCode.PartialContent && rangedTotalBytes > 0)
        {
            return new DownloadProbeResult(
                rangedTotalBytes,
                SupportsRange: rangedTotalBytes >= ParallelDownloadMinFileSizeBytes);
        }

        var fallbackBytes = response.Content.Headers.ContentLength ?? 0;
        return new DownloadProbeResult(fallbackBytes, SupportsRange: false);
    }

    private async Task DownloadFileSequentialAsync(
        string downloadUrl,
        string targetFile,
        IProgress<FileDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
            downloadUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var totalBytes = ResolveTotalBytes(response);
        await DownloadFileSequentialFromResponseAsync(response, targetFile, totalBytes, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task DownloadFileInParallelAsync(
        string downloadUrl,
        string targetFile,
        long totalBytes,
        int segmentCount,
        IProgress<FileDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var ranges = BuildRanges(totalBytes, segmentCount);
        var downloadedBytes = 0L;

        try
        {
            await using (var output = new FileStream(
                             targetFile,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.ReadWrite,
                             1,
                             useAsync: true))
            {
                output.SetLength(totalBytes);
            }

            await Parallel.ForEachAsync(
                Enumerable.Range(0, ranges.Count),
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = segmentCount,
                    CancellationToken = cancellationToken
                },
                async (index, token) =>
                {
                    var (start, end) = ranges[index];

                    using var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
                    request.Headers.Range = new RangeHeaderValue(start, end);

                    using var response = await _httpClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        token).ConfigureAwait(false);

                    if (response.StatusCode != HttpStatusCode.PartialContent)
                    {
                        throw new InvalidOperationException($"Server returned {response.StatusCode} for range request.");
                    }

                    await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                    await using var target = new FileStream(
                        targetFile,
                        FileMode.Open,
                        FileAccess.Write,
                        FileShare.ReadWrite,
                        ParallelDownloadBufferSize,
                        FileOptions.Asynchronous | FileOptions.RandomAccess);
                    target.Seek(start, SeekOrigin.Begin);

                    var buffer = new byte[ParallelDownloadBufferSize];
                    while (true)
                    {
                        var read = await source.ReadAsync(buffer, token).ConfigureAwait(false);
                        if (read <= 0)
                        {
                            break;
                        }

                        await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                        var currentBytes = Interlocked.Add(ref downloadedBytes, read);
                        ReportProgress(progress, currentBytes, totalBytes);
                    }
                }).ConfigureAwait(false);
        }
        catch
        {
            TryDeleteFile(targetFile);
            throw;
        }
    }

    private static async Task DownloadFileSequentialFromResponseAsync(
        HttpResponseMessage response,
        string targetFile,
        long totalBytes,
        IProgress<FileDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var fileStream = new FileStream(
            targetFile,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            SequentialDownloadBufferSize,
            useAsync: true);

        var buffer = new byte[SequentialDownloadBufferSize];
        var bytesRead = 0L;

        while (true)
        {
            var read = await contentStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            bytesRead += read;
            ReportProgress(progress, bytesRead, totalBytes);
        }
    }

    private static void ReportProgress(IProgress<FileDownloadProgress>? progress, long bytesReceived, long totalBytes)
    {
        progress?.Report(new FileDownloadProgress
        {
            BytesReceived = bytesReceived,
            TotalBytes = totalBytes
        });
    }

    private static long ResolveTotalBytes(HttpResponseMessage response)
    {
        var rangedTotalBytes = response.Content.Headers.ContentRange?.Length ?? 0;
        if (rangedTotalBytes > 0)
        {
            return rangedTotalBytes;
        }

        return response.Content.Headers.ContentLength ?? 0;
    }

    private static int GetParallelSegmentCount(long totalBytes)
    {
        var bySize = (int)Math.Ceiling(totalBytes / (double)ParallelDownloadMinSegmentSizeBytes);
        return Math.Clamp(bySize, 2, DefaultMaxSegments);
    }

    private static IReadOnlyList<(long Start, long End)> BuildRanges(long totalBytes, int segmentCount)
    {
        var ranges = new List<(long Start, long End)>(segmentCount);
        var chunk = totalBytes / segmentCount;
        var remainder = totalBytes % segmentCount;

        var start = 0L;
        for (var i = 0; i < segmentCount; i++)
        {
            var size = chunk + (i < remainder ? 1 : 0);
            var end = start + size - 1;
            ranges.Add((start, end));
            start = end + 1;
        }

        return ranges;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private sealed record DownloadProbeResult(long TotalBytes, bool SupportsRange);
}
