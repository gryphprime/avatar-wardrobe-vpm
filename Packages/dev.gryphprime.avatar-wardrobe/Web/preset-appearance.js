/* A saved appearance belongs to the existing preset and exact scene copies. */
(function(global){
  'use strict';
  function create(options){
    var root=options.root,presetId=options.scope(),summary=null,pending=null,busy=false,serial=0;
    function el(tag,text){var node=document.createElement(tag);if(text!=null)node.textContent=text;return node;}
    var title=el('h3','Saved appearance'),status=el('p','Loading saved appearance…'),actions=el('div'),review=el('div');status.className='subtle';status.setAttribute('role','status');actions.className='snapshot-controls';root.append(title,status,actions,review);
    function valid(ticket){return ticket===serial&&root.isConnected&&options.scope()===presetId;}
    function path(suffix,extra){return '/api/preset_appearance'+suffix+'?presetId='+encodeURIComponent(presetId)+(extra||'');}
    async function request(suffix,extra,write){var result=await options.api(path(suffix,extra),{method:write?'POST':'GET'});if(!result||result.ok!==1)throw new Error(result&&result.message||'Saved appearance is unavailable. Refresh Appearance and try again.');return result;}
    function button(label,action,disabled){var node=el('button',label);node.disabled=busy||!!disabled;node.dataset.locked=String(!!disabled);node.onclick=action;actions.appendChild(node);return node;}
    function draw(){
      actions.replaceChildren();review.replaceChildren();if(!summary)return;
      button(summary.hasSaved?'Replace saved appearance':'Save current appearance',save);
      button('Review restore',restore,!summary.hasSaved);button('Export recipe references',download,!summary.hasSaved);
      if(pending){review.append(el('h4','Review appearance restore'),el('p',pending.message));var list=el('ul');pending.changes.forEach(function(change){list.appendChild(el('li',change));});review.appendChild(list);
        var apply=el('button','Apply reviewed restore');apply.className='primary';apply.disabled=busy||!pending.changes.length;apply.dataset.locked=String(!pending.changes.length);apply.onclick=applyRestore;var cancel=el('button','Cancel restore');cancel.disabled=busy;cancel.onclick=function(){pending=null;draw();};review.append(apply,cancel);}
    }
    async function load(){var ticket=++serial;busy=true;draw();try{var value=await request('',null,false);if(!valid(ticket))return;summary=value;status.textContent=(value.hasSaved?'Saved '+new Date(value.savedAt).toLocaleString()+' · '+value.garments+' garments · '+value.materials+' material slots · '+value.shapes+' shapes. ':'')+value.message;if(value.hasSaved)status.textContent+=' Saving again replaces this preset’s saved appearance.';}catch(error){if(valid(ticket)){summary=null;status.textContent=error.message;}}finally{if(valid(ticket)){busy=false;draw();}}}
    async function save(){if(busy||!summary)return;var ticket=++serial;busy=true;pending=null;draw();status.textContent='Saving appearance references…';try{await request('_save','&revision='+encodeURIComponent(summary.revision),true);if(!valid(ticket))return;await load();if(options.onChange)options.onChange();}catch(error){if(valid(ticket)){status.textContent=error.message;busy=false;draw();}}}
    async function restore(){if(busy)return;var ticket=++serial;busy=true;pending=null;draw();status.textContent='Checking exact copies and asset versions…';try{var result=await request('_review',null,true);if(valid(ticket)){pending=result;status.textContent='The working avatar is unchanged. Review the changes below.';}}catch(error){if(valid(ticket))status.textContent=error.message;}finally{if(valid(ticket)){busy=false;draw();}}}
    async function applyRestore(){if(busy||!pending)return;var ticket=++serial,token=pending.token;busy=true;draw();try{var result=await request('_apply','&token='+encodeURIComponent(token),true);if(!valid(ticket))return;pending=null;await load();status.textContent=result.message;if(options.onChange)options.onChange();}catch(error){if(valid(ticket)){pending=null;status.textContent=error.message;busy=false;draw();}}}
    async function download(){if(busy)return;var ticket=++serial;busy=true;draw();try{var result=await request('_export',null,false);if(!valid(ticket))return;if(result.containsAssets!==false||typeof result.json!=='string')throw new Error('Unexpected export format. No download was created.');var url=URL.createObjectURL(new Blob([result.json],{type:'application/json'})),link=el('a');link.href=url;link.download=String(result.fileName||'wardrobe-appearance.json').replace(/[^a-zA-Z0-9._-]/g,'_');root.appendChild(link);link.click();link.remove();setTimeout(function(){URL.revokeObjectURL(url);},1000);status.textContent='Recipe references exported. No textures, meshes, or purchased files are included.';}catch(error){if(valid(ticket))status.textContent=error.message;}finally{if(valid(ticket)){busy=false;draw();}}}
    return {load:load};
  }
  global.WardrobePresetAppearanceUI={create:create};
})(window);
