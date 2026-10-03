import assert from 'node:assert/strict'
import { promises as fs } from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { _electron as electron } from '../node_modules/playwright/index.mjs'

const project = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const version = JSON.parse(await fs.readFile(path.join(project, 'package.json'), 'utf8')).version
const output = path.join(project, '.artifacts', `the-order-${version}-ui-evidence`)
await fs.mkdir(output, {recursive:true})
const temporary = await fs.mkdtemp(path.join(os.tmpdir(), 'kgm-ui-b-'))
const titles = ['湖岸来信', '当星光再次照亮那个没有名字的小镇时我们仍然记得夏日约定与遥远海岸的回声', '星の向こうで君と出会った夏の日々を忘れないための長い長い物語', 'The Last Train Beyond the Horizon and All the Letters We Never Sent', 'TheUnbrokenSyntheticVisualNovelTitleThatHasNoSpacesAndMustNeverWidenTheCardOrPushItsLaunchButtonAway', '雪线之外', '雨中的旧车站', '花园里的第八封信', '暮色下的风筝', '月光与纸船', '虚构愿望清单', '封面损坏测试', '封面加载测试', '暂无封面记录', '远岸的灯塔']
const date = '2026-10-03T00:00:00.000Z'
const games = titles.map((name,i) => ({id:`synthetic-${i}`,name,nameIsCustom:true,metadata:{id:`synthetic-work-${i}`,title:name,originalTitle:name,aliases:[`测试别名${i}`],coverUrl:i===13?null:`https://ui-fixture.invalid/${i===11?'broken.svg':i===12?'delayed.svg':`cover-${i%8}.svg`}`,bannerUrl:null,description:'这是一部虚构的 UI 测试作品。',releaseDate:'2025-01-01',releaseDatePrecision:'day',developers:[i%2?'虚构 · 远岸工房':'虚构 · 夏日制作室'],tags:['虚构测试',i%2?'悬疑':'日常'],ratings:[{source:'test',score:7+i%3,scale:10,votes:null}],characters:[],references:[],webUrl:null,contentRating:'全年龄',fetchedAt:date},installations:i===10?[]:[{id:`install-${i}`,folderPath:path.join(temporary,`game-${i}`),executablePath:path.join(temporary,`game-${i}`,'Game.exe'),arguments:[]}],preferredInstallationId:i===10?null:`install-${i}`,status:['planned','playing','completed','paused','dropped'][i%5],personalRating:i%3?8:null,favorite:i%4===0,categories:[i%2?'长篇':'短篇'],customTags:['虚构测试'],notes:'仅测试数据',totalPlaySeconds:i*3600,playCount:i,sessions:[],addedAt:new Date(Date.parse(date)-i*86400000).toISOString(),lastPlayedAt:i===4?'2026-10-02T12:00:00.000Z':i===8?'2026-09-01T00:00:00.000Z':null}))
let app
const result={fixture:'15 fictional titles, original CC0 vector covers with 5 aspect ratios, inert executable paths; launch IPC spy only',measurements:[],states:[],keyboard:[],errors:[],launches:[],screenshots:[]}
async function contrast(page) {
 const targets=[['card-title','.library-card .card-title'],['developer','.library-card .card-copy p'],['local-marker','.library-card .card-bottom > span'],['launch','.library-card .card-launch'],['status-unselected','.status-tabs button:not(.active)'],['status-count','.status-tabs button:not(.active) small'],['sort','.sort-select'],['filter','.filter-button'],['placeholder','.search-field input','placeholder'],['sidebar-nav','.sidebar .nav-item:not(.active)'],['sidebar-section','.sidebar-section-label']]
 const values=await page.evaluate(targets=>{
  const rgba=s=>{const n=s.match(/[\d.]+/g)?.map(Number)||[];return [n[0]||0,n[1]||0,n[2]||0,n[3]??1]}
  const luminance=rgb=>rgb.slice(0,3).map(x=>{x/=255;return x<=.04045?x/12.92:((x+.055)/1.055)**2.4}).reduce((v,x,i)=>v+x*[.2126,.7152,.0722][i],0)
  return targets.map(([label,selector,pseudo])=>{
   const e=document.querySelector(selector),style=getComputedStyle(e,pseudo?'::placeholder':undefined)
   let background=[255,255,255,1],current=e
   while(current){const bg=rgba(getComputedStyle(current).backgroundColor);if(bg[3]===1){background=bg;break}current=current.parentElement}
   const foreground=rgba(style.color),a=luminance(foreground),b=luminance(background)
   return {label,foreground:style.color,background:background.slice(0,3),ratio:(Math.max(a,b)+.05)/(Math.min(a,b)+.05),fontSize:style.fontSize}
  })
 },targets)
 return values
}
async function start(variant,mode='normal') {
 const profile=path.join(temporary,`${variant}-${mode}`)
 await fs.mkdir(profile,{recursive:true})
 await fs.writeFile(path.join(profile,'library.json'),JSON.stringify({format:'kun-gal-manager',version:1,exportedAt:date,games}))
 app=await electron.launch({executablePath:path.join(project,'node_modules/electron/dist/electron.exe'),args:[path.join(project,'tests/ui-b-bootstrap.cjs')],env:{...process.env,KGM_DATA_DIR:profile,KGM_UI_VARIANT:variant,KGM_UI_MODE:mode,KGM_UI_ASAR:process.argv.includes('--asar')?'1':'0'},timeout:30000})
 const page=await app.firstWindow()
 await page.emulateMedia({reducedMotion:'reduce'})
 page.on('pageerror',e=>result.errors.push({variant,mode,message:e.message}))
 await page.waitForFunction(()=>window.kunManager!==undefined)
 await page.evaluate(() => {
   const label=document.createElement('span');label.className='fixture-label';label.textContent='虚构测试书架 · 原创测试封面';label.style.cssText='font-size:11px;color:#53657a;background:#edf3fa;padding:4px 8px;border-radius:5px';document.querySelector('.library-heading').append(label)
 })
 return page
}
async function size(page,w,h,zoom=1) {
 await app.evaluate(({BrowserWindow},{w,h,zoom})=>{const win=BrowserWindow.getAllWindows()[0];win.setContentSize(w,h);win.webContents.setZoomFactor(zoom)}, {w,h,zoom})
 await page.waitForTimeout(180)
 await page.evaluate(()=>window.scrollTo(0,0))
}
async function shot(page,name) {
 await page.mouse.move(0,0)
 const file=path.join(output,name+'.png')
 const bytes=await app.evaluate(async ({BrowserWindow})=>(await BrowserWindow.getAllWindows()[0].webContents.capturePage()).toPNG().toString('base64'))
 await fs.writeFile(file,Buffer.from(bytes,'base64'));result.screenshots.push({name,file})
}
async function bounds(page,variant,state,w,h,zoom,requireRow=false) {
 const measure=await page.evaluate(()=>{
   const box=e=>{const r=e.getBoundingClientRect();return {x:r.x,y:r.y,width:r.width,height:r.height,bottom:r.bottom,right:r.right}}
   const cards=[...document.querySelectorAll('.library-card')]
   const top=cards[0]?.getBoundingClientRect().top
   const row=cards.filter(e=>Math.abs(e.getBoundingClientRect().top-top)<2)
   return {viewport:{width:innerWidth,height:innerHeight},rootWidth:document.documentElement.scrollWidth,bodyWidth:document.body.scrollWidth,grid:document.querySelector('.library-grid')?box(document.querySelector('.library-grid')):null,columns:row.length,firstRow:row.map(e=>({card:box(e),title:box(e.querySelector('.card-title')),action:box(e.querySelector('.card-bottom')),image:box(e.querySelector('.card-image'))})),controls:[...document.querySelectorAll('.library-controls button,.library-controls input,.library-controls select')].filter(e=>e.getBoundingClientRect().width).map(e=>({label:e.getAttribute('aria-label')||e.textContent.trim(),...box(e)})),images:cards.slice(0,8).map(e=>{const img=e.querySelector('img');return img?{naturalWidth:img.naturalWidth,naturalHeight:img.naturalHeight,fit:getComputedStyle(img).objectFit}:null})}
 })
 assert.ok(measure.rootWidth<=measure.viewport.width+1,`${variant} ${state} ${zoom}: root overflow ${measure.rootWidth}/${measure.viewport.width}`)
 assert.ok(measure.bodyWidth<=measure.viewport.width+1,`${variant} ${state}: body overflow`)
 for(const card of measure.firstRow){assert.ok(card.action.right<=card.card.right+1);assert.ok(card.title.right<=card.card.right+1);assert.ok(card.action.bottom<=card.card.bottom+1)}
 result.measurements.push({variant,state,window:{w,h},zoom,...measure})
 if(requireRow){assert.ok(measure.firstRow.length>=3,`${variant} ${state}: ${measure.columns} columns, grid ${JSON.stringify(measure.grid)}`);for(const c of measure.firstRow)assert.ok(c.card.bottom<=measure.viewport.height,`${variant} ${state} first row bottom ${c.card.bottom}/${measure.viewport.height}`)}
 // Expanded controls and an active matching banner may require vertical scrolling.
 // Keep artwork in view; card targets are separately checked for keyboard reachability.
 if(['expanded-filters','running','running-expanded-filters'].includes(state) && zoom===1){assert.ok(measure.firstRow.length>=3);assert.ok(measure.firstRow[0].image.y<measure.viewport.height-48,`${state}: shelf starts below the screen`)}
 return measure
}
async function refresh(page,mode) { await app.evaluate((_,mode)=>{globalThis.__uiFixture.mode=mode},mode);await app.evaluate(({BrowserWindow})=>BrowserWindow.getAllWindows()[0].webContents.send('kun-manager:changed'));await page.waitForTimeout(400) }
try {
 if(process.argv.includes('--preview')) {
  const requested=process.argv[process.argv.indexOf('--preview')+1] || 'B'
  if(!['A','B'].includes(requested))throw Error('Preview variant must be A or B')
  const page=await start(requested);await size(page,1427,817)
  await app.evaluate(()=>{globalThis.__uiFixture.coverDelay=false;if(globalThis.__uiFixture.releaseCover)globalThis.__uiFixture.releaseCover()})
  console.log(`Isolated synthetic UI ${requested} is open. Launch actions are intercepted; no game runs. Close using the tray Exit command or press Ctrl+C here.`)
  if(process.argv.includes('--smoke'))await app.close()
  else await new Promise(resolve=>{app.once('close',resolve);process.once('SIGINT',resolve)})
  app=null
  result.status='preview-closed'
 } else {
 for(const variant of ['B']) {
  const page=await start(variant)
  await page.locator('.library-card').nth(14).waitFor()
  await size(page,1427,817)
  await page.waitForFunction(()=>[...document.querySelectorAll('.library-card img')].slice(0,8).every(i=>i.naturalWidth>0),undefined,{timeout:10000})
  assert.equal(await page.locator('.fixture-label').innerText(),'虚构测试书架 · 原创测试封面')
  assert.equal(await page.locator('.recent-strip').count(),0)
  const m=await bounds(page,variant,'normal',1427,817,1,true)
  result.contrast=await contrast(page)
  for(const entry of result.contrast)assert.ok(entry.ratio>=4.5,`${entry.label}: text contrast ${entry.ratio.toFixed(2)} is below 4.5`)
  assert.ok(m.images.every(i=>i?.fit==='contain'&&i.naturalWidth>0))
  await shot(page,`The-Order-UI-${variant}-1427x817`)
  await size(page,1280,720);await bounds(page,variant,'normal',1280,720,1,true);await shot(page,`The-Order-UI-${variant}-1280x720`)
  for(const zoom of [1.25,1.5,2]) {
   await size(page,1280,720,zoom);await bounds(page,variant,'normal',1280,720,zoom);await page.getByRole('textbox',{name:'搜索书架',exact:true}).focus()
   const firstLaunch=page.locator('.library-card').first().locator('.card-launch')
   for(let tab=0;tab<32;tab++){if(await firstLaunch.evaluate(e=>e===document.activeElement))break;await page.keyboard.press('Tab')}
   assert.equal(await firstLaunch.evaluate(e=>e===document.activeElement),true,'Tab reaches the first launch button')
   await page.waitForTimeout(80)
   const focus=await firstLaunch.evaluate(e=>{const r=e.getBoundingClientRect();const hit=document.elementFromPoint(r.x+r.width/2,r.y+r.height/2);return {visible:r.top>=0&&r.bottom<=innerHeight&&r.left>=0&&r.right<=innerWidth,unobstructed:hit===e||e.contains(hit),outline:getComputedStyle(e).outlineStyle,rect:{x:r.x,y:r.y,right:r.right,bottom:r.bottom},viewport:{width:innerWidth,height:innerHeight}}})
   assert.equal(focus.visible,true,`${variant} ${zoom}: ${JSON.stringify(focus)}`);assert.equal(focus.unobstructed,true);assert.notEqual(focus.outline,'none')
   result.keyboard.push({variant,zoom,launchFocusVisible:true,tabNavigation:true,...focus})
   if(zoom===1.5)await shot(page,`The-Order-${version}-B-150percent-keyboard-focus`)
   await page.evaluate(()=>window.scrollTo(0,0));if(zoom===1.5)await shot(page,`The-Order-${version}-B-150percent`);if(zoom===2)await shot(page,`The-Order-${version}-B-200percent`)
  }
  await size(page,1280,720)
  await page.getByRole('button',{name:'筛选',exact:true}).click()
  await bounds(page,variant,'expanded-filters',1280,720,1)
  assert.equal(await page.locator('.expanded-filters select').count(),3)
  await shot(page,`The-Order-UI-${variant}-filters`)
  for(const zoom of [1.25,1.5,2]) {await size(page,1280,720,zoom);await bounds(page,variant,'expanded-filters',1280,720,zoom)}
  await size(page,1280,720)
  await page.getByRole('button',{name:'筛选',exact:true}).click()
  const search=page.getByRole('textbox',{name:'搜索书架',exact:true})
  await search.focus();await page.keyboard.press('Tab');assert.equal(await page.getByRole('button',{name:'筛选',exact:true}).evaluate(e=>e===document.activeElement),true)
  await search.fill('测试别名4');await search.press('Enter')
  assert.equal(await page.locator('.library-card').count(),1)
  const title=page.locator('.library-card .card-title')
  await title.focus();await title.press('Enter');await page.locator('.kun-modal-panel').waitFor()
  await page.keyboard.press('Escape');await page.locator('.kun-modal-panel').waitFor({state:'hidden'})
  assert.equal(await title.evaluate(e=>e===document.activeElement),true,'detail returns keyboard focus to title')
  const launch=page.locator('.library-card .card-launch');await launch.focus();await launch.press('Enter')
  await page.waitForTimeout(100)
  let launches=await app.evaluate(()=>globalThis.__uiFixture.launches)
  assert.deepEqual(launches,['synthetic-4'])
  const fav=page.locator('.favorite-button');const before=await fav.getAttribute('aria-pressed');await fav.focus();await fav.press('Space');await page.waitForFunction(before=>document.querySelector('.favorite-button').getAttribute('aria-pressed')!==before,before)
  result.keyboard.push({variant,aliasSearchEnter:true,detailEnterEscapeFocusRestored:true,launchEnterCorrectId:true,favoriteSpace:true})
  await search.fill('没有这样的虚构游戏');assert.equal(await page.locator('.library-card').count(),0);await bounds(page,variant,'no-results',1280,720,1);result.states.push({variant,state:'no-results',clearFiltersVisible:await page.getByRole('button',{name:'清空筛选',exact:true}).isVisible()})
  await page.getByRole('button',{name:'清空筛选',exact:true}).click()
  await page.getByRole('combobox',{name:'游戏排序'}).selectOption('recent');assert.equal(await page.locator('.library-card').first().locator('.card-title').innerText(),titles[4]);await page.locator('.library-card').first().locator('.card-launch').click();result.states.push({variant,state:'continue-path',actions:2,gameId:'synthetic-4'})
  await page.getByRole('combobox',{name:'游戏排序'}).selectOption('added')
  await search.fill('封面损坏测试');await page.locator('.cover-placeholder').waitFor();assert.match(await page.locator('.cover-placeholder').innerText(),/封面暂不可用/);result.states.push({variant,state:'broken-cover',usableTitleAndLaunch:true})
  await search.fill('封面加载测试');await page.locator('.cover-loading-label').waitFor();assert.equal(await page.locator('.card-launch').isVisible(),true);await bounds(page,variant,'cover-loading',1280,720,1);await shot(page,`The-Order-UI-${variant}-cover-loading`)
  await app.evaluate(()=>{globalThis.__uiFixture.coverDelay=false;globalThis.__uiFixture.releaseCover()});await page.waitForFunction(()=>document.querySelector('.library-card img')?.naturalWidth>0);assert.equal(await page.locator('.cover-loading-label').count(),0);result.states.push({variant,state:'cover-loading',titleAndLaunchUsable:true,recovered:true});await search.fill('')
  await page.getByRole('button',{name:'列表视图',exact:true}).click()
  assert.equal(await page.locator('.card-list').count(),15)
  await bounds(page,variant,'list',1280,720,1);await shot(page,`The-Order-${version}-list-light`)
  await page.evaluate(async()=>{const api=window.kunManager;await api.saveSettings({...(await api.getState()).settings,theme:'dark'})})
  await page.waitForFunction(()=>document.documentElement.classList.contains('kun-dark-mode'))
  result.darkContrast=await contrast(page);for(const entry of result.darkContrast)assert.ok(entry.ratio>=4.5,`dark ${entry.label}: ${entry.ratio}`)
  await bounds(page,variant,'list-dark',1280,720,1);await shot(page,`The-Order-${version}-list-dark`)
  await page.locator('.sidebar').getByRole('button',{name:/游玩统计/}).click();await page.locator('.play-statistics').waitFor()
  assert.equal(await page.locator('.day-column').count(),14);await page.locator('.day-column').last().click();assert.equal(await page.locator('.day-column').last().getAttribute('aria-pressed'),'true')
  await bounds(page,variant,'statistics-dark',1280,720,1);await shot(page,`The-Order-${version}-statistics-dark`)
  await page.keyboard.press('Control+k');await page.getByRole('textbox',{name:'搜索书架',exact:true}).waitFor()
  await page.evaluate(async()=>{const api=window.kunManager;await api.saveSettings({...(await api.getState()).settings,theme:'light'})})
  await page.waitForFunction(()=>!document.documentElement.classList.contains('kun-dark-mode'))
  await page.getByRole('button',{name:'封面视图',exact:true}).click()
  await page.getByRole('button',{name:'选择整理',exact:true}).click();await page.getByRole('button',{name:'全选当前结果',exact:true}).click()
  assert.equal(await page.getByRole('checkbox',{checked:true}).count(),15);await bounds(page,variant,'batch-selection',1280,720,1);await shot(page,`The-Order-${version}-batch-selection`)
  await page.keyboard.press('Escape');assert.equal(await page.locator('.selection-toolbar').count(),0)
  await refresh(page,'running');await page.locator('.library-session').waitFor();assert.equal(await page.locator('.recent-strip').count(),0);await bounds(page,variant,'running',1280,720,1);await shot(page,`The-Order-UI-${variant}-running`)
  for(const zoom of [1.25,1.5,2]) {await size(page,1280,720,zoom);await bounds(page,variant,'running',1280,720,zoom)}
  await size(page,1280,720);await page.getByRole('button',{name:'筛选',exact:true}).click();await bounds(page,variant,'running-expanded-filters',1280,720,1)
  for(const zoom of [1.25,1.5,2]) {await size(page,1280,720,zoom);await bounds(page,variant,'running-expanded-filters',1280,720,zoom)}
  await size(page,1280,720);await page.getByRole('button',{name:'筛选',exact:true}).click()
  await size(page,1280,720);await refresh(page,'empty');await page.getByRole('button',{name:'导入本地游戏',exact:true}).waitFor();await bounds(page,variant,'empty',1280,720,1);result.states.push({variant,state:'empty',importAndDiscoverVisible:true});await shot(page,`The-Order-UI-${variant}-empty`)
  launches=await app.evaluate(()=>globalThis.__uiFixture.launches);result.launches.push({variant,ids:launches,realExecutions:0})
  await app.close();app=null
 }
 for(const mode of ['loading','error']) {
  const page=await start('B',mode);await size(page,1280,720)
  await page.getByRole('heading',{name:mode==='loading'?'正在打开你的书架':'书架暂时无法打开',exact:true}).waitFor()
  await bounds(page,'B',mode,1280,720,1);await shot(page,`The-Order-UI-B-${mode}`)
  if(mode==='loading'){await app.evaluate(()=>{globalThis.__uiFixture.mode='normal';globalThis.__uiFixture.releaseLoading()});await page.locator('.library-card').first().waitFor()}
  else {await app.evaluate(()=>globalThis.__uiFixture.mode='normal');await page.getByRole('button',{name:'重新读取',exact:true}).click();await page.locator('.library-card').first().waitFor()}
  result.states.push({variant:'B',state:mode,recovered:true});await app.close();app=null
 }
 assert.equal(result.errors.length,0,JSON.stringify(result.errors))
 result.status='passed'
 console.log(JSON.stringify({status:result.status,measurements:result.measurements.length,screenshots:result.screenshots.length,rendererErrors:result.errors.length,realExecutions:0}))
 }
} catch(error) {result.status='failed';result.failure=error.stack;console.error(error);process.exitCode=1}
finally {
 if(!process.argv.includes('--preview'))await fs.writeFile(path.join(output,process.argv.includes('--final')?'complete-b-asar-final.json':process.argv.includes('--asar')?'complete-b-asar.json':'complete-b.json'),JSON.stringify(result,null,2))
 if(app)await app.close()
 if(path.dirname(temporary)!==os.tmpdir()||!path.basename(temporary).startsWith('kgm-ui-b-'))throw Error('Unexpected temporary profile')
 await fs.rm(temporary,{recursive:true,force:true})
}
