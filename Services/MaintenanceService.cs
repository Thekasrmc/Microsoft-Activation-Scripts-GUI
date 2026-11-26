using System.IO;
using System.Diagnostics;
using System.Security;
using System.Text;
using Microsoft.Win32;

namespace MySystemGui.Services;

public enum MaintenanceTask
{
    WindowsImageRepair,
    SystemFileCheck,
    LicenseStatusCheck,
    WmiRepositoryRepair
}

public sealed record SystemBuildInfo(string EditionId, string BuildNumber)
{
    public override string ToString() => $"Current Edition: [{EditionId}] [{BuildNumber}]";
}

public sealed class MaintenanceService
{
    public SystemBuildInfo GetSystemBuildInfo()
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var versionKey = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion")
            ?? throw new InvalidOperationException("Windows CurrentVersion registry key was not found.");

        var editionId = versionKey.GetValue("EditionID") as string
            ?? throw new InvalidOperationException("Windows EditionID was not found.");
        var currentBuild = versionKey.GetValue("CurrentBuildNumber") as string
            ?? throw new InvalidOperationException("Windows CurrentBuildNumber was not found.");
        var updateBuildRevision = versionKey.GetValue("UBR")?.ToString();

        var buildNumber = string.IsNullOrWhiteSpace(updateBuildRevision)
            ? currentBuild
            : $"{currentBuild}.{updateBuildRevision}";

        return new SystemBuildInfo(editionId, buildNumber);
    }

    public async Task<int> RunCommandAsync(MaintenanceTask task, Action<string> onLog)
    {
        ArgumentNullException.ThrowIfNull(onLog);

        string? temporaryScriptPath = null;
        var (executable, arguments, displayName) = task switch
        {
            MaintenanceTask.WindowsImageRepair => (
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "dism.exe"),
                "/Online /Cleanup-Image /RestoreHealth",
                "DISM RestoreHealth"),
            MaintenanceTask.SystemFileCheck => (
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sfc.exe"),
                "/scannow",
                "SFC Scannow"),
            MaintenanceTask.LicenseStatusCheck => BuildMasActivationStatusCommand(out temporaryScriptPath),
            MaintenanceTask.WmiRepositoryRepair => (
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "winmgmt.exe"),
                "/salvagerepository",
                "Fix WMI Repository"),
            _ => throw new ArgumentOutOfRangeException(nameof(task), task, "Unsupported maintenance task.")
        };

        try
        {
            onLog($"> Starting {displayName}...");

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                },
                EnableRaisingEvents = true
            };

            if (!process.Start())
            {
                throw new InvalidOperationException($"Could not start {displayName}.");
            }

            var stdoutTask = ForwardLinesAsync(process.StandardOutput, onLog);
            var stderrTask = ForwardLinesAsync(process.StandardError, onLog);

            await process.WaitForExitAsync();
            await Task.WhenAll(stdoutTask, stderrTask);

            onLog($"> {displayName} exited with code {process.ExitCode}.");
            return process.ExitCode;
        }
        finally
        {
            if (temporaryScriptPath is not null &&
                !MasScriptProvider.TryDeleteTemporaryScript(temporaryScriptPath))
            {
                onLog("> Warning: Could not remove the temporary MAS script.");
            }
        }
    }

    private static (string Executable, string Arguments, string DisplayName) BuildMasActivationStatusCommand(
        out string temporaryScriptPath)
    {
        temporaryScriptPath = MasScriptProvider.CreateTemporaryScript();

        string escapedScriptPath = temporaryScriptPath.Replace("'", "''", StringComparison.Ordinal);
        string command =
            $"$f=[IO.File]::ReadAllText('{escapedScriptPath}') -split ':sppmgr\\:.*';. ([scriptblock]::Create($f[1]))";
        string encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        string powerShellPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            @"WindowsPowerShell\v1.0\powershell.exe");

        return (
            powerShellPath,
            $"-NoProfile -EncodedCommand {encodedCommand}",
            "Check Activation Status (MAS)");
    }

    private static async Task ForwardLinesAsync(StreamReader reader, Action<string> onLog)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                onLog(line);
            }
        }
    }
}
