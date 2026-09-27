using ChernobylZLauncher.Core.Server;
using ChernobylZLauncher.Core.Mods;
using ChernobylZLauncher.Core.Logging;

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

        Console.WriteLine("\n✅ Listo");
    }
}