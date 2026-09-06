/* Shared, bounded preview pipeline. Cache Blobs, not revocable URLs owned by the UI. */
(function (global) {
  "use strict";
  global.WardrobePreviews = function (options) {
    options = options || {};
    var runtime = global.WardrobeRuntime;
    var cache = new Map(), jobs = new Map(), unavailable = new Set();
    var queue = [], bindings = new WeakMap(), observers = new Map(), observed = new Map();
    var epoch = "", serial = 0, active = 0, bytes = 0, stopped = false;
    var maxBytes = 32 * 1024 * 1024, maxEntries = 256, maxActive = 3;

    function key(guid) { return epoch + ":" + guid; }
    function notify() { if (options.onActivity) options.onActivity({active: active, queued: queue.length}); }
    function remember(id, entry) {
      if (cache.get(id) === entry) return;
      if (cache.has(id)) bytes -= cache.get(id).blob.size;
      cache.delete(id); cache.set(id, entry); bytes += entry.blob.size;
      while (cache.size > maxEntries || bytes > maxBytes) {
        var oldest = cache.keys().next().value;
        bytes -= cache.get(oldest).blob.size;
        cache.delete(oldest);
      }
    }
    function forget(id) {
      if (cache.has(id)) bytes -= cache.get(id).blob.size;
      cache.delete(id); unavailable.delete(id);
    }
    function sleep(ms, signal) {
      return new Promise(function (resolve, reject) {
        if (signal.aborted) { reject(new DOMException("Aborted", "AbortError")); return; }
        var timer = setTimeout(function () { signal.removeEventListener("abort", abort); resolve(); }, ms);
        function abort() {
          clearTimeout(timer); signal.removeEventListener("abort", abort);
          reject(new DOMException("Aborted", "AbortError"));
        }
        signal.addEventListener("abort", abort, {once: true});
      });
    }
    async function decode(blob) {
      var url = URL.createObjectURL(blob), image = new Image();
      image.alt = ""; image.decoding = "async";
      try {
        await new Promise(function (resolve, reject) {
          image.onload = resolve; image.onerror = reject; image.src = url;
        });
        if (image.decode) await image.decode().catch(function () {});
        return image;
      } finally {
        // The decoded node owns its pixels now. Never share a revoked URL with
        // another card; every consumer decodes its own short-lived Blob URL.
        URL.revokeObjectURL(url);
      }
    }
    function blank(image) {
      try {
        var canvas = document.createElement("canvas"); canvas.width = canvas.height = 48;
        var context = canvas.getContext("2d", {willReadFrequently: true});
        context.drawImage(image, 0, 0, 48, 48);
        var data = context.getImageData(0, 0, 48, 48).data, visible = 0, min = 255, max = 0;
        for (var i = 0; i < data.length; i += 4) {
          if (data[i + 3] < 8) continue;
          visible++;
          var light = (data[i] + data[i + 1] + data[i + 2]) / 3;
          min = Math.min(min, light); max = Math.max(max, light);
        }
        return visible === 0 || (visible > 48 * 48 * .99 && max - min < 1.5);
      } catch (_) { return false; }
    }
    async function fetchImage(job, hi, retry) {
      var path = "/api/thumb?guid=" + encodeURIComponent(job.guid) + "&v=" + encodeURIComponent(job.epoch) +
        (hi ? "&hi=1" : "") + (retry ? "&retry=1" : "");
      var response = await runtime.request(path, {binary: true, signal: job.controller.signal, timeout: 35000});
      if (response.status === 202 || response.status === 503) return {pending: true};
      if (response.status === 404) return {dead: true};
      if (!response.ok) throw new Error("Preview request failed (" + response.status + ")");
      var blob = await response.blob();
      if (blank(await decode(blob))) return {dead: true};
      return {blob: blob, hi: hi};
    }
    async function run(job) {
      var low = cache.get(job.key), hiDead = false, loDead = false;
      for (var attempt = 0; attempt < 5; attempt++) {
        if (job.controller.signal.aborted) throw new DOMException("Aborted", "AbortError");
        if (document.hidden || !document.hasFocus()) return low || {paused: true};
        var hi = hiDead ? {dead: true} : await fetchImage(job, true, job.retry && attempt === 0);
        if (hi.blob) return hi;
        hiDead = !!hi.dead;
        if (!low && !loDead) {
          var result = await fetchImage(job, false, false);
          if (result.blob) {
            low = result;
            if (job.epoch !== epoch) return null;
            remember(job.key, low);
            job.partial = low;
            job.progress.forEach(function (report) { report(low); });
          }
          loDead = !!result.dead;
        }
        if (hiDead) return low || {dead: loDead, pending: !loDead};
        await sleep(Math.min(2500, 400 * Math.pow(1.7, attempt)), job.controller.signal);
      }
      return low || {pending: true};
    }
    function pump() {
      if (stopped) return;
      while (active < maxActive && queue.length) {
        queue.sort(function (a, b) { return a.priority - b.priority || a.order - b.order; });
        var job = queue.shift(); active++; notify();
        (function (current) {
          run(current).then(function (result) {
            if (!result || current.epoch !== epoch) { current.resolve(null); return; }
            if (result.blob) remember(current.key, result);
            if (result.dead) unavailable.add(current.key);
            current.resolve(result);
          }).catch(function (error) {
            current.resolve(error.name === "AbortError" ? null : {error: true});
          }).finally(function () {
            active--;
            if (jobs.get(current.key) === current) jobs.delete(current.key);
            notify(); pump();
          });
        })(job);
      }
    }
    function get(guid, priority, retry, onProgress) {
      if (!guid || stopped) return Promise.resolve({dead: true});
      var id = key(guid);
      if (retry) forget(id);
      if (cache.has(id) && cache.get(id).hi) {
        var entry = cache.get(id); cache.delete(id); cache.set(id, entry);
        return Promise.resolve(entry);
      }
      if (!retry && unavailable.has(id)) return Promise.resolve({dead: true});
      if (jobs.has(id)) {
        var current = jobs.get(id); current.priority = Math.min(current.priority, priority || 0);
        if (onProgress) { current.progress.push(onProgress); if (current.partial) onProgress(current.partial); }
        return current.promise;
      }
      var job = {key: id, guid: guid, epoch: epoch, priority: priority || 0, order: serial++, retry: !!retry,
        controller: new AbortController(), progress: onProgress ? [onProgress] : []};
      job.promise = new Promise(function (resolve) { job.resolve = resolve; });
      jobs.set(id, job); queue.push(job); pump(); return job.promise;
    }
    function label(id) { return options.text ? options.text(id) : id; }
    function fallback(node, guid, result) {
      if (!node.isConnected) return;
      if (result && result.dead && options.onUnavailable && options.onUnavailable(node)) return;
      var state = result && result.dead ? "unavailable" : result && result.paused ? "paused" : result && result.pending ? "pending" : "network";
      node.classList.remove("preview-loading"); node.removeAttribute("aria-busy");
      node.innerHTML = '<div class="preview-fallback"><svg class="icon" aria-hidden="true"><use href="#icon-wardrobe"></use></svg><strong>' +
        runtime.escape(label("preview." + state)) + '</strong>' + (guid ? '<button type="button" class="preview-retry">' +
        runtime.escape(label("preview.retry")) + '</button>' : "") + '</div>';
      var retry = node.querySelector("button");
      if (retry) retry.onclick = function (event) { event.stopPropagation(); bind(node, guid, {priority: 0, retry: true}); };
    }
    function bind(node, guid, config) {
      config = config || {};
      if (!node || !guid) { if (node) queueMicrotask(function () { fallback(node, null, {dead: true}); }); return; }
      var previous = bindings.get(node);
      if (previous && previous.guid === guid && previous.epoch === epoch && !config.retry && (previous.loading || previous.ready)) {
        // A prefetched card may become visible while still queued: promote that same job.
        var pending = jobs.get(key(guid));
        if (pending) pending.priority = Math.min(pending.priority, config.priority || 0);
        return;
      }
      var binding = {guid: guid, epoch: epoch, loading: true, ready: false};
      bindings.set(node, binding);
      node._wardrobePreview = {guid: guid, config: config}; node.dataset.thumb = guid;
      if (!node.querySelector("img")) {
        node.classList.add("preview-loading"); node.setAttribute("aria-busy", "true");
        node.innerHTML = '<span class="preview-placeholder" aria-hidden="true"></span>';
      }
      var paintVersion = 0;
      async function paint(result, partial) {
        if (bindings.get(node) !== binding) return;
        if (!result || !node.isConnected) { binding.loading = false; binding.ready = false; return; }
        var version = ++paintVersion;
        if (!result.blob) {
          binding.loading = false; binding.ready = !!result.dead;
          if (node.querySelector("img")) { node.classList.add("preview-stale"); node.dataset.staleLabel = label("preview.pending"); return; }
          fallback(node, guid, result); return;
        }
        try {
          if (binding.blob === result.blob) { binding.ready = !partial; binding.loading = !!partial; return; }
          var image = await decode(result.blob);
          if (version !== paintVersion || bindings.get(node) !== binding) return;
          if (!node.isConnected) { binding.loading = false; binding.ready = false; return; }
          image.className = config.detail ? "big instant" : "instant";
          if (config.detail) image.id = "dImg";
          node.replaceChildren(image);
          node.classList.remove("preview-loading", "preview-stale"); node.removeAttribute("aria-busy");
          binding.blob = result.blob; binding.ready = !partial; binding.loading = !!partial;
        } catch (_) {
          binding.loading = false;
          if (!node.querySelector("img")) fallback(node, guid, {error: true});
        }
      }
      get(guid, config.priority, config.retry, function (result) { paint(result, true); }).then(function (result) { paint(result, false); });
    }
    function sweep() {
      observed.forEach(function (observer, node) { if (!node.isConnected) { observer.unobserve(node); observed.delete(node); } });
    }
    function observe(node, guid, config) {
      config = config || {};
      if (!node || !guid) { if (node) queueMicrotask(function () { fallback(node, null, {dead: true}); }); return; }
      var previous = bindings.get(node);
      if (previous && previous.guid === guid && previous.epoch === epoch && (previous.ready || previous.loading)) return;
      node.dataset.thumb = guid;
      if (!node.querySelector("img")) node.classList.add("preview-loading");
      var root = config.root || null;
      if (!observers.has(root)) {
        var observer = new IntersectionObserver(function (entries) {
          entries.forEach(function (entry) {
            if (entry.isIntersecting) {
              observer.unobserve(entry.target); observed.delete(entry.target);
              var data = entry.target._wardrobePreview;
              if (data) bind(entry.target, data.guid, data.config);
            }
          });
        }, {root: root, rootMargin: "220px"});
        observers.set(root, observer);
      }
      node._wardrobePreview = {guid: guid, config: config};
      observers.get(root).observe(node); observed.set(node, observers.get(root));
    }
    function cancelJobs() {
      jobs.forEach(function (job) { job.controller.abort(); job.resolve(null); });
      queue = []; jobs.clear();
    }
    function reset(nextEpoch) {
      if (String(nextEpoch) === epoch) return;
      epoch = String(nextEpoch); cancelJobs(); cache.clear(); bytes = 0; unavailable.clear(); notify();
    }
    function resume() {
      sweep();
      document.querySelectorAll("[data-thumb]").forEach(function (node) {
        var data = node._wardrobePreview;
        if (data) observe(node, data.guid, data.config);
      });
    }
    global.addEventListener("pagehide", function (event) {
      stopped = true; cancelJobs();
      if (!event.persisted) { cache.clear(); bytes = 0; observers.forEach(function (observer) { observer.disconnect(); }); }
    });
    global.addEventListener("pageshow", function (event) {
      if (event.persisted) { stopped = false; bindings = new WeakMap(); resume(); pump(); }
    });
    return {get: get, bind: bind, observe: observe, resume: resume, reset: reset, decode: decode, fallback: fallback, sweep: sweep,
      isUnavailable: function (guid) { return unavailable.has(key(guid)); },
      stats: function () { return {active: active, queued: queue.length, cached: cache.size, bytes: bytes}; }};
  };
})(window);
