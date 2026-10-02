using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChernobylZLauncher.Core.Logging;

namespace ChernobylZLauncher.Core.Runtime;

public class JavaInfo
{
    public bool IsInstalled { get; set; }
    public string? Path { get; set; }
    public int MajorVersion { get; set; }
    public bool IsCompatible { get; set; }
}

public class JavaService
{
    // minecraft 1.20.1 quiere java 17, ni uno mas ni uno menos (bueno, mas si jala, pero no arriesguemos)
    private const int RequiredMajorVersion = 17;

    // api de adoptium (temurin): nos da el link del zip y su sha256
    private const string AdoptiumAssetsUrl =
        "https://api.adoptium.net/v3/assets/latest/17/hotspot?architecture=x64&image_type=jre&os=windows&vendor=eclipse";

    private readonly HttpClient _httpClient;
    private readonly LauncherLogService? _log;

    public JavaService(LauncherLogService? log = null, HttpClient? httpClient = null)
    {
        _log = log;
        _httpClient = httpClient ?? new HttpClient();
    }

    /// <summary>
    /// Se asegura de que haya un java 17 usable. Orden:
    /// 1) el java 17 que ya descargamos antes en runtimeFolder
    /// 2) el java 17 del sistema
    /// 3) si no hay ninguno, descarga el JRE 17 de Temurin y lo deja en runtimeFolder
    /// </summary>
    public async Task<JavaInfo> EnsureJavaAsync(
        string runtimeFolder,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var javaFolder = Path.Combine(runtimeFolder, "java17");

        // 1) ya lo descargamos antes?
        var bundledJava = FindJavaExecutable(javaFolder);
        if (bundledJava != null)
        {
            var bundledInfo = await DetectJavaAsync(bundledJava);
            if (bundledInfo.IsCompatible)
            {
                _log?.Success("usando el java 17 del launcher");
                return bundledInfo;
            }

            _log?.Warning("el java del launcher esta danado, lo vamos a bajar de nuevo");
        }

        // 2) el sistema ya tiene java 17?
        var systemInfo = await DetectSystemJavaAsync();
        if (systemInfo.IsCompatible)
        {
            return systemInfo;
        }

        // 3) toca descargarlo
        if (!OperatingSystem.IsWindows())
        {
            _log?.Error("la descarga automatica de java solo esta lista para windows");
            return new JavaInfo { IsInstalled = false };
        }

        _log?.Info("no hay java 17, descargando...");
        await DownloadJavaAsync(javaFolder, progress, cancellationToken);

        var downloadedJava = FindJavaExecutable(javaFolder);
        if (downloadedJava == null)
        {
            _log?.Error("descargue java pero no encontre java.exe");
            return new JavaInfo { IsInstalled = false };
        }

        var finalInfo = await DetectJavaAsync(downloadedJava);
        if (finalInfo.IsCompatible)
        {
            _log?.Success("java 17 listo");
        }
        else
        {
            _log?.Error("el java descargado no funciona");
        }

        return finalInfo;
    }

    public Task<JavaInfo> DetectSystemJavaAsync()
    {
        _log?.Info("buscando java por ahi...");
        return DetectJavaAsync("java");
    }

    private async Task<JavaInfo> DetectJavaAsync(string javaPath)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = javaPath,
                    Arguments = "-version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();

            // java manda la version por stderr, cosas de java
            var errorOutput = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            var majorVersion = ParseJavaMajorVersion(errorOutput);

            if (majorVersion == null)
            {
                _log?.Warning("java esta pero no entendi que version es");
                return new JavaInfo { IsInstalled = false };
            }

            var isCompatible = majorVersion == RequiredMajorVersion;

            if (isCompatible)
            {
                _log?.Success($"java {majorVersion} encontrado, compatible");
            }
            else
            {
                _log?.Warning($"java {majorVersion} encontrado pero necesitamos la {RequiredMajorVersion}");
            }

            return new JavaInfo
            {
                IsInstalled = true,
                Path = javaPath,
                MajorVersion = majorVersion.Value,
                IsCompatible = isCompatible
            };
        }
        catch (Exception)
        {
            _log?.Warning("java no esta instalado o no esta en el PATH");
            return new JavaInfo { IsInstalled = false };
        }
    }

    private async Task DownloadJavaAsync(
        string javaFolder,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        // 1) preguntarle a adoptium cual es el zip mas nuevo de java 17 y su hash
        var metadataJson = await _httpClient.GetStringAsync(AdoptiumAssetsUrl, cancellationToken);
        using var metadata = JsonDocument.Parse(metadataJson);

        var package = metadata.RootElement[0].GetProperty("binary").GetProperty("package");
        var downloadUrl = package.GetProperty("link").GetString()
            ?? throw new InvalidOperationException("Adoptium no devolvio el link de descarga.");
        var expectedSha256 = package.GetProperty("checksum").GetString()
            ?? throw new InvalidOperationException("Adoptium no devolvio el checksum.");
        var sizeBytes = package.GetProperty("size").GetInt64();

        // 2) bajar el zip a un archivo temporal
        var parentFolder = Path.GetDirectoryName(javaFolder)!;
        Directory.CreateDirectory(parentFolder);

        var zipPath = Path.Combine(parentFolder, "java17.zip.tmp");
        var extractTemp = Path.Combine(parentFolder, "java17.extracting");

        try
        {
            using (var response = await _httpClient.GetAsync(
                downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? sizeBytes;
                await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);

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

            // 3) verificar que no llego corrupto
            var downloadedHash = ComputeSha256(zipPath);
            if (!string.Equals(downloadedHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                _log?.Error("descarga de java corrupta");
                throw new InvalidOperationException(
                    "El hash del java descargado no coincide con el de Adoptium. Descarga corrupta.");
            }

            // 4) extraer. el zip trae una carpeta raiz tipo jdk-17.0.x+y-jre, la aplanamos a java17/
            if (Directory.Exists(extractTemp))
            {
                Directory.Delete(extractTemp, recursive: true);
            }

            ZipFile.ExtractToDirectory(zipPath, extractTemp);

            var rootFolders = Directory.GetDirectories(extractTemp);
            var extractedRoot = rootFolders.Length == 1 ? rootFolders[0] : extractTemp;

            if (Directory.Exists(javaFolder))
            {
                Directory.Delete(javaFolder, recursive: true);
            }

            Directory.Move(extractedRoot, javaFolder);
        }
        finally
        {
            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }

            if (Directory.Exists(extractTemp))
            {
                Directory.Delete(extractTemp, recursive: true);
            }
        }
    }

    private static string? FindJavaExecutable(string javaFolder)
    {
        var exeName = OperatingSystem.IsWindows() ? "java.exe" : "java";
        var path = Path.Combine(javaFolder, "bin", exeName);
        return File.Exists(path) ? path : null;
    }

    private static string ComputeSha256(string filePath)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hashBytes = sha256.ComputeHash(stream);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private static int? ParseJavaMajorVersion(string versionOutput)
    {
        // el output se ve tipo: openjdk version "17.0.9" 2023-10-17
        var match = Regex.Match(versionOutput, "version \"(\\d+)(?:\\.(\\d+))?");

        if (!match.Success)
        {
            return null;
        }

        var firstNumber = int.Parse(match.Groups[1].Value);

        // formato viejo tipo 1.8.0 (java 8) vs formato nuevo tipo 17.0.9 (java 17)
        if (firstNumber == 1 && match.Groups[2].Success)
        {
            return int.Parse(match.Groups[2].Value);
        }

        return firstNumber;
    }
}