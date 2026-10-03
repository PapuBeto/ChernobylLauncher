// pantalla de login + configuracion + noticias + conexion con c# (webview2)
// todo vive aqui para no tocar las mil lineas del html, pero ojo: abajo le
// robamos el click al boton jugar para quitar el simulador de mentiritas
(() => {
  const $ = id => document.getElementById(id);
  const send = obj => window.chrome && window.chrome.webview && window.chrome.webview.postMessage(obj);

  // versiones y links, cambialos aqui y ya
  const LAUNCHER_VERSION = "1.0";
  const NEWS_URL = "https://raw.githubusercontent.com/PapuBeto/ChernobylLauncher/main/news/news.json";

  let loggedIn = false;
  let installed = true; // c# nos dice la verdad en cuanto abre

  // ---------- estilos ----------
  const style = document.createElement("style");
  style.textContent = `
    .cz-overlay{position:fixed;inset:0;z-index:200;display:none;align-items:center;justify-content:center;padding:25px;background:rgba(4,5,5,.86);backdrop-filter:blur(9px)}
    .cz-overlay.open{display:flex}
    .cz-card{position:relative;width:min(440px,94vw);padding:34px;background:#121617;border:1px solid var(--border);box-shadow:0 30px 100px rgba(0,0,0,.55)}
    .cz-title{font-size:22px;font-weight:600;letter-spacing:-.02em;margin-bottom:8px}
    .cz-text{color:var(--text-soft);font-size:12px;line-height:1.6;opacity:.85}
    .cz-btn{width:100%;height:44px;margin-top:22px;background:var(--green);color:#0d100c;font-size:13px;font-weight:700;letter-spacing:.12em;cursor:pointer}
    .cz-btn:hover{background:#b1ff68}
    .cz-btn:disabled{opacity:.55;cursor:default}
    .cz-btn.ghost{background:transparent;color:var(--text-soft);border:1px solid var(--border)}
    .cz-btn.ghost:hover{background:var(--surface-3);color:var(--text)}
    .cz-code{display:none;margin-top:20px;padding:14px;text-align:center;border:1px dashed var(--border-hover);font-family:var(--mono);font-size:26px;letter-spacing:.25em;color:var(--green)}
    .cz-note{display:none;margin-top:10px;text-align:center;font-family:var(--mono);font-size:9px;color:var(--muted)}
    .cz-error{margin-top:12px;color:#d96a4d;font-family:var(--mono);font-size:10px;line-height:1.5}
    .cz-row{margin-top:26px}
    .cz-label{display:flex;justify-content:space-between;margin-bottom:10px;font-family:var(--mono);font-size:9px;letter-spacing:.12em;color:var(--muted)}
    .cz-label b{color:var(--green);font-weight:400}
    .cz-range{width:100%;accent-color:var(--green)}
    .cz-switch{display:flex;align-items:center;justify-content:space-between;gap:16px}
    .cz-switch input{width:18px;height:18px;accent-color:var(--green)}
    .cz-hint{margin-top:6px;color:var(--muted);font-size:10px}
    .cz-close{position:absolute;top:10px;right:12px;width:28px;height:28px;background:transparent;color:var(--muted);font-size:20px;cursor:pointer}
    .cz-close:hover{color:var(--text)}
  `;
  document.head.appendChild(style);

  // ---------- html ----------
  const root = document.createElement("div");
  root.innerHTML = `
    <div class="cz-overlay" id="czLogin">
      <div class="cz-card">
        <h2 class="cz-title">Inicia sesión</h2>
        <p class="cz-text" id="czLoginText">Para jugar necesitas tu cuenta de Microsoft (la misma de Minecraft).</p>
        <div class="cz-code" id="czCode"></div>
        <div class="cz-note" id="czCodeNote">Código copiado y navegador abierto. Pégalo ahí y regresa aquí.</div>
        <button class="cz-btn" id="czLoginBtn">INICIAR SESIÓN CON MICROSOFT</button>
        <div class="cz-error" id="czLoginError"></div>
      </div>
    </div>

    <div class="cz-overlay" id="czSettings">
      <div class="cz-card">
        <button class="cz-close" id="czSettingsClose">×</button>
        <h2 class="cz-title">Configuración</h2>

        <div class="cz-row">
          <div class="cz-label"><span>MEMORIA RAM</span><b id="czRamValue">—</b></div>
          <input class="cz-range" id="czRam" type="range" min="2048" max="8192" step="512" value="4096">
          <div class="cz-hint">Si el juego se pone lento o crashea, súbele. Si le pones de más, también se pone lento.</div>
        </div>

        <div class="cz-row cz-switch">
          <div>
            <div style="font-size:13px">Conectar directo al servidor</div>
            <div class="cz-hint">Al abrir el juego entra solo al server.</div>
          </div>
          <input type="checkbox" id="czAuto">
        </div>

        <button class="cz-btn ghost" id="czLogoutBtn">CERRAR SESIÓN</button>
      </div>
    </div>
  `;
  document.body.appendChild(root);

  // ---------- helpers ----------
  const open = id => $(id).classList.add("open");
  const close = id => $(id).classList.remove("open");

  const setStatus = (text, bad) => {
    const s = $("status");
    s.textContent = text;
    s.style.color = bad ? "#d96a4d" : "";
    s.title = text;
  };

  // el boton dice INSTALAR si falta minecraft/forge, y JUGAR si ya esta todo
  const setPlay = state => {
    const b = $("playButton");
    b.disabled = state !== "idle";
    if (state === "working") b.textContent = installed ? "INICIANDO" : "INSTALANDO";
    else if (state === "playing") b.textContent = "JUGANDO";
    else b.textContent = installed ? "JUGAR" : "INSTALAR";
  };

  const setName = name => {
    document.querySelector(".profile-name").textContent = name || "Sin sesión";
  };

  const ramText = mb => `${(mb / 1024).toFixed(1)} GB (${mb} MB)`;

  // punto verde/rojo + texto + jugadores de arriba a la derecha
  const setServerStatus = (online, players, max) => {
    const dot = $("serverDot");
    const text = $("serverStatusText");
    const count = $("playerCount");

    if (online) {
      dot.style.background = "var(--green)";
      dot.style.boxShadow = "0 0 8px rgba(156, 255, 66, .5)";
      text.textContent = "EN LÍNEA";
      count.textContent = max > 0 ? `${players}/${max}` : `${players}`;
    } else {
      dot.style.background = "#d96a4d";
      dot.style.boxShadow = "none";
      text.textContent = "SIN CONEXIÓN";
      count.textContent = "—";
    }
  };

  // ---------- versiones (las de mentiritas de la maqueta, ya reales) ----------
  document.querySelector(".brand-version").textContent = LAUNCHER_VERSION;
  document.querySelector(".launcher-version").textContent = "LAUNCHER " + LAUNCHER_VERSION;

  const setBuildInfo = (forge, mc) => {
    const spans = document.querySelectorAll(".build-info span");
    if (spans.length >= 3) {
      spans[0].textContent = "v" + LAUNCHER_VERSION;
      spans[1].textContent = "Forge " + forge;
      spans[2].textContent = "MC " + mc;
    }
  };
  setBuildInfo("47.4.23", "1.20.1");

  // ---------- noticias ----------
  // quitamos las de mentiritas, las de verdad salen de news/news.json en el repo
  document.querySelectorAll(".news .news-item").forEach(n => n.remove());
  const newsList = document.createElement("div");
  newsList.id = "czNewsList";
  document.querySelector(".news").appendChild(newsList);

  const openNews = (n, typeClass) => {
    $("modalTitle").textContent = n.title || "";
    $("modalDescription").textContent = n.description || "";
    $("modalDate").textContent = n.date || "";
    $("modalTag").textContent = n.tag || "";
    $("modalDot").className = "modal-dot" + typeClass;
    $("overlay").classList.add("open");
  };

  const el = (tag, cls, text) => {
    const e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text !== undefined) e.textContent = text;
    return e;
  };

  const renderNews = items => {
    newsList.innerHTML = "";

    if (!items.length) {
      newsList.appendChild(el("div", "cz-hint", "Sin noticias por ahora, todo tranquilo."));
      return;
    }

    items.forEach((n, i) => {
      const typeClass = n.type === "fix" ? " fix" : n.type === "server" ? " server" : "";

      const art = el("article", "news-item changelog" + (i === 0 ? " featured" : ""));
      art.tabIndex = 0;

      if (i === 0) art.appendChild(el("div", "news-image"));

      const meta = el("div", "news-meta");
      meta.appendChild(el("span", "news-dot" + typeClass));
      meta.appendChild(el("span", "news-date", n.date || ""));
      meta.appendChild(el("span", "news-tag", n.tag || ""));
      art.appendChild(meta);
      art.appendChild(el("h2", "news-title", n.title || ""));

      art.addEventListener("click", () => openNews(n, typeClass));
      art.addEventListener("keydown", e => {
        if (e.key === "Enter" || e.key === " ") { e.preventDefault(); openNews(n, typeClass); }
      });

      newsList.appendChild(art);
    });
  };

  renderNews([]);

  fetch(NEWS_URL + "?cachebust=" + Date.now())
    .then(r => r.ok ? r.json() : Promise.reject(new Error("http " + r.status)))
    .then(d => renderNews(Array.isArray(d.news) ? d.news : []))
    .catch(() => renderNews([]));

  // ---------- menu de perfil (lo reemplazamos entero) ----------
  $("profileMenu").innerHTML = `
    <div class="profile-option" id="czOptSettings">Configuración</div>
    <div class="profile-option separator" id="czOptLogout">Cerrar sesión</div>
  `;
  $("czOptSettings").addEventListener("click", () => open("czSettings"));
  $("czOptLogout").addEventListener("click", () => send({ type: "logout" }));
  $("czLogoutBtn").addEventListener("click", () => { close("czSettings"); send({ type: "logout" }); });
  $("czSettingsClose").addEventListener("click", () => close("czSettings"));
  $("czSettings").addEventListener("click", e => { if (e.target === $("czSettings")) close("czSettings"); });
  document.addEventListener("keydown", e => { if (e.key === "Escape") close("czSettings"); });

  // ---------- configuracion ----------
  const saveSettings = () => send({
    type: "saveSettings",
    ramMb: parseInt($("czRam").value, 10),
    autoConnect: $("czAuto").checked
  });

  $("czRam").addEventListener("input", () => { $("czRamValue").textContent = ramText(parseInt($("czRam").value, 10)); });
  $("czRam").addEventListener("change", saveSettings);
  $("czAuto").addEventListener("change", saveSettings);

  // ---------- login ----------
  $("czLoginBtn").addEventListener("click", () => {
    $("czLoginBtn").disabled = true;
    $("czLoginBtn").textContent = "ESPERANDO...";
    $("czLoginError").textContent = "";
    send({ type: "login" });
  });

  const resetLoginUi = () => {
    $("czLoginBtn").disabled = false;
    $("czLoginBtn").textContent = "INICIAR SESIÓN CON MICROSOFT";
    $("czCode").style.display = "none";
    $("czCodeNote").style.display = "none";
  };

  // ---------- jugar ----------
  // capture + stopPropagation = el click ya no llega al simulador de mentiritas del html
  document.addEventListener("click", e => {
    const btn = e.target.closest && e.target.closest("#playButton");
    if (!btn) return;
    e.stopPropagation();
    if (btn.disabled) return;
    if (!loggedIn) { open("czLogin"); return; }
    send({ type: "play" });
  }, true);

  // ---------- mensajes que llegan de c# ----------
  const handle = m => {
    switch (m.type) {
      case "state":
        loggedIn = m.loggedIn;
        installed = m.installed;
        setName(m.loggedIn ? (m.playerName || "Cargando...") : "");
        $("czRam").min = m.minRam;
        $("czRam").max = m.maxRam;
        $("czRam").value = m.ramMb;
        $("czRamValue").textContent = ramText(m.ramMb);
        $("czAuto").checked = m.autoConnect;
        setBuildInfo(m.forge, m.mc);
        // si esta jugando o instalando no le movemos el boton
        if (!$("playButton").disabled) {
          setPlay("idle");
          setStatus(installed ? "Listo para jugar" : "Listo para instalar", false);
        }
        if (m.loggedIn) close("czLogin"); else open("czLogin");
        break;

      case "serverStatus":
        setServerStatus(m.online, m.players, m.max);
        break;

      case "installed":
        installed = true;
        break;

      case "deviceCode":
        loggedIn = false;
        open("czLogin");
        $("czCode").textContent = m.code;
        $("czCode").style.display = "block";
        $("czCodeNote").style.display = "block";
        $("czLoginBtn").disabled = true;
        $("czLoginBtn").textContent = "ESPERANDO...";
        break;

      case "loggedIn":
        loggedIn = true;
        setName(m.name);
        resetLoginUi();
        close("czLogin");
        break;

      case "loggedOut":
        loggedIn = false;
        setName("");
        resetLoginUi();
        open("czLogin");
        break;

      case "loginFailed":
        resetLoginUi();
        $("czLoginError").textContent = m.message;
        break;

      case "progress":
        $("progress").style.width = m.percent + "%";
        $("percentage").textContent = m.percent > 0 && m.percent < 100 ? Math.round(m.percent) + "%" : "";
        setStatus(m.text, false);
        break;

      case "log":
        setStatus(m.text, m.level === "Error");
        break;

      case "playState":
        setPlay(m.state);
        break;

      case "error":
        setStatus(m.message, true);
        break;
    }
  };

  if (window.chrome && window.chrome.webview) {
    window.chrome.webview.addEventListener("message", e => handle(e.data));
  }

  // avisamos a c# que ya estamos listos para que nos mande el estado
  send({ type: "ready" });
})();