using ChernobylZLauncher.Core.Logging;

namespace ChernobylZLauncher.Core.Auth;

public class AuthSession
{
    public string MinecraftToken { get; set; } = string.Empty;
    public MinecraftProfile Profile { get; set; } = new();
}

// junta todo el login en un solo paso:
// 1) si hay sesion guardada, la intenta usar (sin pedirle nada al jugador)
// 2) si no sirve o no existe, hace el login con codigo de siempre
// 3) guarda el refresh token nuevo para la proxima
public class AuthSessionService
{
    private readonly MicrosoftAuthService _auth;
    private readonly TokenStore _store;
    private readonly LauncherLogService? _log;

    public AuthSessionService(MicrosoftAuthService auth, TokenStore store, LauncherLogService? log = null)
    {
        _auth = auth;
        _store = store;
        _log = log;
    }

    // onDeviceCode es para que la CLI o la UI le muestren el codigo al jugador como quieran
    public async Task<AuthSession> LoginAsync(
        Func<DeviceCodeInfo, Task> onDeviceCode,
        CancellationToken cancellationToken = default)
    {
        MicrosoftTokens? tokens = null;

        var savedRefreshToken = _store.Load();
        if (savedRefreshToken != null)
        {
            tokens = await _auth.RefreshTokensAsync(savedRefreshToken);

            if (tokens == null)
            {
                _store.Clear();
            }
        }

        if (tokens == null)
        {
            var deviceCode = await _auth.RequestDeviceCodeAsync();
            await onDeviceCode(deviceCode);
            tokens = await _auth.PollForTokensAsync(deviceCode, cancellationToken);
        }

        // microsoft rota el refresh token, asi que siempre guardamos el mas nuevo
        if (tokens.RefreshToken != null)
        {
            _store.Save(tokens.RefreshToken);
        }

        var (xblToken, _) = await _auth.AuthenticateWithXboxLiveAsync(tokens.AccessToken);
        var (xstsToken, xstsHash) = await _auth.AuthenticateWithXstsAsync(xblToken);
        var minecraftToken = await _auth.LoginWithMinecraftAsync(xstsToken, xstsHash);
        var profile = await _auth.GetMinecraftProfileAsync(minecraftToken);

        return new AuthSession
        {
            MinecraftToken = minecraftToken,
            Profile = profile
        };
    }
}
