import assert from 'node:assert/strict'
import { promises as fs } from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { createHash } from 'node:crypto'
import { execFileSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'
import { _electron as electron } from '../node_modules/playwright/index.mjs'

const project = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const option = name => { const index = process.argv.indexOf(name); return index < 0 ? undefined : process.argv[index + 1] }
const before = process.argv.includes('--before')
const keyboardOnly = process.argv.includes('--keyboard-only')
const phase = before ? 'before' : keyboardOnly ? 'keyboard' : 'after'
const output = path.join(project, '.artifacts', 'iteration-02', 'cover-preferences')
const captureRoot = path.join(output, phase)
const baseline = path.resolve(option('--baseline-asar') ?? path.join(output, 'baseline-app.asar'))
const runtimeMain = before ? path.join(baseline, 'dist-electron', 'main.cjs') : path.join(project, 'dist-electron', 'main.cjs')
const sourceCommit = option('--source-commit') ?? execFileSync('git', ['rev-parse', 'HEAD'], { cwd: project, encoding: 'utf8' }).trim()
const sourceDirty = !before && !!execFileSync('git', ['status', '--porcelain', '--', 'src', 'electron'], { cwd: project, encoding: 'utf8' }).trim()
const sourceLabel = before ? `baseline ${sourceCommit}` : sourceDirty ? `working tree based on ${sourceCommit}` : `commit ${sourceCommit}`
const hash = bytes => createHash('sha256').update(bytes).digest('hex')
const tempRoot = path.resolve(os.tmpdir())
const temporary = await fs.mkdtemp(path.join(tempRoot, 'the-order-cover-preferences-'))
const profile = path.join(temporary, 'profile')
const date = '2026-10-03T00:00:00.000Z'
const mainTitle = '当星光再次照亮那个没有名字的小镇时我们仍然记得夏日约定与遥远海岸的回声 · 合成长标题封面测试'
const names = [mainTitle, '合成空封面列表', '合成损坏封面', '合成旧手选封面']
const image = (id, file, width, height, vote_count = 0, sexual = 'safe') => ({ id, url: `https://cover-preferences.invalid/${file}.svg`, width, height, vote_count, sexual })
const portrait = image('991000', 'cover-0', 600, 900)
const wide = image('991003', 'cover-3', 1000, 600, 99)
const square = image('991002', 'cover-2', 800, 800, 3)
const broken = image('991099', 'broken', 600, 900, 1)
const explicit = image('991007', 'cover-7', 600, 900, 100, 'explicit')
const normalizedCover = raw => ({ id: raw.id, url: raw.url, width: raw.width, height: raw.height, votes: raw.vote_count, sexual: raw.sexual })
const rawWork = (id, title, cover, covers) => ({ object: 'work', id, display_name: title, cover, covers, refs: [], titles: [], tags: [], characters: [], ratings: [], companies: [], intros: [] })
const works = [
  rawWork('981001', mainTitle, portrait, [portrait, wide, square, broken, explicit]),
  rawWork('981002', names[1], null, []),
  rawWork('981003', names[2], broken, [broken]),
  rawWork('981004', names[3], portrait, [portrait]),
]
const metadata = raw => ({ id: raw.id, title: raw.display_name, originalTitle: raw.display_name, aliases: [], coverUrl: raw.cover?.url ?? null, bannerUrl: null, description: '仅用于封面交互验证的合成作品；不含真实书架、游玩记录或密钥。', releaseDate: null, releaseDatePrecision: null, developers: ['虚构测试制作室'], tags: ['合成测试'], ratings: [], characters: [], references: [], webUrl: null, contentRating: null, fetchedAt: date })
const games = works.map((work, index) => ({
  id: `cover-preference-${index}`, name: names[index], nameIsCustom: true, metadata: metadata(work), installations: [], preferredInstallationId: null,
  status: 'planned', personalRating: index === 0 ? 0 : null, favorite: index === 0, categories: ['合成测试'], customTags: ['隔离验证'], notes: `Saved synthetic note ${index}`,
  totalPlaySeconds: 0, playCount: 0, sessions: [], addedAt: date, lastPlayedAt: null,
  ...(index === 3 ? { coverChoice: { workId: work.id, cover: normalizedCover({ ...wide, id: '999999' }) }, nonPortraitCoverDisplay: 'blur' } : {}),
}))
const library = { format: 'kun-gal-manager', version: 1, exportedAt: date, games }
const fixtureSha256 = hash(JSON.stringify(library))
const manifest = {
  phase, sourceCommit, sourceDirty, sourceLabel, sourceHashes: {}, fixtureSha256, syntheticOnly: true, formalProfileAccessed: false, realApiCalls: 0, realGameExecutions: 0,
  fixture: 'Four fictional games; original CC0 SVG covers from tests/ui-fixtures. Every HTTP/S renderer request is blocked or redirected to a local protocol handler. Catalogue transport is mocked; a synthetic credential is encrypted only in the temporary test profile.',
  runtimeMain, runtimeHashes: {}, screenshots: [], checks: [], measurements: [], rendererErrors: [], transport: null,
  zoomExplanation: 'Requested content dimensions are Electron DIP. Screenshots keep host display scaling; actual CSS viewport and devicePixelRatio are recorded. Zoom 1.5 is webContents.setZoomFactor(1.5), not a different screenshot crop.',
}
let application, page

// Generated test-only entry; no production IPC or image policy is changed.
const bootstrap = String.raw`
const path = require('node:path')
const fs = require('node:fs/promises')
const { app, session, protocol, dialog, ipcMain } = require('electron')
const root = process.env.KGM_COVER_TEST_PROJECT
const control = globalThis.__coverPreferences = { works:JSON.parse(process.env.KGM_COVER_TEST_WORKS), requests:[], blockedNetwork:[], coverRequests:[], failId:null, holdId:null, heldId:null, release:null, unexpected:[], launches:[] }
dialog.showErrorBox = (title, message) => { console.error(title, message); app.exit(1) }
dialog.showMessageBox = async () => { throw new Error('Unexpected native dialog in synthetic cover test') }
const registerProtocol = protocol.handle.bind(protocol)
protocol.handle = (scheme, handler) => registerProtocol(scheme, async request => {
  const url = new URL(request.url)
  if (scheme === 'kun-manager' && url.pathname.startsWith('/cover-preferences/')) {
    const file = url.pathname.split('/').pop()
    control.coverRequests.push(file)
    if (!/^cover-[0-7]\.svg$/.test(file)) return new Response('Synthetic missing cover', { status:404 })
    return new Response(await fs.readFile(path.join(root,'tests','ui-fixtures',file)), { headers:{'content-type':'image/svg+xml'} })
  }
  return handler(request)
})
const registerIpc = ipcMain.handle.bind(ipcMain)
ipcMain.handle = (channel, handler) => registerIpc(channel, (...args) => {
  if (channel === 'kun-manager:launchGame') { control.launches.push(args[1]); throw new Error('Synthetic games must never launch') }
  return handler(...args)
})
globalThis.fetch = async (input, init) => {
  const url = new URL(String(input))
  const work = control.works.find(work => url.pathname === '/v2/catalog/works/' + work.id)
  if (url.origin !== 'https://api.nextmoe.dev' || !work || init?.method && init.method !== 'GET') { control.unexpected.push('Unexpected catalogue transport'); throw new Error('Unexpected synthetic transport') }
  if (new Headers(init?.headers).get('Authorization') !== 'Bearer synthetic-cover-preferences-key') { control.unexpected.push('Unexpected synthetic credential'); throw new Error('Unexpected synthetic credential') }
  control.requests.push({ id:work.id, include:url.searchParams.get('include'), nsfw:url.searchParams.get('nsfw') })
  if (control.holdId === work.id) { control.holdId=null; control.heldId=work.id; await new Promise(resolve => { control.release=resolve }); control.heldId=null }
  if (control.failId === work.id) return new Response(JSON.stringify({ error:{ message:'Synthetic catalogue rejection' } }), { status:403, headers:{'content-type':'application/json'} })
  return new Response(JSON.stringify(work), { headers:{'content-type':'application/json'} })
}
app.whenReady().then(() => session.defaultSession.webRequest.onBeforeRequest({ urls:['http://*/*','https://*/*'] }, (details, callback) => {
  const url=new URL(details.url)
  if (url.origin === 'https://cover-preferences.invalid') { callback({ redirectURL:'kun-manager://app/cover-preferences/' + url.pathname.split('/').pop() }); return }
  control.blockedNetwork.push(url.origin); callback({cancel:true})
}))
require(process.env.KGM_COVER_TEST_RUNTIME_MAIN)
`
const env = { ...process.env }
for (const key of Object.keys(env)) if (key.startsWith('KGM_')) delete env[key]
delete env.ELECTRON_RUN_AS_NODE
delete env.NODE_OPTIONS
Object.assign(env, { KGM_DATA_DIR: profile, KGM_COVER_TEST_PROJECT: project, KGM_COVER_TEST_WORKS: JSON.stringify(works), KGM_COVER_TEST_RUNTIME_MAIN: runtimeMain })
async function poll(read, predicate, label) {
  for (let attempt = 0; attempt < 160; attempt++) { const value = await read(); if (predicate(value)) return value; await page.waitForTimeout(30) }
  throw Error(`Timed out: ${label}`)
}
const snapshot = async () => {
  const value = await page.evaluate(() => window.kunManager.getState())
  assert.equal(path.resolve(value.dataPath), path.resolve(profile)); assert.equal(value.activeSession, null)
  return value
}
const mainGame = async () => (await snapshot()).games.find(game => game.id === 'cover-preference-0')
const picker = () => page.getByRole('region', { name: '选择封面', exact: true })
const saveButton = () => picker().getByRole('button', { name: '保存封面设置', exact: true })
const note = () => page.locator('.manage-form .field-label').filter({ hasText: '游玩笔记' }).locator('textarea')
const manage = () => page.locator('.detail-tabs').getByRole('button', { name: '管理', exact: true }).click()
const overview = () => page.locator('.detail-tabs').getByRole('button', { name: '作品资料', exact: true }).click()
async function closeDetail() { await page.getByRole('dialog').getByRole('button', { name: '关闭', exact: true }).click(); await page.getByRole('dialog').waitFor({ state: 'hidden' }) }
async function openGame(index = 0) { await page.getByRole('button', { name: names[index], exact: true }).click(); await page.getByRole('dialog').waitFor() }
async function openPicker() { await page.getByRole('button', { name: '封面与展示', exact: true }).click(); await picker().waitFor() }
async function pickerReady() { await poll(() => picker().getAttribute('aria-busy'), value => value !== 'true', 'picker completion') }
async function closePicker() { await picker().getByRole('button', { name: '收起封面选择', exact: true }).click(); await picker().waitFor({ state: 'hidden' }) }
async function setFakeKey(clear = false) {
  await page.evaluate(async clear => { const settings = (await window.kunManager.getState()).settings; await window.kunManager.saveSettings({ ...settings, apiKey: clear ? undefined : 'synthetic-cover-preferences-key', clearApiKey: clear, includeNsfw: false, readExecutableMetadata: false }) }, clear)
}
async function start() {
  application = await electron.launch({ executablePath: path.join(project, 'node_modules', 'electron', 'dist', 'electron.exe'), args: [path.join(output, 'bootstrap.cjs')], env, timeout: 30000 })
  page = await application.firstWindow()
  page.on('pageerror', error => manifest.rendererErrors.push(error.message))
  await page.waitForFunction(() => window.kunManager !== undefined)
  assert.match(page.url(), /^kun-manager:\/\/app\/index.html/)
  await snapshot()
  await page.locator('.library-card').first().waitFor()
}
async function resize(condition) {
  await application.evaluate(({ BrowserWindow }, value) => { const window = BrowserWindow.getAllWindows()[0]; window.setContentSize(value.width, value.height); window.webContents.setZoomFactor(value.zoom) }, condition)
  await page.waitForTimeout(200)
  await page.evaluate(() => window.scrollTo(0, 0))
}
async function imageReady(selector) {
  await page.waitForFunction(selector => { const image = document.querySelector(selector); return image?.complete && image.naturalWidth > 0 }, selector)
}
async function measure(state, condition) {
  const result = await page.evaluate(() => {
    const rect = element => element?.getBoundingClientRect().toJSON() ?? null
    const dialog = document.querySelector('[role="dialog"]'), section = document.querySelector('.cover-picker')
    const actions = [...(section?.querySelectorAll('button,select') ?? [])].filter(element => element.getBoundingClientRect().width)
    return { viewport: { width: innerWidth, height: innerHeight, dpr: devicePixelRatio }, documentWidth: document.documentElement.scrollWidth,
      dialog: rect(dialog), picker: rect(section), dialogScrollWidth: dialog?.scrollWidth ?? null, dialogClientWidth: dialog?.clientWidth ?? null,
      actions: actions.map(element => ({ label: element.getAttribute('aria-label') ?? element.textContent.trim(), rect: rect(element) })) }
  })
  assert.ok(result.documentWidth <= result.viewport.width + 1, `${state}: document horizontal overflow`)
  if (result.dialog) {
    assert.ok(result.dialog.right <= result.viewport.width + 1, `${state}: dialog right edge`)
    assert.ok(result.dialog.left >= -1, `${state}: dialog left edge`)
    assert.ok(result.dialogScrollWidth <= result.dialogClientWidth + 1, `${state}: dialog horizontal overflow`)
  }
  if (result.picker) for (const action of result.actions) {
    assert.ok(action.rect.right <= result.picker.right + 2, `${state}: control overflow ${action.label}`)
    assert.ok(action.rect.left >= result.picker.left - 2, `${state}: control outside picker ${action.label}`)
  }
  manifest.measurements.push({ state, ...condition, ...result })
  return result
}
async function shot(state, condition) {
  // Wait for the real modal and image opacity transitions to finish; two RAFs
  // alone can capture a translucent opening frame instead of the final UI.
  await page.waitForTimeout(350)
  await measure(state, condition)
  await page.mouse.move(0, 0)
  await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))))
  const bytes = Buffer.from(await application.evaluate(async ({ BrowserWindow }) => (await BrowserWindow.getAllWindows()[0].webContents.capturePage()).toPNG().toString('base64')), 'base64')
  const filename = `${state}-${condition.width}x${condition.height}-zoom${condition.zoom}.png`
  await fs.writeFile(path.join(captureRoot, filename), bytes)
  manifest.screenshots.push({ file: `${phase}/${filename}`, state, ...condition, sha256: hash(bytes), pixelWidth: bytes.readUInt32BE(16), pixelHeight: bytes.readUInt32BE(20) })
}
async function saveAndWait(id, display) {
  await saveButton().click()
  await poll(mainGame, game => (game.coverChoice?.cover.id ?? null) === id && game.nonPortraitCoverDisplay === display, 'saved cover preference')
  await pickerReady()
  assert.equal(await saveButton().isDisabled(), true)
}
async function presentation(mode, source) {
  await imageReady('.detail-foreground')
  await page.waitForFunction(title => { const card = [...document.querySelectorAll('.library-card')].find(card => card.querySelector('.card-title')?.textContent.trim() === title); const image = card?.querySelector('.cover-foreground'); return image?.complete && image.naturalWidth > 0 }, mainTitle)
  const result = await page.evaluate(title => {
    const card = [...document.querySelectorAll('.library-card')].find(card => card.querySelector('.card-title')?.textContent.trim() === title)
    const read = (root, foreground, backdrop) => { const image = root.querySelector(foreground); return { src: image?.getAttribute('src'), fit: image ? getComputedStyle(image).objectFit : null, backdrops: root.querySelectorAll(backdrop).length } }
    return { detail: read(document.querySelector('.detail-artwork'), '.detail-foreground', '.detail-backdrop'), card: read(card, '.cover-foreground', '.cover-backdrop'), preview: read(document.querySelector('.picker-preview'), '.preview-art', '.preview-backdrop') }
  }, mainTitle)
  for (const [location, value] of Object.entries(result)) {
    assert.equal(value.src, source, `${location}: chosen source`)
    assert.equal(value.fit, mode === 'crop' ? 'cover' : 'contain', `${location}: ${mode} foreground`)
    assert.equal(value.backdrops, mode === 'blur' ? 1 : 0, `${location}: ${mode} background`)
  }
  return result
}
const record = value => { manifest.checks.push(value); console.log(value) }
const conditions = [{ width: 1440, height: 960, zoom: 1 }, { width: 1280, height: 720, zoom: 1 }, { width: 1440, height: 960, zoom: 1.5 }, { width: 1280, height: 720, zoom: 1.5 }]
const normal = conditions[0]

