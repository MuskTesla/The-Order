import assert from 'node:assert/strict'
import { promises as fs } from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { createHash } from 'node:crypto'
import { execFileSync } from 'node:child_process'
import { _electron as electron } from '../node_modules/playwright/index.mjs'

// This final smoke test runs the normal packaged program and production IPC.
// Fixtures are fictional; only the native directory picker is given a bounded
// synthetic folder. No catalogue API, game executable or private profile is used.
const project = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const night = path.join(project, '.artifacts/overnight-2026-10-04')
const executable = path.join(night, 'candidate/win-unpacked/The Order.exe')
const output = path.join(night, 'round-8/packaged', new Date().toISOString().replace(/[:.]/g, '-'))
const heldGuard = JSON.parse(await fs.readFile(path.join(night, 'writer.lock'), 'utf8'))
assert.ok(process.env.KGM_OVERNIGHT_GUARD_TOKEN, 'A held overnight guard token is required')
assert.equal(heldGuard.token, process.env.KGM_OVERNIGHT_GUARD_TOKEN); process.kill(heldGuard.pid, 0)
const tempRoot = path.resolve(os.tmpdir())
const temporary = await fs.mkdtemp(path.join(tempRoot, 'the-order-overnight-packaged-'))
const profile = path.join(temporary, 'profile'), emptyFolder = path.join(temporary, 'No game executable · 合成目录')
const version = JSON.parse(await fs.readFile(path.join(project, 'package.json'), 'utf8')).version
const savedKey = 'the-order:saved-library-views:v1', date = '2026-10-04T00:00:00.000Z'
const names = ['【虚构测试】湖岸来信与那些从未忘记的夏日约定'.repeat(3), '【虚构测试】未关联的小镇', '【虚构测试】尚未安装的约定']
const conditions = [{ width: 1440, height: 940, zoom: 1 }, { width: 1000, height: 700, zoom: 1.5 }]
const hash = bytes => createHash('sha256').update(bytes).digest('hex')
const report = { capturedAt: new Date().toISOString(), checkoutHead: execFileSync('git', ['rev-parse', 'HEAD'], { cwd: project, encoding: 'utf8' }).trim(), sourceDirty: !!execFileSync('git', ['status', '--porcelain'], { cwd: project, encoding: 'utf8' }).trim(), executable, packageHashes: {}, sourceHashes: {}, fixtureSha256: null, screenshots: [], checks: [], rendererErrors: [], syntheticOnly: true, nativePickerUsesSyntheticDirectory: true, formalProfileAccessed: false, realApiCallsAttemptedByTest: 0, realGameExecutions: 0, unverified: ['Live catalogue/API credentials and real game identification or launching', 'Actual Windows native folder-picker interaction', 'Large private libraries, screen-reader use, forced crash recovery'] }
let application, page, failure
const state = () => page.evaluate(() => window.kunManager.getState())
const detail = () => page.locator('.game-detail-modal')
const search = () => page.getByRole('textbox', { name: '搜索书架', exact: true })
const settingsField = label => page.locator('.preferences-settings .field-label').filter({ hasText: label }).locator('input,select')
const timeout = setTimeout(() => {
  report.status = 'timed-out'; report.error = 'Final packaged smoke test exceeded five minutes'
  Promise.allSettled([fs.writeFile(path.join(output, 'manifest.json'), JSON.stringify(report, null, 2)), application?.close()]).finally(() => process.exit(1))
}, 300000)

