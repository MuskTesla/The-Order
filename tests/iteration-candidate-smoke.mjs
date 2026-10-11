import assert from 'node:assert/strict'
import { promises as fs } from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { createHash } from 'node:crypto'
import { fileURLToPath } from 'node:url'
import { _electron as electron } from '../node_modules/playwright/index.mjs'

const project = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const candidate = path.join(project, '.artifacts', 'iteration-01', 'candidate', 'win-unpacked')
const output = path.join(project, '.artifacts', 'iteration-01')
const temporary = await fs.mkdtemp(path.join(os.tmpdir(), 'the-order-candidate-smoke-'))
const fixture = JSON.parse(await fs.readFile(path.join(output, 'trial-profile', 'library.json'), 'utf8'))
await fs.writeFile(path.join(temporary, 'library.json'), JSON.stringify(fixture))
const env = { ...process.env, KGM_DATA_DIR: temporary, KGM_DEV_URL: 'http://127.0.0.1:5173' }
delete env.KGM_TEST_EXECUTABLE
let application
try {
  application = await electron.launch({ executablePath: path.join(candidate, 'The Order.exe'), args: [], env, timeout: 30000 })
  const page = await application.firstWindow()
  const errors = []
  page.on('pageerror', error => errors.push(error.message))
  await page.locator('.library-card').nth(fixture.games.length - 1).waitFor()
  const state = await page.evaluate(() => window.kunManager.getState())
  assert.equal(path.resolve(state.dataPath), path.resolve(temporary))
  assert.deepEqual(state.games, fixture.games)
  assert.equal(state.settings.hasApiKey, false)
  assert.equal(state.activeSession, null)
  assert.match(page.url(), /^kun-manager:\/\/app\/index.html/)
  const runtime = await application.evaluate(({ app, BrowserWindow }) => {
    const win = BrowserWindow.getAllWindows()[0]; win.setContentSize(1440, 960)
    return { packaged: app.isPackaged, version: app.getVersion(), appPath: app.getAppPath() }
  })
  assert.equal(runtime.packaged, true)
  assert.equal(path.resolve(runtime.appPath), path.join(candidate, 'resources', 'app.asar'))
  await page.locator('.maintenance-entry').click()
  await page.getByRole('dialog', { name: '资料整理' }).waitFor()
  assert.match(await page.getByRole('dialog').innerText(), /缺资料|待设启动程序/)
  await page.keyboard.press('Escape')
  await page.getByRole('dialog').waitFor({ state: 'hidden' })
  assert.deepEqual(errors, [])
  const pixels = Buffer.from(await application.evaluate(async ({ BrowserWindow }) => (await BrowserWindow.getAllWindows()[0].webContents.capturePage()).toPNG().toString('base64')), 'base64')
  await fs.writeFile(path.join(output, 'candidate-smoke.png'), pixels)
  const asar = await fs.readFile(path.join(candidate, 'resources', 'app.asar'))
  await fs.writeFile(path.join(output, 'candidate-smoke.json'), JSON.stringify({ status: 'passed', runtime, sourceCommit: process.argv[2] ?? null, asarSha256: createHash('sha256').update(asar).digest('hex'), profile: 'temporary copy of the public fictional trial library; no settings copied', formalProfileAccessed: false, nativeEntry: true, developmentUrlIgnored: true, rendererErrors: errors, screenshot: 'candidate-smoke.png' }, null, 2))
  console.log(JSON.stringify({ status: 'passed', ...runtime, isolated: true, developmentUrlIgnored: true, rendererErrors: 0 }))
} finally {
  if (application) await application.close()
  if (path.dirname(temporary) !== path.resolve(os.tmpdir()) || !path.basename(temporary).startsWith('the-order-candidate-smoke-')) throw Error('Unexpected smoke fixture directory')
  await fs.rm(temporary, { recursive: true, force: true })
}
