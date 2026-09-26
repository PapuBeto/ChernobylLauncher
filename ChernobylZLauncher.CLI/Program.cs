using ChernobylZLauncher.Core.Server;
using ChernobylZLauncher.Core.Mods;

class Program
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("☢️ Comprobando estado del servidor de ChernobylZ...");

        // Instanciamos nuestro servicio apuntando al dominio del server
        var serverService = new ServerStatusService("chernobylz.refugenodes.com", 25836);

        var status = await serverService.CheckStatusAsync();

        if (status.IsOnline)
        {
            Console.WriteLine("🟢 Servidor ONLINE");
            Console.WriteLine($"👥 Jugadores: {status.PlayersOnline}/{status.MaxPlayers}");
        }
        else
        {
            Console.WriteLine("🔴 Servidor OFFLINE o inaccesible.");
        }

        Console.WriteLine("\n📦 Chequeando mods...");

        var manifestPath = Path.Combine(AppContext.BaseDirectory, "manifest.json");
        var manifestJson = await File.ReadAllTextAsync(manifestPath);
        var manifest = ModManifest.FromJson(manifestJson);

        var modManager = new ModManagerService();
        var modsFolder = Path.Combine(AppContext.BaseDirectory, "mods");

        var checkResults = modManager.CheckMods(modsFolder, manifest);
        foreach (var result in checkResults)
        {
            Console.WriteLine($"  {result.FileName}: {result.Status}");
        }

        Console.WriteLine("\n⬇️ Sincronizando mods faltantes...");

        var progress = new Progress<(string FileName, double Percent)>(p =>
            Console.WriteLine($"  {p.FileName}: {p.Percent:F0}%"));

        await modManager.SyncModsAsync(modsFolder, manifest, progress);

        Console.WriteLine("\n✅ ¡Listo!");
    }
}