using System.Net.Http.Json;
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
}