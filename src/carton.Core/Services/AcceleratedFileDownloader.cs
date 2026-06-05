using System.ComponentModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Downloader;

namespace carton.Core.Services;

public sealed class FileDownloadProgress
{
    public long BytesReceived { get; init; }
    public long TotalBytes { get; init; }
    public double BytesPerSecond { get; init; }
    public int ActiveChunks { get; init; }
    public int Percent => TotalBytes > 0 ? (int)Math.Clamp(BytesReceived * 100 / TotalBytes, 0, 100) : 0;
}

public sealed class FileDownloadOptions
{
    public static FileDownloadOptions Default { get; } = new();

    public TimeSpan NoDataTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public int MaxRetryAttempts { get; init; } = 2;
}

public sealed class DownloadStalledException : IOException
{
    public DownloadStalledException(TimeSpan timeout)
        : base($"No download data was received for {timeout.TotalSeconds:0.#} seconds.")
    {
        Timeout = timeout;
    }

    public DownloadStalledException(TimeSpan timeout, Exception innerException)
        : base($"No download data was received for {timeout.TotalSeconds:0.#} seconds.", innerException)
    {
        Timeout = timeout;
    }

    public TimeSpan Timeout { get; }
}

public sealed class AcceleratedFileDownloader
{
    private const int DefaultChunkCount = 8;
    private const long ParallelDownloadMinFileSizeBytes = 8L * 1024 * 1024;
    private const long MinimumChunkSizeBytes = 1L * 1024 * 1024;
    private const string DownloadFileExtension = ".download";

    private readonly HttpClient _httpClient;
    private readonly Action<string>? _statusLog;
    private readonly Action<string>? _diagnosticLog;
    private readonly FileDownloadOptions _options;

    public AcceleratedFileDownloader(
        HttpClient httpClient,
        Action<string>? log = null,
        Action<string>? diagnosticLog = null,
        FileDownloadOptions? options = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _statusLog = log;
        _diagnosticLog = diagnosticLog ?? log;
        _options = options ?? FileDownloadOptions.Default;
    }

