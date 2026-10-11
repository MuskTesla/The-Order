import assert from 'node:assert/strict'
import { promises as fs } from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { _electron as electron } from '../node_modules/playwright/index.mjs'

const project = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const temporaryRoot = path.resolve(os.tmpdir())
const temporary = await fs.mkdtemp(path.join(temporaryRoot, 'the-order-concurrency-'))
const profile = path.join(temporary, 'profile'), backup = path.join(temporary, 'synthetic-backup.json')
const packaged = process.argv.includes('--packaged')
const executablePath = path.join(project, packaged ? 'release/win-unpacked/The Order.exe' : 'node_modules/electron/dist/electron.exe')
const launchOptions = { executablePath, args: packaged ? [] : [project], env: { ...process.env, KGM_DATA_DIR: profile }, timeout: 30000 }
let application
const failures = [], errors = []
const check = (value, description) => { if (!value) failures.push(description) }
try {
  application = await electron.launch(launchOptions)
  await application.evaluate(({ dialog }, backupPath) => {
    dialog.showSaveDialog = async () => ({ canceled: false, filePath: backupPath })
    dialog.showOpenDialog = async () => ({ canceled: false, filePaths: [backupPath] })
    globalThis.__concurrency = { hold: false, held: false, release: null, description: 'Current response', nextGate: null, gates: {} }
    globalThis.fetch = async value => {
      const url = new URL(String(value)), id = url.pathname.split('/').pop()
      if (url.origin !== 'https://api.nextmoe.dev' || !['101', '102', '103', '104'].includes(id)) throw new Error('Unexpected synthetic request')
      const control = globalThis.__concurrency, description = control.description
      if (control.nextGate) {
        const gate = control.gates[control.nextGate]; control.nextGate = null; gate.held = true
        await new Promise(resolve => { gate.release = resolve })
      }
      if (control.hold) { control.hold = false; control.held = true; await new Promise(resolve => { control.release = resolve }) }
      const work = { object: 'work', id, display_name: id === '104' ? 'Synthetic default' : 'Synthetic source', intros: [{ lang: 'zh-Hans', value: description }], refs: [], titles: [], tags: [], characters: [], ratings: [] }
      return new Response(JSON.stringify(work), { headers: { 'content-type': 'application/json' } })
    }
  }, backup)
  let page = await application.firstWindow()
  page.on('pageerror', error => errors.push(error.message))
  await page.route('https://**', route => route.abort())
  await page.waitForFunction(() => window.kunManager !== undefined)
  const initial = await page.evaluate(async () => {
    const api = window.kunManager
    await api.saveSettings({ ...(await api.getState()).settings, apiKey: 'test-concurrency-fixture-only' })
    const game = await api.addVirtualGame('101'); await api.updateGame(game.id, { name: 'Confirmed personal' })
    const victim = await api.addVirtualGame('103')
    return { id: game.id, victim: victim.id }
  })
  async function hold(method, id, description, target) {
    await application.evaluate((_electron, description) => { Object.assign(globalThis.__concurrency, { hold: true, held: false, description }) }, description)
    await page.evaluate(({ method, id, target }) => { globalThis.__oldOutcome = window.kunManager[method](id, ...(target ? [target] : [])).then(value => ({ ok: true, value }), error => ({ ok: false, error: error.message })) }, { method, id, target })
    for (let i = 0; i < 100; i++) { if (await application.evaluate(() => globalThis.__concurrency.held)) return; await new Promise(resolve => setTimeout(resolve, 20)) }
    throw new Error('Held synthetic request did not start')
  }
  async function release() { await application.evaluate(() => globalThis.__concurrency.release()); return page.evaluate(() => globalThis.__oldOutcome) }
  async function holdNamed(key, method, id, description, target) {
    await application.evaluate((_electron, { key, description }) => {
      const control = globalThis.__concurrency
      control.gates[key] = { held: false, release: null }; control.nextGate = key; control.description = description
    }, { key, description })
    await page.evaluate(({ key, method, id, target }) => {
      globalThis.__namedOutcomes ??= {}
      globalThis.__namedOutcomes[key] = window.kunManager[method](id, ...(target ? [target] : [])).then(value => ({ ok: true, value }), error => ({ ok: false, error: error.message }))
    }, { key, method, id, target })
    for (let i = 0; i < 100; i++) { if (await application.evaluate((_electron, key) => globalThis.__concurrency.gates[key].held, key)) return; await new Promise(resolve => setTimeout(resolve, 20)) }
    throw new Error(`Held synthetic request ${key} did not start`)
  }
  async function releaseNamed(key) {
    await application.evaluate((_electron, key) => globalThis.__concurrency.gates[key].release(), key)
    return page.evaluate(key => globalThis.__namedOutcomes[key], key)
  }
  async function link(target) { return page.evaluate(({ id, target }) => window.kunManager.linkGame(id, target), { id: initial.id, target }) }
  async function game() { return (await page.evaluate(() => window.kunManager.getState())).games.find(g => g.id === initial.id) }

  await hold('refreshGame', initial.id, 'Obsolete ABA refresh')
  await application.evaluate(() => { globalThis.__concurrency.description = 'Newest association' })
  await link('102'); await link('101')
  const aba = await release()
  check(!aba.ok, 'ABA refresh was accepted'); check((await game()).metadata.description === 'Newest association', 'ABA replaced the newest metadata')

  await hold('linkGame', initial.id, 'Obsolete manual association', '103')
  await link('102'); await link('101')
  const manual = await release()
  const stateAfterManual = await page.evaluate(() => window.kunManager.getState())
  check(!manual.ok, 'Obsolete manual association was accepted')
  check(stateAfterManual.games.some(g => g.id === initial.victim), 'Obsolete association deleted a merge victim')
  // Repair only the synthetic fixture after a baseline failure so remaining cases execute.
  if (!stateAfterManual.games.some(g => g.id === initial.victim)) { await link('101'); await page.evaluate(() => window.kunManager.addVirtualGame('103')) }

  await page.evaluate(() => window.kunManager.exportLibrary())
  await hold('refreshGame', initial.id, 'Obsolete pre-delete refresh')
  await page.evaluate(id => window.kunManager.removeGame(id), initial.id)
  await page.evaluate(() => window.kunManager.importLibrary())
  const restored = await release()
  check(!restored.ok, 'Restored same-ID game accepted the previous lifecycle')

  await hold('refreshGame', initial.id, 'Obsolete same-ID refresh')
  await application.evaluate(() => { globalThis.__concurrency.description = 'Latest requested refresh' })
  await page.evaluate(id => window.kunManager.refreshGame(id), initial.id)
  const reordered = await release()
  check(!reordered.ok, 'Same-ID refresh completed out of order')
  check((await game()).metadata.description === 'Latest requested refresh', 'Older same-ID request replaced newer data')

  let mergeTarget = (await page.evaluate(() => window.kunManager.getState())).games.find(g => g.metadata?.id === '103')
  assert.ok(mergeTarget, 'Synthetic merge target exists')
  await holdNamed('older-merge', 'linkGame', initial.id, 'Older A to C response', '103')
  await holdNamed('newer-target-link', 'linkGame', mergeTarget.id, 'Newer C to D choice', '102')
  const blockedMerge = await releaseNamed('older-merge')
  const whileTargetPending = await page.evaluate(() => window.kunManager.getState())
  check(!blockedMerge.ok, 'An older association deleted a target with a newer explicit association pending')
  check(whileTargetPending.games.some(g => g.id === mergeTarget.id && g.metadata?.id === '103'), 'The pending target record was removed before its response')
  const targetAssociation = await releaseNamed('newer-target-link')
  check(targetAssociation.ok, 'The newer target association failed after the older merge response')
  check((await page.evaluate(() => window.kunManager.getState())).games.some(g => g.id === mergeTarget.id && g.metadata?.id === '102'), 'The newer target choice was not saved')
  // Restore only synthetic state so a broken baseline can still run the next case.
  if (!targetAssociation.ok) { await link('101'); mergeTarget = await page.evaluate(() => window.kunManager.addVirtualGame('103')) }
  else await page.evaluate(id => window.kunManager.linkGame(id, '103'), mergeTarget.id)

  await holdNamed('older-after-target-refresh', 'linkGame', initial.id, 'Obsolete merge data', '103')
  await application.evaluate(() => { globalThis.__concurrency.description = 'Newest completed target refresh' })
  await page.evaluate(id => window.kunManager.refreshGame(id), mergeTarget.id)
  const staleTargetMerge = await releaseNamed('older-after-target-refresh')
  const refreshedTarget = (await page.evaluate(() => window.kunManager.getState())).games.find(g => g.id === mergeTarget.id)
  check(!staleTargetMerge.ok, 'An older source response merged away a newly refreshed target')
  check(refreshedTarget?.metadata?.description === 'Newest completed target refresh', 'The newer completed target metadata was overwritten')
  check((await game()).metadata?.id === '101', 'The rejected merge changed the source association')

  const defaultGame = await page.evaluate(() => window.kunManager.addVirtualGame('104'))
  await page.getByRole('button', { name: 'Synthetic default', exact: true }).click()
  await page.locator('.detail-tabs').getByRole('button', { name: '管理', exact: true }).click()
  const field = label => page.locator('.manage-form .field-label').filter({ hasText: label })
  await field('游玩笔记').locator('textarea').fill('Ordinary notes save')
  await page.getByRole('button', { name: '保存书架信息', exact: true }).click()
  await page.getByText('已保存到你的书架。', { exact: true }).waitFor()
  let saved = (await page.evaluate(() => window.kunManager.getState())).games.find(g => g.id === defaultGame.id)
  check(saved.nameIsCustom !== true, 'Ordinary Save locked an untouched name')
  await field('书架名称').locator('input').fill('Temporary edit')
  await field('书架名称').locator('input').fill('Synthetic default')
  await page.getByRole('button', { name: '保存书架信息', exact: true }).click()
  for (let i = 0; i < 100; i++) { saved = (await page.evaluate(() => window.kunManager.getState())).games.find(g => g.id === defaultGame.id); if (saved.nameIsCustom === true) break; await new Promise(resolve => setTimeout(resolve, 20)) }
  check(saved.nameIsCustom === true, 'Editing name back to the same value was not confirmed')
  const expected = await page.evaluate(() => window.kunManager.getState())
  await application.close(); application = await electron.launch(launchOptions)
  page = await application.firstWindow(); await page.waitForFunction(() => window.kunManager !== undefined)
  check(JSON.stringify((await page.evaluate(() => window.kunManager.getState())).games) === JSON.stringify(expected.games), 'Restart changed the saved synthetic state')
  console.log(JSON.stringify({ packaged, abaRejected: !aba.ok, obsoleteManualRejected: !manual.ok, restoredLifecycleRejected: !restored.ok, outOfOrderRefreshRejected: !reordered.ok, pendingMergeTargetProtected: !blockedMerge.ok && targetAssociation.ok, refreshedMergeTargetProtected: !staleTargetMerge.ok, nameEditBackConfirmed: saved.nameIsCustom === true, rendererErrors: errors, failures }))
  assert.deepEqual(errors, []); assert.deepEqual(failures, [])
} finally {
  if (application) await application.close()
  if (path.dirname(path.resolve(temporary)) !== temporaryRoot || !path.basename(temporary).startsWith('the-order-concurrency-')) throw new Error('Unexpected temporary directory')
  await fs.rm(temporary, { recursive: true, force: true })
}
