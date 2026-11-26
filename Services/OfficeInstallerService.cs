using System.IO;
using System.Net.Http;
using System.Security;

namespace MySystemGui.Services;

public sealed class OfficeInstallerService
{
    private const string InstallerUrl =
        "https://c2rsetup.officeapps.live.com/c2r/download.aspx?ProductreleaseID=O365HomePremRetail&language=en-en&platform=def&version=O16GA&source=MASGUI";
    private const long MaximumInstallerSize = 100 * 1024 * 1024;
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromMinutes(5)
    };

    public async Task<string> DownloadLatestInstallerAsync(Action<int> reportProgress)
    {
        ArgumentNullException.ThrowIfNull(reportProgress);

        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "MAS GUI");
        Directory.CreateDirectory(temporaryDirectory);
        var installerPath = Path.Combine(temporaryDirectory, $"OfficeSetup-{Guid.NewGuid():N}.exe");

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
                installerPath,
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
            if (bytesReadTotal < 2)
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

            return installerPath;
        }
        catch
        {
            TryDeleteInstaller(installerPath);
            throw;
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
