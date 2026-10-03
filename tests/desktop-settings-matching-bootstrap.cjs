const path = require('node:path')
const fs = require('node:fs/promises')
// Bounded fault/gate injection; production settings, matcher and IPC remain intact.
const control = globalThis.__settingsMatching = { fail: null, hold: null, held: false, release: null, requests: 0, response: null }
const rename = fs.rename.bind(fs)
fs.rename = async (source, target) => {
  if (path.dirname(target) === process.env.KGM_DATA_DIR && path.basename(target) === control.fail) {
    control.fail = null; throw new Error('Synthetic atomic rename failure')
  }
  if (path.dirname(target) === process.env.KGM_DATA_DIR && path.basename(target) === control.hold) {
    control.hold = null; control.held = true
    await new Promise(resolve => { control.release = resolve })
    control.held = false
  }
  return rename(source, target)
}
require(path.resolve(__dirname, '../dist-electron/main.cjs'))
