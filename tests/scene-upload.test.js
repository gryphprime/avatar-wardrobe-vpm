const assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm');
const source=fs.readFileSync(require('node:path').resolve(__dirname,'../Packages/dev.gryphprime.avatar-wardrobe/Web/wardrobe.js'),'utf8');
const snippet=source.slice(source.indexOf('  // Direct scene-avatar upload'),source.lastIndexOf('})();'));
function setup(review){const elements={},requests=[],timers=[];let result={pending:1,message:'Building'};
 const $=id=>elements[id]||(elements[id]={dataset:{},removeAttribute(name){delete this[name]},value:'',hidden:false,disabled:false,textContent:'',showModal(){this.open=true},close(){this.open=false},addEventListener(){}});
 const ctx={$,api:async path=>{requests.push(path);return path.includes('review')?review:path.includes('upload_result')?result:{ok:1,job:'test-job'}},setTimeout:f=>{timers.push(f)},refreshState(){},encodeURIComponent};
 vm.runInNewContext(snippet,ctx);return {$,requests,timers,setResult:r=>result=r};}
(async()=>{
 let t=setup({ok:1,avatarId:12,name:'Common only',blueprintId:'',isNew:true});
 await t.$('sceneUpload').onclick();assert.equal(t.requests.length,1,'Opening review must not upload');
 await t.$('sceneBuildCheck').onclick();await new Promise(r=>setImmediate(r));
 assert(t.requests.some(p=>p.includes('/api/scene_upload?')&&p.includes('check=1')&&p.includes('avatarId=12')));
 assert(!t.requests.some(p=>p.includes('check=0')));
 assert(t.$('sceneUploadConfirm').disabled);t.setResult({ok:1,message:'Build check passed'});await t.timers.shift()();
 assert(t.$('sceneUploadConfirm').disabled,'Require fresh review after completed job');
 t=setup({ok:1,avatarId:12,name:'Common only',blueprintId:'avtr-existing',isNew:false});await t.$('sceneUpload').onclick();
 assert(t.$('sceneUploadNameWrap').hidden);await t.$('sceneUploadConfirm').onclick();assert(!t.requests.some(p=>p.includes('check=0')),'Upload requires fresh consent');t.$('sceneUploadConsent').checked=true;await t.$('sceneUploadConfirm').onclick();await new Promise(r=>setImmediate(r));
 assert(t.requests.some(p=>p.includes('check=0')&&p.includes('consent=1')&&p.includes('blueprintId=avtr-existing')));
 t=setup({ok:0,message:'Save untitled scenes first'});await t.$('sceneUpload').onclick();assert(t.$('sceneBuildCheck').disabled&&t.$('sceneUploadConfirm').disabled);
 console.log('PASS: review-only opening, build-only request, explicit upload, reviewed avatar/Blueprint ID, busy state, fresh-review requirement, readiness failure.');
})().catch(e=>{console.error(e);process.exitCode=1});
