const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs'), path = require('node:path'), vm = require('node:vm');
const source = fs.readFileSync(path.join(__dirname, '../Packages/dev.gryphprime.avatar-wardrobe/Web/wardrobe.js'), 'utf8');
function setup() {
  let scroll = 0;
  const cards = Array.from({length:200}, (_, i) => ({hidden:false,
    _family:{thumb:`cover${i}`, variantGuids:[`cover${i}`, `variant${i}`]},
    getBoundingClientRect:()=>({top:i*100-scroll,bottom:(i+1)*100-scroll,left:0,right:100,width:100,height:100})}));
  const context = {grid:{children:cards}, $:()=>({getBoundingClientRect:()=>({top:0,bottom:200,left:0,right:500})}), currentView:'wardrobe', gridPreloadCount:40};
  vm.createContext(context);
  vm.runInContext(source.slice(source.indexOf('  function gridPreviewDemand(){'), source.indexOf('  var previewDemandTimer=')), context);
  return {cards, context, scroll:(value)=>{scroll=value;}, demand:()=>JSON.parse(JSON.stringify(context.gridPreviewDemand()))};
}
test('visible covers and all their variants precede look-ahead covers without opening details', ()=>{
  const s=setup(), d=s.demand();
  assert.equal(d.visibleGuids,'cover0,cover1,variant0,variant1');
  assert.deepEqual(d.guids.split(',').slice(0,6),['cover0','cover1','variant0','variant1','cover2','cover3']);
  assert.equal(d.guids.split(',').length,44);
});
test('deep and reverse scrolling, hidden cards and leaving the grid update variant demand', ()=>{
  const s=setup(); s.scroll(15000);
  assert.equal(s.demand().visibleGuids,'cover150,cover151,variant150,variant151');
  s.scroll(5000); s.cards[50].hidden=true;
  assert.equal(s.demand().visibleGuids,'cover51,variant51');
  s.context.currentView='settings'; assert.equal(s.demand().guids,'');
});
test('deduplicates, tolerates missing variants and bounds oversized families fairly', ()=>{
  const s=setup(); delete s.cards[0]._family.variantGuids;
  s.cards[1]._family.variantGuids=['cover1','cover0','shared','shared'];
  assert.equal(s.demand().visibleGuids,'cover0,cover1,shared');
  s.cards[0]._family.variantGuids=Array.from({length:300},(_,i)=>`a${i}`);
  s.cards[1]._family.variantGuids=Array.from({length:300},(_,i)=>`b${i}`);
  const ids=s.demand().guids.split(',');
  assert.equal(ids.length,120); assert.deepEqual(ids.slice(0,6),['cover0','cover1','a0','b0','a1','b1']);
});
