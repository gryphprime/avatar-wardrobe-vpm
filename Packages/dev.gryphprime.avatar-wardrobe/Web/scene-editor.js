/* Optional exact-object editor. The host supplies the pinned-session API adapter. */
(function(global){
  'use strict';
  var config={api:null,T:null,root:null},snapshot=null,selectedId=0,busy=false,query='',generation=0;
  var expanded=new Set(),statusNode=null,treeNode=null,inspectorNode=null;
  function T(key,fallback){var value=config.T?config.T('scene.'+key):null;return value&&value!=='scene.'+key?value:fallback;}
  function root(){return typeof config.root==='string'?document.querySelector(config.root):(config.root||document.getElementById('advancedScene'));}
  function node(tag,text,className){var value=document.createElement(tag);if(text!=null)value.textContent=text;if(className)value.className=className;return value;}
  function button(text,click,className){var value=node('button',text,className||'btn');value.type='button';value.onclick=click;return value;}
  function status(message,error){if(statusNode){statusNode.textContent=message||'';statusNode.classList.toggle('error',!!error);}}
  function api(path,options){
    if(typeof config.api!=='function')return Promise.reject(new Error(T('unavailable','Scene editing is unavailable in this host.')));
    return Promise.resolve(config.api(path,options||{})).then(function(result){
      if(!result||result.ok===0)throw new Error(result&&result.message||T('failed','The scene request failed. Refresh and review before trying again.'));
      return result;
    });
  }
  function selected(){return snapshot&&snapshot.objects.find(function(item){return item.id===selectedId;});}
  function setBusy(value){busy=value;var host=root();if(host){host.setAttribute('aria-busy',String(value));host.querySelectorAll('button,input,select').forEach(function(control){control.disabled=value||control.dataset.locked==='true';});}}
  async function run(action,extra){
    if(busy||!snapshot)return;
    var item=selected();if(!item)return;
    var command=Object.assign({action:action,avatarId:snapshot.avatarId,objectId:item.id,revision:snapshot.revision},extra||{});
    setBusy(true);status(T('applying','Applying the scene edit…'));
    try{
      var result=await api('/api/scene_execute?command='+encodeURIComponent(JSON.stringify(command)),{method:'POST'});
      if(action==='inspect'){status(result.message||T('nativeOpened','Object selected in Unity Inspector.'));return;}
      if(result.id)selectedId=Number(result.id);
      if(action==='delete')selectedId=item.parentId;
      await load(false);
      status(result.message||T('updated','Scene updated. Undo is available in Unity.'));
    }catch(error){
      // A failed request may have reached Unity. Never replay a mutation or keep
      // editable controls carrying its old revision.
      snapshot=null;
      if(inspectorNode)inspectorNode.replaceChildren(node('p',T('reviewAgain','Refresh the scene and review the object before continuing.')));
      if(treeNode)treeNode.replaceChildren();
      status(error.message,true);
    }finally{setBusy(false);}
  }
  function label(text,control){var row=node('label',null,'scene-editor-field');row.append(node('span',text),control);return row;}
  function input(value,type){var field=document.createElement('input');field.type=type||'text';if(field.type==='checkbox')field.checked=!!value;else field.value=value==null?'':String(value);return field;}
  function isDescendant(id,parentId){var seen=new Set();while(id&&!seen.has(id)){if(id===parentId)return true;seen.add(id);var current=snapshot.objects.find(function(item){return item.id===id;});id=current?current.parentId:0;}return false;}
  function displayPath(item){return (item.path||item.name)+' · #'+item.id;}
  function select(id){selectedId=id;drawTree();drawInspector();}
  function drawTree(){
    if(!treeNode||!snapshot)return;
    treeNode.replaceChildren();
    var byId=new Map(snapshot.objects.map(function(item){return [item.id,item];}));
    var children=new Map();snapshot.objects.forEach(function(item){if(!children.has(item.parentId))children.set(item.parentId,[]);children.get(item.parentId).push(item);});
    var matches=new Set(),term=query.trim().toLowerCase();
    if(term)snapshot.objects.forEach(function(item){if((item.name+' '+item.path+' '+item.id).toLowerCase().includes(term)){var current=item;while(current&&!matches.has(current.id)){matches.add(current.id);current=byId.get(current.parentId);}}});
    snapshot.objects.forEach(function(item){
      if(term&&!matches.has(item.id))return;
      if(!term){var parent=byId.get(item.parentId);while(parent){if(!expanded.has(parent.id))return;parent=byId.get(parent.parentId);}}
      var row=node('div',null,'scene-editor-tree-row');row.style.setProperty('--scene-depth',String(item.depth));
      var hasChildren=children.has(item.id),opened=term||expanded.has(item.id);
      var toggle=button('',function(){if(expanded.has(item.id))expanded.delete(item.id);else expanded.add(item.id);drawTree();},'scene-editor-expand');
      if(hasChildren){var svg=document.createElementNS('http://www.w3.org/2000/svg','svg'),path=document.createElementNS('http://www.w3.org/2000/svg','path');svg.setAttribute('viewBox','0 0 12 12');svg.setAttribute('width','12');svg.setAttribute('height','12');svg.setAttribute('aria-hidden','true');path.setAttribute('d',opened?'M2 4 L6 8 L10 4':'M4 2 L8 6 L4 10');path.setAttribute('fill','none');path.setAttribute('stroke','currentColor');path.setAttribute('stroke-width','1.5');svg.append(path);toggle.append(svg);}
      toggle.setAttribute('aria-label',(opened?T('collapse','Collapse '):T('expand','Expand '))+item.name);
      toggle.disabled=!hasChildren;toggle.dataset.locked=String(!hasChildren);toggle.tabIndex=-1;
      var choice=button(item.name,function(){select(item.id);},'scene-editor-object');choice.dataset.nodeId=String(item.id);
      choice.setAttribute('role','treeitem');choice.setAttribute('aria-level',String(item.depth+1));choice.setAttribute('aria-selected',String(item.id===selectedId));
      if(hasChildren)choice.setAttribute('aria-expanded',String(!!opened));choice.title=displayPath(item);
      if(!item.active)choice.classList.add('inactive');if(item.id===selectedId)choice.classList.add('selected');
      choice.onkeydown=function(event){
        var choices=Array.from(treeNode.querySelectorAll('[data-node-id]')),index=choices.indexOf(choice),next=null;
        if(event.key==='ArrowDown')next=choices[Math.min(index+1,choices.length-1)];
        else if(event.key==='ArrowUp')next=choices[Math.max(index-1,0)];
        else if(event.key==='Home')next=choices[0];else if(event.key==='End')next=choices[choices.length-1];
        else if(event.key==='ArrowRight'&&hasChildren){expanded.add(item.id);drawTree();next=treeNode.querySelector('[data-node-id="'+item.id+'"]');}
        else if(event.key==='ArrowLeft'){if(expanded.has(item.id)){expanded.delete(item.id);drawTree();next=treeNode.querySelector('[data-node-id="'+item.id+'"]');}else next=treeNode.querySelector('[data-node-id="'+item.parentId+'"]');}
        if(next){event.preventDefault();next.focus();}
      };
      row.append(toggle,choice);treeNode.append(row);
    });
    if(!treeNode.childElementCount)treeNode.append(node('p',T('noObjects','No objects match this search.'),'subtle'));
  }
  function vectorFields(title,vector){
    var group=node('fieldset',null,'scene-editor-vector');group.append(node('legend',title));var fields={};
    ['x','y','z'].forEach(function(axis){var field=input(vector[axis],'number');field.step='any';field.required=true;fields[axis]=field;group.append(label(axis.toUpperCase(),field));});
    return {node:group,value:function(){var result={};Object.keys(fields).forEach(function(axis){var number=Number(fields[axis].value);if(fields[axis].value.trim()===''||!Number.isFinite(number))throw new Error(T('finite','Enter a finite value for every transform axis.'));result[axis]=number;});return result;}};
  }
  function drawInspector(){
    if(!inspectorNode)return;inspectorNode.replaceChildren();var item=selected();
    if(!item){inspectorNode.append(node('p',T('select','Select an object from the hierarchy.'),'subtle'));return;}
    var title=node('h3',item.name);inspectorNode.append(title,node('p',displayPath(item),'subtle'));
    var identity=node('form',null,'scene-editor-name');var name=input(item.name);name.maxLength=200;name.required=true;
    identity.append(label(T('name','Name'),name),button(T('rename','Rename'),function(){if(identity.reportValidity())run('rename',{name:name.value});}));
    identity.onsubmit=function(event){event.preventDefault();if(identity.reportValidity())run('rename',{name:name.value});};inspectorNode.append(identity);
    var active=input(item.active,'checkbox');active.onchange=function(){run('active',{active:active.checked});};inspectorNode.append(label(T('active','Active'),active));
    var transform=node('form',null,'scene-editor-transform');
    var position=vectorFields(T('position','Local position'),item.position),rotation=vectorFields(T('rotation','Local rotation'),item.rotation),scale=vectorFields(T('scale','Local scale'),item.scale);
    var applyTransform=button(T('applyTransform','Apply transform'),function(){if(!transform.reportValidity())return;try{run('transform',{position:position.value(),rotation:rotation.value(),scale:scale.value()});}catch(error){status(error.message,true);}});
    transform.append(position.node,rotation.node,scale.node,applyTransform);transform.onsubmit=function(event){event.preventDefault();applyTransform.click();};inspectorNode.append(transform);
    var actions=node('div',null,'scene-editor-actions');var isRoot=item.parentId===0;
    var duplicate=button(T('duplicate','Duplicate object'),function(){run('duplicate');});duplicate.disabled=isRoot;duplicate.dataset.locked=String(isRoot);
    var remove=button(T('delete','Delete object'),function(){
      var confirm=node('div',null,'scene-editor-confirm');confirm.setAttribute('role','group');confirm.setAttribute('aria-label',T('confirmDelete','Confirm object deletion'));
      confirm.append(node('p',T('deleteExact','Delete this exact object and its children?')+' '+displayPath(item)),button(T('delete','Delete object'),function(){run('delete');},'btn danger'),button(T('cancel','Cancel'),function(){confirm.remove();remove.disabled=false;}));
      actions.after(confirm);remove.disabled=true;confirm.querySelector('button').focus();
    },'btn danger');remove.disabled=isRoot;remove.dataset.locked=String(isRoot);
    actions.append(duplicate,remove,button(T('native','Open in Unity Inspector'),function(){run('inspect');}));inspectorNode.append(actions);
    var create=node('form',null,'scene-editor-create');var childName=input('');childName.placeholder=T('newObject','GameObject');childName.maxLength=200;
    create.append(label(T('childName','New child name'),childName),button(T('create','Create empty child'),function(){run('create',{parentId:item.id,name:childName.value});}));create.onsubmit=function(event){event.preventDefault();run('create',{parentId:item.id,name:childName.value});};inspectorNode.append(create);
    var move=node('div',null,'scene-editor-reparent'),parentSelect=document.createElement('select');
    snapshot.objects.filter(function(candidate){return !isDescendant(candidate.id,item.id);}).forEach(function(candidate){var option=node('option',displayPath(candidate));option.value=String(candidate.id);option.selected=candidate.id===item.parentId;parentSelect.append(option);});
    parentSelect.disabled=isRoot;parentSelect.dataset.locked=String(isRoot);var moveButton=button(T('move','Move to parent'),function(){run('reparent',{parentId:Number(parentSelect.value)});});moveButton.disabled=isRoot;moveButton.dataset.locked=String(isRoot);
    move.append(label(T('parent','Parent object'),parentSelect),moveButton);inspectorNode.append(move);
    var components=node('div',null,'scene-editor-components');
    (item.components||[]).forEach(function(component){
      var section=node('details',null,'scene-editor-component');section.append(node('summary',component.type));
      (component.properties||[]).forEach(function(property){
        var row=node('div',null,'scene-editor-property');
        if(!property.editable){row.append(node('span',property.label),node('span',property.value||T('nativeField','Use Unity Inspector'),'subtle'));section.append(row);return;}
        var control;
        if(property.kind==='enum'){control=document.createElement('select');(property.options||[]).forEach(function(option,index){var element=node('option',option);element.value=String(index);control.append(element);});control.value=property.value;}
        else if(property.kind==='bool')control=input(property.value==='true','checkbox');
        else{control=input(property.value,property.kind==='float'?'number':'text');if(property.kind==='float')control.step='any';if(property.kind==='int')control.inputMode='numeric';if(property.kind==='string')control.maxLength=4096;}
        row.append(label(property.label,control),button(T('applyField','Apply'),function(){var value=property.kind==='bool'?String(control.checked):control.value;run('property',{componentId:component.id,propertyPath:property.path,value:value});}));section.append(row);
      });
      if(!(component.properties||[]).length)section.append(node('p',T('nativeComponent','Use Unity Inspector for this component.'),'subtle'));
      components.append(section);
    });inspectorNode.append(components);
  }
  function render(){
    var host=root();if(!host)return;host.replaceChildren();host.classList.add('scene-editor');
    var toolbar=node('div',null,'scene-editor-toolbar');var search=input(query,'search');search.placeholder=T('search','Search hierarchy');search.setAttribute('aria-label',search.placeholder);search.oninput=function(){query=search.value;drawTree();};
    toolbar.append(node('h2',T('title','Advanced Scene')),search,button(T('refresh','Refresh scene'),function(){load(true);}));host.append(toolbar,node('p',T('intro','Edit objects on the pinned scene avatar. Changes support Unity Undo.'),'subtle'));
    statusNode=node('p',null,'scene-editor-status');statusNode.setAttribute('role','status');statusNode.setAttribute('aria-live','polite');host.append(statusNode);
    var grid=node('div',null,'scene-editor-grid');treeNode=node('div',null,'scene-editor-tree');treeNode.setAttribute('role','tree');treeNode.setAttribute('aria-label',T('hierarchy','Avatar hierarchy'));inspectorNode=node('div',null,'scene-editor-inspector');inspectorNode.setAttribute('aria-label',T('inspector','Object Inspector'));grid.append(treeNode,inspectorNode);host.append(grid);
    drawTree();drawInspector();setBusy(busy);
  }
  async function load(clear){
    if(!root())return null;
    var ticket=++generation;if(clear)snapshot=null;setBusy(true);
    if(!statusNode||!root().contains(statusNode))render();status(T('loading','Loading the pinned avatar hierarchy…'));
    try{
      var result=await api('/api/scene_snapshot');if(ticket!==generation)return null;
      if(!Array.isArray(result.objects)||!result.revision||!result.avatarId)throw new Error(T('invalid','Unity returned an invalid scene snapshot.'));
      var changedAvatar=!snapshot||snapshot.avatarId!==result.avatarId;snapshot=result;
      if(!selected())selectedId=result.objects.length?result.objects[0].id:0;
      if(changedAvatar)result.objects.forEach(function(item){if(item.depth<2)expanded.add(item.id);});
      var selectedObject=selected();while(selectedObject&&selectedObject.parentId){expanded.add(selectedObject.parentId);selectedObject=snapshot.objects.find(function(item){return item.id===selectedObject.parentId;});}
      render();status(T('ready','Choose an object to inspect or edit.'));return result;
    }catch(error){if(ticket===generation){snapshot=null;render();status(error.message,true);}return null;}
    finally{if(ticket===generation)setBusy(false);}
  }
  global.WardrobeSceneEditor={configure:function(options){config=Object.assign(config,options||{});return this;},show:function(){return load(true);}};
})(window);
