// Catalog multi-select (Ctrl/Cmd/Shift-click or the card checkbox), the bulk action
// bar, and the card right-click menu. Actions work on one item or the whole selection.
(function(global){
  "use strict";
  var R=global.WardrobeRuntime,O=global.WardrobeOrganize;
  function L(key,fallback){return R.localize(key,fallback);}
  function F(key,fallback){var value=L(key,fallback);for(var a=2;a<arguments.length;a++)value=value.split("{"+(a-2)+"}").join(arguments[a]==null?"":arguments[a]);return value;}
  function esc(value){return R.escape(value);}
  function icon(path){return '<svg class="icon" viewBox="0 0 24 24" aria-hidden="true"><path d="'+path+'"/></svg>';}
  var CLOSE="M6 6l12 12M18 6 6 18",CHEVRON="m7 10 5 5 5-5";

  // host: grid, items(), family(id), open(id), wear(ids), wearLabel(), detail(id),
  // tagsChanged(ids), reload(), hiddenView(), toast(message,kind)
  function create(host){
    var picked=new Set(),anchor="",menu=null,menuReturn=null,busy=false;
    var bar=document.createElement("div");
    bar.className="bulk-bar";bar.setAttribute("role","toolbar");bar.setAttribute("aria-label",L("select.toolbar","Selected items"));bar.hidden=true;
    bar.innerHTML='<span class="bulk-count" role="status" aria-live="polite"></span>'+
      '<button type="button" class="primary" data-act="wear"></button>'+
      '<button type="button" data-act="tags" aria-haspopup="menu" aria-expanded="false"><span>'+esc(L("org.tags","Tags"))+'</span>'+icon(CHEVRON)+'</button>'+
      '<button type="button" data-act="hide"></button>'+
      '<button type="button" data-act="all">'+esc(L("select.all","Select all"))+'</button>'+
      '<button type="button" class="bulk-clear" data-act="clear" aria-label="'+esc(L("select.clear","Clear selection"))+'" title="'+esc(L("select.clear","Clear selection"))+'">'+icon(CLOSE)+'</button>';
    document.body.appendChild(bar);
    // When the bar disappears under keyboard focus, return focus to the last card used.
    function focusAnchor(){var card=cards().find(function(c){return c._family.id===anchor;})||cards()[0];if(card)card.focus();}
    function barButton(act){return bar.querySelector('[data-act="'+act+'"]');}

    function familyOf(id){return host.family(id)||{id:id,tags:[]};}
    function cards(){return Array.from(host.grid.querySelectorAll(".card")).filter(function(card){return card._family&&!card.hidden;});}
    function paint(card){
      var on=!!card._family&&picked.has(card._family.id),box=card.querySelector(".card-pick");
      card.classList.toggle("picked",on);
      if(box){box.setAttribute("aria-pressed",String(on));box.setAttribute("aria-label",L(on?"select.deselect":"select.select",on?"Deselect":"Select"));}
    }
    function paintAll(){
      host.grid.classList.toggle("picking",picked.size>0);
      host.grid.querySelectorAll(".card").forEach(paint);
      paintBar();
    }
    function set(ids,on){ids.forEach(function(id){if(on)picked.add(id);else picked.delete(id);});paintAll();}
    function toggle(id){set([id],!picked.has(id));anchor=id;}
    function range(id){
      var order=cards().map(function(card){return card._family.id;}),from=order.indexOf(anchor||id),to=order.indexOf(id);
      if(from<0||to<0){toggle(id);return;}
      set(order.slice(Math.min(from,to),Math.max(from,to)+1),true);
    }
    function clear(){if(!picked.size)return;picked.clear();anchor="";paintAll();}
    function selectAll(){set(cards().map(function(card){return card._family.id;}),true);}
    // Drops ids no longer in the loaded results (filtered, hidden or reloaded away).
    function prune(){
      if(!picked.size)return;
      var present=new Set(host.items().map(function(f){return f.id;}));
      picked.forEach(function(id){if(!present.has(id))picked.delete(id);});
      paintAll();
    }

    // Returns true when the click was a selection gesture rather than "open details".
    function click(event,card){
      var id=card._family&&card._family.id;if(!id)return false;
      if(event.target.closest(".card-pick")){if(event.shiftKey&&anchor)range(id);else toggle(id);return true;}
      if(event.shiftKey&&(anchor||picked.size)){range(id);return true;}
      if(event.metaKey||event.ctrlKey){toggle(id);return true;}
      // While selecting, a plain click keeps selecting instead of opening details.
      if(picked.size){toggle(id);return true;}
      anchor=id;return false;
    }

    // ---- shared actions on a list of ids ----
    async function run(work){
      if(busy)return;busy=true;paintBar();
      try{await work();}catch(error){host.toast(error.message,"err");}
      finally{busy=false;paintBar();}
    }
    function tagState(ids,tagId){
      var count=ids.filter(function(id){return (familyOf(id).tags||[]).indexOf(tagId)>=0;}).length;
      return count===0?"none":count===ids.length?"all":"some";
    }
    function applyTag(ids,tagId,on){
      return run(async function(){
        await O.setFamilyTag(ids,tagId,on,false);
        ids.forEach(function(id){
          var f=familyOf(id),tags=(f.tags||[]).filter(function(t){return t!==tagId;});
          if(on)tags.push(tagId);f.tags=tags;
        });
        host.tagsChanged(ids);
      });
    }
    function newTag(ids,name){
      name=String(name||"").trim();if(!name)return Promise.resolve();
      return run(async function(){
        var existing=O.state().tags.find(function(t){return t.name.toLowerCase()===name.toLowerCase();});
        var created=existing;
        if(!created){
          var result=await R.request("/api/tag_save?name="+encodeURIComponent(name),{method:"POST"});
          if(!result||!result.ok)throw new Error(result&&result.message||L("org.failed","Could not save the change."));
          await O.refresh();created=result.tag;
        }
        await O.setFamilyTag(ids,created.id,true,false);
        ids.forEach(function(id){var f=familyOf(id);f.tags=(f.tags||[]).filter(function(t){return t!==created.id;}).concat([created.id]);});
        host.tagsChanged(ids);
      });
    }
    function hide(ids,on){
      return run(async function(){
        await O.setHidden(ids,on);
        ids.forEach(function(id){picked.delete(id);});
        host.toast(on?F(ids.length===1?"org.hidden.toast":"select.hidden",ids.length===1?"Hidden. Use the Hidden filter to bring it back.":"{0} items hidden. Use the Hidden filter to bring them back.",ids.length):ids.length===1?L("select.shown.one","Shown in the catalog again."):F("select.shown","{0} items shown in the catalog again.",ids.length),"ok");
        paintAll();
      });
    }
    function hideFolder(id){
      return run(async function(){
        var detail=await host.detail(id);
        if(!detail||!detail.folder)throw new Error(L("org.hide.badFolder","Only folders inside Assets or Packages can be hidden."));
        if(!global.confirm(F("org.hideFolder.confirm","Hide every item in {0}?",detail.folder)))return;
        await O.setFolderHidden(detail.folder,true);
        picked.delete(id);paintAll();
        host.toast(L("org.hidden.toast","Hidden. Use the Hidden filter to bring it back."),"ok");
      });
    }
    function wear(ids){
      if(ids.length>5&&!global.confirm(F("select.wearConfirm","Wear {0} items on the avatar?",ids.length)))return;
      run(async function(){await host.wear(ids);clear();});
    }
    function reveal(id){
      var guid=familyOf(id).thumb||(familyOf(id).variantGuids||[])[0]||"";
      run(async function(){
        var result=await R.request("/api/reveal_asset?guid="+encodeURIComponent(guid),{method:"POST"});
        if(!result||!result.ok)throw new Error(result&&result.message||L("err.notfound","Not found"));
      });
    }

    // ---- bulk bar ----
    function paintBar(){
      var wasHidden=bar.hidden;
      bar.hidden=picked.size===0;
      if(bar.hidden){if(menu&&menu._options.tagsOnly)closeMenu(false);if(!wasHidden&&bar.contains(document.activeElement))focusAnchor();return;}
      var count=picked.size,hidden=host.hiddenView(),canWear=host.wearLabel(),wear=barButton("wear"),hide=barButton("hide");
      R.text(bar.querySelector(".bulk-count"),busy?L("select.working","Applying…"):F(count===1?"select.count.one":"select.count","{0} selected",count));
      bar.setAttribute("aria-busy",String(busy));
      wear.hidden=!canWear;R.text(wear,canWear||"");
      hide.dataset.mode=hidden?"show":"hide";R.text(hide,hidden?L("org.show","Show in catalog"):L("org.hide","Hide from catalog"));
      barButton("tags").setAttribute("aria-expanded",String(!!(menu&&menu._options.tagsOnly)));
      bar.querySelectorAll("button").forEach(function(button){button.disabled=busy&&button.dataset.act!=="clear";});
    }
    bar.addEventListener("click",function(event){
      var button=event.target.closest("button");if(!button||button.disabled)return;
      var ids=Array.from(picked),act=button.dataset.act;
      if(act==="wear")wear(ids);
      else if(act==="tags"&&menu&&menu._options.tagsOnly)closeMenu(true);
      else if(act==="tags"){var rect=button.getBoundingClientRect();openMenu(ids,rect.left,rect.top,{tagsOnly:true,above:true,from:button,instant:event.detail===0});paintBar();}
      else if(act==="hide")hide(ids,button.dataset.mode!=="show");
      else if(act==="all")selectAll();
      else if(act==="clear")clear();
    });

    // ---- context menu ----
    function item(act,label,extra){return '<button type="button" role="menuitem" data-act="'+act+'"'+(extra||'')+'>'+esc(label)+'</button>';}
    function tagItems(ids){
      var tags=O.state().tags;
      return '<div class="ctx-heading">'+esc(L("org.tags","Tags"))+'</div>'+
        (tags.length?'<div class="ctx-tags">'+tags.map(function(t){
          var s=tagState(ids,t.id);
          return '<button type="button" role="menuitemcheckbox" aria-checked="'+(s==="all"?"true":s==="some"?"mixed":"false")+'" data-tag="'+esc(t.id)+'" style="--tag:'+esc(t.color)+'"><span class="ctx-check" aria-hidden="true"></span><span class="ctx-dot" aria-hidden="true"></span>'+esc(t.name)+'</button>';
        }).join("")+'</div>':'')+
        '<form class="ctx-new"><input name="tag" maxlength="40" placeholder="'+esc(L("org.tag.new","Add tag"))+'" aria-label="'+esc(L("org.tag.name","Tag name"))+'"></form>';
    }
    function menuHtml(ids,options){
      if(options.tagsOnly)return tagItems(ids);
      var one=ids.length===1,f=familyOf(ids[0]),hidden=one?!!f.hidden||host.hiddenView():host.hiddenView(),wearLabel=host.wearLabel();
      return (one?item("open",L("select.open","Open details")):'<div class="ctx-heading">'+esc(F("select.count","{0} selected",ids.length))+'</div>')+
        (wearLabel?item("wear",wearLabel):'')+
        item(one&&picked.has(ids[0])?"deselect":"select",one&&picked.has(ids[0])?L("select.deselect","Deselect"):one?L("select.select","Select"):L("select.clear","Clear selection"))+
        '<hr>'+tagItems(ids)+'<hr>'+
        (one?item("reveal",L("select.reveal","Show in Unity")):'')+
        item(hidden?"show":"hide",hidden?L("org.show","Show in catalog"):L("org.hide","Hide from catalog"))+
        (one&&!hidden?item("hideFolder",L("org.hideFolder","Hide this folder")):'');
    }
    function openMenu(ids,x,y,options){
      closeMenu(false);options=options||{};
      menuReturn=options.from||document.activeElement;
      menu=document.createElement("div");menu.className="ctx-menu";menu.setAttribute("role","menu");
      menu._ids=ids;menu._options=options;
      menu.innerHTML=menuHtml(ids,options);
      document.body.appendChild(menu);
      var w=menu.offsetWidth,h=menu.offsetHeight,vw=global.innerWidth,vh=global.innerHeight;
      if(options.above)y=y-h-6;
      var left=Math.max(6,Math.min(x,vw-w-6)),top=Math.max(6,Math.min(y,vh-h-6));
      menu.style.left=left+"px";menu.style.top=top+"px";
      menu.style.transformOrigin=Math.max(0,Math.min(w,x-left))+"px "+(options.above?h+"px":Math.max(0,Math.min(h,y-top))+"px");
      if(options.instant)menu.dataset.instant="";
      var first=menu.querySelector("[role^=menuitem]");if(first)first.focus();
    }
    function repaintMenu(){
      if(!menu)return;
      var focused=document.activeElement&&document.activeElement.dataset&&document.activeElement.dataset.tag;
      menu.innerHTML=menuHtml(menu._ids,menu._options);
      var again=focused&&menu.querySelector('[data-tag="'+focused+'"]');if(again)again.focus();
    }
    function closeMenu(restore){
      if(!menu)return;menu.remove();menu=null;
      if(restore&&menuReturn&&menuReturn.isConnected)menuReturn.focus();
      menuReturn=null;
      if(!bar.hidden)barButton("tags").setAttribute("aria-expanded","false");
    }
    function menuAction(button){
      var ids=menu._ids,act=button.dataset.act,tagId=button.dataset.tag;
      if(tagId){applyTag(ids,tagId,tagState(ids,tagId)!=="all").then(repaintMenu);return;}
      closeMenu(true);
      if(act==="open")host.open(ids[0]);
      else if(act==="wear")wear(ids);
      else if(act==="select")set(ids,true);
      else if(act==="deselect")set(ids,false);
      else if(act==="reveal")reveal(ids[0]);
      else if(act==="hide")hide(ids,true);
      else if(act==="show")hide(ids,false);
      else if(act==="hideFolder")hideFolder(ids[0]);
    }
    document.addEventListener("click",function(event){
      if(!menu)return;
      var button=event.target.closest(".ctx-menu button");
      if(button&&menu.contains(button)){menuAction(button);return;}
      if(!menu.contains(event.target)&&!event.target.closest('.bulk-bar [data-act="tags"]'))closeMenu(false);
    },true);
    document.addEventListener("submit",function(event){
      if(!menu||!menu.contains(event.target))return;
      event.preventDefault();
      var input=event.target.elements.tag,ids=menu._ids;
      newTag(ids,input.value).then(function(){repaintMenu();var next=menu&&menu.querySelector(".ctx-new input");if(next)next.focus();});
    });
    document.addEventListener("keydown",function(event){
      if(menu){
        var list=Array.from(menu.querySelectorAll("[role^=menuitem],input")),index=list.indexOf(document.activeElement);
        if(event.key==="Escape"){event.preventDefault();event.stopPropagation();closeMenu(true);return;}
        if(event.key==="ArrowDown"||event.key==="ArrowUp"){
          event.preventDefault();var step=event.key==="ArrowDown"?1:-1;
          list[(index+step+list.length)%list.length].focus();return;
        }
        if(event.key==="Home"||event.key==="End"){event.preventDefault();list[event.key==="Home"?0:list.length-1].focus();return;}
        if(event.key==="Tab"){closeMenu(true);return;}
        return;
      }
      // Escape clears a selection before anything else on the page reacts.
      if(event.key==="Escape"&&picked.size&&!document.querySelector("dialog[open]")){event.preventDefault();event.stopImmediatePropagation();clear();}
    },true);
    global.addEventListener("resize",function(){closeMenu(false);});
    global.addEventListener("scroll",function(){closeMenu(false);},true);

    host.grid.addEventListener("contextmenu",function(event){
      var card=event.target.closest(".card");if(!card||!card._family)return;
      event.preventDefault();
      var id=card._family.id,ids=picked.has(id)?Array.from(picked):[id];
      openMenu(ids,event.clientX,event.clientY,{from:card});
    });
    host.grid.addEventListener("keydown",function(event){
      var card=event.target.closest(".card");if(!card||!card._family||event.target!==card)return;
      var id=card._family.id;
      if(event.key==="ContextMenu"||(event.shiftKey&&event.key==="F10")){
        event.preventDefault();var rect=card.getBoundingClientRect();
        openMenu(picked.has(id)?Array.from(picked):[id],rect.left+16,rect.top+32,{from:card,instant:true});
      }
      else if(event.key===" "&&picked.size){event.preventDefault();event.stopPropagation();toggle(id);}
      else if((event.ctrlKey||event.metaKey)&&event.key.toLowerCase()==="a"){event.preventDefault();selectAll();}
    },true);

    return {click:click,paint:paint,prune:prune,clear:clear,size:function(){return picked.size;},repaintBar:paintBar,
      pickButton:function(){return '<button type="button" class="card-pick" aria-pressed="false" aria-label="'+esc(L("select.select","Select"))+'"></button>';}};
  }
  global.WardrobeCatalogActions={create:create};
})(window);
