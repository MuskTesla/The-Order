const path = require('node:path')
const fs = require('node:fs/promises')

// Hold one unrelated production library transaction in the real identity queue.
// The matcher still uses its production apply callback and LibraryStore.
const control = globalThis.__cancelQueue = { holdNext: false, held: false, release: null, responseConsumed: false, requests: 0 }
const rename = fs.rename.bind(fs)
fs.rename = async (source, target) => {
  if (control.holdNext && target === path.join(process.env.KGM_DATA_DIR, 'library.json.bak')) {
    control.holdNext = false; control.held = true
    await new Promise(resolve => { control.release = resolve })
    control.held = false
  }
  return rename(source, target)
}
const project = path.resolve(__dirname, '..')
require(path.join(project, process.env.KGM_CANCEL_ASAR === '1' ? 'release/win-unpacked/resources/app.asar/dist-electron/main.cjs' : 'dist-electron/main.cjs'))
