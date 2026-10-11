import assert from 'node:assert/strict'
import { promises as fs } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { createHash } from 'node:crypto'
import { execFileSync } from 'node:child_process'
import { _electron as electron } from '../node_modules/playwright/index.mjs'

const project = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const root = path.join(project, '.artifacts/overnight-2026-10-04/round-6')
const profile = path.join(root, 'ui-profile'), marker = path.join(profile, '.synthetic-profile')
const baseline = process.argv.includes('--baseline'), phase = baseline ? 'before' : 'after', output = path.join(root, phase)
const hash = value => createHash('sha256').update(value).digest('hex')
console.log(JSON.stringify({ phase, stage: 'preparing isolated profile' }))
await fs.mkdir(output, { recursive: true })
if (await fs.stat(profile).catch(e => { if (e.code === 'ENOENT') return null; throw e })) {
  assert.equal(await fs.readFile(marker, 'utf8'), 'review-workspace-synthetic-only')
  assert.equal(path.resolve(profile), path.join(root, 'ui-profile'))
  await fs.rm(profile, { recursive: true })
}
await fs.mkdir(profile); await fs.writeFile(marker, 'review-workspace-synthetic-only')
const date = '2026-10-04T00:00:00.000Z'
const metadata = (id, title) => ({ id, title, originalTitle: title + ' Original Edition', aliases: [], coverUrl: null, bannerUrl: null, description: 'Synthetic description; no real game data.', releaseDate: '2019-03-29', releaseDatePrecision: 'day', developers: ['模拟工作室'], tags: [], ratings: [], characters: [], references: [], webUrl: null, contentRating: null, fetchedAt: date })
const games = Array.from({ length: 18 }, (_, i) => {
  const id = 'review-game-' + (i + 1), folder = path.join(profile, 'fictional-games', `模拟${i + 1} · 很长的游戏目录与版本说明`, 'Windows 日本語 Edition Long Path')
  return { id, name: `模拟核对 ${String(i + 1).padStart(2, '0')} · 很长的本地作品名称与版本备注`, nameIsCustom: true, metadata: i % 3 === 2 ? null : metadata(String(900 + i), `当前关联 ${i + 1} · 另一部作品`), installations: [{ id: 'install-' + i, folderPath: folder, executablePath: null, arguments: [] }], preferredInstallationId: 'install-' + i, status: 'paused', personalRating: 0, favorite: true, categories: ['模拟保留分类'], customTags: ['模拟保留标签'], notes: 'Synthetic personal notes must remain intact', totalPlaySeconds: 600, playCount: 1, sessions: [{ id: 'session-' + i, startedAt: date, endedAt: date, seconds: 600 }], addedAt: date, lastPlayedAt: date }
})
// This mirrors the documented source snapshot format, not private runtime guards.
// Source-less legacy reviews and an explicitly stale source are deliberate fixtures.
const reviews = games.map((game, i) => ({ gameId: game.id, name: game.name, folderPath: game.installations[0].folderPath, reason: '多个作品标题相近，章节、发行版本或独立编号仍需用户对照。', reviewId: 'synthetic-review-' + i, options: i === 2 ? [] : Array.from({ length: 4 }, (_, j) => ({ work: metadata(String(10000 + i * 10 + j), `候选${i + 1}-${j + 1} · ${j ? '附加章节与重制版本' : '本篇'} · 长作品名`), confidence: 0.8 - j * 0.1, reason: j ? '标题近似，但版本信息需要核实。' : '标题线索较接近，尚未自动确认。' })), ...(i === 1 ? { source: { installationId: game.installations[0].id, folderPath: game.installations[0].folderPath, executablePath: null, fingerprint: '0'.repeat(64) } } : {}) }))
for (const game of games) await fs.mkdir(game.installations[0].folderPath, { recursive: true })
const libraryBytes = JSON.stringify({ format: 'kun-gal-manager', version: 1, exportedAt: date, games })
const matchingBytes = JSON.stringify({ id: 'synthetic-completed-job', status: 'completed', mode: 'reidentify', total: 18, processed: 18, matched: 0, unmatched: 18, failed: 0, currentName: null, reviews })
await fs.writeFile(path.join(profile, 'library.json'), libraryBytes); await fs.writeFile(path.join(profile, 'metadata-matching.json'), matchingBytes)
const report = { phase, capturedAt: new Date().toISOString(), checkoutHead: execFileSync('git', ['rev-parse', 'HEAD'], { cwd: project, encoding: 'utf8' }).trim(), sourceDirty: !!execFileSync('git', ['status', '--porcelain'], { cwd: project, encoding: 'utf8' }).trim(), fixtureSha256: hash(libraryBytes + matchingBytes), synthetic: true, formalProfileAccessed: false, realApiCalls: 0, checks: [], screenshots: [], rendererErrors: [] }
const conditions = [{ width: 1440, height: 940, zoom: 1 }, { width: 1000, height: 700, zoom: 1.5 }]
let app, page
const state = () => page.evaluate(() => window.kunManager.getState())
async function capture(label, c) {
  await app.evaluate(({ BrowserWindow }, c) => { const w = BrowserWindow.getAllWindows()[0]; w.setContentSize(c.width, c.height); w.webContents.setZoomFactor(c.zoom) }, c)
  await page.waitForFunction(c => Math.abs(innerWidth - c.width / c.zoom) < 3 && Math.abs(innerHeight - c.height / c.zoom) < 3, c)
  await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))); await page.waitForTimeout(150)
  const actual = await page.evaluate(() => ({ width: innerWidth, height: innerHeight, dpr: devicePixelRatio, scrollWidth: document.documentElement.scrollWidth, dialog: document.querySelector('.matching-review-modal')?.getBoundingClientRect().toJSON(), footer: document.querySelector('.review-footer')?.getBoundingClientRect().toJSON(), option: document.querySelector('.review-option')?.getBoundingClientRect().toJSON() }))
  const bytes = Buffer.from(await app.evaluate(async ({ BrowserWindow }) => (await BrowserWindow.getAllWindows()[0].webContents.capturePage()).toPNG().toString('base64')), 'base64')
  const file = `${label}-${c.width}x${c.height}-zoom${c.zoom}.png`; await fs.writeFile(path.join(output, file), bytes)
  report.screenshots.push({ file, requested: c, actual, pixels: [bytes.readUInt32BE(16), bytes.readUInt32BE(20)], sha256: hash(bytes) })
  if (!baseline) { assert.ok(actual.scrollWidth <= actual.width + 2, 'No horizontal overflow'); assert.ok(actual.footer && actual.footer.top >= 0 && actual.footer.bottom <= actual.height + 1, 'Decision footer visible') }
}
try {
  console.log(JSON.stringify({ phase, stage: 'starting actual Electron' }))
  app = await electron.launch({ executablePath: path.join(project, 'node_modules/electron/dist/electron.exe'), args: [path.join(project, 'tests/desktop-settings-matching-bootstrap.cjs')], env: { ...process.env, KGM_DATA_DIR: profile, KGM_DEV_URL: '', KGM_TEST_EXECUTABLE: '' }, timeout: 30000 })
  await app.evaluate(({ session }) => { session.defaultSession.webRequest.onBeforeRequest({ urls: ['http://*/*', 'https://*/*'] }, (_, cb) => cb({ cancel: true })) })
  page = await app.firstWindow(); page.on('pageerror', error => report.rendererErrors.push(error.message)); await page.waitForFunction(() => window.kunManager !== undefined)
  await page.getByRole('button', { name: '资料整理', exact: true }).click(); await page.locator('.maintenance-review-link').click()
  await page.locator('.matching-review-modal').waitFor()
  assert.equal((await state()).matching.reviews.length, 18)
  for (const c of conditions) await capture('review-initial', c)
  report.checks.push('Initial queue: 18 synthetic reviews, long names/paths, existing personal records, one stale source and one no-candidate item')
  if (!baseline) {
    console.log(JSON.stringify({ phase, stage: 'checking interactions and bounded persistence failures' }))
    const select = () => page.getByRole('combobox', { name: '待核对项目', exact: true })
    const query = () => page.getByRole('textbox', { name: '筛选待核对项目', exact: true })
    const confirm = () => page.getByRole('button', { name: '确认关联', exact: true })
    const firstOption = () => page.locator('.review-option').first()
    const waitState = async (predicate, label) => { for (let i = 0; i < 160; i++) { const s = await state(); if (predicate(s)) return s; await page.waitForTimeout(30) } throw Error('Timed out: ' + label) }
    assert.equal(await confirm().isDisabled(), true, 'No automatic candidate selection')
    await firstOption().focus(); await page.keyboard.press('Space')
    await page.locator('.match-review-choice').getByText(reviews[0].options[0].work.title, { exact: true }).waitFor()
    assert.equal(await firstOption().getAttribute('aria-pressed'), 'true')
    await capture('review-selected', conditions[1])
    await query().fill('模拟核对 04')
    await page.locator('.match-review-hidden').getByText(/当前项目在筛选外/).waitFor()
    assert.equal(await select().inputValue(), games[0].id, 'Filtering never silently changes the current decision')
    assert.equal(await firstOption().getAttribute('aria-pressed'), 'true', 'Filtering retains the same decision')
    await query().fill('nonexistent-review-query')
    await page.getByText('没有符合筛选的项目。', { exact: false }).waitFor()
    await capture('review-no-search-results', conditions[1])
    await page.getByRole('button', { name: '清除筛选', exact: true }).click()
    await select().selectOption(games[1].id)
    assert.equal(await confirm().isDisabled(), true)
    assert.equal(await page.locator('.review-option:disabled').count(), 4)
    await page.getByText('安装、名称或关联已改变，这条建议已过时。请手动搜索，或结束本次建议。', { exact: true }).waitFor()
    assert.equal(await page.getByRole('button', { name: '保留当前关联', exact: true }).isDisabled(), false)
    await capture('review-stale', conditions[1])
    await select().selectOption(games[2].id)
    await page.getByText('暂时没有合适的候选。', { exact: true }).waitFor()
    await capture('review-no-candidates', conditions[1])
    await select().selectOption(games[0].id)
    const source = page.locator('.match-review-source')
    await source.locator('summary').focus(); await page.keyboard.press('Enter')
    assert.equal(await source.getAttribute('open'), '')
    assert.ok((await source.innerText()).includes(games[0].installations[0].folderPath))
    await page.keyboard.press('Enter')
    report.checks.push('Keyboard candidate selection/full paths; filtered/zero-result queue retains exact current decision; stale confirm disabled, keep enabled; no-candidate state')
    await app.evaluate(({ }, works) => {
      globalThis.__reviewTransport = { requests: [], failDetail: true }
      const fixture = globalThis.__reviewTransport
      globalThis.fetch = async value => {
        const u = new URL(String(value)); if (u.origin !== 'https://api.nextmoe.dev') throw Error('Unexpected synthetic origin')
        fixture.requests.push(u.pathname)
        const id = u.pathname.split('/').pop(), work = works.find(w => w.id === id)
        if (!work) throw Error('Unexpected synthetic work')
        if (fixture.failDetail) return new Response(JSON.stringify({ error: { message: 'Synthetic unavailable', code: 'FORBIDDEN' } }), { status: 403, headers: { 'content-type': 'application/json' } })
        return new Response(JSON.stringify({ object: 'work', id, display_name: work.title, intros: [{ lang: 'zh-Hans', value: 'Synthetic full candidate description.' }], companies: [{ object: 'company', id: '1', display_name: '模拟工作室', attribution_role: 'developer' }], refs: [], tags: [], characters: [], ratings: [], titles: [{ title: work.title, title_kind: 'official', is_machine: false, lang: 'zh-Hans' }] }), { headers: { 'content-type': 'application/json' } })
      }
    }, reviews.flatMap(r => r.options.map(o => o.work)))
    await page.evaluate(async () => window.kunManager.saveSettings({ ...(await window.kunManager.getState()).settings, apiKey: 'synthetic-review-workspace-key', readExecutableMetadata: false }))
    await page.locator('.review-detail-link').first().click()
    await page.locator('.review-detail-modal').getByRole('alert').waitFor()
    await page.locator('.review-detail-modal').getByRole('button', { name: '关闭', exact: true }).click()
    assert.equal(await page.locator('.matching-review-modal').count(), 1)
    await app.evaluate(() => { globalThis.__reviewTransport.failDetail = false })
    await page.locator('.review-detail-link').first().click()
    await page.getByText('Synthetic full candidate description.', { exact: true }).waitFor()
    await page.locator('.review-detail-modal').getByRole('button', { name: '关闭', exact: true }).click()
    report.checks.push('Independent candidate detail failure and successful retry through actual IPC with synthetic API; parent decision and library unchanged')
    await select().selectOption(games[3].id); await firstOption().click()
    await app.evaluate(() => { globalThis.__settingsMatching.fail = 'library.json' })
    await confirm().click()
    await page.locator('.match-review-error').getByText(/操作未完整结束/).waitFor()
    assert.equal((await state()).games.find(g => g.id === games[3].id).metadata.id, games[3].metadata.id)
    assert.equal((await state()).matching.reviews.length, 18)
    await capture('review-write-failed', conditions[1])
    assert.equal(await confirm().isDisabled(), true, 'A refreshed identity clears the old selection before retry')
    await firstOption().click()
    report.checks.push('Actual library atomic rename failure retains association and review; persistent error remains, refreshed decision identity requires explicit candidate reselection')
    await app.evaluate(() => { globalThis.__settingsMatching.hold = 'library.json.bak'; globalThis.__settingsMatching.fail = 'metadata-matching.json' })
    await confirm().click()
    await page.waitForFunction(async () => (await window.kunManager.getState()).games.length === 18)
    for (let i = 0; i < 100 && !(await app.evaluate(() => globalThis.__settingsMatching.held)); i++) await page.waitForTimeout(20)
    assert.equal(await app.evaluate(() => globalThis.__settingsMatching.held), true)
    await page.keyboard.press('Escape')
    assert.equal(await page.locator('.matching-review-modal').count(), 1, 'Escape cannot dismiss an active decision')
    assert.equal(await query().isDisabled(), true); assert.equal(await select().isDisabled(), true)
    assert.equal(await page.locator('.review-detail-link').first().isDisabled(), true)
    assert.equal(await page.getByRole('button', { name: '稍后处理', exact: true }).isDisabled(), true)
    await capture('review-saving', conditions[1])
    await app.evaluate(() => globalThis.__settingsMatching.release())
    await page.locator('.match-review-error').getByText(/操作未完整结束/).waitFor()
    await waitState(s => s.games.find(g => g.id === games[3].id).metadata.id === reviews[3].options[0].work.id && s.matching.reviews.length === 18, 'partially completed decision remains truthful')
    await page.locator('.review-current-work').getByText(reviews[3].options[0].work.title, { exact: true }).waitFor()
    await capture('review-linked-queue-failed', conditions[1])
    assert.deepEqual(JSON.parse(await fs.readFile(path.join(profile, 'library.json'), 'utf8')).games.find(g => g.id === games[3].id).metadata.id, reviews[3].options[0].work.id)
    report.checks.push('Real library commit followed by matching-queue rename failure: actual new association displayed, suggestion retained, persistent partial-failure feedback; no automatic resubmission')
    await page.getByRole('button', { name: '保留当前关联', exact: true }).click()
    await waitState(s => s.matching.reviews.length === 17, 'keep partial committed association')
    await select().selectOption(games[0].id); await firstOption().click(); await confirm().click()
    await waitState(s => s.games.find(g => g.id === games[0].id).metadata.id === reviews[0].options[0].work.id && s.matching.reviews.length === 16, 'positive decision')
    const saved = await state()
    for (const game of saved.games) { const { metadata: _, ...personal } = game, { metadata: __, ...original } = games.find(g => g.id === game.id); assert.deepEqual(personal, original) }
    await page.evaluate(async () => window.kunManager.saveSettings({ ...(await window.kunManager.getState()).settings, theme: 'dark' }))
    await select().selectOption(games[4].id)
    // Capture the stable dark workspace after the real success feedback expires.
    await page.getByText('本次建议已结束，书架关联和个人记录未改变。', { exact: true }).waitFor({ state: 'hidden' })
    await page.getByText('已关联你确认的作品资料。', { exact: true }).waitFor({ state: 'hidden' })
    await capture('review-dark', conditions[1])
    await page.getByRole('button', { name: '手动搜索', exact: true }).click()
    await page.getByRole('dialog', { name: '选择要关联的作品', exact: true }).waitFor()
    await page.locator('.matching-review-modal').waitFor({ state: 'hidden' })
    await page.getByRole('dialog').getByRole('button', { name: '关闭', exact: true }).click()
    await page.getByRole('button', { name: '资料整理', exact: true }).click(); await page.locator('.maintenance-review-link').click()
    for (const r of (await state()).matching.reviews) { const s = await state(), current = s.matching.reviews.find(x => x.reviewId === r.reviewId); if (current) await page.evaluate(r => window.kunManager.resolveMetadataMatch(r.gameId, null, r.reviewId, r.decisionFingerprint), current) }
    await page.getByText('待核对项目已处理完毕', { exact: true }).waitFor()
    await capture('review-finished', conditions[1])
    report.checks.push('Positive association and keep-current dismiss preserve every personal field; dark/manual-search and exhausted queue checked; no game program executed')
    report.syntheticApiRequests = await app.evaluate(() => globalThis.__reviewTransport.requests)
  }
  assert.deepEqual(report.rendererErrors, [])
  report.runtime = {}
  for (const relative of ['dist/index.html', 'dist-electron/main.cjs', 'dist-electron/preload.cjs', ...(await fs.readdir(path.join(project, 'dist/assets'))).filter(n => /\.(js|css)$/.test(n)).map(n => 'dist/assets/' + n)]) report.runtime[relative] = hash(await fs.readFile(path.join(project, relative)))
  await fs.writeFile(path.join(output, 'manifest.json'), JSON.stringify(report, null, 2)); console.log(JSON.stringify({ phase, checks: report.checks, screenshots: report.screenshots.map(s => ({ file: s.file, actual: s.actual })) }))
} catch (error) { await fs.writeFile(path.join(output, 'last-failure.json'), JSON.stringify({ ...report, error: String(error) }, null, 2)); throw error }
finally { if (app) { await app.evaluate(() => { globalThis.__settingsMatching.hold = null; globalThis.__settingsMatching.fail = null; globalThis.__settingsMatching.release?.() }).catch(() => {}); await app.close() } }
