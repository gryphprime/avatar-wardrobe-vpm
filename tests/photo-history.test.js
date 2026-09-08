const assert=require('node:assert/strict'),fs=require('fs'),vm=require('vm');
class Element {
 constructor(tag){this.tag=tag;this.children=[];this.isConnected=true;this.dataset={};this.textContent='';this.hidden=false;this.open=false;}
 append(...children){this.children.push(...children);}appendChild(child){this.children.push(child);return child;}
 replaceChildren(){this.children=[];}setAttribute(key,value){this[key]=value;}addEventListener(){}
 showModal(){this.open=true;}close(){this.open=false;}
}
const listeners=new Map(),window={addEventListener:(name,fn)=>listeners.set(name,fn),removeEventListener:name=>listeners.delete(name)};
vm.runInNewContext(fs.readFileSync('Packages/dev.gryphprime.avatar-wardrobe/Web/photo-history.js','utf8'),{window,document:{createElement:tag=>new Element(tag)},JSON,Date,Error,encodeURIComponent});
let context={projectId:'project',sceneGuid:'scene',avatarId:'avatar',scopeId:'common'},scope='common',calls=[],hold=null;
const key='a'.repeat(64),other='b'.repeat(64),items=[{snapshotKey:key,pinned:true,completed:1,view:'front',before:false,target:{...context},sourceRevision:'rev'},
 {snapshotKey:other,pinned:true,completed:2,view:'back',before:false,target:{...context,avatarId:'<img src=x>'},sourceRevision:'old'}];
const api=async(path,options)=>{calls.push({path,options});if(hold)return hold(path);if(path.startsWith('/api/shadow/photo?'))return {ok:1,...items[0]};
 if(path==='/api/shadow/pin'){const body=JSON.parse(options.body);assert.equal(body.pinned,false);items.splice(items.findIndex(item=>item.snapshotKey===body.key),1);return {ok:1};}
 return {ok:1,projectId:'project',items:items.slice(),nextOffset:null};};
const root=new Element('section'),ui=window.WardrobePhotoHistoryUI.create({root,api,context:()=>context,scope:()=>scope});
const all=node=>[node,...node.children.flatMap(all)],find=label=>all(root).find(node=>node.textContent===label),flush=()=>new Promise(resolve=>setImmediate(resolve));
(async()=>{
 await ui.refresh();assert.equal(calls.length,1);assert(calls[0].path.includes('pinned=1&limit=12'));
 assert(find('Avatar <img src=x> · Preset common'),'source labels remain literal');assert(all(root).some(node=>node.textContent.startsWith('Another avatar or preset')));
 const exportLink=find('Export PNG');assert.equal(exportLink.href,'/api/shadow/export?key='+key);assert.equal(exportLink.download,'wardrobe-photo-'+key.slice(0,12)+'.png');
 find('Open photograph').onclick();await flush();assert.equal(root.children[1].open,true);assert(find('Historical photograph. Opening it does not change or apply anything to the avatar.'));assert(!calls.some(call=>call.path.includes('appearance_apply')||call.path.includes('/api/operations')));
 const secondRoot=new Element('section'),reloaded=window.WardrobePhotoHistoryUI.create({root:secondRoot,api,context:()=>context,scope:()=>scope});await reloaded.refresh();assert.equal(all(secondRoot).filter(node=>node.textContent==='Open photograph').length,2,'kept photos are loaded from durable server history after page recreation');
 find('Unkeep photograph').onclick();await flush();await flush();assert(calls.some(call=>call.path==='/api/shadow/pin'&&JSON.parse(call.options.body).key===key));assert.equal(items[0].snapshotKey,other,'unkeep targets only the exact photo');
 context={...context,avatarId:'new-avatar'};ui.contextChanged();await flush();assert.equal(root.children[1].open,false,'target changes close a prior preview');
 let waiting=[];hold=()=>new Promise(resolve=>waiting.push(resolve));const old=ui.refresh();scope='new-scope';ui.contextChanged();hold=null;waiting[0]({ok:1,projectId:'foreign',items:[],nextOffset:null});await old;waiting[1]({ok:1,projectId:'project',items:items.slice(),nextOffset:null});await flush();
 assert(!all(root).some(node=>node.textContent==='foreign'),'late history cannot replace a different scope');ui.destroy();reloaded.destroy();
 console.log('photo history: durable reload, literal provenance, exact unkeep/export, read-only open and scope guards passed');
})().catch(error=>{console.error(error);process.exitCode=1;});
