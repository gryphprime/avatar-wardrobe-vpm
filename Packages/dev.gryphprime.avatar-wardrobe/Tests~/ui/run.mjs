#!/usr/bin/env node
// Browser checks for Wardrobe V2: screenshots, layout rules and interaction timings at several
// window sizes. No npm dependencies: Node 22+ and a local Chrome, driven over the DevTools protocol.
//
//   node run.mjs                                   # against the mock server (started for you)
//   node run.mjs --url http://localhost:8909/v2    # against the Unity editor (read-only steps)
//   options: --out DIR  --viewports laptop,wide  --idle 15  --latency 1  --img DIR  --open
//
// Results land in results/<time>/ (index.html, report.json, screenshots). Exit code 1 when a rule fails.
import { spawn } from "node:child_process";
import { mkdirSync, writeFileSync, readFileSync, rmSync, existsSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { tmpdir } from "node:os";
import { fileURLToPath } from "node:url";
import net from "node:net";

const HERE = dirname(fileURLToPath(import.meta.url));
const opt = parseArgs(process.argv.slice(2));
const VIEWPORTS = {
  laptop: { w: 1280, h: 800 },
  desktop: { w: 1440, h: 900 },
  wide: { w: 2000, h: 1035 },
  ultra: { w: 2560, h: 1440 },
  phone: { w: 390, h: 844, mobile: true },
};
const BUDGET = { firstCardMs: 2500, itemHeadMs: 150, lookUiMs: 150, idleInstalled: 1 };
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

function parseArgs(argv) {
  const o = { idle: 15, latency: 1, viewports: Object.keys({ laptop: 1, desktop: 1, wide: 1, ultra: 1, phone: 1 }).join(",") };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (!a.startsWith("--")) continue;
    const k = a.slice(2), next = argv[i + 1];
    if (k === "open") o.open = true;
    else { o[k] = next; i++; }
  }
  o.idle = Number(o.idle); o.latency = Number(o.latency);
  return o;
}

/* ---------------- processes ---------------- */
function freePort() {
  return new Promise((res, rej) => { const s = net.createServer(); s.listen(0, "127.0.0.1", () => { const p = s.address().port; s.close(() => res(p)); }); s.on("error", rej); });
}
async function waitHttp(url, ms = 15000) {
  const end = Date.now() + ms;
  while (Date.now() < end) { try { const r = await fetch(url); if (r.ok) return; } catch {} await sleep(150); }
  throw new Error("Server did not answer: " + url);
}
async function startMock() {
  const port = await freePort();
  const img = opt.img || process.env.WARDROBE_TEST_IMG || "";
  const argv = ["-I", join(HERE, "mock_server.py"), "--web", resolve(HERE, "../../Web"), "--port", String(port), "--latency", String(opt.latency)];
  if (img) argv.push("--img", resolve(img));
  const proc = spawn("python3", argv, { stdio: ["ignore", "ignore", "inherit"] });
  const url = `http://127.0.0.1:${port}/v2`;
  await waitHttp(url);
  return { proc, url };
}
function chromePath() {
  if (process.env.CHROME) return process.env.CHROME;
  for (const p of ["/Applications/Google Chrome.app/Contents/MacOS/Google Chrome", "/Applications/Chromium.app/Contents/MacOS/Chromium",
    "/usr/bin/google-chrome", "/usr/bin/chromium", "C:/Program Files/Google/Chrome/Application/chrome.exe"]) if (existsSync(p)) return p;
  throw new Error("Chrome not found; set CHROME=/path/to/chrome");
}
async function launchChrome(profile) {
  rmSync(profile, { recursive: true, force: true }); mkdirSync(profile, { recursive: true });
  const proc = spawn(chromePath(), ["--headless=new", "--remote-debugging-port=0", "--user-data-dir=" + profile, "--hide-scrollbars",
    "--no-first-run", "--no-default-browser-check", "--disable-background-timer-throttling", "--disable-renderer-backgrounding", "about:blank"], { stdio: "ignore" });
  const portFile = join(profile, "DevToolsActivePort");
  for (let i = 0; i < 100 && !existsSync(portFile); i++) await sleep(100);
  const port = readFileSync(portFile, "utf8").split("\n")[0].trim();
  let target;
  for (let i = 0; i < 50 && !target; i++) { try { target = (await (await fetch(`http://127.0.0.1:${port}/json/list`)).json()).find((t) => t.type === "page"); } catch {} if (!target) await sleep(100); }
  const ws = new WebSocket(target.webSocketDebuggerUrl);
  await new Promise((r, j) => { ws.onopen = r; ws.onerror = j; });
  return { proc, ws };
}

/* ---------------- DevTools session ---------------- */
class Session {
  constructor(ws) {
    this.ws = ws; this.id = 0; this.pending = new Map(); this.handlers = new Map();
    ws.onmessage = (e) => {
      const m = JSON.parse(e.data);
      if (m.id && this.pending.has(m.id)) { const p = this.pending.get(m.id); this.pending.delete(m.id); m.error ? p.rej(new Error(m.error.message)) : p.res(m.result); }
      else if (m.method) (this.handlers.get(m.method) || []).forEach((h) => h(m.params));
    };
  }
  send(method, params = {}, timeout = 30000) {
    const id = ++this.id; this.ws.send(JSON.stringify({ id, method, params }));
    return new Promise((res, rej) => {
      const timer = setTimeout(() => { this.pending.delete(id); rej(new Error(method + " timed out")); }, timeout);
      this.pending.set(id, { res: (v) => { clearTimeout(timer); res(v); }, rej: (e) => { clearTimeout(timer); rej(e); } });
    });
  }
  on(method, fn) { if (!this.handlers.has(method)) this.handlers.set(method, []); this.handlers.get(method).push(fn); }
  async ev(expression, timeout = 30000) {
    const r = await Promise.race([this.send("Runtime.evaluate", { expression, awaitPromise: true, returnByValue: true }), sleep(timeout).then(() => ({ timeout: true }))]);
    if (r.timeout) throw new Error("evaluate timed out: " + expression.slice(0, 60));
    if (r.exceptionDetails) throw new Error((r.exceptionDetails.exception && r.exceptionDetails.exception.description) || r.exceptionDetails.text);
    return r.result.value;
  }
}

/* ---------------- in-page helpers ---------------- */
// Injected before the page's own scripts run.
const PAGE_HELPERS = `(() => {
  const wt = window.__wt = { marks: {}, gridCleared: 0 };
  wt.at = (fn, timeout = 8000) => new Promise((res) => {
    const t0 = performance.now();
    (function loop() { let ok = false; try { ok = fn(); } catch (_) {} if (ok) return res(performance.now()); if (performance.now() - t0 > timeout) return res(-1); requestAnimationFrame(loop); })();
  });
  wt.visible = (el) => { if (!el) return false; const r = el.getBoundingClientRect(); if (!r.width || !r.height) return false; const s = getComputedStyle(el); return s.visibility !== "hidden" && s.display !== "none"; };
  new MutationObserver(() => {
    const grid = document.getElementById("grid");
    if (!grid) return;
    if (!wt.marks.firstCard && grid.querySelector(".card")) wt.marks.firstCard = performance.now();
    // previews.js decodes off-document and inserts a finished <img>, so insertion is the paint moment.
    if (!wt.marks.firstThumb && grid.querySelector(".well img")) wt.marks.firstThumb = performance.now();
    if (wt.watchGrid && grid.children.length === 0) wt.gridCleared++;
  }).observe(document, { childList: true, subtree: true });
  // Every red toast is recorded: queued work should never produce one on a healthy run.
  wt.errToasts = [];
  new MutationObserver((list) => {
    for (const m of list) for (const n of m.addedNodes) if (n.nodeType === 1 && n.classList.contains("toast") && n.classList.contains("err")) wt.errToasts.push(n.textContent);
  }).observe(document, { childList: true, subtree: true });
})();`;

