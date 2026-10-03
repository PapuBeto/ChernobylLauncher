using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using ChernobylZLauncher.Core.Auth;
using ChernobylZLauncher.Core.Logging;
using ChernobylZLauncher.Core.Minecraft;
using ChernobylZLauncher.Core.Mods;
using ChernobylZLauncher.Core.Runtime;
using ChernobylZLauncher.Core.Server;
using ChernobylZLauncher.Core.Settings;
using Microsoft.Web.WebView2.Core;

namespace ChernobylZLauncher.UI;

public partial class MainWindow : Window
{
    const string McVersion = "1.20.1";
    const string ForgeVersion = "47.4.23";
    const string ServerHost = "chernobylz.refugenodes.com";
    const int ServerPort = 25836;

    // todo vive en appdata para que sobreviva a un dotnet clean (odio c#)
    private static readonly string DataRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ChernobylZLauncher");
    private static readonly string GameRoot = Path.Combine(DataRoot, "minecraft");
    private static readonly string RuntimeRoot = Path.Combine(DataRoot, "runtime");
    private static readonly string LogFile = Path.Combine(DataRoot, "launcher.log");

    private readonly LauncherLogService _log = new();
    private readonly SettingsService _settingsService;
    private readonly TokenStore _tokenStore;
    private readonly object _logLock = new();

    private bool _busy;
    private bool _statusLoopStarted;
    private double _lastPercent;

    public MainWindow()
    {
        InitializeComponent();

        Directory.CreateDirectory(DataRoot);
        try { File.WriteAllText(LogFile, $"=== launcher arrancado {DateTime.Now} ==={Environment.NewLine}"); } catch { }

        _settingsService = new SettingsService(log: _log);
        _tokenStore = new TokenStore(log: _log);

        // todo lo que loguea el core se guarda en launcher.log, porque en la ui no hay consola
        _log.OnLogAdded += entry =>
        {
            try
            {
                lock (_logLock)
                    File.AppendAllText(LogFile, $"[{DateTime.Now:HH:mm:ss}] {entry.Level}: {entry.Message}{Environment.NewLine}");
            }
            catch { }

            // los Info ya salen en la barra de progreso, aqui solo lo importante
            if (entry.Level != LogLevel.Info)
                Send(new { type = "log", level = entry.Level.ToString(), text = entry.Message });
        };

        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await WebView.EnsureCoreWebView2Async();

            var launcherFolder = Path.Combine(AppContext.BaseDirectory, "launcher");

            if (!Directory.Exists(launcherFolder))
            {
                MessageBox.Show($"No se encontró la carpeta:\n{launcherFolder}");
                return;
            }

            WebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "chernobylz.local",
                launcherFolder,
                CoreWebView2HostResourceAccessKind.Allow);

            // ojo: nos suscribimos ANTES de navegar, si no se pierde el "ready"
            WebView.CoreWebView2.WebMessageReceived += OnWebMessage;

