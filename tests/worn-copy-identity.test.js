const {test}=require('node:test'), assert=require('node:assert/strict');
const fs=require('node:fs'),vm=require('node:vm'),path=require('node:path');
const source=fs.readFileSync(path.join(__dirname,'../Packages/dev.gryphprime.avatar-wardrobe/Web/wardrobe.js'),'utf8');
const block=source.slice(source.indexOf('      var copies=installedPresets.find('),source.indexOf('      copy.parentElement.hidden='));
function render(record,selected=0){
 const copy={value:''},context={installedPresets:[Object.assign({id:'common'},record)],id:'common',copy,detailInstanceId:selected,esc:String,T:String};
 Object.defineProperty(copy,'innerHTML',{set(value){this.html=value;this.value='';}});
 vm.runInNewContext(block,context);return copy;
}
test('missing or mismatched scene IDs leave selection empty without throwing',()=>{
 for(const record of [{paths:['Outfit']},{instanceIds:[42]},{paths:['A','B'],instanceIds:[42]},{paths:['A'],instanceIds:['bad']},{paths:['A'],instanceIds:[0]}]){
  const copy=render(record);assert.equal(copy.value,'');assert.equal((copy.html.match(/<option/g)||[]).length,1);
 }
});
test('exact signed Unity instance IDs are retained and selected without guessing a different copy',()=>{
 assert.equal(render({paths:['Outfit'],instanceIds:[-42]}).value,'-42');
 assert.equal(render({paths:['A','B'],instanceIds:[42,43]},43).value,'43');
 assert.equal(render({paths:['A','B'],instanceIds:[42,43]},99).value,'');
});
