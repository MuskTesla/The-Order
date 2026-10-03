import assert from 'node:assert/strict'
import { promises as fs } from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { createHash } from 'node:crypto'
import { execFileSync } from 'node:child_process'
import { _electron as electron } from '../node_modules/playwright/index.mjs'

const project = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const baseline = process.argv.includes('--baseline'), phase = baseline ? 'before' : 'after'
const output = path.join(project, '.artifacts/overnight-2026-10-04/round-1', phase)
const temporaryRoot = path.resolve(os.tmpdir()), temporary = await fs.mkdtemp(path.join(temporaryRoot, 'the-order-saved-views-'))
const profileA = path.join(temporary, 'profile-a'), profileB = path.join(temporary, 'profile-b')
const savedKey = 'the-order:saved-library-views:v1', date = '2026-10-04T00:00:00.000Z'
const names = ['海岸来信', '雨中车站', '海岸回声', '旧收藏', '缺资料小镇', '合成长标题 · ' + '那些没有忘记的夏日约定与海边的小镇'.repeat(3)]
const games = names.map((name, i) => ({ id: `saved-view-${i}`, name, nameIsCustom: true, metadata: i === 4 ? null : {
  id: `saved-work-${i}`, title: name, originalTitle: name, aliases: [], coverUrl: `https://saved-views.invalid/cover-${i}.svg`, bannerUrl: null, description: '原创素材与隔离合成条目。', releaseDate: null, releaseDatePrecision: null, developers: ['合成制作室'], tags: [], ratings: [], characters: [], references: [], webUrl: null, contentRating: null, fetchedAt: date,
}, installations: [1, 3, 4].includes(i) ? [{ id: `saved-install-${i}`, folderPath: path.join(temporary, `game-${i}`), executablePath: null, arguments: [] }] : [], preferredInstallationId: [1, 3, 4].includes(i) ? `saved-install-${i}` : null,
status: i === 1 ? 'completed' : 'planned', favorite: i < 3, personalRating: i === 0 ? 0 : i === 1 ? 9 : i === 2 ? 8 : null,
categories: i < 3 ? ['周末'] : i === 3 ? ['旧分类'] : [], customTags: [0, 2].includes(i) ? ['短篇'] : [], notes: `合成笔记${i}`, sessions: [], totalPlaySeconds: 0, playCount: 0, addedAt: date, lastPlayedAt: null }))
const library = { format: 'kun-gal-manager', version: 1, exportedAt: date, games }
const conditions = [{ width: 1440, height: 940, zoom: 1 }, { width: 1000, height: 700, zoom: 1.5 }]
const longName = '周末的海岸来信与那些夏日约定'.repeat(3)
const sha = value => createHash('sha256').update(value).digest('hex')
const report = { phase, checkoutHead: execFileSync('git', ['rev-parse', 'HEAD'], { cwd: project, encoding: 'utf8' }).trim(), sourceDirty: !!execFileSync('git', ['status', '--porcelain'], { cwd: project, encoding: 'utf8' }).trim(), fixtureSha256: sha(JSON.stringify(library)), sourceHashes: {}, runtimeHashes: {}, screenshots: [], measurements: [], checks: [], baselineFailures: [], rendererErrors: [], syntheticOnly: true, formalProfileAccessed: false, realApiCalls: 0, realGameExecutions: 0 }
let application, page
const search = () => page.getByRole('textbox', { name: '搜索书架', exact: true })
const state = () => page.evaluate(() => window.kunManager.getState())
const stored = () => page.evaluate(key => JSON.parse(localStorage.getItem(key) ?? 'null'), savedKey)
async function start(profile) {
  const env = { ...process.env, KGM_DATA_DIR: profile }; delete env.KGM_TEST_EXECUTABLE; delete env.KGM_DEV_URL
  application = await electron.launch({ executablePath: path.join(project, 'node_modules/electron/dist/electron.exe'), args: [project], env, timeout: 30000 })
  await application.evaluate(({ session }, root) => {
    const fs = process.getBuiltinModule('node:fs'), path = process.getBuiltinModule('node:path')
    session.defaultSession.protocol.handle('https', request => { const url = new URL(request.url); if (url.hostname !== 'saved-views.invalid' || !/^\/cover-[0-7]\.svg$/.test(url.pathname)) return new Response('Unavailable fixture', { status: 404 }); return new Response(fs.readFileSync(path.join(root, 'tests/ui-fixtures', path.basename(url.pathname))), { headers: { 'content-type': 'image/svg+xml' } }) })
    globalThis.fetch = () => { throw Error('Unexpected real catalogue request') }
  }, project)
  page = await application.firstWindow(); page.on('pageerror', error => report.rendererErrors.push(error.message))
  await page.reload(); await page.emulateMedia({ reducedMotion: 'reduce' }); await page.locator('.library-card').first().waitFor()
  assert.equal(path.resolve((await state()).dataPath), profile)
}
async function navigate(index) { await page.locator('.sidebar nav>.nav-item').nth(index).click() }
async function filtersOpen() { if (!(await page.locator('.expanded-filters').count())) await page.locator('.library-filter-line>.filter-button').click() }
async function combination() {
  await search().fill('海岸'); await page.getByRole('group', { name: '书架快捷视图' }).getByRole('button', { name: '愿望单', exact: true }).click()
  await filtersOpen(); await page.locator('.expanded-filters select').nth(0).selectOption('周末'); await page.locator('.expanded-filters select').nth(1).selectOption('短篇'); await page.locator('.expanded-filters select').nth(2).selectOption('0'); await page.getByRole('checkbox', { name: '只看收藏', exact: true }).check(); await page.getByRole('combobox', { name: '游戏排序', exact: true }).selectOption('name')
  assert.equal(await page.locator('.library-card').count(), 2)
}
async function createView(name) {
  await filtersOpen(); await page.locator('.expanded-filters').getByRole('button', { name: '保存当前筛选', exact: true }).click()
  const input = page.getByRole('textbox', { name: '常用筛选名称', exact: true }); await input.waitFor(); await input.fill(name); await input.press('Enter'); await page.locator('.saved-view-editor').waitFor({ state: 'hidden' })
}
async function openView(name, count) { const button = page.getByRole('button', { name: `打开常用筛选：${name}，${count}部作品`, exact: true }); await button.focus(); await button.press('Enter') }
async function resize(value) {
  await application.evaluate(({ BrowserWindow }, c) => { const windows = BrowserWindow.getAllWindows(); assertSingle(windows); function assertSingle(w) { if (w.length !== 1) throw Error('Expected one window') }; const win = windows[0]; if (win.isMaximized()) win.unmaximize(); win.setContentSize(c.width, c.height); win.webContents.setZoomFactor(c.zoom) }, value)
  await page.waitForFunction(c => Math.abs(innerWidth - c.width / c.zoom) < 2 && Math.abs(innerHeight - c.height / c.zoom) < 2, value)
  await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))))
}
async function capture(label, condition, target = null) {
  await resize(condition)
  if (target) await page.locator(target).first().scrollIntoViewIfNeeded(); else await page.evaluate(() => window.scrollTo(0, 0))
  const measured = await page.evaluate(() => ({ width: innerWidth, height: innerHeight, dpr: devicePixelRatio, scrollWidth: document.documentElement.scrollWidth, clientWidth: document.documentElement.clientWidth, heading: document.querySelector('.library-heading h1')?.textContent?.trim() ?? document.querySelector('main h1')?.textContent, url: location.href }))
  report.measurements.push({ label, requested: condition, actual: measured })
  if (measured.scrollWidth > measured.width + 1) { if (baseline) report.baselineFailures.push({ label, ...measured }); else assert.fail(label + ': horizontal overflow') }
  assert.match(measured.heading, /我的书架|分类收藏/)
  await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))))
  const bytes = Buffer.from(await application.evaluate(async ({ BrowserWindow }) => { const windows = BrowserWindow.getAllWindows(); if (windows.length !== 1) throw Error('Expected one captured window'); return (await windows[0].webContents.capturePage()).toPNG().toString('base64') }), 'base64')
  assert.ok(Math.abs(bytes.readUInt32BE(16) - condition.width * measured.dpr / condition.zoom) < 4)
  assert.ok(Math.abs(bytes.readUInt32BE(20) - condition.height * measured.dpr / condition.zoom) < 4)
  const file = `${label}-${condition.width}x${condition.height}-zoom${condition.zoom}.png`
  await fs.writeFile(path.join(output, file), bytes)
  report.screenshots.push({ file, requested: condition, actual: measured, target, pixelWidth: bytes.readUInt32BE(16), pixelHeight: bytes.readUInt32BE(20), sha256: sha(bytes) })
}
try {
  await fs.mkdir(output, { recursive: true })
  for (const profile of [profileA, profileB]) { await fs.mkdir(profile, { recursive: true }); await fs.writeFile(path.join(profile, 'library.json'), JSON.stringify(library)) }
  for (const game of games) for (const location of game.installations) await fs.mkdir(location.folderPath, { recursive: true })
  const html = await fs.readFile(path.join(project, 'dist/index.html'), 'utf8')
  for (const file of ['dist/index.html', 'dist-electron/main.cjs', 'dist-electron/preload.cjs', ...[...html.matchAll(/(?:src|href)="([^"]+\.(?:js|css))"/g)].map(match => path.posix.normalize('dist/' + match[1].replace(/^\//, '')))]) report.runtimeHashes[file] = sha(await fs.readFile(path.join(project, file)))
  if (baseline) {
    const prior = JSON.parse(await fs.readFile(path.join(project, '.artifacts/overnight-2026-10-04/round-0/after/manifest.json'), 'utf8'))
    assert.deepEqual(report.runtimeHashes, prior.runtimeHashes, 'Before comparison must run the verified round-zero build')
    report.businessSourceCommit = 'ca6c1b3b83e3ba06d39decb2171ee0f466785552'
  } else for (const file of ['src/App.vue', 'src/saved-library-views.ts', 'src/components/LibrarySavedViews.vue']) report.sourceHashes[file] = sha((await fs.readFile(path.join(project, file), 'utf8')).replace(/\r\n/g, '\n'))
  await start(profileA); const initial = await state(); await combination()
  if (!baseline) {
    await createView(longName)
    await page.locator('.expanded-filters').getByRole('button', { name: '保存当前筛选', exact: true }).click()
    const draft = page.getByRole('textbox', { name: '常用筛选名称', exact: true }); await draft.fill('没有保存的名称草稿')
    await page.locator('.library-card .card-image').first().click(); await page.locator('.game-detail-modal').waitFor()
    await page.keyboard.press('Escape'); await page.locator('.game-detail-modal').waitFor({ state: 'hidden' })
    assert.equal(await page.locator('.saved-view-editor').count(), 1, 'Closing details must preserve the background saved-view name draft')
    assert.equal(await draft.inputValue(), '没有保存的名称草稿')
    await page.locator('.maintenance-entry').click(); await page.locator('.maintenance-modal').waitFor()
    await page.keyboard.press('Escape'); await page.locator('.maintenance-modal').waitFor({ state: 'hidden' })
    assert.equal(await draft.inputValue(), '没有保存的名称草稿', 'Closing maintenance must also keep the background editor')
    await draft.press('Escape'); await page.locator('.saved-view-editor').waitFor({ state: 'hidden' })
  }
  for (const condition of conditions) await capture('library-combination', condition)
  if (!baseline) {
    await capture('saved-long-name', conditions[1], '.saved-views')
    await page.getByRole('button', { name: '管理常用筛选', exact: true }).click()
    await capture('saved-long-manager', conditions[1], '.saved-views-manager')
    await page.getByRole('button', { name: '收起管理', exact: true }).click()
  }
  await navigate(2); await capture('collections', conditions[0])
  if (!baseline) {
    await openView(longName, 2); assert.equal(await page.locator('.library-card').count(), 2); assert.equal(await page.locator('.library-heading h1').evaluate(el => document.activeElement === el), true)
    const original = (await stored()).views[0]; assert.equal(original.filters.minRating, '0'); assert.equal(original.filters.view, 'wishlist'); assert.equal(original.filters.favorites, true)
    await search().fill('完全不存在的作品'); await page.locator('.saved-active').filter({ hasText: '已调整' }).waitFor()
    await filtersOpen(); await page.locator('.expanded-filters').getByRole('button', { name: '保存当前筛选', exact: true }).click(); const editor = page.getByRole('textbox', { name: '常用筛选名称', exact: true }); await editor.press('Enter'); await page.locator('.saved-views-error').filter({ hasText: '填写名称' }).waitFor()
    await editor.fill(longName); await editor.press('Enter'); await page.locator('.saved-views-error').filter({ hasText: '同名' }).waitFor(); assert.equal((await stored()).views.length, 1)
    await page.evaluate(key => { window.__originalSetItem = Storage.prototype.setItem; Storage.prototype.setItem = function(k, value) { if (k === key) throw Error('Simulated quota failure'); return window.__originalSetItem.call(this, k, value) } }, savedKey)
    await editor.fill('未匹配的故事'); await editor.press('Enter'); await page.locator('.saved-views-error').filter({ hasText: '未能保存' }).waitFor(); assert.equal((await stored()).views.length, 1); assert.equal(await editor.inputValue(), '未匹配的故事'); await capture('save-failure', conditions[0], '.saved-view-editor')
    await page.evaluate(() => { Storage.prototype.setItem = window.__originalSetItem; delete window.__originalSetItem }); await editor.press('Escape'); assert.equal(await page.locator('.expanded-filters').getByRole('button', { name: '保存当前筛选', exact: true }).evaluate(el => document.activeElement === el), true)
    await createView('未匹配的故事'); assert.equal(await page.locator('.library-card').count(), 0); await capture('saved-empty', conditions[0], '.saved-views')
    await page.getByRole('button', { name: '管理常用筛选', exact: true }).click(); await page.getByRole('button', { name: `重命名筛选：${longName}`, exact: true }).click(); await editor.fill('海岸周末'); await editor.press('Enter'); assert.deepEqual((await stored()).views[0].filters, original.filters, 'Rename must not replace the saved combination with current empty results')
    await openView('海岸周末', 2); await filtersOpen(); await page.locator('.expanded-filters').getByRole('button', { name: '重置筛选', exact: true }).click(); await search().fill('雨中'); await page.getByRole('group', { name: '书架快捷视图' }).getByRole('button', { name: '本地游戏', exact: true }).click()
    await page.getByRole('button', { name: '更新筛选条件：海岸周末', exact: true }).click(); assert.equal((await stored()).views[0].filters.query, '雨中'); assert.equal((await stored()).views[0].filters.view, 'local')
    await page.locator('.expanded-filters').getByRole('button', { name: '重置筛选', exact: true }).click(); await page.locator('.expanded-filters select').nth(0).selectOption('旧分类'); await createView('旧分类视图'); await page.evaluate(id => window.kunManager.updateGame(id, { categories: [] }), games[3].id)
    await page.getByRole('button', { name: '打开常用筛选：旧分类视图，0部作品', exact: true }).waitFor(); await page.locator('.saved-active').filter({ hasText: '原分类已不存在' }).waitFor(); assert.equal((await stored()).views.at(-1).filters.category, '旧分类'); assert.equal(await page.locator('.library-card').count(), 0)
    await capture('stale-category', conditions[1], '.saved-views'); const afterStale = await state(); assert.deepEqual(afterStale.games, initial.games.map(game => game.id === games[3].id ? { ...game, categories: [] } : game), 'Only the explicitly removed synthetic category may change')
    await page.locator('.expanded-filters').getByRole('button', { name: '重置筛选', exact: true }).click(); await page.getByRole('button', { name: '选择整理', exact: true }).click(); await page.getByRole('button', { name: '全选当前结果', exact: true }).click(); await openView('海岸周末', 1); assert.equal(await page.locator('.selection-toolbar').count(), 0); assert.equal(await page.locator('.library-card [role="checkbox"]').count(), 0, 'Opening a view never carries batch selections')
    assert.deepEqual((await state()).games, afterStale.games, 'Opening a saved view must not modify games')
    await page.getByRole('button', { name: '删除筛选：未匹配的故事', exact: true }).click(); assert.equal((await stored()).views.length, 2)
    await page.evaluate(async settings => window.kunManager.saveSettings({ ...settings, theme: 'dark' }), initial.settings); await page.waitForFunction(() => document.documentElement.classList.contains('kun-dark-mode')); await capture('saved-dark', conditions[1], '.saved-views')
    const persisted = await stored(); await application.close(); application = null; await start(profileA); assert.deepEqual(await stored(), persisted); await page.getByRole('button', { name: '打开常用筛选：海岸周末，1部作品', exact: true }).waitFor(); await openView('海岸周末', 1); assert.equal(await page.locator('.library-card').count(), 1)
    // Delay only the invocation timing. The original validated IPC handler and store transaction still execute.
    const beforeBulk = (await state()).games
    await application.evaluate(({ ipcMain }) => {
      const channel = 'kun-manager:updateGames', handler = ipcMain._invokeHandlers.get(channel)
      if (typeof handler !== 'function') throw Error('Original bulk IPC handler unavailable')
      let release, entered
      const gate = new Promise(resolve => { release = resolve })
      globalThis.__bulkEntered = new Promise(resolve => { entered = resolve })
      globalThis.__releaseBulk = release
      ipcMain.removeHandler(channel)
      ipcMain.handle(channel, async (event, ...args) => {
        globalThis.__bulkArguments = args; entered()
        let timer
        try { await Promise.race([gate, new Promise((_, reject) => { timer = setTimeout(() => reject(Error('Timed bulk gate was not released')), 10000) })]); return await handler(event, ...args) }
        finally { clearTimeout(timer); ipcMain.removeHandler(channel); ipcMain.handle(channel, handler) }
      })
    })
    await page.getByRole('button', { name: '选择整理', exact: true }).click(); await page.getByRole('button', { name: '全选当前结果', exact: true }).click(); await page.getByRole('combobox', { name: '批量收藏', exact: true }).selectOption('yes'); await page.getByRole('button', { name: '应用到 1 部', exact: true }).click()
    await application.evaluate(async () => { let timer; try { await Promise.race([globalThis.__bulkEntered, new Promise((_, reject) => { timer = setTimeout(() => reject(Error('Bulk invocation did not arrive')), 3000) })]) } finally { clearTimeout(timer) } })
    await page.waitForFunction(() => [...document.querySelectorAll('.saved-views button')].every(button => button.disabled))
    assert.equal(await search().inputValue(), '雨中'); assert.equal(await page.locator('.selection-toolbar').count(), 1); assert.deepEqual((await state()).games, beforeBulk, 'Pending bulk must not have written yet'); assert.deepEqual(await stored(), persisted)
    assert.deepEqual(await application.evaluate(() => globalThis.__bulkArguments), [[games[1].id], { favorite: true }])
    await application.evaluate(() => globalThis.__releaseBulk()); await page.locator('.selection-toolbar').waitFor({ state: 'hidden' }); assert.equal(await page.getByRole('button', { name: '打开常用筛选：海岸周末，1部作品', exact: true }).isEnabled(), true); assert.deepEqual((await state()).games, beforeBulk, 'The real idempotent bulk transaction preserves every game field')
    await application.close(); application = null; await start(profileB); assert.equal(await stored(), null); assert.equal(await page.locator('.saved-views').count(), 0, 'Separate isolated profile cannot see saved views')
    const beforeCorrupt = (await state()).games, raw = '{"version":99,"views":[]}'
    await page.evaluate(({ key, value }) => localStorage.setItem(key, value), { key: savedKey, value: raw }); await page.reload(); await page.locator('.saved-views-error').filter({ hasText: '旧记录未被覆盖' }).waitFor(); await filtersOpen(); assert.equal(await page.locator('.expanded-filters').getByRole('button', { name: '保存当前筛选', exact: true }).isDisabled(), true); assert.equal(await page.evaluate(key => localStorage.getItem(key), savedKey), raw); assert.deepEqual((await state()).games, beforeCorrupt); await capture('corrupt-storage', conditions[1], '.saved-views')
    await page.evaluate(key => localStorage.setItem(key, JSON.stringify({ version: 1, views: [] })), savedKey); await page.getByRole('button', { name: '重新读取', exact: true }).click(); await page.locator('.saved-views-error').waitFor({ state: 'hidden' }); assert.equal(await page.locator('.expanded-filters').getByRole('button', { name: '保存当前筛选', exact: true }).isEnabled(), true)
    report.checks.push('Real UI create/apply/rename/update/delete; zero/wishlist combination; Enter/Escape/focus including detail and maintenance draft preservation; explicit duplicate/empty-name/quota failures; dynamic vanished category without widening; batch selection cleared; saved actions disabled during a gated original IPC bulk transaction; exact no-game mutation; long-name manager and dark 150%; same-profile restart; cross-profile isolation; corrupt version preserved and recovery')
  } else report.checks.push('Verified round-zero runtime captured with identical fixture and filter/window conditions')
  assert.deepEqual(report.rendererErrors, []); report.status = 'passed'
} catch (error) { report.status = 'failed'; report.error = error.stack; throw error }
finally {
  await fs.writeFile(path.join(output, 'manifest.json'), JSON.stringify(report, null, 2)); if (application) await application.close()
  if (path.dirname(path.resolve(temporary)) !== temporaryRoot || !path.basename(temporary).startsWith('the-order-saved-views-')) throw Error('Unexpected cleanup directory')
  await fs.rm(temporary, { recursive: true, force: true })
}
console.log(JSON.stringify({ phase, status: report.status, screenshots: report.screenshots.length, baselineFailures: report.baselineFailures.length, rendererErrors: report.rendererErrors.length }))
