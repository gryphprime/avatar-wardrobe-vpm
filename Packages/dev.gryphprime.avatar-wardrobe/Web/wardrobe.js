/* Avatar Wardrobe browser: stable catalog/installed views and explicit user actions. */
(function(){
  "use strict";
  var R=window.WardrobeRuntime, $=function(id){return document.getElementById(id);};
  var esc=R.escape;
  // Disabled surfaces stay unavailable; opt-in editor overrides are local to this browser.
  var viewFeatures={
    library:false,
    dressingRoom:false,
    appearanceEditor:R.stored("wardrobeFeatureAppearance","0")==="1",
    menuOrganizer:R.stored("wardrobeFeatureMenu","0")==="1"
  };
  $("navAppearance").hidden=!viewFeatures.appearanceEditor;
  $("navMenu").hidden=!viewFeatures.menuOrganizer;
  var page=0,pageSize=60,filter="compatible",search="",shop="",category="",total=0,pageCount=1,aiAvailable=0;
  var sortMode=R.stored("wardrobeSort","recent"),hideEmpty=R.stored("wardrobeHideEmpty","0")==="1"?1:0;
  var baseSaving=false;
  var avatarMode=0,modeSaving=false,selectedPreset="common",workflowRevision=0;
  var indexPhase="discovery";
  var indexing=0,indexDone=0,indexTotal=0,indexAction=null,diagInFlight=false,initialGridPending=true;
  var installInFlight=false,removeInFlight=false,uploadInFlight=false,aiInFlight=false;
  var inspectorMode="installed",selectedFamilyId="",installedItems=[],detailLoadToken=0,detailToken=0;
  var detailSelect=null,detailCount=0,detailIndex=0,detailVariantGuid="";
  var grid=$("grid"),status=$("status"),avatar=$("avatar"),pageinfo=$("pageinfo"),emptyGridState=$("emptyGridState");
  var side=$("side"),sideContent=$("sideContent"),modal=$("modal"),modalContent=$("modalContent"),modalTitle=$("modalTitle"),modalMode=$("modalMode");
  var sideBackdrop=$("sideBackdrop"),hideBtn=$("hideEmpty"),avatarModeChip=$("workflowMulti");
  var lastState=null,catalogEpoch="",contextKey="",previewContext="",connected=false,currentView="wardrobe";
  var listCache=new Map(),detailCache=new Map(),cardCache=new Map(),listItems=[];
  var listToken=0,listInflight=false,listController=null,gridNotice="",installedFlight=null,stateFlight=null;
  var previewActivity={active:0,queued:0},activityEntries=[],pollTimer=null;
  var detailSettingDrafts=new Map(),installedIdentity=null;
  function cacheSet(map,key,value,cap){ map.delete(key); map.set(key,value); while(map.size>cap) map.delete(map.keys().next().value); }
  function dropCaches(){ listCache.clear(); detailCache.clear(); }
  function api(path,options){
    if(path.indexOf("/api/")===0) path+=(path.indexOf("?")>=0?"&":"?")+"lang="+encodeURIComponent(langCode||"en");
    return R.request(path,options);
  }
  function spinner(size){ return '<svg class="spin" width="'+(size||20)+'" height="'+(size||20)+'" viewBox="0 0 24 24" fill="none" aria-hidden="true"><circle cx="12" cy="12" r="9" stroke="currentColor" stroke-opacity=".2" stroke-width="2"/><path d="M21 12a9 9 0 0 0-9-9" stroke="currentColor" stroke-width="2" stroke-linecap="round"/></svg>'; }
  function toast(message,type){
    if(!message) return;
    var node=document.createElement("div"); node.className="toast "+(type||""); node.textContent=message;
    $("toasts").appendChild(node); setTimeout(function(){node.remove();},6000);
    activityEntries.unshift({id:String(Date.now())+"-"+activityEntries.length,message:message,type:type||"",time:new Date().toLocaleTimeString([], {hour:"2-digit",minute:"2-digit"})});
    activityEntries=activityEntries.slice(0,50); paintActivity();
  }
  var writeStatusNode=null;
  window.addEventListener('wardrobe-write-status',function(event){
    if(event.detail.error)toast(event.detail.error,'err');
    var pending=event.detail.pending;
    if(!pending){if(writeStatusNode)writeStatusNode.remove();writeStatusNode=null;return;}
    if(!writeStatusNode){writeStatusNode=document.createElement('div');writeStatusNode.className='toast';writeStatusNode.setAttribute('role','status');$('toasts').appendChild(writeStatusNode);}
    R.text(writeStatusNode,T('write.pending',pending));
  });
  function beginButtonBusy(button,label){
    if(!button||button.dataset.busy==="1") return null;
    var state={html:button.innerHTML,title:button.title}; button.dataset.busy="1"; button.disabled=true;
    button.setAttribute("aria-busy","true"); button.innerHTML=spinner(16)+"<span>"+esc(label)+"</span>"; return state;
  }
  function endButtonBusy(button,state){
    if(!button||!state) return;
    button.innerHTML=state.html; button.title=state.title; button.disabled=false; button.removeAttribute("aria-busy"); delete button.dataset.busy;
  }
  function detailBusy(){ return installInFlight||removeInFlight; }
  function paintHide(){ hideBtn.textContent=T(hideEmpty?"hide.show":"hide.hide"); hideBtn.setAttribute("aria-pressed",String(!!hideEmpty)); }
  function effectivePreset(selected){return avatarMode?(selected||selectedPreset||"common"):"common";}
  async function presetCreated(result){
    if(avatarMode&&result.id){selectedPreset=result.id;workflowRevision++;}
    if(stateFlight)await stateFlight;
    await refreshState();
    dropCaches();load();
  }
  function paintAvatarMode(){
    avatarModeChip.checked=!!avatarMode;$("workflowOne").checked=!avatarMode;
    document.body.classList.toggle("one-avatar",!avatarMode);
    $("workflowPresetWrap").hidden=!avatarMode;
    $("sceneUpload").hidden=!!avatarMode;
    var label=$("navUpload").querySelector("span");label.removeAttribute("data-i18n");R.text(label,avatarMode?T("nav.presets"):T("ui.organization"));
  }
  function applyAvatarMode(value){
    var changed=avatarMode!==(value?1:0);avatarMode=value?1:0;paintAvatarMode();
    if(uploadUI) uploadUI.setSeparateUploads(!!avatarMode);
    if(changed){if(inspectorMode==="selected"&&!detailBusy())closeModal();sideContent.dataset.signature="";loadInstalled();dropCaches();load();}
  }
  hideBtn.onclick=function(){ hideEmpty=hideEmpty?0:1; R.store("wardrobeHideEmpty",String(hideEmpty)); paintHide(); load(); };
  async function saveMode(){
    if(detailBusy()||modeSaving){paintAvatarMode();return;}
    var requested=avatarModeChip.checked;modeSaving=true;workflowRevision++;
    avatarModeChip.disabled=$("workflowOne").disabled=true;
    try {
      var result=await api("/api/workflow?mode="+(requested?"multi-avatar":"one-avatar"));
      if(!result||!result.ok) throw new Error(result&&result.message||T("upload.failed"));
      applyAvatarMode(requested);
    } catch(error){paintAvatarMode();toast(error.message,"err");}
    finally{modeSaving=false;avatarModeChip.disabled=$("workflowOne").disabled=false;}
  }
  avatarModeChip.onchange=saveMode;$("workflowOne").onchange=saveMode;
  $("workflowPreset").onchange=async function(){
    var select=this,previous=selectedPreset;select.disabled=true;modeSaving=true;workflowRevision++;
    try{var result=await api('/api/workflow?selected='+encodeURIComponent(select.value));if(!result.ok)throw new Error(result.message);selectedPreset=select.value;if(currentView==='appearanceEditor')window.WardrobeAppearanceEditor.show();if(snapshots)snapshots.contextChanged();sideContent.dataset.signature="";loadInstalled();dropCaches();load();}
    catch(error){select.value=previous;toast(error.message,'err');}
    finally{select.disabled=false;modeSaving=false;}
  };
  function paintWorkflowState(s){
    if(modeSaving)return;
    var previousPreset=selectedPreset,previousScope=effectivePreset();
    selectedPreset=s.selectedPreset||'common';
    var select=$("workflowPreset"),items=[{id:'common',name:T('preset.common')}].concat(s.workflowPresets||[]);
    R.reconcile(select,items,function(p){return p.id;},function(){return document.createElement('option');},function(option,p){option.value=p.id;R.text(option,p.name);});
    if(!items.some(function(p){return p.id===selectedPreset;}))selectedPreset='common';
    select.value=selectedPreset;applyAvatarMode(s.wardrobeMode==='multi-avatar');
    if(previousPreset!==selectedPreset&&avatarMode){dropCaches();load();}
    if(previousScope!==effectivePreset()){if(snapshots)snapshots.contextChanged();if(currentView==='appearanceEditor')window.WardrobeAppearanceEditor.show();}
  }
  function paintEmptyGrid(){
    var empty=listItems.length===0;
    emptyGridState.classList.toggle("on",empty);
    if(!empty) return;
    var state=lastState||{},filtered=!!(search||shop||category||hideEmpty||filter!=="all");
    var mode=!connected&&state.desktop?"offline":(initialGridPending||indexing||listInflight||indexAction)?"loading":connected&&lastState&&!state.avatarInstanceId?"target":gridNotice==="error"?"error":state.desktop&&state.outfits===0?"files":filtered?"filters":"empty";
    var message=mode==="loading"?T(indexing?(indexPhase==="discovery"?"index.discovery":indexPhase==="dependencies"?"index.dependencies":"banner.indexing"):"grid.loading",indexDone,indexTotal):T({target:"grid.chooseAvatarHint",error:"grid.error",offline:viewFeatures.library?"grid.offlineHint":"grid.connectUnityHint",files:viewFeatures.library?"grid.addFilesHint":"grid.importInUnityHint",filters:"grid.empty",empty:"grid.empty"}[mode]);
    if(message==="grid.loading")message=T("grid.loading");
    var actions=mode==="target"?[["gridChooseAvatar","grid.chooseAvatar"]]:mode==="files"?(viewFeatures.library?[["gridAddFiles","library.add"]]:[]):mode==="filters"?[["gridClearFilters","grid.clearFilters"]]:mode==="error"||mode==="offline"?[["gridRetry","grid.retry"]]:[];
    if(viewFeatures.library&&state.desktop&&(mode==="offline"||mode==="error"))actions.push(["gridAddFiles","grid.openLibrary"]);
    var signature=JSON.stringify([mode,langCode,actions]);
    if(emptyGridState.dataset.mode!==signature){
      emptyGridState.dataset.mode=signature;
      emptyGridState.innerHTML=(mode==="loading"?spinner(36):'<svg class="icon empty-icon" aria-hidden="true"><use href="#icon-wardrobe"></use></svg>')+'<strong class="empty-grid-label"></strong>'+actions.map(function(action){return '<button type="button" id="'+action[0]+'">'+esc(T(action[1]))+'</button>';}).join('');
    }
    R.text(emptyGridState.querySelector(".empty-grid-label"),message);
  }
  function emptyGridAction(id){
    if(id==="gridRetry"){load();return;}
    if(id==="gridChooseAvatar"){$("wardrobeTarget").focus();return;}
    if(id==="gridAddFiles"&&viewFeatures.library){setBatchView("library");$("libraryFiles").focus();return;}
    if(id==="gridClearFilters"){
      search=shop=category="";hideEmpty=0;$("search").value=$("shop").value=$("category").value="";
      R.store("wardrobeHideEmpty","0");paintHide();selectFilter("all");$("search").focus();
    }
  }

  var langCode="en", langTable={}, langList=[];
  function T(key){
    // Empty/loading states can render before the language request completes.
    var gridFallback={"grid.importInUnityHint":"No outfits are available yet. Import your outfit packages in Unity, then refresh the wardrobe.","grid.connectUnityHint":"Unity is offline. Open this project in Unity to reconnect.","grid.empty":"No items match your filters.","grid.loading":"Loading wardrobe…","grid.error":"Could not load items.","grid.retry":"Try again","grid.chooseAvatar":"Choose avatar","grid.chooseAvatarHint":"Choose the scene avatar you want to dress.","grid.addFilesHint":"No outfits are available in this project yet. Add your purchased files to get started.","grid.offlineHint":"Unity is offline. Your local library is still available.","grid.clearFilters":"Clear filters","grid.openLibrary":"Open local library","library.add":"Add purchased files"};
    var v=(langTable[key]!=null)?langTable[key]:(gridFallback[key]||key);
    for(var a=1;a<arguments.length;a++) v=v.split("{"+(a-1)+"}").join(arguments[a]==null?"":arguments[a]);
    return v;
  }
  function resolveLang(stored, navs, codes){
    if(stored && codes.indexOf(stored)>=0) return stored;
    for(var i=0;i<navs.length;i++){
      var n=String(navs[i]||"").toLowerCase();
      for(var c=0;c<codes.length;c++){
        if(n===codes[c]||n.indexOf(codes[c]+"-")===0) return codes[c];
      }
    }
    return "en";
  }
  function loadLangs(){
    return api("/lang.json").then(function(d){
      langList=(d&&d.langs)||[];
      var codes=langList.map(function(l){return l.code;});
      var stored="";
      try{ stored=localStorage.getItem("wardrobeLang")||""; }catch(e){}
      var navs=[];
      try{ navs=navigator.languages||[navigator.language]; }catch(e){}
      langCode=resolveLang(stored, navs, codes);
      if(codes.indexOf(langCode)<0) langCode="en";
      var table={};
      langList.forEach(function(l){
        if(l.code===langCode) (l.strings||[]).forEach(function(en){ table[en.k]=en.v; });
      });
      langList.forEach(function(l){
        if(l.code==="en") (l.strings||[]).forEach(function(en){ if(table[en.k]==null) table[en.k]=en.v; });
      });
      langTable=table;
      if(R.setLanguage)R.setLanguage(langCode,T);
      document.documentElement.lang=langCode;
      if(T("app.title")!=="app.title") document.title=T("app.title");
    }).catch(function(){ /* Keep the last loaded language during temporary connection failures. */ });
  }
  function applyStrings(){
    document.querySelectorAll("[data-i18n]").forEach(function(el){ var key=el.getAttribute("data-i18n"),value=T(key);if(value!==key) el.textContent=value; });
    document.querySelectorAll("[data-i18n-ph]").forEach(function(el){ el.setAttribute("placeholder",T(el.getAttribute("data-i18n-ph"))); });
    document.querySelectorAll("[data-i18n-title]").forEach(function(el){ el.setAttribute("title",T(el.getAttribute("data-i18n-title"))); });
    document.querySelectorAll("[data-i18n-aria]").forEach(function(el){ el.setAttribute("aria-label",T(el.getAttribute("data-i18n-aria"))); });
    if(T("app.title")!=="app.title") document.title=T("app.title");
    document.querySelectorAll("[data-i18n-alt]").forEach(function(el){el.setAttribute("alt",T(el.getAttribute("data-i18n-alt")));});
    buildLangSelect();
    if(window.WardrobeLibrary)window.WardrobeLibrary.localize(T);
    if(window.WardrobeSceneEditor)window.WardrobeSceneEditor.configure({api:api,T:T,root:$("advancedScene")});
    paintHide();
  }
  function lookup(key){ var v=T(key); return v===key?null:v; }
  function wearName(w){ return lookup("wear."+w)||w; }
  function kindName(k){ var key="kind."+String(k||"").toLowerCase(); return lookup(key)||k; }
  function buildLangSelect(){
    var sel=document.getElementById("lang");
    var stored="";
    try{ stored=localStorage.getItem("wardrobeLang")||""; }catch(e){}
    sel.innerHTML="";
    var auto=document.createElement("option"); auto.value=""; auto.textContent="🌐 "+T("lang.auto"); sel.appendChild(auto);
    langList.forEach(function(l){
      var o=document.createElement("option"); o.value=l.code; o.textContent=l.name||l.code; sel.appendChild(o);
    });
    sel.value=stored;
  }


  var previews=new WardrobePreviews({text:T,onActivity:function(value){previewActivity=value;hud();},onUnavailable:function(node){
    if(filter==="all"||!hideEmpty||!node.classList.contains("thumb")) return false;
    var card=node.closest(".card"); if(card) card.hidden=true; return true;
  }});
  function loadDetailThumb(guid){ var wrap=modalContent.querySelector(".imgwrap"); if(wrap) previews.bind(wrap,guid,{priority:0,detail:true}); }
  function markVariantInstalled(familyId,guid,isInstalled,exclusive){
    var detail=detailCache.get(familyId);
    if(detail) detail.variants.forEach(function(variant){ if(variant.guid===guid) variant.installed=isInstalled?1:0; else if(exclusive&&isInstalled) variant.installed=0; });
    var any=detail?detail.variants.some(function(v){return v.installed;}):isInstalled;
    listCache.forEach(function(result){ result.items.forEach(function(f){ if(f.id===familyId) f.installed=any?1:0; }); });
    listItems.forEach(function(f){if(f.id===familyId) f.installed=any?1:0;});
    var card=cardCache.get(familyId),row=listItems.find(function(f){return f.id===familyId;}); if(card&&row) paintCardBody(card,row);
  }
  function cardBodyHtml(f){
    var compatibility=filter==="unknown"&&!f.compatibleOverride?"":'<span class="badge c'+Number(f.compat)+'">'+esc(f.compatText)+'</span>';
    return '<div class="body"><div class="name" title="'+esc(f.name)+'">'+esc(f.name)+'</div><div class="meta">'+esc(T(f.variants===1?"card.variant.one":"card.variant.other",f.variants))+'</div><div class="meta creator" title="'+esc([f.shop,f.product].filter(Boolean).join(" / "))+'">'+(esc([f.shop,f.product].filter(Boolean).join(" / "))||" ")+'</div><div class="badges">'+compatibility+(f.installed?'<span class="installed">'+esc(T("card.installed"))+'</span>':"")+'</div></div>'+(f.variants>1?'<span class="vcount">×'+Number(f.variants)+'</span>':"");
  }
  function paintCardBody(card,f){
    card.dataset.family=f.id; card.classList.toggle("selected",selectedFamilyId===f.id);
    card.setAttribute("aria-label",f.name+", "+f.variants+" "+T("detail.variants"));
    var body=cardBodyHtml(f);
    if(card._body!==body){ card._body=body; card.querySelector(".card-info").innerHTML=body; }
    card._family=f;
  }
  var gridPreloadFrame=0,gridPreloadCount=40;
  function scheduleGridPreload(){
    if(gridPreloadFrame) return;
    gridPreloadFrame=requestAnimationFrame(function(){gridPreloadFrame=0;preloadGrid();});
  }
  // Share the same viewport and look-ahead set with the Unity high-res warmer.
  function gridPreviewDemand(){
    var main=$("main"),bounds=main.getBoundingClientRect();
    var cards=Array.from(grid.children).filter(function(card){return !card.hidden;});
    var visible=[],lastVisible=-1;
    if(currentView==="wardrobe") cards.forEach(function(card,index){
      var rect=card.getBoundingClientRect();
      if(rect.width>0&&rect.height>0&&rect.bottom>bounds.top&&rect.top<bounds.bottom&&rect.right>bounds.left&&rect.left<bounds.right){
        lastVisible=index;visible.push(card);
      }
    });
    var ahead=lastVisible<0?[]:cards.slice(lastVisible+1,lastVisible+1+gridPreloadCount);
    // Covers first, then visible families' variants, before speculative cards.
    // Round-robin variants so a large family cannot monopolize the warm queue.
    var priority=new Set(visible.map(function(card){return card._family.thumb;}).filter(Boolean));
    var variants=visible.map(function(card){return card._family.variantGuids||[];});
    var variantCount=variants.reduce(function(max,items){return Math.max(max,items.length);},0);
    for(var i=0;i<variantCount&&priority.size<120;i++) variants.forEach(function(items){
      if(items[i]&&priority.size<120) priority.add(items[i]);
    });
    var visibleGuids=Array.from(priority).slice(0,120);
    ahead.forEach(function(card){if(card._family.thumb&&priority.size<120) priority.add(card._family.thumb);});
    return {visible:visible,ahead:ahead,remaining:lastVisible<0?gridPreloadCount:cards.length-lastVisible-1,
      visibleGuids:visibleGuids.join(","),guids:Array.from(priority).slice(0,120).join(",")};
  }
  var previewDemandTimer=0,lastPreviewDemand="",lastPreviewGrid=null;
  function schedulePreviewDemand(){
    if(previewDemandTimer) return;
    previewDemandTimer=setTimeout(function(){
      previewDemandTimer=0;
      var demand=gridPreviewDemand();
      if(!document.hidden&&demand.guids+"|"+demand.visibleGuids!==lastPreviewDemand) pingActive(true);
    },150);
  }
  function preloadGrid(){
    if(document.hidden||currentView!=="wardrobe") return;
    var main=$("main"),demand=gridPreviewDemand();
    demand.visible.forEach(function(card){
      previews.bind(card.querySelector(".thumb"),card._family.thumb,{priority:1,root:main});
    });
    demand.ahead.forEach(function(card){
      previews.bind(card.querySelector(".thumb"),card._family.thumb,{priority:3,root:main});
    });
    schedulePreviewDemand();
    // Fetch the next metadata page early enough to keep forty cards ahead of the viewport.
    if(demand.remaining<gridPreloadCount) loadMore();
  }

  function renderGrid(){
    R.reconcile(grid,listItems,function(f){return f.id;},function(f){
      var card=cardCache.get(f.id);
      if(!card){
        card=document.createElement("article"); card.className="card"; card.tabIndex=0; card.setAttribute("role","button");
        card.innerHTML='<div class="thumb preview-loading"></div><div class="card-info"></div>';
        card.onclick=function(event){ if(!event.target.closest("button")) openDetail(card._family.id); };
        card.onkeydown=function(event){ if(event.target===card&&(event.key==="Enter"||event.key===" ")){event.preventDefault();openDetail(card._family.id);} };
        window.WardrobeDragDrop.source(card,function(){var family=card._family;return {version:1,familyId:family.id,variantId:family.variantGuids&&family.variantGuids.length===1?family.variantGuids[0]:'',targetKey:dragContextKey()};});
        cardCache.set(f.id,card);
      }
      return card;
    },function(card,f){
      card.hidden=filter!=="all" && !!hideEmpty && previews.isUnavailable(f.thumb); paintCardBody(card,f);
      previews.observe(card.querySelector(".thumb"),f.thumb,{priority:1,root:$("main")});
    });
    // Detached cards are disposable; retain the current grid and a small warm history.
    if(cardCache.size>180) cardCache.forEach(function(card,id){if(cardCache.size>180&&!card.isConnected) cardCache.delete(id);});
    previews.sweep(); paintCount(); paintEmptyGrid();scheduleGridPreload();
  }
  function queryFor(p){ return "/api/families?target="+encodeURIComponent(effectivePreset())+"&filter="+filter+"&page="+p+"&pageSize="+pageSize+"&search="+encodeURIComponent(search)+"&shop="+encodeURIComponent(shop)+"&category="+encodeURIComponent(category)+"&hideEmpty="+hideEmpty+"&sort="+sortMode; }
  function paintCount(){
    R.text(status,T("page.loaded",listItems.length,total)); R.text(pageinfo,status.textContent);
    var more=$("more"); more.hidden=page>=pageCount-1; more.disabled=listInflight; more.textContent=T(listInflight?"page.loading.more":"page.more");
  }
  async function load(append,preserveScroll){
    append=!!append;
    if(append&&(listInflight||page>=pageCount-1)) return;
    if(listController) listController.abort();
    listController=new AbortController();
    var token=++listToken,controller=listController,target=append?page+1:0;
    var lastPage=!append&&preserveScroll?page:target;
    var cachePrefix=[langCode,catalogEpoch,contextKey].join("|")+"|";
    listInflight=true; gridNotice=""; paintEmptyGrid(); paintCount();
    try {
      // Keep the full loaded range until its replacement is ready, so the grid
      // never collapses to page one during an install or background refresh.
      var refreshed=[],result;
      for(var next=target;next<=lastPage;next++){
        var query=queryFor(next),key=cachePrefix+query;
        result=listCache.get(key)||await api(query,{signal:controller.signal,method:"GET",timeout:40000});
        if(token!==listToken) return;
        if(!result||!Array.isArray(result.items)) throw new Error((result&&result.message)||T("grid.error"));
        cacheSet(listCache,key,result,32);
        refreshed=refreshed.concat(result.items);
        lastPage=Math.min(lastPage,Math.max(0,result.pageCount-1));
      }
      var merged=append?listItems.concat(refreshed):refreshed;
      listItems=Array.from(new Map(merged.map(function(f){return [f.id,f];})).values());
      page=result.page;pageCount=Math.max(1,result.pageCount);total=result.total;
      var scrollTop=$("main").scrollTop;
      renderGrid();
      if(preserveScroll) $("main").scrollTop=scrollTop;
      if(!document.hidden) pingActive(true);
      if(!append&&!preserveScroll) $("main").scrollTop=0;
    } catch(error){
      if(token!==listToken||error.name==="AbortError") return;
      gridNotice="error";
      if(listItems.length) toast(error.message||T("grid.error"),"err");
    } finally {
      if(token===listToken){ initialGridPending=false; listInflight=false; paintEmptyGrid(); paintCount(); if(!gridNotice) scheduleGridPreload(); }
    }
  }
  function groupInstalledItems(items,presets,separate){
    var groups=new Map();
    groups.set("common",{id:"common",name:separate?T("preset.common"):T("side.installed"),items:[]});
    if(separate)presets.forEach(function(p){groups.set(p.id,{id:p.id,name:p.name,items:[]});});
    items.forEach(function(it){
      var key=separate?(it.target||"common"):"common";
      if(!groups.has(key))groups.set(key,{id:key,name:it.targetName||T("detail.preset.tag"),items:[]});
      groups.get(key).items.push(it);
    });
    return groups;
  }
  function loadInstalled(){
    if(installedFlight) return installedFlight;
    var expectedContext=contextKey;
    installedFlight=api("/api/installed",{method:"GET",timeout:40000}).then(function(result){
      if(expectedContext!==contextKey||!result||!Array.isArray(result.items)) return;
      var signature=JSON.stringify([result.items,(lastState||{}).workflowPresets||[]])+"|"+avatarMode+"|"+effectivePreset()+"|"+langCode+"|"+previewContext;
      if(signature===sideContent.dataset.signature) return;
      sideContent.dataset.signature=signature; installedItems=result.items;
      installedIdentity=result.projectId&&result.avatarId?JSON.stringify([result.projectId,result.avatarId]):null;
      var list=$("instlist"),groups=groupInstalledItems(installedItems,(lastState||{}).workflowPresets||[],!!avatarMode);
      R.reconcile(list,Array.from(groups.values()),function(group){return group.id;},function(){
        var group=document.createElement("details");group.className="installed-preset";group.open=true;
        group.innerHTML='<summary><span></span><small></small></summary><div class="installed-preset-items"></div>';return group;
      },function(group,data){
        R.text(group.querySelector("summary span"),data.name);R.text(group.querySelector("summary small"),data.items.length);
        R.reconcile(group.querySelector(".installed-preset-items"),data.items,function(it){return it.guid+"|"+it.instanceId+"|"+(it.path||"");},function(){
          var el=document.createElement("button");el.type="button";el.className="inst";
          el.innerHTML='<span class="inst-thumb"></span><span class="inst-copy"><span class="t"></span><span class="s"></span></span><span class="inst-arrow" aria-hidden="true">›</span>';
          el.onclick=function(){openDetail(el._item.familyId,el._item.guid,el._item);};
          window.WardrobeDragDrop.target(el,{outfit:function(payload){dropOutfit(payload,'replace-outfit',el._item);},error:function(message){toast(message,'err');}});el.title=T("ui.drop.a.variant.here.to.replace.this.exact.worn");return el;
        },function(el,it){
          el._item=it;var variant=it.variant&&it.variant.toLowerCase()!=="default"?it.variant:T("variant.default");
          R.text(el.querySelector(".t"),it.family);R.text(el.querySelector(".s"),it.setupWarning?T("setup.attention")+" — "+it.setupWarning:variant);el.title=it.family+" · "+variant;
          previews.observe(el.querySelector(".inst-thumb"),it.guid,{priority:2,root:side});
        });
      });
      var targetItems=installedItems.filter(function(it){return !avatarMode||(it.target||"common")===effectivePreset();});
      var installedGuids=new Set(targetItems.map(function(x){return x.guid;}));
      var installedFamilies=new Set(targetItems.map(function(x){return x.familyId;}));
      detailCache.forEach(function(detail){detail.variants.forEach(function(v){v.installed=installedGuids.has(v.guid)?1:0;});});
      listItems.forEach(function(f){f.installed=installedFamilies.has(f.id)?1:0;var card=cardCache.get(f.id);if(card) paintCardBody(card,f);});
      listCache.clear();
      if(!installedItems.length&&groups.size===1) list.innerHTML='<p class="subtle">'+esc(T("inst.empty"))+'</p>';
    }).catch(function(){}).finally(function(){if(expectedContext===contextKey)installedFlight=null;});
    return installedFlight;
  }
  function hud(){
    var s=lastState||{},pending=previewActivity.active+previewActivity.queued;
    R.text($("hudLow"),T(!connected?(s.desktop&&viewFeatures.library?"library.offlineReady":"status.reconnecting"):indexing?"banner.indexing":pending?"status.rendering":"status.browseReady",indexDone,indexTotal));
    R.text($("indexedCount"),s.outfits?T("status.indexed",s.outfits):"");
    R.text($("pendingCount"),s.dirty?"· "+T(s.dirty===1?"status.pending":"status.pending.other",s.dirty):"");
    R.text($("hudHi"),pending?T("preview.queue",previewActivity.active,previewActivity.queued):T("preview.idle"));
    // Cache coverage is not a completion target: only the current grid and
    // nearby items are warmed. Keep it in Activity, separate from actual work.
    $("hudPercent").hidden=true; $("hudHiBar").parentNode.hidden=true;
    $("connectionDot").classList.toggle("offline",!connected);
    R.text($("connectionLabel"),T(connected?"status.connected":"status.disconnected"));
  }
  function paintActivity(){
    var s=lastState||{};
    R.text($("activityCatalog"),T("status.indexed",s.outfits||0));
    R.text($("activityIndexState"),indexing?T("banner.indexing",indexDone,indexTotal):s.dirty?T("banner.dirty",s.dirty):T("activity.current"));
    R.text($("activityPreviews"),s.hiTotal?T("preview.cacheCoverage",s.hiBaked,s.hiTotal):"—");
    R.text($("activityConnection"),T(connected?"status.connected":"status.disconnected"));
    if(activityEntries.length) R.reconcile($("activityLog"),activityEntries,function(e){return e.id;},function(){var n=document.createElement("li");n.innerHTML='<time></time><span></span>';return n;},function(n,e){R.text(n.querySelector("time"),e.time);R.text(n.querySelector("span"),e.message);n.className=e.type;});
  }
  function refreshState(){
    if(stateFlight) return stateFlight;
    var expectedWorkflow=workflowRevision;
    stateFlight=api("/api/state",{method:"GET",timeout:30000}).then(function(s){
      if(!s||s.pending||s.ok===0) throw new Error("No state");
      var firstDesktopState=!lastState&&s.desktop,wasConnected=connected;
      connected=s.bridgeOnline!==false; lastState=s; indexing=s.indexing?1:0;indexPhase=s.indexPhase||(s.total?"parsing":"discovery");indexDone=s.done||0;indexTotal=s.total||0;
      R.setContext(s.session, s.avatarInstanceId);
      $("navLibrary").hidden=!s.desktop||!viewFeatures.library;
      if(s.bridgeOnline===false&&(wasConnected||firstDesktopState)) toast(T(viewFeatures.library?"library.offlineHelp":"grid.connectUnityHint"),"info");
      if(firstDesktopState&&viewFeatures.library)setBatchView("library");
      window.WardrobeUpdates.refresh(s.wardrobeVersion, T);
      window.WardrobeReporting.setVersion(s.wardrobeVersion);
      operations.setHost(!!s.desktop);
      $("dressing").hidden=!s.desktop||!viewFeatures.dressingRoom;$("wearingDropHint").hidden=!s.desktop;document.body.classList.toggle("desktop-dressing",!!s.desktop&&viewFeatures.dressingRoom);document.body.classList.toggle("automatic-base",!s.avatarBaseEditable);
      if(!baseSaving){
        var baseSelect=$("avatarBase"),choices=[{guid:"",name:T("avatar.base.auto"),path:""}].concat(s.baseAvatars||[]);
        R.reconcile(baseSelect,choices,function(c){return c.guid;},function(){return document.createElement("option");},function(option,c){option.value=c.guid;R.text(option,c.name);option.title=c.path||"";});
        baseSelect.value=s.avatarOverrideGuid||"";
        baseSelect.disabled=!s.avatarBaseEditable;
        baseSelect.title=s.avatarBaseEditable?T(s.avatarSourceGuid?"avatar.base.saved":"avatar.base.sceneSaved"):T("avatar.base.unlinked");
      }
      if(uploadUI)uploadUI.setUploadReadiness(s.sdkLoggedIn,s.sdkUploadReady);
      var nextContext=[s.session||"",s.avatarGuid,s.avatarName,s.avatarInstanceId||""].join("|");
      var changed=s.epoch!==catalogEpoch||nextContext!==contextKey;
      var avatarChanged=nextContext!==contextKey;
      if(avatarChanged){
        closeModal(true);detailLoadToken++;detailToken++;
        installedFlight=null;installedItems=[];installedIdentity=null;sideContent.dataset.signature="";$("instlist").innerHTML="";
        listToken++;if(listController)listController.abort();listInflight=false;
        dropCaches();listItems=[];cardCache.clear();renderGrid();
        selectedPreset="common";$("workflowPreset").innerHTML="";
        if(uploadUI)uploadUI.setContext(nextContext);
      }
      catalogEpoch=s.epoch||""; contextKey=nextContext;
      if(avatarChanged||expectedWorkflow===workflowRevision)paintWorkflowState(s);
      if(avatarChanged&&uploadUI&&currentView==="upload")uploadUI.show();
      if(avatarChanged&&currentView==="appearanceEditor")window.WardrobeAppearanceEditor.show();
      if(avatarChanged&&currentView==="menuOrganizer")window.WardrobeMenuOrganizer.show();
      if(avatarChanged&&currentView==="advancedScene"&&window.WardrobeSceneEditor)window.WardrobeSceneEditor.show();
      var nextPreviews=[s.session||"",s.epoch||"",s.previewEpoch||0].join("|");
      var previewsChanged=nextPreviews!==previewContext;
      previewContext=nextPreviews;
      previews.reset(nextPreviews);
      if(previewsChanged) previews.resume();
      previews.refresh();
      R.text(avatar,s.avatarLabel||s.avatarName||T("avatar.none"));
      var targetSelect=$('wardrobeTarget'),targetChoices=s.sceneTargets||[];
      if(targetSelect){
        var signature=langCode+"|"+JSON.stringify(targetChoices);
        if(targetSelect.dataset.choices!==signature){
          targetSelect.innerHTML='<option value="">'+esc(T('target.choose'))+'</option>'+targetChoices.map(function(a){return '<option value="'+a.id+'">'+esc(a.name+' · '+(a.scene.split('/').pop()||T('target.unsaved')))+'</option>';}).join('');
          targetSelect.dataset.choices=signature;
        }
        targetSelect.value=String(s.avatarInstanceId||'');
        targetSelect.onchange=async function(){
          if(!this.value)return;
          this.disabled=true;
          try{var result=await api('/api/target?id='+encodeURIComponent(this.value));if(!result.ok)throw new Error(result.message);await refreshState();}
          catch(error){toast(error.message,'err');this.value=String((lastState||{}).avatarInstanceId||'');}
          finally{this.disabled=false;}
        };
      }
      R.text($('targetLocation'),[s.projectPath?s.projectPath.split('/').pop():'',s.scenePath?s.scenePath.split('/').pop():''].filter(Boolean).join(' / '));
      aiAvailable=s.ai?1:0;
      if(changed){dropCaches();loadShops();load(false,listItems.length>0);}
      if(indexAction){if(indexing) indexAction.runningSeen=true;else if(indexAction.runningSeen||Date.now()-indexAction.startedAt>2500) finishIndexAction();}
      hud();paintActivity();paintEmptyGrid();loadInstalled();
      if(s.dirty>0&&!indexing&&!indexAction&&Date.now()>=nextAutoIndexAt){
        nextAutoIndexAt=Date.now()+30000;
        startIndex("/api/index",null,"",true);
      }

      return s;
    }).catch(function(){connected=false;hud();paintActivity();}).finally(function(){stateFlight=null;});
    return stateFlight;
  }
  var nextAutoIndexAt=0;
  function closeModal(force){
    if(detailBusy()&&force!==true) return;
    detailLoadToken++;detailToken++;modal.classList.remove("on");sideBackdrop.classList.remove("on");
    inspectorMode="installed";selectedFamilyId="";detailSelect=null;
    grid.querySelectorAll(".selected").forEach(function(card){card.classList.remove("selected");});R.closeDialog(modal);
  }
  function closeDetail(){closeModal();}
  $("modalClose").onclick=closeDetail;sideBackdrop.onclick=closeDetail;
  var detailInstanceId=0;
  function openDetail(id,selGuid,instance){
    detailInstanceId=instance?instance.instanceId||0:0;
    if(detailBusy()) return;
    inspectorMode="selected";selectedFamilyId=id;modal.classList.add("on");sideBackdrop.classList.add("on");
    modal.scrollTop=0;R.text(modalMode,T(filter==="unknown"?"side.review":"side.selected"));
    grid.querySelectorAll(".card").forEach(function(card){card.classList.toggle("selected",card.dataset.family===id);});
    var token=++detailLoadToken;
    if(detailCache.has(id)){renderDetail(detailCache.get(id),selGuid,instance);R.openDialog(modal);return;}
    R.text(modalTitle,T("detail.loading"));modalContent.innerHTML='<div class="inspector-loading">'+spinner(24)+'<span>'+esc(T("detail.loading"))+'</span></div>';R.openDialog(modal);
    api("/api/family?id="+encodeURIComponent(id)+"&target="+encodeURIComponent(effectivePreset()),{method:"GET"}).then(function(d){
      if(token!==detailLoadToken) return;
      if(!d||!d.variants) throw new Error((d&&d.message)||T("err.notfound"));
      cacheSet(detailCache,id,d,96);renderDetail(d,selGuid,instance);
    }).catch(function(error){if(token===detailLoadToken){
      R.text(modalTitle,T("detail.loadFailed"));
      modalContent.innerHTML='<div class="detail-load-error" role="status"><p></p><button type="button">'+esc(T("grid.retry"))+'</button></div>';
      R.text(modalContent.querySelector('p'),error.message||T("err.notfound"));
      modalContent.querySelector('button').onclick=function(){openDetail(id,selGuid,instance);};
    }});
  }

  function renderDetail(d,selGuid,instance){
    // The compatible-only grid admits a family when any variant is compatible.
    // Keep the modal consistent with that promise: show only the compatible
    // (or probably-compatible) variants in its filmstrip and navigation.
    var variants=!selGuid&&filter==="compatible"
      ? d.variants.filter(function(x){ return x.compat===0||x.compat===1; })
      : d.variants;
    if(!variants.length){ closeDetail(); return; }
    var startIndex=0;
    if(selGuid) for(var k=0;k<variants.length;k++) if(variants[k].guid===selGuid){ startIndex=k; break; }
    var v=variants[startIndex], i=startIndex;
    detailCount=variants.length; detailIndex=startIndex;
    function variantLabel(x,n){
      var label=String(x.variant||"").trim();
      if(label&&label.toLowerCase()!=="default"&&variants.filter(function(v){return v.variant===label;}).length===1) return label;
      var color=String(x.colorway||"").trim();
      if(color&&variants.filter(function(v){return v.colorway===color;}).length===1) return color;
      if(variants.length===1) return T("variant.default");
      var base=String(x.source||"").split("/").pop().replace(/\.prefab$/i,"");
      return base&&variants.filter(function(v){return String(v.source||"").split("/").pop().replace(/\.prefab$/i,"")===base;}).length===1?base:T("detail.variant",n+1);
    }
    function swatch(label){
      var color={black:"#151515",white:"#eee",pink:"#ed87aa",blue:"#6aa9eb",red:"#db5c5c",green:"#65b878",purple:"#a381d2",gold:"#d7ad45",brown:"#956e50",grey:"#aaa",gray:"#aaa"}[String(label||"").toLowerCase()];
      return color?'<i class="swatch" style="background:'+color+'"></i>':"";
    }
    function variantBody(){
      var rows=[[T("dt.source"),v.source],[T("dt.setup"),T(v.setup?"dt.setup.yes":"dt.setup.no")],
        [T("dt.confidence"),v.confidence||"—"]];
      if(v.mats&&v.mats.length) rows.push([T("dt.materials"),v.mats.join(", ")]);
      if(v.parts&&v.parts.length) rows.push([T("dt.parts"),v.parts.join(", ")]);
      return '<details class="technical"><summary>'+esc(T("detail.technical"))+'</summary><dl>'+rows.map(function(row){
        return '<dt>'+esc(row[0])+'</dt><dd>'+esc(row[1])+'</dd>';
      }).join("")+'</dl></details>';
    }
    function draw(){
      var multi=variants.length>1;
      modalTitle.textContent=d.name;
      modalContent.innerHTML=
        '<div class="detail-columns"><section class="detail-gallery" aria-label="'+esc(T("detail.variants"))+'">'+
        '<div id="dCreator" class="inspector-creator"></div>'+
        '<div class="imgwrap preview-loading"></div>'+
        '<div class="variant-head"><span>'+esc(multi?T("detail.variants"):T("variant.default"))+'</span><span id="dVariantCount"></span></div>'+
        (multi
          ? '<div class="variant-control"><button class="variant-nav" id="dPrevVar" aria-label="'+esc(T("nav.prev.variant"))+'">&#8249;</button><div class="filmstrip" id="dFilm"></div><button class="variant-nav" id="dNextVar" aria-label="'+esc(T("nav.next.variant"))+'">&#8250;</button></div>'
          : '<div class="single-variant-label" id="dSingleVariant"></div>')+
        '</section><section class="detail-options"><div id="dCompatibility"></div><div id="dPresetWrap"></div><div id="dVarBody"></div><div id="dAllowWrap"></div></section></div>'+
        '<footer class="detail-footer"><button id="dReportItem">'+esc(T("report.item"))+'</button><div class="actions" id="dActs"></div></footer>';
      document.getElementById("dReportItem").onclick=function(){window.WardrobeReporting.openItem(d,v);};
      if(multi) buildFilm();
      selectVariant(startIndex);
    }
    // Gallery strip: one thumbnail per variant; clicking switches the main
    // view in place (metadata, actions, big image) with no full redraw.
    function buildFilm(){
      var film=document.getElementById("dFilm");
      film.innerHTML="";
      variants.forEach(function(x,n){
        var b=document.createElement("button");
        b.className="film"+(n===i?" on":"");
        b.title=variantLabel(x,n);
        b.innerHTML='<div class="none">'+spinner(18)+'</div><div class="fv">'+swatch(variantLabel(x,n))+esc(variantLabel(x,n))+(x.installed?'<span class="film-state">'+esc(T("detail.installed"))+'</span>':"")+"</div>";
        b.onclick=function(){ if(n!==i) selectVariant(n); };
        film.appendChild(b);
        filmThumb(x.guid,b);
      });
      // Vertical wheel drives the horizontal strip; at either end the event
      // propagates so the sheet itself scrolls. Trackpad horizontals pass
      // through untouched. Requires non-passive to preventDefault.
      film.addEventListener("wheel",function(e){
        if(Math.abs(e.deltaY)<=Math.abs(e.deltaX)) return;
        var max=film.scrollWidth-film.clientWidth;
        if(max<=0) return;
        if((e.deltaY>0&&film.scrollLeft>=max-1)||(e.deltaY<0&&film.scrollLeft<=1)) return;
        e.preventDefault();
        film.scrollLeft+=e.deltaY*(e.deltaMode===1?16:1);
      },{passive:false});
      document.getElementById("dPrevVar").onclick=function(){ selectVariant((i+variants.length-1)%variants.length); };
      document.getElementById("dNextVar").onclick=function(){ selectVariant((i+1)%variants.length); };
    }
    function filmThumb(guid,btn){
      var box=btn.querySelector(".none");
      if(box) previews.observe(box,guid,{priority:1});
    }
    function selectVariant(n){
      if(detailBusy()) return;
      presetsReady=false;settingsReady=false;
      i=n; v=variants[n]; detailVariantGuid=v.guid;detailIndex=n; detailSelect=selectVariant;
      var count=document.getElementById("dVariantCount");
      if(count) count.textContent=variants.length>1?T("detail.variant.count",n+1,variants.length):"";
      var single=document.getElementById("dSingleVariant");
      if(single) single.textContent=variantLabel(v,n);
      var film=document.getElementById("dFilm"), btns=film?film.children:[];
      for(var k=0;k<btns.length;k++){btns[k].classList.toggle("on",k===n);btns[k].setAttribute("aria-pressed",String(k===n));}
      if(film&&btns[n]) { var b=btns[n]; if(b.offsetLeft<film.scrollLeft||b.offsetLeft+b.offsetWidth>film.scrollLeft+film.clientWidth) film.scrollLeft=Math.max(0,b.offsetLeft-film.clientWidth/2+b.offsetWidth/2); }
      R.text(document.getElementById("dCreator"),[v.shop,v.product].filter(Boolean).join(" / "));
      document.getElementById("dCompatibility").innerHTML='<div class="badge c'+v.compat+'">'+esc(v.compatText)+'</div><p class="subtle">'+esc(v.explanation||"")+'</p>'+
        (v.installed?'<div class="variant-installed">✓ '+esc(T("detail.installed"))+'</div>':"");
      document.getElementById("dVarBody").innerHTML=variantBody();
      var familyInstalled=d.variants.some(function(x){ return !!x.installed; });
      document.getElementById("dAllowWrap").innerHTML=
        '<details class="technical"><summary>'+esc(T("detail.advancedOptions"))+'</summary><label class="allow"><input type="checkbox" id="dCreateToggles" disabled> '+esc(T('detail.generateToggles'))+'</label><div class="subtle">'+esc(T('detail.generateTogglesHint'))+'</div><div id="dToggleStatus" class="subtle" role="status" aria-live="polite"></div>'+
        '<label class="allow"><input type="checkbox" id="dCompatibleOverride"'+(v.compatibleOverride?' checked':'')+'> '+esc(T("detail.compatibleOverride"))+'</label>' +'</details>';
      document.getElementById("dCompatibleOverride").onchange=function(){
        var box=this, guid=v.guid, enabled=box.checked, saved=false;
        if(detailBusy()){box.checked=!enabled;return;}
        installInFlight=true;box.disabled=true;
        api("/api/compatibility_override?guid="+encodeURIComponent(guid)+"&enabled="+(enabled?"1":"0")).then(function(r){
          if(!r||!r.ok) throw new Error(r&&r.message||T("detail.install.fail"));
          saved=true;v.compatibleOverride=enabled;
          dropCaches();load();
          return api("/api/family?id="+encodeURIComponent(d.id)+"&target="+encodeURIComponent(effectivePreset()),{method:"GET"});
        }).then(function(updated){
          installInFlight=false;
          if(box.isConnected) renderDetail(updated,guid);
        }).catch(function(error){
          installInFlight=false;
          if(box.isConnected){box.checked=saved?enabled:!enabled;box.disabled=false;}
          toast(error.message,"err");
        });
      };
      document.getElementById("dPresetWrap").innerHTML='<label for="dPreset">'+esc(T("detail.preset.tag"))+'</label><div class="preset-row"><select id="dPreset" disabled></select><button id="dNewPreset" title="'+esc(T("detail.newPreset"))+'">+</button></div><div id="dPresetFeedback" class="detail-load-error" hidden><p id="dPresetStatus" class="subtle" role="status"></p><button id="dPresetRetry" type="button" hidden>'+esc(T('grid.retry'))+'</button></div>';
      document.getElementById('dPresetWrap').insertAdjacentHTML('beforeend','<div id="dGroupWrap" hidden><label for="dGroup">'+esc(T('detail.menuGroup'))+("</label><div class=\"preset-row\"><select id=\"dGroup\"><option value=\"\">"+esc(T("detail.noMenuGroup"))+"</option></select><button id=\"dNewGroup\" title=\""+esc(T("ui.new.menu.group"))+"\">+</button></div><div id=\"dGroupStatus\" class=\"subtle\" role=\"status\" aria-live=\"polite\"></div></div>"));
      var wornFamily=installedItems.filter(function(item){return item.familyId===d.id && item.target===effectivePreset();});
      document.getElementById('dPresetWrap').insertAdjacentHTML('beforeend',
        '<label for="dWearMode">'+esc(T('wear.action'))+'</label><select id="dWearMode"><option value="wear">'+esc(T('wear.apply'))+'</option>'+
        (wornFamily.length?'<option value="replace">'+esc(T('wear.replace'))+'</option>':'')+'<option value="copy">'+esc(T('wear.copy'))+'</option></select>'+
        '<label for="dReplaceCopy">'+esc(T('wear.replaceCopy'))+'</label><select id="dReplaceCopy">'+wornFamily.map(function(item){return '<option value="'+item.instanceId+'">'+esc(item.path+' — '+item.variant)+'</option>';}).join('')+'</select>'+
        '<p id="dSaveSemantics" class="subtle"></p><div id="dSettingsStatus" class="subtle" role="status" aria-live="polite"></div>');
      document.getElementById('dWearMode').value=wornFamily.length&&!v.installed?'replace':'wear';
      var replaceSelect=document.getElementById('dReplaceCopy');
      function paintWearMode(){replaceSelect.hidden=document.getElementById('dWearMode').value!=='replace';replaceSelect.previousElementSibling.hidden=replaceSelect.hidden;}
      document.getElementById('dWearMode').onchange=paintWearMode;paintWearMode();
      var action=("<button id=\"dTryOn\" hidden>"+esc(T("ui.try.on"))+"</button><button id=\"dRemove\" class=\"danger\" hidden>"+esc(T("detail.remove"))+"</button>")+
        '<button id="dAddPreset" class="primary">'+esc(T("wear.apply"))+'</button><button id="dCancelSettings" type="button" hidden>'+esc(T('detail.cancelSettings'))+'</button><button id="dApplySettings" type="button" class="primary" hidden>'+esc(T('detail.applySettings'))+'</button>';
      document.getElementById("dActs").innerHTML=action+
        (aiAvailable?'<button id="dAi">'+esc(T("detail.ai"))+"</button>":"");
      wireActions();
      if(viewFeatures.dressingRoom&&$('dTryOn'))window.WardrobeDragDrop.source($('dTryOn'),function(){return {version:1,familyId:d.id,variantId:v.guid,targetKey:dragContextKey()};});
      var dtok=++detailToken;
      loadDetailThumb(v.guid,dtok);
    }
    var lastPresets=[],installedPresets=[],groupLoadToken=0,presetLoadToken=0,presetsReady=false,settingsReady=false;
    function refreshPresets(){
      var guid=v.guid;
      return Promise.all([api("/api/presets"),api('/api/prefab_presets?guid='+encodeURIComponent(guid))]).then(function(results){
        var p=results[0],memberships=results[1];
        if(!p||!Array.isArray(p.presets)||!memberships||!Array.isArray(memberships.presets))throw new Error(T('detail.presetLoadFailed'));
        p.installedPresets=memberships.presets;return p;
      });
    }
    function presetNameOf(id){
      var choices=lastPresets.concat((lastState||{}).workflowPresets||[]);
      for(var k=0;k<choices.length;k++)if(choices[k]&&choices[k].id===id)return choices[k].name||'';
      return '';
    }
    function validPreset(id){return id==='common'||lastPresets.some(function(p){return p.id===id;});}
    function fillPresetSelect(sel,list,desired){
      var cur=desired||(sel.value&&sel.value!=='__new'?sel.value:instance?instance.target||'common':effectivePreset());
      lastPresets=list.presets;installedPresets=list.installedPresets;
      function installed(id){return installedPresets.some(function(p){return p.id===id;})?" ("+T('detail.installed')+")":"";}
      sel.innerHTML="";
      function option(id,name,disabled){var o=document.createElement('option');o.value=id;o.textContent=name;o.disabled=!!disabled;sel.appendChild(o);}
      option('common',T('preset.common')+installed('common'));
      lastPresets.forEach(function(p){option(p.id,p.name+installed(p.id));});
      if(!validPreset(cur))option(cur,T('detail.presetUnavailable'),true);
      option('__new',T('detail.newPreset'));
      sel.value=cur;
    }
    function setDetailReady(ready){
      var add=$('dAddPreset'),tryOn=$('dTryOn'),remove=$('dRemove');
      if(remove)remove.disabled=!ready||!detailInstanceId||installInFlight;
      if(add)add.disabled=!ready||installInFlight;
      if(tryOn)tryOn.disabled=!viewFeatures.dressingRoom||!ready||!operations.enabled()||installInFlight;
    }
    function loadPresetSelect(sel){
      var token=++presetLoadToken,desired=sel.value&&sel.value!=='__new'?sel.value:instance?instance.target||'common':effectivePreset();
      presetsReady=false;settingsReady=false;groupLoadToken++;sel.disabled=true;setDetailReady(false);
      if(!sel.options.length){var option=document.createElement('option');option.value=desired;option.textContent=desired==='common'?T('preset.common'):presetNameOf(desired)||T('detail.selectedPreset');sel.appendChild(option);sel.value=desired;}
      $('dPresetFeedback').hidden=false;R.text($('dPresetStatus'),T('detail.loadingPresets'));$('dPresetRetry').hidden=true;
      refreshPresets().then(function(list){
        if(!sel.isConnected||token!==presetLoadToken)return;
        fillPresetSelect(sel,list,desired);presetsReady=true;sel.disabled=false;
        paintPresetStatus();
      }).catch(function(error){
        if(!sel.isConnected||token!==presetLoadToken)return;
        presetsReady=false;settingsReady=false;setDetailReady(false);$('dPresetFeedback').hidden=false;
        R.text($('dPresetStatus'),T('detail.presetLoadFailed')+' '+error.message);
        $('dPresetRetry').hidden=false;$('dPresetRetry').onclick=function(){loadPresetSelect(sel);};
      });
    }
    function createPresetFlow(sel){
      if(installInFlight)return;
      var name=prompt(T("detail.presetName"));
      if(name==null){ if(sel.value==="__new") loadPresetSelect(sel); else paintPresetStatus(); return; }
      var token=++presetLoadToken,button=$('dNewPreset');
      presetsReady=false;settingsReady=false;groupLoadToken++;sel.disabled=true;setDetailReady(false);
      installInFlight=true;if(button)button.disabled=true;
      api("/api/preset_save?name="+encodeURIComponent(name)).then(function(r){
        if(!r||!r.ok)throw new Error(r&&r.message||T("detail.install.fail"));
        toast(r.message,"ok");
        return presetCreated(r).then(function(){
          if(!sel.isConnected||token!==presetLoadToken)return;
          // Preserve the newly created destination even if its follow-up read fails.
          sel.innerHTML='';var option=document.createElement('option');option.value=r.id;option.textContent=name;sel.appendChild(option);sel.value=r.id;
        });
      }).catch(function(error){toast(error.message,"err");}).finally(function(){
        installInFlight=false;
        if(!sel.isConnected||token!==presetLoadToken)return;
        if(button)button.disabled=false;loadPresetSelect(sel);
      });
    }
    function deletePresetFlow(sel){
      var pid=sel.value;
      if(!pid||pid==="__new"||pid==="common") return;
      if(!confirm(T("detail.confirm.deletePreset"))) return;
      api("/api/preset_delete?id="+encodeURIComponent(pid)).then(function(r){
        if(r&&r.ok){ toast(r.message,"ok"); loadPresetSelect(sel); refreshState(); }
        else toast((r&&r.message)||T("detail.install.fail"),"err");
      }).catch(function(){ toast(T("detail.install.fail"),"err"); });
    }
    function presetInstall(sel,common){
      if(!presetsReady||!settingsReady||!sel||sel.disabled||($('dAddPreset')&&$('dAddPreset').disabled)||!validPreset(effectivePreset(common?'common':sel.value))){toast(T('detail.verifyPreset'),'err');return;}
      if(operations.enabled()){
        var target=effectivePreset(common?'common':sel.value);
        if(target==='__new'){createPresetFlow(sel);return;}
        var mode=$('dWearMode').value,replacement=$('dReplaceCopy').value,group=$('dGroup');
        var input={variantId:v.guid,assetVersion:v.assetVersion,scopeId:target,createToggles:!!($('dCreateToggles')&&$('dCreateToggles').checked),menuGroup:group&&group.value!=='__mixed'?group.value:'',addCopy:mode==='copy'};
        if(mode==='replace')input.instanceId=replacement;
        queueOutfit(mode==='replace'?'replace-outfit':'wear-outfit',input,d.name+' · '+v.variant);return;
      }
      if(installInFlight) return;
      var target=effectivePreset(common?"common":sel.value);
      common=target==="common";
      var groupSelect=document.getElementById("dGroup"),groupId=groupSelect&&groupSelect.value!=="__mixed"?groupSelect.value:"";
      if(!common&&(!target||target==="__new")){ createPresetFlow(sel); return; }
            installInFlight=true;
      var btn=document.getElementById("dAddPreset");
      var st=btn?beginButtonBusy(btn,avatarMode?(common?T("detail.addCommon"):T("detail.addPreset")):T("ui.adding.outfit")):null;
      var allow="0";
      var togglesBox=document.getElementById("dCreateToggles");
      var toggles=!togglesBox||togglesBox.checked?"1":"0";
      var mode=document.getElementById('dWearMode').value,replacement=document.getElementById('dReplaceCopy').value;
      api("/api/install?guid="+encodeURIComponent(v.guid)+"&allow="+allow+"&toggles="+toggles+"&switch="+(mode==='replace'?'1':'0')+"&copy="+(mode==='copy'?'1':'0')+"&replaceId="+encodeURIComponent(replacement)+"&target="+encodeURIComponent(target)+"&group="+encodeURIComponent(groupId)).then(function(r){
        installInFlight=false;
        if(r&&r.ok){
          toast(r.message,"ok");
          v.assigned=target;
          v.assignedName=common?"":presetNameOf(target);
          markVariantInstalled(d.id,v.guid,true,false);
          dropCaches();
          load(false,true);
          if(btn) endButtonBusy(btn,st);
          refreshState();
          loadInstalled().then(function(){return api('/api/family?id='+encodeURIComponent(d.id)+'&target='+encodeURIComponent(target));}).then(function(updated){if(document.getElementById('dWearMode'))renderDetail(updated,v.guid);});
        }else{
          if(btn) endButtonBusy(btn,st);
          toast((r&&r.message)||T("detail.install.fail"),"err");
        }
      }).catch(function(){
        installInFlight=false;
        if(btn) endButtonBusy(btn,st);
        toast(T("detail.install.fail"),"err");
      });
    }
    function paintPresetStatus() {
      var preset=document.getElementById('dPreset'),wrap=document.getElementById('dGroupWrap'),select=document.getElementById('dGroup');
      if(!preset||!wrap)return;
      settingsReady=false;setDetailReady(false);
      if(!presetsReady||!validPreset(preset.value)){$('dPresetFeedback').hidden=false;R.text($('dPresetStatus'),T('detail.presetUnavailable'));return;}
      R.text($('dPresetStatus'),'');$('dPresetRetry').hidden=true;$('dPresetFeedback').hidden=true;
      wrap.hidden=!preset.value||preset.value==='__new';
      if(wrap.hidden){select.value='';return;}
      var id=instance&&instance.guid===v.guid?instance.target||'common':effectivePreset(preset.value);
      var remove=document.getElementById('dRemove'),present=installedPresets.some(function(p){return p.id===id;});
      if(remove){remove.hidden=!present;remove.textContent=avatarMode?T("detail.removeFrom",id==='common'?T('preset.common'):presetNameOf(id)):T("detail.removeOutfit");}
      var copy=document.getElementById('dInstance');
      if(!copy){
        var copyWrap=document.createElement('div');copyWrap.innerHTML='<label for="dInstance">'+esc(T('detail.wornCopy'))+'</label><select id="dInstance"></select><p id="dSetupWarning" role="status"></p>';
        document.getElementById('dPresetWrap').appendChild(copyWrap);copy=document.getElementById('dInstance');
      }
      var copies=installedPresets.find(function(p){return p.id===id;}),oldId=detailInstanceId||Number(copy.value)||0;
      // Older hosts can report paths without exact scene instance IDs. Keep
      // removal disabled until identities arrive instead of breaking the panel.
      var paths=copies&&Array.isArray(copies.paths)?copies.paths:[],ids=copies&&Array.isArray(copies.instanceIds)?copies.instanceIds:[];
      var choices=paths.length===ids.length?paths.map(function(path,index){return {path:path,id:Number(ids[index])};}).filter(function(item){return Number.isInteger(item.id)&&item.id!==0;}):[];
      copy.innerHTML='<option value="">'+esc(T('detail.chooseCopy'))+'</option>'+choices.map(function(item){return '<option value="'+esc(item.id)+'">'+esc(item.path)+'</option>';}).join('');
      if(choices.some(function(item){return item.id===oldId;}))copy.value=String(oldId);
      else if(choices.length===1)copy.value=String(choices[0].id);
      copy.parentElement.hidden=!present;
      function chooseCopy(){
        detailInstanceId=Number(copy.value)||0;
        if(remove)remove.disabled=!detailInstanceId;
        var worn=installedItems.find(function(item){return item.instanceId===detailInstanceId;});
        R.text(document.getElementById('dSetupWarning'),worn&&worn.setupWarning?T('setup.attention')+' — '+worn.setupWarning:'');
      }
      copy.onchange=chooseCopy;chooseCopy();
      var installedList=document.getElementById('dInstalledPresets');
      if(!installedList){installedList=document.createElement('div');installedList.id='dInstalledPresets';installedList.className='subtle';document.getElementById('dPresetWrap').appendChild(installedList);}
      installedList.hidden=!avatarMode;
      installedList.textContent=installedPresets.length?T("detail.installedIn",installedPresets.map(function(p){return p.name;}).join(', ')):T("ui.not.installed.in.any.preset");
      var scope=id+'|'+v.guid,token=++groupLoadToken,guid=v.guid;
      var partBox=$('dCreateToggles'),membership=installedPresets.find(function(p){return p.id===id;});
      var draftKey=JSON.stringify([installedIdentity||contextKey,id,guid]),partKey='wardrobePartToggles|'+[(lastState||{}).avatarGuid||'',(lastState||{}).avatarName||'',scope].join('|');
      var savedGroup='',savedToggles=membership?!!membership.partToggles:R.stored(partKey,'0')==='1',savedMixed=!!(membership&&membership.partTogglesMixed);
      var apply=$('dApplySettings'),cancel=$('dCancelSettings'),status=$('dSettingsStatus');
      R.text($('dSaveSemantics'),T(membership?'detail.editSemantics':'detail.newSemantics'));
      function current(){return select.isConnected&&preset.value===id&&token===groupLoadToken;}
      function pending(){return select.value!==savedGroup||partBox.checked!==savedToggles||partBox.indeterminate!==savedMixed;}
      function paintPending(){
        if(!current())return;
        var dirty=pending();
        if(dirty)cacheSet(detailSettingDrafts,draftKey,{identity:installedIdentity||contextKey,target:id,guid:guid,familyId:d.id,worn:!!membership,group:select.value,toggles:partBox.checked,mixed:partBox.indeterminate},64);else detailSettingDrafts.delete(draftKey);
        apply.hidden=!membership||!dirty;cancel.hidden=!dirty;
        $('dAddPreset').hidden=!!membership&&dirty;$('dTryOn').hidden=!viewFeatures.dressingRoom||!!membership&&dirty;
        var remove=$('dRemove');if(remove)remove.hidden=!membership||dirty;
        apply.disabled=!settingsReady||installInFlight;cancel.disabled=installInFlight;
        R.text(status,dirty?T(membership?'detail.settingsPending':'detail.settingsForWear'):'');
        setDetailReady(settingsReady&&(!dirty||!membership));
      }
      function controls(busy){select.disabled=partBox.disabled=preset.disabled=busy;apply.disabled=cancel.disabled=busy;setDetailReady(!busy&&settingsReady&&(!pending()||!membership));}
      partBox.checked=savedToggles;partBox.indeterminate=savedMixed;partBox.disabled=true;select.disabled=true;
      select.onchange=partBox.onchange=paintPending;
      cancel.onclick=function(){detailSettingDrafts.delete(draftKey);select.value=savedGroup;partBox.checked=savedToggles;partBox.indeterminate=savedMixed;paintPending();};
      apply.onclick=async function(){
        if(!current()||!settingsReady||!membership||installInFlight||!pending())return;
        var group=select.value,toggles=partBox.checked,changeGroup=group!==savedGroup,changeToggles=toggles!==savedToggles||partBox.indeterminate!==savedMixed;
        installInFlight=true;controls(true);R.text(status,T('detail.settingsApplying'));
        try{
          var url='/api/item_settings?guid='+encodeURIComponent(guid)+'&target='+encodeURIComponent(id)+(changeGroup?'&group='+encodeURIComponent(group):'')+(changeToggles?'&toggles='+(toggles?'1':'0'):'');
          var result=await api(url,{method:'POST'});
          if(!result||!result.ok)throw new Error(result&&result.message||T('detail.settingsFailed'));
          detailSettingDrafts.delete(draftKey);R.store(partKey,toggles?'1':'0');
          if(changeGroup)savedGroup=group;
          if(changeToggles){savedToggles=toggles;savedMixed=false;partBox.indeterminate=false;membership.partToggles=toggles?1:0;membership.partTogglesMixed=0;}
          dropCaches();refreshState();loadInstalled();
          if(current()){paintPending();R.text(status,T('detail.settingsSaved'));}
        }catch(error){if(current())R.text(status,error.message);}
        finally{installInFlight=false;if(current()){controls(false);apply.disabled=false;cancel.disabled=false;}}
      };
      api('/api/menu_groups?id='+encodeURIComponent(id)).then(function(r){
        if(!current())return;
        if(!r||!r.ok||!Array.isArray(r.groups))throw new Error(r&&r.message||T('detail.groupsLoadFailed'));
        var groups=r.groups,paths=membership?membership.paths||[]:[],assigned=paths.map(function(path){var group=groups.find(function(g){return (g.paths||[]).indexOf(path)>=0;});return group?group.id:'';});
        var mixed=assigned.some(function(value){return value!==assigned[0];});savedGroup=mixed?'__mixed':assigned[0]||'';
        select.innerHTML='<option value="">'+esc(T('detail.noMenuGroup'))+'</option>'+groups.map(function(g){return '<option value="'+esc(g.id)+'">'+esc(g.name)+'</option>';}).join('')+(mixed?'<option value="__mixed" disabled>'+esc(T('detail.mixedGroups'))+'</option>':'');
        select.value=savedGroup;
        var draft=detailSettingDrafts.get(draftKey);
        if(draft){if(Array.from(select.options).some(function(o){return o.value===draft.group;}))select.value=draft.group;partBox.checked=draft.toggles;partBox.indeterminate=draft.mixed;}
        settingsReady=true;select.disabled=false;partBox.disabled=false;paintPending();
      }).catch(function(error){
        if(!current())return;
        settingsReady=false;setDetailReady(false);R.text($('dPresetStatus'),error.message);$('dPresetFeedback').hidden=false;
        $('dPresetRetry').hidden=false;$('dPresetRetry').onclick=paintPresetStatus;
      });
      $('dNewGroup').onclick=async function(){
        if(!settingsReady||installInFlight)return;
        var name=prompt(T('detail.menuGroupName'));if(name===null||!name.trim())return;
        try{
          var r=await api('/api/menu_groups?id='+encodeURIComponent(id)+'&op=save&name='+encodeURIComponent(name.trim()));
          if(!r||!r.ok)throw new Error(r&&r.message||T('detail.groupCreateFailed'));
          if(!current())return;select.add(new Option(name.trim(),r.id));select.value=r.id;paintPending();
        }catch(error){if(current())R.text(status,error.message);}
      };
    }
    function wireActions(){
      var ins=document.getElementById("dInstall")||document.getElementById("dSwitch");
      var switching=!!document.getElementById("dSwitch");
      if(ins) ins.onclick=function(){
        if(installInFlight) return;
        installInFlight=true;
        ins.disabled=true;
        ins.setAttribute("aria-busy","true");
        ins.innerHTML=spinner(16)+"<span>"+esc(T(switching?"detail.switch.busy":"detail.install.busy"))+"</span>";

        var createTogglesBox=document.getElementById("dCreateToggles");
        if(createTogglesBox) createTogglesBox.disabled=true;
        Array.prototype.forEach.call(document.querySelectorAll("#dFilm .film"),function(control){ control.disabled=true; });
        var allow="0";
        var createToggles=!createTogglesBox||createTogglesBox.checked?"1":"0";
        api("/api/install?guid="+encodeURIComponent(v.guid)+"&allow="+allow+"&toggles="+createToggles+"&switch="+(switching?"1":"0")).then(function(r){
          if(r&&r.ok){ installInFlight=false; toast(r.message,"ok"); markVariantInstalled(d.id,v.guid,true,switching); closeDetail(); if(filter==="installed"){ dropCaches(); load(false,true); } refreshState(); }
          else restoreInstall(r&&r.message);
        }).catch(function(){ restoreInstall(); });
        function restoreInstall(message){
          installInFlight=false;
          toast(message||T(switching?"detail.switch.fail":"detail.install.fail"),"err");
          if(!ins.isConnected) return;
          ins.disabled=false;
          ins.removeAttribute("aria-busy");
          ins.textContent=T(switching?"detail.switch":"detail.install");
          if(allowBox) allowBox.disabled=false;
          if(createTogglesBox) createTogglesBox.disabled=false;
          Array.prototype.forEach.call(document.querySelectorAll("#dFilm .film"),function(control){ control.disabled=false; });
        }
      };
      var presetSel=document.getElementById("dPreset");
      if(presetSel){
        loadPresetSelect(presetSel);
        presetSel.onchange=function(){
          if(presetSel.value==="__new"){ createPresetFlow(presetSel); return; }
          paintPresetStatus();
        };
        var newBtn=document.getElementById("dNewPreset");
        if(newBtn) newBtn.onclick=function(){ createPresetFlow(presetSel); };
        var delBtn=document.getElementById("dDelPreset");
        if(delBtn) delBtn.onclick=function(){ deletePresetFlow(presetSel); };
        var addP=document.getElementById("dAddPreset");
        if(addP) addP.onclick=function(){ presetInstall(presetSel,false); };
        var addC=document.getElementById("dAddCommon");
        if(addC) addC.onclick=function(){ presetInstall(presetSel,true); };
      }
      var ai=document.getElementById("dAi");
      if(ai) ai.onclick=function(){
        if(aiInFlight) return;
        aiInFlight=true;
        var aiState=beginButtonBusy(ai,T("detail.ai.busy"));
        api("/api/name?id="+encodeURIComponent(d.id)).then(function(r){
          if(!r||!r.ok||!r.job){ finishAi((r&&r.message)||T("detail.ai.fail")); return; }
          (function poll(){
            api("/api/nameResult?job="+encodeURIComponent(r.job)).then(function(q){
              if(q&&q.pending){ setTimeout(poll,1000); return; }
              if(q&&q.ok){ aiInFlight=false; toast(q.message,"ok"); dropCaches(); openDetail(d.id); load(); }
              else finishAi((q&&q.message)||T("detail.ai.fail"));
            }).catch(function(){ finishAi(T("detail.ai.fail")); });
          })();
        }).catch(function(){ finishAi(T("detail.ai.fail")); });
        function finishAi(message){
          aiInFlight=false;
          toast(message,"err");
          endButtonBusy(ai,aiState);
        }
      };
      var tryOn=$('dTryOn');
      if(tryOn&&viewFeatures.dressingRoom){tryOn.disabled=true;tryOn.title=T(operations.enabled()?'detail.tryOnHint':'detail.tryOnSetup');if(!operations.enabled()){var setup=document.createElement('p');setup.className='subtle';setup.textContent=T('detail.tryOnSetup');$('dPresetWrap').appendChild(setup);}tryOn.onclick=function(){
        if(!presetsReady||!settingsReady||!validPreset($('dPreset').value))return;
        var input={variantId:v.guid,assetVersion:v.assetVersion,scopeId:effectivePreset($('dPreset').value),label:d.name+' · '+v.variant,createToggles:!!($('dCreateToggles')&&$('dCreateToggles').checked)};
        if($('dWearMode').value==='replace')input.instanceId=$('dReplaceCopy').value;
        snapshots.begin(input);closeModal();
      };}
      var rem=document.getElementById("dRemove");
      if(rem) rem.onclick=function(){
        if(!presetsReady||!settingsReady||rem.disabled)return;
        var target=instance&&instance.guid===v.guid?instance.target||'common':effectivePreset(document.getElementById('dPreset').value);
        var name=avatarMode?(target==='common'?T('preset.common'):presetNameOf(target)):T('ui.avatar');
        var membership=installedPresets.find(function(p){return p.id===target;}),copy=document.getElementById('dInstance');
        var copyId=copy?Number(copy.value):0,index=membership?membership.instanceIds.indexOf(copyId):-1;
        if(index<0){toast(T('detail.chooseCopy'),'err');return;}
        var itemPath=membership.paths[index];
        if(!confirm(T('detail.confirmRemoveFrom',name))) return;
        if(operations.enabled()){queueOutfit('remove-outfit',{variantId:v.guid,assetVersion:v.assetVersion,scopeId:target,instanceId:String(copyId),itemPath:itemPath},d.name+' · '+v.variant);return;}
        if(removeInFlight) return;
        removeInFlight=true;
        var removeState=beginButtonBusy(rem,T("detail.remove.busy"));
        api("/api/preset_remove_item?guid="+encodeURIComponent(v.guid)+"&target="+encodeURIComponent(target)+"&item="+encodeURIComponent(itemPath)+"&instanceId="+encodeURIComponent(copyId)).then(function(r){
          if(r&&r.ok){ removeInFlight=false; toast(avatarMode?r.message:T("ui.outfit.removed"),"ok"); dropCaches();closeDetail();load();refreshState(); }
          else finishRemove((r&&r.message)||T("detail.remove.fail"));
        }).catch(function(){ finishRemove(T("detail.remove.fail")); });
        function finishRemove(message){
          removeInFlight=false;
          toast(message,"err");
          endButtonBusy(rem,removeState);
        }
      };

    }
    draw();
  }


  var operations=window.WardrobeOperations.create({api:api,onChange:paintOperations,onHydrated:function(){dropCaches();loadInstalled();load(false,true);refreshState();},onSettled:function(record){
    if(window.WardrobeOperations.isMutation(record.type)&&record.state==='succeeded'){
      if(snapshots)snapshots.mutationSettled(record);
      dropCaches();sideContent.dataset.signature='';loadInstalled();load(false,true);refreshState();
      if(inspectorMode==='selected'&&selectedFamilyId){var selectedCopy=record.command.payload.variantId===detailVariantGuid&&record.result&&record.result.addedInstanceId||detailInstanceId;openDetail(selectedFamilyId,detailVariantGuid,{instanceId:selectedCopy});}
    }
  }});
  var snapshots=viewFeatures.dressingRoom?window.WardrobeSnapshots.create({operations:operations,api:api,root:$('dressing'),scope:function(){return effectivePreset();}}):null;
  function queueOutfit(type,input,label){try{var command=operations.build(type,input);operations.submit(command,label).catch(function(error){toast(error.message,'err');});}catch(error){toast(error.message,'err');}}
  function dragContextKey(){var context=operations.context();if(!context)throw new Error(T("ui.wait.for.the.pinned.avatar.to.connect"));return window.WardrobeOperations.targetKey(Object.assign({},context,{scopeId:effectivePreset()}));}
  async function dropOutfit(payload,intent,instance){
    try{
      if(payload.targetKey!==dragContextKey())throw new Error(T("ui.the.avatar.or.preset.changed.during.the.drag.start"));
      if(!payload.variantId){openDetail(payload.familyId);toast(T('wear.chooseVariant'));return;}
      if(instance&&instance.familyId!==payload.familyId)throw new Error(T("ui.replace.accepts.a.variant.of.this.same.outfit.family"));
      var expected=dragContextKey(),detail=await api('/api/family?id='+encodeURIComponent(payload.familyId)+'&target='+encodeURIComponent(effectivePreset()));
      if(expected!==dragContextKey())throw new Error(T("ui.the.target.changed.while.resolving.the.outfit.drag.again"));
      var variant=detail.variants.find(function(v){return v.guid===payload.variantId;});if(!variant)throw new Error(T("ui.the.variant.is.no.longer.available"));
      var input={variantId:variant.guid,assetVersion:variant.assetVersion,scopeId:instance?instance.target||'common':effectivePreset(),addCopy:!instance,label:detail.name+' · '+variant.variant};
      if(instance)input.instanceId=String(instance.instanceId);
      if(intent==='try-on'){if(snapshots)snapshots.begin(input);}else queueOutfit(intent,input,input.label);
    }catch(error){toast(error.message,'err');}
  }
  function operationState(value){return lookup('operation.state.'+value)||value;}
  function operationType(value){return lookup('operation.type.'+value)||value;}
  function paintOperations(records,context){
    function reviewChange(){refreshState();operations.refresh();setBatchView('wardrobe');}
    function retryChange(row){operations.retry(row._record.id,effectivePreset()).then(function(){$('operationSummary').focus();}).catch(function(error){toast(error.message,'err');});}
    function dismissChange(row){operations.dismiss(row._record.id);var restore=row.querySelector('[data-op-restore]');if(restore&&!restore.hidden)restore.focus();else $('operationSummary').focus();}
    function retryTargetMatches(record){return context&&record.command&&record.command.target.scopeId===effectivePreset()&&window.WardrobeOperations.targetKey(record.command.target)===window.WardrobeOperations.targetKey(Object.assign({},context,{scopeId:effectivePreset()}));}
    $('sceneUnsaved').hidden=!(context&&context.unsaved&&lastState&&lastState.session===context.session&&lastState.avatarInstanceId===context.avatarInstanceId);
    var pending=records.filter(function(record){return window.WardrobeOperations.isMutation(record.type)&&!window.WardrobeOperations.isTerminal(record.state);});
    var lastApplied=records.filter(function(record){return window.WardrobeOperations.isMutation(record.type)&&record.state==='succeeded';}).slice(-1)[0];
    var summary=$('operationSummary');summary.hidden=!operations.enabled();R.text(summary,pending.length?T(pending.length===1?'operation.pending':'operation.pending.other',pending.length):T("ui.changes"));
    var recent=records.slice(-30).reverse();
    if(!recent.length)recent=[{id:'empty',label:T("ui.no.outfit.changes.queued"),state:''}];
    R.reconcile($('operationsList'),recent,function(record){return record.id;},function(){
      var row=document.createElement('li');row.innerHTML=("<strong></strong><p></p><button data-op-undo>"+esc(T("ui.undo.this.change"))+"</button><button data-op-cancel>"+esc(T("scene.cancel"))+"</button><button data-op-retry>"+esc(T("ui.check.same.request"))+"</button><button data-op-retry-failed>"+esc(T("ui.retry.change"))+"</button><button data-op-review>"+esc(T("ui.review.current.avatar"))+"</button><button data-op-dismiss>"+esc(T("ui.dismiss.notice"))+"</button><button data-op-restore>"+esc(T("ui.show.notice.in.wearing"))+"</button>");
      row.querySelector('[data-op-undo]').onclick=function(){var record=row._record;try{var command=window.WardrobeOperations.normalize('undo-operation',{undoToken:record.result.undoToken,scopeId:record.command.target.scopeId},Object.assign({},record.command.target,{revision:record.result.confirmedRevision}));operations.submit(command,T('operation.undo',record.label||operationType(record.type))).catch(function(error){toast(error.message,'err');});}catch(error){toast(error.message,'err');}};
      row.querySelector('[data-op-cancel]').onclick=function(){operations.cancel(row._record.id).catch(function(error){toast(error.message,'err');});};
      row.querySelector('[data-op-retry]').onclick=function(){operations.submit(row._record.command,row._record.label).catch(function(error){toast(error.message,'err');});};
      row.querySelector('[data-op-retry-failed]').onclick=function(){retryChange(row);};
      row.querySelector('[data-op-review]').onclick=reviewChange;
      row.querySelector('[data-op-dismiss]').onclick=function(){dismissChange(row);};
      row.querySelector('[data-op-restore]').onclick=function(){operations.restore(row._record.id);row.querySelector('[data-op-dismiss]').focus();};return row;
    },function(row,record){
      row._record=record;R.text(row.querySelector('strong'),record.label||operationType(record.type));
      R.text(row.querySelector('p'),operationState(record.state)+(record.dismissed?' · '+T('operation.noticeDismissed'):'')+(record.retryOperationId?' · '+T('operation.retryRequested'):'')+(record.cancelRequested?' · '+T('operation.cancelRequested'):'')+(record.waitingReason?' · '+record.waitingReason:'')+(record.error?' · '+record.error:''));
      row.querySelector('[data-op-undo]').hidden=record!==lastApplied||!record.result||!record.result.undoToken;
      row.querySelector('[data-op-undo]').disabled=!context||!record.command||context.session!==record.command.target.session||context.avatarInstanceId!==record.command.target.avatarInstanceId||context.revision!==record.result?.confirmedRevision||pending.length>0;
      row.querySelector('[data-op-cancel]').hidden=!['queued','running','submitting','acceptance-unknown'].includes(record.state);
      row.querySelector('[data-op-cancel]').disabled=!!record.cancelRequested;
      row.querySelector('[data-op-retry]').hidden=record.state!=='acceptance-unknown';
      row.querySelector('[data-op-retry-failed]').hidden=record.state!=='failed'||record.dismissed||!!record.retryOperationId||record.type==='undo-operation';
      row.querySelector('[data-op-retry-failed]').disabled=!operations.canRetry(record)||!retryTargetMatches(record)||pending.length>0;
      row.querySelector('[data-op-review]').hidden=!['failed','needs-review'].includes(record.state);
      row.querySelector('[data-op-dismiss]').hidden=!['failed','needs-review'].includes(record.state)||record.dismissed;
      row.querySelector('[data-op-restore]').hidden=!record.dismissed;
    });
    var visible=operations.notices(effectivePreset());
    R.reconcile($('pendingWearing'),visible,function(record){return record.id;},function(){
      var row=document.createElement('div');row.className='pending-wearing';row.style.overflowWrap='anywhere';row.innerHTML=("<strong></strong><small></small><div class=\"snapshot-controls\"><button data-op-retry-failed>"+esc(T("ui.retry.change"))+"</button><button data-op-review>"+esc(T("ui.review.current.avatar"))+"</button><button data-op-dismiss>"+esc(T("ui.dismiss.notice"))+"</button><button data-op-cancel>"+esc(T("ui.cancel.change"))+"</button></div>");
      row.querySelector('[data-op-retry-failed]').onclick=function(){retryChange(row);};row.querySelector('[data-op-review]').onclick=reviewChange;row.querySelector('[data-op-dismiss]').onclick=function(){dismissChange(row);};row.querySelector('[data-op-cancel]').onclick=function(){operations.cancel(row._record.id).catch(function(error){toast(error.message,'err');});};return row;
    },function(row,record){
      row._record=record;var attention=['failed','needs-review'].includes(record.state);
      R.text(row.querySelector('strong'),record.label||operationType(record.type));
      R.text(row.querySelector('small'),(record.state==='needs-review'?T("ui.needs.review.inspect.the.current.avatar.before.another.change"):record.state==='failed'?T("ui.change.failed"):(record.type==='remove-outfit'?T("ui.removing"):record.type==='undo-operation'?T("ui.undoing"):T("ui.adding"))+' · '+record.state)+(record.error?' · '+record.error:'')+(record.waitingReason?' · '+record.waitingReason:'')+(record.cancelRequested?' · cancellation requested':''));
      row.querySelector('[data-op-retry-failed]').hidden=record.state!=='failed'||!!record.retryOperationId||record.type==='undo-operation';row.querySelector('[data-op-retry-failed]').disabled=!operations.canRetry(record)||!retryTargetMatches(record)||pending.length>0;
      row.querySelector('[data-op-review]').hidden=!attention;row.querySelector('[data-op-dismiss]').hidden=!attention;
      row.querySelector('[data-op-cancel]').hidden=attention;row.querySelector('[data-op-cancel]').disabled=!!record.cancelRequested;
    });
    if(snapshots)snapshots.contextChanged();
    $('sceneUpload').disabled=pending.length>0||!connected;
  }
  $('operationSummary').onclick=function(){setBatchView('activity');};
  if(viewFeatures.dressingRoom)window.WardrobeDragDrop.target($('tryOnDrop'),{outfit:function(payload){dropOutfit(payload,'try-on');},error:function(message){toast(message,'err');}});
  window.WardrobeDragDrop.target($('side'),{outfit:function(payload){dropOutfit(payload,'wear-outfit');},error:function(message){toast(message,'err');}});
  if(viewFeatures.library)window.WardrobeDragDrop.target($('library'),{files:function(files){window.WardrobeLibrary.addFiles(files);},error:function(message){toast(message,'err');}});

  function unappliedItemEdits(){
    var identity=installedIdentity||contextKey;
    return Array.from(detailSettingDrafts.values()).filter(function(draft){
      return draft.worn&&draft.identity===identity&&installedItems.some(function(item){return item.guid===draft.guid&&item.target===draft.target;});
    });
  }
  function reviewUnappliedItemEdits(){
    var draft=unappliedItemEdits()[0];if(!draft)return;
    var worn=installedItems.find(function(item){return item.guid===draft.guid&&item.target===draft.target;});
    setBatchView('wardrobe');openDetail(draft.familyId,draft.guid,worn);
  }
  var uploadUI=new WardrobeUpload({api:api,T:T,toast:toast,esc:esc,spinner:spinner,onChange:refreshState,onPresetCreated:presetCreated,getUnappliedItemEdits:function(){return unappliedItemEdits().length;},reviewUnappliedItemEdits:reviewUnappliedItemEdits,hasPendingChanges:function(){return detailBusy()||modeSaving||baseSaving||R.pendingWrites()>0||operations.pending().length>0;}});
  function setBatchView(view){
    schedulePreviewDemand();
    if(Object.prototype.hasOwnProperty.call(viewFeatures,view)&&!viewFeatures[view]) return;
    if(detailBusy()) return;
    closeModal(); currentView=view;document.body.dataset.view=view;
    ["layout","upload","activity","settings","library","advancedScene","menuOrganizer","appearanceEditor"].forEach(function(id){$(id).hidden=(id==="layout"?"wardrobe":id)!==view;});
    $("wardrobeToolbar").hidden=view!=="wardrobe";
    document.querySelectorAll("#navtabs button").forEach(function(button){
      var selected=button.dataset.view===view;button.classList.toggle("on",selected);
      if(selected) button.setAttribute("aria-current","page");else button.removeAttribute("aria-current");
    });
    if(view==="library") window.WardrobeLibrary.show();
    if(view==="appearanceEditor")window.WardrobeAppearanceEditor.configure({api:api,T:T,root:$("appearanceEditor"),scope:function(){return effectivePreset();},onChange:refreshState}).show();
    if(view==="menuOrganizer")window.WardrobeMenuOrganizer.configure({api:api,T:T,root:$("menuOrganizer")}).show();
    if(view==="advancedScene"&&window.WardrobeSceneEditor)window.WardrobeSceneEditor.configure({api:api,T:T,root:$("advancedScene")}).show();
    if(view==="upload") uploadUI.show();
    if(view==="activity") paintActivity();
    
  }
  document.querySelectorAll("#navtabs button").forEach(function(button){button.onclick=function(){setBatchView(button.dataset.view);};});
  $("activityStatus").onclick=function(){setBatchView("activity");};
  function settingsToggle(id,key,apply,defaultValue){
    var input=$(id);input.checked=R.stored(key,defaultValue?"1":"0")==="1";apply(input.checked);
    input.onchange=function(){R.store(key,input.checked?"1":"0");apply(input.checked);};
  }
  settingsToggle("settingCompact","wardrobeCompact",function(on){document.body.classList.toggle("compact",on);});
  settingsToggle("settingAdvancedScene","wardrobeAdvancedScene",function(on){$("navAdvancedScene").hidden=!on;if(!on&&currentView==="advancedScene")setBatchView("wardrobe");});
  document.addEventListener("click",function(event){
    document.querySelectorAll("details.maintenance[open],details.filters[open]").forEach(function(menu){if(!menu.contains(event.target)) menu.open=false;});
  });
  document.addEventListener("keydown",function(event){
    var input=event.target.closest("input,textarea,select,[contenteditable=true]");
    if((event.ctrlKey||event.metaKey)&&event.key.toLowerCase()==="k"){
      event.preventDefault();setBatchView("wardrobe");$("search").focus();return;
    }
    if(event.key==="Escape"){
      if(!$("upModal").hidden){$("upModalClose").click();return;}
      if(inspectorMode==="selected"){closeDetail();return;}
      document.querySelectorAll("details[open]").forEach(function(node){node.open=false;});
    }
    if(input||detailBusy()) return;
    if(inspectorMode==="selected"&&(event.key==="ArrowLeft"||event.key==="ArrowRight")&&detailSelect&&detailCount>1){
      event.preventDefault();detailSelect((detailIndex+(event.key==="ArrowRight"?1:-1)+detailCount)%detailCount);
    }
  });
  $("emptyGridState").onclick=function(event){emptyGridAction(event.target.id);};
  var searchTimer=null;
  function submitSearch(){var value=$("search").value.trim();if(value===search) return;search=value;load();}
  $("search").addEventListener("input",function(event){clearTimeout(searchTimer);if(listController) listController.abort();listToken++;listInflight=false;if(!event.isComposing) searchTimer=setTimeout(submitSearch,180);});
  $("search").addEventListener("compositionend",function(){clearTimeout(searchTimer);submitSearch();});
  $("shop").onchange=function(){shop=this.value;load();};
  $("category").onchange=function(){category=this.value;load();};
  $("sort").value=sortMode;
  $("sort").onchange=function(){sortMode=this.value;R.store("wardrobeSort",sortMode);load();};
  function selectFilter(value){
    filter=value;
    document.querySelectorAll("#chips button").forEach(function(button){var active=button.dataset.f===value;button.classList.toggle("on",active);button.setAttribute("aria-pressed",String(active));});
    load();
  }
  $("chips").onclick=function(event){var button=event.target.closest("[data-f]");if(button) selectFilter(button.dataset.f);};
  function loadMore(){if(!listInflight&&currentView==="wardrobe"&&page<pageCount-1) load(true);}
  $("more").onclick=loadMore;
  var moreObserver=new IntersectionObserver(function(entries){if(entries.some(function(e){return e.isIntersecting;})) loadMore();},{root:$("main"),rootMargin:"300px"});
  moreObserver.observe($("more"));
  var scrollFrame=0;
  $("main").addEventListener("scroll",function(){
    if(scrollFrame) return;
    scrollFrame=requestAnimationFrame(function(){scrollFrame=0;preloadGrid();var main=$("main");if(main.scrollHeight-main.scrollTop-main.clientHeight<400) loadMore();});
  },{passive:true});
  window.addEventListener("resize",scheduleGridPreload,{passive:true});
  function startIndex(path,button,label,quiet){
    if(indexAction||indexing) return;
    var state=button?beginButtonBusy(button,label):null;if(button&&!state) return;
    indexAction={button:button,state:state,runningSeen:false,startedAt:Date.now()};paintEmptyGrid();
    api(path).then(function(result){
      if(!result||!result.ok) throw new Error((result&&result.message)||T("index.failed"));
      if(!quiet) toast(result.message,"ok");refreshState();
    }).catch(function(error){toast(error.message||T("index.failed"),"err");finishIndexAction();});
  }
  function finishIndexAction(){if(indexAction) endButtonBusy(indexAction.button,indexAction.state);indexAction=null;paintEmptyGrid();}
  ["indexInc","activityIndex"].forEach(function(id){$(id).onclick=function(){startIndex("/api/index",this,T("index.inc.busy"));};});
  function fullIndex(button){if(confirm(T("index.confirm"))) startIndex("/api/index?full=1",button,T("index.full.busy"));}
  $("avatarBase").onchange=async function(){
    baseSaving=true;this.disabled=true;
    try{
      var result=await api("/api/avatar_base?guid="+encodeURIComponent(this.value));
      if(!result||!result.ok)throw new Error((result&&result.message)||T("grid.error"));
      dropCaches();toast(T(lastState&&lastState.avatarSourceGuid?"avatar.base.saved":"avatar.base.sceneSaved"),"ok");
    }catch(error){toast(error.message,"err");}
    finally{baseSaving=false;refreshState();}
  };
  $("cacheClear").onclick=async function(){
    if(!confirm(T("cache.confirm")))return;
    this.disabled=true;
    try{
      var result=await api("/api/cache_clear",{timeout:120000});
      if(!result||!result.ok)throw new Error((result&&result.message)||T("grid.error"));
      location.reload();
    }catch(error){toast(error.message,"err");this.disabled=false;}
  };
  $("indexFull").onclick=function(){fullIndex(this);};$("settingsReindex").onclick=function(){fullIndex(this);};
  $('migrateAvatar').onclick=function(){
    api('/api/migrate_avatar').then(function(r){
      if(!r||!r.ok)throw new Error(r&&r.message||T("ui.migration.failed"));
      toast(T("ui.legacy.presets.migrated"),'ok');dropCaches();refreshState();
    }).catch(function(e){toast(e.message,'err');});
  };
  $('regenerateToggles').onclick=function(){
    var button=this;
    if(button.dataset.busy==='1')return;
    var state=beginButtonBusy(button,T("ui.regenerating"));
    api('/api/regenerate_toggles').then(function(result){
      if(!result||!result.ok)throw new Error(result&&result.message||T("ui.could.not.regenerate.toggles"));
      feedback('toggles',enabled?T("ui.toggles.generated.in.unity"):T("ui.toggles.removed.in.unity"),'saved');
            toast(result.message,'ok');refreshState();
    }).catch(function(error){toast(error.message,'err');}).finally(function(){endButtonBusy(button,state);});
  };
  function diagnostics(button){
    if(diagInFlight) return;diagInFlight=true;var state=beginButtonBusy(button,T("diag.busy"));
    api("/api/diag",{method:"GET"}).then(function(d){
      if(!d||d.ok===0) throw new Error(T("diag.failed"));
      toast(T("diag.summary",d.thumbsOnDisk||0,d.hiThumbsOnDisk||0,d.deadCount||0,d.samplePreview?T("diag.sample.ready"):T("diag.sample.missing")),"ok");
      setBatchView("activity");
    }).catch(function(error){toast(error.message||T("diag.failed"),"err");}).finally(function(){diagInFlight=false;endButtonBusy(button,state);});
  }
  $("diag").onclick=function(){diagnostics(this);};$("settingsDiagnostics").onclick=function(){diagnostics(this);};
  async function loadShops(){
    try {
      var d=await api("/api/shops",{method:"GET"});if(!d||!d.items) return;
      var select=$("shop");
      R.reconcile(select,[{name:"",count:0}].concat(d.items),function(x){return x.name;},function(){return document.createElement("option");},function(option,x){option.value=x.name;R.text(option,x.name?x.name+" ("+x.count+")":T("shop.all"));});
      select.value=shop;if(select.value!==shop){shop="";select.value="";}
    } catch(_){}
  }
  $("lang").onchange=function(){
    R.store("wardrobeLang",this.value);var selected=selectedFamilyId;
    loadLangs().then(function(){applyStrings();paintHide();paintAvatarMode();dropCaches();sideContent.dataset.signature="";loadShops();load(false,true);refreshState();uploadUI.localize();paintActivity();operations.refresh();if(currentView==='advancedScene')window.WardrobeSceneEditor.show();if(currentView==='appearanceEditor')window.WardrobeAppearanceEditor.show();if(currentView==='menuOrganizer')window.WardrobeMenuOrganizer.show();if(selected) openDetail(selected);});
  };
  function pingActive(on){
    if(on&&previewPageClosed) return Promise.resolve();
    // Hidden/minimized tabs may report empty bounds. Keep warming the last
    // visible grid instead of replacing its demand with an empty viewport.
    var previewDemand=on?(document.hidden?lastPreviewGrid:gridPreviewDemand()):null;
    if(on&&!document.hidden) lastPreviewGrid=previewDemand;
    var demand=previewDemand?previewDemand.guids:"";
    lastPreviewDemand=previewDemand?previewDemand.guids+"|"+previewDemand.visibleGuids:"";
    return R.request("/api/active?on="+(on?"1":"0")+(on?"&grid="+encodeURIComponent(demand)+"&visible="+encodeURIComponent(previewDemand?previewDemand.visibleGuids:""):""),{timeout:5000,keepalive:!on||document.hidden}).catch(function(){});
  }
  var tickInFlight=false,previewPageClosed=false;
  async function tick(){
    clearTimeout(pollTimer);
    if(tickInFlight||previewPageClosed) return;
    tickInFlight=true;
    try { await pingActive(true);if(!document.hidden&&!previewPageClosed) await refreshState(); }
    finally {tickInFlight=false;if(!previewPageClosed) pollTimer=setTimeout(tick,document.hidden?30000:5000);}
  }
  document.addEventListener("visibilitychange",function(){
    if(document.hidden) tick();
    else {renderGrid();previews.resume();tick();}
  });
  window.addEventListener("focus",function(){pingActive(true).then(function(){renderGrid();previews.resume();loadInstalled();});});
  window.addEventListener("blur",function(){pingActive(true);});
  window.addEventListener("pagehide",function(){previewPageClosed=true;pingActive(false);clearTimeout(pollTimer);if(listController) listController.abort();});
  window.addEventListener("pageshow",function(event){if(event.persisted){previewPageClosed=false;tick();}});
  loadLangs().then(function(){
    applyStrings();paintHide();paintAvatarMode();setBatchView("wardrobe");
    document.querySelectorAll("#chips button").forEach(function(b){b.setAttribute("aria-pressed",String(b.dataset.f===filter));});
    tick();
  });
  // Direct scene-avatar upload, independent of preset records and folders.
  var sceneUploadButton=$("sceneUpload"),sceneDialog=$("sceneUploadDialog"),sceneReview=null,sceneJob=null,scenePoll=null;
  function sceneMessage(text,tone){
    $("sceneUploadMessage").textContent=text||"";
    tone=tone||(sceneJob?"working":"ready");sceneDialog.dataset.tone=tone;
    var title={ready:T("ui.ready.to.review"),working:T("ui.working.on.your.avatar"),waiting:T("ui.action.needed"),error:T("ui.could.not.complete"),success:T("ui.complete")}[tone];
    $("sceneUploadStatusTitle").textContent=title;
    $("sceneUploadStatusIcon").textContent=tone==="success"?"✓":tone==="error"?"!":"●";
    var progress=$("sceneUploadProgress");progress.hidden=tone!=="working";
    $("sceneUploadStages").hidden=tone!=="working";
    var percent=(text||"").match(/(\d+(?:\.\d+)?)%/);
    if(percent)progress.value=Math.min(100,Math.max(0,Number(percent[1])));else progress.removeAttribute("value");
  }
  function sceneConsentState(){ $("sceneUploadConfirm").disabled=!sceneReview||!sceneReview.ok||!!sceneJob||$("sceneUploadConsent").disabled||!$("sceneUploadConsent").checked; }
  $("sceneUploadConsent").onchange=sceneConsentState;
  $("sceneUploadPreview").onload=function(){this.hidden=false;$("sceneUploadPreviewFallback").hidden=true;};
  $("sceneUploadPreview").onerror=function(){this.hidden=true;$("sceneUploadPreviewFallback").hidden=false;};
  function sceneBusy(busy){
    $("sceneBuildCheck").disabled=busy;$("sceneUploadConfirm").disabled=busy;
    $("sceneUploadName").disabled=busy;$("sceneUploadConsent").disabled=busy;$("sceneUploadCancel").hidden=!busy;
    $("sceneUploadCloseX").disabled=busy;
    $("sceneUploadClose").disabled=busy;sceneConsentState();
  }
  sceneUploadButton.onclick=async function(){
    sceneReview=null;$("sceneUploadPreview").hidden=true;$("sceneUploadPreviewFallback").hidden=false;
    $("sceneUploadConsent").checked=false;
    sceneDialog.showModal();sceneMessage(T("ui.checking.selected.avatar"));sceneBusy(true);
    try {
      sceneReview=await api("/api/scene_upload_review");
      $("sceneUploadTarget").textContent=sceneReview.name||T("ui.no.avatar.selected");
      $("sceneUploadBadge").textContent=sceneReview.isNew?T("ui.new.private.avatar"):T("ui.update.existing");
      if(sceneReview.avatarId)$("sceneUploadPreview").src="/api/scene_upload_thumbnail?avatarId="+sceneReview.avatarId;
      $("sceneUploadIdentity").textContent=sceneReview.isNew?T("ui.create.a.new.private.avatar"):T("ui.update.avatar")+(sceneReview.blueprintId||"");
      $("sceneUploadName").value=sceneReview.name||"";
      $("sceneUploadNameWrap").hidden=!sceneReview.isNew;
      sceneMessage(sceneReview.message||T("ui.includes.the.entire.selected.avatar.preserving.enabled.states.and"));
      sceneBusy(false);
      $("sceneBuildCheck").disabled=!sceneReview.ok;sceneConsentState();
      if(!sceneReview.ok)sceneMessage(sceneReview.message,"waiting");
    } catch(error){sceneMessage(error.message,"error");sceneBusy(false);$("sceneBuildCheck").disabled=$("sceneUploadConfirm").disabled=true;}
  };
  async function pollSceneUpload(){
    try {
      var result=await api("/api/upload_result?job="+encodeURIComponent(sceneJob));
      sceneMessage(result.message||(result.pending?T("ui.working"):T("ui.the.job.is.no.longer.available.check.unity.vrchat")),result.pending?"working":result.ok?"success":"error");
      if(result.pending){scenePoll=setTimeout(pollSceneUpload,1000);return;}
      sceneJob=null;sceneBusy(false);sceneReview=null;
      $("sceneBuildCheck").disabled=$("sceneUploadConfirm").disabled=true;
      refreshState();
    } catch(error){
      sceneMessage(T("ui.connection.lost.the.job.may.still.be.running.in")+error.message);
      scenePoll=setTimeout(pollSceneUpload,3000);
    }
  }
  async function startSceneUpload(check){
    if(!sceneReview||!sceneReview.ok||sceneJob)return;
    if(!check&&!$("sceneUploadConsent").checked){sceneMessage(T("ui.confirm.copyright.ownership.before.uploading"));return;}
    sceneBusy(true);sceneMessage(check?T("ui.starting.local.build.check"):T("ui.starting.upload.check.unity.for.sdk.prompts"),"working");
    try {
      var result=await api("/api/scene_upload?avatarId="+sceneReview.avatarId+"&blueprintId="+encodeURIComponent(sceneReview.blueprintId||"")+"&name="+encodeURIComponent($("sceneUploadName").value)+"&consent="+($("sceneUploadConsent").checked?1:0)+"&check="+(check?1:0));
      if(!result.ok)throw new Error(result.message||T("ui.could.not.start"));
      sceneJob=result.job;pollSceneUpload();
    }catch(error){sceneMessage(error.message,"error");sceneBusy(false);}
  }
  $("sceneBuildCheck").onclick=function(){startSceneUpload(true);};
  $("sceneUploadConfirm").onclick=function(){startSceneUpload(false);};
  $("sceneUploadCancel").onclick=async function(){
    if(!sceneJob)return;
    try{var result=await api("/api/scene_upload_cancel?job="+encodeURIComponent(sceneJob));sceneMessage(result.message);}catch(error){sceneMessage(error.message);}
  };
  $("sceneUploadClose").onclick=$("sceneUploadCloseX").onclick=function(){sceneDialog.close();};
  sceneDialog.addEventListener("cancel",function(event){if(sceneJob)event.preventDefault();});
})();
