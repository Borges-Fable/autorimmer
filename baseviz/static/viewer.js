"use strict";
// baseviz canvas viewer. Fetches a render model from the server and draws
// terrain -> thing layers -> roof, with pan/zoom and a hover tooltip.

const $ = (id) => document.getElementById(id);
const canvas = $("c"), ctx = canvas.getContext("2d");

let data = null;          // {ir, things, terrain}
let view = { x: 40, y: 40, scale: 1 };
let layerOn = [];         // per-thing-layer visibility
const opt = { cell: 16, terrain: true, roof: false, glyphs: true, flip: false };

function rgb(c) { return `rgb(${c[0]},${c[1]},${c[2]})`; }

async function init() {
  const layouts = await (await fetch("/api/layouts")).json();
  const pick = $("pick");
  pick.innerHTML = layouts.map(l => `<option value="${encodeURIComponent(l.path)}">${l.name}</option>`).join("");
  pick.onchange = () => loadLayout(pick.value);
  bindControls();
  resize();
  if (layouts.length) loadLayout(pick.value);
}

async function loadLayout(encPath) {
  const res = await fetch("/api/layout?path=" + encPath);
  data = await res.json();
  if (data.error) { $("meta").textContent = "Error: " + data.error; return; }
  const n = data.ir.layers.length;
  layerOn = Array.from({ length: n }, () => true);
  renderLayerButtons();
  const ext = data.ir.extension || {};
  const [w, h] = data.ir.size;
  $("meta").innerHTML =
    `<b>${data.ir.defName}</b> &nbsp; ${w}x${h} &nbsp; ` +
    `<span class="muted">layers ${n} · stage ${ext.stage ?? "?"} · ` +
    `${ext.size ?? "?"}/${ext.techLevel ?? "?"} · ` +
    `animals ${ (data.ir.animalCells||[]).length } · ` +
    `${Object.keys(data.things).length} thing types</span>`;
  draw();
}

function renderLayerButtons() {
  const box = $("layers");
  box.innerHTML = "Layers: ";
  layerOn.forEach((on, i) => {
    const b = document.createElement("button");
    b.textContent = i;
    b.className = on ? "on" : "";
    b.onclick = () => { layerOn[i] = !layerOn[i]; b.className = layerOn[i] ? "on" : ""; draw(); };
    box.appendChild(b);
  });
}

function bindControls() {
  $("cell").oninput = (e) => { opt.cell = +e.target.value; draw(); };
  for (const k of ["terrain", "roof", "glyphs", "flip"])
    $(k).onchange = (e) => { opt[k] = e.target.checked; draw(); };

  let dragging = false, last = null;
  canvas.onmousedown = (e) => { dragging = true; last = [e.clientX, e.clientY]; canvas.classList.add("drag"); };
  window.onmouseup = () => { dragging = false; canvas.classList.remove("drag"); };
  window.onmousemove = (e) => {
    if (dragging) {
      view.x += e.clientX - last[0]; view.y += e.clientY - last[1];
      last = [e.clientX, e.clientY]; draw();
    } else { hover(e); }
  };
  canvas.onwheel = (e) => {
    e.preventDefault();
    const f = e.deltaY < 0 ? 1.1 : 1 / 1.1;
    const mx = e.clientX, my = e.clientY - canvas.getBoundingClientRect().top;
    view.x = mx - (mx - view.x) * f;
    view.y = my - (my - view.y) * f;
    view.scale *= f; draw();
  };
  window.onresize = () => { resize(); draw(); };
}

function resize() {
  canvas.width = window.innerWidth;
  canvas.height = window.innerHeight - canvas.getBoundingClientRect().top;
}

function cellPx() { return opt.cell * view.scale; }
// XML rows are written south-first, so by default draw row 0 at the bottom
// (north up, matching the in-game view). "Flip Y" reverts to raw row order.
function rowToY(r, h) { return opt.flip ? r : (h - 1 - r); }

