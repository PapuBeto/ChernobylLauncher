using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using ChernobylZLauncher.Core.Logging;

namespace ChernobylZLauncher.Core.Minecraft;

// instala forge usando el installer oficial, porque hacerlo a mano es sufrir (odio java)
public class ForgeInstallerService
{
    private static readonly HttpClient _http = new();
    private readonly LauncherLogService? _log;

    public ForgeInstallerService(LauncherLogService? log = null)
    {
        _log = log;
    }

    // regresa el id de la version, ej. "1.20.1-forge-47.4.23"
    public async Task<string> InstallForgeAsync(
        string gameRoot,
        string javaPath,
        string mcVersion,
        string forgeVersion,
        CancellationToken ct = default)
    {
        var versionId = $"{mcVersion}-forge-{forgeVersion}";
        var versionJson = Path.Combine(gameRoot, "versions", versionId, $"{versionId}.json");

        // si ya esta instalado no le movemos, el wey ya cumplio
        if (File.Exists(versionJson))
        {
            _log?.Success($"forge {forgeVersion} ya estaba instalado, ni pedo, seguimos");
            return versionId;
        }

        // el vanilla tiene que estar primero, el installer lo ocupa
        var vanillaJar = Path.Combine(gameRoot, "versions", mcVersion, $"{mcVersion}.jar");
        if (!File.Exists(vanillaJar))
            throw new FileNotFoundException($"falta el jar de minecraft {mcVersion}, corre primero la instalacion vanilla", vanillaJar);

        Directory.CreateDirectory(gameRoot);
        CrearLauncherProfiles(gameRoot);

        var fullVersion = $"{mcVersion}-{forgeVersion}";
        var installerUrl =
            $"https://maven.minecraftforge.net/net/minecraftforge/forge/{fullVersion}/forge-{fullVersion}-installer.jar";

        var installersDir = Path.Combine(gameRoot, "installers");
        Directory.CreateDirectory(installersDir);
        var installerPath = Path.Combine(installersDir, $"forge-{fullVersion}-installer.jar");

        await DescargarInstallerAsync(installerUrl, installerPath, forgeVersion, ct);

        _log?.Info("corriendo el installer de forge, esto tarda un ratote, no le piques");
        var salida = new List<string>();

        var psi = new ProcessStartInfo
        {
            FileName = javaPath,
            WorkingDirectory = gameRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-jar");
        psi.ArgumentList.Add(installerPath);
        psi.ArgumentList.Add("--installClient");
        psi.ArgumentList.Add(gameRoot);

        using var proceso = new Process { StartInfo = psi };

        void ManejarLinea(string? linea)
        {
            if (string.IsNullOrWhiteSpace(linea)) return;
            lock (salida) salida.Add(linea);
            // el installer escupe mil lineas de "Considering", esas ni las mostramos
            if (linea.Contains("Considering", StringComparison.OrdinalIgnoreCase)) return;
            _log?.Info($"forge: {linea}");
        }

        proceso.OutputDataReceived += (_, e) => ManejarLinea(e.Data);
        proceso.ErrorDataReceived += (_, e) => ManejarLinea(e.Data);

        proceso.Start();
        proceso.BeginOutputReadLine();
        proceso.BeginErrorReadLine();

        try
        {
            await proceso.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // si cancelan, matamos al installer para que no quede zombie
            try { proceso.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        if (proceso.ExitCode != 0)
        {
            string cola;
            lock (salida) cola = string.Join(Environment.NewLine, salida.TakeLast(25));
            throw new InvalidOperationException(
                $"el installer de forge termino con codigo {proceso.ExitCode}. ultimas lineas:{Environment.NewLine}{cola}");
        }

        if (!File.Exists(versionJson))
            throw new FileNotFoundException(
                "el installer dijo que todo bien pero no aparecio el json de la version, raro raro", versionJson);

        _log?.Success($"forge {forgeVersion} instalado, ya casi andamos jugando");
        return versionId;
    }

    // el installer exige que exista launcher_profiles.json, aunque sea vacio
    private void CrearLauncherProfiles(string gameRoot)
    {
        var ruta = Path.Combine(gameRoot, "launcher_profiles.json");
        if (File.Exists(ruta)) return;

        File.WriteAllText(ruta, "{\"profiles\":{},\"version\":3}", new UTF8Encoding(false));
        _log?.Info("cree el launcher_profiles.json minimo, el installer es medio chillon");
    }

    private async Task DescargarInstallerAsync(string url, string destino, string forgeVersion, CancellationToken ct)
    {
        // primero el sha1, si da 404 es que esa version de forge no existe
        _log?.Info("pidiendo el sha1 del installer a maven...");
        using var resSha = await _http.GetAsync(url + ".sha1", ct);

        if (resSha.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException(
                $"forge {forgeVersion} no existe en maven (404). prueba con otra version, la ultima que se vio fue 47.4.26");

        resSha.EnsureSuccessStatusCode();
        var shaEsperado = (await resSha.Content.ReadAsStringAsync(ct)).Trim().ToLowerInvariant();

        // si ya lo teniamos bajado y esta bien, no lo bajamos otra vez
        if (File.Exists(destino) && await Sha1DeArchivoAsync(destino, ct) == shaEsperado)
        {
            _log?.Info("el installer ya estaba descargado y bien, nos ahorramos la bajada");
            return;
        }

        _log?.Info("bajando el installer de forge...");
        var tmp = destino + ".tmp";

        using (var res = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            res.EnsureSuccessStatusCode();
            await using var origen = await res.Content.ReadAsStreamAsync(ct);
            await using var archivo = File.Create(tmp);
            await origen.CopyToAsync(archivo, ct);
        }

        var shaReal = await Sha1DeArchivoAsync(tmp, ct);
        if (shaReal != shaEsperado)
        {
            File.Delete(tmp);
            throw new InvalidOperationException(
                $"el sha1 del installer no cuadra (esperado {shaEsperado}, real {shaReal}). descarga chueca, intenta otra vez");
        }

        File.Move(tmp, destino, overwrite: true);
        _log?.Success("installer descargado y verificado, todo chido");
    }

    private static async Task<string> Sha1DeArchivoAsync(string ruta, CancellationToken ct)
    {
        await using var fs = File.OpenRead(ruta);
        var hash = await SHA1.HashDataAsync(fs, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}