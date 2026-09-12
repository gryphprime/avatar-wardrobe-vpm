const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');

const source = fs.readFileSync('apps/desktop/app.js', 'utf8').replace(/^export /gm, '');
const sandbox = {module:{exports:{}},URLSearchParams,sessionStorage:{getItem:()=>null,setItem(){}},location:{hash:''},fetch:async()=>({ok:true,json:async()=>({})}),crypto,setInterval(){},setTimeout(){},URL:{createObjectURL:()=> 'blob:test',revokeObjectURL(){}}};
vm.runInNewContext(source + '\nmodule.exports={getToken,stateLabel,nextRevision,escapeHtml,desiredPayload,isFreshResponse,importReviewSummary,comparisonPhotos,clampColor,colorToHex,hexToColor,cloneRecipe,setMaterialOverride,resetMaterialOverride,setBlendshapeOverride,resetBlendshapeOverride,appearanceOptionLabel,recoveryRecipeSummary,BlobUrlCache};', sandbox);
const {getToken,stateLabel,nextRevision,escapeHtml,desiredPayload,isFreshResponse,importReviewSummary,comparisonPhotos,clampColor,colorToHex,hexToColor,cloneRecipe,setMaterialOverride,resetMaterialOverride,setBlendshapeOverride,resetBlendshapeOverride,appearanceOptionLabel,recoveryRecipeSummary,BlobUrlCache} = sandbox.module.exports;
const plain = value => JSON.parse(JSON.stringify(value));

assert.equal(escapeHtml('<img onerror="x">'), '&lt;img onerror=&quot;x&quot;&gt;');
assert.deepEqual(plain(desiredPayload({desired:{revision:7}},{items:[]})), {expectedRevision:7,recipe:{items:[]}});
assert.equal(getToken('#token=abc', {setItem(){},getItem(){return ''}}), 'abc');
assert.equal(nextRevision(4), 5);
assert.deepEqual(plain(stateLabel(null)), {desired:'No changes yet',confirmed:'Nothing applied',rendered:'No snapshot'});
assert.deepEqual(plain(stateLabel({desired:{recipe:{items:[{name:'Hoodie'}]}},confirmed:{recipe:{items:[]}},rendered:{artifactId:'x'}})), {desired:'Hoodie',confirmed:'Base avatar',rendered:'Snapshot ready'});
assert.deepEqual(plain(importReviewSummary({files:[{state:'new'},{state:'conflict'}],conflicts:1,guidConflicts:[{}],missingDependencies:[{}],codeFiles:2})), {files:2,newFiles:1,conflicts:1,guidConflicts:1,missingDependencies:1,codeFiles:2});
assert.deepEqual(plain(comparisonPhotos({rendered:{view:'front'}},[{id:'new-side',view:'side'},{id:'new-front',view:'front',revision:4},{id:'old-front',view:'front',revision:3}])), {view:'front',after:{id:'new-front',view:'front',revision:4},before:{id:'old-front',view:'front',revision:3}});
assert.deepEqual(plain(comparisonPhotos({rendered:{view:'front',revision:4}},[{id:'old-front',view:'front',revision:3},{id:'new-front',view:'front',revision:4}])), {view:'front',after:{id:'new-front',view:'front',revision:4},before:{id:'old-front',view:'front',revision:3}});
assert.equal(comparisonPhotos({rendered:{view:'back'}},[{id:'front',view:'front'}]), null);
assert.equal(clampColor(2), 1);
assert.equal(colorToHex([1,0.5,0,0.25]), '#ff8000');
assert.deepEqual(plain(hexToColor('#336699',0.4)), [0x33/255,0x66/255,0x99/255,0.4]);
const recipe = {items:[{id:'jacket'}],appearance:{custom:{preserve:true},materials:[{rendererId:'body',slot:0,property:'_Color',color:[1,0,0,1]},{rendererId:'other',slot:0,property:'_Color',color:[0,1,0,1]}],blendshapes:[{rendererId:'face',index:2,value:0.2},{rendererId:'other',index:3,value:0.4}]}};
const changedMaterial = setMaterialOverride(recipe,{rendererId:'body',slot:0,property:'_Color'},[0.1,0.2,0.3,0.8]);
assert.deepEqual(plain(changedMaterial.appearance.materials[0].color), [0.1,0.2,0.3,0.8]);
assert.equal(changedMaterial.appearance.custom.preserve, true);
assert.equal(recipe.appearance.materials[0].color[0], 1);
assert.equal(resetMaterialOverride(changedMaterial,{rendererId:'body',slot:0,property:'_Color'}).appearance.materials.length, 1);
const changedShape = setBlendshapeOverride(recipe,{rendererId:'face',index:2},0.7);
assert.equal(changedShape.appearance.blendshapes[0].value, 0.7);
assert.equal(resetBlendshapeOverride(changedShape,{rendererId:'face',index:2}).appearance.blendshapes.length, 1);
assert.equal(appearanceOptionLabel('_BaseColor'), 'Base color');
assert.deepEqual(plain(recoveryRecipeSummary(recipe)), {items:1,materials:2,blendshapes:2});

(async () => {
  let latest = 2, committed = null;
  const delayed = value => Promise.resolve(value).then(response => {
    if (isFreshResponse(response.generation, latest, response.workspaceId, 'workspace-b')) committed = response.value;
  });
  await Promise.all([delayed({generation:1,workspaceId:'workspace-a',value:'stale'}), delayed({generation:2,workspaceId:'workspace-b',value:'fresh'})]);
  assert.equal(committed, 'fresh');
  assert.equal(isFreshResponse(2, 2, 'workspace-a', 'workspace-b'), false);
  let requests = 0, revoked = [];
  sandbox.fetch = async () => ({ok:true, blob:async () => ({})});
  sandbox.URL.createObjectURL = () => `blob:${++requests}`;
  sandbox.URL.revokeObjectURL = url => revoked.push(url);
  const cache = new BlobUrlCache(1);
  assert.equal(await cache.load('current', '/artifact'), 'blob:1');
  assert.equal(await cache.load('current', '/artifact'), 'blob:1');
  assert.equal(requests, 1);
  await cache.load('older', '/older');
  assert.deepEqual(revoked, ['blob:1']);
  console.log('atelier ui tests passed');
})().catch(error => { console.error(error); process.exitCode = 1; });
