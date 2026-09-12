/* Native gestures carry identities only. Buttons and drops call the same action callbacks. */
(function(global){
  'use strict';
  var mime='application/x-avatar-wardrobe+json',current=null;
  function normalize(raw){
    if(!raw||typeof raw!=='object'||raw.version!==1||typeof raw.familyId!=='string'||raw.familyId.length>256||typeof raw.targetKey!=='string'||raw.targetKey.length>4096)throw new Error('Invalid outfit drag.');
    if(raw.variantId&&!/^[a-f0-9]{32}$/i.test(raw.variantId))throw new Error('Invalid outfit variant.');
    return {version:1,familyId:raw.familyId,variantId:raw.variantId||'',targetKey:raw.targetKey};
  }
  function source(node,payload){
    node.draggable=true;node.ondragstart=function(event){try{current=normalize(payload());event.dataTransfer.setData(mime,JSON.stringify(current));event.dataTransfer.effectAllowed='copy';}catch(error){event.preventDefault();current=null;}};
    node.ondragend=function(){current=null;document.querySelectorAll('.drop-ready').forEach(function(el){el.classList.remove('drop-ready');});};
  }
  function target(node,options){
    node.addEventListener('dragover',function(event){var files=Array.from(event.dataTransfer.types||[]).includes('Files');if((files&&options.files)||(!files&&current&&options.outfit)){event.preventDefault();event.dataTransfer.dropEffect='copy';node.classList.add('drop-ready');}});
    node.addEventListener('dragleave',function(event){if(!node.contains(event.relatedTarget))node.classList.remove('drop-ready');});
    node.addEventListener('drop',function(event){
      node.classList.remove('drop-ready');event.preventDefault();event.stopPropagation();
      try{
        if(event.dataTransfer.files.length){if(!options.files)throw new Error('Import outfit packages in Unity, then refresh the wardrobe.');return options.files(Array.from(event.dataTransfer.files));}
        var text=event.dataTransfer.getData(mime);if(text.length>8192)throw new Error('Invalid drag payload.');var value=normalize(JSON.parse(text));
        if(!options.outfit)throw new Error('Choose a supported drop target.');options.outfit(value);
      }catch(error){if(options.error)options.error(error.message);}
      finally{current=null;}
    });
  }
  global.WardrobeDragDrop={source:source,target:target,normalize:normalize};
})(window);
