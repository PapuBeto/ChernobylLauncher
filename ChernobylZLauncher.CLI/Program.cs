using ChernobylZLauncher.Core.Server;
using ChernobylZLauncher.Core.Mods;
using ChernobylZLauncher.Core.Logging;
using ChernobylZLauncher.Core.Auth;
using ChernobylZLauncher.Core.Runtime;
using ChernobylZLauncher.Core.Minecraft;

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

        Console.WriteLine("\n=== Login Microsoft ===");

        var authService = new MicrosoftAuthService(log: log);
        var tokenStore = new TokenStore(log: log);
        var sessionService = new AuthSessionService(authService, tokenStore, log);

        var session = await sessionService.LoginAsync(deviceCode =>
        {
            Console.WriteLine($"Ve a: {deviceCode.VerificationUri}");
            Console.WriteLine($"Y pon este codigo: {deviceCode.UserCode}");
            Console.WriteLine("Esperando a que inicies sesion...");
            return Task.CompletedTask;
        });

        Console.WriteLine($"\nJugador: {session.Profile.Name}");
        Console.WriteLine($"UUID: {session.Profile.Id}");

        Console.WriteLine("\n=== Java ===");

        var javaService = new JavaService(log: log);
        var runtimeFolder = Path.Combine(AppContext.BaseDirectory, "runtime");

        var javaProgress = new Progress<double>(p =>
            Console.WriteLine($"  java 17: {p:F0}%"));

        var javaInfo = await javaService.EnsureJavaAsync(runtimeFolder, javaProgress);

        if (javaInfo.IsCompatible)
        {
            Console.WriteLine($"Java {javaInfo.MajorVersion} listo: {javaInfo.Path}");
        }
        else
        {
            Console.WriteLine("No se pudo conseguir java 17");
        }

        Console.WriteLine("\n=== Minecraft ===");

        var minecraftFolder = Path.Combine(AppContext.BaseDirectory, "minecraft");
        var installer = new MinecraftInstallerService(log: log);

        var installProgress = new Progress<InstallProgress>(p =>
            Console.WriteLine($"  {p.Stage}: {p.Done}/{p.Total}"));

        await installer.InstallVanillaAsync(minecraftFolder, "1.20.1", installProgress);

        Console.WriteLine("\n✅ Listo");
    }
}