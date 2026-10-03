using System.Text.Json;
using ChernobylZLauncher.Core.Logging;

namespace ChernobylZLauncher.Core.Settings;

// lo que el jugador puede configurar, igual que en curseforge
public class LauncherSettings
{
    public int RamMb { get; set; } = 4096;
    public bool AutoConnect { get; set; } = true;
    public string LastPlayerName { get; set; } = string.Empty;
}

// guarda y lee settings.json en %AppData%\ChernobylZLauncher\ (junto a session.dat)
public class SettingsService
{
    public const int MinRamMb = 2048;

    private readonly LauncherLogService? _log;
    private readonly string _path;

    public SettingsService(LauncherLogService? log = null)
    {
        _log = log;
        var carpeta = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ChernobylZLauncher");
        Directory.CreateDirectory(carpeta);
        _path = Path.Combine(carpeta, "settings.json");
    }

    // maximo de ram que le dejamos elegir: la de la compu menos 2 gb para windows
    public static int MaxRamMb()
    {
        var totalMb = (int)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024);
        var max = (totalMb - 2048) / 512 * 512;
        return Math.Max(MinRamMb, max);
    }

    public LauncherSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var s = JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(_path));
                if (s != null)
                {
                    s.RamMb = Math.Clamp(s.RamMb, MinRamMb, MaxRamMb());
                    return s;
                }
            }
        }
        catch (Exception ex)
        {
            _log?.Warning($"el settings.json estaba chueco ({ex.Message}), uso los de siempre");
        }
        return new LauncherSettings();
    }

    public void Save(LauncherSettings settings)
    {
        settings.RamMb = Math.Clamp(settings.RamMb, MinRamMb, MaxRamMb());
        File.WriteAllText(_path,
            JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        _log?.Success("configuracion guardada, quedo al tiro");
    }
}