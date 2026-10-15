using System;
using System.Reflection;
using System.Linq;

using System.Diagnostics;

using System.IO;

using System.Net.Http;

using System.Net.Http.Headers;

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

    // un solo HttpClient para las skins (crear uno por cada peticion es mala idea, creeme)

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly LauncherLogService _log = new();

    private readonly SettingsService _settingsService;

    private readonly TokenStore _tokenStore;

    private readonly object _logLock = new();

    private bool _busy;
    private bool _updateInstalling;

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

            // ---- quitamos lo que hace que el launcher parezca una pagina web ----

            // (clic derecho con "Recargar", la barrita de abajo, el zoom, etc.)

            var cfg = WebView.CoreWebView2.Settings;

            cfg.AreDefaultContextMenusEnabled = false;   // adios menu del clic derecho

            cfg.IsStatusBarEnabled = false;              // la barrita que sale abajo con el link

            cfg.IsZoomControlEnabled = false;            // que no se pueda hacer zoom con ctrl + rueda

            cfg.IsSwipeNavigationEnabled = false;        // por si alguien tiene pantalla tactil

#if !DEBUG

            // en la version final tampoco dejamos F5, Ctrl+R, F12, etc.

            // si lo dejamos en debug es para que tu si puedas depurar (con F12 abres la consola)

            cfg.AreBrowserAcceleratorKeysEnabled = false;

            cfg.AreDevToolsEnabled = false;

