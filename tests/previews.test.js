/* Run the shipped preview scheduler against controlled, asynchronous HTTP responses. */
const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const source = fs.readFileSync(path.join(__dirname, '../Packages/dev.gryphprime.avatar-wardrobe/Web/previews.js'), 'utf8');
const wait = ms => new Promise(resolve => setTimeout(resolve, ms));
const tick = () => wait(0);
function response(status, hi = false) {
  return {status, ok: status === 200, blob: async () => new Blob([hi ? 'high' : 'low'])};
}
function setup(handler) {
  const calls = [], listeners = {}, nodes = [];
  let now = 0;
  const window = {innerWidth:1200, innerHeight:800, addEventListener: (name, fn) => { listeners[name] = fn; },
    WardrobeRuntime: {escape: value => String(value), request: async (url, options) => {
      const q = new URL(url, 'http://localhost').searchParams;
      const call = {guid:q.get('guid'), hi:q.has('hi'), cached:q.has('cached'), retry:q.has('retry'), signal:options.signal, cache:options.cache};
      calls.push(call); return handler(call);
    }}};
  class Image { set src(value) { queueMicrotask(() => this.onload()); } async decode() {} }
  const document = {hidden:false, hasFocus:()=>true, querySelectorAll:()=>nodes, createElement:()=>({getContext:()=>null})};
  vm.runInNewContext(source, {window, document, Image, URL, Blob, AbortController, DOMException, setTimeout, clearTimeout, setInterval:()=>1, clearInterval:()=>{}, queueMicrotask, Date: {now:()=>now}});
  const pipeline = window.WardrobePreviews();
  return {pipeline, calls, nodes, document, advance:()=>{now += 5001;}, close:()=>listeners.pagehide({persisted:false})};
}

test('selected image starts while grid requests are blocked; only three requests run', async () => {
  const blocked = [];
  const h = setup(call => {
    if (call.guid === 'detail') return response(200, true);
    if (call.cached) return response(202);
    return new Promise(resolve => blocked.push(resolve));
  });
  const cards = ['a','b','c'].map(guid => h.pipeline.get(guid, 1));
  await tick();
  assert.equal(blocked.length, 2);
  assert.equal(h.pipeline.stats().active, 2);
  const detail = await h.pipeline.get('detail', 0);
  assert.equal(detail.hi, true);
  assert.equal(blocked.length, 2, 'detail must not wait for grid completion');
  h.close(); blocked.forEach(resolve => resolve(response(200)));
  await Promise.all(cards);
});

test('low-res selected preview is published before its high-res render finishes', async () => {
  let finishHigh;
  const progress = [];
  const h = setup(call => call.cached ? response(202) : !call.hi ? response(200) : new Promise(resolve => {finishHigh=resolve;}));
  const done = h.pipeline.get('selected', 0, false, result => progress.push(result));
  await tick();
  assert.equal(progress.length, 1);
  assert.equal(progress[0].hi, false);
  assert.ok(finishHigh);
  finishHigh(response(200, true));
  assert.equal((await done).hi, true);
  h.close();
});

test('grid finishes with low-res without requesting a cold high-res render', async () => {
  const h = setup(call => call.cached ? response(202) : response(200));
  assert.equal((await h.pipeline.get('card', 1)).hi, false);
  assert.equal(h.calls.some(call => call.hi && !call.cached), false);
  const count = h.calls.length;
  await h.pipeline.get('card', 1);
  assert.equal(h.calls.length, count, 'reuse the low-res grid cache');
  h.close();
});

test('pending retries release slots and cancellation removes delayed work', async () => {
  const h = setup(call => call.guid === 'ready' ? response(200, true) : response(202));
  const pending = h.pipeline.get('pending', 1);
  await tick();
  assert.equal(h.pipeline.stats().active, 0);
  assert.equal((await h.pipeline.get('ready', 1)).hi, true);
  h.pipeline.reset('next');
  assert.equal(await pending, null);
  const count = h.calls.length;
  await wait(450);
  assert.equal(h.calls.length, count);
  h.close();
});

test('promoting a delayed grid job wakes the selected-image slot immediately', async () => {
  let ready = false;
  const h = setup(() => ready ? response(200, true) : response(202));
  const pending = h.pipeline.get('promoted', 1);
  await tick(); ready = true;
  const start = performance.now();
  const selected = h.pipeline.get('promoted', 0);
  assert.equal((await selected).hi, true);
  assert.ok(performance.now() - start < 200);
  await pending; h.close();
});

