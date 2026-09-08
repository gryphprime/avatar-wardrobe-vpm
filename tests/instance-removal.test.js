const assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm');
const source=fs.readFileSync('Packages/dev.gryphprime.avatar-wardrobe/Web/wardrobe.js','utf8');
const begin=source.indexOf('if(rem) rem.onclick=function(){');
const end=source.indexOf('\n      };',begin)+9;
for(const avatarMode of [false,true]){
 const calls=[];
 const context={rem:{},instance:{guid:'asset',target:'owner-preset',path:'Outfits/Owner/Second copy',instanceId:42},v:{guid:'asset'},avatarMode,
  effectivePreset:()=> 'common',document:{getElementById:id=>({value:id==='dInstance'?'42':'common'})},presetNameOf:()=> 'Owner',
  installedPresets:[{id:'owner-preset',paths:['Outfits/Owner/Second copy'],instanceIds:[42]},{id:'common',paths:[],instanceIds:[]}],confirm:()=>true,removeInFlight:false,
  beginButtonBusy(){},endButtonBusy(){},T:x=>x,encodeURIComponent,operations:{enabled:()=>false},
  api:url=>{calls.push(url);return Promise.resolve({ok:0});},toast(){}};
 vm.runInNewContext(source.slice(begin,end)+'\nrem.onclick();',context);
 assert.equal(calls.length,1);
 const url=new URL(calls[0],'http://localhost');
 assert.equal(url.searchParams.get('target'),'owner-preset');
 assert.equal(url.searchParams.get('instanceId'),'42');
 assert.equal(url.searchParams.get('item'),'Outfits/Owner/Second copy');
}
console.log('PASS: installed-row removal preserves exact owner and copy in both avatar modes.');