function draw() {
  ctx.fillStyle = "#0e0f11";
  ctx.fillRect(0, 0, canvas.width, canvas.height);
  if (!data) return;
  const [w, h] = data.ir.size, cp = cellPx();
  const ox = view.x, oy = view.y;

  // terrain
  if (opt.terrain) {
    data.ir.terrain.forEach((row, r) => row.forEach((tok, c) => {
      if (!tok || tok === ".") return;
      const t = data.terrain[tok]; if (!t) return;
      ctx.fillStyle = rgb(t.color);
      ctx.fillRect(ox + c * cp, oy + rowToY(r, h) * cp, cp + 0.5, cp + 0.5);
    }));
  }

  // thing layers
  data.ir.layers.forEach((layer, li) => {
    if (!layerOn[li]) return;
    layer.forEach((row, r) => row.forEach((tok, c) => {
      if (!tok || tok === ".") return;
      const s = data.things[tok]; if (!s) return;
      const sw = s.size[0] || 1, sh = s.size[1] || 1;
      const x = ox + c * cp, y = oy + rowToY(r, h) * cp;
      ctx.fillStyle = rgb(s.color);
      ctx.globalAlpha = s.known ? 1 : 0.5;
      ctx.fillRect(x + 1, y + 1, sw * cp - 2, sh * cp - 2);
      ctx.globalAlpha = 1;
      if (!s.known) { ctx.strokeStyle = "#e0556a"; ctx.strokeRect(x + 1, y + 1, sw * cp - 2, sh * cp - 2); }
      if (opt.glyphs && cp >= 12) {
        ctx.fillStyle = lum(s.color) > 140 ? "#111" : "#eee";
        ctx.font = `${Math.min(cp * 0.5, 13)}px ui-monospace, monospace`;
        ctx.textAlign = "center"; ctx.textBaseline = "middle";
        ctx.fillText(s.glyph, x + sw * cp / 2, y + sh * cp / 2);
      }
    }));
  });

  // roof overlay
  if (opt.roof && data.ir.roof) {
    ctx.fillStyle = "rgba(40,80,150,0.18)";
    data.ir.roof.forEach((row, r) => row.forEach((v, c) => {
      if (v) ctx.fillRect(ox + c * cp, oy + rowToY(r, h) * cp, cp, cp);
    }));
  }

  // animals
  (data.ir.animalCells || []).forEach(a => {
    const [ax, ay] = a.offset;
    const x = ox + ax * cp, y = oy + rowToY(ay, h) * cp;
    ctx.fillStyle = "#3fcf6a";
    ctx.beginPath(); ctx.arc(x + cp / 2, y + cp / 2, cp * 0.3, 0, 7); ctx.fill();
  });
}

function lum(c) { return 0.299 * c[0] + 0.587 * c[1] + 0.114 * c[2]; }

function hover(e) {
  const tip = $("tip");
  if (!data) return;
  const cp = cellPx(), [w, h] = data.ir.size;
  const top = canvas.getBoundingClientRect().top;
  const c = Math.floor((e.clientX - view.x) / cp);
  const ry = Math.floor((e.clientY - top - view.y) / cp);
  const r = opt.flip ? ry : (h - 1 - ry);
  if (c < 0 || r < 0 || c >= w || r >= h) { tip.style.display = "none"; return; }
  // topmost visible thing at this cell
  let found = null;
  for (let li = data.ir.layers.length - 1; li >= 0; li--) {
    if (!layerOn[li]) continue;
    const tok = (data.ir.layers[li][r] || [])[c];
    if (tok && tok !== ".") { found = data.things[tok]; break; }
  }
  const terr = (data.ir.terrain[r] || [])[c];
  if (!found && (!terr || terr === ".")) { tip.style.display = "none"; return; }
  let html = `<span class="muted">(${c}, ${r})</span><br>`;
  if (found) html += `<b>${found.label}</b> <span class="muted">${found.category}` +
    `${found.stuff ? " · " + found.stuff : ""}${found.rot ? " · " + found.rot : ""}` +
    `${found.known ? "" : " · UNKNOWN"}</span><br>`;
  if (terr && terr !== ".") html += `<span class="muted">floor:</span> ${data.terrain[terr]?.label || terr}`;
  tip.innerHTML = html;
  tip.style.display = "block";
  tip.style.left = (e.clientX + 14) + "px";
  tip.style.top = (e.clientY + 14) + "px";
}

init();
