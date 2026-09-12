/* A saved appearance belongs to the existing preset and exact scene copies. */
(function(global){
  'use strict';
  function L(key,fallback){return window.WardrobeRuntime&&window.WardrobeRuntime.localize?window.WardrobeRuntime.localize(key,fallback):fallback;}
  function esc(value){return window.WardrobeRuntime.escape(value);}

  function create(options){
    var root=options.root,presetId=options.scope(),summary=null,pending=null,busy=false,serial=0,inspectionTimer=null,inspectionAttempts=0;
    function el(tag,text){var node=document.createElement(tag);if(text!=null)node.textContent=text;return node;}
    var title=el('h3',L("ui.saved.appearance","Saved appearance")),status=el('p',L("ui.loading.saved.appearance","Loading saved appearance…")),actions=el('div'),review=el('div');status.className='subtle';status.setAttribute('role','status');actions.className='snapshot-controls';root.append(title,status,actions,review);
    function valid(ticket){return ticket===serial&&root.isConnected&&options.scope()===presetId;}
    function path(suffix,extra){return '/api/preset_appearance'+suffix+'?presetId='+encodeURIComponent(presetId)+(extra||'');}
    async function request(suffix,extra,write){var result=await options.api(path(suffix,extra),{method:write?'POST':'GET'});if(!result||result.ok!==1)throw new Error(result&&result.message||L("ui.saved.appearance.is.unavailable.refresh.appearance.and.try.again","Saved appearance is unavailable. Refresh Appearance and try again."));return result;}
    function button(label,action,disabled){var node=el('button',label);node.disabled=busy||!!disabled;node.dataset.locked=String(!!disabled);node.onclick=action;actions.appendChild(node);return node;}
    function draw(){
      actions.replaceChildren();review.replaceChildren();if(!summary){if(!busy)button(L("upload.refresh","Refresh"),function(){load();});return;}
      button(summary.hasSaved?L("ui.replace.saved.appearance","Replace saved appearance"):L("ui.save.current.appearance","Save current appearance"),save,!summary.revision);
      if(!summary.revision)button(L("upload.refresh","Refresh"),function(){load();});
      button(L("ui.review.restore","Review restore"),restore,!summary.hasSaved);button(L("ui.export.recipe.references","Export recipe references"),download,!summary.hasSaved);
      if(pending){review.append(el('h4',L("ui.review.appearance.restore","Review appearance restore")),el('p',pending.message));var list=el('ul');pending.changes.forEach(function(change){list.appendChild(el('li',change));});review.appendChild(list);
        var apply=el('button',L("ui.apply.reviewed.restore","Apply reviewed restore"));apply.className='primary';apply.disabled=busy||!pending.changes.length;apply.dataset.locked=String(!pending.changes.length);apply.onclick=applyRestore;var cancel=el('button',L("ui.cancel.restore","Cancel restore"));cancel.disabled=busy;cancel.onclick=function(){pending=null;draw();};review.append(apply,cancel);}
    }
    function cancelInspection(){clearTimeout(inspectionTimer);inspectionTimer=null;}
    function scheduleInspection(ticket){
      cancelInspection();
      if(!valid(ticket)||!summary||summary.revision||inspectionAttempts>=30)return;
      inspectionTimer=setTimeout(function(){
        inspectionTimer=null;if(!valid(ticket))return;
        if(document.hidden){scheduleInspection(ticket);return;}
        inspectionAttempts++;load(true);
      },1000);
    }
    async function load(inspecting){cancelInspection();if(!inspecting)inspectionAttempts=0;var ticket=++serial;busy=true;draw();try{var value=await request('',null,false);if(!valid(ticket))return;summary=value;status.textContent=(value.hasSaved?L("ui.saved","Saved ")+new Date(value.savedAt).toLocaleString()+' · '+value.garments+' garments · '+value.materials+' material slots · '+value.shapes+' shapes. ':'')+value.message;if(value.hasSaved)status.textContent+=' Saving again replaces this preset’s saved appearance.';if(!value.revision)status.textContent=L("ui.inspecting.the.avatar.before.saving","Inspecting the avatar before saving… ")+(inspectionAttempts>=30?L("ui.use.refresh.when.unity.is.ready","Use Refresh when Unity is ready."):'');}catch(error){if(valid(ticket)){summary=null;status.textContent=error.message;}}finally{if(valid(ticket)){busy=false;draw();scheduleInspection(ticket);}}}
    async function save(){if(busy||!summary||!summary.revision)return;cancelInspection();var ticket=++serial;busy=true;pending=null;draw();status.textContent=L("ui.saving.appearance.references","Saving appearance references…");try{await request('_save','&revision='+encodeURIComponent(summary.revision),true);if(!valid(ticket))return;await load();if(options.onChange)options.onChange();}catch(error){if(valid(ticket)){status.textContent=error.message;busy=false;draw();}}}
    async function restore(){if(busy)return;cancelInspection();var ticket=++serial;busy=true;pending=null;draw();status.textContent=L("ui.checking.exact.copies.and.asset.versions","Checking exact copies and asset versions…");try{var result=await request('_review',null,true);if(valid(ticket)){pending=result;status.textContent=L("ui.the.working.avatar.is.unchanged.review.the.changes.below","The working avatar is unchanged. Review the changes below.");}}catch(error){if(valid(ticket))status.textContent=error.message;}finally{if(valid(ticket)){busy=false;draw();}}}
    async function applyRestore(){if(busy||!pending)return;var ticket=++serial,token=pending.token;busy=true;draw();try{var result=await request('_apply','&token='+encodeURIComponent(token),true);if(!valid(ticket))return;pending=null;await load();status.textContent=result.message;if(options.onChange)options.onChange();}catch(error){if(valid(ticket)){pending=null;status.textContent=error.message;busy=false;draw();}}}
    async function download(){if(busy)return;var ticket=++serial;busy=true;draw();try{var result=await request('_export',null,false);if(!valid(ticket))return;if(result.containsAssets!==false||typeof result.json!=='string')throw new Error(L("ui.unexpected.export.format.no.download.was.created","Unexpected export format. No download was created."));var url=URL.createObjectURL(new Blob([result.json],{type:'application/json'})),link=el('a');link.href=url;link.download=String(result.fileName||'wardrobe-appearance.json').replace(/[^a-zA-Z0-9._-]/g,'_');root.appendChild(link);link.click();link.remove();setTimeout(function(){URL.revokeObjectURL(url);},1000);status.textContent=L("ui.recipe.references.exported.no.textures.meshes.or.purchased.files","Recipe references exported. No textures, meshes, or purchased files are included.");}catch(error){if(valid(ticket))status.textContent=error.message;}finally{if(valid(ticket)){busy=false;draw();}}}
    return {load:load};
  }
  global.WardrobePresetAppearanceUI={create:create};
})(window);
