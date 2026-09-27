using System.Security.Cryptography;
using ChernobylZLauncher.Core.Logging;

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
    private readonly LauncherLogService? _log;

    public ModManagerService(HttpClient? httpClient = null, LauncherLogService? log = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _log = log;
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
                _log?.Warning($"Falta el mod: {mod.FileName}");
                continue;
            }

            var localHash = ComputeSha256(localPath);
            var status = string.Equals(localHash, mod.Sha256, StringComparison.OrdinalIgnoreCase)
                ? ModStatus.Ok
                : ModStatus.HashMismatch;

            results.Add(new ModCheckResult { FileName = mod.FileName, Status = status });

            if (status == ModStatus.HashMismatch)
            {
                _log?.Warning($"Hash no coincide: {mod.FileName}");
            }

            localFiles.Remove(mod.FileName);
        }

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

        _log?.Info($"Descargando {mod.FileName}...");

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

        var downloadedHash = ComputeSha256(tempPath);
        if (!string.Equals(downloadedHash, mod.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(tempPath);
            _log?.Error($"Descarga corrupta: {mod.FileName}");
            throw new InvalidOperationException(
                $"El hash de '{mod.FileName}' no coincide con el esperado. Descarga corrupta o manifiesto desactualizado.");
        }

        if (File.Exists(destinationPath))
        {
            File.Delete(destinationPath);
        }

        File.Move(tempPath, destinationPath);
        _log?.Success($"{mod.FileName} descargado correctamente");
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

        if (toDownload.Count == 0)
        {
            _log?.Info("Todos los mods están al día");
            return;
        }

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