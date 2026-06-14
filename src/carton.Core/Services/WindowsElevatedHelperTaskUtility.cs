using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;

namespace carton.Core.Services;

public readonly record struct WindowsElevatedHelperTaskRegistrationResult(
    bool Success,
    bool Cancelled,
    string? ErrorMessage = null);

public static class WindowsElevatedHelperTaskUtility
{
    public const string HelperArg = "--carton-elevated-helper";
    public const string HelperExecutableFileName = "carton-helper.exe";
    public const string RequestFileArg = "--request-file";

    private const string TaskNamePrefix = "Carton-ElevatedHelper";
    private const string TaskRequestFileName = "windows-elevated-helper-request.json";
    private const string TaskDefinitionFileName = "windows-elevated-helper-task.xml";

    public static bool IsSupported => OperatingSystem.IsWindows();

    public static string GetRequestFilePath(string workingDirectory)
    {
        return Path.Combine(workingDirectory, "cache", TaskRequestFileName);
    }

    public static string? ResolveHelperExecutablePath(string? appExecutablePath = null)
    {
        var executableDirectory = string.IsNullOrWhiteSpace(appExecutablePath)
            ? null
            : Path.GetDirectoryName(appExecutablePath);
        var helperPath = TryResolveHelperExecutablePath(executableDirectory);
        if (!string.IsNullOrWhiteSpace(helperPath))
        {
            return helperPath;
        }

        helperPath = TryResolveHelperExecutablePath(AppContext.BaseDirectory);
        if (!string.IsNullOrWhiteSpace(helperPath))
        {
            return helperPath;
        }

        return null;
    }

