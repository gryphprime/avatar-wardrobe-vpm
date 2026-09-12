const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs'), path = require('node:path'), vm = require('node:vm');
const source = fs.readFileSync(path.join(__dirname, '../Packages/dev.gryphprime.avatar-wardrobe/Web/wardrobe.js'), 'utf8');
const lifecycle = source.slice(source.indexOf('  function pingActive(on){'), source.indexOf('  loadLangs().then(function(){', source.indexOf('  function pingActive(on){')));
const settle = () => new Promise(resolve => setImmediate(resolve));

function setup() {
  const requests = [], timers = new Map(), pageEvents = {}, documentEvents = {};
  let next = 0, refreshes = 0, release = null;
  const document = {hidden:false, hasFocus:()=>false, addEventListener:(name, fn)=>documentEvents[name]=fn};
  const context = {
    document, window:{addEventListener:(name, fn)=>pageEvents[name]=fn},
    lastPreviewGrid:null, lastPreviewDemand:'', pollTimer:null, listController:null,
    gridPreviewDemand() {
      assert.equal(document.hidden, false, 'hidden-page heartbeat must retain the last visible demand');
      return {guids:'visible,ahead', visibleGuids:'visible'};
    },
    R:{request(url, options) {
      requests.push({query:new URL(url, 'http://localhost').searchParams, options});
      return release ? new Promise(resolve=>{release.push(resolve);}) : Promise.resolve({ok:1});
    }},
    refreshState:async()=>{refreshes++;}, renderGrid(){}, loadInstalled(){}, previews:{resume(){}},
    setTimeout(fn, ms){timers.set(++next, {fn, ms});return next;}, clearTimeout(id){timers.delete(id);},
    encodeURIComponent, Promise
  };
  vm.createContext(context); vm.runInContext(lifecycle, context);
  return {context, document, requests, timers, pageEvents, documentEvents, refreshes:()=>refreshes,
    hold(){release=[];return release;}, unhold(){release=null;}};
}

test('unfocused and hidden pages keep the preview lease without hidden metadata refreshes', async()=>{
  const h=setup(); await h.context.tick();
  assert.equal(h.requests.at(-1).query.get('on'), '1');
  assert.equal(h.refreshes(), 1);
  h.pageEvents.blur(); await settle();
  assert.equal(h.requests.at(-1).query.get('on'), '1');
  h.document.hidden=true; h.documentEvents.visibilitychange(); await settle();
  const hidden=h.requests.at(-1);
  assert.equal(hidden.query.get('on'), '1');
  assert.equal(hidden.query.get('grid'), 'visible,ahead');
  assert.equal(hidden.query.get('visible'), 'visible');
  assert.equal(h.refreshes(), 1);
  assert.equal([...h.timers.values()].at(-1).ms, 30000);
  const timer=[...h.timers.values()].at(-1);await timer.fn();
  assert.equal(h.requests.at(-1).query.get('on'), '1');
});

test('pagehide releases the lease and a finishing heartbeat cannot restart it', async()=>{
  const h=setup();await h.context.tick();
  const pending=h.hold(), tick=h.context.tick();
  h.pageEvents.pagehide({persisted:true});
  assert.equal(h.requests.at(-1).query.get('on'), '0');
  const count=h.requests.length;
  await h.context.pingActive(true);
  assert.equal(h.requests.length, count);
  pending.forEach(resolve=>resolve({ok:1}));await tick;await settle();
  assert.equal(h.timers.size, 0, 'a heartbeat completing after pagehide must not schedule another');
  assert.equal(h.refreshes(), 1, 'closing the page suppresses the pending metadata refresh');
  h.unhold();h.pageEvents.pageshow({persisted:true});await settle();
  assert.equal(h.requests.at(-1).query.get('on'), '1', 'back-forward restoration renews the lease');
  assert.equal(h.timers.size, 1);
});
