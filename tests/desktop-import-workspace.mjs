import assert from 'node:assert/strict'
import { promises as fs } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { createHash } from 'node:crypto'
import { execFileSync } from 'node:child_process'
import { _electron as electron } from '../node_modules/playwright/index.mjs'
const project = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const root = path.join(project, '.artifacts/overnight-2026-10-04/round-5'), profile = path.join(root, 'ui-profile'), marker = path.join(profile, '.synthetic-profile')
const baseline = process.argv.includes('--baseline'), phase = baseline ? 'before' : 'after', output = path.join(root, phase)
await fs.mkdir(output, { recursive: true })
if (await fs.stat(profile).catch(error => { if (error.code === 'ENOENT') return null; throw error })) {
  assert.equal(await fs.readFile(marker, 'utf8'), 'import-workspace-synthetic-only')
  assert.equal(path.resolve(profile), path.join(project, '.artifacts/overnight-2026-10-04/round-5/ui-profile'))
  await fs.rm(profile, { recursive: true })
}
await fs.mkdir(profile); await fs.writeFile(marker, 'import-workspace-synthetic-only')
const gamesRoot = path.join(root, 'synthetic-games-用于扫描的长公共路径'), emptyFolder = path.join(root, 'synthetic-empty')
await fs.mkdir(gamesRoot, { recursive: true }); await fs.mkdir(emptyFolder, { recursive: true })
const folders = []
for (let n = 1; n <= 24; n++) {
  const name = `模拟游戏 ${String(n).padStart(2, '0')} · 很长的版本名称与发行说明 Long Edition`
  const folder = path.join(gamesRoot, name); await fs.mkdir(folder, { recursive: true }); await fs.writeFile(path.join(folder, 'game.exe'), 'Inert synthetic file, never executed'); folders.push(folder)
}
const overlap = path.join(gamesRoot, '模拟重叠安装包'), overlapChild = path.join(overlap, 'bin')
await fs.mkdir(overlapChild, { recursive: true }); await fs.writeFile(path.join(overlapChild, 'game.exe'), 'Inert synthetic file, never executed')
const date = '2026-10-04T00:00:00.000Z'
const libraryGame = (id, folder) => ({ id, name: '模拟已有作品 ' + id, nameIsCustom: true, metadata: null, installations: [{ id: 'install-' + id, folderPath: folder, executablePath: null, arguments: [] }], preferredInstallationId: 'install-' + id, status: 'planned', personalRating: 8, favorite: true, categories: ['模拟保留'], customTags: [], notes: 'Synthetic personal notes must remain', totalPlaySeconds: 0, playCount: 0, sessions: [], addedAt: date, lastPlayedAt: null })
const initialGames = [libraryGame('known-1', folders[0]), libraryGame('known-2', folders[1]), libraryGame('overlap', overlap)]
const libraryBytes = JSON.stringify({ format: 'kun-gal-manager', version: 1, exportedAt: date, games: initialGames })
await fs.writeFile(path.join(profile, 'library.json'), libraryBytes)
const hash = bytes => createHash('sha256').update(bytes).digest('hex')
const report = { phase, checkoutHead: execFileSync('git', ['rev-parse', 'HEAD'], { encoding: 'utf8' }).trim(), sourceDirty: !!execFileSync('git', ['status', '--porcelain'], { encoding: 'utf8' }).trim(), fixtureSha256: hash(libraryBytes), syntheticOnly: true, realApiCalls: 0, formalProfileAccessed: false, screenshots: [], checks: [], rendererErrors: [] }
let app, page
const state = () => page.evaluate(() => window.kunManager.getState())
const dismissToasts = async () => { await page.evaluate(() => document.querySelectorAll('[data-kun-toast-close]').forEach(b => b.click())); await page.waitForFunction(() => !document.querySelector('[data-kun-toast-close]')) }
const conditions = [{ width: 1440, height: 940, zoom: 1 }, { width: 1000, height: 700, zoom: 1.5 }]
async function capture(label, condition, keepToasts = false) {
  if (!keepToasts) await dismissToasts()
  await app.evaluate(({ BrowserWindow }, c) => { const w = BrowserWindow.getAllWindows()[0]; w.setContentSize(c.width, c.height); w.webContents.setZoomFactor(c.zoom) }, condition)
  await page.waitForFunction(c => Math.abs(innerWidth - c.width / c.zoom) < 3 && Math.abs(innerHeight - c.height / c.zoom) < 3, condition)
  await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))); await page.waitForTimeout(120)
  const actual = await page.evaluate(() => { const dialog = document.querySelector('[role=dialog]'), body = document.querySelector('.import-body'), footer = document.querySelector('.scan-footer'); const bounds = body?.getBoundingClientRect(); return { width: innerWidth, height: innerHeight, dpr: devicePixelRatio, scrollWidth: document.documentElement.scrollWidth, dialog: dialog?.getBoundingClientRect().toJSON(), footer: footer?.getBoundingClientRect().toJSON(), rowsVisible: [...document.querySelectorAll('.scan-list label')].filter(el => { const r=el.getBoundingClientRect(); return r.top>=(bounds?.top??0)&&r.bottom<(bounds?.bottom??innerHeight) }).length } })
  const bytes = Buffer.from(await app.evaluate(async ({ BrowserWindow }) => (await BrowserWindow.getAllWindows()[0].webContents.capturePage()).toPNG().toString('base64')), 'base64')
  const file = `${label}-${condition.width}x${condition.height}-zoom${condition.zoom}.png`; await fs.writeFile(path.join(output, file), bytes)
  report.screenshots.push({ file, requested: condition, actual, pixels: [bytes.readUInt32BE(16), bytes.readUInt32BE(20)], sha256: hash(bytes) })
  if (!baseline) { assert.ok(actual.scrollWidth <= actual.width + 2, 'No horizontal overflow'); if (actual.dialog) assert.ok(actual.footer && actual.footer.top >= 0 && actual.footer.bottom <= actual.height, 'Confirmation footer stays in the actual viewport') }
}
try {
  app = await electron.launch({ executablePath: path.join(project, 'node_modules/electron/dist/electron.exe'), args: [path.join(project, 'tests/desktop-settings-matching-bootstrap.cjs')], env: { ...process.env, KGM_DATA_DIR: profile, KGM_DEV_URL: '', KGM_TEST_EXECUTABLE: '' }, timeout: 30000 })
  await app.evaluate(({ dialog, session }, root) => {
    globalThis.__scanChoice = root
    dialog.showOpenDialog = async () => ({ canceled: globalThis.__scanChoice === null, filePaths: globalThis.__scanChoice === null ? [] : [globalThis.__scanChoice] })
    session.defaultSession.webRequest.onBeforeRequest({ urls: ['http://*/*', 'https://*/*'] }, (_, cb) => cb({ cancel: true }))
    globalThis.__importNetworkAttempts = 0
    globalThis.fetch = async () => { globalThis.__importNetworkAttempts++; throw new Error('Unexpected network request') }
  }, gamesRoot)
  page = await app.firstWindow(); page.on('pageerror', error => report.rendererErrors.push(error.message)); await page.waitForFunction(() => window.kunManager !== undefined)
  await page.evaluate(async () => window.kunManager.saveSettings({ ...(await window.kunManager.getState()).settings, readExecutableMetadata: false }))
  await page.getByRole('button', { name: '添加游戏', exact: true }).click()
  await page.getByRole('button', { name: '确认导入 22 部', exact: true }).waitFor()
  for (const c of conditions) await capture('mixed-scan', c)
  assert.equal(await page.locator('.scan-list input:disabled').count(), 3)
  await page.getByRole('textbox', { name: '筛选扫描结果' }).fill('模拟游戏 03')
  assert.equal(await page.locator('.scan-list label').count(), 1)
  if (baseline) {
    await page.getByRole('button', { name: '清空选择', exact: true }).click(); await page.getByRole('button', { name: '全选新游戏', exact: true }).click()
    assert.equal(await page.getByRole('button', { name: '确认导入 22 部', exact: true }).count(), 1)
    report.checks.push('search leaves one visible row but global select and confirm include 22: reproduced')
  }
  for (const c of conditions) await capture('filtered-scan', c)
  if (!baseline) {
    await page.locator('.scan-hidden-selection').getByText(/筛选外仍选中 21 项/).waitFor()
    await page.getByRole('button', { name: '仅选择当前结果', exact: true }).click()
    await page.getByRole('button', { name: '确认导入 1 部', exact: true }).waitFor()
    await page.getByRole('button', { name: '取消当前结果选择', exact: true }).click()
    assert.equal(await page.getByRole('button', { name: '确认导入 0 部', exact: true }).isDisabled(), true)
    await page.getByRole('button', { name: '选择当前结果', exact: true }).click()
    await page.getByRole('textbox', { name: '筛选扫描结果' }).fill('unmatched-no-row')
    await page.getByText('没有符合筛选的目录。', { exact: true }).waitFor()
    assert.equal(await page.getByRole('button', { name: '确认导入 1 部', exact: true }).count(), 1)
    await page.getByRole('textbox', { name: '筛选扫描结果' }).fill('')
    await page.getByRole('button', { name: /已在书架 2/ }).click(); assert.equal(await page.locator('.scan-list input:disabled').count(), 2)
    await page.getByRole('button', { name: /目录重叠 1/ }).click(); assert.equal(await page.locator('.scan-list input:disabled').count(), 1)
    await page.getByRole('button', { name: /全部 25/ }).click()
    report.checks.push('status groups and scoped add/remove/only preserve hidden choices and never select blocked rows')
  }
  await page.getByRole('dialog').getByRole('button', { name: '取消', exact: true }).click()
  await page.getByRole('dialog').waitFor({ state: 'hidden' })
  assert.deepEqual((await state()).games, initialGames)
  if (!baseline) {
    await app.evaluate(() => { globalThis.__scanChoice = null })
    await dismissToasts(); await page.getByRole('button', { name: '添加游戏', exact: true }).click()
    await page.waitForTimeout(150); assert.equal(await page.getByRole('dialog').count(), 0)
    assert.equal(await page.locator('[data-kun-toast-close]').count(), 0, 'Canceled folder dialog does not announce an empty scan')
    await app.evaluate((_electron, folder) => { globalThis.__scanChoice = folder }, emptyFolder)
    await page.getByRole('button', { name: '添加游戏', exact: true }).click()
    await page.getByText('这个文件夹中没有找到游戏程序。', { exact: true }).waitFor()
    assert.equal(await page.getByRole('button', { name: '确认导入 0 部', exact: true }).isDisabled(), true)
    await capture('empty-scan', conditions[1])
    await app.evaluate((_electron, folder) => { globalThis.__scanChoice = folder }, gamesRoot)
    await page.getByRole('button', { name: '重新选择文件夹', exact: true }).click()
    await page.getByRole('button', { name: '确认导入 22 部', exact: true }).waitFor()
    await page.getByRole('textbox', { name: '筛选扫描结果' }).fill('模拟游戏 03')
    await page.getByRole('button', { name: '仅选择当前结果', exact: true }).click()
    const checkbox = page.locator('.scan-list input'); await checkbox.focus(); await page.keyboard.press('Space')
    assert.equal(await page.getByRole('button', { name: '确认导入 0 部', exact: true }).isDisabled(), true)
    await page.keyboard.press('Space')
    const fullPath = page.locator('.scan-row summary')
    // Enter expands the native disclosure without toggling the row selection.
    await fullPath.focus(); await page.keyboard.press('Enter')
    assert.equal(await page.locator('.scan-row details').evaluate(el => el.open), true)
    assert.equal(await page.getByRole('button', { name: '确认导入 1 部', exact: true }).count(), 1)
    await capture('keyboard-full-path', conditions[1])
    await app.evaluate(() => { globalThis.__settingsMatching.fail = 'library.json' })
    await page.getByRole('button', { name: '确认导入 1 部', exact: true }).click()
    await page.getByRole('alert').filter({ hasText: '导入未完成，选择已保留' }).waitFor()
    assert.deepEqual((await state()).games, initialGames)
    assert.equal(await page.locator('.scan-list input:checked').count(), 1)
    await capture('failed-import', conditions[1])
    await app.evaluate(() => { globalThis.__settingsMatching.hold = 'library.json' })
    await page.getByRole('button', { name: '确认导入 1 部', exact: true }).click()
    await page.waitForFunction(() => document.querySelector('.import-workspace')?.getAttribute('aria-busy') === 'true')
    assert.equal(await page.getByRole('textbox', { name: '筛选扫描结果' }).isDisabled(), true)
    assert.equal(await checkbox.isDisabled(), true)
    assert.equal(await page.getByRole('switch', { name: '导入后自动补全封面和资料', exact: true }).isDisabled(), true)
    for (const button of await page.locator('.scan-views button, .scan-selection button, .scan-footer button').all()) assert.equal(await button.isDisabled(), true)
    await page.keyboard.press('Escape'); assert.equal(await page.getByRole('dialog').count(), 1)
    await capture('import-in-progress', conditions[1])
    assert.equal(await app.evaluate(() => globalThis.__settingsMatching.held), true)
    await app.evaluate(() => globalThis.__settingsMatching.release())
    await page.getByRole('dialog').waitFor({ state: 'hidden' })
    const importedState = await state()
    assert.equal(importedState.games.length, 4, 'Only the chosen directory is imported')
    for (const original of initialGames) assert.deepEqual(importedState.games.find(game => game.id === original.id), original)
    assert.equal(importedState.games.filter(game => !initialGames.some(original => original.id === game.id))[0].installations[0].folderPath, folders[2])
    report.checks.push('native folder cancellation is silent; real empty scan is recoverable; keyboard selects and expands full paths', 'actual atomic import failure keeps library and selection; retry imports only the chosen directory; pending commit locks controls and Escape')
    await page.evaluate(async () => window.kunManager.saveSettings({ ...(await window.kunManager.getState()).settings, theme: 'dark' }))
    await page.waitForFunction(() => document.documentElement.classList.contains('kun-dark-mode'))
    await page.getByRole('button', { name: '添加游戏', exact: true }).click()
    await page.getByRole('button', { name: /已在书架 3/ }).waitFor()
    for (const c of conditions) await capture('dark-repeat-scan', c)
    assert.equal(await page.locator('.scan-list input:disabled').count(), 4)
    await page.getByRole('dialog').getByRole('button', { name: '取消', exact: true }).click()
    await page.getByRole('dialog').waitFor({ state: 'hidden' })
    await page.getByRole('button', { name: '添加游戏', exact: true }).focus()
    await page.keyboard.press('Enter'); await page.getByRole('dialog').waitFor()
    await page.keyboard.press('Escape'); await page.getByRole('dialog').waitFor({ state: 'hidden' })
    await page.waitForFunction(() => [...document.querySelectorAll('button')].find(el => el.textContent.trim() === '添加游戏') === document.activeElement, null, { timeout: 1500 })
    assert.equal(await page.getByRole('button', { name: '添加游戏', exact: true }).evaluate(el => el === document.activeElement), true)
    report.checks.push('repeat scan excludes all newly imported directories; dark/light narrow and wide native pages; keyboard Escape returns focus to import entry')
    // A non-secret synthetic credential enables automatic startup. Only its
    // initial job-state write fails; the real library transaction succeeds.
    await page.evaluate(async () => window.kunManager.saveSettings({ ...(await window.kunManager.getState()).settings, apiKey: 'synthetic-offline-import-key' }))
    await page.getByRole('button', { name: '添加游戏', exact: true }).click()
    await page.getByRole('button', { name: '确认导入 21 部', exact: true }).waitFor()
    await page.getByRole('textbox', { name: '筛选扫描结果' }).fill('模拟游戏 04')
    await page.getByRole('button', { name: '仅选择当前结果', exact: true }).click()
    const beforeMatching = (await state()).matching
    await app.evaluate(() => { globalThis.__settingsMatching.fail = 'metadata-matching.json' })
    await page.getByRole('button', { name: '确认导入 1 部', exact: true }).click()
    await page.getByRole('dialog').waitFor({ state: 'hidden' })
    await page.getByText(/本地游戏已导入，但资料补全未能启动/).waitFor()
    const afterMatchingFailure = await state()
    assert.equal(afterMatchingFailure.games.length, 5)
    assert.deepEqual(afterMatchingFailure.matching, beforeMatching, 'Failed recognition initialization cannot leave a running job')
    for (const original of initialGames) assert.deepEqual(afterMatchingFailure.games.find(game => game.id === original.id), original)
    assert.equal(await app.evaluate(() => globalThis.__importNetworkAttempts), 0)
    await capture('import-saved-matching-failed', conditions[1], true)
    await page.getByRole('button', { name: '添加游戏', exact: true }).click()
    await page.getByRole('button', { name: /已在书架 4/ }).waitFor()
    assert.equal(await page.locator('.scan-list input:disabled').count(), 5)
    await page.getByRole('dialog').getByRole('button', { name: '取消', exact: true }).click()
    report.checks.push('library commit succeeds while matching-state rename fails: truthful warning, previous matching state restored, no network requests and no duplicate import')
  }
  report.runtimeHashes = {}
  for (const file of ['dist/index.html', 'dist-electron/main.cjs', 'dist-electron/preload.cjs', ...(await fs.readdir('dist/assets')).filter(x => /\.(js|css)$/.test(x)).map(x => 'dist/assets/' + x)]) report.runtimeHashes[file] = hash(await fs.readFile(file))
  assert.equal(report.rendererErrors.length, 0, report.rendererErrors.join('\n'))
  report.status = 'passed'; await fs.writeFile(path.join(output, 'manifest.json'), JSON.stringify(report, null, 2)); console.log(JSON.stringify({ phase, checks: report.checks, screenshots: report.screenshots.length }))
} catch (error) {
  report.status = 'failed'; report.failure = String(error)
  if (page) report.lastUiText = (await page.locator('body').innerText()).slice(-6000)
  await fs.writeFile(path.join(output, 'last-failure.json'), JSON.stringify(report, null, 2))
  throw error
} finally { if (app) await app.close() }
