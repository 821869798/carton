using System;
using System.Reflection;

namespace carton.Core.Utilities;

public static class CartonApplicationInfo
{
    private const string DefaultVersion = "0.0.0";
    public const string DefaultSingBoxVersion = "1.13.0";
    private static readonly Lazy<string> VersionLazy = new(ResolveVersion);
    private static readonly object SingBoxVersionLock = new();
    private static string? _singBoxVersion;
    private static event Action<string?>? SingBoxVersionChangedHandler;

    public static string Version => VersionLazy.Value;
    public static string? SingBoxVersion => Volatile.Read(ref _singBoxVersion);

    public static void SetSingBoxVersion(string? version)
    {
        var normalized = NormalizeSingBoxVersion(version);

        Action<string?>? listeners;
        lock (SingBoxVersionLock)
        {
            if (string.Equals(_singBoxVersion, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _singBoxVersion = normalized;
            listeners = SingBoxVersionChangedHandler;
        }

        listeners?.Invoke(normalized);
    }

    public static event Action<string?> SingBoxVersionChanged
    {
        add
        {
            lock (SingBoxVersionLock)
            {
                SingBoxVersionChangedHandler += value;
            }
        }
        remove
        {
            lock (SingBoxVersionLock)
            {
                SingBoxVersionChangedHandler -= value;
            }
        }
    }

    private static string? NormalizeSingBoxVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var trimmed = version.Trim();
        if (string.Equals(trimmed, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[1..];
        }

        return trimmed;
    }

    private static string ResolveVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        if (assembly == null)
        {
            return DefaultVersion;
        }

        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plusIndex = informational.IndexOf('+');
            var sanitized = plusIndex >= 0 ? informational[..plusIndex] : informational;
            if (!string.IsNullOrWhiteSpace(sanitized))
            {
                return sanitized;
            }
        }

        return assembly.GetName().Version?.ToString() ?? DefaultVersion;
    }
}
