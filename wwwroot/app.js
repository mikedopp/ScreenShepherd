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
  orbTimer: 0,
  putBack: null,          // { from: Map(handle -> bounds), keys: Set, until } while a put-back plays on the map
  inputArmed: null,       // { device, code, until } after the first click on Switch input
  labeling: null          // the display key the naming popover is open for
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

// Messages Chassis doesn't handle itself: pushed state, displays coming and going, and answers to callHost.
function onHostMessage(message) {
  if (message.type === 'state' && message.ok) { renderState(message.result); return; }
  if (message.type === 'displays') { onDisplaysChanged(message); return; }
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
// The name the user gave a display, else its maker and model.
const nameOf = m => (m.label || '').trim() || identity(m) || monitorLabel(m);
const isWireless = m => m.wireless || m.kind === 'miracast' || m.kind === 'kit';
const byKey = key => state.snapshot?.monitors.find(m => m.key === key);
// Minutes as the host words them: "under a minute", "7 min", "1 h 48 m", "3 d 2 h".
function span(minutes) {
  const raw = Math.max(0, Number(minutes) || 0);
  if (raw < 1) return 'under a minute';
  const m = Math.round(raw);
  if (m < 60) return `${m} min`;
  if (m < 1440) return `${Math.floor(m / 60)} h ${String(m % 60).padStart(2, '0')} m`;
  return `${Math.floor(m / 1440)} d ${Math.floor((m % 1440) / 60)} h`;
}
const minutesSince = iso => (Date.now() - new Date(iso).getTime()) / 60000;
const clock = iso => new Date(iso).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
const plural = (n, noun) => `${n} ${noun}${n === 1 ? '' : 's'}`;
const KIND_TEXT = { wired: 'wired', kit: 'wireless HDMI kit, marked by you', dock: 'dock or USB adapter, marked by you', miracast: 'wireless (Miracast)' };
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

  const healthIssue = snapshot.monitors.some(m => m.healthLevel !== 'ok' || dropsOften(m)) || snapshot.graphics.healthLevel !== 'ok' || snapshot.graphics.optimizationLevel === 'warn';
  $('healthFlag').hidden = !healthIssue;

  // Wireless displays on and away, and drops in the last 24 hours, only when there is something to say.
  const away = snapshot.away ?? [];
  const wirelessOn = snapshot.monitors.filter(isWireless).length;
  const wirelessAway = away.filter(a => a.kind === 'miracast' || a.kind === 'kit').length;
  $('wirelessStatus').hidden = !(wirelessOn || wirelessAway);
  $('wirelessCount').textContent = [wirelessOn ? `${wirelessOn} on` : '', wirelessAway ? `${wirelessAway} away` : ''].filter(Boolean).join(', ');
  const drops = snapshot.history?.dropsToday ?? 0;
  $('dropStatus').hidden = !drops && !(snapshot.history?.events?.length);
  $('dropCount').textContent = drops;
  renderNotice();

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
  const box = list => {
    const b = { x: Math.min(...list.map(r => r.x)), y: Math.min(...list.map(r => r.y)), r: Math.max(...list.map(r => r.right)), b: Math.max(...list.map(r => r.bottom)) };
    return { ...b, w: b.r - b.x, h: b.b - b.y };
  };
  // Away pens sit where their display was, unless a display that is here now covers that spot.
  const awayPens = (snapshot.away ?? []).filter(a => !monitors.some(m => overlap(a.bounds, m.bounds) > a.bounds.width * a.bounds.height * 0.25));
  const present = box(monitors.map(m => m.bounds));
  // The scale includes the away pens, so the desk doesn't jump when a display drops.
  const desk = box(monitors.map(m => m.bounds).concat(awayPens.map(a => a.bounds)));
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
  const fence = { x: X(present.x) - 14, y: Y(present.y) - 14, w: S(present.w) + 28, h: S(present.h) + 28 };
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
    if (isWireless(m)) {
      const glyph = el('g', { transform: `translate(${X(b.x) + 30},${Y(b.y) + 14}) scale(1.05)` }, monitorLayer);
      el('title', {}, glyph).textContent = m.kind === 'kit' ? 'Wireless HDMI kit (marked by you)' : 'Wireless display (Miracast)';
      el('path', { d: 'M2 6.5a8.5 8.5 0 0 1 12 0M4.3 9a5.3 5.3 0 0 1 7.4 0M6.6 11.4a2 2 0 0 1 2.8 0', class: 'mon-wifi' }, glyph);
    }
    // Name and meta sit 42 and 25 px above the pen, clear of the fence line 14 px out.
    el('text', { x: X(b.x) + 1, y: Y(b.y) - 42, class: 'mon-name' }, monitorLayer).textContent = `${monitorLabel(m)}  ·  ${nameOf(m)}`;
    const mode = `${b.width} × ${b.height} @ ${hz(m.refreshRateHz)}`;
    el('text', { x: X(b.x) + 1, y: Y(b.y) - 25, class: 'mon-meta' }, monitorLayer).textContent =
      m.kind === 'miracast' ? `Miracast · ${mode}${m.onSince ? ` · on ${span(minutesSince(m.onSince))}` : ''}`
      : m.kind === 'kit' ? `${m.connection} via wireless kit · ${mode}`
      : m.kind === 'dock' ? `${m.connection} via dock · ${mode}`
      : `${m.primary ? 'primary · ' : ''}${m.connection} · ${mode}`;
  }

  // Away pens: where a display was, dashed, holding the spots its windows go back to when it returns.
  const awayLayer = el('g', {}, svg);
  for (const a of awayPens) {
    const r = { x: X(a.bounds.x), y: Y(a.bounds.y), w: S(a.bounds.width), h: S(a.bounds.height) };
    // Inset the side that touches the desk, so the pen reads as outside the fence.
    if (Math.abs(a.bounds.x - present.r) < 2) { r.x += 28; r.w -= 28; }
    if (Math.abs(a.bounds.right - present.x) < 2) r.w -= 28;
    if (Math.abs(a.bounds.y - present.b) < 2) { r.y += 28; r.h -= 28; }
    if (Math.abs(a.bounds.bottom - present.y) < 2) r.h -= 28;
    if (r.w < 8 || r.h < 8) continue;
    el('rect', { x: r.x, y: r.y, width: r.w, height: r.h, rx: 3, class: 'mon away' }, awayLayer);
    el('text', { x: r.x + 1, y: r.y - 42, class: 'away-name' }, awayLayer).textContent = `${a.name}  ·  away`;
    el('text', { x: r.x + 1, y: r.y - 25, class: 'away-meta' }, awayLayer).textContent =
      `${a.kind === 'miracast' ? 'Miracast' : a.connection || 'Display'} · gone ${span(minutesSince(a.since))}${a.onMinutes ? ` · went at ${clock(a.since)} after ${span(a.onMinutes)}` : ''}`;
    for (const w of a.windows) {
      const spot = clip({ x: X(w.bounds.x), y: Y(w.bounds.y), w: S(w.bounds.width), h: S(w.bounds.height) }, r);
      if (spot) drawWindow(awayLayer, { processName: w.processName, title: w.title, handle: w.handle, bounds: w.bounds, visiblePercent: 0, spot: true }, spot.x, spot.y, spot.w, spot.h);
    }
    el('text', { x: r.x + 2, y: r.y + r.h + 22, class: 'away-note-text' }, awayLayer).textContent = snapshot.settings.putBack
      ? `${plural(a.windows.length, 'window')} go${a.windows.length === 1 ? 'es' : ''} back here when it returns`
      : 'Put-back is off in Settings';
  }
  $('legendAway').hidden = !awayPens.length;

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

  // A put-back that just happened: where each window was, and a drive line to its old place (plays once).
  const pb = state.putBack;
  if (pb && Date.now() < pb.until) {
    const labels = new Set();
    snapshot.windows.forEach((w, index) => {
      const was = pb.from.get(w.handle);
      const home = monitors.find(m => m.deviceName === w.monitorDevice);
      if (!was || !home || !pb.keys.has(home.key) || was.device === w.monitorDevice) return;
      const o = was.bounds;
      el('rect', { x: X(o.x) + 2, y: Y(o.y) + 2, width: Math.max(2, S(o.width) - 4), height: Math.max(2, S(o.height) - 4), rx: 2, class: 'origin' }, driveLayer);
      const from = { x: X(o.x + o.width / 2), y: Y(o.y + o.height / 2) + index * 6 };
      const to = { x: X(w.bounds.x + w.bounds.width / 2), y: Y(w.bounds.y + w.bounds.height / 2) + index * 6 };
      el('path', { d: `M${from.x},${from.y} C${from.x + 80},${from.y - 60} ${to.x - 80},${to.y - 60} ${to.x},${to.y}`, class: `drive${reducedMotion.matches ? '' : ' running'}`, 'marker-end': 'url(#driveHead)' }, driveLayer);
      if (!labels.has(home.key)) {
        labels.add(home.key);
        el('text', { x: X(home.bounds.x) + 14, y: Y(home.bounds.bottom) + 24, class: 'drive-label' }, driveLayer).textContent = `BACK TO ${monitorLabel(home)}`;
      }
    });
  }

  svg.addEventListener('pointerdown', onFieldPointerDown);
  placeChip();
}

