using System.Diagnostics;
using System.Runtime.InteropServices;

namespace carton.Core.Services;

public partial class SingBoxManager
{
    public async Task<bool> IsLinuxCoreAuthorizedAsync()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || !File.Exists(_singBoxPath))
            return false;

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "stat",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };
            process.StartInfo.ArgumentList.Add("-c");
            process.StartInfo.ArgumentList.Add("%U:%G %A");
            process.StartInfo.ArgumentList.Add(_singBoxPath);

            process.Start();
            var output = (await process.StandardOutput.ReadToEndAsync()).Trim();
            await process.WaitForExitAsync();

            // Expected: "root:root -rwsr-xr-x" — owner is root and setuid bit is set
            return output.StartsWith("root:") && output.Contains("rws");
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
            var command = $"sudo -S chown root:root {QuoteShellArg(_singBoxPath)} && sudo -S chmod +sx {QuoteShellArg(_singBoxPath)}";

            var startInfo = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(command);

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            await process.StandardInput.WriteLineAsync(password);
            await process.StandardInput.FlushAsync();
            process.StandardInput.Close();

            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                var filteredError = string.Join('\n', error.Split('\n')
                    .Where(line => !line.Contains("[sudo]") && !string.IsNullOrWhiteSpace(line)));
                return (false, string.IsNullOrWhiteSpace(filteredError) ? "Authorization failed" : filteredError.Trim());
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private async Task<ElevatedStartResult> StartElevatedOnLinuxAsync(string configPath, string logPath)
    {
        var shellCommand =
            $"{QuoteShellArg(_singBoxPath)} run -c {QuoteShellArg(configPath)} < /dev/null >> {QuoteShellArg(logPath)} 2>&1 & echo $!";

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
        await process.WaitForExitAsync();
    }
}
