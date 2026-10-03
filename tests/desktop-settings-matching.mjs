import assert from 'node:assert/strict'
import { promises as fs } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { createHash } from 'node:crypto'
import { execFileSync } from 'node:child_process'
import { _electron as electron } from '../node_modules/playwright/index.mjs'

const project = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const evidenceRound = process.argv.find(argument => argument.startsWith('--evidence-round='))?.split('=')[1]
if (evidenceRound !== undefined && !/^[0-8]$/.test(evidenceRound)) throw new Error('Evidence round must be from 0 to 8')
const output = evidenceRound === undefined ? path.join(project, '.artifacts/overnight-2026-10-04/round-4') : path.join(project, '.artifacts/overnight-2026-10-04', `round-${evidenceRound}`, 'settings-matching')
const profile = path.join(output, 'ui-profile'), marker = path.join(profile, 'synthetic-profile.txt')
const baseline = process.argv.includes('--baseline'), phase = baseline ? 'before' : 'after'
await fs.mkdir(output, { recursive: true })
try {
  if (await fs.readFile(marker, 'utf8') !== 'settings-matching-synthetic-only') throw new Error('Unknown profile')
  if (path.resolve(profile) !== path.join(output, 'ui-profile')) throw new Error('Unexpected profile')
  await fs.rm(profile, { recursive: true })
} catch (error) { if (error.code !== 'ENOENT') throw error }
await fs.mkdir(profile); await fs.writeFile(marker, 'settings-matching-synthetic-only')
const folder = path.join(profile, 'inert-game'); await fs.mkdir(folder); await fs.writeFile(path.join(folder, 'game.exe'), 'Inert synthetic text, never executed')
const date = '2026-10-03T00:00:00.000Z'
const metadata = { id: '101', title: 'Synthetic matching fixture', originalTitle: 'Synthetic matching fixture', aliases: [], coverUrl: null, bannerUrl: null, description: 'Original synthetic metadata', releaseDate: null, releaseDatePrecision: null, developers: [], tags: [], ratings: [], characters: [], references: [], webUrl: null, contentRating: null, fetchedAt: date }
const game = id => ({ id, name: 'Synthetic matching ' + id, nameIsCustom: true, metadata, installations: id === 'target' ? [{ id: 'installation', folderPath: folder, executablePath: null, arguments: [] }] : [], preferredInstallationId: id === 'target' ? 'installation' : null, status: 'planned', personalRating: 9, favorite: true, categories: ['Synthetic'], customTags: [], notes: 'Preserved fixture notes', totalPlaySeconds: 0, playCount: 0, sessions: [], addedAt: date, lastPlayedAt: null })
const libraryBytes = JSON.stringify({ format: 'kun-gal-manager', version: 1, exportedAt: date, games: ['target', 'blocker', 'positive-blocker'].map(game) })
await fs.writeFile(path.join(profile, 'library.json'), libraryBytes)
const evidence = { phase, capturedAt: new Date().toISOString(), checkoutHead: execFileSync('git', ['rev-parse', 'HEAD'], { cwd: project, encoding: 'utf8' }).trim(), sourceDirty: !!execFileSync('git', ['status', '--porcelain'], { cwd: project, encoding: 'utf8' }).trim(), synthetic: true, realElectronIpc: true, realApiCalls: 0, formalProfileAccessed: false, fixtureSha256: createHash('sha256').update(libraryBytes).digest('hex'), checks: [], screenshots: [] }
const errors = []; let app
const poll = async (read, predicate, label) => { for (let i = 0; i < 250; i++) { const value = await read(); if (predicate(value)) return value; await new Promise(resolve => setTimeout(resolve, 20)) } throw new Error('Timed out: ' + label) }
try {
  app = await electron.launch({ executablePath: path.join(project, 'node_modules/electron/dist/electron.exe'), args: [path.join(project, 'tests/desktop-settings-matching-bootstrap.cjs')], env: { ...process.env, KGM_DATA_DIR: profile, KGM_DEV_URL: '', KGM_TEST_EXECUTABLE: '' }, timeout: 30000 })
  await app.evaluate(({ session, dialog }, folder) => {
    session.defaultSession.webRequest.onBeforeRequest({ urls: ['http://*/*', 'https://*/*'] }, (_, callback) => callback({ cancel: true }))
    dialog.showOpenDialog = async () => ({ canceled: false, filePaths: [folder] })
    globalThis.fetch = async (value, options) => {
      const url = new URL(String(value)), control = globalThis.__settingsMatching
      if (!url.pathname.endsWith('/catalog/works/101')) throw new Error('Unexpected synthetic request')
      control.requests++
      control.lastRequest = { origin: url.origin, includeNsfw: url.searchParams.get('nsfw') }
      // Deliberately ignore abort to prove stale results are guarded after return.
      await new Promise(resolve => { control.response = resolve })
      return Response.json({ object: 'work', id: '101', display_name: 'Synthetic refreshed work', images: {}, intros: [{ lang: 'zh-Hans', value: 'Updated synthetic metadata' }], refs: [], titles: [], tags: [], characters: [], ratings: [] })
    }
  }, folder)
  const page = await app.firstWindow(); page.on('pageerror', error => errors.push(error.message))
  await page.waitForFunction(() => window.kunManager !== undefined)
  const state = () => page.evaluate(() => window.kunManager.getState())
  const save = patch => page.evaluate(async patch => window.kunManager.saveSettings({ ...(await window.kunManager.getState()).settings, ...patch }), patch)
  const start = async () => { const requests = await app.evaluate(() => globalThis.__settingsMatching.requests); const job = await page.evaluate(() => window.kunManager.startMetadataMatching(['target'])); await poll(() => app.evaluate(() => globalThis.__settingsMatching.requests), n => n > requests, 'held catalog request'); return job.id }
  const releaseResponse = async () => { await app.evaluate(() => globalThis.__settingsMatching.response?.()); await page.waitForTimeout(100) }
  const cancel = async () => { await page.evaluate(() => window.kunManager.cancelMetadataMatching()); await releaseResponse() }
  await save({ apiKey: 'synthetic-settings-matching-only', readExecutableMetadata: false })
  await start()
  // Native window evidence under identical fixture, viewport and zoom conditions.
  await page.getByRole('button', { name: /偏好设置/ }).click()
  await page.getByRole('heading', { name: '资料源连接' }).waitFor()
  for (const [label, width, height, zoom] of [['wide', 1440, 940, 1], ['narrow-150', 1000, 700, 1.5]]) {
    await app.evaluate(({ BrowserWindow }, p) => { const w = BrowserWindow.getAllWindows()[0]; w.setSize(p.width, p.height); w.webContents.setZoomFactor(p.zoom) }, { width, height, zoom })
    await page.locator('.settings-save-bar').scrollIntoViewIfNeeded()
    await page.evaluate(async () => { await new Promise(requestAnimationFrame); await new Promise(requestAnimationFrame) }); await page.waitForTimeout(100)
    const dimensions = await page.evaluate(() => ({ width: innerWidth, height: innerHeight, dpr: devicePixelRatio, overflow: document.documentElement.scrollWidth > innerWidth }))
    assert.equal(dimensions.overflow, false)
    const relative = `${phase}/matching-${label}.png`; await fs.mkdir(path.join(output, phase), { recursive: true })
    const bytes = await app.evaluate(async ({ BrowserWindow }) => (await BrowserWindow.getAllWindows()[0].webContents.capturePage()).toPNG().toString('base64'))
    await fs.writeFile(path.join(output, relative), Buffer.from(bytes, 'base64')); evidence.screenshots.push({ path: relative, requested: { width, height, zoom }, actual: dimensions })
  }
  const job = (await state()).matching.id
  await save({ theme: 'dark', nonPortraitCoverDisplay: 'plain' })
  assert.equal((await state()).matching.status, baseline ? 'cancelled' : 'running'); assert.equal((await state()).matching.id, job)
  evidence.checks.push(baseline ? 'appearance incorrectly cancels: reproduced' : 'appearance preserves active job')
  if (baseline) await releaseResponse()
  else await cancel()
  for (const failure of ['invalid', 'write']) {
    await start(); const settingsBytes = await fs.readFile(path.join(profile, 'settings.json'), 'utf8')
    if (failure === 'write') await app.evaluate(() => { globalThis.__settingsMatching.fail = 'settings.json' })
    await assert.rejects(save(failure === 'invalid' ? { theme: 'invalid' } : { theme: 'light' }))
    assert.equal((await state()).matching.status, baseline ? 'cancelled' : 'running')
    assert.equal(await fs.readFile(path.join(profile, 'settings.json'), 'utf8'), settingsBytes)
    evidence.checks.push(`${failure} save ${baseline ? 'incorrectly cancels: reproduced' : 'preserves job and settings'}`)
    if (baseline) await releaseResponse(); else await cancel()
  }
  if (!baseline) {
    for (const patch of [{ includeNsfw: false }, { readExecutableMetadata: true }, { baseUrl: 'https://api.nextmoe.dev/v3/' }, { apiKey: 'synthetic-replaced-key' }, { clearApiKey: true }]) {
      await start(); const reply = await save(patch); assert.equal(reply.warning, undefined); assert.equal((await state()).matching.status, 'cancelled'); await releaseResponse()
      assert.equal((await state()).games.find(g => g.id === 'target').metadata.description, 'Original synthetic metadata')
      evidence.checks.push('relevant setting cancels and blocks delayed result: ' + Object.keys(patch)[0])
      if (patch.clearApiKey) await save({ apiKey: 'synthetic-restored-key' })
    }
    const hold = async filename => { await app.evaluate((_electron, filename) => { globalThis.__settingsMatching.hold = filename }, filename) }
    const held = () => poll(() => app.evaluate(() => globalThis.__settingsMatching.held), Boolean, 'bounded atomic write gate')
    const release = () => app.evaluate(() => globalThis.__settingsMatching.release())
    // A start queued behind a settings commit must use the new client config.
    await hold('settings.json')
    await page.evaluate(async () => { const s = (await window.kunManager.getState()).settings; globalThis.__pendingSave = window.kunManager.saveSettings({ ...s, includeNsfw: true }) })
    await held()
    const requests = await app.evaluate(() => globalThis.__settingsMatching.requests)
    await page.evaluate(() => { globalThis.__pendingStart = window.kunManager.startMetadataMatching(['target']) })
    await page.waitForTimeout(80); assert.equal(await app.evaluate(() => globalThis.__settingsMatching.requests), requests)
    await release(); await page.evaluate(() => globalThis.__pendingSave); await page.evaluate(() => globalThis.__pendingStart)
    await poll(() => app.evaluate(() => globalThis.__settingsMatching.requests), n => n > requests, 'new config request')
    assert.equal((await state()).matching.status, 'running'); assert.equal(await app.evaluate(() => globalThis.__settingsMatching.lastRequest.includeNsfw), 'true')
    await cancel(); evidence.checks.push('manual start queued after settings captures new configuration')
    await hold('settings.json')
    await page.evaluate(async () => { const s = (await window.kunManager.getState()).settings; globalThis.__pendingSave = window.kunManager.saveSettings({ ...s, theme: 'light' }) })
    await held()
    const beforeWithdraw = await app.evaluate(() => globalThis.__settingsMatching.requests)
    await page.evaluate(() => { globalThis.__pendingStart = window.kunManager.startMetadataMatching(['target']) })
    await page.waitForTimeout(50); await page.evaluate(() => window.kunManager.cancelMetadataMatching())
    await release(); await page.evaluate(() => globalThis.__pendingSave); await page.evaluate(() => globalThis.__pendingStart); await page.waitForTimeout(80)
    assert.equal(await app.evaluate(() => globalThis.__settingsMatching.requests), beforeWithdraw); assert.notEqual((await state()).matching.status, 'running')
    evidence.checks.push('explicit cancellation withdraws a start queued behind settings')
    // Conversely, a settings change queued behind initial job persistence cancels that job.
    await hold('metadata-matching.json')
    await page.evaluate(() => { globalThis.__pendingStart = window.kunManager.startMetadataMatching(['target']) })
    await held()
    await page.evaluate(async () => { const s = (await window.kunManager.getState()).settings; globalThis.__pendingSave = window.kunManager.saveSettings({ ...s, includeNsfw: false }) })
    await release(); await page.evaluate(() => globalThis.__pendingStart); await page.evaluate(() => globalThis.__pendingSave)
    assert.equal((await state()).matching.status, 'cancelled'); await releaseResponse(); evidence.checks.push('settings queued after initial job persistence cancels old configuration')
    // Import's automatic-start eligibility is evaluated after a queued credential removal.
    const candidates = await page.evaluate(() => window.kunManager.scanFolder()); assert.ok(candidates.length)
    await hold('settings.json')
    await page.evaluate(async () => { const s = (await window.kunManager.getState()).settings; globalThis.__pendingSave = window.kunManager.saveSettings({ ...s, clearApiKey: true }) })
    await held()
    await page.evaluate(ids => { globalThis.__pendingImport = window.kunManager.importLocalGames(ids, true) }, candidates.map(c => c.id))
    await page.waitForTimeout(80); await release(); await page.evaluate(() => globalThis.__pendingSave)
    const imported = await page.evaluate(() => globalThis.__pendingImport)
    assert.equal(imported.matchingStarted, false); assert.equal(imported.added, 0); assert.equal((await state()).games.find(g => g.id === 'target').installations.length, 1)
    await save({ apiKey: 'synthetic-restored-key' }); evidence.checks.push('automatic import start checks committed credential state without duplicate installation')
    await hold('settings.json')
    await page.evaluate(async () => { const s = (await window.kunManager.getState()).settings; globalThis.__pendingSave = window.kunManager.saveSettings({ ...s, theme: 'dark' }) })
    await held()
    await page.evaluate(ids => { globalThis.__pendingImport = window.kunManager.importLocalGames(ids, true) }, candidates.map(c => c.id))
    await page.waitForTimeout(80); await page.evaluate(() => window.kunManager.cancelMetadataMatching())
    await release(); await page.evaluate(() => globalThis.__pendingSave)
    assert.equal((await page.evaluate(() => globalThis.__pendingImport)).matchingStarted, false)
    const beforeNewImport = await app.evaluate(() => globalThis.__settingsMatching.requests)
    assert.equal((await page.evaluate(ids => window.kunManager.importLocalGames(ids, true), candidates.map(c => c.id))).matchingStarted, true)
    await poll(() => app.evaluate(() => globalThis.__settingsMatching.requests), n => n > beforeNewImport, 'fresh automatic start after cancellation')
    await cancel(); evidence.checks.push('explicit cancellation withdraws queued automatic start; a new import starts normally')
    // Queue a stale apply behind a settings transaction, using a real identity blocker.
    await start(); await hold('library.json.bak')
    await page.evaluate(() => { globalThis.__pendingRemoval = window.kunManager.removeGame('blocker') }); await held()
    await page.evaluate(async () => { const s = (await window.kunManager.getState()).settings; globalThis.__pendingSave = window.kunManager.saveSettings({ ...s, includeNsfw: true }) })
    await releaseResponse(); await release(); await page.evaluate(() => globalThis.__pendingRemoval); await page.evaluate(() => globalThis.__pendingSave)
    await page.waitForTimeout(100)
    assert.equal((await state()).games.find(g => g.id === 'target').metadata.description, 'Original synthetic metadata')
    assert.equal((await state()).matching.status, 'cancelled'); evidence.checks.push('old apply queued after settings is rejected')
    // An apply already inside its atomic commit is kept before the settings change.
    await start(); await hold('library.json.bak'); await releaseResponse(); await held()
    await page.evaluate(async () => { const s = (await window.kunManager.getState()).settings; globalThis.__pendingSave = window.kunManager.saveSettings({ ...s, includeNsfw: false }) })
    await release(); await page.evaluate(() => globalThis.__pendingSave); await page.waitForTimeout(100)
    assert.equal((await state()).games.find(g => g.id === 'target').metadata.description, 'Updated synthetic metadata')
    assert.equal((await state()).games.find(g => g.id === 'target').notes, 'Preserved fixture notes'); evidence.checks.push('already-entered atomic metadata commit is preserved')
    // Actual UI acknowledgement must distinguish persisted settings from a job-state write failure.
    await start(); await app.evaluate(() => { globalThis.__settingsMatching.fail = 'metadata-matching.json' })
    const adultSwitch = page.getByRole('switch', { name: '显示成人分级作品', exact: true })
    await adultSwitch.focus(); await page.keyboard.press('Space')
    await page.locator('.settings-save-bar').getByText('资料源、密钥、分级或识别方式变更保存后会停止当前识别；已完成结果保留。', { exact: true }).waitFor()
    await page.locator('.settings-save-bar').scrollIntoViewIfNeeded(); await page.waitForTimeout(120)
    const draftBytes = await app.evaluate(async ({ BrowserWindow }) => (await BrowserWindow.getAllWindows()[0].webContents.capturePage()).toPNG().toString('base64'))
    const draftRelative = 'after/relevant-draft-narrow-150.png'; await fs.writeFile(path.join(output, draftRelative), Buffer.from(draftBytes, 'base64')); evidence.screenshots.push({ path: draftRelative, actual: await page.evaluate(() => ({ width: innerWidth, height: innerHeight, dpr: devicePixelRatio, overflow: document.documentElement.scrollWidth > innerWidth })) })
    await page.getByRole('button', { name: '保存全部设置', exact: true }).click()
    await page.getByRole('form', { name: '偏好设置表单' }).getByRole('alert').filter({ hasText: '设置已保存，但停止识别状态暂未写入磁盘' }).waitFor()
    assert.equal((await state()).settings.includeNsfw, true); assert.equal((await state()).matching.status, 'cancelled'); await releaseResponse()
    assert.equal(await page.locator('.settings-save-bar').getByText('所有设置已保存', { exact: true }).count(), 1)
    await page.evaluate(() => document.querySelectorAll('[data-kun-toast-close]').forEach(b => b.click()))
    await page.waitForFunction(() => !document.querySelector('[data-kun-toast-close]'))
    await page.locator('.settings-save-bar').scrollIntoViewIfNeeded(); await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))); await page.waitForTimeout(120)
    const bytes = await app.evaluate(async ({ BrowserWindow }) => (await BrowserWindow.getAllWindows()[0].webContents.capturePage()).toPNG().toString('base64'))
    const relative = 'after/saved-with-warning-narrow-150.png'; await fs.writeFile(path.join(output, relative), Buffer.from(bytes, 'base64')); evidence.screenshots.push({ path: relative, actual: await page.evaluate(() => ({ width: innerWidth, height: innerHeight, dpr: devicePixelRatio, overflow: document.documentElement.scrollWidth > innerWidth })) })
    evidence.checks.push('actual settings form acknowledges saved preferences and displays cancellation-persistence warning')
    assert.equal(JSON.parse(await fs.readFile(path.join(profile, 'metadata-matching.json'), 'utf8')).status, 'running', 'the injected persistence failure really leaves the previous disk state')
    await app.close()
    app = await electron.launch({ executablePath: path.join(project, 'node_modules/electron/dist/electron.exe'), args: [project], env: { ...process.env, KGM_DATA_DIR: profile, KGM_DEV_URL: '', KGM_TEST_EXECUTABLE: '' }, timeout: 30000 })
    await app.evaluate(({ session }) => { session.defaultSession.webRequest.onBeforeRequest({ urls: ['http://*/*', 'https://*/*'] }, (_, callback) => callback({ cancel: true })) })
    const restartedPage = await app.firstWindow(); await restartedPage.waitForFunction(() => window.kunManager !== undefined)
    const restarted = await restartedPage.evaluate(() => window.kunManager.getState())
    assert.equal(restarted.settings.includeNsfw, true); assert.equal(restarted.settings.hasApiKey, true); assert.equal(restarted.matching.status, 'cancelled')
    assert.equal(restarted.games.find(g => g.id === 'target').metadata.description, 'Updated synthetic metadata'); assert.equal(restarted.games.find(g => g.id === 'target').notes, 'Preserved fixture notes')
    assert.equal(JSON.parse(await fs.readFile(path.join(profile, 'metadata-matching.json'), 'utf8')).status, 'cancelled')
    evidence.checks.push('graceful shutdown retries job-state persistence; restarted process retains settings and library without resuming cancelled work')
  }
  assert.deepEqual(errors, []); evidence.rendererErrors = errors
  evidence.runtime = {}
  for (const relative of ['dist/index.html', 'dist-electron/main.cjs', 'dist-electron/preload.cjs', ...(await fs.readdir(path.join(project, 'dist/assets'))).filter(name => /\.(js|css)$/.test(name)).map(name => 'dist/assets/' + name)]) evidence.runtime[relative] = createHash('sha256').update(await fs.readFile(path.join(project, relative))).digest('hex')
  await fs.writeFile(path.join(output, phase, 'manifest.json'), JSON.stringify(evidence, null, 2)); console.log(JSON.stringify(evidence))
} finally { if (app) { await app.evaluate(() => { globalThis.__settingsMatching.response?.(); globalThis.__settingsMatching.release?.() }).catch(() => {}); await app.close() } }
