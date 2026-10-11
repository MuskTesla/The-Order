// Public synthetic visual fixture only. Never use this entry with a private profile.
const path = require('node:path')
const fs = require('node:fs/promises')
const { app, session, protocol, ipcMain, dialog } = require('electron')
dialog.showErrorBox = (title, message) => { console.error(title, message); app.exit(1) }
dialog.showMessageBox = async (_window, options) => { console.error('Unexpected fixture dialog:', options); return { response:0 } }
const control = globalThis.__iterationVisual = { mode: 'normal', launches: [], blockedNetwork: 0, coverRequests: [] }
const runtimeRoot = path.resolve(process.env.KGM_VISUAL_RUNTIME_ROOT)
const registerProtocol = protocol.handle.bind(protocol)
protocol.handle = (scheme, handler) => registerProtocol(scheme, async request => {
  const url = new URL(request.url)
  if (scheme === 'kun-manager' && url.pathname.startsWith('/review-fixtures/')) {
    const file = url.pathname.split('/').pop()
    control.coverRequests.push(file)
    if (file === 'broken.svg') return new Response('Synthetic missing cover', { status:404 })
    if (file === 'delayed.svg') await new Promise(resolve => { control.releaseCover = resolve; setTimeout(resolve, 2000) })
    if (!/^(cover-[0-7]|delayed)\.svg$/.test(file)) return new Response('Missing fixture', { status:404 })
    return new Response(await fs.readFile(path.join(__dirname,'ui-fixtures',file)), { headers:{'Content-Type':'image/svg+xml'} })
  }
  return handler(request)
})
const registerIpc = ipcMain.handle.bind(ipcMain)
ipcMain.handle = (channel, handler) => registerIpc(channel, async (...args) => {
  if (channel === 'kun-manager:launchGame') { control.launches.push(args[1]); return { ok:true, value:undefined } }
  const result = await handler(...args)
  if (channel === 'kun-manager:getState' && result.value && control.mode === 'running') {
    const game = result.value.games[0]
    result.value.activeSession = { gameId:game.id, installationId:game.preferredInstallationId, startedAt:'2026-10-03T01:00:00.000Z', elapsedSeconds:3660 }
    result.value.matching = { id:'synthetic-iteration-task', mode:'reidentify', status:'running', total:200, processed:7, matched:5, unmatched:2, failed:0, currentName:result.value.games[1].name, reviews:[] }
  }
  return result
})
app.whenReady().then(() => session.defaultSession.webRequest.onBeforeRequest({ urls:['http://*/*','https://*/*'] }, (details, callback) => {
  if (details.url.startsWith('https://iteration-fixture.invalid/')) { callback({redirectURL:'kun-manager://app/review-fixtures/'+new URL(details.url).pathname.split('/').pop()}); return }
  control.blockedNetwork++
  callback({cancel:true})
}))
require(path.join(runtimeRoot,'dist-electron','main.cjs'))
