// Ajustes visuales de la pestaña de skins.
// Cargar después de skins.js. No cambia la lógica de subida, importación ni aplicación.
(() => {
  const style = document.createElement("style");
  style.textContent = `
    .cz-skins .sk-top{padding:14px 24px}
    .cz-skins .sk-body{grid-template-columns:minmax(290px,350px) minmax(0,1fr)}
    .cz-skins .sk-preview{padding:20px 22px}
    .cz-skins .sk-library{padding:22px 24px}
    .cz-skins .sk-stage{align-items:flex-end;gap:22px;padding:12px 0 24px}
    .cz-skins .sk-stage canvas{max-width:46%;max-height:245px;object-fit:contain}
    .cz-skins #skControls{max-width:420px;margin:0 auto}
    .cz-skins .sk-model{gap:8px}
    .cz-skins .sk-model button{border-radius:2px}
    .cz-skins #skApply,.cz-skins #skSecondary{width:100%;min-height:38px}
    .cz-skins .sk-actions{gap:10px}
    .cz-skins .sk-add{border:0}
    .cz-skins .sk-lookup{max-width:500px}
    .cz-skins .sk-grid{grid-template-columns:repeat(auto-fill,minmax(125px,1fr));gap:14px}
    .cz-skins .sk-card{min-height:168px;padding:12px 8px}
    .cz-skins .sk-card canvas{height:112px;max-width:100%}
    .cz-skins .sk-card-name{width:100%;text-align:center}
    .cz-skins .sk-notice{max-width:420px;margin-left:auto;margin-right:auto}
    @media(max-width:850px){
      .cz-skins .sk-body{grid-template-columns:minmax(240px,290px) minmax(0,1fr)}
      .cz-skins .sk-preview,.cz-skins .sk-library{padding:16px}
      .cz-skins .sk-grid{grid-template-columns:repeat(auto-fill,minmax(105px,1fr))}
    }
    @media(max-width:700px){
      .cz-skins .sk-body{display:flex;flex-direction:column;overflow-y:auto}
      .cz-skins .sk-preview{border-right:0;border-bottom:1px solid var(--border);overflow:visible}
      .cz-skins .sk-library{overflow:visible}
      .cz-skins .sk-stage canvas{max-height:190px}
      .cz-skins .sk-actions{align-items:stretch}
      .cz-skins .sk-lookup{min-width:100%;max-width:none}
    }
  `;
  document.head.appendChild(style);
})();
