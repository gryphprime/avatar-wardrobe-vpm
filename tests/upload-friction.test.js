const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const english=Object.fromEntries(JSON.parse(fs.readFileSync(require('node:path').resolve(__dirname,'../Packages/dev.gryphprime.avatar-wardrobe/Web/lang.json'),'utf8')).langs.find(l=>l.code==='en').strings.map(e=>[e.k,e.v]));
const translate=(key,...args)=>{let value=english[key]||key;args.forEach((arg,i)=>{value=value.split('{'+i+'}').join(arg)});return value;};
const source = fs.readFileSync('Packages/dev.gryphprime.avatar-wardrobe/Web/upload.js', 'utf8');
const scope = { window: {} };
vm.runInNewContext(source.slice(0, source.indexOf('  global.WardrobeUpload=function')) + '\n})(window);', scope);
const models = scope.window.WardrobeUploadModels;
const clone = value => JSON.parse(JSON.stringify(value));
const storage = new Map();
const browserStorage = { getItem:key => storage.get(key), setItem:(key,value) => storage.set(key,value), removeItem:key => storage.delete(key) };
const first = models.draftIdentity({projectId:'/project-a', avatarId:'GlobalObjectId-avatar-1', avatarName:'Shinano'}, 'session-one');
const reloaded = models.draftIdentity({projectId:'/project-a', avatarId:'GlobalObjectId-avatar-1', avatarName:'Renamed'}, 'session-two');
const sameNameOtherAvatar = models.draftIdentity({projectId:'/project-a', avatarId:'GlobalObjectId-avatar-2', avatarName:'Shinano'}, 'session-one');
const otherProject = models.draftIdentity({projectId:'/project-b', avatarId:'GlobalObjectId-avatar-1'}, 'session-one');
let drafts = models.createDraftStore(browserStorage);
const draft = {defaults:{upDname:'My unsaved {preset}', 'tag:content_sex':true}, blueprints:{p1:'avtr_draft'}};
drafts.write(first, draft);
draft.defaults.upDname = 'Mutation after snapshot';
assert.equal(drafts.read(reloaded).defaults.upDname, 'My unsaved {preset}', 'Saved-scene draft survives a session change and rename.');
assert.equal(drafts.read(sameNameOtherAvatar), null, 'Same-named avatars must never share drafts.');
assert.equal(drafts.read(otherProject), null, 'Projects must never share drafts.');
drafts = models.createDraftStore(browserStorage);
assert.deepEqual(clone(drafts.read(reloaded).blueprints), {p1:'avtr_draft'}, 'Avatar ID drafts survive page reload within the tab.');
drafts.write(first, null);
assert.equal(drafts.read(first), null, 'Saving or discarding removes the stored draft.');
const unsaved = models.draftIdentity({projectId:'/project-a', avatarId:'session:s1:10'}, 's1');
drafts.write(unsaved, {defaults:{upDname:'Unsaved scene'}});
assert.equal(unsaved.persistent, false);
assert.equal(models.createDraftStore(browserStorage).read(unsaved), null, 'Unstable scene identities are never restored into another session.');
const noStorage = models.createDraftStore({getItem(){throw Error('denied')},setItem(){throw Error('denied')},removeItem(){throw Error('denied')}});
noStorage.write(first, {defaults:{upDname:'Memory draft'}});
assert.equal(noStorage.read(first).defaults.upDname, 'Memory draft', 'Unavailable browser storage must retain the in-memory draft.');
for(const path of [
 '/api/preset_include?id=p&include=1','/api/batch_preset_config?id=p&win=1','/api/batch_preset_config?id=p&blueprint=',
 '/api/batch_preset_items?id=p&item=Glasses&include=1','/api/batch_preset_blends?id=p&bs=Smile&pinned=1&weight=50',
 '/api/batch_preset_blends?id=p&op=capture','/api/batch_preset_faceemo?id=p&op=clear',
 '/api/menu_groups?id=p&op=assign&group=g','/api/menu_groups?id=p&op=save',
 '/api/batch_defaults_set?autoFix=1','/api/batch_config_set?versionMode=1','/api/batch_item?op=default&item=Glasses&include=0','/api/batch_import'
]) assert.equal(models.uploadConfigWrite(path),true,'Upload must wait for '+path);
for(const path of ['/api/batch_state','/api/installed','/api/batch_item','/api/menu_groups?id=p','/api/batch_preset_blends?id=p','/api/batch_preset_items?id=p','/api/batch_preset_faceemo?id=p'])
 assert.equal(models.uploadConfigWrite(path),false,'Reading configuration must not block its review: '+path);
