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
const candidate = process.argv.includes('--candidate')
const layoutOnly = process.argv.includes('--layout-only')
const settingsOnly = process.argv.includes('--settings-only')
assert.ok(!(before && candidate), '--before and --candidate select different runtimes')
assert.ok(!(layoutOnly && settingsOnly), 'Choose one focused verification mode')
assert.ok(!(before && settingsOnly), 'Global settings controls belong to the new candidate')
const phase = (before ? 'before' : candidate ? 'candidate' : 'after') + (layoutOnly ? '-layout' : settingsOnly ? '-settings' : '')
const output = path.join(project, '.artifacts', 'iteration-03', 'cover-personalization')
const captures = path.join(output, phase)
const baseline = path.resolve(option('--baseline-asar') ?? path.join(output, 'baseline-app.asar'))
const candidateRoot = path.join(project, '.artifacts', 'iteration-03', 'candidate', 'win-unpacked')
const runtimeMain = before ? path.join(baseline, 'dist-electron', 'main.cjs') : path.join(project, 'dist-electron', 'main.cjs')
const sourceCommit = option('--source-commit') ?? execFileSync('git', ['rev-parse', 'HEAD'], { cwd: project, encoding: 'utf8' }).trim()
const sourceDirty = !before && !!execFileSync('git', ['status', '--porcelain', '--', 'src', 'electron'], { cwd: project, encoding: 'utf8' }).trim()
const sha256 = bytes => createHash('sha256').update(bytes).digest('hex')
const tempRoot = path.resolve(os.tmpdir())
const temporary = await fs.mkdtemp(path.join(tempRoot, 'the-order-cover-personalization-'))
const profile = path.join(temporary, 'profile')
const media = path.join(temporary, 'media')
const date = '2026-10-03T00:00:00.000Z'
const names = ['合成长标题 · 当星光再次照亮那座没有名字的小镇时我们仍记得夏日约定与遥远海岸的回声', '合成方形封面 · 已指定纯色填边', '合成未关联本地游戏', '合成损坏封面']
const image = (id, file, width, height) => ({ id, url: 'https://cover-personalization.invalid/' + file + '.svg', width, height, vote_count: 1, sexual: 'safe' })
const wide = image('993003', 'cover-3', 1000, 600)
const portrait = image('993000', 'cover-0', 600, 900)
const square = image('993002', 'cover-2', 800, 800)
const broken = image('993099', 'broken', 600, 900)
const rawWork = (id, title, cover, covers) => ({ object: 'work', id, display_name: title, cover, covers, refs: [], titles: [], tags: [], characters: [], ratings: [], companies: [], intros: [] })
const works = [rawWork('983001', names[0], wide, [wide, portrait]), rawWork('983002', names[1], square, [square, portrait]), rawWork('983004', names[3], broken, [broken])]
const metadata = raw => ({ id: raw.id, title: raw.display_name, originalTitle: raw.display_name, aliases: [], coverUrl: raw.cover.url, bannerUrl: null, description: '原创合成封面与虚构条目，仅用于隔离验证。', releaseDate: null, releaseDatePrecision: null, developers: ['虚构测试制作室'], tags: ['合成测试'], ratings: [], characters: [], references: [], webUrl: null, contentRating: null, fetchedAt: date })
const games = names.map((name, index) => ({
  id: 'cover-personalization-' + index, name, nameIsCustom: true, metadata: index === 2 ? null : metadata(works[index === 3 ? 2 : index]),
  installations: index === 2 ? [{ id: 'synthetic-installation', folderPath: path.join(project, '.artifacts', 'iteration-03', 'fictional-not-scanned'), executablePath: null, arguments: [], addedAt: date }] : [],
  preferredInstallationId: index === 2 ? 'synthetic-installation' : null, status: 'planned', personalRating: index === 0 ? 0 : null,
  favorite: index === 0, categories: ['合成测试'], customTags: ['隔离验证'], notes: 'Saved synthetic note ' + index,
  totalPlaySeconds: 0, playCount: 0, sessions: [], addedAt: date, lastPlayedAt: null,
  ...(index === 1 ? { nonPortraitCoverDisplay: 'plain' } : {}),
}))
const library = { format: 'kun-gal-manager', version: 1, exportedAt: date, games }
const fixtureSha256 = sha256(JSON.stringify(library))
const conditions = [{ width: 1440, height: 940, zoom: 1 }, { width: 1000, height: 700, zoom: 1 }, { width: 1440, height: 940, zoom: 1.5 }, { width: 1000, height: 700, zoom: 1.5 }]
const normal = conditions[0]
const manifest = {
  phase, sourceCommit, sourceDirty, fixtureSha256, sourceHashes: {}, runtimeHashes: {}, runtimeMain,
  syntheticOnly: true, formalProfileAccessed: false, realApiCalls: 0, realGameExecutions: 0,
  fixture: 'Four fictional entries and original CC0 tests/ui-fixtures artwork. Local photos are generated geometric PNG/JPEG images; native dialog responses and catalogue HTTP are mocked, but production IPC, native image decoding, persistence and renderer run unchanged.',
  conditions, screenshots: [], measurements: [], checks: [], rendererErrors: [], baselineLayoutIssues: [], knownLayoutIssues: [],
}
let application, page, started = false

