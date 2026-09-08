const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const source = fs.readFileSync('Packages/dev.gryphprime.avatar-wardrobe/Web/upload.js','utf8');
const start = source.indexOf('  function upStartJob(');
const end = source.indexOf('  upEl("upJobCancel")', start);
(async () => {
  for (const terminal of [{done:1,ok:1},{done:1,ok:0},{done:1,ok:0,message:'Cancelled'},{done:1,ok:0,message:'Unknown or expired job'}]) {
    const results=[], requests=[];
    const elements=new Map();
    const context={crypto:{randomUUID:()=> '01234567-0123-0123-0123-012345678901'},upJobToken:0,upJobTimer:null,upJobPolling:false,
      upShowJob(){},upStopPoll(){},upEndJob:(ok,msg)=>results.push({ok,msg}),
      upEl:id=>{if(!elements.has(id))elements.set(id,{style:{}});return elements.get(id);},
      setInterval(){return 1;},encodeURIComponent,T:x=>x,
      request:url=>{requests.push(url);return requests.length===1?Promise.reject(new Error('lost response')):Promise.resolve({...terminal,job:'01234567012301230123012345678901'});}};
    vm.runInNewContext(source.slice(start,end)+'\nupStartJob("/api/batch_upload_presets?ids=test","Upload",true);',context);
    await new Promise(resolve=>setImmediate(resolve));
    assert.equal(results.length,1,JSON.stringify(terminal));
    assert.equal(results[0].ok,!!terminal.ok);
    assert.equal(requests.length,2,'must not resend upload');
    assert.match(requests[1],/batch_job\?job=01234567012301230123012345678901$/);
  }
  console.log('PASS: completed success, failure, cancellation and expired request recovery, with no duplicate upload.');
})().catch(e=>{console.error(e);process.exitCode=1;});
