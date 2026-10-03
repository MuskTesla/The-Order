import assert from 'node:assert/strict'
import { promises as fs } from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { _electron as electron } from '../node_modules/playwright/index.mjs'

// This complements desktop-matching, which retains the default import switch,
// synthetic PE opt-in/out, first association and interrupted-job regressions.
const project = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const temporaryRoot = path.resolve(os.tmpdir())
const temporary = await fs.mkdtemp(path.join(temporaryRoot, 'the-order-reidentification-'))
const profile = path.join(temporary, 'profile'), folder = path.join(temporary, 'fictional-local-games')
const packaged = process.argv.includes('--packaged')
const fakeKey = 'test-reidentification-fixture-only'
const date = '2026-10-03T00:00:00.000Z'
const correctTitle = 'Harbor Letters Beneath the Azure Moon'
const secondTitle = 'The Quiet Observatory Beyond Dawn'
const oldTitle = 'An Incorrect Association From Another Story'
const coverUrl = id => `https://reidentification-fixture.invalid/${id}.svg`
const metadata = (id, title, appId) => ({
  id, title, originalTitle: title, aliases: [], coverUrl: coverUrl(id), bannerUrl: null,
  description: `Saved metadata for ${title}`, releaseDate: null, releaseDatePrecision: null,
  developers: ['Synthetic Test Studio'], tags: [], ratings: [], characters: [],
  references: appId ? [{ source: 'steam', id: appId }] : [], webUrl: null,
  contentRating: null, fetchedAt: date,
})
const games = [
  { id: 'reidentify-wrong', name: '我珍藏的海岸故事', work: metadata('901', oldTitle, '49999'), title: correctTitle, appId: '40001' },
  { id: 'reidentify-correct', name: secondTitle, work: metadata('102', secondTitle, '40002'), title: secondTitle, appId: '40002' },
  { id: 'reidentify-virtual', name: '未安装的虚构愿望清单', work: metadata('903', 'A Fictional Wishlist Story', null), title: null },
].map((item, index) => ({
  id: item.id, name: item.name, nameIsCustom: true, metadata: item.work,
  installations: item.title ? [{ id: `installation-${index}`, folderPath: path.join(folder, item.title), executablePath: path.join(folder, item.title, 'Game.exe'), arguments: ['--saved-argument'] }] : [],
  preferredInstallationId: item.title ? `installation-${index}` : null,
  status: index ? 'planned' : 'paused', personalRating: index ? null : 0,
  favorite: index === 0, categories: ['必须保留的分类'], customTags: ['必须保留的标签'],
  notes: `持久化个人笔记 ${index}：重新识别只修改作品资料。`, totalPlaySeconds: index ? 0 : 900,
  playCount: index ? 0 : 1,
  sessions: index ? [] : [{ id: 'saved-session', startedAt: '2026-10-02T12:00:00.000Z', endedAt: '2026-10-02T12:15:00.000Z', seconds: 900 }],
  addedAt: date, lastPlayedAt: index ? null : '2026-10-02T12:15:00.000Z',
}))
await fs.mkdir(profile, { recursive: true })
for (const [index, game] of games.entries()) {
  for (const installation of game.installations) {
    await fs.mkdir(installation.folderPath, { recursive: true })
    await fs.writeFile(installation.executablePath, 'Inert test fixture. Never execute.')
    await fs.writeFile(path.join(installation.folderPath, 'steam_appid.txt'), String(40001 + index))
  }
}
await fs.writeFile(path.join(profile, 'library.json'), JSON.stringify({ format: 'kun-gal-manager', version: 1, exportedAt: date, games }))
const rawWork = (id, title, appId) => ({
  object: 'work', id, display_name: title,
  refs: appId ? [{ source: 'steam', external_id: appId }] : [],
  cover: { url: coverUrl(id) }, intros: [{ lang: 'en', value: `Verified full metadata for ${title}` }],
  companies: [{ object: 'company', id: '1', display_name: 'Synthetic Test Studio', attribution_role: 'developer' }],
  ratings: [], characters: [], tags: [],
  titles: [{ title, title_kind: 'official', is_machine: false, lang: 'en' }],
})
const works = [rawWork('101', correctTitle, '40001'), rawWork('102', secondTitle, '40002'), rawWork('901', oldTitle, '49999')]
const launchOptions = {
  executablePath: path.join(project, packaged ? 'release/win-unpacked/The Order.exe' : 'node_modules/electron/dist/electron.exe'),
  args: packaged ? [] : [project], env: { ...process.env, KGM_DATA_DIR: profile }, timeout: 30000,
}
let application
const errors = []
async function transport(mode = 'normal') {
  await application.evaluate(({ dialog, session, ipcMain }, input) => {
    dialog.showOpenDialog = async () => ({ canceled: false, filePaths: [input.folder] })
    session.defaultSession.webRequest.onBeforeRequest({ urls: ['http://*/*', 'https://*/*'] }, (_details, callback) => callback({ cancel: true }))
    const fixture = globalThis.__reidentification = { mode: input.mode, requests: [], release: null }
    globalThis.__reidentificationLaunchAttempts ??= 0
    ipcMain.removeHandler('kun-manager:launchGame')
    ipcMain.handle('kun-manager:launchGame', () => {
      globalThis.__reidentificationLaunchAttempts++
      return { ok: false, error: 'Launching any program is forbidden in this isolated test.' }
    })
    globalThis.fetch = async (value, init) => {
      const url = new URL(String(value))
      if (url.origin !== 'https://api.nextmoe.dev') throw new Error('Unexpected fixture destination')
      if (new Headers(init?.headers).get('authorization') !== `Bearer ${input.key}`) throw new Error('Fixture authentication was not forwarded')
      if (init?.redirect !== 'error') throw new Error('Fixture redirect protection was not enabled')
      fixture.requests.push(url.pathname + url.search)
      if (fixture.mode === 'stall' || fixture.mode === 'pause-refs' && url.searchParams.has('refs')) {
        await new Promise((resolve, reject) => {
          if (init.signal.aborted) { reject(new Error('Fixture cancelled')); return }
          const aborted = () => reject(new Error('Fixture cancelled'))
          init.signal.addEventListener('abort', aborted, { once: true })
          fixture.release = () => { init.signal.removeEventListener('abort', aborted); fixture.mode = 'normal'; resolve() }
        })
      }
      let body
      if (url.pathname === '/v2/catalog/search') {
        const query = (url.searchParams.get('q') ?? '').toLowerCase()
        const found = input.works.filter(work => query === work.display_name.toLowerCase())
        body = { object: 'list', items: found.map(work => ({ object: 'search_result', target_object: 'work', id: work.id, display_name: work.display_name })) }
      } else if (url.pathname === '/v2/catalog/works') {
        const refs = new Set((url.searchParams.get('refs') ?? '').split(',')), ids = new Set((url.searchParams.get('ids') ?? '').split(','))
        body = { object: 'list', items: input.works.filter(work => ids.has(work.id) || work.refs.some(ref => refs.has(`${ref.source}:${ref.external_id}`))), missing: [] }
      } else {
        body = input.works.find(work => url.pathname === `/v2/catalog/works/${work.id}`)
        if (!body) throw new Error('Unexpected fixture API request')
      }
      return new Response(JSON.stringify(body), { headers: { 'content-type': 'application/json' } })
    }
  }, { mode, folder, works, key: fakeKey })
}
async function start(mode = 'normal') {
  application = await electron.launch(launchOptions)
  const page = await application.firstWindow()
  // Application IPC handlers are registered before its first window. Install
  // test interceptors only after that point; pre-window registration races main.
  await transport(mode)
  page.setDefaultTimeout(15000)
  page.on('pageerror', error => errors.push(error.message))
  await page.emulateMedia({ reducedMotion: 'reduce' })
  await page.locator('.library-card').nth(2).waitFor()
  return page
}
const snapshot = page => page.evaluate(() => window.kunManager.getState())
async function waitState(page, predicate, label) {
  const deadline = Date.now() + 15000
  while (true) {
    const state = await snapshot(page)
    if (predicate(state)) return state
    assert.ok(Date.now() < deadline, `${label}: ${JSON.stringify(state.matching)}`)
    await new Promise(resolve => setTimeout(resolve, 30))
  }
}
async function waitRequest() {
  const deadline = Date.now() + 10000
  while (!await application.evaluate(() => Boolean(globalThis.__reidentification.release))) {
    assert.ok(Date.now() < deadline, 'The real client did not reach the held fixture request')
    await new Promise(resolve => setTimeout(resolve, 30))
  }
}
async function release() { await waitRequest(); await application.evaluate(() => globalThis.__reidentification.release()) }
async function close() {
  const attempted = await application.evaluate(() => globalThis.__reidentificationLaunchAttempts)
  await application.close(); application = undefined
  assert.equal(attempted, 0, 'Reidentification must never request a program launch')
}
const find = (state, id) => state.games.find(game => game.id === id)
const personal = ({ metadata: _metadata, ...game }) => game
const card = (page, name) => page.locator('.library-card').filter({ has: page.locator('.card-title', { hasText: name }) })
async function beginAndWait(page, button, total) {
  const previousId = (await snapshot(page)).matching?.id
  await button.click()
  return waitState(page, state => state.matching?.id !== previousId && state.matching?.total === total && state.matching?.status === 'completed', 'Reidentification did not complete')
}

