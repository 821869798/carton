using System.Diagnostics;
using System.Runtime.InteropServices;

namespace carton.Core.Services;

public partial class SingBoxManager
{
    /// <summary>
    /// Mode a TUN-capable kernel needs. Spelled out instead of a symbolic "chmod +sx" so the
    /// outcome never depends on the mode the copy happened to land with, nor on the umask of
    /// whatever session launched the app. 6755 is what the previous "+sx" actually produced
    /// ('+s' sets both u+s and g+s), so a kernel authorized by an older build still counts as
    /// authorized by <see cref="IsLinuxCoreAuthorizedAsync"/>.
    /// </summary>
    private const string LinuxAuthorizedKernelMode = "6755";

    public async Task<bool> IsLinuxCoreAuthorizedAsync()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || !File.Exists(_singBoxPath))
            return false;

        try
        {
            var result = await RunCapturedCommandAsync("stat", ["-c", "%U:%G %A", _singBoxPath]);

            // Expected: "root:root -rwsr-sr-x" — owner is root and setuid bit is set
            return result.Output.StartsWith("root:", StringComparison.Ordinal)
                && result.Output.Contains("rws", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    public async Task<(bool Success, string? Error)> AuthorizeCoreOnLinuxAsync(string password)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return (false, "Not running on Linux");

        if (!File.Exists(_singBoxPath))
            return (false, $"sing-box binary not found: {_singBoxPath}");

        try
        {
            // chown and chmod have to run inside a single sudo call. "sudo -S" reads the
            // password from stdin and only one password line is ever written, so a second
            // "sudo -S" reads EOF and authenticates with an empty password — PAM logs
            // "auth could not identify password" and the chmod never happens. That left the
            // kernel owned by root without the setuid bit, which is strictly worse than
            // before: the kernel cannot open the TUN device and the file is no longer
            // writable by the user. It only ever appeared to work where sudo's credential
            // cache happened to carry the first authentication over to the second call.
            var result = await RunCapturedCommandAsync(
                "sudo",
                BuildLinuxAuthorizationArguments(_singBoxPath),
                stdin: password);

            if (result.ExitCode != 0)
            {
                return (false, FormatSudoFailure(result.ExitCode, result.Error));
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// The full sudo argv. One "sudo -S" reads the single password line from stdin and the
    /// privileged command follows after "--", so the elevated shell can never be handed a
    /// second sudo that would find stdin already at EOF. "-p ''" silences the prompt, keeping
    /// stderr free for real failures.
    /// </summary>
    internal static string[] BuildLinuxAuthorizationArguments(string kernelPath) =>
        ["-S", "-p", string.Empty, "--", "/bin/sh", "-c", BuildLinuxAuthorizationCommand(kernelPath)];

    /// <summary>
    /// The single privileged command the authorization runs. Both steps live in one string so
    /// they share one authentication, and the mode is explicit so the setuid bit cannot be
    /// lost to a umask. Must never contain "sudo" itself: the caller supplies exactly one.
    /// </summary>
    internal static string BuildLinuxAuthorizationCommand(string kernelPath)
    {
        var quotedPath = QuoteShellArg(kernelPath);
        return $"chown root:root {quotedPath} && chmod {LinuxAuthorizedKernelMode} {quotedPath}";
    }

    /// <summary>
    /// Builds the message shown when sudo refuses. sudo exits non-zero with an empty stderr
    /// often enough (a rejected password under a silent PAM stack) that the exit code is kept
    /// as the fallback instead of a bare "Authorization failed".
    /// </summary>
    internal static string FormatSudoFailure(int exitCode, string error)
    {
        var detail = string.Join(" / ", error
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith("[sudo]", StringComparison.Ordinal)));

        return string.IsNullOrWhiteSpace(detail)
            ? $"sudo exited with code {exitCode} without a message (most often a rejected password)"
            : $"sudo exited with code {exitCode}: {detail}";
    }

    /// <summary>
    /// Runs a command with both output pipes drained before the wait. Reading one pipe to the
    /// end while the other fills up would block the child process and deadlock the wait.
    /// </summary>
    private static async Task<(int ExitCode, string Output, string Error)> RunCapturedCommandAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? stdin = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardInput = stdin != null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        if (stdin != null)
        {
            await process.StandardInput.WriteLineAsync(stdin);
            await process.StandardInput.FlushAsync();
            process.StandardInput.Close();
        }

        var output = await outputTask;
        var error = await errorTask;
        await process.WaitForExitAsync();

        return (process.ExitCode, output.Trim(), error.Trim());
    }

    private async Task<ElevatedStartResult> StartElevatedOnLinuxAsync(string configPath, string logPath)
    {
        var outputRedirect = string.IsNullOrWhiteSpace(logPath)
            ? "> /dev/null 2>&1"
            : $">> {QuoteShellArg(logPath)} 2>&1";
        var shellCommand =
            $"{BuildLinuxLibrarySearchPathPrefix()}{QuoteShellArg(_singBoxPath)} run -c {QuoteShellArg(configPath)} < /dev/null {outputRedirect} & echo $!";

        var startInfo = new ProcessStartInfo
        {
            FileName = "pkexec",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("/bin/sh");
        startInfo.ArgumentList.Add("-lc");
        startInfo.ArgumentList.Add(shellCommand);

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new ElevatedStartResult
            {
                Success = false,
                ErrorMessage = $"pkexec unavailable: {ex.Message}"
            };
        }

        var output = (await process.StandardOutput.ReadToEndAsync()).Trim();
        var error = (await process.StandardError.ReadToEndAsync()).Trim();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            return new ElevatedStartResult
            {
                Success = false,
                ErrorMessage = string.IsNullOrWhiteSpace(error)
                    ? "Root permission denied or pkexec failed"
                    : error
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

    private static async Task RunPkexecCommandAsync(string shellCommand)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "pkexec",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("/bin/sh");
        startInfo.ArgumentList.Add("-lc");
        startInfo.ArgumentList.Add(shellCommand);

        using var process = new Process { StartInfo = startInfo };

        process.Start();

        // Both pipes are redirected, so both have to be drained: a child that fills one up
        // blocks on write and never exits, hanging the wait below.
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await outputTask;
        await errorTask;
        await process.WaitForExitAsync();
    }
}
