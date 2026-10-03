using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
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
    private static readonly string WebViewData = Path.Combine(DataRoot, "webview");

    private readonly LauncherLogService _log = new();
    private readonly SettingsService _settingsService;
    private readonly TokenStore _tokenStore;
    private readonly object _logLock = new();

    private bool _busy;
    private bool _statusLoopStarted;
    private double _lastPercent;
    private CancellationTokenSource? _cts;

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

        // al maximizar, el borde invisible se sale de la pantalla, asi que lo compensamos
        StateChanged += (_, _) =>
        {
            var max = WindowState == WindowState.Maximized;
            RootGrid.Margin = max ? new Thickness(8) : new Thickness(0);
            Send(new { type = "windowState", maximized = max });
        };

        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // permitimos que la musica arranque sola, sin que el jugador tenga que dar click
            var options = new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required");
            var env = await CoreWebView2Environment.CreateAsync(null, WebViewData, options);
            await WebView.EnsureCoreWebView2Async(env);

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

                // ---- botones y arrastre de la ventana ----
                case "windowReady":
                    Send(new { type = "windowState", maximized = WindowState == WindowState.Maximized });
                    break;

                case "minimize":
                    WindowState = WindowState.Minimized;
                    break;

                case "toggleMaximize":
                    WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                    break;

                case "closeWindow":
                    Close();
                    break;

                case "dragWindow":
                    // solo si el click sigue apretado y no esta maximizada
                    if (WindowState == WindowState.Normal && Mouse.LeftButton == MouseButtonState.Pressed)
                    {
                        try { DragMove(); } catch { }
                    }
                    break;

                case "login":
                    await DoLoginAsync();
                    break;

                case "cancel":
                    // sirve para la instalacion y para el login con codigo
                    _cts?.Cancel();
                    break;

                case "openGameFolder":
                    Directory.CreateDirectory(GameRoot);
                    Process.Start(new ProcessStartInfo(GameRoot) { UseShellExecute = true });
                    break;

                case "openLogs":
                    OpenLogs();
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
            Send(new { type = "error", message = FriendlyError(ex) });
        }
    }

    // ---------- carpetas y logs ----------

    // abre el explorador con launcher.log seleccionado (se reinicia cada vez que abres el launcher)
    private void OpenLogs()
    {
        if (File.Exists(LogFile))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{LogFile}\"") { UseShellExecute = true });
        else
            Process.Start(new ProcessStartInfo(DataRoot) { UseShellExecute = true });
    }

    // traduce los errores tecnicos a algo que el jugador entienda
    private static string FriendlyError(Exception ex)
    {
        if (ex is AggregateException agg && agg.InnerException != null)
            ex = agg.InnerException;

        switch (ex)
        {
            case HttpRequestException:
                return "No hay internet o el servidor de descargas no responde. Revisa tu conexión y vuelve a intentar.";
            case TaskCanceledException:
                return "La conexión tardó demasiado en responder, intenta otra vez.";
            case UnauthorizedAccessException:
                return "Windows no deja escribir en la carpeta del launcher. Cierra lo que la esté usando y reintenta.";
            // 112 = disco lleno, 39 = disco lleno (handle)
            case IOException io when (io.HResult & 0xFFFF) is 112 or 39:
                return "Se acabó el espacio en el disco. Libera espacio y vuelve a intentar.";
        }

        return ex.Message;
    }

    // ---------- ventana mientras se juega ----------

    // minimiza el launcher al abrir el juego y lo regresa al cerrarlo
    private void SetWindowForGame(bool playing)
    {
        Dispatcher.Invoke(() =>
        {
            if (playing)
            {
                WindowState = WindowState.Minimized;
            }
            else
            {
                WindowState = WindowState.Normal;
                Activate();
            }
        });
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

    private async Task<AuthSession> LoginInternalAsync(CancellationToken ct = default)
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
        }, ct);
    }

    private async Task DoLoginAsync()
    {
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            var session = await LoginInternalAsync(ct);

            var s = _settingsService.Load();
            s.LastPlayerName = session.Profile.Name;
            _settingsService.Save(s);

            Send(new { type = "loggedIn", name = session.Profile.Name });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _log.Warning("cancelaste el login, sin rollo");
            Send(new { type = "cancelled" });
            SendState();
        }
        catch (Exception ex)
        {
            _log.Error($"fallo el login: {ex.Message}");
            Send(new { type = "loginFailed", message = FriendlyError(ex) });
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

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            var settings = _settingsService.Load();

            // 1) mods
            Report(2, "Sincronizando mods...");
            var manifestUrl = $"https://raw.githubusercontent.com/PapuBeto/ChernobylLauncher/main/manifest/manifest.json?cachebust={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
            var manifest = await ModManifest.FromUrlAsync(manifestUrl);
            ct.ThrowIfCancellationRequested();

            var modsFolder = Path.Combine(GameRoot, "mods");
            Directory.CreateDirectory(modsFolder);

            var modManager = new ModManagerService(log: _log);
            var modsProgress = new Progress<(string FileName, double Percent)>(p =>
                Report(2 + p.Percent * 0.13, $"Mod: {p.FileName}"));
            await modManager.SyncModsAsync(modsFolder, manifest, modsProgress, ct);

            // 2) sesion fresca (el token de minecraft caduca, asi que lo renovamos cada vez)
            Report(15, "Iniciando sesión...");
            var session = await LoginInternalAsync(ct);

            var s = _settingsService.Load();
            s.LastPlayerName = session.Profile.Name;
            _settingsService.Save(s);
            Send(new { type = "loggedIn", name = session.Profile.Name });

            // 3) java
            Report(18, "Revisando Java 17...");
            var javaService = new JavaService(log: _log);
            var javaProgress = new Progress<double>(p => Report(18 + p * 0.12, $"Java 17: {p:F0}%"));
            var javaInfo = await javaService.EnsureJavaAsync(RuntimeRoot, javaProgress, ct);

            if (!javaInfo.IsCompatible || string.IsNullOrEmpty(javaInfo.Path))
                throw new InvalidOperationException("no se pudo conseguir java 17, sin eso no hay juego");

            // 4) minecraft vanilla (este paso todavia no se puede cortar a la mitad, solo entre pasos)
            ct.ThrowIfCancellationRequested();
            Report(30, "Instalando Minecraft...");
            var installer = new MinecraftInstallerService(log: _log);
            var installProgress = new Progress<InstallProgress>(p =>
            {
                var fraccion = p.Total > 0 ? (double)p.Done / p.Total : 0;
                Report(30 + fraccion * 40, $"{p.Stage}: {p.Done}/{p.Total}");
            });
            await installer.InstallVanillaAsync(GameRoot, McVersion, installProgress);

            // 5) forge
            ct.ThrowIfCancellationRequested();
            Report(72, "Instalando Forge (tarda un rato)...");
            var forgeInstaller = new ForgeInstallerService(log: _log);
            var forgeId = await forgeInstaller.InstallForgeAsync(GameRoot, javaInfo.Path, McVersion, ForgeVersion, ct);

            // ya quedo instalado, el boton cambia de INSTALAR a JUGAR
            Send(new { type = "installed" });

            // 6) a jugar (aqui ya no se cancela: el boton de cancelar se esconde)
            ct.ThrowIfCancellationRequested();
            Report(95, "Arrancando Minecraft...");
            Send(new { type = "playState", state = "playing" });

            // el launcher se va a la barra de tareas mientras dura el juego
            SetWindowForGame(true);

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

            // el juego ya se cerro, regresamos la ventana
            SetWindowForGame(false);

            if (exitCode == 0)
            {
                _lastPercent = 0;
                Report(100, "Listo para jugar");
            }
            else
            {
                Send(new { type = "error", message = $"el juego se cerró con código {exitCode}, abre el registro desde el menú del perfil" });
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _log.Warning("cancelaste la instalacion, aqui no paso nada");
            _lastPercent = 0;
            Send(new { type = "cancelled" });
            SendState();
        }
        catch (Exception ex)
        {
            _log.Error($"se murio algo: {ex.Message}");
            Send(new { type = "error", message = FriendlyError(ex) });
        }
        finally
        {
            // por si el juego truena antes de llegar al paso de arriba, la ventana siempre regresa
            if (WindowState == WindowState.Minimized)
                SetWindowForGame(false);

            _busy = false;
            Send(new { type = "playState", state = "idle" });
        }
    }
}