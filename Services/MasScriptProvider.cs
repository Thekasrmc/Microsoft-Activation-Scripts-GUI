using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;

namespace MySystemGui.Services;

internal static class MasScriptProvider
{
    private const string ResourceSuffix = "MAS_AIO.cmd";

    public static string CreateTemporaryScript()
    {
        var assembly = typeof(MasScriptProvider).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith(ResourceSuffix, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException("The embedded MAS_AIO.cmd resource was not found.");

        using var resourceStream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException("The embedded MAS_AIO.cmd resource could not be opened.");
        using var memoryStream = new MemoryStream();
        resourceStream.CopyTo(memoryStream);
        var scriptContents = memoryStream.ToArray();
        var scriptHash = SHA256.HashData(scriptContents);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException("The Local Application Data folder could not be located.");
        }

        var scriptDirectory = Path.Combine(
            localAppData,
            "Thekasrmc",
            "Microsoft Activation Scripts GUI");
        Directory.CreateDirectory(scriptDirectory);

        var scriptPath = Path.Combine(scriptDirectory, $"MAS_AIO-{Guid.NewGuid():N}.cmd");
        try
        {
            File.WriteAllBytes(scriptPath, scriptContents);
            VerifyScriptHash(scriptPath, scriptHash);
            return scriptPath;
        }
        catch
        {
            TryDeleteTemporaryScript(scriptPath);
            throw;
        }
    }

    public static bool TryDeleteTemporaryScript(string scriptPath)
    {
        try
        {
            if (File.Exists(scriptPath))
            {
                File.Delete(scriptPath);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static void VerifyScriptHash(string scriptPath, byte[] expectedHash)
    {
        using var scriptStream = File.OpenRead(scriptPath);
        var actualHash = SHA256.HashData(scriptStream);
        if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
        {
            throw new IOException(
                $"The extracted MAS script failed its integrity check: {scriptPath}");
        }
    }
}
