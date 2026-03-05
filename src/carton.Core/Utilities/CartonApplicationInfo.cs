using System;
using System.Reflection;

namespace carton.Core.Utilities;

public static class CartonApplicationInfo
{
    private const string DefaultVersion = "0.0.0";
    private const string SingBoxVersion = "1.13.0";
    private static readonly Lazy<string> VersionLazy = new(ResolveVersion);
    private static readonly Lazy<string> UserAgentLazy = new(() => BuildUserAgent(VersionLazy.Value));

    public static string Version => VersionLazy.Value;
    public static string UserAgent => UserAgentLazy.Value;

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

    private static string BuildUserAgent(string version)
    {
        var resolvedVersion = string.IsNullOrWhiteSpace(version) ? DefaultVersion : version.Trim();
        return $"carton/{resolvedVersion};sing-box/{SingBoxVersion}";
    }
}
