'use strict';

const $ = id => document.getElementById(id);
const SVG = 'http://www.w3.org/2000/svg';
const GRID = 120;
const reducedMotion = matchMedia('(prefers-reduced-motion: reduce)');

const state = {
  pending: new Map(),
  snapshot: null,
  view: 'desk',
  selected: null,
  filter: '',
  showWindows: true,
  showMinimized: false,
  grid: false,
  drag: null,
  layout: null,
  gatherArmedUntil: 0,
  toastTimer: 0,
  orbTimer: 0
};

/* ── host bridge ─────────────────────────────────────────────────────── */

// Chassis (chassis/chassis.js) is the shell: the version orb button and its pop-out (Settings, Dependencies, Help,
// Diagnostics), glass with its solid fallback, and snapshot runs. This page adds its own Settings rows, its
// help, and the live Windows checks on the Diagnostics tab.
const chassis = Chassis.start({
  manualSnapshot: true,            // a snapshot waits for the first real state (finishSnapshot)
  help: helpHtml,
  keys: 'Ctrl+Alt+Home moves the foreground window to the display under the mouse, from anywhere. In this window: / filters, F5 refreshes, Esc clears the selection.',
  settings: { html: settingsHtml, bind: bindSettings },
  diag: diagHtml,
  onHello: () => finishSnapshot(),
  onMessage: onHostMessage
});

// The orb thinks while host calls are in flight, flashes success or error when an action finishes, and
// rests otherwise. Right-click the version button to restyle the orb (the look is saved per app).
let orbNow = 'idle';
function setOrb(orbState, holdMs = 0) {
  clearTimeout(state.orbTimer);
  orbNow = orbState;
  chassis.setOrb(orbState);
  if (holdMs > 0) state.orbTimer = setTimeout(() => setOrb(state.pending.size ? 'thinking' : 'idle'), holdMs);
}

function callHost(method, params = {}) {
  const id = `${Date.now()}-${Math.random().toString(16).slice(2)}`;
  return new Promise((resolve, reject) => {
    if (!window.chrome?.webview) {
      reject(new Error('The Screen Shepherd host is not connected. Launch ScreenShepherd.exe.'));
      return;
    }
    const settle = () => { if (!state.pending.size && orbNow === 'thinking') setOrb('idle'); };
    state.pending.set(id, {
      resolve: value => { resolve(value); settle(); },
      reject: error => { reject(error); settle(); }
    });
    if (orbNow === 'idle') setOrb('thinking');
    chassis.post({ cmd: 'call', id, method, params });
    setTimeout(() => {
      const pending = state.pending.get(id);
      if (!pending) return;
      state.pending.delete(id);
      pending.reject(new Error(`${method} did not answer within 15 seconds.`));
    }, 15000);
  });
}

// Messages Chassis doesn't handle itself: pushed state, and answers to callHost.
function onHostMessage(message) {
  if (message.type === 'state' && message.ok) { renderState(message.result); return; }
  if (message.type !== 'response') return;
  const pending = state.pending.get(message.id);
  if (!pending) return;
  state.pending.delete(message.id);
  if (message.ok) pending.resolve(message.result);
  else pending.reject(new Error(message.error?.message ?? 'The host reported an error.'));
}

async function runAction(method, params, fallbackMessage) {
  try {
    const result = await callHost(method, params);
    const failed = result?.ok === false || (typeof result?.moved === 'number' && result.found > 0 && result.moved < result.found);
    toast(result?.message ?? fallbackMessage ?? 'Done.', failed ? 'warn' : 'ok');
    await refreshState();
    setOrb(failed ? 'error' : 'success', failed ? 2600 : 1400);
    return result;
  } catch (error) {
    toast(error.message, 'bad');
    setOrb('error', 2600);
    return null;
  }
}

async function refreshState() {
  try {
    renderState(await callHost('app.getState'));
  } catch (error) {
    $('capturedAt').textContent = 'host offline';
    toast(error.message, 'bad');
    setOrb('error', 2600);
  }
}

/* ── helpers ─────────────────────────────────────────────────────────── */

