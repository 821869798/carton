using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using carton.Core.Models;
using carton.Core.Serialization;

namespace carton.Core.Services;

public static class WindowsElevatedHelperHost
{
    private const string HelperArg = "--carton-elevated-helper";
    private const string TokenHeader = "X-Carton-Helper-Token";

    public static bool TryRunFromArgs(string[] args)
    {
        if (!OperatingSystem.IsWindows() ||
            args.Length == 0 ||
            !string.Equals(args[0], HelperArg, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var port = 0;
        var parentPid = 0;
        string? token = null;
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
            }
        }

        if (port <= 0 || string.IsNullOrWhiteSpace(token))
        {
            return true;
        }

        RunAsync(port, token, parentPid).GetAwaiter().GetResult();
        return true;
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
        var processLock = new object();
        var shouldStop = false;

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

        WindowsHelperActionResponse StopSingBox(bool force)
        {
            lock (processLock)
            {
                try
                {
                    if (singBoxProcess != null)
                    {
                        if (!singBoxProcess.HasExited)
                        {
                            singBoxProcess.Kill(force ? true : true);
                            singBoxProcess.WaitForExit(5000);
                        }

                        singBoxProcess.Dispose();
                        singBoxProcess = null;
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
            if (!File.Exists(request.SingBoxPath))
            {
                return new WindowsHelperActionResponse
                {
                    Success = false,
                    Error = $"sing-box binary not found: {request.SingBoxPath}"
                };
            }

            if (!File.Exists(request.ConfigPath))
            {
                return new WindowsHelperActionResponse
                {
                    Success = false,
                    Error = $"config file not found: {request.ConfigPath}"
                };
            }

            lock (processLock)
            {
                _ = StopSingBox(force: true);

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(request.LogPath) ?? ".");
                    var stream = new FileStream(
                        request.LogPath,
                        FileMode.Append,
                        FileAccess.Write,
                        FileShare.ReadWrite | FileShare.Delete);
                    logWriter = new StreamWriter(stream) { AutoFlush = true };

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
                            CreateNoWindow = true
                        }
                    };

                    process.Start();
                    singBoxProcess = process;
                    stdoutTask = PumpStreamAsync(process.StandardOutput, logWriter);
                    stderrTask = PumpStreamAsync(process.StandardError, logWriter);

                    if (process.HasExited)
                    {
                        return new WindowsHelperActionResponse
                        {
                            Success = false,
                            Error = $"sing-box exited with code {process.ExitCode}"
                        };
                    }

                    return new WindowsHelperActionResponse { Success = true, Pid = process.Id };
                }
                catch (Exception ex)
                {
                    return new WindowsHelperActionResponse { Success = false, Error = ex.Message };
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
                        var stopResult = StopSingBox(force);
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
            if (parentWatchdogTask != null)
            {
                await parentWatchdogTask;
            }
        }
        catch
        {
        }
    }

    private static async Task PumpStreamAsync(StreamReader reader, StreamWriter writer)
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

    private static async Task WriteTextAsync(HttpListenerResponse response, HttpStatusCode statusCode, string text)
    {
        response.StatusCode = (int)statusCode;
        response.ContentType = "text/plain; charset=utf-8";
        await using var writer = new StreamWriter(response.OutputStream, Encoding.UTF8, 1024, leaveOpen: false);
        await writer.WriteAsync(text);
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, HttpStatusCode statusCode, WindowsHelperActionResponse payload)
    {
        response.StatusCode = (int)statusCode;
        response.ContentType = "application/json; charset=utf-8";
        await using var writer = new Utf8JsonWriter(response.OutputStream);
        JsonSerializer.Serialize(writer, payload, CartonCoreJsonContext.Default.WindowsHelperActionResponse);
        await writer.FlushAsync();
    }
}
