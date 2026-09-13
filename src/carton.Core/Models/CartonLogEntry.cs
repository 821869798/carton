namespace carton.Core.Models;

/// <summary>
/// Severity for carton's own manager logs: Debug = 0 and higher values mean
/// MORE severe, so UI filters can compare ranks directly. Note this is the
/// OPPOSITE of the sing-box LogLevel protobuf enum, where Panic = 0 and higher
/// values mean more verbose; never compare the two numeric scales directly.
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