            WebView.CoreWebView2.Navigate("https://chernobylz.local/launcher.html");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error al iniciar WebView2:\n{ex.Message}");
        }
    }

    // ---------- comunicacion con el html ----------

    private void Send(object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        Dispatcher.BeginInvoke(() =>
        {
            if (WebView.CoreWebView2 != null)
                WebView.CoreWebView2.PostWebMessageAsJson(json);
        });
    }

    private async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            string? type;
            int ramMb = 0;
            bool autoConnect = false;

            using (var doc = JsonDocument.Parse(e.WebMessageAsJson))
            {
                var root = doc.RootElement;
                type = root.GetProperty("type").GetString();

                if (type == "saveSettings")
                {
                    ramMb = root.GetProperty("ramMb").GetInt32();
                    autoConnect = root.GetProperty("autoConnect").GetBoolean();
                }
            }

            switch (type)
            {
                case "ready":
                    SendState();

                    // el chequeo del server corre una sola vez, aunque el html mande "ready" otra vez
                    if (!_statusLoopStarted)
                    {
                        _statusLoopStarted = true;
                        _ = ServerStatusLoopAsync();
                    }

                    // sesion guardada pero sin nombre: lo buscamos sin molestar al jugador
                    if (_tokenStore.Load() != null && string.IsNullOrEmpty(_settingsService.Load().LastPlayerName))
                        _ = FetchNameAsync();
                    break;

                case "login":
                    await DoLoginAsync();
                    break;

                case "logout":
                    _tokenStore.Clear();
                    var s = _settingsService.Load();
                    s.LastPlayerName = string.Empty;
                    _settingsService.Save(s);
                    _log.Success("sesion cerrada, la proxima vez te pide codigo otra vez");
                    Send(new { type = "loggedOut" });
                    break;

                case "saveSettings":
                    var cfg = _settingsService.Load();
                    cfg.RamMb = ramMb;
                    cfg.AutoConnect = autoConnect;
                    _settingsService.Save(cfg);
                    SendState();
                    break;

                case "play":
                    await PlayAsync();
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.Error($"se murio algo: {ex.Message}");
            Send(new { type = "error", message = ex.Message });
        }
    }

    // ---------- estado del server ----------

    // le pregunta al server cada 30 segundos y le avisa al html si esta prendido o apagado
    private async Task ServerStatusLoopAsync()
    {
        // sin log a proposito: si no, cada chequeo taparia el texto de la barra de abajo
        var statusService = new ServerStatusService(ServerHost, ServerPort);

        while (true)
        {
            try
            {
                var r = await statusService.CheckStatusAsync();
                Send(new
                {
                    type = "serverStatus",
                    online = r.IsOnline,
                    players = r.PlayersOnline,
                    max = r.MaxPlayers
                });
            }
            catch
            {
                // el servicio ya atrapa sus errores, esto es por si acaso
                Send(new { type = "serverStatus", online = false, players = 0, max = 0 });
            }

            await Task.Delay(TimeSpan.FromSeconds(30));
        }
    }

    // true si minecraft vanilla y forge ya estan en la carpeta del juego
    private static bool IsInstalled()
    {
        var forgeId = $"{McVersion}-forge-{ForgeVersion}";
        var forgeJson = Path.Combine(GameRoot, "versions", forgeId, $"{forgeId}.json");
        var vanillaJar = Path.Combine(GameRoot, "versions", McVersion, $"{McVersion}.jar");
        return File.Exists(forgeJson) && File.Exists(vanillaJar);
    }

    private void SendState()
    {
        var s = _settingsService.Load();
        Send(new
        {
            type = "state",
            loggedIn = _tokenStore.Load() != null,
            playerName = s.LastPlayerName,
            installed = IsInstalled(),
            ramMb = s.RamMb,
            minRam = SettingsService.MinRamMb,
            maxRam = SettingsService.MaxRamMb(),
            autoConnect = s.AutoConnect,
            mc = McVersion,
            forge = ForgeVersion
        });
    }

    // ---------- login ----------

    private async Task<AuthSession> LoginInternalAsync()
    {
        var authService = new MicrosoftAuthService(log: _log);
        var sessionService = new AuthSessionService(authService, _tokenStore, _log);

        // con sesion guardada entra solo y nunca llama al callback del codigo
        return await sessionService.LoginAsync(deviceCode =>
        {
            var uri = deviceCode.VerificationUri?.ToString() ?? "https://microsoft.com/link";
            var code = deviceCode.UserCode?.ToString() ?? "";

            Send(new { type = "deviceCode", uri, code });

            Dispatcher.Invoke(() =>
            {
                try { Clipboard.SetText(code); } catch { }
            });

            try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch { }

            return Task.CompletedTask;
        });
    }

    private async Task DoLoginAsync()
    {
        try
        {
            var session = await LoginInternalAsync();

            var s = _settingsService.Load();
            s.LastPlayerName = session.Profile.Name;
            _settingsService.Save(s);

            Send(new { type = "loggedIn", name = session.Profile.Name });
        }
        catch (Exception ex)
        {
            _log.Error($"fallo el login: {ex.Message}");
            Send(new { type = "loginFailed", message = ex.Message });
        }
    }

    // saca el nombre del jugador con la sesion guardada, sin abrir navegador ni nada
    private async Task FetchNameAsync()
    {
        if (_busy) return;

        try
        {
            var authService = new MicrosoftAuthService(log: _log);
            var sessionService = new AuthSessionService(authService, _tokenStore, _log);

            // si la sesion ya no sirve, el callback truena a proposito y no se abre nada
            var session = await sessionService.LoginAsync(
                _ => throw new InvalidOperationException("la sesion guardada ya vencio"));

            var s = _settingsService.Load();
            s.LastPlayerName = session.Profile.Name;
            _settingsService.Save(s);

            Send(new { type = "loggedIn", name = session.Profile.Name });
        }
        catch (Exception ex)
        {
            _log.Warning($"no pude sacar el nombre del jugador: {ex.Message}");

            // AuthSessionService borra el token si ya no servia, entonces toca login de nuevo
            if (_tokenStore.Load() == null)
                Send(new { type = "loggedOut" });
        }
    }

    // ---------- jugar ----------

    // el porcentaje solo sube, porque cada etapa del instalador reinicia su contador
    private void Report(double percent, string text)
    {
        if (percent > _lastPercent) _lastPercent = percent;
        Send(new { type = "progress", percent = Math.Round(_lastPercent, 1), text });
    }

    private async Task PlayAsync()
    {
        if (_busy) return;
        _busy = true;
        _lastPercent = 0;
        Send(new { type = "playState", state = "working" });

        try
        {
            var settings = _settingsService.Load();

            // 1) mods
            Report(2, "Sincronizando mods...");
            var manifestUrl = $"https://raw.githubusercontent.com/PapuBeto/ChernobylLauncher/main/manifest/manifest.json?cachebust={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
            var manifest = await ModManifest.FromUrlAsync(manifestUrl);

            var modsFolder = Path.Combine(GameRoot, "mods");
            Directory.CreateDirectory(modsFolder);

            var modManager = new ModManagerService(log: _log);
            var modsProgress = new Progress<(string FileName, double Percent)>(p =>
                Report(2 + p.Percent * 0.13, $"Mod: {p.FileName}"));
            await modManager.SyncModsAsync(modsFolder, manifest, modsProgress);

            // 2) sesion fresca (el token de minecraft caduca, asi que lo renovamos cada vez)
            Report(15, "Iniciando sesión...");
            var session = await LoginInternalAsync();

            var s = _settingsService.Load();
            s.LastPlayerName = session.Profile.Name;
            _settingsService.Save(s);
            Send(new { type = "loggedIn", name = session.Profile.Name });

            // 3) java
            Report(18, "Revisando Java 17...");
            var javaService = new JavaService(log: _log);
            var javaProgress = new Progress<double>(p => Report(18 + p * 0.12, $"Java 17: {p:F0}%"));
            var javaInfo = await javaService.EnsureJavaAsync(RuntimeRoot, javaProgress);

            if (!javaInfo.IsCompatible || string.IsNullOrEmpty(javaInfo.Path))
                throw new InvalidOperationException("no se pudo conseguir java 17, sin eso no hay juego");

            // 4) minecraft vanilla
            Report(30, "Instalando Minecraft...");
            var installer = new MinecraftInstallerService(log: _log);
            var installProgress = new Progress<InstallProgress>(p =>
            {
                var fraccion = p.Total > 0 ? (double)p.Done / p.Total : 0;
                Report(30 + fraccion * 40, $"{p.Stage}: {p.Done}/{p.Total}");
            });
            await installer.InstallVanillaAsync(GameRoot, McVersion, installProgress);

            // 5) forge
            Report(72, "Instalando Forge (tarda un rato)...");
            var forgeInstaller = new ForgeInstallerService(log: _log);
            var forgeId = await forgeInstaller.InstallForgeAsync(GameRoot, javaInfo.Path, McVersion, ForgeVersion);

            // ya quedo instalado, el boton cambia de INSTALAR a JUGAR
            Send(new { type = "installed" });

            // 6) a jugar
            Report(95, "Arrancando Minecraft...");
            Send(new { type = "playState", state = "playing" });

            var launcher = new GameLauncherService(log: _log);
            var exitCode = await launcher.LaunchAsync(
                GameRoot,
                javaInfo.Path,
                McVersion,
                forgeId,
                session.Profile.Name,
                session.Profile.Id.ToString(),
                session.MinecraftToken,
                ramMb: settings.RamMb,
                quickPlayServer: settings.AutoConnect ? $"{ServerHost}:{ServerPort}" : null);

            if (exitCode == 0)
            {
                _lastPercent = 0;
                Report(100, "Listo para jugar");
            }
            else
            {
                Send(new { type = "error", message = $"el juego se cerró con código {exitCode}, revisa launcher.log" });
            }
        }
        catch (Exception ex)
        {
            _log.Error($"se murio algo: {ex.Message}");
            Send(new { type = "error", message = ex.Message });
        }
        finally
        {
            _busy = false;
            Send(new { type = "playState", state = "idle" });
        }
    }
}