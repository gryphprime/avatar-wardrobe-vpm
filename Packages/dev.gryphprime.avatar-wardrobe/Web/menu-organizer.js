/* Presentation-only menu editing; the host supplies the pinned-session API adapter. */
(function(global){
  'use strict';
  var config={api:null,T:null,root:null},snapshot=null,rootId=0,nodeId='',busy=false,generation=0;
  var statusNode=null,treeNode=null,detailNode=null,drag=null;
  var MIME='application/x-avatar-wardrobe-menu-node';
  function T(key,fallback){var value=config.T?config.T('menuOrganizer.'+key):null;return value&&value!=='menuOrganizer.'+key?value:fallback;}
  function host(){return typeof config.root==='string'?document.querySelector(config.root):(config.root||document.getElementById('menuOrganizer'));}
  function el(tag,text,cls){var value=document.createElement(tag);if(text!=null)value.textContent=text;if(cls)value.className=cls;return value;}
  function button(text,fn,locked){var value=el('button',text,'btn');value.type='button';value.onclick=fn;value.dataset.locked=String(!!locked);value.disabled=!!locked||busy;return value;}
  function field(title,input){var label=el('label',null,'scene-editor-field');label.append(el('span',title),input);return label;}
  function status(message,error){if(statusNode){statusNode.textContent=message||'';statusNode.classList.toggle('error',!!error);}}
  function selectedRoot(){return snapshot&&snapshot.roots.find(function(root){return root.id===rootId;});}
  function selectedNode(){var root=selectedRoot();return root&&root.nodes.find(function(node){return node.id===nodeId;});}
  function container(node){return node&&['folder','submenu','switchingGroup'].includes(node.kind);}
  function setBusy(value){busy=value;if(host()){host().setAttribute('aria-busy',String(value));host().querySelectorAll('button,input,select').forEach(function(item){item.disabled=value||item.dataset.locked==='true';});}}
  async function api(path,options){
    if(typeof config.api!=='function')throw new Error(T('unavailable','Menu editing is unavailable in this host.'));
    var result=await config.api(path,options||{});
    if(!result||result.ok===0)throw new Error(result&&result.message||T('failed','The menu request failed. Refresh and review before trying again.'));
    return result;
  }
  async function fetchSnapshot(ticket){
    var result=await api('/api/menu_snapshot');
    if(ticket!==generation)return false;
    if(!Array.isArray(result.roots)||!result.revision||!result.avatarId)throw new Error(T('invalid','Unity returned an invalid menu snapshot.'));
    if(snapshot&&snapshot.avatarId!==result.avatarId){rootId=0;nodeId='';}
    snapshot=result;
    if(!selectedRoot()){rootId=result.roots.length?result.roots[0].id:0;nodeId='';}
    if(!selectedNode())nodeId='';
    render();return true;
  }
  async function load(){
    if(!host()||busy)return;
    var ticket=++generation;setBusy(true);render();status(T('loading','Loading the pinned avatar’s menus…'));
    try{if(await fetchSnapshot(ticket))status(T('ready','Choose a control to change where it appears.'));}
    catch(error){if(ticket===generation){snapshot=null;render();status(error.message,true);}}
    finally{if(ticket===generation)setBusy(false);}
  }
  async function run(action,extra){
    var root=selectedRoot();if(busy||!snapshot||!root)return;
    var command=Object.assign({action:action,avatarId:snapshot.avatarId,rootId:root.id,revision:snapshot.revision,nodeId:nodeId},extra||{});
    var ticket=++generation;setBusy(true);status(T('applying','Applying menu layout…'));var applied=false;
    try{
      var result=await api('/api/menu_execute?command='+encodeURIComponent(JSON.stringify(command)),{method:'POST'});
      if(ticket!==generation)return;
      if(action==='inspect'){status(result.message||T('nativeOpened','Menu selected in Unity Inspector.'));return;}
      applied=true;
      if(await fetchSnapshot(ticket))status(result.message||T('applied','Layout applied. Save the scene to keep it; Undo is available in Unity.'));
    }catch(error){
      if(ticket===generation){
        // Delivery may have succeeded. Do not replay a command or reuse an old revision.
        snapshot=null;render();status((applied?T('refreshApplied','Layout applied, but its refreshed state is unavailable. Refresh Menu. '):'')+error.message,true);
      }
    }finally{if(ticket===generation)setBusy(false);}
  }
  function descendant(id,ancestor,root){var seen=new Set();while(id&&!seen.has(id)){if(id===ancestor)return true;seen.add(id);var node=root.nodes.find(function(item){return item.id===id;});id=node?node.parentId:'';}return false;}
  function kind(node){return node.kind==='folder'?T('folder','Folder'):node.kind==='switchingGroup'?T('switching','Only one active'):node.kind==='submenu'?T('submenu','Submenu'):node.kind==='readonly'?T('readOnly','Read-only'):T('control','Control');}
  function clearDrop(){if(host())host().querySelectorAll('.menu-organizer-drop-active').forEach(function(item){item.classList.remove('menu-organizer-drop-active');});}
  function dropTarget(target,parentId,beforeId,text){
    target.ondragover=function(event){if(!drag||!event.dataTransfer.types.includes(MIME))return;event.preventDefault();event.stopPropagation();event.dataTransfer.dropEffect='move';clearDrop();target.classList.add('menu-organizer-drop-active');status(text);};
    target.ondragleave=function(){target.classList.remove('menu-organizer-drop-active');};
    target.ondrop=function(event){
      if(!drag||!event.dataTransfer.types.includes(MIME))return;event.preventDefault();event.stopPropagation();clearDrop();
      var payload;try{payload=JSON.parse(event.dataTransfer.getData(MIME));}catch(error){status(T('staleDrag','Drag this control again from the current Menu view.'),true);return;}
      if(!snapshot||busy||payload.avatarId!==snapshot.avatarId||payload.rootId!==rootId||payload.revision!==snapshot.revision||payload.nodeId!==drag.nodeId){status(T('staleDrag','Drag this control again from the current Menu view.'),true);return;}
      var moving=payload.nodeId;drag=null;if(host())host().classList.remove('menu-organizer-dragging');
      if(moving===beforeId)return;
      run('move',{nodeId:moving,parentId:parentId,beforeId:beforeId});
    };
  }
  function drawTree(){
    if(!treeNode)return;treeNode.replaceChildren();var root=selectedRoot();if(!root)return;
    var canEdit=root.editable&&root.layoutId;
    function append(parent,depth){
      root.nodes.filter(function(node){return (node.parentId||'')===parent;}).forEach(function(node){
        var row=el('div',null,'menu-organizer-row');row.style.setProperty('--menu-depth',String(depth));
        var choice=button('',function(){nodeId=node.id;drawTree();drawDetail();});choice.className='menu-organizer-control';choice.dataset.nodeId=node.id;
        choice.setAttribute('role','treeitem');choice.setAttribute('aria-level',String(depth+1));choice.setAttribute('aria-selected',String(nodeId===node.id));
        if(container(node))choice.setAttribute('aria-expanded','true');if(nodeId===node.id)choice.classList.add('selected');
        var label=el('span',node.label,'menu-organizer-label'),description=kind(node)+(node.isDefault?' · '+T('default','Default'):'');
        choice.append(label,el('small',description,'subtle'));choice.title=node.label+' — '+description;
        choice.onkeydown=function(event){
          var choices=Array.from(treeNode.querySelectorAll('[data-node-id]')),index=choices.indexOf(choice),next=null;
          if(event.key==='ArrowDown')next=choices[Math.min(index+1,choices.length-1)];else if(event.key==='ArrowUp')next=choices[Math.max(index-1,0)];
          else if(event.key==='Home')next=choices[0];else if(event.key==='End')next=choices[choices.length-1];
          if(next){event.preventDefault();next.focus();}
        };
        if(canEdit){
          choice.draggable=true;choice.ondragstart=function(event){
            if(busy){event.preventDefault();return;}
            drag={avatarId:snapshot.avatarId,rootId:root.id,revision:snapshot.revision,nodeId:node.id};event.dataTransfer.setData(MIME,JSON.stringify(drag));event.dataTransfer.effectAllowed='move';host().classList.add('menu-organizer-dragging');
          };
          choice.ondragend=function(){drag=null;clearDrop();if(host())host().classList.remove('menu-organizer-dragging');};
          dropTarget(row,node.parentId||'',node.id,T('dropBefore','Move before ')+node.label);
        }
        row.append(choice);treeNode.append(row);
        if(container(node)){
          if(canEdit){var into=el('div',T('dropInto','Move into ')+node.label,'menu-organizer-drop');into.style.setProperty('--menu-depth',String(depth+1));dropTarget(into,node.id,'',T('dropInto','Move into ')+node.label);treeNode.append(into);}
          append(node.id,depth+1);
        }
      });
    }
    append('',0);
    if(canEdit){var end=el('div',T('dropEnd','Move to end of this menu'),'menu-organizer-drop');dropTarget(end,'','',end.textContent);treeNode.append(end);}
    if(!root.nodes.length)treeNode.append(el('p',T('emptyMenu','This menu has no controls yet.'),'subtle'));
  }
  function parentOptions(root,except,value){
    var select=el('select'),top=el('option',T('menuRoot','Top of this menu'));top.value='';select.append(top);
    root.nodes.filter(function(node){return container(node)&&(!except||!descendant(node.id,except,root));}).forEach(function(node){var option=el('option',node.label+' · '+kind(node));option.value=node.id;select.append(option);});
    select.value=value||'';return select;
  }
  function folderForm(root){
    var form=el('form',null,'menu-organizer-folder'),name=el('input');name.type='text';name.maxLength=80;name.required=true;
    var selected=selectedNode(),parent=parentOptions(root,null,container(selected)?selected.id:(selected&&selected.parentId||''));
    var create=function(){if(form.reportValidity())run('folder',{label:name.value,parentId:parent.value});};
    form.append(el('h3',T('newFolder','New folder')),field(T('folderName','Folder name'),name),field(T('location','Location'),parent),button(T('createFolder','Create folder'),create));
    form.onsubmit=function(event){event.preventDefault();create();};return form;
  }
  function drawDetail(){
    if(!detailNode)return;detailNode.replaceChildren();var root=selectedRoot(),node=selectedNode();if(!root)return;
    if(!root.editable){detailNode.append(el('h3',T('readOnlyMenu','Read-only menu')),el('p',root.reason,'subtle'),button(T('native','Open in Unity Inspector'),function(){run('inspect');}));return;}
    if(!root.layoutId){detailNode.append(el('h3',T('startTitle','Arrange this menu')),el('p',T('startBody','Create a scene layout to reorder these controls and add folders. Clothing and switching defaults keep their current behavior.'),'subtle'),button(T('start','Organize this menu'),function(){run('initialize');}));return;}
    if(node){
      detailNode.append(el('h3',node.label),el('p',kind(node)+(node.isDefault?' · '+T('default','Default'):''),'subtle'));
      if(node.kind==='switchingGroup')detailNode.append(el('p',T('groupHelp','These controls still switch each other off wherever you place them. Use the outfit’s Only one active settings to change that behavior.'),'subtle'));
      var siblings=root.nodes.filter(function(item){return item.parentId===node.parentId;}),index=siblings.indexOf(node),actions=el('div',null,'scene-editor-actions');
      actions.append(button(T('up','Move up'),function(){run('move',{parentId:node.parentId,beforeId:siblings[index-1].id});},index===0),button(T('down','Move down'),function(){run('move',{parentId:node.parentId,beforeId:index+2<siblings.length?siblings[index+2].id:''});},index===siblings.length-1));detailNode.append(actions);
      var parent=parentOptions(root,node.id,node.parentId),before=el('select');
      function insertion(){before.replaceChildren();var end=el('option',T('atEnd','At the end'));end.value='';before.append(end);root.nodes.filter(function(item){return item.id!==node.id&&(item.parentId||'')===parent.value;}).forEach(function(item){var option=el('option',T('before','Before ')+item.label);option.value=item.id;before.append(option);});}
      parent.onchange=insertion;insertion();
      detailNode.append(field(T('destination','Move to'),parent),field(T('insert','Insert'),before),button(T('move','Move control'),function(){run('move',{parentId:parent.value,beforeId:before.value});}));
      if(node.kind==='folder'){
        var rename=el('form',null,'menu-organizer-rename'),name=el('input');name.value=node.label;name.maxLength=80;name.required=true;
        var apply=function(){if(rename.reportValidity())run('rename',{label:name.value});};rename.append(field(T('folderName','Folder name'),name),button(T('rename','Rename folder'),apply));rename.onsubmit=function(event){event.preventDefault();apply();};detailNode.append(rename);
        var hasChildren=root.nodes.some(function(item){return item.parentId===node.id;});detailNode.append(button(T('removeFolder','Remove empty folder'),function(){run('deleteFolder');},hasChildren));
        if(hasChildren)detailNode.append(el('p',T('emptyFirst','Move its controls out before removing this folder.'),'subtle'));
      }
      if(node.parameter){var info=el('details',null,'scene-editor-component');info.append(el('summary',T('controlDetails','Control details')),el('p',T('parameter','Parameter')+': '+node.parameter,'subtle'),el('p',T('parameterHelp','Presentation changes preserve this parameter and its default control.'),'subtle'));detailNode.append(info);}
    }else detailNode.append(el('p',T('choose','Choose a control to move it, or create a folder.'),'subtle'));
    detailNode.append(folderForm(root),button(T('native','Open in Unity Inspector'),function(){run('inspect');}));
  }
  function render(){
    var target=host();if(!target)return;target.replaceChildren();target.classList.add('menu-organizer','scene-editor');
    var toolbar=el('div',null,'scene-editor-toolbar');toolbar.append(el('h2',T('title','Menu')),button(T('refresh','Refresh Menu'),load));
    target.append(toolbar,el('p',T('intro','Arrange where Wardrobe controls appear. Folders change placement; Only one active groups control which outfits switch each other off.'),'subtle'));
    statusNode=el('p',null,'scene-editor-status');statusNode.setAttribute('role','status');statusNode.setAttribute('aria-live','polite');target.append(statusNode);
    if(snapshot&&!snapshot.roots.length){target.append(el('p',T('empty','There are no menu installers on this avatar. Add a Wardrobe toggle or an Only one active group to begin.'),'subtle'));return;}
    if(snapshot){var roots=el('select');roots.setAttribute('aria-label',T('selectMenu','Choose a menu'));snapshot.roots.forEach(function(root){var option=el('option',root.name+(snapshot.roots.filter(function(other){return other.name===root.name;}).length>1?' · '+root.path+' · #'+root.id:'')+(root.editable?'':' · '+T('readOnly','Read-only')));option.value=String(root.id);roots.append(option);});roots.value=String(rootId);roots.onchange=function(){rootId=Number(roots.value);nodeId='';drawTree();drawDetail();status(T('ready','Choose a control to change where it appears.'));};target.append(field(T('selectMenu','Choose a menu'),roots));}
    var grid=el('div',null,'scene-editor-grid');treeNode=el('div',null,'scene-editor-tree menu-organizer-tree');treeNode.setAttribute('role','tree');treeNode.setAttribute('aria-label',T('tree','Menu controls'));
    detailNode=el('div',null,'scene-editor-inspector');grid.append(treeNode,detailNode);target.append(grid);drawTree();drawDetail();setBusy(busy);
  }
  global.WardrobeMenuOrganizer={configure:function(options){config=Object.assign(config,options||{});return this;},show:function(){if(busy){generation++;busy=false;snapshot=null;}return load();}};
})(window);
