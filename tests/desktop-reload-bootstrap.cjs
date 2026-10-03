const path = require('node:path')
const { ipcMain } = require('electron')

// Test-only entry point: preserve the real handler and its snapshot, then inject
// reply latency through public ipcMain.handle. No private handler registry is read.
const control = globalThis.__reloadDelay = { holdNext: false, held: false, release: null, served: 0, sequence: 0, trace: [] }
const register = ipcMain.handle.bind(ipcMain)
ipcMain.handle = (channel, handler) => {
  if (channel !== 'kun-manager:getState') return register(channel, handler)
  return register(channel, async (...args) => {
    const sequence = ++control.sequence
    const result = await handler(...args)
    const preferred = result.value?.games.find(game => game.id === 'reload-game')?.preferredInstallationId ?? null
    control.trace.push({ phase: 'captured', sequence, preferred })
    if (control.holdNext) {
      control.holdNext = false; control.held = true; control.heldSequence = sequence
      await new Promise(resolve => { control.release = resolve })
    }
    control.trace.push({ phase: 'returned', sequence, preferred }); control.served++
    return result
  })
}
const project = path.resolve(__dirname, '..')
const main = process.env.KGM_RELOAD_ASAR === '1'
  ? path.join(project, 'release/win-unpacked/resources/app.asar/dist-electron/main.cjs')
  : path.join(project, 'dist-electron/main.cjs')
require(main)