// Test-only dependencies are installed before production starts in the source
// mode, or after native candidate startup followed by a renderer reload.
// The HTTPS handler never leaves this process: no fixture image is downloaded.
async function installFixture({ app, session, dialog, nativeImage }, input) {
  const path = process.getBuiltinModule('node:path'), fs = process.getBuiltinModule('node:fs').promises
  const control = globalThis.__coverPersonalization = { works: input.works, requests: [], imageRequests: [], blocked: [], dialogs: [], nextDialog: null, heldDialog: false, releaseDialog: null, holdId: null, heldId: null, releaseFetch: null, unexpected: [] }
  dialog.showErrorBox = (title, message) => { console.error(title, message); app.exit(1) }
  dialog.showOpenDialog = async (...args) => {
    const options = args.at(-1), selected = control.nextDialog
    control.nextDialog = null
    control.dialogs.push({ title: options.title, filters: options.filters, properties: options.properties, kind: selected?.kind })
    if (!selected) { control.unexpected.push('Unarmed native file dialog'); throw new Error('Unarmed synthetic file dialog') }
    if (selected.hold) { control.heldDialog = true; await new Promise(resolve => { control.releaseDialog = resolve }); control.heldDialog = false }
    if (selected.kind === 'error') throw new Error('Synthetic native dialog failure')
    return selected.kind === 'cancel' ? { canceled: true, filePaths: [] } : { canceled: false, filePaths: [selected.path] }
  }
  globalThis.fetch = async (value, init) => {
    const url = new URL(String(value)), work = control.works.find(work => url.pathname === '/v2/catalog/works/' + work.id)
    if (url.origin !== 'https://api.nextmoe.dev' || !work || init?.method && init.method !== 'GET') { control.unexpected.push('Unexpected catalogue request'); throw new Error('Unexpected synthetic transport') }
    if (new Headers(init?.headers).get('Authorization') !== 'Bearer synthetic-cover-personalization-key') { control.unexpected.push('Unexpected synthetic credential'); throw new Error('Unexpected synthetic credential') }
    control.requests.push({ id: work.id, include: url.searchParams.get('include') })
    if (control.holdId === work.id) { control.holdId = null; control.heldId = work.id; await new Promise(resolve => { control.releaseFetch = resolve }); control.heldId = null }
    return new Response(JSON.stringify(work), { headers: { 'content-type': 'application/json' } })
  }
  await app.whenReady()
  session.defaultSession.webRequest.onBeforeRequest({ urls: ['http://*/*', 'https://*/*'] }, (details, callback) => {
    const url = new URL(details.url)
    if (url.origin === 'https://cover-personalization.invalid') callback({})
    else { control.blocked.push(url.origin); callback({ cancel: true }) }
  })
  session.defaultSession.protocol.handle('https', async request => {
    const url = new URL(request.url), file = url.pathname.split('/').pop()
    if (url.origin !== 'https://cover-personalization.invalid') { control.unexpected.push('Unexpected image transport'); return new Response('', { status: 403 }) }
    control.imageRequests.push(file)
    if (!/^cover-[0-7]\.svg$/.test(file)) return new Response('Synthetic missing cover', { status: 404 })
    return new Response(await fs.readFile(path.join(input.project, 'tests', 'ui-fixtures', file)), { headers: { 'content-type': 'image/svg+xml' } })
  })
  if (input.createMedia === false) return
  await fs.mkdir(input.media, { recursive: true })
  const create = async (name, width, height, jpeg) => {
    const bitmap = Buffer.alloc(width * height * 4)
    for (let y = 0; y < height; y++) for (let x = 0; x < width; x++) {
      const position = (y * width + x) * 4
      bitmap[position] = x < width / 2 ? 170 : 70
      bitmap[position + 1] = y < height / 2 ? 170 : 90
      bitmap[position + 2] = (x + y) % 150 + 70
      bitmap[position + 3] = 255
    }
    const image = nativeImage.createFromBitmap(bitmap, { width, height })
    await fs.writeFile(path.join(input.media, name), jpeg ? image.toJPEG(90) : image.toPNG())
  }
  await create('synthetic-wide.png', 1200, 800, false)
  await create('synthetic-portrait.jpg', 600, 900, true)
  await fs.writeFile(path.join(input.media, 'broken-photo.jpg'), 'Synthetic invalid image')
}

