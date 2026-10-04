import assert from 'node:assert/strict'
import { promises as fs } from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { createHash } from 'node:crypto'
import { execFileSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'
import { _electron as electron } from '../node_modules/playwright/index.mjs'

const project = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const option = name => { const i=process.argv.indexOf(name); return i<0?undefined:process.argv[i+1] }
const phase = option('--phase') ?? 'after'
const performanceOnly = process.argv.includes('--performance-only')
const skipPerformance = process.argv.includes('--skip-performance')
assert.ok(['before','after'].includes(phase), 'Use --phase before or --phase after')
const runtimeRoot = path.resolve(option('--runtime-root') ?? project)
const sourceCommit = option('--source-commit') ?? execFileSync('git',['rev-parse','HEAD'],{cwd:project,encoding:'utf8'}).trim()
const output = path.join(project,'.artifacts','iteration-01',phase,new Date().toISOString().replace(/[:.]/g,'-'))
const profile = await fs.mkdtemp(path.join(os.tmpdir(),'the-order-iteration-visual-'))
const date='2026-10-03T00:00:00.000Z'
const listCondition={width:1280,height:720,zoom:1,theme:'light'}
const names=['湖岸来信','当星光再次照亮那个没有名字的小镇时我们仍然记得夏日约定与遥远海岸的回声','星の向こうで君と出会った夏の日々を忘れないための長い長い物語','The Last Train Beyond the Horizon and All the Letters We Never Sent','TheUnbrokenSyntheticVisualNovelTitleThatHasNoSpacesAndMustNeverWidenTheCardOrPushItsLaunchButtonAway','未安装的虚构愿望','待设置程序的虚构作品','封面损坏测试','封面加载测试','暂无封面记录']
const games=Array.from({length:200},(_,i)=>({
  id:`iteration-game-${i}`,name:names[i]??`合成书架作品 ${String(i).padStart(3,'0')}`,nameIsCustom:true,
  metadata:{id:`iteration-work-${i}`,title:names[i]??`合成书架作品 ${i}`,originalTitle:names[i]??'',aliases:[`测试别名${i}`],coverUrl:i===9?null:`https://iteration-fixture.invalid/${i===7?'broken':i===8?'delayed':`cover-${i%8}`}.svg`,bannerUrl:null,description:'仅用于界面与滚动比较的虚构测试资料。',releaseDate:'2025-01-01',releaseDatePrecision:'day',developers:[i%2?'虚构 · 远岸工房':'虚构 · 夏日制作室'],tags:['虚构测试'],ratings:[{source:'bangumi',score:7+i%3,scale:10,votes:null}],characters:[],references:[],webUrl:null,contentRating:'全年龄',fetchedAt:date},
  installations:i===5?[]:[{id:`iteration-install-${i}`,folderPath:`C:\\SyntheticReview\\Game-${i}`,executablePath:i===6?null:`C:\\SyntheticReview\\Game-${i}\\Game.exe`,arguments:[]}],
  preferredInstallationId:i===5?null:`iteration-install-${i}`,status:['planned','playing','completed','paused','dropped'][i%5],personalRating:i===0?0:i%3===1?8:null,favorite:i%4===0,categories:[i%2?'长篇':'短篇'],customTags:['虚构测试'],notes:'仅测试数据',totalPlaySeconds:i*3600,playCount:i,sessions:[],addedAt:new Date(Date.parse(date)-i*86400000).toISOString(),lastPlayedAt:i===1?'2026-10-02T12:00:00.000Z':null,
}))
const library={format:'kun-gal-manager',version:1,exportedAt:date,games}
await fs.mkdir(output,{recursive:true})
await fs.writeFile(path.join(profile,'library.json'),JSON.stringify(library))
const hash = bytes => createHash('sha256').update(bytes).digest('hex')
async function runtimeHashes(directory, prefix=directory) {
  const result=[]
  for(const item of (await fs.readdir(directory,{withFileTypes:true})).sort((a,b)=>a.name.localeCompare(b.name))) {
    const file=path.join(directory,item.name)
    if(item.isDirectory())result.push(...await runtimeHashes(file,prefix))
    else result.push({file:path.relative(prefix,file).replaceAll('\\','/'),sha256:hash(await fs.readFile(file))})
  }
  return result
}
const manifest={phase,sourceCommit,runtimeRoot,synthetic:true,fixture:'200 fictional games; original CC0 SVG covers from tests/ui-fixtures; fixed date, inert nonexistent EXE paths; every launch intercepted; HTTP/S blocked apart from fixture.invalid redirected to local files before any network connection',fixtureSha256:hash(JSON.stringify(library)),electronVersion:null,zoomExplanation:'Window sizes are requested Electron content DIP. Actual CSS viewport and DPR are recorded per condition (this host may round content size by 1 DIP). Zoom 1.5 uses webContents.setZoomFactor(1.5); CSS viewport shrinks to content DIP / 1.5. PNG dimensions record physical pixels separately, preserving the host display scale for before/after.',runtimeHashes:{dist:await runtimeHashes(path.join(runtimeRoot,'dist')),electron:await runtimeHashes(path.join(runtimeRoot,'dist-electron'))},screenshots:[],measurements:[],scroll:[],errors:[]}
if(performanceOnly) {
  const previous=JSON.parse(await fs.readFile(path.join(output,'manifest.json'),'utf8'))
  assert.equal(previous.fixtureSha256,manifest.fixtureSha256,'Performance uses the identical saved fixture')
  assert.deepEqual(previous.runtimeHashes.dist,manifest.runtimeHashes.dist,'Performance uses the same frontend build')
  Object.assign(manifest,{screenshots:previous.screenshots,measurements:previous.measurements,contrast:previous.contrast,keyboard:previous.keyboard})
}
const env={...process.env,KGM_DATA_DIR:profile,KGM_VISUAL_RUNTIME_ROOT:runtimeRoot}
delete env.KGM_TEST_EXECUTABLE; delete env.KGM_DEV_SERVER_URL; delete env.VITE_DEV_SERVER_URL
let application
let compactGrid=false
async function resize(page,width,height,zoom=1) {
  await application.evaluate(({BrowserWindow},{width,height,zoom})=>{const win=BrowserWindow.getAllWindows()[0];win.setContentSize(width,height);win.webContents.setZoomFactor(zoom)},{width,height,zoom})
  await page.waitForTimeout(180)
  await page.evaluate(()=>window.scrollTo(0,0))
}
async function measure(page,state,condition) {
  const metric=await page.evaluate(()=>{
    const box=e=>e?.getBoundingClientRect().toJSON()??null
    const cards=[...document.querySelectorAll('.library-card')]
    const firstY=cards[0]?.getBoundingClientRect().y
    return {viewport:{width:innerWidth,height:innerHeight,dpr:devicePixelRatio},documentWidth:document.documentElement.scrollWidth,firstCoverY:cards[0]?.querySelector('.card-image').getBoundingClientRect().y,columns:cards.filter(e=>Math.abs(e.getBoundingClientRect().y-firstY)<2).length,overview:box(document.querySelector('.library-overview')),cards:cards.slice(0,10).map(e=>({name:e.querySelector('.card-title').textContent,card:box(e),image:box(e.querySelector('.card-image')),title:box(e.querySelector('.card-title')),action:box(e.querySelector('.card-bottom')),compact:e.classList.contains('compact-card'),extraControls:e.querySelectorAll('.card-meta,.card-progress,.card-bottom,.favorite-button').length,ratings:e.querySelectorAll('.card-rating-summary,.card-personal-rating,.card-rating').length,rating:[...e.querySelectorAll('.card-rating-summary,.card-personal-rating,.card-rating')].map(n=>n.textContent.trim()),availability:e.querySelector('.card-availability')?.textContent,primary:e.querySelector('.card-launch,.card-configure')?.textContent.trim(),fit:e.querySelector('img')?getComputedStyle(e.querySelector('img')).objectFit:null,preserved:e.querySelector('.card-image').classList.contains('preserve-cover'),visibleTextFonts:[...e.querySelectorAll('span,small,strong,p,button')].filter(n=>n.getBoundingClientRect().height&&!n.querySelector('span,small,strong,p,button')&&n.textContent.trim()).map(n=>({text:n.textContent.trim(),font:parseFloat(getComputedStyle(n).fontSize)}))}))}
  })
  assert.ok(metric.documentWidth<=metric.viewport.width+1,`${state}: horizontal overflow`)
  for(const card of metric.cards){assert.ok(card.title.right<=card.card.right+1);if(card.compact){assert.equal(card.action,null);assert.equal(card.extraControls,0)}else{assert.ok(card.action,'Information cards retain their action row');assert.ok(card.action.right<=card.card.right+1);assert.ok(card.action.bottom<=card.card.bottom+1)}}
  manifest.measurements.push({state,...condition,...metric})
  return metric
}
function checkDetails(metric) {
  assert.ok(metric.cards.every(c=>c.ratings<=1),'At most one rating per information card')
  assert.ok(metric.cards[0].rating[0]?.includes('0.0')&&metric.cards[0].rating[0]?.includes('我的'),'Personal zero rating takes priority')
  assert.ok(metric.cards[2].rating[0]?.includes('Bangumi'),'Unrated personal record labels the source score')
  assert.equal(metric.cards[5].primary,'添加本地游戏')
  assert.equal(metric.cards[6].primary,'设置启动程序')
  assert.ok(metric.cards.every(c=>c.visibleTextFonts.every(f=>f.font>=12)),'Card text is at least 12 CSS px')
  assert.ok(metric.cards.filter(c=>c.fit).every(c=>c.fit===(compactGrid&&!c.preserved?'cover':'contain')),'Cover fit follows the preserved-artwork or crop mode')
}
async function shot(page,state,condition) {
  await page.mouse.move(0,0)
  // capturePage reads the last compositor frame; wait for theme/scroll paints.
  await page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve))))
  const filename=`${state}-${condition.width}x${condition.height}-${condition.theme}-zoom${condition.zoom}.png`,file=path.join(output,filename)
  const bytes=Buffer.from(await application.evaluate(async({BrowserWindow})=>(await BrowserWindow.getAllWindows()[0].webContents.capturePage()).toPNG().toString('base64')),'base64')
  await fs.writeFile(file,bytes)
  manifest.screenshots.push({file:filename,sha256:hash(bytes),pixelWidth:bytes.readUInt32BE(16),pixelHeight:bytes.readUInt32BE(20),state,...condition})
}
async function scrollFrames(page) {
  return page.evaluate(async()=>{window.scrollTo(0,0);const gaps=[],until=performance.now()+1800;let previous=performance.now();await new Promise(resolve=>{function frame(now){gaps.push(now-previous);previous=now;window.scrollBy(0,65);if(now<until)requestAnimationFrame(frame);else resolve()}requestAnimationFrame(frame)});const sorted=[...gaps].sort((a,b)=>a-b);return {frames:gaps.length,maxMs:Math.max(...gaps),over33ms:gaps.filter(n=>n>33).length,p95:sorted[Math.floor(sorted.length*.95)]}})
}
async function contrast(page, condition, extra=false) {
  const targets=extra
    ? [['chip','.active-filter-chips button'],['filter-label','.expanded-filters label'],['filter-select','.expanded-filters select'],['filter-check','.expanded-filters .filter-check']]
    : [['heading','.library-heading h1'],['input','.library-tools .search-field input'],['placeholder','.library-tools .search-field input','::placeholder'],['sort','.sort-select'],['status','.status-tabs button:not(.active)'],['view','.library-smart-views button:not(.active)'],['filter','.library-filter-line .filter-button'],['card-title','.library-card .card-title'],['developer','.library-card .card-copy p'],['availability','.library-card .card-availability'],['rating','.library-card .card-rating-summary'],['launch','.library-card .card-launch'],['configure','.library-card .card-configure'],['sidebar-nav','.sidebar .nav-item:not(.active)'],['sidebar-section','.sidebar-section-label'],['sidebar-source','.sidebar .source-badge'],['sidebar-collection','.sidebar .collection-link'],['continue-kicker','.library-overview .section-kicker'],['continue-title','.library-overview .story-title'],['continue-time','.library-overview .story-time']]
  const entries=await page.evaluate(targets=>{
    // Resolve all browser CSS color syntaxes (including KunUI's OKLCH) to sRGB.
    const canvas=document.createElement('canvas');canvas.width=1;canvas.height=1
    const context=canvas.getContext('2d',{willReadFrequently:true}),cache=new Map()
    const rgba=s=>{if(cache.has(s))return cache.get(s);context.clearRect(0,0,1,1);context.fillStyle=s;context.fillRect(0,0,1,1);const n=[...context.getImageData(0,0,1,1).data];const color=[n[0],n[1],n[2],n[3]/255];cache.set(s,color);return color}
    const blend=(front,back)=>front.slice(0,3).map((v,i)=>v*front[3]+back[i]*(1-front[3])).concat(1)
    const luminance=rgb=>rgb.slice(0,3).map(x=>{x/=255;return x<=.04045?x/12.92:((x+.055)/1.055)**2.4}).reduce((v,x,i)=>v+x*[.2126,.7152,.0722][i],0)
    const ratio=(a,b)=>(Math.max(a,b)+.05)/(Math.min(a,b)+.05)
    return targets.flatMap(([label,selector,pseudo])=>{
      const matches=[...document.querySelectorAll(selector)].filter(e=>e.getBoundingClientRect().width).slice(0,6)
      return matches.map((e,index)=>{
        const style=getComputedStyle(e,pseudo), ancestors=[];let current=e
        while(current){ancestors.unshift(current);current=current.parentElement}
        let palette=[[255,255,255,1]]
        for(const node of ancestors){const computed=getComputedStyle(node),solid=rgba(computed.backgroundColor);palette=palette.map(bg=>blend(solid,bg));const gradient=computed.backgroundImage.includes('gradient')?computed.backgroundImage.match(/rgba?\([^)]+\)/g):null;if(gradient)palette=gradient.flatMap(color=>palette.map(bg=>blend(rgba(color),bg)))}
        const foreground=rgba(style.color),ratios=palette.map(bg=>ratio(luminance(blend(foreground,bg)),luminance(bg)))
        return {label,index,text:e.textContent.trim().slice(0,45),selector,foreground:style.color,backgroundPalette:palette.map(bg=>bg.slice(0,3)),ratio:Math.min(...ratios),fontSize:parseFloat(style.fontSize)}
      })
    })
  },targets)
  manifest.contrast??=[];manifest.contrast.push({condition,extra,entries})
  for(const entry of entries)if(entry.ratio<4.5){manifest.contrastFailures??=[];manifest.contrastFailures.push(`${condition.theme} ${entry.label}: contrast ${entry.ratio.toFixed(2)} <4.5`)}
}
try {
  console.log(`Starting ${phase} synthetic visual profile`)
  application=await electron.launch({executablePath:path.join(project,'node_modules/electron/dist/electron.exe'),args:[path.join(project,'tests/iteration-visual-bootstrap.cjs')],env,timeout:30000})
  console.log('Electron fixture started')
  application.process().stderr.on('data',bytes=>console.log(bytes.toString()))
  manifest.electronVersion=await application.evaluate(()=>process.versions.electron)
  console.log(`Electron ${manifest.electronVersion}; waiting for window`)
  const page=await application.firstWindow()
  page.on('pageerror',error=>manifest.errors.push(error.message))
  await page.emulateMedia({reducedMotion:'reduce'})
  await page.locator('.library-card').nth(199).waitFor({state:'attached'})
  compactGrid=await page.locator('.compact-card').count()>0
  manifest.compactGrid=compactGrid
  await page.evaluate(()=>{const label=document.createElement('span');label.textContent='虚构测试书架 · 原创合成封面';label.className='iteration-fixture-label';label.style.cssText='position:fixed;right:12px;bottom:8px;z-index:9999;padding:4px 8px;border:1px solid #8795a9;background:#f8fafc;color:#384860;font-size:12px;border-radius:5px;pointer-events:none';document.body.append(label)})
  if(!performanceOnly) {
  await resize(page,1440,960)
  await page.locator('.library-card').nth(8).scrollIntoViewIfNeeded()
  // Release after capturing any visible loading state; load timing may differ per runtime.
  await shot(page,'loading-cover',{width:1440,height:960,zoom:1,theme:'light'})
  await application.evaluate(()=>globalThis.__iterationVisual.releaseCover?.())
  for(const {width,height} of [{width:1440,height:960},{width:1280,height:720}])for(const theme of ['light','dark'])for(const zoom of [1,1.5]) {
    await resize(page,width,height,zoom)
    await page.evaluate(theme=>document.documentElement.classList.toggle('kun-dark-mode',theme==='dark'),theme)
    await page.waitForTimeout(100)
    const condition={width,height,zoom,theme}
    const metric=await measure(page,'shelf',condition)
    await shot(page,'shelf',condition)
    if(phase==='after') {
      if(compactGrid){
        assert.ok(metric.cards.every(c=>c.compact&&c.ratings===0&&c.extraControls===0),'The cover grid displays only cover and title')
        assert.ok(metric.cards.every(c=>c.visibleTextFonts.every(f=>f.font>=12)),'Grid titles are at least 12 CSS px')
        assert.ok(metric.cards.filter(c=>c.fit).every(c=>c.fit===(c.preserved?'contain':'cover')),'Grid cover fit honors preserved-artwork mode')
        // Keep the original personal-rating, source-rating and executable
        // assertions in the information list rather than discard them.
        await page.getByRole('button',{name:'列表视图',exact:true}).click()
        const details=await measure(page,'list-details',condition);checkDetails(details)
        if(zoom===1)await contrast(page,condition)
        await page.getByRole('button',{name:'封面视图',exact:true}).click()
      }else checkDetails(metric)
      if(zoom===1&&metric.firstCoverY>200){manifest.layoutFailures??=[];manifest.layoutFailures.push(`${width}×${height} ${theme}: default cover starts at ${metric.firstCoverY}`)}
      if(zoom===1)await contrast(page,condition)
    }
  }
  await resize(page,1280,720);await page.evaluate(()=>document.documentElement.classList.remove('kun-dark-mode'))
  await page.getByRole('button',{name:'列表视图',exact:true}).click()
  await measure(page,'list',listCondition);await shot(page,'list',listCondition)
  await page.getByRole('button',{name:'封面视图',exact:true}).click()
  if(phase==='after') {
    await page.getByRole('button',{name:'筛选',exact:true}).click()
    await page.locator('.expanded-filters select').first().selectOption('短篇')
    for(const theme of ['light','dark']) {
      await page.evaluate(theme=>document.documentElement.classList.toggle('kun-dark-mode',theme==='dark'),theme)
      await contrast(page,{width:1280,height:720,zoom:1,theme},true)
    }
    await page.getByRole('button',{name:'移除分类筛选',exact:true}).click()
    await page.getByRole('button',{name:/^筛选$/,exact:true}).click()
    await page.evaluate(()=>document.documentElement.classList.remove('kun-dark-mode'))
  }
  await page.locator('.library-card').first().locator('.card-image').focus()
  if(compactGrid){await page.getByRole('button',{name:'列表视图',exact:true}).click();await page.locator('.library-card').first().locator('.card-image').focus()}
  const firstLaunch=page.locator('.library-card').first().locator('.card-launch')
  for(let i=0;i<12;i++){if(await firstLaunch.evaluate(e=>e===document.activeElement))break;await page.keyboard.press('Tab')}
  assert.equal(await firstLaunch.evaluate(e=>e===document.activeElement),true,'Tab reaches the first launch')
  manifest.keyboard=await firstLaunch.evaluate(e=>{const r=e.getBoundingClientRect(),hit=document.elementFromPoint(r.x+r.width/2,r.y+r.height/2);return {visible:r.top>=0&&r.bottom<=innerHeight&&r.left>=0&&r.right<=innerWidth,unobstructed:hit===e||e.contains(hit),outline:getComputedStyle(e).outlineStyle}})
  assert.equal(manifest.keyboard.visible,true);assert.equal(manifest.keyboard.unobstructed,true);assert.notEqual(manifest.keyboard.outline,'none')
  manifest.keyboard.layout=compactGrid?'list':'grid'
  if(compactGrid)await page.getByRole('button',{name:'封面视图',exact:true}).click()
  }
  await resize(page,1440,960)
  for(const treatment of phase==='after'?['neutral','soft']:['baseline']) {
    const requestsBefore=await application.evaluate(()=>globalThis.__iterationVisual.coverRequests.length)
    await page.evaluate(treatment=>{document.activeElement?.blur?.();document.documentElement.classList.remove('kun-dark-mode');document.querySelector('.library-workspace').setAttribute('data-cover-treatment',treatment)},treatment)
    if(!skipPerformance) {
      await scrollFrames(page)
      const samples=[];for(let i=0;i<3;i++)samples.push(await scrollFrames(page))
      manifest.scroll.push({treatment,contentSize:{width:1440,height:960},zoom:1,samples})
    }
    if(phase==='after'&&!performanceOnly)for(const theme of ['light','dark']) {
      await page.evaluate(theme=>{document.documentElement.classList.toggle('kun-dark-mode',theme==='dark');window.scrollTo({top:0,left:0,behavior:'instant'})},theme)
      await shot(page,`cover-${treatment}`,{width:1440,height:960,zoom:1,theme})
    }
    const requestsAfter=await application.evaluate(()=>globalThis.__iterationVisual.coverRequests.length)
    manifest.coverTreatmentRequests??=[];manifest.coverTreatmentRequests.push({treatment,requestsBefore,requestsAfter})
    if(treatment==='soft')assert.equal(requestsAfter,requestsBefore,'Soft backing does not add any cover image requests')
  }
  if(!performanceOnly) {
  await page.evaluate(()=>{document.documentElement.classList.remove('kun-dark-mode');document.querySelector('.library-workspace').setAttribute('data-cover-treatment','neutral')})
  await application.evaluate(({BrowserWindow})=>{globalThis.__iterationVisual.mode='running';BrowserWindow.getAllWindows()[0].webContents.send('kun-manager:changed')})
  await page.waitForTimeout(300);await resize(page,1280,720)
  await measure(page,'running',listCondition);await shot(page,'running',listCondition)
  }
  manifest.control=await application.evaluate(()=>globalThis.__iterationVisual)
  assert.equal(manifest.control.launches.length,0,'No game was launched')
  assert.equal(manifest.errors.length,0,'No renderer errors')
  assert.equal(manifest.layoutFailures?.length??0,0,(manifest.layoutFailures??[]).join('; '))
  assert.equal(manifest.contrastFailures?.length??0,0,(manifest.contrastFailures??[]).join('; '))
  manifest.status='passed'
} catch(error) { manifest.status='failed';manifest.failure=error.stack;throw error }
finally {if(application)await application.close();await fs.writeFile(path.join(output,'manifest.json'),JSON.stringify(manifest,null,2));console.log(JSON.stringify({phase,status:manifest.status,output,screenshots:manifest.screenshots.length,scroll:manifest.scroll,errors:manifest.errors}))}
