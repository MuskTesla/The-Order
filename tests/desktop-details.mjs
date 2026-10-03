import assert from 'node:assert/strict'
import { promises as fs } from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { _electron as electron } from '../node_modules/playwright/index.mjs'

const project = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const tempRoot = path.resolve(os.tmpdir())
const temporary = await fs.mkdtemp(path.join(tempRoot, 'order-detail-regression-'))
const profile = path.join(temporary, 'profile')
await fs.mkdir(profile)
const date = '2026-10-03T00:00:00.000Z'
const metadata = (id, title) => ({ id, title, originalTitle: title, aliases: [], coverUrl: null, bannerUrl: null, description: 'Synthetic work', releaseDate: null, releaseDatePrecision: null, developers: [], tags: [], ratings: [], characters: [], references: [], webUrl: null, contentRating: null, fetchedAt: date })
const games = ['A', 'B'].map((id, i) => ({ id, name: `Detail game ${id}`, nameIsCustom: true, metadata: metadata(String(101 + i), `Detail work ${id}`), installations: [], preferredInstallationId: null, status: 'planned', personalRating: 8, favorite: false, categories: ['Synthetic'], customTags: [], notes: `Saved note ${id}`, totalPlaySeconds: 0, playCount: 0, sessions: [], addedAt: date, lastPlayedAt: null }))
await fs.writeFile(path.join(profile, 'library.json'), JSON.stringify({ format: 'kun-gal-manager', version: 1, exportedAt: date, games }))
const fromAsar = process.argv.includes('--asar')
const packaged = process.argv.includes('--packaged')
let application
const errors = [], checks = []
try {
  application = await electron.launch({ executablePath: path.join(project, packaged ? 'release/win-unpacked/The Order.exe' : 'node_modules/electron/dist/electron.exe'), args: packaged ? [] : [path.join(project, 'tests/desktop-details-bootstrap.cjs')], env: { ...process.env, KGM_DATA_DIR: profile, KGM_DETAILS_ASAR: fromAsar ? '1' : '0' }, timeout: 30000 })
  await application.evaluate(({ session }) => {
    session.defaultSession.webRequest.onBeforeRequest({ urls: ['http://*/*', 'https://*/*'] }, (_, callback) => callback({ cancel: true }))
    globalThis.__detailTest = { holdId: null, heldId: null, release: null, returned: 0 }
    const raw = id => ({ object: 'work', id, display_name: `Detail work ${id === '101' ? 'A' : id === '102' ? 'B' : 'C'}`, refs: [], titles: [], tags: [], characters: [], ratings: [], companies: [] })
    const response = value => new Response(JSON.stringify(value), { headers: { 'content-type': 'application/json' } })
    globalThis.fetch = async value => {
      const url = new URL(String(value)), state = globalThis.__detailTest
      if (url.origin !== 'https://api.nextmoe.dev') throw Error('Unexpected synthetic destination')
      if (url.pathname.endsWith('/catalog/search')) return response({ object: 'list', items: ['102', '103'].map(id => ({ object: 'search_result', target_object: 'work', id })), next_cursor: null })
      if (url.pathname.endsWith('/catalog/works')) return response({ object: 'list', items: url.searchParams.get('ids').split(',').map(raw), missing: [] })
      const id = url.pathname.split('/').pop()
      if (!['101', '102', '103'].includes(id)) throw Error('Unexpected synthetic path')
      if (state.holdId === id) { state.holdId = null; state.heldId = id; await new Promise(resolve => { state.release = resolve }); state.heldId = null }
      state.returned++
      return response(raw(id))
    }
  })
  const page = await application.firstWindow()
  page.on('pageerror', error => errors.push(error.message))
  await page.waitForFunction(() => window.kunManager !== undefined)
  await page.evaluate(async () => { const settings = (await window.kunManager.getState()).settings; await window.kunManager.saveSettings({ ...settings, apiKey: 'synthetic-detail-test-key', readExecutableMetadata: false }) })
  const note = () => page.locator('.manage-form .field-label').filter({ hasText: '游玩笔记' }).locator('textarea')
  const title = () => page.getByRole('dialog').getByRole('heading').first().innerText()
  const manage = () => page.locator('.detail-tabs').getByRole('button', { name: '管理', exact: true }).click()
  const close = async () => { await page.getByRole('dialog').getByRole('button', { name: '关闭', exact: true }).click(); await page.getByRole('dialog').waitFor({ state: 'hidden' }) }
  const snapshot = () => page.evaluate(() => window.kunManager.getState())
  const poll = async (read, predicate, label) => { for (let i = 0; i < 200; i++) { const value = await read(); if (predicate(value)) return value; await page.waitForTimeout(25) } throw Error(`Timed out: ${label}`) }
  const hold = id => application.evaluate((_, value) => { globalThis.__detailTest.holdId = value }, id)
  const held = id => poll(() => application.evaluate(() => globalThis.__detailTest.heldId), value => value === id, `held ${id}`)
  const release = () => application.evaluate(() => globalThis.__detailTest.release())
  const openLibrary = async id => { await page.locator('nav').getByRole('button', { name: /^我的书架/ }).click(); await page.getByRole('button', { name: `Detail game ${id}`, exact: true }).click(); await manage() }

  await openLibrary('A'); await note().fill('Unsaved A draft'); await close()
  await page.getByRole('button', { name: '发现作品', exact: true }).click()
  await page.getByRole('textbox', { name: '搜索作品名称' }).fill('Detail')
  await page.getByRole('button', { name: '搜索作品', exact: true }).click()
  await page.getByRole('button', { name: 'Detail work B', exact: true }).waitFor()
  await hold('102'); await page.getByRole('button', { name: 'Detail work B', exact: true }).click(); await held('102'); await manage()
  assert.equal(await title(), 'Detail game B'); assert.equal(await note().inputValue(), 'Saved note B')
  await page.getByRole('button', { name: '保存书架信息', exact: true }).click()
  await page.getByText('已保存到你的书架。', { exact: true }).waitFor()
  const saved = await snapshot()
  assert.equal(saved.games.find(game => game.id === 'A').notes, 'Saved note A')
  assert.equal(saved.games.find(game => game.id === 'B').notes, 'Saved note B')
  await note().fill('Fresh B draft while detail is pending'); await release()
  await page.locator('.detail-tabs .spin').waitFor({ state: 'hidden' })
  assert.equal(await note().inputValue(), 'Fresh B draft while detail is pending')
  checks.push('discovery editor is initialized before detail; real Save targets B; full detail preserves B draft')

  await close(); await hold('102'); await page.getByRole('button', { name: 'Detail work B', exact: true }).click(); await held('102'); await close()
  await page.getByRole('button', { name: 'Detail work B', exact: true }).click(); await page.locator('.detail-tabs .spin').waitFor({ state: 'hidden' }); await manage()
  await note().fill('Reopened B draft'); await release(); await page.waitForTimeout(100)
  assert.equal(await note().inputValue(), 'Reopened B draft')
  checks.push('closing and reopening the same work invalidates its older details')

  await close(); await page.getByRole('button', { name: 'Detail work C', exact: true }).click(); await page.locator('.detail-tabs .spin').waitFor({ state: 'hidden' })
  await hold('103'); await page.getByRole('button', { name: '加入想玩清单', exact: true }).click(); await held('103'); await close()
  await openLibrary('B'); await note().fill('Fresh B draft during wishlist request'); await release()
  await page.getByText('已加入想玩清单。', { exact: true }).waitFor()
  assert.equal(await title(), 'Detail game B'); assert.equal(await note().inputValue(), 'Fresh B draft during wishlist request')
  assert.ok((await snapshot()).games.some(game => game.metadata?.id === '103'))
  checks.push('late wishlist addition persists C and preserves the newer B dialog')

  // The bootstrap wraps the public IPC registration, retaining the real remove
  // handler, save, notification and validation. The native executable mode
  // checks all transport races above; --asar additionally checks reply latency.
  if (!packaged) {
    await close(); await openLibrary('A')
    await application.evaluate(() => { globalThis.__detailRemoval.holdNext = true })
    await page.getByRole('button', { name: '从书架移除这部游戏', exact: true }).click()
    await page.getByRole('alertdialog').getByRole('button', { name: /确定|确认|Confirm|OK/ }).click()
    await page.getByRole('alertdialog').waitFor({ state: 'hidden' })
    await poll(() => application.evaluate(() => globalThis.__detailRemoval.held), Boolean, 'held remove reply')
    await close(); await openLibrary('B'); await note().fill('Fresh B draft during removal')
    await application.evaluate(() => globalThis.__detailRemoval.release())
    await page.getByText('已从书架移除。', { exact: true }).waitFor()
    assert.equal(await title(), 'Detail game B'); assert.equal(await note().inputValue(), 'Fresh B draft during removal')
    assert.ok(!(await snapshot()).games.some(game => game.id === 'A'))
    checks.push('late real remove IPC reply leaves newer B detail and draft open')
    await page.getByRole('button', { name: '从书架移除这部游戏', exact: true }).click()
    await page.getByRole('alertdialog').getByRole('button', { name: /确定|确认|Confirm|OK/ }).click()
    await page.getByRole('alertdialog').waitFor({ state: 'hidden' })
    await page.getByRole('dialog').waitFor({ state: 'hidden' })
    assert.ok(!(await snapshot()).games.some(game => game.id === 'B'))
    checks.push('ordinary remove closes its own detail after the library notification')
  }
  assert.deepEqual(errors, [])
  console.log(JSON.stringify({ packaged, fromAsar, realElectronGui: true, realElectronIpc: true, checks, rendererErrors: errors, realApiCalls: 0, realGameExecutions: 0 }))
} finally {
  if (application) await application.close()
  if (path.dirname(path.resolve(temporary)) !== tempRoot || !path.basename(temporary).startsWith('order-detail-regression-')) throw Error('Unexpected temporary cleanup path')
  await fs.rm(temporary, { recursive: true, force: true })
}
