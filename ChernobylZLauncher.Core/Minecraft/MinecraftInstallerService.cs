using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ChernobylZLauncher.Core.Logging;

namespace ChernobylZLauncher.Core.Minecraft;

public record InstallProgress(string Stage, int Done, int Total);

// baja minecraft "limpio" (sin forge) con la misma estructura de carpetas que el launcher oficial:
//   <gameRoot>/versions/<version>/<version>.json y .jar
//   <gameRoot>/libraries/...
//   <gameRoot>/assets/indexes y assets/objects
// forge se instala encima de esto en la siguiente etapa
public class MinecraftInstallerService
{
    private const string VersionManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    private const string AssetsBaseUrl = "https://resources.download.minecraft.net";
    private const int MaxParallelDownloads = 16;

    private readonly HttpClient _httpClient;
    private readonly LauncherLogService? _log;

    private record DownloadItem(string Url, string Destination, string? Sha1);

    public MinecraftInstallerService(HttpClient? httpClient = null, LauncherLogService? log = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _log = log;
    }

    public async Task InstallVanillaAsync(
        string gameRoot,
        string versionId = "1.20.1",
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(gameRoot);

        // 1) el json de la version: aqui mojang dice que libreria, que assets y que jar necesita
        var versionDir = Path.Combine(gameRoot, "versions", versionId);
        var versionJsonPath = Path.Combine(versionDir, $"{versionId}.json");
        var versionJson = await GetVersionJsonAsync(versionId, versionJsonPath, cancellationToken);

        // 2) el jar del juego
        _log?.Info($"bajando el jar de minecraft {versionId}...");
        var client = versionJson["downloads"]!["client"]!;
        await DownloadFileAsync(
            client["url"]!.GetValue<string>(),
            Path.Combine(versionDir, $"{versionId}.jar"),
            client["sha1"]!.GetValue<string>(),
            cancellationToken);

        // 3) librerias
        await DownloadLibrariesAsync(versionJson["libraries"]!.AsArray(), gameRoot, progress, cancellationToken);

        // 4) assets (sonidos, texturas de idiomas, etc)
        await DownloadAssetsAsync(versionJson["assetIndex"]!, gameRoot, progress, cancellationToken);

        _log?.Success($"minecraft {versionId} listo para usarse");
    }

    private async Task<JsonNode> GetVersionJsonAsync(string versionId, string versionJsonPath, CancellationToken cancellationToken)
    {
        _log?.Info($"preguntandole a mojang donde esta la {versionId}...");

        var manifestText = await _httpClient.GetStringAsync(VersionManifestUrl, cancellationToken);
        var manifest = JsonNode.Parse(manifestText)!;

        var entry = manifest["versions"]!.AsArray()
            .FirstOrDefault(v => v?["id"]?.GetValue<string>() == versionId);

        if (entry == null)
        {
            throw new InvalidOperationException($"Mojang no tiene la version {versionId}");
        }

        await DownloadFileAsync(
            entry["url"]!.GetValue<string>(),
            versionJsonPath,
            entry["sha1"]!.GetValue<string>(),
            cancellationToken);

        return JsonNode.Parse(await File.ReadAllTextAsync(versionJsonPath, cancellationToken))!;
    }

    private async Task DownloadLibrariesAsync(
        JsonArray libraries,
        string gameRoot,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var files = new List<DownloadItem>();

        foreach (var library in libraries)
        {
            if (library == null || !IsAllowedOnThisSystem(library["rules"]))
            {
                continue;
            }

            var artifact = library["downloads"]?["artifact"];
            var url = artifact?["url"]?.GetValue<string>();

            // algunas librerias no traen url (las genera forge despues), esas no se bajan aqui
            if (artifact == null || string.IsNullOrEmpty(url))
            {
                continue;
            }

            files.Add(new DownloadItem(
                url,
                Path.Combine(gameRoot, "libraries", artifact["path"]!.GetValue<string>()),
                artifact["sha1"]!.GetValue<string>()));
        }

        files = files.DistinctBy(f => f.Destination).ToList();

        _log?.Info($"bajando {files.Count} librerias...");
        await RunDownloadsAsync("librerias", files, progress, cancellationToken);
    }

