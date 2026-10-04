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
const output = path.join(project, '.artifacts/overnight-2026-10-04/round-2', phase)
const tempRoot = path.resolve(os.tmpdir()), temporary = await fs.mkdtemp(path.join(tempRoot, 'the-order-detail-workspace-')), profile = path.join(temporary, 'profile')
const date = '2026-10-04T00:00:00.000Z'
const names = ['合成长标题 · ' + '当星光再次照亮海边的小镇时我们仍记得夏日约定'.repeat(2), '合成未关联本地条目', '合成没有笔记的愿望']
const games = names.map((name, i) => ({ id: `detail-record-${i}`, name, nameIsCustom: true, metadata: i === 1 ? null : {
  id: String(983020 + i), title: name, originalTitle: name, aliases: [], coverUrl: `https://detail-workspace.invalid/cover-${i}.svg`, bannerUrl: null,
  description: '原创合成封面与虚构故事。' + '这段简介用来检查长文本换行和独立滚动。'.repeat(12), releaseDate: '2026-10-04', releaseDatePrecision: 'day', developers: ['合成制作室'], tags: ['合成测试'], ratings: [], characters: [], references: [], webUrl: null, contentRating: null, fetchedAt: date,
}, installations: i === 1 ? [{ id: 'detail-install', folderPath: path.join(project, '.artifacts/overnight-2026-10-04/fictional-not-scanned'), executablePath: null, arguments: ['--synthetic'] }] : [], preferredInstallationId: i === 1 ? 'detail-install' : null,
status: 'planned', favorite: i === 0, personalRating: i === 0 ? 0 : null, categories: ['合成分类'], customTags: ['合成标签'], notes: i === 2 ? '' : `已保存的合成笔记${i}`, sessions: [], totalPlaySeconds: 0, playCount: 0, addedAt: date, lastPlayedAt: null }))
const library = { format: 'kun-gal-manager', version: 1, exportedAt: date, games }
const conditions = [{ width: 1440, height: 940, zoom: 1 }, { width: 1000, height: 700, zoom: 1.5 }]
const hash = value => createHash('sha256').update(value).digest('hex')
const report = { phase, checkoutHead: execFileSync('git', ['rev-parse', 'HEAD'], { cwd: project, encoding: 'utf8' }).trim(), sourceDirty: !!execFileSync('git', ['status', '--porcelain'], { cwd: project, encoding: 'utf8' }).trim(), fixtureSha256: hash(JSON.stringify(library)), runtimeHashes: {}, sourceHashes: {}, screenshots: [], measurements: [], baselineIssues: [], checks: [], rendererErrors: [], syntheticOnly: true, formalProfileAccessed: false, realApiCalls: 0, realGameExecutions: 0 }
let application, page, expectedTitle = null
const state = () => page.evaluate(() => window.kunManager.getState())
const dialog = () => page.locator('.game-detail-modal')
const tab = label => page.locator('.detail-tabs').getByRole('button', { name: label, exact: true })
const field = label => page.locator('.manage-form .field-label').filter({ hasText: label }).locator('input,textarea,select')
async function openGame(index) { await page.getByRole('button', { name: names[index], exact: true }).click(); await dialog().waitFor(); await dialog().getByText(names[index], { exact: true }).first().waitFor(); expectedTitle = names[index] }
async function closeDetail() { await page.keyboard.press('Escape'); await dialog().waitFor({ state: 'hidden' }) }
const record = () => page.locator('.personal-record')
const recordScore = () => page.getByRole('textbox', { name: '记录我的评分', exact: true })
async function recordNotes() { const details = page.locator('.record-notes'); if (await details.getAttribute('open') === null) await details.locator('summary').click(); return page.getByRole('textbox', { name: '记录游玩笔记', exact: true }) }
async function saveRecord() { const button = record().getByRole('button', { name: '保存我的记录', exact: true }); await button.focus(); await button.press('Enter'); await page.waitForFunction(() => document.querySelector('.record-save')?.textContent.includes('保存我的记录')) }
async function waitGame(index, patch) { await page.waitForFunction(async ({ id, patch }) => { const game = (await window.kunManager.getState()).games.find(game => game.id === id); return Object.entries(patch).every(([key, value]) => game[key] === value) }, { id: games[index].id, patch }) }
async function armSave(mode) {
  await application.evaluate(({ ipcMain }, mode) => {
    const channel = 'kun-manager:updateGame', original = ipcMain._invokeHandlers.get(channel)
    if (typeof original !== 'function') throw Error('Expected original updateGame handler')
    let entered, release
    const enteredPromise = new Promise(resolve => { entered = resolve }), gate = new Promise(resolve => { release = resolve })
    globalThis.__recordGate = { enteredPromise, release, calls: [] }
    ipcMain.removeHandler(channel)
    ipcMain.handle(channel, async (event, ...args) => {
      globalThis.__recordGate.calls.push(args); entered(); let timer
      try {
        if (mode === 'fail') return { ok: false, error: '合成保存失败，请重试。' }
        await Promise.race([gate, new Promise((_, reject) => { timer = setTimeout(() => reject(Error('Record gate was not released')), 10000) })])
        return await original(event, ...args)
      } finally { clearTimeout(timer); ipcMain.removeHandler(channel); ipcMain.handle(channel, original) }
    })
  }, mode)
}
async function gateEntered() { await application.evaluate(async () => { let timer; try { await Promise.race([globalThis.__recordGate.enteredPromise, new Promise((_, reject) => { timer = setTimeout(() => reject(Error('Record invocation never arrived')), 3000) })]) } finally { clearTimeout(timer) } }) }
async function resize(c) {
  await application.evaluate(({ BrowserWindow }, value) => { const windows = BrowserWindow.getAllWindows(); if (windows.length !== 1) throw Error('Expected one application window'); const win = windows[0]; if (win.isMaximized()) win.unmaximize(); win.setContentSize(value.width, value.height); win.webContents.setZoomFactor(value.zoom) }, c)
  await page.waitForFunction(c => Math.abs(innerWidth - c.width / c.zoom) < 2 && Math.abs(innerHeight - c.height / c.zoom) < 2, c)
  await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))))
}
async function capture(label, c, target = '.detail-tabs') {
  // Close already-checked transient messages through their normal controls; do not hide DOM or alter results.
  await page.evaluate(() => document.querySelectorAll('[data-kun-toast-close]').forEach(button => button.click()));
  await page.waitForFunction(() => document.querySelectorAll('[data-kun-toast-close]').length === 0, null, { timeout: 3000 });
  await resize(c); if (target) await page.locator(target).first().scrollIntoViewIfNeeded()
  await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))))
  await page.waitForTimeout(100) // Let the native compositor present the measured scroll position.
  const measured = await page.evaluate(() => {
    const modal = document.querySelector('.game-detail-modal'), rect = el => el?.getBoundingClientRect().toJSON() ?? null
    const facts = document.querySelector('.detail-facts')
    return { width: innerWidth, height: innerHeight, dpr: devicePixelRatio, documentWidth: document.documentElement.scrollWidth, modal: rect(modal), modalWidth: modal?.clientWidth, modalScrollWidth: modal?.scrollWidth, detailLayout: rect(document.querySelector('.detail-layout')), cover: rect(document.querySelector('.detail-artwork')), tabs: rect(document.querySelector('.detail-tabs')), picker: rect(document.querySelector('.cover-picker')), factsVisible: !!facts && getComputedStyle(facts).display !== 'none', active: document.activeElement?.getAttribute('aria-label') ?? document.activeElement?.textContent?.trim().slice(0,60) }
  })
  report.measurements.push({ label, requested: c, actual: measured, expectedTitle })
  const issues = []
  if (measured.documentWidth > measured.width + 1) issues.push('document horizontal overflow')
  if (target !== '.settings-panel') {
    assert.ok(await dialog().isVisible(), 'The screenshot must really show the detail modal')
    assert.ok(measured.modal && measured.modal.width > 0 && measured.modal.top < measured.height && measured.modal.bottom > 0)
    if (!baseline && label === 'detail-overview') assert.equal(measured.factsVisible, true, 'Release date and developer must remain available in the narrow viewport')
    if (measured.modal.left < -1 || measured.modal.right > measured.width + 1 || measured.modalScrollWidth > measured.modalWidth + 1) issues.push('detail horizontal overflow')
  }
  if (issues.length) { if (baseline) report.baselineIssues.push({ label, issues }); else assert.fail(label + ': ' + issues.join(', ')) }
  const bytes = Buffer.from(await application.evaluate(async ({ BrowserWindow }) => { const windows = BrowserWindow.getAllWindows(); if (windows.length !== 1) throw Error('Expected one captured window'); return (await windows[0].webContents.capturePage()).toPNG().toString('base64') }), 'base64')
  assert.ok(Math.abs(bytes.readUInt32BE(16) - c.width * measured.dpr / c.zoom) < 4); assert.ok(Math.abs(bytes.readUInt32BE(20) - c.height * measured.dpr / c.zoom) < 4)
  const file = `${label}-${c.width}x${c.height}-zoom${c.zoom}.png`; await fs.writeFile(path.join(output, file), bytes)
  report.screenshots.push({ file, requested: c, actual: measured, target, expectedTitle, sha256: hash(bytes), pixelWidth: bytes.readUInt32BE(16), pixelHeight: bytes.readUInt32BE(20) })
}
try {
  await fs.mkdir(output, { recursive: true }); await fs.mkdir(profile); await fs.writeFile(path.join(profile, 'library.json'), JSON.stringify(library))
  const html = await fs.readFile(path.join(project, 'dist/index.html'), 'utf8')
  for (const file of ['dist/index.html', 'dist-electron/main.cjs', 'dist-electron/preload.cjs', ...[...html.matchAll(/(?:src|href)="([^"]+\.(?:js|css))"/g)].map(match => path.posix.normalize('dist/' + match[1].replace(/^\//, '')))]) report.runtimeHashes[file] = hash(await fs.readFile(path.join(project, file)))
  if (baseline) { const prior = JSON.parse(await fs.readFile(path.join(project, '.artifacts/overnight-2026-10-04/round-1/after/manifest.json'), 'utf8')); assert.deepEqual(report.runtimeHashes, prior.runtimeHashes); report.businessSourceCommit = '28b4b500d7e7492e92acc0055ee915a4c703112c' }
  else for (const file of ['src/App.vue', 'src/components/PersonalRecord.vue']) report.sourceHashes[file] = hash((await fs.readFile(path.join(project, file), 'utf8')).replace(/\r\n/g, '\n'))
  const env = { ...process.env, KGM_DATA_DIR: profile }; delete env.KGM_TEST_EXECUTABLE; delete env.KGM_DEV_URL
  application = await electron.launch({ executablePath: path.join(project, 'node_modules/electron/dist/electron.exe'), args: [project], env, timeout: 30000 })
  await application.evaluate(({ session }, root) => {
    const fs = process.getBuiltinModule('node:fs'), path = process.getBuiltinModule('node:path')
    session.defaultSession.protocol.handle('https', request => { const url = new URL(request.url); if (url.hostname !== 'detail-workspace.invalid' || !/^\/cover-[0-7]\.svg$/.test(url.pathname)) return new Response('Unavailable fixture', { status: 404 }); return new Response(fs.readFileSync(path.join(root, 'tests/ui-fixtures', path.basename(url.pathname))), { headers: { 'content-type': 'image/svg+xml' } }) })
    globalThis.fetch = () => { throw Error('No real catalogue requests permitted') }
  }, project)
  page = await application.firstWindow(); page.on('pageerror', error => report.rendererErrors.push(error.message)); await page.reload(); await page.emulateMedia({ reducedMotion: 'reduce' }); await page.locator('.library-card').first().waitFor(); assert.equal(path.resolve((await state()).dataPath), profile)
  for (const c of conditions) {
    await resize(c); await openGame(0); await capture('detail-overview', c); await tab('管理').click(); await capture('detail-manage', c, '.manage-form'); await tab('作品资料').click(); await page.getByRole('button', { name: '封面与展示', exact: true }).click(); await page.locator('.cover-picker').waitFor(); await capture('detail-cover-picker', c, '.cover-picker'); await closeDetail()
  }
  await page.getByRole('button', { name: '偏好设置', exact: true }).click(); await page.locator('.settings-panel').first().waitFor(); await capture('settings', conditions[1], '.settings-panel'); await page.getByRole('button', { name: '我的书架', exact: true }).click()
  if (!baseline) {
    const initial = await state()
    await openGame(0); await tab('管理').click(); await field('书架名称').fill('未保存的名称草稿'); await field('分类').fill('未保存的分类草稿'); await field('个人标签').fill('未保存的标签草稿'); await field('游玩笔记').fill('管理页输入的笔记')
    await tab('作品资料').click(); assert.equal(await (await recordNotes()).inputValue(), '管理页输入的笔记'); await page.getByRole('combobox', { name: '记录游玩状态', exact: true }).selectOption('completed'); await recordScore().fill('0'); await (await recordNotes()).fill('从作品资料页记下下一次的约定'); await saveRecord(); await waitGame(0, { status: 'completed', personalRating: 0, notes: '从作品资料页记下下一次的约定' })
    const saved = await state(), expected = initial.games.map(game => game.id === games[0].id ? { ...game, status: 'completed', personalRating: 0, notes: '从作品资料页记下下一次的约定' } : game)
    assert.deepEqual(saved.games, expected, 'A record save modifies only the three personal record fields')
    const disk = JSON.parse(await fs.readFile(path.join(profile, 'library.json'), 'utf8')); assert.deepEqual(disk.games, saved.games, 'Real IPC persistence matches the renderer snapshot')
    await tab('管理').click(); assert.equal(await field('书架名称').inputValue(), '未保存的名称草稿'); assert.equal(await field('分类').inputValue(), '未保存的分类草稿'); assert.equal(await field('个人标签').inputValue(), '未保存的标签草稿'); assert.equal(await field('游玩笔记').inputValue(), '从作品资料页记下下一次的约定')
    await tab('作品资料').click(); const beforeIncomplete = (await state()).games; await recordScore().fill(''); await recordScore().pressSequentially('1e'); report.checks.push({ incompleteNumberInput: await recordScore().evaluate(el => ({ value: el.value, badInput: el.validity.badInput })) }); await saveRecord(); report.checks.push({ ratingAfterIncompleteSave: (await state()).games.find(game => game.id === games[0].id).personalRating, previousRating: beforeIncomplete.find(game => game.id === games[0].id).personalRating }); await record().getByRole('alert').filter({ hasText: '0 到 10' }).waitFor({ timeout: 2000 }); assert.deepEqual((await state()).games, beforeIncomplete, 'Incomplete numeric input must not clear the saved rating'); assert.equal(await recordScore().inputValue(), '1e');
    await tab('作品资料').click(); await record().getByRole('button', { name: '清除个人评分', exact: true }).click(); await saveRecord(); await waitGame(0, { personalRating: null }); assert.equal(await recordScore().inputValue(), '')
    const beforeInvalid = (await state()).games; await recordScore().fill('11'); await saveRecord(); await record().getByRole('alert').filter({ hasText: '0 到 10' }).waitFor(); assert.deepEqual((await state()).games, beforeInvalid); await capture('record-invalid', conditions[1], '.personal-record')
    await recordScore().fill('7.5'); await (await recordNotes()).fill('保存失败时仍可重试的笔记'); const beforeFailure = (await state()).games; await armSave('fail'); await saveRecord(); await record().getByRole('alert').filter({ hasText: '合成保存失败' }).waitFor(); assert.deepEqual((await state()).games, beforeFailure); assert.equal(await (await recordNotes()).inputValue(), '保存失败时仍可重试的笔记'); await capture('record-save-failure', conditions[1], '.personal-record')
    await saveRecord(); await waitGame(0, { personalRating: 7.5, notes: '保存失败时仍可重试的笔记' }); await record().getByRole('alert').waitFor({ state: 'hidden' })
    await (await recordNotes()).fill('已经提交的笔记'); await armSave('hold'); await record().getByRole('button', { name: '保存我的记录', exact: true }).click(); await gateEntered(); assert.equal(await record().getByRole('button', { name: '正在保存…', exact: true }).isDisabled(), true); await capture('record-saving', conditions[1], '.personal-record')
    await (await recordNotes()).fill('保存期间继续输入的新笔记'); await recordScore().fill('9'); await recordScore().press('Enter'); assert.equal((await application.evaluate(() => globalThis.__recordGate.calls)).length, 1); await application.evaluate(() => globalThis.__recordGate.release()); await waitGame(0, { notes: '已经提交的笔记', personalRating: 7.5 }); await record().getByRole('button', { name: '保存我的记录', exact: true }).waitFor(); assert.equal(await (await recordNotes()).inputValue(), '保存期间继续输入的新笔记'); assert.equal(await recordScore().inputValue(), '9')
    await saveRecord(); await waitGame(0, { notes: '保存期间继续输入的新笔记', personalRating: 9 })
    await (await recordNotes()).fill('关闭详情前已提交的笔记'); await armSave('hold'); await record().getByRole('button', { name: '保存我的记录', exact: true }).click(); await gateEntered(); await closeDetail(); await openGame(1); await (await recordNotes()).fill('未关联作品的新草稿'); await application.evaluate(() => globalThis.__recordGate.release()); await waitGame(0, { notes: '关闭详情前已提交的笔记' }); assert.equal(await (await recordNotes()).inputValue(), '未关联作品的新草稿'); assert.equal((await state()).games.find(game => game.id === games[1].id).notes, initial.games[1].notes); await capture('record-unassociated', conditions[1], '.personal-record'); await closeDetail()
    await openGame(2); assert.equal(await (await recordNotes()).inputValue(), ''); await capture('record-empty-notes', conditions[1], '.personal-record'); await closeDetail()
    await openGame(0); const coverButton = page.getByRole('button', { name: '封面与展示', exact: true }); await coverButton.focus(); await coverButton.press('Enter'); const closePicker = page.getByRole('button', { name: '收起封面选择', exact: true }); await closePicker.waitFor(); assert.equal(await closePicker.evaluate(el => document.activeElement === el), true, 'Opening the picker focuses its first control'); await closePicker.press('Enter'); await page.locator('.cover-picker').waitFor({ state: 'hidden' }); assert.equal(await coverButton.evaluate(el => document.activeElement === el), true, 'Explicit picker closure returns focus to its source button')
    const beforeCover = (await state()).games; await coverButton.press('Enter'); await page.getByRole('combobox', { name: '封面展示', exact: true }).selectOption('plain'); const coverSave = page.getByRole('button', { name: '保存封面设置', exact: true }); await coverSave.focus(); await coverSave.press('Enter'); await waitGame(0, { nonPortraitCoverDisplay: 'plain' }); assert.deepEqual((await state()).games, beforeCover.map(game => game.id === games[0].id ? { ...game, nonPortraitCoverDisplay: 'plain' } : game), 'Offline display saving preserves all other record data')
    if (await page.locator('.cover-picker').count()) await closePicker.click()
    await tab('管理').click(); const beforeManageInvalid = (await state()).games; await field('我的评分').fill('1e'); await page.getByRole('button', { name: '保存书架信息', exact: true }).click(); await page.getByRole('alert').filter({ hasText: '0 到 10' }).waitFor(); assert.deepEqual((await state()).games, beforeManageInvalid); assert.equal(await field('我的评分').inputValue(), '1e'); await field('我的评分').fill('9');
    await tab('管理').click(); await field('书架名称').fill('用户明确保存的合成名称'); await field('分类').fill('用户明确保存的合成分类'); await page.getByRole('button', { name: '保存书架信息', exact: true }).click(); await waitGame(0, { name: '用户明确保存的合成名称' }); assert.equal((await state()).games[0].personalRating, 9); expectedTitle = '用户明确保存的合成名称'
    await tab('作品资料').click(); await page.evaluate(async settings => window.kunManager.saveSettings({ ...settings, theme: 'dark' }), initial.settings); await page.waitForFunction(() => document.documentElement.classList.contains('kun-dark-mode')); await capture('record-dark', conditions[1], '.personal-record')
    const finalGames = (await state()).games; await closeDetail(); await page.reload(); await page.getByRole('button', { name: '用户明确保存的合成名称', exact: true }).click(); await dialog().waitFor(); assert.equal(await recordScore().inputValue(), '9'); assert.equal(await (await recordNotes()).inputValue(), '关闭详情前已提交的笔记'); assert.deepEqual((await state()).games, finalGames); assert.deepEqual(JSON.parse(await fs.readFile(path.join(profile, 'library.json'), 'utf8')).games, finalGames)
    report.checks.push('Shared overview/management draft; real three-field updateGame IPC and disk readback; zero and null rating; invalid and incomplete ratings in both overview and management prevent writes; simulated IPC failure preserves draft and real retry persists; gated original IPC saves submitted values and preserves later input; cross-detail late save preserves other draft; unassociated and empty-note states; picker keyboard focus return and real offline display save; existing management save; dark narrow view; renderer reload and disk equality')
  }
  assert.deepEqual(report.rendererErrors, []); report.status = 'passed'
} catch (error) { report.status = 'failed'; report.error = error.stack; throw error }
finally { await fs.writeFile(path.join(output, 'manifest.json'), JSON.stringify(report, null, 2)); if (application) await application.close(); if (path.dirname(path.resolve(temporary)) !== tempRoot || !path.basename(temporary).startsWith('the-order-detail-workspace-')) throw Error('Unexpected cleanup path'); await fs.rm(temporary, { recursive: true, force: true }) }
console.log(JSON.stringify({ phase, status: report.status, screenshots: report.screenshots.length, baselineIssues: report.baselineIssues.length, rendererErrors: report.rendererErrors.length }))
