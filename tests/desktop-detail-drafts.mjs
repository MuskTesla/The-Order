import assert from 'node:assert/strict'
import { promises as fs } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { createHash } from 'node:crypto'
import { execFileSync } from 'node:child_process'
import { _electron as electron } from '../node_modules/playwright/index.mjs'

// Real desktop entry and restricted preload/IPC with synthetic fixtures. Only the
// bounded test bootstrap injects atomic rename failures; it does not replace IPC.
const project = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const root = path.join(project, '.artifacts/overnight-2026-10-04/round-7')
const profile = path.join(root, 'ui-profile'), marker = path.join(profile, '.synthetic-profile')
const baseline = process.argv.includes('--baseline'), phase = baseline ? 'before' : 'after', output = path.join(root, phase)
const hash = value => createHash('sha256').update(value).digest('hex')
const date = '2026-10-04T00:00:00.000Z'
const conditions = [{ width: 1440, height: 940, zoom: 1 }, { width: 1000, height: 700, zoom: 1.5 }]
const names = ['合成 A · ' + '未保存的约定与长标题'.repeat(5), '合成 B · 未关联的本地记录', '合成 C · 空笔记的愿望']
const games = names.map((name, i) => ({ id: 'draft-game-' + i, name, nameIsCustom: true, metadata: i === 1 ? null : {
  id: String(987100 + i), title: name, originalTitle: 'Synthetic edition ' + i, aliases: [], coverUrl: `https://detail-drafts.invalid/cover-${i}.svg`, bannerUrl: null,
  description: '虚构故事与 CC0 原创合成封面，用于草稿保护检查。' + '用于检查长文本换行及小窗口滚动。'.repeat(8), releaseDate: '2026-10-04', releaseDatePrecision: 'day', developers: ['合成制作室'], tags: ['合成测试标签'], ratings: [], characters: [], references: [], webUrl: null, contentRating: null, fetchedAt: date,
}, installations: i === 0 ? [0, 1].map(j => ({ id: 'draft-install-' + j, folderPath: path.join(profile, 'fictional-games', 'A安装' + j), executablePath: null, arguments: ['--saved-' + j] })) : i === 1 ? [{ id: 'draft-install-B', folderPath: path.join(profile, 'fictional-games/B'), executablePath: null, arguments: [] }] : [], preferredInstallationId: i === 0 ? 'draft-install-0' : i === 1 ? 'draft-install-B' : null,
status: 'planned', favorite: false, personalRating: i === 0 ? 0 : null, categories: ['已保存的合成分类'], customTags: ['已保存的合成标签'], notes: i === 2 ? '' : '已保存的合成笔记' + i, totalPlaySeconds: 0, playCount: 0, sessions: [], addedAt: date, lastPlayedAt: null }))
const libraryBytes = JSON.stringify({ format: 'kun-gal-manager', version: 1, exportedAt: date, games })
const drafts = { name: '未保存的名称草稿 · ' + '下一次游玩的约定'.repeat(5), categories: '未保存的分类草稿', tags: '未保存的标签草稿', notes: '未保存的游玩笔记草稿', args: '["--draft-argument"]' }
const report = { phase, capturedAt: new Date().toISOString(), checkoutHead: execFileSync('git', ['rev-parse', 'HEAD'], { cwd: project, encoding: 'utf8' }).trim(), sourceDirty: !!execFileSync('git', ['status', '--porcelain'], { cwd: project, encoding: 'utf8' }).trim(), fixtureSha256: hash(libraryBytes), draftConditions: drafts, runtimeHashes: {}, sourceHashes: {}, screenshots: [], checks: [], baselineIssues: [], rendererErrors: [], syntheticOnly: true, formalProfileAccessed: false, realApiCalls: 0, realGameExecutions: 0 }
let app, page
const state = () => page.evaluate(() => window.kunManager.getState())
const dialog = () => page.locator('.game-detail-modal')
const detailBackdrop = () => page.locator('[data-kun-overlay][data-placement]').filter({ has: dialog() })
const tab = label => page.locator('.detail-tabs').getByRole('button', { name: label, exact: true })
const field = label => page.locator('.manage-form .field-label').filter({ hasText: label }).locator('input,textarea,select')
const closeButton = () => dialog().getByRole('button', { name: /^(关闭|Close)$/ })
async function openGame(index = 0) { await page.getByRole('button', { name: names[index], exact: true }).click(); await dialog().waitFor(); await tab('管理').click() }
async function notes() { const details = page.locator('.record-notes'); if (await details.getAttribute('open') === null) await details.locator('summary').click(); return page.getByRole('textbox', { name: '记录游玩笔记', exact: true }) }
async function argumentsField() { const details = page.locator('.advanced-options'); if (await details.getAttribute('open') === null) await details.locator('summary').click(); return field('参数数组') }
async function fillDraft() { await field('书架名称').fill(drafts.name); await field('分类').fill(drafts.categories); await field('个人标签').fill(drafts.tags); await field('游玩笔记').fill(drafts.notes); await (await argumentsField()).fill(drafts.args) }
async function resize(c) { await app.evaluate(({ BrowserWindow }, c) => { const windows = BrowserWindow.getAllWindows(); assertOne(windows); const w = windows[0]; if (w.isMaximized()) w.unmaximize(); w.setContentSize(c.width, c.height); w.webContents.setZoomFactor(c.zoom); function assertOne(windows) { if (windows.length !== 1) throw Error('Expected one desktop window') } }, c); await page.waitForFunction(c => Math.abs(innerWidth - c.width / c.zoom) < 3 && Math.abs(innerHeight - c.height / c.zoom) < 3, c) }
async function capture(label, c, target = '.detail-tabs') {
  await resize(c); if (target) await page.locator(target).first().scrollIntoViewIfNeeded()
  await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))); await page.waitForTimeout(100)
  const actual = await page.evaluate(() => { const modal = document.querySelector('.game-detail-modal'), rect = el => el?.getBoundingClientRect().toJSON() ?? null; return { width: innerWidth, height: innerHeight, dpr: devicePixelRatio, scrollWidth: document.documentElement.scrollWidth, modal: rect(modal), modalWidth: modal?.clientWidth, modalScrollWidth: modal?.scrollWidth, draftStatus: rect(document.querySelector('.detail-draft-status')), draftText: document.querySelector('.detail-draft-status')?.textContent?.trim() ?? null } })
  assert.ok(await dialog().isVisible(), 'Actual detail must be visible for evidence')
  if (!baseline) { assert.ok(actual.scrollWidth <= actual.width + 2, 'No document horizontal overflow'); assert.ok(actual.modalScrollWidth <= actual.modalWidth + 2, 'No detail horizontal overflow') }
  const bytes = Buffer.from(await app.evaluate(async ({ BrowserWindow }) => { const windows = BrowserWindow.getAllWindows(); if (windows.length !== 1) throw Error('Expected one captured window'); return (await windows[0].webContents.capturePage()).toPNG().toString('base64') }), 'base64')
  const file = `${label}-${c.width}x${c.height}-zoom${c.zoom}.png`; await fs.writeFile(path.join(output, file), bytes)
  report.screenshots.push({ file, requested: c, actual, target, sha256: hash(bytes), pixelWidth: bytes.readUInt32BE(16), pixelHeight: bytes.readUInt32BE(20) })
}
async function waitGames(predicate, description) { for (let i = 0; i < 180; i++) { const s = await state(); if (predicate(s.games)) return s.games; await page.waitForTimeout(25) } throw Error('Timed out: ' + description) }
async function cancelDiscard() { await page.getByText('放弃未保存的修改？', { exact: true }).waitFor(); await page.getByRole('button', { name: '继续编辑', exact: true }).click(); await page.getByText('放弃未保存的修改？', { exact: true }).waitFor({ state: 'hidden' }); assert.ok(await dialog().isVisible()) }
async function discard() { await page.keyboard.press('Escape'); await page.getByText('放弃未保存的修改？', { exact: true }).waitFor(); await page.getByRole('button', { name: '放弃修改', exact: true }).click(); await dialog().waitFor({ state: 'hidden' }) }
async function fault(file) { await app.evaluate((_, file) => { globalThis.__settingsMatching.fail = file }, file) }
async function hold(file) { await app.evaluate((_, file) => { globalThis.__settingsMatching.hold = file; globalThis.__settingsMatching.held = false }, file) }
async function held() { await app.evaluate(async () => { for (let i = 0; i < 180; i++) { if (globalThis.__settingsMatching.held) return; await new Promise(r => setTimeout(r, 20)) } throw Error('Real atomic write did not reach bounded gate') }) }
try {
  console.log(JSON.stringify({ phase, stage: 'preparing explicit synthetic profile' }))
  await fs.mkdir(output, { recursive: true })
  if (await fs.stat(profile).catch(e => { if (e.code === 'ENOENT') return null; throw e })) {
    assert.equal(await fs.readFile(marker, 'utf8'), 'detail-drafts-synthetic-only'); assert.equal(path.resolve(profile), path.join(root, 'ui-profile')); await fs.rm(profile, { recursive: true })
  }
  await fs.mkdir(profile); await fs.writeFile(marker, 'detail-drafts-synthetic-only')
  for (const game of games) for (const installation of game.installations) await fs.mkdir(installation.folderPath, { recursive: true })
  await fs.writeFile(path.join(profile, 'library.json'), libraryBytes)
  const html = await fs.readFile(path.join(project, 'dist/index.html'), 'utf8')
  for (const file of ['dist/index.html', 'dist-electron/main.cjs', 'dist-electron/preload.cjs', ...[...html.matchAll(/(?:src|href)="([^"]+\.(?:js|css))"/g)].map(m => path.posix.normalize('dist/' + m[1].replace(/^\//, '')))]) report.runtimeHashes[file] = hash(await fs.readFile(path.join(project, file)))
  for (const file of ['src/App.vue', 'src/components/CoverPicker.vue', 'src/components/PersonalRecord.vue', ...baseline ? [] : ['src/detail-drafts.css', 'src/main.ts']]) report.sourceHashes[file] = hash((await fs.readFile(path.join(project, file), 'utf8')).replace(/\r\n/g, '\n'))
  report.fixtureAssets = {}
  for (const file of ['tests/ui-fixtures/LICENSE.txt', 'tests/ui-fixtures/cover-0.svg', 'tests/ui-fixtures/cover-1.svg', 'tests/ui-fixtures/cover-2.svg']) report.fixtureAssets[file] = hash(await fs.readFile(path.join(project, file)))
  if (baseline) report.businessSourceCommit = '1908c3123192ebb07fdf28125e1db650ab8cf474'
  console.log(JSON.stringify({ phase, stage: 'starting actual Electron and restricted IPC' }))
  const env = { ...process.env, KGM_DATA_DIR: profile }; delete env.KGM_DEV_URL; delete env.KGM_TEST_EXECUTABLE
  app = await electron.launch({ executablePath: path.join(project, 'node_modules/electron/dist/electron.exe'), args: [path.join(project, 'tests/desktop-detail-drafts-bootstrap.cjs')], env, timeout: 30000 })
  await app.evaluate(({ session }, root) => {
    const fs = process.getBuiltinModule('node:fs'), path = process.getBuiltinModule('node:path')
    session.defaultSession.protocol.handle('https', request => { const u = new URL(request.url); if (u.hostname !== 'detail-drafts.invalid' || !/^\/cover-[0-2]\.svg$/.test(u.pathname)) return new Response('Blocked synthetic-only network', { status: 404 }); return new Response(fs.readFileSync(path.join(root, 'tests/ui-fixtures', path.basename(u.pathname))), { headers: { 'content-type': 'image/svg+xml' } }) })
    globalThis.fetch = () => { throw Error('Real API disabled for isolated draft test') }
  }, project)
  page = await app.firstWindow(); page.on('pageerror', error => report.rendererErrors.push(error.message)); await page.reload(); await page.emulateMedia({ reducedMotion: 'reduce' }); page.setDefaultTimeout(6000)
  await page.locator('.library-card').first().waitFor(); assert.equal(path.resolve((await state()).dataPath), profile)
  for (const c of conditions) {
    await resize(c); await openGame(); await fillDraft(); await capture('detail-draft-manage', c)
    if (baseline) {
      await page.keyboard.press('Escape'); await dialog().waitFor({ state: 'hidden' }); await openGame()
      assert.equal(await field('书架名称').inputValue(), games[0].name); assert.equal(await field('分类').inputValue(), games[0].categories.join('，')); assert.equal(await field('个人标签').inputValue(), games[0].customTags.join('，')); assert.equal(await field('游玩笔记').inputValue(), games[0].notes); assert.equal(await (await argumentsField()).inputValue(), JSON.stringify(games[0].installations[0].arguments))
      report.baselineIssues.push({ condition: c, issue: 'Escape closes dirty detail without confirmation; reopening discards unsaved name/categories/tags/notes/arguments' })
      await page.keyboard.press('Escape'); await dialog().waitFor({ state: 'hidden' })
    } else {
      await page.locator('.detail-draft-status').filter({ hasText: '未保存' }).waitFor()
      await closeButton().click(); await cancelDiscard(); assert.equal(await field('游玩笔记').inputValue(), drafts.notes)
      await detailBackdrop().click({ position: { x: 2, y: 2 } }); await cancelDiscard(); assert.equal(await (await argumentsField()).inputValue(), drafts.args)
      await page.keyboard.press('Escape'); await cancelDiscard(); assert.equal(await field('书架名称').inputValue(), drafts.name)
      await tab('作品资料').click(); assert.equal(await (await notes()).inputValue(), drafts.notes); await page.locator('.detail-tags').getByRole('button', { name: '合成测试标签', exact: true }).click(); await cancelDiscard(); assert.equal(await (await notes()).inputValue(), drafts.notes)
      await tab('管理').click(); await discard()
    }
  }
  report.checks.push('Same synthetic fixture and identical drafts at 1440×940/100% and 1000×700/150%; native capturePage images; baseline loss actually reproduced or guarded Close/outside/Escape/tag cancellation checked')
  if (!baseline) {
    // Additional stateful checks are below; they use real persistence, not API mocks.
    await openGame(); await fillDraft(); await tab('作品资料').click(); await page.locator('.personal-record').getByRole('button', { name: '保存我的记录', exact: true }).click(); await waitGames(g => g[0].notes === drafts.notes, 'personal record saved')
    await tab('管理').click(); assert.equal(await field('书架名称').inputValue(), drafts.name); assert.equal(await field('分类').inputValue(), drafts.categories); assert.equal(await (await argumentsField()).inputValue(), drafts.args); await page.locator('.detail-draft-status').filter({ hasText: '未保存' }).waitFor()
    const before = (await state()).games; await fault('library.json'); await page.getByRole('button', { name: '保存书架信息', exact: true }).click(); await page.locator('.detail-save-error').waitFor(); assert.deepEqual((await state()).games, before); assert.equal(await field('书架名称').inputValue(), drafts.name); await tab('作品资料').click(); await tab('管理').click(); await page.locator('.detail-save-error').waitFor(); await capture('detail-save-failure', conditions[1], '.detail-save-error')
    await page.getByRole('button', { name: '保存书架信息', exact: true }).click(); await waitGames(g => g[0].name === drafts.name && g[0].categories[0] === drafts.categories, 'management retry saved'); await page.locator('.detail-save-error').waitFor({ state: 'hidden' }); await page.locator('.detail-draft-status').filter({ hasText: '未保存' }).waitFor()
    report.checks.push('Personal save persists only its group, leaving name/categories/tags/arguments drafts dirty; bounded library rename failure keeps actual persisted state and draft; management retry succeeds and preserves argument draft')
    assert.equal(await (await argumentsField()).inputValue(), drafts.args, 'Reopening advanced settings after tab switches keeps the argument draft')
    await fault('library.json'); await page.getByRole('button', { name: '保存参数', exact: true }).click(); await page.locator('.arguments-save-error').waitFor(); assert.equal(await (await argumentsField()).inputValue(), drafts.args); assert.deepEqual((await state()).games[0].installations[0].arguments, ['--saved-0']); await capture('arguments-save-failure', conditions[1], '.arguments-save-error')
    await page.getByRole('button', { name: '保存参数', exact: true }).click(); await waitGames(g => g[0].installations[0].arguments[0] === '--draft-argument', 'arguments retry saved'); await page.locator('.arguments-save-error').waitFor({ state: 'hidden' })
    await (await argumentsField()).fill('["--new-unsaved-arguments"]'); await page.locator('.installation-row').nth(1).getByRole('button', { name: '设为默认', exact: true }).click(); await cancelDiscard(); assert.equal(await (await argumentsField()).inputValue(), '["--new-unsaved-arguments"]'); assert.equal((await state()).games[0].preferredInstallationId, 'draft-install-0')
    report.checks.push('Parameter save failure persists and retry succeeds through real IPC; canceling default-installation switch preserves unsaved arguments and previous preferred installation')
    const beforeRemoveCancel = (await state()).games
    await field('游玩笔记').fill('移除确认取消时应保留的隔离草稿'); await page.getByRole('button', { name: '从书架移除这部游戏', exact: true }).click(); await page.getByText('从书架移除？', { exact: true }).waitFor(); await page.keyboard.press('Escape'); await page.getByText('从书架移除？', { exact: true }).waitFor({ state: 'hidden' }); assert.ok(await dialog().isVisible()); assert.equal(await page.getByText('放弃未保存的修改？', { exact: true }).count(), 0); assert.equal(await field('游玩笔记').inputValue(), '移除确认取消时应保留的隔离草稿'); assert.deepEqual((await state()).games, beforeRemoveCancel)
    report.checks.push('Escape cancels the nested real removal alert without closing dirty detail, opening a discard alert, losing the note draft or removing any record')
    await field('游玩笔记').fill('真实写盘被有界暂停时的已提交笔记'); await hold('library.json.bak'); await page.getByRole('button', { name: '保存书架信息', exact: true }).click(); await held(); await page.keyboard.press('Escape'); assert.ok(await dialog().isVisible()); if (await closeButton().count() && !await closeButton().isDisabled()) await closeButton().click(); assert.ok(await dialog().isVisible()); assert.equal(await page.getByText('放弃未保存的修改？', { exact: true }).count(), 0); await detailBackdrop().click({ position: { x: 2, y: 2 } }); assert.ok(await dialog().isVisible()); await capture('detail-saving', conditions[1], '.detail-draft-status')
    await app.evaluate(() => globalThis.__settingsMatching.release()); await waitGames(g => g[0].notes === '真实写盘被有界暂停时的已提交笔记', 'gated management save'); await page.locator('.detail-draft-status').filter({ hasText: '未保存' }).waitFor()
    report.checks.push('Real library atomic write held at backup rename; Escape cannot close while saving; release completes original persistence and leaves unsaved arguments dirty')
    await tab('作品资料').click(); await page.getByRole('button', { name: '封面与展示', exact: true }).click(); await page.locator('.cover-picker').waitFor(); await page.getByRole('combobox', { name: '封面展示', exact: true }).selectOption('plain'); await page.getByRole('button', { name: '收起封面选择', exact: true }).click(); await cancelDiscard(); await tab('管理').click(); await page.locator('.cover-picker').waitFor({ state: 'hidden' }); await tab('作品资料').click(); await page.locator('.cover-picker').waitFor(); assert.equal(await page.getByRole('combobox', { name: '封面展示', exact: true }).inputValue(), 'plain')
    await page.keyboard.press('Escape'); await cancelDiscard(); assert.equal(await page.getByRole('combobox', { name: '封面展示', exact: true }).inputValue(), 'plain'); await capture('cover-draft-retained', conditions[1], '.cover-picker')
    report.checks.push('Cover display draft survives switching detail tabs, canceled picker closure and canceled detail leave; no remote artwork or real API request')
    const settings = (await state()).settings; await page.evaluate(async settings => window.kunManager.saveSettings({ ...settings, theme: 'dark' }), settings); await page.waitForFunction(() => document.documentElement.classList.contains('kun-dark-mode')); await tab('管理').click(); await capture('detail-draft-dark', conditions[1], '.detail-draft-status')
    // Keep the original capture with real notifications; also record the same
    // page after success messages expire normally. Never remove or hide them.
    for (const text of ['个人记录已保存。', '书架信息已保存，启动参数和封面按各自按钮保存。', '启动参数已保存。']) {
      await page.waitForFunction(text => ![...document.querySelectorAll('*')].some(element => element.textContent?.trim() === text && element.getClientRects().length > 0 && getComputedStyle(element).visibility !== 'hidden'), text, { timeout: 15000 })
    }
    await capture('detail-draft-dark-clear', conditions[1], '.detail-draft-status')
    await discard(); await page.getByRole('button', { name: drafts.name, exact: true }).click(); await dialog().waitFor(); await tab('管理').click(); assert.equal(await (await argumentsField()).inputValue(), drafts.args); assert.equal((await state()).games[0].nonPortraitCoverDisplay, undefined); await page.keyboard.press('Escape'); await dialog().waitFor({ state: 'hidden' })
    // The write is real and acknowledged; only subsequent state replies fail.
    // Read the isolated JSON directly while the bridge is intentionally failing.
    await page.getByRole('button', { name: drafts.name, exact: true }).click(); await dialog().waitFor(); await tab('管理').click()
    const acknowledgedNote = '原始 IPC 已保存、读取失败后重开仍须保留的合成笔记'
    await field('游玩笔记').fill(acknowledgedNote)
    const beforeReadFault = await app.evaluate(() => { const c = globalThis.__detailDrafts; if (!c) throw Error('Expected scoped state fault control'); c.failReads = true; return { stateReads: c.stateReads, gameWrites: c.gameWrites } })
    await page.getByRole('button', { name: '保存书架信息', exact: true }).click(); await page.locator('.detail-save-error').waitFor(); await page.locator('.detail-draft-status').filter({ hasText: '书架状态需要重新读取' }).waitFor()
    const actualDisk = JSON.parse(await fs.readFile(path.join(profile, 'library.json'), 'utf8')); assert.equal(actualDisk.games[0].notes, acknowledgedNote, 'Original real updateGame persisted the acknowledged note before the state reply failed')
    const afterReadFault = await app.evaluate(() => ({ stateReads: globalThis.__detailDrafts.stateReads, gameWrites: globalThis.__detailDrafts.gameWrites }))
    assert.equal(afterReadFault.gameWrites, beforeReadFault.gameWrites + 1, 'Exactly one original updateGame invocation for the acknowledged save')
    await page.keyboard.press('Escape'); await page.locator('.detail-save-error').filter({ hasText: '书架仍未能读取' }).waitFor(); assert.ok(await dialog().isVisible()); assert.equal(await field('游玩笔记').inputValue(), acknowledgedNote); assert.equal(await page.getByText('放弃未保存的修改？', { exact: true }).count(), 0)
    const afterFailedClose = await app.evaluate(() => ({ stateReads: globalThis.__detailDrafts.stateReads, gameWrites: globalThis.__detailDrafts.gameWrites }))
    assert.equal(afterFailedClose.gameWrites, afterReadFault.gameWrites, 'Failed close recovery only reads, never repeats the acknowledged write'); assert.ok(afterFailedClose.stateReads > afterReadFault.stateReads)
    await capture('detail-read-failure', conditions[1], '.detail-draft-status')
    await app.evaluate(() => { globalThis.__detailDrafts.failReads = false }); await page.keyboard.press('Escape'); await dialog().waitFor({ state: 'hidden' })
    const afterRecoveredClose = await app.evaluate(() => ({ stateReads: globalThis.__detailDrafts.stateReads, gameWrites: globalThis.__detailDrafts.gameWrites }))
    assert.equal(afterRecoveredClose.gameWrites, afterReadFault.gameWrites, 'Recovered close only reads latest state and performs no duplicate write'); assert.ok(afterRecoveredClose.stateReads > afterFailedClose.stateReads)
    await page.getByRole('button', { name: drafts.name, exact: true }).click(); await dialog().waitFor(); await tab('管理').click(); assert.equal(await field('游玩笔记').inputValue(), acknowledgedNote, 'Reopening initializes from the newly read persisted note'); await page.keyboard.press('Escape'); await dialog().waitFor({ state: 'hidden' })
    report.stateReadFault = { before: beforeReadFault, afterSave: afterReadFault, afterFailedClose, afterRecoveredClose, acknowledgedNote }
    report.checks.push('Actual restricted updateGame IPC acknowledged and isolated library.json saved before scoped getState reply failure; Escape keeps detail/input while read fails, never writes twice; successful read-only close and reopen show the saved note')
    await openGame(1); assert.equal(await field('游玩笔记').inputValue(), games[1].notes); await field('游玩笔记').fill('B 的隔离草稿'); await page.keyboard.press('Escape'); await cancelDiscard(); await discard(); await openGame(2); assert.equal(await field('游玩笔记').inputValue(), ''); await page.keyboard.press('Escape'); await dialog().waitFor({ state: 'hidden' })
    const final = (await state()).games; assert.deepEqual(JSON.parse(await fs.readFile(path.join(profile, 'library.json'), 'utf8')).games, final); assert.deepEqual(final.slice(1), games.slice(1))
    report.checks.push('Dark narrow screenshot; confirmed discard reopens persisted arguments and cover; unrelated local/empty virtual records unchanged; final disk equals restricted bridge snapshot')
  }
  assert.deepEqual(report.rendererErrors, []); report.status = 'passed'
} catch (error) { report.status = 'failed'; report.error = error.stack; throw error }
finally {
  await fs.mkdir(output, { recursive: true }); await fs.writeFile(path.join(output, 'manifest.json'), JSON.stringify(report, null, 2))
  if (app) { await app.evaluate(() => { const c = globalThis.__settingsMatching; if (c) { c.hold = null; c.fail = null; c.release?.() }; if (globalThis.__detailDrafts) globalThis.__detailDrafts.failReads = false }).catch(() => {}); await app.close() }
}
console.log(JSON.stringify({ phase, status: report.status, screenshots: report.screenshots.length, checks: report.checks.length, baselineIssues: report.baselineIssues.length, rendererErrors: report.rendererErrors.length }))
