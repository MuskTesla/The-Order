import assert from 'node:assert/strict'
import {test} from 'node:test'
import {promises as fs} from 'node:fs'
import vm from 'node:vm'
import {transform} from 'esbuild'
import * as vue from 'vue'
import * as libraryWorkspace from '../src/library-workspace'
import * as importWorkspace from '../src/import-workspace'
import * as libraryInsights from '../src/library-insights'
import * as savedLibraryViews from '../src/saved-library-views'

async function harness() {
 const source=await fs.readFile(new URL('../src/App.vue',import.meta.url),'utf8')
 const script=source.split('<script setup lang="ts">')[1]!.split('</script>')[0]!
 const sandbox:any={...vue,...libraryWorkspace,...importWorkspace,...libraryInsights,...savedLibraryViews,onMounted:()=>{},onUnmounted:()=>{},isPreview:false,useKunMessage:()=>{},useKunAlert:async()=>true,api:{},document:{documentElement:{dataset:{},classList:{toggle(){}}}},matchMedia:()=>({matches:false})}
 for(const line of script.split('\n').filter(line=>/^import \{/.test(line)))for(const name of line.match(/^import \{([^}]+)\}/)![1]!.split(',').map(name=>name.trim()))if(!(name in sandbox))sandbox[name]=name
 const expose='\nglobalThis.audit={snapshot,selectedGameId,bindOpen,bindTargetId,bindQuery,bindResults,bindCursor,bindError,bindNotice,busy,beginBind,searchBinding,bindWork,showGame};'
 const compiled=await transform(script.replace(/^import .*$/gm,'')+expose,{loader:'ts',format:'cjs',target:'es2022'})
 vm.createContext(sandbox);vm.runInContext(compiled.code,sandbox)
 const a=sandbox.audit
 const game=(id:string)=>({id,name:id,metadata:null,status:'planned',personalRating:null,categories:[],customTags:[],notes:'draft-safe',installations:[],preferredInstallationId:null})
 a.snapshot.value={games:[game('A'),game('B')],settings:{baseUrl:'',includeNsfw:false,hasApiKey:false}}
 a.selectedGameId.value='A';await a.beginBind()
 return {a,api:sandbox.api}
}
const work=(id:string)=>({id,title:id})
const deferred=()=>{let resolve!:(v:any)=>void,reject!:(e:any)=>void;const promise=new Promise((yes,no)=>{resolve=yes;reject=no});return {promise,resolve,reject}}

test('second page deduplicates IDs, blocks double submit, preserves first page and cursor on failure, retries the same cursor',async()=>{
 const {a,api}=await harness();const calls:any[]=[];const pending=deferred()
 api.searchWorks=(q:string,c?:string)=>{calls.push([q,c]);return calls.length===1?Promise.resolve({items:[work('1'),work('1')],nextCursor:'cur_two'}):pending.promise}
 await a.searchBinding();assert.equal(a.bindResults.value.length,1)
 const request=a.searchBinding(true);await a.searchBinding(true);assert.equal(calls.length,2)
 pending.reject(Error('fixture failure'));await request
 assert.equal(a.bindResults.value.length,1);assert.equal(a.bindCursor.value,'cur_two');assert.match(a.bindError.value,/失败/)
 api.searchWorks=async(q:string,c:string)=>{calls.push([q,c]);return {items:[work('1'),work('correct-page-two')],nextCursor:null}}
 await a.searchBinding(true);assert.equal(calls[2][1],'cur_two');assert.deepEqual([...a.bindResults.value].map((w:any)=>w.id),['1','correct-page-two']);assert.equal(a.bindCursor.value,null);assert.equal(a.bindError.value,'')
})
test('editing away and back invalidates a delayed first page without triggering an API call',async()=>{
 const {a,api}=await harness();const pending=deferred();let calls=0
 api.searchWorks=()=>{calls++;return pending.promise}
 const request=a.searchBinding();a.bindQuery.value='changed';a.bindQuery.value='A';pending.resolve({items:[work('old')],nextCursor:'cur_old'});await request
 assert.equal(calls,1);assert.equal(a.bindResults.value.length,0);assert.equal(a.bindCursor.value,null);assert.equal(a.busy.has('bind-search'),false)
})
test('close and reopen ignores old page and old failure while a newer request remains busy',async()=>{
 const {a,api}=await harness();const old=deferred(),current=deferred();let calls=0
 api.searchWorks=()=>++calls===1?old.promise:current.promise
 const oldRequest=a.searchBinding();a.bindOpen.value=false;await a.beginBind();const newRequest=a.searchBinding()
 old.reject(Error('obsolete'));await oldRequest;assert.equal(a.bindError.value,'');assert.equal(a.busy.has('bind-search'),true)
 current.resolve({items:[work('new')],nextCursor:null});await newRequest;assert.equal(a.bindResults.value[0].id,'new')
})
test('switching target rejects delayed results and prevents choosing stale work for a different game',async()=>{
 const {a,api}=await harness();const pending=deferred();let linked=0
 api.searchWorks=()=>pending.promise;api.linkGame=()=>{linked++}
 const request=a.searchBinding();a.selectedGameId.value='B';assert.equal(a.bindOpen.value,false);await a.beginBind()
 pending.resolve({items:[work('old-target')],nextCursor:'cur_old'});await request;await a.bindWork(work('old-target'))
 assert.equal(a.bindTargetId.value,'B');assert.equal(a.bindResults.value.length,0);assert.equal(linked,0)
})
test('an empty advancing page remains traversable but repeated cursor cycles stop without duplicate entries',async()=>{
 const {a,api}=await harness();const pages=[{items:[work('1')],nextCursor:'cur_one'},{items:[],nextCursor:'cur_two'},{items:[work('1'),work('2')],nextCursor:'cur_one'}];const cursors:any[]=[]
 api.searchWorks=async(_q:string,c:string)=>{cursors.push(c);return pages.shift()}
 await a.searchBinding();await a.searchBinding(true);assert.equal(a.bindCursor.value,'cur_two');assert.match(a.bindNotice.value,/没有新增/)
 await a.searchBinding(true);assert.equal(a.bindResults.value.length,2);assert.equal(a.bindCursor.value,null);assert.match(a.bindNotice.value,/重新搜索/)
 await a.searchBinding(true);assert.equal(cursors.length,3)
})
test('changing query during load-more starts from page one and ignores a late successful page',async()=>{
 const {a,api}=await harness();const more=deferred();const calls:any[]=[]
 api.searchWorks=(q:string,c:string)=>{calls.push([q,c]);return c?more.promise:Promise.resolve({items:[work(q)],nextCursor:'cur_two'})}
 await a.searchBinding();const old=a.searchBinding(true);a.bindQuery.value='new';await a.searchBinding()
 more.resolve({items:[work('old-more')],nextCursor:null});await old
 assert.equal(calls[2][1],undefined);assert.deepEqual([...a.bindResults.value].map((w:any)=>w.id),['new']);assert.equal(a.bindCursor.value,'cur_two')
})
test('a completed association for the old target cannot close a new target dialog',async()=>{
 const {a,api}=await harness();api.searchWorks=async()=>({items:[work('w')],nextCursor:null});await a.searchBinding()
 const pending=deferred();api.linkGame=()=>pending.promise;api.getState=async()=>a.snapshot.value
 const linking=a.bindWork(work('w'));a.bindOpen.value=false;a.selectedGameId.value='B';await a.beginBind()
 pending.resolve({...a.snapshot.value.games[0],metadata:work('w')});await linking
 assert.equal(a.bindOpen.value,true);assert.equal(a.bindTargetId.value,'B')
})
