using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using ChernobylZLauncher.Core.Logging;

namespace ChernobylZLauncher.Core.Minecraft;

// arma el comando de java y arranca el juego, aqui es donde se hace la magia (odio java)
public class GameLauncherService
{
    private readonly LauncherLogService? _log;

    public GameLauncherService(LauncherLogService? log = null)
    {
        _log = log;
    }

    // regresa el codigo de salida del juego (0 = cerro normal)
    public async Task<int> LaunchAsync(
        string gameRoot,
        string javaPath,
        string mcVersion,
        string versionId,
        string playerName,
        string uuid,
        string accessToken,
        int ramMb = 4096,
        string? quickPlayServer = null,
        CancellationToken ct = default)
    {
        var versionsDir = Path.Combine(gameRoot, "versions");
        var forgeJsonPath = Path.Combine(versionsDir, versionId, $"{versionId}.json");
        var vanillaJsonPath = Path.Combine(versionsDir, mcVersion, $"{mcVersion}.json");
        var vanillaJar = Path.Combine(versionsDir, mcVersion, $"{mcVersion}.jar");

        if (!File.Exists(forgeJsonPath))
            throw new FileNotFoundException("no encuentro el json de forge, ya instalaste forge?", forgeJsonPath);
        if (!File.Exists(vanillaJsonPath))
            throw new FileNotFoundException("no encuentro el json de vanilla", vanillaJsonPath);
        if (!File.Exists(vanillaJar))
            throw new FileNotFoundException("no encuentro el jar de minecraft", vanillaJar);

        var forge = JsonNode.Parse(File.ReadAllText(forgeJsonPath))!.AsObject();
        var vanilla = JsonNode.Parse(File.ReadAllText(vanillaJsonPath))!.AsObject();

        var libsDir = Path.Combine(gameRoot, "libraries");
        var assetsDir = Path.Combine(gameRoot, "assets");
        var nativesDir = Path.Combine(gameRoot, "natives", versionId);
        Directory.CreateDirectory(nativesDir);

        // librerias: primero las de vanilla, luego las de forge (si se repiten, gana forge)
        var libs = new Dictionary<string, string>();
        AgregarLibrerias(vanilla, libsDir, libs);
        AgregarLibrerias(forge, libsDir, libs);

        var rutas = libs.Values.ToList();
        rutas.Add(vanillaJar);
        var sep = ";"; // solo windows, aqui no hay linux que valga
        var classpath = string.Join(sep, rutas);

        _log?.Info($"classpath armado con {rutas.Count} jars");

        var jvmArgs = new List<string>();
        var gameArgs = new List<string>();
        AgregarArgumentos(vanilla["arguments"]?["jvm"], jvmArgs);
        AgregarArgumentos(forge["arguments"]?["jvm"], jvmArgs);
        AgregarArgumentos(vanilla["arguments"]?["game"], gameArgs);
        AgregarArgumentos(forge["arguments"]?["game"], gameArgs);

        var assetIndex = vanilla["assetIndex"]?["id"]?.GetValue<string>() ?? mcVersion;
        var mainClass = forge["mainClass"]?.GetValue<string>()
                        ?? vanilla["mainClass"]?.GetValue<string>()
                        ?? throw new InvalidOperationException("no hay mainClass en el json, raro raro");

        // OJO: version_name va con la version de vanilla, forge lo usa para ignorar el jar de mc
        var reemplazos = new Dictionary<string, string>
        {
            ["auth_player_name"] = playerName,
            ["auth_uuid"] = uuid,
            ["auth_access_token"] = accessToken,
            ["auth_xuid"] = "0",
            ["clientid"] = "0",
            ["user_type"] = "msa",
            ["user_properties"] = "{}",
            ["version_name"] = mcVersion,
            ["version_type"] = "release",
            ["game_directory"] = gameRoot,
            ["assets_root"] = assetsDir,
            ["assets_index_name"] = assetIndex,
            ["library_directory"] = libsDir,
            ["classpath_separator"] = sep,
            ["natives_directory"] = nativesDir,
            ["launcher_name"] = "ChernobylZLauncher",
            ["launcher_version"] = "0.1",
            ["classpath"] = classpath,
        };

        var comando = new List<string>
        {
            $"-Xmx{ramMb}M",
            $"-Xms{Math.Min(ramMb, 1024)}M",
        };
        comando.AddRange(jvmArgs.Select(a => Reemplazar(a, reemplazos)));
        comando.Add(mainClass);
        comando.AddRange(gameArgs.Select(a => Reemplazar(a, reemplazos)));

        if (!string.IsNullOrWhiteSpace(quickPlayServer))
        {
            comando.Add("--quickPlayMultiplayer");
            comando.Add(quickPlayServer);
        }

        var psi = new ProcessStartInfo
        {
            FileName = javaPath,
            WorkingDirectory = gameRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in comando) psi.ArgumentList.Add(a);

        // el token NUNCA se imprime, que luego se lo roban
        var vista = string.Join(" ", comando).Replace(accessToken, "***");
        _log?.Info($"comando: java {vista.Substring(0, Math.Min(vista.Length, 400))}...");

        using var proceso = new Process { StartInfo = psi };

        void Linea(string? l)
        {
            if (string.IsNullOrWhiteSpace(l)) return;
            _log?.Info($"mc: {l.Replace(accessToken, "***")}");
        }

        proceso.OutputDataReceived += (_, e) => Linea(e.Data);
        proceso.ErrorDataReceived += (_, e) => Linea(e.Data);

        _log?.Info("arrancando minecraft, abre los ojos que ya viene la ventana");
        proceso.Start();
        proceso.BeginOutputReadLine();
        proceso.BeginErrorReadLine();

        try
        {
            await proceso.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { proceso.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        if (proceso.ExitCode == 0)
            _log?.Success("el juego se cerro normal, buena sesion");
        else
            _log?.Error($"el juego se cerro con codigo {proceso.ExitCode}, algo salio mal");

        return proceso.ExitCode;
    }

    private static string Reemplazar(string texto, Dictionary<string, string> reemplazos)
    {
        foreach (var kv in reemplazos)
            texto = texto.Replace("${" + kv.Key + "}", kv.Value);
        return texto;
    }

    private void AgregarLibrerias(JsonObject json, string libsDir, Dictionary<string, string> destino)
    {
        if (json["libraries"] is not JsonArray arr) return;

        foreach (var nodo in arr)
        {
            if (nodo is not JsonObject lib) continue;
            if (!ReglasPermiten(lib["rules"])) continue;

            var nombre = lib["name"]?.GetValue<string>();
            if (nombre == null) continue;

            var ruta = lib["downloads"]?["artifact"]?["path"]?.GetValue<string>() ?? RutaDeNombre(nombre);
            var completa = Path.Combine(libsDir, ruta.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(completa))
            {
                _log?.Warning($"falta la libreria {nombre}, el wey no se descargo");
                continue;
            }

            destino[ClaveDe(nombre)] = completa;
        }
    }

    // grupo:artefacto(:classifier), sin version, para que forge pueda pisar versiones de vanilla
    private static string ClaveDe(string nombre)
    {
        var p = nombre.Split(':');
        var clave = p.Length >= 2 ? $"{p[0]}:{p[1]}" : nombre;
        if (p.Length > 3) clave += ":" + p[3];
        return clave;
    }

    private static string RutaDeNombre(string nombre)
    {
        var ext = "jar";
        var arroba = nombre.IndexOf('@');
        if (arroba >= 0)
        {
            ext = nombre[(arroba + 1)..];
            nombre = nombre[..arroba];
        }

        var p = nombre.Split(':');
        var grupo = p[0].Replace('.', '/');
        var artefacto = p[1];
        var version = p[2];
        var classifier = p.Length > 3 ? "-" + p[3] : "";
        return $"{grupo}/{artefacto}/{version}/{artefacto}-{version}{classifier}.{ext}";
    }

    private static void AgregarArgumentos(JsonNode? nodo, List<string> destino)
    {
        if (nodo is not JsonArray arr) return;

        foreach (var item in arr)
        {
            if (item is JsonValue v && v.TryGetValue<string>(out var s))
            {
                destino.Add(s);
            }
            else if (item is JsonObject obj)
            {
                if (!ReglasPermiten(obj["rules"])) continue;

                var valor = obj["value"];
                if (valor is JsonArray lista)
                {
                    foreach (var x in lista)
                        if (x is JsonValue xv && xv.TryGetValue<string>(out var xs)) destino.Add(xs);
                }
                else if (valor is JsonValue vv && vv.TryGetValue<string>(out var vs))
                {
                    destino.Add(vs);
                }
            }
        }
    }

    private static bool ReglasPermiten(JsonNode? reglas)
    {
        if (reglas is not JsonArray arr) return true;

        var permitido = false;
        foreach (var r in arr)
        {
            if (r is not JsonObject regla) continue;
            if (!ReglaCoincide(regla)) continue;
            permitido = regla["action"]?.GetValue<string>() == "allow";
        }
        return permitido;
    }

    private static bool ReglaCoincide(JsonObject regla)
    {
        // las reglas de features (demo, resolucion, etc) no las usamos, se saltan
        if (regla["features"] != null) return false;

        if (regla["os"] is not JsonObject os) return true;

        var nombre = os["name"]?.GetValue<string>();
        if (nombre != null && nombre != "windows") return false;

        var arch = os["arch"]?.GetValue<string>();
        if (arch != null)
        {
            var miArch = RuntimeInformation.OSArchitecture switch
            {
                Architecture.X86 => "x86",
                Architecture.Arm64 => "arm64",
                _ => "x64"
            };
            if (arch != miArch) return false;
        }

        // reglas por version de windows las saltamos, no hacen falta
        if (os["version"] != null) return false;

        return true;
    }
}