#endif

            // ---- links externos ----

            // por defecto los target="_blank" abren una ventanita rara dentro del launcher,

            // asi que los mandamos al navegador de verdad (chrome, edge, lo que tenga el jugador)

            WebView.CoreWebView2.NewWindowRequested += (_, args) =>

            {

                args.Handled = true; // le decimos que NO abra su popup

                // solo dejamos pasar http y https, nada de abrir cosas raras en el pc del jugador

                if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var url) &&

                    (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp))

                {

                    try { Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true }); } catch { }

                }

            };

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

            string? skinVariant = null, skinData = null, lookupName = null;

            using (var doc = JsonDocument.Parse(e.WebMessageAsJson))

            {

                var root = doc.RootElement;

                type = root.GetProperty("type").GetString();

                if (type == "saveSettings")

                {

                    ramMb = root.GetProperty("ramMb").GetInt32();

                    autoConnect = root.GetProperty("autoConnect").GetBoolean();

                }

                // lo de las skins tambien se lee aqui adentro, porque el doc se cierra al salir

                if (type == "applySkin")

                {

                    skinVariant = root.GetProperty("variant").GetString();

                    skinData = root.GetProperty("data").GetString();

                }

                if (type == "lookupSkin")

                    lookupName = root.GetProperty("name").GetString();

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

                    // sesion guardada pero sin nombre (o sin uuid, que es para la skin): lo buscamos sin molestar al jugador

                    var saved = _settingsService.Load();

                    if (_tokenStore.Load() != null &&

                        (string.IsNullOrEmpty(saved.LastPlayerName) || string.IsNullOrEmpty(saved.LastPlayerUuid)))

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

                    s.LastPlayerUuid = string.Empty;

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

                // ---- skins ----

                case "getSkin":

                    await SendCurrentSkinAsync();

                    break;

                case "applySkin":

                    await ApplySkinAsync(skinVariant, skinData);

                    break;

                case "lookupSkin":

                    await LookupSkinAsync(lookupName);

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

            playerUuid = s.LastPlayerUuid,

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

            s.LastPlayerUuid = session.Profile.Id.ToString(); // para la cabeza de la skin

            _settingsService.Save(s);

            Send(new { type = "loggedIn", name = session.Profile.Name, uuid = session.Profile.Id.ToString() });

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

            s.LastPlayerUuid = session.Profile.Id.ToString(); // para la cabeza de la skin

            _settingsService.Save(s);

            Send(new { type = "loggedIn", name = session.Profile.Name, uuid = session.Profile.Id.ToString() });

        }

        catch (Exception ex)

        {

            _log.Warning($"no pude sacar el nombre del jugador: {ex.Message}");

            // AuthSessionService borra el token si ya no servia, entonces toca login de nuevo

            if (_tokenStore.Load() == null)

                Send(new { type = "loggedOut" });

        }

    }

    // ---------- skins ----------

    // saca la sesion guardada sin abrir navegador ni pedir codigo

    // si ya vencio devuelve null y listo, que el jugador inicie sesion normal

    private async Task<AuthSession?> GetSessionSilentAsync()

    {

        try

        {

            var authService = new MicrosoftAuthService(log: _log);

            var sessionService = new AuthSessionService(authService, _tokenStore, _log);

            return await sessionService.LoginAsync(

                _ => throw new InvalidOperationException("la sesion guardada ya vencio"));

        }

        catch (Exception ex)

        {

            _log.Warning($"no pude sacar la sesion para las skins: {ex.Message}");

            return null;

        }

    }

    // le pide a mojang la skin que tienes puesta y se la manda al html (ya descargada, en base64)

    private async Task SendCurrentSkinAsync()

    {

        var session = await GetSessionSilentAsync();

        if (session == null)

        {

            Send(new { type = "skinError", message = "Inicia sesión para ver y cambiar tu skin." });

            return;

        }

        try

        {

            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");

            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", $"{session.MinecraftToken}");

            using var res = await Http.SendAsync(req);

            res.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());

            // buscamos la skin que esta ACTIVE (puede haber mas guardadas en la cuenta)

            if (doc.RootElement.TryGetProperty("skins", out var skins))

            {

                foreach (var skin in skins.EnumerateArray())

                {

                    if (skin.GetProperty("state").GetString() != "ACTIVE") continue;

                    var url = skin.GetProperty("url").GetString();

                    var slim = skin.TryGetProperty("variant", out var v) &&

                               string.Equals(v.GetString(), "SLIM", StringComparison.OrdinalIgnoreCase);

                    var png = await Http.GetByteArrayAsync(url);

                    Send(new { type = "currentSkin", variant = slim ? "slim" : "classic", data = Convert.ToBase64String(png) });

                    return;

                }

            }

            // cuenta sin skin propia (usa la de por defecto)

            Send(new { type = "currentSkin", variant = "classic", data = (string?)null });

        }

        catch (Exception ex)

        {

            _log.Warning($"no pude bajar tu skin: {ex.Message}");

            Send(new { type = "skinError", message = FriendlyError(ex) });

        }

    }

    // sube la skin a la cuenta de mojang (esto es lo mismo que hace minecraft.net)

    private async Task ApplySkinAsync(string? variant, string? base64)

    {

        try

        {

            var v = variant == "slim" ? "slim" : "classic";

            if (string.IsNullOrEmpty(base64))

                throw new InvalidOperationException("no llegó la imagen de la skin");

            var png = Convert.FromBase64String(base64);

            // chequeo rapido antes de molestar a mojang: que sea png y mida 64x64 o 64x32

            if (png.Length < 33 || png.Length > 200_000 ||

                png[0] != 0x89 || png[1] != 0x50 || png[2] != 0x4E || png[3] != 0x47)

                throw new InvalidOperationException("esa imagen no es un PNG válido");

            // el ancho y el alto estan guardados en bytes fijos del png (odio los formatos binarios)

            var w = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];

            var h = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];

            if (w != 64 || (h != 64 && h != 32))

                throw new InvalidOperationException($"la skin tiene que medir 64x64 (o 64x32), la tuya mide {w}x{h}");

            var session = await GetSessionSilentAsync();

            if (session == null)

                throw new InvalidOperationException("tu sesión venció, cierra sesión y vuelve a entrar");

            using var form = new MultipartFormDataContent();

            form.Add(new StringContent(v), "variant");

            var file = new ByteArrayContent(png);

            file.Headers.ContentType = new MediaTypeHeaderValue("image/png");

            form.Add(file, "file", "skin.png");

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.minecraftservices.com/minecraft/profile/skins")

            {

                Content = form

            };

            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", $"{session.MinecraftToken}");

            using var res = await Http.SendAsync(req);

            if (!res.IsSuccessStatusCode)

            {

                var code = (int)res.StatusCode;

                throw new InvalidOperationException(code switch

                {

                    429 => "Mojang dice que cambias la skin muy seguido, espera un ratito y reintenta",

                    401 or 403 => "Mojang no aceptó tu sesión, cierra sesión y vuelve a entrar",

                    _ => $"Mojang rechazó la skin (error {code})"

                });

            }

            _log.Success("skin cambiada");

            Send(new { type = "skinApplied", variant = v, data = base64 });

        }

        catch (Exception ex)

        {

            _log.Warning($"no se pudo cambiar la skin: {ex.Message}");

            Send(new { type = "skinError", message = FriendlyError(ex) });

        }

    }

    // busca la skin de cualquier jugador por su nombre (nombre -> uuid -> textura -> png)

    private async Task LookupSkinAsync(string? name)

    {

        try

        {

            name = (name ?? "").Trim();

            if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z0-9_]{3,16}$"))

                throw new InvalidOperationException("ese nombre no parece de Minecraft (3 a 16 letras, números o _)");

            // 1) nombre -> uuid

            using var r1 = await Http.GetAsync($"https://api.mojang.com/users/profiles/minecraft/{name}");

            if (r1.StatusCode == System.Net.HttpStatusCode.NotFound || r1.StatusCode == System.Net.HttpStatusCode.NoContent)

                throw new InvalidOperationException("no existe ningún jugador con ese nombre");

            r1.EnsureSuccessStatusCode();

            using var d1 = JsonDocument.Parse(await r1.Content.ReadAsStringAsync());

            var id = d1.RootElement.GetProperty("id").GetString();

            var realName = d1.RootElement.GetProperty("name").GetString() ?? name;

            // 2) uuid -> perfil, que trae la textura escondida en base64

            using var r2 = await Http.GetAsync($"https://sessionserver.mojang.com/session/minecraft/profile/{id}");

            r2.EnsureSuccessStatusCode();

            using var d2 = JsonDocument.Parse(await r2.Content.ReadAsStringAsync());

            string? texturesB64 = null;

            foreach (var prop in d2.RootElement.GetProperty("properties").EnumerateArray())

            {

                if (prop.GetProperty("name").GetString() == "textures")

                    texturesB64 = prop.GetProperty("value").GetString();

            }

            if (texturesB64 == null)

                throw new InvalidOperationException("ese jugador no tiene skin");

            using var d3 = JsonDocument.Parse(Convert.FromBase64String(texturesB64));

            if (!d3.RootElement.GetProperty("textures").TryGetProperty("SKIN", out var skin))

                throw new InvalidOperationException("ese jugador usa la skin por defecto, no hay nada que copiar");

            var url = skin.GetProperty("url").GetString();

            var slim = skin.TryGetProperty("metadata", out var meta) &&

                       meta.TryGetProperty("model", out var model) &&

                       model.GetString() == "slim";

            // 3) bajamos el png

            var png = await Http.GetByteArrayAsync(url);

            Send(new { type = "skinLookup", name = realName, variant = slim ? "slim" : "classic", data = Convert.ToBase64String(png) });

        }

        catch (Exception ex)

        {

            _log.Warning($"no pude buscar esa skin: {ex.Message}");

            Send(new { type = "skinError", message = FriendlyError(ex) });

        }

    }

    // ---------- jugar ----------

    // el porcentaje solo sube, porque cada etapa del instalador reinicia su contador

    private void Report(double percent, string text)

    {

        if (percent > _lastPercent) _lastPercent = percent;

        Send(new { type = "progress", percent = Math.Round(_lastPercent, 1), text });

    }

    // ---------- actualizador del launcher ----------

    private async Task CheckForLauncherUpdateAsync()
    {
        try
        {
            Version? currentVersion = Assembly.GetEntryAssembly()?.GetName().Version;
            if (currentVersion is null)
            {
                _log.Warning("No se pudo leer la versión instalada del launcher.");
                return;
            }

            currentVersion = new Version(currentVersion.Major, currentVersion.Minor,
                Math.Max(currentVersion.Build, 0));

            var update = await LauncherUpdateService.CheckAndDownloadAsync(currentVersion);
            if (update is null)
                return;

            if (_busy || _updateInstalling)
            {
                _log.Warning("Hay una operación en curso; se omitió instalar la actualización por ahora.");
                return;
            }

            var answer = MessageBox.Show(
                $"Hay una actualización del launcher disponible (versión {update.Version}).\n\n" +
                "¿Quieres instalarla ahora? El launcher se cerrará y volverá a abrirse automáticamente.",
                "Actualización de ChernobylZLauncher",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (answer != MessageBoxResult.Yes)
            {
                _log.Info("El jugador decidió instalar la actualización más tarde.");
                return;
            }

            await InstallLauncherUpdateAsync(update.ZipPath);
        }
        catch (Exception ex)
        {
            _log.Warning($"No se pudo comprobar/instalar la actualización: {ex.Message}");
        }
    }

    private async Task InstallLauncherUpdateAsync(string zipPath)
    {
        if (_busy || _updateInstalling)
            return;

        _updateInstalling = true;
        try
        {
            string installDirectory = AppContext.BaseDirectory.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string localRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ChernobylZLauncher");
            string updaterSource = Path.Combine(AppContext.BaseDirectory, "updater");

            if (!File.Exists(zipPath))
                throw new FileNotFoundException("No se encontró el paquete de actualización.", zipPath);

            string updaterExeSource = Path.Combine(updaterSource, "ChernobylZLauncher.Updater.exe");
            if (!File.Exists(updaterExeSource))
                throw new FileNotFoundException(
                    "No se encontró updater\\ChernobylZLauncher.Updater.exe. El paquete debe incluir la carpeta updater.",
                    updaterExeSource);

            string updaterRunDirectory = Path.Combine(localRoot, "Updater", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(updaterRunDirectory);
            CopyDirectory(updaterSource, updaterRunDirectory);

            string updaterExe = Path.Combine(updaterRunDirectory, "ChernobylZLauncher.Updater.exe");
            string backupDirectory = Path.Combine(localRoot, "Backups", DateTime.Now.ToString("yyyyMMdd-HHmmss"));

            var startInfo = new ProcessStartInfo
            {
                FileName = updaterExe,
                WorkingDirectory = updaterRunDirectory,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("--install");
            startInfo.ArgumentList.Add(zipPath);
            startInfo.ArgumentList.Add(installDirectory);
            startInfo.ArgumentList.Add(backupDirectory);
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
            startInfo.ArgumentList.Add(Path.Combine(installDirectory, "ChernobylZLauncher.exe"));

            Process? updaterProcess = Process.Start(startInfo);
            if (updaterProcess is null)
                throw new InvalidOperationException("Windows no pudo iniciar el actualizador.");

            _log.Info("Actualizador iniciado. Cerrando el launcher para reemplazar sus archivos...");
            Application.Current.Shutdown();
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _updateInstalling = false;
            _log.Error($"No se pudo iniciar la instalación de la actualización: {ex.Message}");
            MessageBox.Show(
                $"No se pudo instalar la actualización automáticamente.\n\n{ex.Message}",
                "Error de actualización",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (string file in Directory.GetFiles(sourceDirectory))
        {
            string destinationFile = Path.Combine(destinationDirectory, Path.GetFileName(file));
            File.Copy(file, destinationFile, overwrite: true);
        }

        foreach (string directory in Directory.GetDirectories(sourceDirectory))
        {
            string destinationSubdirectory = Path.Combine(
                destinationDirectory, Path.GetFileName(directory));
            CopyDirectory(directory, destinationSubdirectory);
        }
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

            s.LastPlayerUuid = session.Profile.Id.ToString(); // para la cabeza de la skin

            _settingsService.Save(s);

            Send(new { type = "loggedIn", name = session.Profile.Name, uuid = session.Profile.Id.ToString() });

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