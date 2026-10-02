using ChernobylZLauncher.Core.Server;
using ChernobylZLauncher.Core.Mods;
using ChernobylZLauncher.Core.Logging;
using ChernobylZLauncher.Core.Auth;
using ChernobylZLauncher.Core.Runtime;

class Program
{
    static async Task Main(string[] args)
    {
        var log = new LauncherLogService();

        log.OnLogAdded += entry =>
        {
            var icon = entry.Level switch
            {
                LogLevel.Success => "🟢",
                LogLevel.Warning => "🟡",
                LogLevel.Error => "🔴",
                _ => "ℹ️"
            };

            Console.WriteLine($"{icon} {entry.Message}");
        };

        Console.WriteLine("=== Estado del servidor ===");
        var serverService = new ServerStatusService("chernobylz.refugenodes.com", 25836, log: log);
        var status = await serverService.CheckStatusAsync();

        Console.WriteLine("\n=== Mods ===");
        var manifestUrl = $"https://raw.githubusercontent.com/PapuBeto/ChernobylLauncher/main/manifest/manifest.json?cachebust={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        var manifest = await ModManifest.FromUrlAsync(manifestUrl);

        var modManager = new ModManagerService(log: log);
        var modsFolder = Path.Combine(AppContext.BaseDirectory, "mods");

        var checkResults = modManager.CheckMods(modsFolder, manifest);
        foreach (var result in checkResults)
        {
            Console.WriteLine($"  {result.FileName}: {result.Status}");
        }

        var progress = new Progress<(string FileName, double Percent)>(p =>
            Console.WriteLine($"  {p.FileName}: {p.Percent:F0}%"));

        await modManager.SyncModsAsync(modsFolder, manifest, progress);

        /*
        Console.WriteLine("\n=== Login Microsoft ===");

        var authService = new MicrosoftAuthService(log: log);
        var deviceCode = await authService.RequestDeviceCodeAsync();

        Console.WriteLine($"Ve a: {deviceCode.VerificationUri}");
        Console.WriteLine($"Y pon este codigo: {deviceCode.UserCode}");
        Console.WriteLine("Esperando a que inicies sesion...");

        var msToken = await authService.PollForAccessTokenAsync(deviceCode);

        var (xblToken, xblHash) = await authService.AuthenticateWithXboxLiveAsync(msToken);
        var (xstsToken, xstsHash) = await authService.AuthenticateWithXstsAsync(xblToken);
        var mcToken = await authService.LoginWithMinecraftAsync(xstsToken, xstsHash);
        var profile = await authService.GetMinecraftProfileAsync(mcToken);

        Console.WriteLine($"\nJugador: {profile.Name}");
        Console.WriteLine($"UUID: {profile.Id}");
        */

        Console.WriteLine("\n=== Java ===");

        var javaService = new JavaService(log: log);
        var javaInfo = await javaService.DetectSystemJavaAsync();

        if (javaInfo.IsInstalled)
        {
            Console.WriteLine($"Version detectada: Java {javaInfo.MajorVersion}");
            Console.WriteLine($"Compatible: {(javaInfo.IsCompatible ? "si" : "no, necesitamos descargar la correcta")}");
        }
        else
        {
            Console.WriteLine("No se encontro java instalado");
        }

        Console.WriteLine("\n✅ Listo");
    }
}