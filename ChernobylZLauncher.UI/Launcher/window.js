// botones de la ventana + arrastrar + musica de fondo
// pon tu cancion en la carpeta launcher con el nombre music.mp3
(() => {
  const $ = id => document.getElementById(id);
  const send = obj => window.chrome && window.chrome.webview && window.chrome.webview.postMessage(obj);

  const MUSIC_FILE = "music.mp3";
  const MUSIC_VOLUME = 0.3; // de 0 a 1

  // ---------- estilos ----------
  const style = document.createElement("style");
  style.textContent = `
    .cz-wc{display:flex;align-items:center;height:100%;margin-left:14px}
    .cz-wbtn{width:44px;height:100%;display:flex;align-items:center;justify-content:center;background:transparent;color:var(--muted);cursor:pointer;transition:background .15s,color .15s}
    .cz-wbtn:hover{background:var(--surface-3);color:var(--text)}
    .cz-wbtn.close:hover{background:#c0392b;color:#fff}
    .cz-wbtn svg{width:12px;height:12px;stroke:currentColor;fill:none;stroke-width:1.2}
    .cz-music{width:36px;font-size:13px}
    .cz-music.off{opacity:.45}
  `;
  document.head.appendChild(style);

  // ---------- botones ----------
  const header = document.querySelector("header");
  const wc = document.createElement("div");
  wc.className = "cz-wc";
  wc.innerHTML = `
    <button class="cz-wbtn cz-music" id="czMusic" title="Música">♪</button>
    <button class="cz-wbtn" id="czMin" title="Minimizar">
      <svg viewBox="0 0 12 12"><line x1="1" y1="6" x2="11" y2="6"/></svg>
    </button>
    <button class="cz-wbtn" id="czMax" title="Maximizar">
      <svg viewBox="0 0 12 12" id="czMaxIcon"><rect x="1.5" y="1.5" width="9" height="9"/></svg>
    </button>
    <button class="cz-wbtn close" id="czClose" title="Cerrar">
      <svg viewBox="0 0 12 12"><line x1="1.5" y1="1.5" x2="10.5" y2="10.5"/><line x1="10.5" y1="1.5" x2="1.5" y2="10.5"/></svg>
    </button>
  `;
  header.appendChild(wc);
  header.style.paddingRight = "0";

  $("czMin").addEventListener("click", () => send({ type: "minimize" }));
  $("czMax").addEventListener("click", () => send({ type: "toggleMaximize" }));
  $("czClose").addEventListener("click", () => send({ type: "closeWindow" }));

  // iconos de maximizar / restaurar
  const setMaxIcon = maximized => {
    $("czMaxIcon").innerHTML = maximized
      ? '<rect x="1.5" y="3.5" width="7" height="7"/><polyline points="3.5,3.5 3.5,1.5 10.5,1.5 10.5,8.5 8.5,8.5"/>'
      : '<rect x="1.5" y="1.5" width="9" height="9"/>';
    $("czMax").title = maximized ? "Restaurar" : "Maximizar";
  };

  // ---------- arrastrar la ventana ----------
  // se arrastra desde cualquier parte vacia de la barra de arriba
  header.addEventListener("mousedown", e => {
    if (e.button !== 0) return;
    if (e.target.closest("a, button, .profile, .profile-menu")) return;
    if (e.detail === 2) { send({ type: "toggleMaximize" }); return; }
    send({ type: "dragWindow" });
  });

  // ---------- musica ----------
  let muted = false;
  try { muted = localStorage.getItem("cz_music_muted") === "1"; } catch { }

  const audio = new Audio(MUSIC_FILE);
  audio.loop = true;
  audio.volume = MUSIC_VOLUME;
  let gameRunning = false;
  let audioOk = true;
  audio.addEventListener("error", () => { audioOk = false; $("czMusic").style.display = "none"; });

  const refreshMusicBtn = () => {
    $("czMusic").classList.toggle("off", muted);
    $("czMusic").title = muted ? "Activar música" : "Silenciar música";
  };

  const tryPlay = () => {
    if (!audioOk || muted || gameRunning) return;
    audio.play().catch(() => {
      // el navegador no dejo arrancar solo, empieza con el primer click
      const once = () => { document.removeEventListener("click", once, true); tryPlay(); };
      document.addEventListener("click", once, true);
    });
  };

  $("czMusic").addEventListener("click", () => {
    muted = !muted;
    try { localStorage.setItem("cz_music_muted", muted ? "1" : "0"); } catch { }
    if (muted) audio.pause(); else tryPlay();
    refreshMusicBtn();
  });

  refreshMusicBtn();
  tryPlay();

  // ---------- mensajes de c# ----------
  if (window.chrome && window.chrome.webview) {
    window.chrome.webview.addEventListener("message", e => {
      const m = e.data;
      if (m.type === "windowState") setMaxIcon(m.maximized);

      // mientras corre minecraft la musica se pausa
      if (m.type === "playState") {
        gameRunning = m.state === "playing";
        if (gameRunning) audio.pause(); else tryPlay();
      }
    });
  }

  send({ type: "windowReady" });
})();