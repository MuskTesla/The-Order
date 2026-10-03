import assert from 'node:assert/strict'
import {promises as fs} from 'node:fs'
import path from 'node:path'
import os from 'node:os'
import {fileURLToPath} from 'node:url'
import {_electron as electron} from '../node_modules/playwright/index.mjs'
const project=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..')
const temporary=await fs.mkdtemp(path.join(os.tmpdir(),'kgm-binding-pages-'))
const profile=path.join(temporary,'profile');await fs.mkdir(profile)
const date='2026-10-03T00:00:00.000Z'
const games=['A','B'].map(id=>({id,name:`分页测试${id}`,nameIsCustom:true,metadata:null,installations:[],preferredInstallationId:null,status:'planned',personalRating:8,favorite:false,categories:['合成测试'],customTags:['分页'],notes:'Saved synthetic note',totalPlaySeconds:3600,playCount:1,sessions:[],addedAt:date,lastPlayedAt:null}))
await fs.writeFile(path.join(profile,'library.json'),JSON.stringify({format:'kun-gal-manager',version:1,exportedAt:date,games}))
const packaged=process.argv.includes('--packaged');let application
const errors=[],checks=[]
try {
 application=await electron.launch({executablePath:path.join(project,packaged?'release/win-unpacked/The Order.exe':'node_modules/electron/dist/electron.exe'),args:packaged?[]:[project],env:{...process.env,KGM_DATA_DIR:profile},timeout:30000})
 await application.evaluate(({session})=>{
  session.defaultSession.webRequest.onBeforeRequest({urls:['http://*/*','https://*/*']},(_,callback)=>callback({cancel:true}))
  const state=globalThis.__pagination={calls:[],hold:false,held:false,release:null,failOnce:false}
  const work=id=>({object:'work',id,display_name:id==='21'?'正确作品 · 第二页':`合成候选 ${id}`,refs:[],titles:[],tags:[],characters:[],ratings:[],companies:[]})
  const response=(value,status=200)=>new Response(JSON.stringify(value),{status,headers:{'content-type':'application/json'}})
  globalThis.fetch=async value=>{
   const url=new URL(String(value));if(url.origin!=='https://api.nextmoe.dev')throw Error('Unexpected synthetic destination')
   if(url.pathname.endsWith('/catalog/search')) {
    const q=url.searchParams.get('q'),cursor=url.searchParams.get('cursor');state.calls.push({q,cursor})
    if(state.hold){state.hold=false;state.held=true;await new Promise(resolve=>state.release=resolve);state.held=false}
    if(cursor&&state.failOnce){state.failOnce=false;return response({error:{code:'FIXTURE_FAIL',message:'Synthetic failure'}},400)}
    let ids=[],next=null
    if(q==='repeat'){ids=cursor?['1','2']:['1'];next='cur_repeat'}
    else if(q==='empty'){ids=cursor==='cur_empty'?[]:cursor===null?['1']:['21'];next=cursor===null?'cur_empty':cursor==='cur_empty'?'cur_last':null}
    else if(q==='new'){ids=['31']}
    else {ids=cursor?['1','21']:Array.from({length:20},(_,i)=>String(i+1));next=cursor?null:'cur_two'}
    return response({object:'list',items:ids.map(id=>({object:'search_result',target_object:'work',id})),next_cursor:next})
   }
   if(url.pathname.endsWith('/catalog/works'))return response({object:'list',items:url.searchParams.get('ids').split(',').map(work),missing:[]})
   const id=url.pathname.split('/').pop();if(/^[0-9]+$/.test(id))return response(work(id))
   throw Error('Unexpected synthetic path')
  }
 })
 const page=await application.firstWindow();page.on('pageerror',e=>errors.push(e.message))
 await page.waitForFunction(()=>window.kunManager!==undefined)
 await page.evaluate(async()=>{const s=(await window.kunManager.getState()).settings;await window.kunManager.saveSettings({...s,apiKey:'synthetic-pagination-key-only'})})
 const open=async(id,draft)=>{await page.getByRole('button',{name:`分页测试${id}`,exact:true}).click();await page.locator('.detail-tabs').getByRole('button',{name:'管理',exact:true}).click();if(draft){const note=page.locator('.manage-form .field-label').filter({hasText:'游玩笔记'}).locator('textarea');await note.fill(draft);assert.equal(await note.inputValue(),draft)}await page.getByRole('button',{name:'关联作品',exact:true}).click();await page.getByRole('textbox',{name:'关联作品搜索'}).waitFor()}
 const bindPanel=()=>page.locator('.kun-modal-panel').filter({has:page.getByRole('textbox',{name:'关联作品搜索'})})
 const input=()=>page.getByRole('textbox',{name:'关联作品搜索'})
 const search=async q=>{await input().fill(q);await bindPanel().getByRole('button',{name:'搜索',exact:true}).click()}
 const rows=()=>page.locator('.binding-result')
 const more=()=>bindPanel().getByRole('button',{name:'加载更多',exact:true})
 const closeBind=async()=>{await bindPanel().locator('button.absolute[aria-label]').click();await input().waitFor({state:'hidden'})}
 const hold=async()=>application.evaluate(()=>{globalThis.__pagination.hold=true;globalThis.__pagination.held=false})
 const untilHeld=async()=>{for(let i=0;i<100;i++){if(await application.evaluate(()=>globalThis.__pagination.held))return;await page.waitForTimeout(20)}throw Error('Synthetic hold not reached')}
 const release=async()=>application.evaluate(()=>globalThis.__pagination.release())
 await open('A','Unsaved note stays intact')
 await search('main');await rows().nth(19).waitFor()
 await page.locator('.bind-results').evaluate(e=>e.scrollTop=e.scrollHeight)
 await hold();await more().evaluate(e=>{e.click();e.click()});await untilHeld()
 const before=await page.locator('.bind-results').evaluate(e=>e.scrollTop)
 assert.equal(await application.evaluate(()=>globalThis.__pagination.calls.filter(c=>c.q==='main'&&c.cursor).length),1)
 assert.equal(await bindPanel().getByRole('button',{name:'加载中…',exact:true}).isDisabled(),true)
 await release();await rows().nth(20).waitFor()
 assert.equal(await rows().count(),21);assert.ok(Math.abs(await page.locator('.bind-results').evaluate(e=>e.scrollTop)-before)<=1,'Appending preserves scroll position')
 await rows().filter({hasText:'正确作品 · 第二页'}).click();await input().waitFor({state:'hidden'})
 const linked=await page.evaluate(()=>window.kunManager.getState());assert.equal(linked.games.find(g=>g.id==='A').metadata.id,'21');assert.equal(linked.games.find(g=>g.id==='A').name,'分页测试A');assert.equal(linked.games.find(g=>g.id==='B').metadata,null)
 assert.equal(await page.locator('.manage-form .field-label').filter({hasText:'游玩笔记'}).locator('textarea').inputValue(),'Unsaved note stays intact');checks.push('second-page correct association, deduplication, double-click lock, scroll and drafts preserved')
 await page.getByRole('button',{name:'关联作品',exact:true}).click()
 await search('retry');await rows().nth(19).waitFor();await application.evaluate(()=>globalThis.__pagination.failOnce=true);await more().click();await bindPanel().getByRole('button',{name:'重试',exact:true}).waitFor();assert.equal(await rows().count(),20)
 await bindPanel().getByRole('button',{name:'重试',exact:true}).click();await rows().nth(20).waitFor();assert.equal(await application.evaluate(()=>globalThis.__pagination.calls.filter(c=>c.q==='retry'&&c.cursor==='cur_two').length),2);checks.push('failed page retained and same cursor retried')
 await search('repeat');await rows().first().waitFor();await more().click();await rows().nth(1).waitFor();assert.equal(await rows().count(),2);assert.equal(await more().count(),0);checks.push('repeated cursor stopped after deduplication')
 await search('empty');await more().waitFor();await more().click();await bindPanel().getByText('此页没有新增结果，可继续加载。',{exact:true}).waitFor();assert.equal(await rows().count(),1);await more().click();await rows().nth(1).waitFor();checks.push('empty advancing page remains traversable')
 await hold();await search('old-query');await untilHeld();const callsBefore=await application.evaluate(()=>globalThis.__pagination.calls.length);await input().fill('new');assert.equal(await application.evaluate(()=>globalThis.__pagination.calls.length),callsBefore);await bindPanel().getByRole('button',{name:'搜索',exact:true}).click();await rows().filter({hasText:'合成候选 31'}).waitFor();await release();await page.waitForTimeout(80);assert.equal(await rows().count(),1);checks.push('new query invalidates old response without input-triggered API')
 await hold();await search('closed-query');await untilHeld();await closeBind();await page.getByRole('button',{name:'关联作品',exact:true}).click();await search('new');await rows().first().waitFor();await release();await page.waitForTimeout(80);assert.equal(await rows().count(),1);checks.push('close and reopen invalidates old response')
 await hold();await search('target-A');await untilHeld();await closeBind();await page.locator('.kun-modal-panel button.absolute[aria-label]').click();await page.locator('.kun-modal-panel').waitFor({state:'hidden'});await open('B');await search('new');await rows().first().waitFor();await release();await page.waitForTimeout(80);assert.equal(await rows().count(),1);await rows().first().click();await input().waitFor({state:'hidden'});const final=await page.evaluate(()=>window.kunManager.getState());assert.equal(final.games.find(g=>g.id==='A').metadata.id,'21');assert.equal(final.games.find(g=>g.id==='B').metadata.id,'31');checks.push('target switch cannot associate late results to new game')
 assert.equal(errors.length,0,errors.join('\n'))
 console.log(JSON.stringify({packaged,realElectronIpc:true,realNextMoeParsingWithSyntheticTransport:true,checks,rendererErrors:errors.length,realApiCalls:0,realGameExecutions:0}))
}finally {if(application)await application.close();if(path.dirname(temporary)!==os.tmpdir()||!path.basename(temporary).startsWith('kgm-binding-pages-'))throw Error('Unexpected Temp target');await fs.rm(temporary,{recursive:true,force:true})}
