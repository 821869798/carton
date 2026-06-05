using System.Net;
using System.Net.Sockets;
using System.Text;
using carton.Core.Services;
using Xunit;

namespace carton.GUI.Tests.Services;

public sealed class AcceleratedFileDownloaderTests
{
    [Fact]
    public async Task DownloadFileAsync_ThrowsWhenNoDataArrivesWithinTimeout()
    {
        await using var server = await TestHttpDownloadServer.StartAsync(
            async (request, stream, token) =>
            {
                await TestHttpDownloadServer.WriteResponseHeadersAsync(stream, HttpStatusCode.OK, 4, token);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            });

        var targetFile = Path.Combine(Path.GetTempPath(), $"carton-download-test-{Guid.NewGuid():N}.bin");
        using var httpClient = new HttpClient();
        var downloader = new AcceleratedFileDownloader(
            httpClient,
            options: new FileDownloadOptions
            {
                NoDataTimeout = TimeSpan.FromMilliseconds(150),
                MaxRetryAttempts = 0
            });

        await Assert.ThrowsAsync<DownloadStalledException>(() =>
            downloader.DownloadFileAsync(server.Url, targetFile));

        Assert.False(File.Exists(targetFile));
        Assert.True(File.Exists(targetFile + ".download"));
    }

    [Fact]
    public async Task DownloadFileAsync_ResumesPartialDownloadAfterStalledRead()
    {
        var content = new byte[] { 1, 2, 3, 4 };
        var completedRequests = 0;

        await using var server = await TestHttpDownloadServer.StartAsync(
            async (request, stream, token) =>
            {
                if (request.Method == "HEAD")
                {
                    await TestHttpDownloadServer.WriteResponseHeadersAsync(stream, HttpStatusCode.OK, content.Length, token);
                    return;
                }

                var requestNumber = Interlocked.Increment(ref completedRequests);
                if (requestNumber == 1)
                {
                    await TestHttpDownloadServer.WriteResponseHeadersAsync(stream, HttpStatusCode.OK, content.Length, token);
                    await stream.WriteAsync(content.AsMemory(0, 2), token);
                    await stream.FlushAsync(token);
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return;
                }

                if (request.RangeStart is >= 2)
                {
                    var remaining = content.AsMemory((int)request.RangeStart.Value);
                    await TestHttpDownloadServer.WritePartialContentHeadersAsync(
                        stream,
                        request.RangeStart.Value,
                        content.Length - 1,
                        content.Length,
                        remaining.Length,
                        token);
                    await stream.WriteAsync(remaining, token);
                    return;
                }

                await TestHttpDownloadServer.WriteResponseHeadersAsync(stream, HttpStatusCode.OK, content.Length, token);
                await stream.WriteAsync(content, token);
            });

        var targetFile = Path.Combine(Path.GetTempPath(), $"carton-download-test-{Guid.NewGuid():N}.bin");
        using var httpClient = new HttpClient();
        var downloader = new AcceleratedFileDownloader(
            httpClient,
            options: new FileDownloadOptions
            {
                NoDataTimeout = TimeSpan.FromMilliseconds(150),
                MaxRetryAttempts = 0
            });

        try
        {
            await downloader.DownloadFileAsync(server.Url, targetFile);

            Assert.Equal(content, await File.ReadAllBytesAsync(targetFile));
            Assert.False(File.Exists(targetFile + ".download"));
            Assert.True(completedRequests >= 2);
        }
        finally
        {
            TryDelete(targetFile);
            TryDelete(targetFile + ".download");
        }
    }

    private static void TryDelete(string path)
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

    private sealed record TestHttpRequest(string Method, long? RangeStart);

    private sealed class TestHttpDownloadServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Func<TestHttpRequest, NetworkStream, CancellationToken, Task> _handler;
        private readonly CancellationTokenSource _shutdown = new();
        private readonly Task _acceptLoop;

        private TestHttpDownloadServer(
            TcpListener listener,
            Func<TestHttpRequest, NetworkStream, CancellationToken, Task> handler)
        {
            _listener = listener;
            _handler = handler;
            Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/file.bin";
            _acceptLoop = Task.Run(AcceptLoopAsync);
        }

        public string Url { get; }

        public static Task<TestHttpDownloadServer> StartAsync(
            Func<TestHttpRequest, NetworkStream, CancellationToken, Task> handler)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return Task.FromResult(new TestHttpDownloadServer(listener, handler));
        }

        public static async Task WriteResponseHeadersAsync(
            NetworkStream stream,
            HttpStatusCode status,
            long contentLength,
            CancellationToken cancellationToken)
        {
            var headers = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {(int)status} {status}\r\n" +
                $"Content-Length: {contentLength}\r\n" +
                "Accept-Ranges: bytes\r\n" +
                "Connection: close\r\n\r\n");
            await stream.WriteAsync(headers, cancellationToken);
        }

        public static async Task WritePartialContentHeadersAsync(
            NetworkStream stream,
            long rangeStart,
            long rangeEnd,
            long totalLength,
            long contentLength,
            CancellationToken cancellationToken)
        {
            var headers = Encoding.ASCII.GetBytes(
                "HTTP/1.1 206 Partial Content\r\n" +
                $"Content-Length: {contentLength}\r\n" +
                $"Content-Range: bytes {rangeStart}-{rangeEnd}/{totalLength}\r\n" +
                "Accept-Ranges: bytes\r\n" +
                "Connection: close\r\n\r\n");
            await stream.WriteAsync(headers, cancellationToken);
        }

        private async Task AcceptLoopAsync()
        {
            while (!_shutdown.IsCancellationRequested)
            {
                TcpClient? client = null;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_shutdown.Token);
                    _ = Task.Run(() => HandleClientAsync(client), _shutdown.Token);
                }
                catch (OperationCanceledException)
                {
                    client?.Dispose();
                    break;
                }
                catch
                {
                    client?.Dispose();
                }
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            using var _client = client;
            await using var stream = client.GetStream();
            var request = await ReadRequestAsync(stream, _shutdown.Token);
            await _handler(request, stream, _shutdown.Token);
        }

        private static async Task<TestHttpRequest> ReadRequestAsync(
            NetworkStream stream,
            CancellationToken cancellationToken)
        {
            var buffer = new byte[4096];
            var received = 0;
            while (received < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(received, buffer.Length - received), cancellationToken);
                if (read <= 0)
                {
                    break;
                }

                received += read;
                var text = Encoding.ASCII.GetString(buffer, 0, received);
                if (text.Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    return ParseRequest(text);
                }
            }

            return new TestHttpRequest("GET", null);
        }

        private static TestHttpRequest ParseRequest(string text)
        {
            var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            var method = lines.Length == 0
                ? "GET"
                : lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "GET";
            long? rangeStart = null;
            var rangeLine = lines.FirstOrDefault(line => line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase));
            if (rangeLine != null)
            {
                var equalsIndex = rangeLine.IndexOf('=');
                var dashIndex = rangeLine.IndexOf('-', equalsIndex + 1);
                if (equalsIndex >= 0 && dashIndex > equalsIndex &&
                    long.TryParse(rangeLine[(equalsIndex + 1)..dashIndex], out var parsedStart))
                {
                    rangeStart = parsedStart;
                }
            }

            return new TestHttpRequest(method, rangeStart);
        }

        public async ValueTask DisposeAsync()
        {
            await _shutdown.CancelAsync();
            _listener.Stop();
            try
            {
                await _acceptLoop;
            }
            catch
            {
            }

            _shutdown.Dispose();
        }
    }
}
