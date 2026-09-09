/* Focused appearance drafts, reviewed exact-slot edits, and optional native tools. */
(function(global){
  'use strict';
  var config={api:null,T:null,root:null},snapshot=null,tools=null,selectedId=0,busy=false,serial=0,review=null;
  var statusNode,partsNode,detailNode,reviewNode;
  function T(key,fallback){var value=config.T?config.T('appearance.'+key):null;return value&&value!=='appearance.'+key?value:fallback;}
  function host(){return typeof config.root==='string'?document.querySelector(config.root):(config.root||document.getElementById('appearanceEditor'));}
  function el(tag,text,cls){var value=document.createElement(tag);if(text!=null)value.textContent=text;if(cls)value.className=cls;return value;}
  function button(text,fn,locked){var value=el('button',text,'btn');value.type='button';value.onclick=fn;value.disabled=busy||!!locked;value.dataset.locked=String(!!locked);return value;}
  function field(label,input){var row=el('label',null,'scene-editor-field');row.append(el('span',label),input);return row;}
  function selected(){return snapshot&&snapshot.renderers.find(function(item){return item.id===selectedId;});}
  function message(text,error){if(statusNode){statusNode.textContent=text||'';statusNode.classList.toggle('error',!!error);}}
  function setBusy(value){busy=value;var root=host();if(root){root.setAttribute('aria-busy',String(value));root.querySelectorAll('button,input,select').forEach(function(item){item.disabled=value||item.dataset.locked==='true';});}}
  async function api(path,options){
    if(!config.api)throw new Error(T('unavailable','Appearance editing is unavailable in this host.'));
    var result=await config.api(path,options||{});if(!result||result.ok===0)throw new Error(result&&result.message||T('failed','Appearance request failed. Refresh and review before trying again.'));return result;
  }
  async function fetchSnapshot(ticket){
    var results=await Promise.all([api('/api/appearance_snapshot'),api('/api/appearance_tools').catch(function(error){return {tools:[],message:error.message};})]);
    if(ticket!==serial)return false;
    var next=results[0];if(!next.avatarId||!next.revision||!Array.isArray(next.renderers))throw new Error(T('invalid','Unity returned an invalid appearance snapshot.'));
    if(snapshot&&snapshot.avatarId!==next.avatarId)selectedId=0;snapshot=next;tools=results[1];review=null;
    if(!selected())selectedId=next.renderers.length?next.renderers[0].id:0;render();return true;
  }
  async function load(){
    if(!host()||busy)return;var ticket=++serial;setBusy(true);render();message(T('loading','Loading appearance from the pinned avatar…'));
    try{if(await fetchSnapshot(ticket))message(T('ready','Choose a renderer and material slot, or a static shape control.'));}
    catch(error){if(ticket===serial){snapshot=null;tools=null;render();message(error.message,true);}}
    finally{if(ticket===serial)setBusy(false);}
  }
  function invalidateDraft(){review=null;drawReview();message(T('draft','Draft only. Review the change before applying it.'));}
  function command(extra){var renderer=selected();return Object.assign({avatarId:snapshot.avatarId,revision:snapshot.revision,rendererId:renderer&&renderer.id},extra);}
  async function requestReview(payload,optimizer){
    if(busy||!snapshot)return;var ticket=++serial;setBusy(true);review=null;drawReview();message(optimizer?T('processing','Processing before and after copies with the default optimizer…'):T('reviewing','Reviewing this exact appearance change…'));
    try{
      var result=await api((optimizer?'/api/appearance_optimizer_review':'/api/appearance_review')+'?command='+encodeURIComponent(JSON.stringify(payload)),{method:'POST'});
      if(ticket!==serial)return;review={result:result,avatarId:snapshot.avatarId,optimizer:!!optimizer};drawReview();reviewNode.focus();message(T('reviewReady','Review ready. The working avatar has not changed.'));
    }catch(error){if(ticket===serial){review=null;drawReview();message(error.message,true);}}
    finally{if(ticket===serial)setBusy(false);}
  }
  async function apply(){
    if(busy||!review||!snapshot)return;var current=review,ticket=++serial;setBusy(true);message(T('applying','Applying the reviewed change…'));var applied=false;
    try{
      var result=await api((current.optimizer?'/api/appearance_optimizer_apply':'/api/appearance_apply')+'?review='+encodeURIComponent(JSON.stringify({avatarId:current.avatarId,token:current.result.token})),{method:'POST'});
      applied=true;if(ticket!==serial)return;if(await fetchSnapshot(ticket))message(result.message||T('applied','Applied to the scene. Save to keep it; Undo is available in Unity.'));
    }catch(error){if(ticket===serial){snapshot=null;tools=null;review=null;render();message((applied?T('appliedRefresh','The change applied, but refreshed state is unavailable. Refresh Appearance. '):'')+error.message,true);}}
    finally{if(ticket===serial)setBusy(false);}
  }
  function colorHex(color){return '#'+['r','g','b'].map(function(key){return Math.round(Math.max(0,Math.min(1,color[key]))*255).toString(16).padStart(2,'0');}).join('');}
  function colorCss(color){return 'rgba('+[color.r*255,color.g*255,color.b*255,color.a].join(',')+')';}
  function drawReview(){
    if(!reviewNode)return;reviewNode.replaceChildren();reviewNode.hidden=!review;if(!review)return;
    reviewNode.tabIndex=-1;var value=review.result;reviewNode.append(el('h3',review.optimizer?T('optimizationReview','Default optimizer review'):T('reviewTitle','Review appearance change')));
    if(value.target)reviewNode.append(el('p',value.target,'subtle'));reviewNode.append(el('p',value.message,'subtle'));
    var compare=el('div',null,'appearance-compare');
    if(review.optimizer){
      [{label:T('before','Before'),image:value.beforeImage,metrics:value.preview&&value.preview.beforeMetrics},{label:T('after','After'),image:value.afterImage,metrics:value.preview&&value.preview.afterMetrics}].forEach(function(side){
        var pane=el('div');pane.append(el('h4',side.label));if(side.image){var image=el('img');image.src='data:image/png;base64,'+side.image;image.alt=side.label+' '+T('optimizerPhoto','processed avatar with the default optimizer comparison');pane.append(image);}
        if(side.metrics){var m=side.metrics;pane.append(el('p',T('triangles','Triangles')+': '+m.triangles+' · '+T('materials','Materials')+': '+m.materials+' · '+T('textures','Textures')+': '+m.textures,'subtle'));pane.append(el('p',T('textureEstimate','Texture allocation estimate')+': '+Math.round(m.estimatedTextureBytes/1048576*10)/10+' MiB','subtle'));}
        compare.append(pane);
      });
      reviewNode.append(compare,el('p',T('optimizationLimits','These are processed editor copies. Texture allocation is an estimate, not exact platform VRAM; runtime animation and SDK upload callbacks are outside this preview.'),'subtle'));
    }else{
      [{label:T('before','Before'),text:value.before,color:value.beforeColor},{label:T('after','After'),text:value.after,color:value.afterColor}].forEach(function(side){var pane=el('div');pane.append(el('h4',side.label));if(side.color){var swatch=el('div',null,'appearance-swatch');swatch.style.backgroundColor=colorCss(side.color);swatch.setAttribute('aria-label',side.label+' '+side.text);pane.append(swatch);}pane.append(el('p',side.text||'—'));compare.append(pane);});reviewNode.append(compare);
    }
    var actions=el('div',null,'scene-editor-actions');actions.append(button(review.optimizer?T('applyOptimizer','Apply reviewed optimizer'):T('apply','Apply reviewed change'),apply),button(T('cancel','Cancel'),function(){review=null;drawReview();message(T('cancelled','Review cancelled. The working avatar is unchanged.'));}));reviewNode.append(actions);
  }
  function drawParts(){
    if(!partsNode)return;partsNode.replaceChildren();if(!snapshot)return;
    snapshot.renderers.forEach(function(renderer){var choice=button('',function(){selectedId=renderer.id;review=null;drawParts();drawDetail();drawReview();message(T('ready','Choose a renderer and material slot, or a static shape control.'));});choice.className='appearance-part';choice.setAttribute('aria-pressed',String(renderer.id===selectedId));
      choice.append(el('span',renderer.name),el('small',(renderer.path||renderer.name)+' · #'+renderer.id,'subtle'));partsNode.append(choice);});
    if(!snapshot.renderers.length)partsNode.append(el('p',T('noRenderers','This avatar has no mesh renderers to edit.'),'subtle'));
  }
  function numeric(value,min,max){var input=el('input');input.type='number';input.required=true;input.min=String(min);input.max=String(max);input.step='any';input.value=String(value);return input;}
  function materialProperty(slot,property){
    var section=el('div',null,'appearance-property');section.append(el('h4',property.label));
    if(!property.editable){section.append(el('p',property.reason||T('nativeField','Use Unity Inspector for this value.'),'subtle'));return section;}
    var base={action:'material',slot:slot.slot,materialId:slot.materialId,property:property.name,kind:property.kind};
    if(property.kind==='color'){
      var color=Object.assign({},property.color),picker=el('input');picker.type='color';picker.value=colorHex(color);picker.setAttribute('aria-label',property.label);
      picker.oninput=function(){color.r=parseInt(picker.value.slice(1,3),16)/255;color.g=parseInt(picker.value.slice(3,5),16)/255;color.b=parseInt(picker.value.slice(5,7),16)/255;invalidateDraft();};
      var alpha=numeric(color.a,0,1);alpha.oninput=function(){color.a=Number(alpha.value);invalidateDraft();};
      var row=el('div',null,'appearance-tint');row.append(field(T('color','Color'),picker),field(T('opacity','Opacity (shader dependent)'),alpha));section.append(row,button(T('review','Review change'),function(){if(alpha.reportValidity())requestReview(command(Object.assign({},base,{color:color})));}));
    }else if(property.kind==='float'){
      var number=numeric(property.value,property.min,property.max),range=el('input');range.type='range';range.min=property.min;range.max=property.max;range.step='0.01';range.value=property.value;range.setAttribute('aria-label',property.label);
      range.oninput=function(){number.value=range.value;invalidateDraft();};number.oninput=function(){range.value=number.value;invalidateDraft();};
      section.append(range,field(T('value','Value'),number),button(T('review','Review change'),function(){if(number.reportValidity())requestReview(command(Object.assign({},base,{value:Number(number.value)})));}));
    }else if(property.kind==='texture'){
      section.append(el('p',T('currentTexture','Current texture')+': '+property.textureName,'subtle'));
      var search=el('input');search.type='search';search.minLength=2;search.maxLength=80;search.placeholder=T('textureSearch','Search project textures');search.setAttribute('aria-label',search.placeholder);
      var choices=el('select'),placeholder=el('option',T('chooseTexture','Choose a replacement texture'));placeholder.value='__choose';choices.append(placeholder);var none=el('option',T('noTexture','Remove main texture'));none.value='';choices.append(none);choices.onchange=invalidateDraft;
      var searchButton=button(T('search','Search'),async function(){
        if(busy||!search.reportValidity())return;setBusy(true);message(T('searching','Searching project textures…'));
        try{var result=await api('/api/appearance_textures?search='+encodeURIComponent(search.value));choices.replaceChildren(placeholder,none);(result.textures||[]).forEach(function(texture){var option=el('option',texture.name+' · '+texture.width+'×'+texture.height+' · '+texture.path);option.value=texture.guid;choices.append(option);});choices.value='__choose';message(result.message);}
        catch(error){message(error.message,true);}finally{setBusy(false);}
      });
      var searchRow=el('div',null,'appearance-texture-search');searchRow.append(search,searchButton);section.append(searchRow,field(T('replacementTexture','Replacement'),choices),el('p',T('uvHelp','Use a texture made for this slot’s UV layout. Region matching is not inferred.'),'subtle'),button(T('review','Review change'),function(){if(choices.value==='__choose'){message(T('chooseTexture','Choose a replacement texture'),true);return;}requestReview(command(Object.assign({},base,{textureGuid:choices.value})));}));
    }
    return section;
  }
  async function inspectRenderer(){
    var renderer=selected();if(busy||!renderer||!snapshot)return;var avatarId=snapshot.avatarId;setBusy(true);
    try{var scene=await api('/api/scene_snapshot');if(scene.avatarId!==avatarId)throw new Error(T('targetChanged','The pinned avatar changed. Refresh Appearance.'));
      var result=await api('/api/scene_execute?command='+encodeURIComponent(JSON.stringify({action:'inspect',avatarId:avatarId,objectId:renderer.objectId,revision:scene.revision})),{method:'POST'});message(result.message);
    }catch(error){message(error.message,true);}finally{setBusy(false);}
  }
  function drawDetail(){
    if(!detailNode)return;detailNode.replaceChildren();var renderer=selected();if(!renderer)return;
    detailNode.append(el('h3',renderer.name),el('p',(renderer.path||renderer.name)+' · #'+renderer.id,'subtle'),button(T('native','Open in Unity Inspector'),inspectRenderer));
    renderer.slots.forEach(function(slot,index){var section=el('details',null,'appearance-slot');section.open=index===0;section.append(el('summary',T('slot','Material slot')+' '+slot.slot+' · '+slot.name));section.append(el('p',slot.shader,'subtle'));
      if(slot.reason)section.append(el('p',slot.reason,'subtle'));else slot.properties.forEach(function(property){section.append(materialProperty(slot,property));});detailNode.append(section);});
    if(renderer.shapes.length){
      var shapes=el('details',null,'appearance-slot');shapes.append(el('summary',T('shapes','Static face and body shapes')),el('p',T('shapeHelp','These weights affect this exact renderer. Expression animations may overwrite them. This review shows values; it does not render a shape preview.'),'subtle'));
      var search=el('input');search.type='search';search.placeholder=T('shapeSearch','Search shape names');search.setAttribute('aria-label',search.placeholder);shapes.append(search);var list=el('div');
      function drawShapes(){list.replaceChildren();var query=search.value.toLowerCase();renderer.shapes.filter(function(shape){return shape.name.toLowerCase().includes(query);}).forEach(function(shape){var row=el('div',null,'appearance-shape');if(!shape.editable){row.append(el('span',shape.name),el('small',T('nativeField','Use Unity Inspector for this value.'),'subtle'));}else{var value=numeric(shape.value,0,100);value.oninput=invalidateDraft;row.append(field(shape.name,value),button(T('review','Review change'),function(){if(value.reportValidity())requestReview(command({action:'blendshape',shapeIndex:shape.index,shapeName:shape.name,value:Number(value.value)}));}));}list.append(row);});}
      search.oninput=drawShapes;drawShapes();shapes.append(list);detailNode.append(shapes);
    }
  }
  async function nativeTool(tool){
    if(busy||!snapshot)return;var payload={avatarId:snapshot.avatarId,revision:snapshot.revision,id:tool.id};
    if(tool.id==='optimizer'&&tool.requiresReview){requestReview(payload,true);return;}
    var ticket=++serial;setBusy(true);message(T('openingTool','Opening the optional tool in Unity…'));
    try{var result=await api('/api/appearance_tool?command='+encodeURIComponent(JSON.stringify(payload)),{method:'POST'});if(await fetchSnapshot(ticket))message(result.message);}
    catch(error){if(ticket===serial){snapshot=null;tools=null;review=null;render();message(error.message,true);}}
    finally{if(ticket===serial)setBusy(false);}
  }
  function drawTools(target){
    var section=el('details',null,'appearance-tools');section.append(el('summary',T('optionalTools','Optional appearance and testing tools')));
    (tools&&tools.tools||[]).forEach(function(tool){var row=el('div',null,'appearance-tool');row.append(el('h4',tool.name+(tool.version?' '+tool.version:'')),el('p',tool.message,'subtle'));if(tool.available)row.append(button(tool.actionLabel,function(){nativeTool(tool);}));if(tool.url){var link=el('a',T('officialGuide','Official guide'));link.href=tool.url;link.target='_blank';link.rel='noopener noreferrer';row.append(link);}section.append(row);});
    if(tools&&tools.message)section.append(el('p',tools.message,'subtle'));target.append(section);
  }
  function render(){
    var target=host();if(!target)return;target.replaceChildren();target.classList.add('scene-editor','appearance-editor');
    var toolbar=el('div',null,'scene-editor-toolbar');toolbar.append(el('h2',T('title','Appearance')),button(T('refresh','Refresh Appearance'),load));target.append(toolbar,el('p',T('intro','Adjust supported material slots and static shapes on the pinned avatar. Review each draft before applying it.'),'subtle'));
    statusNode=el('p',null,'scene-editor-status');statusNode.setAttribute('role','status');statusNode.setAttribute('aria-live','polite');target.append(statusNode);
    if(global.WardrobePresetAppearanceUI){var saved=el('section',null,'saved-appearance');target.append(saved);global.WardrobePresetAppearanceUI.create({root:saved,api:config.api,scope:config.scope||function(){return 'common';},onChange:function(){load();if(config.onChange)config.onChange();}}).load();}
    reviewNode=el('section',null,'appearance-review');reviewNode.setAttribute('aria-label',T('reviewTitle','Review appearance change'));target.append(reviewNode);
    var grid=el('div',null,'scene-editor-grid');partsNode=el('div',null,'scene-editor-tree appearance-parts');partsNode.setAttribute('aria-label',T('renderers','Renderers'));detailNode=el('div',null,'scene-editor-inspector');grid.append(partsNode,detailNode);target.append(grid);drawParts();drawDetail();drawReview();drawTools(target);setBusy(busy);
  }
  global.WardrobeAppearanceEditor={configure:function(options){config=Object.assign(config,options||{});return this;},show:function(){if(busy){serial++;busy=false;snapshot=null;review=null;}return load();}};
})(window);
