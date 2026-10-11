const path = require('node:path')
const { BrowserWindow, ipcMain, app, session, protocol } = require('electron')
const fs = require('node:fs/promises')
const control = globalThis.__uiFixture = { mode: process.env.KGM_UI_MODE || 'normal', launches: [], networkBlocked: 0, coverDelay: true }
// Synthetic covers live outside dist and app.asar; this public test wrapper serves them.
const handleProtocol = protocol.handle.bind(protocol)
protocol.handle = (scheme, handler) => handleProtocol(scheme, async request => {
  const url = new URL(request.url)
  if (scheme === 'kun-manager' && url.pathname.startsWith('/ui-fixtures/')) {
    const file = url.pathname.split('/').pop()
    if (!/^(cover-[0-7]|delayed)\.svg$/.test(file)) return new Response('Missing test cover', {status:404})
    return new Response(await fs.readFile(path.join(__dirname,'ui-fixtures',file)), {headers:{'Content-Type':'image/svg+xml'}})
  }
  return handler(request)
})
const register = ipcMain.handle.bind(ipcMain)
ipcMain.handle = (channel, handler) => register(channel, async (...args) => {
  if (channel === 'kun-manager:launchGame') { control.launches.push(args[1]); return { ok:true, value:undefined } }
  if (channel === 'kun-manager:getState') {
    if (control.mode === 'loading') await new Promise(resolve => { control.releaseLoading = resolve })
    if (control.mode === 'error') return { ok:false, error:'虚构测试：书架读取失败' }
    const result = await handler(...args)
    if (result.value) {
      result.value.version = require('../package.json').version
      if (control.mode === 'empty') result.value.games = []
      if (control.mode === 'running') {
        const g = result.value.games[4]
        result.value.activeSession = {gameId:g.id,installationId:g.preferredInstallationId,startedAt:new Date().toISOString(),elapsedSeconds:3660}
        result.value.matching = {id:'synthetic-match',status:'running',total:15,processed:7,matched:5,unmatched:2,failed:0,currentName:result.value.games[1].name,reviews:[]}
      }
    }
    return result
  }
  return handler(...args)
})
app.whenReady().then(() => session.defaultSession.webRequest.onBeforeRequest({urls:['http://*/*','https://*/*']}, (details, callback) => {
  if (details.url.startsWith('https://ui-fixture.invalid/')) {
    const file = new URL(details.url).pathname.split('/').pop()
    const answer = () => callback({redirectURL:'kun-manager://app/ui-fixtures/'+file})
    if (file === 'delayed.svg' && control.coverDelay) { control.releaseCover = answer } else answer()
  } else { control.networkBlocked++; callback({cancel:true}) }
}))
require(process.env.KGM_UI_ASAR === '1' ? path.resolve(__dirname,'../release/win-unpacked/resources/app.asar/dist-electron/main.cjs') : path.resolve(__dirname, '../dist-electron/main.cjs'))
