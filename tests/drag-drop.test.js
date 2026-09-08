const assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm');
const window={};vm.runInNewContext(fs.readFileSync('Packages/dev.gryphprime.avatar-wardrobe/Web/drag-drop.js','utf8'),{window});
const D=window.WardrobeDragDrop;
assert.throws(()=>D.normalize({version:2,familyId:'f',targetKey:'t'}),/Invalid/);
assert.throws(()=>D.normalize({version:1,familyId:'f',targetKey:'t',variantId:'../../etc'}),/Invalid/);
assert.equal(D.normalize({version:1,familyId:'f',targetKey:'t'}).variantId,'','ambiguous families remain unresolved');
assert.equal(D.normalize({version:1,familyId:'f',targetKey:'t',variantId:'a'.repeat(32),ignored:'x'}).ignored,undefined);
console.log('drag/drop payload identity and ambiguity checks passed');
