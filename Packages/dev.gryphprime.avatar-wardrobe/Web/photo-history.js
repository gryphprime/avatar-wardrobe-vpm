/* Kept photographs are immutable evidence; opening one never changes the avatar. */
(function(global){
  'use strict';
  function L(key,fallback){return window.WardrobeRuntime&&window.WardrobeRuntime.localize?window.WardrobeRuntime.localize(key,fallback):fallback;}
  function esc(value){return window.WardrobeRuntime.escape(value);}

  function create(options){
    var root=options.root,items=[],project='',nextOffset=null,serial=0,openSerial=0,busy=false,lastContext='',onlyCurrent=false;
    function el(tag,text,cls){var node=document.createElement(tag);if(text!=null)node.textContent=text;if(cls)node.className=cls;return node;}
    function context(){return options.context&&options.context()||{};}
    function scope(){return options.scope?options.scope():'common';}
    function targetKey(value){return JSON.stringify([value.projectId,value.sceneGuid,value.avatarId,value.scopeId]);}
    function currentTarget(){return Object.assign({},context(),{scopeId:scope()});}
    function matches(value){return value&&targetKey(value)===targetKey(currentTarget());}
    function button(label,action){var node=el('button',label,'btn');node.type='button';node.onclick=action;node.disabled=busy;return node;}
    var box=el('details',null,'photo-history'),title=el('summary',L("ui.kept.photos","Kept photos")),controls=el('div',null,'snapshot-controls'),status=el('p',null,'subtle'),list=el('div',null,'photo-history-list'),more=button(L("ui.load.more.kept.photos","Load more kept photos"),function(){refresh(true);});
    status.setAttribute('role','status');status.setAttribute('aria-live','polite');
    var refreshButton=button(L("ui.refresh.kept.photos","Refresh kept photos"),function(){refresh();}),filter=el('select');filter.setAttribute('aria-label',L("ui.kept.photo.scope","Kept photo scope"));
    [['all',L("ui.all.kept.photos.in.this.project","All kept photos in this project")],['current',L("ui.this.avatar.and.preset","This avatar and preset")]].forEach(function(pair){var option=el('option',pair[1]);option.value=pair[0];filter.appendChild(option);});
    filter.onchange=function(){onlyCurrent=filter.value==='current';refresh();};controls.append(refreshButton,filter);
    box.append(title,el('p',L("ui.open.or.export.a.photograph.without.changing.the.scene","Open or export a photograph without changing the scene. Unkept photos may expire from the recent cache."),'subtle'),controls,status,list,more);root.appendChild(box);
    var dialog=el('dialog',null,'photo-history-dialog'),dialogTitle=el('h3',L("ui.kept.photograph","Kept photograph")),dialogStatus=el('p',null,'subtle'),image=el('img'),provenance=el('dl'),dialogActions=el('div',null,'snapshot-controls');
    image.alt=L("ui.kept.avatar.photograph","Kept avatar photograph");var close=button(L("ui.close.photograph","Close photograph"),function(){openSerial++;dialog.close();});dialogActions.appendChild(close);dialog.append(dialogTitle,dialogStatus,image,provenance,dialogActions);root.appendChild(dialog);dialog.addEventListener('cancel',function(){openSerial++;});
    function date(value){return value?new Date(value*1000).toLocaleString():L("ui.capture.date.unavailable","Capture date unavailable");}
    function description(value){var t=value.target||{};return (matches(t)?L("ui.current.avatar.and.preset","Current avatar and preset"):L("ui.another.avatar.or.preset","Another avatar or preset"))+' · '+(value.view||'view unavailable')+(value.before?' · Before':' · After')+' · '+date(value.completed||value.created);}
    function sourceText(value){var t=value.target||{};return L("ui.avatar.2","Avatar ")+(t.avatarId||t.avatarInstanceId||'identity unavailable')+' · Preset '+(t.scopeId||'unavailable');}
    function valid(value){return value&&/^[a-f0-9]{64}$/.test(value.snapshotKey||'')&&value.target&&value.target.projectId===project;}
    async function request(path,opts){var result=await options.api(path,opts||{method:'GET'});if(!result||result.ok!==1)throw new Error(result&&result.message||L("ui.photograph.history.is.unavailable","Photograph history is unavailable."));return result;}
    function draw(){
      list.replaceChildren();refreshButton.disabled=filter.disabled=busy;
      items.forEach(function(value){var row=el('article',null,'photo-history-row');row.append(el('h4',description(value)),el('p',sourceText(value),'subtle'));
        var actions=el('div',null,'snapshot-controls');actions.append(button(L("ui.open.photograph","Open photograph"),function(){open(value);}),button(L("ui.unkeep.photograph","Unkeep photograph"),function(){unkeep(value);}));
        var exportLink=el('a',L("ui.export.png","Export PNG"),'btn');exportLink.href='/api/shadow/export?key='+value.snapshotKey;exportLink.download='wardrobe-photo-'+value.snapshotKey.slice(0,12)+'.png';actions.appendChild(exportLink);row.appendChild(actions);list.appendChild(row);});
      more.hidden=nextOffset==null;more.disabled=busy;
    }
    async function refresh(append){
      var ticket=++serial,target=currentTarget(),offset=append&&nextOffset!=null?nextOffset:0;busy=true;draw();status.textContent=L("ui.loading.kept.photographs","Loading kept photographs…");
      var path='/api/shadow/history?pinned=1&limit=12&offset='+offset;
      if(onlyCurrent){if(!target.avatarId||!target.projectId){busy=false;items=[];nextOffset=null;draw();status.textContent=L("ui.connect.and.pin.an.avatar.to.filter.its.kept","Connect and pin an avatar to filter its kept photos. All project photos remain available.");return;}
        path+='&avatarId='+encodeURIComponent(target.avatarId)+'&sceneGuid='+encodeURIComponent(target.sceneGuid||'')+'&scopeId='+encodeURIComponent(target.scopeId||'common');}
      try{var result=await request(path);if(ticket!==serial||!root.isConnected)return;
        if(typeof result.projectId!=='string'||(target.projectId&&result.projectId!==target.projectId))throw new Error(L("ui.this.history.belongs.to.another.project.reopen.the.selected","This history belongs to another project. Reopen the selected project’s desktop host."));
        project=result.projectId;var incoming=(result.items||[]).filter(valid);items=offset?items.concat(incoming):incoming;nextOffset=result.nextOffset;status.textContent=items.length?items.length+' kept photograph'+(items.length===1?'':'s')+' shown. These photographs do not restore scene objects.':L("ui.no.kept.photographs.yet.use.keep.photo.after.a","No kept photographs yet. Use Keep photo after a successful capture.");
      }catch(error){if(ticket===serial){if(!offset){items=[];nextOffset=null;}status.textContent=error.message;}}
      finally{if(ticket===serial){busy=false;draw();}}
    }
    async function open(value){
      if(busy||!valid(value))return;var ticket=++openSerial;dialogTitle.textContent=L("ui.kept.photograph","Kept photograph");dialogStatus.textContent=L("ui.verifying.photograph.bytes","Verifying photograph bytes…");image.hidden=true;provenance.replaceChildren();dialog.showModal();
      try{var result=await request('/api/shadow/photo?key='+value.snapshotKey);if(ticket!==openSerial||!dialog.open)return;if(!valid(result)||result.snapshotKey!==value.snapshotKey)throw new Error(L("ui.the.photograph.identity.changed.refresh.kept.photos","The photograph identity changed. Refresh kept photos."));
        dialogTitle.textContent=description(result);dialogStatus.textContent=L("ui.historical.photograph.opening.it.does.not.change.or.apply","Historical photograph. Opening it does not change or apply anything to the avatar.");
        image.onload=function(){if(ticket===openSerial)image.hidden=false;};image.onerror=function(){if(ticket===openSerial)dialogStatus.textContent=L("ui.the.image.is.unavailable.or.changed.refresh.kept.photos","The image is unavailable or changed. Refresh kept photos.");};image.src='/api/shadow/image?key='+value.snapshotKey;
        var t=result.target||{};[[L("ui.project","Project"),t.projectId],[L("ui.avatar","Avatar"),t.avatarId||t.avatarInstanceId],[L("scene.nav","Scene"),t.sceneGuid],[L("detail.preset.tag","Preset"),t.scopeId],[L("ui.source.revision","Source revision"),result.sourceRevision],[L("ui.appearance.revision","Appearance revision"),result.visualRevision],[L("ui.recipe.revision","Recipe revision"),result.recipeRevision]].forEach(function(pair){provenance.append(el('dt',pair[0]),el('dd',String(pair[1]||L("ui.unavailable.in.this.older.capture","Unavailable in this older capture"))));});
      }catch(error){if(ticket===openSerial)dialogStatus.textContent=error.message;}
    }
    async function unkeep(value){
      if(busy||!valid(value))return;busy=true;draw();status.textContent=L("ui.removing.this.photograph.from.kept.history","Removing this photograph from kept history…");
      try{await request('/api/shadow/pin',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({key:value.snapshotKey,pinned:false})});await refresh();}
      catch(error){busy=false;draw();status.textContent=error.message;}
    }
    function contextChanged(){var key=targetKey(currentTarget());if(key!==lastContext){lastContext=key;openSerial++;if(dialog.open)dialog.close();refresh();}}
    function kept(){refresh();}global.addEventListener('wardrobe-photo-kept',kept);
    return {refresh:refresh,contextChanged:contextChanged,destroy:function(){serial++;openSerial++;global.removeEventListener('wardrobe-photo-kept',kept);dialog.close();}};
  }
  global.WardrobePhotoHistoryUI={create:create};
})(window);
