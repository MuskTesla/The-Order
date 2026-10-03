import assert from 'node:assert/strict'
import { createHash } from 'node:crypto'
import { createReadStream, promises as fs } from 'node:fs'
import { execFileSync } from 'node:child_process'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { _electron as electron } from '../node_modules/playwright/index.mjs'

// Exercises the current root Electron build and its actual preload/IPC/matcher.
// All games, persisted reviews and catalog responses are synthetic. Programs
// are inert text fixtures, and every network destination is intercepted.
const project = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const temporaryRoot = path.resolve(os.tmpdir())
const temporary = await fs.mkdtemp(path.join(temporaryRoot, 'the-order-iteration-review-'))
const profile = path.join(temporary, 'profile')
const artifacts = path.join(project, '.artifacts', 'iteration-01', 'flows')
await fs.mkdir(artifacts, { recursive: true })
const captures = []
const hashes = async files => Object.fromEntries(await Promise.all(files.map(async file => {
  const hash = createHash('sha256')
  for await (const bytes of createReadStream(path.join(project, file))) hash.update(bytes)
  return [file.replaceAll('\\', '/'), hash.digest('hex')]
})))
const sourceFiles = execFileSync('git', ['-C', project, 'ls-files', '--cached', '--others', '--exclude-standard', 'src', 'electron', 'package.json', 'package-lock.json', 'vite.config.ts', 'scripts/build-electron.mjs'], { encoding: 'utf8' }).trim().split(/\r?\n/).filter(Boolean)
const sourceHashes = await hashes(sourceFiles)
const runtimeFiles = ['node_modules/electron/dist/electron.exe', 'dist-electron/main.cjs', 'dist-electron/preload.cjs', 'dist/index.html',
  ...(await fs.readdir(path.join(project, 'dist', 'assets'))).filter(file => /\.(?:css|js)$/.test(file)).map(file => `dist/assets/${file}`)]
