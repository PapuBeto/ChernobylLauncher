using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
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

// los dos tokens que nos da microsoft: el access (dura poco) y el refresh (el pase para pedir otro access)
public record MicrosoftTokens(string AccessToken, string? RefreshToken);

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

    // xbox live espera los nombres en PascalCase (Properties, RpsTicket...).
    // PostAsJsonAsync los pasa a camelCase por defecto, por eso mandamos el json a mano
    // aqui se nos fue un buen rato por culpa de una mayuscula, odio c#
    private static readonly JsonSerializerOptions XboxJsonOptions = new() { PropertyNamingPolicy = null };

    private readonly HttpClient _httpClient;
    private readonly LauncherLogService? _log;

    public MicrosoftAuthService(HttpClient? httpClient = null, LauncherLogService? log = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _log = log;
    }

    public async Task<DeviceCodeInfo> RequestDeviceCodeAsync()
    {
        _log?.Info("a ver microsoft, pasame un codigo we...");

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

        _log?.Success($"simon, tu codigo es {info.UserCode}, no lo pierdas bro");

        return info;
    }

    public async Task<MicrosoftTokens> PollForTokensAsync(DeviceCodeInfo info, CancellationToken cancellationToken = default)
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
                _log?.Success("listo, microsoft ya nos reconocio, chido");
                return new MicrosoftTokens(body.AccessToken, body.RefreshToken);
            }

            if (body?.Error is not ("authorization_pending" or "slow_down"))
            {
                throw new InvalidOperationException($"Error de autenticacion: {body?.Error}");
            }

            // el wey todavia no le da click al login, paciencia
        }

        throw new TimeoutException("El codigo expiro antes de que el jugador iniciara sesion");
    }

    // se queda por si algo todavia lo usa, pero ya no tira el refresh token
    public async Task<string> PollForAccessTokenAsync(DeviceCodeInfo info, CancellationToken cancellationToken = default)
    {
        var tokens = await PollForTokensAsync(info, cancellationToken);
        return tokens.AccessToken;
    }

    // el refresh token es un pase para pedir otro access token sin que el jugador haga login otra vez.
    // microsoft lo cambia cada vez que lo usas, asi que hay que guardar el nuevo.
    // regresa null si el pase ya no sirve (caduco, lo revocaron, etc) y toca login normal
    public async Task<MicrosoftTokens?> RefreshTokensAsync(string refreshToken)
    {
        _log?.Info("a ver si la sesion guardada todavia jala...");

        var response = await _httpClient.PostAsync(
            "https://login.microsoftonline.com/consumers/oauth2/v2.0/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = ClientId,
                ["refresh_token"] = refreshToken,
                ["scope"] = Scope
            }));

        // si microsoft anda mal (5xx) no es culpa del pase, mejor tronar que borrar la sesion
        if ((int)response.StatusCode >= 500)
        {
            response.EnsureSuccessStatusCode();
        }

        DeviceTokenResponse? body = null;

        try
        {
            body = await response.Content.ReadFromJsonAsync<DeviceTokenResponse>();
        }
        catch (JsonException)
        {
            // respuesta rara, la tratamos como pase invalido
        }

        if (response.IsSuccessStatusCode && body?.AccessToken != null)
        {
            _log?.Success("la sesion guardada sigue viva, entramos sin codigo");
            return new MicrosoftTokens(body.AccessToken, body.RefreshToken ?? refreshToken);
        }

        _log?.Warning($"la sesion guardada ya no sirve ({body?.Error}), toca login normal");
        return null;
    }

    public async Task<(string Token, string UserHash)> AuthenticateWithXboxLiveAsync(string microsoftAccessToken)
    {
        _log?.Info("tocando en xbox live, a ver si nos abren...");

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

        var result = await SendXboxRequestAsync(
            "https://user.auth.xboxlive.com/user/authenticate",
            payload,
            stepName: "xbox live");

        _log?.Success("xbox live nos dejo pasar, tilin");

        return (result.Token!, result.DisplayClaims!.Xui![0].Uhs);
    }

    public async Task<(string Token, string UserHash)> AuthenticateWithXstsAsync(string xboxLiveToken)
    {
        _log?.Info("xsts nos esta checando, aguanta tantito...");

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

        var result = await SendXboxRequestAsync(
            "https://xsts.auth.xboxlive.com/xsts/authorize",
            payload,
            stepName: "xsts");

        _log?.Success("xsts dijo va, sin tanto rollo");

        return (result.Token!, result.DisplayClaims!.Xui![0].Uhs);
    }

    public async Task<string> LoginWithMinecraftAsync(string xstsToken, string userHash)
    {
        _log?.Info("ensenandole la carta de xsts a minecraft, ojala nos crea...");

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

        _log?.Success("minecraft dijo simon, ya estamos dentro");

        return body.AccessToken;
    }

    public async Task<MinecraftProfile> GetMinecraftProfileAsync(string minecraftAccessToken)
    {
        _log?.Info("a ver quien eres bro...");

        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", minecraftAccessToken);

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

        _log?.Success($"que onda {profile.Name}, ya quedo todo chido");

        return profile;
    }

    private Task<HttpResponseMessage> PostXboxAsync(string url, object payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, XboxJsonOptions),
                Encoding.UTF8,
                "application/json")
        };

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("x-xbl-contract-version", "1");

        return _httpClient.SendAsync(request);
    }

    // xbox live y xsts responden con la misma forma, asi que comparten esto.
    // solo reintenta si el fallo es del servidor (5xx o 429); un 400/401 es culpa
    // de lo que mandamos o de la cuenta, reintentar no sirve y mejor mostramos el error real
    private async Task<XboxAuthResponse> SendXboxRequestAsync(string url, object payload, string stepName)
    {
        const int maxAttempts = 3;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var response = await PostXboxAsync(url, payload);

            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadFromJsonAsync<XboxAuthResponse>();

                if (body?.Token != null && body.DisplayClaims?.Xui?.Length > 0)
                {
                    return body;
                }

                throw new InvalidOperationException($"{stepName} respondio ok pero sin token o sin user hash");
            }

            var statusCode = (int)response.StatusCode;
            var errorText = await response.Content.ReadAsStringAsync();
            var isServerSide = statusCode >= 500 || statusCode == 429;

            if (isServerSide && attempt < maxAttempts)
            {
                _log?.Warning($"{stepName} anda de malas ({statusCode}), intento {attempt}/{maxAttempts}, le damos otra vez...");
                await Task.Delay(TimeSpan.FromSeconds(3 * attempt));
                continue;
            }

            throw new InvalidOperationException($"{stepName} dijo: {statusCode} - {errorText}");
        }

        throw new InvalidOperationException($"{stepName} no responde bien despues de varios intentos");
    }
}