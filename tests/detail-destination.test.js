const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm');
const source=fs.readFileSync('Packages/dev.gryphprime.avatar-wardrobe/Web/wardrobe.js','utf8');
function block(name){const start=source.indexOf('    function '+name+'('),end=source.indexOf('\n    function ',start+1);assert.ok(start>=0&&end>start);return source.slice(start,end);}
const code=['refreshPresets','presetNameOf','validPreset','fillPresetSelect','setDetailReady','loadPresetSelect','createPresetFlow'].map(block).join('\n');
const tick=()=>new Promise(setImmediate);
function setup(api){
 const elements={};for(const id of ['dAddPreset','dTryOn','dRemove','dPresetStatus','dPresetRetry','dPresetFeedback','dNewPreset'])elements[id]={disabled:false};
 const select={value:'casual',isConnected:true,disabled:false,options:[{value:'casual'}],appendChild(option){this.options.push(option);},set innerHTML(_){this.options=[];}};
 const context={api,prompt:()=> 'New preset',toast:()=>{},presetCreated:()=>Promise.resolve(),v:{guid:'outfit'},lastState:{},lastPresets:[{id:'casual',name:'Everyday'}],installedPresets:[],instance:null,effectivePreset:()=> 'casual',presetsReady:true,settingsReady:true,presetLoadToken:0,groupLoadToken:0,detailInstanceId:42,installInFlight:false,T:key=>key,$:id=>elements[id],R:{text:(node,text)=>node.textContent=text},document:{createElement:()=>({})},operations:{enabled:()=>true},paintPresetStatus(){context.settingsReady=true;context.setDetailReady(true);},sel:select};
 vm.createContext(context);vm.runInContext(code,context);return {context,select,elements};
}
test('failed preset reads preserve the chosen destination and prevent Wear/Remove/Try on',async()=>{
 const h=setup(()=>Promise.reject(new Error('offline')));h.context.loadPresetSelect(h.select);await tick();
 assert.equal(h.select.value,'casual');assert.equal(h.select.disabled,true);assert.equal(h.context.presetsReady,false);
 for(const id of ['dAddPreset','dTryOn','dRemove'])assert.equal(h.elements[id].disabled,true);
 assert.equal(h.elements.dPresetRetry.hidden,false);assert.match(h.elements.dPresetStatus.textContent,/offline/);
});
test('retry restores the original preset and enables actions only after verified data',async()=>{
 let online=false;const h=setup(path=>online?Promise.resolve(path==='/api/presets'?{presets:[{id:'casual',name:'Everyday'}]}:{presets:[]}):Promise.reject(new Error('offline')));
 h.context.loadPresetSelect(h.select);await tick();online=true;h.elements.dPresetRetry.onclick();await tick();
 assert.equal(h.select.value,'casual');assert.equal(h.select.disabled,false);assert.equal(h.context.presetsReady,true);assert.equal(h.elements.dAddPreset.disabled,false);
});
test('a missing preset remains explicit instead of falling back to Common',()=>{
 const h=setup(()=>{});h.context.fillPresetSelect(h.select,{presets:[],installedPresets:[]});
 assert.equal(h.select.value,'casual');assert.equal(h.select.options.find(o=>o.value==='casual').disabled,true);assert.equal(h.context.validPreset('casual'),false);
});
test('malformed membership data cannot masquerade as a verified empty list',async()=>{
 const h=setup(path=>Promise.resolve(path==='/api/presets'?{presets:[]}:{ok:0}));h.context.loadPresetSelect(h.select);await tick();
 assert.equal(h.context.presetsReady,false);assert.equal(h.select.value,'casual');assert.equal(h.elements.dAddPreset.disabled,true);
});
test('late responses for an older load cannot replace the current destination',async()=>{
 const pending=[];const h=setup(()=>new Promise(resolve=>pending.push(resolve)));h.context.loadPresetSelect(h.select);h.select.value='other';h.context.loadPresetSelect(h.select);
 pending[2]({presets:[{id:'other',name:'Other'}]});pending[3]({presets:[]});await tick();pending[0]({presets:[{id:'casual',name:'Everyday'}]});pending[1]({presets:[]});await tick();
 assert.equal(h.select.value,'other');assert.equal(h.context.lastPresets[0].id,'other');
});

test('creating a preset invalidates a delayed load and keeps the new destination',async()=>{
 const pending=[];let reads=0;
 const h=setup(path=>{
  if(path.startsWith('/api/preset_save'))return Promise.resolve({ok:1,id:'new',message:'Created'});
  if(reads++<2)return new Promise(resolve=>pending.push(resolve));
  return Promise.resolve(path==='/api/presets'?{presets:[{id:'new',name:'New preset'}]}:{presets:[]});
 });
 h.context.loadPresetSelect(h.select);h.context.createPresetFlow(h.select);await tick();await tick();
 assert.equal(h.select.value,'new');assert.equal(h.context.installInFlight,false);
 pending[0]({presets:[{id:'casual',name:'Everyday'}]});pending[1]({presets:[]});await tick();
 assert.equal(h.select.value,'new');assert.equal(h.context.lastPresets[0].id,'new');assert.equal(h.elements.dAddPreset.disabled,false);
});
test('unapplied item edits are scoped to the current avatar and currently worn copies',()=>{
 const start=source.indexOf('  function unappliedItemEdits('),end=source.indexOf('  function reviewUnappliedItemEdits(',start);
 const context={installedIdentity:'project-avatar',contextKey:'session',detailSettingDrafts:new Map(),installedItems:[{guid:'coat',target:'casual'},{guid:'new',target:'common'}]};
 const draft={worn:true,identity:'project-avatar',guid:'coat',target:'casual'};
 context.detailSettingDrafts.set('current',draft);
 context.detailSettingDrafts.set('other-avatar',{...draft,identity:'other-avatar'});
 context.detailSettingDrafts.set('removed',{...draft,guid:'removed'});
 context.detailSettingDrafts.set('not-worn',{...draft,worn:false,guid:'new',target:'common'});
 vm.createContext(context);vm.runInContext(source.slice(start,end),context);
 assert.equal(context.unappliedItemEdits().length,1);assert.equal(context.unappliedItemEdits()[0],draft);
 context.installedIdentity='other-avatar';assert.equal(context.unappliedItemEdits().length,1);
 context.installedIdentity='third-avatar';assert.equal(context.unappliedItemEdits().length,0);
});
