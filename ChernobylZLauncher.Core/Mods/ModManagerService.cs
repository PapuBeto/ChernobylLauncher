using System.IO.Compression;
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

    // si el manifest trae un nombre chueco (con rutas, sin .jar) mejor tronamos de una vez
    private static void ValidateManifest(ModManifest manifest)
    {
        foreach (var mod in manifest.Mods)
        {
            var name = mod.FileName;
            if (string.IsNullOrWhiteSpace(name)
                || name != Path.GetFileName(name)
                || !name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"el manifest trae un nombre de mod raro: '{name}'. tiene que ser solo el nombre del archivo y terminar en .jar");
            }
        }
    }

    // un jar es un zip, si no se puede abrir como zip forge se muere al arrancar
    public static bool IsValidJar(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            return zip.Entries.Count > 0;
        }
        catch (InvalidDataException)
        {
            return false;
        }
        catch (IOException)
        {
            // archivo en uso o algo asi, no podemos saber, mejor no tocarlo
            return true;
        }
    }

    // los jar rotos se renombran a .jar.roto: forge solo carga *.jar, asi que ya no estorban
    public List<string> QuarantineBrokenJars(string modsFolder)
    {
        var quarantined = new List<string>();
        if (!Directory.Exists(modsFolder)) return quarantined;

        foreach (var path in Directory.GetFiles(modsFolder, "*.jar"))
        {
            if (IsValidJar(path)) continue;

            var name = Path.GetFileName(path);
            try
            {
                File.Move(path, path + ".roto", overwrite: true);
                quarantined.Add(name);
                _log?.Warning($"{name} no es un jar de verdad, lo aparte como {name}.roto");
            }
            catch (Exception ex)
            {
                _log?.Warning($"no pude apartar {name}: {ex.Message}");
            }
        }

        return quarantined;
    }

    // restos de descargas cortadas
    private static void CleanTempFiles(string modsFolder)
    {
        if (!Directory.Exists(modsFolder)) return;

        foreach (var tmp in Directory.GetFiles(modsFolder, "*.tmp"))
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    public List<ModCheckResult> CheckMods(string modsFolder, ModManifest manifest)
    {
        ValidateManifest(manifest);

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

        // el hash puede coincidir y aun asi no ser un jar (como el dummy que apuntaba a un README)
        if (!IsValidJar(tempPath))
        {
            File.Delete(tempPath);
            _log?.Error($"{mod.FileName} no es un jar de verdad");
            throw new InvalidOperationException(
                $"'{mod.FileName}' se descargo bien pero no es un jar valido. Revisa el downloadUrl de ese mod en el manifest.");
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
        CleanTempFiles(modsFolder);

        var checkResults = CheckMods(modsFolder, manifest);

        var toDownload = checkResults
            .Where(r => r.Status is ModStatus.Missing or ModStatus.HashMismatch)
            .Select(r => manifest.Mods.First(m => m.FileName == r.FileName))
            .ToList();

        foreach (var mod in toDownload)
        {
            cancellationToken.ThrowIfCancellationRequested();
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

        // ultima red de seguridad: ningun jar roto se queda en la carpeta
        QuarantineBrokenJars(modsFolder);

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