const runtimeHashes = await hashes(runtimeFiles)
const coverFixtures = await Promise.all(Array.from({ length: 8 }, (_, index) => fs.readFile(path.join(project, 'tests', 'ui-fixtures', `cover-${index}.svg`))))
const capture = async (page, file, scene) => {
  const target = path.join(artifacts, file)
  await page.screenshot({ path: target })
  const bytes = await fs.readFile(target)
  const dimensions = await page.evaluate(() => ({ cssWidth: innerWidth, cssHeight: innerHeight, deviceScaleFactor: devicePixelRatio }))
  captures.push({ file, scene, data: '合成游戏、合成安装、模拟 API；封面为 tests/ui-fixtures 的 CC0 素材。',
    capturedAt: new Date().toISOString(), timezone: 'Asia/Shanghai', ...dimensions, pixelWidth: bytes.readUInt32BE(16), pixelHeight: bytes.readUInt32BE(20),
    bytes: bytes.length, sha256: createHash('sha256').update(bytes).digest('hex') })
}
const fakeKey = 'test-iteration-review-fixture-only'
const date = '2026-10-03T00:00:00.000Z'
const titles = ['The Azure Envelope Memory Orchard', 'The Glass Observatory Letter Season', 'The Lantern Harbor Evening Pages', 'The Quiet Horizon Archive Voyage']
const coverUrl = id => `https://iteration-review-fixture.invalid/${id}.svg`
const metadata = (id, title, appId, description = `Saved fixture metadata ${id}`) => ({
  id, title, originalTitle: title, aliases: [], coverUrl: coverUrl(id), bannerUrl: null,
  description, releaseDate: '2026-01-01', releaseDatePrecision: 'day', developers: ['Synthetic Review Studio'],
  tags: ['测试资料'], ratings: [], characters: [], references: appId ? [{ source: 'steam', id: appId }] : [],
  webUrl: null, contentRating: null, fetchedAt: date,
})
const candidates = titles.map((title, index) => metadata(String((index + 1) * 100 + 1), title, String(490001 + index), `Synthetic complete candidate description ${(index + 1) * 100 + 1}`))
const games = titles.slice(0, 3).map((name, index) => {
  const folderPath = path.join(temporary, 'synthetic-games', name)
  const installations = [{ id: `fixture-installation-${index}`, folderPath, executablePath: path.join(folderPath, 'Game.exe'), arguments: ['--saved'] }]
  if (!index) {
    const alternate = path.join(temporary, 'synthetic-games', `${name} [汉化]`)
    installations.push({ id: 'fixture-preferred-second', folderPath: alternate, executablePath: path.join(alternate, 'Game.exe'), arguments: ['--alternate'] })
  }
  return {
    id: `fixture-review-${index}`, name, nameIsCustom: true, metadata: metadata(String(901 + index), `Stored current work ${index}`, String(499901 + index)),
    installations, preferredInstallationId: installations.at(-1).id, status: 'paused', personalRating: 0,
    favorite: true, categories: ['保留分类'], customTags: ['保留个人标签'], notes: `保存的个人笔记 ${index}`,
    totalPlaySeconds: 600, playCount: 1, sessions: [{ id: `fixture-session-${index}`, startedAt: '2026-10-02T12:00:00.000Z', endedAt: '2026-10-02T12:10:00.000Z', seconds: 600 }],
    addedAt: date, lastPlayedAt: '2026-10-02T12:10:00.000Z',
  }
})
const identity = game => createHash('sha256').update(JSON.stringify([
  game.name, game.nameIsCustom, game.metadata?.id ?? null, game.preferredInstallationId,
  game.installations.map(({ id, folderPath, executablePath }) => [id, folderPath, executablePath]),
])).digest('hex')
function review(game, candidate, legacy = false) {
  const selected = game.installations.find(item => item.id === game.preferredInstallationId)
  return {
    gameId: game.id, name: game.name, folderPath: selected.folderPath, reason: '合成评审建议：当前关联和个人记录保持到明确确认。',
    options: [{ work: candidate, confidence: 0.9, reason: '合成标题与独立来源编号匹配；仍须人工核对。' }],
    ...(legacy ? {} : { reviewId: `seed-review-${game.id}`, source: {
      installationId: selected.id, folderPath: selected.folderPath, executablePath: selected.executablePath, fingerprint: identity(game),
      installations: [...game.installations].sort((a, b) => Number(b.id === game.preferredInstallationId) - Number(a.id === game.preferredInstallationId))
        .map(({ id, folderPath, executablePath }) => ({ installationId: id, folderPath, executablePath })),
    } }),
  }
}
await fs.mkdir(profile, { recursive: true })
for (const [index, game] of games.entries()) for (const installation of game.installations) {
  await fs.mkdir(installation.folderPath, { recursive: true })
  await fs.writeFile(installation.executablePath, 'Inert iteration review fixture. Never execute.')
  await fs.writeFile(path.join(installation.folderPath, 'steam_appid.txt'), String(490001 + index))
}
const quickFolder = path.join(temporary, 'quick-import', titles[3])
await fs.mkdir(quickFolder, { recursive: true })
await fs.writeFile(path.join(quickFolder, 'Game.exe'), 'Inert iteration review fixture. Never execute.')
await fs.writeFile(path.join(quickFolder, 'steam_appid.txt'), '490004')
await fs.writeFile(path.join(profile, 'library.json'), JSON.stringify({ format: 'kun-gal-manager', version: 1, exportedAt: date, games }))
await fs.writeFile(path.join(profile, 'metadata-matching.json'), JSON.stringify({
  id: 'synthetic-seeded-review-job', status: 'completed', mode: 'reidentify', total: 3, processed: 3,
  matched: 0, unmatched: 3, failed: 0, currentName: null,
  reviews: games.map((game, index) => review(game, candidates[index], index === 1)),
}))
const works = candidates.map(candidate => ({
  object: 'work', id: candidate.id, display_name: candidate.title,
  refs: candidate.references.map(ref => ({ source: ref.source, external_id: ref.id })), cover: { url: candidate.coverUrl },
  intros: [{ lang: 'en', value: candidate.description }],
  companies: [{ object: 'company', id: '1', display_name: 'Synthetic Review Studio', attribution_role: 'developer' }],
  ratings: [], characters: [], tags: [], titles: [{ title: candidate.title, title_kind: 'official', is_machine: false, lang: 'en' }],
}))
const launchEnv = { ...process.env, KGM_DATA_DIR: profile }
delete launchEnv.KGM_TEST_EXECUTABLE
delete launchEnv.KGM_DEV_URL
const launchOptions = { executablePath: path.join(project, 'node_modules/electron/dist/electron.exe'), args: [project], env: launchEnv, timeout: 30000 }
let application
const rendererErrors = []
let requestCount = 0
const results = {}
const snapshot = page => page.evaluate(() => window.kunManager.getState())
const findGame = (state, id) => state.games.find(game => game.id === id)
const findReview = (state, id) => state.matching.reviews.find(item => item.gameId === id)
const personal = ({ metadata: _metadata, ...game }) => game
async function waitState(page, predicate, label) {
  const deadline = Date.now() + 15000
  while (true) {
    const state = await snapshot(page)
    if (predicate(state)) return state
    assert.ok(Date.now() < deadline, `${label}: ${JSON.stringify(state.matching)}`)
    await new Promise(resolve => setTimeout(resolve, 30))
  }
}
async function launch() {
  application = await electron.launch(launchOptions)
  const page = await application.firstWindow()
  await application.evaluate(({ dialog, session, ipcMain }, input) => {
    const fixture = globalThis.__iterationReview = { requests: [], launchAttempts: 0, nextHeldWorkId: null, held: false, release: null }
    dialog.showOpenDialog = async () => ({ canceled: false, filePaths: [input.quickFolder] })
    session.defaultSession.webRequest.onBeforeRequest({ urls: ['http://*/*', 'https://*/*'] }, (details, callback) => callback({ cancel: !details.url.startsWith('https://iteration-review-fixture.invalid/') }))
    ipcMain.removeHandler('kun-manager:launchGame')
    ipcMain.handle('kun-manager:launchGame', () => { fixture.launchAttempts++; return { ok: false, error: 'Program execution is forbidden in this test.' } })
    globalThis.fetch = async (value, init) => {
      const url = new URL(String(value))
      if (url.origin !== 'https://api.nextmoe.dev') throw new Error('Unexpected fixture destination')
      if (new Headers(init?.headers).get('authorization') !== `Bearer ${input.key}`) throw new Error('Fixture authentication missing')
      if (init?.redirect !== 'error') throw new Error('Fixture redirect protection missing')
      fixture.requests.push(url.pathname + url.search)
      if (fixture.nextHeldWorkId && url.pathname === `/v2/catalog/works/${fixture.nextHeldWorkId}`) {
        fixture.nextHeldWorkId = null; fixture.held = true
        await new Promise((resolve, reject) => {
          const aborted = () => reject(new Error('Fixture request cancelled'))
          if (init.signal.aborted) { aborted(); return }
          init.signal.addEventListener('abort', aborted, { once: true })
          fixture.release = () => { init.signal.removeEventListener('abort', aborted); resolve() }
        })
      }
      let body
      if (url.pathname === '/v2/catalog/search') {
        const query = (url.searchParams.get('q') ?? '').toLowerCase()
        body = { object: 'list', items: input.works.filter(work => query === work.display_name.toLowerCase())
          .map(work => ({ object: 'search_result', target_object: 'work', id: work.id, display_name: work.display_name })) }
      } else if (url.pathname === '/v2/catalog/works') {
        const ids = new Set((url.searchParams.get('ids') ?? '').split(',')), refs = new Set((url.searchParams.get('refs') ?? '').split(','))
        body = { object: 'list', items: input.works.filter(work => ids.has(work.id) || work.refs.some(ref => refs.has(`${ref.source}:${ref.external_id}`))), missing: [] }
      } else {
        body = input.works.find(work => url.pathname === `/v2/catalog/works/${work.id}`)
        if (!body) throw new Error('Unexpected fixture catalog request')
      }
      return new Response(JSON.stringify(body), { headers: { 'content-type': 'application/json' } })
    }
  }, { works, key: fakeKey, quickFolder })
  await page.route(/^https?:\/\//, async route => {
    const url = new URL(route.request().url()), match = /^\/(\d+)\.svg$/.exec(url.pathname)
    if (url.origin === 'https://iteration-review-fixture.invalid' && match) {
      await route.fulfill({ contentType: 'image/svg+xml', body: coverFixtures[(Number(match[1]) - 1) % coverFixtures.length] })
    } else await route.abort()
  })
  await application.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows()[0].setContentSize(1440, 960))
  page.setDefaultTimeout(15000)
  page.on('pageerror', error => rendererErrors.push(error.message))
  await page.emulateMedia({ reducedMotion: 'reduce' })
  await page.locator('.library-card').nth(2).waitFor()
  assert.equal(page.url(), 'kun-manager://app/index.html', 'The test must use the current built desktop renderer')
  assert.equal((await snapshot(page)).dataPath, profile, 'The formal data directory is never used')
  assert.equal(await application.evaluate(({ app }) => app.isPackaged), false)
  return page
}
async function close() {
  const fixture = await application.evaluate(() => ({ requests: globalThis.__iterationReview.requests.length, launchAttempts: globalThis.__iterationReview.launchAttempts }))
  assert.equal(fixture.launchAttempts, 0)
  requestCount += fixture.requests
  await application.close(); application = undefined
}
async function openReviews(page) {
  await page.locator('.sidebar .maintenance-entry').click()
  await page.locator('.maintenance-modal').waitFor()
  await page.locator('.maintenance-review-link').click()
  await page.locator('.matching-review-modal').waitFor()
}
const selectReview = (page, name) => page.locator('.review-queue').getByRole('button', { name, exact: true }).click()
const card = (page, name) => page.locator('.library-card').filter({ has: page.locator('.card-title', { hasText: name }) })
async function rejectedDecision(page, review, workId) {
  return page.evaluate(async ({ review, workId }) => {
    try { await window.kunManager.resolveMetadataMatch(review.gameId, workId, review.reviewId, review.decisionFingerprint); return { ok: true } }
    catch (error) { return { ok: false, error: error.message } }
  }, { review, workId })
}