    [SupportedOSPlatform("windows")]
    public static string GetTaskNameForCurrentUser()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var sid = identity.User?.Value;
            if (!string.IsNullOrWhiteSpace(sid))
            {
                return $"{TaskNamePrefix}-{sid.Replace('-', '_')}";
            }
        }
        catch
        {
        }

        return TaskNamePrefix;
    }

    [SupportedOSPlatform("windows")]
    public static async Task<WindowsElevatedHelperTaskRegistrationResult> EnsureRegisteredAsync(
        string workingDirectory,
        string? executablePath = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new WindowsElevatedHelperTaskRegistrationResult(false, false, "Windows only");
        }

        executablePath = ResolveHelperExecutablePath(executablePath);

        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return new WindowsElevatedHelperTaskRegistrationResult(false, false, "Executable path unavailable");
        }

        var taskDefinitionPath = GetTaskDefinitionPath(workingDirectory);
        var requestFilePath = GetRequestFilePath(workingDirectory);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(taskDefinitionPath)!);
            var xml = BuildTaskDefinitionXml(executablePath, requestFilePath);
            await File.WriteAllTextAsync(taskDefinitionPath, xml, Encoding.Unicode);
        }
        catch (Exception ex)
        {
            return new WindowsElevatedHelperTaskRegistrationResult(
                false,
                false,
                $"Failed to prepare scheduled task definition: {ex.Message}");
        }

        try
        {
            var taskArgs =
                $"/Create /TN \"{EscapeForPowerShellSingleQuoted(GetTaskNameForCurrentUser())}\" /XML " +
                $"\"{EscapeForPowerShellSingleQuoted(taskDefinitionPath)}\" /F";
            var script =
                "$p = Start-Process -FilePath 'schtasks.exe' -ArgumentList " +
                $"'{EscapeForPowerShellSingleQuoted(taskArgs)}' " +
                "-Verb RunAs -WindowStyle Hidden -Wait -PassThru; " +
                "if ($p) { exit $p.ExitCode }";
            var result = await RunWindowsUacCommandAsync(script);
            return new WindowsElevatedHelperTaskRegistrationResult(
                result.Success,
                result.Cancelled,
                result.ErrorMessage);
        }
        finally
        {
            TryDeleteFile(taskDefinitionPath);
        }
    }

    [SupportedOSPlatform("windows")]
    public static async Task<bool> RunTaskAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var result = await RunProcessAsync(
            "schtasks.exe",
            $"/Run /TN \"{GetTaskNameForCurrentUser()}\"");
        return result.Success;
    }

    [SupportedOSPlatform("windows")]
    public static async Task<bool> IsRegistrationCurrentAsync(string workingDirectory, string executablePath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var xmlResult = await RunProcessAsync(
            "schtasks.exe",
            $"/Query /TN \"{GetTaskNameForCurrentUser()}\" /XML");
        if (!xmlResult.Success || string.IsNullOrWhiteSpace(xmlResult.ErrorMessage))
        {
            return false;
        }

        try
        {
            var document = XDocument.Parse(xmlResult.ErrorMessage);
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

            var command = document.Descendants(ns + "Command").FirstOrDefault()?.Value?.Trim();
            var arguments = document.Descendants(ns + "Arguments").FirstOrDefault()?.Value?.Trim();
            var taskWorkingDirectory = document.Descendants(ns + "WorkingDirectory").FirstOrDefault()?.Value?.Trim();

            if (string.IsNullOrWhiteSpace(command) || string.IsNullOrWhiteSpace(arguments))
            {
                return false;
            }

            var expectedExecutablePath = Path.GetFullPath(executablePath);
            var actualExecutablePath = Path.GetFullPath(command);
            if (!string.Equals(actualExecutablePath, expectedExecutablePath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var expectedRequestFilePath = Path.GetFullPath(GetRequestFilePath(workingDirectory));
            var expectedArguments = $"{HelperArg} {RequestFileArg} \"{expectedRequestFilePath}\"";
            if (!string.Equals(arguments, expectedArguments, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var expectedWorkingDirectory = Path.GetFullPath(Path.GetDirectoryName(executablePath) ?? ".");
            var actualWorkingDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(taskWorkingDirectory) ? "." : taskWorkingDirectory);
            return string.Equals(actualWorkingDirectory, expectedWorkingDirectory, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    public static async Task<bool> DeleteTaskAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var success = true;
        foreach (var taskName in GetKnownTaskNames())
        {
            if (!await TaskExistsAsync(taskName))
            {
                continue;
            }

            var result = await RunProcessAsync(
                "schtasks.exe",
                $"/Delete /TN \"{taskName}\" /F");
            if (!result.Success)
            {
                success = false;
            }
        }

        return success;
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> GetKnownTaskNames()
    {
        var taskName = GetTaskNameForCurrentUser();
        if (string.Equals(taskName, TaskNamePrefix, StringComparison.OrdinalIgnoreCase))
        {
            yield return TaskNamePrefix;
            yield break;
        }

        yield return taskName;
        yield return TaskNamePrefix;
    }

    [SupportedOSPlatform("windows")]
    private static async Task<bool> TaskExistsAsync(string taskName)
    {
        var result = await RunProcessAsync(
            "schtasks.exe",
            $"/Query /TN \"{taskName}\"");
        return result.Success;
    }

    private static string GetTaskDefinitionPath(string workingDirectory)
    {
        return Path.Combine(workingDirectory, "cache", TaskDefinitionFileName);
    }

    private static string? TryResolveHelperExecutablePath(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        try
        {
            var path = Path.Combine(directory, HelperExecutableFileName);
            return File.Exists(path) ? path : null;
        }
        catch
        {
            return null;
        }
    }

    private static string BuildTaskDefinitionXml(string executablePath, string requestFilePath)
    {
        var command = SecurityElement.Escape(executablePath) ?? executablePath;
        var arguments = SecurityElement.Escape(
                            $"{HelperArg} {RequestFileArg} \"{requestFilePath}\"") ??
                        $"{HelperArg} {RequestFileArg} \"{requestFilePath}\"";
        var workingDirectory = SecurityElement.Escape(Path.GetDirectoryName(executablePath) ?? ".") ?? ".";

        return $"""
                <?xml version="1.0" encoding="UTF-16"?>
                <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
                  <Triggers />
                  <Principals>
                    <Principal id="Author">
                      <LogonType>InteractiveToken</LogonType>
                      <RunLevel>HighestAvailable</RunLevel>
                    </Principal>
                  </Principals>
                  <Settings>
                    <MultipleInstancesPolicy>Parallel</MultipleInstancesPolicy>
                    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                    <AllowHardTerminate>false</AllowHardTerminate>
                    <StartWhenAvailable>false</StartWhenAvailable>
                    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                    <IdleSettings>
                      <StopOnIdleEnd>false</StopOnIdleEnd>
                      <RestartOnIdle>false</RestartOnIdle>
                    </IdleSettings>
                    <AllowStartOnDemand>true</AllowStartOnDemand>
                    <Enabled>true</Enabled>
                    <Hidden>true</Hidden>
                    <RunOnlyIfIdle>false</RunOnlyIfIdle>
                    <WakeToRun>false</WakeToRun>
                    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                    <Priority>3</Priority>
                  </Settings>
                  <Actions Context="Author">
                    <Exec>
                      <Command>{command}</Command>
                      <Arguments>{arguments}</Arguments>
                      <WorkingDirectory>{workingDirectory}</WorkingDirectory>
                    </Exec>
                  </Actions>
                </Task>
                """;
    }

    private static string EscapeForPowerShellSingleQuoted(string value)
    {
        return value.Replace("'", "''");
    }

    private static async Task<CommandExecutionResult> RunWindowsUacCommandAsync(string script)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encoded}",
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

        if (process.ExitCode == 0)
        {
            return new CommandExecutionResult(true, false, null);
        }

        var details = string.IsNullOrWhiteSpace(error) ? output : error;
        return new CommandExecutionResult(
            false,
            IsUacCancellationMessage(details),
            string.IsNullOrWhiteSpace(details) ? "UAC denied or failed" : details);
    }

    private static async Task<CommandExecutionResult> RunProcessAsync(string fileName, string arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
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

        return new CommandExecutionResult(
            process.ExitCode == 0,
            false,
            string.IsNullOrWhiteSpace(error) ? output : error);
    }

    private static bool IsUacCancellationMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        return message.Contains("canceled by the user", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("cancelled by the user", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("operation was canceled", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("operation was cancelled", StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private readonly record struct CommandExecutionResult(
        bool Success,
        bool Cancelled,
        string? ErrorMessage);
}