test('unavailable images recover on explicit retry and reset both resolutions', async () => {
  let retried = false;
  const h = setup(call => {
    if (call.retry) retried = true;
    return call.cached ? response(202) : retried ? response(200, call.hi) : response(404);
  });
  assert.equal((await h.pipeline.get('dead', 1)).dead, true);
  assert.equal(h.pipeline.isUnavailable('dead'), true);
  assert.equal((await h.pipeline.get('dead', 0, true)).hi, true);
  assert.equal(h.calls.filter(call => call.retry).length, 1);
  assert.equal(h.pipeline.isUnavailable('dead'), false);
  h.close();
});

test('successful shared retry repaints a visible failed consumer without a second render', async () => {
  let recovered = false;
  const h = setup(call => call.retry || recovered ? response(200, call.hi) : response(404));
  const failed = previewNode(); h.nodes.push(failed);
  h.pipeline.bind(failed, 'filmstrip', {priority:1}); await tick(); await tick();
  assert.equal(h.pipeline.isUnavailable('filmstrip'), true);
  recovered = true;
  await h.pipeline.get('filmstrip', 0, true);
  const before = h.calls.length;
  h.advance(); h.pipeline.refresh(); await tick(); await tick();
  assert.ok(failed.image, 'failed visible consumer should adopt the shared recovered blob');
  assert.equal(h.calls.length, before, 'repainting from cache must not issue another request');
  h.close();
});

function previewNode() {
  const node = {isConnected:true, dataset:{}, image:null, button:null,
    classList:{add(){},remove(){}}, setAttribute(){},removeAttribute(){},
    querySelector(selector){return selector === 'img' ? this.image : selector === 'button' ? this.button : null;},
    replaceChildren(image){this.image=image;},
    getBoundingClientRect(){return {width:100,height:100,top:0,left:0,bottom:100,right:100};}};
  Object.defineProperty(node, 'innerHTML', {set(value){this.button = value.indexOf('preview-retry') >= 0 ? {onclick:null} : null;}, get(){return '';}});
  return node;
}

test('detail retry preserves detail image styling', async () => {
  let recovered = false;
  const h = setup(call => recovered ? response(200, true) : response(404));
  const node = previewNode(); h.nodes.push(node); h.pipeline.bind(node, 'detail', {priority:0, detail:true}); await tick(); await tick();
  recovered = true; node.button.onclick({stopPropagation(){}}); await tick(); await tick();
  assert.equal(node.image.className, 'big instant'); assert.equal(node.image.id, 'dImg'); h.close();
});

test('visible low-res card upgrades when a later background render becomes available', async () => {
  let ready = false;
  const h = setup(call => call.hi ? response(ready ? 200 : 202, true) : response(200));
  const node = previewNode(); h.nodes.push(node);
  h.pipeline.bind(node, 'card', {priority:1}); await tick(); await tick();
  const lowImage = node.image; assert.ok(lowImage);
  h.advance(); h.pipeline.refresh(); await tick(); await tick();
  assert.equal(node.image, lowImage, 'pending upgrade retains displayed pixels');
  ready = true; h.advance(); h.pipeline.refresh(); await tick(); await tick();
  assert.notEqual(node.image, lowImage, 'ready high-res replaces the low-res node');
  assert.equal((await h.pipeline.get('card', 1)).hi, true);
  assert.equal(h.calls.filter(call => !call.hi).length, 1);
  assert.equal(h.calls.some(call => call.hi && !call.cached), false, 'upgrade probes never render on Unity');
  const count = h.calls.length; h.advance(); h.pipeline.refresh(); await tick();
  assert.equal(h.calls.length, count, 'high-res bindings stop polling'); h.close();
});

test('upgrade probes skip offscreen cards and respect their interval', async () => {
  const h = setup(call => call.hi ? response(202) : response(200));
  const node = previewNode(); h.nodes.push(node);
  h.pipeline.bind(node, 'card', {priority:1}); await tick(); await tick();
  const count = h.calls.length;
  h.pipeline.refresh(); await tick(); assert.equal(h.calls.length, count);
  h.advance();
  node.getBoundingClientRect = () => ({width:100,height:100,top:900,left:0,bottom:1000,right:100});
  h.pipeline.refresh(); await tick(); assert.equal(h.calls.length, count); h.close();
});

