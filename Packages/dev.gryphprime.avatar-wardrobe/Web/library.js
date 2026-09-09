/* The library is served by the external host and remains available without Unity. */
(function(global){
  'use strict';
  var R=global.WardrobeRuntime,root,items=[],busy=false,translate=null;
  function T(key){var args=Array.prototype.slice.call(arguments);return translate?translate.apply(null,args):key;}
  function el(id){return document.getElementById(id);}
  function status(message){R.text(el('libraryStatus'),message);}
  async function request(path,options){
    var response=await fetch(path,Object.assign({cache:'no-store'},options));
    var result=await response.json();
    if(!response.ok||result.ok===0)throw new Error(result.message||T('library.error'));
    return result;
  }
  function post(path){return request(path,{method:'POST',headers:{'X-Wardrobe-Request':'1'}});}
  function matches(item,query){return [item.product,item.creator,item.filename,item.hash].join(' ').toLowerCase().includes(query.toLowerCase());}
  function paint(){
    var list=el('libraryList'),query=el('librarySearch').value;
    var shown=items.filter(function(item){return matches(item,query);});
    list.replaceChildren();
    if(!shown.length){var empty=document.createElement('p');empty.className='subtle';empty.textContent=items.length?T('library.noMatches'):T('library.empty');list.appendChild(empty);return;}
    shown.forEach(function(item){
      var row=document.createElement('article');row.className='library-item';
      var heading=document.createElement('h3');heading.textContent=item.product||item.filename;
      var detail=document.createElement('p');detail.className='subtle';detail.textContent=[item.creator,item.filename,item.hash.slice(0,12)].filter(Boolean).join(' · ');
      var state=document.createElement('p');state.textContent=item.usage.length?T('library.used',item.usage.length):T('library.unused');
      var variants=document.createElement('details'),summary=document.createElement('summary');summary.textContent=T('library.references');variants.appendChild(summary);
      var names=document.createElement('ul');
      item.files.filter(function(file){return /\.prefab$/i.test(file.path);}).forEach(function(file){var li=document.createElement('li');li.textContent=file.path;names.appendChild(li);});
      item.usage.forEach(function(use){var li=document.createElement('li');li.textContent=[use.project,use.avatar,use.instance].filter(Boolean).join(' / ')+(use.observed?' · Last seen '+new Date(use.observed*1000).toLocaleString()+' · '+(use.match==='prefab-bytes'?'Prefab bytes match; dependencies may differ':'GUID match; version unconfirmed'):' · Project import');names.appendChild(li);});
      variants.appendChild(names);
      var action=document.createElement('button');action.textContent=T('library.review');action.disabled=busy;action.onclick=function(){review(item);};
      var impact=document.createElement('button');impact.textContent=T('library.compare');impact.disabled=busy||items.length<2;impact.onclick=function(){compare(item);};
      row.append(heading,detail,state,variants,action,impact,metadataEditor(item));list.appendChild(row);
    });
  }
  function metadataEditor(item){
    var details=document.createElement('details'),summary=document.createElement('summary');summary.textContent=T('library.editDetails');details.appendChild(summary);
    var fields={};[['creator',T('library.creator'),200],['product',T('library.product'),200],['source',T('library.source'),2048]].forEach(function(field){var label=document.createElement('label'),input=document.createElement('input');label.textContent=field[1];input.type=field[0]==='source'?'url':'text';input.maxLength=field[2];input.value=item[field[0]==='source'?'source_url':field[0]]||'';fields[field[0]]=input;label.appendChild(input);details.appendChild(label);});
    var save=document.createElement('button');save.textContent=T('library.saveDetails');save.onclick=async function(){if(busy||!fields.source.reportValidity())return;save.disabled=true;try{var result=await post('/api/library/metadata?hash='+encodeURIComponent(item.hash)+'&creator='+encodeURIComponent(fields.creator.value)+'&product='+encodeURIComponent(fields.product.value)+'&source='+encodeURIComponent(fields.source.value));status(result.message);await refresh();}catch(error){status(error.message);}finally{save.disabled=false;}};details.appendChild(save);return details;
  }
  async function refresh(){try{var result=await request('/api/library');items=result.items;R.text(el('libraryProject'),result.project||T('library.noProject'));paint();}catch(error){status(error.message);}}
  async function add(input){
    var files=Array.from(input.files||[]).filter(function(file){return /\.(zip|unitypackage)$/i.test(file.name);});if(busy)return;
    if(!files.length){status(T('library.noArchives'));return;}
    busy=true;el('libraryFiles').disabled=true;el('libraryFolder').disabled=true;paint();
    try{
      var results=document.createElement('ul');results.className='library-results';results.setAttribute('aria-label','File import results');var previous=root&&root.querySelector('.library-results');if(previous)previous.remove();(root||el('library')).appendChild(results);
      var failed=0;
      for(var file of files){
        var row=document.createElement('li');row.textContent=file.name+' · '+T('library.adding',file.name);results.appendChild(row);status(T('library.adding',file.name));
        try{var result=await request('/api/library/add?filename='+encodeURIComponent(file.name),{method:'POST',headers:{'X-Wardrobe-Request':'1'},body:file});row.textContent=file.name+' · '+(result.duplicate?T('library.duplicate'):T('library.added'));}
        catch(error){failed++;row.className='failed';row.textContent=file.name+' · '+error.message;}
      }
      await refresh();status(T('library.batchResult',files.length-failed,failed));
    }catch(error){status(error.message);}
    finally{busy=false;el('libraryFiles').disabled=false;el('libraryFolder').disabled=false;input.value='';paint();}
  }
  async function review(item){
    if(busy)return;busy=true;paint();status(T('library.checking'));
    try{
      var plan=await post('/api/library/import_review?hash='+encodeURIComponent(item.hash));
      var pane=el('libraryReview');pane.hidden=false;pane.replaceChildren();
      var title=document.createElement('h3');title.textContent=T('library.importTitle',item.product);
      var text=document.createElement('p');text.textContent=T('library.importSummary',plan.files.filter(function(f){return f.state==='new';}).length,plan.conflicts,plan.codeFiles);
      var details=document.createElement('details'),summary=document.createElement('summary');summary.textContent=T('library.changes');details.appendChild(summary);
      var list=document.createElement('ul');plan.files.forEach(function(file){var li=document.createElement('li');li.textContent=file.state+' · '+file.destination+(file.kind==='code'?' · executable code':'');list.appendChild(li);});details.appendChild(list);
      var label=document.createElement('label'),code=document.createElement('input');code.type='checkbox';label.append(code,document.createTextNode(T('library.codeConsent')));label.hidden=!plan.codeFiles;
      var apply=document.createElement('button');apply.className='primary';apply.textContent=T('library.apply');apply.disabled=plan.conflicts>0||(plan.guidConflicts||[]).length>0||plan.codeFiles>0;
      code.onchange=function(){apply.disabled=plan.conflicts>0||(plan.guidConflicts||[]).length>0||(plan.codeFiles>0&&!code.checked);};
      var cancel=document.createElement('button');cancel.textContent=T('library.cancel');cancel.onclick=function(){pane.hidden=true;status(T('library.cancelled'));};
      apply.onclick=async function(){
        busy=true;apply.disabled=true;cancel.disabled=true;el('libraryFiles').disabled=true;el('libraryFolder').disabled=true;paint();
        try{var result=await post('/api/library/import_apply?token='+encodeURIComponent(plan.token)+'&allowCode='+(code.checked?'1':'0'));status(result.message);pane.hidden=true;await refresh();}
        catch(error){status(error.message);pane.hidden=true;}
        finally{busy=false;cancel.disabled=false;el('libraryFiles').disabled=false;el('libraryFolder').disabled=false;paint();}
      };
      var dependencies=document.createElement('details'),dependencyTitle=document.createElement('summary');
      dependencyTitle.textContent=T('library.dependencies',(plan.missingDependencies||[]).length,(plan.guidConflicts||[]).length);dependencies.appendChild(dependencyTitle);
      var issues=document.createElement('ul');
      (plan.missingDependencies||[]).forEach(function(item){var li=document.createElement('li');li.textContent=T('library.missingGuid',item.guid,item.referencedBy.join(', '));issues.appendChild(li);});
      (plan.guidConflicts||[]).forEach(function(item){var li=document.createElement('li');li.textContent=T('library.guidConflict',item.destination,item.existingPaths.join(', '));issues.appendChild(li);});
      (plan.dependencyScanIncomplete||[]).forEach(function(path){var li=document.createElement('li');li.textContent=T('library.incomplete',path);issues.appendChild(li);});
      dependencies.hidden=!issues.children.length;dependencies.appendChild(issues);
      pane.append(title,text,details,dependencies,label,apply,cancel);status(T('library.ready'));title.tabIndex=-1;title.focus();
    }catch(error){status(error.message);}
    finally{busy=false;paint();}
  }
  function compare(item){
    var pane=el('libraryReview');pane.hidden=false;pane.replaceChildren();
    var heading=document.createElement('h3');heading.textContent=T('library.compareTitle',item.product);
    var select=document.createElement('select');select.setAttribute('aria-label',T('library.newVersion'));
    items.filter(function(other){return other.hash!==item.hash;}).forEach(function(other){var option=document.createElement('option');option.value=other.hash;option.textContent=other.product+' · '+other.hash.slice(0,12);select.appendChild(option);});
    var button=document.createElement('button');button.textContent=T('library.impact');
    var output=document.createElement('p');output.setAttribute('role','status');
    button.onclick=async function(){button.disabled=true;try{var result=await request('/api/library/impact?old='+item.hash+'&new='+select.value);output.textContent=T('library.impactSummary',result.changed.length,result.removed.length,result.added.length,result.guidChanges.length,result.usage.length);var previous=pane.querySelector('[data-impact-usage]');if(previous)previous.remove();var uses=document.createElement('ul');uses.dataset.impactUsage='1';result.usage.forEach(function(use){var row=document.createElement('li');row.textContent=[use.project,use.avatar,use.instance].filter(Boolean).join(' / ')+(use.match==='guid-only'?' · Version unconfirmed':'');uses.appendChild(row);});pane.appendChild(uses);}catch(error){output.textContent=error.message;}finally{button.disabled=false;}};
    var cancel=document.createElement('button');cancel.textContent=T('library.close');cancel.onclick=function(){pane.hidden=true;status(T('library.cancelled'));};pane.append(heading,select,button,output,cancel);
  }
  global.WardrobeLibrary={localize:function(t){translate=t;if(root&&root.dataset.ready)paint();},show:function(){
    root=el('library');if(!root.dataset.ready){root.dataset.ready='1';el('libraryFiles').onchange=function(){add(this);};el('libraryFolder').onchange=function(){add(this);};el('librarySearch').oninput=paint;el('libraryRefresh').onclick=refresh;}refresh();
  },addFiles:function(files){return add({files:files,value:""});},matches:matches};
})(window);
