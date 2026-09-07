const assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm'),path=require('node:path');
const source=fs.readFileSync(path.join(__dirname,'../Packages/dev.gryphprime.avatar-wardrobe/Web/reporting.js'),'utf8');
function setup(fetcher){
 const context={window:{},document:{getElementById:()=>null},fetch:fetcher};
 vm.runInNewContext(source,context);return context.window.WardrobeReporting;
}
(async()=>{
 let calls=[];const api=setup(async(url,options)=>{calls.push({url,options});return {ok:true,json:async()=>({id:'confirmed'})};});
 const item=api.itemMetadata({id:'internal-family-path',name:'Hat'}, {guid:'abc',source:'C:\\Users\\Private\\Assets\\Hat.prefab',category:'outfit',confidence:'high',parts:['Brim'],mats:['Felt'],token:'secret',assignedName:'Private avatar'});
 assert.equal(item.prefab,'Hat.prefab');assert(!JSON.stringify(item).includes('Private'));assert(!JSON.stringify(item).includes('secret'));assert(!JSON.stringify(item).includes('internal-family-path'));assert.equal(item.category,'outfit');
 assert.equal(api.itemMetadata({}, {parts:Array(100).fill('x'.repeat(500))}).parts.length,40);
 assert.equal(api.itemMetadata({}, {parts:['x'.repeat(500)]}).parts[0].length,120);
 const bug={type:'bug',message:'broken',requestId:'retry-id'};
 await api.send(bug);await api.send(bug);await api.send({type:'misclassification',item});
 assert(calls[0].url.endsWith('/v1/reports'));assert(calls[2].url.endsWith('/v1/misclassifications'));
 assert.equal(calls[0].options.credentials,'omit');assert.equal(calls[0].options.referrerPolicy,'no-referrer');assert.equal(calls[0].options.body,calls[1].options.body);
 await assert.rejects(()=>setup(async()=>({ok:false,status:429})).send(bug),/hour/);
 await assert.rejects(()=>setup(async()=>({ok:false,status:503})).send(bug),/text is still here/);
 await assert.rejects(()=>setup(async()=>({ok:true,json:async()=>({})})).send(bug),/did not confirm/);
 console.log('Reporting: separate routes, metadata minimization, bounded fields, retry payload and error handling passed.');
})().catch(e=>{console.error(e);process.exitCode=1;});
