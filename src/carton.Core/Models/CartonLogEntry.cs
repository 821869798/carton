namespace carton.Core.Models;

/// <summary>
/// Severity for carton's own manager logs. Numeric order matches increasing
/// severity so filters can compare directly (the same convention as the sing-box
/// LogLevel protobuf enum).
/// </summary>
public enum CartonLogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3
}

/// <summary>
/// Structured manager log entry: carries an explicit severity instead of a
/// "[WARN] "-style prefix that consumers had to parse back. The string prefix is
/// only materialized at the display/copy boundary.
/// </summary>
public readonly record struct CartonLogEntry(CartonLogLevel Level, string Message);
