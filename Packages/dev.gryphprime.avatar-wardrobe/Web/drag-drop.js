/* Native gestures carry identities only. Buttons and drops call the same action callbacks. */
(function(global){
  'use strict';
  function L(key,fallback){return window.WardrobeRuntime&&window.WardrobeRuntime.localize?window.WardrobeRuntime.localize(key,fallback):fallback;}
  function esc(value){return window.WardrobeRuntime.escape(value);}

  var mime='application/x-avatar-wardrobe+json',current=null;
  function normalize(raw){
    if(!raw||typeof raw!=='object'||raw.version!==1||typeof raw.familyId!=='string'||raw.familyId.length>256||typeof raw.targetKey!=='string'||raw.targetKey.length>4096)throw new Error(L("ui.invalid.outfit.drag","Invalid outfit drag."));
    if(raw.variantId&&!/^[a-f0-9]{32}$/i.test(raw.variantId))throw new Error(L("ui.invalid.outfit.variant","Invalid outfit variant."));
    return {version:1,familyId:raw.familyId,variantId:raw.variantId||'',targetKey:raw.targetKey};
  }
  function source(node,payload){
    node.draggable=true;node.ondragstart=function(event){try{current=normalize(payload());event.dataTransfer.setData(mime,JSON.stringify(current));event.dataTransfer.effectAllowed='copy';var rect=node.getBoundingClientRect();event.dataTransfer.setDragImage(node,Math.max(0,Math.min(rect.width,event.clientX-rect.left)),Math.max(0,Math.min(rect.height,event.clientY-rect.top)));}catch(error){event.preventDefault();current=null;global.dispatchEvent(new CustomEvent('wardrobe-drag-error',{detail:error.message}));}};
    node.ondragend=function(){current=null;document.querySelectorAll('.drop-ready').forEach(function(el){el.classList.remove('drop-ready');});};
  }
  function target(node,options){
    node.addEventListener('dragover',function(event){if(options.enabled&&!options.enabled()){node.classList.remove('drop-ready');return;}var files=Array.from(event.dataTransfer.types||[]).includes("Files");if((files&&options.files)||(!files&&current&&options.outfit)){event.preventDefault();event.stopPropagation();document.querySelectorAll('.drop-ready').forEach(function(el){if(el!==node)el.classList.remove('drop-ready');});event.dataTransfer.dropEffect='copy';node.classList.add('drop-ready');}});
    node.addEventListener('dragleave',function(event){if(!node.contains(event.relatedTarget))node.classList.remove('drop-ready');});
    node.addEventListener('drop',function(event){
      node.classList.remove('drop-ready');if(options.enabled&&!options.enabled())return;event.preventDefault();event.stopPropagation();
      try{
        if(event.dataTransfer.files.length){if(!options.files)throw new Error(L("ui.import.outfit.packages.in.unity.then.refresh.the.wardrobe","Import outfit packages in Unity, then refresh the wardrobe."));return options.files(Array.from(event.dataTransfer.files));}
        var text=event.dataTransfer.getData(mime);if(text.length>8192)throw new Error(L("ui.invalid.drag.payload","Invalid drag payload."));var value=normalize(JSON.parse(text));
        if(!options.outfit)throw new Error(L("ui.choose.a.supported.drop.target","Choose a supported drop target."));options.outfit(value);
      }catch(error){if(options.error)options.error(error.message);}
      finally{current=null;}
    });
  }
  global.WardrobeDragDrop={source:source,target:target,normalize:normalize};
})(window);

