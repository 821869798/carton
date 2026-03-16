using System.Diagnostics;

namespace carton.Core.Services;

public partial class SingBoxManager
{
    private const string MacTunPermissionPrompt = "Carton requires administrator permission to start or stop the TUN interface (sing-box).";

    private async Task<ElevatedStartResult> StartElevatedOnMacAsync(string configPath, string logPath)
    {
        var shellCommand =
            $"{QuoteShellArg(_singBoxPath)} run -c {QuoteShellArg(configPath)} < /dev/null >> {QuoteShellArg(logPath)} 2>&1 & echo $!";
        var appleScript =
            $"do shell script \"{EscapeForAppleScript(shellCommand)}\" with prompt \"{EscapeForAppleScript(MacTunPermissionPrompt)}\" with administrator privileges";

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "osascript",
                Arguments = $"-e \"{EscapeForAppleScript(appleScript)}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        process.Start();
        var output = (await process.StandardOutput.ReadToEndAsync()).Trim();
        var error = (await process.StandardError.ReadToEndAsync()).Trim();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            return new ElevatedStartResult
            {
                Success = false,
                ErrorMessage = string.IsNullOrWhiteSpace(error) ? "Administrator permission denied or failed" : error
            };
        }

        if (!int.TryParse(output, out var pid))
        {
            return new ElevatedStartResult
            {
                Success = false,
                ErrorMessage = $"Failed to parse elevated PID: '{output}'"
            };
        }

        return new ElevatedStartResult { Success = true, Pid = pid };
    }

    private static async Task RunAppleScriptAdminCommandAsync(string shellCommand, string? prompt = null)
    {
        var promptClause = string.IsNullOrWhiteSpace(prompt)
            ? string.Empty
            : $" with prompt \"{EscapeForAppleScript(prompt)}\"";
        var appleScript = $"do shell script \"{EscapeForAppleScript(shellCommand)}\"{promptClause} with administrator privileges";
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "osascript",
                Arguments = $"-e \"{EscapeForAppleScript(appleScript)}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        process.Start();
        await process.WaitForExitAsync();
    }

    private static string EscapeForAppleScript(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
