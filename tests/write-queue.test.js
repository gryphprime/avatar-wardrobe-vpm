// Test the actual shared request helper with a deferred Unity write receipt.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
function setup(replies) {
  const calls=[], events=[];
  const window={crypto:require('node:crypto').webcrypto,dispatchEvent:event=>events.push(event.detail),localStorage:{getItem(){return null;}}};
  const context={window,document:{addEventListener(){}},location:{href:'http://localhost:8765/',origin:'http://localhost:8765'},
    URL,AbortController,CustomEvent:class{constructor(type,init){this.type=type;this.detail=init.detail;}},
    setTimeout:(fn,ms)=>setTimeout(fn,ms===500?0:ms),clearTimeout,
    fetch:async(url,init)=>{calls.push({url,init});const next=replies.shift();if(next instanceof Error)throw next;
      assert.ok(next,'Unexpected request');return {status:next.status||200,ok:!next.status||next.status<400,text:async()=>JSON.stringify(next.body)};}};
  vm.runInNewContext(fs.readFileSync(path.join(__dirname,'../Packages/dev.gryphprime.avatar-wardrobe/Web/runtime.js'),'utf8'),context);
  window.WardrobeRuntime.setContext('session',42);
  return {runtime:window.WardrobeRuntime,calls,events};
}
(async()=>{
  let t=setup([{status:202,body:{writeJob:'session:job'}},{body:{state:'running'}},{body:{state:'completed',result:{ok:1,message:'Applied'}}}]);
  let result=await t.runtime.request('/api/regenerate_toggles');
  assert.equal(result.message,'Applied');
  assert.equal(t.calls[0].init.headers['X-Wardrobe-Queue'],'1');
  assert.equal(t.calls[0].init.headers['X-Wardrobe-Avatar'],'42');
  assert.equal(t.calls.filter(c=>c.init.method==='POST').length,1);
  assert.equal(t.calls[1].init.method,'GET');
  assert.deepEqual(t.events.map(e=>e.pending),[1,0]);
  t=setup([{status:202,body:{writeJob:'job'}},{body:{state:'failed',error:'Avatar changed'}}]);
  await assert.rejects(t.runtime.request('/api/menu_execute'),/Avatar changed/);
  assert.equal(t.events.at(-1).pending,0);
  t=setup([{status:202,body:{writeJob:'job'}},new Error('Disconnected'),{body:{state:'completed',result:{ok:1,message:'Recovered'}}}]);
  assert.equal((await t.runtime.request('/api/install')).message,'Recovered','Transient receipt failures must resume polling');
  assert.equal(t.calls.filter(c=>c.init.method==='POST').length,1,'Never automatically repeat an uncertain mutation');
  t=setup([{body:{ok:1}}]);
  assert.equal((await t.runtime.request('/api/part_toggles')).ok,1,'Older bridge compatibility');
  t=setup([{status:202,body:{writeJob:'item-settings'}},{body:{state:'completed',result:{ok:1}}}]);
  const settings=t.runtime.request('/api/item_settings?guid=asset&target=preset&group=group',{method:'POST'});
  assert.ok(t.runtime.pendingWrites()>0,'outbound item settings block uploads before receipt acceptance');
  assert.ok(t.calls[0].init.headers['X-Wardrobe-Write-Id'],'item settings use a durable identity');
  assert.equal((await settings).ok,1);assert.equal(t.runtime.pendingWrites(),0);
  console.log('Browser write receipt, failure, uncertain acceptance, and compatibility checks passed');
})().catch(error=>{console.error(error);process.exitCode=1;});
