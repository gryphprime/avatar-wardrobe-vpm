export function getToken(hash = typeof location !== 'undefined' ? location.hash : '', storage = typeof sessionStorage !== 'undefined' ? sessionStorage : null) {
  const token = new URLSearchParams(hash.replace(/^#/, '')).get('token');
  if (token && storage) storage.setItem('atelier-token', token);
  return token || (storage && storage.getItem('atelier-token')) || '';
}
export function stateLabel(workspace) {
  if (!workspace) return { desired:'No changes yet', confirmed:'Nothing applied', rendered:'No snapshot' };
  const item = state => state?.recipe?.items?.map(entry => entry.name).filter(Boolean).join(', ') || 'Base avatar';
  return { desired:item(workspace.desired), confirmed:item(workspace.confirmed), rendered:workspace.rendered?.artifactId || workspace.rendered?.url ? 'Snapshot ready' : 'No snapshot' };
}
export function nextRevision(current = 0) { return Number(current || 0) + 1; }
export function escapeHtml(value='') { return String(value).replace(/[&<>'"]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[c])); }
export function desiredPayload(workspace, recipe) { return {expectedRevision: workspace?.desired?.revision || 0, recipe}; }
export function isFreshResponse(generation, latestGeneration, workspaceId, currentWorkspaceId) { return generation === latestGeneration && workspaceId === currentWorkspaceId; }
export function importReviewSummary(plan = {}) { return {files:(plan.files || []).length, newFiles:(plan.files || []).filter(file => file.state === 'new').length, conflicts:Number(plan.conflicts || 0), guidConflicts:(plan.guidConflicts || []).length, missingDependencies:(plan.missingDependencies || []).length, codeFiles:Number(plan.codeFiles || plan.codeCount || 0)}; }
export function comparisonPhotos(workspace, photos = [], camera = 'front') { const view=workspace?.rendered?.view || camera, matching=photos.filter(photo=>photo?.view===view); return matching.length >= 2 ? {view,before:matching[1],after:matching[0]} : null; }

const token = getToken();
if (typeof location !== 'undefined' && location.hash.includes('token=')) history.replaceState(null, '', location.pathname + location.search);
const doc = typeof document !== 'undefined' ? document : null;
const $ = selector => doc ? doc.querySelector(selector) : null;
const $$ = selector => doc ? [...doc.querySelectorAll(selector)] : [];
const api = async (path, options = {}) => {
  const headers = {'Content-Type':'application/json', ...(token ? {'X-Atelier-Token':token} : {}), ...(options.headers || {})};
  const response = await fetch(path, {...options, headers});
  const body = await response.json().catch(() => ({}));
  if (!response.ok) throw new Error(body.error || `Request failed (${response.status})`);
  return body;
};

class BlobUrlCache {
  constructor(limit = 6) { this.limit = limit; this.entries = new Map(); this.pending = new Map(); }
  async load(key, source, headers) {
    if (this.entries.has(key)) return this.entries.get(key);
    if (this.pending.has(key)) return this.pending.get(key);
    const request = fetch(source, {headers}).then(async response => {
      if (!response.ok) throw new Error('Snapshot unavailable');
      const url = URL.createObjectURL(await response.blob());
      this.entries.set(key, url); this.pending.delete(key); this.trim(); return url;
    }).catch(error => { this.pending.delete(key); throw error; });
    this.pending.set(key, request); return request;
  }
  trim() { while (this.entries.size > this.limit) { const [key, url] = this.entries.entries().next().value; this.entries.delete(key); URL.revokeObjectURL(url); } }
  clear() { this.pending.clear(); for (const url of this.entries.values()) URL.revokeObjectURL(url); this.entries.clear(); }
}

let appState = {workspaces:[], workspace:null, library:[], operations:[], worker:{state:'offline'}, photos:[], backgroundJobs:[], integrations:[]};
let cameraView = 'front';
let activeView = (typeof sessionStorage !== 'undefined' && sessionStorage.getItem('atelier-view')) || 'studio';
let refreshGeneration = 0, importGeneration = 0, currentWorkspaceId = null, requestedWorkspaceId = null;
let selectedPrefab = null, selectedTarget = null, pendingImport = null, displayedArtifact = null;
const blobs = new BlobUrlCache(6);

function element(tag, className, text) { const value = doc.createElement(tag); if (className) value.className = className; if (text !== undefined) value.textContent = text; return value; }
function clear(node) { if (node) node.replaceChildren(); }
function showDialog(node) { if (node && !node.open) node.showModal(); }
function toast(message) { const el=$('#toast'); if (!el) return; el.textContent=message; el.classList.add('show'); setTimeout(()=>el.classList.remove('show'),3000); }
function changeView(view) { activeView=view; sessionStorage?.setItem?.('atelier-view',view); render(); }
function workspaceId() { return appState.workspace?.id || null; }
function resetViewport() { displayedArtifact = null; const viewport=$('#snapshot'); if (viewport) { viewport.dataset.artifact=''; clear(viewport); viewport.append(element('div','snapshot-empty')); const empty=viewport.firstChild; empty.append(element('span','', '◌'),element('strong','', 'Waiting for a snapshot'),element('small','', 'Choose a saved avatar target, then capture a view.')); } $('#snapshot-state').textContent='No render yet'; $('#export-snapshot').disabled=true; $('#before-after').disabled=true; }

function renderWorkspaceSelector() {
  const host=$('.workspace-head'); if (!host) return;
  let select=$('#workspace-switcher');
  if (!appState.workspaces?.length) { select?.remove(); return; }
  const focused = doc.activeElement === select;
  if (!select) { select=element('select','workspace-switcher'); select.id='workspace-switcher'; select.setAttribute('aria-label','Switch workspace'); select.addEventListener('change',()=>switchWorkspace(select.value)); host.append(select); }
  const value=select.value; clear(select);
  appState.workspaces.forEach(space => { const option=element('option','',space.name || space.id); option.value=space.id; select.append(option); });
  select.value=requestedWorkspaceId || appState.workspace?.id || value;
  if (focused) select.focus();
}
function workerText(worker) { if (worker?.error) return `Worker error: ${worker.error}`; if (worker?.phase) return worker.phase; return worker?.state || 'offline'; }
function renderState() {
  const w=appState.workspace, labels=stateLabel(w), worker=appState.worker || {state:'offline'};
  $('#workspace-name').textContent=w?.name || 'Welcome to Atelier'; $('#workspace-path').textContent=w?.projectPath || 'Register a Unity project to begin';
  renderWorkspaceSelector();
  $('#onboarding').classList.toggle('hidden',!!w); $('#studio').classList.toggle('hidden',!w || activeView!=='studio'); $('#library').classList.toggle('hidden',activeView!=='library'); $('#activity').classList.toggle('hidden',activeView!=='activity');
  $$('.rail-item[data-view]').forEach(button=>button.classList.toggle('active',button.dataset.view===activeView));
  const running=['online','interactive'].includes(worker.state), starting=worker.state==='starting'; $('#worker-status').className=`worker ${worker.state==='online'?'online':'offline'}${worker.error?' error':''}`; $('#worker-status').replaceChildren(element('i'),element('span','',workerText(worker)));
  $('#worker-action').textContent=starting?'Starting worker…':running?'Stop worker':'Start worker'; $('#worker-action').disabled=!w || starting; ['open-unity','provision-worker'].forEach(id=>$('#'+id).disabled=!w || starting);
  ['desired','confirmed','rendered'].forEach(key=>{ $(`#${key}-label`).textContent=labels[key]; $(`#${key}-rev`).textContent=w?.[key]?.revision ? `r${w[key].revision}` : '—'; });
  $('#sync-pill').textContent=w?.syncStatus || 'Offline'; $('#sync').disabled=!w?.target || w?.desired?.revision===w?.confirmed?.revision; $('#undo').disabled=!w?.desired?.revision; $('#before-after').disabled=!comparisonPhotos(w,appState.photos,cameraView);
  const target=w?.target; $('#target-empty').classList.toggle('hidden',!!target); $('#target-detail').classList.toggle('hidden',!target);
  if(target) { $('#target-name').textContent=target.name || target.objectId || 'Avatar'; $('#target-id').textContent=`${target.sceneGuid || 'Scene'} · ${target.objectId || 'Object'}`; }
}
function renderWorn() {
  const list=$('#worn-list'), items=appState.workspace?.desired?.recipe?.items || []; clear(list);
  if (!items.length) { const empty=element('div','empty-row','Your avatar is ready for a first layer'); empty.append(element('span','', 'Drag an item from the library onto the viewport, or choose one.')); list.append(empty); return; }
  items.forEach(item=>{ const row=element('div','state-row'), dot=element('span','state-dot desired'), detail=element('div'); detail.append(element('strong','',item.name || item.assetId),element('small','',item.prefabGuid || 'Draft item'));
    const replace=element('button','link replace-item','Replace'); replace.dataset.id=item.id; const remove=element('button','link remove-item','Remove'); remove.dataset.id=item.id; row.append(dot,detail,replace,remove); list.append(row); });
}
function assetCard(asset) {
  const card=element('article','panel library-card'); card.draggable=true; card.dataset.assetId=asset.id;
  card.append(element('p','eyebrow',asset.product || 'ASSET'),element('h3','',asset.name || asset.id),element('small','muted',`${asset.prefabs?.length || 0} prefab${asset.prefabs?.length===1?'':'s'}`));
  const actions=element('div','card-actions'), tryOn=element('button','button try-item','Try on avatar'); tryOn.dataset.id=asset.id; actions.append(tryOn);
  if (appState.workspace) { const review=element('button','link import-plan','Plan import'); review.dataset.id=asset.id; actions.append(review); }
  card.append(actions); return card;
}
function renderLibraryGrid(grid) { clear(grid); const library=appState.library || []; if (!library.length) { const empty=element('div','library-empty'); empty.append(element('span','', '▱'),element('h3','', 'No library items yet'),element('p','', 'Import a local archive to build an immutable, searchable library.')); grid.append(empty); return; } library.forEach(asset=>grid.append(assetCard(asset))); }
function renderActivity() { const list=$('#activity-list'); clear(list); const operations=appState.operations || []; if (!operations.length) { const empty=element('div','library-empty'); empty.append(element('span','', '≋'),element('h3','', 'No operations yet'),element('p','', 'Accepted changes and worker receipts will appear here.')); list.append(empty); return; } operations.forEach(operation=>{ const state=operation.state || 'accepted', row=element('div','state-row'), detail=element('div'); row.append(element('span',`state-dot ${state==='failed'||state==='needs-review'?'desired':'confirmed'}`)); detail.append(element('strong','',operation.action || 'Operation'),element('small','',`${state}${operation.phase?` · ${operation.phase}`:''}${operation.error?` · ${operation.error}`:''}`)); row.append(detail); if (state==='failed'||state==='needs-review') { const retry=element('button','link retry','Retry'); retry.dataset.id=operation.id; row.append(retry); if(state==='failed') { const dismiss=element('button','link dismiss','Dismiss'); dismiss.dataset.id=operation.id; row.append(dismiss); } } list.append(row); }); }
function jobRow(job) { const row=element('div','job-row'); row.append(element('span',`state-dot ${job.state==='failed'?'desired':'confirmed'}`),element('span','',job.state==='running' ? 'Working in background' : job.state),element('small','',job.error || '')); return row; }
function renderJobs() { const jobs=appState.backgroundJobs || [], panel=$('#job-panel'), activityPanel=$('#activity-jobs'); panel.classList.toggle('hidden',!jobs.length); activityPanel.classList.toggle('hidden',!jobs.length); [$('#job-list'),$('#activity-job-list')].forEach(list=>{clear(list); jobs.forEach(job=>list.append(jobRow(job)));}); }
function renderIntegrations() { const list=$('#integrations-list'); clear(list); const integrations=appState.integrations || []; if (!integrations.length) { list.append(element('p','muted','No adapter manifests are available.')); return; } integrations.forEach(manifest=>{ const card=element('article','manifest-card'); card.append(element('h3','',manifest.name || manifest.id),element('p','muted',`Read-only manifest · ${manifest.origin || 'community'} · protocol ${manifest.protocol || 'unknown'}`),element('p','small',`Sample declaration: ${manifest.actions?.map(action=>action.name || action.id).join(', ') || 'no actions declared'}. Install and configure an adapter separately; this screen does not enable it.`)); list.append(card); }); }
function render() {
  if (!doc) return; const id=workspaceId(), intended=requestedWorkspaceId || id;
  if (id !== intended) { currentWorkspaceId=intended; renderPendingWorkspace(); return; }
  if (id !== currentWorkspaceId) { currentWorkspaceId=id; importGeneration++; pendingImport=null; blobs.clear(); resetViewport(); }
  renderState(); renderWorn(); renderLibraryGrid($('#library-grid')); renderLibraryGrid($('#studio-library-grid')); renderActivity(); renderJobs(); renderIntegrations(); hydrateSnapshot(appState.workspace);
}
function renderPendingWorkspace() { renderWorkspaceSelector(); $('#workspace-name').textContent='Loading workspace…'; $('#workspace-path').textContent='Switching local workspace'; $('#onboarding').classList.remove('hidden'); $('#studio').classList.add('hidden'); $('#library').classList.add('hidden'); $('#activity').classList.add('hidden'); ['worker-action','open-unity','provision-worker','sync','undo','capture'].forEach(id=>$('#'+id).disabled=true); }
async function hydrateSnapshot(workspace) {
  const artifact=workspace?.rendered, id=workspace?.id, key=artifact?.artifactId || artifact?.id || artifact?.url;
  if (!key || !artifact?.url) return;
  if (displayedArtifact?.workspaceId===id && displayedArtifact.key===key) return;
  const request={workspaceId:id,key};
  try { const url=await blobs.load(`${id}:${key}`,artifact.url,token?{'X-Atelier-Token':token}:{}); if (!isFreshResponse(refreshGeneration,refreshGeneration,id,workspaceId()) || request.workspaceId!==currentWorkspaceId || appState.workspace?.rendered?.url!==artifact.url) return;
    const viewport=$('#snapshot'); clear(viewport); const image=element('img','snapshot-image'); image.src=url; image.alt=`${workspace.name || 'Avatar'} snapshot`; viewport.append(image); viewport.dataset.artifact=key; displayedArtifact={...request,url,artifact}; $('#snapshot-state').textContent='Rendered'; $('#export-snapshot').disabled=false; $('#before-after').disabled=!comparisonPhotos(workspace,appState.photos,cameraView);
  } catch (error) { if (request.workspaceId===currentWorkspaceId) { $('#snapshot-state').textContent='Snapshot unavailable'; } }
}
function switchWorkspace(id) { if (!id || id===workspaceId()) return refresh(id); requestedWorkspaceId=id; currentWorkspaceId=id; importGeneration++; pendingImport=null; blobs.clear(); resetViewport(); refresh(id); }
async function refresh(id=requestedWorkspaceId || workspaceId()) { const generation=++refreshGeneration, requested=id; requestedWorkspaceId=requested; try { const state=await api(requested?`/api/state?workspace=${encodeURIComponent(requested)}`:'/api/state'); if (generation!==refreshGeneration) return; appState=state; requestedWorkspaceId=workspaceId(); render(); } catch (error) { if(generation===refreshGeneration) { toast(error.message); render(); } } }
async function register() { const name=$('#project-name').value.trim(), projectPath=$('#project-path').value.trim(); if(!name||!projectPath) return; try { const result=await api('/api/workspaces',{method:'POST',body:JSON.stringify({name,projectPath})}); $('#register-dialog').close(); await refresh(result.id); } catch(error) { toast(error.message); } }
async function worker(action) { const id=workspaceId(); if(!id) return; try { await api(`/api/workspaces/${id}/worker`,{method:'POST',body:JSON.stringify({action})}); await refresh(id); } catch(error) { toast(error.message); } }
function newItemId() { return typeof crypto !== 'undefined' && crypto.randomUUID ? crypto.randomUUID() : `${Date.now()}-${Math.random()}`; }
async function applyAsset(asset, prefab, replaceId) { const workspace=appState.workspace, recipe=workspace?.desired?.recipe || {items:[]}, items=recipe.items || []; const next=replaceId ? items.map(item=>item.id===replaceId ? {...item,assetId:asset.id,name:asset.name,prefabGuid:prefab.guid}:item) : [...items,{id:newItemId(),assetId:asset.id,name:asset.name,prefabGuid:prefab.guid}]; await api(`/api/workspaces/${workspace.id}/desired`,{method:'POST',body:JSON.stringify(desiredPayload(workspace,{...recipe,items:next}))}); await refresh(workspace.id); }
function choosePrefab(asset, replaceId=null) { if(!asset?.prefabs?.length) return toast('This library item has no usable prefabs.'); selectedPrefab={asset,replaceId}; $('#prefab-title').textContent=replaceId?'Replace worn item':'Add to draft'; $('#prefab-copy').textContent=`Choose one prefab from ${asset.name || asset.id}.`; const options=$('#prefab-options'); clear(options); asset.prefabs.forEach((prefab,index)=>{const label=element('label','choice'); const input=element('input'); input.type='radio'; input.name='prefab'; input.value=String(index); input.checked=index===0; label.append(input,element('span','',prefab.path || prefab.guid || `Prefab ${index+1}`)); options.append(label);}); $('#apply-prefab').textContent=replaceId?'Replace in draft':'Add to draft'; showDialog($('#prefab-dialog')); }
async function chooseTarget() { const id=workspaceId(); if(!id) return; try { const context=await api(`/api/workspaces/${id}/context`); if(id!==workspaceId()) return; const targets=context.targets || [], options=$('#target-options'); clear(options); selectedTarget=null; $('#apply-target').disabled=!targets.length; $('#target-dialog-copy').textContent=targets.length ? 'Choose the exact object in a saved scene. Atelier will not fall back to a different avatar.' : 'No saved avatar targets are currently available. Open a saved scene in Unity, then try again.'; targets.forEach((target,index)=>{ const label=element('label','choice'); const input=element('input'); input.type='radio'; input.name='target'; input.value=String(index); input.checked=index===0; label.append(input,element('span','',target.name || target.objectId || `Target ${index+1}`),element('small','',`${target.sceneGuid || 'unsaved scene'} · ${target.objectId || 'missing object id'}`)); options.append(label); }); selectedTarget=targets; showDialog($('#target-dialog')); } catch(error) { toast(error.message); } }
function reviewLine(title, text, kind='') { const row=element('div',`review-line ${kind}`); row.append(element('strong','',title),element('span','',text)); return row; }
function renderImportReview(plan) { const summary=importReviewSummary(plan), body=$('#import-body'); clear(body); $('#import-title').textContent='Review project import'; body.append(reviewLine('Files',`${summary.files} planned · ${summary.newFiles} new`),reviewLine('Existing file conflicts',String(summary.conflicts),summary.conflicts?'warning':''),reviewLine('GUID conflicts',String(summary.guidConflicts),summary.guidConflicts?'warning':''),reviewLine('Missing dependencies',String(summary.missingDependencies),summary.missingDependencies?'warning':''));
  if (summary.conflicts || summary.guidConflicts) body.append(element('p','review-warning','Resolve conflicts in the project before importing. The host will refuse this plan while they remain.'));
  const details=element('details','review-files'); details.append(element('summary','',`Show ${summary.files} planned files`)); const fileList=element('ul'); (plan.files || []).forEach(file=>fileList.append(element('li','',`${file.destination || file.path} · ${file.state || 'planned'}${file.kind==='code'?' · code':''}`))); details.append(fileList); body.append(details);
  if (summary.guidConflicts) { const guid=element('details','review-files'); guid.append(element('summary','',`Show ${summary.guidConflicts} GUID conflicts`)); const list=element('ul'); plan.guidConflicts.forEach(entry=>list.append(element('li','',`${entry.guid}: ${(entry.existingPaths || []).join(', ')}`))); guid.append(list); body.append(guid); }
  if (summary.missingDependencies) { const missing=element('details','review-files'); missing.append(element('summary','',`Show ${summary.missingDependencies} missing dependencies`)); const list=element('ul'); plan.missingDependencies.forEach(entry=>list.append(element('li','',`${entry.guid}: ${(entry.referencedBy || []).join(', ')}`))); missing.append(list); body.append(missing); }
  $('#code-consent').classList.toggle('hidden',!summary.codeFiles); $('#allow-code').checked=false; $('#apply-import').disabled=Boolean(summary.conflicts || summary.guidConflicts || summary.codeFiles); pendingImport={...pendingImport,plan};
}
async function pollImportPlan(jobId, requestId, id) { try { const job=await api(`/api/jobs/${encodeURIComponent(jobId)}`); if(requestId!==importGeneration || id!==workspaceId()) return; if(job.state==='running') { setTimeout(()=>pollImportPlan(jobId,requestId,id),600); return; } if(job.state==='failed') { $('#import-title').textContent='Import review failed'; $('#import-body').textContent=job.error || 'The import plan could not be created.'; return; } if(job.state==='succeeded') renderImportReview(job.result || {}); } catch(error) { if(requestId===importGeneration) { $('#import-title').textContent='Import review failed'; $('#import-body').textContent=error.message; } } }
async function startImportPlan(assetId) { const id=workspaceId(); if(!id) return; const requestId=++importGeneration; pendingImport={id:requestId,workspaceId:id,assetId}; $('#import-title').textContent='Preparing review'; $('#import-body').textContent='Inspecting files and dependencies. No project files will change until you approve this review.'; $('#code-consent').classList.add('hidden'); $('#apply-import').disabled=true; showDialog($('#import-dialog')); try { const job=await api(`/api/workspaces/${id}/import-plan`,{method:'POST',body:JSON.stringify({assetId})}); if(requestId!==importGeneration || id!==workspaceId()) return; pendingImport.jobId=job.id; pollImportPlan(job.id,requestId,id); } catch(error) { if(requestId===importGeneration) { $('#import-title').textContent='Import review failed'; $('#import-body').textContent=error.message; } } }
async function applyReviewedImport() { const pending=pendingImport, id=workspaceId(); if(!pending?.plan || pending.workspaceId!==id) return; const summary=importReviewSummary(pending.plan), allowCode=$('#allow-code').checked; if(summary.codeFiles && !allowCode) return toast('Check “Include executable Unity code” to approve this plan.'); try { const result=await api(`/api/workspaces/${id}/import`,{method:'POST',body:JSON.stringify({planId:pending.jobId,allowCode})}); $('#import-dialog').close(); toast(result.state==='running'?'Import is working in the background.':'Import accepted'); await refresh(id); } catch(error) { toast(error.message); } }
function photoCaption(photo, view) { return `${view} · r${photo.revision ?? '—'}`; }
async function showComparison() { const id=workspaceId(), pair=comparisonPhotos(appState.workspace,appState.photos,cameraView); if(!pair) return; const {after,before,view}=pair; try { const [afterUrl,beforeUrl]=await Promise.all([blobs.load(`${id}:${after.id || after.artifactId}`,after.url,token?{'X-Atelier-Token':token}:{}),blobs.load(`${id}:${before.id || before.artifactId}`,before.url,token?{'X-Atelier-Token':token}:{})]); if(id!==workspaceId()) return; $('#before-image').src=beforeUrl; $('#after-image').src=afterUrl; $('#before-caption').textContent=`Before · ${photoCaption(before,view)}`; $('#after-caption').textContent=`After · ${photoCaption(after,view)}`; showDialog($('#compare-dialog')); } catch(error) { toast(error.message); } }
function exportSnapshot() { if(!displayedArtifact?.url) return; const link=element('a'); link.href=displayedArtifact.url; link.download='atelier-snapshot.png'; link.click(); }

doc?.addEventListener('click', async event => { const button=event.target.closest('button'); if(!button) return;
  const nav=button.closest('.rail-item[data-view]'); if(nav) return changeView(nav.dataset.view);
  if(button.matches('#register,#register-inline')) return showDialog($('#register-dialog'));
  if(button.matches('#open-unity')) return worker('open-unity'); if(button.matches('#provision-worker')) return worker('provision'); if(button.matches('#worker-action')) return worker(['online','interactive'].includes(appState.worker?.state)?'stop':'start');
  if(button.matches('#change-target')) return chooseTarget(); if(button.matches('#capture')) { const id=workspaceId(); if(!id) return; try { await api(`/api/workspaces/${id}/snapshot`,{method:'POST',body:JSON.stringify({view:cameraView})}); toast('Snapshot queued. The previous render remains visible.'); await refresh(id); } catch(error) { toast(error.message); } return; }
  if(button.matches('#before-after')) return showComparison(); if(button.matches('#export-snapshot')) return exportSnapshot();
  if(button.matches('#undo')) { const space=appState.workspace; try { await api(`/api/workspaces/${space.id}/undo`,{method:'POST',body:JSON.stringify({expectedRevision:space.desired.revision})}); await refresh(space.id); } catch(error) { toast(error.message); } return; }
  if(button.matches('#sync')) { const space=appState.workspace; try { await api(`/api/workspaces/${space.id}/sync`,{method:'POST',body:JSON.stringify({expectedRevision:space.desired.revision})}); await refresh(space.id); } catch(error) { toast(error.message); } return; }
  if(button.matches('[data-camera]')) { cameraView=button.dataset.camera; $$('.camera[data-camera]').forEach(camera=>camera.classList.toggle('active',camera===button)); return; }
  if(button.matches('.try-item')) return choosePrefab(appState.library.find(asset=>asset.id===button.dataset.id),selectedPrefab?.replaceId || null); if(button.matches('.import-plan')) return startImportPlan(button.dataset.id); if(button.matches('.replace-item')) return chooseReplacement(button.dataset.id); if(button.matches('.remove-item')) { const space=appState.workspace, recipe=space.desired.recipe; try { await api(`/api/workspaces/${space.id}/desired`,{method:'POST',body:JSON.stringify(desiredPayload(space,{...recipe,items:recipe.items.filter(item=>item.id!==button.dataset.id)}))}); await refresh(space.id); } catch(error) { toast(error.message); } return; }
  if(button.matches('#add-item,.show-library')) return changeView('library'); if(button.matches('#import-library')) return showDialog($('#archive-dialog')); if(button.matches('#integrations')) return showDialog($('#integrations-dialog'));
  if(button.matches('.retry,.dismiss')) { try { await api(`/api/operations/${button.dataset.id}/${button.classList.contains('retry')?'retry':'dismiss'}`,{method:'POST'}); await refresh(workspaceId()); } catch(error) { toast(error.message); } return; }
  if(button.matches('.close-dialog')) button.closest('dialog').close();
});
function chooseReplacement(itemId) { const asset=appState.library.find(Boolean); if(!asset) { changeView('library'); return toast('Choose a replacement from the library.'); } selectedPrefab={replaceId:itemId}; changeView('library'); toast('Choose “Try on avatar” for the replacement layer.'); }
doc?.addEventListener('dragstart',event=>{ const card=event.target.closest('.library-card'); if(card) { event.dataTransfer.effectAllowed='copy'; event.dataTransfer.setData('text/atelier-asset',card.dataset.assetId); } });
doc?.addEventListener('dragover',event=>{ if(event.target.closest('#snapshot')) { event.preventDefault(); event.dataTransfer.dropEffect='copy'; } });
doc?.addEventListener('drop',event=>{ const id=event.dataTransfer?.getData('text/atelier-asset'); if(!event.target.closest('#snapshot') || !id) return; event.preventDefault(); choosePrefab(appState.library.find(asset=>asset.id===id),selectedPrefab?.replaceId || null); });
$('#register-form')?.addEventListener('submit',event=>{ if(event.submitter?.value==='cancel') return; event.preventDefault(); register(); });
$('#archive-form')?.addEventListener('submit',async event=>{ if(event.submitter?.value==='cancel') return; event.preventDefault(); const path=$('#archive-path').value.trim(); if(!path) return; try { await api('/api/library',{method:'POST',body:JSON.stringify({path})}); $('#archive-dialog').close(); toast('Archive is being added to the local library.'); await refresh(workspaceId()); } catch(error) { toast(error.message); } });
$('#prefab-form')?.addEventListener('submit',async event=>{ if(event.submitter?.value==='cancel') return; event.preventDefault(); const index=Number($('input[name="prefab"]:checked')?.value), prefab=selectedPrefab?.asset?.prefabs?.[index]; if(!prefab) return; try { await applyAsset(selectedPrefab.asset,prefab,selectedPrefab.replaceId); $('#prefab-dialog').close(); selectedPrefab=null; } catch(error) { toast(error.message); } });
$('#target-form')?.addEventListener('submit',async event=>{ if(event.submitter?.value==='cancel') return; event.preventDefault(); const index=Number($('input[name="target"]:checked')?.value), target=selectedTarget?.[index], id=workspaceId(); if(!target||!id) return; try { await api(`/api/workspaces/${id}/target`,{method:'POST',body:JSON.stringify(target)}); $('#target-dialog').close(); await refresh(id); } catch(error) { toast(error.message); } });
$('#import-form')?.addEventListener('submit',event=>{ if(event.submitter?.value==='cancel') return; event.preventDefault(); applyReviewedImport(); });
$('#prefab-dialog')?.addEventListener('close',()=>{ selectedPrefab=null; });
$('#allow-code')?.addEventListener('change',event=>{ const summary=importReviewSummary(pendingImport?.plan); $('#apply-import').disabled=Boolean(summary.conflicts || summary.guidConflicts || (summary.codeFiles && !event.target.checked)); });
if (typeof window !== 'undefined') window.addEventListener('beforeunload',()=>blobs.clear());
if(doc) { setInterval(()=>refresh(requestedWorkspaceId || workspaceId()),1000); refresh(); }
if(typeof module!=='undefined') module.exports={getToken,stateLabel,nextRevision,escapeHtml,desiredPayload,isFreshResponse,importReviewSummary,comparisonPhotos,BlobUrlCache};
