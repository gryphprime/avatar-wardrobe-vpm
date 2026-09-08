const assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm');
const source=fs.readFileSync('Packages/dev.gryphprime.avatar-wardrobe/Web/snapshots.js','utf8');
class Element {
 constructor(){this.children=[];this.hidden=false;this.dataset={};this.textContent='';}
 appendChild(child){this.children.push(child);return child;}
 replaceChildren(){this.children=[];}
 setAttribute(key,value){this[key]=value;}
}
const root=new Element(),elements=new Map(),views=['front','three-quarter','back'].map(view=>{const e=new Element();e.dataset.snapshotView=view;return e;});
root.querySelector=selector=>{if(!elements.has(selector))elements.set(selector,new Element());return elements.get(selector);};
root.querySelectorAll=()=>views;
let sequence=0,scope='common',context={avatarInstanceId:1,session:'s',revision:'r1'},pending=[],submitted=[],cancelled=[],shadowCancelled=[],holdSubmit=null,holdShadow=null;
const targetKey=x=>[x.avatarInstanceId,x.session,x.scopeId].join('|');
const terminal=state=>['succeeded','failed','cancelled'].includes(state);
const ops={context:()=>context,pending:()=>pending,build:(type,input)=>({id:String(++sequence),type,target:{...context,scopeId:input.scopeId||scope}}),
 submit:async command=>{submitted.push(command);if(holdSubmit)return holdSubmit(command);return {id:command.id,state:'succeeded',result:{confirmedRevision:'r1'}};},
 cancel:async id=>{cancelled.push(id);},wait:async()=>{throw Error('Unexpected wait');}};
const window={WardrobeOperations:{targetKey,isTerminal:terminal}};
const preview={beforeMetrics:{triangles:10},afterMetrics:{triangles:20,largestTextures:[{name:'<img src=x>',width:2,height:2,estimatedBytes:16}]},menuControls:['Coats/Blue'],parameters:['Outfit'],limitations:['Static fixture.']};
const api=async(path,options)=>{
 if(path==='/api/shadow/cancel'){shadowCancelled.push(JSON.parse(options.body).id);return {};}
 if(path==='/api/shadow/submit'){if(holdShadow)return holdShadow();return {id:'job',state:'succeeded',snapshotKey:'photo',preview};}
 throw Error(path);
};
class Image {set src(value){this.url=value;queueMicrotask(()=>this.onload());}}
vm.runInNewContext(source,{window,document:{createElement:()=>new Element()},Image,Map,Set,Promise,Number,Math,JSON,Error,setTimeout});
const ui=window.WardrobeSnapshots.create({operations:ops,api,root,scope:()=>scope});
const flush=()=>new Promise(resolve=>setImmediate(resolve));
(async()=>{
 await ui.begin({variantId:'a',scopeId:'common'});
 assert.equal(root.querySelector('img').hidden,false);assert.equal(root.querySelector('[data-snapshot-wear]').disabled,false);
 assert.match(root.querySelector('[data-snapshot-caption]').textContent,/Try-on/);
 const texts=e=>[e.textContent,...e.children.flatMap(texts)];assert(texts(root.querySelector('[data-snapshot-metrics]')).some(x=>x.includes('<img src=x>')),'texture names stay literal text');
 scope='another-preset';ui.contextChanged();assert.equal(root.querySelector('img').hidden,true,'a photo cannot appear under another preset');assert.equal(root.querySelector('[data-snapshot-metrics]').hidden,true,'costs cannot appear under another preset');
 scope='common';pending=[{command:{target:{avatarInstanceId:1,session:'s'}}}];ui.contextChanged();assert.equal(root.querySelector('[data-snapshot-wear]').disabled,true,'pending mutation blocks stale photo apply');pending=[];
 let release;holdSubmit=command=>new Promise(resolve=>{release=()=>resolve({id:command.id,state:'succeeded',result:{confirmedRevision:'r1'}});});
 const old=ui.begin({variantId:'b'});await flush();const oldId=submitted.at(-1).id;ui.discard();release();await old;assert(cancelled.includes(oldId),'discard requests capture cancellation even when acceptance arrives late');holdSubmit=null;
 let releaseShadow;holdShadow=()=>new Promise(resolve=>{releaseShadow=resolve;});const late=ui.begin({variantId:'c'});await flush();assert.equal(root.querySelector('[data-snapshot-wear]').disabled,true,'Wear waits for the current draft photograph, not merely capture acceptance');ui.discard();releaseShadow({id:'late-job',state:'queued'});await late;await flush();assert(shadowCancelled.includes('late-job'),'late accepted render is cancelled');assert.notEqual(ui.current().draft.input.variantId,'c','late photograph never replaces previous image');holdShadow=null;
 await ui.begin({variantId:'d'});root.querySelector('[data-snapshot-zoom]').value='1.25';await root.querySelector('[data-snapshot-zoom]').onchange();await flush();assert.match(root.querySelector('[data-snapshot-caption]').textContent,/125%/);
 const releases=[];holdShadow=()=>new Promise(resolve=>releases.push(resolve));
 const oldRender=ui.begin({variantId:'race'});await flush();views[2].onclick();await flush();
 releases[1]({id:'new-render',state:'queued'});await flush();releases[0]({id:'old-render',state:'queued'});await oldRender;await flush();ui.discard();await flush();
 assert(shadowCancelled.includes('old-render'));assert(shadowCancelled.includes('new-render'),'late acceptance must not erase the newer render cancellation handle');holdShadow=null;
 console.log('snapshots: target/preset freshness, pending edits, discard/acceptance races, literal metadata and zoom passed');
})().catch(error=>{console.error(error);process.exitCode=1;});

