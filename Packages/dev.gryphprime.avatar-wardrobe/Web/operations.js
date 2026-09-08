/* Desktop intent projection: acceptance is distinct from confirmed Unity state. */
(function(global){
  'use strict';
  var terminal=new Set(['succeeded','failed','cancelled','superseded','needs-review']);
  var mutations=new Set(['wear-outfit','replace-outfit','remove-outfit']);
  function key(target){return JSON.stringify([target.projectId,target.sceneGuid,target.avatarId,target.avatarInstanceId,target.session,target.scopeId]);}
  function uuid(){return global.crypto.randomUUID();}
  function normalize(type,input,context,id,predecessor){
    if(!context||!context.revision||!context.avatarInstanceId)throw new Error('Pin an avatar and wait for Unity to confirm its current state.');
    if(!['wear-outfit','replace-outfit','remove-outfit','prepare-preview','capture-source','render-snapshot'].includes(type))throw new Error('Unsupported operation.');
    input=input||{};
    var payload={};
    ['variantId','assetVersion','instanceId','previewToken','view','menuGroup','itemPath'].forEach(function(field){if(input[field]!=null)payload[field]=String(input[field]);});
    ['addCopy','allowUnverified','createToggles','before'].forEach(function(field){if(input[field]!=null)payload[field]=!!input[field];});
    payload.zoom=input.zoom==null?1:Number(input.zoom);
    if(!Number.isFinite(payload.zoom)||payload.zoom<=0||payload.zoom>3)throw new Error('Invalid snapshot zoom.');
    if(type!=='render-snapshot'&&type!=='prepare-preview'&&type!=='capture-source'&&!/^[a-f0-9]{32}$/i.test(payload.variantId||''))throw new Error('Choose a specific outfit variant first.');
    if((type==='replace-outfit'||type==='remove-outfit')&&!payload.instanceId)throw new Error('Choose the exact worn copy first.');
    return {id:id||uuid(),type:type,target:{projectId:context.projectId,sceneGuid:context.sceneGuid||'',avatarId:context.avatarId,avatarInstanceId:context.avatarInstanceId,session:context.session,scopeId:input.scopeId||context.scopeId||'common'},
      precondition:{observedRevision:context.revision,afterOperationId:predecessor||''},payload:payload};
  }
  function create(options){
    var records=new Map(),context=null,enabled=false,timer=null,polling=false,waiters=new Map();
    function ordered(){return Array.from(records.values()).sort(function(a,b){return (a.createdAt||0)-(b.createdAt||0);});}
    function changed(){
      var all=ordered();while(records.size>512){var old=all.find(function(record){return terminal.has(record.state);});if(!old)break;records.delete(old.id);all=all.filter(function(record){return record!==old;});}
      if(options.onChange)options.onChange(all,context);
    }
    function merge(record){
      if(!record||!record.id)return;
      var old=records.get(record.id);records.set(record.id,Object.assign({},old,record));
      if(terminal.has(record.state)){
        var waiting=waiters.get(record.id);if(waiting){waiters.delete(record.id);waiting.forEach(function(done){done(record);});}
        if((!old||!terminal.has(old.state))&&options.onSettled)options.onSettled(record);
      }
    }
    async function refresh(){
      if(!enabled||polling)return;polling=true;
      try{
        // Both routes are desktop/thread-safe reads and remain responsive while Unity is occupied.
        var all=await options.api('/api/operations',{method:'GET',timeout:4000});
        (all.items||all.operations||[]).forEach(merge);changed();
        var next=await options.api('/api/operation_context',{method:'GET',timeout:4000});
        if(next&&next.revision){context=next;changed();}
      }catch(error){if(context)context=Object.assign({},context,{waitingReason:'Unity connection unavailable'});changed();}
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
      if(!enabled)throw new Error('Open Library and Dressing Room from the Unity launcher to use queued changes.');
      if(context&&key(Object.assign({},context,{scopeId:command.target.scopeId}))!==key(command.target))throw new Error('The target changed. Review the outfit for the newly selected avatar.');
      var old=records.get(command.id);
      if(old&&JSON.stringify(old.command)!==JSON.stringify(command))throw new Error('This operation ID already belongs to another command.');
      records.set(command.id,Object.assign({},old,{id:command.id,type:command.type,command:command,label:label||(old&&old.label)||command.payload.variantId||command.type,createdAt:old?old.createdAt:Date.now()/1000,state:'submitting'}));changed();
      try{var record=await options.api('/api/operations',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(command),timeout:8000});merge(record);changed();return record;}
      catch(error){var record=records.get(command.id);record.state='acceptance-unknown';record.error='Acceptance is unconfirmed. Check or retry this same operation ID. '+error.message;changed();throw error;}
    }
    async function cancel(id){var result=await options.api('/api/operations/cancel?id='+encodeURIComponent(id),{method:'POST',timeout:5000});merge(result);changed();return result;}
    function wait(id){var record=records.get(id);if(record&&terminal.has(record.state))return Promise.resolve(record);return new Promise(function(resolve){var group=waiters.get(id)||[];group.push(resolve);waiters.set(id,group);});}
    function setHost(value){enabled=!!value;if(enabled&&!timer){refresh();timer=setInterval(refresh,1500);}if(!enabled&&timer){clearInterval(timer);timer=null;context=null;changed();}}
    return {build:build,submit:submit,cancel:cancel,wait:wait,refresh:refresh,setHost:setHost,context:function(){return context;},list:ordered,enabled:function(){return enabled;},pending:function(){return Array.from(records.values()).filter(function(x){return mutations.has(x.type)&&!terminal.has(x.state);});},close:function(){if(timer)clearInterval(timer);timer=null;}};
  }
  global.WardrobeOperations={create:create,normalize:normalize,targetKey:key,isTerminal:function(state){return terminal.has(state);},isMutation:function(type){return mutations.has(type);}};
})(window);
