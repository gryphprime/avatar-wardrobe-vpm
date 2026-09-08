const fs=require('node:fs'),vm=require('node:vm'),assert=require('node:assert/strict'),path=require('node:path');
const source=fs.readFileSync(path.resolve(__dirname,'../Packages/dev.gryphprime.avatar-wardrobe/Web/wardrobe.js'),'utf8');
const context={T:x=>x};vm.createContext(context);
vm.runInContext(source.slice(source.indexOf('  function groupInstalledItems('),source.indexOf('  function loadInstalled(')),context);
const items=[{guid:'outfit',path:'KE',target:'common'},{guid:'candidate',path:'Gals',target:'old-preset'},{guid:'outfit',path:'Outfits/KE',target:'old-preset'}];
const presets=[{id:'old-preset',name:'KE 1'},{id:'empty',name:'KE 1 (2)'}];
let groups=context.groupInstalledItems(items,presets,false);
assert.equal(groups.size,1);assert.equal(groups.get('common').items.length,3);
assert.equal(groups.get('common').items[1].guid,'candidate');
assert.equal(items[1].target,'old-preset','Display grouping must preserve stored assignment');
groups=context.groupInstalledItems(items,presets,true);
assert.equal(groups.size,3);assert.equal(groups.get('common').items.length,1);assert.equal(groups.get('old-preset').items.length,2);
assert.equal(context.groupInstalledItems([],presets,false).size,1);
console.log('PASS: single-avatar flattening, candidate/duplicate instances retained, original assignments preserved, multi-avatar grouping and empty state.');

// Contextual catalog recovery is executable UI logic, with no network writes.
const elements=new Map(),focused=[],stored=[],selectedFilters=[],views=[];let loads=0;
function element(id){if(!elements.has(id))elements.set(id,{value:'old',focus(){focused.push(id);}});return elements.get(id);}
const empty={dataset:{},innerHTML:'',classList:{toggle(){}},label:{},querySelector(){return this.label;}};
const gridContext={listItems:[],emptyGridState:empty,lastState:{desktop:true,avatarInstanceId:0,outfits:0},connected:true,
 initialGridPending:false,indexing:0,listInflight:false,indexAction:null,gridNotice:'',indexPhase:'discovery',indexDone:0,indexTotal:0,
 search:'',shop:'',category:'',hideEmpty:0,filter:'compatible',langCode:'en',T:x=>x,esc:x=>x,spinner:()=>'<svg>loading</svg>',
 R:{text:(target,value)=>{target.textContent=value;},store:(key,value)=>stored.push([key,value])},$:element,
 load:()=>{loads++;},paintHide(){},selectFilter:value=>selectedFilters.push(value),setBatchView:value=>views.push(value)};
vm.createContext(gridContext);vm.runInContext(source.slice(source.indexOf('  function paintEmptyGrid('),source.indexOf('  var langCode=')),gridContext);
gridContext.paintEmptyGrid();assert.match(empty.innerHTML,/gridChooseAvatar/);assert.equal(empty.label.textContent,'grid.chooseAvatarHint');
gridContext.emptyGridAction('gridChooseAvatar');assert.equal(focused.at(-1),'wardrobeTarget');assert.equal(loads,0);
gridContext.lastState.avatarInstanceId=1;gridContext.paintEmptyGrid();assert.match(empty.innerHTML,/gridAddFiles/);assert.equal(empty.label.textContent,'grid.addFilesHint');
gridContext.emptyGridAction('gridAddFiles');assert.equal(views.at(-1),'library');assert.equal(focused.at(-1),'libraryFiles');
gridContext.lastState.outfits=20;gridContext.search='No such jacket';gridContext.shop='Shop';gridContext.hideEmpty=1;gridContext.paintEmptyGrid();assert.match(empty.innerHTML,/gridClearFilters/);
gridContext.emptyGridAction('gridClearFilters');assert.equal(gridContext.search,'');assert.equal(gridContext.shop,'');assert.equal(gridContext.hideEmpty,0);assert.equal(element('search').value,'');assert.equal(selectedFilters.at(-1),'all');assert.equal(focused.at(-1),'search');
gridContext.connected=false;gridContext.indexing=1;gridContext.paintEmptyGrid();assert.equal(empty.label.textContent,'grid.offlineHint');assert.match(empty.innerHTML,/gridRetry/);assert.match(empty.innerHTML,/gridAddFiles/,'offline retains a local-library action even with stale indexing status');
gridContext.emptyGridAction('gridRetry');assert.equal(loads,1);
gridContext.connected=true;gridContext.indexing=0;gridContext.gridNotice='error';gridContext.paintEmptyGrid();assert.match(empty.innerHTML,/gridRetry/);assert.equal(empty.label.textContent,'grid.error');
gridContext.gridNotice='';gridContext.listInflight=true;gridContext.paintEmptyGrid();assert.match(empty.innerHTML,/loading/);assert.doesNotMatch(empty.innerHTML,/<button/);
const languages=JSON.parse(fs.readFileSync('Packages/dev.gryphprime.avatar-wardrobe/Web/lang.json','utf8'));
for(const language of languages.langs.filter(value=>['en','ja'].includes(value.code))){for(const key of ['grid.chooseAvatar','grid.chooseAvatarHint','grid.addFilesHint','grid.offlineHint','grid.clearFilters','grid.openLibrary'])assert(language.strings.some(value=>value.k===key&&value.v),language.code+' '+key);}
console.log('PASS: contextual empty states choose an avatar, add files, clear filters, retry and open the offline library with EN/JA copy.');
