/* Avatar Wardrobe V2. Same Unity API as the classic page; a new shell built around Looks:
   rail (avatar, Looks, tools, status) · catalog · Wearing panel. Item details replace the
   Wearing panel, Fit is a focused full-screen view, upload is one list dialog for 1..n Looks. */
(function(){
  "use strict";
  var R=window.WardrobeRuntime,esc=R.escape,$=function(id){return document.getElementById(id);};
  var TAPES=["pink","yellow","green","blue","orange","violet"];

  /* ---------- Copy ---------- */
  var STR={
    en:{thumbnail:"Thumbnail",thDefault:"Default",thDefaultTitle:"Default thumbnail",thCamera:"Camera",thImage:"Image",thOnUpload:"On upload",thReplace:"Replace",thKeep:"Keep",
      thNewCamera:"New camera",thFromView:"From view",thChoose:"Choose image",thRender:"Render",thFreeze:"Use as image",thZoom:"Zoom",thEditDefault:"Edit default",
      thFollows:"{0} · {1}",thImageType:"Use an image file",looks:"Looks",newLook:"New look",allLooks:"All looks",wardrobe:"Wardrobe",tools:"Tools",menuLayout:"Menu layout",avatarEditor:"Avatar editor",
      connecting:"Connecting…",settings:"Settings",search:"Search",compatible:"Compatible",all:"All",filter:"Filter",show:"Show",showDefault:"Catalog",
      showWorn:"Worn",showReview:"Review",showHidden:"Hidden",creator:"Creator",tag:"Tag",sort:"Sort",sortNewest:"Newest",sortUsed:"Recently used",
      sortName:"Name",hideNoPreview:"Hide no-preview",editTags:"Edit tags",reset:"Reset",typeAll:"All",typeOutfits:"Outfits",typeHair:"Hair",
      typeGimmicks:"Gimmicks",selectAll:"Select all",clear:"Clear",objects:"Objects",materials:"Materials",sharedItems:"Shared items",allItems:"All items",upload:"Upload",
      synced:"Synced",offline:"Unity offline",unityBusy:"Unity busy",indexing:"Indexing {0} / {1}",pending:"{0} pending",failedN:"{0} failed",
      wearing:"Wearing",nothingWorn:"Nothing worn",allCreators:"All creators",loading:"Loading…",noMatch:"No matches",clearFilters:"Clear filters",
      chooseAvatar:"Choose an avatar",noItems:"No items yet. Import outfits in Unity.",retry:"Retry",loadError:"Couldn't load items",
      wear:"Wear",wearOptions:"More wear options",replaceV:"Replace {0}",addCopy:"Add copy",fit:"Fit",remove:"Remove",removeConfirm:"Remove {0} from {1}?",
      menuGroup:"Menu group",none:"None",newGroup:"New group",groupName:"Group name",partToggles:"Part toggles",blendshapeSync:"Blendshape sync",
      more:"More",markCompatible:"Mark compatible",aiName:"Clean up name",report:"Report misclassification",look:"Look",apply:"Apply",cancel:"Cancel",
      discard:"Discard",unsaved:"Unsaved",applying:"Applying…",saved:"Saved",source:"Source",setup:"Setup",confidence:"Confidence",mats:"Materials",
      parts:"Parts",yes:"Yes",no:"No",shapeChanges:"Shape changes",blendshape:"Blendshape",mode:"Mode",value:"Value",whileW:"While",modeSet:"Set",
      modeDelete:"Delete",modeRadial:"Radial",whileWorn:"Worn",radialName:"Menu name",mesh:"Mesh",add:"Add",radials:"{0} radials · {1} bits",radial1:"1 radial · {1} bits",
      noMesh:"No blendshape meshes",copies:"Applies to {0} copies",discardChanges:"Discard unsaved changes?",lookName:"Look name",rename:"Rename",
      del:"Delete",deleteLook:"Delete {0}? Items stay in the project.",notUploaded:"Not uploaded",selectedN:"{0} selected",uploadN:"Upload {0}",
      uploadLook:"Upload look",uploadLooks:"Upload {0} looks",newU:"New",updateU:"Update",noPlatform:"No platform",invalidId:"Invalid avatar ID",
      missing:"Missing",rights:"I own the rights to this content",rightsSub:"AW confirms VRChat's ownership prompt",private:"Private",public:"Public",
      uploadingN:"Uploading {0} / {1}",done:"Done",queued:"Queued",cancelAll:"Cancel all",resultN:"{0} of {1} uploaded",failed:"Failed",log:"Log",
      close:"Close",blockPending:"Wait for changes to finish",blockSdk:"Sign in to the VRChat SDK in Unity",blockBusy:"An upload is running",
      changedReview:"Looks changed. Check again.",uploadAvatar:"Upload avatar",avatarName:"Name",buildCheck:"Build check",checking:"Checking…",
      working:"Working…",activity:"Activity",changes:"Changes",noChanges:"No changes",index:"Index",indexChanges:"Index changes",itemsN:"{0} items",
      previews:"Previews",connected:"Unity connected",dismiss:"Dismiss",notConfirmed:"Not confirmed by Unity",stApplying:"Applying",stQueued:"Queued",
      general:"General",language:"Language",auto:"Auto",compactCards:"Compact cards",updateAvail:"{0} available",whatsNew:"What's new",
      multipleLooks:"Multiple looks",baseAvatar:"Base avatar",automatic:"Automatic",maintenance:"Maintenance",reindex:"Reindex",clearCache:"Clear cache",
      regenToggles:"Regenerate toggles",migrate:"Migrate legacy presets",checkPreviews:"Check previews",help:"Help",reportProblem:"Report a problem",
      classic:"Classic UI",unsavedScene:"Unsaved scene",version:"Version",newVersion:"Version {0}",wornIn:"Worn in {0}",copyN:"Copy {0}",
      chooseCopy:"Choose a copy",uploadsBlocked:"Upload unavailable",noLooks:"No looks yet",undo:"Undo",copyLabel:"{0} · {1}"},
    ja:{thumbnail:"サムネイル",thDefault:"デフォルト",thDefaultTitle:"デフォルトのサムネイル",thCamera:"カメラ",thImage:"画像",thOnUpload:"アップロード時",thReplace:"更新",thKeep:"維持",
      thNewCamera:"新しいカメラ",thFromView:"シーンビューから",thChoose:"画像を選択",thRender:"レンダリング",thFreeze:"画像として使う",thZoom:"ズーム",thEditDefault:"デフォルトを編集",
      thFollows:"{0} · {1}",thImageType:"画像ファイルを使ってください",looks:"ルック",newLook:"新しいルック",allLooks:"すべてのルック",wardrobe:"ワードローブ",tools:"ツール",menuLayout:"メニュー配置",avatarEditor:"アバターエディター",
      connecting:"接続中…",settings:"設定",search:"検索",compatible:"対応",all:"すべて",filter:"フィルター",show:"表示",showDefault:"カタログ",
      showWorn:"着用中",showReview:"要確認",showHidden:"非表示",creator:"ショップ",tag:"タグ",sort:"並び順",sortNewest:"新しい順",sortUsed:"最近使った順",
      sortName:"名前",hideNoPreview:"プレビューなしを隠す",editTags:"タグを編集",reset:"リセット",typeAll:"すべて",typeOutfits:"衣装",typeHair:"髪",
      typeGimmicks:"ギミック",selectAll:"すべて選択",clear:"クリア",objects:"オブジェクト",materials:"マテリアル",sharedItems:"共通アイテム",allItems:"すべてのアイテム",upload:"アップロード",
      synced:"同期済み",offline:"Unity オフライン",unityBusy:"Unity 処理中",indexing:"インデックス中 {0} / {1}",pending:"{0} 件処理中",failedN:"{0} 件失敗",
      wearing:"着用中",nothingWorn:"何も着ていません",allCreators:"すべてのショップ",loading:"読み込み中…",noMatch:"一致なし",clearFilters:"フィルターを解除",
      chooseAvatar:"アバターを選択",noItems:"アイテムがありません。Unity で衣装をインポートしてください。",retry:"再試行",loadError:"読み込めませんでした",
      wear:"着る",wearOptions:"その他の着用方法",replaceV:"{0} と入れ替え",addCopy:"コピーを追加",fit:"フィット",remove:"外す",removeConfirm:"{1} から {0} を外しますか？",
      menuGroup:"メニューグループ",none:"なし",newGroup:"新しいグループ",groupName:"グループ名",partToggles:"パーツトグル",blendshapeSync:"Blendshape Sync",
      more:"その他",markCompatible:"対応としてマーク",aiName:"名前を整理",report:"分類の誤りを報告",look:"ルック",apply:"適用",cancel:"キャンセル",
      discard:"破棄",unsaved:"未適用",applying:"適用中…",saved:"保存しました",source:"ソース",setup:"セットアップ",confidence:"信頼度",mats:"マテリアル",
      parts:"パーツ",yes:"はい",no:"いいえ",shapeChanges:"シェイプ変更",blendshape:"ブレンドシェイプ",mode:"モード",value:"値",whileW:"条件",modeSet:"Set",
      modeDelete:"Delete",modeRadial:"Radial",whileWorn:"着用時",radialName:"メニュー名",mesh:"メッシュ",add:"追加",radials:"Radial {0} 個 · {1} bit",radial1:"Radial 1 個 · {1} bit",
      noMesh:"ブレンドシェイプのあるメッシュがありません",copies:"{0} 個のコピーに適用",discardChanges:"未適用の変更を破棄しますか？",lookName:"ルック名",rename:"名前を変更",
      del:"削除",deleteLook:"{0} を削除しますか？アイテムはプロジェクトに残ります。",notUploaded:"未アップロード",selectedN:"{0} 件選択",uploadN:"{0} 件をアップロード",
      uploadLook:"ルックをアップロード",uploadLooks:"{0} 件のルックをアップロード",newU:"新規",updateU:"更新",noPlatform:"プラットフォームなし",invalidId:"アバター ID が無効",
      missing:"見つかりません",rights:"このコンテンツの権利を所有しています",rightsSub:"VRChat の所有権確認は AW が承認します",private:"Private",public:"Public",
      uploadingN:"アップロード中 {0} / {1}",done:"完了",queued:"待機中",cancelAll:"すべてキャンセル",resultN:"{1} 件中 {0} 件アップロード",failed:"失敗",log:"ログ",
      close:"閉じる",blockPending:"変更の適用が終わるまでお待ちください",blockSdk:"Unity で VRChat SDK にログインしてください",blockBusy:"アップロード実行中",
      changedReview:"ルックが変更されました。もう一度確認してください。",uploadAvatar:"アバターをアップロード",avatarName:"名前",buildCheck:"ビルドチェック",checking:"確認中…",
      working:"処理中…",activity:"アクティビティ",changes:"変更",noChanges:"変更なし",index:"インデックス",indexChanges:"変更をインデックス",itemsN:"{0} 件",
      previews:"プレビュー",connected:"Unity 接続中",dismiss:"閉じる",notConfirmed:"Unity で確認できません",stApplying:"適用中",stQueued:"待機中",
      general:"一般",language:"言語",auto:"自動",compactCards:"コンパクト表示",updateAvail:"{0} が利用可能",whatsNew:"更新内容",
      multipleLooks:"複数のルック",baseAvatar:"素体",automatic:"自動",maintenance:"メンテナンス",reindex:"再インデックス",clearCache:"キャッシュを削除",
      regenToggles:"トグルを再生成",migrate:"旧プリセットを移行",checkPreviews:"プレビューを確認",help:"ヘルプ",reportProblem:"問題を報告",
      classic:"従来の UI",unsavedScene:"未保存のシーン",version:"バージョン",newVersion:"バージョン {0}",wornIn:"{0} で着用中",copyN:"コピー {0}",
      chooseCopy:"コピーを選択",uploadsBlocked:"アップロードできません",noLooks:"ルックがありません",undo:"元に戻す",copyLabel:"{0} · {1}"}
  };
  var langCode="en",langTable={},langList=[];
  function fmt(v,args){for(var a=0;a<args.length;a++)v=v.split("{"+a+"}").join(args[a]==null?"":args[a]);return v;}
  function T(key){var v=(STR[langCode]&&STR[langCode][key])||STR.en[key]||langTable[key]||key;return fmt(v,Array.prototype.slice.call(arguments,1));}
  // lang.json lookups for shared modules: return the key itself when missing so their fallbacks apply.
  function LT(key){var v=langTable[key]!=null?langTable[key]:key;return fmt(v,Array.prototype.slice.call(arguments,1));}
  function loadLangs(){
    return R.request("/lang.json",{method:"GET"}).then(function(d){
      langList=(d&&d.langs)||[];
      var codes=langList.map(function(l){return l.code;}),pick=R.stored("wardrobeLang","");
      if(!pick||codes.indexOf(pick)<0){
        pick="en";var navs=[];try{navs=navigator.languages||[navigator.language];}catch(_){}
        outer:for(var i=0;i<navs.length;i++){var n=String(navs[i]||"").toLowerCase();for(var c=0;c<codes.length;c++)if(n===codes[c]||n.indexOf(codes[c]+"-")===0){pick=codes[c];break outer;}}
      }
      langCode=pick;var table={};
      langList.forEach(function(l){if(l.code===langCode)(l.strings||[]).forEach(function(e){table[e.k]=e.v;});});
      langList.forEach(function(l){if(l.code==="en")(l.strings||[]).forEach(function(e){if(table[e.k]==null)table[e.k]=e.v;});});
      langTable=table;R.setLanguage(langCode,LT);document.documentElement.lang=langCode;
    }).catch(function(){});
  }
  function applyStrings(root){
    (root||document).querySelectorAll("[data-t]").forEach(function(n){n.textContent=T(n.dataset.t);});
    (root||document).querySelectorAll("[data-t-ph]").forEach(function(n){n.placeholder=T(n.dataset.tPh);});
    (root||document).querySelectorAll("[data-t-title]").forEach(function(n){n.title=T(n.dataset.tTitle);});
    (root||document).querySelectorAll("[data-t-aria]").forEach(function(n){n.setAttribute("aria-label",T(n.dataset.tAria));});
    document.title=LT("app.title")!=="app.title"?LT("app.title"):"Avatar Wardrobe";
  }

  /* ---------- Small helpers ---------- */
  function api(path,options){return R.request(path,options);}
  function icon(id,cls){return '<svg class="i'+(cls?" "+cls:"")+'" aria-hidden="true"><use href="#'+id+'"/></svg>';}
  function spinner(){return '<svg class="i spin" viewBox="0 0 24 24" aria-hidden="true"><circle cx="12" cy="12" r="9" stroke-opacity=".25"/><path d="M21 12a9 9 0 0 0-9-9"/></svg>';}
  var activityLog=[];
  function toast(message,type){
    if(!message)return;
    var node=document.createElement("div");node.className="toast "+(type||"");node.textContent=message;
    $("toasts").appendChild(node);setTimeout(function(){node.remove();},type==="err"?8000:5000);
    activityLog.unshift({id:Date.now()+":"+activityLog.length,message:message,type:type||"",time:new Date().toLocaleTimeString([],{hour:"2-digit",minute:"2-digit"})});
    activityLog=activityLog.slice(0,30);if(!$("activityPop").hidden)paintActivity();
  }
  function busyButton(button,on){
    if(!button)return;
    if(on){if(button.dataset.busy)return false;button.dataset.busy="1";button._html=button.innerHTML;button.disabled=true;button.innerHTML=spinner()+"<span>"+button.textContent+"</span>";return true;}
    if(button.dataset.busy){button.innerHTML=button._html;delete button.dataset.busy;button.disabled=false;}
  }
  function stored(key,fallback){return R.stored(key,fallback);}
  function markItemUsed(id){
    if(!id)return;var key="wardrobeRecentItems:"+recentScope,list=recentItems().filter(function(x){return x!==id;});
    list.unshift(id);R.store(key,JSON.stringify(list.slice(0,200)));
  }
  function recentItems(){try{var v=JSON.parse(stored("wardrobeRecentItems:"+recentScope,"[]"));return Array.isArray(v)?v.filter(function(x){return typeof x==="string";}).slice(0,200):[];}catch(_){return [];}}
  function closeOnOutside(node,anchor,onClose){
    function handler(event){if(node.hidden){cleanup();return;}if(node.contains(event.target)||(anchor&&anchor.contains(event.target)))return;node.hidden=true;cleanup();if(onClose)onClose();}
    function cleanup(){document.removeEventListener("pointerdown",handler,true);}
    setTimeout(function(){document.addEventListener("pointerdown",handler,true);});
  }
  // A small anchored menu: items [{label, act, danger}] | "hr".
  var openMenuNode=null;
  function showMenu(anchor,items){
    closeMenu();
    var menu=document.createElement("div");menu.className="menu";menu.setAttribute("role","menu");
    items.forEach(function(item){
      if(item==="hr"){menu.appendChild(document.createElement("hr"));return;}
      var b=document.createElement("button");b.type="button";b.setAttribute("role","menuitem");b.textContent=item.label;if(item.danger)b.className="danger";
      b.onclick=function(){closeMenu();item.act();};menu.appendChild(b);
    });
    document.body.appendChild(menu);
    var r=anchor.getBoundingClientRect(),w=Math.max(220,menu.offsetWidth),left=Math.min(window.innerWidth-w-8,Math.max(8,r.right-w));
    var top=r.bottom+4;if(top+menu.offsetHeight>window.innerHeight-8)top=Math.max(8,r.top-menu.offsetHeight-4);
    menu.style.left=left+"px";menu.style.top=top+"px";openMenuNode=menu;
    var first=menu.querySelector("button");if(first)first.focus();
    closeOnOutside(menu,anchor,closeMenu);
  }
  function closeMenu(){if(openMenuNode){openMenuNode.remove();openMenuNode=null;}}

  /* ---------- Session state ---------- */
  var S=null,connected=false,unityBusy=false,contextKey="",catalogEpoch="",previewContext="",recentScope=location.origin;
  var multi=false,scope="common",workflowRevision=0,selectingLook=false,currentView="wardrobe";
  var picked=new Set((function(){try{var v=JSON.parse(stored("wardrobeV2.picked","[]"));return Array.isArray(v)?v:[];}catch(_){return [];}})()),batchAsked=false;
  function savePicked(){R.store("wardrobeV2.picked",JSON.stringify(Array.from(picked)));}
  function prunePicked(){if(!S)return;var n=picked.size;picked.forEach(function(id){if(!presets().some(function(p){return p.id===id;}))picked.delete(id);});if(picked.size!==n)savePicked();}
  var installed=[],installedFlight=null,installedSignature="",installedKey=null,installedAt=0,wornIndex=new Map();
  var detailCache=new Map();
  function presets(){return (S&&S.workflowPresets)||[];}
  function lookName(id){if(id==="common")return T("sharedItems");var p=presets().find(function(x){return x.id===id;});return p?p.name:id;}
  function lookColor(id){
    if(id==="common")return {c:"white",alt:false};
    var i=presets().findIndex(function(x){return x.id===id;});if(i<0)i=0;
    return {c:TAPES[i%TAPES.length],alt:i>=TAPES.length};
  }
  function tape(id,name){var c=lookColor(id);return '<span class="tape c-'+c.c+(c.alt?" alt":"")+'"><span>'+esc(name==null?lookName(id):name)+'</span></span>';}
  function chip(id){var c=lookColor(id);return '<i class="chip c-'+c.c+(c.alt?" alt":"")+'"></i>';}
  function effectiveScope(){return multi?scope:"common";}
  function scopedItems(id){return installed.filter(function(it){return !multi||(it.target||"common")===id;});}
  // guid -> looks wearing it. Catalog marks come from here, so switching looks never refetches the catalog.
  function indexWorn(){
    wornIndex=new Map();
    installed.forEach(function(it){var t=multi?(it.target||"common"):"common",set=wornIndex.get(it.guid);if(!set)wornIndex.set(it.guid,set=new Set());set.add(t);});
  }
  function wornLooks(f){
    var guids=Array.isArray(f.variantGuids)&&f.variantGuids.length?f.variantGuids:null;
    if(!guids)return f.installed?[effectiveScope()]:[];
    var set=new Set();guids.forEach(function(g){var t=wornIndex.get(g);if(t)t.forEach(function(x){set.add(x);});});
    var cur=effectiveScope();return Array.from(set).sort(function(a,b){return (b===cur)-(a===cur);});
  }
  function paintAccent(){
    var color=currentView==="wardrobe"&&multi&&scope!=="common"?"var(--t-"+lookColor(scope).c+")":"var(--t-white)";
    document.documentElement.style.setProperty("--accent",color);
  }

  /* ---------- Previews ---------- */
  var previewActivity={active:0,queued:0};
  var previews=new window.WardrobePreviews({text:LT,onActivity:function(value){previewActivity=value;},onUnavailable:function(node){
    if(!cat.hideEmpty||filterValue()==="all"||!node.closest(".grid"))return false;
    var card=node.closest(".card");if(card)card.hidden=true;return true;
  }});

  /* ---------- Operations (desktop host) ---------- */
  var operations=window.WardrobeOperations.create({api:api,
    onChange:function(){paintNotices();paintStatus();if(!$("activityPop").hidden)paintActivity();},
    onHydrated:function(){refreshAfterChange();},
    onSettled:function(record){
      if(window.WardrobeOperations.isMutation(record.type)&&record.state==="succeeded")refreshAfterChange(true);
      if(record.state==="failed"||record.state==="needs-review")toast((record.label||record.type)+" · "+(record.error||T("failed")),"err");
    }});
  function queueOutfit(type,input,label){
    try{var command=operations.build(type,input);return operations.submit(command,label).catch(function(error){toast(error.message,"err");});}
    catch(error){toast(error.message,"err");return Promise.resolve();}
  }
  function pendingOps(){return operations.enabled()?operations.pending().length:0;}

  /* ---------- State polling ---------- */
  var stateFlight=null,nextAutoIndexAt=0,indexAction=false;
  function refreshState(){
    if(stateFlight)return stateFlight;
    var revision=workflowRevision;
    stateFlight=api("/api/state",{method:"GET",timeout:30000}).then(function(s){
      if(!s||s.pending||s.ok===0)throw new Error("No state");
      var wasConnected=connected;
      connected=s.bridgeOnline!==false;unityBusy=false;S=s;
      R.setContext(s.session,s.avatarInstanceId);
      window.WardrobeUpdates.refresh(s.wardrobeVersion,LT);
      $("updDot").hidden=$("wardrobeUpdate")?$("wardrobeUpdate").hidden:!window.WardrobeUpdates.newer(updateLatest(),s.wardrobeVersion);
      window.WardrobeReporting.setVersion(s.wardrobeVersion);
      operations.setHost(!!s.desktop);
      if(s.bridgeOnline===false&&wasConnected)toast(T("offline"),"err");
      var next=[s.session||"",s.avatarGuid,s.avatarName,s.avatarInstanceId||""].join("|");
      var avatarChanged=next!==contextKey,changed=avatarChanged||s.epoch!==catalogEpoch;
      if(avatarChanged){
        closeItem(true);closeFit(true);installed=[];installedSignature="";installedFlight=null;detailCache.clear();
        resetCatalog();
      }
      contextKey=next;catalogEpoch=s.epoch||"";
      var prevScope=effectiveScope();
      if(!selectingLook&&(avatarChanged||revision===workflowRevision)){
        multi=s.wardrobeMode==="multi-avatar";
        scope=multi?(s.selectedPreset||"common"):"common";
        if(scope!=="common"&&!presets().some(function(p){return p.id===scope;}))scope="common";
      }
      if(multi&&!batchAsked){batchAsked=true;loadBatchState(true);}
      var nextPreviews=[s.session||"",s.epoch||"",s.previewEpoch||0].join("|");
      if(nextPreviews!==previewContext){previewContext=nextPreviews;previews.reset(nextPreviews);previews.resume();}
      previews.refresh();
      if(changed){detailCache.clear();loadShops();loadCatalog(false,!avatarChanged&&cat.items.length>0);}
      else if(prevScope!==effectiveScope()){closeItem(true);scopeChanged();paintWearing();}
      if(s.dirty>0&&!s.indexing&&!indexAction&&Date.now()>=nextAutoIndexAt){nextAutoIndexAt=Date.now()+30000;startIndex(null,true);}
      if(indexAction&&!s.indexing&&Date.now()-indexAction>2500)indexAction=false;
      paintRail();paintStatus();paintEmpty();
      // The worn list is the heaviest read; the server stamps it so idle polls can skip it.
      if(s.installedKey==null||s.installedKey!==installedKey||Date.now()-installedAt>60000)loadInstalled(s.installedKey);
      if(!$("activityPop").hidden)paintActivity();
      return s;
    }).catch(function(error){
      if(connected&&error&&(error.status===503||error.name==="AbortError"))unityBusy=true;
      else{connected=false;unityBusy=false;}
      paintStatus();paintEmpty();
    }).finally(function(){stateFlight=null;});
    return stateFlight;
  }
  function updateLatest(){try{var c=JSON.parse(localStorage.getItem("wardrobe.vpm.update.v1"));return c&&c.latest||"";}catch(_){return "";}}
  function refreshAfterChange(reopen){
    detailCache.clear();installedSignature="";
    loadInstalled();loadCatalog(false,true);refreshState();
    if(reopen&&item&&item.d)openItem(item.d.id,item.v&&item.v.guid,item.instance);
  }
  function loadInstalled(stamp){
    if(installedFlight)return installedFlight;
    var expected=contextKey;
    installedFlight=api("/api/installed",{method:"GET",timeout:40000}).then(function(result){
      if(expected!==contextKey||!result||!Array.isArray(result.items))return;
      if(result.projectId)recentScope=result.projectId;
      installedKey=stamp==null?(S&&S.installedKey!=null?S.installedKey:null):stamp;installedAt=Date.now();
      var signature=JSON.stringify([result.items,presets(),scope,multi,langCode,previewContext]);
      installed=result.items;
      if(signature===installedSignature)return;
      installedSignature=signature;indexWorn();installedRev++;pinCache.clear();
      paintWearing();paintRail();renderGrid();loadPins();
    }).catch(function(){}).finally(function(){if(expected===contextKey)installedFlight=null;});
    return installedFlight;
  }
  function pingActive(on){
    if(on&&pageClosed)return Promise.resolve();
    var demand=on?previewDemand():null;
    return R.request("/api/active?on="+(on?"1":"0")+(on&&demand?"&grid="+encodeURIComponent(demand.guids)+"&visible="+encodeURIComponent(demand.visible):""),{timeout:5000,keepalive:!on||document.hidden}).catch(function(){});
  }
  var pollTimer=0,ticking=false,pageClosed=false;
  async function tick(){
    clearTimeout(pollTimer);if(ticking||pageClosed)return;ticking=true;
    try{await pingActive(true);if(!document.hidden)await refreshState();}
    finally{ticking=false;if(!pageClosed)pollTimer=setTimeout(tick,document.hidden?30000:5000);}
  }
  document.addEventListener("visibilitychange",function(){if(!document.hidden){previews.resume();}tick();});
  window.addEventListener("focus",function(){pingActive(true).then(function(){previews.resume();loadInstalled();});});
  window.addEventListener("pagehide",function(){pageClosed=true;pingActive(false);clearTimeout(pollTimer);if(cat.controller)cat.controller.abort();});
  window.addEventListener("pageshow",function(event){if(event.persisted){pageClosed=false;tick();}});
  window.addEventListener("wardrobe-write-status",function(event){if(event.detail.error)toast(event.detail.error,"err");paintStatus();});

  /* ---------- Rail ---------- */
  function paintRail(){
    var s=S||{};
    R.text($("avName"),s.avatarInstanceId?(s.avatarLabel||s.avatarName||"—"):T("chooseAvatar"));
    var sub=s.scenePath?s.scenePath.split("/").pop().replace(/\.unity$/i,""):"";
    R.text($("avSub"),[sub,s.sceneUnsaved?T("unsavedScene"):""].filter(Boolean).join(" · "));
    $("looksSec").hidden=!multi;$("singleSec").hidden=multi;
    if(multi){
      var counts={};installed.forEach(function(it){var k=it.target||"common";counts[k]=(counts[k]||0)+1;});
      prunePicked();
      // Shared items first: what every look includes. Checkboxes pick looks for Upload; the row selects the look.
      var rows=[{id:"common",name:T("sharedItems")}].concat(presets().map(function(p){return {id:p.id,name:p.name};}));
      R.reconcile($("lookList"),rows,function(r){return r.id;},function(){
        var row=document.createElement("div");row.className="rail-row look-row";row.setAttribute("role","listitem");
        row.innerHTML='<input type="checkbox" class="lk-ck"><button type="button" class="lk-main"><i class="chip"></i><span class="nm"></span><span class="pip" hidden></span><span class="n"></span></button><button type="button" class="btn btn-ghost icon-btn sm lk-more">'+icon("i-more")+'</button>';
        row.querySelector(".lk-main").onclick=function(){selectLook(row._id);};
        row.querySelector(".lk-ck").onchange=function(){if(this.checked)picked.add(row._id);else picked.delete(row._id);savePicked();paintRail();paintWearing();};
        var more=row.querySelector(".lk-more");more.onclick=function(){lookMenu(more,row._id);};
        row.oncontextmenu=function(event){if(row._id==="common")return;event.preventDefault();lookMenu(more,row._id);};
        return row;
      },function(row,r){
        row._id=r.id;var common=r.id==="common",c=lookColor(r.id),on=currentView==="wardrobe"&&scope===r.id;
        row.querySelector(".chip").className="chip c-"+c.c+(c.alt?" alt":"");
        R.text(row.querySelector(".nm"),r.name);R.text(row.querySelector(".n"),counts[r.id]?String(counts[r.id]):"");
        row.classList.toggle("on",on);row.classList.toggle("shared",common);
        var main=row.querySelector(".lk-main");if(on)main.setAttribute("aria-current","true");else main.removeAttribute("aria-current");
        var box=row.querySelector(".lk-ck");box.disabled=common;box.checked=picked.has(r.id);box.style.visibility=common?"hidden":"";box.setAttribute("aria-label",r.name);
        var more=row.querySelector(".lk-more");more.hidden=common;more.setAttribute("aria-label",T("more"));
        var bp=batchState&&(batchState.presets||[]).find(function(p){return p.id===r.id;}),fresh=!common&&!!batchState&&(!bp||!String(bp.blueprintId||"").trim());
        var pip=row.querySelector(".pip");pip.hidden=!fresh;pip.title=fresh?T("notUploaded"):"";
      });
      var all=$("lookAll"),n=picked.size,total=presets().length;
      all.checked=n>0&&n===total;all.indeterminate=n>0&&n<total;all.disabled=!total;
      R.text($("lookSelN"),n?T("selectedN",n):"");
      if(!total){var note=document.createElement("p");note.className="empty-note sub";note.textContent=T("noLooks");$("lookList").appendChild(note);}
    }
    var menuOn=stored("wardrobeV2.tool.menu","0")==="1",editorOn=stored("wardrobeV2.tool.editor","0")==="1";
    $("toolMenu").hidden=!menuOn;$("toolEditor").hidden=!editorOn;$("toolsSec").hidden=!menuOn&&!editorOn;
    document.querySelectorAll(".rail [data-view]").forEach(function(b){
      var v=b.dataset.view,on=v===currentView&&(v!=="wardrobe"||!multi);b.classList.toggle("on",on);
    });
    paintAccent();
  }
  function paintStatus(){
    var s=S||{},dot=$("statusDot"),pending=R.pendingWrites()+pendingOps(),failed=operations.enabled()?operations.list().filter(function(r){return (r.state==="failed"||r.state==="needs-review")&&!r.dismissed;}).length:0;
    var text,cls="";
    if(!connected&&S){text=T("offline");cls="off";}
    else if(!S){text=T("connecting");cls="busy";}
    else if(unityBusy){text=T("unityBusy");cls="busy";}
    else if(s.indexing){text=s.total?T("indexing",(s.done||0).toLocaleString(),s.total.toLocaleString()):LT("index.discovery");cls="work";}
    else if(upload.running){text=T("uploadingN",Math.min((upload.q.index||0)+1,upload.q.total||1),upload.q.total||1);cls="work";}
    else if(pending){text=T("pending",pending);cls="work";}
    else text=T("synced");
    if(failed){text+=" · "+T("failedN",failed);cls=cls||"busy";}
    dot.className="dot "+cls;R.text($("statusText"),text);
  }
  function selectLook(id){
    if(currentView!=="wardrobe")setView("wardrobe");
    if(id===scope){paintRail();return;}
    var previous=scope;scope=id;selectingLook=true;workflowRevision++;
    closeItem(true);paintRail();paintWearing();scopeChanged();gridScroll.scrollTop=0;
    api("/api/workflow?selected="+encodeURIComponent(id)).then(function(r){
      if(!r||!r.ok)throw new Error(r&&r.message||T("loadError"));
    }).catch(function(error){scope=previous;toast(error.message,"err");paintRail();paintWearing();scopeChanged();})
      .finally(function(){selectingLook=false;});
  }
  // The catalog list depends on the avatar, not the Look; only the "Worn" filter is per Look.
  function scopeChanged(){
    indexWorn();
    if(cat.show==="installed"){loadCatalog();return;}
    detailCache.clear();loadPins();
  }
  function newLook(){
    var name=prompt(T("lookName"));if(name==null||!name.trim())return;
    api("/api/preset_save?name="+encodeURIComponent(name.trim())).then(async function(r){
      if(!r||!r.ok)throw new Error(r&&r.message||T("loadError"));
      await refreshState();
      if(r.id)selectLook(r.id);
    }).catch(function(error){toast(error.message,"err");});
  }
  $("newLook").onclick=newLook;
  $("lookAll").onchange=function(){if(this.checked)presets().forEach(function(p){picked.add(p.id);});else picked.clear();savePicked();paintRail();paintWearing();};
  document.querySelectorAll(".rail [data-view]").forEach(function(b){b.onclick=function(){setView(b.dataset.view);};});

  // Avatar switcher
  $("avatarSwitch").onclick=function(){
    var pop=$("avatarPop");if(!pop.hidden){pop.hidden=true;this.setAttribute("aria-expanded","false");return;}
    var targets=(S&&S.sceneTargets)||[],current=S&&S.avatarInstanceId;
    pop.innerHTML='<div class="sec"><span>'+esc(T("chooseAvatar"))+'</span></div>'+(targets.length?targets.map(function(t){
      return '<button type="button" class="target-row'+(t.id===current?" on":"")+'" data-id="'+esc(t.id)+'"><span class="nm">'+esc(t.name)+'</span><span class="sub">'+esc(String(t.scene||"").split("/").pop().replace(/\.unity$/i,""))+'</span></button>';
    }).join(""):'<p class="empty-note">'+esc(LT("grid.chooseAvatarHint"))+'</p>');
    pop.hidden=false;this.setAttribute("aria-expanded","true");
    var anchor=this;closeOnOutside(pop,anchor,function(){anchor.setAttribute("aria-expanded","false");});
    pop.onclick=function(event){var b=event.target.closest("[data-id]");if(!b)return;pop.hidden=true;anchor.setAttribute("aria-expanded","false");selectTarget(b.dataset.id);};
  };
  function selectTarget(id){
    api("/api/target?id="+encodeURIComponent(id)).then(function(r){if(!r||!r.ok)throw new Error(r&&r.message||T("loadError"));return refreshState();})
      .catch(function(error){toast(error.message,"err");});
  }

  /* ---------- Catalog ---------- */
  var cat={filter:"compatible",show:"",search:"",shop:"",category:"",tag:"",sort:stored("wardrobeSort","added"),hideEmpty:stored("wardrobeHideEmpty","0")==="1",
    page:0,pageCount:1,total:0,items:[],token:0,inflight:false,controller:null,initial:true,error:false};
  if(["added","used","name"].indexOf(cat.sort)<0)cat.sort="added";
  var grid=$("grid"),gridScroll=$("gridScroll"),cardCache=new Map(),selectedFamily="";
  function filterValue(){return cat.show||cat.filter;}
  // Items worn in the current look sit at the top of the grid. The worn list names them right away;
  // a filtered /api/families read then fills in exact rows (same ids), so the section never jumps.
  var pin={key:"",rows:[]},pinCache=new Map(),pinToken=0,installedRev=0;
  function pinsOn(){return cat.show!=="installed";}
  function pinKey(){return [effectiveScope(),cat.search,cat.shop,cat.category,cat.tag,cat.hideEmpty,cat.sort,installedRev,catalogEpoch].join("|");}
  function pinQuery(){return query(0).replace(/&filter=[^&]*/,"&filter=installed").replace(/&pageSize=\d+/,"&pageSize=200");}
  function provisionalPins(){
    var byFamily=new Map();
    scopedItems(effectiveScope()).forEach(function(it){
      if(!it.familyId||byFamily.has(it.familyId))return;
      var row=cat.items.find(function(f){return f.id===it.familyId;});
      byFamily.set(it.familyId,row||{id:it.familyId,name:it.family,thumb:it.guid,shop:"",product:"",variants:0,installed:1,
        variantGuids:installed.filter(function(x){return x.familyId===it.familyId;}).map(function(x){return x.guid;})});
    });
    return Array.from(byFamily.values());
  }
  function loadPins(){
    if(!pinsOn()||!S||!S.avatarInstanceId){if(pin.rows.length||pin.key){pin={key:"",rows:[]};renderGrid();}return;}
    var key=pinKey();if(key===pin.key)return;
    var cached=pinCache.get(key);
    if(cached){pin={key:key,rows:cached};renderGrid();return;}
    if(!scopedItems(effectiveScope()).length){pinCache.set(key,[]);pin={key:key,rows:[]};renderGrid();return;}
    var filtered=!!(cat.search||cat.shop||cat.category||cat.tag);
    pin={key:key,rows:filtered?[]:provisionalPins()};renderGrid();
    var token=++pinToken;
    api(pinQuery(),{method:"GET",timeout:30000}).then(function(r){
      if(token!==pinToken||!r||!Array.isArray(r.items))return;
      pinCache.set(key,r.items);if(pinCache.size>48)pinCache.delete(pinCache.keys().next().value);
      if(pin.key===key){pin={key:key,rows:r.items};renderGrid();}
    }).catch(function(){});
  }
  function query(page){
    return "/api/families?target="+encodeURIComponent(effectiveScope())+"&filter="+filterValue()+"&page="+page+"&pageSize=60&search="+encodeURIComponent(cat.search)+
      "&shop="+encodeURIComponent(cat.shop)+"&category="+encodeURIComponent(cat.category)+"&tag="+encodeURIComponent(cat.tag)+"&hideEmpty="+(cat.hideEmpty?1:0)+
      "&sort="+cat.sort+(cat.sort==="used"?"&used="+encodeURIComponent(JSON.stringify({ids:recentItems()})):"");
  }
  function resetCatalog(){cat.token++;if(cat.controller)cat.controller.abort();cat.items=[];cat.page=0;cat.pageCount=1;cat.total=0;cat.inflight=false;renderGrid();}
  async function loadCatalog(append,preserve){
    if(append&&(cat.inflight||cat.page>=cat.pageCount-1))return;
    if(!S||!S.avatarInstanceId){cat.initial=false;renderGrid();return;}
    if(cat.controller)cat.controller.abort();
    var controller=new AbortController(),token=++cat.token,target=append?cat.page+1:0,last=!append&&preserve?cat.page:target;
    cat.controller=controller;cat.inflight=true;cat.error=false;if(!append)loadPins();paintEmpty();
    try{
      var rows=[],result;
      for(var p=target;p<=last;p++){
        result=await api(query(p),{signal:controller.signal,method:"GET",timeout:40000});
        if(token!==cat.token)return;
        if(!result||!Array.isArray(result.items))throw new Error(result&&result.message||T("loadError"));
        rows=rows.concat(result.items);last=Math.min(last,Math.max(0,result.pageCount-1));
      }
      var merged=append?cat.items.concat(rows):rows;
      cat.items=Array.from(new Map(merged.map(function(f){return [f.id,f];})).values());
      cat.page=result.page;cat.pageCount=Math.max(1,result.pageCount);cat.total=result.total;
      var top=gridScroll.scrollTop;renderGrid();
      if(preserve)gridScroll.scrollTop=top;else if(!append)gridScroll.scrollTop=0;
      if(!document.hidden)pingActive(true);
    }catch(error){
      if(token!==cat.token||error.name==="AbortError")return;
      cat.error=true;if(cat.items.length)toast(error.message||T("loadError"),"err");
    }finally{
      if(token===cat.token){cat.inflight=false;cat.initial=false;paintEmpty();R.text($("catalogCount"),cat.total?cat.total.toLocaleString():"");setTimeout(maybeMore,50);}
    }
  }
  function gridEntries(){
    var rows=pinsOn()?pin.rows:[];
    if(!rows.length)return cat.items.map(function(f){return {key:f.id,f:f};});
    var ids=new Set(rows.map(function(f){return f.id;})),rest=cat.items.filter(function(f){return !ids.has(f.id);});
    var out=[{key:"#pin",head:"pin",n:rows.length}].concat(rows.map(function(f){return {key:f.id,f:f,pin:true};}));
    if(rest.length)out.push({key:"#all",head:"all"});
    return out.concat(rest.map(function(f){return {key:f.id,f:f};}));
  }
  function renderGrid(){
    var root=gridScroll;
    R.reconcile(grid,gridEntries(),function(e){return e.key;},function(e){
      if(e.head){var head=document.createElement("div");head.className="grid-head";return head;}
      var f=e.f,card=cardCache.get(f.id);
      if(!card){
        card=document.createElement("div");card.className="card";card.tabIndex=0;card.setAttribute("role","button");
        // The preview pipeline owns .well's children; marks and count sit beside it in .frame.
        card.innerHTML='<span class="frame"><span class="well"></span><span class="marks"></span><span class="vc" hidden></span></span><span class="nm"></span><span class="cr"></span>';
        card.onclick=function(event){if(event.target.closest(".preview-retry"))return;openItem(card._f.id);};
        card.onkeydown=function(event){if(event.target===card&&(event.key==="Enter"||event.key===" ")){event.preventDefault();openItem(card._f.id);}};
        cardCache.set(f.id,card);
      }
      return card;
    },function(card,e){
      if(e.head){
        var html=e.head==="pin"?(multi?tape(effectiveScope()):'<strong>'+esc(T("wearing"))+'</strong>')+'<span class="n">'+e.n+'</span>':'<strong>'+esc(T("allItems"))+'</strong>';
        if(card._html!==html){card._html=html;card.innerHTML=html;}
        card.classList.toggle("first",e.head==="pin");return;
      }
      var f=e.f;card._f=f;card.classList.toggle("pin",!!e.pin);
      card.hidden=filterValue()!=="all"&&cat.hideEmpty&&previews.isUnavailable(f.thumb);
      card.classList.toggle("sel",selectedFamily===f.id);
      R.text(card.querySelector(".nm"),f.name);
      var incompatible=Number(f.compat)>1&&!f.compatibleOverride,cr=card.querySelector(".cr");
      R.text(cr,incompatible?f.compatText:[f.shop,f.product].filter(Boolean).join(" · ")||" ");cr.classList.toggle("warn-t",incompatible);
      var vc=card.querySelector(".vc");vc.hidden=!(f.variants>1);R.text(vc,f.variants>1?String(f.variants):"");
      var looks=wornLooks(f),marks=looks.slice(0,3).map(chip).join("")+(looks.length>3?'<i class="more">+'+(looks.length-3)+'</i>':"");
      card.title=f.name+(looks.length?" · "+looks.map(lookName).join(", "):"");
      if(card._marks!==marks){card._marks=marks;card.querySelector(".marks").innerHTML=marks;}
      card.setAttribute("aria-label",f.name+(f.variants>1?", "+f.variants:""));
      previews.observe(card.querySelector(".well"),f.thumb,{priority:1,root:root});
    });
    if(cardCache.size>240)cardCache.forEach(function(card,id){if(cardCache.size>240&&!card.isConnected)cardCache.delete(id);});
    previews.sweep();paintEmpty();
  }
  function paintEmpty(){
    var node=$("gridEmpty"),s=S||{},empty=!cat.items.length&&!(pinsOn()&&pin.rows.length),mode="";
    if(!empty){node.hidden=true;return;}
    var filtered=!!(cat.search||cat.shop||cat.category||cat.tag||cat.show||cat.hideEmpty);
    if(!connected&&S)mode="offline";
    else if(!S||cat.initial||cat.inflight||s.indexing)mode="loading";
    else if(!s.avatarInstanceId)mode="target";
    else if(cat.error)mode="error";
    else if(filtered||cat.filter!=="all")mode=filtered?"filters":"none";
    else mode="none";
    var html="";
    if(mode==="loading")html=spinner()+'<span>'+esc(s.indexing&&s.total?T("indexing",s.done||0,s.total):T("loading"))+'</span>';
    else if(mode==="target"){
      var targets=s.sceneTargets||[];
      html='<svg class="icon" aria-hidden="true"><use href="#icon-wardrobe"/></svg><strong>'+esc(T("chooseAvatar"))+'</strong><div class="targets">'+targets.map(function(t){
        return '<button type="button" class="target-row" data-target="'+esc(t.id)+'"><span class="nm">'+esc(t.name)+'</span><span class="sub">'+esc(String(t.scene||"").split("/").pop().replace(/\.unity$/i,""))+'</span><span class="btn sm">'+esc(LT("grid.chooseAvatar"))+'</span></button>';
      }).join("")+'</div>';
    }
    else if(mode==="offline")html='<strong>'+esc(T("offline"))+'</strong><button type="button" class="btn" data-act="retry">'+esc(T("retry"))+'</button>';
    else if(mode==="error")html='<strong>'+esc(T("loadError"))+'</strong><button type="button" class="btn" data-act="retry">'+esc(T("retry"))+'</button>';
    else if(mode==="filters")html='<strong>'+esc(T("noMatch"))+'</strong><button type="button" class="btn" data-act="clear">'+esc(T("clearFilters"))+'</button>';
    else if(cat.filter==="compatible")html='<strong>'+esc(T("noMatch"))+'</strong><button type="button" class="btn" data-act="all">'+esc(T("all"))+'</button>';
    else html='<svg class="icon" aria-hidden="true"><use href="#icon-wardrobe"/></svg><strong>'+esc(T("noItems"))+'</strong>';
    if(node._html!==html){node._html=html;node.innerHTML=html;}
    node.hidden=false;
  }
  $("gridEmpty").onclick=function(event){
    var target=event.target.closest("[data-target]");if(target){selectTarget(target.dataset.target);return;}
    var act=(event.target.closest("[data-act]")||{}).dataset;act=act&&act.act;
    if(act==="retry"){refreshState();loadCatalog();}
    else if(act==="clear")resetFilters();
    else if(act==="all")setFilter("all");
  };
  function maybeMore(){
    if(currentView!=="wardrobe"||cat.inflight||cat.page>=cat.pageCount-1)return;
    var edge=Math.min(innerHeight,gridScroll.getBoundingClientRect().bottom);
    if($("sentinel").getBoundingClientRect().top-edge<600)loadCatalog(true);
  }
  new IntersectionObserver(function(entries){if(entries.some(function(e){return e.isIntersecting;}))maybeMore();},{rootMargin:"600px"}).observe($("sentinel"));
  window.addEventListener("scroll",function(){if(gridScroll.scrollHeight<=gridScroll.clientHeight+1)maybeMore();},{passive:true});
  var scrollFrame=0,demandTimer=0,lastDemand="";
  gridScroll.addEventListener("scroll",function(){
    if(scrollFrame)return;
    scrollFrame=requestAnimationFrame(function(){scrollFrame=0;maybeMore();clearTimeout(demandTimer);demandTimer=setTimeout(function(){var d=previewDemand();if(d&&d.guids+"|"+d.visible!==lastDemand)pingActive(true);},180);});
  },{passive:true});
  function previewDemand(){
    if(currentView!=="wardrobe"||document.hidden)return null;
    var bounds=gridScroll.getBoundingClientRect(),cards=Array.from(grid.children).filter(function(c){return c._f&&!c.hidden;}),visible=[],last=-1;
    cards.forEach(function(card,i){var r=card.getBoundingClientRect();if(r.width&&r.bottom>bounds.top&&r.top<bounds.bottom){visible.push(card);last=i;}});
    var set=new Set(visible.map(function(c){return c._f.thumb;}).filter(Boolean));
    var variants=visible.map(function(c){return c._f.variantGuids||[];}),max=variants.reduce(function(m,v){return Math.max(m,v.length);},0);
    for(var i=0;i<max&&set.size<120;i++)variants.forEach(function(v){if(v[i]&&set.size<120)set.add(v[i]);});
    var vis=Array.from(set).slice(0,120).join(",");
    cards.slice(last+1,last+41).forEach(function(c){if(c._f.thumb&&set.size<120)set.add(c._f.thumb);});
    var d={visible:vis,guids:Array.from(set).slice(0,120).join(",")};lastDemand=d.guids+"|"+d.visible;return d;
  }

  // Toolbar
  var searchTimer=0;
  $("search").addEventListener("input",function(event){clearTimeout(searchTimer);if(!event.isComposing)searchTimer=setTimeout(submitSearch,180);});
  $("search").addEventListener("compositionend",function(){clearTimeout(searchTimer);submitSearch();});
  function submitSearch(){var v=$("search").value.trim();if(v===cat.search)return;cat.search=v;loadCatalog();}
  function setFilter(value){
    cat.filter=value;
    document.querySelectorAll("#compatSeg button").forEach(function(b){var on=b.dataset.f===value;b.classList.toggle("on",on);b.setAttribute("aria-pressed",String(on));});
    loadCatalog();
  }
  $("compatSeg").onclick=function(event){var b=event.target.closest("[data-f]");if(!b)return;if(cat.show){cat.show="";$("showSel").value="";paintFilterCount();}setFilter(b.dataset.f);};
  $("typeTabs").onclick=function(event){
    var b=event.target.closest("[data-cat]");if(!b)return;cat.category=b.dataset.cat;
    document.querySelectorAll("#typeTabs .tab").forEach(function(t){var on=t===b;t.classList.toggle("on",on);t.setAttribute("aria-selected",String(on));});
    loadCatalog();
  };
  $("filterBtn").onclick=function(){
    var pop=$("filterPop"),btn=this;pop.hidden=!pop.hidden;btn.setAttribute("aria-expanded",String(!pop.hidden));
    if(!pop.hidden)closeOnOutside(pop,btn,function(){btn.setAttribute("aria-expanded","false");});
  };
  $("showSel").onchange=function(){cat.show=this.value;$("compatSeg").classList.toggle("dim",!!cat.show);paintFilterCount();loadCatalog();};
  $("shopSel").onchange=function(){cat.shop=this.value;paintFilterCount();loadCatalog();};
  $("tagSel").onchange=function(){cat.tag=this.value;paintFilterCount();loadCatalog();};
  $("sortSel").value=cat.sort;$("sortSel").onchange=function(){cat.sort=this.value;R.store("wardrobeSort",cat.sort);loadCatalog();};
  $("hideEmptySw").checked=cat.hideEmpty;$("hideEmptySw").onchange=function(){cat.hideEmpty=this.checked;R.store("wardrobeHideEmpty",cat.hideEmpty?"1":"0");paintFilterCount();loadCatalog();};
  $("editTags").onclick=function(){$("filterPop").hidden=true;window.WardrobeOrganize.openManager();};
  $("resetFilters").onclick=resetFilters;
  function resetFilters(){
    cat.show=cat.shop=cat.tag=cat.search="";cat.hideEmpty=false;R.store("wardrobeHideEmpty","0");
    $("showSel").value=$("shopSel").value=$("tagSel").value=$("search").value="";$("hideEmptySw").checked=false;$("compatSeg").classList.remove("dim");
    paintFilterCount();loadCatalog();
  }
  function paintFilterCount(){var n=[cat.show,cat.shop,cat.tag,cat.hideEmpty].filter(Boolean).length;$("filterCount").hidden=!n;R.text($("filterCount"),n||"");}
  async function loadShops(){
    try{
      var d=await api("/api/shops",{method:"GET"});if(!d||!d.items)return;
      R.reconcile($("shopSel"),[{name:"",count:0}].concat(d.items),function(x){return x.name;},function(){return document.createElement("option");},function(o,x){o.value=x.name;R.text(o,x.name?x.name+" ("+x.count+")":T("allCreators"));});
      $("shopSel").value=cat.shop;if($("shopSel").value!==cat.shop){cat.shop="";$("shopSel").value="";}
    }catch(_){}
    window.WardrobeOrganize.refresh();
  }
  window.WardrobeOrganize.configure({toast:toast,changed:function(scene,patch){
    if(patch&&patch.familyId&&!cat.tag&&!cat.search){var f=cat.items.find(function(x){return x.id===patch.familyId;});if(f)f.tags=patch.tags;return;}
    if(patch&&patch.sceneOnly){refreshState();loadInstalled();return;}
    detailCache.clear();loadCatalog(false,true);if(scene){refreshState();loadInstalled();}
  }});
  window.WardrobeOrganize.onChange(function(){
    var value=window.WardrobeOrganize.fillTagSelect($("tagSel"),cat.tag);
    if(value!==cat.tag){cat.tag=value;paintFilterCount();loadCatalog();}
  });

  /* ---------- Wearing panel ---------- */
  function paintWearing(){
    var s=S||{},id=effectiveScope(),items=scopedItems(id);
    $("wearTitle").innerHTML=multi?tape(id):'<strong>'+esc(T("wearing"))+'</strong>';
    R.text($("wearCount"),items.length?String(items.length):"");
    var list=$("wearList");
    R.reconcile(list,items,function(it){return it.guid+"|"+it.instanceId+"|"+(it.path||"");},function(){
      var b=document.createElement("button");b.type="button";b.className="worn";
      b.innerHTML='<span class="thumb-s"></span><span class="worn-copy"><span class="t"></span><span class="s"></span></span>';
      b.onclick=function(){openItem(b._it.familyId,b._it.guid,b._it);};return b;
    },function(b,it){
      b._it=it;var variant=it.variant&&it.variant.toLowerCase()!=="default"?it.variant:LT("variant.default");
      R.text(b.querySelector(".t"),it.family);var sub=b.querySelector(".s");
      R.text(sub,it.setupWarning?it.setupWarning:variant);sub.classList.toggle("warn-t",!!it.setupWarning);
      b.title=it.family+" · "+variant;b.classList.toggle("hl",!!(item&&item.instance&&item.instance.instanceId===it.instanceId));
      previews.observe(b.querySelector(".thumb-s"),it.guid,{priority:2,root:$("wearBody")});
    });
    var empty=list.querySelector(".empty-note");
    if(!items.length&&!empty){empty=document.createElement("p");empty.className="empty-note";list.appendChild(empty);}
    if(empty){if(items.length)empty.remove();else R.text(empty,s.avatarInstanceId?T("nothingWorn"):T("chooseAvatar"));}
    var btn=$("uploadBtn");btn.disabled=!connected||!s.avatarInstanceId;
    R.text(btn,upload.running?T("uploadingN",Math.min((upload.q.index||0)+1,upload.q.total||1),upload.q.total||1):
      multi&&picked.size?(picked.size===1?T("uploadLook"):T("uploadLooks",picked.size)):T("upload"));
    paintNotices();
  }
  $("uploadBtn").onclick=function(){
    if(!multi){openSceneUpload();return;}
    if(upload.running){openUpload(null);return;}
    // Checked looks win; otherwise upload the look on screen (Shared items: every included look).
    if(picked.size){openUpload(presets().filter(function(p){return picked.has(p.id);}).map(function(p){return p.id;}));return;}
    var id=effectiveScope();
    openUpload(id==="common"?null:[id]);
  };
  function paintNotices(){
    var node=$("notices");if(!node)return;
    var records=operations.enabled()?operations.notices(effectiveScope()):[];
    R.reconcile(node,records,function(r){return r.id;},function(){
      var d=document.createElement("div");d.className="notice";
      d.innerHTML='<span class="ic"></span><span class="ev-copy"><strong></strong><span class="sub"></span><span class="acts"><button type="button" class="btn sm" data-a="retry"></button><button type="button" class="btn btn-ghost sm" data-a="dismiss"></button><button type="button" class="btn btn-ghost sm" data-a="cancel"></button></span></span>';
      d.onclick=function(event){
        var a=(event.target.closest("[data-a]")||{}).dataset;a=a&&a.a;var r=d._r;if(!a)return;
        if(a==="retry")operations.retry(r.id,effectiveScope()).catch(function(e){toast(e.message,"err");});
        else if(a==="dismiss"){try{operations.dismiss(r.id);}catch(e){toast(e.message,"err");}}
        else if(a==="cancel")operations.cancel(r.id).catch(function(e){toast(e.message,"err");});
      };
      return d;
    },function(d,r){
      d._r=r;var attn=r.state==="failed"||r.state==="needs-review";d.classList.toggle("attn",attn);
      d.querySelector(".ic").innerHTML=attn?icon("i-warn","warn-t"):spinner();
      R.text(d.querySelector("strong"),r.label||r.type);
      R.text(d.querySelector(".sub"),r.state==="needs-review"?T("notConfirmed"):r.state==="failed"?(r.error||T("failed")):r.state==="queued"?T("stQueued"):T("stApplying"));
      var retry=d.querySelector('[data-a="retry"]'),dismiss=d.querySelector('[data-a="dismiss"]'),cancel=d.querySelector('[data-a="cancel"]');
      R.text(retry,T("retry"));R.text(dismiss,T("dismiss"));R.text(cancel,T("cancel"));
      retry.hidden=r.state!=="failed"||!operations.canRetry(r);dismiss.hidden=!attn;cancel.hidden=attn||!!r.cancelRequested;
    });
  }

  /* ---------- Item panel ---------- */
  var item=null,itemBusy=false;
  function familyUrl(id,target){return "/api/family?id="+encodeURIComponent(id)+"&target="+encodeURIComponent(target);}
  function openItem(id,selGuid,instance){
    if(itemBusy)return;
    markItemUsed(id);selectedFamily=id;
    grid.querySelectorAll(".card.sel").forEach(function(c){c.classList.remove("sel");});var card=cardCache.get(id);if(card)card.classList.add("sel");
    var panel=$("itemDlg"),token={};
    item={id:id,token:token,instance:instance||null,selGuid:selGuid||""};
    if(currentView!=="wardrobe")setView("wardrobe");
    if(!panel.open)panel.showModal();
    paintWearing();
    var cached=detailCache.get(id+"|"+effectiveScope());
    if(cached){renderItem(cached);return;}
    var row=cat.items.find(function(f){return f.id===id;}),guess=row?{name:row.name,shop:[row.shop,row.product].filter(Boolean).join(" · "),guid:selGuid||row.thumb}:
      instance?{name:instance.family,shop:"",guid:instance.guid}:null;
    panel.innerHTML=itemHead()+(guess?'<div class="item-grid"><div class="item-media"><div class="big" id="itBig"></div></div><div class="item-body"><h2 class="item-title" id="itTitle">'+esc(guess.name)+'</h2><div class="item-meta">'+(guess.shop?'<span>'+esc(guess.shop)+'</span>':'')+'</div><div class="loading slim">'+spinner()+'</div></div></div>':
      '<div class="loading">'+spinner()+'<span>'+esc(T("loading"))+'</span></div>');wireItemHead();
    if(guess&&guess.guid)previews.bind($("itBig"),guess.guid,{priority:0,detail:true});
    api(familyUrl(id,effectiveScope()),{method:"GET"}).then(function(d){
      if(!item||item.token!==token)return;
      if(!d||!d.variants)throw new Error(d&&d.message||LT("err.notfound"));
      detailCache.set(id+"|"+effectiveScope(),d);if(detailCache.size>96)detailCache.delete(detailCache.keys().next().value);
      renderItem(d);
    }).catch(function(error){
      if(!item||item.token!==token)return;
      panel.innerHTML=itemHead()+'<div class="loading"><span>'+esc(error.message||T("loadError"))+'</span><button type="button" class="btn sm" id="itRetry">'+esc(T("retry"))+'</button></div>';wireItemHead();
      $("itRetry").onclick=function(){openItem(id,selGuid,instance);};
    });
  }
  function itemHead(){
    return '<button type="button" class="btn btn-ghost icon-btn sm item-x" id="itClose" aria-label="'+esc(T("close"))+'">'+icon("i-x")+'</button>';
  }
  function wireItemHead(){$("itClose").onclick=function(){closeItem();};}
  $("itemDlg").addEventListener("cancel",function(event){event.preventDefault();closeItem();});
  $("itemDlg").addEventListener("click",function(event){if(event.target===this)closeItem();});
  function closeItem(force){
    if(itemBusy&&!force)return;
    if(!force&&item&&item.dirty&&!confirm(T("discardChanges")))return;
    item=null;selectedFamily="";grid.querySelectorAll(".card.sel").forEach(function(c){c.classList.remove("sel");});
    var dlg=$("itemDlg");if(dlg.open)dlg.close();dlg.innerHTML="";paintWearing();
  }
  function variantLabel(variants,x,n){
    var label=String(x.variant||"").trim();
    if(label&&label.toLowerCase()!=="default"&&variants.filter(function(v){return v.variant===label;}).length===1)return label;
    var color=String(x.colorway||"").trim();
    if(color&&variants.filter(function(v){return v.colorway===color;}).length===1)return color;
    if(variants.length===1)return LT("variant.default");
    var base=String(x.source||"").split("/").pop().replace(/\.prefab$/i,"");
    return base&&variants.filter(function(v){return String(v.source||"").split("/").pop().replace(/\.prefab$/i,"")===base;}).length===1?base:LT("detail.variant",n+1);
  }
  function compatClass(c){return c===0?"ok-t":c===1?"warn-t":"bad-t";}
  function renderItem(d){
    var it=item;if(!it)return;
    var variants=!it.selGuid&&filterValue()==="compatible"?d.variants.filter(function(x){return x.compat===0||x.compat===1;}):d.variants;
    if(!variants.length)variants=d.variants;
    var index=0;
    if(it.selGuid)variants.forEach(function(x,n){if(x.guid===it.selGuid)index=n;});
    else{var worn=variants.findIndex(function(x){return x.installed;});if(worn>=0)index=worn;}
    it.d=d;it.variants=variants;it.dest=it.instance?(it.instance.target||"common"):effectiveScope();
    var panel=$("itemDlg"),multiVariant=variants.length>1;
    panel.innerHTML=itemHead()+'<div class="item-grid"><div class="item-media">'+
      '<div class="big" id="itBig"></div>'+
      '<div class="vlabel"><span id="itVname"></span><span class="sub" id="itVcount"></span></div>'+
      (multiVariant?'<div class="vstrip" id="itStrip"></div>':'')+
      '</div><div class="item-body" id="itBody">'+
      '<h2 class="item-title" id="itTitle">'+esc(d.name)+'</h2><div class="item-meta" id="itMeta"></div>'+
      '<div class="wear-row" id="itActs"></div>'+
      '<div class="rows">'+
        (multi?'<div class="row"><span class="lbl">'+esc(T("look"))+'</span><div class="ctl"><select id="itLook" aria-label="'+esc(T("look"))+'"></select></div></div>':'')+
        '<div class="row"><span class="lbl">'+esc(T("menuGroup"))+'</span><div class="ctl"><select id="itGroup" aria-label="'+esc(T("menuGroup"))+'" disabled></select><button type="button" class="btn btn-ghost icon-btn sm" id="itNewGroup" title="'+esc(T("newGroup"))+'" aria-label="'+esc(T("newGroup"))+'">'+icon("i-plus")+'</button></div></div>'+
        '<label class="row"><span class="lbl">'+esc(T("partToggles"))+'</span><input type="checkbox" class="sw" id="itToggles" disabled></label>'+
        '<label class="row" id="itSyncRow"><span class="lbl">'+esc(T("blendshapeSync"))+'</span><input type="checkbox" class="sw" id="itSync"></label>'+
        '<div class="row col" id="itTags"></div>'+
        '<details class="more"><summary><span>'+esc(T("more"))+'</span>'+icon("i-down")+'</summary><div class="more-body">'+
          '<label class="row"><span class="lbl">'+esc(T("markCompatible"))+'</span><input type="checkbox" class="sw" id="itCompat"></label>'+
          '<div id="itHide" class="detail-hide"></div>'+
          '<div class="acts">'+(S&&S.ai?'<button type="button" class="btn sm" id="itAi">'+esc(T("aiName"))+'</button>':'')+'<button type="button" class="btn btn-ghost sm" id="itReport">'+esc(T("report"))+'</button></div>'+
          '<dl class="tech" id="itTech"></dl>'+
        '</div></details>'+
      '</div>'+
      '<div class="settle" id="itSettle" hidden><span class="sub" id="itSettleMsg"></span><button type="button" class="btn btn-ghost sm" id="itCancel">'+esc(T("cancel"))+'</button><button type="button" class="btn btn-primary sm" id="itApply">'+esc(T("apply"))+'</button></div>'+
    '</div></div>';
    wireItemHead();
    if(multi){
      var sel=$("itLook");
      [{id:"common",name:T("sharedItems")}].concat(presets()).forEach(function(p){sel.add(new Option(p.name,p.id));});
      sel.value=it.dest;sel.onchange=function(){it.dest=sel.value;loadMemberships();};
    }
    if(multiVariant){
      var strip=$("itStrip");
      variants.forEach(function(x,n){
        var b=document.createElement("span");b.className="vthumb";b.tabIndex=0;b.setAttribute("role","button");b.title=variantLabel(variants,x,n);b.setAttribute("aria-label",b.title);
        b.innerHTML='<span class="vimg"></span>';b.onclick=function(){if(n!==it.i)selectVariant(n);};b.onkeydown=function(e){if(e.key==="Enter"||e.key===" "){e.preventDefault();b.click();}};strip.appendChild(b);
        previews.observe(b.querySelector(".vimg"),x.guid,{priority:1,root:strip});
      });
      strip.addEventListener("wheel",function(e){if(Math.abs(e.deltaY)<=Math.abs(e.deltaX))return;var max=strip.scrollWidth-strip.clientWidth;if(max<=0)return;e.preventDefault();strip.scrollLeft+=e.deltaY;},{passive:false});
    }
    window.WardrobeOrganize.renderDetail($("itTags"),d);
    window.WardrobeOrganize.renderHide($("itHide"),d);
    $("itReport").onclick=function(){window.WardrobeReporting.openItem(d,it.v);};
    if($("itAi"))$("itAi").onclick=function(){aiName(this,d);};
    $("itSync").checked=stored("wardrobeFollowBody","1")!=="0";
    $("itSync").onchange=function(){R.store("wardrobeFollowBody",this.checked?"1":"0");};
    $("itNewGroup").onclick=newGroup;
    $("itCancel").onclick=function(){paintSettings(true);};
    $("itApply").onclick=applySettings;
    $("itToggles").onchange=function(){if(!it.memb)R.store("wardrobeGeneratePartToggles",this.checked?"1":"0");paintDirty();};
    $("itGroup").onchange=paintDirty;
    selectVariant(index);
  }
  function selectVariant(n){
    var it=item;if(!it||itemBusy)return;
    it.i=n;it.v=it.variants[n];
    var v=it.v,d=it.d;
    if(it.instance&&it.instance.guid!==v.guid)it.instance=null;
    R.text($("itVname"),variantLabel(it.variants,v,n));
    R.text($("itVcount"),it.variants.length>1?(n+1)+" / "+it.variants.length:"");
    var strip=$("itStrip");
    if(strip)Array.from(strip.children).forEach(function(b,k){
      b.classList.toggle("on",k===n);b.setAttribute("aria-pressed",String(k===n));
      var has=b.querySelector(".chip"),worn=it.variants[k].installed;
      if(worn&&!has){b.insertAdjacentHTML("afterbegin",chip(effectiveScope()));}else if(!worn&&has)has.remove();
      if(k===n&&(b.offsetLeft<strip.scrollLeft||b.offsetLeft+b.offsetWidth>strip.scrollLeft+strip.clientWidth))strip.scrollLeft=Math.max(0,b.offsetLeft-strip.clientWidth/2);
    });
    var shop=[v.shop||d.shop,v.product].filter(Boolean).join(" · ");
    $("itMeta").innerHTML=(shop?'<span>'+esc(shop)+'</span><span>·</span>':'')+'<span class="'+compatClass(v.compat)+'">'+(v.compat===0&&!/^\s*✓/.test(v.compatText||"")?"✓ ":"")+esc(v.compatText||"")+'</span>';
    if(v.explanation)$("itMeta").title=v.explanation;
    previews.bind($("itBig"),v.guid,{priority:0,detail:true,requireHi:true});
    $("itCompat").checked=!!v.compatibleOverride;$("itCompat").onchange=function(){compatOverride(this);};
    var rows=[[T("source"),v.source],[T("setup"),v.setup?T("yes"):T("no")],[T("confidence"),v.confidence||"—"]];
    if(v.mats&&v.mats.length)rows.push([T("mats"),v.mats.join(", ")]);
    if(v.parts&&v.parts.length)rows.push([T("parts"),v.parts.join(", ")]);
    $("itTech").innerHTML=rows.map(function(r){return '<dt>'+esc(r[0])+'</dt><dd>'+esc(r[1])+'</dd>';}).join("");
    loadMemberships();
  }
  // Which Looks wear this variant, and the menu groups of the destination Look.
  function loadMemberships(){
    var it=item;if(!it)return;
    var token=it.mtoken={},guid=it.v.guid;it.ready=false;it.memb=null;paintActions();
    $("itGroup").disabled=true;$("itToggles").disabled=true;
    Promise.all([api("/api/prefab_presets?guid="+encodeURIComponent(guid),{method:"GET"}),api("/api/menu_groups?id="+encodeURIComponent(it.dest),{method:"GET"})]).then(function(results){
      if(item!==it||it.mtoken!==token)return;
      var m=results[0],g=results[1];
      if(!m||!Array.isArray(m.presets))throw new Error(LT("detail.presetLoadFailed"));
      if(!g||!g.ok||!Array.isArray(g.groups))throw new Error(g&&g.message||LT("detail.groupsLoadFailed"));
      it.memberships=m.presets;it.memb=m.presets.find(function(p){return p.id===it.dest;})||null;it.groups=g.groups;
      var paths=it.memb?it.memb.paths||[]:[],assigned=paths.map(function(path){var grp=it.groups.find(function(x){return (x.paths||[]).indexOf(path)>=0;});return grp?grp.id:"";});
      var mixed=assigned.some(function(x){return x!==assigned[0];});
      it.savedGroup=mixed?"__mixed":assigned[0]||"";
      it.savedToggles=it.memb?!!it.memb.partToggles:stored("wardrobeGeneratePartToggles","0")==="1";
      it.savedMixed=!!(it.memb&&it.memb.partTogglesMixed);
      var sel=$("itGroup");
      sel.innerHTML='<option value="">'+esc(T("none"))+'</option>'+it.groups.map(function(x){return '<option value="'+esc(x.id)+'">'+esc(x.name)+'</option>';}).join("")+(mixed?'<option value="__mixed" disabled>'+esc(LT("detail.mixedGroups"))+'</option>':'');
      it.ready=true;paintSettings(true);
    }).catch(function(error){
      if(item!==it||it.mtoken!==token)return;
      it.ready=false;$("itActs").innerHTML='<span class="sub">'+esc(error.message)+'</span><button type="button" class="btn sm" id="itRetryM">'+esc(T("retry"))+'</button>';
      $("itRetryM").onclick=loadMemberships;
    });
  }
  function paintSettings(reset){
    var it=item;if(!it||!it.ready)return;
    if(reset){$("itGroup").value=it.savedGroup;var t=$("itToggles");t.checked=it.savedToggles;t.indeterminate=it.savedMixed;}
    $("itGroup").disabled=itemBusy;$("itToggles").disabled=itemBusy;
    $("itSyncRow").hidden=!!it.memb;
    paintActions();paintDirty();
  }
  function settingsDirty(){
    var it=item;if(!it||!it.ready)return false;
    var t=$("itToggles");return $("itGroup").value!==it.savedGroup||t.checked!==it.savedToggles||t.indeterminate!==it.savedMixed;
  }
  function paintDirty(){
    var it=item;if(!it)return;
    var dirty=!!it.memb&&settingsDirty();it.dirty=dirty;
    $("itSettle").hidden=!dirty;R.text($("itSettleMsg"),dirty?T("unsaved"):"");
    var acts=$("itActs");if(acts)acts.querySelectorAll("button").forEach(function(b){b.disabled=dirty||itemBusy||!it.ready;});
  }
  function paintActions(){
    var it=item,acts=$("itActs");if(!it||!acts)return;
    if(!it.ready){acts.innerHTML='<button type="button" class="btn btn-primary" disabled>'+spinner()+'<span>'+esc(T("wear"))+'</span></button>';return;}
    var wornFamily=installed.filter(function(x){return x.familyId===it.d.id&&(x.target||"common")===it.dest;});
    var others=wornFamily.filter(function(x){return x.guid!==it.v.guid;});
    if(it.memb){
      acts.innerHTML='<button type="button" class="btn btn-primary" id="itFit">'+esc(T("fit"))+'</button><button type="button" class="btn" id="itRemove">'+esc(T("remove"))+'</button><button type="button" class="btn icon-btn" id="itMore" aria-label="'+esc(T("wearOptions"))+'">'+icon("i-more")+'</button>';
      $("itFit").onclick=function(){openFit();};
      $("itRemove").onclick=function(){removeFlow(this);};
      $("itMore").onclick=function(){showMenu(this,[{label:T("addCopy"),act:function(){wear("copy");}}]);};
    }else{
      acts.innerHTML='<div class="split"><button type="button" class="btn btn-primary" id="itWear">'+esc(others.length?T("replaceV",others[0].variant||LT("variant.default")):T("wear"))+'</button>'+
        (others.length?'<button type="button" class="btn btn-primary caret" id="itWearMore" aria-label="'+esc(T("wearOptions"))+'">'+icon("i-down")+'</button>':'')+'</div>';
      $("itWear").onclick=function(){if(others.length)wear("replace",others[0].instanceId);else wear("wear");};
      if($("itWearMore"))$("itWearMore").onclick=function(){
        var items=others.map(function(o){return {label:T("replaceV",(o.variant||LT("variant.default"))+(others.length>1?" · "+String(o.path||"").split("/").pop():"")),act:function(){wear("replace",o.instanceId);}};});
        items.push("hr");items.push({label:T("addCopy"),act:function(){wear("copy");}});
        showMenu(this,items);
      };
    }
    paintDirty();
  }
  async function wear(mode,replaceId){
    var it=item;if(!it||!it.ready||itemBusy)return;
    var v=it.v,d=it.d,dest=it.dest,group=$("itGroup").value==="__mixed"?"":$("itGroup").value,toggles=$("itToggles").checked,sync=stored("wardrobeFollowBody","1")!=="0";
    R.store("wardrobeGeneratePartToggles",toggles?"1":"0");markItemUsed(d.id);
    var label=d.name+" · "+(v.variant||"");
    if(operations.enabled()){
      var input={variantId:v.guid,assetVersion:v.assetVersion,scopeId:dest,createToggles:toggles,followBody:sync,menuGroup:group,addCopy:mode==="copy"};
      if(mode==="replace")input.instanceId=String(replaceId);
      queueOutfit(mode==="replace"?"replace-outfit":"wear-outfit",input,label);return;
    }
    var button=$("itWear")||$("itMore");itemBusy=true;busyButton(button,true);paintDirty();
    try{
      var r=await api("/api/install?guid="+encodeURIComponent(v.guid)+"&allow=0&toggles="+(toggles?1:0)+"&switch="+(mode==="replace"?1:0)+"&copy="+(mode==="copy"?1:0)+
        "&replaceId="+encodeURIComponent(mode==="replace"?replaceId:"")+"&target="+encodeURIComponent(dest)+"&group="+encodeURIComponent(group)+"&sync="+(sync?1:0));
      if(!r||!r.ok)throw new Error(r&&r.message||LT("detail.install.fail"));
      toast(r.message,"ok");itemBusy=false;
      detailCache.clear();installedSignature="";await loadInstalled();loadCatalog(false,true);refreshState();
      if(item===it){it.selGuid=v.guid;var fresh=await api(familyUrl(d.id,effectiveScope()),{method:"GET"}).catch(function(){return null;});if(item===it&&fresh&&fresh.variants)renderItem(fresh);}
    }catch(error){toast(error.message||LT("detail.install.fail"),"err");}
    finally{itemBusy=false;if(item===it){busyButton(button,false);paintDirty();}}
  }
  function removeFlow(anchor){
    var it=item;if(!it||!it.memb)return;
    var ids=it.memb.instanceIds||[],paths=it.memb.paths||[];
    var copies=paths.length===ids.length?paths.map(function(p,i){return {path:p,id:Number(ids[i])};}).filter(function(c){return c.id;}):[];
    if(!copies.length){toast(LT("detail.chooseCopy"),"err");return;}
    function run(copy){
      if(!confirm(T("removeConfirm",it.d.name,multi?lookName(it.dest):LT("ui.avatar"))))return;
      doRemove(copy);
    }
    var preferred=it.instance&&copies.find(function(c){return c.id===it.instance.instanceId;});
    if(copies.length===1||preferred)run(preferred||copies[0]);
    else showMenu(anchor,copies.map(function(c,i){return {label:T("copyLabel",T("copyN",i+1),String(c.path).split("/").pop()),act:function(){run(c);}};}));
  }
  async function doRemove(copy){
    var it=item;if(!it)return;var v=it.v;
    if(operations.enabled()){queueOutfit("remove-outfit",{variantId:v.guid,assetVersion:v.assetVersion,scopeId:it.dest,instanceId:String(copy.id),itemPath:copy.path},it.d.name+" · "+v.variant);return;}
    var button=$("itRemove");itemBusy=true;busyButton(button,true);
    try{
      var r=await api("/api/preset_remove_item?guid="+encodeURIComponent(v.guid)+"&target="+encodeURIComponent(it.dest)+"&item="+encodeURIComponent(copy.path)+"&instanceId="+encodeURIComponent(copy.id));
      if(!r||!r.ok)throw new Error(r&&r.message||LT("detail.remove.fail"));
      toast(multi?r.message:LT("ui.outfit.removed"),"ok");itemBusy=false;closeItem(true);refreshAfterChange();
    }catch(error){toast(error.message||LT("detail.remove.fail"),"err");}
    finally{itemBusy=false;if(item===it){busyButton(button,false);paintDirty();}}
  }
  async function applySettings(){
    var it=item;if(!it||!it.memb||itemBusy||!settingsDirty())return;
    var group=$("itGroup").value,t=$("itToggles"),changeGroup=group!==it.savedGroup,changeToggles=t.checked!==it.savedToggles||t.indeterminate!==it.savedMixed;
    itemBusy=true;var button=$("itApply");busyButton(button,true);R.text($("itSettleMsg"),T("applying"));
    try{
      var r=await api("/api/item_settings?guid="+encodeURIComponent(it.v.guid)+"&target="+encodeURIComponent(it.dest)+(changeGroup?"&group="+encodeURIComponent(group):"")+(changeToggles?"&toggles="+(t.checked?1:0):""),{method:"POST"});
      if(!r||!r.ok)throw new Error(r&&r.message||LT("detail.settingsFailed"));
      if(changeGroup)it.savedGroup=group;
      if(changeToggles){it.savedToggles=t.checked;it.savedMixed=false;t.indeterminate=false;it.memb.partToggles=t.checked?1:0;it.memb.partTogglesMixed=0;}
      toast(T("saved"),"ok");refreshState();installedSignature="";loadInstalled();
    }catch(error){toast(error.message,"err");}
    finally{itemBusy=false;if(item===it){busyButton(button,false);paintSettings(false);}}
  }
  async function newGroup(){
    var it=item;if(!it||!it.ready||itemBusy)return;
    var name=prompt(T("groupName"));if(name==null||!name.trim())return;
    try{
      var r=await api("/api/menu_groups?id="+encodeURIComponent(it.dest)+"&op=save&name="+encodeURIComponent(name.trim()));
      if(!r||!r.ok)throw new Error(r&&r.message||LT("detail.groupCreateFailed"));
      if(item!==it)return;$("itGroup").add(new Option(name.trim(),r.id));$("itGroup").value=r.id;paintDirty();
    }catch(error){toast(error.message,"err");}
  }
  async function compatOverride(box){
    var it=item;if(!it||itemBusy){box.checked=!box.checked;return;}
    var enabled=box.checked,guid=it.v.guid;itemBusy=true;box.disabled=true;
    try{
      var r=await api("/api/compatibility_override?guid="+encodeURIComponent(guid)+"&enabled="+(enabled?1:0));
      if(!r||!r.ok)throw new Error(r&&r.message||LT("detail.install.fail"));
      it.v.compatibleOverride=enabled;detailCache.clear();loadCatalog(false,true);
    }catch(error){box.checked=!enabled;toast(error.message,"err");}
    finally{itemBusy=false;box.disabled=false;}
  }
  function aiName(button,d){
    if(!busyButton(button,true))return;
    api("/api/name?id="+encodeURIComponent(d.id)).then(function(r){
      if(!r||!r.ok||!r.job)throw new Error(r&&r.message||LT("detail.ai.fail"));
      return new Promise(function(resolve,reject){(function poll(){api("/api/nameResult?job="+encodeURIComponent(r.job)).then(function(q){
        if(q&&q.pending){setTimeout(poll,1000);return;}if(q&&q.ok)resolve(q);else reject(new Error(q&&q.message||LT("detail.ai.fail")));
      }).catch(reject);})();});
    }).then(function(q){toast(q.message,"ok");detailCache.clear();loadCatalog(false,true);openItem(d.id);})
      .catch(function(error){toast(error.message,"err");busyButton(button,false);});
  }

  /* ---------- Fit ---------- */
  var fit=null;
  function openFit(){
    var it=item;if(!it||!it.memb)return;
    if($("itemDlg").open)$("itemDlg").close();
    fit={it:it,guid:it.v.guid,dest:it.dest,name:it.d.name,variant:variantLabel(it.variants,it.v,it.i),data:null,rows:[],saved:"",toggles:it.savedToggles,togglesMixed:it.savedMixed,busy:false};
    var node=$("fit");node.hidden=false;
    node.innerHTML='<header class="fit-head"><button type="button" class="btn btn-ghost sm" id="fitBack">'+icon("i-back")+'<span>'+esc(T("wardrobe"))+'</span></button>'+(multi?tape(fit.dest):'')+
      '<span class="ttl">'+esc(fit.name)+' <span class="sub">'+esc(fit.variant)+'</span></span><span class="fit-status" id="fitStatus" role="status" aria-live="polite"></span>'+
      '<button type="button" class="btn btn-ghost" id="fitDiscard" disabled>'+esc(T("discard"))+'</button><button type="button" class="btn btn-primary" id="fitApply" disabled>'+esc(T("apply"))+'</button></header>'+
      '<div class="fit-body"><div class="big" id="fitBig"></div><div class="fit-main" id="fitMain"><div class="loading">'+spinner()+'<span>'+esc(T("loading"))+'</span></div></div></div>';
    previews.bind($("fitBig"),fit.guid,{priority:0,detail:true,requireHi:true});
    $("fitBack").onclick=function(){closeFit();};
    $("fitDiscard").onclick=function(){loadFit();};
    $("fitApply").onclick=applyFit;
    loadFit();
  }
  function closeFit(force){
    if(!fit){$("fit").hidden=true;return;}
    if(!force&&(fit.busy||(fitDirty()&&!confirm(T("discardChanges")))))return;
    fit=null;$("fit").hidden=true;$("fit").innerHTML="";
    if(item&&!$("itemDlg").open)$("itemDlg").showModal();
  }
  async function loadFit(){
    var f=fit;if(!f)return;
    try{
      var r=await api("/api/item_parts?guid="+encodeURIComponent(f.guid)+"&target="+encodeURIComponent(f.dest),{method:"GET"});
      if(fit!==f)return;
      if(!r||!r.ok)throw new Error(r&&r.message||LT("parts.loadFailed"));
      f.data=r;f.rows=[];
      r.parts.forEach(function(p){p.shapes.forEach(function(s){f.rows.push({mesh:s.mesh,shape:s.shape,value:Number(s.value)||0,mode:s.delete?"hide":"set",part:p.key,label:""});});});
      (r.sliders||[]).forEach(function(s){f.rows.push({mesh:s.mesh,shape:s.shape,value:Number(s.value)||0,mode:"slider",part:"",label:s.label||""});});
      f.saved=JSON.stringify(fitPayload());f.toggles=f.it.savedToggles;f.togglesMixed=f.it.savedMixed;f.savedToggles=f.toggles;
      paintFit();
    }catch(error){
      if(fit!==f)return;
      $("fitMain").innerHTML='<div class="loading"><span>'+esc(error.message)+'</span><button type="button" class="btn sm" id="fitRetry">'+esc(T("retry"))+'</button></div>';
      $("fitRetry").onclick=loadFit;
    }
  }
  function fitMeshes(mode){var d=fit.data;return mode==="slider"?d.meshes.concat(d.itemMeshes||[]):d.meshes;}
  function fitAllMeshes(){return fit.data.meshes.concat(fit.data.itemMeshes||[]);}
  function fitPayload(){
    var d=fit.data,parts=d.parts.map(function(p){return {key:p.key,worn:p.worn?1:0,shapes:[]};}),sliders=[];
    fit.rows.filter(function(r){return r.shape;}).forEach(function(r){
      if(r.mode==="slider"){sliders.push({mesh:r.mesh,shape:r.shape,label:r.label||"",value:r.value});return;}
      var part=parts.find(function(p){return p.key===r.part;})||parts[0];
      part.shapes.push({mesh:r.mesh,shape:r.shape,value:r.mode==="hide"?0:r.value,delete:r.mode==="hide"?1:0});
    });
    return {sync:d.sync?1:0,syncMesh:"",parts:parts,sliders:sliders};
  }
  function fitDirty(){return !!(fit&&fit.data)&&(JSON.stringify(fitPayload())!==fit.saved||fit.toggles!==fit.savedToggles);}
  function paintFitDirty(){var d=fitDirty();$("fitApply").disabled=!d||fit.busy;$("fitDiscard").disabled=!d||fit.busy;if(d)R.text($("fitStatus"),T("unsaved"));else if(!fit.busy)R.text($("fitStatus"),"");}
  function opt(value,text,selected){return '<option value="'+esc(value)+'"'+(selected?" selected":"")+'>'+esc(text)+'</option>';}
  function paintFit(){
    var f=fit,d=f.data,named=d.parts.filter(function(p){return p.key;}),all=fitAllMeshes(),multiMesh=all.length>1;
    var radials=f.rows.filter(function(r){return r.mode==="slider";}).length;
    var lists=all.map(function(m,i){return '<datalist id="fitShapes'+i+'">'+m.shapes.map(function(n){return '<option value="'+esc(n)+'">';}).join("")+'</datalist>';}).join("");
    function listFor(path){var i=all.findIndex(function(m){return m.path===path;});return "fitShapes"+Math.max(0,i);}
    function row(r,i){
      var slider=r.mode==="slider",hide=r.mode==="hide",meshes=fitMeshes(r.mode);
      return '<tr data-row="'+i+'">'+
        '<td><input data-f="shape" list="'+listFor(r.mesh)+'" value="'+esc(r.shape)+'" placeholder="Shrink_Chest" aria-label="'+esc(T("blendshape"))+'"></td>'+
        (multiMesh?'<td>'+(meshes.length>1?'<select data-f="mesh" aria-label="'+esc(T("mesh"))+'">'+meshes.map(function(m){return opt(m.path,m.name,m.path===r.mesh);}).join("")+'</select>':'<span class="sub">'+esc((meshes[0]||{}).name||"")+'</span>')+'</td>':'')+
        '<td><select data-f="mode" aria-label="'+esc(T("mode"))+'">'+opt("set",T("modeSet"),r.mode==="set")+opt("hide",T("modeDelete"),hide)+opt("slider",T("modeRadial"),slider)+'</select></td>'+
        '<td class="v">'+(hide?'<span class="sub">—</span>':'<div class="val"><input type="range" min="0" max="100" step="1" data-f="value" value="'+Math.round(r.value)+'" aria-label="'+esc(T("value"))+'"><output>'+Math.round(r.value)+'</output></div>')+'</td>'+
        '<td>'+(slider?'<input data-f="label" value="'+esc(r.label)+'" maxlength="40" placeholder="'+esc(T("radialName"))+'" aria-label="'+esc(T("radialName"))+'">':
          '<select data-f="part" aria-label="'+esc(T("whileW"))+'">'+opt("",T("whileWorn"),!r.part)+named.map(function(p){return opt(p.key,p.name,p.key===r.part);}).join("")+'</select>')+'</td>'+
        '<td class="x"><button type="button" class="btn btn-ghost icon-btn sm" data-a="remove" aria-label="'+esc(T("remove"))+'">'+icon("i-x")+'</button></td></tr>';
    }
    var hasMesh=d.meshes.length||(d.itemMeshes||[]).length;
    $("fitMain").innerHTML=lists+
      (named.length?'<section class="fit-sec"><h2><span>'+esc(T("parts"))+'</span><label>'+esc(T("partToggles"))+'<input type="checkbox" class="sw" id="fitToggles"></label></h2><div class="parts">'+named.map(function(p){
        var i=d.parts.indexOf(p);return '<label class="part"><input type="checkbox" data-part="'+i+'"'+(p.worn?" checked":"")+'><span>'+esc(p.name)+'</span></label>';
      }).join("")+'</div></section>':'<section class="fit-sec"><h2><span>'+esc(T("partToggles"))+'</span><label><input type="checkbox" class="sw" id="fitToggles"></label></h2></section>')+
      (d.meshes.length?'<section class="fit-sec"><h2><span>'+esc(T("blendshapeSync"))+(d.syncCandidates?' <span class="sub">'+d.syncCandidates+'</span>':'')+'</span><label><input type="checkbox" class="sw" id="fitSync"'+(d.sync?" checked":"")+'></label></h2></section>':'')+
      '<section class="fit-sec"><h2><span>'+esc(T("shapeChanges"))+'</span></h2>'+
        (hasMesh?(f.rows.length?'<table class="shape-table"><thead><tr><th>'+esc(T("blendshape"))+'</th>'+(multiMesh?'<th>'+esc(T("mesh"))+'</th>':'')+'<th>'+esc(T("mode"))+'</th><th>'+esc(T("value"))+'</th><th>'+esc(T("whileW"))+'</th><th></th></tr></thead><tbody>'+f.rows.map(row).join("")+'</tbody></table>':'')+
          '<div class="shape-foot"><button type="button" class="btn" data-a="add">'+icon("i-plus")+'<span>'+esc(T("add"))+'</span></button><span class="sub">'+(radials?esc(T(radials===1?"radial1":"radials",radials,radials*8)):'')+(d.copies>1?(radials?" · ":"")+esc(T("copies",d.copies)):'')+'</span></div>'
        :'<p class="sub">'+esc(T("noMesh"))+'</p>')+
      '</section>';
    var toggles=$("fitToggles");if(toggles){toggles.checked=f.toggles;toggles.indeterminate=f.togglesMixed&&f.toggles===f.savedToggles;}
    paintFitDirty();
  }
  $("fit").addEventListener("input",function(event){
    var f=fit,t=event.target;if(!f||!f.data)return;
    if(t.id==="fitSync"){f.data.sync=t.checked?1:0;paintFitDirty();return;}
    if(t.id==="fitToggles"){f.toggles=t.checked;paintFitDirty();return;}
    if(t.dataset.part!=null){f.data.parts[Number(t.dataset.part)].worn=t.checked?1:0;paintFitDirty();return;}
    var tr=t.closest("tr[data-row]");if(!tr)return;var r=f.rows[Number(tr.dataset.row)],field=t.dataset.f;
    if(field==="value"){r.value=Number(t.value);tr.querySelector("output").textContent=Math.round(r.value);}
    else if(field==="shape")r.shape=t.value.trim();
    else if(field==="label")r.label=t.value;
    paintFitDirty();
  });
  $("fit").addEventListener("change",function(event){
    var f=fit,t=event.target,tr=t.closest("tr[data-row]");if(!f||!f.data||!tr)return;
    var r=f.rows[Number(tr.dataset.row)],field=t.dataset.f;
    if(field==="mode"){r.mode=t.value;if(r.mode==="slider")r.part="";if(!fitMeshes(r.mode).some(function(m){return m.path===r.mesh;}))r.mesh=fitMeshes(r.mode)[0].path;}
    else if(field==="mesh")r.mesh=t.value;
    else if(field==="part")r.part=t.value;
    else return;
    paintFit();var again=$("fitMain").querySelector('tr[data-row="'+tr.dataset.row+'"] [data-f="'+field+'"]');if(again)again.focus();
  });
  $("fit").addEventListener("click",function(event){
    var f=fit,b=event.target.closest("[data-a]");if(!f||!f.data||!b)return;
    if(b.dataset.a==="add"){
      var first=f.data.meshes[0]||(f.data.itemMeshes||[])[0];if(!first)return;
      f.rows.push({mesh:first.path,shape:"",value:f.data.meshes.length?100:50,mode:f.data.meshes.length?"set":"slider",part:"",label:""});
      paintFit();var inputs=$("fitMain").querySelectorAll('[data-f="shape"]');if(inputs.length)inputs[inputs.length-1].focus();
    }else if(b.dataset.a==="remove"){f.rows.splice(Number(b.closest("tr").dataset.row),1);paintFit();}
  });
  async function applyFit(){
    var f=fit;if(!f||!fitDirty()||f.busy)return;
    f.busy=true;paintFitDirty();R.text($("fitStatus"),T("applying"));
    var partsChanged=JSON.stringify(fitPayload())!==f.saved,togglesChanged=f.toggles!==f.savedToggles;
    try{
      if(partsChanged){
        var r=await api("/api/item_parts?op=save&guid="+encodeURIComponent(f.guid)+"&target="+encodeURIComponent(f.dest)+"&data="+encodeURIComponent(JSON.stringify(fitPayload())),{method:"POST"});
        if(!r||!r.ok)throw new Error(r&&r.message||LT("parts.saveFailed"));
      }
      if(togglesChanged){
        var s=await api("/api/item_settings?guid="+encodeURIComponent(f.guid)+"&target="+encodeURIComponent(f.dest)+"&toggles="+(f.toggles?1:0),{method:"POST"});
        if(!s||!s.ok)throw new Error(s&&s.message||LT("detail.settingsFailed"));
        if(f.it.memb){f.it.memb.partToggles=f.toggles?1:0;f.it.memb.partTogglesMixed=0;}f.it.savedToggles=f.toggles;f.it.savedMixed=false;
      }
      if(fit!==f)return;
      f.busy=false;R.text($("fitStatus"),T("saved"));toast(T("saved"),"ok");refreshState();installedSignature="";loadInstalled();
      await loadFit();
    }catch(error){if(fit===f){f.busy=false;paintFitDirty();R.text($("fitStatus"),error.message);}}
  }

  /* ---------- Looks gallery ---------- */
  var batchState=null,batchStateAt=0;
  function loadBatchState(force){
    if(!force&&batchState&&Date.now()-batchStateAt<8000)return Promise.resolve(batchState);
    return api("/api/batch_state",{method:"GET",timeout:30000}).then(function(d){if(d&&d.ok){batchState=d;batchStateAt=Date.now();if(multi)paintRail();}return batchState;}).catch(function(){return batchState;});
  }
  function lookMenu(anchor,id){
    showMenu(anchor,[{label:T("thumbnail"),act:function(){loadBatchState(true).then(function(){openThumb(id);});}},{label:T("rename"),act:function(){
      var name=prompt(T("lookName"),lookName(id));if(name==null||!name.trim())return;
      api("/api/preset_save?id="+encodeURIComponent(id)+"&name="+encodeURIComponent(name.trim())).then(function(r){if(!r||!r.ok)throw new Error(r&&r.message||LT("ui.rename.failed"));return refreshState();}).catch(function(e){toast(e.message,"err");});
    }},"hr",{label:T("del"),danger:true,act:function(){
      if(!confirm(T("deleteLook",lookName(id))))return;
      api("/api/preset_delete?id="+encodeURIComponent(id)).then(function(r){if(!r||!r.ok)throw new Error(r&&r.message||LT("detail.install.fail"));toast(r.message,"ok");picked.delete(id);savePicked();return refreshState();}).catch(function(e){toast(e.message,"err");});
    }}]);
  }

  /* ---------- Upload looks: review, queue, result ---------- */
  var upload={running:false,step:"review",ids:[],rows:[],picked:new Set(),consent:false,job:"",q:{},timer:0,message:"",results:null,snapshot:""};
  function reviewRows(state,ids){
    return ids.map(function(id){
      var p=(state.presets||[]).find(function(x){return x.id===id;}),error="";
      if(!p)error=T("missing");
      else{
        var platforms=[p.win?"Windows":"",p.and?"Android":"",p.ios?"iOS":""].filter(Boolean),bp=String(p.blueprintId||"").trim();
        if(!platforms.length)error=T("noPlatform");
        else if(bp&&!/^avtr_[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(bp))error=T("invalidId");
      }
      return {id:id,name:p?p.name:lookName(id),error:error,platforms:p?[p.win?"Windows":"",p.and?"Android":"",p.ios?"iOS":""].filter(Boolean):[],blueprint:p?String(p.blueprintId||"").trim():""};
    });
  }
  function uploadBlocker(){
    if(R.pendingWrites()>0||pendingOps()>0)return T("blockPending");
    if(S&&S.sdkUploadReady===false)return T("blockSdk");
    return "";
  }
  async function openUpload(ids){
    var dlg=$("uploadDlg");
    if(!dlg.open)dlg.showModal();
    if(upload.running){upload.step="queue";paintUpload();return;}
    dlg.innerHTML='<div class="dlg-head"><h2 id="uploadHeading">'+esc(T("upload"))+'</h2></div><div class="dlg-body"><div class="loading">'+spinner()+'<span>'+esc(T("checking"))+'</span></div></div>';
    var state=await loadBatchState(true);
    if(!state){dlg.querySelector(".dlg-body").innerHTML='<p class="up-msg warn-t">'+esc(T("loadError"))+'</p>';return;}
    // A job may already be running (another tab, or a reload). Follow it; never resend.
    var active=await api("/api/batch_job",{method:"GET"}).catch(function(){return null;});
    if(active&&active.job&&!active.done){followJob(active.job,[]);return;}
    if(!ids)ids=(state.presets||[]).filter(function(p){return p.include;}).map(function(p){return p.id;});
    if(!ids.length)ids=(state.presets||[]).map(function(p){return p.id;});
    upload.ids=ids;upload.rows=reviewRows(state,ids);upload.picked=new Set(upload.rows.filter(function(r){return !r.error;}).map(function(r){return r.id;}));
    upload.release=state.defaults&&state.defaults.release==="public"?"public":"private";
    upload.step="review";upload.consent=false;upload.message="";upload.results=null;
    paintUpload();
  }
  function rowThumb(id){var it=scopedItems(id)[0];return it?it.guid:"";}
  function paintUpload(){
    var dlg=$("uploadDlg"),u=upload,html="";
    function thumb(id){return '<span class="thumb-s" data-thumbid="'+esc(rowThumb(id))+'"></span>';}
    if(u.step==="review"){
      var n=u.picked.size,blocker=uploadBlocker()||u.message;
      html='<div class="dlg-head"><h2 id="uploadHeading">'+esc(u.ids.length===1?T("uploadLook"):T("uploadLooks",u.ids.length))+'</h2><button type="button" class="btn sm btn-ghost" id="upThumbDef">'+esc(T("thDefaultTitle"))+'</button></div><div class="dlg-body">'+
        u.rows.map(function(r){
          var status=r.error||[r.blueprint?T("updateU"):T("newU")].concat(r.platforms.length?[r.platforms.join(", ")]:[]).join(" · ");
          return '<label class="up-row'+(r.error?" err":"")+'"><input type="checkbox" data-id="'+esc(r.id)+'"'+(u.picked.has(r.id)?" checked":"")+(r.error?" disabled":"")+'>'+(r.error?thumb(r.id):thumbWell(r.id,true))+tape(r.id,r.name)+'<span class="st">'+esc(status)+'</span></label>';
        }).join("")+
        '<label class="consent"><input type="checkbox" id="upConsent"'+(u.consent?" checked":"")+'><span><strong>'+esc(T("rights"))+'</strong><span class="sub">'+esc(T("rightsSub"))+'</span></span></label>'+
        (blocker?'<p class="up-msg warn-t" role="status">'+esc(blocker)+'</p>':'')+
        '</div><div class="dlg-foot"><span class="sub grow">'+esc(T(u.release))+'</span><button type="button" class="btn btn-ghost" id="upCancel">'+esc(T("cancel"))+'</button><button type="button" class="btn btn-primary" id="upGo"'+(!n||!u.consent||uploadBlocker()?" disabled":"")+'>'+esc(T("uploadN",n))+'</button></div>';
    }else{
      var q=u.q||{},total=q.total||u.order.length||1,index=q.index||0,doneAll=!!q.done;
      var rows=u.order.length?u.order:[{id:"",name:q.current||T("working")}];
      var okCount=doneAll?(u.results?rows.filter(function(r){return u.results[r.id]!=="failed";}).length:(q.ok?rows.length:0)):Math.min(index,rows.length);
      html='<div class="dlg-head"><h2 id="uploadHeading">'+esc(doneAll?T("resultN",okCount,rows.length):T("uploadingN",Math.min(index+1,total),total))+'</h2></div><div class="dlg-body">'+
        rows.map(function(r,i){
          var st,right;
          if(doneAll){var failed=u.results?u.results[r.id]==="failed":!q.ok;st=failed?T("failed"):T("done");right='<span class="st'+(failed?" warn-t":" ok-t")+'">'+esc(st)+'</span>';}
          else if(i<index)right='<span class="st ok-t">'+esc(T("done"))+'</span>';
          else if(i===index){var p=Number(q.uploadProgress),known=Number.isFinite(p)&&p>=0;right='<span class="st">'+esc(q.stage||T("working"))+'</span><div class="bar'+(known?"":" indet")+'"><div style="width:'+(known?Math.round(Math.min(1,p)*100):0)+'%"></div></div>';}
          else right='<span class="st">'+esc(T("queued"))+'</span>';
          return '<div class="up-row">'+(r.id?thumb(r.id)+tape(r.id,r.name):'<strong>'+esc(r.name)+'</strong>')+right+'</div>';
        }).join("")+
        (doneAll&&q.message?'<details><summary class="sub">'+esc(T("log"))+'</summary><pre class="log">'+esc(q.message)+'</pre></details>':'')+
        '</div><div class="dlg-foot"><span class="grow"></span>'+
        (doneAll?((u.results&&Object.values(u.results).indexOf("failed")>=0)||!q.ok?'<button type="button" class="btn" id="upRetry">'+esc(T("retry"))+'</button>':'')+'<button type="button" class="btn btn-primary" id="upClose">'+esc(T("close"))+'</button>'
          :'<button type="button" class="btn btn-ghost" id="upHide">'+esc(T("close"))+'</button><button type="button" class="btn btn-danger" id="upCancelAll">'+esc(T("cancelAll"))+'</button>')+'</div>';
    }
    dlg.innerHTML=html;
    dlg.querySelectorAll("[data-thumbid]").forEach(function(n){if(n.dataset.thumbid)previews.observe(n,n.dataset.thumbid,{priority:2});});
    bindCrops(dlg);
    dlg.querySelectorAll("[data-thumb-look]").forEach(function(b){b.onclick=function(e){e.preventDefault();e.stopPropagation();openThumb(b.dataset.thumbLook);};});
    if($("upThumbDef"))$("upThumbDef").onclick=function(){openThumb("default");};
    var go=$("upGo");
    if(u.step==="review"){
      dlg.querySelectorAll("input[data-id]").forEach(function(box){box.onchange=function(){if(box.checked)u.picked.add(box.dataset.id);else u.picked.delete(box.dataset.id);paintUpload();};});
      $("upConsent").onchange=function(){u.consent=this.checked;paintUpload();};
      $("upCancel").onclick=function(){dlg.close();};
      go.onclick=commitUpload;
    }else{
      if($("upClose"))$("upClose").onclick=function(){dlg.close();upload.step="review";upload.q={};};
      if($("upHide"))$("upHide").onclick=function(){dlg.close();};
      if($("upCancelAll"))$("upCancelAll").onclick=function(){var b=this;b.disabled=true;api("/api/batch_cancel").then(function(r){if(r&&r.message)toast(r.message,r.ok?"ok":"err");}).catch(function(e){toast(e.message,"err");b.disabled=false;});};
      if($("upRetry"))$("upRetry").onclick=function(){
        var b=this;b.disabled=true;
        api("/api/batch_retry",{timeout:120000}).then(function(r){if(!r||!r.ok||!r.job)throw new Error(r&&r.message||T("failed"));followJob(r.job,u.order.filter(function(x){return !u.results||u.results[x.id]==="failed";}));})
          .catch(function(e){toast(e.message,"err");b.disabled=false;});
      };
    }
  }
  async function commitUpload(){
    var u=upload,button=$("upGo");if(!button||button.disabled)return;
    var blocker=uploadBlocker();if(blocker){u.message=blocker;paintUpload();return;}
    busyButton(button,true);
    var ids=u.ids.filter(function(id){return u.picked.has(id);});
    var before=JSON.stringify(u.rows.filter(function(r){return u.picked.has(r.id);}));
    // Read again at commitment: never upload a changed Look using an old review.
    var state=await loadBatchState(true);
    if(!state){busyButton(button,false);u.message=T("loadError");paintUpload();return;}
    if(state.batchActive){busyButton(button,false);u.message=T("blockBusy");paintUpload();return;}
    var rows=reviewRows(state,u.ids),now=JSON.stringify(rows.filter(function(r){return u.picked.has(r.id);}));
    if(now!==before){u.rows=rows;rows.forEach(function(r){if(r.error)u.picked.delete(r.id);});u.message=T("changedReview");paintUpload();return;}
    var order=rows.filter(function(r){return u.picked.has(r.id);}).map(function(r){return {id:r.id,name:r.name};});
    var requestId=crypto.randomUUID().replace(/-/g,"");
    u.order=order;u.step="queue";u.q={index:0,total:order.length};u.running=true;u.results=null;paintUpload();paintWearing();paintStatus();
    api("/api/batch_upload_presets?ids="+encodeURIComponent(ids.join("\n"))+"&requestId="+encodeURIComponent(requestId),{timeout:120000}).then(function(r){
      if(!r||!r.ok||!r.job)throw new Error(r&&r.message||T("failed"));
      picked.clear();savePicked();paintRail();
      followJob(r.job,order);
    }).catch(function(error){
      // Recover status only; never resend the upload command.
      if(error&&error.status){finishJob({done:1,ok:0,message:error.message});return;}
      followJob(requestId,order,true);
    });
  }
  function followJob(job,order,recovering){
    var u=upload;clearInterval(u.timer);
    u.job=job;u.running=true;u.step="queue";if(order&&order.length)u.order=order;else if(!u.order)u.order=[];
    if(!recovering)u.q=u.q&&u.q.index!=null?u.q:{index:0,total:u.order.length};
    var polling=false;
    function poll(){
      if(polling)return;polling=true;
      api("/api/batch_job?job="+encodeURIComponent(job),{method:"GET",timeout:15000}).then(function(q){
        if(upload.job!==job||!q)return;
        if(recovering&&!q.done&&q.job!==job)return;
        u.q=q;if(q.done){finishJob(q);return;}
        if($("uploadDlg").open)paintUpload();paintWearing();
      }).catch(function(){}).finally(function(){polling=false;});
    }
    u.timer=setInterval(poll,recovering?5000:2000);poll();
    if($("uploadDlg").open)paintUpload();paintWearing();paintStatus();
  }
  async function finishJob(q){
    var u=upload;clearInterval(u.timer);u.running=false;u.q=Object.assign({},q,{done:1});
    var state=await loadBatchState(true),failed=new Set(((state&&state.failed)||[]).map(function(f){return f.outfit;}));
    u.results={};(u.order||[]).forEach(function(r){u.results[r.id]=failed.has(r.name)||(!q.ok&&!failed.size)?"failed":"done";});
    toast(q.ok?LT("upload.complete"):(q.message||LT("upload.finishedErrors")),q.ok?"ok":"err");
    u.step="result";if($("uploadDlg").open)paintUpload();
    paintWearing();paintStatus();refreshState();
  }
  // Reattach to a server-owned upload after a reload.
  function reconnectUpload(){
    api("/api/batch_job",{method:"GET"}).then(function(q){if(q&&q.job&&!q.done&&!upload.running)followJob(q.job,[]);}).catch(function(){});
  }

  /* ---------- Look thumbnails: source (Auto · Camera · Image) + update (Replace · Keep) ----------
     Plans live in Unity (Upload/OutfitThumbnails.cs). A Look without its own plan follows the default.
     Image crops render client-side here with the same math Unity uses at upload time. */
  var thumbPreviews=new Map(),thumbState=null;
  function batchPreset(id){return batchState&&(batchState.presets||[]).find(function(p){return p.id===id;});}
  function cropWindow(w,h,zoom,cx,cy){
    zoom=Math.min(8,Math.max(1,Number(zoom)||1));var a=w/Math.max(1,h),f=4/3,u,v;
    if(a>f){u=f/a;v=1;}else{u=1;v=a/f;}u/=zoom;v/=zoom;
    cx=Math.min(1-u/2,Math.max(u/2,isFinite(cx)?cx:.5));cy=Math.min(1-v/2,Math.max(v/2,isFinite(cy)?cy:.5));
    return {u:u,v:v,cx:cx,cy:cy,zoom:zoom};
  }
  function placeCrop(img,crop){
    var w=img.naturalWidth,h=img.naturalHeight;if(!w||!h)return null;
    var c=cropWindow(w,h,crop.zoom,crop.cx,crop.cy);
    img.style.width=(100/c.u)+"%";img.style.height=(100/c.v)+"%";
    img.style.left=(50-c.cx*100/c.u)+"%";img.style.top=(50-c.cy*100/c.v)+"%";
    return c;
  }
  function originalUrl(id,key){return "/api/look_thumb_original?id="+encodeURIComponent(id)+"&v="+encodeURIComponent(key||"");}
  function cropImg(id,key,crop){
    return '<img class="crop" alt="" draggable="false" data-cx="'+crop.cx+'" data-cy="'+crop.cy+'" data-zoom="'+crop.zoom+'" src="'+esc(originalUrl(id,key))+'">';
  }
  function bindCrops(root){
    root.querySelectorAll("img.crop").forEach(function(img){
      var apply=function(){placeCrop(img,{cx:Number(img.dataset.cx),cy:Number(img.dataset.cy),zoom:Number(img.dataset.zoom)});img.classList.add("ready");};
      if(img.complete&&img.naturalWidth)apply();else img.onload=apply;
    });
  }
  function thumbWell(id,interactive){
    var p=batchPreset(id)||{},tok=thumbPreviews.get(id),inner;
    if(p.thumbSource==="image"&&p.thumbOriginal)inner=cropImg(id,p.thumbOriginal,{cx:p.thumbCropX,cy:p.thumbCropY,zoom:p.thumbCropZoom});
    else if(tok)inner='<img alt="" src="/api/batch_thumb_img?token='+encodeURIComponent(tok)+'">';
    else inner=icon(p.thumbSource==="camera"?"i-camera":"i-portrait","glyph");
    var kept=p.thumbUpdate!=="always"&&String(p.blueprintId||"").trim();
    var tag=interactive?'button type="button" data-thumb-look="'+esc(id)+'" title="'+esc(T("thumbnail"))+'" aria-label="'+esc(T("thumbnail"))+'"':'span';
    return '<'+tag+' class="thumb-w">'+inner+(kept?'<span class="lock" title="'+esc(T("thKeep"))+'">'+icon("i-lock")+'</span>':'')+'</'+(interactive?'button':'span')+'>';
  }
  async function prepareThumbImage(file){
    if(!file||!/^image\//.test(file.type||""))throw new Error(T("thImageType"));
    var bmp=await createImageBitmap(file),max=2400,s=Math.min(1,max/Math.max(bmp.width,bmp.height));
    var w=Math.max(1,Math.round(bmp.width*s)),h=Math.max(1,Math.round(bmp.height*s)),c=document.createElement("canvas");
    c.width=w;c.height=h;var g=c.getContext("2d");g.fillStyle="#000";g.fillRect(0,0,w,h);g.imageSmoothingQuality="high";g.drawImage(bmp,0,0,w,h);
    if(bmp.close)bmp.close();
    return await new Promise(function(resolve,reject){c.toBlob(function(b){if(b)resolve(b);else reject(new Error(T("thImageType")));},"image/jpeg",0.92);});
  }
  function thumbChanged(id){
    if(id==="default")thumbPreviews.clear();else thumbPreviews.delete(id);
    return loadBatchState(true).then(function(){if($("uploadDlg").open&&upload.step==="review")paintUpload();});
  }
  async function openThumb(id){
    var dlg=$("thumbDlg");
    thumbState={id:id,d:null,busy:"",note:"",crop:null,saveTimer:0};
    if(!dlg.open)dlg.showModal();
    dlg.innerHTML='<div class="dlg-head"><h2 id="thumbHeading">'+esc(T("thumbnail"))+'</h2></div><div class="dlg-body"><div class="loading">'+spinner()+'<span>'+esc(T("loading"))+'</span></div></div>';
    await reloadThumb();
  }
  async function reloadThumb(){
    var th=thumbState;if(!th)return;
    var d=await api("/api/look_thumb?id="+encodeURIComponent(th.id),{method:"GET"}).catch(function(e){return {ok:0,message:e.message};});
    if(thumbState!==th)return;
    if(!d||!d.ok){$("thumbDlg").querySelector(".dlg-body").innerHTML='<p class="up-msg warn-t">'+esc((d&&d.message)||T("loadError"))+'</p>';return;}
    th.d=d;th.crop={cx:d.cropX,cy:d.cropY,zoom:d.cropZoom};paintThumb();
  }
  function thumbSet(fields){
    var th=thumbState,q="id="+encodeURIComponent(th.id);
    Object.keys(fields).forEach(function(k){q+="&"+k+"="+encodeURIComponent(fields[k]);});
    return api("/api/look_thumb_set?"+q).then(function(r){if(!r||!r.ok)throw new Error(r&&r.message||T("failed"));return thumbChanged(th.id);});
  }
  function thumbAct(label,work){
    var th=thumbState;if(!th||th.busy)return;th.busy=label;th.note="";paintThumb();
    Promise.resolve().then(work).then(function(){if(thumbState===th){th.busy="";return reloadThumb();}})
      .catch(function(e){if(thumbState===th){th.busy="";th.note=e.message;paintThumb();}else toast(e.message,"err");});
  }
  function uploadThumbFile(file){
    var th=thumbState;
    thumbAct("upload",async function(){
      var blob=await prepareThumbImage(file);
      var r=await api("/api/look_thumb_upload?id="+encodeURIComponent(th.id),{method:"POST",body:blob,headers:{"Content-Type":"application/octet-stream"},timeout:60000});
      if(!r||!r.ok)throw new Error(r&&r.message||T("failed"));
      await thumbChanged(th.id);
    });
  }
  function paintThumb(){
    var th=thumbState,dlg=$("thumbDlg");if(!th||!th.d||!dlg.open)return;
    var d=th.d,isDef=th.id==="default",inherit=!isDef&&d.inherited,src=d.source,tok=thumbPreviews.get(th.id),busy=!!th.busy;
    var stage;
    if(src==="image"&&d.original)stage='<img class="crop" id="thImg" alt="" draggable="false" src="'+esc(originalUrl(th.id,d.original))+'">';
    else if(src==="image")stage='<div class="th-empty">'+icon("i-image","glyph")+'<button type="button" class="btn sm" id="thPick2"'+(busy||inherit?" disabled":"")+'>'+esc(T("thChoose"))+'</button></div>';
    else if(tok)stage='<img alt="" src="/api/batch_thumb_img?token='+encodeURIComponent(tok)+'">';
    else stage='<div class="th-empty">'+icon(src==="camera"?"i-camera":"i-portrait","glyph")+'</div>';
    if(th.busy==="render"||th.busy==="upload")stage='<div class="th-empty">'+spinner()+'</div>';
    function seg(name,opts,cur,disabled){return '<div class="seg" role="group" data-seg="'+name+'">'+opts.map(function(o){return '<button type="button" data-v="'+o[0]+'" class="'+(o[0]===cur?"on":"")+'" aria-pressed="'+(o[0]===cur)+'"'+(disabled?" disabled":"")+'>'+esc(o[1])+'</button>';}).join("")+'</div>';}
    var sources=[["auto",T("auto")],["camera",T("thCamera")],["image",T("thImage")]];
    if(!isDef)sources.unshift(["",T("thDefault")]);
    var cams=(d.cameras||[]),camOpts=(d.camera?"":'<option value="">'+esc(T("none"))+'</option>')+cams.map(function(c){var sel=c.id&&c.id===d.camera;return '<option value="'+esc(c.reference)+'"'+(sel?" selected":"")+'>'+esc(c.path)+'</option>';}).join("");
    if(d.camera&&!cams.some(function(c){return c.id===d.camera;}))camOpts='<option value="" selected>'+esc(T("missing"))+'</option>'+camOpts;
    var html='<div class="dlg-head"><h2 id="thumbHeading">'+esc(isDef?T("thDefaultTitle"):T("thumbnail"))+'</h2>'+(isDef?'':tape(th.id,d.name||lookName(th.id)))+'</div><div class="dlg-body">'+
      '<div class="th-stage'+(src==="image"&&d.original&&!inherit?" pan":"")+'" id="thStage">'+stage+'</div>'+
      (src==="image"&&d.original&&!inherit?'<div class="row"><span class="lbl">'+esc(T("thZoom"))+'</span><span class="ctl grow"><input type="range" id="thZoom" min="1" max="4" step="0.01" value="'+Math.min(4,th.crop.zoom||1)+'"'+(busy?" disabled":"")+'></span></div>':'')+
      '<div class="rows">'+
      '<div class="row"><span class="lbl">'+esc(T("source"))+'</span><span class="ctl">'+seg("source",sources,inherit?"":src,busy)+'</span></div>'+
      (inherit?'<div class="row"><span class="lbl sub">'+esc(T("thFollows",T(d.defSource==="camera"?"thCamera":d.defSource==="image"?"thImage":"auto"),T(d.defUpdate==="always"?"thReplace":"thKeep")))+'</span><span class="ctl"><button type="button" class="btn sm btn-ghost" id="thEditDef">'+esc(T("thEditDefault"))+'</button></span></div>':
       (src==="camera"?'<div class="row"><span class="lbl">'+esc(T("thCamera"))+'</span><span class="ctl"><select id="thCam"'+(busy?" disabled":"")+'>'+camOpts+'</select></span></div>'+
         '<div class="row"><span class="lbl"></span><span class="ctl"><button type="button" class="btn sm" id="thNewCam"'+(busy?" disabled":"")+'>'+esc(T("thNewCamera"))+'</button><button type="button" class="btn sm" id="thViewCam"'+(busy?" disabled":"")+'>'+esc(T("thFromView"))+'</button></span></div>':'')+
       (src==="image"?'<div class="row"><span class="lbl">'+esc(T("thImage"))+'</span><span class="ctl"><button type="button" class="btn sm" id="thPick"'+(busy?" disabled":"")+'>'+esc(T("thChoose"))+'</button></span></div>':'')+
       '<div class="row"><span class="lbl">'+esc(T("thOnUpload"))+'</span><span class="ctl">'+seg("update",[["always",T("thReplace")],["keep",T("thKeep")]],d.update,busy)+'</span></div>')+
      '</div>'+(th.note?'<p class="up-msg warn-t" role="status">'+esc(th.note)+'</p>':'')+
      '<input type="file" id="thFile" accept="image/*" hidden></div>'+
      '<div class="dlg-foot">'+(isDef||src==="image"?'':'<button type="button" class="btn" id="thRender"'+(busy?" disabled":"")+'>'+esc(T("thRender"))+'</button>'+(tok?'<button type="button" class="btn btn-ghost" id="thFreeze"'+(busy?" disabled":"")+'>'+esc(T("thFreeze"))+'</button>':''))+
      '<span class="grow"></span><button type="button" class="btn btn-primary" id="thDone">'+esc(T("done"))+'</button></div>';
    dlg.innerHTML=html;
    var img=$("thImg");
    if(img){var place=function(){var c=placeCrop(img,th.crop);if(c){th.crop={cx:c.cx,cy:c.cy,zoom:c.zoom};img.classList.add("ready");}};if(img.complete&&img.naturalWidth)place();else img.onload=place;}
    dlg.querySelectorAll("[data-seg]").forEach(function(group){group.querySelectorAll("button").forEach(function(b){b.onclick=function(){
      var v=b.dataset.v,name=group.dataset.seg;if(b.classList.contains("on"))return;
      thumbAct("set",function(){
        var f={};f[name]=v;
        if(name==="source"&&v==="camera"&&!d.camera){var first=(d.cameras||[]).find(function(c){return c.id;});if(first)f.camera=first.reference;}
        return thumbSet(f);
      });
    };});});
    if($("thCam"))$("thCam").onchange=function(){var v=this.value;if(v)thumbAct("set",function(){return thumbSet({camera:v});});};
    if($("thNewCam"))$("thNewCam").onclick=function(){thumbAct("camera",function(){return api("/api/look_thumb_camera?id="+encodeURIComponent(th.id)+"&from=auto").then(function(r){if(!r||!r.ok)throw new Error(r&&r.message||T("failed"));return thumbChanged(th.id);});});};
    if($("thViewCam"))$("thViewCam").onclick=function(){thumbAct("camera",function(){return api("/api/look_thumb_camera?id="+encodeURIComponent(th.id)+"&from=view").then(function(r){if(!r||!r.ok)throw new Error(r&&r.message||T("failed"));return thumbChanged(th.id);});});};
    var pick=function(){$("thFile").click();};
    if($("thPick"))$("thPick").onclick=pick;if($("thPick2"))$("thPick2").onclick=pick;
    $("thFile").onchange=function(){if(this.files&&this.files[0])uploadThumbFile(this.files[0]);};
    var render=function(){thumbAct("render",function(){
      return api("/api/look_thumb_preview?id="+encodeURIComponent(th.id),{timeout:180000}).then(function(r){
        if(!r||!r.ok)throw new Error(r&&r.message||T("failed"));
        thumbPreviews.set(th.id,r.token);if(r.message)th.note=r.message;
        if($("uploadDlg").open&&upload.step==="review")paintUpload();
      });
    });};
    if($("thRender"))$("thRender").onclick=render;
    if($("thFreeze"))$("thFreeze").onclick=function(){thumbAct("set",function(){return api("/api/look_thumb_freeze?id="+encodeURIComponent(th.id)+"&token="+encodeURIComponent(tok)).then(function(r){if(!r||!r.ok)throw new Error(r&&r.message||T("failed"));return thumbChanged(th.id);});});};
    if($("thEditDef"))$("thEditDef").onclick=function(){openThumb("default");};
    $("thDone").onclick=function(){dlg.close();};
    // Crop: drag to pan, wheel or slider to zoom. Saves after a short pause.
    var stageNode=$("thStage");
    function saveCrop(){clearTimeout(th.saveTimer);th.saveTimer=setTimeout(function(){
      var c=th.crop;api("/api/look_thumb_set?id="+encodeURIComponent(th.id)+"&cropX="+c.cx.toFixed(4)+"&cropY="+c.cy.toFixed(4)+"&cropZoom="+c.zoom.toFixed(3))
        .then(function(r){if(!r||!r.ok)throw new Error(r&&r.message||T("failed"));th.d.cropX=c.cx;th.d.cropY=c.cy;th.d.cropZoom=c.zoom;return thumbChanged(th.id);}).catch(function(e){toast(e.message,"err");});
    },350);}
    function setCrop(next){var c=placeCrop(img,next);if(c){th.crop={cx:c.cx,cy:c.cy,zoom:c.zoom};if($("thZoom"))$("thZoom").value=Math.min(4,c.zoom);saveCrop();}}
    if(img&&stageNode.classList.contains("pan")){
      stageNode.onpointerdown=function(e){
        if(busy||!img.naturalWidth)return;e.preventDefault();stageNode.setPointerCapture(e.pointerId);
        var start={x:e.clientX,y:e.clientY,cx:th.crop.cx,cy:th.crop.cy},rect=stageNode.getBoundingClientRect();
        var win=cropWindow(img.naturalWidth,img.naturalHeight,th.crop.zoom,th.crop.cx,th.crop.cy);
        stageNode.onpointermove=function(m){setCrop({zoom:th.crop.zoom,cx:start.cx-(m.clientX-start.x)/rect.width*win.u,cy:start.cy-(m.clientY-start.y)/rect.height*win.v});};
        stageNode.onpointerup=stageNode.onpointercancel=function(){stageNode.onpointermove=null;};
      };
      stageNode.onwheel=function(e){if(busy)return;e.preventDefault();setCrop({cx:th.crop.cx,cy:th.crop.cy,zoom:Math.min(4,Math.max(1,th.crop.zoom*Math.exp(-e.deltaY*0.0015)))});};
      if($("thZoom"))$("thZoom").oninput=function(){setCrop({cx:th.crop.cx,cy:th.crop.cy,zoom:Number(this.value)});};
    }
    if(!inherit&&!busy){
      stageNode.ondragover=function(e){e.preventDefault();stageNode.classList.add("drop");};
      stageNode.ondragleave=function(){stageNode.classList.remove("drop");};
      stageNode.ondrop=function(e){e.preventDefault();stageNode.classList.remove("drop");var f=e.dataTransfer&&e.dataTransfer.files&&e.dataTransfer.files[0];if(f)uploadThumbFile(f);};
    }
  }
  document.addEventListener("paste",function(e){
    var dlg=$("thumbDlg");if(!dlg||!dlg.open||!thumbState||!thumbState.d||thumbState.busy)return;
    if(thumbState.id!=="default"&&thumbState.d.inherited)return;
    var items=(e.clipboardData&&e.clipboardData.items)||[];
    for(var i=0;i<items.length;i++)if(items[i].kind==="file"&&/^image\//.test(items[i].type)){e.preventDefault();uploadThumbFile(items[i].getAsFile());return;}
  });

  /* ---------- Upload avatar (one avatar) ---------- */
  var scene={review:null,job:"",timer:0};
  async function openSceneUpload(){
    var dlg=$("sceneDlg");scene.review=null;
    dlg.innerHTML='<div class="dlg-head"><h2 id="sceneHeading">'+esc(T("uploadAvatar"))+'</h2></div><div class="dlg-body">'+
      '<div class="scene-sum"><div class="thumb-l" id="scThumb">'+esc(LT("preview.unavailable"))+'</div><div><span class="sub" id="scBadge"></span><div class="item-title" id="scName">—</div><label id="scNameWrap" hidden><span class="sub">'+esc(T("avatarName"))+'</span><input id="scNameInput" maxlength="100"></label></div></div>'+
      '<p class="up-msg" id="scMsg" role="status">'+esc(T("checking"))+'</p><div class="bar indet" id="scBar" hidden><div></div></div>'+
      '<label class="consent"><input type="checkbox" id="scConsent"><span><strong>'+esc(T("rights"))+'</strong><span class="sub">'+esc(T("rightsSub"))+'</span></span></label>'+
      '</div><div class="dlg-foot"><button type="button" class="btn" id="scCheck" disabled>'+esc(T("buildCheck"))+'</button><span class="grow"></span><button type="button" class="btn btn-ghost" id="scClose">'+esc(T("close"))+'</button><button type="button" class="btn btn-danger" id="scCancel" hidden>'+esc(T("cancel"))+'</button><button type="button" class="btn btn-primary" id="scGo" disabled>'+esc(T("upload"))+'</button></div>';
    dlg.showModal();
    $("scClose").onclick=function(){dlg.close();};
    $("scConsent").onchange=sceneButtons;
    $("scCheck").onclick=function(){startScene(true);};
    $("scGo").onclick=function(){startScene(false);};
    $("scCancel").onclick=function(){if(scene.job)api("/api/scene_upload_cancel?job="+encodeURIComponent(scene.job)).then(function(r){sceneMsg(r.message);}).catch(function(e){sceneMsg(e.message);});};
    try{
      var r=await api("/api/scene_upload_review");scene.review=r;
      R.text($("scName"),r.name||"—");R.text($("scBadge"),r.isNew?T("newU"):T("updateU"));
      $("scNameWrap").hidden=!r.isNew;$("scNameInput").value=r.name||"";
      if(r.avatarId){var img=new Image();img.alt="";img.onload=function(){$("scThumb").replaceChildren(img);};img.src="/api/scene_upload_thumbnail?avatarId="+r.avatarId;}
      sceneMsg(r.ok?"":r.message,r.ok?"":"warn");sceneButtons();
    }catch(error){sceneMsg(error.message,"warn");}
  }
  function sceneMsg(text,tone){var m=$("scMsg");if(!m)return;m.textContent=text||"";m.className="up-msg"+(tone==="warn"?" warn-t":"");}
  function sceneButtons(){
    if(!$("scGo"))return;var ok=scene.review&&scene.review.ok,busy=!!scene.job;
    $("scCheck").disabled=!ok||busy;$("scGo").disabled=!ok||busy||!$("scConsent").checked;$("scConsent").disabled=busy;
    $("scCancel").hidden=!busy;$("scClose").disabled=busy;$("scBar").hidden=!busy;
  }
  async function startScene(check){
    var r=scene.review;if(!r||!r.ok||scene.job)return;
    if(!check&&!$("scConsent").checked)return;
    scene.job="starting";sceneButtons();sceneMsg(check?T("checking"):T("working"));
    try{
      var res=await api("/api/scene_upload?avatarId="+r.avatarId+"&blueprintId="+encodeURIComponent(r.blueprintId||"")+"&name="+encodeURIComponent($("scNameInput").value)+"&consent="+($("scConsent").checked?1:0)+"&check="+(check?1:0));
      if(!res.ok)throw new Error(res.message||T("failed"));
      scene.job=res.job;pollScene();
    }catch(error){scene.job="";sceneButtons();sceneMsg(error.message,"warn");}
  }
  async function pollScene(){
    try{
      var res=await api("/api/upload_result?job="+encodeURIComponent(scene.job));
      sceneMsg(res.message||(res.pending?T("working"):""),res.pending||res.ok?"":"warn");
      var pct=(res.message||"").match(/(\d+(?:\.\d+)?)%/),bar=$("scBar");
      if(bar){bar.classList.toggle("indet",!pct);bar.firstChild.style.width=pct?Math.min(100,Number(pct[1]))+"%":"";}
      if(res.pending){scene.timer=setTimeout(pollScene,1000);return;}
      scene.job="";scene.review=null;sceneButtons();$("scCheck").disabled=$("scGo").disabled=true;refreshState();
      if(res.ok)toast(res.message,"ok");
    }catch(error){sceneMsg(error.message);scene.timer=setTimeout(pollScene,3000);}
  }
  $("sceneDlg").addEventListener("cancel",function(event){if(scene.job)event.preventDefault();});

  /* ---------- Activity ---------- */
  $("statusBtn").onclick=function(){
    var pop=$("activityPop"),btn=this;if(!pop.hidden){pop.hidden=true;btn.setAttribute("aria-expanded","false");return;}
    pop.hidden=false;btn.setAttribute("aria-expanded","true");paintActivity();
    closeOnOutside(pop,btn,function(){btn.setAttribute("aria-expanded","false");});
  };
  function paintActivity(){
    var s=S||{},pop=$("activityPop"),events=[];
    if(operations.enabled()){
      events=operations.list().filter(function(r){return window.WardrobeOperations.isMutation(r.type);}).slice(-8).reverse().map(function(r){
        var attn=r.state==="failed"||r.state==="needs-review",ok=r.state==="succeeded",time=r.createdAt?new Date(r.createdAt*1000).toLocaleTimeString([],{hour:"2-digit",minute:"2-digit"}):"";
        return {id:r.id,label:r.label||r.type,sub:attn?(r.state==="needs-review"?T("notConfirmed"):(r.error||T("failed"))):ok?time:window.WardrobeOperations.isTerminal(r.state)?(LT("operation.state."+r.state)):T("stApplying"),
          kind:attn?"bad":ok?"ok":"",record:r,attn:attn&&!r.dismissed};
      });
    }else{
      events=activityLog.slice(0,8).map(function(e){return {id:e.id,label:e.message,sub:e.time,kind:e.type==="err"?"bad":e.type==="ok"?"ok":""};});
    }
    var pending=R.pendingWrites();
    var html='<div class="act-head"><h2 id="activityHeading">'+esc(T("activity"))+'</h2></div>'+
      '<div class="act-sec"><h3>'+esc(T("changes"))+'</h3>'+
        (upload.running?'<div class="ev"><span class="ic">'+spinner()+'</span><span class="ev-copy"><strong>'+esc(T("uploadingN",Math.min((upload.q.index||0)+1,upload.q.total||1),upload.q.total||1))+'</strong><span class="acts"><button type="button" class="btn sm" id="actUpload">'+esc(T("upload"))+'</button></span></span></div>':'')+
        (pending?'<div class="ev"><span class="ic">'+spinner()+'</span><span class="ev-copy"><strong>'+esc(T("pending",pending))+'</strong></span></div>':'')+
        (events.length?events.map(function(e){
          return '<div class="ev"><span class="ic '+e.kind+'">'+(e.kind==="ok"?icon("i-check"):e.kind==="bad"?icon("i-warn"):spinner())+'</span><span class="ev-copy"><strong>'+esc(e.label)+'</strong><span class="sub">'+esc(e.sub)+'</span>'+
            (e.attn?'<span class="acts">'+(operations.canRetry(e.record)?'<button type="button" class="btn sm" data-retry="'+esc(e.id)+'">'+esc(T("retry"))+'</button>':'')+'<button type="button" class="btn btn-ghost sm" data-dismiss="'+esc(e.id)+'">'+esc(T("dismiss"))+'</button></span>':'')+'</span></div>';
        }).join(""):(pending||upload.running?'':'<p class="sub">'+esc(T("noChanges"))+'</p>'))+
      '</div>'+
      '<div class="act-sec"><div class="kv"><span class="grow"><strong>'+esc(T("index"))+'</strong><span class="sub">'+esc(s.indexing&&s.total?T("indexing",(s.done||0).toLocaleString(),s.total.toLocaleString()):T("itemsN",(s.outfits||0).toLocaleString()))+'</span></span><button type="button" class="btn sm" id="actIndex"'+(s.indexing||indexAction?" disabled":"")+'>'+esc(T("indexChanges"))+'</button></div>'+
        (s.indexing&&s.total?'<div class="bar"><div style="width:'+Math.round(100*(s.done||0)/Math.max(1,s.total))+'%"></div></div>':'')+
        (s.hiTotal?'<div class="kv"><span class="grow"><strong>'+esc(T("previews"))+'</strong></span><span class="sub">'+esc((s.hiBaked||0).toLocaleString()+" / "+s.hiTotal.toLocaleString())+'</span></div><div class="bar"><div style="width:'+Math.round(100*(s.hiBaked||0)/Math.max(1,s.hiTotal))+'%"></div></div>':'')+
      '</div>'+
      '<div class="act-sec"><div class="kv"><span class="dot '+(!connected?"off":unityBusy?"busy":"")+'"></span><span class="sub">'+esc(!connected?T("offline"):unityBusy?T("unityBusy"):T("connected"))+'</span></div></div>';
    pop.innerHTML=html;
    if($("actUpload"))$("actUpload").onclick=function(){pop.hidden=true;openUpload(null);};
    var idx=$("actIndex");if(idx)idx.onclick=function(){startIndex(this,false);};
    pop.querySelectorAll("[data-retry]").forEach(function(b){b.onclick=function(){operations.retry(b.dataset.retry,effectiveScope()).catch(function(e){toast(e.message,"err");});};});
    pop.querySelectorAll("[data-dismiss]").forEach(function(b){b.onclick=function(){try{operations.dismiss(b.dataset.dismiss);}catch(e){toast(e.message,"err");}};});
  }
  function startIndex(button,quiet,full){
    if(indexAction||(S&&S.indexing))return;
    if(button)busyButton(button,true);indexAction=Date.now();
    api(full?"/api/index?full=1":"/api/index").then(function(r){
      if(!r||!r.ok)throw new Error(r&&r.message||LT("index.failed"));
      if(!quiet)toast(r.message,"ok");refreshState();
    }).catch(function(error){toast(error.message||LT("index.failed"),"err");indexAction=false;})
      .finally(function(){if(button)busyButton(button,false);});
  }

  /* ---------- Settings ---------- */
  $("settingsBtn").onclick=openSettings;
  function openSettings(){
    var dlg=$("settingsDlg"),s=S||{},storedLang=stored("wardrobeLang","");
    var bases=[{guid:"",name:T("automatic")}].concat(s.baseAvatars||[]);
    dlg.innerHTML='<div class="dlg-head"><h2 id="settingsHeading">'+esc(T("settings"))+'</h2><button type="button" class="btn btn-ghost icon-btn sm" id="setClose" aria-label="'+esc(T("close"))+'">'+icon("i-x")+'</button></div><div class="dlg-body">'+
      '<section class="set-sec"><h3>'+esc(T("general"))+'</h3>'+
        '<label class="set-row"><span>'+esc(T("language"))+'</span><select id="setLang"><option value="">'+esc(T("auto"))+'</option>'+langList.map(function(l){return '<option value="'+esc(l.code)+'"'+(l.code===storedLang?" selected":"")+'>'+esc(l.name||l.code)+'</option>';}).join("")+'</select></label>'+
        '<label class="set-row"><span>'+esc(T("compactCards"))+'</span><input type="checkbox" class="sw" id="setCompact"'+(stored("wardrobeCompact","0")==="1"?" checked":"")+'></label>'+
        '<div class="set-row"><span>'+esc(T("version"))+' <span class="sub">'+esc(s.wardrobeVersion||"")+'</span></span><a id="wardrobeUpdate" class="btn sm" href="https://gryphprime.github.io/avatar-wardrobe-vpm/#update" target="_blank" rel="noopener noreferrer" hidden></a></div>'+
      '</section>'+
      '<section class="set-sec"><h3>'+esc(T("looks"))+'</h3>'+
        '<label class="set-row"><span>'+esc(T("multipleLooks"))+'</span><input type="checkbox" class="sw" id="setMulti"'+(multi?" checked":"")+'></label>'+
        '<label class="set-row"><span>'+esc(T("baseAvatar"))+'</span><select id="setBase"'+(s.avatarBaseEditable?"":" disabled")+'>'+bases.map(function(b){return '<option value="'+esc(b.guid)+'"'+(b.guid===(s.avatarOverrideGuid||"")?" selected":"")+' title="'+esc(b.path||"")+'">'+esc(b.name)+'</option>';}).join("")+'</select></label>'+
      '</section>'+
      '<section class="set-sec"><h3>'+esc(T("tools"))+'</h3>'+
        '<label class="set-row"><span>'+esc(T("menuLayout"))+'</span><input type="checkbox" class="sw" id="setToolMenu"'+(stored("wardrobeV2.tool.menu","0")==="1"?" checked":"")+'></label>'+
        '<label class="set-row"><span>'+esc(T("avatarEditor"))+'</span><input type="checkbox" class="sw" id="setToolEditor"'+(stored("wardrobeV2.tool.editor","0")==="1"?" checked":"")+'></label>'+
      '</section>'+
      '<section class="set-sec"><h3>'+esc(T("maintenance"))+'</h3><div class="btn-row">'+
        '<button type="button" class="btn sm" data-m="index">'+esc(T("indexChanges"))+'</button><button type="button" class="btn sm" data-m="reindex">'+esc(T("reindex"))+'</button>'+
        '<button type="button" class="btn sm" data-m="cache">'+esc(T("clearCache"))+'</button><button type="button" class="btn sm" data-m="toggles">'+esc(T("regenToggles"))+'</button>'+
        '<button type="button" class="btn sm" data-m="migrate">'+esc(T("migrate"))+'</button><button type="button" class="btn sm" data-m="diag">'+esc(T("checkPreviews"))+'</button>'+
      '</div></section>'+
      '<section class="set-sec"><h3>'+esc(T("help"))+'</h3><div class="btn-row"><button type="button" class="btn sm" id="setReport">'+esc(T("reportProblem"))+'</button><a class="btn btn-ghost sm" href="/">'+esc(T("classic"))+'</a></div></section>'+
      '</div>';
    dlg.showModal();
    window.WardrobeUpdates.refresh(s.wardrobeVersion,LT);
    $("setClose").onclick=function(){dlg.close();};
    $("setLang").onchange=function(){R.store("wardrobeLang",this.value);loadLangs().then(function(){applyStrings();openSettings();relocalize();});};
    $("setCompact").onchange=function(){R.store("wardrobeCompact",this.checked?"1":"0");document.body.classList.toggle("compact",this.checked);};
    $("setMulti").onchange=function(){
      var box=this,want=box.checked;box.disabled=true;workflowRevision++;
      api("/api/workflow?mode="+(want?"multi-avatar":"one-avatar")).then(function(r){
        if(!r||!r.ok)throw new Error(r&&r.message||T("failed"));
        multi=want;scope=want?scope:"common";installedSignature="";indexWorn();closeItem(true);paintRail();paintWearing();scopeChanged();loadInstalled();refreshState();
      }).catch(function(e){box.checked=!want;toast(e.message,"err");}).finally(function(){box.disabled=false;});
    };
    $("setBase").onchange=function(){
      var sel=this;sel.disabled=true;
      api("/api/avatar_base?guid="+encodeURIComponent(sel.value)).then(function(r){if(!r||!r.ok)throw new Error(r&&r.message||LT("grid.error"));toast(LT(S&&S.avatarSourceGuid?"avatar.base.saved":"avatar.base.sceneSaved"),"ok");detailCache.clear();})
        .catch(function(e){toast(e.message,"err");}).finally(function(){sel.disabled=false;refreshState();});
    };
    $("setToolMenu").onchange=function(){R.store("wardrobeV2.tool.menu",this.checked?"1":"0");if(!this.checked&&currentView==="menu")setView("wardrobe");paintRail();};
    $("setToolEditor").onchange=function(){R.store("wardrobeV2.tool.editor",this.checked?"1":"0");if(!this.checked&&currentView==="editor")setView("wardrobe");paintRail();};
    $("setReport").onclick=function(){window.WardrobeReporting.openBug();};
    dlg.querySelector(".btn-row").onclick=function(event){var b=event.target.closest("[data-m]");if(b)maintenance(b.dataset.m,b);};
  }
  function maintenance(kind,button){
    if(kind==="index"){startIndex(button,false);return;}
    if(kind==="reindex"){if(confirm(LT("index.confirm")))startIndex(button,false,true);return;}
    if(kind==="cache"){
      if(!confirm(LT("cache.confirm")))return;busyButton(button,true);
      api("/api/cache_clear",{timeout:120000}).then(function(r){if(!r||!r.ok)throw new Error(r&&r.message||LT("grid.error"));location.reload();}).catch(function(e){toast(e.message,"err");busyButton(button,false);});
      return;
    }
    var path={toggles:"/api/regenerate_toggles",migrate:"/api/migrate_avatar",diag:"/api/diag"}[kind];
    busyButton(button,true);
    api(path,kind==="diag"?{method:"GET"}:undefined).then(function(r){
      if(!r||r.ok===0||r.ok===false)throw new Error(r&&r.message||T("failed"));
      if(kind==="diag")toast(LT("diag.summary",r.thumbsOnDisk||0,r.hiThumbsOnDisk||0,r.deadCount||0,r.samplePreview?LT("diag.sample.ready"):LT("diag.sample.missing")),"ok");
      else toast(r.message||LT("ui.legacy.presets.migrated"),"ok");
      detailCache.clear();refreshState();
    }).catch(function(e){toast(e.message,"err");}).finally(function(){busyButton(button,false);});
  }
  function relocalize(){
    applyStrings();cardCache.clear();grid.innerHTML="";renderGrid();detailCache.clear();loadShops();loadCatalog(false,true);installedSignature="";loadInstalled();paintRail();paintStatus();paintWearing();
    if(item&&item.d)openItem(item.d.id,item.v&&item.v.guid,item.instance);
    
  }

  /* ---------- Views ---------- */
  var editorTab="objects";
  function setView(view){
    if(view===currentView&&view!=="wardrobe"){return;}
    if(view!=="wardrobe")closeItem(true);
    currentView=view;document.body.dataset.view=view;
    $("viewWardrobe").hidden=view!=="wardrobe";$("viewMenu").hidden=view!=="menu";$("viewEditor").hidden=view!=="editor";
    if(view==="menu")window.WardrobeMenuOrganizer.configure({api:api,T:LT,root:$("menuOrganizer")}).show();
    if(view==="editor")showEditorTab(editorTab);
    if(view==="wardrobe")setTimeout(function(){previews.resume();maybeMore();},0);
    paintRail();
  }
  function showEditorTab(tab){
    editorTab=tab;
    document.querySelectorAll("[data-etab]").forEach(function(t){var on=t.dataset.etab===tab;t.classList.toggle("on",on);t.setAttribute("aria-selected",String(on));});
    $("advancedScene").hidden=tab!=="objects";$("appearanceEditor").hidden=tab!=="materials";
    if(tab==="objects")window.WardrobeSceneEditor.configure({api:api,T:LT,root:$("advancedScene")}).show();
    else window.WardrobeAppearanceEditor.configure({api:api,T:LT,root:$("appearanceEditor"),scope:function(){return effectiveScope();},onChange:refreshState}).show();
  }
  document.querySelectorAll("[data-etab]").forEach(function(t){t.onclick=function(){showEditorTab(t.dataset.etab);};});

  /* ---------- Keyboard ---------- */
  document.addEventListener("keydown",function(event){
    var typing=event.target.closest("input,textarea,select,[contenteditable=true]");
    if((event.metaKey||event.ctrlKey)&&event.key.toLowerCase()==="k"){event.preventDefault();if(fit)return;setView("wardrobe");$("search").focus();$("search").select();return;}
    if(event.key==="Escape"){
      if(document.querySelector("dialog[open]"))return;
      if(openMenuNode){closeMenu();return;}
      var pops=["filterPop","avatarPop","activityPop"].map($).filter(function(p){return !p.hidden;});
      if(pops.length){pops.forEach(function(p){p.hidden=true;});return;}
      if(fit){closeFit();return;}
      if(item){closeItem();return;}
    }
    if(typing||fit||!item||!item.variants||item.variants.length<2)return;
    if(event.key==="ArrowLeft"||event.key==="ArrowRight"){event.preventDefault();selectVariant((item.i+(event.key==="ArrowRight"?1:-1)+item.variants.length)%item.variants.length);}
  });
  window.addEventListener("beforeunload",function(event){if((fit&&fitDirty())||(item&&item.dirty)){event.preventDefault();event.returnValue="";}});

  /* ---------- Boot ---------- */
  document.body.classList.toggle("compact",stored("wardrobeCompact","0")==="1");
  if(!/Mac|iPhone|iPad/.test(navigator.platform||navigator.userAgent)){var kbd=document.querySelector(".search kbd");if(kbd)kbd.textContent="Ctrl K";}
  document.body.dataset.view="wardrobe";
  paintStatus();
  loadLangs().then(function(){
    applyStrings();paintRail();paintWearing();renderGrid();tick();reconnectUpload();
  });
})();
