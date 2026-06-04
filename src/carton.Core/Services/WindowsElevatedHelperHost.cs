using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using carton.Core.Models;
using carton.Core.Serialization;

namespace carton.Core.Services;

public static class WindowsElevatedHelperHost
{
    private const string TokenHeader = "X-Carton-Helper-Token";

    public static bool TryRunFromArgs(string[] args)
    {
        if (!OperatingSystem.IsWindows() ||
            args.Length == 0 ||
            !string.Equals(args[0], WindowsElevatedHelperTaskUtility.HelperArg, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var port = 0;
        var parentPid = 0;
        string? token = null;
        string? requestFilePath = null;
        for (var i = 1; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--port", StringComparison.OrdinalIgnoreCase) &&
                i + 1 < args.Length &&
                int.TryParse(args[i + 1], out var parsedPort))
            {
                port = parsedPort;
                i++;
                continue;
            }

            if (string.Equals(args[i], "--token", StringComparison.OrdinalIgnoreCase) &&
                i + 1 < args.Length)
            {
                token = args[i + 1];
                i++;
                continue;
            }

            if (string.Equals(args[i], "--parent-pid", StringComparison.OrdinalIgnoreCase) &&
                i + 1 < args.Length &&
                int.TryParse(args[i + 1], out var parsedParentPid))
            {
                parentPid = parsedParentPid;
                i++;
                continue;
            }

            if (string.Equals(args[i], WindowsElevatedHelperTaskUtility.RequestFileArg, StringComparison.OrdinalIgnoreCase) &&
                i + 1 < args.Length)
            {
                requestFilePath = args[i + 1];
                i++;
            }
        }

        if (!TryResolveLaunchParameters(requestFilePath, ref port, ref token, ref parentPid))
        {
            return true;
        }

        RunAsync(port, token!, parentPid).GetAwaiter().GetResult();
        return true;
    }

    private static bool TryResolveLaunchParameters(
        string? requestFilePath,
        ref int port,
        ref string? token,
        ref int parentPid)
    {
        if (!string.IsNullOrWhiteSpace(requestFilePath))
        {
            try
            {
                if (!File.Exists(requestFilePath))
                {
                    return false;
                }

                var payload = File.ReadAllText(requestFilePath);
                try
                {
                    File.Delete(requestFilePath);
                }
                catch
                {
                }

                var launchRequest = JsonSerializer.Deserialize(
                    payload,
                    CartonCoreJsonContext.Default.WindowsHelperLaunchRequest);
                if (launchRequest == null ||
                    launchRequest.Port <= 0 ||
                    string.IsNullOrWhiteSpace(launchRequest.Token) ||
                    launchRequest.ExpiresAtUtc <= DateTimeOffset.UtcNow)
                {
                    return false;
                }

                port = launchRequest.Port;
                token = launchRequest.Token;
                parentPid = launchRequest.ParentPid;
                return true;
            }
            catch
            {
                return false;
            }
        }

        return port > 0 && !string.IsNullOrWhiteSpace(token);
    }

    private static async Task RunAsync(int port, string token, int parentPid)
    {
        // Stop signal file - shared path between carton and helper
        var stopSignalPath = Path.Combine(
            Path.GetDirectoryName(Environment.ProcessPath) ?? ".", ".carton-stop-signal");
        // Clean up stale signal on startup
        try { if (File.Exists(stopSignalPath)) File.Delete(stopSignalPath); } catch { }

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        Process? singBoxProcess = null;
        StreamWriter? logWriter = null;
        Task? stdoutTask = null;
        Task? stderrTask = null;
        CancellationTokenSource? startupWatchCts = null;
        Task? startupWatchTask = null;
        int? lastKnownPid = null;
        int? lastKnownExitCode = null;
        string? lastKnownError = null;
        string? lastApiAddress = null;
        string? lastApiSecret = null;
        var processLock = new object();
        var shouldStop = false;

        static void WriteResponseFile(string path, WindowsHelperActionResponse response)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
                var payload = JsonSerializer.Serialize(
                    response,
                    CartonCoreJsonContext.Default.WindowsHelperActionResponse);
                var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
                File.WriteAllText(tempPath, payload, new UTF8Encoding(false));
                File.Move(tempPath, path, overwrite: true);
            }
            catch
            {
            }
        }

        bool IsParentAlive()
        {
            if (parentPid <= 0)
            {
                return true;
            }

            try
            {
                using var parent = Process.GetProcessById(parentPid);
                return !parent.HasExited;
            }
            catch
            {
                return false;
            }
        }

