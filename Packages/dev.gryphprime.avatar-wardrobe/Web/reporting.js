(function(){
  "use strict";
  function L(key,fallback){return window.WardrobeRuntime&&window.WardrobeRuntime.localize?window.WardrobeRuntime.localize(key,fallback):fallback;}
  function esc(value){return window.WardrobeRuntime.escape(value);}

  function count(value){value=Number(value);return Number.isFinite(value)&&value>=0?Math.floor(value):0;}
  var endpoint="https://reporting.aelchor.com/v1/", version="", installation;
  function id(){return crypto.randomUUID();}
  function installationId(){
    if(installation)return installation;
    try{installation=localStorage.getItem("wardrobe.reporting.installation.v1");if(!/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(installation||"")){installation=id();localStorage.setItem("wardrobe.reporting.installation.v1",installation);}}
    catch(e){installation=id();}
    return installation;
  }
  function short(value,max){return String(value==null?"":value).slice(0,max||500);}
  function names(value){return Array.isArray(value)?value.slice(0,40).map(function(v){return short(v,120);}):[];}
  function itemMetadata(family,variant){
    return {guid:short(variant.guid,128),name:short(family.name),shop:short(variant.shop),product:short(variant.product),
      prefab:short(String(variant.source||"").replace(/\\/g,"/").split("/").pop()),category:short(variant.category,100),
      confidence:short(variant.confidence,100),variant:short(variant.variant,100),colorway:short(variant.colorway,100),
      parts:names(variant.parts),materials:names(variant.mats),physicsComponents:count(variant.phys),contacts:count(variant.contact)};
  }
  async function send(payload,signal){
    var encoded=JSON.stringify(payload);
    if(new TextEncoder().encode(encoded).length>65536)throw new Error(L("ui.this.report.is.too.large.please.shorten.it","This report is too large. Please shorten it."));
    var response=await fetch(endpoint+(payload.type==="misclassification"?"misclassifications":"reports"),{
      method:"POST",headers:{"Content-Type":"application/json"},credentials:"omit",referrerPolicy:"no-referrer",body:encoded,signal:signal
    });
    if(response.status!==200&&response.status!==201)throw new Error(response.status===429?L("ui.too.many.reports.please.try.again.in.an.hour","Too many reports. Please try again in an hour."):response.status===413?L("ui.this.report.is.too.large.please.shorten.it","This report is too large. Please shorten it."):L("ui.could.not.send.the.report.your.text.is.still","Could not send the report. Your text is still here; please try again."));
    var result=await response.json();if(!result.id)throw new Error(L("ui.the.server.did.not.confirm.the.report.please.try","The server did not confirm the report. Please try again."));return result;
  }
  function open(item){
    if(document.getElementById("reportDialog"))return;
    var previous=document.activeElement, dialog=document.createElement("dialog");dialog.id="reportDialog";dialog.className="report-dialog";
    dialog.setAttribute("aria-labelledby","reportTitle");
    dialog.innerHTML=("<form id=\"reportForm\"><h2 id=\"reportTitle\"></h2><p id=\"reportPrivacy\"></p><label>"+esc(L("ui.what.went.wrong","What went wrong?"))+"<textarea name=\"message\" required maxlength=\"8000\" rows=\"5\"></textarea></label>")+
      (item?("<label>"+esc(L("ui.what.should.this.item.be.classified.as","What should this item be classified as?"))+"<input name=\"expectedCategory\" required maxlength=\"200\" placeholder=\""+esc(L("ui.for.example.hair.accessory.or.avatar","For example: hair, accessory, or avatar"))+"\"></label>"):'')+
      ("<label>"+esc(L("ui.email.for.a.reply.optional","Email for a reply (optional)"))+"<input name=\"email\" type=\"email\" maxlength=\"254\" autocomplete=\"email\"></label>")+
      ("<label class=\"report-check\"><input name=\"includeDiagnostics\" type=\"checkbox\" checked> "+esc(L("ui.include.app.version.and.browser.information","Include app version and browser information"))+"</label>")+
      ("<details><summary>"+esc(L("ui.view.information.to.be.sent","View information to be sent"))+"</summary><pre id=\"reportPreview\"></pre></details>")+
      ("<p id=\"reportStatus\" role=\"status\" aria-live=\"polite\"></p><div class=\"actions\"><button type=\"button\" id=\"reportCancel\">"+esc(L("appearance.cancel","Cancel"))+"</button><button type=\"submit\" id=\"reportSend\">"+esc(L("ui.send.report","Send report"))+"</button></div></form>");
    document.body.appendChild(dialog);
    var form=dialog.querySelector("form"), status=dialog.querySelector("#reportStatus"), button=dialog.querySelector("#reportSend"), busy=false,lastBody="",requestId=id();
    dialog.querySelector("h2").textContent=item?L("report.item","Item was misclassified"):L("report.openBug","Report a problem");
    dialog.querySelector("#reportPrivacy").textContent=L("ui.reports.are.sent.to.aelchor.and.kept.for.90","Reports are sent to Aelchor and kept for 90 days. An anonymous browser ID groups reports. ")+(item?L("ui.item.names.material.names.and.classification.metadata.are.included","Item names, material names and classification metadata are included; review them below. "):"")+L("ui.no.screenshot.logs.project.paths.or.asset.files.are","No screenshot, logs, project paths or asset files are collected automatically. Your description and optional email are also sent. Unchecking browser information removes app version and browser details; item classification metadata remains included.");
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
      var payload=body();if(!payload.message||(item&&!payload.expectedCategory)){status.textContent=L("ui.please.describe.the.problem.and.the.expected.classification","Please describe the problem and the expected classification.");return;}
      var encoded=JSON.stringify(payload);if(encoded!==lastBody){requestId=id();lastBody=encoded;}payload.requestId=requestId;
      busy=true;Array.from(form.elements).forEach(function(el){el.disabled=true;});status.textContent=L("ui.sending","Sending…");
      var controller=new AbortController(),timer=setTimeout(function(){controller.abort();},15000);
      try{var result=await send(payload,controller.signal);status.textContent=L("ui.thank.you.report.received","Thank you. Report received: ")+result.id;button.hidden=true;form.onsubmit=function(e){e.preventDefault();};dialog.querySelector("#reportCancel").textContent=L("library.close","Close");}
      catch(error){status.textContent=error.name==="AbortError"?L("ui.the.request.timed.out.your.text.is.still.here","The request timed out. Your text is still here; retrying will not create a duplicate."):error.message;}
      finally{clearTimeout(timer);busy=false;Array.from(form.elements).forEach(function(el){el.disabled=false;});}
    };
    dialog.showModal();form.elements.message.focus();
  }
  window.WardrobeReporting={setVersion:function(v){version=v||"";},openBug:function(){open(null);},openItem:function(f,v){open(itemMetadata(f,v));},itemMetadata:itemMetadata,send:send};
  var button=document.getElementById("settingsReportBug");if(button)button.onclick=function(){open(null);};
})();