/* Catalog organization is browser-only; membership uses stable family IDs. */
(function(global){
  'use strict';
  global.WardrobeGridFolders=function(options){
    var R=global.WardrobeRuntime,scope='',folders=[],grid=options.grid,finishReveal=null,displacementAnimations=[];
    function read(){
      try{var value=JSON.parse(R.stored('wardrobeGridFolders:'+scope,'[]'));folders=Array.isArray(value)?value.filter(function(f){return f&&typeof f.id==='string'&&typeof f.name==='string'&&Array.isArray(f.members);}).map(function(f){return {id:f.id,name:f.name,members:Array.from(new Set(f.members.filter(function(id){return typeof id==='string';}))),open:!!f.open};}):[];}catch(error){folders=[];}
    }
    function save(){
      try{localStorage.setItem('wardrobeGridFolders:'+scope,JSON.stringify(folders));return true;}
      catch(error){options.error('Could not save folders in this browser.');read();return false;}
    }
    function change(){save();options.render();}
    function owner(id){return folders.find(function(f){return f.members.includes(id);});}
    function move(id,folder){folders.forEach(function(f){f.members=f.members.filter(function(member){return member!==id;});});if(folder)folder.members.push(id);}
    function create(ids){
      var name=global.prompt('Folder name');if(!name||!name.trim())return;
      var folder={id:crypto.randomUUID(),name:name.trim(),members:[],open:true};folders.push(folder);
      (ids||[]).forEach(function(id){move(id,folder);});change();
    }
    var toolbar=document.createElement('div');toolbar.className='grid-folder-toolbar';
    toolbar.innerHTML='<button type="button" class="grid-new-folder">＋ New folder</button><span>Drop outfits into folders to organize this grid. Saved in this browser.</span><div class="grid-ungroup-drop">Drop here to ungroup</div>';
    grid.before(toolbar);
    toolbar.querySelector('.grid-new-folder').onclick=function(){create([]);};
    global.WardrobeDragDrop.target(toolbar.querySelector('.grid-new-folder'),{outfit:function(payload){create([payload.familyId]);},error:options.error});
    global.WardrobeDragDrop.target(toolbar.querySelector('.grid-ungroup-drop'),{outfit:function(payload){move(payload.familyId,null);change();},error:options.error});
    function attach(card){
      if(card._folderTarget)return;card._folderTarget=true;
      global.WardrobeDragDrop.target(card,{outfit:function(payload){
        var target=card._family.id;if(payload.familyId===target)return;
        var folder=owner(target);if(folder){move(payload.familyId,folder);change();}else create([target,payload.familyId]);
      },error:options.error});
    }
    function tileKey(node){return node._family?'item:'+node._family.id:node.dataset.folder?'folder:'+node.dataset.folder:null;}
    function gridPositions(){
      var positions=new Map(),base=grid.getBoundingClientRect();
      Array.from(grid.children).forEach(function(node){
        var key=tileKey(node);if(!key||node.hidden)return;
        var rect=node.getBoundingClientRect();if(!rect.width||!rect.height)return;
        positions.set(key,{x:rect.left-base.left,y:rect.top-base.top});
      });
      return positions;
    }
    function animateDisplacement(before){
      var base=grid.getBoundingClientRect();
      Array.from(grid.children).forEach(function(node){
        var previous=before.get(tileKey(node));if(!previous||node.hidden)return;
        var rect=node.getBoundingClientRect(),dx=previous.x-(rect.left-base.left),dy=previous.y-(rect.top-base.top);
        if(Math.abs(dx)<.5&&Math.abs(dy)<.5)return;
        var animation=node.animate([{transform:'translate('+dx+'px,'+dy+'px)'},{transform:'translate(0px,0px)'}],{duration:450,easing:'cubic-bezier(.22,.75,.2,1)'});
        displacementAnimations.push(animation);
        animation.finished.then(function(){displacementAnimations=displacementAnimations.filter(function(entry){return entry!==animation;});},function(){});
      });
    }
    function render(items){
      displacementAnimations.forEach(function(animation){animation.cancel();});displacementAnimations=[];
      if(finishReveal)finishReveal();
      var ranks=new Map(items.map(function(item,index){return [item.id,index];}));
      grid.querySelectorAll('.grid-folder').forEach(function(node){node.remove();});
      var cards=new Map();
      Array.from(grid.children).forEach(function(card){
        if(!card._family)return;cards.set(card._family.id,card);attach(card);
        var badge=card.querySelector('.grid-folder-badge');if(badge)badge.remove();
        card.classList.remove('in-grid-folder');delete card.dataset.gridFolder;
      });
      var visible=new Set(Array.from(cards).filter(function(pair){return !pair[1].hidden;}).map(function(pair){return pair[0];}));
      folders.forEach(function(folder){
        var members=folder.members.filter(function(id){return cards.has(id)&&visible.has(id);}).sort(function(a,b){return ranks.get(a)-ranks.get(b);});
        if(!members.length&&folder.members.length&&options.filtered())return;
        var tile=document.createElement('section');tile.className='grid-folder'+(folder.open?' is-open':'');tile.dataset.folder=folder.id;
        tile.innerHTML='<button type="button" class="grid-folder-toggle"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M3 6h7l2 2h9v12H3z M3 6V4h7l2 2"/></svg><strong></strong><span></span></button><div class="grid-folder-actions"><button type="button" data-folder-rename>Rename</button><button type="button" data-folder-delete>Delete folder</button></div>';
        if(folder.open){
          var indicator=document.createElement('span');indicator.className='grid-folder-open-indicator';indicator.textContent='Open';tile.appendChild(indicator);
          tile.querySelector('svg path').setAttribute('d','M3 7V4h7l2 2h8v4 M3 7v13h16l3-10H7L3 20');
        }
        var toggle=tile.querySelector('.grid-folder-toggle');toggle.setAttribute('aria-expanded',String(folder.open));
        toggle.querySelector('strong').textContent=folder.name;
        toggle.querySelector('span').textContent=(folder.open?'▾ ':'▸ ')+members.length+' of '+folder.members.length+' outfits shown';
        toggle.onclick=function(){
          var reducedMotion=global.matchMedia('(prefers-reduced-motion: reduce)').matches;
          var previousPositions=reducedMotion?null:gridPositions();
          folder.open=!folder.open;change();
          if(previousPositions)animateDisplacement(previousPositions);
          var tile=Array.from(grid.querySelectorAll('.grid-folder')).find(function(node){return node.dataset.folder===folder.id;});
          if(tile)tile.querySelector('.grid-folder-toggle').focus({preventScroll:true});
          if(!folder.open||reducedMotion)return;
          var members=Array.from(grid.querySelectorAll('.in-grid-folder')).filter(function(card){return !card.hidden&&card.dataset.gridFolder===folder.id;});
          if(!members.length)return;
          var rects=members.map(function(card){return card.getBoundingClientRect();}),base=grid.getBoundingClientRect();
          var origin=tile.getBoundingClientRect();
          var left=Math.min(origin.left,Math.min.apply(null,rects.map(function(r){return r.left;}))),top=Math.min(origin.top,Math.min.apply(null,rects.map(function(r){return r.top;})));
          var right=Math.max.apply(null,rects.map(function(r){return r.right;})),bottom=Math.max.apply(null,rects.map(function(r){return r.bottom;}));
          // All cards start in one stack behind the folder and fan rightward
          // into their grid positions on the same timeline.
          var reveal=document.createElement('div');reveal.className='grid-folder-expansion';reveal.setAttribute('aria-hidden','true');reveal.inert=true;
          Object.assign(reveal.style,{left:(left-base.left)+'px',top:(top-base.top)+'px',width:(right-left)+'px',height:(bottom-top)+'px'});
          var animations=[];
          grid.appendChild(reveal);
          members.forEach(function(card,index){
            var clone=card.cloneNode(true),rect=rects[index];
            // Preview URLs are revoked after decode; cloning their src loses
            // the pixels. Snapshot the decoded image for the temporary spread.
            var images=card.querySelectorAll('img');
            clone.querySelectorAll('img').forEach(function(image,index){
              var source=images[index];if(!source||!source.naturalWidth)return;
              var canvas=document.createElement('canvas');canvas.width=source.naturalWidth;canvas.height=source.naturalHeight;
              canvas.className=image.className;canvas.style.cssText='display:block;width:100%;height:100%;object-fit:contain';
              canvas.getContext('2d').drawImage(source,0,0);image.replaceWith(canvas);
            });
            clone.removeAttribute('id');clone.querySelectorAll('[id]').forEach(function(node){node.removeAttribute('id');});
            Object.assign(clone.style,{position:'absolute',left:(rect.left-left)+'px',top:(rect.top-top)+'px',width:rect.width+'px',height:rect.height+'px',margin:'0',contentVisibility:'visible'});
            clone.style.zIndex=String(members.length-index);
            reveal.appendChild(clone);card.style.visibility='hidden';
            var stackOffset=Math.min(index,5)*4;
            animations.push(clone.animate([
              {transform:'translate('+(origin.left+24+stackOffset-rect.left)+'px,'+(origin.top+8+stackOffset-rect.top)+'px) rotate(-3deg) scale(.97)'},
              {transform:'translate(0px,0px) rotate(0deg) scale(1)'}
            ],{duration:420+Math.min(index*25,125),easing:'cubic-bezier(.22,.75,.2,1)',fill:'both'}));
          });
          var clean=function(){animations.forEach(function(animation){animation.cancel();});reveal.remove();members.forEach(function(card){card.style.removeProperty('visibility');});if(finishReveal===clean)finishReveal=null;};
          finishReveal=clean;Promise.all(animations.map(function(animation){return animation.finished;})).then(clean,function(){});
        };
        tile.querySelector('[data-folder-rename]').onclick=function(){var name=global.prompt('Folder name',folder.name);if(name&&name.trim()){folder.name=name.trim();change();}};
        tile.querySelector('[data-folder-delete]').onclick=function(){if(!global.confirm('Delete folder “'+folder.name+'”? Its outfits will stay in the grid.'))return;folders=folders.filter(function(f){return f!==folder;});change();};
        global.WardrobeDragDrop.target(tile,{outfit:function(payload){move(payload.familyId,folder);change();},error:options.error});
        grid.appendChild(tile);
        members.forEach(function(id){
          var card=cards.get(id);card.hidden=!folder.open;card.classList.add('in-grid-folder');card.dataset.gridFolder=folder.id;
          var badge=document.createElement('div');badge.className='grid-folder-badge';
          var name=document.createElement('span');name.textContent='▱ '+folder.name;name.title=folder.name;badge.appendChild(name);
          var remove=document.createElement('button');remove.type='button';remove.textContent='×';remove.title='Move out of '+folder.name;remove.setAttribute('aria-label',remove.title);
          remove.onclick=function(event){event.stopPropagation();move(id,null);change();};badge.appendChild(remove);
          card.appendChild(badge);grid.appendChild(card);
        });
      });
      var blocks=[];
      grid.querySelectorAll('.grid-folder').forEach(function(tile){
        var folder=folders.find(function(f){return f.id===tile.dataset.folder;});
        var members=folder.members.filter(function(id){return cards.has(id)&&visible.has(id);}).sort(function(a,b){return ranks.get(a)-ranks.get(b);});
        blocks.push({folder:folder,rank:members.length?ranks.get(members[0]):Infinity,nodes:[tile].concat(members.map(function(id){return cards.get(id);}))});
      });
      cards.forEach(function(card,id){if(!owner(id))blocks.push({rank:ranks.get(id),nodes:[card]});});
      blocks.sort(function(a,b){
        if(!!a.folder!==!!b.folder)return a.folder?-1:1;
        if(a.folder&&b.folder&&options.sort&&options.sort()==='name')return a.folder.name.localeCompare(b.folder.name,undefined,{numeric:true,sensitivity:'base'});
        return (a.rank-b.rank)||(a.folder&&b.folder?a.folder.name.localeCompare(b.folder.name):0);
      }).forEach(function(block){block.nodes.forEach(function(node){grid.appendChild(node);});});
    }
    return {render:render,setScope:function(next){if(scope===next)return false;scope=next;read();return true;}};
  };
})(window);