    private async Task DownloadAssetsAsync(
        JsonNode assetIndex,
        string gameRoot,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var id = assetIndex["id"]!.GetValue<string>();
        var indexPath = Path.Combine(gameRoot, "assets", "indexes", $"{id}.json");

        await DownloadFileAsync(
            assetIndex["url"]!.GetValue<string>(),
            indexPath,
            assetIndex["sha1"]!.GetValue<string>(),
            cancellationToken);

        var index = JsonNode.Parse(await File.ReadAllTextAsync(indexPath, cancellationToken))!;
        var files = new List<DownloadItem>();

        foreach (var entry in index["objects"]!.AsObject())
        {
            var hash = entry.Value!["hash"]!.GetValue<string>();
            var prefix = hash[..2];

            files.Add(new DownloadItem(
                $"{AssetsBaseUrl}/{prefix}/{hash}",
                Path.Combine(gameRoot, "assets", "objects", prefix, hash),
                hash));
        }

        // varios nombres pueden apuntar al mismo archivo, solo lo bajamos una vez
        files = files.DistinctBy(f => f.Sha1).ToList();

        _log?.Info($"bajando {files.Count} assets, esto es lo que mas tarda...");
        await RunDownloadsAsync("assets", files, progress, cancellationToken);
    }

    private async Task RunDownloadsAsync(
        string stage,
        List<DownloadItem> files,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var total = files.Count;

        if (total == 0)
        {
            return;
        }

        var done = 0;
        var lastReportedBucket = -1;

        progress?.Report(new InstallProgress(stage, 0, total));

        await Parallel.ForEachAsync(
            files,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = MaxParallelDownloads,
                CancellationToken = cancellationToken
            },
            async (file, token) =>
            {
                await DownloadFileAsync(file.Url, file.Destination, file.Sha1, token);

                var current = Interlocked.Increment(ref done);

                // avisamos de 5% en 5%, si no seria un mensaje por archivo (miles)
                var bucket = current * 100 / total / 5;
                if (Interlocked.Exchange(ref lastReportedBucket, bucket) != bucket)
                {
                    progress?.Report(new InstallProgress(stage, current, total));
                }
            });
    }

    // si el archivo ya esta y su hash coincide, no lo vuelve a bajar.
    // baja a un .tmp y lo mueve al final, asi nunca queda un archivo a medias
    private async Task DownloadFileAsync(string url, string destinationPath, string? expectedSha1, CancellationToken cancellationToken)
    {
        if (File.Exists(destinationPath) &&
            (expectedSha1 == null || string.Equals(Sha1Of(destinationPath), expectedSha1, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var tempPath = destinationPath + ".tmp";

        const int maxAttempts = 3;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using (var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                {
                    response.EnsureSuccessStatusCode();

                    await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    await using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
                    await contentStream.CopyToAsync(fileStream, cancellationToken);
                }

                if (expectedSha1 != null &&
                    !string.Equals(Sha1Of(tempPath), expectedSha1, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"hash incorrecto en {Path.GetFileName(destinationPath)}");
                }

                File.Move(tempPath, destinationPath, overwrite: true);
                return;
            }
            catch (Exception ex) when ((ex is HttpRequestException or InvalidDataException or IOException) && attempt < maxAttempts)
            {
                _log?.Warning($"fallo bajando {Path.GetFileName(destinationPath)} (intento {attempt}/{maxAttempts}), le damos otra vez...");

                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }

                await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken);
            }
        }
    }

    // las reglas de mojang: sin reglas = siempre; con reglas, gana la ultima que aplique.
    // ejemplo: una libreria de natives de windows trae "allow solo en windows"
    private static bool IsAllowedOnThisSystem(JsonNode? rules)
    {
        if (rules is not JsonArray ruleList || ruleList.Count == 0)
        {
            return true;
        }

        var allowed = false;

        foreach (var rule in ruleList)
        {
            if (rule == null)
            {
                continue;
            }

            var os = rule["os"];
            var applies = os == null || OsMatches(os);

            if (applies)
            {
                allowed = rule["action"]?.GetValue<string>() == "allow";
            }
        }

        return allowed;
    }

    private static bool OsMatches(JsonNode os)
    {
        var name = os["name"]?.GetValue<string>();
        if (name != null && name != CurrentOsName())
        {
            return false;
        }

        var arch = os["arch"]?.GetValue<string>();
        if (arch != null && arch != CurrentArch())
        {
            return false;
        }

        return true;
    }

    private static string CurrentOsName()
    {
        if (OperatingSystem.IsWindows())
        {
            return "windows";
        }

        return OperatingSystem.IsMacOS() ? "osx" : "linux";
    }

    private static string CurrentArch()
    {
        return RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => "x64"
        };
    }

    private static string Sha1Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA1.HashData(stream)).ToLowerInvariant();
    }
}