const p1={id:'one',name:'Everyday <blue>',include:1,win:1,and:1,ios:0,blueprintId:'avtr_12345678-1234-1234-1234-123456789012',members:[]};
const p2={id:'two',name:'Evening',include:1,win:1,and:0,ios:0,blueprintId:'',members:[]};
const initial={ok:1,avatarRoot:'Shinano',presets:[p1,p2],defaults:{release:'private'},batchActive:0};
assert.throws(()=>models.presetReview(initial,['missing'],'Shinano'), /no longer available/);
assert.throws(()=>models.presetReview({...initial,presets:[{...p1,win:0,and:0}]},['one'],'Shinano'), /at least one platform/);
assert.throws(()=>models.presetReview({...initial,presets:[{...p1,blueprintId:'not-an-avatar'}]},['one'],'Shinano'), /valid Avatar ID/);
assert.throws(()=>models.presetReview({...initial,batchActive:1},['one'],'Shinano'), /already running/);
function reviewHarness(){
  const elements={};const errors=[],dialogs=[],uploads=[];let state=clone(initial);
  const element=id => elements[id]||(elements[id]={isConnected:true,disabled:false,textContent:''});
  const context={options:{},presetReview:models.presetReview,pendingConfigWrites:0,defaultsDirty:false,upBlueprintDrafts:{},sdkReady:true,upRunning:false,
    contextRevision:1,reviewAvatarName:'Shinano <scene>',upRefreshState:async()=>state,paintDraftStatus(){},
    toast:(message)=>errors.push(message),esc:value=>String(value).replaceAll('&','&amp;').replaceAll('<','&lt;').replaceAll('>','&gt;'),
    upOpenModal:(title,html)=>{dialogs.push({title,html});elements.upReviewConfirm={isConnected:true,disabled:false};elements.upReviewMessage={textContent:''};},
    upEl:element,closeUploadModal(){},upStartJob:(...args)=>uploads.push(args),T:key=>key,U:(key,...args)=>{let value=scope.window.WardrobeUploadCopy['upload.'+key];args.forEach((arg,i)=>{value=value.replaceAll('{'+i+'}',arg)});return value;},encodeURIComponent};
  vm.runInNewContext(source.slice(source.indexOf('  function uploadEditBlocker('),source.indexOf('  var upContentTags')),context);
  return {context,errors,dialogs,uploads,element,setState:next=>{state=next}};
}
(async()=>{
  let resolveWrite;
  const apiContext={T:translate,request:()=>new Promise(resolve=>{resolveWrite=resolve}),contextRevision:1,pendingConfigWrites:0,uploadConfigWrite:models.uploadConfigWrite};
  vm.runInNewContext(source.slice(source.indexOf('    function api('),source.indexOf('    var T=')),apiContext);
  const saving=apiContext.api('/api/batch_preset_items?id=p&item=Glasses&include=1');
  assert.equal(apiContext.pendingConfigWrites,1,'A fire-and-forget caller still blocks upload while its request is pending.');
  resolveWrite({ok:1});await saving;assert.equal(apiContext.pendingConfigWrites,0);
  const changedContext=apiContext.api('/api/batch_preset_blends?id=p&bs=Smile&weight=10');apiContext.contextRevision++;
  resolveWrite({ok:1});await assert.rejects(changedContext,/Avatar changed/);assert.equal(apiContext.pendingConfigWrites,0,'Context rejection must release the pending counter.');
  let test=reviewHarness();
  await test.context.openUploadReview(['one','two'],true);
  assert.equal(test.uploads.length,0,'Opening a review must never upload.');
  assert.match(test.dialogs[0].html,/Shinano &lt;scene&gt;/);
  assert.match(test.dialogs[0].html,/Everyday &lt;blue&gt;/);
  assert.match(test.dialogs[0].html,/Windows, Android/);
  assert.match(test.dialogs[0].html,/Update existing avatar/);
  assert.match(test.dialogs[0].html,/Create a new VRChat avatar/);
  assert.match(test.dialogs[0].html,/Cancel stops the remaining queue/);
  await test.element('upReviewConfirm').onclick();
  assert.equal(test.uploads.length,1);
  assert.match(test.uploads[0][0],/ids=one%0Atwo/);
  assert.equal(test.uploads[0][2],true);
  for(const [name,value] of [['defaultsDirty',true],['upBlueprintDrafts',{one:'draft'}],['pendingConfigWrites',1],['upRunning',true]]){
    test=reviewHarness();test.context[name]=value;await test.context.openUploadReview(['one']);
    assert.equal(test.dialogs.length,0,name+' must block a review until resolved.');assert.equal(test.uploads.length,0);
  }
  test=reviewHarness();await test.context.openUploadReview(['one']);
  test.setState({...clone(initial),presets:[{...p1,and:0},p2]});
  await test.element('upReviewConfirm').onclick();
  assert.equal(test.uploads.length,0,'Changed configuration requires a fresh confirmation.');
  assert.match(test.dialogs.at(-1).html,/Settings changed/);
  await test.element('upReviewConfirm').onclick();assert.equal(test.uploads.length,1);
  test=reviewHarness();await test.context.openUploadReview(['one','two'],true);
  test.setState({...clone(initial),presets:[p1,{...p2,include:0}]});
  await test.element('upReviewConfirm').onclick();assert.equal(test.uploads.length,0);
  assert.match(test.dialogs.at(-1).html,/Upload 1 preset/,'Batch membership is refreshed at commitment.');
  test=reviewHarness();await test.context.openUploadReview(['one']);test.context.contextRevision++;
  await test.element('upReviewConfirm').onclick();assert.equal(test.uploads.length,0);
  assert.match(test.element('upReviewMessage').textContent,/dressing avatar changed/);
  test=reviewHarness();test.context.options.hasPendingChanges=()=>true;await test.context.openUploadReview(['one']);assert.equal(test.dialogs.length,0,'External wardrobe writes must also block review.');
  test=reviewHarness();await test.context.openUploadReview(['one']);test.context.pendingConfigWrites++;
  await test.element('upReviewConfirm').onclick();assert.equal(test.uploads.length,0);
  test=reviewHarness();let itemEditCount=2,reviewedItemEdits=0;
  test.context.options.getUnappliedItemEdits=()=>itemEditCount;
  test.context.options.reviewUnappliedItemEdits=()=>{reviewedItemEdits++};
  test.context.options.hasPendingChanges=()=>true;
  await test.context.openUploadReview(['one']);
  assert.equal(test.uploads.length,0);
  assert.match(test.dialogs.at(-1).html,/Unapplied item edits: 2/);
  assert.doesNotMatch(test.dialogs.at(-1).html,/still saving|upReviewConfirm/,'Retained drafts are not described as writes in flight.');
  assert.match(test.dialogs.at(-1).html,/Review item edits/);
  test.element('upReviewItemEdits').onclick();assert.equal(reviewedItemEdits,1,'The recovery action opens the retained item edits.');
  itemEditCount=0;test.context.options.hasPendingChanges=()=>false;
  await test.context.openUploadReview(['one']);assert.match(test.dialogs.at(-1).html,/upReviewConfirm/,'Resolving item edits permits a fresh review.');
  itemEditCount=1;await test.element('upReviewConfirm').onclick();
  assert.equal(test.uploads.length,0,'Item edits introduced during review block the final upload.');
  assert.match(test.dialogs.at(-1).html,/Unapplied item edits: 1/);
  test=reviewHarness();itemEditCount=0;
  test.context.options.getUnappliedItemEdits=()=>itemEditCount;
  test.context.options.reviewUnappliedItemEdits=()=>{};
  test.context.upRefreshState=async()=>{itemEditCount=1;return clone(initial)};
  await test.context.openUploadReview(['one']);
  assert.match(test.dialogs.at(-1).html,/Unapplied item edits: 1/,'Drafts appearing during initial refresh must block review.');
  test=reviewHarness();itemEditCount=0;
  test.context.options.getUnappliedItemEdits=()=>itemEditCount;
  test.context.options.reviewUnappliedItemEdits=()=>{};
  await test.context.openUploadReview(['one']);
  test.context.upRefreshState=async()=>{itemEditCount=1;return clone(initial)};
  await test.element('upReviewConfirm').onclick();
  assert.equal(test.uploads.length,0,'Drafts appearing during the commitment refresh must block upload.');
  assert.match(test.dialogs.at(-1).html,/Review item edits/);
  assert.equal(scope.window.WardrobeUploadCopy['upload.preset.itemCount'],'Items: {0}');
  assert.equal(scope.window.WardrobeUploadCopy['upload.preset.sharedCount'],'Shared items: {0}');
  console.log('PASS: stable scoped drafts, storage fallback, explicit upload review, escaped targets, refreshed settings/membership, dirty/pending/context safeguards.');
})().catch(error=>{console.error(error);process.exitCode=1;});