const env = { ...process.env }
for (const name of Object.keys(env)) if (name.startsWith('KGM_')) delete env[name]
delete env.ELECTRON_RUN_AS_NODE
delete env.NODE_OPTIONS
Object.assign(env, { KGM_DATA_DIR: profile, KGM_PERSONALIZATION_INPUT: JSON.stringify({ project, media, works }), KGM_PERSONALIZATION_MAIN: runtimeMain })
const record = message => { manifest.checks.push(message); console.log(message) }
async function poll(read, predicate, label) {
  for (let attempt = 0; attempt < 200; attempt++) { const value = await read(); if (predicate(value)) return value; await page.waitForTimeout(25) }
  throw Error('Timed out: ' + label)
}
async function snapshot() {
  const state = await page.evaluate(() => window.kunManager.getState())
  assert.equal(path.resolve(state.dataPath), path.resolve(profile)); assert.equal(state.activeSession, null)
  return state
}
const entry = async index => (await snapshot()).games.find(game => game.id === games[index].id)
const picker = () => page.getByRole('region', { name: '选择封面', exact: true })
const display = () => picker().getByRole('combobox', { name: '封面展示', exact: true })
const saveCover = () => picker().getByRole('button', { name: '保存封面设置', exact: true })
const note = () => page.locator('.manage-form .field-label').filter({ hasText: '游玩笔记' }).locator('textarea')
const tab = label => page.locator('.detail-tabs').getByRole('button', { name: label, exact: true }).click()
const navigate = label => page.locator('.sidebar .nav-item').filter({ hasText: label }).click()
async function closeDetail() { await page.getByRole('dialog').getByRole('button', { name: '关闭', exact: true }).click(); await page.getByRole('dialog').waitFor({ state: 'hidden' }) }
async function openGame(index) { await navigate('我的书架'); await page.getByRole('button', { name: names[index], exact: true }).click(); await page.getByRole('dialog').waitFor() }
async function openPicker() { await page.getByRole('button', { name: '封面与展示', exact: true }).click(); await picker().waitFor(); await pickerReady() }
async function pickerReady() { await poll(() => picker().getAttribute('aria-busy'), value => value !== 'true', 'picker completion') }
async function dialogResult(kind, filename, hold = false) {
  await application.evaluate((_, value) => { globalThis.__coverPersonalization.nextDialog = value }, { kind, path: filename && path.join(media, filename), hold })
}
async function pickPhoto(filename, hold = false, kind = 'file') {
  await dialogResult(kind, filename, hold)
  await picker().getByRole('button', { name: /^(选择|更换)本地图片$/ }).click()
  if (!hold) await pickerReady()
}
async function waitImage(selector) { await page.waitForFunction(selector => { const image = document.querySelector(selector); return image?.complete && image.naturalWidth > 0 }, selector) }
async function resize(condition) {
  await application.evaluate(({ BrowserWindow }, value) => { const window = BrowserWindow.getAllWindows()[0]; window.setContentSize(value.width, value.height); window.webContents.setZoomFactor(value.zoom) }, condition)
  await page.waitForTimeout(200)
}
async function capture(state, condition, writeImage = true) {
  await page.waitForTimeout(300)
  const measurements = await page.evaluate(() => {
    const rect = element => element?.getBoundingClientRect().toJSON() ?? null
    const dialog = document.querySelector('[role="dialog"]'), picker = document.querySelector('.cover-picker')
    const signature = [...document.querySelectorAll('.section-signature')].filter(element => element.getBoundingClientRect().width)
    return { viewport: { width: innerWidth, height: innerHeight, dpr: devicePixelRatio }, documentWidth: document.documentElement.scrollWidth, documentClientWidth: document.documentElement.clientWidth,
      dialog: rect(dialog), dialogWidth: dialog?.clientWidth, dialogScrollWidth: dialog?.scrollWidth, picker: rect(picker),
      actions: [...(picker?.querySelectorAll('button,select') ?? [])].filter(element => element.getBoundingClientRect().width).map(element => ({ label: element.getAttribute('aria-label') ?? element.textContent.trim(), rect: rect(element) })),
      headings: [...document.querySelectorAll('h1,h2')].filter(element => element.getBoundingClientRect().width).map(element => ({ text: element.textContent.trim(), fontSize: getComputedStyle(element).fontSize, color: getComputedStyle(element).color })),
      decorations: signature.map(element => ({ text: element.textContent.trim(), fontSize: getComputedStyle(element).fontSize, color: getComputedStyle(element).color, rect: rect(element), parent: rect(element.parentElement), scrollWidth: element.scrollWidth, clientWidth: element.clientWidth })) }
  })
  manifest.measurements.push({ state, ...condition, ...measurements })
  if (measurements.documentWidth > measurements.viewport.width + 1) {
    const issue = { state, ...condition, documentWidth: measurements.documentWidth, documentClientWidth: measurements.documentClientWidth, viewportWidth: measurements.viewport.width }
    if (before) {
      if (!manifest.baselineLayoutIssues.some(value => value.state === state && value.width === condition.width && value.height === condition.height && value.zoom === condition.zoom)) manifest.baselineLayoutIssues.push(issue)
    }
    else {
      const previous = JSON.parse(await fs.readFile(path.join(output, 'before-layout', 'manifest.json'), 'utf8'))
      const baselineIssue = previous.baselineLayoutIssues.find(value => value.state === state && value.width === condition.width && value.height === condition.height && value.zoom === condition.zoom)
      assert.ok(baselineIssue && measurements.documentWidth <= baselineIssue.documentWidth + 1, state + ': new or increased page overflow')
      if (!manifest.knownLayoutIssues.some(value => value.state === state && value.width === condition.width && value.height === condition.height && value.zoom === condition.zoom)) manifest.knownLayoutIssues.push({ ...issue, baselineDocumentWidth: baselineIssue.documentWidth, classification: 'Existing baseline overflow remains unresolved' })
    }
  }
  for (const decoration of measurements.decorations) {
    assert.ok(decoration.rect.left >= decoration.parent.left - 1 && decoration.rect.right <= decoration.parent.right + 1, state + ': decoration outside its container')
    assert.ok(decoration.scrollWidth <= decoration.clientWidth + 1, state + ': decoration text clipped')
  }
  if (measurements.dialog) {
    assert.ok(measurements.dialog.left >= -1 && measurements.dialog.right <= measurements.viewport.width + 1, state + ': dialog bounds')
    assert.ok(measurements.dialogScrollWidth <= measurements.dialogWidth + 1, state + ': dialog overflow')
  }
  if (measurements.picker) for (const action of measurements.actions) assert.ok(action.rect.left >= measurements.picker.left - 2 && action.rect.right <= measurements.picker.right + 2, state + ': control overflow ' + action.label)
  if (!writeImage) return measurements
  await page.mouse.move(0, 0)
  const bytes = Buffer.from(await application.evaluate(async ({ BrowserWindow }) => (await BrowserWindow.getAllWindows()[0].webContents.capturePage()).toPNG().toString('base64')), 'base64')
  const filename = state + '-' + condition.width + 'x' + condition.height + '-zoom' + condition.zoom + '.png'
  await fs.writeFile(path.join(captures, filename), bytes)
  manifest.screenshots.push({ state, ...condition, file: phase + '/' + filename, sha256: sha256(bytes), pixelWidth: bytes.readUInt32BE(16), pixelHeight: bytes.readUInt32BE(20) })
  return measurements
}
async function checkPresentation(index, mode, expectedUrl, withPicker = false) {
  await waitImage('.detail-foreground')
  await page.waitForFunction(title => {
    const card = [...document.querySelectorAll('.library-card')].find(card => card.querySelector('.card-title')?.textContent.trim() === title), image = card?.querySelector('.cover-foreground')
    return image?.complete && image.naturalWidth > 0
  }, names[index])
  // Decoding may finish before Vue applies its @load-driven fit class.
  await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))))
  const actual = await page.evaluate(({ title, withPicker }) => {
    const card = [...document.querySelectorAll('.library-card')].find(card => card.querySelector('.card-title')?.textContent.trim() === title)
    const read = (root, imageClass, backdropClass) => { const image = root.querySelector(imageClass); return { src: image?.getAttribute('src'), fit: image && getComputedStyle(image).objectFit, backdrops: root.querySelectorAll(backdropClass).length } }
    return [read(card, '.cover-foreground', '.cover-backdrop'), read(document.querySelector('.detail-artwork'), '.detail-foreground', '.detail-backdrop'), ...(withPicker ? [read(document.querySelector('.picker-preview'), '.preview-art', '.preview-backdrop')] : [])]
  }, { title: names[index], withPicker })
  for (const value of actual) { assert.equal(value.src, expectedUrl); assert.equal(value.fit, mode === 'crop' ? 'cover' : 'contain'); assert.equal(value.backdrops, mode === 'blur' ? 1 : 0) }
}
async function chooseDisplay(index, value) {
  await display().selectOption(value); await saveCover().click()
  await poll(() => entry(index), game => (game.nonPortraitCoverDisplay ?? null) === (value === 'global' ? null : value), 'per-game display persistence')
  await pickerReady(); assert.equal(await saveCover().isDisabled(), true)
}
async function saveGlobal(value) {
  await navigate('偏好设置')
  await page.getByRole('combobox', { name: '封面默认展示', exact: true }).selectOption(value)
  await page.getByRole('button', { name: '应用界面设置', exact: true }).click()
  await poll(snapshot, state => state.settings.nonPortraitCoverDisplay === value, 'global display persistence')
}
async function start() {
  const fixtureInput = { project, media, works, createMedia: !started }
  env.KGM_PERSONALIZATION_INPUT = JSON.stringify(fixtureInput)
  const executablePath = candidate ? path.join(candidateRoot, 'The Order.exe') : path.join(project, 'node_modules', 'electron', 'dist', 'electron.exe')
  application = await electron.launch({ executablePath, args: candidate ? [] : [path.join(output, 'bootstrap.cjs')], env, timeout: 30000 })
  if (candidate) await application.evaluate(installFixture, fixtureInput)
  started = true
  page = await application.firstWindow()
  page.on('pageerror', error => manifest.rendererErrors.push(error.message))
  await page.waitForFunction(() => window.kunManager !== undefined)
  if (candidate) await page.reload()
  assert.match(page.url(), /^kun-manager:\/\/app\/index.html/)
  await snapshot(); await page.locator('.library-card').first().waitFor()
}