    public async Task DownloadFileAsync(
        string downloadUrl,
        string targetFile,
        IProgress<FileDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetFile)) ?? Environment.CurrentDirectory);
        var throttler = new ProgressThrottler(progress, TimeSpan.FromMilliseconds(500));
        throttler.Report(0, 0, force: true);

        var completion = new TaskCompletionSource<AsyncCompletedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var configuration = CreateConfiguration();
        var createdHttpClients = new ConcurrentBag<HttpClient>();
        configuration.CustomHttpClientFactory = () =>
        {
            var client = CreateDownloadHttpClient();
            createdHttpClients.Add(client);
            return client;
        };
        var download = new DownloadService(configuration);
        var lastDiagnosticLogAt = DateTimeOffset.MinValue;

        _diagnosticLog?.Invoke(
            $"Download config: chunks={configuration.ChunkCount}, parallel={configuration.ParallelCount}, " +
            $"minChunking={ParallelDownloadMinFileSizeBytes} bytes, minChunk={MinimumChunkSizeBytes} bytes, " +
            $"blockTimeout={configuration.BlockTimeout} ms, noDataTimeout={_options.NoDataTimeout.TotalSeconds:0.#} s");

        download.DownloadStarted += (_, e) =>
        {
            _statusLog?.Invoke($"Downloading {Path.GetFileName(e.FileName)} ({e.TotalBytesToReceive} bytes)...");
            throttler.Report(0, e.TotalBytesToReceive, force: true);
        };

        download.DownloadProgressChanged += (_, e) =>
        {
            throttler.Report(
                e.ReceivedBytesSize,
                e.TotalBytesToReceive,
                e.BytesPerSecondSpeed,
                e.ActiveChunks);

            var now = DateTimeOffset.UtcNow;
            if (lastDiagnosticLogAt == DateTimeOffset.MinValue ||
                now - lastDiagnosticLogAt >= TimeSpan.FromSeconds(5) ||
                (e.TotalBytesToReceive > 0 && e.ReceivedBytesSize >= e.TotalBytesToReceive))
            {
                _diagnosticLog?.Invoke(
                    $"Download progress: {e.ReceivedBytesSize}/{e.TotalBytesToReceive} bytes, " +
                    $"speed={e.BytesPerSecondSpeed:0} B/s, activeChunks={e.ActiveChunks}");
                lastDiagnosticLogAt = now;
            }
        };

        download.DownloadFileCompleted += (_, e) =>
        {
            completion.TrySetResult(e);
        };

        try
        {
            await download.DownloadFileTaskAsync(downloadUrl, targetFile, cancellationToken).ConfigureAwait(false);
            if (completion.Task.IsCompleted)
            {
                await HandleCompletionAsync(completion.Task.Result, targetFile, cancellationToken).ConfigureAwait(false);
                throttler.Flush();
            }
            else if (!File.Exists(targetFile))
            {
                throw new IOException("Download did not produce the target file.");
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
                                   IsDownloaderNoDataTimeout(ex))
        {
            throw new DownloadStalledException(_options.NoDataTimeout, ex);
        }
        finally
        {
            foreach (var client in createdHttpClients)
            {
                client.Dispose();
            }
        }
    }

    private DownloadConfiguration CreateConfiguration()
    {
        return new DownloadConfiguration
        {
            BufferBlockSize = 128 * 1024,
            ChunkCount = DefaultChunkCount,
            ParallelDownload = true,
            ParallelCount = DefaultChunkCount,
            MaxTryAgainOnFailure = Math.Max(0, _options.MaxRetryAttempts),
            BlockTimeout = (int)Math.Clamp(_options.NoDataTimeout.TotalMilliseconds, 1, int.MaxValue),
            HttpClientTimeout = (int)TimeSpan.FromHours(1).TotalMilliseconds,
            ClearPackageOnCompletionWithFailure = false,
            MinimumSizeOfChunking = ParallelDownloadMinFileSizeBytes,
            MinimumChunkSize = MinimumChunkSizeBytes,
            EnableAutoResumeDownload = true,
            DownloadFileExtension = DownloadFileExtension,
            FileExistPolicy = FileExistPolicy.Delete,
            RequestConfiguration = CreateRequestConfiguration()
        };
    }

    private HttpClient CreateDownloadHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        CopyHeaders(_httpClient.DefaultRequestHeaders, client.DefaultRequestHeaders);
        return client;
    }

    private RequestConfiguration CreateRequestConfiguration()
    {
        var request = new RequestConfiguration
        {
            Accept = "*/*",
            AllowAutoRedirect = true,
            KeepAlive = true,
            UserAgent = ResolveUserAgent(),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            ConnectTimeout = (int)Math.Clamp(_options.NoDataTimeout.TotalMilliseconds, 1, int.MaxValue)
        };

        if (_httpClient.DefaultRequestHeaders.Authorization != null)
        {
            request.Authorization = _httpClient.DefaultRequestHeaders.Authorization;
        }

        foreach (var header in _httpClient.DefaultRequestHeaders)
        {
            AddRequestHeader(request, header.Key, header.Value);
        }

        return request;
    }

    private string ResolveUserAgent()
    {
        var userAgent = _httpClient.DefaultRequestHeaders.UserAgent;
        return userAgent.Count == 0
            ? HttpClientFactory.DefaultUserAgent
            : string.Join(" ", userAgent.Select(value => value.ToString()));
    }

    private void AddRequestHeader(RequestConfiguration request, string name, IEnumerable<string> values)
    {
        var value = string.Join(", ", values);
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (string.Equals(name, "User-Agent", StringComparison.OrdinalIgnoreCase))
        {
            request.UserAgent = value;
            return;
        }

        if (string.Equals(name, "Accept", StringComparison.OrdinalIgnoreCase))
        {
            request.Accept = value;
            return;
        }

        if (string.Equals(name, "Authorization", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            request.Headers[name] = value;
        }
        catch (Exception ex)
        {
            _diagnosticLog?.Invoke($"Skipped download request header '{name}': {ex.Message}");
        }
    }

    private static void CopyHeaders(HttpHeaders source, HttpHeaders destination)
    {
        foreach (var header in source)
        {
            destination.TryAddWithoutValidation(header.Key, header.Value);
        }
    }

    private async Task HandleCompletionAsync(
        AsyncCompletedEventArgs completion,
        string targetFile,
        CancellationToken cancellationToken)
    {
        if (completion.Cancelled)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        if (completion.Error != null)
        {
            if (IsDownloaderNoDataTimeout(completion.Error))
            {
                throw new DownloadStalledException(_options.NoDataTimeout, completion.Error);
            }

            throw new IOException($"Download failed: {completion.Error.Message}", completion.Error);
        }

        if (!File.Exists(targetFile))
        {
            throw new IOException("Download completed without creating the target file.");
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private sealed class ProgressThrottler
    {
        private readonly IProgress<FileDownloadProgress>? _progress;
        private readonly TimeSpan _minimumInterval;
        private readonly object _gate = new();
        private FileDownloadProgress _lastProgress = new();
        private DateTimeOffset _lastReportedAt = DateTimeOffset.MinValue;

        public ProgressThrottler(IProgress<FileDownloadProgress>? progress, TimeSpan minimumInterval)
        {
            _progress = progress;
            _minimumInterval = minimumInterval;
        }

        public void Report(
            long bytesReceived,
            long totalBytes,
            double bytesPerSecond = 0,
            int activeChunks = 0,
            bool force = false)
        {
            lock (_gate)
            {
                _lastProgress = new FileDownloadProgress
                {
                    BytesReceived = bytesReceived,
                    TotalBytes = totalBytes,
                    BytesPerSecond = bytesPerSecond,
                    ActiveChunks = activeChunks
                };

                var now = DateTimeOffset.UtcNow;
                if (!force &&
                    _lastReportedAt != DateTimeOffset.MinValue &&
                    now - _lastReportedAt < _minimumInterval &&
                    (totalBytes <= 0 || bytesReceived < totalBytes))
                {
                    return;
                }

                _progress?.Report(_lastProgress);
                _lastReportedAt = now;
            }
        }

        public void Flush()
        {
            lock (_gate)
            {
                _progress?.Report(_lastProgress);
                _lastReportedAt = DateTimeOffset.UtcNow;
            }
        }
    }

    private static bool IsDownloaderNoDataTimeout(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is TimeoutException or TaskCanceledException)
            {
                return true;
            }

            if (current.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
                current.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