function esc(value) {
  return String(value ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}
const runOf = monitor => (String(monitor?.displayName ?? '').match(/\d+$/) || [monitor?.number ?? '?'])[0];
const monitorLabel = monitor => `DISPLAY ${runOf(monitor)}`;
const byDevice = device => state.snapshot?.monitors.find(m => m.deviceName === device);
const hz = value => Number(value) > 0 ? `${Number(value).toFixed(Number(value) % 1 ? 2 : 0)} Hz` : 'unknown refresh';
const levelClass = level => level === 'ok' ? 'ok' : level === 'bad' ? 'bad' : level === 'warn' ? 'warn' : 'info';
const isStray = w => w.rescueCandidate;
function identity(m) {
  const maker = String(m.manufacturer ?? '').trim(), model = String(m.model ?? '').trim();
  return model.toLowerCase().startsWith(maker.toLowerCase()) ? model : [maker, model].filter(Boolean).join(' ');
}
function icon(name) { return `<svg class="i"><use href="#i-${name}"/></svg>`; }
function el(tag, attrs = {}, parent) {
  const node = document.createElementNS(SVG, tag);
  for (const [key, value] of Object.entries(attrs)) if (value !== undefined && value !== null) node.setAttribute(key, value);
  parent?.appendChild(node);
  return node;
}

function toast(message, tone = 'ok') {
  const node = $('toast');
  node.textContent = message;
  node.dataset.tone = tone;
  node.classList.add('visible');
  clearTimeout(state.toastTimer);
  state.toastTimer = setTimeout(() => node.classList.remove('visible'), 3600);
}

/* ── state → page ────────────────────────────────────────────────────── */

function renderState(snapshot) {
  state.snapshot = snapshot;
  if (state.selected && !snapshot.windows.some(w => w.handle === state.selected)) state.selected = null;
  $('monitorCount').textContent = snapshot.monitors.length;
  $('windowCount').textContent = snapshot.windows.length;
  const strays = snapshot.windows.filter(isStray).length;
  $('strayCount').textContent = strays;
  $('strayStatus').className = strays ? 'stray' : '';
  $('watchStatus').textContent = snapshot.settings.autoRescueNewWindows ? 'armed' : 'off';
  $('hotkeyStatus').textContent = snapshot.hotkey.status;
  $('capturedAt').textContent = new Date(snapshot.capturedAt).toLocaleTimeString();
  $('rescueCount').textContent = strays;
  $('rescueAll').classList.toggle('calm', strays === 0);
  $('rescueAll').disabled = strays === 0;
  $('rescueAll').title = strays ? 'Drive every stranded window to its nearest display' : 'No stranded windows';

  const healthIssue = snapshot.monitors.some(m => m.healthLevel !== 'ok') || snapshot.graphics.healthLevel !== 'ok' || snapshot.graphics.optimizationLevel === 'warn';
  $('healthFlag').hidden = !healthIssue;

  if (!state.drag) renderField();
  renderFlock();
  renderGatherTargets();
  renderVerdicts();
  renderHealth();
  // The Diagnostics tab is drawn when it opens; if that was before the first state, draw it again with the checks.
  if (chassis.tab === 'diag' && !document.querySelector('[data-report="copy"]')) chassis.refresh();
  finishSnapshot();
}

/* ── the field (to-scale desk map) ───────────────────────────────────── */

function renderField() {
  const host = $('field');
  const snapshot = state.snapshot;
  host.replaceChildren();
  if (!snapshot || !snapshot.monitors.length) return;
  const W = host.clientWidth, H = host.clientHeight;
  if (!W || !H) return;

  const monitors = snapshot.monitors;
  const desk = {
    x: Math.min(...monitors.map(m => m.bounds.x)), y: Math.min(...monitors.map(m => m.bounds.y)),
    r: Math.max(...monitors.map(m => m.bounds.right)), b: Math.max(...monitors.map(m => m.bounds.bottom))
  };
  desk.w = desk.r - desk.x; desk.h = desk.b - desk.y;
  const strays = snapshot.windows.filter(isStray);
  const beyond = strays.filter(w => w.status === 'off-screen');
  const pad = { l: 46, r: 46, t: 112, b: beyond.length ? 168 : 70 };
  const scale = Math.max(0.02, Math.min((W - pad.l - pad.r) / desk.w, (H - pad.t - pad.b) / desk.h));
  const ox = (W - desk.w * scale) / 2 - desk.x * scale;
  const oy = pad.t + Math.max(0, (H - pad.t - pad.b - desk.h * scale) / 2) - desk.y * scale;
  const X = v => ox + v * scale, Y = v => oy + v * scale, S = v => v * scale;
  state.layout = { scale, ox, oy, X, Y, S, desk, W, H };

  const svg = el('svg', { viewBox: `0 0 ${W} ${H}` }, host);
  const defs = el('defs', {}, svg);
  const hatch = el('pattern', { id: 'beyondHatch', width: 9, height: 9, patternUnits: 'userSpaceOnUse', patternTransform: 'rotate(45)' }, defs);
  el('line', { x1: 0, y1: 0, x2: 0, y2: 9, stroke: 'rgba(236,231,217,.16)', 'stroke-width': 1.2 }, hatch);
  const head = el('marker', { id: 'driveHead', viewBox: '0 0 10 10', refX: 7, refY: 5, markerWidth: 7, markerHeight: 7, orient: 'auto' }, defs);
  el('path', { d: 'M0,0 L10,5 L0,10 z', class: 'drive-head' }, head);

  // Fence around the desk, with posts; the field beyond it holds the strays.
  const fence = { x: X(desk.x) - 14, y: Y(desk.y) - 14, w: S(desk.w) + 28, h: S(desk.h) + 28 };
  if (beyond.length) {
    el('rect', { x: fence.x, y: fence.y + fence.h + 14, width: fence.w, height: H - (fence.y + fence.h + 14) - 34, class: 'beyond', rx: 4 }, svg);
    el('text', { x: fence.x + 10, y: H - 44, class: 'beyond-label' }, svg).textContent = 'Beyond the fence: outside every display';
  }
  el('rect', { x: fence.x, y: fence.y, width: fence.w, height: fence.h, rx: 3, class: 'fence' }, svg);
  const postsAcross = Math.max(6, Math.round(fence.w / 70)), postsDown = Math.max(2, Math.round(fence.h / 70));
  for (let i = 0; i <= postsAcross; i++) {
    const px = fence.x + (fence.w * i) / postsAcross;
    el('line', { x1: px, y1: fence.y - 5, x2: px, y2: fence.y + 5, class: 'post' }, svg);
    el('line', { x1: px, y1: fence.y + fence.h - 5, x2: px, y2: fence.y + fence.h + 5, class: 'post' }, svg);
  }
  for (let i = 1; i < postsDown; i++) {
    const py = fence.y + (fence.h * i) / postsDown;
    el('line', { x1: fence.x - 5, y1: py, x2: fence.x + 5, y2: py, class: 'post' }, svg);
    el('line', { x1: fence.x + fence.w - 5, y1: py, x2: fence.x + fence.w + 5, y2: py, class: 'post' }, svg);
  }

  // Monitors (pens). The one a stray is driven to is dashed ochre.
  const pens = new Set(strays.map(w => w.nearestMonitorDevice));
  const monitorLayer = el('g', {}, svg);
  for (const m of monitors) {
    const b = m.bounds, wa = m.workingArea;
    el('rect', { x: X(b.x), y: Y(b.y), width: S(b.width), height: S(b.height), rx: 3, class: `mon${pens.has(m.deviceName) ? ' pen' : ''}`, 'data-device': m.deviceName }, monitorLayer);
    // The part of the screen the taskbar or app bars take.
    if (wa.height < b.height) el('rect', { x: X(b.x), y: Y(wa.bottom), width: S(b.width), height: S(b.bottom - wa.bottom), class: 'mon-taskbar' }, monitorLayer);
    if (wa.y > b.y) el('rect', { x: X(b.x), y: Y(b.y), width: S(b.width), height: S(wa.y - b.y), class: 'mon-taskbar' }, monitorLayer);
    el('text', { x: X(b.x) + 10, y: Y(b.y) + 32, class: 'mon-run' }, monitorLayer).textContent = runOf(m);
    el('text', { x: X(b.x) + 1, y: Y(b.y) - 30, class: 'mon-name' }, monitorLayer).textContent = `${monitorLabel(m)}  ·  ${identity(m)}`;
    el('text', { x: X(b.x) + 1, y: Y(b.y) - 13, class: 'mon-meta' }, monitorLayer).textContent =
      `${m.primary ? 'primary · ' : ''}${m.connection} · ${b.width} × ${b.height} @ ${hz(m.refreshRateHz)}`;
  }

  if (state.grid) {
    const gridLayer = el('g', {}, svg);
    for (const m of monitors) {
      const b = m.bounds;
      for (let gx = GRID; gx < b.width; gx += GRID)
        el('line', { x1: X(b.x + gx), y1: Y(b.y), x2: X(b.x + gx), y2: Y(b.bottom), class: `gridline${gx % (GRID * 4) === 0 ? ' major' : ''}` }, gridLayer);
      for (let gy = GRID; gy < b.height; gy += GRID)
        el('line', { x1: X(b.x), y1: Y(b.y + gy), x2: X(b.right), y2: Y(b.y + gy), class: `gridline${gy % (GRID * 4) === 0 ? ' major' : ''}` }, gridLayer);
    }
  }

  // Windows, largest first so smaller ones stay clickable on top.
  const windowLayer = el('g', {}, svg);
  const onDesk = snapshot.windows
    .filter(w => w.status !== 'off-screen' && (state.showWindows || isStray(w)) && (!w.minimized || state.showMinimized))
    .sort((a, b) => (b.bounds.width * b.bounds.height) - (a.bounds.width * a.bounds.height));
  for (const w of onDesk) drawWindow(windowLayer, w, X(w.bounds.x), Y(w.bounds.y), S(w.bounds.width), S(w.bounds.height));
  // Labels: the window on top gets its name; a name that would print over it is folded into a
  // "+N" on the top label (every window is still in the tooltip, the list and the selection bar).
  const placed = [];
  [...windowLayer.querySelectorAll('.win')].reverse().forEach(node => {
    const label = node.querySelector('.win-name');
    if (!label) return;
    const box = label.getBBox();
    const hit = placed.find(p => box.x < p.box.x + p.box.width + 4 && box.x + box.width + 4 > p.box.x && box.y < p.box.y + p.box.height && box.y + box.height > p.box.y);
    if (!hit) { placed.push({ box, label, more: 0, name: label.textContent }); return; }
    label.remove();
    hit.more += 1;
    hit.label.textContent = `${hit.name} +${hit.more}`;
  });

  // Strays beyond the fence: true size where it fits, laid out left to right, true coordinates beside.
  const strayLayer = el('g', {}, svg);
  let cursor = fence.x + 16;
  const bandTop = fence.y + fence.h + 34, bandHeight = Math.max(40, H - bandTop - 64);
  beyond.forEach(w => {
    let tw = S(w.bounds.width), th = S(w.bounds.height);
    const fit = Math.min(1, bandHeight / th, 220 / tw);
    tw *= fit; th *= fit;
    const tx = Math.min(cursor, fence.x + fence.w - tw - 8), ty = bandTop;
    drawWindow(strayLayer, w, tx, ty, tw, th, fit < 1);
    const label = el('text', { x: tx + tw + 8, y: ty + 13, class: 'win-meta' }, strayLayer);
    label.textContent = `at ${w.bounds.x}, ${w.bounds.y}`;
    w._tile = { x: tx, y: ty, w: tw, h: th };
    cursor = tx + tw + 120;
  });

  // Drive lines: every stray to its pen, the display Rescue sends it to.
  const driveLayer = el('g', {}, svg);
  strays.forEach((w, index) => {
    const pen = byDevice(w.nearestMonitorDevice) ?? monitors.find(m => m.primary);
    if (!pen) return;
    const tile = w._tile ?? { x: X(w.bounds.x), y: Y(w.bounds.y), w: S(w.bounds.width), h: S(w.bounds.height) };
    const from = { x: tile.x + tile.w / 2, y: w._tile ? tile.y : tile.y + tile.h / 2 };
    const pb = pen.bounds;
    const to = { x: X(pb.x + pb.width / 2) + index * 14, y: Y(pb.y + pb.height / 2) + index * 10 };
    const bend = Math.max(40, Math.abs(from.y - to.y) * 0.5);
    el('path', {
      d: `M${from.x},${from.y} C${from.x},${from.y - bend} ${to.x},${to.y + bend} ${to.x},${to.y}`,
      class: 'drive', 'marker-end': 'url(#driveHead)', 'data-handle': w.handle
    }, driveLayer);
    el('text', { x: to.x + 10, y: to.y - 8, class: 'drive-label' }, driveLayer).textContent = `DRIVE TO ${monitorLabel(pen)}`;
  });

  svg.addEventListener('pointerdown', onFieldPointerDown);
  placeChip();
}

function drawWindow(layer, w, x, y, width, height, shrunk = false) {
  const classes = ['win'];
  if (isStray(w)) classes.push('stray');
  else if (w.minimized) classes.push('minimized');
  else if (w.status === 'partial') classes.push('partial');
  if (w.handle === state.selected) classes.push('selected');
  const g = el('g', { class: classes.join(' '), 'data-handle': w.handle, tabindex: -1 }, layer);
  el('title', {}, g).textContent = `${w.processName}: ${w.title}\n${w.bounds.x}, ${w.bounds.y} · ${w.bounds.width} × ${w.bounds.height} · ${w.visiblePercent.toFixed(1)}% visible${shrunk ? '\n(drawn smaller to fit)' : ''}`;
  el('rect', { x: x + .5, y: y + .5, width: Math.max(2, width - 1), height: Math.max(2, height - 1), rx: 2, class: 'body' }, g);
  // A name that doesn't fit is shortened with an ellipsis; below a few readable letters it is left
  // off (the tooltip, the list and the selection bar still name the window).
  const fits = Math.floor((width - 14) / 6.8);
  if (height > 20 && fits >= 4) {
    const name = el('text', { x: x + 7, y: y + height - 8, class: 'win-name' }, g);
    name.textContent = w.processName.length <= fits ? w.processName : `${w.processName.slice(0, fits - 1)}…`;
  }
}

/* drag to place, click to select */
function onFieldPointerDown(event) {
  const node = event.target.closest('.win');
  if (!node) { select(null); return; }
  const w = state.snapshot.windows.find(item => item.handle === node.dataset.handle);
  if (!w || event.button !== 0) return;
  const svg = event.currentTarget;
  const rect = node.querySelector('rect').getBBox();
  const point = svgPoint(svg, event);
  state.drag = { handle: w.handle, window: w, node, svg, start: point, offset: { x: point.x - rect.x, y: point.y - rect.y }, size: { w: rect.width, h: rect.height }, moved: false, ghost: null, target: null };
  svg.setPointerCapture(event.pointerId);
  svg.addEventListener('pointermove', onFieldPointerMove);
  svg.addEventListener('pointerup', onFieldPointerUp, { once: true });
  svg.addEventListener('pointercancel', cancelDrag, { once: true });
}

function svgPoint(svg, event) {
  const box = svg.getBoundingClientRect();
  return { x: event.clientX - box.left, y: event.clientY - box.top };
}

function onFieldPointerMove(event) {
  const drag = state.drag;
  if (!drag) return;
  const point = svgPoint(drag.svg, event);
  if (!drag.moved && Math.hypot(point.x - drag.start.x, point.y - drag.start.y) < 5) return;
  const L = state.layout;
  if (!drag.moved) {
    drag.moved = true;
    drag.node.classList.add('dragging');
    drag.ghost = el('g', { class: 'ghost' }, drag.svg);
    // A dropped window keeps its real size, so the ghost is drawn at true scale.
    const realW = drag.window._tile ? L.S(drag.window.bounds.width) : drag.size.w;
    const realH = drag.window._tile ? L.S(drag.window.bounds.height) : drag.size.h;
    drag.size = { w: realW, h: realH };
    drag.offset = { x: Math.min(drag.offset.x, realW / 2), y: Math.min(drag.offset.y, realH / 2) };
    el('rect', { width: realW, height: realH, rx: 2 }, drag.ghost);
  }
  const physical = { x: (point.x - L.ox) / L.scale, y: (point.y - L.oy) / L.scale };
  const target = state.snapshot.monitors.find(m => physical.x >= m.bounds.x && physical.x < m.bounds.right && physical.y >= m.bounds.y && physical.y < m.bounds.bottom);
  let topLeft = { x: Math.round((point.x - drag.offset.x - L.ox) / L.scale), y: Math.round((point.y - drag.offset.y - L.oy) / L.scale) };
  if (target) {
    const wa = target.workingArea, w = drag.window.bounds;
    if (state.grid) topLeft = { x: wa.x + Math.round((topLeft.x - wa.x) / GRID) * GRID, y: wa.y + Math.round((topLeft.y - wa.y) / GRID) * GRID };
    topLeft = { x: Math.max(wa.x, Math.min(topLeft.x, wa.right - Math.min(w.width, wa.width))), y: Math.max(wa.y, Math.min(topLeft.y, wa.bottom - Math.min(w.height, wa.height))) };
  }
  drag.target = target ? { device: target.deviceName, x: topLeft.x, y: topLeft.y, monitor: target } : null;
  drag.ghost.setAttribute('transform', `translate(${target ? L.X(topLeft.x) : point.x - drag.offset.x},${target ? L.Y(topLeft.y) : point.y - drag.offset.y})`);
  drag.svg.querySelectorAll('.mon').forEach(m => m.classList.toggle('target', !!target && m.dataset.device === target.deviceName));
  const readout = $('dragReadout');
  readout.hidden = false;
  readout.style.left = `${point.x + 16}px`;
  readout.style.top = `${point.y + 14}px`;
  readout.textContent = target
    ? (drag.window.maximized || drag.window.minimized ? `→ ${monitorLabel(target)} (keeps ${drag.window.maximized ? 'maximized' : 'minimized'})` : `→ ${monitorLabel(target)} at ${topLeft.x}, ${topLeft.y}`)
    : 'Drop on a display';
}

async function onFieldPointerUp() {
  const drag = state.drag;
  if (!drag) return;
  drag.svg.removeEventListener('pointermove', onFieldPointerMove);
  $('dragReadout').hidden = true;
  if (!drag.moved) { state.drag = null; select(drag.handle); return; }
  const target = drag.target;
  state.drag = null;
  if (!target) { renderField(); return; }
  const keepsState = drag.window.maximized || drag.window.minimized;
  state.selected = drag.handle;
  await runAction('windows.move', keepsState
    ? { handle: drag.handle, deviceName: target.device }
    : { handle: drag.handle, deviceName: target.device, x: target.x, y: target.y }, `Moved to ${monitorLabel(target.monitor)}.`);
}

function cancelDrag() {
  if (!state.drag) return;
  state.drag.svg.removeEventListener('pointermove', onFieldPointerMove);
  state.drag = null;
  $('dragReadout').hidden = true;
  renderField();
}

/* ── selection: map, list and the floating bar stay in step ──────────── */

function select(handle) {
  state.selected = handle;
  if (!state.drag) renderField();
  renderFlock();
}

function moveButtons(w, small = true) {
  const size = small ? ' small' : '';
  const buttons = [];
  if (isStray(w)) {
    const pen = byDevice(w.nearestMonitorDevice);
    if (pen) buttons.push(`<button class="btn primary${size}" type="button" data-move="${esc(pen.deviceName)}">${icon('rescue')}Drive home to ${runOf(pen)}</button>`);
  }
  for (const m of state.snapshot.monitors) {
    if (m.deviceName === w.monitorDevice && !isStray(w)) continue;
    if (isStray(w) && m.deviceName === w.nearestMonitorDevice) continue;
    buttons.push(`<button class="btn${size}" type="button" data-move="${esc(m.deviceName)}">Send to ${runOf(m)}</button>`);
  }
  if (!isStray(w) && w.monitorDevice) buttons.push(`<button class="btn${size}" type="button" data-move="${esc(w.monitorDevice)}" title="Centre it on its current display">Center</button>`);
  return buttons.join('');
}

function placeChip() {
  const chip = $('selectionChip');
  const w = state.snapshot?.windows.find(item => item.handle === state.selected);
  const node = w && $('field').querySelector(`.win[data-handle="${CSS.escape(w.handle)}"] rect`);
  if (!w || !node || state.view !== 'desk') { chip.hidden = true; return; }
  const where = isStray(w) ? `stranded at ${w.bounds.x}, ${w.bounds.y}` : `${w.monitorName ? 'DISPLAY ' + w.monitorName.replace(/\D/g, '') : ''} · ${w.bounds.x}, ${w.bounds.y} · ${w.bounds.width} × ${w.bounds.height}`;
  chip.innerHTML = `<div class="who"><b>${esc(w.processName)}</b><span class="num">${esc(where)}</span></div>${moveButtons(w)}`;
  chip.hidden = false;
  const box = node.getBBox();
  const field = $('field').getBoundingClientRect(), app = $('app').getBoundingClientRect();
  const chipBox = chip.getBoundingClientRect();
  let left = field.left - app.left + box.x + 8, top = field.top - app.top + box.y + 8;
  left = Math.max(field.left - app.left + 8, Math.min(left, field.right - app.left - chipBox.width - 8));
  top = Math.max(field.top - app.top + 56, Math.min(top, field.bottom - app.top - chipBox.height - 40));
  chip.style.left = `${left}px`;
  chip.style.top = `${top}px`;
}

document.addEventListener('click', event => {
  const button = event.target.closest('[data-move]');
  if (!button) return;
  const handle = button.closest('[data-handle]')?.dataset.handle ?? state.selected;
  const monitor = byDevice(button.dataset.move);
  if (!handle || !monitor) return;
  const w = state.snapshot.windows.find(item => item.handle === handle);
  if (w && isStray(w)) playDrive([handle]);
  runAction('windows.move', { handle, deviceName: monitor.deviceName }, `Moved to ${monitorLabel(monitor)}.`);
});

function playDrive(handles) {
  if (reducedMotion.matches) return;
  $('field').querySelectorAll('.drive').forEach(path => {
    if (handles && !handles.includes(path.dataset.handle)) return;
    path.classList.remove('running');
    void path.getBoundingClientRect();
    path.classList.add('running');
  });
}

/* ── the flock: windows grouped by display ───────────────────────────── */

function renderFlock() {
  const body = $('flockBody');
  const snapshot = state.snapshot;
  if (!snapshot) return;
  const query = state.filter.trim().toLowerCase();
  const matches = w => !query || [w.processName, w.title, w.monitorName, w.nearestMonitorName, w.status].some(v => String(v).toLowerCase().includes(query));
  const windows = snapshot.windows.filter(matches);
  const parts = [];
  const strays = windows.filter(isStray);
  if (strays.length) {
    parts.push(`<div class="group-head strays"><b>Stranded</b><span class="sub">beyond the fence</span><span class="count num">${strays.length}</span></div>`);
    strays.forEach(w => parts.push(row(w)));
  }
  for (const m of snapshot.monitors) {
    const items = windows.filter(w => !isStray(w) && w.monitorDevice === m.deviceName);
    const minimizedHere = windows.filter(w => !isStray(w) && !w.monitorDevice && w.minimized && w.nearestMonitorDevice === m.deviceName);
    const all = items.concat(minimizedHere);
    parts.push(`<div class="group-head"><span class="run">${runOf(m)}</span><b>${monitorLabel(m)}</b><span class="sub">${esc(identity(m))}${m.primary ? ' · primary' : ''}</span><span class="count num">${all.length}</span></div>`);
    if (!all.length) parts.push(`<div class="empty">${query ? 'No matches on this display.' : 'Nothing open here.'}</div>`);
    all.forEach(w => parts.push(row(w)));
  }
  const unplaced = windows.filter(w => !isStray(w) && !w.monitorDevice && !(w.minimized && snapshot.monitors.some(m => m.deviceName === w.nearestMonitorDevice)));
  if (unplaced.length) {
    parts.push(`<div class="group-head"><b>Elsewhere</b><span class="count num">${unplaced.length}</span></div>`);
    unplaced.forEach(w => parts.push(row(w)));
  }
  if (!windows.length) parts.push(`<div class="empty">${snapshot.windows.length ? 'No windows match this filter.' : 'No normal app windows are open.'}</div>`);
  body.innerHTML = parts.join('');
}

function row(w) {
  const pills = [];
  if (isStray(w)) pills.push(`<span class="pill bad">${w.status === 'off-screen' ? 'off every display' : 'title bar out of reach'}</span>`);
  if (w.maximized) pills.push('<span class="pill">maximized</span>');
  if (w.minimized) pills.push('<span class="pill">minimized</span>');
  if (w.status === 'partial') pills.push(`<span class="pill warn">${w.visiblePercent.toFixed(0)}% visible</span>`);
  const where = isStray(w)
    ? `<b class="num">${w.bounds.x}, ${w.bounds.y}</b><span>nearest ${esc(w.nearestMonitorName)}</span>`
    : w.minimized
      ? `<b class="num">${w.bounds.x}, ${w.bounds.y}</b><span>restores here</span>`
      : `<b class="num">${w.bounds.x}, ${w.bounds.y}</b><span class="num">${w.bounds.width} × ${w.bounds.height}</span>`;
  const selected = w.handle === state.selected;
  const actions = selected || isStray(w) ? `<div class="row-actions">${moveButtons(w)}</div>` : '';
  return `<div class="row${selected ? ' selected' : ''}${isStray(w) ? ' stray' : ''}" data-handle="${esc(w.handle)}" role="button" tabindex="0" aria-pressed="${selected}">
    <div class="name"><span>${esc(w.processName)}</span>${pills.join('')}</div>
    <div class="where">${where}</div>
    <div class="title" title="${esc(w.title)}">${esc(w.title)}</div>${actions}</div>`;
}

$('flockBody').addEventListener('click', event => {
  if (event.target.closest('button')) return;
  const node = event.target.closest('.row');
  if (node) select(node.dataset.handle === state.selected ? null : node.dataset.handle);
});
$('flockBody').addEventListener('keydown', event => {
  if ((event.key === 'Enter' || event.key === ' ') && event.target.classList.contains('row')) {
    event.preventDefault();
    select(event.target.dataset.handle);
  }
});

/* ── health at a glance + the Health view ────────────────────────────── */

function renderVerdicts() {
  const s = state.snapshot, g = s.graphics, t = g.tuning;
  const healthy = s.monitors.filter(m => m.healthLevel === 'ok').length;
  const monitorReason = s.monitors.find(m => m.healthLevel !== 'ok')?.healthReasons?.[0];
  const firstWarn = t.recommendations.find(r => r.level === 'warn');
  const modes = g.displays.map(d => d.currentMode.split('@')[1]?.trim()).filter(Boolean).join(' · ');
  const items = [
    ['Monitors', `${healthy} of ${s.monitors.length} healthy`, healthy === s.monitors.length ? 'ok' : 'warn', monitorReason ?? 'Valid EDID, active paths, readable modes'],
    ['Graphics', g.health, g.healthLevel, g.adapters.filter(a => !a.virtual).map(a => a.name).join(', ') + (g.displayResetEvents30Days ? ` · ${g.displayResetEvents30Days} driver reset${g.displayResetEvents30Days === 1 ? '' : 's'} in 30 days` : ' · no driver resets in 30 days')],
    ['Display modes', g.optimization, g.optimizationLevel, modes || 'Current modes'],
    ['Windows checks', t.overall === 'Tuned' ? 'No action needed' : 'Worth a look', t.overallLevel, firstWarn?.title ?? 'Memory, power plan and driver evidence look fine']
  ];
  $('verdicts').innerHTML = items.map(([key, value, level, evidence]) =>
    `<button class="verdict" type="button" data-goto="health"><span class="k">${key}${icon('chevron')}</span><span class="v"><i class="${levelClass(level)}"></i>${esc(value)}</span><span class="e" title="${esc(evidence)}">${esc(evidence)}</span></button>`).join('');
}

function renderHealth() {
  const s = state.snapshot, g = s.graphics, t = g.tuning;
  // Fixed-scale registration: every panel silhouette uses one centimetre scale, so sizes compare
  // directly; a panel that doesn't report its size is drawn dashed at the median shape.
  const known = s.monitors.filter(m => m.physicalWidthCm && m.physicalHeightCm);
  const maxCm = Math.max(1, ...known.map(m => m.physicalWidthCm));
  const pxPerCm = 52 / maxCm;
  const silhouette = m => {
    const unknown = !(m.physicalWidthCm && m.physicalHeightCm);
    const wcm = unknown ? (known[0]?.physicalWidthCm ?? 53) : m.physicalWidthCm;
    const hcm = unknown ? (known[0]?.physicalHeightCm ?? 30) : m.physicalHeightCm;
    const w = Math.max(14, wcm * pxPerCm), h = Math.max(8, hcm * pxPerCm);
    const caption = unknown ? 'size ?' : `${(Math.hypot(wcm, hcm) / 2.54).toFixed(1)} in`;
    return `<figure class="silhouette${unknown ? ' unknown' : ''}" title="${unknown ? 'This panel does not report its size' : `${wcm} × ${hcm} cm, drawn to the same scale as the other panels`}">
      <svg width="56" height="34" aria-hidden="true"><rect x="${(56 - w) / 2}" y="${34 - h - 1}" width="${w.toFixed(1)}" height="${h.toFixed(1)}" rx="2"/></svg>
      <figcaption class="num">${caption}</figcaption></figure>`;
  };
  const modeFor = m => g.displays.find(d => d.displayName === m.displayName);

  $('monitorCards').innerHTML = s.monitors.map(m => {
    const mode = modeFor(m);
    const built = m.manufactureYear ? (m.manufactureWeek ? `week ${m.manufactureWeek}, ${m.manufactureYear}` : `${m.manufactureYear}`) : 'unknown';
    const caps = [...(m.capabilities ?? [])];
    if (m.advancedColorSupported) caps.push(m.advancedColorEnabled ? 'Advanced color on' : 'Advanced color available');
    const pathMax = mode && mode.maxRefreshAtCurrentResolutionHz > 0 ? hz(mode.maxRefreshAtCurrentResolutionHz) : 'unknown';
    const panelMax = mode && mode.vendorMaxRefreshAtNativeHz > 0 ? hz(mode.vendorMaxRefreshAtNativeHz) : 'not cataloged';
    return `<article class="monitor-card">
      <header>
        <span class="run">${runOf(m)}</span>${silhouette(m)}
        <div class="who"><b>${esc(identity(m))}</b><span>${monitorLabel(m)}${m.primary ? ' · primary' : ''} · ${esc(m.connection)}</span></div>
        <span class="pill ${levelClass(m.healthLevel)}">${esc(m.health)}</span>
      </header>
      <dl class="facts">
        <dt>Mode</dt><dd><span class="num nowrap">${m.bounds.width} × ${m.bounds.height} @ ${hz(m.refreshRateHz)}</span> <small class="num nowrap">preferred ${m.preferredWidth ? `${m.preferredWidth} × ${m.preferredHeight}` : 'unknown'} · ${m.bitsPerPixel}-bit${m.bitsPerColorChannel ? ` · ${m.bitsPerColorChannel} bpc` : ''}</small></dd>
        <dt>Refresh</dt><dd>${mode ? `<span class="pill ${levelClass(mode.optimizationLevel)}">${esc(mode.optimization)}</span> ` : ''}<span class="num nowrap">path max ${pathMax} · panel max ${panelMax}</span>${mode ? `<small class="block">${esc(mode.reasons.join(' '))}</small>` : ''}</dd>
        <dt>Identity</dt><dd>${esc(m.manufacturer)} <small class="num">${m.serialNumber ? `S/N ${esc(m.serialNumber)}` : 'no serial'}${m.productCode ? ` · product ${esc(m.productCode)}` : ''} · built ${built}</small></dd>
        <dt>Path</dt><dd>${m.targetAvailable ? 'Target available' : 'Target unavailable'}${m.multipleActivePaths ? ' · same panel on several paths' : ''}${m.edidValid ? ' · valid EDID' : ' · EDID missing or invalid'}</dd>
        <dt>Capabilities</dt><dd><div class="tags">${caps.length ? caps.map(c => `<span class="tag">${esc(c)}</span>`).join('') : '<small>No extra EDID flags</small>'}</div></dd>
      </dl>
      ${m.healthReasons?.length ? `<p class="reasons warn">${esc(m.healthReasons.join(' '))}</p>` : ''}
    </article>`;
  }).join('');

  $('adapterCards').innerHTML = g.adapters.length ? g.adapters.map(a => {
    const date = a.driverDate ? new Date(a.driverDate).toLocaleDateString() : 'date unknown';
    const resets = a.virtual ? '' : `<dt>Resets</dt><dd>${g.displayResetEvents30Days ? `${g.displayResetEvents30Days} recovered driver reset${g.displayResetEvents30Days === 1 ? '' : 's'} in 30 days` : 'None in 30 days'} <small>System log, Display event 4101</small></dd>`;
    return `<article class="monitor-card adapter">
      <header>
        <span class="run gpu">${a.virtual ? 'V' : 'GPU'}</span>
        <div class="who"><b>${esc(a.name)}</b><span>${a.virtual ? 'Virtual display adapter' : 'Physical adapter'} · ${esc(a.driverProvider)}</span></div>
        <span class="pill ${levelClass(a.healthLevel)}">${esc(a.health)}</span>
      </header>
      <dl class="facts">
        <dt>Driver</dt><dd><span class="num nowrap">${esc(a.driverVersion)}</span> <small class="num">${esc(date)} · latest vendor version not checked</small></dd>
        <dt>Plug and Play</dt><dd>${esc(a.status)} <small class="num">problem code ${a.problemCode}</small></dd>
        <dt>Mode</dt><dd><span class="num nowrap">${esc(a.currentMode)}</span> <small>${a.bitsPerPixel ? `${a.bitsPerPixel}-bit desktop` : 'no desktop'}</small></dd>
        ${resets}
      </dl>
      ${a.healthReasons?.length ? `<p class="reasons warn">${esc(a.healthReasons.join(' '))}</p>` : ''}
    </article>`;
  }).join('') : '<p class="note">Windows returned no graphics adapters.</p>';

  $('tuningVerdict').className = `pill ${levelClass(t.overallLevel)}`;
  $('tuningVerdict').textContent = t.overall === 'Tuned' ? 'No action needed' : 'Worth a look';
  const commit = typeof t.committedPercent === 'number' ? ` · commit ${t.committedPercent.toFixed(0)}%` : '';
  $('tuningHost').textContent = `${t.device} · ${t.windows} · ${t.powerPlan} · ${t.availableMemoryGiB.toFixed(1)} of ${t.totalMemoryGiB.toFixed(1)} GiB free${commit}`;
  $('tuningList').innerHTML = t.recommendations.map(r => `<li class="check">
    <span class="pill ${levelClass(r.level)}">${esc(r.status)}</span>
    <b>${esc(r.title)}</b>
    <p>${esc(r.evidence)}</p>
    <p class="next">${esc(r.nextStep)}</p>
    ${r.source ? `<span class="src">Source: ${esc(r.source)}</span>` : ''}
    ${r.settingsTarget ? `<button class="btn small" type="button" data-settings="${esc(r.settingsTarget)}">${icon('open')}Open settings</button>` : ''}
  </li>`).join('');

  $('browserList').innerHTML = s.browsers.browsers.map(b => {
    const running = b.status !== 'Not running';
    const tabs = b.tabCountStatus === 'ready' ? `${b.tabCount} tab${b.tabCount === 1 ? '' : 's'}` : b.tabCountStatus === 'not-running' ? '' : 'tabs not exposed';
    return `<li><b>${esc(b.name)}</b>${running
      ? `<span class="num">${[tabs, `${b.processCount} processes`, `${b.workingSetMiB.toFixed(0)} MiB working set`, `${b.privateMemoryMiB.toFixed(0)} MiB private`].filter(Boolean).join(' · ')}</span>`
      : '<span>Not running</span>'}</li>`;
  }).join('');
}

document.addEventListener('click', event => {
  const target = event.target.closest('[data-settings]');
  if (target) runAction('system.openSettings', { target: target.dataset.settings });
  if (event.target.closest('[data-goto="health"]')) selectView('health');
});

/* ── views ───────────────────────────────────────────────────────────── */

function selectView(view) {
  state.view = view;
  $('viewDesk').hidden = view !== 'desk';
  $('viewHealth').hidden = view !== 'health';
  $('viewDeskTab').setAttribute('aria-selected', String(view === 'desk'));
  $('viewHealthTab').setAttribute('aria-selected', String(view === 'health'));
  if (view === 'desk') requestAnimationFrame(() => renderField());
  else $('selectionChip').hidden = true;
}
document.querySelectorAll('.views [data-view]').forEach(tab => tab.addEventListener('click', () => selectView(tab.dataset.view)));

/* ── version pop-out (Chassis's): this app's Settings rows, Help and Diagnostics ── */

function settingsHtml(app) {
  const row = (id, key, fallback, title, note) =>
    `<div class="chassis-row"><label for="${id}">${title}<small>${note}</small></label><input id="${id}" class="chassis-switch" type="checkbox" data-key="${key}" ${(app[key] ?? fallback) ? 'checked' : ''}></div>`;
  return `<h3>Rescue</h3>
    ${row('sRescueNew', 'autoRescueNewWindows', true, 'Rescue stranded windows after launch', "Watches new top-level windows and moves only ones whose title bar can't be reached.")}
    ${row('sRescueDisplay', 'autoRescueDisplayChanges', true, 'Rescue after display changes', 'Waits for Windows to settle after a monitor change, then drives stranded windows to their nearest display.')}
    <h3>Start</h3>
    ${row('sStartHidden', 'startMinimized', false, 'Start hidden in the notification area', 'Applies the next time Screen Shepherd starts. The launch watcher, display-change rescue and Ctrl+Alt+Home run from there.')}`;
}

function bindSettings(app, hello, save) {
  for (const id of ['sRescueNew', 'sRescueDisplay', 'sStartHidden']) {
    $(id).onchange = event => {
      app[event.target.dataset.key] = event.target.checked;
      save();
      toast('Settings saved.');
      // The status bar's "Launch watch" follows the setting once the host has it.
      setTimeout(refreshState, 200);
    };
  }
}

function helpHtml() {
  return `<h3>Moving windows</h3><p>Drag a window on the map and drop it where you want it; it lands at that spot on that display, kept inside the work area. Or select it (map or list) and use Send to. With Grid on, drops snap to a 120-pixel grid. Minimized and maximized windows keep their state.</p>
    <h3>Stranded windows and drive lines</h3><p>A window is stranded when at least 64 pixels of its title bar can't be reached on any display. Stranded windows sit beyond the fence, with a drive line to their nearest display. Rescue stranded sends each one home; minimized windows are never moved automatically.</p>
    <h3>Why a powered-off monitor can still look active</h3><p>Some HDMI and DisplayPort links keep advertising the display after the panel goes dark, so Windows keeps placing windows there. Use Gather all (it asks twice) to pull everything onto a display you can see.</p>
    <h3>What health means</h3><p>Healthy means Windows reports an active, available path with a valid EDID identity, a readable mode and a model name. It doesn't test backlights, pixels, temperature or cable quality.</p>
    <h3>Windows that won't move</h3><p>Windows can refuse moves across integrity levels (elevated apps), and full-screen games, secure-desktop prompts and some custom-framed apps ignore placement requests.</p>
    <h3>From the tray</h3><p>The tray icon's menu rescues off-screen windows and moves the last app you used to the display under the mouse, without opening this window. Results show as a Windows notification.</p>`;
}

// Drawn when the Diagnostics tab opens: what each Windows part answered on the last read.
function diagHtml() {
  const s = state.snapshot;
  if (!s) return '';
  const rows = s.dependencies.items.map(d =>
    `<div class="chassis-dep"><b>${esc(d.name)}</b><span class="pill ${levelClass(d.level)}">${esc(d.status)}</span><span class="where">${esc(d.target)}</span></div>`).join('');
  return `<h3>Windows parts, as last read</h3>${rows}
    <div class="chassis-actions"><button class="chassis-ghost small" type="button" data-report="copy">Copy window and monitor report</button></div>
    <p>The report is window positions, monitor identity and health as JSON on the clipboard. No window contents, keystrokes or credentials.</p>`;
}
document.addEventListener('click', event => {
  if (event.target.closest('[data-report="copy"]')) runAction('app.copyDiagnostics');
});
/* ── shell actions ───────────────────────────────────────────────────── */

$('rescueAll').addEventListener('click', () => {
  playDrive(null);
  runAction('windows.rescueAll', {}, 'Rescue finished.');
});
$('moveActive').addEventListener('click', () => runAction('windows.moveLastExternalToCursor', {}, 'Moved the previous app here.'));
$('refreshState').addEventListener('click', refreshState);
$('wakeDisplays').addEventListener('click', () => runAction('displays.wake', {}, 'Wake signal sent.'));
for (const [id, key] of [['toolWindows', 'showWindows'], ['toolMinimized', 'showMinimized'], ['toolGrid', 'grid']]) {
  $(id).addEventListener('click', () => {
    state[key] = !state[key];
    $(id).setAttribute('aria-pressed', String(state[key]));
    renderField();
  });
}
$('windowFilter').addEventListener('input', event => { state.filter = event.target.value; renderFlock(); });

function renderGatherTargets() {
  const select = $('gatherTarget');
  const previous = select.value;
  select.innerHTML = (state.snapshot?.monitors ?? []).map(m => `<option value="${esc(m.deviceName)}">To ${monitorLabel(m)}${m.primary ? ' (primary)' : ''}</option>`).join('');
  if ([...select.options].some(o => o.value === previous)) select.value = previous;
}
$('gatherAll').addEventListener('click', async event => {
  const button = event.currentTarget;
  if (Date.now() > state.gatherArmedUntil) {
    state.gatherArmedUntil = Date.now() + 5000;
    button.classList.add('armed');
    button.lastChild.textContent = 'Confirm gather';
    toast('Click Confirm gather within five seconds to move every open app window.', 'warn');
    setTimeout(() => { if (Date.now() >= state.gatherArmedUntil) { button.classList.remove('armed'); button.lastChild.textContent = 'Gather all'; } }, 5100);
    return;
  }
  state.gatherArmedUntil = 0;
  button.classList.remove('armed');
  button.lastChild.textContent = 'Gather all';
  await runAction('windows.gatherAll', { deviceName: $('gatherTarget').value }, 'Gathered.');
});

document.addEventListener('keydown', event => {
  if (event.key === 'Escape') {
    if (chassis.tab) return;          // Chassis closes its pop-out
    if (state.selected) select(null);
  }
  if (event.key === 'F5') { event.preventDefault(); refreshState(); }
  if (event.key === '/' && !/^(INPUT|SELECT|TEXTAREA)$/.test(document.activeElement?.tagName ?? '')) {
    event.preventDefault();
    selectView('desk');
    $('windowFilter').focus();
  }
});

let resizeFrame = 0;
addEventListener('resize', () => { cancelAnimationFrame(resizeFrame); resizeFrame = requestAnimationFrame(() => { if (!state.drag) renderField(); }); });

/* ── snapshot runs (ScreenShepherd.exe --snapshot out.png [--query "view=health" | "select=1"]) ── */

// Chassis takes the picture once the page says it is ready. This page says so after the first real state is
// drawn, in the view the options ask for, and first tells the host what it is showing (saved beside the picture).
let snapshotDone = false;
function finishSnapshot() {
  if (snapshotDone || !chassis.hello?.snapshot || !state.snapshot) return;
  snapshotDone = true;
  const options = chassis.query;
  if (options.get('view') === 'health') selectView('health');
  else if (options.get('select') === '1') {
    // Prefer an ordinary window; with none open, a stray, then a maximized window.
    const windows = state.snapshot.windows;
    const bySize = (a, b) => a.bounds.width * a.bounds.height - b.bounds.width * b.bounds.height;
    const pick = windows.filter(w => !w.maximized && !w.minimized && !isStray(w)).sort(bySize)[0]
      ?? windows.find(isStray)
      ?? windows.filter(w => w.maximized).sort(bySize)[0];
    select(pick?.handle ?? null);
  }
  setTimeout(() => {
    chassis.post({ cmd: 'facts', facts: pageFacts() });
    chassis.ready();
  }, 150);
}

const pageFacts = () => ({
  versionLabel: $('chassisVersionText')?.textContent ?? null,
  gleamLoaded: !!window.GleamBadge,
  chassis: chassis.version,
  orbPreset: $('chassisVersion')?.dataset.preset ?? null,
  orbState: $('chassisVersion')?.dataset.state ?? null,
  solidPanels: document.body.classList.contains('chassis-solid'),
  view: state.view,
  popoutTab: chassis.tab,
  hotkey: $('hotkeyStatus').textContent,
  strays: Number($('strayCount').textContent),
  mapMonitors: $('field').querySelectorAll('.mon').length,
  mapWindows: $('field').querySelectorAll('.win').length,
  driveLines: $('field').querySelectorAll('.drive').length,
  selected: !$('selectionChip').hidden,
  windowRows: [...document.querySelectorAll('#flockBody .row')].map(r => `${r.querySelector('.name span')?.textContent} | ${r.querySelector('.title')?.textContent}`),
  monitorIdentities: [...document.querySelectorAll('#monitorCards .facts dd:nth-of-type(3)')].map(d => d.textContent.trim()),
  tuningFindings: [...document.querySelectorAll('#tuningList .check b')].map(b => b.textContent)
});
selectView('desk');
refreshState();
