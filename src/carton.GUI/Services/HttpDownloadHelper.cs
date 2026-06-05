using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using carton.Core.Services;

namespace carton.GUI.Services;

public static class HttpDownloadHelper
{
    public static async Task<string> DownloadTextAsync(
        HttpClient httpClient,
        string url,
        Action<long, long>? progress = null,
        CancellationToken cancellationToken = default,
        TimeSpan? noDataTimeout = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        return await DownloadTextAsync(httpClient, request, progress, cancellationToken, noDataTimeout).ConfigureAwait(false);
    }

    public static async Task<string> DownloadTextAsync(
        HttpClient httpClient,
        HttpRequestMessage request,
        Action<long, long>? progress = null,
        CancellationToken cancellationToken = default,
        TimeSpan? noDataTimeout = null)
    {
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? 0;
        var encoding = ResolveEncoding(response.Content.Headers.ContentType?.CharSet);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var memory = totalBytes > 0 && totalBytes <= int.MaxValue
            ? new MemoryStream((int)totalBytes)
            : new MemoryStream();

        var buffer = new byte[16 * 1024];
        var bytesReceived = 0L;
        var lastReportAt = DateTimeOffset.MinValue;
        var minInterval = TimeSpan.FromMilliseconds(500);
        var dataTimeout = noDataTimeout ?? FileDownloadOptions.Default.NoDataTimeout;

        while (true)
        {
            var read = await ReadWithNoDataTimeoutAsync(
                stream,
                buffer,
                dataTimeout,
                cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            bytesReceived += read;

            if (progress != null)
            {
                var now = DateTimeOffset.UtcNow;
                if (lastReportAt == DateTimeOffset.MinValue ||
                    now - lastReportAt >= minInterval ||
                    (totalBytes > 0 && bytesReceived >= totalBytes))
                {
                    progress(bytesReceived, totalBytes);
                    lastReportAt = now;
                }
            }
        }

        progress?.Invoke(bytesReceived, totalBytes);
        return encoding.GetString(memory.ToArray());
    }

    private static async Task<int> ReadWithNoDataTimeoutAsync(
        Stream stream,
        byte[] buffer,
        TimeSpan noDataTimeout,
        CancellationToken cancellationToken)
    {
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readCts.CancelAfter(noDataTimeout);
        try
        {
            return await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), readCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DownloadStalledException(noDataTimeout, ex);
        }
    }

    private static Encoding ResolveEncoding(string? charset)
    {
        if (string.IsNullOrWhiteSpace(charset))
        {
            return Encoding.UTF8;
        }

        try
        {
            return Encoding.GetEncoding(charset);
        }
        catch
        {
            return Encoding.UTF8;
        }
    }
}
