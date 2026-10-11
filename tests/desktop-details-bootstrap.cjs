const path = require('node:path')
const { ipcMain } = require('electron')

// Keep the production handler and IPC validation, delaying only one test reply.
const control = globalThis.__detailRemoval = { holdNext: false, held: false, release: null, returned: 0 }
const register = ipcMain.handle.bind(ipcMain)
ipcMain.handle = (channel, handler) => {
  if (channel !== 'kun-manager:removeGame') return register(channel, handler)
  return register(channel, async (...args) => {
    const result = await handler(...args)
    if (control.holdNext) {
      control.holdNext = false; control.held = true
      await new Promise(resolve => { control.release = resolve })
      control.held = false
    }
    control.returned++
    return result
  })
}
const project = path.resolve(__dirname, '..')
require(path.join(project, process.env.KGM_DETAILS_ASAR === '1' ? 'release/win-unpacked/resources/app.asar/dist-electron/main.cjs' : 'dist-electron/main.cjs'))
