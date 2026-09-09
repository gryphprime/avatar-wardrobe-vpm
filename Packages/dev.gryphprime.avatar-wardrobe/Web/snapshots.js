/* A photograph belongs to a captured target and recipe, never to whichever avatar is selected later. */
(function(global){
  'use strict';
  function create(options){
    var ops=options.operations,api=options.api,root=options.root,serial=0,draft=null,current=null,view='front',before=false,zoom=1,mode='shadow',cache=new Map();
    var manualEpoch=0,refreshRequest=null,restoreKey='',restoreSerial=0,history=null;
    var image=root.querySelector('img'),caption=root.querySelector('[data-snapshot-caption]'),status=root.querySelector('[data-snapshot-status]'),wear=root.querySelector('[data-snapshot-wear]'),fallback=root.querySelector('[data-snapshot-fallback]');
    function message(text){status.textContent=text;}
    function targetMatches(target){var context=ops.context();return context&&global.WardrobeOperations.targetKey(Object.assign({},context,{scopeId:options.scope()}))===global.WardrobeOperations.targetKey(target);}
    function sameAvatar(a,b){return a&&b&&global.WardrobeOperations.targetKey(Object.assign({},a,{scopeId:''}))===global.WardrobeOperations.targetKey(Object.assign({},b,{scopeId:''}));}
    function pendingFor(target){return ops.pending().some(function(record){return record.command&&sameAvatar(record.command.target,target);});}
    function selectedTarget(){var context=ops.context();return context&&Object.assign({},context,{scopeId:options.scope()});}
    function savedTargetMatches(target){
      var selected=selectedTarget();if(!selected||!target)return false;
      if(!selected.sceneGuid||!selected.avatarId||!target.avatarId)return targetMatches(target);
      return selected.projectId===target.projectId&&selected.sceneGuid===target.sceneGuid&&selected.avatarId===target.avatarId&&selected.scopeId===target.scopeId;
    }
    function displayMatches(value){return value&&(value.draft.restored?savedTargetMatches(value.draft.command.target):targetMatches(value.draft.command.target));}
    function fresh(){return draft&&!draft.restored&&draft.receipt&&draft.hasAfter&&current&&current.draft===draft&&current.view===view&&current.before===before&&current.zoom===zoom&&targetMatches(draft.command.target)&&ops.context().revision===draft.receipt.result.confirmedRevision&&!pendingFor(draft.command.target);}
    function photoFresh(){
      if(!draft||!draft.receipt||!targetMatches(draft.command.target))return false;
      var result=draft.receipt.result,visual=result.visualRevision||(result.preview&&result.preview.visualRevision),context=ops.context();
      return (visual&&context.visualRevision?visual===context.visualRevision:context.revision===result.confirmedRevision)&&!pendingFor(draft.command.target);
    }
    function paint(){
      wear.hidden=!draft||!draft.input.variantId;wear.disabled=!fresh();
      root.querySelector('[data-snapshot-pin]').disabled=!current||current.draft.mode!=='shadow'||!displayMatches(current);
      root.querySelector('[data-snapshot-discard]').disabled=!draft;
      var metrics=root.querySelector('[data-snapshot-metrics]');if(metrics)metrics.hidden=!current||!displayMatches(current)||!current.preview||!current.preview.beforeMetrics||!current.preview.afterMetrics;
      if(!current){caption.textContent='No avatar photograph yet';return;}
      if(!displayMatches(current)){image.hidden=true;caption.textContent='Photograph belongs to another avatar or preset';return;}
      image.hidden=false;
      var same=draft&&current.draft===draft&&current.view===view&&current.before===before&&current.zoom===zoom;
      caption.textContent=(current.draft.restored?'Restored photo · ':'')+(same&&photoFresh()?(draft.input.variantId?'Try-on':'Current avatar'):'Previous photo · out of date')+' · '+current.view+(current.before?' · Before':' · After')+' · '+Math.round(current.zoom*100)+'% · static editor pose'+(current.draft.restored&&current.completed?' · captured '+new Date(current.completed*1000).toLocaleString():'');
    }
    function contextChanged(){
      paint();if(history)history.contextChanged();
      var target=selectedTarget(),key=target&&global.WardrobeOperations.targetKey(target);
      if(key!==restoreKey){restoreKey=key;var attempt=++restoreSerial;if(target&&(!current||!displayMatches(current)))restoreLast(target,attempt,manualEpoch);}
      var request=refreshRequest,context=ops.context();
      if(!request)return;
      if(!context||!targetMatches(request.target)||request.epoch!==manualEpoch){refreshRequest=null;return;}
      if(context.revision!==request.revision||context.waitingReason||pendingFor(request.target))return;
      Promise.resolve().then(function(){
        var latest=ops.context();
        if(refreshRequest!==request||request.epoch!==manualEpoch||!targetMatches(request.target)||!latest||latest.revision!==request.revision||latest.waitingReason||pendingFor(request.target))return;
        refreshRequest=null;
        begin({scopeId:request.target.scopeId,label:'Current avatar after confirmed change'},'shadow',request);
      });
    }
    function mutationSettled(record){
      var context=ops.context();
      if(!context||!record||record.state!=='succeeded'||!global.WardrobeOperations.isMutation(record.type)||!record.command||!sameAvatar(record.command.target,context)||!record.result||!record.result.confirmedRevision)return;
      refreshRequest={target:selectedTarget(),revision:record.result.confirmedRevision,epoch:manualEpoch};
      contextChanged();
    }
    async function restoreLast(target,attempt,epoch){
      try{
        var query='/api/shadow/history?pinned=0&limit=100&avatarId='+encodeURIComponent(target.avatarId||'')+'&sceneGuid='+encodeURIComponent(target.sceneGuid||'')+'&scopeId='+encodeURIComponent(target.scopeId);
        var result=await api(query,{method:'GET',timeout:5000});
        if(attempt!==restoreSerial||epoch!==manualEpoch||!targetMatches(target))return;
        if(result.projectId&&result.projectId!==target.projectId)return;
        var record=(result.items||[]).find(function(item){return /^[a-f0-9]{64}$/.test(item.snapshotKey||'')&&savedTargetMatches(item.target)&&item.before===false&&item.input&&!item.input.variantId;});
        if(!record)return;
        var selectedKey=record.snapshotKey;record=await api('/api/shadow/photo?key='+encodeURIComponent(selectedKey),{method:'GET',timeout:5000});
        if(attempt!==restoreSerial||epoch!==manualEpoch||!targetMatches(target)||!savedTargetMatches(record.target)||!record.input||record.input.variantId||(record.preview&&record.preview.guid)||record.before!==false||record.snapshotKey!==selectedKey||!['front','three-quarter','back'].includes(record.view)||!Number.isFinite(record.zoom)||record.zoom<0.5||record.zoom>2.5)return;
        var url='/api/shadow/image?key='+encodeURIComponent(record.snapshotKey),next=new Image();
        await new Promise(function(resolve,reject){next.onload=resolve;next.onerror=reject;next.src=url;});
        if(attempt!==restoreSerial||epoch!==manualEpoch||!targetMatches(target))return;
        var restored={restored:true,mode:'shadow',input:{scopeId:record.target.scopeId},command:{id:record.operationId||'',target:record.target},receipt:{result:record},hasAfter:true};
        draft=restored;view=record.view;before=false;zoom=record.zoom;
        current={draft:restored,view:view,before:false,zoom:zoom,key:record.snapshotKey,preview:record.preview,completed:record.completed||record.created};
        image.src=url;image.alt='Restored current-avatar photograph';image.hidden=false;
        root.querySelectorAll('[data-snapshot-view]').forEach(function(button){button.setAttribute('aria-pressed',String(button.dataset.snapshotView===view));});
        root.querySelector('[data-snapshot-before]').checked=false;var control=root.querySelector('[data-snapshot-zoom]');if(control)control.value=String(zoom);
        showMetrics(record.preview);message('Restored the last completed current-avatar photograph. It does not authorize Wear; capture again after scene changes.');paint();
      }catch(error){/* History is optional; retain any current photograph when it is unavailable. */}
    }
    function cancelShadow(id){return api('/api/shadow/cancel',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({id:id}),timeout:5000}).catch(function(){});}
    function cancelWork(value,renderOnly){
      if(!value)return;
      if(value.shadowJobId){var id=value.shadowJobId;value.shadowJobId=null;cancelShadow(id);}
      if(value.pendingOperationId&&(!renderOnly||value.receipt)){var operationId=value.pendingOperationId;value.pendingOperationId=null;ops.cancel(operationId).catch(function(){});}
    }
    function discard(){serial++;manualEpoch++;restoreSerial++;refreshRequest=null;cancelWork(draft);draft=null;message('Try-on discarded. Any running photograph is being cancelled.');paint();}
    function bytes(value){return (Number(value||0)/1048576).toLocaleString(undefined,{maximumFractionDigits:1})+' MiB';}
    function showMetrics(preview){
      var host=root.querySelector('[data-snapshot-metrics]');if(!host)return;
      host.replaceChildren();host.hidden=!preview||!preview.beforeMetrics||!preview.afterMetrics;if(host.hidden)return;
      var a=preview.beforeMetrics,b=preview.afterMetrics;
      var summary=document.createElement('summary');summary.textContent='What this appearance changes';host.appendChild(summary);
      var table=document.createElement('table'),head=document.createElement('thead'),body=document.createElement('tbody');
      var header=document.createElement('tr');['Measured on built copies','Before','After'].forEach(function(label){var cell=document.createElement('th');cell.scope='col';cell.textContent=label;header.appendChild(cell);});head.appendChild(header);table.appendChild(head);
      [['Visible renderers','visibleRenderers'],['Material slots','materials'],['Triangles','triangles'],['PhysBone components','physBones'],['Contact components','contacts'],['Texture allocation estimate','estimatedTextureBytes']].forEach(function(metric){
        var row=document.createElement('tr'),label=document.createElement('th');label.scope='row';label.textContent=metric[0];row.appendChild(label);
        [a,b].forEach(function(value){var cell=document.createElement('td');cell.textContent=metric[1]==='estimatedTextureBytes'?bytes(value[metric[1]]):Number(value[metric[1]]||0).toLocaleString();row.appendChild(cell);});body.appendChild(row);
      });table.appendChild(body);host.appendChild(table);
      var note=document.createElement('p');note.className='subtle';note.textContent=b.textureEstimateScope||'Distinct referenced textures, including editor allocations. This is not exact platform VRAM or an FPS prediction.';host.appendChild(note);
      if(b.largestTextures&&b.largestTextures.length){var heading=document.createElement('p');heading.textContent='Largest textures after try-on';host.appendChild(heading);var list=document.createElement('ul');b.largestTextures.forEach(function(texture){var item=document.createElement('li');item.textContent=texture.name+' · '+texture.width+' × '+texture.height+' · '+bytes(texture.estimatedBytes);list.appendChild(item);});host.appendChild(list);}
      var menu=document.createElement('details'),title=document.createElement('summary');title.textContent='Built menu and parameters';menu.appendChild(title);
      var menuNote=document.createElement('p');menuNote.className='subtle';menuNote.textContent='Controls from this processed copy. Use Test in Appearance for interactive checks in Unity.';menu.appendChild(menuNote);
      [['Menu',preview.menuControls],['Parameters',preview.parameters],['Parameter checks',preview.parameterProblems]].forEach(function(group){var label=document.createElement('p');label.textContent=group[0];menu.appendChild(label);var list=document.createElement('ul');(group[1]&&group[1].length?group[1]:['None reported']).forEach(function(text){var item=document.createElement('li');item.textContent=text;list.appendChild(item);});menu.appendChild(list);});host.appendChild(menu);
    }
    async function terminal(record){if(global.WardrobeOperations.isTerminal(record.state))return record;return ops.wait(record.id);}
    async function begin(input,chosenMode,automatic){
      if(!automatic){manualEpoch++;refreshRequest=null;}restoreSerial++;
      if(automatic){before=false;root.querySelector('[data-snapshot-before]').checked=false;}
      cancelWork(draft);var token=++serial;mode=chosenMode||'shadow';fallback.hidden=true;
      try{
        var command=ops.build(mode==='shadow'?'capture-source':'prepare-preview',input);
        draft={input:Object.assign({},input),command:command,receipt:null,mode:mode,pendingOperationId:command.id,automatic:automatic};paint();message(mode==='shadow'?'Capturing the complete avatar for Try on…':'Preparing a complete-avatar preview in Unity…');
        var accepted=await ops.submit(command,input.label||'Try on');if(token!==serial){ops.cancel(command.id).catch(function(){});return;}
        var result=await terminal(accepted);if(token!==serial)return;
        if(result.state!=='succeeded')throw new Error(result.error||'Preview preparation did not complete.');
        draft.pendingOperationId=null;draft.receipt=result;
        if(automatic&&(!targetMatches(automatic.target)||ops.context().revision!==automatic.revision||result.result.confirmedRevision!==automatic.revision||pendingFor(automatic.target)))throw new Error('The avatar changed while refreshing. The previous photograph is preserved.');
        await render(token);
      }catch(error){if(token!==serial)return;message(error.message);fallback.hidden=mode!=='shadow';paint();}
    }
    async function render(token){
      if(!draft||!draft.receipt)return;
      if(draft.restored){return begin({scopeId:options.scope()},'shadow');}
      cancelWork(draft,true);token=token||++serial;var expected=draft,selectedView=view,selectedBefore=before,selectedZoom=zoom;
      var cacheKey=expected.command.id+'|'+selectedView+'|'+selectedBefore+'|'+selectedZoom;
      paint();message('Preparing '+selectedView+' photograph…');
      try{
        var record=cache.get(cacheKey),url;
        if(!record){
          if(expected.mode==='shadow'){
            record=await api('/api/shadow/submit',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({operationId:expected.command.id,view:selectedView,before:selectedBefore,zoom:selectedZoom}),timeout:8000});
            if(token!==serial){cancelShadow(record.id);return;}expected.shadowJobId=record.id;
            while(!['succeeded','failed','cancelled','needs-review'].includes(record.state)){
              await new Promise(function(resolve){setTimeout(resolve,750);});if(token!==serial)return;
              record=await api('/api/shadow/result?id='+encodeURIComponent(record.id),{method:'GET',timeout:5000});
            }
            if(expected.shadowJobId===record.id)expected.shadowJobId=null;
            if(record.state!=='succeeded')throw new Error(record.message||record.error||'Snapshot did not complete.');
          }else{
            var command=global.WardrobeOperations.normalize('render-snapshot',{previewToken:expected.receipt.result.preview.token,view:selectedView,before:selectedBefore,zoom:selectedZoom,scopeId:expected.command.target.scopeId},Object.assign({},expected.command.target,{revision:expected.receipt.result.confirmedRevision}));
            expected.pendingOperationId=command.id;var accepted=await ops.submit(command,'Photograph '+selectedView);if(token!==serial){ops.cancel(command.id).catch(function(){});return;}record=await terminal(accepted);if(expected.pendingOperationId===command.id)expected.pendingOperationId=null;
            if(record.state!=='succeeded')throw new Error(record.error||'Snapshot did not complete.');
          }
          cache.set(cacheKey,record);while(cache.size>24)cache.delete(cache.keys().next().value);
        }
        if(token!==serial||draft!==expected||!targetMatches(expected.command.target)||!automaticCurrent(expected))return;
        var key=expected.mode==='shadow'?record.snapshotKey:record.result.snapshotKey;
        url=(expected.mode==='shadow'?'/api/shadow/image?key=':'/api/snapshot?key=')+encodeURIComponent(key);
        var next=new Image();next.alt='Complete avatar '+selectedView+(selectedBefore?' before try-on':' after try-on');
        await new Promise(function(resolve,reject){next.onload=resolve;next.onerror=function(){reject(new Error('The photograph could not be loaded. The previous image is preserved.'));};next.src=url;});
        if(token!==serial||draft!==expected||!targetMatches(expected.command.target)||!automaticCurrent(expected))return;
        image.src=url;image.alt=next.alt;image.hidden=false;
        current={draft:expected,view:selectedView,before:selectedBefore,zoom:selectedZoom,key:key};
        var preview=expected.mode==='shadow'?record.preview:expected.receipt.result.preview;
        if(!selectedBefore)expected.hasAfter=true;current.preview=preview;showMetrics(preview);message(preview&&preview.limitations?preview.limitations.join(' '):'Captured source with supported build processing. Inspect the fit before wearing.');paint();
      }catch(error){if(token===serial){message(error.message);paint();fallback.hidden=expected.mode!=='shadow';}}
    }
    function automaticCurrent(value){return !value.automatic||(ops.context().revision===value.automatic.revision&&!pendingFor(value.command.target));}
    root.querySelectorAll('[data-snapshot-view]').forEach(function(button){button.onclick=function(){view=button.dataset.snapshotView;root.querySelectorAll('[data-snapshot-view]').forEach(function(b){b.setAttribute('aria-pressed',String(b===button));});render();};});
    root.querySelector('[data-snapshot-before]').onchange=function(){before=this.checked;render();};
    root.querySelector('[data-snapshot-current]').onclick=function(){begin({scopeId:options.scope()},'shadow');};
    root.querySelector('[data-snapshot-discard]').onclick=discard;
    var zoomControl=root.querySelector('[data-snapshot-zoom]');if(zoomControl)zoomControl.onchange=function(){zoom=Number(this.value);render();};
    fallback.onclick=function(){if(draft)begin(draft.input,'active');};
    wear.onclick=async function(){
      if(!fresh()){message('The avatar changed. Prepare a fresh try-on before wearing.');return;}
      try{var input=Object.assign({},draft.input),type=input.instanceId?'replace-outfit':'wear-outfit';input.addCopy=!input.instanceId;
        var command=global.WardrobeOperations.normalize(type,input,Object.assign({},draft.command.target,{revision:draft.receipt.result.confirmedRevision}));
        await ops.submit(command,input.label||'Wear outfit');message('Wear queued. The photograph becomes current only after a new confirmed capture.');}
      catch(error){message(error.message);}
    };
    root.querySelector('[data-snapshot-pin]').onclick=async function(){if(!current||current.draft.mode!=='shadow'||!displayMatches(current))return;try{await api('/api/shadow/pin',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({key:current.key,pinned:true})});if(history)history.refresh();message('Photograph pinned in local history.');}catch(error){message(error.message);}};
    var historyRoot=root.querySelector('#photoHistory');if(historyRoot&&global.WardrobePhotoHistoryUI)history=global.WardrobePhotoHistoryUI.create({root:historyRoot,api:api,context:function(){return ops.context();},scope:options.scope});
    return {begin:begin,contextChanged:contextChanged,mutationSettled:mutationSettled,discard:discard,current:function(){return current;}};
  }
  global.WardrobeSnapshots={create:create};
})(window);
