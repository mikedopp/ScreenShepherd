/* Chassis page kit: the bridge to the host, the Gleam version button, the version pop-out (Settings, Dependencies,
   Help, Diagnostics, Credentials when the app has any), glass on/off/opacity/blur with the solid fallback, and the
   busy/done status line. Copied into wwwroot\chassis by Chassis\tools\install.py. Do not edit this copy: change Chassis, reinstall.

   const chassis = Chassis.start({
     slot: "#versionSlot",            // where the version button goes (default [data-chassis-version], else top right)
     help: "<h3>..</h3><p>..</p>",     // Help tab (string or function returning HTML)
     keys: "F5 refreshes.",            // added to Help
     settings: { html: (s, app) => "...rows...", bind: (s, app, save) => {} },  // the app's own Settings rows
     diag: () => "<h3>..</h3>",         // extra Diagnostics HTML
     onHello: (hello) => {}, onMessage: (m) => {},
     manualSnapshot: false,             // true: call chassis.ready() when the page is fully drawn
     art: "art/hero.webp",              // the app's picture: faint behind the glass, a banner atop the pop-out; Settings can turn it off
   });
   chassis.post({ cmd }); chassis.busy(t); chassis.done(t, error); chassis.log(t); chassis.save(); chassis.app (the app settings bag)
   chassis.copy(text) puts plain text on the Windows clipboard through the host (no page clipboard permission needed)
   chassis.refresh() draws the open pop-out tab again; chassis.tab is the open tab (null when closed)
*/
(function () {
  "use strict";
  const VERSION = "0.5.0";
  const host = window.chrome && window.chrome.webview;
  const post = (m) => { if (host) host.postMessage(m); };
  const esc = (s) => String(s == null ? "" : s).replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
  const logLines = [];
  const log = (t) => { logLines.push(new Date().toLocaleTimeString() + "  " + t); if (logLines.length > 120) logLines.shift(); };

  addEventListener("error", (e) => post({ cmd: "pageError", text: (e.message || "error") + " @ " + (e.filename || "").split("/").pop() + ":" + (e.lineno || 0) }));
  addEventListener("unhandledrejection", (e) => post({ cmd: "pageError", text: "unhandled promise: " + (e.reason && e.reason.message || e.reason) }));

  function blurWorks() {
    if (!(CSS.supports("backdrop-filter", "blur(1px)") || CSS.supports("-webkit-backdrop-filter", "blur(1px)"))) return false;
    const probe = document.createElement("div");
    probe.style.cssText = "position:absolute;left:-99px;width:1px;height:1px;backdrop-filter:blur(2px);-webkit-backdrop-filter:blur(2px)";
    document.body.appendChild(probe);
    const cs = getComputedStyle(probe), ok = (cs.backdropFilter || cs.webkitBackdropFilter || "none") !== "none";
    probe.remove();
    return ok;
  }

  function start(opts) {
    opts = opts || {};
    const st = { hello: null, settings: { glass: true, opacity: 0.62, blur: 22, closeToTray: true, art: true, app: {} }, forcedSolid: false, tab: "settings", diag: null };
    const art = typeof opts.art === "string" && opts.art ? opts.art : null;
    let badge = null, statusTimer = 0, snapped = false;

    // ---- DOM: haze, version button, panel, status line
    if (!document.querySelector(".chassis-haze")) {
      const h = document.createElement("div"); h.className = "chassis-haze"; h.setAttribute("aria-hidden", "true"); h.innerHTML = "<i></i><i></i><i></i>";
      document.body.prepend(h);
    }
    if (art) {
      // one CSS variable feeds the haze and the pop-out banner. Made absolute here: a relative url() in a custom property
      // resolves against the stylesheet that uses it (chassis/chassis.css), not the page. Characters that could end url("") are escaped.
      const artUrl = new URL(art, document.baseURI).href;
      document.documentElement.style.setProperty("--chassis-art", `url("${artUrl.replace(/["\\\s]/g, encodeURIComponent)}")`);
      const probe = new Image();
      probe.onerror = () => post({ cmd: "pageError", text: "app picture not found: " + art });
      probe.src = artUrl;
      document.body.classList.add("chassis-has-art");
      const pic = document.createElement("b"); pic.className = "chassis-art";
      document.querySelector(".chassis-haze").appendChild(pic);
    }
    let slot = typeof opts.slot === "string" ? document.querySelector(opts.slot) : opts.slot || document.querySelector("[data-chassis-version]");
    if (!slot) { slot = document.createElement("div"); slot.style.cssText = "position:fixed;top:14px;right:16px;z-index:45"; document.body.appendChild(slot); }
    slot.innerHTML = `<button id="chassisVersion" class="version-button gleam-version" type="button" data-preset="aurora" data-state="idle"
      aria-haspopup="dialog" aria-controls="chassisPanel" aria-expanded="false" title="Settings, dependencies, help and diagnostics">
      <span class="gleam-dot" aria-hidden="true"></span><span class="gleam-label" id="chassisVersionText">v…</span></button>`;
    const btn = document.getElementById("chassisVersion");

    const panel = document.createElement("aside");
    panel.className = "chassis-panel"; panel.id = "chassisPanel"; panel.hidden = true;
    panel.setAttribute("role", "dialog"); panel.setAttribute("aria-modal", "false"); panel.setAttribute("aria-labelledby", "chassisPanelTitle");
    if (art) panel.classList.add("chassis-has-banner");
    panel.innerHTML = `<div class="chassis-panel-head"><span class="chassis-title" id="chassisPanelTitle">App</span>
        <button class="chassis-x" id="chassisClose" type="button" aria-label="Close">&times;</button></div>
      <div class="chassis-tabs" role="tablist" id="chassisTabs"></div>
      <div class="chassis-body" id="chassisBody" role="tabpanel" tabindex="-1"></div>`;
    document.body.appendChild(panel);
    const status = document.createElement("div");
    status.className = "chassis-status"; status.setAttribute("role", "status"); status.setAttribute("aria-live", "polite");
    status.innerHTML = "<i></i><span></span>";
    document.body.appendChild(status);
    const $b = () => document.getElementById("chassisBody");

    // ---- glass
    function applyGlass() {
      const s = st.settings, solid = !s.glass || st.forcedSolid;
      document.body.classList.toggle("chassis-solid", solid);
      document.body.classList.toggle("chassis-art-off", !s.art);
      const r = document.documentElement.style;
      r.setProperty("--alpha", s.opacity);
      r.setProperty("--blur", s.blur + "px");
    }
    function save() { post({ cmd: "settings", glass: st.settings.glass, opacity: st.settings.opacity, blur: st.settings.blur, closeToTray: st.settings.closeToTray, art: st.settings.art, app: st.settings.app }); }

    // ---- tabs
    function tabs() {
      const t = [["settings", "Settings"], ["deps", "Dependencies"], ["help", "Help"], ["diag", "Diagnostics"]];
      if (st.hello && Array.isArray(st.hello.credentials)) t.push(["creds", "Credentials"]);
      return t;
    }
    function drawTabs() {
      document.getElementById("chassisTabs").innerHTML = tabs().map(([id, name]) =>
        `<button role="tab" type="button" data-tab="${id}" aria-selected="${id === st.tab}" tabindex="${id === st.tab ? 0 : -1}">${name}</button>`).join("");
    }
    document.getElementById("chassisTabs").addEventListener("click", (e) => { const t = e.target.dataset && e.target.dataset.tab; if (t) show(t); });
    document.getElementById("chassisTabs").addEventListener("keydown", (e) => {
      if (e.key !== "ArrowRight" && e.key !== "ArrowLeft") return;
      const ids = tabs().map((x) => x[0]), i = ids.indexOf(st.tab);
      show(ids[(i + (e.key === "ArrowRight" ? 1 : ids.length - 1)) % ids.length]);
      document.querySelector(`#chassisTabs [data-tab="${st.tab}"]`).focus();
    });
    function show(tab) { st.tab = tab; drawTabs(); render(); }
    function open(tab) {
      if (tab) st.tab = tab;
      panel.hidden = false; btn.setAttribute("aria-expanded", "true");
      requestAnimationFrame(() => panel.classList.add("open"));
      drawTabs(); render();
    }
    function close() {
      panel.classList.remove("open"); btn.setAttribute("aria-expanded", "false");
      setTimeout(() => { panel.hidden = true; }, matchMedia("(prefers-reduced-motion: reduce)").matches ? 0 : 260);
      btn.focus();
    }
    btn.addEventListener("click", () => (panel.hidden ? open() : close()));
    document.getElementById("chassisClose").addEventListener("click", close);
    addEventListener("keydown", (e) => { if (e.key === "Escape" && !panel.hidden) close(); });

    function render() {
      const h = st.hello || {}, s = st.settings, body = $b(), app = h.app || "App";
      if (st.tab === "settings") {
        body.innerHTML = `
          ${st.forcedSolid ? `<div class="chassis-notice">${st.forcedReason}, so ${esc(app)} uses solid surfaces.</div>` : ""}
          <h3>Glass</h3>
          <div class="chassis-row"><label for="hgGlass">Glass surfaces<small>Frosted surfaces over a soft glow. Off gives solid surfaces.</small></label><input id="hgGlass" class="chassis-switch" type="checkbox" ${s.glass ? "checked" : ""} ${st.forcedSolid ? "disabled" : ""}></div>
          <div class="chassis-row"><label for="hgOp">Opacity<small id="hgOpV">${Math.round(s.opacity * 100)}%</small></label><input id="hgOp" type="range" min="0.25" max="0.95" step="0.01" value="${s.opacity}"></div>
          <div class="chassis-row"><label for="hgBl">Blur<small id="hgBlV">${s.blur} px</small></label><input id="hgBl" type="range" min="0" max="40" step="1" value="${s.blur}"></div>
          ${art ? `<div class="chassis-row"><label for="hgArt">${esc(app)} picture<small>Shows ${esc(app)}'s picture faintly behind the glass and across the top of this panel.</small></label><input id="hgArt" class="chassis-switch" type="checkbox" ${s.art ? "checked" : ""}></div>` : ""}
          <div class="chassis-actions"><button id="hgReset" class="chassis-ghost small" type="button">Reset glass</button></div>
          <h3>Window</h3>
          <div class="chassis-row"><label for="hgTray">Close to tray<small>The close button hides ${esc(app)} to its tray icon. Turn it off to make close quit. Tray menu &rarr; Exit always quits.</small></label><input id="hgTray" class="chassis-switch" type="checkbox" ${s.closeToTray ? "checked" : ""}></div>
          ${opts.settings && opts.settings.html ? `<div id="hgApp">${opts.settings.html(s.app, h)}</div>` : ""}
          <h3>Data</h3>
          <div class="chassis-actions"><button id="hgFolder" class="chassis-ghost small" type="button">Open data folder</button></div>`;
        const g = document.getElementById.bind(document);
        g("hgGlass").onchange = (e) => { s.glass = e.target.checked; applyGlass(); save(); };
        g("hgOp").oninput = (e) => { s.opacity = +e.target.value; g("hgOpV").textContent = Math.round(s.opacity * 100) + "%"; applyGlass(); };
        g("hgOp").onchange = save;
        g("hgBl").oninput = (e) => { s.blur = +e.target.value; g("hgBlV").textContent = s.blur + " px"; applyGlass(); };
        g("hgBl").onchange = save;
        if (g("hgArt")) g("hgArt").onchange = (e) => { s.art = e.target.checked; applyGlass(); save(); };
        g("hgReset").onclick = () => { Object.assign(s, { glass: true, opacity: 0.62, blur: 22, art: true }); applyGlass(); save(); render(); };
        g("hgTray").onchange = (e) => { s.closeToTray = e.target.checked; save(); };
        g("hgFolder").onclick = () => post({ cmd: "openDataFolder" });
        if (opts.settings && opts.settings.bind) opts.settings.bind(s.app, h, save);
      } else if (st.tab === "deps") {
        const deps = h.deps || [];
        const groups = [["runtime", "Runtime"], ["package", "Packages"], ["kit", "Our kits"], ["tool", "Tools"], ["model", "Models"],
          ["service", "Services"], ["file", "Files"], ["folder", "Folders"], ["variable", "Environment variables"], ["host", "Network"]];
        const known = new Set(groups.map((g) => g[0]));
        const other = deps.filter((d) => !known.has(d.kind));
        body.innerHTML = groups.map(([k, title]) => {
          const list = deps.filter((d) => d.kind === k);
          return list.length ? `<h3>${title}</h3>` + list.map(dep).join("") : "";
        }).join("") + (other.length ? "<h3>Other</h3>" + other.map(dep).join("") : "")
          + (Array.isArray(h.credentials) ? "" : `<h3>Credentials</h3><p>None: ${esc(app)} uses no sign-ins, API keys or tokens.</p>`);
      } else if (st.tab === "help") {
        const help = typeof opts.help === "function" ? opts.help(h) : opts.help || "";
        body.innerHTML = help + `<h3>Keys</h3><p>${opts.keys ? esc(opts.keys) + " " : ""}Esc closes this panel. Arrow keys move between its tabs.</p>
          <h3>Window</h3><p>The window is a normal Windows window: snap, resize and maximise work as usual. ${s.closeToTray ? "Closing hides it to the tray; tray menu &rarr; Exit quits." : "Closing quits."} Starting ${esc(app)} again brings this window back.</p>`;
      } else if (st.tab === "creds") {
        const list = h.credentials || [];
        body.innerHTML = `<h3>Credentials</h3><p>Where each one is kept and whether it is set. Values are never shown here.</p>`
          + list.map((c) => `<div class="chassis-dep"><b>${esc(c.name)}</b><span class="chassis-pill ${c.isSet ? "" : "missing"}">${c.isSet ? "set" : "not set"}</span><span class="where">${esc(c.where)}</span>${c.note ? `<span class="note">${esc(c.note)}</span>` : ""}</div>`).join("");
      } else {
        body.innerHTML = `<h3>Versions</h3><dl class="chassis-kv" id="hdVers"><dt>Loading</dt><dd>&hellip;</dd></dl>
          <h3>Paths</h3><dl class="chassis-kv">${Object.entries(h.paths || {}).map(([k, v]) => `<dt>${esc(k)}</dt><dd>${esc(v)}</dd>`).join("")}</dl>
          ${opts.diag ? opts.diag(h) : ""}
          <div class="chassis-actions"><button id="hdCopy" class="chassis-ghost small" type="button">Copy diagnostics</button></div>
          <h3>Run log</h3><pre class="chassis-log" id="hdLog">Loading&hellip;</pre>`;
        post({ cmd: "diag" });
      }
    }
    function dep(d) {
      return `<div class="chassis-dep"><b>${esc(d.name)}</b><span class="kind">${esc(d.kind)}</span><span class="where">${esc(d.where)}</span>${d.note ? `<span class="note">${esc(d.note)}</span>` : ""}</div>`;
    }
    function renderDiag(m) {
      st.diag = m;
      if (st.tab !== "diag" || panel.hidden) return;
      const v = Object.assign({}, m.versions, { "Chassis page kit": "Chassis " + VERSION });
      document.getElementById("hdVers").innerHTML = Object.entries(v).map(([k, x]) => `<dt>${esc(k)}</dt><dd>${esc(x || "—")}</dd>`).join("");
      const lines = (m.log || []).concat(["--- page ---"], logLines);
      document.getElementById("hdLog").textContent = lines.join("\n");
      const text = Object.entries(v).map(([k, x]) => k + ": " + (x || "-")).join("\n")
        + "\nProcess: " + JSON.stringify(m.process) + "\nGlass: " + JSON.stringify({ glass: st.settings.glass, opacity: st.settings.opacity, blur: st.settings.blur, solid: document.body.classList.contains("chassis-solid") })
        + "\n\n" + lines.join("\n");
      document.getElementById("hdCopy").onclick = () => { post({ cmd: "copy", text }); document.getElementById("hdCopy").textContent = "Copied"; };
    }

    // ---- status line and orb state
    function orb(state) { try { badge && badge.setState(state); } catch (e) { log("orb: " + e.message); } }
    function busy(text) {
      clearTimeout(statusTimer);
      status.className = "chassis-status show busy"; status.lastChild.textContent = text || "Working";
      orb("thinking");
    }
    function done(text, error) {
      clearTimeout(statusTimer);
      status.className = "chassis-status show " + (error ? "error" : "ok"); status.lastChild.textContent = text || (error ? "That didn't work" : "Done");
      orb(error ? "error" : "success");
      statusTimer = setTimeout(() => { status.classList.remove("show"); orb("idle"); }, error ? 6000 : 2600);
      log((error ? "error: " : "done: ") + text);
    }

    function ready() {
      if (snapped || !st.hello || !st.hello.snapshot) return;
      snapped = true;
      setTimeout(() => post({ cmd: "snapshotReady" }), 700);
    }

    function setForced(m) {
      const noBlur = !blurWorks();
      st.forcedSolid = !!m.reducedTransparency || noBlur;
      st.forcedReason = m.highContrast ? "Windows is using a high-contrast theme" : m.reducedTransparency ? "Windows has transparency effects turned off" : "This system can't draw the blur";
    }

    // ---- host messages
    if (host) host.addEventListener("message", (e) => {
      const m = e.data || {};
      switch (m.type) {
        case "chassis.hello":
          st.hello = m;
          st.settings = Object.assign({ art: true, app: {} }, m.settings || {});
          if (!st.settings.app) st.settings.app = {};
          setForced(m);
          applyGlass();
          document.getElementById("chassisVersionText").textContent = "v" + m.version;
          document.getElementById("chassisPanelTitle").textContent = m.app + " v" + m.version;
          // a pinned preset, or (preset null or "auto") the orb seeded from hello.seed or the app name (Gleam 0.10.0+)
          const pinned = m.preset && m.preset !== "auto" ? m.preset : null;
          if (pinned) btn.dataset.preset = pinned; else delete btn.dataset.preset;
          document.title = document.title || m.app;
          try { badge = window.GleamBadge ? GleamBadge.attach(btn, { app: m.app, seed: m.seed || undefined, preset: pinned || "auto" }) : null; } catch (err) { log("badge: " + err.message); }
          if (!window.GleamBadge) log("Gleam badge not loaded");
          try { opts.onHello && opts.onHello(m); } catch (err) { post({ cmd: "pageError", text: "onHello: " + err.message }); }
          if (m.panel) open(m.panel === "creds" && !Array.isArray(m.credentials) ? "deps" : m.panel);
          if (m.exec) post({ cmd: m.exec });
          if (!opts.manualSnapshot) ready();
          break;
        case "chassis.system":
          setForced(m); applyGlass(); if (!panel.hidden && st.tab === "settings") render();
          break;
        case "chassis.diag": renderDiag(m); break;
        case "chassis.busy": busy(m.text); break;
        case "chassis.done": done(m.text, m.error); break;
        default:
          try { opts.onMessage && opts.onMessage(m); } catch (err) { post({ cmd: "pageError", text: "onMessage: " + err.message }); }
      }
    });

    const api = {
      version: VERSION,
      post, busy, done, log, ready, save, open, close, esc,
      /** Plain text onto the Windows clipboard through the host. */
      copy(text) { const t = String(text == null ? "" : text); if (!t) return false; post({ cmd: "copy", text: t }); return true; },
      /** Draws the open pop-out tab again (after the app's own data behind it changed). */
      refresh() { if (!panel.hidden) { drawTabs(); render(); } },
      get tab() { return panel.hidden ? null : st.tab; },
      get hello() { return st.hello; },
      /** The --query options (the page address's own, so they work in a plain browser too). */
      get query() { return new URLSearchParams(location.search); },
      get app() { return st.settings.app; },
      get settings() { return st.settings; },
      setOrb: orb,
    };
    // let the app finish wiring its own handlers before the host replies
    setTimeout(() => post({ cmd: "ready" }), 0);
    if (!host) log("no host: open this page inside the app");
    return api;
  }

  window.Chassis = { start, version: VERSION };
})();
