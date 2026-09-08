/* Shared browser helpers. No framework or external runtime dependency. */
(function (global) {
  "use strict";
  var savedFocus = new WeakMap(), session = "", avatarId = 0;
  function text(node, value) {
    if (!node) return;
    value = value == null ? "" : String(value);
    if (node.textContent !== value) node.textContent = value;
  }
  function escape(value) {
    return String(value == null ? "" : value).replace(/[&<>"']/g, function (c) {
      return {"&":"&amp;", "<":"&lt;", ">":"&gt;", '"':"&quot;", "'":"&#39;"}[c];
    });
  }
  function stored(key, fallback) {
    try { var value = localStorage.getItem(key); return value == null ? fallback : value; }
    catch (_) { return fallback; }
  }
  function store(key, value) { try { localStorage.setItem(key, value); } catch (_) {} }
  // Reuse keyed DOM nodes. Move only nodes whose order actually changes.
  function reconcile(parent, items, keyOf, create, update) {
    var old = new Map(), seen = new Map(), retained = new Set();
    Array.from(parent.children).forEach(function (node) {
      if (node._wardrobeKey != null) old.set(node._wardrobeKey, node);
    });
    var cursor = parent.firstElementChild;
    items.forEach(function (item, index) {
      var base = String(keyOf(item)), occurrence = seen.get(base) || 0;
      seen.set(base, occurrence + 1);
      var key = JSON.stringify([base, occurrence]);
      var node = old.get(key) || create(item, index);
      node._wardrobeKey = key;
      if (node !== cursor) parent.insertBefore(node, cursor);
      cursor = node.nextElementSibling;
      retained.add(node);
      update(node, item, index);
    });
    Array.from(parent.children).forEach(function (node) { if (!retained.has(node)) node.remove(); });
  }
  async function request(path, options) {
    options = options || {};
    var controller = new AbortController(), upstream = options.signal;
    var relay = function () { controller.abort(); };
    if (upstream) {
      if (upstream.aborted) controller.abort();
      else upstream.addEventListener("abort", relay, {once:true});
    }
    var timer = setTimeout(function () { controller.abort(); }, options.timeout || 45000);
    var init = Object.assign({}, options, {signal:controller.signal, cache:options.cache || "no-store"});
    delete init.timeout; delete init.binary;
    var url = new URL(path, location.href);
    var reads = new Set(["state", "families", "family", "installed", "nameResult", "shops", "diag", "thumb",
      "operation_context", "operation_result", "snapshot", "menu_snapshot", "scene_snapshot", "scene_upload_review", "upload_result", "upload_status", "batch_state", "batch_job", "batch_export", "presets", "batch_thumb_img", "batch_unassigned"]);
    var endpoint = url.pathname.split("/").pop(), op = url.searchParams.get("op");
    var read = reads.has(endpoint) || (["batch_blendshape", "batch_item", "batch_faceemo"].includes(endpoint) && (!op || op === "get"));
    if (endpoint === "thumb" && url.searchParams.get("retry") === "1") read = false;
    if (url.origin === location.origin && url.pathname.startsWith("/api/")) {
      // State changes are POST-only and reject cross-origin requests in the Unity host.
      init.method = options.method || (read ? "GET" : "POST");
      read = init.method === "GET";
      if (!read) {
        init.headers = Object.assign({}, init.headers, {"X-Wardrobe-Request":"1"});
        if (session) init.headers["X-Wardrobe-Session"] = session;
        if (avatarId) init.headers["X-Wardrobe-Avatar"] = String(avatarId);
      }
    } else if (!init.method) init.method = "GET";
    try {
      var response = await fetch(path, init);
      if (options.binary) return response;
      var body = await response.text(), value;
      try { value = body ? JSON.parse(body) : null; }
      catch (_) { throw new Error(response.ok ? "Invalid server response" : body.slice(0, 240)); }
      if (!response.ok) throw new Error((value && (value.message || value.error)) || "Request failed (" + response.status + ")");
      return value;
    } finally {
      clearTimeout(timer);
      if (upstream) upstream.removeEventListener("abort", relay);
    }
  }
  function openDialog(node) {
    if (!savedFocus.has(node)) savedFocus.set(node, document.activeElement);
    node.setAttribute("aria-hidden", "false");
    requestAnimationFrame(function () { if (node.isConnected && node.getClientRects().length) node.focus({preventScroll:true}); });
  }
  function closeDialog(node) {
    node.setAttribute("aria-hidden", "true");
    var previous = savedFocus.get(node); savedFocus.delete(node);
    if (previous && previous.isConnected) previous.focus({preventScroll:true});
  }
  document.addEventListener("keydown", function (event) {
    if (event.key !== "Tab") return;
    var dialogs = Array.from(document.querySelectorAll('[role="dialog"][aria-modal="true"]'));
    var dialog = dialogs.filter(function (node) { return node.getClientRects().length; }).pop();
    if (!dialog) return;
    var targets = Array.from(dialog.querySelectorAll('button:not([disabled]),input:not([disabled]),select:not([disabled]),textarea:not([disabled]),a[href],[tabindex]:not([tabindex="-1"])')).filter(function (node) { return node.getClientRects().length; });
    if (!targets.length) { event.preventDefault(); dialog.focus(); return; }
    var index = targets.indexOf(document.activeElement);
    if (event.shiftKey && index <= 0) { event.preventDefault(); targets[targets.length-1].focus(); }
    else if (!event.shiftKey && (index < 0 || index === targets.length-1)) { event.preventDefault(); targets[0].focus(); }
  });
  global.WardrobeRuntime = {text:text,escape:escape,stored:stored,store:store,reconcile:reconcile,request:request,setContext:function(value,id){session=value||"";avatarId=id||0;},openDialog:openDialog,closeDialog:closeDialog};
})(window);

