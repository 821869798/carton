using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using carton.Core.Services;
using Velopack.Sources;

namespace carton.GUI.Services;

public sealed class VelopackAcceleratedFileDownloader : HttpClientFileDownloader
{
    private readonly Action<string>? _log;

    public VelopackAcceleratedFileDownloader(Action<string>? log = null)
    {
        _log = log;
    }

    public override async Task DownloadFile(
        string url,
        string targetFile,
        Action<int> progress,
        IDictionary<string, string> headers,
        double timeout,
        CancellationToken cancelToken)
    {
        using var httpClient = CreateHttpClient(headers, timeout);
        var downloader = new AcceleratedFileDownloader(httpClient, _log);
        await downloader.DownloadFileAsync(
            url,
            targetFile,
            new Progress<FileDownloadProgress>(downloadProgress => progress(downloadProgress.Percent)),
            cancelToken).ConfigureAwait(false);
    }
}
