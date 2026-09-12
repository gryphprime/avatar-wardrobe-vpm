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
export function comparisonPhotos(workspace, photos = [], camera = 'front') {
  const view=workspace?.rendered?.view || camera;
  const matching=photos.filter(photo=>photo?.view===view);
  if (matching.length < 2) return null;
  const renderedId=workspace?.rendered?.artifactId || workspace?.rendered?.id;
  const currentRevision=Number(workspace?.rendered?.revision ?? workspace?.confirmed?.revision ?? workspace?.desired?.revision);
  const ordered=matching.slice().sort((left,right)=>{
    const a=Number(left?.revision), b=Number(right?.revision);
    if (Number.isFinite(a) && Number.isFinite(b) && a!==b) return b-a;
    return matching.indexOf(left)-matching.indexOf(right);
  });
  const after=matching.find(photo=>renderedId && (photo.id===renderedId || photo.artifactId===renderedId)) || (Number.isFinite(currentRevision) && ordered.find(photo=>Number(photo?.revision)===currentRevision)) || ordered[0];
  const afterRevision=Number(after?.revision);
  const before=matching.find(photo=>photo!==after && Number.isFinite(afterRevision) && Number.isFinite(Number(photo?.revision)) && Number(photo.revision)<afterRevision) || matching.find(photo=>photo!==after) || null;
  return before ? {view,before,after} : null;
}
export function clampColor(value, fallback = 1) { const number=Number(value); return Number.isFinite(number) ? Math.max(0, Math.min(1, number)) : fallback; }
export function colorToHex(color) {
  const values=Array.isArray(color) ? color : [1,1,1,1];
  return '#' + values.slice(0,3).map(value=>Math.round(clampColor(value)*255).toString(16).padStart(2,'0')).join('');
}
export function hexToColor(value, alpha = 1) {
  const text=String(value || '').trim().replace(/^#/,'');
  const expanded=text.length===3 ? text.split('').map(char=>char+char).join('') : text;
  if (!/^[0-9a-f]{6}$/i.test(expanded)) return [0,0,0,clampColor(alpha)];
  return [0,2,4].map(offset=>parseInt(expanded.slice(offset,offset+2),16)/255).concat(clampColor(alpha));
}
export function cloneRecipe(recipe = {}) {
  const appearance = recipe && typeof recipe.appearance === 'object' && !Array.isArray(recipe.appearance) ? {...recipe.appearance} : {};
  return {...(recipe && typeof recipe === 'object' ? recipe : {}), items:Array.isArray(recipe?.items) ? recipe.items.map(item=>({...item})) : [], appearance:{...appearance, materials:Array.isArray(appearance.materials) ? appearance.materials.map(item=>({...item,color:Array.isArray(item.color) ? item.color.slice() : item.color})) : [], blendshapes:Array.isArray(appearance.blendshapes) ? appearance.blendshapes.map(item=>({...item})) : []}};
}
function sameMaterialOverride(entry, option) { return String(entry?.rendererId)===String(option?.rendererId) && String(entry?.slot)===String(option?.slot) && String(entry?.property)===String(option?.property); }
function sameBlendshapeOverride(entry, option) { return String(entry?.rendererId)===String(option?.rendererId) && Number(entry?.index)===Number(option?.index); }
export function setMaterialOverride(recipe, option, color) {
  const next=cloneRecipe(recipe), overrides=next.appearance.materials || [], source=Array.isArray(color) ? color : [], value=[0,1,2,3].map(index=>clampColor(source[index],index===3?1:0));
  const index=overrides.findIndex(entry=>sameMaterialOverride(entry,option));
  const override={rendererId:option.rendererId,slot:option.slot,property:option.property,color:value};
  if (index<0) overrides.push(override); else overrides[index]={...overrides[index],...override};
  next.appearance.materials=overrides;
  return next;
}
export function resetMaterialOverride(recipe, option) { const next=cloneRecipe(recipe); next.appearance.materials=(next.appearance.materials || []).filter(entry=>!sameMaterialOverride(entry,option)); return next; }
export function setBlendshapeOverride(recipe, option, value) {
  const next=cloneRecipe(recipe), overrides=next.appearance.blendshapes || [], numeric=Number(value), index=overrides.findIndex(entry=>sameBlendshapeOverride(entry,option));
  const override={rendererId:option.rendererId,index:Number(option.index),value:Number.isFinite(numeric) ? numeric : Number(option.value || 0)};
  if (index<0) overrides.push(override); else overrides[index]={...overrides[index],...override};
  next.appearance.blendshapes=overrides;
  return next;
}
export function resetBlendshapeOverride(recipe, option) { const next=cloneRecipe(recipe); next.appearance.blendshapes=(next.appearance.blendshapes || []).filter(entry=>!sameBlendshapeOverride(entry,option)); return next; }
export function appearanceOptionLabel(property) { return property==='_BaseColor' ? 'Base color' : property==='_Color' ? 'Color' : String(property || 'Color'); }
export function recoveryRecipeSummary(recipe = {}) { const value=recipe || {}; return {items:Array.isArray(value.items) ? value.items.length : 0, materials:Array.isArray(value.appearance?.materials) ? value.appearance.materials.length : 0, blendshapes:Array.isArray(value.appearance?.blendshapes) ? value.appearance.blendshapes.length : 0}; }

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

let appState = {workspaces:[], workspace:null, library:[], operations:[], projectOperations:[], worker:{state:'offline'}, photos:[], backgroundJobs:[], integrations:[]};
let cameraView = 'front';
let activeView = (typeof sessionStorage !== 'undefined' && sessionStorage.getItem('atelier-view')) || 'studio';
let refreshGeneration = 0, importGeneration = 0, appearanceGeneration = 0, recoveryGeneration = 0, integrationGeneration = 0, currentWorkspaceId = null, requestedWorkspaceId = null;
let selectedPrefab = null, selectedTarget = null, pendingImport = null, displayedArtifact = null, appearanceInspection = null, appearanceRefreshing = false, appearanceError = '', appearanceInspectionWorkspaceId = null, appearanceSaveTimer = null, appearanceSaveInFlight = null, appearancePending = null, appearanceFailedDraft = null, appearanceSaveSequence = 0, appearanceSaveError = '', recoveryReview = null, integrationPlan = null, projectReview = null;
const blobs = new BlobUrlCache(6);

function element(tag, className, text) { const value = doc.createElement(tag); if (className) value.className = className; if (text !== undefined) value.textContent = text; return value; }
function clear(node) { if (node) node.replaceChildren(); }
function showDialog(node) { if (node && !node.open) node.showModal(); }
function toast(message) { const el=$('#toast'); if (!el) return; el.textContent=message; el.classList.add('show'); setTimeout(()=>el.classList.remove('show'),3000); }
function changeView(view) { activeView=view; sessionStorage?.setItem?.('atelier-view',view); render(); }
function workspaceId() { return appState.workspace?.id || null; }
function targetIdentity(target=appState.workspace?.target) { return target ? `${target.sceneGuid || ''}:${target.objectId || ''}` : ''; }
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
  $('#sync-pill').textContent=w?.syncStatus || 'Offline'; $('#sync').disabled=!w?.target || w?.desired?.revision===w?.confirmed?.revision || Boolean(appearancePending || appearanceSaveInFlight || appearanceFailedDraft); $('#undo').disabled=!w?.desired?.revision || Boolean(appearanceFailedDraft); $('#before-after').disabled=!comparisonPhotos(w,appState.photos,cameraView);
  $('#refresh-properties').disabled=!w || !w.target;
  $('#review-recovery').disabled=!w || !w.target;
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
function renderActivity() {
  const list=$('#activity-list'); clear(list); const operations=appState.operations || [], projectOperations=appState.projectOperations || [];
  if (!operations.length && !projectOperations.length) { const empty=element('div','library-empty'); empty.append(element('span','', '≋'),element('h3','', 'No operations yet'),element('p','', 'Accepted changes and worker receipts will appear here.')); list.append(empty); return; }
  operations.forEach(operation=>{ const state=operation.state || 'accepted', row=element('div','state-row'), detail=element('div'); row.append(element('span',`state-dot ${state==='failed'||state==='needs-review'?'desired':'confirmed'}`)); detail.append(element('strong','',operation.action || 'Operation'),element('small','',`${state}${operation.phase?` · ${operation.phase}`:''}${operation.error?` · ${operation.error}`:''}`)); row.append(detail); if (state==='failed'||state==='needs-review') { const retry=element('button','link retry','Retry'); retry.dataset.id=operation.id; row.append(retry); if(state==='failed') { const dismiss=element('button','link dismiss','Dismiss'); dismiss.dataset.id=operation.id; row.append(dismiss); } } list.append(row); if (state==='succeeded' && operation.action==='inspect' && operation.result?.inspection) { const report=element('details','review-files'); report.append(element('summary','', 'View inspection report')); report.append(element('pre','project-review-pre',boundedProjectValue(operation.result.inspection))); list.append(report); } });
  projectOperations.forEach(operation=>{ const state=operation.state || 'running', row=element('div','state-row'), detail=element('div'); row.append(element('span',`state-dot ${state==='needs-review'?'desired':'confirmed'}`)); detail.append(element('strong','',`Project files · ${operation.kind || 'operation'}`),element('small','',`${state}${operation.error?` · ${operation.error}`:''}`)); row.append(detail); if (state==='needs-review') { const review=element('button','link project-review','Review project files'); review.dataset.operationId=operation.id; review.dataset.workspaceId=operation.workspaceId || workspaceId(); row.append(review); } list.append(row); });
}
function jobRow(job) { const row=element('div','job-row'); row.append(element('span',`state-dot ${job.state==='failed'?'desired':'confirmed'}`),element('span','',job.phase || job.message || (job.state==='running' ? 'Working in background' : job.state)),element('small','',job.error || '')); return row; }
function renderJobs() { const jobs=appState.backgroundJobs || [], panel=$('#job-panel'), activityPanel=$('#activity-jobs'); panel.classList.toggle('hidden',!jobs.length); activityPanel.classList.toggle('hidden',!jobs.length); [$('#job-list'),$('#activity-job-list')].forEach(list=>{clear(list); jobs.forEach(job=>list.append(jobRow(job)));}); }
function materialOptions(inspect = appearanceInspection) {
  const options=[];
  (inspect?.appearanceOptions?.materials || []).forEach(material=>{
    if (!material || material.rendererId===undefined || !Number.isInteger(material.slot) || material.slot<0 || material.slot>65535) return;
    (material?.properties || []).forEach(property=>{
      const name=property?.name;
      if (name!=='_Color' && name!=='_BaseColor') return;
      options.push({rendererId:material.rendererId,name:material.name,slot:material.slot,property:name,color:Array.isArray(property.color) ? property.color.slice(0,4) : [1,1,1,1]});
    });
  });
  return options;
}
function blendshapeOptions(inspect = appearanceInspection) { return (inspect?.appearanceOptions?.blendshapes || []).filter(option=>option && option.rendererId!==undefined && Number.isInteger(option.index) && option.index>=0 && option.index<=65535).map(option=>({...option})); }
function findMaterialOverride(recipe, option) { return (recipe?.appearance?.materials || []).find(entry=>sameMaterialOverride(entry,option)); }
function findBlendshapeOverride(recipe, option) { return (recipe?.appearance?.blendshapes || []).find(entry=>sameBlendshapeOverride(entry,option)); }
function appearanceOptionFromElement(node) {
  const slot=node.dataset.slot;
  let parsedSlot=slot;
  if (slot==='') parsedSlot=undefined;
  else if (/^-?\d+(?:\.\d+)?$/.test(slot)) parsedSlot=Number(slot);
  return {rendererId:node.dataset.rendererId,slot:parsedSlot,property:node.dataset.property,index:node.dataset.index!==undefined ? Number(node.dataset.index) : undefined};
}
function renderAppearance() {
  const controls=$('#appearance-controls'), warning=$('#appearance-warning'), status=$('#appearance-status'), refresh=$('#refresh-properties');
  if (!controls || !status) return;
  clear(controls);
  if (refresh) refresh.disabled=appearanceRefreshing || !workspaceId() || !appState.workspace?.target;
  if (appearanceRefreshing) status.textContent='Reading declared properties from Unity…';
  else if (appearanceSaveInFlight) status.textContent='Saving draft…';
  else if (appearanceSaveError) status.textContent=appearanceSaveError;
  else if (appearanceError) status.textContent=appearanceError;
  else if (!appearanceInspection || appearanceInspectionWorkspaceId!==workspaceId()) status.textContent='Refresh properties after choosing a saved Unity target.';
  else status.textContent=`Only properties declared by the connected Unity bridge are editable.${appearanceInspection.revision ? ` Unity revision ${appearanceInspection.revision}.` : ''}`;
  const warnings=Array.isArray(appearanceInspection?.warnings) ? appearanceInspection.warnings : (appearanceInspection?.warnings ? [appearanceInspection.warnings] : []);
  if (warning) { warning.classList.toggle('hidden',!appearanceError && !warnings.length); warning.textContent=appearanceError || warnings.map(item=>typeof item==='string' ? item : item?.message || 'Unity reported an unsupported property.').join(' '); }
  if (!appearanceInspection || appearanceInspectionWorkspaceId!==workspaceId()) return;
  const recipe=appState.workspace?.desired?.recipe || {items:[],appearance:{}};
  const materials=materialOptions(), blendshapes=blendshapeOptions();
  if (!materials.length && !blendshapes.length) { controls.append(element('p','muted small','No supported material colors or blendshapes were declared for this target.')); return; }
  if (materials.length) {
    const group=element('section','appearance-group'); group.append(element('h4','', 'Material colors'));
    materials.forEach(option=>{
      const field=element('div','appearance-field'), label=element('label',''), title=option.name || option.rendererId || 'Material';
      label.append(element('span','',`${title}${option.slot!==undefined ? ` · Slot ${option.slot}` : ''}`),element('small','',appearanceOptionLabel(option.property)));
      const value=findMaterialOverride(recipe,option), color=value?.color || option.color || [1,1,1,1], input=element('input'); input.type='color'; input.value=colorToHex(color); input.dataset.appearanceKind='material'; input.dataset.rendererId=String(option.rendererId ?? ''); input.dataset.slot=option.slot===undefined ? '' : String(option.slot); input.dataset.property=option.property; input.dataset.alpha=String(clampColor(color[3],1)); input.setAttribute('aria-label',`${title} ${appearanceOptionLabel(option.property)}`);
      const reset=element('button','link appearance-reset','Reset'); reset.type='button'; reset.disabled=!value; reset.dataset.appearanceReset='material'; Object.assign(reset.dataset,{rendererId:String(option.rendererId ?? ''),slot:option.slot===undefined ? '' : String(option.slot),property:option.property});
      const right=element('div','appearance-control'); right.append(input,reset); field.append(label,right); group.append(field);
    });
    controls.append(group);
  }
  if (blendshapes.length) {
    const group=element('section','appearance-group'); group.append(element('h4','', 'Shape keys'));
    blendshapes.forEach(option=>{
      const field=element('div','appearance-field'), label=element('label',''), title=option.name || `Shape ${option.index}`;
      label.append(element('span','',title),element('small','',`${option.rendererId} · index ${option.index}`));
      const value=findBlendshapeOverride(recipe,option), current=Number(value?.value ?? option.value ?? 0), range=element('input'), numeric=element('span','appearance-value',Number.isFinite(current) ? String(Math.round(current*100)/100) : '0'); range.type='range'; range.min=String(option.min ?? 0); range.max=String(option.max ?? 100); range.step=String(option.step ?? 0.01); range.value=String(current); range.dataset.appearanceKind='blendshape'; range.dataset.rendererId=String(option.rendererId ?? ''); range.dataset.index=String(option.index); range.dataset.min=String(option.min ?? 0); range.dataset.max=String(option.max ?? 100); range.setAttribute('aria-label',title); range.addEventListener('input',()=>{ numeric.textContent=String(Math.round(Number(range.value)*100)/100); });
      const reset=element('button','link appearance-reset','Reset'); reset.type='button'; reset.disabled=!value; reset.dataset.appearanceReset='blendshape'; Object.assign(reset.dataset,{rendererId:String(option.rendererId ?? ''),index:String(option.index)});
      const right=element('div','appearance-control'); right.append(range,numeric,reset); field.append(label,right); group.append(field);
    });
    controls.append(group);
  }
}
function setAppearanceStatus(message, error=false) { appearanceSaveError=error ? message : ''; if ($('#appearance-status')) $('#appearance-status').textContent=message; if ($('#appearance-warning')) { $('#appearance-warning').classList.toggle('hidden',!error); $('#appearance-warning').textContent=error ? message : ''; } }
function queueAppearanceSave(recipe) {
  const id=workspaceId(); if (!id || !appState.workspace?.desired) return;
  appearanceFailedDraft=null; appearancePending={workspaceId:id,targetKey:targetIdentity(),recipe:cloneRecipe(recipe),sequence:++appearanceSaveSequence};
  appearanceSaveError=''; clearTimeout(appearanceSaveTimer); appearanceSaveTimer=setTimeout(flushAppearanceSave,200); if ($('#appearance-status')) $('#appearance-status').textContent='Draft change pending…'; if ($('#sync')) $('#sync').disabled=true;
}
async function flushAppearanceSave() {
  appearanceSaveTimer=null; const pending=appearancePending; if (!pending || pending.workspaceId!==workspaceId()) return;
  if (appearanceSaveInFlight) return;
  appearancePending=null; const space=appState.workspace, expectedRevision=space?.desired?.revision || 0, sentSequence=pending.sequence;
  appearanceSaveInFlight={workspaceId:pending.workspaceId,targetKey:pending.targetKey,sequence:sentSequence}; setAppearanceStatus('Saving draft…');
  try {
    const result=await api(`/api/workspaces/${pending.workspaceId}/desired`,{method:'POST',body:JSON.stringify(desiredPayload({desired:{revision:expectedRevision}},pending.recipe))});
    if (pending.workspaceId!==workspaceId() || pending.targetKey!==targetIdentity()) return;
    const latest=appearancePending;
    const saved=result?.desired || result;
    if (saved && typeof saved==='object') {
      const revision=saved.revision ?? nextRevision(expectedRevision);
      appState.workspace.desired={...appState.workspace.desired,revision};
      if (!latest || latest.sequence===sentSequence) appState.workspace.desired.recipe=saved.recipe || pending.recipe;
      if (!latest || latest.sequence===sentSequence) appearanceFailedDraft=null;
    } else { appState.workspace.desired.revision=nextRevision(expectedRevision); if (!latest || latest.sequence===sentSequence) appearanceFailedDraft=null; }
    setAppearanceStatus(latest ? 'Saving latest draft…' : 'Draft saved');
  } catch (error) {
    if (pending.workspaceId===workspaceId() && pending.targetKey===targetIdentity()) { appearanceFailedDraft={workspaceId:pending.workspaceId,targetKey:pending.targetKey,recipe:pending.recipe}; setAppearanceStatus(`Draft could not be saved: ${error.message}`,true); }
  } finally {
    appearanceSaveInFlight=null;
    if (appearancePending && appearancePending.workspaceId===workspaceId()) { clearTimeout(appearanceSaveTimer); appearanceSaveTimer=setTimeout(flushAppearanceSave,200); }
    renderState(); renderAppearance();
  }
}
function applyLocalAppearance(recipe, control=null) {
  if (!appState.workspace) return;
  appState.workspace.desired={...appState.workspace.desired,recipe:cloneRecipe(recipe)};
  const reset=control?.closest?.('.appearance-field')?.querySelector?.('.appearance-reset'); if (reset) reset.disabled=false;
  queueAppearanceSave(recipe);
}
function refreshProperties() {
  const id=workspaceId(); if (!id) return;
  const generation=++appearanceGeneration; appearanceRefreshing=true; appearanceError=''; appearanceInspectionWorkspaceId=id; renderAppearance();
  api(`/api/workspaces/${id}/inspect`).then(result=>{
    if (generation!==appearanceGeneration || id!==workspaceId()) return;
    appearanceInspection=result || {}; appearanceInspectionWorkspaceId=id; appearanceRefreshing=false; appearanceError='';
    if (result?.target && appState.workspace) appState.workspace.target=result.target;
    renderAppearance(); renderState();
  }).catch(error=>{
    if (generation!==appearanceGeneration || id!==workspaceId()) return;
    appearanceRefreshing=false; appearanceError=`Properties unavailable: ${error.message}`; appearanceInspection=null; renderAppearance();
  });
}
function integrationIsInstalled(manifest) { return manifest?.installed===true || manifest?.state==='installed'; }
function integrationIsAvailable(manifest) { return manifest?.available!==false && (manifest?.builtin || manifest?.packageManagerAvailable!==false); }
function integrationStatus(manifest) {
  if (!integrationIsAvailable(manifest)) return {text:'Unavailable on this host',className:'disabled'};
  if (manifest?.builtin && integrationIsInstalled(manifest)) return {text:'Built-in · ready',className:'ready'};
  if (integrationIsInstalled(manifest)) return {text:`Installed${manifest.installedVersion ? ` · ${manifest.installedVersion}` : ''}`,className:'ready'};
  if (manifest?.installed===false) return {text:'Available · not installed',className:'disabled'};
  return {text:'Availability not confirmed',className:'disabled'};
}
function renderIntegrations() {
  const list=$('#integrations-list'); if (!list) return; clear(list);
  const integrations=appState.integrations || [];
  if (!integrations.length) { list.append(element('p','muted','No adapter integrations are available on this host.')); renderIntegrationPlan(); return; }
  integrations.forEach(manifest=>{
    const card=element('article','manifest-card'), status=integrationStatus(manifest), title=manifest.name || manifest.id || 'Unnamed integration';
    card.append(element('h3','',title));
    card.append(element('p','manifest-meta',`${manifest.origin || 'community'} · protocol ${manifest.protocol || manifest.sdk?.protocol || 'unknown'}${manifest.version ? ` · v${manifest.version}` : ''}`));
    if (manifest.description) card.append(element('p','small',manifest.description));
    if (manifest.package) card.append(element('p','small',`Package: ${manifest.package.id || 'unknown'}${manifest.package.version ? ` · ${manifest.package.version}` : ''}${manifest.package.repository ? ` · ${manifest.package.repository}` : ''}`));
    if (Array.isArray(manifest.dependencies) && manifest.dependencies.length) card.append(element('p','small',`Dependencies: ${manifest.dependencies.map(dep=>typeof dep==='string' ? dep : `${dep.id || 'package'} ${dep.version || ''}`).join(', ')}`));
    if (manifest.packageManagerAvailable!==undefined) card.append(element('p','manifest-meta',`vrc-get: ${manifest.packageManagerAvailable ? 'available' : 'unavailable'}${manifest.unavailableReason ? ` · ${manifest.unavailableReason}` : ''}`));
    const statusLine=element('p',`integration-status ${status.className}`,status.text); card.append(statusLine);
    const actions=element('div','manifest-actions');
    const canChange=!manifest.builtin, plan=element('button',integrationIsInstalled(manifest) ? 'ghost integration-plan-start' : 'button integration-plan-start',manifest.builtin && integrationIsInstalled(manifest) ? 'Built-in' : integrationIsInstalled(manifest) ? 'Plan removal' : 'Plan installation'); plan.type='button'; plan.dataset.integrationId=manifest.id; plan.dataset.integrationAction=integrationIsInstalled(manifest) ? 'remove' : 'install'; plan.disabled=!integrationIsAvailable(manifest) || !canChange; actions.append(plan);
    const actionList=element('div','manifest-action-list');
    (manifest.actions || []).forEach(action=>{
      if (!action?.id) return;
      const button=element('button','link adapter-action',action.name || action.id); button.type='button'; button.dataset.integrationId=manifest.id; button.dataset.actionId=action.id; button.disabled=!integrationIsInstalled(manifest) || !integrationIsAvailable(manifest); button.title=button.disabled ? 'Install this integration before running its declared actions.' : `${action.class || 'declared'} action`; actionList.append(button);
    });
    if (actionList.childNodes?.length) actions.append(actionList);
    card.append(actions); list.append(card);
  });
  renderIntegrationPlan();
}
function integrationPlanId() { return integrationPlan?.planId || integrationPlan?.plan?.planId || integrationPlan?.plan?.id || integrationPlan?.result?.planId || integrationPlan?.result?.id || null; }
function renderIntegrationPlan() {
  const panel=$('#integration-plan'), body=$('#integration-plan-body'), title=$('#integration-plan-title'), state=$('#integration-plan-state'), apply=$('#integration-plan-apply');
  if (!panel || !body) return;
  clear(body);
  if (!integrationPlan) { panel.classList.add('hidden'); if (apply) apply.disabled=true; return; }
  panel.classList.remove('hidden');
  const result=integrationPlan.plan || integrationPlan.result || null, running=integrationPlan.state==='running' || integrationPlan.state==='pending';
  title.textContent=integrationPlan.action==='remove' ? 'Review integration removal' : 'Review integration installation';
  state.textContent=running ? 'Working' : result ? 'Ready for approval' : integrationPlan.state || 'Review';
  if (running) { body.append(element('p','muted small','Preparing a package plan. Nothing will be installed or removed until you approve it.')); if (apply) apply.disabled=true; return; }
  if (integrationPlan.error) { body.append(element('p','review-warning',integrationPlan.error)); if (apply) apply.disabled=true; return; }
  if (!result) { body.append(element('p','muted small','The integration plan is unavailable. Try again.')); if (apply) apply.disabled=true; return; }
  const warnings=result.warnings || [];
  const vrc=result.vrcGet || result.vrc_get || result.packageManager;
  body.append(reviewLine('Integration',integrationPlan.integrationName || integrationPlan.integrationId || 'Selected integration'));
  if (result.explanation) body.append(element('p','muted small',result.explanation));
  if (vrc) {
    const available=vrc.available===true || vrc.state==='available' || vrc.installed===true;
    body.append(reviewLine('vrc-get',vrc.state || (available ? 'Available' : 'Unavailable'),available ? '' : 'warning'));
    if (vrc.error) body.append(element('p','review-warning',String(vrc.error)));
  } else if (result.packageManagerAvailable!==undefined) body.append(reviewLine('vrc-get',result.packageManagerAvailable ? 'Available' : 'Unavailable',result.packageManagerAvailable ? '' : 'warning'));
  const packages=result.packages || result.packageChanges || result.operations || [];
  if (Array.isArray(packages) && packages.length) {
    const details=element('details','review-files'); details.append(element('summary','',`Show ${packages.length} package changes`)); const list=element('ul'); packages.forEach(pkg=>{ const text=typeof pkg==='string' ? pkg : `${pkg.action || pkg.state || 'change'} · ${pkg.id || pkg.package || pkg.name || 'package'}${pkg.version ? ` · ${pkg.version}` : ''}`; list.append(element('li','',text)); }); details.append(list); body.append(details);
  } else if (result.package || result.action) body.append(reviewLine('Package',`${result.action || 'change'} · ${result.package || result.id || 'package'}${result.version ? ` · ${result.version}` : ''}${result.current ? ` · currently ${result.current}` : ''}`));
  else body.append(element('p','muted small','No package changes were reported by the host.'));
  if (warnings.length) { const details=element('details','review-files'); details.open=true; details.append(element('summary','',`${warnings.length} warning${warnings.length===1?'':'s'}`)); const list=element('ul'); warnings.forEach(warning=>list.append(element('li','review-warning',typeof warning==='string' ? warning : warning?.message || 'The host reported a warning.'))); details.append(list); body.append(details); }
  if (apply) { apply.disabled=!integrationPlanId(); apply.dataset.planId=integrationPlanId() || ''; }
}
async function pollIntegrationPlan(jobId, requestId, id) {
  try {
    const job=await api(`/api/jobs/${encodeURIComponent(jobId)}`);
    if (requestId!==integrationGeneration || id!==workspaceId()) return;
    if (job.state==='running' || job.state==='pending') { integrationPlan={...integrationPlan,state:job.state}; renderIntegrationPlan(); setTimeout(()=>pollIntegrationPlan(jobId,requestId,id),500); return; }
    if (job.state==='failed') { integrationPlan={...integrationPlan,state:'failed',error:job.error || 'The integration plan failed.'}; renderIntegrationPlan(); return; }
    integrationPlan={...integrationPlan,state:job.state || 'succeeded',plan:job.result || {}}; renderIntegrationPlan();
  } catch (error) {
    if (requestId===integrationGeneration && id===workspaceId()) { integrationPlan={...integrationPlan,state:'failed',error:error.message}; renderIntegrationPlan(); }
  }
}
async function startIntegrationPlan(integrationId, action) {
  const id=workspaceId(); if (!id || !integrationId) return;
  const manifest=appState.integrations?.find(item=>item.id===integrationId); if (!manifest || !integrationIsAvailable(manifest)) return;
  const requestId=++integrationGeneration; integrationPlan={workspaceId:id,integrationId,integrationName:manifest.name || integrationId,action,state:'running'}; renderIntegrationPlan();
  try {
    const job=await api(`/api/workspaces/${id}/integration-plan`,{method:'POST',body:JSON.stringify({integrationId,action})});
    if (requestId!==integrationGeneration || id!==workspaceId()) return;
    integrationPlan={...integrationPlan,jobId:job.id,state:job.state || 'running'}; renderIntegrationPlan(); pollIntegrationPlan(job.id,requestId,id);
  } catch (error) { if (requestId===integrationGeneration && id===workspaceId()) { integrationPlan={...integrationPlan,state:'failed',error:error.message}; renderIntegrationPlan(); } }
}
async function applyIntegrationPlan() {
  const id=workspaceId(), planId=integrationPlanId(); if (!id || !planId) return;
  const requestId=++integrationGeneration; integrationPlan={...integrationPlan,state:'running'}; renderIntegrationPlan();
  try {
    const result=await api(`/api/workspaces/${id}/integration-apply`,{method:'POST',body:JSON.stringify({planId})});
    if (requestId!==integrationGeneration || id!==workspaceId()) return;
    integrationPlan={...integrationPlan,state:result.state || 'succeeded',jobId:result.id || integrationPlan.jobId}; renderIntegrationPlan();
    if (result.state==='running' && result.id) pollIntegrationApply(result.id,requestId,id);
    else { toast('Integration change accepted.'); await refresh(id); }
  } catch (error) { if (requestId===integrationGeneration && id===workspaceId()) { integrationPlan={...integrationPlan,state:'failed',error:error.message}; renderIntegrationPlan(); } }
}
async function pollIntegrationApply(jobId, requestId, id) {
  try {
    const job=await api(`/api/jobs/${encodeURIComponent(jobId)}`); if (requestId!==integrationGeneration || id!==workspaceId()) return;
    if (job.state==='running' || job.state==='pending') { integrationPlan={...integrationPlan,state:job.state}; renderIntegrationPlan(); setTimeout(()=>pollIntegrationApply(jobId,requestId,id),500); return; }
    if (job.state==='failed') { integrationPlan={...integrationPlan,state:'failed',error:job.error || 'The integration change failed.'}; renderIntegrationPlan(); return; }
    integrationPlan={...integrationPlan,state:'succeeded'}; renderIntegrationPlan(); toast('Integration change completed.'); await refresh(id);
  } catch (error) { if (requestId===integrationGeneration && id===workspaceId()) { integrationPlan={...integrationPlan,state:'failed',error:error.message}; renderIntegrationPlan(); } }
}
async function invokeAdapterAction(integrationId, actionId) {
  const id=workspaceId(), manifest=appState.integrations?.find(item=>item.id===integrationId); if (!id || !manifest || !integrationIsInstalled(manifest) || !integrationIsAvailable(manifest)) return;
  try { const result=await api(`/api/workspaces/${id}/adapter-action`,{method:'POST',body:JSON.stringify({integrationId,actionId,input:{}})}); toast(result.state==='running' || result.state==='queued' ? 'Adapter action queued.' : 'Adapter action accepted.'); await refresh(id); } catch (error) { toast(error.message); }
}
function recoveryReviewId() { return recoveryReview?.id || recoveryReview?.reviewId || recoveryReview?.result?.id || recoveryReview?.jobId || null; }
function recoveryMaterialLabel(entry) { const option=materialOptions().find(candidate=>sameMaterialOverride(entry,candidate)); return `${option?.name || entry?.rendererId || 'Material'} · ${option ? appearanceOptionLabel(option.property) : appearanceOptionLabel(entry?.property)} · slot ${entry?.slot}`; }
function recoveryBlendshapeLabel(entry) { const option=blendshapeOptions().find(candidate=>sameBlendshapeOverride(entry,candidate)); return `${option?.name || entry?.rendererId || 'Shape'} · index ${entry?.index}`; }
function recoveryRecipeList(title, entries, formatter) {
  if (!entries.length) return null;
  const details=element('details','review-files'); details.open=true; details.append(element('summary','',title)); const list=element('ul'); entries.slice(0,256).forEach(entry=>list.append(element('li','',formatter(entry)))); details.append(list); return details;
}
function renderRecoveryRecipe(recipe, title) {
  const panel=element('div','panel recovery-recipe'), summary=recoveryRecipeSummary(recipe), items=Array.isArray(recipe?.items) ? recipe.items : [], materials=Array.isArray(recipe?.appearance?.materials) ? recipe.appearance.materials : [], blendshapes=Array.isArray(recipe?.appearance?.blendshapes) ? recipe.appearance.blendshapes : [];
  panel.append(element('h4','',title),element('p','',`${summary.items} worn item${summary.items===1?'':'s'}`),element('p','',`${summary.materials} material override${summary.materials===1?'':'s'}`),element('p','',`${summary.blendshapes} shape override${summary.blendshapes===1?'':'s'}`));
  const itemDetails=recoveryRecipeList('Worn items',items,item=>`${item.name || item.assetId || 'Item'}${item.id ? ` · copy ${item.id}` : ''}${item.prefabGuid ? ` · prefab ${item.prefabGuid}` : ''}`); if (itemDetails) panel.append(itemDetails);
  const materialDetails=recoveryRecipeList('Material colors',materials,entry=>`${recoveryMaterialLabel(entry)} · ${colorToHex(entry.color)} · [${(entry.color || []).map(value=>Math.round(Number(value)*100)/100).join(', ')}]`); if (materialDetails) panel.append(materialDetails);
  const shapeDetails=recoveryRecipeList('Shape keys',blendshapes,entry=>`${recoveryBlendshapeLabel(entry)} · weight ${entry.value}`); if (shapeDetails) panel.append(shapeDetails);
  return panel;
}
function renderRecoveryReview() {
  const body=$('#recovery-body'), actions=$('#recovery-actions'), title=$('#recovery-title'); if (!body || !actions) return; clear(body); clear(actions);
  if (!recoveryReview) { title.textContent='Review Unity state'; body.append(element('p','muted small','Atelier will compare the current Unity state with your local draft before asking which one to keep.')); return; }
  title.textContent=recoveryReview.state==='running' ? 'Inspecting Unity state' : 'Choose the state to keep';
  if (recoveryReview.state==='running' || recoveryReview.state==='pending') { body.append(element('p','muted small','Reading the original target and revision. No state has been changed.')); return; }
  if (recoveryReview.error) { body.append(element('p','review-warning',recoveryReview.error)); return; }
  const result=recoveryReview.result || recoveryReview, actual=result.actualRecipe || result.actual || {}, desired=result.desiredRecipe || result.desired || appState.workspace?.desired?.recipe || {};
  body.append(element('p','muted small','Unity and the local draft differ. Choose explicitly which recipe should become the next desired draft.')); const recipes=element('div','recovery-recipe'); recipes.append(renderRecoveryRecipe(actual,'Current Unity state'),renderRecoveryRecipe(desired,'Local Atelier draft')); body.append(recipes);
  if (result.unityRevision!==undefined) body.append(reviewLine('Unity revision',String(result.unityRevision))); if (result.desiredRevision!==undefined) body.append(reviewLine('Draft revision',String(result.desiredRevision))); if (result.target) body.append(reviewLine('Target',`${result.target.sceneGuid || 'Scene'} · ${result.target.objectId || 'Object'}`)); if (Array.isArray(result.operations) && result.operations.length) body.append(reviewLine('Operations',`${result.operations.length} change${result.operations.length===1?'':'s'} available`));
  if (Array.isArray(result.warnings) && result.warnings.length) { result.warnings.forEach(warning=>body.append(element('p','review-warning',typeof warning==='string' ? warning : warning?.message || 'Unity reported a recovery warning.'))); }
  const keep=element('button','button accent','Keep my Atelier draft'); keep.type='button'; keep.dataset.recoveryDecision='keep-draft'; const use=element('button','ghost','Use Unity state'); use.type='button'; use.dataset.recoveryDecision='use-unity'; actions.append(keep,use);
}
async function pollRecoveryReview(jobId, requestId, id) {
  try {
    const job=await api(`/api/jobs/${encodeURIComponent(jobId)}`); if (requestId!==recoveryGeneration || id!==workspaceId()) return;
    if (job.state==='running' || job.state==='pending') { recoveryReview={...recoveryReview,state:job.state}; renderRecoveryReview(); setTimeout(()=>pollRecoveryReview(jobId,requestId,id),500); return; }
    if (job.state==='failed') { recoveryReview={...recoveryReview,state:'failed',error:job.error || 'The recovery review failed.'}; renderRecoveryReview(); return; }
    recoveryReview={...recoveryReview,state:job.state || 'succeeded',result:job.result || {}}; renderRecoveryReview();
  } catch (error) { if (requestId===recoveryGeneration && id===workspaceId()) { recoveryReview={...recoveryReview,state:'failed',error:error.message}; renderRecoveryReview(); } }
}
async function startRecoveryReview() {
  const id=workspaceId(); if (!id || !appState.workspace?.target) return;
  const requestId=++recoveryGeneration; recoveryReview={workspaceId:id,state:'running'}; renderRecoveryReview(); showDialog($('#recovery-dialog'));
  try {
    const job=await api(`/api/workspaces/${id}/recover-review`,{method:'POST',body:JSON.stringify({})}); if (requestId!==recoveryGeneration || id!==workspaceId()) return;
    recoveryReview={...recoveryReview,jobId:job.id,state:job.state || 'running'}; renderRecoveryReview(); pollRecoveryReview(job.id,requestId,id);
  } catch (error) { if (requestId===recoveryGeneration && id===workspaceId()) { recoveryReview={...recoveryReview,state:'failed',error:error.message}; renderRecoveryReview(); } }
}
async function applyRecoveryDecision(decision) {
  const id=workspaceId(), reviewId=recoveryReviewId(); if (!id || !reviewId || !['keep-draft','use-unity'].includes(decision)) return;
  const requestId=++recoveryGeneration; recoveryReview={...recoveryReview,state:'running',error:''}; renderRecoveryReview();
  try {
    const result=await api(`/api/workspaces/${id}/recover`,{method:'POST',body:JSON.stringify({reviewId,decision})}); if (requestId!==recoveryGeneration || id!==workspaceId()) return;
    if (result.state==='running' && result.id) { recoveryReview={...recoveryReview,jobId:result.id,state:'running'}; renderRecoveryReview(); pollRecoveryReview(result.id,requestId,id); return; }
    $('#recovery-dialog')?.close(); toast('Recovery decision accepted.'); await refresh(id);
  } catch (error) { if (requestId===recoveryGeneration && id===workspaceId()) { recoveryReview={...recoveryReview,state:'failed',error:error.message}; renderRecoveryReview(); } }
}
function boundedProjectValue(value, limit=12000) {
  if (value===undefined || value===null) return '';
  const text=typeof value==='string' ? value : JSON.stringify(value,null,2);
  return text.length>limit ? `${text.slice(0,limit)}\n…truncated by Atelier…` : text;
}
function renderProjectReview() {
  const body=$('#project-review-body'), button=$('#project-review-acknowledge'), title=$('#project-review-title'); if (!body) return; clear(body);
  if (!projectReview) { if (button) button.disabled=true; return; }
  const result=projectReview.result || projectReview, reviewId=result.id || projectReview.reviewId; if (title) title.textContent=projectReview.error ? 'Project review failed' : 'Review project files';
  if (projectReview.state==='reviewing') { body.append(element('p','muted small','Reading the saved project manifest. No files will be changed.')); if (button) button.disabled=true; return; }
  if (projectReview.error) { body.append(element('p','review-warning',projectReview.error)); if (button) button.disabled=true; return; }
  if (result.explanation) body.append(element('p','muted small',result.explanation));
  if (result.currentManifest!==undefined) { body.append(element('h3','project-review-heading','Current project manifest (bounded)')); const pre=element('pre','project-review-pre',boundedProjectValue(result.currentManifest)); body.append(pre); }
  if (Array.isArray(result.currentFiles)) {
    const files=result.currentFiles.slice(0,200), filesTruncated=Boolean(result.currentFilesTruncated || result.filesTruncated || files.length<result.currentFiles.length), changed=files.filter(file=>file?.state==='changed').length, missing=files.filter(file=>file?.state==='missing').length, unchanged=files.filter(file=>file?.state==='unchanged').length;
    body.append(reviewLine('Files observed',`${files.length}${filesTruncated ? '+' : ''} entries · ${changed} changed · ${missing} missing · ${unchanged} unchanged`,filesTruncated ? 'warning' : ''));
    const details=element('details','review-files'); details.append(element('summary','',`Show ${files.length} observed file${files.length===1?'':'s'}${filesTruncated ? ' (bounded/truncated)' : ''}`)); const list=element('ul'); files.forEach(file=>list.append(element('li','',`${file.path || 'project file'} · ${file.state || 'observed'}${file.sha256 ? ` · ${file.sha256}` : ''}`))); details.append(list); body.append(details);
    if (filesTruncated) body.append(element('p','review-warning','The host returned a bounded file review. Review the remaining project files in Unity before relying on this acknowledgement.'));
  }
  if (result.result!==undefined) { body.append(element('h3','project-review-heading','Operation result')); const pre=element('pre','project-review-pre',boundedProjectValue(result.result)); body.append(pre); }
  if (result.error || result.errors) { body.append(element('h3','project-review-heading','Errors')); const pre=element('pre','project-review-pre',boundedProjectValue(result.errors || result.error)); body.append(pre); }
  if (!result.currentManifest && !result.result && !result.error && !result.errors) body.append(element('p','muted small','The host returned no additional project details.'));
  if (button) { button.disabled=!reviewId; button.dataset.reviewId=reviewId || ''; }
}
async function reviewProjectFiles(operationId, id=workspaceId()) {
  if (!operationId || !id) return;
  projectReview={workspaceId:id,operationId,state:'reviewing'}; renderProjectReview(); showDialog($('#project-review-dialog'));
  try { const result=await api(`/api/workspaces/${id}/project-review`,{method:'POST',body:JSON.stringify({operationId})}); if (id!==workspaceId()) return; projectReview={...projectReview,result,state:'ready'}; renderProjectReview(); }
  catch (error) { if (id===workspaceId()) { projectReview={...projectReview,state:'failed',error:error.message}; renderProjectReview(); } }
}
async function acknowledgeProjectReview() {
  const id=workspaceId(), result=projectReview?.result || projectReview, reviewId=result?.id || projectReview?.reviewId; if (!id || !reviewId) return;
  try { await api(`/api/workspaces/${id}/project-acknowledge`,{method:'POST',body:JSON.stringify({reviewId})}); $('#project-review-dialog')?.close(); projectReview=null; toast('Current project files acknowledged. Inspect the avatar before making another change.'); await refresh(id); }
  catch (error) { projectReview={...projectReview,state:'failed',error:error.message}; renderProjectReview(); }
}
function render() {
  if (!doc) return; const id=workspaceId(), intended=requestedWorkspaceId || id;
  if (id !== intended) { currentWorkspaceId=intended; renderPendingWorkspace(); return; }
  if (id !== currentWorkspaceId) { currentWorkspaceId=id; importGeneration++; appearanceGeneration++; recoveryGeneration++; integrationGeneration++; pendingImport=null; appearanceInspection=null; appearanceInspectionWorkspaceId=null; appearanceError=''; appearanceSaveError=''; appearancePending=null; appearanceFailedDraft=null; clearTimeout(appearanceSaveTimer); appearanceSaveTimer=null; recoveryReview=null; integrationPlan=null; projectReview=null; blobs.clear(); resetViewport(); }
  renderState(); renderWorn(); renderLibraryGrid($('#library-grid')); renderLibraryGrid($('#studio-library-grid')); renderActivity(); renderJobs(); renderAppearance(); renderIntegrations(); renderProjectReview(); hydrateSnapshot(appState.workspace);
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
function switchWorkspace(id) { if (!id || id===workspaceId()) return refresh(id); requestedWorkspaceId=id; currentWorkspaceId=id; importGeneration++; appearanceGeneration++; recoveryGeneration++; integrationGeneration++; pendingImport=null; appearanceInspection=null; appearanceInspectionWorkspaceId=null; appearanceError=''; appearanceSaveError=''; appearancePending=null; appearanceFailedDraft=null; clearTimeout(appearanceSaveTimer); appearanceSaveTimer=null; recoveryReview=null; integrationPlan=null; projectReview=null; blobs.clear(); resetViewport(); refresh(id); }
async function refresh(id=requestedWorkspaceId || workspaceId()) { const generation=++refreshGeneration, requested=id, currentTarget=targetIdentity(), preserveFailed=appearanceFailedDraft?.workspaceId===requested && appearanceFailedDraft?.targetKey===currentTarget && appState.workspace?.id===requested, preservePending=appearancePending?.workspaceId===requested && appearancePending?.targetKey===currentTarget, preserveFlight=appearanceSaveInFlight?.workspaceId===requested && appearanceSaveInFlight?.targetKey===currentTarget, localDraft=(requested && (preservePending || preserveFlight || preserveFailed) && appState.workspace?.id===requested) ? cloneRecipe(preserveFailed ? appearanceFailedDraft.recipe : appState.workspace?.desired?.recipe || {}) : null; requestedWorkspaceId=requested; try { const state=await api(requested?`/api/state?workspace=${encodeURIComponent(requested)}`:'/api/state'); if (generation!==refreshGeneration) return; if (localDraft && state.workspace?.id===requested) state.workspace={...state.workspace,desired:{...state.workspace.desired,recipe:localDraft}}; appState=state; requestedWorkspaceId=workspaceId(); render(); } catch (error) { if(generation===refreshGeneration) { toast(error.message); render(); } } }
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
async function showComparison() { const id=workspaceId(), pair=comparisonPhotos(appState.workspace,appState.photos,cameraView); if(!pair) return; const {after,before,view}=pair; try { const [afterUrl,beforeUrl]=await Promise.all([blobs.load(`${id}:${after.id || after.artifactId}`,after.url,token?{'X-Atelier-Token':token}:{}),blobs.load(`${id}:${before.id || before.artifactId}`,before.url,token?{'X-Atelier-Token':token}:{} )]); if(id!==workspaceId()) return; $('#before-image').src=beforeUrl; $('#after-image').src=afterUrl; $('#before-caption').textContent=`Before · Unity state · ${photoCaption(before,view)}`; $('#after-caption').textContent=`After · current revision · ${photoCaption(after,view)}`; showDialog($('#compare-dialog')); } catch(error) { toast(error.message); } }
function exportSnapshot() { if(!displayedArtifact?.url) return; const link=element('a'); link.href=displayedArtifact.url; link.download='atelier-snapshot.png'; link.click(); }

doc?.addEventListener('click', async event => { const button=event.target.closest('button'); if(!button) return;
  const nav=button.closest('.rail-item[data-view]'); if(nav) return changeView(nav.dataset.view);
  if(button.matches('#register,#register-inline')) return showDialog($('#register-dialog'));
  if(button.matches('#open-unity')) return worker('open-unity'); if(button.matches('#provision-worker')) return worker('provision'); if(button.matches('#worker-action')) return worker(['online','interactive'].includes(appState.worker?.state)?'stop':'start');
  if(button.matches('#refresh-properties')) return refreshProperties(); if(button.matches('#review-recovery')) return startRecoveryReview();
  if(button.matches('.appearance-reset')) { const recipe=appState.workspace?.desired?.recipe; if(!recipe) return; const option=appearanceOptionFromElement(button); applyLocalAppearance(button.dataset.appearanceReset==='material' ? resetMaterialOverride(recipe,option) : resetBlendshapeOverride(recipe,option)); button.disabled=true; return; }
  if(button.matches('[data-recovery-decision]')) return applyRecoveryDecision(button.dataset.recoveryDecision);
  if(button.matches('.project-review')) return reviewProjectFiles(button.dataset.operationId,button.dataset.workspaceId || workspaceId()); if(button.matches('#project-review-acknowledge')) return acknowledgeProjectReview(); if(button.matches('#project-review-close')) { $('#project-review-dialog')?.close(); return; }
  if(button.matches('.integration-plan-start')) { showDialog($('#integrations-dialog')); return startIntegrationPlan(button.dataset.integrationId,button.dataset.integrationAction); }
  if(button.matches('#integration-plan-apply')) return applyIntegrationPlan(); if(button.matches('#integration-plan-cancel')) { integrationPlan=null; renderIntegrationPlan(); return; }
  if(button.matches('.adapter-action')) return invokeAdapterAction(button.dataset.integrationId,button.dataset.actionId);
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
function handleAppearanceControl(event) {
  const control=event.target.closest('[data-appearance-kind]'); if(!control || !appState.workspace?.desired) return;
  const option=appearanceOptionFromElement(control), recipe=appState.workspace.desired.recipe || {items:[],appearance:{}};
  applyLocalAppearance(control.dataset.appearanceKind==='material' ? setMaterialOverride(recipe,option,hexToColor(control.value,Number(control.dataset.alpha || 1))) : setBlendshapeOverride(recipe,option,Number(control.value)),control);
}
doc?.addEventListener('input',handleAppearanceControl);
doc?.addEventListener('change',handleAppearanceControl);
$('#register-form')?.addEventListener('submit',event=>{ if(event.submitter?.value==='cancel') return; event.preventDefault(); register(); });
$('#archive-form')?.addEventListener('submit',async event=>{ if(event.submitter?.value==='cancel') return; event.preventDefault(); const path=$('#archive-path').value.trim(); if(!path) return; try { await api('/api/library',{method:'POST',body:JSON.stringify({path})}); $('#archive-dialog').close(); toast('Archive is being added to the local library.'); await refresh(workspaceId()); } catch(error) { toast(error.message); } });
$('#prefab-form')?.addEventListener('submit',async event=>{ if(event.submitter?.value==='cancel') return; event.preventDefault(); const index=Number($('input[name="prefab"]:checked')?.value), prefab=selectedPrefab?.asset?.prefabs?.[index]; if(!prefab) return; try { await applyAsset(selectedPrefab.asset,prefab,selectedPrefab.replaceId); $('#prefab-dialog').close(); selectedPrefab=null; } catch(error) { toast(error.message); } });
$('#target-form')?.addEventListener('submit',async event=>{ if(event.submitter?.value==='cancel') return; event.preventDefault(); const index=Number($('input[name="target"]:checked')?.value), target=selectedTarget?.[index], id=workspaceId(); if(!target||!id) return; try { await api(`/api/workspaces/${id}/target`,{method:'POST',body:JSON.stringify(target)}); appearanceGeneration++; appearanceInspection=null; appearanceInspectionWorkspaceId=null; appearanceError=''; appearancePending=null; appearanceFailedDraft=null; clearTimeout(appearanceSaveTimer); appearanceSaveTimer=null; $('#target-dialog').close(); await refresh(id); } catch(error) { toast(error.message); } });
$('#import-form')?.addEventListener('submit',event=>{ if(event.submitter?.value==='cancel') return; event.preventDefault(); applyReviewedImport(); });
$('#prefab-dialog')?.addEventListener('close',()=>{ selectedPrefab=null; });
$('#allow-code')?.addEventListener('change',event=>{ const summary=importReviewSummary(pendingImport?.plan); $('#apply-import').disabled=Boolean(summary.conflicts || summary.guidConflicts || (summary.codeFiles && !event.target.checked)); });
if (typeof window !== 'undefined') window.addEventListener('beforeunload',()=>blobs.clear());
if(doc) { setInterval(()=>refresh(requestedWorkspaceId || workspaceId()),1000); refresh(); }
if(typeof module!=='undefined') module.exports={getToken,stateLabel,nextRevision,escapeHtml,desiredPayload,isFreshResponse,importReviewSummary,comparisonPhotos,clampColor,colorToHex,hexToColor,cloneRecipe,setMaterialOverride,resetMaterialOverride,setBlendshapeOverride,resetBlendshapeOverride,appearanceOptionLabel,recoveryRecipeSummary,BlobUrlCache};
