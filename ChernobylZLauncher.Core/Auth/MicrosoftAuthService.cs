using System.Net.Http.Json;
using System.Text.Json.Serialization;
using ChernobylZLauncher.Core.Logging;

namespace ChernobylZLauncher.Core.Auth;

public class DeviceCodeInfo
{
    [JsonPropertyName("device_code")]
    public string DeviceCode { get; set; } = string.Empty;

    [JsonPropertyName("user_code")]
    public string UserCode { get; set; } = string.Empty;

    [JsonPropertyName("verification_uri")]
    public string VerificationUri { get; set; } = string.Empty;

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("interval")]
    public int Interval { get; set; }
}

internal class DeviceTokenResponse
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

internal class XboxAuthResponse
{
    [JsonPropertyName("Token")]
    public string? Token { get; set; }

    [JsonPropertyName("DisplayClaims")]
    public XboxDisplayClaims? DisplayClaims { get; set; }
}

internal class XboxDisplayClaims
{
    [JsonPropertyName("xui")]
    public XboxUserInfo[]? Xui { get; set; }
}

internal class XboxUserInfo
{
    [JsonPropertyName("uhs")]
    public string Uhs { get; set; } = string.Empty;
}

internal class MinecraftLoginResponse
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }
}

public class MinecraftProfile
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

public class MicrosoftAuthService
{
    // el id de azure, no preguntes como se saca esto
    private const string ClientId = "2032c77f-379f-445d-94ac-868d35951424";
    private const string Scope = "XboxLive.signin offline_access";

    private readonly HttpClient _httpClient;
    private readonly LauncherLogService? _log;

    public MicrosoftAuthService(HttpClient? httpClient = null, LauncherLogService? log = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _log = log;
    }

    public async Task<DeviceCodeInfo> RequestDeviceCodeAsync()
    {
        _log?.Info("solicitando codigo de dispositivo a microsoft...");

        var response = await _httpClient.PostAsync(
            "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["scope"] = Scope
            }));

        response.EnsureSuccessStatusCode();

        var info = await response.Content.ReadFromJsonAsync<DeviceCodeInfo>();

        if (info == null)
        {
            throw new InvalidOperationException("No se pudo obtener el codigo de dispositivo");
        }

        _log?.Success($"codigo generado: {info.UserCode}");

        return info;
    }

    public async Task<string> PollForAccessTokenAsync(DeviceCodeInfo info, CancellationToken cancellationToken = default)
    {
        var intervalSeconds = Math.Max(info.Interval, 5);
        var deadline = DateTime.UtcNow.AddSeconds(info.ExpiresIn);

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), cancellationToken);

            var response = await _httpClient.PostAsync(
                "https://login.microsoftonline.com/consumers/oauth2/v2.0/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                    ["client_id"] = ClientId,
                    ["device_code"] = info.DeviceCode
                }));

            var body = await response.Content.ReadFromJsonAsync<DeviceTokenResponse>();

            if (response.IsSuccessStatusCode && body?.AccessToken != null)
            {
                _log?.Success("sesion de microsoft iniciada");
                return body.AccessToken;
            }

            if (body?.Error is not ("authorization_pending" or "slow_down"))
            {
                throw new InvalidOperationException($"Error de autenticacion: {body?.Error}");
            }

            // el wey todavia no le da click al login, paciencia
        }

        throw new TimeoutException("El codigo expiro antes de que el jugador iniciara sesion");
    }

    public async Task<(string Token, string UserHash)> AuthenticateWithXboxLiveAsync(string microsoftAccessToken)
    {
        _log?.Info("tocando la puerta de xbox live...");

        var payload = new
        {
            Properties = new
            {
                AuthMethod = "RPS",
                SiteName = "user.auth.xboxlive.com",
                RpsTicket = $"d={microsoftAccessToken}"
            },
            RelyingParty = "http://auth.xboxlive.com",
            TokenType = "JWT"
        };

        const int maxAttempts = 3;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var response = await _httpClient.PostAsJsonAsync(
                "https://user.auth.xboxlive.com/user/authenticate",
                payload);

            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadFromJsonAsync<XboxAuthResponse>();

                if (body?.Token != null && body.DisplayClaims?.Xui?.Length > 0)
                {
                    _log?.Success("xbox live nos dejo pasar");
                    return (body.Token, body.DisplayClaims.Xui[0].Uhs);
                }
            }

            if (attempt < maxAttempts)
            {
                _log?.Warning($"xbox live nos tiro la puerta en la cara (intento {attempt}/{maxAttempts}), reintentando...");
                await Task.Delay(TimeSpan.FromSeconds(3 * attempt));
            }
        }

        throw new InvalidOperationException("xbox live no responde bien despues de varios intentos");
    }

    public async Task<(string Token, string UserHash)> AuthenticateWithXstsAsync(string xboxLiveToken)
    {
        _log?.Info("pidiendole permiso a xsts, el gatekeeper...");

        var payload = new
        {
            Properties = new
            {
                SandboxId = "RETAIL",
                UserTokens = new[] { xboxLiveToken }
            },
            RelyingParty = "rp://api.minecraftservices.com/",
            TokenType = "JWT"
        };

        const int maxAttempts = 3;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var response = await _httpClient.PostAsJsonAsync(
                "https://xsts.auth.xboxlive.com/xsts/authorize",
                payload);

            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadFromJsonAsync<XboxAuthResponse>();

                if (body?.Token != null && body.DisplayClaims?.Xui?.Length > 0)
                {
                    _log?.Success("xsts nos dio luz verde");
                    return (body.Token, body.DisplayClaims.Xui[0].Uhs);
                }
            }

            if (attempt < maxAttempts)
            {
                _log?.Warning($"xsts nos ignoro (intento {attempt}/{maxAttempts}), reintentando...");
                await Task.Delay(TimeSpan.FromSeconds(3 * attempt));
            }
        }

        throw new InvalidOperationException("xsts no responde bien despues de varios intentos");
    }

    public async Task<string> LoginWithMinecraftAsync(string xstsToken, string userHash)
    {
        _log?.Info("tocandole la puerta a minecraft con la carta de xsts...");

        var payload = new
        {
            identityToken = $"XBL3.0 x={userHash};{xstsToken}"
        };

        var response = await _httpClient.PostAsJsonAsync(
            "https://api.minecraftservices.com/authentication/login_with_xbox",
            payload);

        if (!response.IsSuccessStatusCode)
        {
            var errorText = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"minecraft dijo: {(int)response.StatusCode} - {errorText}");
        }

        var body = await response.Content.ReadFromJsonAsync<MinecraftLoginResponse>();

        if (body?.AccessToken == null)
        {
            throw new InvalidOperationException("minecraft no nos dio token, raro");
        }

        _log?.Success("minecraft nos reconocio");

        return body.AccessToken;
    }

    public async Task<MinecraftProfile> GetMinecraftProfileAsync(string minecraftAccessToken)
    {
        _log?.Info("buscando quien eres tu en el sistema...");

        _httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", minecraftAccessToken);

        var response = await _httpClient.GetAsync("https://api.minecraftservices.com/minecraft/profile");

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException(
                "esta cuenta no tiene minecraft comprado. ni modo");
        }

        response.EnsureSuccessStatusCode();

        var profile = await response.Content.ReadFromJsonAsync<MinecraftProfile>();

        if (profile == null)
        {
            throw new InvalidOperationException("no se pudo leer el perfil, intenta de nuevo");
        }

        _log?.Success($"hola, {profile.Name}");

        return profile;
    }
}