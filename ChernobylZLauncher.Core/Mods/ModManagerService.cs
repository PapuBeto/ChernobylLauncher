using System.Security.Cryptography;

namespace ChernobylZLauncher.Core.Mods;

public enum ModStatus
{
    Ok,
    Missing,
    HashMismatch,
    Extra
}

public class ModCheckResult
{
    public string FileName { get; set; } = string.Empty;
    public ModStatus Status { get; set; }
}

public class ModManagerService
{
    private readonly HttpClient _httpClient;

    public ModManagerService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    public List<ModCheckResult> CheckMods(string modsFolder, ModManifest manifest)
    {
        var results = new List<ModCheckResult>();
        Directory.CreateDirectory(modsFolder);

        var localFiles = Directory.GetFiles(modsFolder, "*.jar")
            .Select(Path.GetFileName)
            .Where(f => f != null)
            .Select(f => f!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in manifest.Mods)
        {
            var localPath = Path.Combine(modsFolder, mod.FileName);

            if (!File.Exists(localPath))
            {
                results.Add(new ModCheckResult { FileName = mod.FileName, Status = ModStatus.Missing });
                continue;
            }

            var localHash = ComputeSha256(localPath);
            results.Add(new ModCheckResult
            {
                FileName = mod.FileName,
                Status = string.Equals(localHash, mod.Sha256, StringComparison.OrdinalIgnoreCase)
                    ? ModStatus.Ok
                    : ModStatus.HashMismatch
            });

            localFiles.Remove(mod.FileName);
        }

        // lo que queda aca es lo que sobra en la carpeta y no deberia estar
        foreach (var extra in localFiles)
        {
            results.Add(new ModCheckResult { FileName = extra, Status = ModStatus.Extra });
        }

        return results;
    }

    public async Task DownloadModAsync(
        string modsFolder,
        ModInfo mod,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(modsFolder);
        var destinationPath = Path.Combine(modsFolder, mod.FileName);
        var tempPath = destinationPath + ".tmp";

        using (var response = await _httpClient.GetAsync(
            mod.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? mod.SizeBytes;
            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);

            var buffer = new byte[81920];
            long totalRead = 0;
            int read;

            while ((read = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                totalRead += read;

                if (totalBytes > 0)
                {
                    progress?.Report((double)totalRead / totalBytes * 100);
                }
            }
        }

        // chequeamos el hash antes de dejarlo como definitivo, por si la descarga vino corrupta
        var downloadedHash = ComputeSha256(tempPath);
        if (!string.Equals(downloadedHash, mod.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(tempPath);
            throw new InvalidOperationException(
                $"El hash de '{mod.FileName}' no coincide con el esperado. Descarga corrupta o manifiesto desactualizado.");
        }

        if (File.Exists(destinationPath))
        {
            File.Delete(destinationPath);
        }

        File.Move(tempPath, destinationPath);
    }

    public async Task SyncModsAsync(
        string modsFolder,
        ModManifest manifest,
        IProgress<(string FileName, double Percent)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var checkResults = CheckMods(modsFolder, manifest);
        var toDownload = checkResults
            .Where(r => r.Status is ModStatus.Missing or ModStatus.HashMismatch)
            .Select(r => manifest.Mods.First(m => m.FileName == r.FileName))
            .ToList();

        foreach (var mod in toDownload)
        {
            var fileProgress = new Progress<double>(p => progress?.Report((mod.FileName, p)));
            await DownloadModAsync(modsFolder, mod, fileProgress, cancellationToken);
        }
    }

    private static string ComputeSha256(string filePath)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hashBytes = sha256.ComputeHash(stream);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}