function lifecycleFixture(){
 const state={context:{projectId:'project',sceneGuid:'scene',avatarId:'avatar',avatarInstanceId:1,session:'session',revision:'r1',visualRevision:'v1'},scope:'common',pending:[],submitted:[],requests:[],items:[],photos:new Map(),holdPhoto:null,holdRender:null,kept:0,historyRefreshes:0};
 const host=new Element(),nodes=new Map(),buttons=['front','three-quarter','back'].map(view=>{const e=new Element();e.dataset.snapshotView=view;return e;});
 host.querySelector=selector=>{if(!nodes.has(selector))nodes.set(selector,new Element());return nodes.get(selector);};host.querySelectorAll=()=>buttons;
 const key=target=>JSON.stringify([target.projectId,target.sceneGuid,target.avatarId,target.avatarInstanceId,target.session,target.scopeId]);
 const operations={context:()=>state.context,pending:()=>state.pending,
  build:(type,input)=>({id:String(state.submitted.length+1),type,target:{...state.context,scopeId:input.scopeId||state.scope},precondition:{observedRevision:state.context.revision},payload:{...input}}),
  submit:async command=>{state.submitted.push(command);return {id:command.id,state:'succeeded',result:{confirmedRevision:command.precondition.observedRevision,visualRevision:state.context.visualRevision}};},
  cancel:async()=>{},wait:async()=>{throw Error('Unexpected wait');}};
 const api=async(path,request)=>{
  state.requests.push(path);
  if(path.startsWith('/api/shadow/history?'))return {items:state.items};
  if(path.startsWith('/api/shadow/photo?')){if(state.holdPhoto)return state.holdPhoto();return state.photos.get(path.split('key=')[1]);}
  if(path==='/api/shadow/submit'){
   const input=JSON.parse(request.body),record={id:'job-'+input.operationId,state:'succeeded',snapshotKey:input.operationId.padStart(64,'0'),preview:{...preview,parameterProblems:['Missing parameter: Outfit']}};
   if(state.holdRender)return state.holdRender(record);return record;
  }
  if(path==='/api/shadow/pin'){state.kept++;return {};}
  if(path==='/api/shadow/cancel')return {};
  throw Error(path);
 };
 const win={WardrobeOperations:{targetKey:key,isTerminal:terminal,isMutation:type=>['wear-outfit','replace-outfit','remove-outfit','undo-operation'].includes(type)},WardrobePhotoHistoryUI:{create:()=>({refresh:()=>state.historyRefreshes++,contextChanged:()=>{}})}};
 vm.runInNewContext(source,{window:win,document:{createElement:()=>new Element()},Image,Map,Set,Promise,Number,Math,JSON,Error,setTimeout});
 const ui=win.WardrobeSnapshots.create({operations,api,root:host,scope:()=>state.scope});
 const succeeded=(revision,type='wear-outfit')=>({id:'mutation-'+revision,type,state:'succeeded',command:{target:{...state.context,scopeId:state.scope}},result:{confirmedRevision:revision}});
 const photo=(overrides={})=>({snapshotKey:'a'.repeat(64),target:{...state.context,scopeId:state.scope},confirmedRevision:state.context.revision,sourceRevision:'source',visualRevision:state.context.visualRevision,input:{scopeId:state.scope},view:'three-quarter',before:false,zoom:1,preview,...overrides});
 return {state,ui,host,buttons,succeeded,photo};
}

