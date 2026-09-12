/* Saved-appearance controls wait for a revision without blocking the browser. */
const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs'), vm=require('node:vm'), path=require('node:path');
const source=fs.readFileSync(path.join(__dirname,'../Packages/dev.gryphprime.avatar-wardrobe/Web/preset-appearance.js'),'utf8');
function node(tag){return {tag,children:[],isConnected:true,dataset:{},setAttribute(){},append(...items){this.children.push(...items);},appendChild(item){this.children.push(item);},replaceChildren(...items){this.children=items;}};}
const tick=()=>new Promise(resolve=>setImmediate(resolve));
function setup(){
 const root=node('section'),timers=new Map(),calls=[];let next=0,scope='common',ready=false;
 const window={},document={hidden:false,createElement:node};
 vm.runInNewContext(source,{window,document,Date,encodeURIComponent,setTimeout(fn){timers.set(++next,fn);return next;},clearTimeout(id){timers.delete(id);}});
 const ui=window.WardrobePresetAppearanceUI.create({root,scope:()=>scope,api:async url=>{calls.push(url);return {ok:1,revision:ready?'current-revision':'',hasSaved:false,message:'References only'};}});
 function buttons(){return root.children[2].children;}
 return {root,calls,timers,document,ui,buttons,ready(){ready=true;},scope(value){scope=value;},async fire(){const [id,fn]=timers.entries().next().value;timers.delete(id);fn();await tick();}};
}
test('Save is disabled until inspection publishes a revision; then submits that revision once',async()=>{
 const h=setup();await h.ui.load();
 let save=h.buttons().find(b=>b.textContent==='Save current appearance');assert.equal(save.disabled,true);
 await save.onclick();assert.equal(h.calls.length,1,'empty revision cannot be submitted even by a stale handler');
 assert.equal(h.timers.size,1);h.ready();await h.fire();
 save=h.buttons().find(b=>b.textContent==='Save current appearance');assert.equal(save.disabled,false);
 assert.equal(h.timers.size,0);await save.onclick();
 assert.equal(h.calls.filter(url=>url.includes('_save')).length,1);
 assert.ok(h.calls.some(url=>url.includes('_save')&&url.includes('revision=current-revision')));
});
test('inspection stops when the component is removed or the preset scope changes',async()=>{
 for(const change of [h=>h.root.isConnected=false,h=>h.scope('other')]){
  const h=setup();await h.ui.load();change(h);await h.fire();
  assert.equal(h.calls.length,1);assert.equal(h.timers.size,0);
 }
});
test('inspection retries are bounded and hidden pages send no polls',async()=>{
 const h=setup();await h.ui.load();h.document.hidden=true;await h.fire();
 assert.equal(h.calls.length,1);h.document.hidden=false;
 for(let i=0;i<30;i++)await h.fire();
 assert.equal(h.calls.length,31);assert.equal(h.timers.size,0);
 assert.equal(h.buttons().find(b=>b.textContent==='Save current appearance').disabled,true);
 assert.ok(h.buttons().find(b=>b.textContent==='Refresh'));
});
