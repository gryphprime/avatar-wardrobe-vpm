/* Avatar Wardrobe browser: stable catalog/installed views and explicit user actions. */
(function(){
  "use strict";
  var R=window.WardrobeRuntime, $=function(id){return document.getElementById(id);};
  var esc=R.escape;
  var page=0,pageSize=60,filter="compatible",search="",shop="",category="",total=0,pageCount=1,aiAvailable=0;
  var sortMode=R.stored("wardrobeSort","recent"),hideEmpty=R.stored("wardrobeHideEmpty","0")==="1"?1:0;
  var baseSaving=false;
  var avatarMode=0,modeSaving=false,selectedPreset="common",workflowRevision=0;
  var indexPhase="discovery";
  var indexing=0,indexDone=0,indexTotal=0,indexAction=null,diagInFlight=false,initialGridPending=true;
  var installInFlight=false,removeInFlight=false,uploadInFlight=false,aiInFlight=false;
  var inspectorMode="installed",selectedFamilyId="",installedItems=[],detailLoadToken=0,detailToken=0;
  var detailSelect=null,detailCount=0,detailIndex=0;
  var grid=$("grid"),status=$("status"),avatar=$("avatar"),pageinfo=$("pageinfo"),emptyGridState=$("emptyGridState");
  var side=$("side"),sideContent=$("sideContent"),modal=$("modal"),modalContent=$("modalContent"),modalTitle=$("modalTitle"),modalMode=$("modalMode");
  var sideBackdrop=$("sideBackdrop"),hideBtn=$("hideEmpty"),avatarModeChip=$("workflowMulti");
  var lastState=null,catalogEpoch="",contextKey="",previewContext="",connected=false,currentView="wardrobe";
  var listCache=new Map(),detailCache=new Map(),cardCache=new Map(),listItems=[];
  var listToken=0,listInflight=false,listController=null,gridNotice="",installedFlight=null,stateFlight=null;
  var previewActivity={active:0,queued:0},activityEntries=[],pollTimer=null;
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
    var label=$("navUpload").querySelector("span");label.removeAttribute("data-i18n");R.text(label,avatarMode?T("nav.presets"):"Organization");
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
    try{var result=await api('/api/workflow?selected='+encodeURIComponent(select.value));if(!result.ok)throw new Error(result.message);selectedPreset=select.value;sideContent.dataset.signature="";loadInstalled();dropCaches();load();}
    catch(error){select.value=previous;toast(error.message,'err');}
    finally{select.disabled=false;modeSaving=false;}
  };
  function paintWorkflowState(s){
    if(modeSaving)return;
    var previousPreset=selectedPreset;
    selectedPreset=s.selectedPreset||'common';
    var select=$("workflowPreset"),items=[{id:'common',name:T('preset.common')}].concat(s.workflowPresets||[]);
    R.reconcile(select,items,function(p){return p.id;},function(){return document.createElement('option');},function(option,p){option.value=p.id;R.text(option,p.name);});
    if(!items.some(function(p){return p.id===selectedPreset;}))selectedPreset='common';
    select.value=selectedPreset;applyAvatarMode(s.wardrobeMode==='multi-avatar');
    if(previousPreset!==selectedPreset&&avatarMode){dropCaches();load();}
  }
  function paintEmptyGrid(){
    var empty=listItems.length===0;
    emptyGridState.classList.toggle("on",empty);
    if(!empty) return;
    var mode=(initialGridPending||indexing||listInflight||indexAction)?"loading":gridNotice==="error"?"error":"empty";
    var message=mode==="loading"?T(indexing?(indexPhase==="discovery"?"index.discovery":indexPhase==="dependencies"?"index.dependencies":"banner.indexing"):"grid.loading",indexDone,indexTotal):T(mode==="error"?"grid.error":"grid.empty");
    if(message==="grid.loading")message="Loading wardrobe…";
    if(emptyGridState.dataset.mode!==mode){
      emptyGridState.dataset.mode=mode;
      emptyGridState.innerHTML=(mode==="loading"?spinner(36):'<svg class="icon empty-icon" aria-hidden="true"><use href="#icon-wardrobe"></use></svg>')+'<strong class="empty-grid-label"></strong>'+(mode==="error"?'<button type="button" id="gridRetry">'+esc(T("grid.retry"))+'</button>':"");
    }
    R.text(emptyGridState.querySelector(".empty-grid-label"),message);
  }

  var langCode="en", langTable={}, langList=[];
  function T(key){
    // Empty/loading states can render before the language request completes.
    var gridFallback={"grid.empty":"No items match your filters.","grid.loading":"Loading wardrobe…","grid.error":"Could not load items.","grid.retry":"Try again"};
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
    buildLangSelect();
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
  function preloadGrid(){
    if(document.hidden||!document.hasFocus()||currentView!=="wardrobe") return;
    var main=$("main"),bounds=main.getBoundingClientRect();
    var cards=Array.from(grid.children).filter(function(card){return !card.hidden;});
    var lastVisible=-1;
    cards.forEach(function(card,index){
      var rect=card.getBoundingClientRect();
      if(rect.bottom>bounds.top&&rect.top<bounds.bottom){
        lastVisible=index;
        previews.bind(card.querySelector(".thumb"),card._family.thumb,{priority:1,root:main});
      }
    });
    if(lastVisible<0) return;
    cards.slice(lastVisible+1,lastVisible+1+gridPreloadCount).forEach(function(card){
      previews.bind(card.querySelector(".thumb"),card._family.thumb,{priority:3,root:main});
    });
    // Fetch the next metadata page early enough to keep forty cards ahead of the viewport.
    if(cards.length-lastVisible-1<gridPreloadCount) loadMore();
  }
  function renderGrid(){
    R.reconcile(grid,listItems,function(f){return f.id;},function(f){
      var card=cardCache.get(f.id);
      if(!card){
        card=document.createElement("article"); card.className="card"; card.tabIndex=0; card.setAttribute("role","button");
        card.innerHTML='<div class="thumb preview-loading"></div><div class="card-info"></div>';
        card.onclick=function(event){ if(!event.target.closest("button")) openDetail(card._family.id); };
        card.onkeydown=function(event){ if(event.target===card&&(event.key==="Enter"||event.key===" ")){event.preventDefault();openDetail(card._family.id);} };
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
      if(!document.hidden&&document.hasFocus()) pingActive(true);
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
    groups.set("common",{id:"common",name:separate?T("preset.common"):"Installed items",items:[]});
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
      var list=$("instlist"),groups=groupInstalledItems(installedItems,(lastState||{}).workflowPresets||[],!!avatarMode);
      R.reconcile(list,Array.from(groups.values()),function(group){return group.id;},function(){
        var group=document.createElement("details");group.className="installed-preset";group.open=true;
        group.innerHTML='<summary><span></span><small></small></summary><div class="installed-preset-items"></div>';return group;
      },function(group,data){
        R.text(group.querySelector("summary span"),data.name);R.text(group.querySelector("summary small"),data.items.length);
        R.reconcile(group.querySelector(".installed-preset-items"),data.items,function(it){return it.guid+"|"+(it.path||"");},function(){
          var el=document.createElement("button");el.type="button";el.className="inst";
          el.innerHTML='<span class="inst-thumb"></span><span class="inst-copy"><span class="t"></span><span class="s"></span></span><span class="inst-arrow" aria-hidden="true">›</span>';
          el.onclick=function(){openDetail(el._item.familyId,el._item.guid);};return el;
        },function(el,it){
          el._item=it;var variant=it.variant&&it.variant.toLowerCase()!=="default"?it.variant:T("variant.default");
          R.text(el.querySelector(".t"),it.family);R.text(el.querySelector(".s"),variant);el.title=it.family+" · "+variant;
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
    var s=lastState||{},percent=s.hiTotal>0?Math.min(100,Math.round(100*s.hiBaked/s.hiTotal)):0;
    R.text($("hudLow"),T(!connected?"status.reconnecting":indexing?"banner.indexing":previewActivity.active?"status.rendering":"status.ready",indexDone,indexTotal));
    R.text($("indexedCount"),s.outfits?T("status.indexed",s.outfits):"");
    R.text($("pendingCount"),s.dirty?"· "+T(s.dirty===1?"status.pending":"status.pending.other",s.dirty):"");
    R.text($("hudHi"),s.hiTotal>0?T("hud.hi",s.hiBaked,s.hiTotal):T("activity.previews"));
    R.text($("hudPercent"),s.hiTotal>0?percent+"%":"—");
    $("hudHiBar").style.width=percent+"%"; $("hudHiBar").parentNode.setAttribute("aria-valuenow",String(percent));
    $("connectionDot").classList.toggle("offline",!connected);
    R.text($("connectionLabel"),T(connected?"status.connected":"status.disconnected"));
  }
  function paintActivity(){
    var s=lastState||{};
    R.text($("activityCatalog"),T("status.indexed",s.outfits||0));
    R.text($("activityIndexState"),indexing?T("banner.indexing",indexDone,indexTotal):s.dirty?T("banner.dirty",s.dirty):T("activity.current"));
    R.text($("activityPreviews"),s.hiTotal?T("hud.hi",s.hiBaked,s.hiTotal):"—");
    R.text($("activityConnection"),T(connected?"status.connected":"status.disconnected"));
    if(activityEntries.length) R.reconcile($("activityLog"),activityEntries,function(e){return e.id;},function(){var n=document.createElement("li");n.innerHTML='<time></time><span></span>';return n;},function(n,e){R.text(n.querySelector("time"),e.time);R.text(n.querySelector("span"),e.message);n.className=e.type;});
  }
  function refreshState(){
    if(stateFlight) return stateFlight;
    var expectedWorkflow=workflowRevision;
    stateFlight=api("/api/state",{method:"GET",timeout:30000}).then(function(s){
      if(!s||s.pending||s.ok===0) throw new Error("No state");
      connected=true; lastState=s; indexing=s.indexing?1:0;indexPhase=s.indexPhase||(s.total?"parsing":"discovery");indexDone=s.done||0;indexTotal=s.total||0;
      R.setContext(s.session, s.avatarInstanceId);
      window.WardrobeUpdates.refresh(s.wardrobeVersion, T);
      window.WardrobeReporting.setVersion(s.wardrobeVersion);
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
        installedFlight=null;installedItems=[];sideContent.dataset.signature="";$("instlist").innerHTML="";
        listToken++;if(listController)listController.abort();listInflight=false;
        dropCaches();listItems=[];cardCache.clear();renderGrid();
        selectedPreset="common";$("workflowPreset").innerHTML="";
        if(uploadUI)uploadUI.setContext(nextContext);
      }
      catalogEpoch=s.epoch||""; contextKey=nextContext;
      if(avatarChanged||expectedWorkflow===workflowRevision)paintWorkflowState(s);
      if(avatarChanged&&uploadUI&&currentView==="upload")uploadUI.show();
      var nextPreviews=[s.session||"",s.epoch||"",s.previewEpoch||0].join("|");
      var previewsChanged=nextPreviews!==previewContext;
      previewContext=nextPreviews;
      previews.reset(nextPreviews);
      if(previewsChanged) previews.resume();
      R.text(avatar,s.avatarLabel||s.avatarName||T("avatar.none"));
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
  function openDetail(id,selGuid){
    if(detailBusy()) return;
    inspectorMode="selected";selectedFamilyId=id;modal.classList.add("on");sideBackdrop.classList.add("on");
    modal.scrollTop=0;R.text(modalMode,T(filter==="unknown"?"side.review":"side.selected"));
    grid.querySelectorAll(".card").forEach(function(card){card.classList.toggle("selected",card.dataset.family===id);});
    var token=++detailLoadToken;
    if(detailCache.has(id)){renderDetail(detailCache.get(id),selGuid);R.openDialog(modal);return;}
    R.text(modalTitle,T("detail.loading"));modalContent.innerHTML='<div class="inspector-loading">'+spinner(24)+'<span>'+esc(T("detail.loading"))+'</span></div>';R.openDialog(modal);
    api("/api/family?id="+encodeURIComponent(id)+"&target="+encodeURIComponent(effectivePreset()),{method:"GET"}).then(function(d){
      if(token!==detailLoadToken) return;
      if(!d||!d.variants) throw new Error((d&&d.message)||T("err.notfound"));
      cacheSet(detailCache,id,d,96);renderDetail(d,selGuid);
    }).catch(function(error){if(token===detailLoadToken){toast(error.message||T("err.notfound"),"err");closeModal();}});
  }

  function renderDetail(d,selGuid){
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
      return '<details class="technical" open><summary>'+esc(T("detail.technical"))+'</summary><dl>'+rows.map(function(row){
        return '<dt>'+esc(row[0])+'</dt><dd>'+esc(row[1])+'</dd>';
      }).join("")+'</dl></details>';
    }
    function draw(){
      var multi=variants.length>1;
      modalTitle.textContent=d.name;
      modalContent.innerHTML=
        '<div class="detail-columns"><section class="detail-gallery" aria-label="'+esc(T("detail.variants"))+'">'+
        '<div id="dCreator" class="inspector-creator"></div>'+
        '<div class="imgwrap"><div class="imgspin" id="dSpin">'+spinner(30)+'</div><img class="big" id="dImg" style="display:none;position:relative"></div>'+
        '<div class="variant-head"><span>'+esc(multi?T("detail.variants"):T("variant.default"))+'</span><span id="dVariantCount"></span></div>'+
        (multi
          ? '<div class="variant-control"><button class="variant-nav" id="dPrevVar" aria-label="'+esc(T("nav.prev.variant"))+'">&#8249;</button><div class="filmstrip" id="dFilm"></div><button class="variant-nav" id="dNextVar" aria-label="'+esc(T("nav.next.variant"))+'">&#8250;</button></div>'
          : '<div class="single-variant-label" id="dSingleVariant"></div>')+
        '</section><section class="detail-options"><div id="dCompatibility"></div><div id="dPresetWrap"></div><div id="dVarBody"></div><div id="dAllowWrap"></div></section></div>'+
        '<footer class="detail-footer"><button id="dReportItem">Item was misclassified</button><button id="dClose">'+esc(T("detail.close"))+'</button><div class="actions" id="dActs"></div></footer>';
      document.getElementById("dClose").onclick=closeDetail;
      document.getElementById("dReportItem").onclick=function(){ window.WardrobeReporting.openItem(d,v); };
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
      i=n; v=variants[n]; detailIndex=n; detailSelect=selectVariant;
      var count=document.getElementById("dVariantCount");
      if(count) count.textContent=variants.length>1?T("detail.variant.count",n+1,variants.length):"";
      var single=document.getElementById("dSingleVariant");
      if(single) single.textContent=variantLabel(v,n);
      var film=document.getElementById("dFilm"), btns=film?film.children:[];
      for(var k=0;k<btns.length;k++){btns[k].classList.toggle("on",k===n);btns[k].setAttribute("aria-pressed",String(k===n));}
      if(film&&btns[n]) { var b=btns[n]; if(b.offsetLeft<film.scrollLeft||b.offsetLeft+b.offsetWidth>film.scrollLeft+film.clientWidth) film.scrollLeft=Math.max(0,b.offsetLeft-film.clientWidth/2+b.offsetWidth/2); }
      R.text(document.getElementById("dCreator"),[v.shop,v.product].filter(Boolean).join(" / "));
      document.getElementById("dCompatibility").innerHTML='<div class="badge c'+v.compat+'">'+esc(v.compatText)+'</div>'+
        (v.installed?'<div class="variant-installed">✓ '+esc(T("detail.installed"))+'</div>':"");
      document.getElementById("dVarBody").innerHTML=variantBody();
      var familyInstalled=d.variants.some(function(x){ return !!x.installed; });
      document.getElementById("dAllowWrap").innerHTML=
        '<details class="technical" open><summary>'+esc(T("detail.advancedOptions"))+'</summary><label class="allow"><input type="checkbox" id="dCreateToggles" disabled> Generate toggles</label><div class="subtle">Create independent toggles for each child object in the prefab.</div><div id="dToggleStatus" class="subtle" role="status" aria-live="polite"></div>'+
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
      document.getElementById("dPresetWrap").innerHTML='<label for="dPreset">'+esc(T("detail.preset.tag"))+'</label><div class="preset-row"><select id="dPreset"></select><button id="dNewPreset" title="'+esc(T("detail.newPreset"))+'">+</button></div>';
      document.getElementById('dPresetWrap').insertAdjacentHTML('beforeend','<div id="dGroupWrap" hidden><label for="dGroup">Menu Group</label><div class="preset-row"><select id="dGroup"><option value="">No menu group</option></select><button id="dNewGroup" title="New menu group">+</button></div><div id="dGroupStatus" class="subtle" role="status" aria-live="polite"></div></div>');
      var action='<button id="dRemove" class="danger" hidden>Remove</button>'+
        '<button id="dAddPreset" class="primary">'+esc(avatarMode?T("detail.addPreset"):"Add Outfit")+"</button>";
      document.getElementById("dActs").innerHTML=action+
        (aiAvailable?'<button id="dAi">'+esc(T("detail.ai"))+"</button>":"");
      wireActions();
      var dtok=++detailToken;
      loadDetailThumb(v.guid,dtok);
    }
    var lastPresets=[],installedPresets=[],groupLoadToken=0;
    function refreshPresets(){
      var guid=v.guid;
      return Promise.all([api("/api/presets"),api('/api/prefab_presets?guid='+encodeURIComponent(guid))]).then(function(results){
        var p=results[0];p.installedPresets=results[1].presets||[];return p;
      }).catch(function(){ return null; });
    }
    function presetNameOf(id){
      for(var k=0;k<lastPresets.length;k++) if(lastPresets[k]&&lastPresets[k].id===id) return lastPresets[k].name||"";
      return "";
    }
    function fillPresetSelect(sel,list){
      lastPresets=(list&&list.presets)||[];
      installedPresets=(list&&list.installedPresets)||[];
      function installed(id){return installedPresets.some(function(p){return p.id===id;})?" (installed)":"";}
      sel.innerHTML="";
      var common=document.createElement("option");common.value="common";common.textContent=T("preset.common")+installed("common");sel.appendChild(common);
      lastPresets.forEach(function(p){
        var o=document.createElement("option");
        o.value=p.id;
        o.textContent=p.name+installed(p.id);
        sel.appendChild(o);
      });
      var n=document.createElement("option");
      n.value="__new";
      n.textContent=T("detail.newPreset");
      sel.appendChild(n);
      var cur=effectivePreset();
      var has=false;
      for(var k=0;k<sel.options.length;k++) if(sel.options[k].value===cur){ has=true; break; }
      sel.value=has?cur:"common";
    }
    function loadPresetSelect(sel){
      sel.disabled=true;
      refreshPresets().then(function(list){
        if(!sel.isConnected) return;
        fillPresetSelect(sel,list);
        sel.disabled=false;
        paintPresetStatus();
      });
    }
    function createPresetFlow(sel){
      var name=prompt(T("detail.presetName"));
      if(name==null){ if(sel.value==="__new") loadPresetSelect(sel); else paintPresetStatus(); return; }
      api("/api/preset_save?name="+encodeURIComponent(name)).then(function(r){
        if(r&&r.ok){
          toast(r.message,"ok");
          return presetCreated(r).then(refreshPresets).then(function(list){
            if(!sel.isConnected) return;
            fillPresetSelect(sel,list);
            if(r.id) sel.value=r.id;
            paintPresetStatus();
          });
        }else toast((r&&r.message)||T("detail.install.fail"),"err");
      }).catch(function(){ toast(T("detail.install.fail"),"err"); });
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
      if(installInFlight) return;
      var target=effectivePreset(common?"common":sel.value);
      common=target==="common";
      var groupSelect=document.getElementById("dGroup"),groupId=groupSelect?groupSelect.value:"";
      if(!common&&(!target||target==="__new")){ createPresetFlow(sel); return; }
            installInFlight=true;
      var btn=document.getElementById("dAddPreset");
      var st=btn?beginButtonBusy(btn,avatarMode?(common?T("detail.addCommon"):T("detail.addPreset")):"Adding outfit…"):null;
      var allow="0";
      var togglesBox=document.getElementById("dCreateToggles");
      var toggles=!togglesBox||togglesBox.checked?"1":"0";
      api("/api/install?guid="+encodeURIComponent(v.guid)+"&allow="+allow+"&toggles="+toggles+"&switch=0&target="+encodeURIComponent(target)+"&group="+encodeURIComponent(groupId)).then(function(r){
        installInFlight=false;
        if(r&&r.ok){
          toast(r.message,"ok");
          v.assigned=target;
          v.assignedName=common?"":presetNameOf(target);
          markVariantInstalled(d.id,v.guid,true,false);
          dropCaches();
          load(false,true);
          refreshState();
          closeDetail();
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
      wrap.hidden=!preset.value||preset.value==='__new';
      if(wrap.hidden){select.value='';return;}
      var id=effectivePreset(preset.value);
      var remove=document.getElementById('dRemove'),present=installedPresets.some(function(p){return p.id===id;});
      if(remove){remove.hidden=!present;remove.textContent=avatarMode?'Remove from '+(id==='common'?T('preset.common'):presetNameOf(id)):'Remove Outfit';}
      var installedList=document.getElementById('dInstalledPresets');
      if(!installedList){installedList=document.createElement('div');installedList.id='dInstalledPresets';installedList.className='subtle';document.getElementById('dPresetWrap').appendChild(installedList);}
      installedList.hidden=!avatarMode;
      installedList.textContent=installedPresets.length?'Installed in: '+installedPresets.map(function(p){return p.name;}).join(', '):'Not installed in any preset.';
      var scope=id+'|'+v.guid,token=++groupLoadToken,add=document.getElementById('dAddPreset');
      function feedback(kind,message,state){
        var node=document.getElementById(kind==='group'?'dGroupStatus':'dToggleStatus');
        if(node){node.textContent=message;node.dataset.state=state||'';}
      }
      feedback('group','');feedback('toggles','');
      var partBox=document.getElementById('dCreateToggles'),membership=installedPresets.find(function(p){return p.id===id;}),partKey='wardrobePartToggles|'+[(lastState||{}).avatarGuid||'',(lastState||{}).avatarName||'',scope].join('|');
      if(partBox){
        partBox.checked=membership?!!membership.partToggles:R.stored(partKey,"0")==="1";
        partBox.indeterminate=!!(membership&&membership.partTogglesMixed);
        partBox.disabled=true;
        partBox.onchange=async function(){
          var enabled=partBox.checked,guid=v.guid;
          if(!membership){R.store(partKey,enabled?"1":"0");feedback('toggles','Will apply when you add this outfit.','pending');return;}
          var previous=!!membership.partToggles,mixed=!!membership.partTogglesMixed;
          feedback('toggles','Applying in Unity…','pending');
          installInFlight=true;partBox.disabled=true;select.disabled=true;preset.disabled=true;if(add)add.disabled=true;
          try{
            var result=await api('/api/part_toggles?guid='+encodeURIComponent(guid)+'&target='+encodeURIComponent(id)+'&enabled='+(enabled?'1':'0'));
            if(!result||!result.ok)throw new Error(result&&result.message||'Could not update part toggles.');
            R.store(partKey,enabled?"1":"0");membership.partToggles=enabled?1:0;membership.partTogglesMixed=0;partBox.indeterminate=false;
            feedback('toggles',enabled?'✓ Toggles generated in Unity.':'✓ Toggles removed in Unity.','saved');
            toast(result.message,'ok');refreshState();
          }catch(error){partBox.checked=previous;partBox.indeterminate=mixed;feedback('toggles',error.message,'error');toast(error.message,'err');}
          finally{installInFlight=false;if(partBox.isConnected){partBox.disabled=false;select.disabled=false;preset.disabled=false;if(add)add.disabled=false;}}
        };
      }
      if(select.dataset.scope!==scope){select.dataset.scope=scope;delete select.dataset.userChoice;select.value='';}
      select.onchange=async function(){
        select.dataset.userChoice='1';
        var membership=installedPresets.find(function(p){return p.id===id;});
        // New installs keep the choice until Add; existing copies save it immediately.
        if(!membership){feedback('group','Will apply when you add this outfit.','pending');return;}
        feedback('group','Applying in Unity…','pending');
        var group=select.value,guid=v.guid,previous=select.dataset.savedGroup||'';
        var paths=membership.paths&&membership.paths.length?membership.paths:[''];
        installInFlight=true;select.disabled=true;preset.disabled=true;if(partBox)partBox.disabled=true;if(add)add.disabled=true;
        try{
          for(var path of paths){
            var result=await api('/api/menu_groups?id='+encodeURIComponent(id)+'&op=assign&group='+encodeURIComponent(group)+'&guid='+encodeURIComponent(guid)+'&item='+encodeURIComponent(path));
            if(!result||!result.ok)throw new Error(result&&result.message||'Could not update the menu group.');
          }
          select.dataset.savedGroup=group;
          feedback('group',group?'✓ Saved in Unity: '+select.selectedOptions[0].textContent+'.':'✓ Removed from menu group in Unity.','saved');
          toast('Menu group updated and toggles regenerated.','ok');
          dropCaches();refreshState();
        }catch(error){
          select.value=previous;delete select.dataset.userChoice;
          feedback('group',error.message,'error');toast(error.message,'err');
        }finally{
          installInFlight=false;
          if(select.isConnected){select.disabled=false;preset.disabled=false;if(partBox)partBox.disabled=false;if(add)add.disabled=false;}
        }
      };
      select.disabled=true;if(add)add.disabled=true;
      api('/api/menu_groups?id='+encodeURIComponent(id)).then(function(r){
        if(!select.isConnected||preset.value!==id||token!==groupLoadToken)return;
        if(!r||!r.ok)throw new Error(r&&r.message||'Could not load menu groups.');
        var current=select.value,groups=r.groups||[],membership=installedPresets.find(function(p){return p.id===id;}),paths=membership?membership.paths||[]:[];
        var assigned=groups.find(function(g){return (g.paths||[]).some(function(path){return paths.indexOf(path)>=0;});});
        select.innerHTML='<option value="">No menu group</option>'+groups.map(function(g){return '<option value="'+esc(g.id)+'">'+esc(g.name)+'</option>';}).join('');
        select.value=select.dataset.userChoice&&Array.from(select.options).some(function(o){return o.value===current;})?current:assigned?assigned.id:'';
        select.dataset.savedGroup=assigned?assigned.id:'';
        select.disabled=false;if(partBox)partBox.disabled=false;if(add&&!installInFlight)add.disabled=false;
      }).catch(function(error){if(select.isConnected&&token===groupLoadToken)toast(error.message,'err');});
      document.getElementById('dNewGroup').onclick=function(){
        var name=prompt('Menu group name');if(name===null||!name.trim())return;
        api('/api/menu_groups?id='+encodeURIComponent(id)+'&op=save&name='+encodeURIComponent(name.trim())).then(function(r){
          if(!r||!r.ok){toast(r&&r.message||'Could not create menu group.','err');return;}
          select.add(new Option(name.trim(),r.id));select.value=r.id;select.onchange();
        });
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
      var rem=document.getElementById("dRemove");
      if(rem) rem.onclick=function(){
        var target=effectivePreset(document.getElementById('dPreset').value);
        var name=avatarMode?(target==='common'?T('preset.common'):presetNameOf(target)):'avatar';
        if(!installedPresets.some(function(p){return p.id===target;}))return;
        if(!confirm('Remove this prefab from '+name+'?')) return;
        if(removeInFlight) return;
        removeInFlight=true;
        var removeState=beginButtonBusy(rem,T("detail.remove.busy"));
        api("/api/preset_remove_item?guid="+encodeURIComponent(v.guid)+"&target="+encodeURIComponent(target)).then(function(r){
          if(r&&r.ok){ removeInFlight=false; toast(avatarMode?r.message:"Outfit removed.","ok"); dropCaches();closeDetail();load();refreshState(); }
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

  var uploadUI=new WardrobeUpload({api:api,T:T,toast:toast,esc:esc,spinner:spinner,onChange:refreshState,onPresetCreated:presetCreated});
  function setBatchView(view){
    if(detailBusy()) return;
    closeModal(); currentView=view;
    ["layout","upload","activity","settings"].forEach(function(id){$(id).hidden=(id==="layout"?"wardrobe":id)!==view;});
    $("wardrobeToolbar").hidden=view!=="wardrobe";
    document.querySelectorAll("#navtabs button").forEach(function(button){
      var selected=button.dataset.view===view;button.classList.toggle("on",selected);
      if(selected) button.setAttribute("aria-current","page");else button.removeAttribute("aria-current");
    });
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
  $("emptyGridState").onclick=function(event){if(event.target.id==="gridRetry") load();};
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
  $('regenerateToggles').onclick=function(){
    var button=this;
    if(button.dataset.busy==='1')return;
    var state=beginButtonBusy(button,'Regenerating…');
    api('/api/regenerate_toggles').then(function(result){
      if(!result||!result.ok)throw new Error(result&&result.message||'Could not regenerate toggles.');
      feedback('toggles',enabled?'✓ Toggles generated in Unity.':'✓ Toggles removed in Unity.','saved');
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
    loadLangs().then(function(){applyStrings();paintHide();paintAvatarMode();dropCaches();sideContent.dataset.signature="";loadShops();load(false,true);refreshState();uploadUI.localize();if(selected) openDetail(selected);});
  };
  function pingActive(on){
    return R.request("/api/active?on="+(on?"1":"0")+(on?"&grid="+encodeURIComponent(listItems.slice(0,120).map(function(item){return item.thumb;}).join(",")):""),{timeout:5000,keepalive:!on}).catch(function(){});
  }
  var tickInFlight=false;
  async function tick(){
    clearTimeout(pollTimer);
    if(tickInFlight) return;
    tickInFlight=true;
    try { if(!document.hidden){await pingActive(document.hasFocus());await refreshState();} }
    finally {tickInFlight=false;if(!document.hidden) pollTimer=setTimeout(tick,5000);}
  }
  document.addEventListener("visibilitychange",function(){
    if(document.hidden){clearTimeout(pollTimer);pingActive(false);}
    else {renderGrid();previews.resume();tick();}
  });
  window.addEventListener("focus",function(){pingActive(true).then(function(){renderGrid();previews.resume();loadInstalled();});});
  window.addEventListener("blur",function(){pingActive(false);});
  window.addEventListener("pagehide",function(){pingActive(false);clearTimeout(pollTimer);if(listController) listController.abort();});
  window.addEventListener("pageshow",function(event){if(event.persisted) tick();});
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
    var title={ready:"Ready to review",working:"Working on your avatar",waiting:"Action needed",error:"Could not complete",success:"Complete"}[tone];
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
    sceneDialog.showModal();sceneMessage("Checking selected avatar…");sceneBusy(true);
    try {
      sceneReview=await api("/api/scene_upload_review");
      $("sceneUploadTarget").textContent=sceneReview.name||"No avatar selected";
      $("sceneUploadBadge").textContent=sceneReview.isNew?"New private avatar":"Update existing";
      if(sceneReview.avatarId)$("sceneUploadPreview").src="/api/scene_upload_thumbnail?avatarId="+sceneReview.avatarId;
      $("sceneUploadIdentity").textContent=sceneReview.isNew?"Create a new private avatar":"Update avatar: "+(sceneReview.blueprintId||"");
      $("sceneUploadName").value=sceneReview.name||"";
      $("sceneUploadNameWrap").hidden=!sceneReview.isNew;
      sceneMessage(sceneReview.message||"Includes the entire selected avatar, preserving enabled states and existing controls. PC only. Your existing thumbnail is kept. If none exists, AW generates one automatically.");
      sceneBusy(false);
      $("sceneBuildCheck").disabled=!sceneReview.ok;sceneConsentState();
      if(!sceneReview.ok)sceneMessage(sceneReview.message,"waiting");
    } catch(error){sceneMessage(error.message,"error");sceneBusy(false);$("sceneBuildCheck").disabled=$("sceneUploadConfirm").disabled=true;}
  };
  async function pollSceneUpload(){
    try {
      var result=await api("/api/upload_result?job="+encodeURIComponent(sceneJob));
      sceneMessage(result.message||(result.pending?"Working…":"The job is no longer available. Check Unity/VRChat before retrying."),result.pending?"working":result.ok?"success":"error");
      if(result.pending){scenePoll=setTimeout(pollSceneUpload,1000);return;}
      sceneJob=null;sceneBusy(false);sceneReview=null;
      $("sceneBuildCheck").disabled=$("sceneUploadConfirm").disabled=true;
      refreshState();
    } catch(error){
      sceneMessage("Connection lost. The job may still be running in Unity. Do not start another upload. "+error.message);
      scenePoll=setTimeout(pollSceneUpload,3000);
    }
  }
  async function startSceneUpload(check){
    if(!sceneReview||!sceneReview.ok||sceneJob)return;
    if(!check&&!$("sceneUploadConsent").checked){sceneMessage("Confirm copyright ownership before uploading.");return;}
    sceneBusy(true);sceneMessage(check?"Starting local build check…":"Starting upload; check Unity for SDK prompts…","working");
    try {
      var result=await api("/api/scene_upload?avatarId="+sceneReview.avatarId+"&blueprintId="+encodeURIComponent(sceneReview.blueprintId||"")+"&name="+encodeURIComponent($("sceneUploadName").value)+"&consent="+($("sceneUploadConsent").checked?1:0)+"&check="+(check?1:0));
      if(!result.ok)throw new Error(result.message||"Could not start.");
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
