
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

namespace ChernobylZLauncher.UI;

internal sealed record LauncherUpdate(Version Version, string ZipPath);

internal static class LauncherUpdateService
{
    private const string Owner = "PapuBeto";
    private const string Repo = "ChernobylLauncher";

    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10)
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "ChernobylZLauncher");

        return client;
    }

    public static async Task<LauncherUpdate?> CheckAndDownloadAsync(
        Version currentVersion)
    {
        string apiUrl =
            $"https://api.github.com/repos/{Owner}/{Repo}/releases?per_page=100";

        using var response = await Http.GetAsync(apiUrl);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());

        Version? latestVersion = null;
        string? downloadUrl = null;
        string? expectedDigest = null;

        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean() ||
                release.GetProperty("prerelease").GetBoolean())
            {
                continue;
            }

            string tag =
                release.GetProperty("tag_name").GetString() ?? "";

            if (!tag.StartsWith(
                    "launcher-v",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!Version.TryParse(
                    tag["launcher-v".Length..],
                    out var version))
            {
                continue;
            }

            if (version <= currentVersion ||
                (latestVersion != null && version <= latestVersion))
            {
                continue;
            }

            string expectedName =
                $"ChernobylZLauncher-v{version}-win-x64.zip";

            string? assetUrl = null;
            string? assetDigest = null;

            foreach (var asset in release
                         .GetProperty("assets")
                         .EnumerateArray())
            {
                string name =
                    asset.GetProperty("name").GetString() ?? "";

                if (!string.Equals(
                        name,
                        expectedName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                assetUrl =
                    asset.GetProperty("browser_download_url").GetString();

                if (asset.TryGetProperty("digest", out var digest))
                {
                    assetDigest = digest.GetString();
                }

                break;
            }

            if (assetUrl is null)
            {
                continue;
            }

            latestVersion = version;
            downloadUrl = assetUrl;
            expectedDigest = assetDigest;
        }

        if (latestVersion is null || downloadUrl is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(expectedDigest) ||
            !expectedDigest.StartsWith(
                "sha256:",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "GitHub no proporcionó el SHA-256 del paquete.");
        }

        string updatesFolder = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "ChernobylZLauncher",
            "Updates");

        Directory.CreateDirectory(updatesFolder);

        string zipPath = Path.Combine(
            updatesFolder,
            $"ChernobylZLauncher-v{latestVersion}-win-x64.zip");

        string tempPath = zipPath + ".download";

        try
        {
            using (var download = await Http.GetAsync(
                       downloadUrl,
                       HttpCompletionOption.ResponseHeadersRead))
            {
                download.EnsureSuccessStatusCode();

                await using var input =
                    await download.Content.ReadAsStreamAsync();

                await using var output = new FileStream(
                    tempPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None);

                await input.CopyToAsync(output);
            }

            await using (var file = File.OpenRead(tempPath))
            {
                string actualDigest = Convert.ToHexString(
                    await SHA256.HashDataAsync(file));

                string expectedHash =
                    expectedDigest["sha256:".Length..];

                if (!string.Equals(
                        actualDigest,
                        expectedHash,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "El SHA-256 del ZIP no coincide.");
                }
            }

            using (var archive = ZipFile.OpenRead(tempPath))
            {
                if (archive.Entries.Count == 0)
                {
                    throw new InvalidDataException(
                        "El ZIP está vacío.");
                }

                bool containsLauncher = archive.Entries.Any(entry =>
                    string.Equals(
                        entry.FullName.Replace('\\', '/'),
                        "ChernobylZLauncher.exe",
                        StringComparison.OrdinalIgnoreCase));

                if (!containsLauncher)
                {
                    throw new InvalidDataException(
                        "El ZIP no contiene ChernobylZLauncher.exe en la raíz.");
                }
            }

            File.Move(tempPath, zipPath, overwrite: true);

            return new LauncherUpdate(latestVersion, zipPath);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }
}