async function start() {
  const env = { ...process.env, KGM_DATA_DIR: profile, KGM_DEV_URL: 'http://127.0.0.1:5173' }
  delete env.KGM_TEST_EXECUTABLE; delete env.KGM_PROVISION_FILE
  application = await electron.launch({ executablePath: executable, args: [], env, timeout: 30000 })
  page = await application.firstWindow(); page.setDefaultTimeout(12000); page.on('pageerror', error => report.rendererErrors.push(error.message))
  await page.waitForFunction(() => window.kunManager !== undefined); await page.emulateMedia({ reducedMotion: 'reduce' })
  assert.equal(page.url(), 'kun-manager://app/index.html', 'The candidate must load the production origin despite KGM_DEV_URL')
  const identity = await application.evaluate(({ app, BrowserWindow }) => ({ packaged: app.isPackaged, version: app.getVersion(), userData: app.getPath('userData'), executable: process.execPath, preferences: BrowserWindow.getAllWindows()[0].webContents.getLastWebPreferences() }))
  assert.equal(identity.packaged, true); assert.equal(identity.version, version); assert.equal(path.resolve(identity.userData), profile)
  assert.equal(path.resolve(identity.executable), executable)
  assert.equal(identity.preferences.contextIsolation, true); assert.equal(identity.preferences.nodeIntegration, false); assert.equal(identity.preferences.sandbox, true)
  assert.equal((await state()).dataPath, profile); assert.equal((await state()).settings.hasApiKey, false)
  report.identity = { packaged: identity.packaged, version: identity.version, dataPath: identity.userData, contextIsolation: true, sandbox: true, nodeIntegration: false }
}
async function navigate(label) { await page.locator('.sidebar').getByRole('button', { name: label, exact: true }).click() }
async function resize(condition) {
  await application.evaluate(({ BrowserWindow }, c) => { const windows = BrowserWindow.getAllWindows(); if (windows.length !== 1) throw Error('Expected one candidate window'); const win = windows[0]; if (win.isMaximized()) win.unmaximize(); win.setContentSize(c.width, c.height); win.webContents.setZoomFactor(c.zoom) }, condition)
  await page.waitForFunction(c => Math.abs(innerWidth - c.width / c.zoom) < 3 && Math.abs(innerHeight - c.height / c.zoom) < 3, condition)
}
async function capture(label, condition, target) {
  // Dismiss already-asserted transient feedback through its ordinary close
  // controls, without hiding DOM, changing CSS, or removing error state.
  await page.evaluate(() => document.querySelectorAll('[data-kun-toast-close]').forEach(button => button.click()))
  await page.waitForFunction(() => document.querySelectorAll('[data-kun-toast-close]').length === 0, null, { timeout: 3000 })
  await resize(condition); if (target) await page.locator(target).first().scrollIntoViewIfNeeded()
  await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))); await page.waitForTimeout(100)
  const actual = await page.evaluate(() => { const modal = document.querySelector('.game-detail-modal'); return { width: innerWidth, height: innerHeight, dpr: devicePixelRatio, scrollWidth: document.documentElement.scrollWidth, theme: document.documentElement.classList.contains('kun-dark-mode') ? 'dark' : 'light', origin: location.href, modalWidth: modal?.clientWidth ?? null, modalScrollWidth: modal?.scrollWidth ?? null, active: document.activeElement?.getAttribute('aria-label') ?? document.activeElement?.textContent?.trim().slice(0, 60) } })
  assert.ok(actual.scrollWidth <= actual.width + 2, `${label}: document has horizontal overflow`)
  if (actual.modalWidth !== null) assert.ok(actual.modalScrollWidth <= actual.modalWidth + 2, `${label}: detail has horizontal overflow`)
  const bytes = Buffer.from(await application.evaluate(async ({ BrowserWindow }) => (await BrowserWindow.getAllWindows()[0].webContents.capturePage()).toPNG().toString('base64')), 'base64')
  const pixelWidth = bytes.readUInt32BE(16), pixelHeight = bytes.readUInt32BE(20)
  assert.ok(Math.abs(pixelWidth - condition.width * actual.dpr / condition.zoom) < 5); assert.ok(Math.abs(pixelHeight - condition.height * actual.dpr / condition.zoom) < 5)
  const filename = `${label}-${condition.width}x${condition.height}-zoom${condition.zoom}.png`
  await fs.writeFile(path.join(output, filename), bytes)
  report.screenshots.push({ file: path.join(output, filename), requested: condition, actual, target: target ?? null, pixelWidth, pixelHeight, sha256: hash(bytes) })
}
async function closeCleanDetail() { await page.keyboard.press('Escape'); await detail().waitFor({ state: 'hidden' }) }
async function notes() { if (await page.locator('.record-notes').getAttribute('open') === null) await page.locator('.record-notes summary').click(); return page.getByRole('textbox', { name: '记录游玩笔记', exact: true }) }
async function waitRecord(patch) { await page.waitForFunction(async patch => { const game = (await window.kunManager.getState()).games.find(game => game.id === 'final-smoke-0'); return Object.entries(patch).every(([key, value]) => game[key] === value) }, patch) }

