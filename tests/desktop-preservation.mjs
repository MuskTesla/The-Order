import assert from 'node:assert/strict'
import { promises as fs } from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { _electron as electron } from '../node_modules/playwright/index.mjs'

const project = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const temporaryRoot = path.resolve(os.tmpdir())
const temporary = await fs.mkdtemp(path.join(temporaryRoot, 'the-order-preservation-'))
const profile = path.join(temporary, 'profile')
const folder = path.join(temporary, 'Synthetic games')
for (const name of ['Installation A', 'Installation B']) {
  const directory = path.join(folder, name)
  await fs.mkdir(directory, { recursive: true })
  await fs.writeFile(path.join(directory, 'Game.exe'), 'Inert regression fixture; never execute.')
}
const works = ['101', '102', '103'].map(id => ({ object: 'work', id, display_name: `Synthetic work ${id}`, refs: [], titles: [], tags: [], characters: [], ratings: [] }))
const packaged = process.argv.includes('--packaged')
const executablePath = process.env.KGM_TEST_EXECUTABLE || path.join(project, packaged ? 'release/win-unpacked/The Order.exe' : 'node_modules/electron/dist/electron.exe')
const launchOptions = { executablePath, args: packaged ? [] : [project], env: { ...process.env, KGM_DATA_DIR: profile }, timeout: 30000 }
let application
const failures = []
const errors = []
const check = (condition, message) => { if (!condition) failures.push(message) }
async function waitState(page, predicate) {
  const deadline = Date.now() + 10000
  while (true) {
    const state = await page.evaluate(() => window.kunManager.getState())
    if (predicate(state)) return state
    assert.ok(Date.now() < deadline, 'Synthetic state transition timed out')
    await new Promise(resolve => setTimeout(resolve, 20))
  }
}
async function openManagement(page, name) {
  await page.getByRole('button', { name, exact: true }).click()
  await page.locator('.detail-tabs').getByRole('button', { name: '管理', exact: true }).click()
}
async function closeDetail(page) {
  const close = page.locator('.kun-modal-panel button.absolute[aria-label]')
  assert.equal(await close.count(), 1)
  await close.click(); await page.locator('.kun-modal-panel').waitFor({ state: 'hidden' })
}
try {
  application = await electron.launch(launchOptions)
  await application.evaluate(({ dialog }, input) => {
    dialog.showOpenDialog = async () => ({ canceled: false, filePaths: [input.folder] })
    globalThis.__preservationTransport = { holdNext: false, held: false, release: null }
    globalThis.fetch = async value => {
      const url = new URL(String(value))
      if (url.origin !== 'https://api.nextmoe.dev') throw new Error('Unexpected synthetic destination')
      const work = input.works.find(item => url.pathname === `/v2/catalog/works/${item.id}`)
      if (!work) throw new Error('Unexpected synthetic request')
      const control = globalThis.__preservationTransport
      if (control.holdNext) {
        control.holdNext = false; control.held = true
        await new Promise(resolve => { control.release = resolve })
      }
      return new Response(JSON.stringify(work), { headers: { 'content-type': 'application/json' } })
    }
  }, { folder, works })
  let page = await application.firstWindow()
  page.on('pageerror', e => errors.push(e.message))
  await page.route('https://**', route => route.abort())
  await page.waitForFunction(() => window.kunManager !== undefined)
  const initial = await page.evaluate(async () => {
    const api = window.kunManager
    await api.saveSettings({ ...(await api.getState()).settings, apiKey: 'test-preservation-fixture-only' })
    const candidates = await api.scanFolder()
    await api.importLocalGames(candidates.map(item => item.id), false)
    const games = (await api.getState()).games
    await api.updateGame(games[0].id, { name: 'Confirmed local title' })
    const first = await api.linkGame(games[0].id, '101')
    const merged = await api.linkGame(games[1].id, '101')
    await api.updateGame(merged.id, { name: 'Confirmed edition title', notes: 'Saved note', categories: ['Saved category'], customTags: ['Saved tag'], personalRating: 7 })
    for (let index = 0; index < merged.installations.length; index++) await api.setLaunchArguments(merged.id, merged.installations[index].id, [`--fixture-${index}`])
    await api.addVirtualGame('103')
    return { id: merged.id, firstName: first.name, initialPreferred: merged.preferredInstallationId }
  })
  check(initial.firstName === 'Confirmed local title', 'First association overwrote confirmed local name')
  await openManagement(page, 'Confirmed edition title')
  const field = label => page.locator('.manage-form .field-label').filter({ hasText: label })
  await field('游玩笔记').locator('textarea').fill('Unsaved note')
  await field('分类').locator('input').fill('Draft category')
  await field('个人标签').locator('input').fill('Draft tag')
  await field('我的评分').locator('input').fill('9')
  await page.getByRole('button', { name: '设为默认', exact: true }).click()
  await waitState(page, state => state.games.find(g => g.id === initial.id).preferredInstallationId !== initial.initialPreferred)
  check(await field('游玩笔记').locator('textarea').inputValue() === 'Unsaved note', 'Default installation reset note draft')
  check(await field('分类').locator('input').inputValue() === 'Draft category', 'Default installation reset category draft')
  check(await field('个人标签').locator('input').inputValue() === 'Draft tag', 'Default installation reset tag draft')
  check(await field('我的评分').locator('input').inputValue() === '9', 'Default installation reset rating draft')
  const beforeSave = (await page.evaluate(() => window.kunManager.getState())).games.find(g => g.id === initial.id)
  check(beforeSave.notes === 'Saved note' && beforeSave.personalRating === 7, 'Installation action implicitly saved personal drafts')
  const submittedNote = await field('游玩笔记').locator('textarea').inputValue()
  await page.getByRole('button', { name: '保存书架信息', exact: true }).click()
  await waitState(page, state => state.games.find(g => g.id === initial.id).notes === submittedNote)
  await closeDetail(page)

  // A pending native picker belongs to its original game, even after opening another.
  await application.evaluate(({ dialog }) => {
    globalThis.__pickerControl = { held: false, release: null }
    dialog.showOpenDialog = async (_window, options) => {
      globalThis.__pickerControl.held = true
      await new Promise(resolve => { globalThis.__pickerControl.release = resolve })
      return { canceled: false, filePaths: [options.defaultPath + '/Game.exe'] }
    }
  })
  await openManagement(page, 'Confirmed edition title')
  await field('游玩笔记').locator('textarea').fill('Picker draft')
  await page.getByRole('button', { name: '选择程序', exact: true }).click()
  for (let attempt = 0; attempt < 100; attempt++) {
    if (await application.evaluate(() => globalThis.__pickerControl.held)) break
    await new Promise(resolve => setTimeout(resolve, 20))
  }
  assert.equal(await application.evaluate(() => globalThis.__pickerControl.held), true)
  await closeDetail(page)
  await openManagement(page, 'Synthetic work 103')
  await field('游玩笔记').locator('textarea').fill('Other game draft')
  await field('书架名称').locator('input').fill('Other game name draft')
  await application.evaluate(() => globalThis.__pickerControl.release())
  await page.getByText('已设置游戏启动文件。', { exact: true }).waitFor()
  check(await field('游玩笔记').locator('textarea').inputValue() === 'Other game draft', 'Pending picker overwrote another game note draft')
  check(await field('书架名称').locator('input').inputValue() === 'Other game name draft', 'Pending picker overwrote another game name draft')
  await closeDetail(page)

  await application.evaluate(() => { globalThis.__preservationTransport.holdNext = true })
  await page.evaluate(id => {
    globalThis.__refreshOutcome = window.kunManager.refreshGame(id).then(game => ({ ok: true, game }), error => ({ ok: false, error: error.message }))
  }, initial.id)
  for (let attempt = 0; attempt < 100; attempt++) {
    if (await application.evaluate(() => globalThis.__preservationTransport.held)) break
    await new Promise(resolve => setTimeout(resolve, 20))
  }
  assert.equal(await application.evaluate(() => globalThis.__preservationTransport.held), true)
  await page.evaluate(id => window.kunManager.linkGame(id, '102'), initial.id)
  await application.evaluate(() => globalThis.__preservationTransport.release())
  const refresh = await page.evaluate(() => globalThis.__refreshOutcome)
  const after = (await page.evaluate(() => window.kunManager.getState())).games.find(g => g.id === initial.id)
  check(!refresh.ok && /名称或资料已更新/.test(refresh.error), 'Stale refresh was not rejected')
  check(after.metadata.id === '102', 'Stale refresh overwrote the new manual association')
  check(after.name === 'Confirmed edition title', 'Refresh changed the confirmed name')
  if (!failures.length) {
    check(after.notes === 'Unsaved note' && after.personalRating === 9, 'Explicitly saved personal edits were lost')
    check(after.categories.includes('Draft category') && after.customTags.includes('Draft tag'), 'Saved organization was lost')
    await openManagement(page, 'Confirmed edition title')
    await field('游玩笔记').locator('textarea').fill('Refresh draft')
    await page.getByRole('button', { name: '刷新资料', exact: true }).click()
    await page.getByText('游戏资料已更新。', { exact: true }).waitFor()
    check(await field('游玩笔记').locator('textarea').inputValue() === 'Refresh draft', 'Normal refresh overwrote unsaved note')
    const stillSaved = (await page.evaluate(() => window.kunManager.getState())).games.find(g => g.id === initial.id)
    check(stillSaved.notes === 'Unsaved note', 'Refresh implicitly saved draft')
    await closeDetail(page)
  }
  const expected = await page.evaluate(() => window.kunManager.getState())
  await application.close(); application = await electron.launch(launchOptions)
  page = await application.firstWindow()
  await page.waitForFunction(() => window.kunManager !== undefined)
  const restarted = await page.evaluate(() => window.kunManager.getState())
  check(JSON.stringify(restarted.games) === JSON.stringify(expected.games), 'Restart changed saved synthetic library')
  console.log(JSON.stringify({ packaged, firstAssociationPreserved: initial.firstName === 'Confirmed local title', staleRefreshRejected: !refresh.ok, newManualAssociationPreserved: after.metadata.id === '102', restartPreserved: JSON.stringify(restarted.games) === JSON.stringify(expected.games), rendererErrors: errors, failures }))
  assert.deepEqual(errors, [])
  assert.deepEqual(failures, [], 'User-confirmed data and drafts must survive the complete workflow')
} finally {
  if (application) await application.close()
  if (path.dirname(path.resolve(temporary)) !== temporaryRoot || !path.basename(temporary).startsWith('the-order-preservation-')) throw new Error('Unexpected temporary directory')
  await fs.rm(temporary, { recursive: true, force: true })
}
