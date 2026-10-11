import assert from 'node:assert/strict'
import { promises as fs } from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { _electron as electron } from '../node_modules/playwright/index.mjs'

const project = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const temporaryRoot = path.resolve(os.tmpdir())
const temporary = await fs.mkdtemp(path.join(temporaryRoot, 'the-order-reload-'))
const profile = path.join(temporary, 'profile'), backup = path.join(temporary, 'fixture.json')
const fromAsar = process.argv.includes('--asar')
const installations = []
for (const id of ['A', 'B']) {
  const folderPath = path.join(temporary, 'Synthetic installation ' + id), executablePath = path.join(folderPath, 'Game.exe')
  await fs.mkdir(folderPath); await fs.writeFile(executablePath, 'Inert fixture; never execute.')
  installations.push({ id, folderPath, executablePath, arguments: ['--' + id] })
}
const game = { id: 'reload-game', name: 'IPC reload fixture', nameIsCustom: false, metadata: null, installations, preferredInstallationId: 'A', status: 'planned', personalRating: null, favorite: false, categories: [], customTags: [], notes: 'Saved fixture note', totalPlaySeconds: 0, playCount: 0, sessions: [], addedAt: '2026-10-03T00:00:00.000Z', lastPlayedAt: null }
await fs.writeFile(backup, JSON.stringify({ format: 'kun-gal-manager', version: 1, exportedAt: '2026-10-03T00:00:00.000Z', games: [game] }))
let application
const errors = []
try {
  application = await electron.launch({ executablePath: path.join(project, 'node_modules/electron/dist/electron.exe'), args: [path.join(project, 'tests/desktop-reload-bootstrap.cjs')], env: { ...process.env, KGM_DATA_DIR: profile, KGM_RELOAD_ASAR: fromAsar ? '1' : '0' }, timeout: 30000 })
  await application.evaluate(({ dialog }, fixture) => {
    dialog.showOpenDialog = async () => ({ canceled: false, filePaths: [fixture] })
    globalThis.fetch = async () => { throw new Error('Network requests forbidden in the reload fixture') }
  }, backup)
  const page = await application.firstWindow()
  page.on('pageerror', error => errors.push(error.message))
  await page.route('https://**', route => route.abort())
  await page.waitForFunction(() => window.kunManager !== undefined)
  await page.evaluate(() => window.kunManager.importLibrary())
  await page.getByRole('button', { name: 'IPC reload fixture', exact: true }).click()
  await page.locator('.detail-tabs').getByRole('button', { name: '管理', exact: true }).click()
  await page.getByText('高级启动参数', { exact: true }).click()
  const argumentsField = page.locator('.advanced-options .field-label input')
  assert.equal(await argumentsField.inputValue(), '["--A"]')
  // Start the old reload through the production onChanged listener and real IPC.
  await application.evaluate(({ BrowserWindow }) => {
    globalThis.__reloadDelay.holdNext = true
    BrowserWindow.getAllWindows()[0].webContents.send('kun-manager:changed')
  })
  for (let i = 0; i < 100; i++) {
    if (await application.evaluate(() => globalThis.__reloadDelay.held)) break
    await new Promise(resolve => setTimeout(resolve, 20))
  }
  assert.equal(await application.evaluate(() => globalThis.__reloadDelay.held), true)
  await page.getByRole('button', { name: '设为默认', exact: true }).click()
  await page.locator('.installation-row').nth(1).getByRole('button', { name: '默认启动', exact: true }).waitFor()
  assert.equal(await argumentsField.inputValue(), '["--B"]')
  await argumentsField.fill('["--NEW-B-DRAFT"]')
  const before = { installation: 'B', draft: await argumentsField.inputValue() }
  await application.evaluate(() => globalThis.__reloadDelay.release())
  await page.waitForTimeout(150)
  const after = { installation: await page.locator('.installation-row').nth(1).getByRole('button', { name: /^(默认启动|设为默认)$/ }).innerText() === '默认启动' ? 'B' : 'A', draft: await argumentsField.inputValue() }
  const trace = await application.evaluate(() => globalThis.__reloadDelay.trace)
  const old = trace.filter(item => item.phase === 'returned' && item.preferred === 'A').at(-1)
  assert.ok(old && trace.some(item => item.phase === 'returned' && item.preferred === 'B' && item.sequence > old.sequence && trace.indexOf(item) < trace.indexOf(old)), 'A newer B snapshot must have returned before the delayed A snapshot')
  const output = { realElectronIpc: true, injectedReplyDelay: true, fromAsar, before, after, delayedASequence: old.sequence, trace, rendererErrors: errors }
  console.log(JSON.stringify(output))
  assert.deepEqual(after, before, 'A delayed old snapshot must neither roll back the displayed installation nor erase its draft')
  await page.getByRole('button', { name: '保存参数', exact: true }).click()
  await page.getByText('启动参数已保存。', { exact: true }).waitFor()
  const saved = (await page.evaluate(() => window.kunManager.getState())).games.find(game => game.id === 'reload-game')
  assert.equal(saved.preferredInstallationId, 'B')
  assert.deepEqual(saved.installations.find(installation => installation.id === 'A').arguments, ['--A'])
  assert.deepEqual(saved.installations.find(installation => installation.id === 'B').arguments, ['--NEW-B-DRAFT'])
  assert.deepEqual(errors, [])
  console.log(JSON.stringify({ realElectronIpc: true, fromAsar, savedToB: true, installationAUnchanged: true }))
} finally {
  if (application) await application.close()
  if (path.dirname(path.resolve(temporary)) !== temporaryRoot || !path.basename(temporary).startsWith('the-order-reload-')) throw new Error('Unexpected temporary directory')
  await fs.rm(temporary, { recursive: true, force: true })
}
