// Catalog organization (project-wide tags, hidden items/folders) and the worn-item
// parts editor (wear defaults, body shape changes, Blendshape Sync).
(function(global){
  "use strict";
  var R=global.WardrobeRuntime;
  function L(key,fallback){return R.localize(key,fallback);}
  function F(key,fallback){var value=L(key,fallback);for(var a=2;a<arguments.length;a++)value=value.split("{"+(a-2)+"}").join(arguments[a]==null?"":arguments[a]);return value;}
  function esc(value){return R.escape(value);}
  var palette=["#e57373","#f0a35e","#e8cf62","#7cc77f","#5fb7c9","#6f9de8","#a487de","#e083b8","#8a8f98"];
  var state={tags:[],hiddenPaths:[],hiddenCount:0},listeners=[],options={toast:function(){},changed:function(){}};
  function ids(list){return encodeURIComponent(JSON.stringify({ids:list}));}
  function emit(){listeners=listeners.filter(function(listener){try{return listener(state)!==false;}catch(_){return false;}});}
  function accept(result){
    if(!result||!result.ok)throw new Error(result&&result.message||L("org.failed","Could not save the change."));
    state={tags:result.tags||[],hiddenPaths:result.hiddenPaths||[],hiddenCount:result.hiddenCount||0};emit();return result;
  }
  async function refresh(){
    try{accept(await R.request("/api/organization",{method:"GET"}));}catch(_){}
    return state;
  }
  // changed: false skips the refresh; an object describes a change the page can patch locally.
  async function write(path,changed){
    var result=accept(await R.request(path,{method:"POST"}));
    if(changed!==false)options.changed(false,changed||null);
    return result;
  }
  function tag(id){return state.tags.find(function(t){return t.id===id;});}
  function nextColor(){return palette[state.tags.length%palette.length];}
  async function createTag(name,color){
    name=String(name||"").trim();if(!name)return null;
    var result=await write("/api/tag_save?name="+encodeURIComponent(name)+"&color="+encodeURIComponent(color||nextColor()),false);
    return result.tag;
  }
  function setFamilyTag(familyIds,tagId,on,patch){return write("/api/family_tag?ids="+ids(familyIds)+"&tag="+encodeURIComponent(tagId)+"&on="+(on?"1":"0"),patch);}
  function setHidden(familyIds,hidden){return write("/api/family_hide?ids="+ids(familyIds)+"&on="+(hidden?"1":"0"));}
  function setFolderHidden(folder,hidden){return write("/api/folder_hide?folder="+encodeURIComponent(folder)+"&on="+(hidden?"1":"0"));}

  // Card badges: at most three tags, the rest summarized.
  function cardTags(tagIds){
    var known=(tagIds||[]).map(tag).filter(Boolean);if(!known.length)return "";
    var shown=known.slice(0,3).map(function(t){return '<span class="tag-chip" style="--tag:'+esc(t.color)+'" title="'+esc(t.name)+'">'+esc(t.name)+'</span>';}).join("");
    return shown+(known.length>3?'<span class="tag-chip more">+'+(known.length-3)+'</span>':"");
  }

  function fillTagSelect(select,value){
    var items=[{id:"",name:L("org.tags.all","All tags")},{id:"untagged",name:L("org.tags.untagged","Untagged")}].concat(state.tags);
    R.reconcile(select,items,function(t){return t.id;},function(){return document.createElement("option");},function(option,t){option.value=t.id;R.text(option,t.count!=null&&t.id?t.name+" ("+t.count+")":t.name);});
    select.value=value||"";if(select.value!==(value||""))select.value="";
    return select.value;
  }

  // Detail panel: tag toggles for one catalog item, with inline tag creation.
  function renderDetail(container,family){
    var current=new Set(family.tags||[]),busy=false,adding=false;
    function paint(){
      var chips=state.tags.map(function(t){var on=current.has(t.id);return '<button type="button" class="tag-toggle'+(on?' on':'')+'" data-tag="'+esc(t.id)+'" aria-pressed="'+on+'" style="--tag:'+esc(t.color)+'">'+esc(t.name)+'</button>';}).join("");
      container.innerHTML='<section class="detail-tags"><div class="detail-section-head"><h3 class="detail-section-title">'+esc(L("org.tags","Tags"))+'</h3>'+(state.tags.length?'<button type="button" class="link" data-act="manage">'+esc(L("org.manage","Edit tags"))+'</button>':'')+'</div>'+
        '<div class="tag-row">'+chips+(adding
          ?'<input class="tag-input" maxlength="40" placeholder="'+esc(L("org.tag.name","Tag name"))+'" aria-label="'+esc(L("org.tag.name","Tag name"))+'">'
          :'<button type="button" class="tag-toggle add" data-act="new">'+esc(L("org.tag.new","Add tag"))+'</button>')+'</div></section>';
      if(adding){var input=container.querySelector(".tag-input");input.focus();
        input.onkeydown=function(e){if(e.key==="Escape"){e.stopPropagation();adding=false;paint();}else if(e.key==="Enter"){e.preventDefault();create(input.value);}};
        input.onblur=function(){if(!busy&&!input.value.trim()){adding=false;paint();}};}
    }
    async function run(work){
      if(busy)return;busy=true;container.querySelectorAll("button,input").forEach(function(b){b.disabled=true;});
      try{await work();}catch(error){options.toast(error.message,"err");}
      finally{busy=false;if(container.isConnected)paint();}
    }
    function create(name){
      name=String(name||"").trim();if(!name){adding=false;paint();return;}
      run(async function(){
        var existing=state.tags.find(function(t){return t.name.toLowerCase()===name.toLowerCase();});
        var created=existing||await createTag(name);
        if(created&&!current.has(created.id)){await setFamilyTag([family.id],created.id,true);current.add(created.id);family.tags=Array.from(current);}
        adding=false;
      });
    }
    // Toggles apply at once and save in order; a rejected save puts the chip back.
    var saving=Promise.resolve();
    function toggle(id){
      var on=!current.has(id);
      if(on)current.add(id);else current.delete(id);
      family.tags=Array.from(current);paint();
      var tags=family.tags.slice();options.changed(false,{familyId:family.id,tags:tags});
      saving=saving.then(function(){return setFamilyTag([family.id],id,on,{familyId:family.id,tags:tags});}).catch(function(error){
        if(on)current.delete(id);else current.add(id);
        family.tags=Array.from(current);
        options.toast(error.message,"err");options.changed(false,{familyId:family.id,tags:family.tags.slice()});
        if(container.isConnected&&!busy&&!adding)paint();
      });
    }
    container.onclick=function(event){
      var button=event.target.closest("button");if(!button||busy)return;
      var id=button.dataset.tag,act=button.dataset.act;
      if(id)toggle(id);
      else if(act==="new"){adding=true;paint();}
      else if(act==="manage")openManager();
    };
    paint();
    listeners.push(function(){if(!container.isConnected)return false;if(!busy&&!adding)paint();});
  }

  // Hide controls live with the item's less-used actions.
  function renderHide(container,family){
    var busy=false;
    function paint(){
      var folder=family.hiddenFolder||"";
      container.innerHTML=family.hidden
        ?'<p class="subtle">'+esc(folder?F("org.hiddenByFolder","Hidden with the folder {0}",folder):L("org.hiddenItem","Hidden from the catalog."))+'</p><button type="button" data-act="'+(folder?'showFolder':'show')+'">'+esc(L(folder?"org.showFolder":"org.show",folder?"Show folder again":"Show in catalog"))+'</button>'
        :'<button type="button" data-act="hide">'+esc(L("org.hide","Hide from catalog"))+'</button>'+(family.folder?'<button type="button" data-act="hideFolder" title="'+esc(family.folder)+'">'+esc(L("org.hideFolder","Hide this folder"))+'</button>':'')+
          '<p class="subtle">'+esc(L("org.hide.title","For things you never wear, like shader helpers or props. The Hidden filter brings them back."))+'</p>';
    }
    async function run(work){
      if(busy)return;busy=true;container.querySelectorAll("button").forEach(function(b){b.disabled=true;});
      try{await work();}catch(error){options.toast(error.message,"err");}
      finally{busy=false;if(container.isConnected)paint();}
    }
    container.onclick=function(event){
      var act=(event.target.closest("button")||{}).dataset;act=act&&act.act;if(!act||busy)return;
      if(act==="hide")run(async function(){await setHidden([family.id],true);family.hidden=1;options.toast(L("org.hidden.toast","Hidden. Use the Hidden filter to bring it back."),"ok");});
      else if(act==="show")run(async function(){await setHidden([family.id],false);family.hidden=0;});
      else if(act==="hideFolder"){
        if(!global.confirm(F("org.hideFolder.confirm","Hide every item in {0}?",family.folder)))return;
        run(async function(){await setFolderHidden(family.folder,true);family.hidden=1;family.hiddenFolder=family.folder+"/";});
      }
      else if(act==="showFolder")run(async function(){await setFolderHidden(family.hiddenFolder,false);family.hidden=0;family.hiddenFolder="";});
    };
    paint();
  }

  // Manager dialog: rename/recolor/delete tags and unhide folders.
  function openManager(){
    var previous=document.activeElement,dialog=document.createElement("dialog");
    dialog.className="report-dialog organize-dialog";dialog.setAttribute("aria-labelledby","organizeTitle");
    document.body.appendChild(dialog);
    function paint(){
      dialog.innerHTML='<h2 id="organizeTitle">'+esc(L("org.title","Organize"))+'</h2>'+
        '<h3>'+esc(L("org.tags","Tags"))+'</h3><div class="organize-list">'+(state.tags.length?state.tags.map(function(t){
          return '<div class="organize-row" data-id="'+esc(t.id)+'"><input type="color" value="'+esc(t.color)+'" aria-label="'+esc(L("org.tag.color","Color"))+'"><input class="tag-name" value="'+esc(t.name)+'" maxlength="40" aria-label="'+esc(L("org.tag.name","Tag name"))+'"><span class="subtle">'+esc(F("org.tag.count","{0} items",t.count||0))+'</span><button type="button" data-act="delete" class="danger">'+esc(L("org.delete","Delete"))+'</button></div>';
        }).join(""):'<p class="subtle">'+esc(L("org.tags.none","No tags yet. Add tags from an item's details."))+'</p>')+'</div>'+
        '<form class="organize-new" data-act="newForm"><input name="tag" maxlength="40" placeholder="'+esc(L("org.tag.name","Tag name"))+'" aria-label="'+esc(L("org.tag.name","Tag name"))+'"><button type="submit">'+esc(L("org.tag.new","Add tag"))+'</button></form>'+
        '<h3>'+esc(L("org.hiddenFolders","Hidden folders"))+'</h3><div class="organize-list">'+(state.hiddenPaths.length?state.hiddenPaths.map(function(p){
          return '<div class="organize-row" data-folder="'+esc(p)+'"><code>'+esc(p)+'</code><button type="button" data-act="unhideFolder">'+esc(L("org.showFolder","Show folder again"))+'</button></div>';
        }).join(""):'<p class="subtle">'+esc(L("org.hiddenFolders.none","No hidden folders."))+'</p>')+'</div>'+
        '<p class="subtle">'+esc(F("org.hiddenCount","{0} hidden items. Choose the Hidden filter to review them.",state.hiddenCount))+'</p>'+
        '<p class="subtle">'+esc(L("org.saved","Tags and hidden items are saved in this Unity project (ProjectSettings/AvatarWardrobeOrganization.json)."))+'</p>'+
        '<div class="actions"><button type="button" data-act="close">'+esc(L("library.close","Close"))+'</button></div>';
    }
    async function run(work){try{await work();}catch(error){options.toast(error.message,"err");}if(dialog.isConnected)paint();}
    function close(){dialog.close();dialog.remove();if(previous&&previous.isConnected)previous.focus();}
    dialog.addEventListener("cancel",function(e){e.preventDefault();close();});
    dialog.addEventListener("keydown",function(e){e.stopPropagation();});
    dialog.addEventListener("submit",function(event){
      event.preventDefault();var input=event.target.elements.tag;var name=input.value;
      run(async function(){await createTag(name);}).then(function(){var next=dialog.querySelector(".organize-new input");if(next)next.focus();});
    });
    dialog.addEventListener("change",function(event){
      var row=event.target.closest(".organize-row[data-id]");if(!row)return;
      if(!row.querySelector(".tag-name").value.trim()){paint();return;}
      var name=row.querySelector(".tag-name").value,color=row.querySelector("input[type=color]").value;
      run(function(){return write("/api/tag_save?id="+encodeURIComponent(row.dataset.id)+"&name="+encodeURIComponent(name)+"&color="+encodeURIComponent(color));});
    });
    dialog.addEventListener("click",function(event){
      var button=event.target.closest("button");if(!button)return;
      var row=button.closest(".organize-row"),act=button.dataset.act;
      if(act==="close")close();
      else if(act==="delete"&&row){var t=tag(row.dataset.id);if(t&&global.confirm(F("org.tag.deleteConfirm","Delete the tag {0}? Items keep everything else.",t.name)))run(function(){return write("/api/tag_delete?id="+encodeURIComponent(t.id));});}
      else if(act==="unhideFolder"&&row)run(function(){return setFolderHidden(row.dataset.folder,false);});
    });
    paint();dialog.showModal();
    refresh().then(function(){if(dialog.isConnected)paint();});
  }

  // Customize a worn item: which parts start on, body adjustments (fixed, hidden skin or
  // an in-game slider) and Blendshape Sync. One flat list; one Apply.
  function renderParts(container,guid,target,worn){
    var token=(container._partsToken||0)+1;container._partsToken=token;
    if(!worn){container.innerHTML="";container.hidden=true;return;}
    container.hidden=false;
    container.innerHTML='<section class="customize" aria-labelledby="customizeTitle'+token+'"><h3 class="detail-section-title" id="customizeTitle'+token+'">'+esc(L("parts.title","Customize"))+'</h3><div class="customize-body"><p class="subtle">'+esc(L("grid.loading","Loading…"))+'</p></div></section>';
    var body=container.querySelector(".customize-body"),data=null,rows=[],saved="";
    function live(){return container.isConnected&&container._partsToken===token;}
    function all(){return data.meshes.concat(data.itemMeshes||[]);}
    function listId(path){return "adjustShapes"+token+"_"+Math.max(0,all().findIndex(function(m){return m.path===path;}));}
    function meshes(mode){return mode==="slider"?data.meshes.concat(data.itemMeshes||[]):data.meshes;}
    async function load(){
      try{
        var result=await R.request("/api/item_parts?guid="+encodeURIComponent(guid)+"&target="+encodeURIComponent(target),{method:"GET"});
        if(!live())return;
        if(!result||!result.ok)throw new Error(result&&result.message||L("parts.loadFailed","Could not read this item's parts."));
        data=result;
        rows=[];
        data.parts.forEach(function(p){p.shapes.forEach(function(s){rows.push({mesh:s.mesh,shape:s.shape,value:Number(s.value)||0,mode:s.delete?"hide":"set",part:p.key,label:""});});});
        (data.sliders||[]).forEach(function(s){rows.push({mesh:s.mesh,shape:s.shape,value:Number(s.value)||0,mode:"slider",part:"",label:s.label||""});});
        saved=JSON.stringify(payload());paint();
      }catch(error){if(live())body.innerHTML='<p class="subtle">'+esc(error.message)+'</p><button type="button" data-act="retry">'+esc(L("grid.retry","Try again"))+'</button>';}
    }
    function payload(){
      var parts=data.parts.map(function(p){return {key:p.key,worn:p.worn?1:0,shapes:[]};});
      var sliders=[];
      rows.filter(function(r){return r.shape;}).forEach(function(r){
        if(r.mode==="slider"){sliders.push({mesh:r.mesh,shape:r.shape,label:r.label||"",value:r.value});return;}
        var part=parts.find(function(p){return p.key===r.part;})||parts[0];
        part.shapes.push({mesh:r.mesh,shape:r.shape,value:r.mode==="hide"?0:r.value,delete:r.mode==="hide"?1:0});
      });
      return {sync:data.sync?1:0,syncMesh:"",parts:parts,sliders:sliders};
    }
    function dirty(){return !!data&&JSON.stringify(payload())!==saved;}
    function option(value,text,selected){return '<option value="'+esc(value)+'"'+(selected?' selected':'')+'>'+esc(text)+'</option>';}
    function row(r,i){
      var list=meshes(r.mode),slider=r.mode==="slider",hide=r.mode==="hide";
      var named=data.parts.filter(function(p){return p.key;});
      return '<li class="adjust" data-row="'+i+'">'+
        '<div class="adjust-main">'+
          '<input data-field="shape" list="'+listId(r.mesh)+'" value="'+esc(r.shape)+'" placeholder="'+esc(L("parts.shape.ph","Blendshape, e.g. Shrink_Chest"))+'" aria-label="'+esc(L("parts.shape","Blendshape"))+'">'+
          '<select data-field="mode" aria-label="'+esc(L("parts.mode","Adjustment"))+'">'+option("set",L("parts.mode.set","Set to"),r.mode==="set")+option("hide",L("parts.deleteShape","Hide skin"),hide)+option("slider",L("parts.mode.slider","Menu slider"),slider)+'</select>'+
          '<button type="button" class="icon-button" data-act="remove" aria-label="'+esc(L("parts.removeShape","Remove"))+'" title="'+esc(L("parts.removeShape","Remove"))+'"><svg class="icon" aria-hidden="true" viewBox="0 0 24 24"><path d="M6 6l12 12M18 6 6 18"/></svg></button>'+
        '</div><div class="adjust-detail">'+
          (list.length>1?'<select data-field="mesh" aria-label="'+esc(L("parts.mesh","Mesh"))+'">'+list.map(function(m){return option(m.path,m.name,m.path===r.mesh);}).join("")+'</select>':'')+
          (slider
            ?'<input data-field="label" value="'+esc(r.label)+'" maxlength="40" placeholder="'+esc(L("sliders.label.ph","Menu name"))+'" aria-label="'+esc(L("sliders.label","Menu name"))+'">'
            :named.length?'<select data-field="part" aria-label="'+esc(L("parts.when","Applies while"))+'">'+option("",L("parts.when.item","While the item is worn"),!r.part)+named.map(function(p){return option(p.key,F("parts.when.part","While {0} is worn",p.name),p.key===r.part);}).join("")+'</select>':'')+
          (hide?'<span class="subtle adjust-note">'+esc(L("parts.hide.note","Removes the skin this shape moves"))+'</span>'
            :'<label class="adjust-value"><span>'+esc(L(slider?"sliders.start":"parts.value",slider?"Starts at":"Value"))+'</span><input type="range" min="0" max="100" step="1" data-field="value" value="'+Math.round(r.value)+'"><output>'+Math.round(r.value)+'</output></label>')+
        '</div></li>';
    }
    function paint(){
      var lists=all().map(function(m){return '<datalist id="'+listId(m.path)+'">'+m.shapes.map(function(n){return '<option value="'+esc(n)+'">';}).join("")+'</datalist>';}).join("");
      var named=data.parts.filter(function(p){return p.key;});
      var sliderCount=rows.filter(function(r){return r.mode==="slider";}).length;
      body.innerHTML=lists+
        (named.length?'<fieldset class="customize-group"><legend>'+esc(L("parts.worn","Parts worn"))+'</legend><div class="part-pills">'+named.map(function(p){
          var i=data.parts.indexOf(p);
          return '<label class="part-pill"><input type="checkbox" data-part="'+i+'" data-field="worn"'+(p.worn?' checked':'')+'><span>'+esc(p.name)+'</span></label>';
        }).join("")+'</div>'+(named.some(function(p){return p.toggle;})?'<p class="subtle">'+esc(L("parts.worn.toggles","Unticked parts start off; switch them in the in-game menu."))+'</p>':'')+'</fieldset>':'')+
        '<fieldset class="customize-group"><legend>'+esc(L("parts.adjust","Body adjustments"))+'</legend>'+
          (data.meshes.length||(data.itemMeshes||[]).length
            ?(rows.length?'<ul class="adjust-list">'+rows.map(row).join("")+'</ul>':'<p class="subtle">'+esc(L("parts.adjust.empty","Shrink or hide the body under this item, or add a slider to the in-game menu."))+'</p>')+
              '<button type="button" class="link" data-act="add">'+esc(L("parts.addShape","Add adjustment"))+'</button>'+
              (sliderCount?'<p class="subtle">'+esc(F("sliders.note","Sliders appear in the menu under “{0}”. Each uses 8 of the avatar’s 256 synced parameter bits.",F("sliders.menu","{0} sliders",data.parts[0].name)))+'</p>':'')
            :'<p class="subtle">'+esc(L("parts.noBody","No mesh with blendshapes was found."))+'</p>')+
        '</fieldset>'+
        (data.meshes.length?'<label class="customize-switch"><input type="checkbox" data-field="sync"'+(data.sync?' checked':'')+'><span><strong>'+esc(L("parts.sync","Follow body shape"))+'</strong><span class="subtle">'+esc(F("parts.syncHint","Clothes follow body sliders like chest or hips ({0} matching shapes).",data.syncCandidates||0))+'</span></span></label>':'')+
        (data.copies>1?'<p class="subtle">'+esc(F("parts.copies","Applies to all {0} worn copies in this preset.",data.copies))+'</p>':'')+
        '<div class="customize-actions"><span class="subtle" role="status" aria-live="polite" data-status></span><button type="button" data-act="revert"'+(dirty()?'':' hidden')+'>'+esc(L("appearance.cancel","Cancel"))+'</button><button type="button" class="primary" data-act="save"'+(dirty()?'':' disabled')+'>'+esc(L("parts.save","Apply to avatar"))+'</button></div>';
    }
    function paintDirty(){
      var d=dirty(),save=body.querySelector('[data-act="save"]'),revert=body.querySelector('[data-act="revert"]');
      if(save)save.disabled=!d;if(revert)revert.hidden=!d;
      var status=body.querySelector("[data-status]");if(status&&d)R.text(status,"");
    }
    body.addEventListener("input",function(event){
      var field=event.target.dataset.field;if(!data||!field)return;
      if(field==="sync"){data.sync=event.target.checked?1:0;paintDirty();return;}
      if(field==="worn"){data.parts[Number(event.target.dataset.part)].worn=event.target.checked?1:0;paintDirty();return;}
      var li=event.target.closest(".adjust");if(!li)return;
      var r=rows[Number(li.dataset.row)];
      if(field==="value"){r.value=Number(event.target.value);li.querySelector("output").textContent=Math.round(r.value);}
      else if(field==="shape")r.shape=event.target.value.trim();
      else if(field==="label")r.label=event.target.value;
      paintDirty();
    });
    body.addEventListener("change",function(event){
      var field=event.target.dataset.field,li=event.target.closest(".adjust");if(!data||!li)return;
      var r=rows[Number(li.dataset.row)];
      if(field==="mode"){
        r.mode=event.target.value;
        if(r.mode==="slider")r.part="";
        if(!meshes(r.mode).some(function(m){return m.path===r.mesh;}))r.mesh=meshes(r.mode)[0].path;
      }
      else if(field==="mesh")r.mesh=event.target.value;
      else if(field==="part")r.part=event.target.value;
      else return;
      paint();var again=body.querySelector('.adjust[data-row="'+li.dataset.row+'"] [data-field="'+field+'"]');if(again)again.focus();
    });
    body.addEventListener("click",async function(event){
      var button=event.target.closest("button");if(!button)return;
      var act=button.dataset.act;
      if(act==="retry"){load();return;}
      if(!data)return;
      if(act==="add"){
        var first=data.meshes[0]||(data.itemMeshes||[])[0];
        rows.push({mesh:first.path,shape:"",value:data.meshes.length?100:50,mode:data.meshes.length?"set":"slider",part:"",label:""});
        paint();var inputs=body.querySelectorAll('[data-field="shape"]');if(inputs.length)inputs[inputs.length-1].focus();
      }
      else if(act==="remove"){rows.splice(Number(button.closest(".adjust").dataset.row),1);paint();var add=body.querySelector('[data-act="add"]');if(add)add.focus();}
      else if(act==="revert"){load();}
      else if(act==="save"){
        var status=body.querySelector("[data-status]");
        body.querySelectorAll("button,input,select").forEach(function(c){c.disabled=true;});R.text(status,L("detail.settingsApplying","Applying…"));
        try{
          var result=await R.request("/api/item_parts?op=save&guid="+encodeURIComponent(guid)+"&target="+encodeURIComponent(target)+"&data="+encodeURIComponent(JSON.stringify(payload())),{method:"POST"});
          if(!result||!result.ok)throw new Error(result&&result.message||L("parts.saveFailed","Could not apply the changes."));
          options.toast(result.message||L("parts.saved","Applied to the avatar."),"ok");options.changed(true,{sceneOnly:true});
          await load();
        }catch(error){if(live()){paint();R.text(body.querySelector("[data-status]"),error.message);}}
      }
    });
    load();
  }

  global.WardrobeOrganize={
    configure:function(value){options=Object.assign(options,value||{});},
    refresh:refresh,state:function(){return state;},onChange:function(listener){listeners.push(listener);},
    cardTags:cardTags,fillTagSelect:fillTagSelect,renderDetail:renderDetail,renderHide:renderHide,renderParts:renderParts,openManager:openManager,
    setHidden:setHidden,setFamilyTag:setFamilyTag
  };
})(window);