try {
  await fs.mkdir(captures, { recursive: true }); await fs.mkdir(profile, { recursive: true })
  await fs.writeFile(path.join(profile, 'library.json'), JSON.stringify(library))
  const bootstrap = 'const electron = require("electron"); (' + installFixture.toString() + ')(electron, JSON.parse(process.env.KGM_PERSONALIZATION_INPUT)); require(process.env.KGM_PERSONALIZATION_MAIN);'
  await fs.writeFile(path.join(output, 'bootstrap.cjs'), bootstrap)
  if (before || candidate) manifest.runtimeHashes.appAsar = sha256(await fs.readFile(before ? baseline : path.join(candidateRoot, 'resources', 'app.asar')))
  else {
    manifest.runtimeHashes.electron = sha256(await fs.readFile(runtimeMain))
    const html = await fs.readFile(path.join(project, 'dist', 'index.html'), 'utf8'), asset = /src="([^"]+\.js)"/.exec(html)?.[1]
    assert.ok(asset, 'Built frontend entry missing')
    manifest.runtimeHashes.frontend = sha256(await fs.readFile(path.join(project, 'dist', asset.replace(/^\//, ''))))
  }
  if (!before) {
    for (const file of execFileSync('rg', ['--files', 'src', 'electron'], { cwd: project, encoding: 'utf8' }).trim().split(/\r?\n/).filter(Boolean)) manifest.sourceHashes[file.replaceAll('\\', '/')] = sha256(await fs.readFile(path.join(project, file)))
    try { assert.equal(JSON.parse(await fs.readFile(path.join(output, 'before', 'manifest.json'), 'utf8')).fixtureSha256, fixtureSha256) } catch (error) { if (error.code !== 'ENOENT') throw error }
  }
  await start()
  await page.evaluate(async () => { const state = await window.kunManager.getState(); await window.kunManager.saveSettings({ ...state.settings, apiKey: 'synthetic-cover-personalization-key', includeNsfw: false, readExecutableMetadata: false }) })
  if (settingsOnly) {
    await resize(normal); await saveGlobal('blur')
    await page.getByRole('combobox', { name: '封面默认展示' }).evaluate(element => element.closest('.settings-panel').scrollIntoView({ block: 'start' }))
    await page.getByText('设置已保存。', { exact: true }).waitFor({ state: 'visible' })
    await page.getByText('设置已保存。', { exact: true }).waitFor({ state: 'hidden', timeout: 15000 })
    const controls = await page.evaluate(() => {
      const select = document.querySelector('[aria-label="封面默认展示"]'), panel = select.closest('.settings-panel')
      const help = [...panel.querySelectorAll('.field-help')].find(element => element.textContent.includes('横版与方形'))
      const save = [...panel.querySelectorAll('button')].find(element => element.textContent.includes('应用界面设置'))
      return [select, help, save].map(element => ({ label: element.textContent.trim(), top: element.getBoundingClientRect().top, bottom: element.getBoundingClientRect().bottom, viewport: innerHeight }))
    })
    for (const control of controls) assert.ok(control.top >= -1 && control.bottom <= control.viewport + 1, 'Global setting control or explanation must be fully visible: ' + control.label)
    await capture('global-setting-controls', normal)
    record('The saved global default, its explanation and Apply button are simultaneously visible after ordinary scrolling and natural toast dismissal')
  } else if (layoutOnly) {
    const labels = ['我的书架', '发现作品', '分类收藏', '游玩统计', '偏好设置', '资料整理']
    const slugs = ['library', 'discover', 'categories', 'statistics', 'settings', 'maintenance']
    for (const condition of conditions) {
      await resize(condition)
      for (const [index, label] of labels.entries()) {
        await navigate(label)
        if (label === '资料整理') await page.getByRole('dialog').waitFor()
        const measured = await capture('layout-' + slugs[index], condition, false)
        if (!before) {
          const heading = measured.headings.find(value => value.text.includes(label))
          const decoration = measured.decorations[0]
          assert.ok(heading && decoration && parseFloat(heading.fontSize) > parseFloat(decoration.fontSize), label + ': Chinese heading must dominate at each tested scale')
        }
        if (measured.documentWidth > measured.viewport.width + 1) await capture('layout-' + slugs[index], condition)
        if (label === '资料整理') await closeDetail()
      }
    }
    record('All six sections measured at both window sizes and zoom factors; any baseline overflow is recorded quantitatively and is not reported as a successful layout check')
  } else for (const condition of conditions) {
    await resize(condition); await waitImage('.library-card .cover-foreground'); await capture('default-library', condition)
    await openGame(0); await waitImage('.detail-foreground'); await capture('default-detail', condition); await closeDetail()
    const labels = condition === normal ? ['发现作品', '分类收藏', '游玩统计', '偏好设置'] : ['偏好设置']
    for (const label of labels) { await navigate(label); await capture('page-' + ({ '发现作品': 'discover', '分类收藏': 'categories', '游玩统计': 'statistics', '偏好设置': 'settings' })[label], condition) }
    if (condition === normal) { await navigate('资料整理'); await page.getByRole('dialog').waitFor(); await capture('page-maintenance', condition); await closeDetail() }
    await navigate('我的书架')
  }
  if (!layoutOnly && !settingsOnly) record('Identical synthetic fixture and six sections captured at 1440x940 and 1000x700, zoom 1 and 1.5')
  if (!before && !layoutOnly && !settingsOnly) {
    await resize(normal)
    const expected = { '我的书架': 'Avalon', '发现作品': 'Planetarian', '分类收藏': 'Clover', '游玩统计': 'Memoria', '偏好设置': 'Harmonia', '资料整理': 'Rewrite' }
    for (const [label, decoration] of Object.entries(expected)) {
      await navigate(label)
      if (label === '资料整理') await page.getByRole('dialog').waitFor()
      const root = label === '资料整理' ? page.getByRole('dialog') : page
      await root.locator('.section-signature').filter({ hasText: decoration }).waitFor()
      const sizes = await page.evaluate(label => {
        const element = label === '资料整理' ? document.querySelector('[role="dialog"]') : document.querySelector('.workspace')
        const heading = [...element.querySelectorAll('h1,h2')].find(node => node.getBoundingClientRect().width && node.textContent.includes(label))
        const signature = [...element.querySelectorAll('.section-signature')].find(node => node.getBoundingClientRect().width)
        return { heading: parseFloat(getComputedStyle(heading).fontSize), decoration: parseFloat(getComputedStyle(signature).fontSize) }
      }, label)
      assert.ok(sizes.heading > sizes.decoration, label + ': Chinese heading must dominate decoration')
      if (label === '资料整理') await closeDetail()
    }
    record('All six sections retain Chinese headings larger than their secondary Galgame names')

    await navigate('偏好设置'); await page.getByRole('combobox', { name: '封面默认展示' }).selectOption('crop')
    assert.equal((await snapshot()).settings.nonPortraitCoverDisplay, 'blur')
    await openGame(0); await checkPresentation(0, 'blur', wide.url); await closeDetail()
    await saveGlobal('crop'); await openGame(0); await checkPresentation(0, 'crop', wide.url); await closeDetail()
    await navigate('偏好设置'); await page.getByRole('combobox', { name: '封面默认展示' }).evaluate(element => element.closest('.settings-panel').scrollIntoView({ block: 'start' })); await capture('saved-global-setting', normal)
    await openGame(1); await checkPresentation(1, 'plain', square.url); await openPicker()
    await chooseDisplay(1, 'global'); await checkPresentation(1, 'crop', square.url, true); await capture('follows-global-crop', normal); await closeDetail()
    await saveGlobal('blur'); await openGame(0); await openPicker(); await checkPresentation(0, 'blur', wide.url, true)
    record('Global drafts remain unapplied before Save; saved defaults affect inheritors, preserve existing overrides, and a game can return to following global')

    await tab('管理'); await note().fill('Unsaved personal draft survives cover changes'); await tab('作品资料')
    const requestCount = await application.evaluate(() => globalThis.__coverPersonalization.requests.length)
    await pickPhoto('synthetic-wide.png')
    const png = (await entry(0)).localCover
    assert.match(png.dataUrl, /^data:image\/jpeg;base64,/); assert.ok(png.width > png.height)
    await checkPresentation(0, 'blur', png.dataUrl, true); await capture('local-png', normal)
    await page.getByText('本地图片已保存，之后仍可更换。', { exact: true }).waitFor({ state: 'hidden', timeout: 15000 })
    await capture('local-png-clear', normal)
    await fs.rename(path.join(media, 'synthetic-wide.png'), path.join(media, 'synthetic-wide-moved.png'))
    assert.equal(await picker().getByRole('radio', { name: '当前本地图片' }).getAttribute('aria-checked'), 'true')
    await pickPhoto(undefined, false, 'cancel')
    assert.deepEqual((await entry(0)).localCover, png); assert.equal(await picker().getByRole('alert').count(), 0)
    await display().selectOption('plain'); await pickPhoto('broken-photo.jpg')
    await picker().getByRole('alert').waitFor(); assert.deepEqual((await entry(0)).localCover, png)
    assert.equal((await entry(0)).nonPortraitCoverDisplay ?? null, null); assert.equal(await display().inputValue(), 'plain')
    await capture('invalid-photo-retains-current', normal)
    await pickPhoto(undefined, false, 'error'); await picker().getByRole('alert').waitFor(); assert.deepEqual((await entry(0)).localCover, png)
    await pickPhoto('synthetic-portrait.jpg'); const jpeg = (await entry(0)).localCover
    assert.match(jpeg.dataUrl, /^data:image\/jpeg;base64,/); assert.ok(jpeg.height > jpeg.width)
    assert.notEqual(jpeg.dataUrl, png.dataUrl); assert.equal((await entry(0)).nonPortraitCoverDisplay, 'plain')
    assert.equal(await application.evaluate(() => globalThis.__coverPersonalization.requests.length), requestCount, 'Local images never call the catalogue API')
    await tab('管理'); assert.equal(await note().inputValue(), 'Unsaved personal draft survives cover changes'); await tab('作品资料')
    assert.equal((await entry(0)).notes, 'Saved synthetic note 0')
    record('Native PNG/JPEG choices pass real image normalization and IPC; moved sources, cancellation, corrupt files and dialog failure preserve saved data and unsaved management drafts')

    await picker().getByRole('radio', { name: '资料源默认封面', exact: true }).click(); await saveCover().click(); await pickerReady()
    assert.equal((await entry(0)).localCover, undefined); assert.equal((await entry(0)).coverChoice, undefined)
    await pickPhoto('synthetic-portrait.jpg')
    await picker().getByRole('radio', { name: '封面 2', exact: true }).click(); await saveCover().click(); await pickerReady()
    assert.equal((await entry(0)).localCover, undefined); assert.equal((await entry(0)).coverChoice.cover.id, portrait.id)
    await capture('remote-replaces-local', normal); await closeDetail()
    record('Explicit source-default or remote selection removes the local image through real atomic persistence')

    await openGame(2); await openPicker()
    const beforeLocal = await application.evaluate(() => globalThis.__coverPersonalization.requests.length)
    await pickPhoto('synthetic-portrait.jpg')
    const unassociated = (await entry(2)).localCover
    assert.ok(unassociated); assert.equal((await entry(2)).metadata, null)
    assert.equal(await application.evaluate(() => globalThis.__coverPersonalization.requests.length), beforeLocal)
    await capture('unassociated-local-image', normal); await closeDetail()
    await fs.rename(path.join(media, 'synthetic-portrait.jpg'), path.join(media, 'synthetic-portrait-moved.jpg'))
    await openGame(2); await waitImage('.detail-foreground')
    assert.equal(await page.locator('.detail-foreground').getAttribute('src'), unassociated.dataUrl); await closeDetail()
    record('Unassociated local games can save photos without metadata, authentication or network; reopening after source removal still shows the embedded copy')

    await openGame(0); await openPicker(); await pickPhoto('synthetic-wide-moved.png', true)
    await poll(() => application.evaluate(() => globalThis.__coverPersonalization.heldDialog), Boolean, 'held native photo response')
    await closeDetail(); await openGame(1); await tab('管理'); await note().fill('New B draft while old native dialog is pending')
    await application.evaluate(() => globalThis.__coverPersonalization.releaseDialog())
    await poll(() => entry(0), game => !!game.localCover, 'old local image save')
    await page.waitForTimeout(120)
    assert.equal(await note().inputValue(), 'New B draft while old native dialog is pending')
    assert.equal((await entry(1)).localCover, undefined); assert.equal((await entry(1)).notes, 'Saved synthetic note 1')
    await closeDetail()
    record('A delayed native response persists only its original game and cannot overwrite a newer detail editor')

    for (const condition of conditions) {
      await resize(condition); await openGame(0); await openPicker()
      await picker().getByRole('radio', { name: '当前本地图片', exact: true }).focus(); await page.keyboard.press('End'); await page.keyboard.press('Home')
      assert.equal(await picker().getByRole('radio', { name: '当前本地图片', exact: true }).getAttribute('aria-checked'), 'true')
      await display().selectOption('global')
      if (!await saveCover().isDisabled()) { await saveCover().focus(); await page.keyboard.press('Enter'); await pickerReady() }
      await display().focus(); await page.keyboard.press('Home'); await page.keyboard.press('ArrowDown'); await page.keyboard.press('ArrowDown')
      assert.equal(await display().inputValue(), 'plain')
      assert.equal(await saveCover().isDisabled(), false)
      await saveCover().focus(); await page.keyboard.press('Enter')
      await poll(() => entry(0), game => game.nonPortraitCoverDisplay === 'plain', 'keyboard per-game plain persisted')
      await pickerReady(); await checkPresentation(0, 'plain', (await entry(0)).localCover.dataUrl, true)
      await display().focus(); await page.keyboard.press('Home')
      assert.equal(await display().inputValue(), 'global')
      assert.equal(await saveCover().isDisabled(), false)
      await saveCover().focus(); await page.keyboard.press('Enter')
      await poll(() => entry(0), game => game.nonPortraitCoverDisplay == null, 'keyboard global following persisted')
      await pickerReady()
      await checkPresentation(0, 'blur', (await entry(0)).localCover.dataUrl, true)
      await capture('local-picker-keyboard', condition); await closeDetail()
    }
    record('At every size and zoom, native keyboard arrows select plain and Home restores global; focused Save plus Enter performs both real IPC commits, including 1000x700 at 150% zoom without horizontal overflow')

    const persisted = await snapshot()
    assert.deepEqual(JSON.parse(await fs.readFile(path.join(profile, 'library.json'), 'utf8')).games, persisted.games)
    const previousControl = await application.evaluate(() => ({ requests: globalThis.__coverPersonalization.requests, dialogs: globalThis.__coverPersonalization.dialogs, unexpected: globalThis.__coverPersonalization.unexpected, blocked: globalThis.__coverPersonalization.blocked }))
    await application.close(); application = undefined; await start()
    assert.deepEqual((await snapshot()).games, persisted.games); assert.equal((await snapshot()).settings.nonPortraitCoverDisplay, 'blur')
    await resize(normal); await openGame(2); await waitImage('.detail-foreground'); assert.equal(await page.locator('.detail-foreground').getAttribute('src'), unassociated.dataUrl); await closeDetail()
    assert.deepEqual(previousControl.unexpected, []); assert.deepEqual(previousControl.blocked, [])
    for (const dialog of previousControl.dialogs) { assert.equal(dialog.title, '选择本地封面照片'); assert.deepEqual(dialog.filters[0].extensions, ['png', 'jpg', 'jpeg']) }
    manifest.transport = { requests: previousControl.requests, dialogs: previousControl.dialogs, restartedRequests: await application.evaluate(() => globalThis.__coverPersonalization.requests), blockedNetwork: [], unexpected: [] }
    assert.deepEqual(manifest.rendererErrors, [])
    record('Native restart retains normalized local copies, global settings and per-game choices; no real catalogue API, formal profile, executable or personal photograph was used')
  }
  manifest.status = manifest.baselineLayoutIssues.length || manifest.knownLayoutIssues.length ? 'passed-with-known-layout-issue' : 'passed'
} catch (error) {
  manifest.status = 'failed'; manifest.failure = error.stack ?? error.message
  if (page && application) { try { await capture('failure', normal) } catch {} }
  throw error
} finally {
  await fs.mkdir(captures, { recursive: true }); await fs.writeFile(path.join(captures, 'manifest.json'), JSON.stringify(manifest, null, 2))
  const runs = []
  for (const phase of ['before', 'after', 'candidate', 'before-layout', 'after-layout', 'candidate-layout', 'after-settings', 'candidate-settings']) { try { runs.push(JSON.parse(await fs.readFile(path.join(output, phase, 'manifest.json'), 'utf8'))) } catch (error) { if (error.code !== 'ENOENT') throw error } }
  await fs.writeFile(path.join(output, 'README.md'), ['# 第三轮：封面与栏目装饰验证', '', '所有截图使用相同虚构书架、原创 CC0 SVG 及生成的几何 PNG/JPEG；原生文件选择结果与远程传输被隔离测试替代，生产 IPC、图像处理、存储和界面实际运行。正式资料、真实照片与密钥未访问。', '', ...runs.flatMap(run => ['## ' + run.phase + ' · ' + run.status, '', '源码基线：' + run.sourceCommit + (run.sourceDirty ? '（未提交工作区）' : '') + '；合成书架 SHA256：' + run.fixtureSha256 + '。程序哈希、实际窗口尺寸和所有检查见 ' + run.phase + '/manifest.json。', '', ...run.checks.map(check => '- ' + check), ...[...new Map([...(run.baselineLayoutIssues ?? []), ...(run.knownLayoutIssues ?? [])].map(issue => [issue.state + '-' + issue.width + '-' + issue.height + '-' + issue.zoom, issue])).values()].map(issue => '- 已有布局缺口未解决：' + issue.state + '，' + issue.width + ' × ' + issue.height + '、缩放 ' + issue.zoom + '；文档宽 ' + issue.documentWidth + ' / 视口宽 ' + issue.viewportWidth + (issue.baselineDocumentWidth ? '，旧版文档宽 ' + issue.baselineDocumentWidth : '') + '。该项不属于布局通过。'), '', '| 状态 | 请求窗口 | 缩放 | 截图 |', '| --- | --- | --- | --- |', ...run.screenshots.map(shot => '| ' + shot.state + ' | ' + shot.width + ' × ' + shot.height + ' | ' + shot.zoom + ' | [查看](' + shot.file + ') |'), ''])].join('\n'))
  if (application) await application.close()
  if (path.dirname(path.resolve(temporary)) !== tempRoot || !path.basename(temporary).startsWith('the-order-cover-personalization-')) throw Error('Unexpected temporary profile cleanup path')
  await fs.rm(temporary, { recursive: true, force: true })
  console.log(JSON.stringify({ phase, status: manifest.status, checks: manifest.checks.length, screenshots: manifest.screenshots.length, output, syntheticOnly: true, formalProfileAccessed: false }))
}
