// pestaña de skins: ves tu skin, guardas las que quieras en tu lista, subes pngs,
// copias la de otro jugador por su nombre y la aplicas a tu cuenta
// ojo: lo de hablar con mojang lo hace c# (MainWindow.xaml.cs), aqui solo se dibuja todo
(() => {
  const $ = id => document.getElementById(id);
  const send = obj => window.chrome && window.chrome.webview && window.chrome.webview.postMessage(obj);

  const LIB_KEY = "cz_skins_v1"; // tu lista de skins vive en el webview (localStorage)
  const MAX_SKINS = 40;

  let loggedIn = false;
  let asked = false;       // ya le pedimos a c# la skin actual?
  let busy = false;        // aplicando una skin ahorita
  let current = null;      // la skin que tiene tu cuenta: { variant, data }
  let library = [];        // tus skins guardadas: [{ id, name, variant, data }]
  let selectedId = null;   // la que se ve en la vista previa

  // ---------- cosas chicas ----------
  const uid = () => Math.random().toString(36).slice(2, 10);
  const dataUrl = b64 => "data:image/png;base64," + b64;

  const el = (tag, cls, text) => {
    const e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text !== undefined) e.textContent = text;
    return e;
  };

  const loadLibrary = () => {
    try {
      const raw = JSON.parse(localStorage.getItem(LIB_KEY) || "[]");
      if (Array.isArray(raw)) library = raw.filter(s => s && s.id && s.data);
    } catch { library = []; }
  };

  const saveLibrary = () => {
    try { localStorage.setItem(LIB_KEY, JSON.stringify(library)); } catch { }
  };

  // cargar imagenes una sola vez (si no, cada click las vuelve a decodificar)
  const imgCache = new Map();
  const loadImg = src => {
    if (!imgCache.has(src)) {
      imgCache.set(src, new Promise((ok, fail) => {
        const i = new Image();
        i.onload = () => ok(i);
        i.onerror = () => fail(new Error("no se pudo leer la imagen"));
        i.src = src;
      }));
    }
    return imgCache.get(src);
  };

  // ---------- dibujar el muñeco ----------
  // dibuja la skin de frente o de espalda, como la ves en minecraft.net
  // k = cuantas veces la agrandamos (sin suavizar, que se vean los pixeles)
  // las coordenadas son las del png de la skin, no me preguntes como las aprendi
  const drawSkin = (canvas, img, variant, back, k) => {
    const legacy = img.height === 32;     // skins viejas 64x32: no tienen capa extra ni brazo/pierna izquierda
    const slim = variant === "slim";
    const aw = slim ? 3 : 4;              // ancho del brazo (slim = 3, clasica = 4)
    const f = back ? 1 : 0;

    canvas.width = (aw * 2 + 8) * k;
    canvas.height = 32 * k;

    const ctx = canvas.getContext("2d");
    ctx.imageSmoothingEnabled = false;
    ctx.clearRect(0, 0, canvas.width, canvas.height);

    // sx,sy,sw,sh = de donde la sacamos del png | dx,dy = donde la ponemos en el muñeco | flip = espejo
    const put = (sx, sy, sw, sh, dx, dy, flip) => {
      ctx.save();
      if (flip) {
        ctx.translate((dx + sw) * k, dy * k);
        ctx.scale(-1, 1);
        ctx.drawImage(img, sx, sy, sw, sh, 0, 0, sw * k, sh * k);
      } else {
        ctx.drawImage(img, sx, sy, sw, sh, dx * k, dy * k, sw * k, sh * k);
      }
      ctx.restore();
    };

    // donde esta cada cosa en el png (depende de si miras de frente o de espalda)
    const rArmX = f ? (slim ? 51 : 52) : 44;
    const lArmX = f ? (slim ? 43 : 44) : 36;
    const lArmOX = f ? (slim ? 59 : 60) : 52;
    const rLegX = f ? 12 : 4;
    const lLegX = f ? 28 : 20;
    const lLegOX = f ? 12 : 4;

    const armR = dx => {
      put(rArmX, 20, aw, 12, dx, 8);
      if (!legacy) put(rArmX, 36, aw, 12, dx, 8);
    };
    const armL = dx => {
      if (legacy) put(rArmX, 20, aw, 12, dx, 8, true); // skins viejas: brazo izquierdo = derecho en espejo
      else { put(lArmX, 52, aw, 12, dx, 8); put(lArmOX, 52, aw, 12, dx, 8); }
    };
    const legR = dx => {
      put(rLegX, 20, 4, 12, dx, 20);
      if (!legacy) put(rLegX, 36, 4, 12, dx, 20);
    };
    const legL = dx => {
      if (legacy) put(rLegX, 20, 4, 12, dx, 20, true);
      else { put(lLegX, 52, 4, 12, dx, 20); put(lLegOX, 52, 4, 12, dx, 20); }
    };

    // de frente el brazo derecho del personaje queda a TU izquierda, de espalda al reves
    if (!back) { legR(aw); legL(aw + 4); }
    else { legL(aw); legR(aw + 4); }

    put(f ? 32 : 20, 20, 8, 12, aw, 8);                  // cuerpo
    if (!legacy) put(f ? 32 : 20, 36, 8, 12, aw, 8);     // chaqueta

    if (!back) { armR(0); armL(aw + 8); }
    else { armL(0); armR(aw + 8); }

    put(f ? 24 : 8, 8, 8, 8, aw, 0);                     // cabeza
    put(f ? 56 : 40, 8, 8, 8, aw, 0);                    // sombrero (la capa de encima)
  };

  // detecta si una skin es slim: en las slim las 2 columnas de al lado del brazo estan vacias
  // no es perfecto, por eso el jugador igual puede cambiarlo a mano
  const detectSlim = img => {
    if (img.height !== 64) return false;
    const c = document.createElement("canvas");
    c.width = 64; c.height = 64;
    const ctx = c.getContext("2d");
    ctx.drawImage(img, 0, 0);
    return ctx.getImageData(54, 25, 1, 1).data[3] === 0 && ctx.getImageData(55, 25, 1, 1).data[3] === 0;
  };

  // ---------- estilos ----------
  const style = document.createElement("style");
  style.textContent = `
    .cz-skins{position:fixed;left:0;right:0;top:58px;bottom:42px;z-index:15;display:none;flex-direction:column;background:var(--background)}
    .cz-skins.open{display:flex}
    .sk-top{display:flex;align-items:center;gap:18px;padding:16px 26px;border-bottom:1px solid var(--border)}
    .sk-back{height:30px;padding:0 12px;background:transparent;border:1px solid var(--border);color:var(--text-soft);font-family:var(--mono);font-size:9px;letter-spacing:.12em;cursor:pointer}
    .sk-back:hover{color:var(--text);background:var(--surface-2)}
    .sk-title{font-size:16px;font-weight:600;letter-spacing:.16em}
    .sk-body{flex:1;min-height:0;display:grid;grid-template-columns:320px 1fr}
    .sk-preview{padding:22px 24px;border-right:1px solid var(--border);overflow-y:auto;background:linear-gradient(180deg,rgba(156,255,66,.02),transparent 40%)}
    .sk-stage{display:flex;justify-content:center;gap:18px;padding:14px 0 20px}
    .sk-stage canvas{image-rendering:pixelated;max-width:128px;height:auto;filter:drop-shadow(0 8px 18px rgba(0,0,0,.5))}
    .sk-empty{display:none;padding:50px 10px;text-align:center;color:var(--muted);font-size:12px;line-height:1.6}
    .sk-input{width:100%;height:36px;padding:0 12px;background:var(--surface-2);border:1px solid var(--border);color:var(--text);font-family:var(--main);font-size:12px;outline:none}
    .sk-input:focus{border-color:var(--border-hover)}
    .sk-input:disabled{opacity:.5}
    .sk-model{display:flex;gap:1px;margin-top:12px}
    .sk-model button{flex:1;height:34px;background:var(--surface-2);border:1px solid var(--border);color:var(--muted);font-family:var(--mono);font-size:9px;letter-spacing:.12em;cursor:pointer}
    .sk-model button.on{color:var(--green);border-color:var(--border-hover);background:rgba(156,255,66,.05)}
    .sk-model button:disabled{cursor:default;opacity:.6}
    .sk-status{margin-top:14px;min-height:16px;color:var(--text-soft);font-family:var(--mono);font-size:10px;line-height:1.5}
    .sk-notice{display:none;margin-top:14px;padding:10px 12px;border:1px dashed var(--border-hover);color:var(--muted);font-size:11px;line-height:1.5}
    .sk-library{padding:22px 26px;overflow-y:auto;min-width:0}
    .sk-actions{display:flex;gap:10px;flex-wrap:wrap;align-items:center}
    .sk-add{height:36px;padding:0 16px;background:var(--green);color:#0d100c;font-size:11px;font-weight:700;letter-spacing:.12em;cursor:pointer}
    .sk-add:hover{background:#b1ff68}
    .sk-lookup{display:flex;gap:1px;flex:1;min-width:220px;max-width:420px}
    .sk-lookup .sk-input{flex:1}
    .sk-lookup button{height:36px;padding:0 14px;background:var(--surface-3);border:1px solid var(--border);color:var(--text-soft);font-family:var(--mono);font-size:9px;letter-spacing:.12em;cursor:pointer}
    .sk-lookup button:hover{color:var(--text);border-color:var(--border-hover)}
    .sk-grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(112px,1fr));gap:12px;margin-top:20px}
    .sk-card{position:relative;padding:14px 8px 10px;display:flex;flex-direction:column;align-items:center;gap:10px;background:var(--surface);border:1px solid var(--border);cursor:pointer;transition:border-color .15s,background .15s}
    .sk-card:hover{background:var(--surface-2);border-color:var(--border-hover)}
    .sk-card.sel{border-color:var(--green);background:rgba(156,255,66,.04)}
    .sk-card canvas{image-rendering:pixelated;height:112px;width:auto}
    .sk-card-name{max-width:100%;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;color:var(--text-soft);font-size:11px}
    .sk-badge{position:absolute;top:6px;right:6px;padding:2px 5px;background:rgba(156,255,66,.12);color:var(--green);font-family:var(--mono);font-size:7px;letter-spacing:.08em}
    .socials .social.active{color:var(--green)}
    @media (max-width:950px){.sk-body{grid-template-columns:260px 1fr}}
    @media (max-width:700px){.cz-skins{top:55px;bottom:40px}.sk-body{grid-template-columns:1fr;overflow-y:auto}.sk-preview{border-right:0;border-bottom:1px solid var(--border)}}
  `;
  document.head.appendChild(style);

  // ---------- html ----------
  const panel = document.createElement("section");
  panel.className = "cz-skins";
  panel.id = "czSkins";
  panel.innerHTML = `
    <div class="sk-top">
      <button class="sk-back" id="skBackBtn">← VOLVER</button>
      <div class="sk-title">SKINS</div>
    </div>

    <div class="sk-body">
      <aside class="sk-preview">
        <div class="sk-stage" id="skStage">
          <canvas id="skFront"></canvas>
          <canvas id="skBackView"></canvas>
        </div>
        <div class="sk-empty" id="skEmpty">Todavía no hay nada que ver.<br>Sube un .png o importa la skin de un jugador.</div>

        <div id="skControls">
          <input class="sk-input" id="skName" maxlength="24" placeholder="Nombre de la skin">

          <div class="sk-model">
            <button data-v="classic">CLÁSICO</button>
            <button data-v="slim">SLIM</button>
          </div>

          <button class="cz-btn" id="skApply" style="margin-top:16px">APLICAR SKIN</button>
          <button class="cz-btn ghost" id="skSecondary" style="margin-top:10px">ELIMINAR</button>
        </div>

        <div class="sk-notice" id="skNotice">Inicia sesión con tu cuenta de Microsoft para poder aplicar skins.</div>
        <div class="sk-status" id="skStatus"></div>
      </aside>

      <div class="sk-library">
        <div class="section-label">TUS SKINS</div>

        <div class="sk-actions">
          <button class="sk-add" id="skUpload">+ SUBIR PNG</button>
          <input type="file" id="skFile" accept="image/png" hidden>

          <div class="sk-lookup">
            <input class="sk-input" id="skUser" maxlength="16" placeholder="Copiar la skin de un jugador...">
            <button id="skImport">IMPORTAR</button>
          </div>
        </div>

        <div class="sk-grid" id="skGrid"></div>
      </div>
    </div>
  `;
  document.body.appendChild(panel);

  // boton "SKINS" en la barra de arriba, el primero de la fila de links
  const skinsBtn = document.createElement("a");
  skinsBtn.className = "social";
  skinsBtn.href = "#";
  skinsBtn.title = "Skins";
  skinsBtn.innerHTML = `
    <svg viewBox="0 0 24 24"><path d="M12 12a4 4 0 1 0 0-8 4 4 0 0 0 0 8Zm0 2c-4.4 0-8 2.2-8 5v1h16v-1c0-2.8-3.6-5-8-5Z"/></svg>
    <span>SKINS</span>
  `;
  const nav = document.querySelector(".socials");
  if (nav) nav.prepend(skinsBtn);

  // tambien en el menu del perfil, arriba de todo
  const optSettings = $("czOptSettings");
  if (optSettings) {
    optSettings.insertAdjacentHTML("beforebegin", '<div class="profile-option" id="czOptSkins">Skins</div>');
  }

  // ---------- lo que se ve ----------
  const setStatus = (text, bad) => {
    const s = $("skStatus");
    s.textContent = text;
    s.style.color = bad ? "#d96a4d" : "";
  };

  // la lista completa: la skin actual de tu cuenta (si no esta ya guardada) + tus guardadas
  const items = () => {
    const list = [];
    const saved = current && library.find(s => s.data === current.data && s.variant === current.variant);
    if (current && !saved) {
      list.push({ id: "__current", name: "Skin actual", variant: current.variant, data: current.data, isCurrent: true });
    }
    return list.concat(library);
  };

  const sel = () => items().find(i => i.id === selectedId) || null;
  const isInUse = it => !!current && current.data === it.data && current.variant === it.variant;

  const renderGrid = () => {
    const grid = $("skGrid");
    grid.innerHTML = "";

    const list = items();
    if (!list.length) {
      grid.appendChild(el("div", "cz-hint", "Todavía no tienes skins guardadas. Sube un .png o importa la de un jugador."));
      return;
    }

    list.forEach(it => {
      const card = el("div", "sk-card" + (it.id === selectedId ? " sel" : ""));
      const cv = document.createElement("canvas");
      card.appendChild(cv);
      card.appendChild(el("div", "sk-card-name", it.name));
      if (isInUse(it)) card.appendChild(el("div", "sk-badge", "EN USO"));

      card.addEventListener("click", () => { selectedId = it.id; renderAll(); });
      grid.appendChild(card);

      loadImg(it.data).then(img => drawSkin(cv, img, it.variant, false, 4)).catch(() => { });
    });
  };

  const renderPreview = () => {
    const it = sel();

    $("skStage").style.display = it ? "flex" : "none";
    $("skEmpty").style.display = it ? "none" : "block";
    $("skControls").style.display = it ? "block" : "none";
    $("skNotice").style.display = loggedIn ? "none" : "block";
    if (!it) return;

    loadImg(it.data).then(img => {
      drawSkin($("skFront"), img, it.variant, false, 8);
      drawSkin($("skBackView"), img, it.variant, true, 8);
    }).catch(() => setStatus("No pude leer esa skin", true));

    $("skName").value = it.name;
    $("skName").disabled = !!it.isCurrent;

    document.querySelectorAll(".sk-model button").forEach(b => {
      b.classList.toggle("on", b.dataset.v === it.variant);
      b.disabled = !!it.isCurrent; // la skin actual viene de mojang, esa no se toca
    });

    const inUse = isInUse(it);
    const apply = $("skApply");
    apply.style.display = it.isCurrent ? "none" : "block";
    apply.disabled = busy || !loggedIn || inUse;
    apply.textContent = inUse ? "EN USO" : busy ? "APLICANDO..." : "APLICAR SKIN";

    $("skSecondary").textContent = it.isCurrent ? "GUARDAR EN MI LISTA" : "ELIMINAR";
  };

  const renderAll = () => { renderGrid(); renderPreview(); };

  // agrega una skin a tu lista (si ya la tenias igualita, solo la selecciona)
  const addSkin = (name, variant, data) => {
    const dup = library.find(s => s.data === data && s.variant === variant);
    if (dup) { selectedId = dup.id; return dup; }

    if (library.length >= MAX_SKINS) {
      setStatus(`Ya tienes ${MAX_SKINS} skins guardadas, borra alguna.`, true);
      return null;
    }

    const it = { id: uid(), name: (name || "Mi skin").slice(0, 24), variant, data };
    library.unshift(it);
    selectedId = it.id;
    saveLibrary();
    return it;
  };

  // la cabecita de arriba, dibujada con la skin de verdad (asi se actualiza al instante)
  const syncHead = () => {
    if (!loggedIn || !current) return;

    loadImg(current.data).then(img => {
      const c = document.createElement("canvas");
      c.width = 64; c.height = 64;
      const ctx = c.getContext("2d");
      ctx.imageSmoothingEnabled = false;
      ctx.drawImage(img, 8, 8, 8, 8, 0, 0, 64, 64);   // cara
      ctx.drawImage(img, 40, 8, 8, 8, 0, 0, 64, 64);  // sombrero
      const head = document.querySelector(".profile-button img.avatar");
      if (head) head.src = c.toDataURL("image/png");
    }).catch(() => { });
  };

  // ---------- abrir / cerrar ----------
  const openSkins = () => {
    panel.classList.add("open");
    skinsBtn.classList.add("active");
    if (loggedIn && !current) send({ type: "getSkin" });
    renderAll();
  };

  const closeSkins = () => {
    panel.classList.remove("open");
    skinsBtn.classList.remove("active");
  };

  const toggleSkins = () => (panel.classList.contains("open") ? closeSkins() : openSkins());

  skinsBtn.addEventListener("click", e => { e.preventDefault(); toggleSkins(); });
  if ($("czOptSkins")) $("czOptSkins").addEventListener("click", openSkins);
  $("skBackBtn").addEventListener("click", closeSkins);

  document.addEventListener("keydown", e => {
    if (e.key !== "Escape" || !panel.classList.contains("open")) return;
    if (document.querySelector(".cz-overlay.open, .overlay.open")) return; // primero se cierra lo que este encima
    closeSkins();
  });

  // ---------- acciones ----------
  // subir un png
  $("skUpload").addEventListener("click", () => $("skFile").click());

  $("skFile").addEventListener("change", () => {
    const file = $("skFile").files[0];
    $("skFile").value = "";
    if (!file) return;

    if (file.type !== "image/png") { setStatus("Tiene que ser un archivo .png", true); return; }
    if (file.size > 100 * 1024) { setStatus("Ese archivo pesa demasiado para ser una skin", true); return; }

    const reader = new FileReader();
    reader.onload = () => {
      const data = reader.result;

      loadImg(data).then(img => {
        if (img.width !== 64 || (img.height !== 64 && img.height !== 32)) {
          setStatus(`La skin debe medir 64x64 (o 64x32 las viejas). La tuya mide ${img.width}x${img.height}.`, true);
          return;
        }

        const variant = detectSlim(img) ? "slim" : "classic";
        const it = addSkin(file.name.replace(/\.png$/i, ""), variant, data);
        if (it) setStatus(`Skin agregada (${variant === "slim" ? "slim" : "clásica"}). Si los brazos se ven raros, cambia el modelo y dale a aplicar.`, false);
        renderAll();
      }).catch(() => setStatus("No pude leer esa imagen", true));
    };
    reader.readAsDataURL(file);
  });

  // importar la skin de otro jugador (c# la busca en mojang)
  const doImport = () => {
    const name = $("skUser").value.trim();
    if (!name) return;
    setStatus(`Buscando la skin de ${name}...`, false);
    send({ type: "lookupSkin", name });
  };
  $("skImport").addEventListener("click", doImport);
  $("skUser").addEventListener("keydown", e => { if (e.key === "Enter") doImport(); });

  // cambiar el nombre
  $("skName").addEventListener("change", () => {
    const it = sel();
    const saved = it && library.find(s => s.id === it.id);
    if (!saved) return;
    saved.name = $("skName").value.trim().slice(0, 24) || "Mi skin";
    saveLibrary();
    renderGrid();
  });

  // clasico / slim
  document.querySelectorAll(".sk-model button").forEach(b => {
    b.addEventListener("click", () => {
      const it = sel();
      const saved = it && library.find(s => s.id === it.id);
      if (!saved) return;
      saved.variant = b.dataset.v;
      saveLibrary();
      renderAll();
    });
  });

  // aplicar en tu cuenta
  $("skApply").addEventListener("click", () => {
    const it = sel();
    if (!it || busy || !loggedIn) return;

    busy = true;
    setStatus("Aplicando skin...", false);
    renderPreview();
    send({ type: "applySkin", variant: it.variant, data: it.data.split(",")[1] });
  });

  // eliminar (o guardar, si es la skin actual de la cuenta)
  $("skSecondary").addEventListener("click", () => {
    const it = sel();
    if (!it) return;

    if (it.isCurrent) {
      addSkin("Mi skin", it.variant, it.data);
      setStatus("Guardada en tu lista.", false);
    } else {
      library = library.filter(s => s.id !== it.id);
      selectedId = null;
      saveLibrary();
      setStatus("Skin eliminada de tu lista (la de tu cuenta no se toca).", false);
    }
    renderAll();
  });

  // ---------- mensajes que llegan de c# ----------
  const askSkin = () => {
    if (asked) return;
    asked = true;
    send({ type: "getSkin" });
  };

  const resetSession = () => {
    asked = false;
    current = null;
    busy = false;
  };

  const handle = m => {
    switch (m.type) {
      case "state":
        loggedIn = !!m.loggedIn;
        if (loggedIn) askSkin(); else resetSession();
        renderAll();
        break;

      case "loggedIn":
        loggedIn = true;
        askSkin();
        renderPreview();
        break;

      case "loggedOut":
        loggedIn = false;
        resetSession();
        renderAll();
        break;

      case "currentSkin":
        current = m.data ? { variant: m.variant, data: dataUrl(m.data) } : null;
        if (!selectedId && current) selectedId = "__current";
        syncHead();
        renderAll();
        break;

      case "skinApplied":
        busy = false;
        current = { variant: m.variant, data: dataUrl(m.data) };
        setStatus("Listo, tu skin ya cambió. En el juego la ves cuando vuelvas a entrar al servidor.", false);
        syncHead();
        renderAll();
        break;

      case "skinLookup": {
        const it = addSkin(m.name, m.variant, dataUrl(m.data));
        if (it) setStatus(`Skin de ${m.name} agregada a tu lista.`, false);
        $("skUser").value = "";
        renderAll();
        break;
      }

      case "skinError":
        busy = false;
        setStatus(m.message, true);
        renderPreview();
        break;
    }
  };

  if (window.chrome && window.chrome.webview) {
    window.chrome.webview.addEventListener("message", e => handle(e.data));
  }

  loadLibrary();
  selectedId = library[0] ? library[0].id : null;
  renderAll();

  // este script carga despues que el resto, asi que le pedimos a c# que nos reenvie el estado
  // (si no, nos perdemos el primer mensaje de sesion. c# ya aguanta que le manden "ready" varias veces)
  send({ type: "ready" });
})();