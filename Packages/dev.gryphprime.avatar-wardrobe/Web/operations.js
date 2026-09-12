/* Desktop intent projection: acceptance is distinct from confirmed Unity state. */
(function(global){
  'use strict';
  function L(key,fallback){return window.WardrobeRuntime&&window.WardrobeRuntime.localize?window.WardrobeRuntime.localize(key,fallback):fallback;}
  function esc(value){return window.WardrobeRuntime.escape(value);}

  var terminal=new Set(['succeeded','failed','cancelled','superseded','needs-review']);
  var mutations=new Set(['wear-outfit','replace-outfit','remove-outfit','undo-operation']);
  function key(target){return JSON.stringify([target.projectId,target.sceneGuid,target.avatarId,target.avatarInstanceId,target.session,target.scopeId]);}
  function uuid(){return global.crypto.randomUUID();}
  function normalize(type,input,context,id,predecessor){
    if(!context||!context.revision||!context.avatarInstanceId)throw new Error(L("ui.pin.an.avatar.and.wait.for.unity.to.confirm","Pin an avatar and wait for Unity to confirm its current state."));
    if(!['wear-outfit','replace-outfit','remove-outfit','prepare-preview','capture-source','render-snapshot','undo-operation'].includes(type))throw new Error(L("ui.unsupported.operation","Unsupported operation."));
    input=input||{};
    var payload={};
    ['variantId','assetVersion','instanceId','previewToken','undoToken','view','menuGroup','itemPath'].forEach(function(field){if(input[field]!=null)payload[field]=String(input[field]);});
    ['addCopy','allowUnverified','createToggles','before'].forEach(function(field){if(input[field]!=null)payload[field]=!!input[field];});
    payload.zoom=input.zoom==null?1:Number(input.zoom);
    if(!Number.isFinite(payload.zoom)||payload.zoom<=0||payload.zoom>3)throw new Error(L("ui.invalid.snapshot.zoom","Invalid snapshot zoom."));
    if(type!=='render-snapshot'&&type!=='prepare-preview'&&type!=='capture-source'&&type!=='undo-operation'&&!/^[a-f0-9]{32}$/i.test(payload.variantId||''))throw new Error(L("ui.choose.a.specific.outfit.variant.first","Choose a specific outfit variant first."));
    if(type==='undo-operation'&&!/^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/.test(payload.undoToken||''))throw new Error(L("ui.this.session.only.undo.is.no.longer.available","This session-only Undo is no longer available."));
    if((type==='replace-outfit'||type==='remove-outfit')&&!payload.instanceId)throw new Error(L("ui.choose.the.exact.worn.copy.first","Choose the exact worn copy first."));
    return {id:id||uuid(),type:type,target:{projectId:context.projectId,sceneGuid:context.sceneGuid||'',avatarId:context.avatarId,avatarInstanceId:context.avatarInstanceId,session:context.session,scopeId:input.scopeId||context.scopeId||'common'},
      precondition:{observedRevision:context.revision,afterOperationId:predecessor||''},payload:payload};
  }
  function create(options){
    var hydrated=false,records=new Map(),context=null,enabled=false,timer=null,polling=false,waiters=new Map(),retrying=new Map();
    var dismissalKey='wardrobe.operations.dismissed.v1',dismissals=new Map(),retryLinks=new Map();
    try{var saved=JSON.parse(global.localStorage.getItem(dismissalKey)||'[]');if(Array.isArray(saved))saved.slice(-512).forEach(function(entry){if(Array.isArray(entry)&&typeof entry[0]==='string'&&entry[0].length<=64&&typeof entry[1]==='string'&&entry[1].length<=8192){dismissals.set(entry[0],entry[1]);if(typeof entry[2]==='string'&&entry[2].length<=64)retryLinks.set(entry[0],entry[2]);}});}catch(error){}
    function dismissalStamp(record){return JSON.stringify([record.state,String(record.error||'').slice(0,8000)]);}
    function saveDismissals(){while(dismissals.size>512)dismissals.delete(dismissals.keys().next().value);while(retryLinks.size>512)retryLinks.delete(retryLinks.keys().next().value);var ids=Array.from(new Set(Array.from(dismissals.keys()).concat(Array.from(retryLinks.keys())))).slice(-512);try{global.localStorage.setItem(dismissalKey,JSON.stringify(ids.map(function(id){return [id,dismissals.get(id)||'',retryLinks.get(id)||''];})));}catch(error){}}
    function ordered(){return Array.from(records.values()).sort(function(a,b){return (a.createdAt||0)-(b.createdAt||0);});}
    function changed(){
      var all=ordered();while(records.size>512){var old=all.find(function(record){return terminal.has(record.state);});if(!old)break;records.delete(old.id);all=all.filter(function(record){return record!==old;});}
      if(options.onChange)options.onChange(all,context);
    }
    function merge(record){
      if(!record||!record.id)return;
      var old=records.get(record.id),next=Object.assign({},old,record);
      next.dismissed=dismissals.get(record.id)===dismissalStamp(next);if(retryLinks.get(record.id))next.retryOperationId=retryLinks.get(record.id);records.set(record.id,next);
      if(terminal.has(record.state)){
        var waiting=waiters.get(record.id);if(waiting){waiters.delete(record.id);waiting.forEach(function(done){done(record);});}
        if(old&&!terminal.has(old.state)&&options.onSettled)options.onSettled(record);
      }
    }
    async function refresh(){
      if(!enabled||polling)return;polling=true;
      try{
        // Both routes are desktop/thread-safe reads and remain responsive while Unity is occupied.
        var all=await options.api('/api/operations',{method:'GET',timeout:4000});
        (all.items||all.operations||[]).forEach(merge);changed();
        if(!hydrated){hydrated=true;if(options.onHydrated)options.onHydrated();}
        var next=await options.api('/api/operation_context',{method:'GET',timeout:4000});
        if(next&&next.revision){context=next;changed();}
      }catch(error){if(context)context=Object.assign({},context,{waitingReason:L("ui.unity.connection.unavailable","Unity connection unavailable")});changed();}
      finally{polling=false;}
    }
    function build(type,input){
      var command=normalize(type,input,context),chain='';
      var same=ordered().filter(function(record){return record.command&&key(record.command.target)===key(command.target)&&mutations.has(record.type||record.command.type);});
      for(var i=same.length-1;i>=0;i--){
        var record=same[i];
        if(['submitting','queued','running','acceptance-unknown'].includes(record.state)){chain=record.id;break;}
        if(record.state==='succeeded'&&record.result&&record.command.precondition.observedRevision===context.revision){chain=record.id;break;}
        if(terminal.has(record.state))break;
      }
      command.precondition.afterOperationId=chain;return command;
    }
    async function submit(command,label){
      if(!enabled)throw new Error(L("ui.open.avatar.wardrobe.desktop.from.the.unity.launcher.to","Open Avatar Wardrobe Desktop from the Unity launcher to use queued changes."));
      if(context&&key(Object.assign({},context,{scopeId:command.target.scopeId}))!==key(command.target))throw new Error(L("ui.the.target.changed.review.the.outfit.for.the.newly","The target changed. Review the outfit for the newly selected avatar."));
      var old=records.get(command.id);
      if(old&&JSON.stringify(old.command)!==JSON.stringify(command))throw new Error(L("ui.this.operation.id.already.belongs.to.another.command","This operation ID already belongs to another command."));
      records.set(command.id,Object.assign({},old,{id:command.id,type:command.type,command:command,label:label||(old&&old.label)||command.payload.variantId||command.type,createdAt:old?old.createdAt:Date.now()/1000,state:'submitting'}));changed();
      try{var record=await options.api('/api/operations',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(command),timeout:8000});merge(record);changed();return record;}
      catch(error){var record=records.get(command.id);record.state=error.accepted===false?'failed':'acceptance-unknown';record.error=error.accepted===false?error.message:L("ui.acceptance.is.unconfirmed.check.or.retry.this.same.operation","Acceptance is unconfirmed. Check or retry this same operation ID. ")+error.message;merge(record);changed();throw error;}
    }
    async function cancel(id){var result=await options.api('/api/operations/cancel?id='+encodeURIComponent(id),{method:'POST',timeout:5000});merge(result);changed();return result;}
    function dismiss(id){
      var record=records.get(id);if(!record||!terminal.has(record.state))throw new Error(L("ui.only.a.settled.change.can.be.dismissed.cancel.pending","Only a settled change can be dismissed. Cancel pending work instead."));
      dismissals.set(id,dismissalStamp(record));record.dismissed=true;saveDismissals();changed();
    }
    function restore(id){var record=records.get(id);dismissals.delete(id);if(record)record.dismissed=false;saveDismissals();changed();}
    function retryAvailable(record){return record&&record.command&&record.state==='failed'&&['wear-outfit','replace-outfit','remove-outfit'].includes(record.type||record.command.type)&&!record.retryOperationId&&!retrying.has(record.id);}
    function retry(id,scopeId){
      if(retrying.has(id))return retrying.get(id);
      var original=records.get(id);
      if(!retryAvailable(original))return Promise.reject(new Error(L("ui.review.this.change.first.uncertain.or.accepted.changes.must","Review this change first. Uncertain or accepted changes must keep their original request ID.")));
      var action=(async function(){
        if(!enabled)throw new Error(L("ui.reconnect.the.desktop.host.before.retrying","Reconnect the desktop host before retrying."));
        if(scopeId&&scopeId!==original.command.target.scopeId)throw new Error(L("ui.choose.the.original.preset.before.retrying.this.change","Choose the original preset before retrying this change."));
        if(!context||key(Object.assign({},context,{scopeId:original.command.target.scopeId}))!==key(original.command.target))throw new Error(L("ui.choose.the.original.pinned.avatar.before.retrying.this.change","Choose the original pinned avatar before retrying this change."));
        // Explicit retry rechecks the durable failure and fresh pinned context.
        // An unknown acceptance or Needs review can never create a new command.
        var fresh=await Promise.all([options.api('/api/operations?id='+encodeURIComponent(id),{method:'GET',timeout:4000}),options.api('/api/operation_context',{method:'GET',timeout:4000})]);
        var receipt=fresh[0],next=fresh[1];
        if(receipt&&receipt.id===id){merge(receipt);changed();}
        if(!receipt||receipt.id!==id||receipt.state!=='failed'||!receipt.command||JSON.stringify(receipt.command)!==JSON.stringify(original.command))throw new Error(L("ui.the.durable.result.changed.or.is.uncertain.review.the","The durable result changed or is uncertain. Review the current avatar before doing anything else."));
        if(!next||!next.revision||!context||key(Object.assign({},next,{scopeId:original.command.target.scopeId}))!==key(original.command.target)||key(Object.assign({},context,{scopeId:original.command.target.scopeId}))!==key(original.command.target))throw new Error(L("ui.the.pinned.project.scene.or.avatar.changed.review.the","The pinned project, scene or avatar changed. Review the outfit on the current target."));
        if(ordered().some(function(record){return record.command&&record.command.target.projectId===next.projectId&&mutations.has(record.type||record.command.type)&&!terminal.has(record.state);}))throw new Error(L("ui.wait.for.pending.outfit.changes.to.settle.before.retrying","Wait for pending outfit changes to settle before retrying."));
        context=next;merge(receipt);
        var command=normalize(original.command.type,Object.assign({},original.command.payload,{scopeId:original.command.target.scopeId}),next);
        records.get(id).retryOperationId=command.id;retryLinks.set(id,command.id);
        // Retain the failed outcome in Activity; the new request owns its own row.
        dismiss(id);
        return submit(command,original.label);
      })();
      retrying.set(id,action);changed();
      action.then(function(){retrying.delete(id);changed();},function(){retrying.delete(id);changed();});return action;
    }
    function notices(scopeId){return ordered().filter(function(record){
      if(!record.command||!context||!mutations.has(record.type||record.command.type)||record.dismissed)return false;
      var target=record.command.target,current=Object.assign({},context,{scopeId:scopeId||context.scopeId||'common'}),attention=record.state==='failed'||record.state==='needs-review';
      // After restart, a saved avatar can still show its old review notice. Retrying
      // remains session-bound; this display match never authorizes execution.
      var savedAvatar=attention&&target.sceneGuid&&target.avatarId&&target.projectId===current.projectId&&target.sceneGuid===current.sceneGuid&&target.avatarId===current.avatarId&&target.scopeId===current.scopeId;
      return (savedAvatar||key(target)===key(current))&&(attention||!terminal.has(record.state));
    });}
    function wait(id){var record=records.get(id);if(record&&terminal.has(record.state))return Promise.resolve(record);return new Promise(function(resolve){var group=waiters.get(id)||[];group.push(resolve);waiters.set(id,group);});}
    function setHost(value){enabled=!!value;if(enabled&&!timer){refresh();timer=setInterval(refresh,1500);}if(!enabled&&timer){clearInterval(timer);timer=null;context=null;changed();}}
    return {build:build,submit:submit,cancel:cancel,retry:retry,canRetry:retryAvailable,dismiss:dismiss,restore:restore,notices:notices,wait:wait,refresh:refresh,setHost:setHost,context:function(){return context;},list:ordered,enabled:function(){return enabled;},pending:function(){return Array.from(records.values()).filter(function(x){return mutations.has(x.type)&&!terminal.has(x.state);});},close:function(){if(timer)clearInterval(timer);timer=null;}};
  }
  global.WardrobeOperations={create:create,normalize:normalize,targetKey:key,isTerminal:function(state){return terminal.has(state);},isMutation:function(type){return mutations.has(type);}};
})(window);
