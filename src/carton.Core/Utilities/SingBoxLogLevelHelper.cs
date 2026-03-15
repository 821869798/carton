namespace carton.Core.Utilities;

public static class SingBoxLogLevelHelper
{
    public const string DefaultLevel = "warn";

    public static IReadOnlyList<string> Levels { get; } =
    [
        "trace",
        "debug",
        "info",
        "warn",
        "error",
        "fatal",
        "panic"
    ];

    public static string Normalize(string? level)
    {
        return (level ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "trace" => "trace",
            "debug" => "debug",
            "info" => "info",
            "warn" => "warn",
            "warning" => "warn",
            "error" => "error",
            "fatal" => "fatal",
            "panic" => "panic",
            _ => DefaultLevel
        };
    }

    public static bool IsVerbose(string? level)
    {
        return Normalize(level) is "trace" or "debug" or "info";
    }
}
