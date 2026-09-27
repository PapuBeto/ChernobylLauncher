using System.Security.Cryptography;
using System.Text.Json;
using ChernobylZLauncher.Core.Logging;

namespace ChernobylZLauncher.Core.Mods;

public enum ModStatus
{
    Ok,
    Missing,
    HashMismatch,
    Extra,
    Obsolete
}

public class ModCheckResult
{
    public string FileName { get; set; } = string.Empty;
    public ModStatus Status { get; set; }
}

public class ModManagerService
{
    private const string StateFileName = "chernobylz-managed-mods.json";

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

        var managedFiles = LoadManagedFileNames(modsFolder);

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
                _log?.Warning($"falta el mod: {mod.FileName}");
                continue;
            }

            var localHash = ComputeSha256(localPath);
            var status = string.Equals(localHash, mod.Sha256, StringComparison.OrdinalIgnoreCase)
                ? ModStatus.Ok
                : ModStatus.HashMismatch;

            results.Add(new ModCheckResult { FileName = mod.FileName, Status = status });

            if (status == ModStatus.HashMismatch)
            {
                _log?.Warning($"hash no coincide: {mod.FileName}");
            }

            localFiles.Remove(mod.FileName);
        }

        foreach (var extra in localFiles)
        {
            var isManaged = managedFiles.Contains(extra);
            results.Add(new ModCheckResult
            {
                FileName = extra,
                Status = isManaged ? ModStatus.Obsolete : ModStatus.Extra
            });
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

        _log?.Info($"descargando {mod.FileName}...");

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
            _log?.Error($"descarga corrupta: {mod.FileName}");
            throw new InvalidOperationException(
                $"El hash de '{mod.FileName}' no coincide con el esperado. Descarga corrupta o manifiesto desactualizado.");
        }

        if (File.Exists(destinationPath))
        {
            File.Delete(destinationPath);
        }

        File.Move(tempPath, destinationPath);
        _log?.Success($"{mod.FileName} descargado");
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

        var obsolete = checkResults.Where(r => r.Status == ModStatus.Obsolete).ToList();
        foreach (var mod in obsolete)
        {
            var path = Path.Combine(modsFolder, mod.FileName);
            if (File.Exists(path))
            {
                File.Delete(path);
                _log?.Info($"removido (ya no esta en el manifest): {mod.FileName}");
            }
        }

        if (toDownload.Count == 0 && obsolete.Count == 0)
        {
            _log?.Info("todos los mods estan al dia");
        }

        SaveManagedFileNames(modsFolder, manifest.Mods.Select(m => m.FileName));
    }

    private static HashSet<string> LoadManagedFileNames(string modsFolder)
    {
        var statePath = Path.Combine(modsFolder, StateFileName);

        if (!File.Exists(statePath))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var json = File.ReadAllText(statePath);
        var list = JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        return new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);
    }

    private static void SaveManagedFileNames(string modsFolder, IEnumerable<string> fileNames)
    {
        var statePath = Path.Combine(modsFolder, StateFileName);
        var json = JsonSerializer.Serialize(fileNames.ToList());
        File.WriteAllText(statePath, json);
    }

    private static string ComputeSha256(string filePath)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hashBytes = sha256.ComputeHash(stream);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}