// Layout rules, evaluated in the page. Returns a list of { rule, message, detail }.
function audit() {
  const out = [], vw = innerWidth, vh = innerHeight, desktop = vw > 760;
  const add = (rule, message, detail = "") => out.push({ rule, message, detail });
  const describe = (el) => { if (!el) return ""; let s = el.tagName.toLowerCase(); if (el.type && el.tagName === "INPUT") s += "[" + el.type + "]"; if (el.id) s += "#" + el.id; else if (el.parentElement) s = describe.parent(el.parentElement) + " > " + s; if (el.classList.length) s += "." + [...el.classList].slice(0, 2).join("."); const t = (el.textContent || "").trim().slice(0, 40); return t ? s + ' "' + t + '"' : s; };
  describe.parent = (p) => p.tagName.toLowerCase() + (p.id ? "#" + p.id : p.classList.length ? "." + [...p.classList].slice(0, 2).join(".") : "");
  const vis = (el) => window.__wt.visible(el);
  const doc = document.documentElement;
  if (doc.scrollWidth > vw + 1) add("page-hscroll", `The page scrolls sideways (${doc.scrollWidth}px content in a ${vw}px window).`);
  const top = document.querySelector(".top"), main = document.querySelector("main"), aside = document.querySelector("aside");
  // Fixed columns must keep their CSS width whatever their content is.
  for (const [el, label] of [[aside, "side panel"]]) {
    if (!desktop || !el || !vis(el)) continue;
    const w = el.getBoundingClientRect().width, want = parseFloat(getComputedStyle(el).flexBasis);
    if (want && Math.abs(w - want) > 2) add("column-width", `The ${label} is ${Math.round(w)}px wide; its CSS width is ${want}px.`, describe(el));
  }
  if (desktop && main && vis(main) && main.getBoundingClientRect().width < 380) add("main-too-narrow", `The catalog is only ${Math.round(main.getBoundingClientRect().width)}px wide.`);
  // Content poking out of its column, unless something on the way clips or scrolls it.
  const clipped = (el, stop) => { for (let p = el.parentElement; p && p !== stop; p = p.parentElement) { const s = getComputedStyle(p); if (s.overflowX !== "visible") return true; } return false; };
  for (const region of [top, main, aside].filter((r) => r && vis(r))) {
    const rr = region.getBoundingClientRect(); let n = 0;
    for (const el of region.querySelectorAll("*")) {
      if (n >= 3) break;
      const r = el.getBoundingClientRect();
      if (!r.width || !r.height) continue;
      if ((r.right > rr.right + 1 || r.left < rr.left - 1) && !clipped(el, region)) { n++; add("escapes-column", `An element sticks out of its column by ${Math.round(Math.max(r.right - rr.right, rr.left - r.left))}px.`, describe(el)); }
    }
  }
  for (const dlg of document.querySelectorAll("dialog[open]")) {
    const r = dlg.getBoundingClientRect();
    if (r.left < -1 || r.top < -1 || r.right > vw + 1 || r.bottom > vh + 1) add("dialog-overflow", `A dialog is ${Math.round(r.width)}×${Math.round(r.height)}px and does not fit the ${vw}×${vh} window.`, describe(dlg));
    if (dlg.scrollWidth > dlg.clientWidth + 1) add("dialog-hscroll", "A dialog scrolls sideways.", describe(dlg));
  }
  const big = document.querySelector("#itemDlg[open] #itBig");
  if (big) { const r = big.getBoundingClientRect(); if (Math.min(r.width, r.height) < 160) add("preview-collapsed", `The item preview is squeezed to ${Math.round(r.width)}×${Math.round(r.height)}px.`); }
  // Headings and labels must be visible, not painted over by something else in the same layer.
  const layer = [...document.querySelectorAll("dialog[open]")].pop() || document;
  let covered = 0;
  for (const el of layer.querySelectorAll("h1, h2, h3, .item-title, .item-meta, .lbl, .vlabel, .card .nm, .look .nm, .opt .nm, .top-tab, #wearTitle, .worn .t")) {
    if (covered >= 3 || !vis(el)) continue;
    const r = el.getBoundingClientRect(), x = r.left + Math.min(r.width / 2, 24), y = r.top + r.height / 2;
    if (x < 0 || y < 0 || x > vw || y > vh) continue;
    // Scrolled out of its own scroller: clipped on purpose, not covered.
    let clippedAway = false;
    // Stop at the layer root: a modal dialog is not clipped by the page, and <body> scrolls the page itself.
    for (let p = el.parentElement; p && p !== document.body && !clippedAway; p = p.parentElement) {
      if (getComputedStyle(p).overflowY !== "visible" || getComputedStyle(p).overflowX !== "visible") {
        const pr = p.getBoundingClientRect(); if (x < pr.left || x > pr.right || y < pr.top || y > pr.bottom) clippedAway = true;
      }
      if (p.tagName === "DIALOG") break;
    }
    if (clippedAway) continue;
    const top = document.elementFromPoint(x, y);
    if (top && top !== el && !el.contains(top) && !top.contains(el)) {
      // Ignore things that merely scroll under a sticky header of the same scroller.
      if (getComputedStyle(top).position === "sticky" || top.closest(".toolbar, .tabs, .panel-head, .dlg-head, .fit-head, .selbar")) continue;
      // Overlays (Fit screen, popovers, menus, toasts) sit above the page on purpose.
      const overlay = top.closest(".fit, .pop, .menu, #toasts");
      if (overlay && !overlay.contains(el)) continue;
      covered++; add("covered-text", "Text is covered by another element.", describe(el) + " under " + describe(top));
    }
  }
  // Text that only appears when data is missing or duplicated.
  const text = document.body.innerText;
  for (const [re, what] of [[/✓\s*✓/, "a doubled check mark"], [/\bundefined\b/, "\"undefined\""], [/\bNaN\b/, "\"NaN\""], [/\[object /, "\"[object …]\""], [/(^|\s)null(\s|$)/, "\"null\""], [/\{\d\}/, "an unfilled {0} placeholder"]]) {
    if (re.test(text)) { const m = text.match(new RegExp(".{0,30}" + re.source + ".{0,30}")); add("bad-text", `The page shows ${what}.`, m ? m[0].replace(/\s+/g, " ").trim() : ""); }
  }
  for (const t of document.querySelectorAll("#toasts .toast")) if (vis(t) && /err/.test(t.className)) add("error-toast", "An error toast is showing.", t.textContent.trim().slice(0, 120));
  // Lines of text cut off with no ellipsis (single-line labels in rows and cards).
  let cut = 0;
  for (const el of document.querySelectorAll(".top-tab, .av-name, .opt .nm, .lname > span, .card .nm, .card .cr, .worn .t, .worn .s, .look-foot .nm, .item-title, .btn")) {
    if (cut >= 3 || !vis(el)) continue;
    const s = getComputedStyle(el);
    if (el.scrollWidth > el.clientWidth + 1 && s.textOverflow !== "ellipsis" && s.overflowX !== "visible" && s.whiteSpace === "nowrap") { cut++; add("text-cut", "A label is cut off without an ellipsis.", describe(el)); }
  }
  // Overlapping controls inside the same row.
  const controls = [...document.querySelectorAll("button, select, input:not([type=hidden]), [role=button]")].filter(vis);
  let overlaps = 0;
  for (let i = 0; i < controls.length && overlaps < 3; i++) {
    const a = controls[i].getBoundingClientRect();
    for (let j = i + 1; j < controls.length && overlaps < 3; j++) {
      const b = controls[j].getBoundingClientRect();
      if (controls[i].contains(controls[j]) || controls[j].contains(controls[i])) continue;
      const ix = Math.min(a.right, b.right) - Math.max(a.left, b.left), iy = Math.min(a.bottom, b.bottom) - Math.max(a.top, b.top);
      if (ix > 4 && iy > 4 && a.top >= 0 && b.top >= 0 && a.bottom <= vh && b.bottom <= vh) {
        // ignore stacked layers (popovers, dialogs) — only flag siblings sharing a parent row
        if (controls[i].parentElement !== controls[j].parentElement) continue;
        overlaps++; add("overlap", "Two controls overlap.", describe(controls[i]) + " / " + describe(controls[j]));
      }
    }
  }
  if (!desktop) for (const el of controls) { const r = el.getBoundingClientRect(); if (r.height < 24 && r.width < 24 && el.closest("main, aside, .top")) { add("small-target", "A control is smaller than 24px on a phone.", describe(el)); break; } }
  // Look cards are square.
  let skew = 0;
  for (const m of document.querySelectorAll("#viewLooks:not([hidden]) .mosaic")) { const r = m.getBoundingClientRect(); if (skew < 1 && r.width && Math.abs(r.width - r.height) > 2) { skew++; add("look-not-square", `A look card is ${Math.round(r.width)}×${Math.round(r.height)}px instead of square.`, describe(m)); } }
  return out;
}

/* ---------------- run ---------------- */
const stamp = new Date().toISOString().replace(/[:.]/g, "-").slice(0, 19);
const OUT = resolve(opt.out || join(HERE, "results", stamp));
mkdirSync(OUT, { recursive: true });
const live = !!opt.url;
let mock = null;
const url = opt.url || (mock = await startMock()).url;
// The profile stays on the system disk: Chrome's profile churn misbehaves on exFAT volumes.
const PROFILE = join(tmpdir(), "wardrobe-ui-" + process.pid);
const { proc: chrome, ws } = await launchChrome(PROFILE);
const page = new Session(ws);
const report = { url, live, started: new Date().toISOString(), viewports: [], idle: null, failures: 0, warnings: 0 };
const net_ = [];
const consoleErrors = [];
page.on("Network.requestWillBeSent", (p) => { const u = new URL(p.request.url); net_.push({ id: p.requestId, path: u.pathname, url: p.request.url, method: p.request.method, start: Date.now(), end: 0 }); });
const finish = (p, failed) => { const r = net_.findLast((x) => x.id === p.requestId); if (r && !r.end) { r.end = Date.now(); r.failed = failed ? (p.canceled ? "canceled" : p.errorText) : ""; } };
page.on("Network.loadingFinished", (p) => finish(p, false));
page.on("Network.loadingFailed", (p) => finish(p, true));
page.on("Runtime.exceptionThrown", (p) => consoleErrors.push("exception: " + ((p.exceptionDetails.exception && p.exceptionDetails.exception.description) || p.exceptionDetails.text).split("\n").slice(0, 2).join(" ")));
page.on("Runtime.consoleAPICalled", (p) => { if (p.type === "error") consoleErrors.push("console: " + p.args.map((a) => a.value || a.description).join(" ")); });
// Missing thumbnails are data, not page errors: the page shows "Preview unavailable" for them. Count them instead.
const missingThumbs = new Set();
page.on("Log.entryAdded", (p) => {
  const u = p.entry.url || "";
  if (p.entry.level !== "error" || /favicon/.test(u)) return;
  if (/\/api\/thumb\?/.test(u)) { missingThumbs.add(new URL(u).searchParams.get("guid")); return; }
  consoleErrors.push("log: " + p.entry.text + " " + u);
});
page.on("Page.javascriptDialogOpening", (p) => { page.send("Page.handleJavaScriptDialog", { accept: p.type !== "prompt" }); });
await page.send("Runtime.enable"); await page.send("Log.enable"); await page.send("Page.enable"); await page.send("Network.enable");
await page.send("Network.setCacheDisabled", { cacheDisabled: false });
await page.send("Page.addScriptToEvaluateOnNewDocument", { source: PAGE_HELPERS });

const inflight = () => net_.filter((r) => !r.end && r.path.startsWith("/api/") && r.path !== "/api/thumb").length;
async function settle(quiet = 500, max = 12000) {
  const end = Date.now() + max; let calm = 0;
  while (Date.now() < end) { if (inflight() === 0) { calm += 100; if (calm >= quiet) return true; } else calm = 0; await sleep(100); }
  return false;
}
const apiSince = (mark) => net_.slice(mark).filter((r) => r.path.startsWith("/api/") && r.path !== "/api/thumb");
const countBy = (rows) => rows.reduce((m, r) => ((m[r.path] = (m[r.path] || 0) + 1), m), {});

// Mock only, once per run (it changes the first look's plan): drag-to-crop, drop-to-upload, source switches.
let thumbEdited = false;
async function thumbInteractions(issue) {
  const label = "thumb-edit";
  const box = await page.ev(`(() => { const r = document.getElementById("thStage").getBoundingClientRect(); return { x: r.left + r.width / 2, y: r.top + r.height / 2 }; })()`);
  const mark = net_.length;
  await page.send("Input.dispatchMouseEvent", { type: "mousePressed", x: box.x, y: box.y, button: "left", clickCount: 1 });
  for (let i = 1; i <= 6; i++) await page.send("Input.dispatchMouseEvent", { type: "mouseMoved", x: box.x + i * 12, y: box.y, button: "left", buttons: 1 });
  await page.send("Input.dispatchMouseEvent", { type: "mouseReleased", x: box.x + 72, y: box.y, button: "left", clickCount: 1 });
  await sleep(900); await settle();
  const crops = net_.slice(mark).filter((r) => r.path === "/api/look_thumb_set" && /cropX=/.test(r.url));
  if (crops.length !== 1) issue(label, "fail", "thumb-crop-save", `Dragging the crop saved ${crops.length} time(s) instead of once.`);
  else if (!(Number(new URL(crops[0].url).searchParams.get("cropX")) < 0.42)) issue(label, "fail", "thumb-crop-direction", "Dragging right should move the window left: " + crops[0].url);
  const dropMark = net_.length;
  await page.ev(`(async () => {
    const c = document.createElement("canvas"); c.width = 320; c.height = 180; const g = c.getContext("2d"); g.fillStyle = "#4a8"; g.fillRect(0, 0, 320, 180);
    const blob = await new Promise((r) => c.toBlob(r, "image/png"));
    const dt = new DataTransfer(); dt.items.add(new File([blob], "photo.png", { type: "image/png" }));
    document.getElementById("thStage").dispatchEvent(new DragEvent("drop", { dataTransfer: dt, bubbles: true, cancelable: true }));
  })()`);
  await sleep(600); await settle();
  const uploads = net_.slice(dropMark).filter((r) => r.path === "/api/look_thumb_upload" && r.method === "POST");
  if (uploads.length !== 1) issue(label, "fail", "thumb-upload", `Dropping an image sent ${uploads.length} upload(s).`);
  const auto = await page.ev(`(async () => {
    document.querySelector('#thumbDlg [data-seg="source"] [data-v="auto"]').click();
    await __wt.at(() => document.querySelector('#thumbDlg [data-seg="source"] [data-v="auto"].on'), 5000);
    const glyph = !!document.querySelector("#thStage .th-empty") && !document.getElementById("thZoom");
    document.querySelector('#thumbDlg [data-seg="source"] [data-v=""]').click();
    await __wt.at(() => document.getElementById("thEditDef"), 5000);
    return { glyph, inherit: !!document.getElementById("thEditDef") };
  })()`);
  if (!auto.glyph) issue(label, "fail", "thumb-auto", "Switching to Auto still shows the image crop.");
  if (!auto.inherit) issue(label, "fail", "thumb-default", "Switching to Default does not show the default's plan.");
  await settle();
}

async function runViewport(name) {
  const vp = VIEWPORTS[name];
  if (!vp) throw new Error("Unknown viewport " + name);
  const result = { name, size: `${vp.w}×${vp.h}`, steps: [], perf: {}, issues: [] };
  let shotIndex = 0;
  const issue = (step, severity, rule, message, detail = "") => result.issues.push({ step, severity, rule, message, detail });
  const check = async (step) => {
    await sleep(250);
    const file = `${name}-${String(++shotIndex).padStart(2, "0")}-${step}.png`;
    const shot = await page.send("Page.captureScreenshot", { format: "png" });
    writeFileSync(join(OUT, file), Buffer.from(shot.data, "base64"));
    const found = await page.ev(`(${audit.toString()})()`);
    found.forEach((f) => issue(step, "fail", f.rule, f.message, f.detail));
    while (consoleErrors.length) issue(step, "fail", "console-error", consoleErrors.shift());
    const toasts = await page.ev(`__wt.errToasts.splice(0)`);
    toasts.forEach((t) => issue(step, live ? "warn" : "fail", "error-toast", `A red toast appeared: "${t}"`));
    result.steps.push({ step, file });
  };
  const step = async (label, fn) => { try { await fn(); } catch (e) { issue(label, "fail", "step-failed", e.message.split("\n")[0]); } };
  // Checking looks writes Unity's "include in upload" setting: put the person's own picks back afterwards.
  let originalPicks = null;
  const restorePicks = async () => {
    if (!originalPicks) return;
    // Everything checked (the default) goes back in one write through the header checkbox.
    await page.ev(`(() => { const want = new Set(${JSON.stringify(originalPicks)});
      const rows = [...document.querySelectorAll("#looksRows tr")].filter((r) => !r.querySelector("input").hidden);
      const all = document.getElementById("looksAllBox");
      if (rows.length && rows.every((r) => want.has(r.querySelector(".lname").textContent))) { all.checked = true; all.dispatchEvent(new Event("change", { bubbles: true })); return; }
      rows.forEach((r) => { const b = r.querySelector("input"); const on = want.has(r.querySelector(".lname").textContent); if (b.checked !== on) { b.checked = on; b.dispatchEvent(new Event("change", { bubbles: true })); } }); })()`);
  };

  await page.send("Emulation.setDeviceMetricsOverride", { width: vp.w, height: vp.h, deviceScaleFactor: 1, mobile: !!vp.mobile });
  const bootMark = net_.length;
  await page.send("Page.navigate", { url });
  await step("boot", async () => {
    const ok = await page.ev(`__wt.at(() => document.querySelector("#grid .card") || (document.getElementById("gridEmpty") && !document.getElementById("gridEmpty").hidden && !document.querySelector("#gridEmpty .spin")), 20000)`, 25000);
    await settle();
    const marks = await page.ev(`__wt.marks`);
    // Where thumbnail time goes: when the first preview request left, and when its bytes arrived.
    const thumbs = await page.ev(`performance.getEntriesByType("resource").filter((e) => e.name.includes("/api/thumb")).map((e) => [Math.round(e.startTime), Math.round(e.responseEnd)]).sort((a, b) => a[0] - b[0]).slice(0, 12)`);
    if (thumbs.length) Object.assign(result.perf, { thumbFirstRequestMs: thumbs[0][0], thumbFirstResponseMs: Math.min(...thumbs.map((t) => t[1])), thumbStarts: thumbs.map((t) => t[0]).join(" ") });
    result.perf.firstCardMs = marks.firstCard ? Math.round(marks.firstCard) : null;
    result.perf.firstThumbMs = marks.firstThumb ? Math.round(marks.firstThumb) : null;
    result.perf.bootRequests = countBy(apiSince(bootMark));
    if (ok < 0) issue("boot", "fail", "boot", "The catalog never appeared.");
    if (result.perf.firstCardMs > BUDGET.firstCardMs) issue("boot", live ? "warn" : "fail", "slow-boot", `First card took ${result.perf.firstCardMs}ms (budget ${BUDGET.firstCardMs}ms).`);
    const dupes = Object.entries(result.perf.bootRequests).filter(([p, n]) => n > 1 && p !== "/api/active" && p !== "/api/families");
    if (dupes.length) issue("boot", "warn", "duplicate-requests", "Requested more than once while loading: " + dupes.map(([p, n]) => `${p} ×${n}`).join(", "));
    await check("boot");
  });

  await step("item", async () => {
    const r = await page.ev(`(async () => {
      const cards = [...document.querySelectorAll("#grid .card")].filter((c) => !c.hidden);
      if (!cards.length) return { skip: "no cards" };
      let best = cards[0], most = 0;
      for (const c of cards) { const v = +(c.querySelector(".vc") && c.querySelector(".vc").textContent || 1); if (v > most) { most = v; best = c; } }
      best.scrollIntoView({ block: "center" });
      await new Promise((r) => setTimeout(r, 200));
      const name = best.querySelector(".nm").textContent.trim();
      const t0 = performance.now(); best.click();
      const head = await __wt.at(() => { const t = document.querySelector("#itemDlg[open] .item-title"); return t && t.textContent.trim() === name; });
      const full = await __wt.at(() => document.querySelector("#itActs") && document.querySelector("#itActs").children.length > 0, 15000);
      return { name, variants: most, headMs: head < 0 ? -1 : Math.round(head - t0), fullMs: full < 0 ? -1 : Math.round(full - t0) };
    })()`);
    if (r.skip) return issue("item", "warn", "skipped", r.skip);
    Object.assign(result.perf, { item: r.name, itemVariants: r.variants, itemHeadMs: r.headMs, itemFullMs: r.fullMs });
    if (r.headMs < 0 || r.headMs > BUDGET.itemHeadMs) issue("item", "fail", "slow-item", `Clicking "${r.name}" took ${r.headMs < 0 ? ">8000" : r.headMs}ms to show its name (budget ${BUDGET.itemHeadMs}ms).`);
    await settle();
    await check("item");
    await page.ev(`document.getElementById("itClose") && document.getElementById("itClose").click()`);
  });

  await step("look-switch", async () => {
    const before = await page.ev(`(async () => {
      const pick = document.getElementById("lookPick");
      if (!pick || pick.disabled) return { skip: "single-look mode" };
      const gs = document.getElementById("gridScroll");
      gs.scrollTop = Math.min(500, gs.scrollHeight - gs.clientHeight);
      await new Promise((r) => setTimeout(r, 300));
      // On a phone the Wearing panel sits below the catalog.
      if (innerWidth <= 760) { pick.scrollIntoView({ block: "start" }); await new Promise((r) => setTimeout(r, 200)); }
      pick.click();
      await __wt.at(() => __wt.visible(document.getElementById("lookPop")) && document.querySelector("#lookOpts .opt"), 3000);
      const opts = [...document.querySelectorAll("#lookOpts .opt")];
      const looks = opts.filter((o) => !o.classList.contains("shared"));
      if (looks.length < 2) return { skip: "fewer than two looks" };
      const cur = opts.findIndex((o) => o.getAttribute("aria-selected") === "true");
      // Prefer a look that wears something, so the outlines have work to do.
      const next = looks.find((o) => o.getAttribute("aria-selected") !== "true" && +o.querySelector(".n").textContent > 0) || looks.find((o) => o.getAttribute("aria-selected") !== "true");
      return { cur, next: opts.indexOf(next), scroll: Math.round(gs.scrollTop) };
    })()`);
    if (before.skip) return issue("look-switch", "warn", "skipped", before.skip);
    await settle(300);
    await check("look-picker");
    const mark = net_.length;
    const r = await page.ev(`(async () => {
      const target = document.querySelectorAll("#lookOpts .opt")[${before.next}], name = target.querySelector(".nm").textContent;
      __wt.gridCleared = 0; __wt.watchGrid = true;
      const t0 = performance.now(); target.click();
      const ui = await __wt.at(() => document.getElementById("lookPop").hidden && document.getElementById("wearTitle").textContent.includes(name));
      return { name, uiMs: ui < 0 ? -1 : Math.round(ui - t0) };
    })()`);
    await settle();
    const after = await page.ev(`(() => {
      __wt.watchGrid = false;
      const gs = document.getElementById("gridScroll");
      return { scroll: Math.round(gs.scrollTop), cleared: __wt.gridCleared, worn: document.querySelectorAll("#wearList .worn").length, outlined: document.querySelectorAll("#grid .card.cur").length };
    })()`);
    const reqs = apiSince(mark), catalog = reqs.filter((x) => x.path === "/api/families");
    Object.assign(result.perf, { lookUiMs: r.uiMs, lookRequests: countBy(reqs), lookFamiliesRequests: catalog.length, lookGridCleared: after.cleared > 0, lookOutlined: after.outlined });
    if (r.uiMs < 0 || r.uiMs > BUDGET.lookUiMs) issue("look-switch", "fail", "slow-look-switch", `Choosing "${r.name}" took ${r.uiMs < 0 ? ">8000" : r.uiMs}ms to show (budget ${BUDGET.lookUiMs}ms).`);
    if (after.cleared) issue("look-switch", "fail", "catalog-reloaded", "Choosing a look emptied and reloaded the catalog.");
    if (catalog.length) issue("look-switch", "fail", "catalog-refetched", `Choosing a look refetched the catalog (${catalog.length} request(s)).`);
    if (Math.abs(after.scroll - before.scroll) > 4) issue("look-switch", "fail", "look-scroll-jump", `Choosing a look moved the catalog from ${before.scroll}px to ${after.scroll}px.`);
    await check("look-switch");
    // Put the original look back (this also restores the editor's selection when running live).
    await page.ev(`(async () => {
      document.getElementById("lookPick").click();
      await __wt.at(() => document.querySelector("#lookOpts .opt"), 3000);
      const opt = document.querySelectorAll("#lookOpts .opt")[${before.cur < 0 ? 1 : before.cur}]; if (opt) opt.click();
    })()`);
    await settle();
  });

  await step("fit", async () => {
    const r = await page.ev(`(async () => {
      const worn = document.querySelector("#wearList .worn");
      if (!worn) return { skip: "nothing worn in this look" };
      worn.click();
      if (await __wt.at(() => document.getElementById("itFit"), 15000) < 0) return { skip: "worn item has no Fit button" };
      document.getElementById("itFit").click();
      const ok = await __wt.at(() => __wt.visible(document.getElementById("fit")) && document.querySelector("#fit .part, #fit .parts *"), 15000);
      return { ok: ok > 0 };
    })()`);
    if (r.skip) return issue("fit", "warn", "skipped", r.skip);
    await settle();
    await check("fit");
    await page.ev(`(async () => { const b = document.getElementById("fitBack"); if (b) b.click(); await __wt.at(() => !__wt.visible(document.getElementById("fit")), 3000); await __wt.at(() => document.getElementById("itemDlg").open, 3000); const c = document.getElementById("itClose"); if (c) c.click(); })()`);
  });

  await step("looks", async () => {
    const r = await page.ev(`(async () => {
      const tab = document.getElementById("looksTab");
      if (!tab || tab.hidden) return { skip: "single-look mode" };
      tab.click();
      document.getElementById("looksAsGrid").click();
      const ok = await __wt.at(() => document.querySelector("#looksGrid .look") && !document.getElementById("viewLooks").hidden, 5000);
      return { ok: ok > 0, cards: document.querySelectorAll("#looksGrid .look").length };
    })()`);
    if (r.skip) return issue("looks", "warn", "skipped", r.skip);
    if (!r.ok) return issue("looks", "fail", "looks-page", "The Looks page did not open.");
    await settle();
    await check("looks-grid");
    const l = await page.ev(`(async () => {
      document.getElementById("looksAsList").click();
      const ok = await __wt.at(() => document.querySelector("#looksRows tr") && document.getElementById("looksGrid").hidden, 3000);
      const rows = [...document.querySelectorAll("#looksRows tr")];
      const target = rows[1] || rows[0];
      target.querySelector(".lk-name").click();
      await new Promise((r) => setTimeout(r, 120));
      const insp = document.getElementById("lookInsp");
      return { ok: ok > 0, rows: rows.length, sel: target.classList.contains("sel"), title: (insp.querySelector(".insp-title") || {}).textContent || "",
        name: target.querySelector(".lname").textContent, original: rows.filter((r) => { const b = r.querySelector("input"); return !b.hidden && b.checked; }).map((r) => r.querySelector(".lname").textContent) };
    })()`);
    if (!l.ok) return issue("looks", "fail", "looks-list", "The list view did not show.");
    if (!l.sel || l.title.trim() !== l.name.trim()) issue("looks", "fail", "inspector", `Clicking a look does not show it in the inspector ("${l.title.trim()}" vs "${l.name.trim()}").`);
    originalPicks = l.original;
    await settle();
    await check("looks-list");
  });

  // The size slider works in both layouts and is remembered per layout.
  await step("look-size", async () => {
    const r = await page.ev(`(async () => {
      const slider = document.getElementById("lookSize");
      if (!__wt.visible(slider)) return { skip: "no size slider at this width" };
      const set = (v) => { slider.value = v; slider.dispatchEvent(new Event("input", { bubbles: true })); slider.dispatchEvent(new Event("change", { bubbles: true })); };
      const raf = () => new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(r)));
      document.getElementById("looksAsGrid").click(); await raf();
      const before = slider.value; set(slider.min); await raf();
      const small = document.querySelector("#looksGrid .look").getBoundingClientRect().width; set(slider.max); await raf();
      const big = document.querySelector("#looksGrid .look").getBoundingClientRect().width; set(before);
      document.getElementById("looksAsList").click(); await raf();
      const beforeList = slider.value; set(slider.min); await raf();
      const t1 = document.querySelector("#looksRows .thumb-s").getBoundingClientRect().width; set(slider.max); await raf();
      const t2 = document.querySelector("#looksRows .thumb-s").getBoundingClientRect().width; set(beforeList); await raf();
      return { small, big, t1, t2 };
    })()`);
    if (r.skip) return;
    if (!(r.big > r.small * 1.3)) issue("look-size", "fail", "grid-size", `The size slider does not resize look cards (${Math.round(r.small)} → ${Math.round(r.big)}px).`);
    if (!(r.t2 > r.t1 * 1.5)) issue("look-size", "fail", "list-size", `The size slider does not resize list previews (${Math.round(r.t1)} → ${Math.round(r.t2)}px).`);
  });

  await step("pick-looks", async () => {
    const r = await page.ev(`(async () => {
      if (document.getElementById("viewLooks").hidden) return { skip: "Looks page not open" };
      const all = document.getElementById("looksAllBox");
      all.checked = false; all.dispatchEvent(new Event("change", { bubbles: true }));
      const boxes = [...document.querySelectorAll("#looksRows tr input:not([hidden])")].slice(0, 2);
      if (boxes.length < 2) return { skip: "fewer than two looks" };
      boxes.forEach((b) => { b.checked = true; b.dispatchEvent(new Event("change", { bubbles: true })); });
      await new Promise((r) => setTimeout(r, 150));
      return { label: document.getElementById("selUpload").textContent.trim(), bar: __wt.visible(document.getElementById("selbar")) };
    })()`);
    if (r.skip) return issue("pick-looks", "warn", "skipped", r.skip);
    if (!r.bar) issue("pick-looks", "fail", "selection-bar", "Checking looks does not show the selection bar.");
    if (!/2/.test(r.label)) issue("pick-looks", "fail", "upload-label", `With two looks checked the upload button reads "${r.label}".`);
    await check("pick-looks");
  });

  // The checked looks are saved in Unity: after the changes land and the page reloads, the same two are checked.
  if (mock && name === names[0]) await step("picks-reload", async () => {
    const before = await page.ev(`(async () => {
      await __wt.at(() => document.getElementById("statusText").textContent.trim() === "Synced", 15000);
      localStorage.removeItem("wardrobeV2.picked");
      return [...document.querySelectorAll("#looksRows tr")].filter((r) => { const b = r.querySelector("input"); return !b.hidden && b.checked; }).map((r) => r.dataset.id || r.querySelector(".lname").textContent);
    })()`);
    await page.send("Page.navigate", { url });
    const after = await page.ev(`(async () => {
      await __wt.at(() => !document.getElementById("looksTab").hidden, 20000);
      document.getElementById("looksTab").click();
      await __wt.at(() => document.querySelector("#looksRows tr") && !document.getElementById("selbar").hidden, 10000);
      return [...document.querySelectorAll("#looksRows tr")].filter((r) => { const b = r.querySelector("input"); return !b.hidden && b.checked; }).map((r) => r.dataset.id || r.querySelector(".lname").textContent);
    })()`, 30000);
    if (JSON.stringify(before) !== JSON.stringify(after)) issue("picks-reload", "fail", "picks-saved", `Checked looks before reload (${before.join(", ")}) differ after (${after.join(", ")}).`);
    await settle();
  });

  // Mock only (it writes): platforms, an invalid avatar ID, and a menu group from the inspector.
  if (mock && name === names[0]) await step("inspector-edit", async () => {
    const r = await page.ev(`(async () => {
      const rows = [...document.querySelectorAll("#looksRows tr")];
      rows[1].querySelector(".lk-name").click();
      await __wt.at(() => document.querySelector("#lookInsp [data-plat=ios]:not([disabled])"), 5000);
      const ios = document.querySelector("#lookInsp [data-plat=ios]"); ios.checked = !ios.checked; ios.dispatchEvent(new Event("change", { bubbles: true }));
      await new Promise((r) => setTimeout(r, 50));
      const plat = rows[1].querySelector(".c-pl").textContent;
      const id = document.getElementById("inspId"); id.focus(); id.value = "avtr_nope"; id.dispatchEvent(new Event("input", { bubbles: true }));
      document.querySelector("#lookInsp [data-a=saveid]").click();
      await new Promise((r) => setTimeout(r, 50));
      const err = (document.querySelector("#lookInsp .insp-err") || {}).textContent || "";
      document.querySelector('#lookInsp [data-tab="groups"]').click();
      await __wt.at(() => document.querySelector("#lookInsp .insp-group"), 5000);
      const groups = document.querySelectorAll("#lookInsp .insp-group").length;
      document.querySelector('#lookInsp [data-tab="items"]').click();
      return { plat, err, groups, ios: ios.checked };
    })()`);
    if (r.ios !== /iOS/.test(r.plat)) issue("inspector-edit", "fail", "platforms", `Toggling iOS does not show in the list ("${r.plat}").`);
    if (!r.err) issue("inspector-edit", "fail", "avatar-id", "An invalid avatar ID is not refused next to the field.");
    if (!r.groups) issue("inspector-edit", "fail", "menu-groups", "The menu groups tab is empty.");
    await settle();
    await check("inspector-edit");
    // Put iOS back so later sizes see the same data.
    await page.ev(`(() => { const ios = document.querySelector("#lookInsp [data-plat=ios]"); if (ios) { ios.checked = !ios.checked; ios.dispatchEvent(new Event("change", { bubbles: true })); } const id = document.getElementById("inspId"); if (id) { id.value = ""; id.dispatchEvent(new Event("input", { bubbles: true })); id.blur(); } })()`);
  });

  // Copy the two checked looks to another avatar: review, then one queued Unity change.
  await step("copy-looks", async () => {
    const r = await page.ev(`(async () => {
      if (document.getElementById("viewLooks").hidden || document.getElementById("selbar").hidden) return { skip: "no looks checked" };
      document.getElementById("selCopy").click();
      const ok = await __wt.at(() => document.getElementById("copyDlg").open && (document.querySelector("#copyDlg .copy-look") || document.querySelector("#copyDlg .up-msg")), 10000);
      return { ok: ok > 0, looks: document.querySelectorAll("#copyDlg .copy-look").length, msg: (document.querySelector("#copyDlg .up-msg") || {}).textContent || "" };
    })()`);
    if (r.skip) return issue("copy-looks", "warn", "skipped", r.skip);
    if (!r.ok) return issue("copy-looks", "fail", "copy-dialog", "The copy dialog did not open.");
    if (r.msg) issue("copy-looks", live ? "warn" : "fail", "copy-review", `The copy review says "${r.msg}".`);
    else if (r.looks !== 2) issue("copy-looks", "fail", "copy-rows", `The copy review lists ${r.looks} look(s) instead of 2.`);
    await settle();
    await check("copy-looks");
    if (live || name !== names[0]) { await page.ev(`document.getElementById("copyDlg").close()`); return; }
    const done = await page.ev(`(async () => {
      document.getElementById("copyGo").click();
      const ok = await __wt.at(() => [...document.querySelectorAll(".toast.ok")].some((t) => /Copied/.test(t.textContent)), 15000);
      return { ok: ok > 0, open: document.getElementById("copyDlg").open };
    })()`, 20000);
    if (done.open) issue("copy-looks", "fail", "copy-close", "The copy dialog stays open while Unity works.");
    if (!done.ok) issue("copy-looks", "fail", "copy-done", "Copying never reported that it finished.");
  });

  await step("upload-review", async () => {
    const r = await page.ev(`(async () => {
      const go = document.getElementById("selUpload");
      if (document.getElementById("viewLooks").hidden || document.getElementById("selbar").hidden) return { skip: true };
      go.click();
      const ok = await __wt.at(() => document.getElementById("uploadDlg").open && document.querySelector("#uploadDlg .up-row, #uploadDlg .up-msg"), 10000);
      return { ok: ok > 0, rows: document.querySelectorAll("#uploadDlg .up-row").length };
    })()`);
    if (r.skip) return issue("upload-review", "warn", "skipped", "no looks checked");
    if (!r.ok) return issue("upload-review", "fail", "upload-dialog", "The upload dialog did not open.");
    if (r.rows !== 2) issue("upload-review", "fail", "upload-rows", `The upload review lists ${r.rows} look(s) instead of the 2 checked.`);
    await settle();
    await check("upload-review");
    // Thumbnail editor: a cropped image (first look), then a scene camera kept on re-upload.
    const wells = await page.ev(`document.querySelectorAll("#uploadDlg [data-thumb-look]").length`);
    if (wells !== 2) issue("upload-review", "fail", "thumb-wells", `The upload review shows ${wells} thumbnail button(s) instead of 2.`);
    for (const [index, label] of [[0, "thumb-image"], [1, "thumb-camera"]]) {
      const t = await page.ev(`(async () => {
        const b = document.querySelectorAll("#uploadDlg [data-thumb-look]")[${index}]; if (!b) return { ok: false };
        b.click();
        const ok = await __wt.at(() => document.getElementById("thumbDlg").open && document.querySelector("#thumbDlg .th-stage"), 8000);
        await new Promise((r) => setTimeout(r, 300));
        const img = document.querySelector("#thumbDlg .th-stage img.crop");
        return { ok: ok > 0, crop: !!img, ready: !!(img && img.classList.contains("ready")), source: (document.querySelector('#thumbDlg [data-seg="source"] .on') || {}).dataset?.v };
      })()`);
      if (!t.ok) { issue(label, "fail", "thumb-dialog", "The thumbnail dialog did not open."); continue; }
      if (index === 0 && !thumbEdited && !(t.crop && t.ready)) issue(label, "fail", "thumb-crop", "The cropped image is not shown.");
      if (index === 1 && t.source !== "camera") issue(label, "fail", "thumb-source", `The camera look shows source "${t.source}".`);
      await settle();
      await check(label);
      if (index === 0 && !live && !thumbEdited) { thumbEdited = true; await thumbInteractions(issue); }
      await page.ev(`document.getElementById("thumbDlg").close()`);
    }
    await page.ev(`(() => { const d = document.getElementById("uploadDlg"); if (d.open) d.close(); })()`);
    await restorePicks();
  });

  // Mock only (it uploads): one of two looks fails. Only that look is marked failed, the toast counts
  // failures instead of repeating the summary, and Retry uploads just the failed look again.
  if (mock && name === names[0]) await step("upload-result", async () => {
    const result = `async (ms) => {
      const ok = await __wt.at(() => document.getElementById("upClose"), ms);
      const rows = [...document.querySelectorAll("#uploadDlg .up-row")];
      return { ok: ok > 0, rows: rows.length, done: rows.filter((r) => r.querySelector(".st.ok-t")).length,
        failed: rows.filter((r) => r.querySelector(".st.warn-t")).map((r) => r.querySelector(".st.warn-t").title || ""),
        retry: !!document.getElementById("upRetry") };
    }`;
    const first = await page.ev(`(async () => {
      if (document.getElementById("viewLooks").hidden) return { skip: "Looks page not open" };
      const all = document.getElementById("looksAllBox"); all.checked = false; all.dispatchEvent(new Event("change", { bubbles: true }));
      const boxes = [...document.querySelectorAll("#looksRows tr input:not([hidden])")].slice(0, 2);
      if (boxes.length < 2) return { skip: "fewer than two looks" };
      boxes.forEach((b) => { b.checked = true; b.dispatchEvent(new Event("change", { bubbles: true })); });
      await new Promise((r) => setTimeout(r, 150));
      document.getElementById("selUpload").click();
      if (await __wt.at(() => document.getElementById("uploadDlg").open && document.getElementById("upConsent"), 10000) < 0) return { skip: "no upload review" };
      const consent = document.getElementById("upConsent"); consent.checked = true; consent.dispatchEvent(new Event("change", { bubbles: true }));
      await new Promise((r) => setTimeout(r, 100));
      document.getElementById("upGo").click();
      return (${result})(30000);
    })()`);
    if (first.skip) return issue("upload-result", "warn", "skipped", first.skip);
    if (!first.ok) return issue("upload-result", "fail", "upload-finish", "The upload never reached its result screen.");
    if (first.done !== 1 || first.failed.length !== 1)
      issue("upload-result", "fail", "per-look-result", `One of two looks failed, but the result shows ${first.done} done and ${first.failed.length} failed.`);
    if (first.failed.length === 1 && !/Failed to upload file/.test(first.failed[0]))
      issue("upload-result", "fail", "failure-reason", "The failed look does not show why it failed.");
    if (!first.retry) issue("upload-result", "fail", "retry", "There is no Retry button after a failed look.");
    // The expected toast is checked here, then cleared so the layout rules don't flag it.
    const toasts = await page.ev(`(() => { document.querySelectorAll("#toasts .toast").forEach((t) => t.remove()); return __wt.errToasts.splice(0); })()`);
    if (toasts.length !== 1 || !/^1 failed$/.test(toasts[0].trim()))
      issue("upload-result", "fail", "failure-toast", `Expected one "1 failed" toast, got: ${JSON.stringify(toasts)}`);
    await settle();
    await check("upload-result");
    const net0 = net_.length;
    const again = await page.ev(`(async () => {
      const b = document.getElementById("upRetry"); if (!b) return { ok: false };
      b.click();
      await __wt.at(() => !document.getElementById("upClose"), 3000);
      return (${result})(20000);
    })()`);
    const later = net_.slice(net0), sent = later.filter((r) => r.path === "/api/batch_upload_presets");
    if (sent.length !== 1 || (new URL(sent[0].url).searchParams.get("ids") || "").split("\n").length !== 1)
      issue("upload-retry", "fail", "retry-request", `Retry sent ${sent.length} upload request(s); it should re-upload only the failed look.`);
    if (later.some((r) => r.path === "/api/batch_retry")) issue("upload-retry", "fail", "retry-route", "Retry used the scene uploader's retry.");
    if (!again.ok || again.rows !== 1 || again.done !== 1)
      issue("upload-retry", "fail", "retry-result", `Retry result: ${JSON.stringify(again)}`);
    await settle();
    await check("upload-retry");
    await page.ev(`(() => { const c = document.getElementById("upClose"); if (c) c.click(); const d = document.getElementById("uploadDlg"); if (d.open) d.close(); const s = document.getElementById("selClear"); if (s && !document.getElementById("selbar").hidden) s.click(); })()`);
    await restorePicks();
  });

  // Mock only (it edits the scene): changes never block, show at once, and survive a Unity reload or a busy Unity.
  if (mock && name === names[0]) await step("queue", async () => {
    const base = new URL(url).origin;
    const wearOne = `async (card) => {
      card.scrollIntoView({ block: "center" }); card.click();
      if (await __wt.at(() => document.getElementById("itWear"), 8000) < 0) return null;
      const t0 = performance.now(); document.getElementById("itWear").click();
      return { ms: Math.round(performance.now() - t0), shown: !!document.querySelector("#itActs .q-state") && !!document.querySelector("#wearList .worn.queued") };
    }`;
    const free = `() => [...document.querySelectorAll("#grid .card")].filter((c) => !c.hidden && !c.classList.contains("cur") && !c.classList.contains("pending"))`;
    const synced = `() => !document.querySelector("#wearList .worn.queued, #wearList .worn.removing") && /Synced/.test(document.getElementById("statusText").textContent)`;
    const r = await page.ev(`(async () => {
      const wear = ${wearOne}, free = ${free}, out = { clicks: [] };
      for (const c of free().slice(0, 3)) {
        const w = await wear(c); if (!w) continue; out.clicks.push(w);
        document.getElementById("itClose").click(); if (document.getElementById("itemDlg").open) out.locked = true;
      }
      out.status = document.getElementById("statusText").textContent;
      out.queued = document.querySelectorAll("#wearList .worn.queued").length;
      out.settled = await __wt.at(${synced}, 15000) > 0;
      return out;
    })()`, 40000);
    if (!r.clicks.length) return issue("queue", "warn", "skipped", "no free cards to wear");
    if (r.clicks.some((c) => !c.shown)) issue("queue", "fail", "queue-not-shown", "Clicking Wear did not show the queued item in the modal and the Wearing list at once.");
    if (r.clicks.some((c) => c.ms > 50)) issue("queue", "fail", "queue-blocked", `Clicking Wear blocked the page for ${Math.max(...r.clicks.map((c) => c.ms))}ms.`);
    if (r.locked) issue("queue", "fail", "modal-locked", "The item modal would not close while a change was queued.");
    if (!/Saving/.test(r.status)) issue("queue", "fail", "queue-status", `With ${r.clicks.length} changes queued the status read "${r.status}".`);
    if (!r.settled) issue("queue", "fail", "queue-stuck", "Queued changes did not settle within 15s.");
    // Unity reloads (new session): the next change re-signs itself, no error.
    await fetch(base + "/mock/reload", { method: "POST" });
    const r2 = await page.ev(`(async () => { const w = await (${wearOne})((${free})()[0]); document.getElementById("itClose").click(); return { w, settled: await __wt.at(${synced}, 15000) > 0 }; })()`, 30000);
    if (!r2.w || !r2.settled) issue("queue", "fail", "reload-resend", "A change made after a Unity reload did not apply by itself.");
    // The first attempt after a reload is Unity's 409 by design (the page re-signs and resends); Chrome logs it.
    for (let i = consoleErrors.length - 1; i >= 0; i--) if (/409 \(Conflict\)/.test(consoleErrors[i])) consoleErrors.splice(i, 1);
    // Busy Unity (import or compile): the change waits visibly, the page stays usable.
    const r3a = await page.ev(`(async () => { const c = (${free})()[0]; c.scrollIntoView({ block: "center" }); c.click(); return await __wt.at(() => document.getElementById("itWear"), 8000) > 0; })()`, 15000);
    await fetch(base + "/mock/busy?ms=2500", { method: "POST" });
    const r3 = await page.ev(`(async () => {
      document.getElementById("itWear").click();
      await new Promise((r) => setTimeout(r, 600));
      const waiting = !!document.querySelector("#itActs .q-state");
      document.getElementById("itClose").click();
      const other = (${free})()[1]; other.click();
      const usable = await __wt.at(() => document.getElementById("itemDlg").open && document.querySelector("#itActs button"), 5000) > 0;
      return { waiting, usable };
    })()`, 20000);
    if (!r3a || !r3.waiting) issue("queue", "fail", "busy-hidden", "While Unity was busy the queued change was not shown as waiting.");
    if (!r3.usable) issue("queue", "fail", "busy-blocked", "While Unity was busy another item could not be opened.");
    await check("queue");
    const done = await page.ev(`(async () => { const c = document.getElementById("itClose"); if (c) c.click(); return await __wt.at(${synced}, 15000) > 0; })()`, 20000);
    if (!done) issue("queue", "fail", "queue-stuck", "The change queued while Unity was busy did not apply afterwards.");
    // Change a worn item's part toggles, then open another item while Unity is busy: the panel is usable at once
    // (cached status), and going back shows the change.
    const r5a = await page.ev(`(async () => {
      const row = document.querySelector("#wearList .worn:not(.queued):not(.removing)"); if (!row) return null;
      row.click();
      if (await __wt.at(() => document.getElementById("itemDlg").open && document.getElementById("itBody") && !document.getElementById("itBody").dataset.loading && !document.getElementById("itToggles").disabled, 8000) < 0) return null;
      const t = document.getElementById("itToggles"); const want = !t.checked; t.click();
      await __wt.at(() => !document.getElementById("itSettle").hidden, 2000);
      document.getElementById("itApply").click();
      return { want, name: document.getElementById("itTitle").textContent };
    })()`, 20000);
    if (!r5a) issue("queue", "warn", "skipped", "no worn item to change part toggles on");
    else {
      await fetch(base + "/mock/busy?ms=3000", { method: "POST" });
      const r5 = await page.ev(`(async () => {
        document.getElementById("itClose").click();
        const other = [...document.querySelectorAll("#grid .card")].find((c) => !c.hidden && c.querySelector(".nm")?.textContent !== ${JSON.stringify(r5a.name)});
        const t0 = performance.now(); other.click();
        const ready = () => document.getElementById("itBody") && !document.getElementById("itBody").dataset.loading && !document.getElementById("itGroup").disabled;
        const ms = await __wt.at(ready, 6000) > 0 ? Math.round(performance.now() - t0) : -1;
        document.getElementById("itClose").click();
        const back = [...document.querySelectorAll("#wearList .worn")].find((b) => b.textContent.includes(${JSON.stringify(r5a.name)}));
        back?.click();
        const t1 = performance.now();
        await __wt.at(ready, 6000);
        const backMs = Math.round(performance.now() - t1);
        const kept = document.getElementById("itToggles").checked === ${r5a.want};
        document.getElementById("itClose").click();
        return { ms, backMs, kept };
      })()`, 30000);
      if (r5.ms < 0 || r5.ms > 400) issue("queue", "fail", "item-status-blocked", `With a change applying, opening another item took ${r5.ms < 0 ? "over 6000" : r5.ms}ms to become usable.`);
      if (r5.backMs > 400) issue("queue", "fail", "item-status-blocked", `Reopening the changed item took ${r5.backMs}ms to become usable.`);
      if (!r5.kept) issue("queue", "fail", "item-status-lost", "Reopening the item did not show the part-toggle change that was just applied.");
      await page.ev(`__wt.at(${synced}, 15000)`, 20000);
    }
    // New look from the item panel: Wear can be clicked before Unity has made the look, and the current look stays.
    const selectedBefore = (await (await fetch(base + "/api/state")).json()).selectedPreset;
    const r4 = await page.ev(`(async () => {
      const c = (${free})()[0]; if (!c) return null;
      c.scrollIntoView({ block: "center" }); c.click();
      if (await __wt.at(() => document.getElementById("itWear"), 8000) < 0 || !document.getElementById("itNewLook")) return null;
      const prompt0 = window.prompt; window.prompt = () => "Queue test look";
      document.getElementById("itNewLook").click(); window.prompt = prompt0;
      const stand = document.getElementById("itLook").value;
      document.getElementById("itWear").click();
      const shown = !!document.querySelector("#itActs .q-state");
      const settled = await __wt.at(${synced}, 15000) > 0 && await __wt.at(() => !/^new~/.test(document.getElementById("itLook").value), 5000) > 0;
      const id = document.getElementById("itLook").value;
      await __wt.at(() => document.getElementById("itRemove"), 5000);
      const worn = !!document.getElementById("itRemove");
      document.getElementById("itClose").click();
      return { stand, id, shown, settled, worn };
    })()`, 40000);
    if (!r4) issue("queue", "warn", "skipped", "no item panel with a Look picker for New look");
    else {
      if (!/^new~/.test(r4.stand) || !r4.shown) issue("queue", "fail", "newlook-not-shown", "New look in the item panel did not select the queued look and queue Wear at once.");
      if (!r4.settled || !r4.worn) issue("queue", "fail", "newlook-wear", `Wearing into a look created from the item panel did not apply (look ${r4.id}).`);
      const st = await (await fetch(base + "/api/state")).json();
      if (st.selectedPreset !== selectedBefore) issue("queue", "fail", "newlook-switched", "Creating a look from the item panel switched the current look.");
      if (!(st.workflowPresets || []).some((p) => p.id === r4.id && p.name === "Queue test look")) issue("queue", "fail", "newlook-missing", "The look created from the item panel is not in Unity's list.");
      await fetch(base + "/mock/forget_look?id=" + encodeURIComponent(r4.id), { method: "POST" });
    }
  });

  await step("settings", async () => {
    await page.ev(`(async () => { document.getElementById("settingsBtn").click(); await __wt.at(() => document.getElementById("settingsDlg").open, 5000); })()`);
    await settle();
    await check("settings");
    await page.ev(`document.getElementById("settingsDlg").close()`);
  });

  await step("activity", async () => {
    await page.ev(`(async () => { document.getElementById("statusBtn").click(); await __wt.at(() => !document.getElementById("activityPop").hidden, 5000); })()`);
    await settle();
    await check("activity");
    await page.ev(`document.getElementById("statusBtn").click()`);
  });

  // Back to the catalog so the next viewport (or the idle check) starts from the main screen.
  await page.ev(`(() => { const b = document.querySelector('#sections [data-view="wardrobe"]'); if (b) b.click(); })()`).catch(() => {});
  return result;
}

const names = String(opt.viewports).split(",").map((s) => s.trim()).filter(Boolean);
for (const name of names) {
  process.stdout.write(`· ${name} … `);
  const r = await runViewport(name);
  report.viewports.push(r);
  const f = r.issues.filter((i) => i.severity === "fail").length;
  console.log(f ? `${f} failing` : "ok");
}

if (opt.idle > 0) {
  process.stdout.write(`· idle ${opt.idle}s … `);
  await page.send("Emulation.setDeviceMetricsOverride", { width: 1440, height: 900, deviceScaleFactor: 1, mobile: false });
  await page.ev(`document.getElementById("itClose") && document.getElementById("itClose").click()`).catch(() => {});
  await settle();
  const mark = net_.length;
  await sleep(opt.idle * 1000);
  const reqs = apiSince(mark);
  const ms = reqs.reduce((s, r) => s + Math.max(0, (r.end || Date.now()) - r.start), 0);
  report.idle = { seconds: opt.idle, requests: countBy(reqs), totalMs: ms, issues: [] };
  const installed = report.idle.requests["/api/installed"] || 0;
  if (installed > BUDGET.idleInstalled) report.idle.issues.push({ step: "idle", severity: "fail", rule: "idle-refetch", message: `/api/installed was fetched ${installed}× in ${opt.idle}s with nothing changing (budget ${BUDGET.idleInstalled}).` });
  if (report.idle.requests["/api/families"]) report.idle.issues.push({ step: "idle", severity: "fail", rule: "idle-refetch", message: `The catalog was refetched ${report.idle.requests["/api/families"]}× while idle.` });
  console.log(Object.entries(report.idle.requests).map(([p, n]) => `${p.replace("/api/", "")}×${n}`).join(" ") || "quiet");
}

const all = report.viewports.flatMap((v) => v.issues.map((i) => ({ ...i, viewport: v.name }))).concat(report.idle ? report.idle.issues.map((i) => ({ ...i, viewport: "idle" })) : []);
report.failures = all.filter((i) => i.severity === "fail").length;
report.warnings = all.filter((i) => i.severity === "warn").length;
report.missingThumbnails = missingThumbs.size;
report.finished = new Date().toISOString();
writeFileSync(join(OUT, "report.json"), JSON.stringify(report, null, 2));
writeFileSync(join(OUT, "index.html"), renderHtml(report));

console.log(`\n${report.failures} failing, ${report.warnings} warnings${missingThumbs.size ? ` · ${missingThumbs.size} items have no preview on the server` : ""}`);
const seen = new Set();
for (const i of all) { const k = i.severity + i.rule + i.message; if (seen.has(k)) continue; seen.add(k); const where = all.filter((x) => x.severity + x.rule + x.message === k).map((x) => x.viewport + "/" + x.step); console.log(`  ${i.severity === "fail" ? "✗" : "!"} [${i.rule}] ${i.message}${i.detail ? "  — " + i.detail : ""}  (${[...new Set(where)].join(", ")})`); }
console.log("\nReport: " + join(OUT, "index.html"));

if (mock) mock.proc.kill();
await new Promise((r) => { chrome.once("exit", r); chrome.kill(); setTimeout(r, 3000); });
try { rmSync(PROFILE, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 }); } catch {}
if (opt.open) spawn("open", [join(OUT, "index.html")], { stdio: "ignore", detached: true });
process.exit(report.failures ? 1 : 0);

