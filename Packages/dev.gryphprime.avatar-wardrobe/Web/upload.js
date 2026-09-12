/* Preset/upload UI extracted from the supplied 1.0 page; same Unity endpoints. */
(function(global){
  "use strict";
  // Drafts use the installed avatar's GlobalObjectId, never its display name or
  // source prefab GUID (several scene avatars can share either of those).
  function draftIdentity(installed, transientContext){
    if(installed&&installed.projectId&&installed.avatarId)
      return {key:JSON.stringify([installed.projectId,installed.avatarId]),persistent:!installed.avatarId.startsWith('session:')};
    return {key:'transient:'+transientContext,persistent:false};
  }
  function createDraftStore(storage){
    var memory={};
    return {
      read:function(scope){
        if(!scope)return null;
        var json=memory[scope.key]||'null';
        try{if(scope.persistent&&storage)json=storage.getItem('wardrobe.uploadDraft.'+scope.key)||json;}catch(error){}
        try{return JSON.parse(json);}catch(error){return null;}
      },
      write:function(scope,value){
        if(!scope)return;
        var key='wardrobe.uploadDraft.'+scope.key;
        if(!value){delete memory[scope.key];try{if(scope.persistent&&storage)storage.removeItem(key);}catch(error){}return;}
        var json=JSON.stringify(value);memory[scope.key]=json;
        try{if(scope.persistent&&storage)storage.setItem(key,json);}catch(error){}
      }
    };
  }
  function presetReview(state,ids,avatarName){
    function failure(key,message,name){var error=new Error(message);error.copyKey=key;error.copyArgs=name?[name]:[];return error;}
    var presets=ids.map(function(id){return (state.presets||[]).find(function(p){return p.id===id;});});
    if(!presets.length||presets.some(function(p){return !p;}))throw failure('review.missing','A selected preset is no longer available. Refresh and choose it again.');
    if(state.batchActive)throw failure('review.busy','An upload is already running. Wait for it to finish.');
    var rows=presets.map(function(p){
      var platforms=[p.win?'Windows':'',p.and?'Android':'',p.ios?'iOS':''].filter(Boolean);
      if(!platforms.length)throw failure('review.noPlatform','Choose at least one platform for '+p.name+' before uploading.',p.name);
      var blueprint=String(p.blueprintId||'').trim();
      if(blueprint&&!/^avtr_[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(blueprint))throw failure('review.invalidId','Save a valid Avatar ID for '+p.name+' before uploading.',p.name);
      return {id:p.id,name:p.name,platforms:platforms,blueprint:blueprint,configuration:p};
    });
    return {avatar:avatarName||state.avatarRoot||'Selected avatar',presets:rows,defaults:state.defaults||{}};
  }
  function uploadConfigWrite(path){
    var endpoint=path.split('?')[0].split('/').pop(),query=path.split('?')[1]||'';
    var opMatch=query.match(/(?:^|&)op=([^&]*)/),op=opMatch?decodeURIComponent(opMatch[1]):'';
    var mutation=op&&op!=='get';
    if(['batch_defaults_set','batch_config_set','batch_preset_config','preset_include','preset_save','preset_delete','preset_remove_item','preset_show','batch_preset_from_scene','batch_import'].includes(endpoint))return true;
    if(endpoint==='batch_preset_blends')return !!mutation||/(?:^|&)(bs|weight|pinned)=/.test(query);
    if(endpoint==='batch_preset_items')return !!mutation||/(?:^|&)(item|include)=/.test(query);
    if(['menu_groups','batch_preset_faceemo','batch_item'].includes(endpoint))return !!mutation;
    return false;
  }
  global.WardrobeUploadModels={draftIdentity:draftIdentity,createDraftStore:createDraftStore,presetReview:presetReview,uploadConfigWrite:uploadConfigWrite};
  global.WardrobeUploadCopy={
  "upload.draft.discard": "Discard changes",
  "upload.draft.discardConfirm": "Discard unsaved Defaults and Avatar ID changes for this avatar?",
  "upload.defaults.identity": "Upload identity",
  "upload.defaults.hint": "Defaults apply when presets are uploaded. Save changes with the Save button above.",
  "upload.defaults.name": "Name template",
  "upload.defaults.description": "Description template",
  "upload.defaults.release": "Release",
  "upload.defaults.private": "Private",
  "upload.defaults.public": "Public",
  "upload.defaults.tags": "Tags",
  "upload.defaults.version": "Description version text",
  "upload.defaults.versionReplace": "Replace description",
  "upload.defaults.versionAppend": "Append a line",
  "upload.defaults.thumbnail": "Thumbnail",
  "upload.defaults.capture": "Capture method",
  "upload.defaults.captureAuto": "Automatic front view",
  "upload.defaults.captureScene": "Current Unity Scene view",
  "upload.defaults.captureImage": "Image file",
  "upload.defaults.imagePath": "Image path",
  "upload.defaults.imageHelp": "Use a project path such as Assets/Thumbnails/avatar.png, or an absolute image path on the computer running Unity. Missing files fall back to an avatar capture.",
  "upload.defaults.background": "Background color",
  "upload.defaults.colorHelp": "Hex color: RRGGBB or RRGGBBAA, with or without #.",
  "upload.defaults.automation": "Advanced automation",
  "upload.defaults.sps": "Detect SPS/DPS content",
  "upload.defaults.spsHelp": "Adds the Sexually Suggestive tag when SPS or DPS components are detected.",
  "upload.defaults.sdkFix": "Apply suggested SDK fixes",
  "upload.defaults.sdkFixHelp": "Attempts to accept fixes offered by VRChat SDK alerts during setup.",
  "upload.defaults.consent": "Confirm SDK ownership dialog in Unity",
  "upload.defaults.consentHelp": "Automatically confirms the SDK copyright and ownership dialog in the Unity setup flow. Browser preset uploads confirm this dialog automatically.",
  "upload.defaults.optimization": "Texture optimization",
  "upload.defaults.optimize": "Optimize textures when creating a new avatar",
  "upload.defaults.optimizeHelp": "Reduces texture import resolution to lower video memory use during Express setup.",
  "upload.defaults.optimizeAsk": "Ask before optimizing in Unity",
  "upload.defaults.optimizeAskHelp": "Browser uploads apply enabled optimization without a separate prompt.",
  "upload.defaults.maxResolution": "Maximum resolution (pixels)",
  "upload.defaults.minResolution": "Minimum resolution (pixels)",
  "upload.defaults.includeShared": "Include shared items",
  "upload.defaults.invalidColor": "Enter a background color as RRGGBB or RRGGBBAA.",
  "upload.defaults.invalidResolution": "Minimum texture resolution cannot exceed the maximum.",
  "upload.review.title": "Review preset upload",
  "upload.review.back": "Back to presets",
  "upload.review.create": "Create a new VRChat avatar",
  "upload.review.warning": "This upload cannot be cancelled once it starts. Unity will stage and upload the selected presets in order.",
  "upload.review.changed": "Settings changed while this review was open. Check the updated details before uploading.",
  "upload.review.dirty": "Save or discard the unsaved changes shown above before reviewing an upload.",
  "upload.review.saving": "Settings are still saving. Wait for them to finish, then review the upload again.",
  "upload.review.busy": "An upload is already running. Wait for it to finish.",
  "upload.review.sdk": "Connect the VRChat SDK and sign in before uploading.",
  "upload.review.avatarChanged": "The dressing avatar changed. Close this review and choose the upload again.",
  "upload.review.connectionChanged": "The avatar connection changed. Close this review and try again.",
  "upload.preset.commonHelp": "Items here are included with every preset.",
  "upload.preset.remove": "Remove preset",
  "upload.preset.noPlatform": "No platform selected"
};
  Object.assign(global.WardrobeUploadCopy,{
    'upload.draft.ids':'Avatar IDs ({0})',
    'upload.draft.unsaved':'Unsaved changes: {0}. Save them before uploading.',
    'upload.draft.restored':'Restored unsaved changes: {0}. Save them before uploading.',
    'upload.draft.kept':'Draft kept for this avatar in this browser tab.',
    'upload.draft.temporary':'Draft kept while this avatar session is connected.',
    'upload.review.avatarLabel':'Dressing avatar:',
    'upload.review.oneSelected':'1 preset selected. Common Preset items are included.',
    'upload.review.manySelected':'{0} presets selected. Common Preset items are included with each.',
    'upload.review.update':'Update existing avatar:',
    'upload.review.release':'Release setting:',
    'upload.review.uploadOne':'Upload 1 preset',
    'upload.review.uploadMany':'Upload {0} presets',
    'upload.preset.sharedCount':'Shared items: {0}',
    'upload.preset.itemCount':'Items: {0}',
    'upload.preset.include':'Include in batch upload',
    'upload.review.missing':'A selected preset is no longer available. Refresh and choose it again.',
    'upload.review.noPlatform':'Choose at least one platform for {0} before uploading.',
    'upload.review.invalidId':'Save a valid Avatar ID for {0} before uploading.',
    'upload.review.unappliedItems':'Unapplied item edits: {0}. Review and apply or discard them before uploading.',
    'upload.review.reviewItemEdits':'Review item edits'
  });
  global.WardrobeUpload=function(options){
    var request=options.api,avatarContext="",contextRevision=0,pendingConfigWrites=0;
    function api(path,opts){
      var revision=contextRevision;
      var writing=uploadConfigWrite(path);
      if(writing)pendingConfigWrites++;
      return request(path,opts).then(function(result){
        if(revision!==contextRevision)throw new Error("Avatar changed; previous response discarded.");
        return result;
      }).finally(function(){if(writing)pendingConfigWrites--;});
    }
    var T=options.T,toast=options.toast,esc=options.esc,spinner=options.spinner;
    function U(key){key="upload."+key;var value=T(key);if(value===key)value=global.WardrobeUploadCopy[key]||key;for(var i=1;i<arguments.length;i++)value=value.split('{'+(i-1)+'}').join(arguments[i]);return value;}
  var separateUploads=false,sdkReady=false,sdkLoggedIn=false,sdkKnown=false;
  function paintSdkReadiness(){
    var notice=upEl("upSdkNotice");
    notice.hidden=sdkKnown&&sdkReady;
    var message=T(!sdkKnown?'upload.sdk.checking':!sdkLoggedIn?'upload.sdk.login':'upload.sdk.builder');
    R.text(notice,message);
    document.querySelectorAll('#upAll,[data-pact="upload"]').forEach(function(button){
      button.disabled=!sdkReady;button.title=sdkReady?'':message;
    });
  }
  var BS=null, upTab="presets", upJobTimer=null, upJobPolling=false, upStateFlight=null, upJobToken=0,upRunning=false;
  var R=global.WardrobeRuntime, defaultsDirty=false, defaultsSignature="", unassignedToken=0;
  var upEl=function(id){ return document.getElementById(id); };
  var draftStorage;try{draftStorage=global.sessionStorage;}catch(error){}
  var draftStore=createDraftStore(draftStorage),draftScope=null,defaultsPendingDraft=null,draftRestored=false,reviewAvatarName='';
  function formValues(){var values={};upEl('upDefsForm').querySelectorAll('input,select').forEach(function(input){values[input.id||'tag:'+input.dataset.dtag]=input.type==='checkbox'?input.checked:input.value;});return values;}
  function saveDraft(){
    if(!draftScope)return;
    var defaults=defaultsDirty?(defaultsPendingDraft||formValues()):null;
    draftStore.write(draftScope,defaults||Object.keys(upBlueprintDrafts).length?{defaults:defaults,blueprints:upBlueprintDrafts}:null);
    paintDraftStatus();
  }
  function paintDraftStatus(){
    var status=upEl('upDraftStatus');
    if(!status){status=document.createElement('div');status.id='upDraftStatus';status.className='up-draft-status';status.setAttribute('role','status');status.innerHTML=("<span></span><button type=\"button\">"+esc(U("draft.discard"))+"</button>");upEl('upload').insertBefore(status,upEl('upSdkNotice'));
      status.querySelector('button').onclick=function(){if(!confirm(U("draft.discardConfirm")))return;defaultsDirty=false;defaultsPendingDraft=null;defaultsSignature='';upBlueprintDrafts={};draftRestored=false;draftStore.write(draftScope,null);renderDefs();renderPresets();paintDraftStatus();};}
    var count=Object.keys(upBlueprintDrafts).length;status.hidden=!defaultsDirty&&!count;
    status.querySelector('button').textContent=U('draft.discard');
    var parts=[];if(defaultsDirty)parts.push(T('nav.defaults'));if(count)parts.push(U('draft.ids',count));
    status.querySelector('span').textContent=U(draftRestored?'draft.restored':'draft.unsaved',parts.join(' · '))+' '+U(draftScope&&draftScope.persistent?'draft.kept':'draft.temporary');
  }
  function loadDraftScope(installed){
    var next=draftIdentity(installed,avatarContext);reviewAvatarName=installed.avatarName||'';
    if(draftScope&&draftScope.key===next.key)return;
    draftScope=next;var saved=draftStore.read(next);
    defaultsPendingDraft=saved&&saved.defaults||null;defaultsDirty=!!defaultsPendingDraft;
    upBlueprintDrafts=saved&&saved.blueprints||{};draftRestored=!!saved;defaultsSignature='';
    paintDraftStatus();
  }
  function upSetTab(t){
    upTab=t;
    upEl("upTabPresets").classList.toggle("on",t==="presets");
    upEl("upTabDefs").classList.toggle("on",t==="defs");
    upEl("upPresets").hidden=t!=="presets";
    upEl("upDefs").hidden=t!=="defs";
    if(t==="presets"){ renderPresets(); renderUnassigned(); }
    else renderDefs();
  }
  upEl("upTabPresets").onclick=function(){ upSetTab("presets"); };
  upEl("upTabDefs").onclick=function(){ upSetTab("defs"); };
  function upRefreshState(){
    if(upStateFlight) return upStateFlight;
    var revision=contextRevision;
    upStateFlight=Promise.all([api("/api/batch_state"),api("/api/installed")]).then(function(results){
      var d=results[0],installed=results[1];
      if(!d||!d.ok){ toast((d&&d.message)||T("upload.failed"),"err"); return null; }
      loadDraftScope(installed||{});
      d.common={id:'common',name:T('preset.common'),members:((installed&&installed.items)||[]).filter(function(item){return !item.target||item.target==='common';}).map(function(item){return {guid:item.guid,path:item.path||'',name:item.family+(item.variant&&item.variant!=='Default'?' — '+item.variant:'')};})};
      BS=d;
      if(upTab==="presets"){ renderPresets(); renderUnassigned(); }
      else renderDefs();
      return d;
    }).catch(function(){ if(revision===contextRevision)toast(T("upload.failed"),"err"); return null; }).finally(function(){if(revision===contextRevision)upStateFlight=null;});
    return upStateFlight;
  }
  function upShowJob(label,canCancel){
    upRunning=true;
    upEl("upJob").hidden=false;
    upEl("upJobLabel").textContent=label;
    upEl("upJobBar").style.width="0%";
    upEl("upJobMsg").textContent="";
    upEl("upJobCancel").style.display=canCancel?"":"none";
    upEl("upJobClose").style.display="none";
    upEl("upJobSpin").innerHTML=spinner(16);
  }
  function upStopPoll(){ if(upJobTimer){ clearInterval(upJobTimer); upJobTimer=null; } }
  function upEndJob(ok,msg){
    upRunning=false;
    upEl("upJobSpin").innerHTML="";
    upEl("upJobLabel").textContent=T(ok?"upload.complete":"upload.finishedErrors");
    if(ok)upEl("upJobBar").style.width="100%";
    upEl("upJobMsg").style.whiteSpace="pre-line";
    upEl("upJobMsg").textContent=msg||"";
    upEl("upJobCancel").style.display="none";
    upEl("upJobClose").style.display="";
    toast(msg||(ok?"OK":"Failed"),ok?"ok":"err");
    upRefreshState();
  }
  function upStartJob(url,label,canCancel,onDone){
    upShowJob(label,canCancel);
    var token=++upJobToken;
    var requestId=crypto.randomUUID().replace(/-/g, "");
    url+=(url.indexOf("?")>=0?"&":"?")+"requestId="+encodeURIComponent(requestId);
    function handleResult(q){
      if(token!==upJobToken||!q)return;
      upEl("upJobMsg").textContent="";
      if(q.total>0)upEl("upJobBar").style.width=Math.round(100*q.index/Math.max(1,q.total))+"%";
      if(q.current)upEl("upJobLabel").textContent=label+" - "+q.current;
      if(q.done){upStopPoll();upEndJob(!!q.ok,q.message);if(onDone)onDone(q);}
    }
    function followJob(r){
      if(token!==upJobToken)return;
      if(!r||!r.ok||!r.job){ upEndJob(false,(r&&r.message)||T("upload.failed")); return; }
      var job=r.job;
      upStopPoll();
      upJobTimer=setInterval(function(){
        if(upJobPolling) return;
        upJobPolling=true;
        request("/api/batch_job?job="+encodeURIComponent(job)).then(function(q){
          handleResult(q);
        }).catch(function(){
          // Builds can occupy Unity for a long time. Preserve progress and retry
          // quietly; only an explicit job result can finish the upload UI.
        }).finally(function(){upJobPolling=false;});
      },2000);
    }
    request(url,{timeout:120000}).then(followJob).catch(function(){
      if(token!==upJobToken)return;
      // Recover status only; never resend the upload command. Unity being busy
      // is expected, so keep the existing progress display while reconnecting.
      upStopPoll();
      function recoverJob(){
        if(token!==upJobToken||upJobPolling)return;
        upJobPolling=true;
        request("/api/batch_job?job="+encodeURIComponent(requestId)).then(function(q){
          if(token!==upJobToken)return;
          if(q&&q.done)handleResult(q);
          else if(q&&q.job===requestId)followJob({ok:1,job:q.job});
        }).catch(function(){}).finally(function(){upJobPolling=false;});
      }
      upJobTimer=setInterval(recoverJob,5000);
      recoverJob();
    });
  }
  upEl("upJobCancel").onclick=function(){ api("/api/batch_cancel").catch(function(){}); };
  upEl("upJobClose").onclick=function(){ upStopPoll(); upEl("upJob").hidden=true; };
  function upOpenModal(title,html){
    upEl("upModalTitle").textContent=title;
    var body=upEl("upModalBody");
    if(typeof html==="string") body.innerHTML=html;
    else { body.innerHTML=""; body.appendChild(html); }
    upEl("upModal").hidden=false;
    R.openDialog(upEl("upModal").querySelector("[role=dialog]"));
  }
  function closeUploadModal(){
    upEl("upModal").hidden=true;
    upEl("upModalBody").innerHTML="";
    R.closeDialog(upEl("upModal").querySelector("[role=dialog]"));
  }
  upEl("upModalClose").onclick=closeUploadModal;
  upEl("upModal").addEventListener("click",function(event){if(event.target===this)closeUploadModal();});
  upEl("upModal").addEventListener("keydown",function(event){if(event.key==="Escape"){event.stopPropagation();closeUploadModal();}});
  var upExpanded={},upPanelOpen={},upBlueprintDrafts={};
  function upFindPreset(id){
    if(!BS) return null;
    if(id==='common') return BS.common;
    for(var i=0;i<BS.presets.length;i++) if(BS.presets[i].id===id) return BS.presets[i];
    return null;
  }
  function renderPresets(){
    var list=upEl("upList"), st=upEl("upStatus");
    upEl("upAll").hidden=!separateUploads;
    upEl("upTabDefs").hidden=!separateUploads;
    upEl("upUnassignedHeading").hidden=true;
    upEl("upUnassigned").hidden=true;
    upEl("upNew").hidden=!separateUploads;
    upEl("upPresetHint").hidden=!separateUploads;
    var heading=document.querySelector('#upPresets h2');heading.removeAttribute('data-i18n');R.text(heading,separateUploads?T('upload.presetsTitle'):'Installed items');
    document.querySelector('#upload .up-tabs').hidden=!separateUploads;
    R.text(upEl("upPresetHint"),T("upload.presetsHint"));
    if(!BS){ st.textContent=T("upload.loading"); list.innerHTML=""; paintSdkReadiness();return; }
    st.textContent="";
    var presets=[BS.common].concat(separateUploads?(BS.presets||[]):[]);
    if(!presets.length){ list.innerHTML="<div class="+qq("up-empty")+">"+esc(T("upload.noSets"))+"</div>"; return; }
    R.reconcile(list,presets,function(p){return p.id;},function(){var node=document.createElement("div");node.className="up-card";return node;},function(node,p){
      if(Object.prototype.hasOwnProperty.call(upBlueprintDrafts,p.id)&&node.contains(document.activeElement)&&document.activeElement.hasAttribute("data-blueprint")) return;
      var open=!separateUploads||!!upExpanded[p.id];
      var signature=JSON.stringify(p)+"|"+open+"|"+separateUploads+"|"+T("upload.upload")+'|'+(upBlueprintDrafts[p.id]||'');
      if(node._signature===signature) return;
      node._signature=signature;node.dataset.preset=p.id;
      var html="";
      if(p.id==='common'){
        node.innerHTML=(separateUploads?'<div class="up-row up-preset-summary"><button class="up-name" data-pact="exp" aria-expanded="'+open+'">'+esc(T('preset.common'))+'</button><span class="up-preset-meta">'+esc(U('preset.sharedCount',(p.members||[]).length))+'</span></div>':'')+(open?'<div class="up-detail">'+(separateUploads?("<p class=\"subtle\">"+esc(U("preset.commonHelp"))+"</p>"):'')+'<section class="up-panel up-items-section" data-panel="items"><div data-itembody></div></section></div>':'');
        if(open)wirePresetPanels(p.id,node);return;
      }
      if(!separateUploads){
        node.innerHTML='<div class="up-row"><span class="up-name">'+esc(p.name)+'</span><button data-pact="rename">Rename</button><button class="danger" data-pact="removepreset">Remove</button><button data-pact="showunity">Show in Unity</button></div><section class="up-panel up-items-section" data-panel="items"><div data-itembody></div></section>';
        wirePresetPanels(p.id,node);
        return;
      }
      var plats=((p.win?"Windows ":"")+(p.and?"Android ":"")+(p.ios?"iOS":"")).replace(/ +$/,"");
      html+="<div class="+qq("up-row up-preset-summary")+">"
        
        +'<button class="up-name" data-pact="exp" aria-expanded="'+open+'">'+esc(p.name)+'</button>'
        +'<span class="up-preset-meta">'+esc(U('preset.itemCount',(p.members||[]).length))+' · '+esc(plats||U("preset.noPlatform"))+'</span>'
        
        +"<button data-pact="+qq("upload")+">"+esc(T("upload.upload"))+"</button>"
      html+='</div><label class="up-formrow"><input type="checkbox" role="switch" data-pinc'+(p.include?' checked':'')+'> '+esc(U('preset.include'))+'</label>';
      if(open) html+="<div class="+qq("up-detail")+"></div>";
      node.innerHTML=html;
      var detail=node.querySelector(".up-detail");
      if(detail) upFillPreset(p.id,detail);
    });
    paintSdkReadiness();
  }
  function qq(s){ return String.fromCharCode(34)+s+String.fromCharCode(34); }
  var upBlendCache={}, upItemCache={}, upBsSearch={}, upGroups={};
  function upFillPreset(id,box){
    var p=upFindPreset(id);
    if(!p){ box.innerHTML=""; return; }
    var h=("<div class=\"up-formrow\"><button data-pact=\"rename\">Rename</button><button data-pact=\"showunity\">Show in Unity</button><button class=\"danger\" data-pact=\"removepreset\">"+esc(U("preset.remove"))+"</button></div>");
    h+='<div class="up-formrow"><label><input type="checkbox" data-pcfg="win"'+(p.win?' checked':'')+'> Windows</label>';
    h+='<label><input type="checkbox" data-pcfg="and"'+(p.and?' checked':'')+'> Android</label>';
    h+='<label><input type="checkbox" data-pcfg="ios"'+(p.ios?' checked':'')+'> iOS</label></div>';
    var blueprint=Object.prototype.hasOwnProperty.call(upBlueprintDrafts,id)?upBlueprintDrafts[id]:(p.blueprintId||'');
    h+='<div class="up-formrow up-blueprint"><label>Avatar ID <input data-blueprint aria-label="Avatar ID" placeholder="Created automatically on first upload" value="'+esc(blueprint)+'"></label><button data-pact="saveid">'+esc(T("upload.save"))+'</button></div>';
    if(p.lastUpload) h+='<div class="up-last">'+esc(p.lastUpload)+'</div>';
    h+='<details class="up-panel" data-panel="blends"><summary>Blendshapes (<span data-bscount>'+p.blendCount+'</span>)</summary><div data-bsbody></div></details>';
    h+='<section class="up-panel up-items-section" data-panel="items"><div data-itembody></div></section>';
    
    box.innerHTML=h;
    wirePresetPanels(id,box);
  }
  function wirePresetPanels(id,box){
    var items=box.querySelector('[data-itembody]');
    if(items&&!items.dataset.loading&&!items.dataset.loaded)upItemsLoad(id,items);
    box.querySelectorAll('details[data-panel]').forEach(function(panel){
      var key=id+'|'+panel.dataset.panel;
      function loadPanel(){
        if(!panel.open) return;
        var body=panel.querySelector('[data-bsbody],[data-itembody]');
        if(!body||body.dataset.loaded||body.dataset.loading) return;
        if(panel.dataset.panel==='items'){upItemsLoad(id,body);return;}
        upBsLoad(id,body);
      }
      panel.open=!!upPanelOpen[key];
      panel.addEventListener('toggle',function(){upPanelOpen[key]=panel.open;loadPanel();});
      loadPanel();
    });
  }

  function upPid(el){ var n=el; while(n){ if(n.getAttribute&&n.getAttribute("data-preset")) return n.getAttribute("data-preset"); n=n.parentNode; } return null; }
  function upBsLoad(id,body){
    if(body.dataset.loading) return;
    body.dataset.loading='1';body.textContent=T("upload.loading");
    return api("/api/batch_preset_blends?id="+encodeURIComponent(id)).then(function(d){
      if(!body.isConnected) return;
      if(!d||!d.ok) throw new Error(d&&d.message||T("upload.failed"));
      upBlendCache[id]=d.items||[];upBsRender(id,body);body.dataset.loaded='1';
    }).catch(function(error){if(body.isConnected){body.textContent=error.message;delete body.dataset.loaded;}})
      .finally(function(){delete body.dataset.loading;});
  }
  function upBsRender(id,body){
    var items=upBlendCache[id]||[];
    var flt=(upBsSearch[id]||"").toLowerCase();
    var h="<div class="+qq("up-formrow")+"><input data-bssearch placeholder="+qq("Search")+" value="+qq(esc(upBsSearch[id]||""))+">";
    h+="<button data-pact="+qq("bscap")+">Capture</button><button data-pact="+qq("bsclear")+">Clear</button></div><div class="+qq("up-bslist")+">";
    items.forEach(function(b){
      if(flt&&b.name.toLowerCase().indexOf(flt)<0) return;
      var dis=b.pinned?"":" disabled";
      h+="<div class="+qq("up-bsrow")+"><input type="+qq("checkbox")+" data-pact="+qq("bspin")+" data-bs="+qq(esc(b.name))+(b.pinned?" checked":"")+">";
      h+="<span class="+qq("up-bsname")+">"+esc(b.name)+"</span>";
      h+="<input type="+qq("range")+" min="+qq("0")+" max="+qq("100")+" step="+qq("1")+" value="+qq(String(Math.round(b.weight)))+" data-pact="+qq("bsrange")+" data-bs="+qq(esc(b.name))+dis+">";
      h+="<input type="+qq("number")+" min="+qq("0")+" max="+qq("100")+" value="+qq(String(Math.round(b.weight)))+" data-pact="+qq("bsnum")+" data-bs="+qq(esc(b.name))+dis+"></div>";
    });
    body.innerHTML=h+"</div>";
  }
  function upBsSave(id,bs,pinned,weight){
    api("/api/batch_preset_blends?id="+encodeURIComponent(id)+"&bs="+encodeURIComponent(bs)+"&pinned="+(pinned?"1":"0")+"&weight="+encodeURIComponent(weight)).then(function(){
      var items=upBlendCache[id]||[];
      for(var i=0;i<items.length;i++) if(items[i].name===bs){ if(pinned){ items[i].pinned=1; items[i].weight=weight; } else items[i].pinned=0; break; }
    });
  }
  function upItemsLoad(id,body){
    // The preset's detected contents are distinct from the legacy avatar-wide Items folder.
    upItemCache[id]=(upFindPreset(id)||{}).members||[];
    body.dataset.loading='1';
    api('/api/menu_groups?id='+encodeURIComponent(id)).then(function(result){
      if(!result||!result.ok) throw new Error(result&&result.message||'Could not load menu groups.');
      upGroups[id]=result.groups||[];upItemsRender(id,body);body.dataset.loaded='1';
    }).catch(function(error){body.textContent=error.message;}).finally(function(){delete body.dataset.loading;});
  }
  function upItemsRender(id,body){
    var shown=upItemCache[id]||[];
    var h='<section class="up-menu-groups"><div class="up-section-heading"><h3>Menu Groups</h3><button data-pact="newgroup">New menu group</button></div><div class="up-group-list">'+(upGroups[id]||[]).map(function(g){return '<div class="up-group-row"><strong>'+esc(g.name)+'</strong><button data-pact="renamegroup" data-group="'+esc(g.id)+'">Rename</button><button data-pact="deletegroup" data-group="'+esc(g.id)+'">Delete group</button></div>';}).join('')+'</div></section><section class="up-items-list"><h3>Items</h3><div class="up-preset-items">';
    shown.forEach(function(it){
      h+='<div class="up-bsrow"><span class="up-bsname">'+esc(it.name)+'</span>';
      h+='<select aria-label="Menu group" data-pact="itemgroup" data-path="'+esc(it.path||'')+'" data-guid="'+esc(it.guid||'')+'"><option value="">No menu group</option>';
      (upGroups[id]||[]).forEach(function(g){h+='<option value="'+esc(g.id)+'"'+((g.paths||[]).indexOf(it.path)>=0?' selected':'')+'>'+esc(g.name)+'</option>';});
      h+='<option value="__new">+ New menu group</option></select>';
      if(it.guid) h+='<button data-pact="locate" data-guid="'+esc(it.guid)+'">'+esc(T("upload.locate"))+'</button>';
      h+='<button class="danger" data-pact="removeitem" data-instance="'+esc(it.instanceId||'')+'" data-path="'+esc(it.path||'')+'" data-guid="'+esc(it.guid||'')+'">Remove</button></div>';
    });
    body.innerHTML=h+'</div>'+(shown.length?'':'<p class="subtle">'+esc(T("upload.noMembers"))+'</p>')+'</section>';
  }
  function upFeLoad(id,body){
    api("/api/batch_preset_faceemo?id="+encodeURIComponent(id)).then(function(d){
      if(!d||!d.ok){ body.textContent=(d&&d.message)||T("upload.failed"); return; }
      var h="<div class="+qq("up-formrow")+"><span>"+(d.assigned?esc(d.assigned):esc(T("upload.none")))+"</span>";
      h+="<button data-pact="+qq("fecap")+">Capture</button><button data-pact="+qq("feclear")+">Clear</button><button data-pact="+qq("feopen")+">Open FaceEmo</button></div>";
      if(d.assigned&&!d.assignedExists) h+="<div>"+esc(T("upload.feMissing"))+"</div>";
      if(d.strayExists) h+="<div>"+esc(T("upload.feStray"))+"</div>";
      body.innerHTML=h;
    });
  }
  function upGo(act,el){
    var card=el;
    while(card&&!(card.getAttribute&&card.getAttribute("data-preset"))) card=card.parentNode;
    var id=card?card.getAttribute("data-preset"):null;
    if(!id) return;
    if(act==='removeitem'||act==='removepreset'){
      var presetName=separateUploads?(id==='common'?T('preset.common'):(upFindPreset(id)||{}).name):'the avatar';
      if(act==='removepreset'&&id==='common')return;
      if(!confirm(act==='removepreset'?'Remove '+presetName+' and its scene items? Source assets and uploaded avatars will remain.':'Remove this item from '+presetName+'?'))return;
      el.disabled=true;
      var url=act==='removepreset'?'/api/preset_delete?id='+encodeURIComponent(id):'/api/preset_remove_item?target='+encodeURIComponent(id)+'&guid='+encodeURIComponent(el.dataset.guid||'')+'&item='+encodeURIComponent(el.dataset.path||'')+'&instanceId='+encodeURIComponent(el.dataset.instance||'');
      api(url).then(function(r){if(!r||!r.ok)throw new Error(r&&r.message||'Remove failed.');upRefreshState();if(options.onChange)options.onChange();})
        .catch(function(e){toast(e.message,'err');}).finally(function(){el.disabled=false;});
    }
    else if(act==="rename"){
      var preset=upFindPreset(id),name=prompt('Preset name',preset.name);
      if(name===null||!name.trim())return;
      api('/api/preset_save?id='+encodeURIComponent(id)+'&name='+encodeURIComponent(name.trim())).then(function(r){if(!r||!r.ok)throw new Error(r&&r.message||'Rename failed.');upRefreshState();if(options.onChange)options.onChange();}).catch(function(e){toast(e.message,'err');});
    }
    else if(act==="newgroup"||act==="renamegroup"||act==="deletegroup"||act==="itemgroup"){
      var groupId=act==='itemgroup'?el.value:(el.dataset.group||''),groups=upGroups[id]||[],group=groups.find(function(g){return g.id===groupId;});
      var name=null,op=act==='deletegroup'?'delete':act==='itemgroup'?'assign':'save';
      if(op==='delete'&&!confirm('Delete this menu group? Its items will remain installed.'))return;
      if(op==='save'||groupId==='__new'){name=prompt('Menu group name',group?group.name:'');if(name===null||!name.trim()){upItemsRender(id,card.querySelector('[data-itembody]'));return;}}
      var query='/api/menu_groups?id='+encodeURIComponent(id);
      var create=groupId==='__new';
      el.disabled=true;
      var request=api(query+'&op='+(create?'save':op)+'&group='+encodeURIComponent(create?'':groupId)+'&name='+encodeURIComponent(name||'')+'&item='+encodeURIComponent(el.dataset.path||'')+'&guid='+encodeURIComponent(el.dataset.guid||''));
      request.then(function(r){if(!r||!r.ok)throw new Error(r&&r.message||'Menu group update failed.');if(create)return api(query+'&op=assign&group='+encodeURIComponent(r.id)+'&item='+encodeURIComponent(el.dataset.path||'')+'&guid='+encodeURIComponent(el.dataset.guid||''));return r;})
        .then(function(r){if(!r||!r.ok)throw new Error(r&&r.message||'Assignment failed.');upGroups[id]=r.groups||[];upItemsRender(id,card.querySelector('[data-itembody]'));toast('Menu group saved in Unity and toggles regenerated.','ok');upRefreshState();if(options.onChange)options.onChange();})
        .catch(function(e){toast(e.message,'err');upItemsRender(id,card.querySelector('[data-itembody]'));}).finally(function(){if(el.isConnected)el.disabled=false;});
    }
    else if(act==="exp"){var open=!upExpanded[id];upExpanded={};if(open)upExpanded[id]=true;renderPresets();var summary=Array.from(upEl('upList').children).find(function(node){return node.dataset.preset===id;});if(summary)summary.querySelector('[data-pact="exp"]').focus();}
    else if(act==="showunity"){
      el.disabled=true;
      api("/api/preset_show?id="+encodeURIComponent(id)).then(function(result){
        if(!result||!result.ok) throw new Error(result&&result.message||"Could not show preset.");
        toast("Preset shown in Unity","ok");
      }).catch(function(error){toast(error.message,"err");}).finally(function(){el.disabled=false;});
    }
    else if(act==="saveid"){
      var input=card.querySelector('[data-blueprint]'),value=input.value.trim();
      if(value&&!/^avtr_[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value)){
        toast(T("upload.invalidId"),"err");input.focus();return;
      }
      el.disabled=true;
      api('/api/batch_preset_config?id='+encodeURIComponent(id)+'&blueprint='+encodeURIComponent(value)).then(function(result){
        if(!result||!result.ok) throw new Error(result&&result.message||T("upload.failed"));
        if(upBlueprintDrafts[id]===input.value&&input.value.trim()===value)delete upBlueprintDrafts[id];var p=upFindPreset(id);if(p)p.blueprintId=value;
        saveDraft();
        toast(T("upload.saved"),"ok");upRefreshState();
      }).catch(function(error){toast(error.message,"err");}).finally(function(){el.disabled=false;});
    }
    else if(act==="upload"){ openUploadReview([id]); }
    else if(act==="locate"){ api("/api/batch_ping_object?guid="+encodeURIComponent(el.getAttribute("data-guid")||"")); }
    else if(act==="bscap"){ api("/api/batch_preset_blends?id="+encodeURIComponent(id)+"&op=capture").then(function(d){ if(d&&!d.ok) toast(d.message,"err"); var b=card.querySelector("[data-bsbody]"); if(b) upBsLoad(id,b); upRefreshState(); }); }
    else if(act==="bsclear"){ api("/api/batch_preset_blends?id="+encodeURIComponent(id)+"&op=clear").then(function(){ var b=card.querySelector("[data-bsbody]"); if(b) upBsLoad(id,b); upRefreshState(); }); }
    else if(act==="bspin"){ var bs=el.getAttribute("data-bs"); var row=el.parentNode; var num=row?row.querySelector("[data-pact="+qq("bsnum")+"]"):null; var rng=row?row.querySelector("[data-pact="+qq("bsrange")+"]"):null; var w=num?parseFloat(num.value)||0:0; if(rng) rng.disabled=!el.checked; if(num) num.disabled=!el.checked; upBsSave(id,bs,el.checked,w); }
    else if(act==="bsrange"||act==="bsnum"){ var bs2=el.getAttribute("data-bs"); var row2=el.parentNode; var other=row2?row2.querySelector(act==="bsrange"?"[data-pact="+qq("bsnum")+"]":"[data-pact="+qq("bsrange")+"]"):null; if(other) other.value=el.value; upBsSave(id,bs2,true,parseFloat(el.value)||0); }
    else if(act==="item"){ api("/api/batch_preset_items?id="+encodeURIComponent(id)+"&item="+encodeURIComponent(el.getAttribute("data-item"))+"&include="+(el.checked?"1":"0")); }
    else if(act==="fecap"){ api("/api/batch_preset_faceemo?id="+encodeURIComponent(id)+"&op=capture").then(function(d){ if(d&&!d.ok) toast(d.message,"err"); var b=card.querySelector("[data-febody]"); if(b) upFeLoad(id,b); upRefreshState(); }); }
    else if(act==="feclear"){ api("/api/batch_preset_faceemo?id="+encodeURIComponent(id)+"&op=clear").then(function(){ var b=card.querySelector("[data-febody]"); if(b) upFeLoad(id,b); upRefreshState(); }); }
    else if(act==="feopen"){ api("/api/batch_faceemo?op=open"); }
  }
  upEl("upList").addEventListener("click",function(e){
    var t=e.target;
    if(t&&(t.tagName==="INPUT"||t.tagName==="SELECT"||t.tagName==="TEXTAREA")) return;
    if(t&&t.closest("summary")) return;
    while(t&&t!==this){ if(t.getAttribute&&t.getAttribute("data-pact")){ upGo(t.getAttribute("data-pact"),t); return; } t=t.parentNode; }
  });
  upEl("upList").addEventListener("change",function(e){
    var t=e.target;
    if(!(t&&t.getAttribute)) return;
    if(t.getAttribute("data-pinc")!==null&&t.getAttribute("data-pinc")!=="false"){ var id=upPid(t); api("/api/preset_include?id="+encodeURIComponent(id)+"&include="+(t.checked?"1":"0")).then(function(){ upRefreshState(); }); return; }
    var act=t.hasAttribute("data-pcfg")?"pcfg":t.getAttribute("data-pact");
    if(act==="pcfg"){ var id2=upPid(t); var card=t; while(card&&!(card.getAttribute&&card.getAttribute("data-preset"))) card=card.parentNode; var vals={win:0,and:0,ios:0}; if(card) Array.prototype.forEach.call(card.querySelectorAll("[data-pcfg]"),function(bx){ vals[bx.getAttribute("data-pcfg")]=bx.checked?1:0; }); api("/api/batch_preset_config?id="+encodeURIComponent(id2)+"&win="+vals.win+"&and="+vals.and+"&ios="+vals.ios).then(function(){ upRefreshState(); }); }
    else if(act) upGo(act,t);
  });
  upEl("upList").addEventListener("input",function(e){
    if(e.target.hasAttribute('data-blueprint')){var id=upPid(e.target),saved=(upFindPreset(id)||{}).blueprintId||'';if(e.target.value===saved)delete upBlueprintDrafts[id];else upBlueprintDrafts[id]=e.target.value;saveDraft();return;}
    var t=e.target;
    if(!t||!t.getAttribute) return;
    if(t.hasAttribute("data-bssearch")){ var nm=upPid(t); upBsSearch[nm]=t.value; var pos=t.selectionStart; var card=t; while(card&&!(card.getAttribute&&card.getAttribute("data-preset"))) card=card.parentNode; var body=card?card.querySelector("[data-bsbody]"):null; if(body){ upBsRender(nm,body); var ni=body.querySelector("[data-bssearch]"); if(ni){ ni.focus(); try{ ni.setSelectionRange(pos,pos); }catch(x){} } } }
  });
  function renderUnassigned(){
    var box=upEl("upUnassigned"),token=++unassignedToken;
    api("/api/batch_unassigned").then(function(d){
      if(token!==unassignedToken) return;
      if(!d||!d.ok||!(d.items||[]).length){ box.innerHTML=""; return; }
      var h="";
      d.items.forEach(function(it){
        h+="<div class="+qq("up-row")+"><span class="+qq("up-name")+">"+esc(it.name)+"</span>";
        if(it.hasBlueprint) h+="<span class="+qq("up-id")+">id</span>";
        h+="<button data-un="+qq(esc(it.name))+">"+esc(T("upload.fromScene"))+"</button></div>";
      });
      if(box._signature===h) return;
      box._signature=h;box.innerHTML=h;
      Array.prototype.forEach.call(box.querySelectorAll("[data-un]"),function(btn){
        btn.onclick=function(){
          api("/api/batch_preset_from_scene?name="+encodeURIComponent(btn.getAttribute("data-un"))).then(function(r){ toast((r&&r.message)||"OK",r&&r.ok?"ok":"err"); upRefreshState(); });
        };
      });
    });
  }
  upEl("upRefresh").onclick=function(){ upRefreshState(); };
  upEl("upNew").onclick=function(){
    var name=prompt(T("upload.newSetName"));
    if(!name) return;
    var button=upEl("upNew");button.disabled=true;
    api("/api/preset_save?name="+encodeURIComponent(name)).then(async function(result){
      if(!result||!result.ok)throw new Error(result&&result.message||'Could not create preset.');
      if(result.id){upExpanded={};upExpanded[result.id]=true;}
      if(options.onPresetCreated)await options.onPresetCreated(result);
      await upRefreshState();
      toast(result.message||'Preset created.','ok');
    }).catch(function(error){toast(error.message,'err');}).finally(function(){button.disabled=false;});
  };
  upEl("upAll").onclick=function(){
    if(!BS||!BS.presets) return;
    var ids=BS.presets.filter(function(p){ return p.include; }).map(function(p){ return p.id; });
    if(!ids.length){ toast(T("upload.noneIncluded"),"err"); return; }
    openUploadReview(ids,true);
  };
  function uploadEditBlocker(){
    if(upRunning)return U('review.busy');
    var itemEdits=unappliedItemEditCount();if(itemEdits)return U('review.unappliedItems',itemEdits);
    if(pendingConfigWrites||(options.hasPendingChanges&&options.hasPendingChanges()))return U('review.saving');
    if(defaultsDirty||Object.keys(upBlueprintDrafts).length)return U('review.dirty');
    if(!sdkReady)return U('review.sdk');
    return '';
  }
  function unappliedItemEditCount(){
    var count=options.getUnappliedItemEdits?Number(options.getUnappliedItemEdits()):0;
    return Number.isFinite(count)&&count>0?Math.floor(count):0;
  }
  function showUnappliedItemEdits(){
    var count=unappliedItemEditCount();if(!count)return false;
    upOpenModal(U('review.title'),'<p id="upReviewMessage" role="status">'+esc(U('review.unappliedItems',count))+'</p><div class="up-review-actions"><button id="upReviewBack">'+esc(U('review.back'))+'</button><button id="upReviewItemEdits" class="primary">'+esc(U('review.reviewItemEdits'))+'</button></div>');
    upEl('upReviewBack').onclick=closeUploadModal;
    var review=upEl('upReviewItemEdits');review.hidden=typeof options.reviewUnappliedItemEdits!=='function';
    review.onclick=function(){closeUploadModal();if(options.reviewUnappliedItemEdits)options.reviewUnappliedItemEdits();};
    return true;
  }
  function uploadErrorText(error){return error.copyKey?U.apply(null,[error.copyKey].concat(error.copyArgs||[])):error.message;}
  async function openUploadReview(ids,all){
    var blocked=uploadEditBlocker();if(blocked){if(!showUnappliedItemEdits()){toast(blocked,'err');paintDraftStatus();}return;}
    var revision=contextRevision,state=await upRefreshState();
    if(!state||revision!==contextRevision)return;
    blocked=uploadEditBlocker();if(blocked){if(!showUnappliedItemEdits())toast(blocked,'err');return;}
    try{
      var selected=all?state.presets.filter(function(p){return p.include;}).map(function(p){return p.id;}):ids;
      showUploadReview(presetReview(state,selected,reviewAvatarName),selected,all,revision,'');
    }catch(error){toast(uploadErrorText(error),'err');}
  }
  function showUploadReview(model,ids,all,revision,message){
    var count=model.presets.length;
    var html='<div class="up-review-summary"><p>'+esc(U('review.avatarLabel'))+' <strong>'+esc(model.avatar)+'</strong></p><p>'+esc(U(count===1?'review.oneSelected':'review.manySelected',count))+'</p></div>'+
      '<ul class="up-review-list">'+model.presets.map(function(p){return '<li><strong>'+esc(p.name)+'</strong><p>'+esc(p.platforms.join(', '))+'</p><p>'+(p.blueprint?esc(U('review.update'))+' <code>'+esc(p.blueprint)+'</code>':esc(U('review.create')))+'</p></li>';}).join('')+'</ul>'+
      '<p>'+esc(U('review.release'))+' <strong>'+esc(U(model.defaults.release==='public'?'defaults.public':'defaults.private'))+'</strong></p><p class="up-review-warning">'+esc(U('review.warning'))+'</p>'+
      '<p id="upReviewMessage" role="status">'+esc(message)+'</p><div class="up-review-actions"><button id="upReviewBack">'+esc(U('review.back'))+'</button><button id="upReviewConfirm" class="primary">'+esc(U(count===1?'review.uploadOne':'review.uploadMany',count))+'</button></div>';
    upOpenModal(U('review.title'),html);
    upEl('upReviewBack').onclick=closeUploadModal;
    upEl('upReviewConfirm').onclick=async function(){
      var button=this;button.disabled=true;
      try{
        var blocked=uploadEditBlocker();if(blocked){if(showUnappliedItemEdits())return;throw new Error(blocked);}
        if(revision!==contextRevision)throw new Error(U('review.avatarChanged'));
        // Read again at commitment: never upload a changed target using an old review.
        var state=await upRefreshState();
        if(!state||revision!==contextRevision)throw new Error(U('review.connectionChanged'));
        blocked=uploadEditBlocker();if(blocked){if(showUnappliedItemEdits())return;throw new Error(blocked);}
        var selected=all?state.presets.filter(function(p){return p.include;}).map(function(p){return p.id;}):ids;
        var current=presetReview(state,selected,reviewAvatarName);
        if(JSON.stringify(current)!==JSON.stringify(model)){showUploadReview(current,selected,all,revision,U('review.changed'));return;}
        closeUploadModal();
        upStartJob('/api/batch_upload_presets?ids='+encodeURIComponent(selected.join('\n')),T('upload.uploading'),false);
      }catch(error){if(upEl('upReviewMessage'))upEl('upReviewMessage').textContent=uploadErrorText(error);}
      finally{if(button.isConnected)button.disabled=false;}
    };
  }
  var upContentTags = ["content_sex", "content_adult", "content_violence", "content_gore", "content_horror"];
  var upTagLabels = {"content_sex": "Sexually Suggestive", "content_adult": "Adult Language and Themes", "content_violence": "Graphic Violence", "content_gore": "Excessive Gore", "content_horror": "Extreme Horror"};
  function renderDefs(){
    var el = upEl("upDefsForm");
    if(!BS){ el.innerHTML = ""; return; }
    var d = BS.defaults || {};
    var signature=JSON.stringify(d)+"|"+T("upload.saved");
    upEl("upLog").textContent=BS.logTail||"";
    if((defaultsDirty&&!defaultsPendingDraft)||defaultsSignature===signature) return;
    defaultsSignature=signature;
    var h = ("<fieldset class=\"up-default-group\"><legend>"+esc(U("defaults.identity"))+"</legend><p class=\"up-field-help\">"+esc(U("defaults.hint"))+"</p>");
    h += ("<label>"+esc(U("defaults.name"))+" <input id=") + qq("upDname") + " value=" + qq(esc(d.nameTemplate)) + "></label>";
    h += "<div class=" + qq("up-sub") + ">Tokens: {preset}, {avatar}</div>";
    h += ("<label>"+esc(U("defaults.description"))+" <input id=") + qq("upDdesc") + " value=" + qq(esc(d.descTemplate)) + "></label>";
    h += ("<label>"+esc(U("defaults.release"))+" <select id=") + qq("upDrel") + "><option value=" + qq("private") + (d.release === "public" ? "" : " selected") + (">"+esc(U("defaults.private"))+"</option><option value=") + qq("public") + (d.release === "public" ? " selected" : "") + (">"+esc(U("defaults.public"))+"</option></select></label>");
    h += ("<div>"+esc(U("defaults.tags"))+" ");
    var tagmap = {};
    (d.tags || []).forEach(function(t){ tagmap[t.key] = t.on; });
    upContentTags.forEach(function(t){ h += "<label><input type=" + qq("checkbox") + " data-dtag=" + qq(t) + (tagmap[t] ? " checked" : "") + "> " + esc(upTagLabels[t] || t) + "</label> "; });
    h += ("</div><label>"+esc(U("defaults.version"))+" <select id=\"upDvmode\"><option value=\"0\"")+(d.versionMode?'':' selected')+(">"+esc(U("defaults.versionReplace"))+"</option><option value=\"1\"")+(d.versionMode?' selected':'')+(">"+esc(U("defaults.versionAppend"))+"</option></select></label></fieldset>");
    h += ("<fieldset class=\"up-default-group\"><legend>"+esc(U("defaults.thumbnail"))+"</legend>");
    h += ("<label>"+esc(U("defaults.capture"))+" <select id=") + qq("upDthumb") + "><option value=" + qq("scene") + (d.thumbMode === "scene" ? " selected" : "") + (">"+esc(U("defaults.captureAuto"))+"</option><option value=") + qq("sceneview") + (d.thumbMode === "sceneview" ? " selected" : "") + (">"+esc(U("defaults.captureScene"))+"</option><option value=") + qq("image") + (d.thumbMode === "image" ? " selected" : "") + (">"+esc(U("defaults.captureImage"))+"</option></select></label>");
    h += ("<div id=\"upThumbFileFields\" class=\"up-dependent\"><label>"+esc(U("defaults.imagePath"))+" <input id=\"upDthumbimg\" aria-describedby=\"upThumbFileHelp\" value=\"")+esc(d.thumbImage)+("\"></label><p id=\"upThumbFileHelp\" class=\"up-field-help\">"+esc(U("defaults.imageHelp"))+"</p></div>");
    h += ("<div id=\"upThumbCaptureFields\" class=\"up-dependent\"><label>"+esc(U("defaults.background"))+" <input id=\"upDbg\" aria-describedby=\"upColorHelp\" value=\"")+esc(d.bgColor)+("\" size=\"10\"></label><p id=\"upColorHelp\" class=\"up-field-help\">"+esc(U("defaults.colorHelp"))+"</p></div></fieldset>");
    h += ("<fieldset class=\"up-default-group\"><legend>"+esc(U("defaults.automation"))+"</legend>");
    h += "<label><input type=" + qq("checkbox") + " id=" + qq("upDautosps") + (d.autoSps ? " checked" : "") + ("> "+esc(U("defaults.sps"))+"</label><p class=\"up-field-help\">"+esc(U("defaults.spsHelp"))+"</p>");
    h += "<label><input type=" + qq("checkbox") + " id=" + qq("upDautofix") + (d.autoFix ? " checked" : "") + ("> "+esc(U("defaults.sdkFix"))+"</label><p class=\"up-field-help\">"+esc(U("defaults.sdkFixHelp"))+"</p>");
    h += "<label><input type=" + qq("checkbox") + " id=" + qq("upDautoconsent") + (d.autoConsent ? " checked" : "") + ("> "+esc(U("defaults.consent"))+"</label><p class=\"up-field-help\">"+esc(U("defaults.consentHelp"))+"</p></fieldset>");
    h += ("<fieldset class=\"up-default-group\"><legend>"+esc(U("defaults.optimization"))+"</legend>");
    h += "<label><input type=" + qq("checkbox") + " id=" + qq("upDopten") + (d.optEnabled ? " checked" : "") + ("> "+esc(U("defaults.optimize"))+"</label><p class=\"up-field-help\">"+esc(U("defaults.optimizeHelp"))+"</p><div id=\"upOptimizationFields\" class=\"up-dependent\">");
    h += "<label><input type=" + qq("checkbox") + " id=" + qq("upDoptask") + (d.optAsk ? " checked" : "") + ("> "+esc(U("defaults.optimizeAsk"))+"</label><p class=\"up-field-help\">"+esc(U("defaults.optimizeAskHelp"))+"</p>");
    h += ("<label>"+esc(U("defaults.maxResolution"))+" <select id=") + qq("upDoptmax") + ">";
    [256, 512, 1024, 2048, 4096].forEach(function(n){ h += "<option" + (d.optMaxRes === n ? " selected" : "") + ">" + n + "</option>"; });
    h += ("</select></label><label>"+esc(U("defaults.minResolution"))+" <select id=") + qq("upDoptmin") + ">";
    [0, 256, 512, 1024, 2048].forEach(function(n){ h += "<option value=" + qq(String(n)) + (d.optMinRes === n ? " selected" : "") + ">" + (n === 0 ? "none" : n) + "</option>"; });
    h += "</select></label>";
    h += "<label><input type=" + qq("checkbox") + " id=" + qq("upDoptitems") + (d.optItems ? " checked" : "") + ("> "+esc(U("defaults.includeShared"))+"</label></div></fieldset>");
    el.innerHTML = h;
    if(defaultsPendingDraft){var restored=defaultsPendingDraft;el.querySelectorAll('input,select').forEach(function(input){var key=input.id||'tag:'+input.dataset.dtag;if(Object.prototype.hasOwnProperty.call(restored,key)){if(input.type==='checkbox')input.checked=!!restored[key];else input.value=restored[key];}});defaultsPendingDraft=null;}
    paintDefaultDependencies();paintDraftStatus();
    api("/api/batch_item").then(function(it){
      var box = upEl("upItemDefs");
      if(!it || !it.ok){ box.textContent = (it && it.message) || ""; return; }
      var hh = "";
      (it.items || []).forEach(function(x){
        hh += "<label><input type=" + qq("checkbox") + " data-idef=" + qq(esc(x.name)) + (x.isDefault ? " checked" : "") + "> " + esc(x.name) + "</label><br>";
      });
      box.innerHTML = hh || esc(T("upload.noItems"));
      Array.prototype.forEach.call(box.querySelectorAll("[data-idef]"), function(cb){
        cb.onchange = function(){ api("/api/batch_item?op=default&item=" + encodeURIComponent(cb.getAttribute("data-idef")) + "&include=" + (cb.checked ? "1" : "0")); };
      });
    });
    upEl("upLog").textContent = BS.logTail || "";
  }
  function paintDefaultDependencies(){if(!upEl('upDthumb'))return;upEl('upThumbFileFields').hidden=upEl('upDthumb').value!=='image';upEl('upThumbCaptureFields').hidden=upEl('upDthumb').value==='image';upEl('upOptimizationFields').hidden=!upEl('upDopten').checked;}
  upEl("upDefsForm").addEventListener("input",function(){defaultsDirty=true;saveDraft();});
  upEl("upDefsForm").addEventListener("change",function(){defaultsDirty=true;paintDefaultDependencies();saveDraft();});
  upEl("upDefsSave").onclick = function(){
    var g = function(id){ var n = upEl(id); return n ? n.value : ""; };
    var c = function(id){ var n = upEl(id); return n && n.checked ? "1" : "0"; };
    if(g('upDthumb')!=='image'&&!/^#?(?:[0-9a-f]{6}|[0-9a-f]{8})$/i.test(g('upDbg'))){toast(U('defaults.invalidColor'),'err');upEl('upDbg').focus();return;}
    if(c('upDopten')==='1'&&Number(g('upDoptmin'))>Number(g('upDoptmax'))){toast(U('defaults.invalidResolution'),'err');upEl('upDoptmin').focus();return;}
    var savingValues=JSON.stringify(formValues());
    var url = "/api/batch_defaults_set?nameTemplate=" + encodeURIComponent(g("upDname"))
      + "&descTemplate=" + encodeURIComponent(g("upDdesc"))
      + "&release=" + encodeURIComponent(g("upDrel"))
      + "&thumbMode=" + encodeURIComponent(g("upDthumb"))
      + "&thumbImage=" + encodeURIComponent(g("upDthumbimg"))
      + "&bgColor=" + encodeURIComponent(g("upDbg"))
      + "&autoSps=" + c("upDautosps") + "&autoFix=" + c("upDautofix") + "&autoConsent=" + c("upDautoconsent")
      + "&optEnabled=" + c("upDopten") + "&optAsk=" + c("upDoptask")
      + "&optMaxRes=" + encodeURIComponent(g("upDoptmax")) + "&optMinRes=" + encodeURIComponent(g("upDoptmin"))
      + "&optItems=" + c("upDoptitems");
    document.querySelectorAll("[data-dtag]").forEach(function(cb){ url += "&tag_" + encodeURIComponent(cb.getAttribute("data-dtag")) + "=" + (cb.checked ? "1" : "0"); });
    var button=upEl("upDefsSave");
    if(button.disabled)return;
    button.disabled=true;
    var versionMode=g("upDvmode");
    api(url).then(function(d){
      if(!d||!d.ok)throw new Error((d&&d.message)||T("upload.failed"));
      return api("/api/batch_config_set?versionMode="+encodeURIComponent(versionMode));
    }).then(function(d){
      if(!d||!d.ok)throw new Error((d&&d.message)||T("upload.failed"));
      if(JSON.stringify(formValues())===savingValues){defaultsDirty=false;defaultsSignature="";}
      saveDraft();
      toast(T("upload.saved"),"ok");
      upRefreshState();
    }).catch(function(error){toast(error.message||T("upload.failed"),"err");})
      .finally(function(){button.disabled=false;});
  };
  upEl("upExport").onclick = function(){
    api("/api/batch_export").then(function(d){
      if(!d || !d.ok){ toast((d && d.message) || T("upload.failed"), "err"); return; }
      var blob = new Blob([d.json || ""], {type: "application/json"});
      var a = document.createElement("a");
      a.href = URL.createObjectURL(blob);
      a.download = d.filename || "backup.json";
      document.body.appendChild(a);
      a.click();
      setTimeout(function(){ URL.revokeObjectURL(a.href); a.remove(); }, 1000);
    });
  };
  upEl("upImportBtn").onclick = function(){ upEl("upImportFile").click(); };
  upEl("upImportFile").addEventListener("change", async function(){
    var file=this.files[0];this.value="";
    if(!file)return;
    var button=upEl("upImportBtn");
    if(button.disabled)return;
    button.disabled=true;
    try {
      if(file.size>4*1024*1024)throw new Error("Settings file exceeds 4 MB.");
      var text=await file.text();
      JSON.parse(text);
      var result=await api("/api/batch_import", {method:"POST", headers:{"Content-Type":"application/json"}, body:text});
      if(!result||!result.ok)throw new Error((result&&result.message)||T("upload.failed"));
      defaultsDirty=false;defaultsSignature="";
      defaultsPendingDraft=null;saveDraft();
      toast(result.message||T("upload.saved"),"ok");
      upRefreshState();
    }catch(error){toast(error.message||T("upload.failed"),"err");}
    finally{button.disabled=false;}
  });

    global.addEventListener("pagehide",upStopPoll,{once:true});
    return {show:upRefreshState,setUploadReadiness:function(loggedIn,ready){
      sdkKnown=true;sdkLoggedIn=!!loggedIn;sdkReady=!!ready;paintSdkReadiness();
    },setContext:function(key){
      if(avatarContext===key)return;
      saveDraft();
      avatarContext=key;contextRevision++;unassignedToken++;
      BS=null;upStateFlight=null;
      upExpanded={};upPanelOpen={};upBlueprintDrafts={};
      upBlendCache={};upItemCache={};upBsSearch={};upGroups={};
      defaultsDirty=false;defaultsSignature="";
      draftScope=null;defaultsPendingDraft=null;draftRestored=false;reviewAvatarName='';
      if(!upEl('upModal').hidden)closeUploadModal();
      // Upload jobs belong to the server session, not to the browsed avatar.
      // Keep their progress and polling alive while avatar-specific panels reset.
      ["upList","upUnassigned","upDefsForm","upItemDefs","upLog"].forEach(function(id){var node=upEl(id);if(node)node.innerHTML="";});
      renderPresets();
      paintDraftStatus();
    },setSeparateUploads:function(enabled){
      if(separateUploads===enabled) return;
      separateUploads=enabled;
      if(!enabled&&upTab==="defs") upSetTab("presets");
      renderPresets();
    },localize:function(){paintDraftStatus();if(!upEl("upload").hidden) upRefreshState();}};
  };
})(window);