test('hidden and unfocused pages continue bounded preview work', async () => {
  const h = setup(call => call.cached ? response(202) : response(200, call.hi));
  h.document.hidden = true; h.document.hasFocus = () => false;
  assert.equal((await h.pipeline.get('hidden', 0)).hi, true);
  const node = previewNode(); h.nodes.push(node); h.pipeline.bind(node, 'upgrade', {priority:1}); await tick(); await tick();
  const count = h.calls.length; h.advance(); h.pipeline.refresh(); await tick(); await tick();
  assert.ok(h.calls.length > count, 'hidden cache upgrades should continue'); h.close();
});

test('cached high-res cards bypass blocked Unity renders', async () => {
  const h = setup(call => call.cached ? response(call.guid.startsWith('cold') ? 202 : 200, true) :
    new Promise(resolve => setTimeout(() => resolve(response(200)), 300)));
  const cold = ['cold-a', 'cold-b'].map(guid => h.pipeline.get(guid, 1));
  await tick();
  const start = performance.now();
  const warm = await Promise.all(Array.from({length:12}, (_, i) => h.pipeline.get('warm-' + i, 1)));
  const elapsed = performance.now() - start;
  console.log('12 cached high-res cards behind 2 × 300ms Unity renders: ' + elapsed.toFixed(1) + 'ms');
  h.close(); await Promise.all(cold);
  assert.ok(warm.every(result => result.hi));
  assert.ok(elapsed < 150, 'disk cache hits must not wait behind rendering');
});

test('cached-image requests allow browser caching; explicit retry bypasses it', async () => {
  const h = setup(call => call.cached ? response(202) : response(200, call.hi));
  await h.pipeline.get('retry-cache', 0, true);
  assert.ok(h.calls.find(call => call.retry));
  assert.ok(h.calls.filter(call => call.retry).every(call => call.cache === 'no-store'));
  assert.ok(h.calls.filter(call => !call.retry).every(call => call.cache === 'reload'), 'a regenerated image must bypass the old immutable response');
  await h.pipeline.get('normal-cache', 1);
  assert.ok(h.calls.filter(call => call.guid === 'normal-cache').every(call => call.cache === 'default'));
  h.close();
});


test('explicit retry replaces an existing delayed request without replaying it', async () => {
  let ready = false;
  const h = setup(call => call.retry || ready ? response(200, call.hi) : response(202));
  const pending = h.pipeline.get('retry-delayed', 1);
  await tick(); ready = true;
  const retry = h.pipeline.get('retry-delayed', 0, true);
  assert.equal(await pending, null);
  assert.equal((await retry).hi, true);
  const count = h.calls.length;
  await wait(450);
  assert.equal(h.calls.length, count);
  assert.equal(h.calls.filter(call => call.retry).length, 1);
  h.close();
});

test('disk probe concurrency stays bounded during a large scroll', async () => {
  const waiting = [];
  const h = setup(() => new Promise(resolve => waiting.push(resolve)));
  const jobs = Array.from({length:40}, (_, i) => h.pipeline.get('scroll-' + i, 1));
  await tick();
  assert.equal(waiting.length, 4);
  assert.equal(h.pipeline.stats().active, 4);
  h.pipeline.reset('new-epoch');
  waiting.forEach(resolve => resolve(response(200, true)));
  assert.ok((await Promise.all(jobs)).every(result => result === null));
  await tick();
  assert.equal(h.pipeline.stats().active, 0);
  assert.equal(h.pipeline.stats().queued, 0);
  h.close();
});

 test('cache upgrades never replay a one-time explicit retry', async () => {
  const h = setup(call => call.hi ? response(202) : response(200));
  const node = previewNode(); h.nodes.push(node);
  h.pipeline.bind(node, 'retry-once', {priority:1, retry:true});
  await tick(); await tick();
  assert.ok(node.image);
  const before = h.calls.length; h.advance(); h.pipeline.refresh();
  await tick(); await tick();
  assert.equal(h.calls.slice(before).filter(call => call.retry).length, 0);
  assert.ok(h.calls.slice(before).every(call => call.cached));
  h.close();
 });