const overlap = (a, b) => Math.max(0, Math.min(a.right, b.right) - Math.max(a.x, b.x)) * Math.max(0, Math.min(a.bottom, b.bottom) - Math.max(a.y, b.y));
function clip(a, r) {
  const x = Math.max(a.x, r.x), y = Math.max(a.y, r.y), right = Math.min(a.x + a.w, r.x + r.w), bottom = Math.min(a.y + a.h, r.y + r.h);
  return right - x >= 4 && bottom - y >= 4 ? { x, y, w: right - x, h: bottom - y } : null;
}

function drawWindow(layer, w, x, y, width, height, shrunk = false) {
  const classes = ['win'];
  if (w.spot) classes.push('spot');
  else if (isStray(w)) classes.push('stray');
  else if (w.minimized) classes.push('minimized');
  else if (w.status === 'partial') classes.push('partial');
  if (w.handle === state.selected) classes.push('selected');
  // A spot (where a window goes back to) carries no handle: clicks and the selection belong to the real window.
  const g = el('g', { class: classes.join(' '), 'data-handle': w.spot ? undefined : w.handle, tabindex: -1 }, layer);
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
    const sub = m.label ? `${monitorLabel(m)} · ${identity(m)}` : monitorLabel(m);
    const how = m.kind === 'miracast' ? ' · wireless' : m.kind === 'kit' ? ' · wireless kit' : m.kind === 'dock' ? ' · dock' : '';
    parts.push(`<div class="group-head"><span class="run">${runOf(m)}</span><b>${esc(nameOf(m))}</b><span class="sub">${esc(sub)}${m.primary ? ' · primary' : ''}${how}</span><span class="count num">${all.length}</span></div>`);
    if (!all.length) parts.push(`<div class="empty">${query ? 'No matches on this display.' : 'Nothing open here.'}</div>`);
    all.forEach(w => parts.push(row(w)));
  }
  // Displays that went away and have windows waiting to go back.
  for (const a of snapshot.away ?? []) {
    parts.push(`<div class="group-head away"><span class="run">${icon('wifi')}</span><b>${esc(a.name)}</b><span class="sub">away ${span(minutesSince(a.since))}${a.kind === 'miracast' ? ' · Miracast' : a.kind === 'kit' ? ' · wireless kit' : ''}</span><span class="count num">0</span></div>`);
    parts.push(`<div class="empty">${snapshot.settings.putBack
      ? `Its ${plural(a.windows.length, 'window')} ${a.windows.length === 1 ? 'is' : 'are'} on other displays until it returns.`
      : 'Put-back is off, so its windows stay where Windows put them.'}</div>`);
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
  const waiting = state.snapshot.settings.putBack && (state.snapshot.away ?? []).find(a => a.windows.some(p => p.handle === w.handle));
  if (waiting) pills.push(`<span class="pill" title="Goes back to ${esc(waiting.name)} when it returns">${icon('return')}back to ${esc(waiting.name)}</span>`);
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
  const healthy = s.monitors.filter(m => m.healthLevel === 'ok' && !dropsOften(m)).length;
  const dropper = s.monitors.find(dropsOften);
  const monitorReason = s.monitors.find(m => m.healthLevel !== 'ok')?.healthReasons?.[0]
    ?? (dropper ? `${nameOf(dropper)} dropped ${plural(dropper.dropsToday, 'time')} in 24 hours${dropPattern(dropper) ? `, each after about ${span(dropPattern(dropper))}` : ''}` : undefined);
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

  // A slider being dragged or a list being chosen from keeps its card until the user lets go.
  const busy = Date.now() < (state.cardsHeldUntil ?? 0) || ($('monitorCards').contains(document.activeElement) && document.activeElement.tagName === 'SELECT');
  if (!busy) $('monitorCards').innerHTML = s.monitors.map(m => monitorCard(m, modeFor(m), silhouette)).join('');
  renderDisplayHistory();

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

/* ── one monitor's card: Link, Picture, Panel, Monitor settings ──────── */

const historyRow = key => state.snapshot?.history?.rows.find(r => r.key === key);
const dropsOften = m => (m.dropsToday ?? 0) >= 2;
const ms = iso => new Date(iso).getTime();

// How long each session ran before it dropped, from the last 24 hours. A session clipped by the start of the
// 24 hours or of the watch is left out, since its true length isn't known.
function runsBeforeDrops(row) {
  const h = state.snapshot?.history;
  if (!row || !h) return [];
  const floor = Math.max(ms(h.from), ms(h.watchSince));
  return row.drops.map(d => {
    const s = row.spans.find(x => x.end && Math.abs(ms(x.end) - ms(d)) < 90000);
    return s && ms(s.start) > floor + 1000 ? (ms(s.end) - ms(s.start)) / 60000 : null;
  }).filter(v => v !== null);
}
// How long each drop lasted: until the next session started.
function gapsAfterDrops(row) {
  if (!row) return [];
  return row.drops.map(d => row.spans.find(x => ms(x.start) >= ms(d)) ? (ms(row.spans.find(x => ms(x.start) >= ms(d)).start) - ms(d)) / 60000 : null)
    .filter(v => v !== null);
}
// The usual run time before a drop, when every drop came after about the same time on (within a quarter, at least
// half an hour): the pattern a hot adapter or a power-saving timer leaves.
function dropPattern(m) {
  const runs = runsBeforeDrops(historyRow(m.key));
  if (runs.length < 2) return 0;
  const mean = runs.reduce((a, b) => a + b, 0) / runs.length;
  return mean >= 30 && runs.every(r => Math.abs(r - mean) <= mean * 0.25) ? mean : 0;
}
const listOf = items => items.length < 2 ? items.join('') : `${items.slice(0, -1).join(', ')} and ${items[items.length - 1]}`;

// The last 24 hours as a strip: on periods, and a mark at each drop.
function ribbon(row) {
  const h = state.snapshot.history;
  const from = ms(h.from), to = ms(h.to), total = Math.max(1, to - from);
  const at = t => Math.max(0, Math.min(100, (ms(t) - from) / total * 100));
  const onMinutes = (row?.spans ?? []).reduce((sum, s) => sum + (ms(s.end ?? h.to) - ms(s.start)) / 60000, 0);
  const spans = (row?.spans ?? []).map(s => {
    const left = at(s.start), right = at(s.end ?? h.to);
    return `<i class="${s.end ? '' : 'live'}" style="left:${left.toFixed(2)}%;width:${Math.max(0.3, right - left).toFixed(2)}%"></i>`;
  }).join('');
  const drops = (row?.drops ?? []).map(d => `<b style="left:${at(d).toFixed(2)}%"></b>`).join('');
  return `<div class="ribbon" role="img" aria-label="On ${span(onMinutes)} in the last 24 hours, ${plural(row?.drops.length ?? 0, 'drop')}">${spans}${drops}</div>`;
}
function axis(parts = 4) {
  const h = state.snapshot.history, from = ms(h.from), to = ms(h.to);
  const ticks = Array.from({ length: parts + 1 }, (_, i) => i === parts ? 'now' : clock(new Date(from + (to - from) * i / parts)));
  return `<div class="axis num">${ticks.map(t => `<span>${t}</span>`).join('')}</div>`;
}

function bandwidth(m) {
  const e = m.edid ?? {}, mhz = m.signal?.pixelClockMHz ?? 0;
  if (m.kind === 'miracast') return `Compressed stream <small class="block">Miracast sends encoded video over Wi-Fi, so there's no cable pixel clock to read.</small>`;
  if (!mhz) return 'Not reported <small>Windows gave no pixel clock for this path</small>';
  const now = `<span class="num nowrap">${mhz.toFixed(1)} MHz pixel clock</span>`;
  const hdmi = /hdmi/i.test(m.connection) && e.hdmiMaxTmdsMHz > 0;
  const limit = hdmi ? e.hdmiMaxTmdsMHz : e.maxPixelClockMHz;
  if (!limit) return `${now} <small>the EDID gives no bandwidth limit to compare with</small>`;
  const pct = Math.round(mhz / limit * 100);
  if (pct > 100) return hdmi
    ? `${now} <small class="num block">above the ${limit} MHz HDMI TMDS limit in the EDID, so this link is likely using HDMI 2.1's FRL signalling, which that figure doesn't cover.</small>`
    : `${now} <small class="num block">above the ${limit} MHz in the EDID's range limits. That figure often predates the monitor's faster modes, which are listed elsewhere in its EDID.</small>`;
  return `${now} <small class="num">${hdmi ? 'HDMI TMDS limit' : 'EDID allows up to'} ${limit} MHz, so ${pct}% is in use${pct >= 90 ? `. A higher refresh or bit depth won't fit on this link.` : ''}</small>`;
}

function hdr(m) {
  const e = m.edid ?? {};
  const nits = [e.hdrMaxNits ? `peak ${e.hdrMaxNits} nits` : '', e.hdrMaxAverageNits ? `full frame ${e.hdrMaxAverageNits} nits` : '', e.hdrMinNits ? `black ${e.hdrMinNits} nits` : ''].filter(Boolean).join(' · ');
  const sdr = m.sdrWhiteNits ? ` · SDR content at ${Math.round(m.sdrWhiteNits)} nits` : '';
  if (e.hdrTransfer?.length) return `${e.hdrTransfer.join(' and ')} ready, ${m.advancedColorEnabled ? 'on' : 'off'} <small class="num block">${nits ? `${nits} (EDID HDR block)` : 'no brightness figures in the EDID'}${sdr}</small>`;
  if (m.advancedColorSupported) return `Advanced color ${m.advancedColorEnabled ? 'on' : 'available'} <small>no HDR block in the EDID${sdr}</small>`;
  return `Not supported <small>${e.extensionBlocks ? 'no HDR block in the EDID' : 'the EDID has no extension block'}</small>`;
}

function vrr(m) {
  const e = m.edid ?? {};
  if (e.hdmiVrrMaxHz) return `<span class="num nowrap">HDMI VRR ${e.hdmiVrrMinHz}–${e.hdmiVrrMaxHz} Hz</span> <small>HDMI Forum block in the EDID${e.freeSyncBlock ? ' · FreeSync block too' : ''}</small>`;
  const range = e.maxVerticalHz > 0 && e.maxVerticalHz - e.minVerticalHz >= 20;
  if (e.freeSyncBlock) return `<span class="num nowrap">Adaptive-Sync${range ? ` ${e.minVerticalHz}–${e.maxVerticalHz} Hz` : ''}</span> <small>FreeSync block in the EDID</small>`;
  if (range) return `<span class="num nowrap">Refresh range ${e.minVerticalHz}–${e.maxVerticalHz} Hz</span> <small>EDID range limits; a wide range often means Adaptive-Sync over DisplayPort</small>`;
  return 'None advertised <small>no variable refresh range in the EDID</small>';
}

function colour(m) {
  const e = m.edid ?? {}, p3 = e.dciP3CoveragePercent, srgb = e.srgbCoveragePercent;
  if (!p3 && !srgb) return 'Not reported <small>no colour primaries in the EDID</small>';
  const word = p3 >= 90 ? 'Wide gamut' : p3 >= 75 ? 'Wider than sRGB' : 'Standard gamut';
  return `${word} <small class="num">primaries cover about ${p3}% of DCI-P3 and ${srgb}% of sRGB (EDID chromaticity)</small>`;
}

function heat(m) {
  const note = m.kind === 'miracast' ? 'No TV or wireless adapter tells Windows its temperature.'
    : m.kind === 'kit' ? 'No wireless kit or projector tells Windows its temperature.'
    : m.kind === 'dock' ? 'No dock or adapter tells Windows its temperature.'
    : 'No monitor tells Windows its temperature. A colour preset in kelvin is the white point, not heat.';
  const pattern = dropPattern(m);
  return `<dt>Heat</dt><dd>Not reported <small class="block">${note}</small>${pattern
    ? `<p class="reasons warn">The drops came after about ${span(pattern)} on each time. A steady run time before each drop often means the adapter or dongle is getting hot, or a power-saving timer. Screen Shepherd can't measure heat: give the adapter air or a short extension cable and see whether the time before a drop grows.</p>` : ''}</dd>`;
}

function ddcSection(m) {
  const settings = state.snapshot.settings, d = m.ddc;
  let body;
  if (!settings.readDdc) body = `<dt>Answer</dt><dd>Not read <small class="block">Read monitor settings (DDC/CI) is off in Settings.</small></dd>`;
  else if (!d) body = `<dt>Answer</dt><dd>Reading… <small class="block">Monitors answer slowly; the first read takes a few seconds.</small></dd>`;
  else if (!d.available) body = `<dt>Answer</dt><dd>${/miracast/i.test(d.note) ? 'Not available' : 'No answer'} <small class="block">${esc(d.note)}</small></dd>`;
  else {
    const allow = m.ddcAllowed, off = allow ? '' : 'disabled';
    const id = `allow-${m.deviceName.replace(/\W/g, '')}`;
    const pct = (v, max) => max ? Math.round(v / max * 100) : v;
    const slider = (setting, label, v, max) => v == null ? '' :
      `<dt>${label}</dt><dd><input type="range" min="0" max="${max || 100}" value="${v}" aria-label="${label}" data-ddc="${setting}" data-device="${esc(m.deviceName)}" ${off}></dd><dd class="num" data-pct="${setting}">${pct(v, max)}%</dd>`;
    const inputs = d.inputs.length ? d.inputs : d.input != null ? [{ code: d.input, name: d.inputName }] : [];
    const armed = state.inputArmed?.device === m.deviceName && Date.now() < state.inputArmed.until;
    const chosen = armed ? state.inputArmed.code : d.input;
    const input = d.input == null ? '' : `<dt>Input</dt><dd class="input-row"><select aria-label="Input" data-input-for="${esc(m.deviceName)}" ${off}>${inputs.map(i =>
      `<option value="${i.code}" ${i.code === chosen ? 'selected' : ''}>${esc(i.name)}</option>`).join('')}</select>${allow && inputs.length > 1
      ? `<button class="btn small${armed ? ' danger' : ''}" type="button" data-switch-input="${esc(m.deviceName)}">${armed ? 'Confirm switch' : 'Switch'}</button>` : ''}</dd><dd><small>${allow && inputs.length > 1 ? 'this picture goes dark if you switch away' : inputs.length > 1 ? '' : 'the monitor lists no other inputs'}</small></dd>`;
    const read = d.readAt ? (minutesSince(d.readAt) < 1 ? `read ${Math.max(1, Math.round(minutesSince(d.readAt) * 60))} s ago` : `read ${span(minutesSince(d.readAt))} ago`) : '';
    body = `<div class="ddc">
      <div class="allow"><label for="${id}">Allow changes to this monitor<small>Off: Screen Shepherd only reads. On: the controls below send changes to the monitor.</small></label><input id="${id}" class="chassis-switch" type="checkbox" data-allow="${esc(m.key)}" ${allow ? 'checked' : ''}></div>
      ${slider('brightness', 'Brightness', d.brightness, d.brightnessMax)}
      ${slider('contrast', 'Contrast', d.contrast, d.contrastMax)}
      ${input}
      ${d.colorPreset ? `<dt>Colour preset</dt><dd class="num">${esc(d.colorPreset)}</dd><dd><small>white point</small></dd>` : ''}
      ${d.powerMode ? `<dt>Power mode</dt><dd>${esc(d.powerMode)}</dd><dd></dd>` : ''}
      ${d.usageHours ? `<dt>Powered on for</dt><dd class="num">${d.usageHours.toLocaleString()} hours</dd><dd><small>the monitor's own count</small></dd>` : ''}
      <dt>Protocol</dt><dd class="num">${esc([d.version || 'DDC/CI', read].filter(Boolean).join(' · '))}</dd><dd></dd>
    </div>`;
  }
  return `<h4>Monitor settings (DDC/CI)</h4>${body}`;
}

function monitorCard(m, mode, silhouette) {
  const e = m.edid ?? {};
  const built = m.manufactureYear ? (m.manufactureWeek ? `week ${m.manufactureWeek}, ${m.manufactureYear}` : `${m.manufactureYear}`) : 'unknown';
  const caps = [...(m.capabilities ?? [])];
  if (m.advancedColorSupported) caps.push(m.advancedColorEnabled ? 'Advanced color on' : 'Advanced color available');
  const pathMax = mode && mode.maxRefreshAtCurrentResolutionHz > 0 ? hz(mode.maxRefreshAtCurrentResolutionHz) : 'unknown';
  const panelMax = mode && mode.vendorMaxRefreshAtNativeHz > 0 ? hz(mode.vendorMaxRefreshAtNativeHz) : 'not cataloged';
  const row = historyRow(m.key);
  const showLink = m.kind !== 'wired' || (m.dropsToday ?? 0) > 0;
  const pill = m.healthLevel === 'ok' && dropsOften(m) ? '<span class="pill warn">Drops often</span>' : `<span class="pill ${levelClass(m.healthLevel)}">${esc(m.health)}</span>`;
  const kindText = m.kind === 'miracast' && /miracast/i.test(m.connection) ? '' : m.kind === 'wired' ? 'wired' : `<span class="kind">${esc(KIND_TEXT[m.kind] ?? m.kind)}</span>`;
  const sub = [monitorLabel(m), m.primary ? 'primary' : '', esc(m.connection), m.label ? esc(identity(m)) : '', kindText].filter(Boolean).join(' · ');
  const runs = runsBeforeDrops(row), gaps = gapsAfterDrops(row);
  const connection = m.kind === 'miracast' ? `Miracast over Wi-Fi Direct <small class="block">Started from Windows' Connect pane (Win+K).</small>`
    : m.kind === 'kit' ? `${esc(m.connection)} through a wireless kit <small class="block">Kits look like a plain cable to Windows; only the drops show they're there.</small>`
    : m.kind === 'dock' ? `${esc(m.connection)} through a dock or USB adapter`
    : `${esc(m.connection)} cable`;
  const link = !showLink ? '' : `<h4>Link</h4>
    <dt>Connection</dt><dd>${connection}</dd>
    <dt>Last 24 h</dt><dd>${ribbon(row)}${axis()}</dd>
    <dt>Session</dt><dd>${m.onSince ? `<span class="num">on ${span(minutesSince(m.onSince))}</span> <small class="num">since ${clock(m.onSince)}</small>` : '<small>On since before the watch started</small>'}</dd>
    ${m.dropsToday ? `<dt>Drops</dt><dd><span class="num">${m.dropsToday} in 24 h</span>${runs.length || gaps.length ? ` <small class="num block">${[
      runs.length ? `after ${listOf(runs.map(span))} on` : '',
      gaps.length ? `back within ${span(Math.max(...gaps))}${gaps.length > 1 ? ' each time' : ''}` : ''].filter(Boolean).join('; ')}</small>` : ''}</dd>` : ''}
    ${heat(m)}`;
  const size = m.physicalWidthCm && m.physicalHeightCm
    ? `<span class="num">${m.physicalWidthCm} × ${m.physicalHeightCm} cm · ${(Math.hypot(m.physicalWidthCm, m.physicalHeightCm) / 2.54).toFixed(1)} in</span> <small>from the EDID</small>`
    : 'Not reported <small>TVs and projectors often leave it out</small>';
  const edidText = !m.edidValid ? 'EDID missing or invalid'
    : `valid EDID${e.extensionBlocks ? ` with ${plural(e.extensionBlocks, 'extension block')}` : ''}${e.source === 'registry + WMI' ? ' (extensions read through WMI)' : ''}`;
  return `<article class="monitor-card">
    <header>
      <span class="run">${runOf(m)}</span>${silhouette(m)}
      <div class="who"><b><span>${esc(nameOf(m))}</span><button class="edit" type="button" data-label="${esc(m.key)}" title="Name this display" aria-label="Name ${monitorLabel(m)}">${icon('pencil')}</button></b><span>${sub}</span></div>
      ${pill}
    </header>
    <dl class="facts">
      ${link}
      <h4>Picture</h4>
      <dt>Mode</dt><dd><span class="num nowrap">${m.bounds.width} × ${m.bounds.height} @ ${hz(m.refreshRateHz)}</span> <small class="num nowrap">preferred ${m.preferredWidth ? `${m.preferredWidth} × ${m.preferredHeight}` : 'unknown'} · ${m.bitsPerPixel}-bit${m.bitsPerColorChannel ? ` · ${m.bitsPerColorChannel} bpc` : ''}</small></dd>
      <dt>Refresh</dt><dd>${mode ? `<span class="pill ${levelClass(mode.optimizationLevel)}">${esc(mode.optimization)}</span> ` : ''}<span class="num nowrap">path max ${pathMax} · panel max ${panelMax}</span>${mode ? `<small class="block">${esc(mode.reasons.join(' '))}</small>` : ''}</dd>
      <dt>Bandwidth</dt><dd>${bandwidth(m)}</dd>
      <dt>HDR</dt><dd>${hdr(m)}</dd>
      <dt>Variable refresh</dt><dd>${vrr(m)}</dd>
      <dt>Colour</dt><dd>${colour(m)}</dd>
      <h4>Panel</h4>
      <dt>Size</dt><dd>${size}</dd>
      <dt>Identity</dt><dd>${esc(m.manufacturer)} <small class="num">${m.serialNumber ? `S/N ${esc(m.serialNumber)}` : 'no serial'}${m.productCode ? ` · product ${esc(m.productCode)}` : ''} · built ${built}</small></dd>
      <dt>Path</dt><dd>${m.targetAvailable ? 'Target available' : 'Target unavailable'}${m.multipleActivePaths ? ' · same panel on several paths' : ''} · ${edidText}</dd>
      <dt>Capabilities</dt><dd><div class="tags">${caps.length ? caps.map(c => `<span class="tag">${esc(c)}</span>`).join('') : '<small>No extra EDID flags</small>'}</div></dd>
      ${showLink ? '' : heat(m)}
      ${ddcSection(m)}
    </dl>
    ${m.healthReasons?.length ? `<p class="reasons warn">${esc(m.healthReasons.join(' '))}</p>` : ''}
  </article>`;
}

/* ── Displays that come and go (Health, right column) ────────────────── */

function renderDisplayHistory() {
  const s = state.snapshot, h = s.history, host = $('displayHistory');
  if (!h) { host.innerHTML = ''; return; }
  const runFor = key => { const m = byKey(key); return m ? runOf(m) : '·'; };
  const rows = [...h.rows].sort((a, b) => (b.drops.length - a.drops.length) || a.name.localeCompare(b.name));
  const drops = h.dropsToday;
  host.innerHTML = `<h3>Displays that come and go${drops ? ` <span class="pill warn">${plural(drops, 'drop')} in 24 h</span>` : ''}</h3>
    <div class="head-row"><p class="note">Screen Shepherd's own watch since ${clock(h.watchSince)}${minutesSince(h.watchSince) > 1440 ? ` on ${new Date(h.watchSince).toLocaleDateString()}` : ''}. A gap under ${h.dropGapMinutes} minutes counts as a drop; a longer one counts as turned off.</p>
      ${h.events.length ? `<button class="btn small" type="button" data-copy-history>${icon('copy')}Copy history</button>` : ''}</div>
    ${rows.length ? `<div class="timeline">
      ${rows.map(r => `<div class="tl-row"><div class="who"><span class="run">${runFor(r.key)}</span><span title="${esc(r.name)}">${esc(r.name)}</span></div>${ribbon(r)}</div>`).join('')}
      <div class="tl-row"><span></span>${axis(8)}</div>
      <div class="tl-key"><span><i class="k-on"></i>On</span><span><i class="k-drop"></i>Drop</span></div>
    </div>` : ''}
    ${h.events.length ? `<div class="table-wrap" style="margin-top:12px"><table class="data compact events"><tbody>
      ${h.events.map(e => `<tr><td class="num">${clock(e.time)}</td><td><span class="run">${runFor(e.key)}</span>${esc(e.name)}</td><td>${esc(e.what)}</td><td class="wins">${esc(e.windows)}</td></tr>`).join('')}
    </tbody></table></div>` : `<p class="note">No display has come or gone since the watch started.</p>`}`;
}

function historyText() {
  const h = state.snapshot.history;
  return [`Screen Shepherd display history, ${new Date(h.from).toLocaleString()} to ${new Date(h.to).toLocaleString()}`,
    ...h.events.map(e => [new Date(e.time).toLocaleString(), e.name, e.what, e.windows].filter(Boolean).join('\t'))].join('\n');
}

/* ── naming a display ────────────────────────────────────────────────── */

function openLabeler(key, anchor) {
  const m = byKey(key);
  if (!m) return;
  state.labeling = key;
  const forced = m.wireless;   // Miracast is found on its own
  const opt = (value, title, note) => `<label class="opt"><input type="radio" name="labelKind" value="${value}" ${m.kind === value ? 'checked' : ''} ${forced || value === 'miracast' ? 'disabled' : ''}><span class="dot"></span>${title}<small>${note}</small></label>`;
  const mask = s => s.length > 4 ? `${s.slice(0, 2)}…${s.slice(-2)}` : '…';
  const where = m.serialNumber
    ? `Saved against this panel's identity (${[m.manufacturerCode, m.productCode].filter(Boolean).join(' ')}, S/N ${mask(m.serialNumber)}), so the name follows it to another port or dock.`
    : 'This panel has no serial number in its EDID, so the name is saved against its model and the port it is on.';
  $('overlay').innerHTML = `<div class="labeler glass" role="dialog" aria-labelledby="labelerTitle">
    <h4 id="labelerTitle">Name ${monitorLabel(m)}</h4>
    <input type="text" id="labelName" maxlength="40" value="${esc(m.label)}" placeholder="${esc(identity(m))}" aria-label="Name" autocomplete="off" spellcheck="false">
    <fieldset><legend>What is it?</legend>
      ${opt('wired', 'Wired monitor', 'A cable straight from the PC.')}
      ${opt('kit', 'Wireless HDMI kit', 'Looks like HDMI to Windows. Track its drops.')}
      ${opt('dock', 'Dock or USB adapter', 'Track its drops.')}
      ${opt('miracast', 'Wireless display (Miracast)', 'Found on its own; shown for reference.')}
    </fieldset>
    <p class="num">${esc(where)}</p>
    <div class="acts"><button class="btn small" type="button" data-labeler="cancel">Cancel</button><button class="btn small primary" type="button" data-labeler="save">Save</button></div>
  </div>`;
  const box = $('overlay').firstElementChild, app = $('app').getBoundingClientRect(), at = anchor.getBoundingClientRect();
  const left = Math.max(12, Math.min(at.left - app.left - 40, app.width - box.offsetWidth - 12));
  let top = at.bottom - app.top + 6;
  if (top + box.offsetHeight > app.height - 12) top = Math.max(12, at.top - app.top - box.offsetHeight - 6);
  box.style.left = `${left}px`;
  box.style.top = `${top}px`;
  $('labelName').focus();
  $('labelName').select();
}
function closeLabeler() {
  if (!state.labeling) return;
  const key = state.labeling;
  state.labeling = null;
  $('overlay').innerHTML = '';
  document.querySelector(`[data-label="${CSS.escape(key)}"]`)?.focus();
}
async function saveLabeler() {
  const key = state.labeling, m = byKey(key);
  if (!key) return;
  const kind = document.querySelector('input[name="labelKind"]:checked')?.value ?? '';
  const name = $('labelName').value.trim();
  closeLabeler();
  await runAction('displays.setName', { key, name, kind: m?.wireless || kind === 'miracast' ? '' : kind }, 'Saved.');
}
$('overlay').addEventListener('click', event => {
  const action = event.target.closest('[data-labeler]')?.dataset.labeler;
  if (action === 'cancel') closeLabeler();
  if (action === 'save') saveLabeler();
});
$('overlay').addEventListener('keydown', event => {
  if (event.key === 'Enter' && event.target.id === 'labelName') { event.preventDefault(); saveLabeler(); }
});
document.addEventListener('pointerdown', event => {
  if (state.labeling && !event.target.closest('.labeler') && !event.target.closest('[data-label]')) closeLabeler();
});

/* ── monitor settings over DDC/CI, displays that come and go ─────────── */

// Sliders and lists hold their card still for a moment, so a state push doesn't redraw it under the pointer.
for (const type of ['pointerdown', 'input', 'keydown']) $('monitorCards').addEventListener(type, () => { state.cardsHeldUntil = Date.now() + 1500; });
$('monitorCards').addEventListener('input', event => {
  const slider = event.target.closest('[data-ddc]');
  if (!slider) return;
  const out = slider.closest('.ddc').querySelector(`[data-pct="${slider.dataset.ddc}"]`);
  if (out) out.textContent = `${Math.round(slider.value / (Number(slider.max) || 100) * 100)}%`;
});
$('monitorCards').addEventListener('change', event => {
  const slider = event.target.closest('[data-ddc]');
  if (slider) { runAction('monitor.set', { deviceName: slider.dataset.device, setting: slider.dataset.ddc, value: Number(slider.value) }); return; }
  const allow = event.target.closest('[data-allow]');
  if (allow) runAction('displays.allowChanges', { key: allow.dataset.allow, allow: allow.checked });
});
$('monitorCards').addEventListener('click', event => {
  const edit = event.target.closest('[data-label]');
  if (edit) { openLabeler(edit.dataset.label, edit); return; }
  const button = event.target.closest('[data-switch-input]');
  if (!button) return;
  const device = button.dataset.switchInput;
  const code = Number(button.parentElement.querySelector('select').value);
  const m = byDevice(device);
  if (m?.ddc?.input === code) { toast(`${nameOf(m)} is already on that input.`, 'warn'); return; }
  // Switching input can take this picture away, so it asks twice.
  if (!(state.inputArmed?.device === device && state.inputArmed.code === code && Date.now() < state.inputArmed.until)) {
    state.inputArmed = { device, code, until: Date.now() + 5000 };
    button.textContent = 'Confirm switch';
    button.classList.add('danger');
    toast('Click Confirm switch within five seconds. This monitor shows the other input until you switch it back from its own buttons.', 'warn');
    setTimeout(() => { if (state.inputArmed?.device === device && Date.now() >= state.inputArmed.until) { state.inputArmed = null; renderHealth(); } }, 5100);
    return;
  }
  state.inputArmed = null;
  runAction('monitor.set', { deviceName: device, setting: 'input', value: code });
});
document.addEventListener('click', event => {
  if (!event.target.closest('[data-copy-history]')) return;
  navigator.clipboard.writeText(historyText()).then(() => toast('Display history copied.'), () => toast('The clipboard is not available here.', 'bad'));
});

// The notice on the map while a display is away with windows waiting for it.
function renderNotice() {
  const notice = $('notice');
  const away = [...(state.snapshot.away ?? [])].sort((a, b) => ms(b.since) - ms(a.since))[0];
  if (!away || !state.snapshot.settings.putBack || state.hiddenNotice === `${away.key}|${away.since}`) { notice.hidden = true; return; }
  // Where Windows put its windows now: the display holding most of them.
  const here = away.windows.map(p => state.snapshot.windows.find(w => w.handle === p.handle)).filter(Boolean);
  const counts = new Map();
  here.forEach(w => counts.set(w.monitorDevice, (counts.get(w.monitorDevice) ?? 0) + 1));
  const top = [...counts.entries()].sort((a, b) => b[1] - a[1])[0];
  const holder = top && byDevice(top[0]);
  const keep = holder && counts.size === 1 ? `Keep them on ${runOf(holder)}` : 'Keep them where they are';
  notice.innerHTML = `<b>${esc(away.name)} went away at ${clock(away.since)}${away.onMinutes ? `, after ${span(away.onMinutes)}` : ''}.</b>
    <p>Windows moved its ${plural(away.windows.length, 'window')}${holder ? ` to ${counts.size === 1 ? monitorLabel(holder) : 'other displays'}` : ''}. Screen Shepherd remembers where they were and puts them back when it returns.</p>
    <div class="acts">${away.kind === 'miracast' ? `<button class="btn small primary" type="button" data-connect>${icon('cast')}Connect</button>` : ''}<button class="btn small" type="button" data-forget="${esc(away.key)}">${esc(keep)}</button></div>`;
  notice.hidden = false;
}
document.addEventListener('click', event => {
  if (event.target.closest('[data-connect]')) runAction('displays.connect', {}, `Opened Windows' Connect pane.`);
  const forget = event.target.closest('[data-forget]');
  if (forget) runAction('displays.forget', { key: forget.dataset.forget }, 'Its windows stay where they are.');
});

// The host says a display came or went. A put-back plays on the map: where each window was, and a line to where it went.
function onDisplaysChanged(message) {
  for (const gone of message.gone ?? []) toast(`${gone.name} went away after ${span(gone.minutes)}.`, 'warn');
  const moved = (message.putBack ?? []).filter(p => p.moved > 0);
  for (const back of message.back ?? []) {
    const done = moved.find(p => p.key === back.key);
    toast(`${back.name} is back${back.gapMinutes != null ? ` after ${span(back.gapMinutes)}` : ''}.${done ? ` Put ${plural(done.moved, 'window')} back where ${done.moved === 1 ? 'it was' : 'they were'}.` : ''}`);
  }
  if (moved.length && state.snapshot) {
    state.putBack = {
      from: new Map(state.snapshot.windows.map(w => [w.handle, { bounds: w.bounds, device: w.monitorDevice }])),
      keys: new Set(moved.map(p => p.key)),
      until: Date.now() + 6000
    };
    setTimeout(() => { state.putBack = null; if (!state.drag) renderField(); }, 6100);
  }
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
    <h3>Displays that come and go</h3>
    ${row('sPutBack', 'putBack', true, 'Put windows back when a display returns', "Remembers where each window was on a display that goes away (wireless, kits, docks, a monitor that sleeps) and moves it back when the display returns. A window you've moved since stays where you put it.")}
    ${range('sPutBackDelay', 'putBackDelaySeconds', 3, 0, 30, 'Wait before putting back', 'Gives a display time to settle on its mode.', v => `${v} s`)}
    ${row('sNotifyDrops', 'notifyDrops', true, 'Tell me when a display drops', 'A Windows notification with how long it was on, and another when it returns.')}
    ${pick('sDropGap', 'dropGapMinutes', 10, [2, 5, 10, 15, 30, 60], 'A gap counts as a drop when shorter than', 'Longer gaps count as turned off.', v => `${v} minutes`)}
    ${pick('sLayoutDays', 'layoutDays', 7, [1, 3, 7, 14, 30], 'Remember layouts of away displays for', 'Then their windows stay wherever they are.', v => v === 1 ? '1 day' : `${v} days`)}
    <h3>Monitor settings</h3>
    ${row('sReadDdc', 'readDdc', true, 'Read monitor settings (DDC/CI)', "Asks each monitor for brightness, input and hours on, once a minute and never on the window's thread. A few monitors flicker when asked; turn this off if yours does. Changes are allowed per monitor, on its Health card.")}
    <h3>Start</h3>
    ${row('sStartHidden', 'startMinimized', false, 'Start hidden in the notification area', 'Applies the next time Screen Shepherd starts. The launch watcher, display-change rescue and Ctrl+Alt+Home run from there.')}`;
}
function range(id, key, fallback, min, max, title, note, text) {
  const value = Number(state.snapshot?.settings?.[key] ?? fallback);
  return `<div class="chassis-row"><label for="${id}">${title}<small>${note}</small></label><span class="val"><input id="${id}" type="range" min="${min}" max="${max}" value="${value}" data-key="${key}" data-number><span class="num" data-out="${id}">${text(value)}</span></span></div>`;
}
function pick(id, key, fallback, values, title, note, text) {
  const value = Number(state.snapshot?.settings?.[key] ?? fallback);
  const all = values.includes(value) ? values : [...values, value].sort((a, b) => a - b);
  return `<div class="chassis-row"><label for="${id}">${title}<small>${note}</small></label><select id="${id}" data-key="${key}" data-number>${all.map(v => `<option value="${v}" ${v === value ? 'selected' : ''}>${text(v)}</option>`).join('')}</select></div>`;
}

function bindSettings(app, hello, save) {
  $('sPutBackDelay').oninput = event => { $('hgApp').querySelector('[data-out="sPutBackDelay"]').textContent = `${event.target.value} s`; };
  for (const id of ['sRescueNew', 'sRescueDisplay', 'sStartHidden', 'sPutBack', 'sPutBackDelay', 'sNotifyDrops', 'sDropGap', 'sLayoutDays', 'sReadDdc']) {
    $(id).onchange = event => {
      app[event.target.dataset.key] = 'number' in event.target.dataset ? Number(event.target.value) : event.target.checked;
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
    <h3>Displays that come and go</h3><p>Screen Shepherd keeps its own watch of which displays are on. When one goes away (a wireless display, a wireless HDMI kit, a dock, a monitor that sleeps), Windows moves its windows elsewhere; Screen Shepherd remembers where they were, draws the display as a dashed pen on the map, and puts them back when it returns. A window you moved in the meantime stays where you put it. A gap shorter than the time in Settings counts as a drop; a longer one as turned off. The history (Health, right column) keeps 30 days on this PC.</p>
    <h3>Wireless displays and kits</h3><p>Miracast displays are found on their own and marked with a wireless sign. A wireless HDMI kit looks like an ordinary HDMI cable to Windows, so name it with the pencil on its Health card and mark what it is; its drops are tracked like any other display's. Connect opens Windows' Connect pane (Win+K) to cast to a TV or adapter. Windows' own Miracast log isn't read: it is off by default and needs admin to turn on.</p>
    <h3>Heat</h3><p>No monitor, TV, projector or adapter reports its temperature to Windows, so Heat always says Not reported. When drops keep coming after about the same time on, the card says so: that pattern often means a hot adapter or a power-saving timer.</p>
    <h3>Monitor settings (DDC/CI)</h3><p>Most desktop monitors answer brightness, contrast, input, colour preset and hours-on questions over the display cable. Wireless links, kits, docks, TVs and most projectors don't. Screen Shepherd only reads until you turn on Allow changes for a monitor on its Health card; switching input asks twice, because that monitor then shows the other source.</p>
    <h3>What health means</h3><p>Healthy means Windows reports an active, available path with a valid EDID identity, a readable mode and a model name. Bandwidth, HDR, variable refresh and colour gamut are what the monitor's EDID advertises, compared with the signal Windows is sending. None of it tests backlights, pixels or cable quality.</p>
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
$('connectDisplay').addEventListener('click', () => runAction('displays.connect', {}, `Opened Windows' Connect pane.`));
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
    if (state.labeling) { closeLabeler(); return; }
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
  awayPens: $('field').querySelectorAll('.mon.away').length,
  wirelessGlyphs: $('field').querySelectorAll('.mon-wifi').length,
  mapNames: [...$('field').querySelectorAll('.mon-name')].map(t => t.textContent),
  historyRows: document.querySelectorAll('#displayHistory .tl-row .ribbon').length,
  historyEvents: document.querySelectorAll('#displayHistory .events tr').length,
  cardGroups: [...document.querySelectorAll('#monitorCards .facts h4')].map(h => h.textContent),
  ddcAnswers: [...document.querySelectorAll('#monitorCards .monitor-card')].map(card => card.querySelector('.ddc') ? 'answered' : (card.querySelector('.facts h4:last-of-type ~ dd')?.textContent.trim() ?? '')),
  bandwidth: [...document.querySelectorAll('#monitorCards .facts')].map(dl => [...dl.querySelectorAll('dt')].find(dt => dt.textContent === 'Bandwidth')?.nextElementSibling?.textContent.trim() ?? ''),
  selected: !$('selectionChip').hidden,
  windowRows: [...document.querySelectorAll('#flockBody .row')].map(r => `${r.querySelector('.name span')?.textContent} | ${r.querySelector('.title')?.textContent}`),
  monitorIdentities: [...document.querySelectorAll('#monitorCards .facts')].map(dl => [...dl.querySelectorAll('dt')].find(dt => dt.textContent === 'Identity')?.nextElementSibling?.textContent.trim() ?? ''),
  tuningFindings: [...document.querySelectorAll('#tuningList .check b')].map(b => b.textContent)
});
selectView('desk');
refreshState();
