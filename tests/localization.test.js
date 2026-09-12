const {test}=require('node:test');
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
const root=path.resolve(__dirname,'../Packages/dev.gryphprime.avatar-wardrobe');
const data=JSON.parse(fs.readFileSync(root+'/Web/lang.json','utf8'));
const tables=Object.fromEntries(data.langs.map(l=>[l.code,Object.fromEntries(l.strings.map(e=>[e.k,e.v]))]));
const tokens=s=>(s.match(/\{[^{}]+\}/g)||[]).sort();
test('all shipped languages have complete, unique keys and intact interpolation tokens',()=>{
 assert.deepEqual(data.langs.map(l=>l.code),['en','ja','ko','zh']);
 for(const lang of data.langs){
  assert.equal(lang.strings.length,Object.keys(tables[lang.code]).length,'duplicate '+lang.code);
  assert.deepEqual(Object.keys(tables[lang.code]).sort(),Object.keys(tables.en).sort());
  for(const [key,value]of Object.entries(tables[lang.code])){
   assert.ok(value.trim(),lang.code+': '+key);
   assert.deepEqual(tokens(value),tokens(tables.en[key]),lang.code+': '+key);
  }
 }
});
test('static HTML translation hooks all resolve, including accessibile names',()=>{
 const html=fs.readFileSync(root+'/Web/wardrobe.html','utf8');
 for(const match of html.matchAll(/data-i18n(?:-ph|-title|-aria|-alt)?="([^"]+)"/g))assert.ok(tables.en[match[1]],match[1]);
});
test('reported misses use translated labels in Japanese, Korean and Chinese',()=>{
 const keys=['ui.organization','nav.activity','nav.settings','status.connected','status.indexed','ui.installed.items','ui.menu.groups','ui.avatar.workflow','ui.one.avatar','ui.multiple.avatars','ui.upload.avatar','ui.migrate.legacy.presets','ui.regenerate.toggles','detail.removeOutfit'];
 for(const code of ['ja','ko','zh'])for(const key of keys){assert.ok(tables[code][key],key);assert.notEqual(tables[code][key],tables.en[key],code+': '+key);}
});
test('browser language matching supports regional Korean and Chinese tags',()=>{
 const s=fs.readFileSync(root+'/Web/wardrobe.js','utf8'),c={};vm.createContext(c);
 vm.runInContext(s.slice(s.indexOf('  function resolveLang('),s.indexOf('  function loadLangs(')),c);
 for(const [tag,code]of [['ko-KR','ko'],['zh-CN','zh'],['zh-Hans','zh'],['ja-JP','ja']])assert.equal(c.resolveLang('',[tag],Object.keys(tables)),code);
 assert.equal(c.resolveLang('ko',['ja-JP'],Object.keys(tables)),'ko');
});
test('all same-origin runtime API requests carry the current language',async()=>{
 const requests=[],events={};const location={href:'http://localhost:8765/',origin:'http://localhost:8765'};
 const window={addEventListener:(name,fn)=>events[name]=fn,crypto:{randomUUID:()=> 'request'},dispatchEvent(){}};
 const context={window,location,document:{addEventListener(){}},URL,AbortController,setTimeout,clearTimeout,Map,Set,Promise,CustomEvent:function(){},fetch:async(url)=>{requests.push(new URL(url));return {ok:true,status:200,text:async()=>'{"ok":1}'};}};
 vm.runInNewContext(fs.readFileSync(root+'/Web/runtime.js','utf8'),context);
 const R=window.WardrobeRuntime;
 R.setLanguage('ko',k=>tables.ko[k]||k);await R.request('/api/batch_state');assert.equal(requests.at(-1).searchParams.get('lang'),'ko');
 R.setLanguage('zh',k=>tables.zh[k]||k);await R.request('/api/installed');assert.equal(requests.at(-1).searchParams.get('lang'),'zh');
 await R.request('/api/installed?lang=ja');assert.equal(requests.at(-1).searchParams.get('lang'),'ja');
 await R.request('https://example.com/state');assert.equal(requests.at(-1).searchParams.has('lang'),false);
 assert.equal(R.localize('detail.removeOutfit','Remove Outfit'),tables.zh['detail.removeOutfit']);
});
