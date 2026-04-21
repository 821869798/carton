using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using carton.Core.Utilities;

namespace carton.GUI.Services;

public static class SingleInstanceService
{
    private static Mutex? _mutex;
    private static FileStream? _lockFileStream;
    private static bool _ownsMutex;
    private static string _mutexName = string.Empty;
    private static string _pipeName = string.Empty;
    private static CancellationTokenSource? _listenerCts;
    private static Window? _mainWindow;

    public static bool TryClaim(string instanceKey)
    {
        _mutexName = BuildMutexName(instanceKey);
        _pipeName = BuildPipeName(instanceKey);

        if (!TryAcquireSingleInstanceLock(instanceKey))
        {
            return false;
        }

        _listenerCts = new CancellationTokenSource();
        _ = Task.Run(() => ListenForSignalsAsync(_listenerCts.Token));
        return true;
    }

    public static void NotifyExistingInstance()
    {
        try
        {
            NotifyExistingInstanceAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // ignored
        }
    }

    public static void RegisterMainWindow(Window? window)
    {
        _mainWindow = window;
    }

    public static void Dispose()
    {
        _listenerCts?.Cancel();
        _listenerCts = null;

        if (_lockFileStream != null)
        {
            try
            {
                _lockFileStream.Dispose();
            }
            catch
            {
                // ignored
            }

            _lockFileStream = null;
        }

        if (_ownsMutex && _mutex != null)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch
            {
                // ignored
            }
        }

        _mutex?.Dispose();
        _mutex = null;
        _ownsMutex = false;
    }

    private static string BuildMutexName(string instanceKey)
    {
        if (OperatingSystem.IsWindows())
        {
            return $@"Global\carton-{instanceKey}";
        }

        return $"carton-{instanceKey}";
    }

    private static string BuildPipeName(string instanceKey)
    {
        return $"carton-{instanceKey}-pipe";
    }

    private static bool TryAcquireSingleInstanceLock(string instanceKey)
    {
        if (OperatingSystem.IsWindows())
        {
            _mutex = new Mutex(true, _mutexName, out _ownsMutex);
            return _ownsMutex;
        }

        return TryAcquireFileLock(instanceKey);
    }

    private static bool TryAcquireFileLock(string instanceKey)
    {
        try
        {
            var lockDirectory = Path.Combine(PathHelper.GetAppDataPath(), ".locks");
            Directory.CreateDirectory(lockDirectory);
            var lockFilePath = Path.Combine(lockDirectory, $"carton-{instanceKey}.lock");
            _lockFileStream = new FileStream(lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            _ownsMutex = true;
            return true;
        }
        catch (IOException)
        {
            _ownsMutex = false;
            _lockFileStream = null;
            return false;
        }
    }

    private static async Task NotifyExistingInstanceAsync()
    {
        using var client = new NamedPipeClientStream(
            ".",
            _pipeName,
            PipeDirection.Out,
            PipeOptions.Asynchronous);

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await client.ConnectAsync(cts.Token).ConfigureAwait(false);
            var buffer = new byte[] { 1 };
            await client.WriteAsync(buffer.AsMemory(0, 1), cts.Token).ConfigureAwait(false);
            await client.FlushAsync(cts.Token).ConfigureAwait(false);
        }
        catch
        {
            // ignored
        }
    }

    private static async Task ListenForSignalsAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                server.ReadByte();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch
            {
            }

            if (token.IsCancellationRequested)
            {
                break;
            }

            if (_mainWindow != null)
            {
                Dispatcher.UIThread.Post(() => BringToFront(_mainWindow));
            }
        }
    }

    private static void BringToFront(Window window)
    {
        if (!window.IsVisible)
        {
            window.Show();
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
        window.Focus();
        window.Topmost = true;
        window.Topmost = false;
#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            var handle = window.TryGetPlatformHandle();
            if (handle?.Handle != IntPtr.Zero)
            {
                NativeMethods.ShowWindow(handle.Handle, NativeMethods.SW_RESTORE);
                NativeMethods.SetForegroundWindow(handle.Handle);
            }
        }
#endif
    }

#if WINDOWS
    private static class NativeMethods
    {
        public const int SW_RESTORE = 9;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    }
#endif
}