        var parentWatchdogTask = parentPid > 0
            ? Task.Run(async () =>
            {
                while (!shouldStop)
                {
                    // Check stop signal file (file-based IPC, bypasses TUN)
                    try
                    {
                        if (File.Exists(stopSignalPath))
                        {
                            File.Delete(stopSignalPath);
                            _ = StopSingBox(force: true);
                            // DON'T exit helper - keep it alive for next start
                        }
                    }
                    catch { }

                    if (!IsParentAlive())
                    {
                        _ = StopSingBox(force: true);
                        shouldStop = true;
                        try { listener.Stop(); } catch { }
                        break;
                    }

                    await Task.Delay(500);
                }
            })
            : null;

        async Task<WindowsHelperProcessStatusResponse> GetProcessStatusAsync()
        {
            int? pid;
            int? exitCode;
            string? error;
            bool hasProcess;
            bool isRunning;
            string? apiAddress;
            string? apiSecret;

            lock (processLock)
            {
                if (singBoxProcess != null)
                {
                    try
                    {
                        if (!singBoxProcess.HasExited)
                        {
                            pid = singBoxProcess.Id;
                            exitCode = null;
                            error = lastKnownError;
                            hasProcess = true;
                            isRunning = true;
                            apiAddress = lastApiAddress;
                            apiSecret = lastApiSecret;
                            goto BuildResponse;
                        }

                        lastKnownPid = singBoxProcess.Id;
                        lastKnownExitCode = singBoxProcess.ExitCode;
                    }
                    catch
                    {
                    }
                }

                pid = lastKnownPid;
                exitCode = lastKnownExitCode;
                error = lastKnownError;
                hasProcess = lastKnownPid.HasValue || !string.IsNullOrWhiteSpace(lastKnownError);
                isRunning = false;
                apiAddress = lastApiAddress;
                apiSecret = lastApiSecret;
            }

        BuildResponse:
            var apiReady = isRunning && await IsApiReadyAsync(apiAddress, apiSecret);
            return new WindowsHelperProcessStatusResponse
            {
                HasProcess = hasProcess,
                IsRunning = isRunning,
                ApiReady = apiReady,
                Pid = pid,
                ExitCode = exitCode,
                Error = error
            };
        }

        WindowsHelperActionResponse StopSingBox(bool force, int? fallbackPid = null)
        {
            lock (processLock)
            {
                try
                {
                    startupWatchCts?.Cancel();
                    startupWatchCts = null;
                    if (singBoxProcess != null)
                    {
                        lastKnownPid = singBoxProcess.Id;
                        if (!singBoxProcess.HasExited)
                        {
                            singBoxProcess.Kill(entireProcessTree: true);
                            if (!singBoxProcess.WaitForExit(force ? 1500 : 3000))
                            {
                                return new WindowsHelperActionResponse
                                {
                                    Success = false,
                                    Error = $"sing-box process {lastKnownPid} did not exit after kill"
                                };
                            }
                        }

                        lastKnownExitCode = singBoxProcess.ExitCode;

                        singBoxProcess.Dispose();
                        singBoxProcess = null;
                    }
                    else if (fallbackPid is > 0)
                    {
                        try
                        {
                            using var fallbackProcess = Process.GetProcessById(fallbackPid.Value);
                            if (!fallbackProcess.HasExited)
                            {
                                fallbackProcess.Kill(entireProcessTree: true);
                                if (!fallbackProcess.WaitForExit(force ? 1500 : 3000))
                                {
                                    return new WindowsHelperActionResponse
                                    {
                                        Success = false,
                                        Error = $"sing-box process {fallbackPid.Value} did not exit after kill"
                                    };
                                }
                            }

                            lastKnownPid = fallbackPid.Value;
                            lastKnownExitCode = null;
                        }
                        catch (ArgumentException)
                        {
                            lastKnownPid = fallbackPid.Value;
                            lastKnownExitCode = null;
                        }
                    }
                }
                catch (Exception ex)
                {
                    return new WindowsHelperActionResponse { Success = false, Error = ex.Message };
                }
                finally
                {
                    try
                    {
                        logWriter?.Dispose();
                        logWriter = null;
                    }
                    catch
                    {
                    }
                }

                return new WindowsHelperActionResponse { Success = true };
            }
        }

