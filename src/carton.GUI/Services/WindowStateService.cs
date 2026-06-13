using System;
using System.IO;
using System.Text.Json;
using carton.Core.Utilities;
using carton.GUI.Serialization;

namespace carton.GUI.Services;

public interface IWindowStateService
{
    MainWindowState? LoadMainWindowState();
    void SaveMainWindowState(MainWindowState state, long sequence = 0);
    void DeleteMainWindowState(long sequence = 0);
}

public sealed class WindowStateService : IWindowStateService
{
    private const string CacheDirectoryName = "cache";
    private const string MainWindowStateFileName = "window-state.json";

    private readonly string _statePath;
    private readonly object _saveLock = new();
    private long _lastWrittenSequence;

    public WindowStateService()
    {
        var cacheDirectory = Path.Combine(PathHelper.GetRoamingAppDataPath(), CacheDirectoryName);
        _statePath = Path.Combine(cacheDirectory, MainWindowStateFileName);
    }

    public MainWindowState? LoadMainWindowState()
    {
        try
        {
            if (!File.Exists(_statePath))
            {
                return null;
            }

            var json = File.ReadAllText(_statePath);
            return JsonSerializer.Deserialize(json, CartonGuiJsonContext.Default.MainWindowState);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // Writes are serialized and ordered by <paramref name="sequence"/>: a slower, older save can
    // never overwrite a newer one regardless of which thread finishes first. Pass a monotonically
    // increasing sequence per logical save; sequence 0 means "write unconditionally" (no ordering).
    public void SaveMainWindowState(MainWindowState state, long sequence = 0)
    {
        ArgumentNullException.ThrowIfNull(state);

        lock (_saveLock)
        {
            if (sequence != 0 && sequence < _lastWrittenSequence)
            {
                return;
            }

            try
            {
                var directory = Path.GetDirectoryName(_statePath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var json = JsonSerializer.Serialize(state, CartonGuiJsonContext.Default.MainWindowState);
                File.WriteAllText(_statePath, json);

                if (sequence != 0)
                {
                    _lastWrittenSequence = sequence;
                }
            }
            catch (Exception)
            {
                // Window placement is a best-effort local cache and should not block shutdown.
            }
        }
    }

    public void DeleteMainWindowState(long sequence = 0)
    {
        lock (_saveLock)
        {
            if (sequence != 0 && sequence < _lastWrittenSequence)
            {
                return;
            }

            try
            {
                if (File.Exists(_statePath))
                {
                    File.Delete(_statePath);
                }
            }
            catch (Exception)
            {
                // Window placement is a best-effort local cache and should not block settings changes.
            }

            if (sequence != 0)
            {
                _lastWrittenSequence = sequence;
            }
        }
    }
}

public sealed class MainWindowState
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public MainWindowSavedState State { get; set; } = MainWindowSavedState.Normal;
}

public enum MainWindowSavedState
{
    Normal = 0,
    Maximized = 1
}
