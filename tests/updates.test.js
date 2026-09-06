const assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm');
const source=fs.readFileSync(require('node:path').join(__dirname,'../Packages/dev.gryphprime.avatar-wardrobe/Web/runtime.js'),'utf8').split('/* Update checks run in the browser, never on Unity\'s editor thread. */')[1];
function setup(fetcher,cache){
 const node={hidden:true},store=new Map(cache?[['wardrobe.vpm.update.v1',JSON.stringify(cache)]]:[]);
 const context={window:{},document:{getElementById:()=>node},Date,Number,Object,JSON,Error,AbortController,setTimeout,clearTimeout,fetch:fetcher,localStorage:{getItem:k=>store.get(k),setItem:(k,v)=>store.set(k,v)}};
 vm.runInNewContext(source,context);return {api:context.window.WardrobeUpdates,node};
}
const t=(key,...v)=>key+v.join(',');
(async()=>{
 let calls=0;
 const {api,node}=setup(async()=>{calls++;return {ok:true,json:async()=>({packages:{'dev.gryphprime.avatar-wardrobe':{versions:Object.fromEntries(['1.0.2','1.0.10','2.0.0-beta.1'].map(v=>[v,{name:'dev.gryphprime.avatar-wardrobe',version:v}]))}}})};});
 assert(api.newer('1.0.10','1.0.2'));assert(!api.newer('1.0.2','1.0.2'));assert(!api.newer('1.0.1','1.0.2'));assert(!api.newer('2.0.0-beta.1','1.0.2'));assert(api.newer('1.0.2','1.0.2-beta.1'));assert(!api.newer('1.0.2',''));
 await api.refresh('1.0.2',t);assert.equal(node.hidden,false);assert(node.title.includes('1.0.10'));await api.refresh('1.0.2',t);assert.equal(calls,1);
 await api.refresh('1.0.10',t);assert.equal(node.hidden,true);
 const cached=setup(()=>{throw Error('should not fetch');},{latest:'1.0.10',checkedAt:Date.now()});cached.api.refresh('1.0.2',t);assert.equal(cached.node.hidden,false);
 let failures=0;const offline=setup(async()=>{failures++;throw Error('offline');});await offline.api.refresh('1.0.2',t);await offline.api.refresh('1.0.2',t);assert.equal(failures,1);assert.equal(offline.node.hidden,true);
 console.log('Update indicator: version ordering, prerelease exclusion, visibility, caching and offline retry checks passed.');
})().catch(e=>{console.error(e);process.exitCode=1;});
