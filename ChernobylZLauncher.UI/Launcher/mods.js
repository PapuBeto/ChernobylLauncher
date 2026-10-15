// Chernobyl Z Launcher — catálogo informativo de mods.
// Lee el mismo manifest.json que utiliza el sincronizador. No descarga ni modifica mods.
(() => {
  "use strict";

  const MANIFEST_URL =
    "https://raw.githubusercontent.com/PapuBeto/ChernobylLauncher/main/manifest/manifest.json";
  const $ = (id) => document.getElementById(id);
  const send = (obj) => {
    if (window.chrome && window.chrome.webview) {
      window.chrome.webview.postMessage(obj);
    }
  };

  const style = document.createElement("style");
  style.textContent = `
    .cz-mods{position:fixed;left:0;right:0;top:58px;bottom:42px;z-index:16;display:none;flex-direction:column;background:var(--background,#0d0f10);color:var(--text,#e8eee4)}
    .cz-mods.open{display:flex}
    .cz-mods *{box-sizing:border-box}
    .md-top{display:flex;align-items:center;gap:18px;padding:16px 26px;border-bottom:1px solid var(--border,#283126);flex-wrap:wrap}
    .md-back{height:30px;padding:0 12px;background:transparent;border:1px solid var(--border,#283126);color:var(--text-soft,#aab4a5);font-family:var(--mono,monospace);font-size:9px;letter-spacing:.12em;cursor:pointer}
    .md-back:hover{color:var(--text,#e8eee4);background:var(--surface-2,#1b2119)}
    .md-title{font-size:16px;font-weight:700;letter-spacing:.16em}
    .md-count{font-family:var(--mono,monospace);font-size:10px;color:var(--green,#9cff42);margin-left:auto}
    .md-content{padding:22px 26px;overflow:auto;min-height:0;flex:1}
    .md-intro{display:flex;justify-content:space-between;gap:20px;align-items:end;margin-bottom:18px;flex-wrap:wrap}
    .md-heading{font-size:20px;font-weight:700;letter-spacing:.04em;margin:0 0 6px}
    .md-subtitle{font-size:12px;color:var(--text-soft,#aab4a5);line-height:1.6;max-width:620px}
    .md-search{width:min(100%,330px);height:38px;padding:0 13px;background:var(--surface-2,#1b2119);border:1px solid var(--border,#283126);color:var(--text,#e8eee4);font:12px var(--main,Arial,sans-serif);outline:none}
    .md-search:focus{border-color:var(--green,#9cff42)}
    .md-meta{display:flex;gap:8px;flex-wrap:wrap;margin:14px 0 18px}
    .md-chip{padding:5px 8px;border:1px solid var(--border,#283126);color:var(--text-soft,#aab4a5);font:9px var(--mono,monospace);letter-spacing:.06em}
    .md-grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(220px,1fr));gap:12px}
    .md-card{min-width:0;padding:15px;background:var(--surface,#141914);border:1px solid var(--border,#283126);transition:border-color .15s,background .15s}
    .md-card:hover{border-color:var(--border-hover,#668b45);background:var(--surface-2,#1b2119)}
    .md-card-top{display:flex;align-items:flex-start;gap:10px}
    .md-icon{width:38px;height:38px;flex:0 0 38px;display:grid;place-items:center;background:rgba(156,255,66,.08);border:1px solid rgba(156,255,66,.22);color:var(--green,#9cff42);font:700 12px var(--mono,monospace)}
    .md-name{font-size:12px;font-weight:700;line-height:1.45;overflow-wrap:anywhere}
    .md-version{margin-top:5px;color:var(--green,#9cff42);font:9px var(--mono,monospace)}
    .md-file{margin-top:12px;color:var(--text-soft,#aab4a5);font-size:10px;line-height:1.5;overflow-wrap:anywhere}
    .md-foot{display:flex;justify-content:space-between;gap:8px;align-items:center;margin-top:12px;padding-top:10px;border-top:1px solid var(--border,#283126);color:var(--muted,#7f897a);font:9px var(--mono,monospace)}
    .md-state{padding:22px 16px;border:1px dashed var(--border,#283126);color:var(--text-soft,#aab4a5);font-size:12px;line-height:1.6}
    .md-error{color:#e58b75}
    .socials .social.active{color:var(--green,#9cff42)}
    @media(max-width:700px){.cz-mods{top:55px;bottom:40px}.md-content{padding:18px}.md-top{padding:14px 18px}.md-grid{grid-template-columns:repeat(auto-fill,minmax(165px,1fr))}}
  `;
  document.head.appendChild(style);

  const panel = document.createElement("section");
  panel.className = "cz-mods";
  panel.id = "czMods";
  panel.innerHTML = `
    <div class="md-top">
      <button class="md-back" id="mdBackBtn" type="button">← VOLVER</button>
      <div class="md-title">MODS DEL MODPACK</div>
      <div class="md-count" id="mdCount">CARGANDO...</div>
    </div>
    <div class="md-content">
      <div class="md-intro">
        <div>
          <h2 class="md-heading">ARCHIVOS DEL MODPACK</h2>
          <div class="md-subtitle">Consulta los mods incluidos en Chernobyl Z. Esta lista se obtiene del mismo manifiesto que usa el sincronizador; esta sección solo muestra información y no instala ni elimina archivos.</div>
        </div>
        <input class="md-search" id="mdSearch" type="search" placeholder="Buscar un mod por nombre..." aria-label="Buscar mods">
      </div>
      <div class="md-meta" id="mdMeta"></div>
      <div class="md-grid" id="mdGrid"><div class="md-state">Cargando la lista de mods…</div></div>
    </div>
  `;
  document.body.appendChild(panel);

  const modsBtn = document.createElement("a");
  modsBtn.className = "social";
  modsBtn.href = "#";
  modsBtn.title = "Mods del modpack";
  modsBtn.innerHTML = `<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M8 3h8v3h3v5h-3v2h3v5h-3v3H8v-3H5v-5h3v-2H5V6h3V3Zm2 3v3H7v1h3v4H7v1h3v3h4v-3h3v-1h-3v-4h3V9h-3V6h-4Z" fill="currentColor"/></svg><span>MODS</span>`;
  const nav = document.querySelector(".socials");
  if (nav) nav.prepend(modsBtn);

  const optSettings = $("czOptSettings");
  if (optSettings && !$("czOptMods")) {
    optSettings.insertAdjacentHTML("beforebegin", '<div class="profile-option" id="czOptMods">Mods del modpack</div>');
  }

  let allMods = [];
  let manifestVersion = "—";
  let loading = false;
  let loaded = false;

  const formatBytes = (bytes) => {
    const n = Number(bytes);
    if (!Number.isFinite(n) || n <= 0) return "Tamaño no disponible";
    if (n >= 1024 ** 3) return `${(n / 1024 ** 3).toFixed(2)} GB`;
    if (n >= 1024 ** 2) return `${(n / 1024 ** 2).toFixed(1)} MB`;
    if (n >= 1024) return `${(n / 1024).toFixed(0)} KB`;
    return `${n} B`;
  };

  const cleanName = (fileName) => {
    let name = String(fileName || "").replace(/\.jar$/i, "");
    name = name.replace(/[-_](forge|fabric|neoforge)(?=[-_]|$)/ig, " ");
    name = name.replace(/[-_](mc)?1\.20\.1(?=[-_]|$)/ig, " ");
    name = name.replace(/[-_]+/g, " ").replace(/\s+/g, " ").trim();
    return name || String(fileName || "Mod sin nombre");
  };

  const displayVersion = (fileName) => {
    const matches = String(fileName || "").match(/\d+(?:\.\d+){1,}(?:[-+][\w.]+)?/g);
    if (!matches) return "Versión incluida";
    const likely = matches.find(v => /1\.20\.1/.test(v) === false) || matches[matches.length - 1];
    return `Archivo ${likely}`;
  };

  const render = () => {
    const grid = $("mdGrid");
    const search = ($("mdSearch").value || "").trim().toLocaleLowerCase();
    const filtered = allMods.filter(mod => {
      const file = String(mod.fileName || "");
      const name = cleanName(file);
      return `${name} ${file}`.toLocaleLowerCase().includes(search);
    });

    $("mdCount").textContent = `${filtered.length} / ${allMods.length} MODS`;
    $("mdMeta").innerHTML = "";
    [
      `MODPACK ${manifestVersion}`,
      `${allMods.length} ARCHIVOS`,
      "MINECRAFT 1.20.1",
      "FORGE"
    ].forEach(label => {
      const chip = document.createElement("span");
      chip.className = "md-chip";
      chip.textContent = label;
      $("mdMeta").appendChild(chip);
    });

    grid.innerHTML = "";
    if (!filtered.length) {
      const empty = document.createElement("div");
      empty.className = "md-state";
      empty.textContent = allMods.length ? "No hay mods que coincidan con tu búsqueda." : "El manifiesto no contiene mods.";
      grid.appendChild(empty);
      return;
    }

    const frag = document.createDocumentFragment();
    filtered.forEach(mod => {
      const fileName = String(mod.fileName || "Archivo sin nombre");
      const card = document.createElement("article");
      card.className = "md-card";

      const top = document.createElement("div");
      top.className = "md-card-top";
      const icon = document.createElement("div");
      icon.className = "md-icon";
      icon.textContent = "MOD";
      const info = document.createElement("div");
      info.style.minWidth = "0";
      const name = document.createElement("div");
      name.className = "md-name";
      name.textContent = cleanName(fileName);
      const version = document.createElement("div");
      version.className = "md-version";
      version.textContent = displayVersion(fileName);
      info.append(name, version);
      top.append(icon, info);

      const file = document.createElement("div");
      file.className = "md-file";
      file.textContent = fileName;

      const foot = document.createElement("div");
      foot.className = "md-foot";
      const size = document.createElement("span");
      size.textContent = formatBytes(mod.sizeBytes);
      const included = document.createElement("span");
      included.textContent = "INCLUIDO";
      included.style.color = "var(--green,#9cff42)";
      foot.append(size, included);
      card.append(top, file, foot);
      frag.appendChild(card);
    });
    grid.appendChild(frag);
  };

  const loadManifest = async () => {
    if (loading || loaded) return;
    loading = true;
    $("mdGrid").innerHTML = '<div class="md-state">Cargando la lista de mods…</div>';
    try {
      const response = await fetch(`${MANIFEST_URL}?t=${Date.now()}`, { cache: "no-store" });
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      const manifest = await response.json();
      if (!manifest || !Array.isArray(manifest.mods)) {
        throw new Error("El manifiesto no contiene una lista válida de mods.");
      }
      allMods = manifest.mods.filter(mod => mod && typeof mod.fileName === "string");
      manifestVersion = String(manifest.modpackVersion || "sin versión");
      loaded = true;
      render();
    } catch (error) {
      $("mdCount").textContent = "SIN CONEXIÓN";
      $("mdGrid").innerHTML = "";
      const state = document.createElement("div");
      state.className = "md-state md-error";
      state.textContent = "No se pudo cargar la lista de mods. Comprueba tu conexión e inténtalo de nuevo.";
      const retry = document.createElement("button");
      retry.className = "md-back";
      retry.type = "button";
      retry.textContent = "REINTENTAR";
      retry.style.marginTop = "12px";
      retry.addEventListener("click", () => { loading = false; loadManifest(); });
      state.appendChild(document.createElement("br"));
      state.appendChild(retry);
      $("mdGrid").appendChild(state);
    } finally {
      loading = false;
    }
  };

  const openMods = () => {
    panel.classList.add("open");
    modsBtn.classList.add("active");
    loadManifest();
    $("mdSearch").focus();
  };
  const closeMods = () => {
    panel.classList.remove("open");
    modsBtn.classList.remove("active");
  };
  const toggleMods = () => panel.classList.contains("open") ? closeMods() : openMods();

  modsBtn.addEventListener("click", e => { e.preventDefault(); toggleMods(); });
  $("mdBackBtn").addEventListener("click", closeMods);
  $("mdSearch").addEventListener("input", render);
  if ($("czOptMods")) $("czOptMods").addEventListener("click", openMods);
  document.addEventListener("keydown", e => {
    if (e.key === "Escape" && panel.classList.contains("open")) closeMods();
  });

  // Avisar al launcher de que el script terminó de cargar no afecta al sincronizador.
  send({ type: "modsCatalogReady" });
})();