        WindowsHelperActionResponse StartSingBox(WindowsHelperStartRequest request)
        {
            var recentLogLock = new object();
            var recentLogLines = new Queue<string>();

            void RememberLogLine(string line)
            {
                var normalized = StripTerminalDecorations(line);
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    return;
                }

                lock (recentLogLock)
                {
                    recentLogLines.Enqueue(normalized.Trim());
                    while (recentLogLines.Count > 20)
                    {
                        recentLogLines.Dequeue();
                    }
                }
            }

            string? GetRecentLogSnapshot()
            {
                lock (recentLogLock)
                {
                    if (recentLogLines.Count == 0)
                    {
                        return null;
                    }

                    return string.Join(" | ", recentLogLines);
                }
            }

            void WriteStartResult(WindowsHelperActionResponse response)
            {
                WriteResponseFile(request.ResultFilePath, response);
            }

            WindowsHelperActionResponse ReturnStartResult(WindowsHelperActionResponse response)
            {
                WriteStartResult(response);
                return response;
            }

            if (!File.Exists(request.SingBoxPath))
            {
                var response = new WindowsHelperActionResponse
                {
                    Success = false,
                    Error = $"sing-box binary not found: {request.SingBoxPath}"
                };
                lastApiAddress = request.ApiAddress;
                lastApiSecret = request.ApiSecret;
                lastKnownPid = null;
                lastKnownExitCode = null;
                lastKnownError = response.Error;
                return ReturnStartResult(response);
            }

            if (!File.Exists(request.ConfigPath))
            {
                var response = new WindowsHelperActionResponse
                {
                    Success = false,
                    Error = $"config file not found: {request.ConfigPath}"
                };
                lastApiAddress = request.ApiAddress;
                lastApiSecret = request.ApiSecret;
                lastKnownPid = null;
                lastKnownExitCode = null;
                lastKnownError = response.Error;
                return ReturnStartResult(response);
            }

            lock (processLock)
            {
                _ = StopSingBox(force: true);

                try
                {
                    lastApiAddress = request.ApiAddress;
                    lastApiSecret = request.ApiSecret;
                    if (!string.IsNullOrWhiteSpace(request.LogPath))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(request.LogPath) ?? ".");
                        var stream = new FileStream(
                            request.LogPath,
                            FileMode.Append,
                            FileAccess.Write,
                            FileShare.ReadWrite | FileShare.Delete);
                        logWriter = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                    }

