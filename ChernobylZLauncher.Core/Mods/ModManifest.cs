using System.Text.Json;

namespace ChernobylZLauncher.Core.Mods;

public class ModManifest
{
    public string ModpackVersion { get; set; } = string.Empty;
    public List<ModInfo> Mods { get; set; } = new();

    public static ModManifest FromJson(string json)
    {
        return JsonSerializer.Deserialize<ModManifest>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? new ModManifest();
    }

    public static async Task<ModManifest> FromUrlAsync(string url, HttpClient? httpClient = null, CancellationToken cancellationToken = default)
    {
        var client = httpClient ?? new HttpClient();
        var json = await client.GetStringAsync(url, cancellationToken);
        return FromJson(json);
    }
}