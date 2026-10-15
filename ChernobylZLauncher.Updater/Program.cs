
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

// Modo instalación: se ejecuta con --install ZIP DESTINO BACKUP PID EXE.
if (args.Length > 0 &&
    args[0].Equals("--install", StringComparison.OrdinalIgnoreCase))
{
    try
    {
        await UpdateInstaller.RunAsync(args);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(
            $"Error durante la instalación: {ex.Message}");

        Environment.ExitCode = 1;
    }

    return;
}

// Detector independiente de actualizaciones.
const string owner = "PapuBeto";
const string repo = "ChernobylLauncher";
const string currentVersion = "1.0.0";
const string tagPrefix = "launcher-v";
const string packagePrefix = "ChernobylZLauncher-v";

using var http = new HttpClient
{
    Timeout = TimeSpan.FromMinutes(10)
};

http.DefaultRequestHeaders.UserAgent.ParseAdd(
    "ChernobylZLauncher-Updater");

try
{
    string apiUrl =
        $"https://api.github.com/repos/{owner}/{repo}/releases?per_page=100";

    string json = await http.GetStringAsync(apiUrl);

    using JsonDocument document = JsonDocument.Parse(json);

    Version? latestVersion = null;
    string? latestTag = null;
    string? downloadUrl = null;
    string? expectedDigest = null;

    foreach (JsonElement release in document.RootElement.EnumerateArray())
    {
        if (release.GetProperty("draft").GetBoolean() ||
            release.GetProperty("prerelease").GetBoolean())
        {
            continue;
        }

        string tag = release.GetProperty("tag_name").GetString() ?? "";

        if (!tag.StartsWith(tagPrefix, StringComparison.OrdinalIgnoreCase))
            continue;

        if (!Version.TryParse(tag[tagPrefix.Length..], out Version? version))
            continue;

        if (latestVersion is not null && version <= latestVersion)
            continue;

        string expectedName = $"{packagePrefix}{version}-win-x64.zip";

        string? assetUrl = null;
        string? assetDigest = null;

        foreach (JsonElement asset in release.GetProperty("assets").EnumerateArray())
        {
            string name = asset.GetProperty("name").GetString() ?? "";

            if (!string.Equals(
                    name,
                    expectedName,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            assetUrl = asset.GetProperty("browser_download_url").GetString();

            if (asset.TryGetProperty("digest", out JsonElement digest))
                assetDigest = digest.GetString();

            break;
        }

        if (assetUrl is null)
            continue;

        latestVersion = version;
        latestTag = tag;
        downloadUrl = assetUrl;
        expectedDigest = assetDigest;
    }

    Console.WriteLine($"Versión instalada (prueba): {currentVersion}");

    if (latestVersion is null || downloadUrl is null)
    {
        Console.WriteLine(
            "No se encontró una actualización con un ZIP válido.");
        return;
    }

    Console.WriteLine($"Última versión: {latestVersion}");

    if (!Version.TryParse(currentVersion, out Version? installed))
    {
        Console.WriteLine("La versión instalada no es válida.");
        return;
    }

    if (latestVersion <= installed)
    {
        Console.WriteLine("El launcher ya está actualizado.");
        return;
    }

    if (string.IsNullOrWhiteSpace(expectedDigest) ||
        !expectedDigest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine("GitHub no proporcionó SHA-256. Descarga descartada.");
        return;
    }

    string updateDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ChernobylZLauncher",
        "Updates");

    Directory.CreateDirectory(updateDirectory);

    string zipPath = Path.Combine(
        updateDirectory,
        $"{packagePrefix}{latestVersion}-win-x64.zip");

    string tempPath = zipPath + ".download";

    try
    {
        Console.WriteLine("Descargando actualización...");

        using (HttpResponseMessage response = await http.GetAsync(
                   downloadUrl,
                   HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();

            await using var source = await response.Content.ReadAsStreamAsync();

            await using var destination = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None);

            await source.CopyToAsync(destination);
        }

        await using (var file = File.OpenRead(tempPath))
        {
            byte[] hash = await SHA256.HashDataAsync(file);
            string actualDigest = Convert.ToHexString(hash);
            string expectedHash = expectedDigest["sha256:".Length..];

            if (!string.Equals(
                    actualDigest,
                    expectedHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "El SHA-256 del ZIP no coincide.");
            }
        }

        using (ZipArchive archive = ZipFile.OpenRead(tempPath))
        {
            if (archive.Entries.Count == 0)
                throw new InvalidDataException("El ZIP está vacío.");

            if (!archive.Entries.Any(entry =>
                    string.Equals(
                        entry.FullName.Replace('\\', '/'),
                        "ChernobylZLauncher.exe",
                        StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException(
                    "El ZIP no contiene ChernobylZLauncher.exe en la raíz.");
            }
        }

        File.Move(tempPath, zipPath, overwrite: true);

        Console.WriteLine("Descarga verificada correctamente.");
        Console.WriteLine($"Paquete: {zipPath}");
        Console.WriteLine("Esta ejecución independiente no instala el paquete.");
    }
    finally
    {
        if (File.Exists(tempPath))
            File.Delete(tempPath);
    }
}
catch (HttpRequestException ex)
{
    Console.Error.WriteLine($"Error de conexión: {ex.Message}");
}
catch (JsonException ex)
{
    Console.Error.WriteLine($"Error leyendo GitHub: {ex.Message}");
}
catch (InvalidDataException ex)
{
    Console.Error.WriteLine($"Paquete no válido: {ex.Message}");
}
catch (IOException ex)
{
    Console.Error.WriteLine($"Error de archivos: {ex.Message}");
}
catch (TaskCanceledException)
{
    Console.Error.WriteLine(
        "La operación fue cancelada o agotó el tiempo.");
}

// Instalador que se ejecuta únicamente con --install.
internal static class UpdateInstaller
{
    public static async Task RunAsync(string[] args)
    {
        if (args.Length != 6)
        {
            throw new ArgumentException(
                "Uso: Updater.exe --install <zip> <destino> <backup> <pid> <launcher.exe>");
        }

        string zipPath = Path.GetFullPath(args[1]);
        string targetPath = Path.GetFullPath(args[2]);
        string backupPath = Path.GetFullPath(args[3]);

        if (!int.TryParse(args[4], out int launcherPid) || launcherPid <= 0)
        {
            throw new ArgumentException("El PID del launcher no es válido.");
        }

        string launcherExe = Path.GetFullPath(args[5]);

        if (!File.Exists(zipPath))
            throw new FileNotFoundException("No se encontró el ZIP.", zipPath);

        if (!Directory.Exists(targetPath))
            throw new DirectoryNotFoundException(
                $"No se encontró la carpeta de instalación: {targetPath}");

        if (!string.Equals(
                Path.GetDirectoryName(launcherExe)?.TrimEnd('\\', '/'),
                targetPath.TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "El ejecutable del launcher debe estar en la carpeta de destino.");
        }

        if (string.Equals(
                targetPath.TrimEnd('\\', '/'),
                backupPath.TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "La copia de seguridad no puede ser la carpeta de instalación.");
        }

        string stagingPath = Path.Combine(
            Path.GetTempPath(),
            "ChernobylZLauncher-Staging-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(stagingPath);

        try
        {
            Console.WriteLine("Validando paquete...");

            using (ZipArchive archive = ZipFile.OpenRead(zipPath))
            {
                if (archive.Entries.Count == 0)
                    throw new InvalidDataException("El ZIP está vacío.");

                string stagingRoot = Path.GetFullPath(stagingPath)
                                     + Path.DirectorySeparatorChar;

                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string name = entry.FullName.Replace('\\', '/');

                    if (name.StartsWith('/') ||
                        Path.IsPathRooted(name) ||
                        name.Split('/').Any(part => part == ".."))
                    {
                        throw new InvalidDataException(
                            $"Ruta no permitida: {entry.FullName}");
                    }

                    string destination = Path.GetFullPath(
                        Path.Combine(stagingPath, name));

                    if (!destination.StartsWith(
                            stagingRoot,
                            StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(
                            destination,
                            Path.GetFullPath(stagingPath),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException(
                            $"Ruta fuera de la carpeta temporal: {name}");
                    }
                }

                archive.ExtractToDirectory(stagingPath);
            }

            if (!File.Exists(Path.Combine(
                    stagingPath,
                    "ChernobylZLauncher.exe")))
            {
                throw new InvalidDataException(
                    "No se encontró ChernobylZLauncher.exe en la raíz del ZIP.");
            }

            // No reemplazamos el actualizador que está ejecutándose.
            string[] newFiles = Directory.GetFiles(
                    stagingPath,
                    "*",
                    SearchOption.AllDirectories)
                .Where(file =>
                {
                    string relative = Path.GetRelativePath(stagingPath, file);

                    string firstPart = relative.Split(
                        new[]
                        {
                            Path.DirectorySeparatorChar,
                            Path.AltDirectorySeparatorChar
                        },
                        StringSplitOptions.None)[0];

                    return !string.Equals(
                        firstPart,
                        "updater",
                        StringComparison.OrdinalIgnoreCase);
                })
                .ToArray();

            if (newFiles.Length == 0)
                throw new InvalidDataException("No hay archivos para instalar.");

            if (Directory.Exists(backupPath) &&
                Directory.EnumerateFileSystemEntries(backupPath).Any())
            {
                throw new IOException(
                    "La carpeta de copia de seguridad ya contiene archivos. " +
                    "Utiliza una carpeta nueva.");
            }

            Console.WriteLine("Esperando a que se cierre el launcher...");

            try
            {
                using Process launcherProcess = Process.GetProcessById(launcherPid);
                await launcherProcess.WaitForExitAsync();
            }
            catch (ArgumentException)
            {
                // El proceso ya terminó.
            }

            Console.WriteLine("Preparando copia de seguridad...");

            Directory.CreateDirectory(backupPath);

            var changedFiles = new List<string>();
            var backedUpFiles = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

            try
            {
                // Primero hacemos copia de todos los archivos que vamos a reemplazar.
                foreach (string sourceFile in newFiles)
                {
                    string relativePath = Path.GetRelativePath(
                        stagingPath,
                        sourceFile);

                    string destinationFile = Path.GetFullPath(
                        Path.Combine(targetPath, relativePath));

                    string targetRoot = Path.GetFullPath(targetPath)
                                        + Path.DirectorySeparatorChar;

                    if (!destinationFile.StartsWith(
                            targetRoot,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException(
                            "Un archivo intenta salir de la carpeta de instalación.");
                    }

                    if (File.Exists(destinationFile))
                    {
                        string backupFile = Path.Combine(
                            backupPath,
                            relativePath);

                        Directory.CreateDirectory(
                            Path.GetDirectoryName(backupFile)!);

                        File.Copy(destinationFile, backupFile, overwrite: true);
                        backedUpFiles.Add(relativePath);
                    }
                }

                Console.WriteLine("Instalando archivos...");

                foreach (string sourceFile in newFiles)
                {
                    string relativePath = Path.GetRelativePath(
                        stagingPath,
                        sourceFile);

                    string destinationFile = Path.GetFullPath(
                        Path.Combine(targetPath, relativePath));

                    Directory.CreateDirectory(
                        Path.GetDirectoryName(destinationFile)!);

                    File.Copy(sourceFile, destinationFile, overwrite: true);
                    changedFiles.Add(relativePath);
                }
            }
            catch
            {
                Console.WriteLine("Error. Intentando restaurar archivos...");

                foreach (string relativePath in changedFiles.AsEnumerable().Reverse())
                {
                    string destinationFile = Path.Combine(
                        targetPath,
                        relativePath);

                    if (backedUpFiles.Contains(relativePath))
                    {
                        string backupFile = Path.Combine(
                            backupPath,
                            relativePath);

                        if (File.Exists(backupFile))
                        {
                            File.Copy(
                                backupFile,
                                destinationFile,
                                overwrite: true);
                        }
                    }
                    else if (File.Exists(destinationFile))
                    {
                        File.Delete(destinationFile);
                    }
                }

                throw;
            }

            Console.WriteLine("Actualización instalada correctamente.");
            Console.WriteLine($"Copia de seguridad: {backupPath}");

            Console.WriteLine("Reiniciando launcher...");

            var startInfo = new ProcessStartInfo
            {
                FileName = launcherExe,
                WorkingDirectory = Path.GetDirectoryName(launcherExe)!,
                UseShellExecute = true
            };

            Process.Start(startInfo);
        }
        finally
        {
            try
            {
                if (Directory.Exists(stagingPath))
                    Directory.Delete(stagingPath, recursive: true);
            }
            catch (IOException)
            {
                Console.WriteLine(
                    "Aviso: no se pudo eliminar la carpeta temporal.");
            }
            catch (UnauthorizedAccessException)
            {
                Console.WriteLine(
                    "Aviso: faltan permisos para limpiar temporales.");
            }
        }
    }
}