try {
  let page = await launch()
  await page.evaluate(key => window.kunManager.saveSettings({ baseUrl: 'https://api.nextmoe.dev/v2/', apiKey: key, includeNsfw: true, theme: 'light', readExecutableMetadata: false }), fakeKey)
  let state = await snapshot(page)
  assert.equal(state.matching.reviews.length, 3)
  assert.equal(findReview(state, games[0].id).sourceStatus, 'current')
  assert.equal(findReview(state, games[1].id).sourceStatus, 'legacy')
  assert.ok(state.matching.reviews.every(item => item.reviewId && item.decisionFingerprint))
  const legacyId = findReview(state, games[1].id).reviewId
  // Startup completion banners are collected, while the permanent entry stays
  // reachable from a different page with the exact outstanding-review count.
  await page.locator('.matching-banner').waitFor({ state: 'hidden' })
  await page.locator('.sidebar nav').getByRole('button', { name: '游玩统计', exact: true }).click()
  assert.equal((await page.locator('.sidebar .maintenance-entry .nav-count').innerText()).trim(), '3')
  await page.locator('.sidebar .maintenance-entry').click()
  await page.locator('.maintenance-modal').waitFor()
  await capture(page, '01-maintenance.png', '从游玩统计通过常驻入口打开资料整理；显示最近任务和三项待核对。')
  await page.locator('.maintenance-review-link').click()
  await page.locator('.matching-review-modal').waitFor()
  await selectReview(page, games[0].name)
  assert.equal((await page.locator('.review-current-work strong').innerText()).trim(), games[0].metadata.title)
  assert.match(await page.locator('.review-local-game').innerText(), /首选识别目录/)
  assert.ok((await page.locator('.review-local-game').innerText()).includes(games[0].installations[1].folderPath))
  await page.locator('.review-local-game summary').click()
  for (const installation of games[0].installations) assert.ok((await page.locator('.review-local-game').innerText()).includes(installation.folderPath))
  await capture(page, '02-review-current-source.png', '核对当前关联、实际首选识别目录及两个输入安装，并展示可比较的候选。')
  const beforeLater = await snapshot(page)
  await page.getByRole('button', { name: '稍后处理', exact: true }).click()
  await page.locator('.matching-review-modal').waitFor({ state: 'hidden' })
  assert.deepEqual((await snapshot(page)).matching.reviews, beforeLater.matching.reviews)
  assert.deepEqual((await snapshot(page)).games, beforeLater.games)
  results.currentBindingAndActualPreferredSource = true; results.laterRetainsExactQueue = true

  // Open an editor draft normally. The internal overlay activation below is
  // deliberate test control for isolation, not evidence of sidebar reachability
  // through a blocking modal. Candidate details must never fill this editor.
  await page.locator('.sidebar nav').getByRole('button', { name: /我的书架/ }).click()
  await card(page, games[0].name).locator('.card-title').click()
  await page.locator('.detail-tabs').getByRole('button', { name: '管理', exact: true }).click()
  const field = label => page.locator('.manage-form .field-label').filter({ hasText: label })
  const draftNotes = '候选详情不能覆盖这段尚未保存的笔记。'
  await field('游玩笔记').locator('textarea').fill(draftNotes)
  await field('我的评分').locator('input').fill('6.4')
  await page.locator('.sidebar .maintenance-entry').evaluate(button => button.click())
  await page.locator('.maintenance-review-link').click()
  await selectReview(page, games[0].name)
  await page.locator('.review-option-row').first().getByRole('button', { name: '查看详情', exact: true }).click()
  await page.locator('.review-detail-modal').waitFor()
  await page.locator('.review-detail-modal .description-text').filter({ hasText: candidates[0].description }).waitFor()
  await capture(page, '03-candidate-detail.png', '独立候选详情；原管理页保留未保存的笔记和评分草稿，模拟目录请求已返回完整简介。')
  await page.keyboard.press('Escape')
  await page.locator('.review-detail-modal').waitFor({ state: 'hidden' })
  await page.getByRole('button', { name: '稍后处理', exact: true }).click()
  await page.locator('.matching-review-modal').waitFor({ state: 'hidden' })
  assert.equal(await field('游玩笔记').locator('textarea').inputValue(), draftNotes)
  assert.equal(await field('我的评分').locator('input').inputValue(), '6.4')
  assert.equal((await snapshot(page)).games[0].metadata.id, games[0].metadata.id)
  await page.getByRole('button', { name: '保存书架信息', exact: true }).click()
  await waitState(page, current => findGame(current, games[0].id).notes === draftNotes, 'The preserved editor draft did not save to its original game')
  await page.keyboard.press('Escape')
  await page.locator('.game-detail-modal').waitFor({ state: 'hidden' })
  results.candidateDetailsDoNotPolluteEditor = true

  await openReviews(page); await selectReview(page, games[1].name)
  assert.match(await page.locator('.review-source-note').innerText(), /旧队列未记录完整来源/)
  assert.equal((await page.locator('.review-current-work strong').innerText()).trim(), games[1].metadata.title)
  assert.equal(await page.locator('.review-option').isEnabled(), true, 'Legacy evidence remains an explicit user choice, not an automatic proof')
  await capture(page, '04-legacy-review.png', '旧队列安全降级：说明没有完整来源记录，显示实时当前关联并允许显式决定。')
  const beforeKeep = await snapshot(page)
  await page.getByRole('button', { name: '保留当前关联', exact: true }).click()
  state = await waitState(page, current => !findReview(current, games[1].id), 'Keep Current did not finish exactly this suggestion')
  assert.deepEqual(state.games, beforeKeep.games)
  assert.deepEqual(state.matching.reviews.map(item => item.reviewId), beforeKeep.matching.reviews.filter(item => item.reviewId !== legacyId).map(item => item.reviewId))
  results.legacyReadableAndKeepCurrentExact = true

  await selectReview(page, games[0].name)
  const staleInput = findReview(await snapshot(page), games[0].id)
  const renamed = `${games[0].name} · 后来确认的个人名称`
  await page.evaluate(({ id, name }) => window.kunManager.updateGame(id, { name }), { id: games[0].id, name: renamed })
  state = await waitState(page, current => findReview(current, games[0].id)?.sourceStatus === 'stale', 'The edited source was not marked stale')
  await page.locator('.review-reason').filter({ hasText: '这条建议已过时' }).waitFor()
  assert.equal(await page.locator('.review-option').isDisabled(), true)
  assert.equal(await page.getByRole('button', { name: '确认关联', exact: true }).isDisabled(), true)
  await capture(page, '05-stale-review.png', '名称变更使旧建议过时；候选选择与确认禁用，保留当前与手动搜索仍可用。')
  assert.equal((await rejectedDecision(page, staleInput, '101')).ok, false)
  assert.equal((await rejectedDecision(page, staleInput, null)).ok, false)
  assert.ok(findReview(await snapshot(page), games[0].id), 'Rejected stale actions must retain the queue')
  await page.getByRole('button', { name: '手动搜索', exact: true }).click()
  await page.getByRole('textbox', { name: '关联作品搜索', exact: true }).fill(titles[0])
  await page.locator('.modal-search').getByRole('button', { name: '搜索', exact: true }).click()
  await page.locator('.binding-result').filter({ hasText: titles[0] }).click()
  state = await waitState(page, current => findGame(current, games[0].id).metadata?.id === '101' && !findReview(current, games[0].id), 'Explicit manual search could not replace a stale suggestion')
  assert.equal(findGame(state, games[0].id).name, renamed)
  assert.equal(findGame(state, games[0].id).notes, draftNotes); assert.equal(findGame(state, games[0].id).personalRating, 6.4)
  assert.ok(findReview(state, games[2].id)); results.staleBlockedAndManualSearchPreservesPersonalData = true
  await page.locator('.binding-result').waitFor({ state: 'hidden' })

  const raceReview = findReview(await snapshot(page), games[2].id)
  await application.evaluate(() => { globalThis.__iterationReview.nextHeldWorkId = '301'; globalThis.__iterationReview.held = false })
  await page.evaluate(review => { globalThis.__oldReviewOutcome = window.kunManager.resolveMetadataMatch(review.gameId, '301', review.reviewId, review.decisionFingerprint)
    .then(value => ({ ok: true, value }), error => ({ ok: false, error: error.message })) }, raceReview)
  const deadline = Date.now() + 10000
  while (!await application.evaluate(() => globalThis.__iterationReview.held)) {
    assert.ok(Date.now() < deadline, 'The old decision did not reach its held real catalog request')
    await new Promise(resolve => setTimeout(resolve, 30))
  }
  await page.evaluate(id => window.kunManager.startMetadataMatching([id], 'reidentify'), games[2].id)
  state = await waitState(page, current => current.matching.status === 'completed' && findReview(current, games[2].id)?.reviewId !== raceReview.reviewId, 'A new same-game suggestion did not replace the old decision')
  const newReview = findReview(state, games[2].id)
  await application.evaluate(() => globalThis.__iterationReview.release())
  const oldOutcome = await page.evaluate(() => globalThis.__oldReviewOutcome)
  assert.equal(oldOutcome.ok, false); assert.match(oldOutcome.error, /建议已更新|核对操作已更新/)
  assert.equal((await rejectedDecision(page, raceReview, null)).ok, false)
  state = await snapshot(page)
  assert.equal(findReview(state, games[2].id).reviewId, newReview.reviewId)
  assert.equal(findGame(state, games[2].id).metadata.id, games[2].metadata.id)
  results.newSameGameSuggestionProtectedFromOldActions = true
  await openReviews(page); await selectReview(page, games[2].name)
  await page.locator('.review-option').filter({ hasText: titles[2] }).click()
  await page.getByRole('button', { name: '确认关联', exact: true }).click()
  state = await waitState(page, current => findGame(current, games[2].id).metadata?.id === '301' && current.matching.reviews.length === 0, 'The latest explicit decision did not commit')
  assert.deepEqual(personal(findGame(state, games[2].id)), personal(games[2]))
  await page.getByRole('button', { name: '返回书架', exact: true }).click()
  results.latestCorrectionPreservesAllPersonalRecords = true

  // Immediate transport responses exercise completion before the import toast
  // expires. A historical matchingStarted flag must not claim it is still busy.
  await page.getByRole('button', { name: '添加游戏', exact: true }).click()
  await page.getByRole('button', { name: '确认导入 1 部', exact: true }).click()
  state = await waitState(page, current => current.games.length === 4 && current.matching.status === 'completed' && current.games.some(game => game.metadata?.id === '401'), 'Fast import recognition did not complete')
  await page.getByText(/已添加 1 部游戏，跳过 0 个重复条目/).waitFor()
  await page.locator('.matching-banner.running').waitFor({ state: 'hidden' })
  assert.equal(await page.getByText(/正在自动识别封面与资料|正在自动补全游戏资料/).count(), 0)
  assert.equal(await page.locator('.matching-banner').count(), 0, 'Completed task banners are collected')
  await page.locator('.sidebar .maintenance-entry').click()
  await page.locator('.maintenance-task').filter({ hasText: '本次任务已结束' }).waitFor()
  assert.equal(await page.locator('.maintenance-task').getByText('正在补全缺项', { exact: false }).count(), 0)
  await page.keyboard.press('Escape')
  results.fastCompletionHasNoObsoleteRunningToast = true
  const expectedGames = (await snapshot(page)).games
  await close()
  page = await launch()
  state = await snapshot(page)
  assert.deepEqual(state.games, expectedGames); assert.deepEqual(state.matching.reviews, [])
  assert.equal(state.matching.status, 'completed'); assert.equal(state.activeSession, null)
  assert.equal(state.settings.readExecutableMetadata, false)
  for (const game of expectedGames) for (const installation of game.installations) assert.equal(await fs.readFile(installation.executablePath, 'utf8'), 'Inert iteration review fixture. Never execute.')
  results.restartPreservesCommittedLibraryAndQueue = true
  assert.deepEqual(rendererErrors, [])
  await close()
  const report = { ...results, fixtureApiRequests: requestCount, actualApiRequests: 0, actualGameExecutions: 0, rendererErrors: 0, profileIsolated: true, builtRootElectron: true }
  const runtimeAfter = await hashes(runtimeFiles), sourceAfter = await hashes(sourceFiles)
  assert.deepEqual(runtimeAfter, runtimeHashes, 'Running output files changed during capture')
  assert.deepEqual(sourceAfter, sourceHashes, 'Source files changed during capture; these shots require a clear new baseline')
  const manifest = { project, version: state.version, packaged: false, runtimeOrigin: 'kun-manager://app/index.html',
    gitHead: execFileSync('git', ['-C', project, 'rev-parse', 'HEAD'], { encoding: 'utf8' }).trim(),
    branch: execFileSync('git', ['-C', project, 'branch', '--show-current'], { encoding: 'utf8' }).trim(),
    sourceBaseline: '本轮未提交工作区源码，精确内容由 sourceHashes 标识；gitHead 为修改前的提交。',
    capturedAt: new Date().toISOString(), timezone: 'Asia/Shanghai', sourceHashes, runtimeHashes, screenshots: captures, verification: report,
    limitations: ['仅当前开发构建的 Electron，不是打包版。', '全部游戏、安装、评分和 API 结果为合成或模拟数据；不证明真实游戏识别率。', '候选详情编辑隔离使用程序控制打开重叠面板，不证明可通过阻挡弹窗点击侧栏。', '没有正式书架、真实账号、实际 API 请求、游戏执行或版本替换。'],
  }
  await fs.writeFile(path.join(artifacts, 'manifest.json'), JSON.stringify(manifest, null, 2))
  await fs.writeFile(path.join(artifacts, 'README.md'), `# 第一轮迭代：核对与资料整理流程证据\n\n当前根目录 Electron 构建，版本 ${state.version}；合成 profile 和模拟 API。当前运行源码、JS/CSS、Electron 入口、截图 SHA-256 与完整验证结果见 [manifest.json](manifest.json)。\n\n` + captures.map(item => `- [${item.file}](${item.file})：${item.scene}`).join('\n') + '\n\n实际 API 请求 0 次，实际游戏执行 0 次，渲染错误 0。截图不能代替全部交互或真实游戏验证；本套相应 IPC 与持久化断言已实际执行。\n')
  console.log(JSON.stringify({ ...report, screenshotIndex: path.join(artifacts, 'README.md'), screenshotCount: captures.length }))
} finally {
  if (application) await close()
  if (path.dirname(path.resolve(temporary)) !== temporaryRoot || !path.basename(temporary).startsWith('the-order-iteration-review-')) throw new Error('Unexpected test directory')
  await fs.rm(temporary, { recursive: true, force: true })
}