try {
  const lock = JSON.parse(await fs.readFile(path.join(night, 'writer.lock'), 'utf8')); assert.equal(lock.token, heldGuard.token); process.kill(lock.pid, 0)
  await fs.access(executable); await fs.mkdir(output, { recursive: true }); await fs.mkdir(emptyFolder)
  for (const filename of ['The Order.exe', 'resources/app.asar']) report.packageHashes[filename] = hash(await fs.readFile(path.join(path.dirname(executable), filename)))
  for (const filename of ['src/App.vue', 'src/components/PersonalRecord.vue', 'src/components/LibraryCollections.vue', 'src/components/ImportCandidates.vue', 'src/components/PreferencesSettings.vue', 'tests/packaged-overnight.mjs']) report.sourceHashes[filename] = hash((await fs.readFile(path.join(project, filename), 'utf8')).replace(/\r\n/g, '\n'))
  await start(); await page.getByRole('heading', { name: '让第一段故事，住进书架', exact: true }).waitFor(); assert.equal((await state()).games.length, 0)
  await capture('empty-production-library', conditions[0], '.empty-state')
  const responsePromise = page.waitForResponse(response => response.url() === 'kun-manager://app/index.html'); await page.reload(); const response = await responsePromise
  const csp = (await response.allHeaders())['content-security-policy']; assert.ok(csp); assert.ok(!csp.includes('127.0.0.1:5173'))
  assert.equal(await page.evaluate(async () => { try { await fetch('http://127.0.0.1:5173/blocked-production-check'); return false } catch { return true } }), true)
  assert.equal(await page.evaluate(() => typeof window.require), 'undefined'); report.productionCsp = csp
  report.checks.push('Normal packaged executable, exact isolated userData/version, production origin ignores DEV override, production CSP blocks localhost, sandbox/contextIsolation and no renderer Node')

  // Generate JPEG fixtures from the repository's original CC0 vector scene in
  // the actual Chromium renderer. This is fixture preparation, not API data.
  const svg = await fs.readFile(path.join(project, 'tests/ui-fixtures/cover-0.svg'), 'utf8')
  const covers = await page.evaluate(async svg => {
    const image = new Image(); image.src = 'data:image/svg+xml;base64,' + btoa(unescape(encodeURIComponent(svg))); await image.decode()
    return [{ width: 360, height: 540 }, { width: 540, height: 360 }].map(size => { const canvas = document.createElement('canvas'); canvas.width = size.width; canvas.height = size.height; canvas.getContext('2d').drawImage(image, 0, 0, size.width, size.height); return { ...size, dataUrl: canvas.toDataURL('image/jpeg', .8) } })
  }, svg)
  await application.close(); application = undefined
  const games = names.map((name, index) => ({ id: `final-smoke-${index}`, name, nameIsCustom: true, metadata: index === 1 ? null : { id: String(987800 + index), title: name, originalTitle: 'Fictional trial ' + index, aliases: ['合成别名'], coverUrl: null, bannerUrl: null, description: '原创合成测试资料，不是游戏图片或真实联网结果。' + '长文本用于检查换行和窄窗口阅读。'.repeat(8), releaseDate: '2026-10-04', releaseDatePrecision: 'day', developers: ['虚构测试工房'], tags: ['虚构测试'], ratings: [], characters: [], references: [], webUrl: null, contentRating: '全年龄', fetchedAt: date }, ...(index !== 1 ? { localCover: covers[index === 0 ? 0 : 1] } : {}), installations: index < 2 ? [{ id: `final-install-${index}`, folderPath: path.join(temporary, 'fictional-games', String(index)), executablePath: null, arguments: [] }] : [], preferredInstallationId: index < 2 ? `final-install-${index}` : null, status: 'planned', personalRating: index === 0 ? 0 : null, favorite: index === 0, categories: [index === 0 ? '合成长分类名称 · ' + '那些值得珍藏的故事'.repeat(5) : '合成分类'], customTags: ['隔离试用'], notes: '已保存的合成笔记', totalPlaySeconds: 0, playCount: 0, sessions: [], addedAt: date, lastPlayedAt: null }))
  for (const game of games) for (const installation of game.installations) await fs.mkdir(installation.folderPath, { recursive: true })
  const fixture = JSON.stringify({ format: 'kun-gal-manager', version: 1, exportedAt: date, games }); report.fixtureSha256 = hash(fixture)
  await fs.writeFile(path.join(profile, 'library.json'), fixture); await start(); await page.locator('.library-card').first().waitFor(); assert.deepEqual((await state()).games, games)
  await capture('fictional-library', conditions[0], '.library-controls')
  await navigate('分类收藏'); await capture('collections-long-name', conditions[0], '.collections-toolbar')
  await navigate('我的书架'); await search().fill('湖岸来信'); assert.equal(await page.locator('.library-card').count(), 1)
  await page.locator('.library-filter-line>.filter-button').click(); await page.locator('.expanded-filters').getByRole('button', { name: '保存当前筛选', exact: true }).click()
  const editor = page.getByRole('textbox', { name: '常用筛选名称', exact: true }); await editor.fill('湖岸来信 · 隔离试用'); await editor.press('Enter'); await page.locator('.saved-view-editor').waitFor({ state: 'hidden' })
  const saved = await page.evaluate(key => JSON.parse(localStorage.getItem(key)), savedKey); assert.equal(saved.views.length, 1); assert.equal(saved.views[0].filters.query, '湖岸来信')
  await search().fill('不存在的合成作品'); assert.equal(await page.locator('.library-card').count(), 0)
  const view = page.getByRole('button', { name: '打开常用筛选：湖岸来信 · 隔离试用，1部作品', exact: true }); await view.focus(); await view.press('Enter'); assert.equal(await search().inputValue(), '湖岸来信'); assert.equal(await page.locator('.library-card').count(), 1)
  report.checks.push('Fictional offline JPEG covers, long categories and title, saved combination created through UI and applied by keyboard with actual persisted localStorage')

  await page.getByRole('button', { name: names[0], exact: true }).click(); await detail().waitFor(); const beforeRecord = (await state()).games[0]
  await page.getByRole('textbox', { name: '记录我的评分', exact: true }).fill('7.5'); await (await notes()).fill('尚未保存的合成草稿')
  await page.keyboard.press('Escape'); await page.getByText('放弃未保存的修改？', { exact: true }).waitFor(); await page.getByRole('button', { name: '继续编辑', exact: true }).click(); await page.getByText('放弃未保存的修改？', { exact: true }).waitFor({ state: 'hidden' })
  assert.ok(await detail().isVisible()); assert.equal(await (await notes()).inputValue(), '尚未保存的合成草稿'); assert.deepEqual((await state()).games[0], beforeRecord)
  await capture('detail-uncommitted-record', conditions[0], '.personal-record')
  await page.getByRole('combobox', { name: '记录游玩状态', exact: true }).selectOption('playing'); const expectedRecord = { personalRating: 7.5, notes: '已保存的夜间候选合成笔记', status: 'playing' }; await (await notes()).fill(expectedRecord.notes)
  const saveRecord = page.getByRole('button', { name: '保存我的记录', exact: true }); await saveRecord.focus(); await saveRecord.press('Enter'); await waitRecord(expectedRecord); await page.locator('.detail-draft-status').waitFor({ state: 'hidden' }); await closeCleanDetail()
  report.checks.push('Actual detail record edit; Escape confirmation cancellation retains draft without disk write; keyboard explicit save persists status/score/notes; clean close')

  await search().fill(''); await page.getByRole('button', { name: '添加游戏', exact: true }).click({ trial: true })
  await application.evaluate(({ dialog }, folder) => { dialog.showOpenDialog = async () => ({ canceled: false, filePaths: [folder] }) }, emptyFolder)
  await page.getByRole('button', { name: '添加游戏', exact: true }).click(); await page.getByText('这个文件夹中没有找到游戏程序。', { exact: true }).waitFor(); assert.equal(await page.getByRole('button', { name: '确认导入 0 部', exact: true }).isDisabled(), true)
  await capture('actual-scan-empty-directory', conditions[0], '.scan-empty'); await page.getByRole('dialog').getByRole('button', { name: '取消', exact: true }).click(); await page.locator('.scan-empty').waitFor({ state: 'hidden' })
  await navigate('资料整理'); await page.locator('.maintenance-modal').waitFor(); const zeroReview = page.getByRole('button', { name: /待核对 0 项/ }); assert.equal(await zeroReview.isDisabled(), true); assert.equal((await state()).matching.reviews.length, 0); await capture('maintenance-no-review', conditions[0], '.maintenance-review-link'); await page.keyboard.press('Escape'); await page.locator('.maintenance-modal').waitFor({ state: 'hidden' })
  report.checks.push('Real scanner reads synthetic directory with no executable and reports actual empty result; no import and disabled zero confirmation; real maintenance zero review queue has disabled entry')

  await navigate('偏好设置'); const baselineSettings = (await state()).settings; await settingsField('API Base URL').fill('not-a-url'); await page.getByRole('button', { name: '保存全部设置', exact: true }).click(); await page.locator('.preferences-error').filter({ hasText: '设置保存未确认' }).waitFor(); assert.deepEqual((await state()).settings, baselineSettings); assert.equal(await settingsField('API Base URL').inputValue(), 'not-a-url'); await capture('settings-invalid-address', conditions[0], '.settings-save-bar')
  await page.getByRole('button', { name: '放弃未保存更改', exact: true }).click(); await settingsField('外观主题').selectOption('dark'); await page.getByRole('combobox', { name: '非竖版封面默认展示', exact: true }).selectOption('plain'); const saveSettings = page.getByRole('button', { name: '保存全部设置', exact: true }); await saveSettings.focus(); await saveSettings.press('Enter')
  await page.waitForFunction(async () => (await window.kunManager.getState()).settings.theme === 'dark'); assert.equal((await state()).settings.nonPortraitCoverDisplay, 'plain'); await page.waitForFunction(() => document.documentElement.classList.contains('kun-dark-mode')); await capture('settings-dark', conditions[1], '.settings-panel:nth-of-type(2)')
  await navigate('分类收藏'); await capture('collections-dark-long-name', conditions[1], '.collections-toolbar'); await navigate('我的书架'); await capture('library-dark', conditions[1], '.saved-views')
  await navigate('分类收藏'); await capture('collections-dark-long-content', conditions[1], '.collection-card:last-child .collection-copy'); await navigate('我的书架')
  await page.getByRole('button', { name: names[0], exact: true }).click(); await detail().waitFor(); await capture('detail-dark-record', conditions[1], '.personal-record'); await closeCleanDetail()
  report.checks.push('Real settings transaction rejects invalid address without altering saved settings and retains failed input; explicit dark/plain preferences saved by keyboard; measured wide 100% and 1000x700 150% dark layouts have no horizontal overflow')

  const persistedGames = (await state()).games, persistedSettings = (await state()).settings
  await application.close(); application = undefined; await start(); await page.locator('.library-card').first().waitFor()
  assert.deepEqual((await state()).games, persistedGames); assert.deepEqual((await state()).settings, persistedSettings); assert.deepEqual(await page.evaluate(key => JSON.parse(localStorage.getItem(key)), savedKey), saved)
  const reloadedView = page.getByRole('button', { name: '打开常用筛选：湖岸来信 · 隔离试用，1部作品', exact: true }); await reloadedView.focus(); await reloadedView.press('Enter'); assert.equal(await page.locator('.library-card').count(), 1); await capture('restarted-candidate-library', conditions[0], '.saved-views')
  assert.deepEqual(JSON.parse(await fs.readFile(path.join(profile, 'library.json'), 'utf8')).games, persistedGames); assert.deepEqual(report.rendererErrors, [])
  report.checks.push('Normal packaged process restart preserves full library record, saved view and explicit preferences; actual library.json matches public state; zero renderer errors')
  report.status = 'passed'
} catch (error) { report.status = 'failed'; report.error = error.stack; failure = error }
finally {
  clearTimeout(timeout)
  if (application) await application.close().catch(error => { report.closeError = error.message; report.status = 'failed'; failure ??= error })
  await fs.mkdir(output, { recursive: true }); await fs.writeFile(path.join(output, 'manifest.json'), JSON.stringify(report, null, 2))
  if (path.dirname(temporary) !== tempRoot || !path.basename(temporary).startsWith('the-order-overnight-packaged-')) throw Error('Unexpected temporary profile cleanup directory')
  await fs.rm(temporary, { recursive: true, force: true })
}
console.log(JSON.stringify({ status: report.status, executable, checks: report.checks.length, screenshots: report.screenshots.length, rendererErrors: report.rendererErrors.length, report: path.join(output, 'manifest.json') }))
if (failure) throw failure
