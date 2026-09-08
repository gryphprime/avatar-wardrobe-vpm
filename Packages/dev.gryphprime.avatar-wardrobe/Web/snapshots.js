/* A photograph belongs to a captured target and recipe, never to whichever avatar is selected later. */
(function(global){
  'use strict';
  function create(options){
    var ops=options.operations,api=options.api,root=options.root,serial=0,draft=null,current=null,view='front',before=false,mode='shadow',cache=new Map();
    var image=root.querySelector('img'),caption=root.querySelector('[data-snapshot-caption]'),status=root.querySelector('[data-snapshot-status]'),wear=root.querySelector('[data-snapshot-wear]'),fallback=root.querySelector('[data-snapshot-fallback]');
    function message(text){status.textContent=text;}
    function targetMatches(target){var context=ops.context();return context&&global.WardrobeOperations.targetKey(Object.assign({},context,{scopeId:target.scopeId}))===global.WardrobeOperations.targetKey(target);}
    function fresh(){return draft&&draft.receipt&&targetMatches(draft.command.target)&&ops.context().revision===draft.receipt.result.confirmedRevision&&!ops.pending().some(function(record){return record.command.target.avatarInstanceId===draft.command.target.avatarInstanceId&&record.command.target.session===draft.command.target.session;});}
    function paint(){
      wear.hidden=!draft||!draft.input.variantId;wear.disabled=!fresh();
      if(!current){caption.textContent='No avatar photograph yet';return;}
      if(!targetMatches(current.draft.command.target)){image.hidden=true;caption.textContent='Photograph belongs to another avatar or preset';return;}
      image.hidden=false;
      var same=draft&&current.draft===draft&&current.view===view&&current.before===before;
      caption.textContent=(same&&fresh()?(draft.input.variantId?'Try-on':'Current avatar'):'Previous photo · out of date')+' · '+current.view+(current.before?' · Before':' · After')+' · static editor pose';
    }
    function contextChanged(){paint();}
    async function terminal(record){if(global.WardrobeOperations.isTerminal(record.state))return record;return ops.wait(record.id);}
    async function begin(input,chosenMode){
      var token=++serial;mode=chosenMode||'shadow';fallback.hidden=true;
      try{
        var command=ops.build(mode==='shadow'?'capture-source':'prepare-preview',input);
        draft={input:Object.assign({},input),command:command,receipt:null,mode:mode};paint();message(mode==='shadow'?'Capturing the complete avatar for Try on…':'Preparing a complete-avatar preview in Unity…');
        var accepted=await ops.submit(command,input.label||'Try on');
        var result=await terminal(accepted);if(token!==serial)return;
        if(result.state!=='succeeded')throw new Error(result.error||'Preview preparation did not complete.');
        draft.receipt=result;await render(token);
      }catch(error){if(token!==serial)return;message(error.message);fallback.hidden=mode!=='shadow';paint();}
    }
    async function render(token){
      if(!draft||!draft.receipt)return;
      token=token||++serial;var expected=draft,selectedView=view,selectedBefore=before;
      var cacheKey=expected.command.id+'|'+selectedView+'|'+selectedBefore;
      paint();message('Preparing '+selectedView+' photograph…');
      try{
        var record=cache.get(cacheKey),url;
        if(!record){
          if(expected.mode==='shadow'){
            record=await api('/api/shadow/submit',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({operationId:expected.command.id,view:selectedView,before:selectedBefore,zoom:1}),timeout:8000});
            while(!['succeeded','failed','cancelled','needs-review'].includes(record.state)){
              await new Promise(function(resolve){setTimeout(resolve,750);});if(token!==serial)return;
              record=await api('/api/shadow/result?id='+encodeURIComponent(record.id),{method:'GET',timeout:5000});
            }
            if(record.state!=='succeeded')throw new Error(record.message||record.error||'Snapshot did not complete.');
          }else{
            var command=global.WardrobeOperations.normalize('render-snapshot',{previewToken:expected.receipt.result.preview.token,view:selectedView,before:selectedBefore,scopeId:expected.command.target.scopeId},Object.assign({},expected.command.target,{revision:expected.receipt.result.confirmedRevision}));
            record=await terminal(await ops.submit(command,'Photograph '+selectedView));
            if(record.state!=='succeeded')throw new Error(record.error||'Snapshot did not complete.');
          }
          cache.set(cacheKey,record);while(cache.size>24)cache.delete(cache.keys().next().value);
        }
        if(token!==serial||draft!==expected||!targetMatches(expected.command.target))return;
        var key=expected.mode==='shadow'?record.snapshotKey:record.result.snapshotKey;
        url=(expected.mode==='shadow'?'/api/shadow/image?key=':'/api/snapshot?key=')+encodeURIComponent(key);
        var next=new Image();next.alt='Complete avatar '+selectedView+(selectedBefore?' before try-on':' after try-on');
        await new Promise(function(resolve,reject){next.onload=resolve;next.onerror=function(){reject(new Error('The photograph could not be loaded. The previous image is preserved.'));};next.src=url;});
        if(token!==serial||draft!==expected||!targetMatches(expected.command.target))return;
        image.src=url;image.alt=next.alt;image.hidden=false;
        current={draft:expected,view:selectedView,before:selectedBefore,key:key};
        var preview=expected.mode==='shadow'?record.preview:expected.receipt.result.preview;
        message(preview&&preview.limitations?preview.limitations.join(' '):'Captured source with supported build processing. Inspect the fit before wearing.');paint();
      }catch(error){if(token===serial){message(error.message);paint();fallback.hidden=expected.mode!=='shadow';}}
    }
    root.querySelectorAll('[data-snapshot-view]').forEach(function(button){button.onclick=function(){view=button.dataset.snapshotView;root.querySelectorAll('[data-snapshot-view]').forEach(function(b){b.setAttribute('aria-pressed',String(b===button));});render();};});
    root.querySelector('[data-snapshot-before]').onchange=function(){before=this.checked;render();};
    root.querySelector('[data-snapshot-current]').onclick=function(){begin({scopeId:options.scope()},'shadow');};
    root.querySelector('[data-snapshot-discard]').onclick=function(){serial++;draft=null;message('Try-on discarded. The working avatar was not changed.');paint();};
    fallback.onclick=function(){if(draft)begin(draft.input,'active');};
    wear.onclick=async function(){
      if(!fresh()){message('The avatar changed. Prepare a fresh try-on before wearing.');return;}
      try{var input=Object.assign({},draft.input),type=input.instanceId?'replace-outfit':'wear-outfit';input.addCopy=!input.instanceId;
        var command=global.WardrobeOperations.normalize(type,input,Object.assign({},draft.command.target,{revision:draft.receipt.result.confirmedRevision}));
        await ops.submit(command,input.label||'Wear outfit');message('Wear queued. The photograph becomes current only after a new confirmed capture.');}
      catch(error){message(error.message);}
    };
    root.querySelector('[data-snapshot-pin]').onclick=async function(){if(!current||current.draft.mode!=='shadow')return;try{await api('/api/shadow/pin',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({key:current.key,pinned:true})});message('Photograph pinned in local history.');}catch(error){message(error.message);}};
    return {begin:begin,contextChanged:contextChanged,discard:function(){serial++;draft=null;paint();},current:function(){return current;}};
  }
  global.WardrobeSnapshots={create:create};
})(window);
