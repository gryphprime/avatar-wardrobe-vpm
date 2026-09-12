const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');

const source = fs.readFileSync('apps/desktop/app.js', 'utf8').replace(/^export /gm, '');
const sandbox = {module:{exports:{}},URLSearchParams,sessionStorage:{getItem:()=>null,setItem(){}},location:{hash:''},fetch:async()=>({ok:true,json:async()=>({})}),crypto,setInterval(){},setTimeout(){},URL:{createObjectURL:()=> 'blob:test',revokeObjectURL(){}}};
vm.runInNewContext(source + '\nmodule.exports={getToken,stateLabel,nextRevision,escapeHtml,desiredPayload,isFreshResponse,importReviewSummary,comparisonPhotos,BlobUrlCache};', sandbox);
const {getToken,stateLabel,nextRevision,escapeHtml,desiredPayload,isFreshResponse,importReviewSummary,comparisonPhotos,BlobUrlCache} = sandbox.module.exports;
const plain = value => JSON.parse(JSON.stringify(value));

assert.equal(escapeHtml('<img onerror="x">'), '&lt;img onerror=&quot;x&quot;&gt;');
assert.deepEqual(plain(desiredPayload({desired:{revision:7}},{items:[]})), {expectedRevision:7,recipe:{items:[]}});
assert.equal(getToken('#token=abc', {setItem(){},getItem(){return ''}}), 'abc');
assert.equal(nextRevision(4), 5);
assert.deepEqual(plain(stateLabel(null)), {desired:'No changes yet',confirmed:'Nothing applied',rendered:'No snapshot'});
assert.deepEqual(plain(stateLabel({desired:{recipe:{items:[{name:'Hoodie'}]}},confirmed:{recipe:{items:[]}},rendered:{artifactId:'x'}})), {desired:'Hoodie',confirmed:'Base avatar',rendered:'Snapshot ready'});
assert.deepEqual(plain(importReviewSummary({files:[{state:'new'},{state:'conflict'}],conflicts:1,guidConflicts:[{}],missingDependencies:[{}],codeFiles:2})), {files:2,newFiles:1,conflicts:1,guidConflicts:1,missingDependencies:1,codeFiles:2});
assert.deepEqual(plain(comparisonPhotos({rendered:{view:'front'}},[{id:'new-side',view:'side'},{id:'new-front',view:'front',revision:4},{id:'old-front',view:'front',revision:3}])), {view:'front',after:{id:'new-front',view:'front',revision:4},before:{id:'old-front',view:'front',revision:3}});
assert.equal(comparisonPhotos({rendered:{view:'back'}},[{id:'front',view:'front'}]), null);

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