try {
  let page = await start('pause-refs')
  await page.evaluate(key => window.kunManager.saveSettings({ baseUrl: 'https://api.nextmoe.dev/v2/', apiKey: key, includeNsfw: true, theme: 'light', readExecutableMetadata: false }), fakeKey)
  const beforeInvalidMode = await snapshot(page)
  const invalidModeRejected = await page.evaluate(async () => {
    try { await window.kunManager.startMetadataMatching(undefined, 'unsupported-mode'); return false }
    catch { return true }
  })
  assert.equal(invalidModeRejected, true, 'The real IPC boundary must reject an unsupported matching mode')
  assert.deepEqual(await snapshot(page), beforeInvalidMode, 'Invalid mode does not change job or library state')
  assert.deepEqual(await application.evaluate(() => globalThis.__reidentification.requests), [], 'Invalid mode must not fetch')
  assert.ok((await snapshot(page)).games.every(game => game.metadata?.coverUrl))
  const allButton = page.getByRole('button', { name: '重新识别', exact: true })
  assert.equal(await allButton.isEnabled(), true, 'Complete covers must not disable reidentification')
  // Dispatch two same-turn activations against the real renderer handler. The
  // pending guard must prevent the second activation from creating another job.
  await allButton.evaluate(button => { button.click(); button.click() })
  await waitState(page, state => state.matching?.status === 'running' && state.matching.total === 2, 'All local games were not selected')
  await release()
  let state = await waitState(page, state => state.matching?.status === 'completed', 'Full library reidentification did not finish')
  assert.equal(state.games.length, 3)
  assert.equal(find(state, games[0].id).metadata.id, '901', 'A different work must not replace existing metadata automatically')
  assert.deepEqual(personal(find(state, games[0].id)), personal(games[0]))
  assert.match(find(state, games[1].id).metadata.description, /^Verified full metadata/)
  assert.deepEqual(find(state, games[2].id), games[2], 'A virtual game is outside local reidentification')
  const review = state.matching.reviews.find(item => item.gameId === games[0].id)
  assert.ok(review?.options.some(option => option.work.id === '101'), JSON.stringify(state.matching))
  const savedReviews = state.matching.reviews
  await close()

  // Pending corrections survive restart, including the existing association.
  page = await start()
  state = await snapshot(page)
  assert.deepEqual(state.matching.reviews, savedReviews)
  assert.equal(find(state, games[0].id).metadata.id, '901')
  await page.getByRole('button', { name: /待核对 \d+ 项/ }).click()
  await page.locator('.review-queue').getByRole('button', { name: games[0].name, exact: true }).click()
  await page.locator('.review-option').filter({ hasText: correctTitle }).click()
  await page.getByRole('button', { name: '确认关联', exact: true }).click()
  state = await waitState(page, state => find(state, games[0].id).metadata.id === '101', 'Confirmed correction was not saved')
  assert.deepEqual(personal(find(state, games[0].id)), personal(games[0]), 'Correction preserves custom name, notes, zero rating, categories, tags, paths, arguments and sessions')
  await page.keyboard.press('Escape')
  await page.locator('.kun-modal-panel').waitFor({ state: 'hidden' })

  // Scanning the same directories exposes a useful action without importing a
  // second copy, even though every local game already has metadata and artwork.
  await transport('pause-refs')
  await page.getByRole('button', { name: '添加游戏', exact: true }).click()
  await page.locator('.scan-list .scan-known').nth(1).waitFor()
  assert.equal(await page.locator('.scan-list .scan-known').count(), 2)
  assert.equal(await page.getByRole('button', { name: '确认导入 0 部', exact: true }).isDisabled(), true)
  const scanJobId = (await snapshot(page)).matching.id
  await page.getByRole('button', { name: /^识别已入库游戏/ }).click()
  await waitState(page, state => state.matching.id !== scanJobId && state.matching.status === 'running' && state.matching.total === 2, 'Rescanned games were not reidentified')
  await release()
  state = await waitState(page, state => state.matching.status === 'completed', 'Rescan recognition did not complete')
  assert.equal(state.games.length, 3)
  assert.equal(new Set(state.games.flatMap(game => game.installations.map(installation => installation.folderPath))).size, 2)
  assert.deepEqual(personal(find(state, games[0].id)), personal(games[0]))
  await page.keyboard.press('Escape')

  // Selecting one complete game must not accidentally start a full-library job.
  await transport()
  await page.getByRole('button', { name: '选择整理', exact: true }).click()
  await card(page, games[1].name).getByRole('checkbox').click()
  state = await beginAndWait(page, page.getByRole('button', { name: /^识别所选/ }), 1)
  const selectedRequests = await application.evaluate(() => globalThis.__reidentification.requests)
  const selectedRefs = selectedRequests.flatMap(value => (new URL(value, 'https://api.nextmoe.dev').searchParams.get('refs') ?? '').split(','))
  assert.ok(selectedRefs.includes('steam:40002'))
  assert.equal(selectedRefs.includes('steam:40001'), false)
  assert.equal(state.games.length, 3)
  if (await page.getByRole('button', { name: '取消整理', exact: true }).isVisible()) await page.getByRole('button', { name: '取消整理', exact: true }).click()

  // The individual management action follows the same real IPC and matcher.
  await transport()
  await card(page, games[0].name).locator('.card-title').click()
  await page.getByRole('button', { name: '管理', exact: true }).click()
  state = await beginAndWait(page, page.getByRole('button', { name: '重新识别此游戏', exact: true }), 1)
  assert.equal(find(state, games[0].id).metadata.id, '101')
  assert.deepEqual(personal(find(state, games[0].id)), personal(games[0]))
  await page.keyboard.press('Escape')

  // Cancellation interrupts an actual pending transport, never a simulated UI
  // state. It preserves all records and the cancellation survives restart.
  await transport('stall')
  const beforeCancel = (await snapshot(page)).games
  await page.getByRole('button', { name: '重新识别', exact: true }).click()
  await waitRequest()
  await page.locator('.sidebar nav').getByRole('button', { name: '游玩统计', exact: true }).click()
  await page.getByRole('button', { name: '取消识别', exact: true }).evaluate(button => { button.click(); button.click() })
  state = await waitState(page, state => state.matching.status === 'cancelled', 'Reidentification did not cancel')
  assert.deepEqual(state.games, beforeCancel)
  assert.equal(state.activeSession, null)
  await close()
  page = await start()
  state = await snapshot(page)
  assert.equal(state.matching.status, 'cancelled')
  assert.deepEqual(state.games, beforeCancel)
  assert.equal(state.settings.readExecutableMetadata, false)
  for (const game of games) for (const installation of game.installations) assert.equal(await fs.readFile(installation.executablePath, 'utf8'), 'Inert test fixture. Never execute.')
  assert.deepEqual(errors, [])
  console.log(JSON.stringify({ packaged, completeLibraryReidentified: true, invalidModeRejectedWithoutFetch: true, repeatedStartAndCancelSafe: true, virtualExcluded: true, correctionRequiresReview: true, reviewsSurviveRestart: true, confirmedCorrectionPreservesPersonalData: true, rescanWithoutDuplicates: true, selectedOnly: true, individualManagementAction: true, inflightCancelAndRestart: true, realClientParsing: true, actualGameExecutions: 0, rendererErrors: 0 }))
} finally {
  if (application) await close()
  if (path.dirname(path.resolve(temporary)) !== temporaryRoot || !path.basename(temporary).startsWith('the-order-reidentification-')) throw new Error('Unexpected test directory')
  await fs.rm(temporary, { recursive: true, force: true })
}