function renderHtml(r) {
  const esc = (s) => String(s == null ? "" : s).replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
  const perfKeys = [["firstCardMs", "First card", "ms"], ["firstThumbMs", "First thumbnail", "ms"], ["itemHeadMs", "Item opens", "ms"], ["itemFullMs", "Item ready", "ms"], ["lookUiMs", "Look switch", "ms"], ["lookFamiliesRequests", "Catalog refetches on look switch", ""], ["lookOutlined", "Cards outlined for the look", ""]];
  const perf = `<table><tr><th></th>${r.viewports.map((v) => `<th>${esc(v.name)}<br><span>${esc(v.size)}</span></th>`).join("")}</tr>${perfKeys.map(([k, label, unit]) => `<tr><td>${label}</td>${r.viewports.map((v) => `<td>${v.perf[k] == null ? "—" : esc(v.perf[k]) + unit}</td>`).join("")}</tr>`).join("")}</table>`;
  const idle = r.idle ? `<p class="sub">Idle ${r.idle.seconds}s: ${Object.entries(r.idle.requests).map(([p, n]) => `${esc(p)} ×${n}`).join(", ") || "no requests"} · ${r.idle.totalMs}ms of server time</p>` : "";
  const issues = (list) => list.length ? `<ul>${list.map((i) => `<li class="${i.severity}"><b>${esc(i.rule)}</b> <span>${esc(i.step)}</span> ${esc(i.message)}${i.detail ? `<code>${esc(i.detail)}</code>` : ""}</li>`).join("")}</ul>` : `<p class="ok">No issues.</p>`;
  return `<!doctype html><meta charset="utf-8"><title>Wardrobe UI checks</title><style>
:root{color-scheme:dark}body{margin:0;padding:24px;background:#121213;color:#ecebe6;font:14px/1.45 -apple-system,system-ui,sans-serif}
h1{font-size:20px;margin:0 0 4px}h2{font-size:15px;margin:28px 0 8px}.sub,span{color:#8d8c87}table{border-collapse:collapse;margin-top:12px}
td,th{padding:6px 12px;border-bottom:1px solid #2a2a2e;text-align:left;font-variant-numeric:tabular-nums}th{font-weight:600}
ul{padding:0;list-style:none}li{padding:6px 0;border-bottom:1px solid #222225}li.fail b{color:#ff6b6b}li.warn b{color:#e8cf62}li span{margin:0 6px}
code{display:block;color:#a9a8a2;font-size:12px;margin-top:2px}.ok{color:#7bd88f}.shots{display:grid;grid-template-columns:repeat(auto-fill,minmax(320px,1fr));gap:14px}
figure{margin:0}figure img{width:100%;border:1px solid #2a2a2e;border-radius:6px;display:block}figcaption{font-size:12px;color:#8d8c87;margin-top:4px}
</style><h1>Wardrobe UI checks</h1><p class="sub">${esc(r.url)} · ${esc(r.started)} · ${r.failures} failing · ${r.warnings} warnings</p>
<h2>Timings</h2>${perf}${idle}${r.idle ? issues(r.idle.issues) : ""}
${r.viewports.map((v) => `<h2>${esc(v.name)} <span>${esc(v.size)}</span></h2>${issues(v.issues)}<div class="shots">${v.steps.map((s) => `<figure><a href="${esc(s.file)}"><img loading="lazy" src="${esc(s.file)}"></a><figcaption>${esc(s.step)}</figcaption></figure>`).join("")}</div>`).join("")}`;
}
