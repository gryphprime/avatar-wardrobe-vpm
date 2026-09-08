const assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm');
class Element {constructor(tag){this.tag=tag;this.children=[];this.isConnected=true;this.dataset={};}append(...items){this.children.push(...items);}appendChild(item){this.children.push(item);}replaceChildren(){this.children=[];}setAttribute(){}remove(){this.isConnected=false;}click(){if(this.onclick)return this.onclick();}}
const window={},document={createElement:tag=>new Element(tag)};
vm.runInNewContext(fs.readFileSync('Packages/dev.gryphprime.avatar-wardrobe/Web/preset-appearance.js','utf8'),{window,document,URL,Blob,Date,Number,JSON,Error,encodeURIComponent,setTimeout});
let scope='common',saved=false,calls=[],hold=null;
const api=async(path,options)=>{calls.push({path,options});if(hold)return hold();if(path.includes('_review?'))return {ok:1,token:'review-one',changes:['Restore <literal> coat'],message:'Review exact coat'};if(path.includes('_apply?'))return {ok:1,message:'Restored'};if(path.includes('_save?')){saved=true;return {ok:1};}return {ok:1,hasSaved:saved,revision:'revision-one',savedAt:'2026-09-07T00:00:00Z',garments:1,materials:2,shapes:3,message:'Existing copies required.'};};
const root=new Element('section'),ui=window.WardrobePresetAppearanceUI.create({root,api,scope:()=>scope});
const actions=()=>root.children[2].children,button=name=>actions().find(x=>x.textContent===name);
(async()=>{
 await ui.load();assert.equal(button('Review restore').disabled,true);assert.equal(button('Save current appearance').disabled,false);
 await button('Save current appearance').onclick();assert(calls.some(x=>x.path.includes('_save?')&&x.path.includes('revision=revision-one')&&x.options.method==='POST'));assert(button('Replace saved appearance'));
 await button('Review restore').onclick();assert(!calls.some(x=>x.path.includes('_apply?')),'review never applies');const review=root.children[3];assert.equal(review.children[2].children[0].textContent,'Restore <literal> coat','review text is literal');
 await review.children.find(x=>x.textContent==='Apply reviewed restore').onclick();assert(calls.some(x=>x.path.includes('_apply?')&&x.path.includes('token=review-one')));
 let release;hold=()=>new Promise(resolve=>{release=resolve;});const loading=ui.load();scope='new-preset';release({ok:1,hasSaved:false,revision:'foreign',message:'foreign'});await loading;assert(!root.children[1].textContent.includes('foreign'),'late old-preset response is ignored');
 console.log('saved appearance: explicit save/review/apply, version/token binding, literal changes and preset races passed');
})().catch(error=>{console.error(error);process.exitCode=1;});
