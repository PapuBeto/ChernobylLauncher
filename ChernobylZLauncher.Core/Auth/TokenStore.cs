using System.Security.Cryptography;
using System.Text;
using ChernobylZLauncher.Core.Logging;

namespace ChernobylZLauncher.Core.Auth;

// guarda el refresh token en %AppData%\ChernobylZLauncher\session.dat
// va cifrado con DPAPI de windows: solo lo puede abrir tu usuario de windows en esta compu.
// ese token es como la llave de tu cuenta, por eso no va en texto plano ni dentro del repo
public class TokenStore
{
    private readonly string _filePath;
    private readonly LauncherLogService? _log;

    public TokenStore(string? filePath = null, LauncherLogService? log = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ChernobylZLauncher",
            "session.dat");
        _log = log;
    }

    public void Save(string refreshToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            _log?.Warning("guardar la sesion solo esta listo para windows, se queda sin guardar");
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);

        var encrypted = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(refreshToken),
            optionalEntropy: null,
            DataProtectionScope.CurrentUser);

        File.WriteAllBytes(_filePath, encrypted);
        _log?.Info("sesion guardada, la proxima ya no pedimos codigo");
    }

    public string? Load()
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(_filePath))
        {
            return null;
        }

        try
        {
            var encrypted = File.ReadAllBytes(_filePath);

            var decrypted = ProtectedData.Unprotect(
                encrypted,
                optionalEntropy: null,
                DataProtectionScope.CurrentUser);

            return Encoding.UTF8.GetString(decrypted);
        }
        catch (CryptographicException)
        {
            // archivo de otro usuario o danado, mejor ignorarlo
            _log?.Warning("la sesion guardada no se pudo abrir, la ignoramos");
            return null;
        }
    }

    public void Clear()
    {
        if (File.Exists(_filePath))
        {
            File.Delete(_filePath);
        }
    }
}