/* Update checks run in the browser, never on Unity's editor thread. */
(function (global) {
  'use strict';
  var listing = 'https://gryphprime.github.io/avatar-wardrobe-vpm/index.json';
  var key = 'wardrobe.vpm.update.v1', ttl = 6 * 60 * 60 * 1000;
  var nextCheck = 0, flight = null, latest = '', current = '', translate = null;
  function parse(version) {
    var m = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?$/.exec(version || '');
    return m ? {numbers:m.slice(1,4).map(Number), prerelease:m[4] || ''} : null;
  }
  function newer(candidate, installed) {
    var a = parse(candidate), b = parse(installed);
    if (!a || !b || a.prerelease) return false;
    for (var i=0; i<3; i++) if (a.numbers[i] !== b.numbers[i]) return a.numbers[i] > b.numbers[i];
    return !!b.prerelease;
  }
  function paint() {
    var node = document.getElementById('wardrobeUpdate');
    if (!node) return;
    node.hidden = !newer(latest, current);
    if (!node.hidden) {
      node.textContent = translate('update.available');
      node.title = translate('update.hint', current, latest);
    }
  }
  function refresh(version, t) {
    current = version || ''; translate = t;
    if (!parse(current)) { paint(); return; }
    if (!nextCheck) {
      try {
        var cached = JSON.parse(localStorage.getItem(key));
        if (cached && parse(cached.latest) && cached.checkedAt <= Date.now() && Date.now()-cached.checkedAt < ttl) {
          latest = cached.latest; nextCheck = cached.checkedAt + ttl;
        }
      } catch (_) { /* Storage may be disabled. */ }
    }
    paint();
    if (flight || Date.now() < nextCheck) return flight;
    // Failed checks remain quiet and retry after an hour rather than on every state poll.
    nextCheck = Date.now() + 60 * 60 * 1000;
    var controller = new AbortController(), timer = setTimeout(function(){controller.abort();},8000);
    flight = fetch(listing, {signal:controller.signal, credentials:'omit', referrerPolicy:'no-referrer'})
      .then(function(response){if (!response.ok) throw Error('Update check unavailable'); return response.json();})
      .then(function(data){
        var entry = data.packages && data.packages['dev.gryphprime.avatar-wardrobe'];
        if (!entry || !entry.versions) throw Error('No package versions');
        var best = '';
        Object.keys(entry.versions).forEach(function(v){
          var manifest = entry.versions[v], parsed = parse(v);
          if (!parsed || parsed.prerelease || !manifest || manifest.version !== v || manifest.name !== 'dev.gryphprime.avatar-wardrobe' || manifest['vrc-get.yanked']) return;
          if (!best || newer(v,best)) best=v;
        });
        if (!best) throw Error('No regular package versions');
        latest=best; nextCheck=Date.now()+ttl;
        try { localStorage.setItem(key,JSON.stringify({latest:latest,checkedAt:Date.now()})); } catch (_) {}
        paint();
      }).catch(function(){ /* Offline checks must not disturb the wardrobe. */ })
      .finally(function(){clearTimeout(timer);flight=null;});
    return flight;
  }
  global.WardrobeUpdates = {refresh:refresh, newer:newer};
})(window);
