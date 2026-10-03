using ChernobylZLauncher.Core.Server;
using ChernobylZLauncher.Core.Mods;
using ChernobylZLauncher.Core.Logging;
using ChernobylZLauncher.Core.Auth;
using ChernobylZLauncher.Core.Runtime;
using ChernobylZLauncher.Core.Minecraft;
using ChernobylZLauncher.Core.Settings;

class Program
{
    const string McVersion = "1.20.1";
    const string ForgeVersion = "47.4.23";
    const string ServerHost = "chernobylz.refugenodes.com";
    const int ServerPort = 25836;

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

        var settingsService = new SettingsService(log: log);
        var tokenStore = new TokenStore(log: log);

        // menu principal, aqui se queda el wey hasta que le de a salir
        while (true)
        {
            var settings = settingsService.Load();
            var cuenta = tokenStore.Load() != null ? "sesion guardada" : "sin sesion";

            Console.WriteLine("\n===== ChernobylZ Launcher =====");
            Console.WriteLine($"Cuenta: {cuenta} | RAM: {settings.RamMb} MB | Conectar directo: {(settings.AutoConnect ? "si" : "no")}");
            Console.WriteLine("1) Jugar");
            Console.WriteLine("2) Cambiar RAM");
            Console.WriteLine("3) Conectar directo al server (si/no)");
            Console.WriteLine("4) Cerrar sesion");
            Console.WriteLine("5) Salir");
            Console.Write("> ");

            switch (Console.ReadLine()?.Trim())
            {
                case "1":
                    try
                    {
                        await Jugar(log, settings, tokenStore);
                    }
                    catch (Exception ex)
                    {
                        log.Error($"se murio algo: {ex.Message}");
                    }
                    break;

                case "2":
                    CambiarRam(settingsService, settings);
                    break;

                case "3":
                    settings.AutoConnect = !settings.AutoConnect;
                    settingsService.Save(settings);
                    break;

                case "4":
                    tokenStore.Clear();
                    log.Success("sesion cerrada, la proxima vez te pide codigo otra vez");
                    break;

                case "5":
                    return;

                default:
                    Console.WriteLine("esa opcion no existe, wey");
                    break;
            }
        }
    }

    static void CambiarRam(SettingsService settingsService, LauncherSettings settings)
    {
        var max = SettingsService.MaxRamMb();
        Console.WriteLine($"\nRAM actual: {settings.RamMb} MB (minimo {SettingsService.MinRamMb}, maximo {max})");
        Console.Write("Nueva RAM en MB (ej. 4096), Enter para cancelar: ");

        var texto = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(texto)) return;

        if (!int.TryParse(texto, out var mb))
        {
            Console.WriteLine("eso no es un numero, wey");
            return;
        }

        settings.RamMb = mb; // Save lo ajusta al minimo y maximo
        settingsService.Save(settings);
    }

    static async Task Jugar(LauncherLogService log, LauncherSettings settings, TokenStore tokenStore)
    {
        Console.WriteLine("\n=== Estado del servidor ===");
        var serverService = new ServerStatusService(ServerHost, ServerPort, log: log);
        var status = await serverService.CheckStatusAsync();

        Console.WriteLine("\n=== Mods ===");
        var manifestUrl = $"https://raw.githubusercontent.com/PapuBeto/ChernobylLauncher/main/manifest/manifest.json?cachebust={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        var manifest = await ModManifest.FromUrlAsync(manifestUrl);

        var modManager = new ModManagerService(log: log);
        var modsFolder = Path.Combine(AppContext.BaseDirectory, "minecraft", "mods");

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

        if (!javaInfo.IsCompatible || string.IsNullOrEmpty(javaInfo.Path))
        {
            Console.WriteLine("No se pudo conseguir java 17, no se puede seguir");
            return;
        }

        Console.WriteLine($"Java {javaInfo.MajorVersion} listo: {javaInfo.Path}");

        Console.WriteLine("\n=== Minecraft ===");

        var minecraftFolder = Path.Combine(AppContext.BaseDirectory, "minecraft");
        var installer = new MinecraftInstallerService(log: log);

        var installProgress = new Progress<InstallProgress>(p =>
            Console.WriteLine($"  {p.Stage}: {p.Done}/{p.Total}"));

        await installer.InstallVanillaAsync(minecraftFolder, McVersion, installProgress);

        Console.WriteLine("\n=== Forge ===");

        var forgeInstaller = new ForgeInstallerService(log: log);
        var forgeId = await forgeInstaller.InstallForgeAsync(
            minecraftFolder, javaInfo.Path, McVersion, ForgeVersion);

        Console.WriteLine($"Forge listo: {forgeId}");

        Console.WriteLine("\n=== Lanzando el juego ===");

        var launcher = new GameLauncherService(log: log);
        var exitCode = await launcher.LaunchAsync(
            minecraftFolder,
            javaInfo.Path,
            McVersion,
            forgeId,
            session.Profile.Name,
            session.Profile.Id.ToString(),
            session.MinecraftToken,
            ramMb: settings.RamMb,
            quickPlayServer: settings.AutoConnect ? $"{ServerHost}:{ServerPort}" : null);

        Console.WriteLine($"Juego cerrado con codigo {exitCode}");
    }
}