                    var process = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = request.SingBoxPath,
                            Arguments = $"run -c \"{request.ConfigPath}\"",
                            WorkingDirectory = request.WorkingDirectory,
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            RedirectStandardInput = true,
                            CreateNoWindow = true,
                            StandardOutputEncoding = Encoding.UTF8,
                            StandardErrorEncoding = Encoding.UTF8
                        }
                    };

                    process.Start();
                    singBoxProcess = process;
                    lastKnownPid = process.Id;
                    lastKnownExitCode = null;
                    lastKnownError = null;
                    stdoutTask = PumpStreamAsync(process.StandardOutput, logWriter, RememberLogLine);
                    stderrTask = PumpStreamAsync(process.StandardError, logWriter, RememberLogLine);

                    if (process.WaitForExit(300))
                    {
                        try
                        {
                            stdoutTask?.Wait(500);
                            stderrTask?.Wait(500);
                        }
                        catch
                        {
                        }

                        var recentLog = GetRecentLogSnapshot() ?? TryReadRecentLog(request.LogPath, 10);
                        var response = new WindowsHelperActionResponse
                        {
                            Success = false,
                            Error = string.IsNullOrWhiteSpace(recentLog)
                                ? $"sing-box exited with code {process.ExitCode}"
                                : recentLog
                        };
                        lastKnownExitCode = process.ExitCode;
                        lastKnownError = response.Error;
                        return ReturnStartResult(response);
                    }

                    if (process.HasExited)
                    {
                        var recentLog = GetRecentLogSnapshot() ?? TryReadRecentLog(request.LogPath, 10);
                        var response = new WindowsHelperActionResponse
                        {
                            Success = false,
                            Error = string.IsNullOrWhiteSpace(recentLog)
                                ? $"sing-box exited with code {process.ExitCode}"
                                : recentLog
                        };
                        lastKnownExitCode = process.ExitCode;
                        lastKnownError = response.Error;
                        return ReturnStartResult(response);
                    }

                    startupWatchCts = new CancellationTokenSource();
                    startupWatchTask = WatchStartupExitAsync(
                        process,
                        () => GetRecentLogSnapshot() ?? TryReadRecentLog(request.LogPath, 12),
                        (exitCode, recentLog) =>
                        {
                            lock (processLock)
                            {
                                lastKnownPid = process.Id;
                                lastKnownExitCode = exitCode;
                                lastKnownError = string.IsNullOrWhiteSpace(recentLog)
                                    ? $"sing-box exited with code {exitCode} before API became ready"
                                    : recentLog;
                            }
                        },
                        startupWatchCts.Token);

                    return ReturnStartResult(new WindowsHelperActionResponse { Success = true, Pid = process.Id });
                }
                catch (Exception ex)
                {
                    var response = new WindowsHelperActionResponse { Success = false, Error = ex.Message };
                    lastKnownPid = null;
                    lastKnownExitCode = null;
                    lastKnownError = response.Error;
                    return ReturnStartResult(response);
                }
            }
        }

        while (!shouldStop)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            try
            {
                if (!string.Equals(context.Request.Headers[TokenHeader], token, StringComparison.Ordinal))
                {
                    await WriteTextAsync(context.Response, HttpStatusCode.Unauthorized, "unauthorized");
                    continue;
                }

                var path = context.Request.Url?.AbsolutePath?.Trim('/').ToLowerInvariant() ?? string.Empty;
                switch (path)
                {
                    case "ping":
                        await WriteTextAsync(context.Response, HttpStatusCode.OK, token);
                        break;
                    case "status":
                        await WriteProcessStatusAsync(context.Response, HttpStatusCode.OK, await GetProcessStatusAsync());
                        break;
                    case "start":
                    {
                        if (!string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
                        {
                            await WriteTextAsync(context.Response, HttpStatusCode.MethodNotAllowed, "method not allowed");
                            break;
                        }

                        using var reader = new StreamReader(
                            context.Request.InputStream,
                            context.Request.ContentEncoding ?? Encoding.UTF8);
                        var payload = await reader.ReadToEndAsync();
                        var request = JsonSerializer.Deserialize(
                            payload,
                            CartonCoreJsonContext.Default.WindowsHelperStartRequest);
                        if (request == null)
                        {
                            await WriteJsonAsync(
                                context.Response,
                                HttpStatusCode.BadRequest,
                                new WindowsHelperActionResponse { Success = false, Error = "invalid payload" });
                            break;
                        }

                        var startResult = StartSingBox(request);
                        await WriteJsonAsync(context.Response, HttpStatusCode.OK, startResult);
                        break;
                    }
                    case "stop":
                    {
                        var force = string.Equals(
                            context.Request.QueryString["force"],
                            "1",
                            StringComparison.Ordinal);
                        var fallbackPid = int.TryParse(context.Request.QueryString["pid"], out var parsedPid)
                            ? parsedPid
                            : (int?)null;
                        var stopResult = StopSingBox(force, fallbackPid);
                        await WriteJsonAsync(context.Response, HttpStatusCode.OK, stopResult);
                        break;
                    }
                    case "shutdown":
                    {
                        var shutdownResult = StopSingBox(force: true);
                        await WriteJsonAsync(context.Response, HttpStatusCode.OK, shutdownResult);
                        shouldStop = true;
                        break;
                    }
                    default:
                        await WriteTextAsync(context.Response, HttpStatusCode.NotFound, "not found");
                        break;
                }
            }
            catch (Exception ex)
            {
                await WriteTextAsync(context.Response, HttpStatusCode.InternalServerError, ex.Message);
            }
        }

        _ = StopSingBox(force: true);
        try
        {
            if (stdoutTask != null)
            {
                await stdoutTask;
            }
        }
        catch
        {
        }

        try
        {
            if (stderrTask != null)
            {
                await stderrTask;
            }
        }
        catch
        {
        }

        try
        {
            startupWatchCts?.Cancel();
            if (startupWatchTask != null)
            {
                await startupWatchTask;
            }
        }
        catch
        {
        }

        try
        {
            if (parentWatchdogTask != null)
            {
                await parentWatchdogTask;
            }
        }
        catch
        {
        }
    }

    private static async Task PumpStreamAsync(StreamReader reader, StreamWriter? writer, Action<string>? onLine = null)
    {
        try
        {
            while (true)
            {
                var line = await reader.ReadLineAsync();
                if (line == null)
                {
                    break;
                }

                onLine?.Invoke(line);
                if (writer == null)
                {
                    continue;
                }

                lock (writer)
                {
                    writer.WriteLine(line);
                }
            }
        }
        catch
        {
        }
    }

    private static async Task WatchStartupExitAsync(
        Process process,
        Func<string?> getRecentLog,
        Action<int, string?> onExited,
        CancellationToken cancellationToken)
    {
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(35);
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (process.HasExited)
                {
                    await Task.Delay(150, cancellationToken);
                    var recentLog = getRecentLog();
                    onExited(process.ExitCode, recentLog);
                    return;
                }

                await Task.Delay(200, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
    }

    private static string? TryReadRecentLog(string path, int maxLines)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var lines = File.ReadAllLines(path);
            if (lines.Length == 0)
            {
                return null;
            }

            var slice = lines.Skip(Math.Max(0, lines.Length - maxLines));
            return string.Join(" | ", slice
                .Select(StripTerminalDecorations)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => line.Trim()));
        }
        catch
        {
            return null;
        }
    }

    private static string StripTerminalDecorations(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(message.Length);
        for (var i = 0; i < message.Length; i++)
        {
            var ch = message[i];
            if (ch == '\u001b' && i + 1 < message.Length && message[i + 1] == '[')
            {
                var endIndex = FindCsiTerminator(message, i + 2);
                if (endIndex >= 0)
                {
                    i = endIndex;
                    continue;
                }
            }

            if (ch == '[' && i + 1 < message.Length && IsCsiParameterChar(message[i + 1]))
            {
                var endIndex = FindCsiTerminator(message, i + 1);
                if (endIndex >= 0)
                {
                    i = endIndex;
                    continue;
                }
            }

            if (!char.IsControl(ch) || ch is '\r' or '\n' or '\t')
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }

    private static int FindCsiTerminator(string message, int startIndex)
    {
        for (var i = startIndex; i < message.Length; i++)
        {
            var ch = message[i];
            if ((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z'))
            {
                return i;
            }

            if (!IsCsiParameterChar(ch))
            {
                return -1;
            }
        }

        return -1;
    }

    private static bool IsCsiParameterChar(char ch)
    {
        return (ch >= '0' && ch <= '9') || ch == ';';
    }

    private static async Task WriteTextAsync(HttpListenerResponse response, HttpStatusCode statusCode, string text)
    {
        try
        {
            response.StatusCode = (int)statusCode;
            response.ContentType = "text/plain; charset=utf-8";
            await using var writer = new StreamWriter(response.OutputStream, Encoding.UTF8, 1024, leaveOpen: true);
            await writer.WriteAsync(text);
        }
        finally
        {
            response.Close();
        }
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, HttpStatusCode statusCode, WindowsHelperActionResponse payload)
    {
        try
        {
            response.StatusCode = (int)statusCode;
            response.ContentType = "application/json; charset=utf-8";
            await using var writer = new Utf8JsonWriter(response.OutputStream);
            JsonSerializer.Serialize(writer, payload, CartonCoreJsonContext.Default.WindowsHelperActionResponse);
            await writer.FlushAsync();
        }
        finally
        {
            response.Close();
        }
    }

    private static async Task WriteProcessStatusAsync(
        HttpListenerResponse response,
        HttpStatusCode statusCode,
        WindowsHelperProcessStatusResponse payload)
    {
        try
        {
            response.StatusCode = (int)statusCode;
            response.ContentType = "application/json; charset=utf-8";
            await using var writer = new Utf8JsonWriter(response.OutputStream);
            JsonSerializer.Serialize(writer, payload, CartonCoreJsonContext.Default.WindowsHelperProcessStatusResponse);
            await writer.FlushAsync();
        }
        finally
        {
            response.Close();
        }
    }

    private static async Task<bool> IsApiReadyAsync(string? apiAddress, string? apiSecret)
    {
        if (string.IsNullOrWhiteSpace(apiAddress))
        {
            return false;
        }

        try
        {
            using var client = HttpClientFactory.CreateLoopbackClient(TimeSpan.FromSeconds(1));
            if (!string.IsNullOrWhiteSpace(apiSecret))
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiSecret);
            }

            using var response = await client.GetAsync($"{apiAddress.TrimEnd('/')}/version");
            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            return response.StatusCode is HttpStatusCode.NotFound
                or HttpStatusCode.Unauthorized
                or HttpStatusCode.Forbidden
                or HttpStatusCode.MethodNotAllowed;
        }
        catch
        {
            return false;
        }
    }
}
