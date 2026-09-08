/* Preset/upload UI extracted from the supplied 1.0 page; same Unity endpoints. */
(function(global){
  "use strict";
  global.WardrobeUpload=function(options){
    var request=options.api,avatarContext="",contextRevision=0;
    function api(path,opts){
      var revision=contextRevision;
      return request(path,opts).then(function(result){
        if(revision!==contextRevision)throw new Error("Avatar changed; previous response discarded.");
        return result;
      });
    }
    var T=options.T,toast=options.toast,esc=options.esc,spinner=options.spinner;
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
  var BS=null, upTab="presets", upJobTimer=null, upJobPolling=false, upStateFlight=null, upJobToken=0;
  var R=global.WardrobeRuntime, defaultsDirty=false, defaultsSignature="", unassignedToken=0;
  var upEl=function(id){ return document.getElementById(id); };
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
      d.common={id:'common',name:T('preset.common'),members:((installed&&installed.items)||[]).filter(function(item){return !item.target||item.target==='common';}).map(function(item){return {guid:item.guid,path:item.path||'',name:item.family+(item.variant&&item.variant!=='Default'?' — '+item.variant:'')};})};
      BS=d;
      if(upTab==="presets"){ renderPresets(); renderUnassigned(); }
      else renderDefs();
      return d;
    }).catch(function(){ if(revision===contextRevision)toast(T("upload.failed"),"err"); return null; }).finally(function(){if(revision===contextRevision)upStateFlight=null;});
    return upStateFlight;
  }
  function upShowJob(label,canCancel){
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
      var signature=JSON.stringify(p)+"|"+!!upExpanded[p.id]+"|"+separateUploads+"|"+T("upload.upload");
      if(node._signature===signature) return;
      node._signature=signature;node.dataset.preset=p.id;
      var html="";
      var open=true;
      if(p.id==='common'){
        node.innerHTML=(separateUploads?'<div class="up-row"><span class="up-name">'+esc(T('preset.common'))+'</span></div><p class="subtle">Common Preset contains items shared by all other presets.</p>':'')+'<section class="up-panel up-items-section" data-panel="items"><div data-itembody></div></section>';
        wirePresetPanels(p.id,node);return;
      }
      if(!separateUploads){
        node.innerHTML='<div class="up-row"><span class="up-name">'+esc(p.name)+'</span><button data-pact="rename">Rename</button><button class="danger" data-pact="removepreset">Remove</button><button data-pact="showunity">Show in Unity</button></div><section class="up-panel up-items-section" data-panel="items"><div data-itembody></div></section>';
        wirePresetPanels(p.id,node);
        return;
      }
      var plats=((p.win?"Windows ":"")+(p.and?"Android ":"")+(p.ios?"iOS":"")).replace(/ +$/,"");
      html+="<div class="+qq("up-row")+">"
        
        +"<span class="+qq("up-name")+">"+esc(p.name)+"</span>"
        +'<button data-pact="rename">Rename</button><button class="danger" data-pact="removepreset">Remove</button>'
        +"<span class="+qq("up-plats")+">"+esc(plats)+"</span>"
        +'<button data-pact="showunity">Show in Unity</button>'
        
        +"<button data-pact="+qq("upload")+">"+esc(T("upload.upload"))+"</button>"
      html+='</div><label class="up-formrow"><input type="checkbox" role="switch" data-pinc'+(p.include?' checked':'')+'> Include in batch upload</label>';
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
    var h="";
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
      h+='<button class="danger" data-pact="removeitem" data-path="'+esc(it.path||'')+'" data-guid="'+esc(it.guid||'')+'">Remove</button></div>';
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
      var url=act==='removepreset'?'/api/preset_delete?id='+encodeURIComponent(id):'/api/preset_remove_item?target='+encodeURIComponent(id)+'&guid='+encodeURIComponent(el.dataset.guid||'')+'&item='+encodeURIComponent(el.dataset.path||'');
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
    else if(act==="exp"){ if(upExpanded[id]) delete upExpanded[id]; else upExpanded[id]=true; renderPresets(); }
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
        delete upBlueprintDrafts[id];var p=upFindPreset(id);if(p)p.blueprintId=value;
        toast(T("upload.saved"),"ok");upRefreshState();
      }).catch(function(error){toast(error.message,"err");}).finally(function(){el.disabled=false;});
    }
    else if(act==="upload"){ if(!confirm(T("upload.confirmOne"))) return; upStartJob("/api/batch_upload_presets?ids="+encodeURIComponent(id),T("upload.uploading"),false); }
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
    if(e.target.hasAttribute('data-blueprint')){upBlueprintDrafts[upPid(e.target)]=e.target.value;return;}
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
      if(result.id)upExpanded[result.id]=true;
      if(options.onPresetCreated)await options.onPresetCreated(result);
      await upRefreshState();
      toast(result.message||'Preset created.','ok');
    }).catch(function(error){toast(error.message,'err');}).finally(function(){button.disabled=false;});
  };
  upEl("upAll").onclick=function(){
    if(!BS||!BS.presets) return;
    var ids=BS.presets.filter(function(p){ return p.include; }).map(function(p){ return p.id; });
    if(!ids.length){ toast(T("upload.noneIncluded"),"err"); return; }
    if(!confirm(T("upload.confirmAll"))) return;
    upStartJob("/api/batch_upload_presets?ids="+encodeURIComponent(ids.join("\n")),T("upload.uploading"),false);
  };
  var upContentTags = ["content_sex", "content_adult", "content_violence", "content_gore", "content_horror"];
  var upTagLabels = {"content_sex": "Sexually Suggestive", "content_adult": "Adult Language and Themes", "content_violence": "Graphic Violence", "content_gore": "Excessive Gore", "content_horror": "Extreme Horror"};
  function renderDefs(){
    var el = upEl("upDefsForm");
    if(!BS){ el.innerHTML = ""; return; }
    var d = BS.defaults || {};
    var signature=JSON.stringify(d)+"|"+T("upload.saved");
    upEl("upLog").textContent=BS.logTail||"";
    if(defaultsDirty||defaultsSignature===signature) return;
    defaultsSignature=signature;
    var h = "";
    h += "<label>Name template <input id=" + qq("upDname") + " value=" + qq(esc(d.nameTemplate)) + "></label>";
    h += "<div class=" + qq("up-sub") + ">Tokens: {preset}, {avatar}</div>";
    h += "<label>Description template <input id=" + qq("upDdesc") + " value=" + qq(esc(d.descTemplate)) + "></label>";
    h += "<label>Release <select id=" + qq("upDrel") + "><option value=" + qq("private") + (d.release === "public" ? "" : " selected") + ">private</option><option value=" + qq("public") + (d.release === "public" ? " selected" : "") + ">public</option></select></label>";
    h += "<div>Tags ";
    var tagmap = {};
    (d.tags || []).forEach(function(t){ tagmap[t.key] = t.on; });
    upContentTags.forEach(function(t){ h += "<label><input type=" + qq("checkbox") + " data-dtag=" + qq(t) + (tagmap[t] ? " checked" : "") + "> " + esc(upTagLabels[t] || t) + "</label> "; });
    h += "</div>";
    h += "<label>Thumb mode <select id=" + qq("upDthumb") + "><option value=" + qq("scene") + (d.thumbMode === "scene" ? " selected" : "") + ">auto</option><option value=" + qq("sceneview") + (d.thumbMode === "sceneview" ? " selected" : "") + ">scene view</option><option value=" + qq("image") + (d.thumbMode === "image" ? " selected" : "") + ">image</option></select></label>";
    h += "<label>Thumb image <input id=" + qq("upDthumbimg") + " value=" + qq(esc(d.thumbImage)) + "></label>";
    h += "<label>BG color <input id=" + qq("upDbg") + " value=" + qq(esc(d.bgColor)) + " size=" + qq("8") + "></label>";
    h += "<label><input type=" + qq("checkbox") + " id=" + qq("upDautosps") + (d.autoSps ? " checked" : "") + "> Auto SPS tag</label>";
    h += "<label><input type=" + qq("checkbox") + " id=" + qq("upDautofix") + (d.autoFix ? " checked" : "") + "> Auto SDK fixes</label>";
    h += "<label><input type=" + qq("checkbox") + " id=" + qq("upDautoconsent") + (d.autoConsent ? " checked" : "") + "> Auto consent</label>";
    h += "<div>VRAM: <label><input type=" + qq("checkbox") + " id=" + qq("upDopten") + (d.optEnabled ? " checked" : "") + "> optimize on Express</label>";
    h += "<label><input type=" + qq("checkbox") + " id=" + qq("upDoptask") + (d.optAsk ? " checked" : "") + "> ask first</label>";
    h += "<label>cap <select id=" + qq("upDoptmax") + ">";
    [256, 512, 1024, 2048, 4096].forEach(function(n){ h += "<option" + (d.optMaxRes === n ? " selected" : "") + ">" + n + "</option>"; });
    h += "</select></label><label>floor <select id=" + qq("upDoptmin") + ">";
    [0, 256, 512, 1024, 2048].forEach(function(n){ h += "<option value=" + qq(String(n)) + (d.optMinRes === n ? " selected" : "") + ">" + (n === 0 ? "none" : n) + "</option>"; });
    h += "</select></label>";
    h += "<label><input type=" + qq("checkbox") + " id=" + qq("upDoptitems") + (d.optItems ? " checked" : "") + "> include items</label></div>";
    h += "<div>Version mode <select id=" + qq("upDvmode") + "><option value=" + qq("0") + (d.versionMode ? "" : " selected") + ">replace description</option><option value=" + qq("1") + (d.versionMode ? " selected" : "") + ">append line</option></select></div>";
    el.innerHTML = h;
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
  upEl("upDefsForm").addEventListener("input",function(){defaultsDirty=true;});
  upEl("upDefsForm").addEventListener("change",function(){defaultsDirty=true;});
  upEl("upDefsSave").onclick = function(){
    var g = function(id){ var n = upEl(id); return n ? n.value : ""; };
    var c = function(id){ var n = upEl(id); return n && n.checked ? "1" : "0"; };
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
      defaultsDirty=false;defaultsSignature="";
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
      var begin=await api("/api/batch_import");
      if(!begin||!begin.ok)throw new Error((begin&&begin.message)||T("upload.failed"));
      var token=encodeURIComponent(begin.message),pos=0;
      while(pos<text.length){
        // Keep URI-encoded Japanese text under common request-line limits.
        var end=Math.min(pos+768,text.length);
        if(end<text.length && text.charCodeAt(end-1)>=0xD800 && text.charCodeAt(end-1)<=0xDBFF)end--;
        var chunk=text.slice(pos,end);
        var part=await api("/api/batch_import?op=chunk&token="+token+"&data="+encodeURIComponent(chunk));
        if(!part||!part.ok)throw new Error((part&&part.message)||T("upload.failed"));
        pos=end;
      }
      var result=await api("/api/batch_import?op=commit&token="+token);
      if(!result||!result.ok)throw new Error((result&&result.message)||T("upload.failed"));
      defaultsDirty=false;defaultsSignature="";
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
      avatarContext=key;contextRevision++;unassignedToken++;
      BS=null;upStateFlight=null;
      upExpanded={};upPanelOpen={};upBlueprintDrafts={};
      upBlendCache={};upItemCache={};upBsSearch={};upGroups={};
      defaultsDirty=false;defaultsSignature="";
      // Upload jobs belong to the server session, not to the browsed avatar.
      // Keep their progress and polling alive while avatar-specific panels reset.
      ["upList","upUnassigned","upDefsForm","upItemDefs","upLog"].forEach(function(id){var node=upEl(id);if(node)node.innerHTML="";});
      renderPresets();
    },setSeparateUploads:function(enabled){
      if(separateUploads===enabled) return;
      separateUploads=enabled;
      if(!enabled&&upTab==="defs") upSetTab("presets");
      renderPresets();
    },localize:function(){if(!upEl("upload").hidden) upRefreshState();}};
  };
})(window);