(async()=>{
 let f=lifecycleFixture();await f.ui.begin({variantId:'candidate',scopeId:'common'});
 const failed={...f.succeeded('r2'),state:'failed'};f.ui.mutationSettled(failed);await flush();assert.equal(f.state.submitted.length,1,'failed mutations never capture a photo');
 f.ui.mutationSettled(f.succeeded('r2'));await flush();assert.equal(f.state.submitted.length,1,'success waits for its confirmed context revision');
 f.state.context={...f.state.context,revision:'r2',visualRevision:'v2'};
 f.state.pending=[{command:{target:{...f.state.context,scopeId:'common'}}}];f.ui.contextChanged();await flush();assert.equal(f.state.submitted.length,1,'queued edits prevent intermediate automatic photographs');
 f.ui.mutationSettled(f.succeeded('r3','remove-outfit'));
 f.state.context={...f.state.context,revision:'r3',visualRevision:'v3'};f.state.pending=[];f.ui.contextChanged();f.ui.contextChanged();await flush();await flush();
 assert.equal(f.state.submitted.length,2,'one latest confirmed capture replaces a run of scene edits');
 assert.equal(f.state.submitted[1].payload.variantId,undefined,'automatic current-avatar capture must not wear the try-on candidate a second time');
 assert.equal(f.state.submitted[1].precondition.observedRevision,'r3');assert.match(f.host.querySelector('[data-snapshot-caption]').textContent,/Current avatar/);
 assert.equal(f.ui.current().draft.input.variantId,undefined);
 const metrics=e=>[e.textContent,...e.children.flatMap(metrics)];assert(metrics(f.host.querySelector('[data-snapshot-metrics]')).includes('Missing parameter: Outfit'));
 await f.host.querySelector('[data-snapshot-pin]').onclick();assert.equal(f.state.kept,1);assert.equal(f.state.historyRefreshes,1,'keeping a photo refreshes the mounted history module');

 f=lifecycleFixture();await f.ui.begin({scopeId:'common'});const original=f.ui.current();let release;
 f.state.holdRender=record=>new Promise(resolve=>{release=()=>resolve(record);});
 f.state.context={...f.state.context,revision:'r2'};f.ui.mutationSettled(f.succeeded('r2'));await flush();
 f.state.context={...f.state.context,revision:'r3'};f.ui.contextChanged();release();await flush();await flush();
 assert.equal(f.ui.current(),original,'an automatic image from an older revision cannot replace the last photo');
 f.state.holdRender=null;f.ui.mutationSettled(f.succeeded('r3'));await flush();await flush();assert.equal(f.ui.current().draft.receipt.result.confirmedRevision,'r3');

 f=lifecycleFixture();await f.ui.begin({scopeId:'common'});f.ui.mutationSettled(f.succeeded('r2'));
 await f.ui.begin({variantId:'explicit-next',scopeId:'common'});f.state.context={...f.state.context,revision:'r2'};f.ui.contextChanged();await flush();assert.equal(f.state.submitted.length,2,'a newer manual try-on supersedes delayed automatic refresh');
 f.ui.mutationSettled(f.succeeded('r3'));f.state.context={...f.state.context,avatarInstanceId:2,avatarId:'other-avatar',revision:'r3'};f.ui.contextChanged();await flush();assert.equal(f.state.submitted.length,2,'target switches cancel an old-target automatic refresh');

 f=lifecycleFixture();const saved=f.photo();f.state.items=[saved];f.state.photos.set(saved.snapshotKey,saved);f.ui.contextChanged();await flush();await flush();
 assert(f.ui.current().draft.restored);assert.match(f.host.querySelector('[data-snapshot-caption]').textContent,/Restored photo/);assert.equal(f.state.submitted.length,0,'restoring a photo never submits a capture or mutation');assert.equal(f.host.querySelector('[data-snapshot-wear]').disabled,true,'history cannot restore a Wear capability');
 f.buttons[0].onclick();await flush();await flush();assert.equal(f.state.submitted.length,1,'changing a restored view prepares a fresh current-avatar capture');

 f=lifecycleFixture();const stale=f.photo({target:{...f.state.context,session:'old-session',avatarInstanceId:7,scopeId:'common'},confirmedRevision:'old-revision',visualRevision:'old-visual'});
 f.state.items=[f.photo({snapshotKey:'b'.repeat(64),target:{...stale.target,projectId:'other-project'}}),stale];f.state.photos.set(stale.snapshotKey,stale);f.ui.contextChanged();await flush();await flush();
 assert.equal(f.ui.current().key,stale.snapshotKey);assert.equal(f.host.querySelector('img').hidden,false);assert.match(f.host.querySelector('[data-snapshot-caption]').textContent,/out of date/,'same saved avatar across sessions is explicitly stale');

 f=lifecycleFixture();const lateSaved=f.photo();f.state.items=[lateSaved];let releasePhoto;f.state.holdPhoto=()=>new Promise(resolve=>{releasePhoto=resolve;});f.ui.contextChanged();await flush();
 await f.ui.begin({variantId:'new-manual',scopeId:'common'});const explicit=f.ui.current();releasePhoto(lateSaved);await flush();await flush();assert.equal(f.ui.current(),explicit,'late restore cannot replace a manual try-on');
 f=lifecycleFixture();const wrongKey=f.photo();f.state.items=[wrongKey];f.state.photos.set(wrongKey.snapshotKey,{...wrongKey,snapshotKey:'c'.repeat(64)});f.ui.contextChanged();await flush();await flush();assert.equal(f.ui.current(),null,'history detail must match the requested photograph identity');
 f=lifecycleFixture();const oldTarget=f.photo();f.state.items=[oldTarget];let releaseTarget;f.state.holdPhoto=()=>new Promise(resolve=>{releaseTarget=resolve;});f.ui.contextChanged();await flush();f.state.context={...f.state.context,avatarId:'switched',avatarInstanceId:2};f.ui.contextChanged();releaseTarget(oldTarget);await flush();await flush();assert.equal(f.ui.current(),null,'late restoration cannot cross a target switch');
 f=lifecycleFixture();f.state.items=[f.photo({input:{variantId:'speculative-candidate'}}),f.photo({input:undefined})];f.ui.contextChanged();await flush();await flush();assert.equal(f.ui.current(),null,'candidate and unclassified legacy photographs remain history-only');
 f=lifecycleFixture();f.state.context=null;f.ui.mutationSettled({...failed,state:'succeeded'});f.state.context={projectId:'project',sceneGuid:'scene',avatarId:'avatar',avatarInstanceId:1,session:'session',revision:'r2'};f.ui.contextChanged();await flush();assert.equal(f.state.submitted.length,0,'loading historical receipts before context does not trigger capture');
 console.log('snapshots: confirmed mutation refresh, latest revision/target guards, readonly reload restore, stale provenance and history integration passed');
})().catch(error=>{console.error(error);process.exitCode=1;});
