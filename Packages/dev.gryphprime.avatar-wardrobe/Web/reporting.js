(function(){
  "use strict";
  var endpoint="https://reporting.aelchor.com/v1/", version="", installation;
  function id(){return crypto.randomUUID();}
  function installationId(){
    if(installation)return installation;
    try{installation=localStorage.getItem("wardrobe.reporting.installation.v1");if(!/^[0-9a-f-]{36}$/i.test(installation||"")){installation=id();localStorage.setItem("wardrobe.reporting.installation.v1",installation);}}
    catch(e){installation=id();}
    return installation;
  }
  function short(value,max){return String(value==null?"":value).slice(0,max||500);}
  function names(value){return Array.isArray(value)?value.slice(0,40).map(function(v){return short(v,120);}):[];}
  function itemMetadata(family,variant){
    return {guid:short(variant.guid,128),name:short(family.name),shop:short(variant.shop),product:short(variant.product),
      prefab:short(String(variant.source||"").replace(/\\/g,"/").split("/").pop()),category:short(variant.category,100),
      confidence:short(variant.confidence,100),variant:short(variant.variant,100),colorway:short(variant.colorway,100),
      parts:names(variant.parts),materials:names(variant.mats),physicsComponents:Number(variant.phys)||0,contacts:Number(variant.contact)||0};
  }
  async function send(payload,signal){
    var response=await fetch(endpoint+(payload.type==="misclassification"?"misclassifications":"reports"),{
      method:"POST",headers:{"Content-Type":"application/json"},credentials:"omit",referrerPolicy:"no-referrer",body:JSON.stringify(payload),signal:signal
    });
    if(!response.ok)throw new Error(response.status===429?"Too many reports. Please try again in an hour.":response.status===413?"This report is too large. Please shorten it.":"Could not send the report. Your text is still here; please try again.");
    var result=await response.json();if(!result.id)throw new Error("The server did not confirm the report. Please try again.");return result;
  }
  function open(item){
    if(document.getElementById("reportDialog"))return;
    var previous=document.activeElement, dialog=document.createElement("dialog");dialog.id="reportDialog";dialog.className="report-dialog";
    dialog.setAttribute("aria-labelledby","reportTitle");
    dialog.innerHTML='<form id="reportForm"><h2 id="reportTitle"></h2><p id="reportPrivacy"></p><label>What went wrong?<textarea name="message" required maxlength="8000" rows="5"></textarea></label>'+
      (item?'<label>What should this item be classified as?<input name="expectedCategory" required maxlength="200" placeholder="For example: hair, accessory, or avatar"></label>':'')+
      '<label>Email for a reply (optional)<input name="email" type="email" maxlength="254" autocomplete="email"></label>'+
      '<label class="report-check"><input name="includeDiagnostics" type="checkbox" checked> Include app version and browser information</label>'+
      '<details><summary>View information to be sent</summary><pre id="reportPreview"></pre></details>'+
      '<p id="reportStatus" role="status" aria-live="polite"></p><div class="actions"><button type="button" id="reportCancel">Cancel</button><button type="submit" id="reportSend">Send report</button></div></form>';
    document.body.appendChild(dialog);
    var form=dialog.querySelector("form"), status=dialog.querySelector("#reportStatus"), button=dialog.querySelector("#reportSend"), busy=false,lastBody="",requestId=id();
    dialog.querySelector("h2").textContent=item?"Item was misclassified":"Report a problem";
    dialog.querySelector("#reportPrivacy").textContent="Reports are sent to Aelchor and kept for 90 days. An anonymous browser ID groups reports. "+(item?"Item names, material names and classification metadata are included; review them below. ":"")+"No screenshot, logs, project paths or asset files are collected. Your description and optional email are also sent.";
    function body(){
      var diagnostics=form.elements.includeDiagnostics.checked?{browser:short(navigator.userAgent,1000),language:short(navigator.language,100)}:null;
      var payload={appId:"avatar-wardrobe",installationId:installationId(),type:item?"misclassification":"bug",message:form.elements.message.value.trim(),email:form.elements.email.value.trim()||null,appVersion:diagnostics?short(version,100):"",diagnostics:diagnostics};
      if(item){payload.item=item;payload.expectedCategory=form.elements.expectedCategory.value.trim();}return payload;
    }
    function preview(){dialog.querySelector("#reportPreview").textContent=JSON.stringify(body(),null,2);}
    function close(){if(busy)return;dialog.close();dialog.remove();if(previous&&previous.isConnected)previous.focus();}
    dialog.querySelector("#reportCancel").onclick=close;
    dialog.addEventListener("cancel",function(e){e.preventDefault();close();});
    dialog.addEventListener("keydown",function(e){e.stopPropagation();});
    form.addEventListener("input",preview);preview();
    form.onsubmit=async function(e){
      e.preventDefault();if(busy)return;
      var payload=body();if(!payload.message||(item&&!payload.expectedCategory)){status.textContent="Please describe the problem and the expected classification.";return;}
      var encoded=JSON.stringify(payload);if(encoded!==lastBody){requestId=id();lastBody=encoded;}payload.requestId=requestId;
      busy=true;Array.from(form.elements).forEach(function(el){el.disabled=true;});status.textContent="Sending…";
      var controller=new AbortController(),timer=setTimeout(function(){controller.abort();},15000);
      try{var result=await send(payload,controller.signal);status.textContent="Thank you. Report received: "+result.id;button.hidden=true;form.onsubmit=function(e){e.preventDefault();};dialog.querySelector("#reportCancel").textContent="Close";}
      catch(error){status.textContent=error.name==="AbortError"?"The request timed out. Your text is still here; retrying will not create a duplicate.":error.message;}
      finally{clearTimeout(timer);busy=false;Array.from(form.elements).forEach(function(el){el.disabled=false;});}
    };
    dialog.showModal();form.elements.message.focus();
  }
  window.WardrobeReporting={setVersion:function(v){version=v||"";},openBug:function(){open(null);},openItem:function(f,v){open(itemMetadata(f,v));},itemMetadata:itemMetadata,send:send};
  var button=document.getElementById("settingsReportBug");if(button)button.onclick=function(){open(null);};
})();
