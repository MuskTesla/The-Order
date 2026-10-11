import assert from 'node:assert/strict'
import {promises as fs} from 'node:fs'
import path from 'node:path'
import os from 'node:os'
import {fileURLToPath} from 'node:url'
import {_electron as electron} from '../node_modules/playwright/index.mjs'
const project=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..')
const temporary=await fs.mkdtemp(path.join(os.tmpdir(),'kgm-packaged-b-smoke-'))
let app
try {
 app=await electron.launch({executablePath:path.join(project,'release/win-unpacked/The Order.exe'),env:{...process.env,KGM_DATA_DIR:temporary},timeout:30000})
 const page=await app.firstWindow();const errors=[];page.on('pageerror',e=>errors.push(e.message))
 await page.getByRole('button',{name:'导入本地游戏',exact:true}).waitFor()
 const state=await page.evaluate(()=>window.kunManager.getState())
 assert.equal(state.version,JSON.parse(await fs.readFile(path.join(project,'package.json'),'utf8')).version);assert.equal(state.games.length,0)
 assert.equal(await page.locator('.fixture-label,.recent-strip').count(),0)
 assert.doesNotMatch(await page.locator('body').innerText(),/虚构测试书架|原创测试封面/)
 assert.equal(errors.length,0)
 console.log(JSON.stringify({nativePackagedExe:true,version:state.version,cleanTempProfile:true,noFixtureUi:true,rendererErrors:errors.length}))
}finally {if(app)await app.close();if(path.dirname(temporary)!==os.tmpdir()||!path.basename(temporary).startsWith('kgm-packaged-b-smoke-'))throw Error('Unexpected Temp path');await fs.rm(temporary,{recursive:true,force:true})}