try {
  await fs.mkdir(captureRoot, { recursive: true }); await fs.mkdir(profile, { recursive: true })
  await fs.writeFile(path.join(profile, 'library.json'), JSON.stringify(library))
  await fs.writeFile(path.join(output, 'bootstrap.cjs'), bootstrap)
  if (before) manifest.runtimeHashes.appAsar = hash(await fs.readFile(baseline))
  else {
    const build = await fs.readFile(path.join(project, 'dist', 'index.html'), 'utf8')
    const js = /src="([^"]+\.js)"/.exec(build)?.[1]
    assert.ok(js, 'Built frontend entry missing')
    const asset = await fs.readFile(path.join(project, 'dist', js.replace(/^\//, '')), 'utf8')
    assert.ok(asset.includes('保存封面设置'), 'Build is stale: compile this iteration before the UI test')
    manifest.runtimeHashes.frontend = hash(asset)
    manifest.runtimeHashes.electron = hash(await fs.readFile(runtimeMain))
    for (const file of execFileSync('rg', ['--files', 'src', 'electron'], { cwd: project, encoding: 'utf8' }).trim().split(/\r?\n/).filter(Boolean)) {
      manifest.sourceHashes[file.replaceAll('\\', '/')] = hash(await fs.readFile(path.join(project, file)))
    }
    try { const previous = JSON.parse(await fs.readFile(path.join(output, 'before', 'manifest.json'), 'utf8')); assert.equal(previous.fixtureSha256, fixtureSha256, 'Before and after must use identical fixtures') } catch (error) { if (error.code !== 'ENOENT') throw error }
  }
  await start(); await setFakeKey()
  if (keyboardOnly) {
    const condition = conditions[3]
    await resize(condition); await openGame(); await openPicker(); await pickerReady()
    await picker().getByRole('radio', { name: '封面 2', exact: true }).click()
    await picker().getByRole('combobox', { name: '非竖版封面展示', exact: true }).selectOption('plain')
    await picker().getByRole('combobox', { name: '非竖版封面展示', exact: true }).focus()
    await page.keyboard.press('Tab')
    assert.equal(await page.evaluate(() => document.activeElement?.getAttribute('aria-label')), '封面 2')
    await page.keyboard.press('Tab')
    assert.equal(await page.evaluate(() => document.activeElement?.textContent.trim()), '保存封面设置')
    assert.equal(await saveButton().isDisabled(), false)
    await page.keyboard.press('Enter')
    await poll(mainGame, game => game.coverChoice?.cover.id === wide.id && game.nonPortraitCoverDisplay === 'plain', 'keyboard save persisted')
    await pickerReady(); await presentation('plain', wide.url); await shot('keyboard-save', condition)
    const transport = await application.evaluate(() => globalThis.__coverPreferences)
    assert.deepEqual(transport.unexpected, []); assert.deepEqual(transport.blockedNetwork, []); assert.deepEqual(transport.launches, [])
    assert.deepEqual(manifest.rendererErrors, [])
    manifest.transport = { requests: transport.requests, fixtureImageRequests: transport.coverRequests, blockedNetwork: [], unexpected: [], launches: [] }
    record('At 1280×720 with 150% zoom, native Tab reaches the selected radio then Save; Enter saves the new image and display through real IPC, with scrolling and no horizontal overflow')
  } else {
  // First save identical default-state evidence before any preference changes.
  for (const condition of conditions) {
    await resize(condition); await imageReady('.library-card .cover-foreground'); await shot('default-library', condition)
    await openGame(); await imageReady(before ? '.detail-aside .detail-cover' : '.detail-foreground'); await shot('default-detail', condition); await closeDetail()
  }
  record('Identical default fixture captured at 1440×960 and 1280×720, zoom 1 and 1.5')
  if (!before) {
    await resize(normal); await openGame(); await openPicker(); await pickerReady()
    assert.equal(await picker().getByRole('radio').count(), 5, 'Explicit thumbnail must be filtered out')
    assert.equal(await picker().getByRole('radio', { name: '资料源默认封面', exact: true }).getAttribute('aria-checked'), 'true')
    await picker().getByRole('radio', { name: '资料源默认封面', exact: true }).focus()
    await page.keyboard.press('ArrowRight'); await page.keyboard.press('ArrowRight')
    assert.equal(await picker().getByRole('radio', { name: '封面 2', exact: true }).getAttribute('aria-checked'), 'true')
    assert.equal(await page.evaluate(() => document.activeElement?.getAttribute('aria-label')), '封面 2')
    await shot('keyboard-wide-draft', normal)
    await saveAndWait(wide.id, 'blur'); await presentation('blur', wide.url); await shot('wide-blur', normal)
    record('Confirmed default can be replaced using real detail UI; Arrow keys move selection and native focus')

    await manage(); await note().fill('Unsaved management draft survives cover changes'); await overview()
    const requestCount = await application.evaluate(() => globalThis.__coverPreferences.requests.length)
    for (const mode of ['plain', 'crop']) {
      await picker().getByRole('combobox', { name: '非竖版封面展示', exact: true }).selectOption(mode)
      await saveAndWait(wide.id, mode); await presentation(mode, wide.url); await shot(`wide-${mode}`, normal)
    }
    assert.equal(await application.evaluate(() => globalThis.__coverPreferences.requests.length), requestCount, 'Display-only changes must not refetch or require authentication')
    await manage(); assert.equal(await note().inputValue(), 'Unsaved management draft survives cover changes'); await overview()
    assert.equal((await mainGame()).notes, 'Saved synthetic note 0')
    record('All three nonportrait modes agree in card, detail and picker; display-only saves preserve unsaved management draft and issue no catalogue request')

    await picker().getByRole('radio', { name: '封面 3', exact: true }).click()
    await picker().getByRole('combobox', { name: '非竖版封面展示', exact: true }).selectOption('plain')
    await saveAndWait(square.id, 'plain'); await presentation('plain', square.url); await shot('square-plain', normal)
    await picker().getByRole('radio', { name: '资料源默认封面', exact: true }).click(); await saveAndWait(null, 'plain')
    assert.equal((await mainGame()).metadata.coverUrl, portrait.url)
    await imageReady('.detail-foreground'); assert.equal(await page.locator('.detail-foreground').getAttribute('src'), portrait.url)
    await shot('restored-default', normal)
    record('An already chosen cover can be changed again or restored to the source default; high votes do not override the default')

    await picker().getByRole('radio', { name: '封面 2', exact: true }).click()
    await picker().getByRole('combobox', { name: '非竖版封面展示', exact: true }).selectOption('crop')
    await saveAndWait(wide.id, 'crop'); await closeDetail()
    const persisted = await snapshot()
    assert.deepEqual(JSON.parse(await fs.readFile(path.join(profile, 'library.json'), 'utf8')).games, persisted.games)
    const previousTransport = await application.evaluate(() => globalThis.__coverPreferences)
    await application.close(); application = undefined; await start()
    assert.deepEqual((await snapshot()).games, persisted.games)
    assert.deepEqual(await application.evaluate(() => globalThis.__coverPreferences.requests), [])
    await resize(normal); await openGame(); await openPicker(); await pickerReady(); await presentation('crop', wide.url)
    record('Clean native restart and library JSON retain personal cover and presentation without automatic catalogue refresh')

    await closePicker(); await setFakeKey(true); await openPicker(); await pickerReady()
    await picker().getByRole('alert').waitFor()
    const noKeyRequests = await application.evaluate(() => globalThis.__coverPreferences.requests.length)
    await picker().getByRole('combobox', { name: '非竖版封面展示', exact: true }).selectOption('plain'); await saveAndWait(wide.id, 'plain')
    await picker().getByRole('radio', { name: '资料源默认封面', exact: true }).click(); await saveAndWait(null, 'plain')
    assert.equal(await application.evaluate(() => globalThis.__coverPreferences.requests.length), noKeyRequests)
    await shot('no-key-default-recovery', normal)
    record('After a missing-key load failure, retaining the current cover with new display and restoring default both succeed offline')

    await closePicker(); await setFakeKey()
    await application.evaluate(() => { globalThis.__coverPreferences.holdId = '981001' })
    await openPicker(); await poll(() => application.evaluate(() => globalThis.__coverPreferences.heldId), id => id === '981001', 'held catalogue request')
    await picker().getByText('正在读取可选封面…', { exact: true }).waitFor()
    await picker().getByRole('combobox', { name: '非竖版封面展示', exact: true }).selectOption('blur')
    assert.equal(await saveButton().isDisabled(), true)
    await shot('loading-covers', normal)
    await application.evaluate(() => globalThis.__coverPreferences.release()); await pickerReady()
    await closePicker(); await application.evaluate(() => { globalThis.__coverPreferences.failId = '981001' }); await openPicker(); await pickerReady()
    await picker().getByRole('alert').waitFor(); await shot('catalogue-failure', normal)
    await application.evaluate(() => { globalThis.__coverPreferences.failId = null }); await picker().getByRole('button', { name: '重试', exact: true }).click(); await pickerReady()
    assert.equal(await picker().getByRole('alert').count(), 0)
    record('Loading disables premature saves; catalogue failure stays visible and the real retry action recovers the candidate list')

    for (const condition of conditions) {
      await resize(condition); await picker().getByRole('radio', { name: '封面 2', exact: true }).click()
      await picker().getByRole('combobox', { name: '非竖版封面展示', exact: true }).selectOption('blur')
      if (!await saveButton().isDisabled()) await saveAndWait(wide.id, 'blur')
      await presentation('blur', wide.url); await shot('picker-wide-blur', condition)
      // Keyboard selection must stay reachable even in a short zoomed dialog.
      await picker().getByRole('radio', { name: '封面 2', exact: true }).focus(); await page.keyboard.press('End')
      assert.equal(await page.evaluate(() => document.activeElement?.getAttribute('aria-label')), '封面 4')
      await picker().getByRole('radio', { name: '封面 2', exact: true }).click()
      await picker().getByRole('combobox', { name: '非竖版封面展示', exact: true }).focus()
      assert.equal(await page.evaluate(() => document.activeElement?.getAttribute('aria-label')), '非竖版封面展示')
    }
    record('Long title, radios, display select and save remain usable without horizontal overflow at both window sizes and 150% zoom')
    await resize(normal); await closeDetail()
    await openGame(1); await openPicker(); await pickerReady(); assert.equal(await picker().getByRole('radio').count(), 1)
    await picker().getByText('资料源暂无其他符合当前分级设置的封面，仍可使用默认封面。', { exact: true }).waitFor(); await shot('empty-covers', normal); await closeDetail()
    await openGame(2); await page.locator('.detail-art-placeholder').getByText('封面暂不可用', { exact: true }).waitFor()
    await openPicker(); await pickerReady(); await picker().getByText('封面暂不可用', { exact: true }).waitFor(); await shot('broken-cover', normal); await closeDetail()
    await openGame(3); await openPicker(); await pickerReady(); await picker().getByText(/当前手选封面不在这次返回的列表中/).waitFor()
    await picker().getByRole('combobox', { name: '非竖版封面展示', exact: true }).selectOption('plain'); await saveButton().click(); await pickerReady()
    assert.equal((await snapshot()).games.find(game => game.id === 'cover-preference-3').coverChoice.cover.id, '999999')
    await shot('legacy-retained-cover', normal)
    record('Empty lists, failed images and a saved image absent from fresh catalogue each have explicit usable states')
    const transport = await application.evaluate(() => globalThis.__coverPreferences)
    assert.deepEqual(transport.unexpected, []); assert.deepEqual(transport.blockedNetwork, []); assert.deepEqual(transport.launches, [])
    assert.deepEqual(previousTransport.unexpected, []); assert.deepEqual(previousTransport.blockedNetwork, []); assert.deepEqual(previousTransport.launches, [])
    manifest.transport = { beforeRestartRequests: previousTransport.requests, afterRestartRequests: transport.requests, fixtureImageRequests: transport.coverRequests, blockedNetwork: [], unexpected: [], launches: [] }
    assert.deepEqual(manifest.rendererErrors, [])
  }
  }
  manifest.status = 'passed'
} catch (error) {
  manifest.status = 'failed'; manifest.failure = error.stack ?? error.message
  if (application && page) { try { await shot('failure', normal) } catch {} }
  throw error
} finally {
  await fs.mkdir(captureRoot, { recursive: true }); await fs.writeFile(path.join(captureRoot, 'manifest.json'), JSON.stringify(manifest, null, 2))
  const manifests = []
  for (const name of ['before', 'after', 'keyboard']) { try { manifests.push(JSON.parse(await fs.readFile(path.join(output, name, 'manifest.json'), 'utf8'))) } catch (error) { if (error.code !== 'ENOENT') throw error } }
  const index = ['# 封面偏好：真实 Electron 合成验证', '', '图片只含虚构作品与 tests/ui-fixtures 原创 CC0 SVG；真实接口被模拟，正式资料未访问。窗口、缩放与合成种子保持一致。缩放后详情允许正常纵向滚动；保存截图可能保留真实成功提示。', '', ...manifests.flatMap(value => [`## ${value.phase} · ${value.status}`, '', `源码记录：${value.sourceLabel ?? value.sourceCommit}；合成书架 SHA256：${value.fixtureSha256}。实际构建哈希与 CSS 窗口尺寸见 ${value.phase}/manifest.json。`, '', ...value.checks.map(check => `- ${check}`), '', '| 状态 | 请求窗口 | 缩放 | 截图 |', '| --- | --- | --- | --- |', ...value.screenshots.map(shot => `| ${shot.state} | ${shot.width} × ${shot.height} | ${shot.zoom} | [查看](${shot.file}) |`), ''])].join('\n')
  await fs.writeFile(path.join(output, 'README.md'), index)
  if (application) await application.close()
  if (path.dirname(path.resolve(temporary)) !== tempRoot || !path.basename(temporary).startsWith('the-order-cover-preferences-')) throw Error('Unexpected synthetic profile cleanup path')
  await fs.rm(temporary, { recursive: true, force: true })
  console.log(JSON.stringify({ phase, status: manifest.status, checks: manifest.checks.length, screenshots: manifest.screenshots.length, output, syntheticOnly: true, formalProfileAccessed: false }))
}
