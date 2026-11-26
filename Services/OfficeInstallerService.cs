using System.IO;
using System.Net.Http;
using System.Security;
using Microsoft.Win32;

namespace MySystemGui.Services;

public sealed class OfficeInstallerService
{
    private const string OfficeInventoryPath = @"SOFTWARE\Microsoft\Office\ClickToRun\Inventory\Office\16.0";
    private const string InstallerUrl =
        "https://c2rsetup.officeapps.live.com/c2r/download.aspx?ProductreleaseID=O365HomePremRetail&language=en-en&platform=def&version=O16GA&source=MASGUI";
    private const long MaximumInstallerSize = 100 * 1024 * 1024;
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromMinutes(5)
    };

    public static OfficeInstallation? DetectInstalledOffice()
    {
        foreach (var registryView in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, registryView);
            using var inventoryKey = baseKey.OpenSubKey(OfficeInventoryPath);
            if (inventoryKey is null)
            {
                continue;
            }

            var productReleaseIds = inventoryKey.GetValue("OfficeProductReleaseIds")?.ToString();
            if (string.IsNullOrWhiteSpace(productReleaseIds))
            {
                continue;
            }

            var packageVersion = inventoryKey.GetValue("OfficePackageVersion")?.ToString();
            return new OfficeInstallation(productReleaseIds, packageVersion ?? "Unknown version");
        }

        return null;
    }

    public async Task<OfficeInstallerDownloadResult> GetInstallerAsync(Action<int> reportProgress)
    {
        ArgumentNullException.ThrowIfNull(reportProgress);

        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "MAS GUI");
        Directory.CreateDirectory(temporaryDirectory);
        var installerPath = Path.Combine(temporaryDirectory, "OfficeSetup.exe");
        if (IsValidInstaller(installerPath))
        {
            return new OfficeInstallerDownloadResult(installerPath, UsedCachedInstaller: true);
        }

        if (File.Exists(installerPath) && !TryDeleteInstaller(installerPath))
        {
            throw new IOException("The cached Office installer is invalid and could not be removed.");
        }

        var downloadPath = Path.Combine(temporaryDirectory, $"OfficeSetup-{Guid.NewGuid():N}.download");

        try
        {
            using var response = await HttpClient.GetAsync(
                InstallerUrl,
                HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var finalUri = response.RequestMessage?.RequestUri;
            if (finalUri is null ||
                !finalUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                !IsMicrosoftInstallerHost(finalUri.Host))
            {
                throw new SecurityException("The Office installer download did not come from a Microsoft HTTPS host.");
            }

            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength > MaximumInstallerSize)
            {
                throw new IOException("The Office installer download exceeded the allowed size.");
            }

            await using var input = await response.Content.ReadAsStreamAsync();
            await using var output = new FileStream(
                downloadPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                81920,
                useAsync: true);

            var buffer = new byte[81920];
            long bytesReadTotal = 0;
            var lastReportedProgress = -1;
            while (true)
            {
                var bytesRead = await input.ReadAsync(buffer);
                if (bytesRead == 0)
                {
                    break;
                }

                bytesReadTotal += bytesRead;
                if (bytesReadTotal > MaximumInstallerSize)
                {
                    throw new IOException("The Office installer download exceeded the allowed size.");
                }

                await output.WriteAsync(buffer.AsMemory(0, bytesRead));

                if (contentLength is > 0)
                {
                    var progress = (int)(bytesReadTotal * 100 / contentLength.Value);
                    if (progress >= lastReportedProgress + 10 || progress == 100)
                    {
                        lastReportedProgress = progress;
                        reportProgress(Math.Min(progress, 100));
                    }
                }
            }

            await output.FlushAsync();
            if (bytesReadTotal < 1024 * 1024)
            {
                throw new InvalidDataException("The downloaded Office installer was empty or incomplete.");
            }

            if (contentLength is > 0 && bytesReadTotal != contentLength.Value)
            {
                throw new InvalidDataException("The downloaded Office installer was incomplete.");
            }

            output.Position = 0;
            if (output.ReadByte() != 'M' || output.ReadByte() != 'Z')
            {
                throw new InvalidDataException("The downloaded file is not a Windows installer executable.");
            }

            await output.DisposeAsync();
            File.Move(downloadPath, installerPath, overwrite: true);
            return new OfficeInstallerDownloadResult(installerPath, UsedCachedInstaller: false);
        }
        catch
        {
            TryDeleteInstaller(downloadPath);
            throw;
        }
    }

    private static bool IsValidInstaller(string installerPath)
    {
        try
        {
            var fileInfo = new FileInfo(installerPath);
            if (!fileInfo.Exists || fileInfo.Length < 1024 * 1024 || fileInfo.Length > MaximumInstallerSize)
            {
                return false;
            }

            using var installer = File.OpenRead(installerPath);
            return installer.ReadByte() == 'M' && installer.ReadByte() == 'Z';
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }

    public static bool TryDeleteInstaller(string installerPath)
    {
        try
        {
            if (File.Exists(installerPath))
            {
                File.Delete(installerPath);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }

    private static bool IsMicrosoftInstallerHost(string host) =>
        host.Equals("officeapps.live.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".officeapps.live.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("microsoft.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".microsoft.com", StringComparison.OrdinalIgnoreCase);
}

public sealed record OfficeInstallation(string ProductReleaseIds, string PackageVersion);

public sealed record OfficeInstallerDownloadResult(string Path, bool UsedCachedInstaller);
