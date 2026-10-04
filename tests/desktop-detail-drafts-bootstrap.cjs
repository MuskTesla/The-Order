const { ipcMain } = require('electron')

// Test-only reply fault and counters for the explicit synthetic profile. Always
// call the original production handler with its real sender/frame and arguments;
// its validation and real persistence run unchanged before a state reply fails.
const control = globalThis.__detailDrafts = { failReads: false, stateReads: 0, gameWrites: 0 }
const handle = ipcMain.handle.bind(ipcMain)
ipcMain.handle = (channel, listener) => {
  if (!['kun-manager:getState', 'kun-manager:updateGame', 'kun-manager:setLaunchArguments'].includes(channel)) return handle(channel, listener)
  return handle(channel, async (event, ...args) => {
    const reading = channel === 'kun-manager:getState'
    if (reading) control.stateReads++
    else control.gameWrites++
    const result = await listener(event, ...args)
    if (reading && control.failReads && result?.ok === true) return { ok: false, error: 'Synthetic state reply failure after real handler validation' }
    return result
  })
}
require('./desktop-settings-matching-bootstrap.cjs')
