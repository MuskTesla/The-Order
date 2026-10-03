import {promises as fs} from 'node:fs'
import path from 'node:path'
import os from 'node:os'
import {createHash} from 'node:crypto'
import {fileURLToPath} from 'node:url'
import {_electron as electron} from '../node_modules/playwright/index.mjs'
const project=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..')
const version=JSON.parse(await fs.readFile(path.join(project,'package.json'),'utf8')).version
const sourceArgument=process.argv.indexOf('--source-data-dir')
const sourceDirectory=sourceArgument>=0?process.argv[sourceArgument+1]:undefined
if(!process.argv.includes('--allow-private-profile')||!sourceDirectory||!path.isAbsolute(sourceDirectory))throw Error('This optional private-library audit requires --allow-private-profile and --source-data-dir <absolute profile path>.')
const original=path.resolve(sourceDirectory)
const names=['library.json','library.json.bak','settings.json','metadata-matching.json']
const digest=bytes=>createHash('sha256').update(bytes).digest('hex')
const hashes=async()=>Object.fromEntries(await Promise.all(names.map(async name=>[name,digest(await fs.readFile(path.join(original,name)))])))
const before=await hashes()
const temporary=await fs.mkdtemp(path.join(os.tmpdir(),'kgm-private-compat-'))
const profile=path.join(temporary,'profile');await fs.mkdir(profile)
let app,stage='copy';const report={status:'not-completed',originalHashesBefore:before,checks:[],networkIsolation:true,credentialCopied:false,screenshotsCreated:false,privateDataUploaded:false}
const check=(condition,label)=>{report.checks.push({label,passed:!!condition});if(!condition)throw Error(label)}
const projection=g=>({id:g.id,name:g.name,nameIsCustom:g.nameIsCustom,installations:g.installations,preferredInstallationId:g.preferredInstallationId,status:g.status,personalRating:g.personalRating,favorite:g.favorite,categories:g.categories,customTags:g.customTags,notes:g.notes,totalPlaySeconds:g.totalPlaySeconds,playCount:g.playCount,sessions:g.sessions,addedAt:g.addedAt,lastPlayedAt:g.lastPlayedAt,metadata:g.metadata?{id:g.metadata.id,title:g.metadata.title,originalTitle:g.metadata.originalTitle,aliases:g.metadata.aliases}:null})
const canonical=games=>JSON.stringify(games.map(projection))
// Chromium proxy applies before the first renderer request. Credentials are absent,
// so production initialization cannot create an authenticated Node API request.
const launch={executablePath:path.join(project,'release/win-unpacked/The Order.exe'),args:['--proxy-server=127.0.0.1:9','--proxy-bypass-list=<-loopback>'],env:{...process.env,KGM_DATA_DIR:profile},timeout:30000}
async function isolateInstance() {
 await app.firstWindow() // production IPC registration precedes window creation
 return app.evaluate(async({session,ipcMain})=>{
  const proxy=await session.defaultSession.resolveProxy('https://compatibility-isolation.invalid/')
  if(proxy!=='PROXY 127.0.0.1:9')throw Error('Startup proxy did not apply')
  const state=globalThis.__privateCompatibility={blockedRequests:0,forbiddenActions:0}
  globalThis.fetch=async()=>{state.blockedRequests++;throw Error('No network in compatibility instance')}
  session.defaultSession.webRequest.onBeforeRequest({urls:['http://*/*','https://*/*']},(_,callback)=>{state.blockedRequests++;callback({cancel:true})})
  for(const name of ['launchGame','scanFolder','importLocalGames','startMetadataMatching','refreshGame','linkGame','getWork','searchWorks','testConnection','addVirtualGame','pickExecutable','openExternal','openGameFolder','openDataFolder']){const channel='kun-manager:'+name;ipcMain.removeHandler(channel);ipcMain.handle(channel,()=>{state.forbiddenActions++;return {ok:false,error:'Disabled in compatibility instance'}})}
  return proxy
 })
}
try {
 const source=JSON.parse(await fs.readFile(path.join(original,'library.json'),'utf8'))
 for(const name of names.filter(name=>name!=='settings.json'))await fs.copyFile(path.join(original,name),path.join(profile,name))
 const settings=JSON.parse(await fs.readFile(path.join(original,'settings.json'),'utf8'));settings.encryptedKey=null
 await fs.writeFile(path.join(profile,'settings.json'),JSON.stringify(settings))
 const expected=digest(canonical(source.games)),copyBefore=digest(await fs.readFile(path.join(profile,'library.json')))
 stage='first-start';console.log('compatibility-stage: native startup');app=await electron.launch(launch);report.verifiedStartupProxy=await isolateInstance();console.log('compatibility-stage: proxy and instance guards verified');report.nativePackagedExecutable=true;let page=await app.firstWindow();const errors=[];page.on('pageerror',()=>errors.push('renderer-error'))
 await page.locator('.heading-count').waitFor();let snapshot=await page.evaluate(()=>window.kunManager.getState())
 check(snapshot.dataPath===profile,'independent profile used');check(snapshot.settings.hasApiKey===false,'no credential in copied settings')
 check(digest(canonical(snapshot.games))===expected,'all requested game fields equal original projection')
 check(snapshot.games.length===source.games.length,'entry count preserved')
 report.statistics={entries:source.games.length,installations:source.games.reduce((n,g)=>n+g.installations.length,0),associated:source.games.filter(g=>g.metadata).length,categories:new Set(source.games.flatMap(g=>g.categories)).size,customTags:new Set(source.games.flatMap(g=>g.customTags)).size,rated:source.games.filter(g=>g.personalRating!==null).length,recordedSessions:source.games.reduce((n,g)=>n+g.sessions.length,0)}
 for(const [w,h] of [[1280,720],[1427,817]]) {
  await app.evaluate(({BrowserWindow},{w,h})=>BrowserWindow.getAllWindows()[0].setContentSize(w,h),{w,h});await page.waitForTimeout(200)
  const layout=await page.evaluate(()=>({viewportWidth:innerWidth,scrollWidth:document.documentElement.scrollWidth,cards:document.querySelectorAll('.library-card').length,firstBottom:document.querySelector('.library-card')?.getBoundingClientRect().bottom??null}))
  check(layout.scrollWidth<=layout.viewportWidth+1,`no horizontal overflow at ${w}x${h}`);report.checks.push({label:`local render at ${w}x${h}`,passed:true,cardCount:layout.cards,firstCardBottom:layout.firstBottom})
 }
 // Read-only detail UI; no image capture and no launch, discovery or scraping.
 if(source.games.length){const card=page.locator('.library-card .card-title').first();const shownName=await card.innerText();await card.click();await page.locator('.detail-tabs').getByRole('button',{name:'管理',exact:true}).click();const value=await page.locator('.manage-form .field-label').filter({hasText:'书架名称'}).locator('input').inputValue();check(value===shownName,'displayed user name preserved');await page.keyboard.press('Escape')}
 const network=await app.evaluate(()=>globalThis.__privateCompatibility);check(network.forbiddenActions===0,'no API, game launch or scan actions invoked');report.blockedRequestsFirstStart=network.blockedRequests
 await app.close();app=null
 stage='restart';console.log('compatibility-stage: restart');app=await electron.launch(launch);await isolateInstance();page=await app.firstWindow();await page.locator('.heading-count').waitFor();snapshot=await page.evaluate(()=>window.kunManager.getState());check(digest(canonical(snapshot.games))===expected,'restart preserves all requested game fields')
 const restartedNetwork=await app.evaluate(()=>globalThis.__privateCompatibility);check(restartedNetwork.forbiddenActions===0,'restart invokes no forbidden actions');report.blockedRequestsRestart=restartedNetwork.blockedRequests
 await app.close();app=null
 check(digest(await fs.readFile(path.join(profile,'library.json')))===copyBefore,'isolated library bytes unchanged after reads and restart')
 check(errors.length===0,'no renderer errors on first start')
 report.status='passed'
} catch {report.status='failed';report.failureStage=stage;process.exitCode=1}
finally {
 if(app)await app.close()
 report.originalHashesAfter=await hashes();report.originalFourFilesUnchanged=names.every(name=>before[name]===report.originalHashesAfter[name])
 if(!report.originalFourFilesUnchanged){report.status='failed';process.exitCode=1}
 const output=path.join(project,'.artifacts',`the-order-${version}-private-compat.json`);await fs.mkdir(path.dirname(output),{recursive:true});await fs.writeFile(output,JSON.stringify(report,null,2))
 if(path.dirname(temporary)!==os.tmpdir()||!path.basename(temporary).startsWith('kgm-private-compat-'))throw Error('Unexpected Temp target')
 await fs.rm(temporary,{recursive:true,force:true})
 console.log(JSON.stringify(report))
}
