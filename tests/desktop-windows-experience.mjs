import assert from 'node:assert/strict'
import { promises as fs } from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { createHash } from 'node:crypto'
import { execFileSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'
import { _electron as electron } from '../node_modules/playwright/index.mjs'

// Normal packaged entry, real preload and IPC, isolated fictional data. This
// driver never changes host appearance, copies private data, or runs a game.
const project = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const option = name => { const index = process.argv.indexOf(name); return index < 0 ? undefined : process.argv[index + 1] }
const phase = option('--phase')
assert.ok(phase === 'baseline' || phase === 'after', '--phase baseline|after is required')
assert.ok(option('--executable'), '--executable must explicitly identify the program under test')
const executable = path.resolve(option('--executable'))
const diagnosticModal = process.argv.includes('--diagnostic-modal')
const diagnosticForcedColors = process.argv.includes('--diagnostic-forced-colors')
const phaseRoot = path.join(project, '.artifacts', 'iteration-05', 'windows-experience', phase)
const output = path.join(phaseRoot, new Date().toISOString().replace(/[:.]/g, '-'))
const tempRoot = path.resolve(os.tmpdir())
const temporary = await fs.mkdtemp(path.join(tempRoot, 'the-order-windows-experience-'))
const profile = path.join(temporary, 'profile')
const date = '2026-10-04T00:00:00.000Z'
const names = [
  '【虚构】湖岸来信与那些从未忘记的夏日约定，以及再一次向远方出发的故事 · 合成长标题',
  '【虚构】长夜灯塔 · 细长竖图', '【虚构】横越星海 · 横图', '【虚构】方形庭院 · 方图',
  '【虚构】失去的图像 · 坏图', '【虚构】保留长夜 · 独立柔化', '【虚构】安静长夜 · 独立纯色',
  '【虚构】尚未写下的故事 · 无封面',
]
const conditions = [{ width: 1440, height: 940, zoom: 1 }, { width: 1000, height: 700, zoom: 1.5 }, { width: 640, height: 560, zoom: 1 }, { width: 500, height: 600, zoom: 1 }]
const hash = bytes => createHash('sha256').update(bytes).digest('hex')
const report = {
  capturedAt: new Date().toISOString(), phase, diagnosticModal, diagnosticForcedColors, executable, output, profile, host: { platform: process.platform, release: os.release() },
  checkoutHead: execFileSync('git', ['rev-parse', 'HEAD'], { cwd: project, encoding: 'utf8' }).trim(),
  sourceDirty: !!execFileSync('git', ['status', '--porcelain', '--', 'src', 'electron'], { cwd: project, encoding: 'utf8' }).trim(),
  sourceHashScope: 'Source hashes describe the current checkout, not the earlier baseline EXE; runtime executable/ASAR hashes identify the actual application under test.',
  sourceHashes: {}, runtimeHashes: {}, conditions, ownWindowCaptureCondition: { width: 1100, height: 700, zoom: 1, centered: true }, geometry: [], themes: [], screenshots: [], checks: [], rendererErrors: [], blockedRequestCount: 0,
  syntheticOnly: true, formalProfileAccessed: false, realApiCalls: 0, realGameExecutions: 0,
  fixture: 'Same eight fictional books and original geometric JPEG artwork as the compact-library trial. Synthetic installation folders contain no executables. Missing-image URL is an unavailable loopback port and all HTTP/S requests are cancelled.',
  unverified: ['Real API/catalogue and real game launch', 'Private bookshelf and formal profile', 'Mouse operation of native captions, Windows Snap flyout, Alt+Space and taskbar behaviour', 'Changing host OS scaling or system theme; real webContents zoom and simulated accessibility media are reported separately'],
}
let application, page, failure
const state = () => page.evaluate(() => window.kunManager.getState())
const card = index => page.locator('.library-card').filter({ has: page.getByRole('button', { name: names[index], exact: true }) })
const addCheck = text => { report.checks.push(text); console.log(text) }
const timeout = setTimeout(() => {
  report.status = 'timed-out'; report.error = 'Windows experience UI check exceeded eight minutes'
  Promise.allSettled([fs.writeFile(path.join(output, 'manifest.json'), JSON.stringify(report, null, 2)), application?.close()]).finally(() => process.exit(1))
}, 480000)

async function start() {
  const env = { ...process.env }
  for (const key of Object.keys(env)) if (key.startsWith('KGM_')) delete env[key]
  delete env.ELECTRON_RUN_AS_NODE; delete env.NODE_OPTIONS
  Object.assign(env, { KGM_DATA_DIR: profile, KGM_DEV_URL: 'http://127.0.0.1:5173' })
  application = await electron.launch({ executablePath: executable, args: [], env, timeout: 30000 })
  page = await application.firstWindow(); page.setDefaultTimeout(15000)
  page.on('pageerror', error => report.rendererErrors.push(error.message))
  await page.waitForFunction(() => window.kunManager !== undefined)
  // Electron's Playwright context may otherwise force its default light media
  // scheme. Null explicitly lets Chromium observe the actual native theme.
  await page.emulateMedia({ colorScheme: null, reducedMotion: 'reduce' })
  assert.equal(page.url(), 'kun-manager://app/index.html')
  const identity = await application.evaluate(({ app, BrowserWindow, session, nativeTheme, screen }) => {
    globalThis.__windowsBlockedRequestCount = 0
    session.defaultSession.webRequest.onBeforeRequest({ urls: ['http://*/*', 'https://*/*'] }, (_request, callback) => {
      globalThis.__windowsBlockedRequestCount++; callback({ cancel: true })
    })
    const win = BrowserWindow.getAllWindows()[0], prefs = win.webContents.getLastWebPreferences()
    return { executable: process.execPath, packaged: app.isPackaged, version: app.getVersion(), electron: process.versions.electron, chromium: process.versions.chrome,
      profile: app.getPath('userData'), contextIsolation: prefs.contextIsolation, sandbox: prefs.sandbox, nodeIntegration: prefs.nodeIntegration, webSecurity: prefs.webSecurity,
      initialBounds: win.getBounds(), initialContent: win.getContentBounds(), minimumSize: win.getMinimumSize(), nativeThemeSource: nativeTheme.themeSource, nativeDark: nativeTheme.shouldUseDarkColors,
      displays: screen.getAllDisplays().map(display => ({ bounds: display.bounds, workArea: display.workArea, scaleFactor: display.scaleFactor })) }
  })
  assert.equal(path.resolve(identity.executable), executable); assert.equal(identity.packaged, true)
  assert.equal(path.resolve(identity.profile), profile); assert.equal(identity.contextIsolation, true)
  assert.equal(identity.sandbox, true); assert.equal(identity.nodeIntegration, false); assert.equal(identity.webSecurity, true)
  assert.equal((await state()).dataPath, profile); assert.equal((await state()).settings.hasApiKey, false); assert.equal((await state()).activeSession, null)
  report.identity = identity
}
async function stop() {
  if (!application) return
  report.blockedRequestCount += await application.evaluate(() => globalThis.__windowsBlockedRequestCount ?? 0)
  await application.close(); application = undefined
}
async function navigate(label) {
  const button = page.locator('.sidebar').getByRole('button', { name: label, exact: true })
  await button.focus(); await button.press('Enter')
}
async function resize(condition) {
  const geometry = await application.evaluate(async ({ BrowserWindow }, requested) => {
    const windows = BrowserWindow.getAllWindows(); if (windows.length !== 1) throw Error('Expected one isolated window')
    const win = windows[0]; if (win.isMaximized()) win.unmaximize(); if (win.isMinimized()) win.restore()
    win.setContentSize(requested.width, requested.height); win.webContents.setZoomFactor(requested.zoom)
    await new Promise(resolve => setTimeout(resolve, 100))
    return { requested, actualBounds: win.getBounds(), actualContent: win.getContentBounds(), minimumSize: win.getMinimumSize(), zoom: win.webContents.getZoomFactor() }
  }, condition)
  await page.waitForTimeout(100)
  await page.waitForFunction(actual => Math.abs(innerWidth - actual.width / actual.zoom) < 3 && Math.abs(innerHeight - actual.height / actual.zoom) < 3,
    { ...geometry.actualContent, zoom: condition.zoom })
  geometry.limitedByMinimum = Math.abs(geometry.actualContent.width - condition.width) > 3 || Math.abs(geometry.actualContent.height - condition.height) > 3
  if (phase === 'after') assert.equal(geometry.limitedByMinimum, false, `Candidate cannot reach requested ${condition.width}×${condition.height}`)
  report.geometry.push(geometry); return geometry
}
async function capture(label, condition, target) {
  const geometry = await resize(condition)
  await page.evaluate(() => document.querySelectorAll('[data-kun-toast-close]').forEach(button => button.click()))
  if (target) await page.locator(target).first().scrollIntoViewIfNeeded()
  else await page.evaluate(() => window.scrollTo(0, 0))
  await page.mouse.move(0, 0); await page.waitForTimeout(150)
  const actual = await page.evaluate(() => {
    const sidebar = document.querySelector('.sidebar'), modal = document.querySelector('.game-detail-modal'), titlebar = document.querySelector('.desktop-titlebar')
    const rect = node => { const r = node?.getBoundingClientRect(); return r ? { x: r.x, y: r.y, width: r.width, height: r.height, right: r.right, bottom: r.bottom } : null }
    const overlay = navigator.windowControlsOverlay
    return { width: innerWidth, height: innerHeight, dpr: devicePixelRatio, scrollWidth: document.documentElement.scrollWidth,
      theme: document.documentElement.classList.contains('kun-dark-mode') ? 'dark' : 'light', mediaDark: matchMedia('(prefers-color-scheme: dark)').matches,
      bodyFont: getComputedStyle(document.body).fontFamily, bodyBackground: getComputedStyle(document.body).backgroundColor, titlebar: rect(titlebar), sidebar: rect(sidebar),
      typography: Object.fromEntries([['body', document.body], ['cardTitle', document.querySelector('.card-title')], ['heading', document.querySelector('h1')], ['settingsInput', document.querySelector('.preferences-settings input')]].filter(([,node]) => node).map(([name,node]) => { const style = getComputedStyle(node); return [name,{ fontFamily: style.fontFamily, fontSize: style.fontSize, fontWeight: style.fontWeight, lineHeight: style.lineHeight }] })),
      sidebarClientWidth: sidebar?.clientWidth ?? null, sidebarScrollWidth: sidebar?.scrollWidth ?? null,
      modalClientWidth: modal?.clientWidth ?? null, modalScrollWidth: modal?.scrollWidth ?? null,
      overlay: overlay ? { visible: overlay.visible, rect: rect({ getBoundingClientRect: () => overlay.getTitlebarAreaRect() }) } : null,
      captionArea: rect(document.querySelector('.desktop-caption-area')), captionHome: rect(document.querySelector('.desktop-caption-home')),
      captionEnabled: document.documentElement.classList.contains('windows-caption'),
      frames: [...document.querySelectorAll('.library-grid:not(.game-list) .compact-card .card-image')].map(frame => rect(frame)),
      forcedColors: matchMedia('(forced-colors: active)').matches, reducedMotion: matchMedia('(prefers-reduced-motion: reduce)').matches }
  })
  if (phase === 'after') {
    assert.ok(actual.scrollWidth <= actual.width + 2, `${label}: document horizontal overflow ${actual.scrollWidth} > ${actual.width}`)
    if (actual.sidebarClientWidth !== null) assert.ok(actual.sidebarScrollWidth <= actual.sidebarClientWidth + 2, `${label}: sidebar horizontal overflow`)
    if (actual.modalClientWidth !== null) assert.ok(actual.modalScrollWidth <= actual.modalClientWidth + 2, `${label}: detail horizontal overflow`)
    if (label.startsWith('grid-')) { assert.equal(actual.frames.length, names.length); for (const frame of actual.frames) assert.ok(Math.abs(frame.width / frame.height - .75) < .03, `${label}: cover aspect ratio`) }
    assert.ok(actual.captionEnabled && actual.overlay?.visible, `${label}: native caption integration inactive`)
    assert.ok(actual.bodyFont.includes('Segoe UI'), `${label}: Windows font is not in the resolved CSS stack`)
    for (const [name, typography] of Object.entries(actual.typography)) assert.ok(typography.fontFamily.replace(/^["']/, '').startsWith('Segoe UI Variable'), `${label}: ${name} lacks Windows-first font stack (${typography.fontFamily})`)
    assert.ok(actual.captionHome && actual.captionArea && actual.overlay.rect, `${label}: caption geometry missing`)
    assert.ok(actual.captionHome.right <= actual.overlay.rect.right + 2 && actual.captionHome.bottom <= actual.overlay.rect.bottom + 2, `${label}: home control intrudes into native controls`)
    assert.ok(Math.abs(actual.captionArea.right - actual.overlay.rect.right) < 2 && Math.abs(actual.captionArea.x - actual.overlay.rect.x) < 2, `${label}: draggable caption safe area differs from native geometry`)
    assert.ok(actual.sidebar.y >= actual.titlebar.bottom - 2, `${label}: navigation intrudes into titlebar`)
  }
  const bytes = Buffer.from(await application.evaluate(async ({ BrowserWindow }) => (await BrowserWindow.getAllWindows()[0].webContents.capturePage()).toPNG().toString('base64')), 'base64')
  const file = path.join(output, `${label}-${condition.width}x${condition.height}-zoom${condition.zoom}.png`)
  assert.ok(!report.screenshots.some(shot => shot.file === file), 'Duplicate evidence filename')
  await fs.writeFile(file, bytes)
  const pixels = { width: bytes.readUInt32BE(16), height: bytes.readUInt32BE(20) }
  assert.ok(Math.abs(pixels.width - actual.width * actual.dpr) < 5); assert.ok(Math.abs(pixels.height - actual.height * actual.dpr) < 5)
  report.screenshots.push({ file, label, requested: condition, geometry, actual, pixels, sha256: hash(bytes) })
}
async function ownWindowFrame(label) {
  try {
    // getSources necessarily enumerates IDs internally. Only the exact isolated
    // BrowserWindow media source is retained; no other titles or pixels escape.
    const result = await application.evaluate(async ({ BrowserWindow, desktopCapturer }) => {
      const win = BrowserWindow.getAllWindows()[0], wanted = win.getMediaSourceId()
      const sources = await desktopCapturer.getSources({ types: ['window'], thumbnailSize: { width: 1600, height: 1200 }, fetchWindowIcons: false })
      const own = sources.find(source => source.id === wanted)
      if (!own || own.thumbnail.isEmpty()) return { captured: false, reason: 'Own window source was unavailable or returned an empty image' }
      return { captured: true, png: own.thumbnail.toPNG().toString('base64'), size: own.thumbnail.getSize(), bounds: win.getBounds(), sourceMatchedOwnWindow: true }
    })
    if (!result.captured) { report.unverified.push(`Own-window full-frame capture: ${result.reason}`); return }
    const bytes = Buffer.from(result.png, 'base64'), file = path.join(output, `${label}-own-window-frame.png`)
    await fs.writeFile(file, bytes)
    delete result.png
    report.screenshots.push({ file, label, method: 'desktopCapturer exact BrowserWindow mediaSourceId only; thumbnail resolution may differ from physical pixels; source capture success does not establish non-client caption coverage', ...result, sha256: hash(bytes) })
    addCheck('Captured only the isolated application window source; no other window title or image was retained; native caption pixel coverage requires visual inspection')
  } catch (error) { report.unverified.push(`Own-window full-frame capture: ${error.message}`) }
}
async function settings(theme) {
  await navigate('偏好设置')
  await page.locator('.preferences-settings .field-label').filter({ hasText: '外观主题' }).locator('select').selectOption(theme)
  const save = page.getByRole('button', { name: '保存全部设置', exact: true })
  if (!await save.isDisabled()) { await save.focus(); await save.press('Enter') }
  await page.waitForFunction(async expected => (await window.kunManager.getState()).settings.theme === expected, theme)
  if (phase === 'after') await page.waitForFunction(theme => document.documentElement.classList.contains('kun-dark-mode') === (theme === 'system' ? matchMedia('(prefers-color-scheme: dark)').matches : theme === 'dark'), theme)
  const native = await application.evaluate(({ nativeTheme, BrowserWindow }) => ({ themeSource: nativeTheme.themeSource, dark: nativeTheme.shouldUseDarkColors, backgroundColor: BrowserWindow.getAllWindows()[0].getBackgroundColor() }))
  const renderer = await page.evaluate(() => ({ dark: document.documentElement.classList.contains('kun-dark-mode'), mediaDark: matchMedia('(prefers-color-scheme: dark)').matches }))
  if (phase === 'after') { assert.equal(native.themeSource, theme); assert.equal(native.dark, renderer.dark); assert.equal(renderer.mediaDark, native.dark) }
  report.themes.push({ saved: theme, native, renderer })
  await navigate('我的书架')
}
async function modalCaptionInteraction(modal, label) {
  if (phase !== 'after') return
  const home = page.locator('.desktop-caption-home')
  report.modalCaptionInteractions ??= []
  if (!await home.count()) {
    report.modalCaptionInteractions.push({ label, interactiveHome: false, reason: 'No interactive caption home control is present' }); return
  }
  const underlying = () => page.evaluate(() => ({ settings: !!document.querySelector('.preferences-settings'), cards: document.querySelectorAll('.library-card').length, heading: document.querySelector('.workspace .topbar')?.textContent?.trim() }))
  const before = await underlying()
  const control = await home.evaluate(button => { button.focus(); return { disabled: button.disabled, inertAncestor: !!button.closest('[inert]'), appInert: document.getElementById('app')?.inert, acquiredFocus: document.activeElement === button, appRegion: getComputedStyle(button).webkitAppRegion } })
  assert.equal(control.inertAncestor, true, `${label}: caption home must be within the inert application during a modal`)
  assert.equal(control.appInert, true, `${label}: modal did not mark application inert`)
  assert.equal(control.acquiredFocus, false, `${label}: inactive caption home acquired focus`)
  const after = await underlying(), modalStillOpen = await modal.isVisible()
  report.modalCaptionInteractions.push({ label, interactiveHome: true, control, action: 'Inert ancestry and actual focus refusal; bounded mouse click was independently verified in the prototype diagnostic', before, after, modalStillOpen })
  assert.deepEqual(after, before, `${label}: caption home navigated the page behind an open modal`)
  assert.equal(modalStillOpen, true, `${label}: caption home unexpectedly closed the modal`)
}
async function buttonColorEvidence() {
  return page.getByRole('button', { name: '添加游戏', exact: true }).evaluate(button => {
    const styles = node => { const style = getComputedStyle(node); return { tag: node.tagName, classes: node.className?.baseVal ?? node.className, text: node.textContent?.trim(), color: style.color, backgroundColor: style.backgroundColor, forcedColorAdjust: style.forcedColorAdjust, borderColor: style.borderColor, display: style.display, opacity: style.opacity, font: style.font } }
    const ancestors = []; let parent = button.parentElement
    for (let count = 0; parent && count < 5; count++, parent = parent.parentElement) ancestors.push(styles(parent))
    const walker = document.createTreeWalker(button, NodeFilter.SHOW_TEXT), labels = []
    while (walker.nextNode()) if (walker.currentNode.textContent.trim()) labels.push({ text: walker.currentNode.textContent.trim(), parent: styles(walker.currentNode.parentElement) })
    return { outerHTML: button.outerHTML, button: styles(button), descendants: [...button.querySelectorAll('*')].map(styles), labels, ancestors }
  })
}
async function buttonPixelEvidence() {
  const geometry = await page.getByRole('button', { name: '添加游戏', exact: true }).evaluate(button => {
    const buttonRect = button.getBoundingClientRect(), walker = document.createTreeWalker(button, NodeFilter.SHOW_TEXT), labels = []
    while (walker.nextNode()) if (walker.currentNode.textContent.trim()) {
      const range = document.createRange(); range.selectNodeContents(walker.currentNode); const r = range.getBoundingClientRect()
      labels.push({ text: walker.currentNode.textContent.trim(), x: r.x, y: r.y, width: r.width, height: r.height })
    }
    return { button: { x: buttonRect.x, y: buttonRect.y, width: buttonRect.width, height: buttonRect.height }, labels, dpr: devicePixelRatio }
  })
  const png = await application.evaluate(async ({ BrowserWindow }) => (await BrowserWindow.getAllWindows()[0].webContents.capturePage()).toPNG().toString('base64'))
  return page.evaluate(async ({ png, geometry }) => {
    const image = new Image(); image.src = `data:image/png;base64,${png}`; await image.decode()
    const canvas = document.createElement('canvas'); canvas.width = image.naturalWidth; canvas.height = image.naturalHeight
    const context = canvas.getContext('2d'); context.drawImage(image, 0, 0)
    const data = context.getImageData(0, 0, canvas.width, canvas.height).data, dpr = geometry.dpr
    const at = (x, y) => { const offset = (Math.floor(y) * canvas.width + Math.floor(x)) * 4; return [data[offset], data[offset + 1], data[offset + 2]] }
    const luminance = color => color.map(value => { const n = value / 255; return n <= .04045 ? n / 12.92 : ((n + .055) / 1.055) ** 2.4 }).reduce((sum, value, index) => sum + value * [.2126, .7152, .0722][index], 0)
    const contrast = (a, b) => { const x = luminance(a), y = luminance(b); return (Math.max(x, y) + .05) / (Math.min(x, y) + .05) }
    const reference = at((geometry.button.x + geometry.button.width - 5) * dpr, (geometry.button.y + geometry.button.height / 2) * dpr)
    const labels = geometry.labels.map(label => {
      let total = 0, background = 0, ink = 0
      const left = Math.max(0, Math.ceil(label.x * dpr)), right = Math.min(canvas.width, Math.floor((label.x + label.width) * dpr))
      const top = Math.max(0, Math.ceil((label.y + 1) * dpr)), bottom = Math.min(canvas.height, Math.floor((label.y + label.height - 1) * dpr))
      for (let y = top; y < bottom; y++) for (let x = left; x < right; x++) {
        const color = at(x, y); total++
        if (color.every((value, index) => Math.abs(value - reference[index]) <= 12)) background++
        if (contrast(color, reference) >= 4.5) ink++
      }
      return { ...label, pixelCount: total, paddingBackgroundFraction: background / total, contrastingInkFraction: ink / total }
    })
    return { geometry, paddingReferenceRGB: reference, labels, method: 'Actual capturePage PNG pixels, text-node Range boxes, button padding background reference; detects a forced-colors text backplate hiding the label even when computed CSS contrast appears sufficient' }
  }, { png, geometry })
}
function assertButtonPixelReadable(evidence) {
  assert.ok(evidence.labels.length > 0, 'Forced-colors add-game button lacks text')
  for (const label of evidence.labels) {
    assert.ok(label.paddingBackgroundFraction > .25, `Forced-colors label is covered by a different backplate: ${label.text} (${label.paddingBackgroundFraction})`)
    assert.ok(label.contrastingInkFraction > .005, `Forced-colors label has no distinguishable text pixels: ${label.text}`)
  }
}
async function seed() {
  await page.evaluate(async () => {
    const saved = (await window.kunManager.getState()).settings
    await window.kunManager.saveSettings({ ...saved, theme: 'light', nonPortraitCoverDisplay: 'crop', includeNsfw: false, readExecutableMetadata: false })
    localStorage.setItem('the-order:library:v1', JSON.stringify({ layout: 'grid', sort: 'added', overview: false }))
  })
  const covers = await page.evaluate(() => {
    const palettes = [['#254459', '#7498ba', '#ffe6c1'], ['#402640', '#945479', '#ffc8ac'], ['#1a4252', '#438989', '#f4dab7'], ['#3c3756', '#7884b9', '#dfdbf2']]
    return [{ width: 360, height: 540 }, { width: 220, height: 720 }, { width: 720, height: 360 }, { width: 480, height: 480 }].map((size, index) => {
      const canvas = document.createElement('canvas'); canvas.width = size.width; canvas.height = size.height
      const context = canvas.getContext('2d'), [base, middle, bright] = palettes[index]
      const gradient = context.createLinearGradient(0, 0, size.width, size.height); gradient.addColorStop(0, base); gradient.addColorStop(1, middle)
      context.fillStyle = gradient; context.fillRect(0, 0, size.width, size.height)
      context.fillStyle = bright; context.beginPath(); context.arc(size.width * .5, size.height * .26, size.width * .27, 0, Math.PI * 2); context.fill()
      context.fillStyle = base; context.fillRect(size.width * .44, size.height * .38, size.width * .12, size.height * .42)
      for (let line = 0; line < 7; line++) { context.strokeStyle = bright; context.globalAlpha = .12 + line * .015; context.beginPath(); context.moveTo(0, size.height * (.54 + line * .07)); context.lineTo(size.width, size.height * (.42 + line * .08)); context.stroke() }
      context.globalAlpha = 1; context.strokeStyle = bright; context.lineWidth = 4; context.strokeRect(8, 8, size.width - 16, size.height - 16)
      context.fillStyle = bright; context.font = `${Math.max(16, Math.floor(size.width / 11))}px sans-serif`; context.textAlign = 'center'; context.fillText(`FICTIONAL ${index + 1}`, size.width / 2, size.height * .91)
      return { ...size, dataUrl: canvas.toDataURL('image/jpeg', .88) }
    })
  })
  await stop()
  const metadata = (index, coverUrl = null) => ({ id: String(988400 + index), title: names[index], originalTitle: 'Fictional compact library ' + index, aliases: ['隔离合成别名'], coverUrl, bannerUrl: null, description: '原创虚构测试资料；不是联网结果，也不是正式书架。', releaseDate: '2026-10-04', releaseDatePrecision: 'day', developers: ['虚构测试工房'], tags: ['隔离测试'], ratings: [{ source: 'bangumi', score: 8.7, scale: 10, votes: 1 }], characters: [], references: [], webUrl: null, contentRating: '全年龄', fetchedAt: date })
  const games = names.map((name, index) => ({ id: `compact-game-${index}`, name, nameIsCustom: true,
    metadata: [0, 4].includes(index) ? metadata(index, index === 4 ? 'https://127.0.0.1:1/synthetic-missing-cover.jpg' : null) : null,
    ...(![4, 7].includes(index) ? { localCover: covers[index > 4 ? 1 : index] } : {}),
    ...(index === 5 ? { nonPortraitCoverDisplay: 'blur' } : index === 6 ? { nonPortraitCoverDisplay: 'plain' } : {}),
    installations: [{ id: `compact-install-${index}`, folderPath: path.join(temporary, 'fictional-games', String(index)), executablePath: null, arguments: [] }], preferredInstallationId: `compact-install-${index}`,
    status: index === 0 ? 'playing' : 'planned', personalRating: index === 0 ? 7.5 : null, favorite: index === 0,
    categories: ['隔离合成分类', ...(index === 0 ? ['合成长分类 · ' + '遥远夏日的那些约定'.repeat(6)] : [])], customTags: ['虚构测试'], notes: '已保存的合成笔记',
    totalPlaySeconds: index === 0 ? 3600 : 0, playCount: index === 0 ? 1 : 0, sessions: [], addedAt: `2026-10-04T00:00:0${7 - index}.000Z`, lastPlayedAt: null,
  }))
  for (const game of games) await fs.mkdir(game.installations[0].folderPath, { recursive: true })
  const fixture = { format: 'kun-gal-manager', version: 1, exportedAt: date, games }, bytes = JSON.stringify(fixture)
  report.fixtureFileSha256 = hash(bytes)
  const canonical = structuredClone(fixture); canonical.games.forEach((game, index) => { game.installations[0].folderPath = `<ISOLATED_FIXTURE_ROOT>/fictional-games/${index}` })
  report.fixtureSha256 = hash(JSON.stringify(canonical))
  if (phase === 'after') {
    const baseline = JSON.parse(await fs.readFile(path.join(project, '.artifacts', 'iteration-05', 'windows-experience', 'baseline', 'manifest.json'), 'utf8'))
    assert.equal(baseline.status, 'passed'); assert.equal(report.fixtureSha256, baseline.fixtureSha256); assert.deepEqual(report.conditions, baseline.conditions)
    report.baselineManifest = path.join(baseline.output, 'manifest.json')
  }
  await fs.writeFile(path.join(profile, 'library.json'), bytes)
  await start(); await page.locator('.library-card').first().waitFor(); assert.deepEqual((await state()).games, games)
  await page.waitForFunction(() => [...document.querySelectorAll('.cover-foreground')].every(image => image.complete && image.naturalWidth > 0))
  await card(4).locator('.cover-placeholder').waitFor(); await card(7).locator('.cover-placeholder').waitFor()
  return games
}

try {
  await fs.access(executable); await fs.mkdir(output, { recursive: true })
  for (const file of ['The Order.exe', 'resources/app.asar']) report.runtimeHashes[file] = hash(await fs.readFile(path.join(path.dirname(executable), file)))
  const styles = (await fs.readdir(path.join(project, 'src'))).filter(file => file.endsWith('.css')).map(file => `src/${file}`)
  for (const file of ['src/App.vue', 'src/main.ts', 'src/components/GameCard.vue', 'src/components/DesktopTitlebar.vue', 'src/components/PreferencesSettings.vue', ...styles, 'electron/main.ts', 'electron/settings.ts', 'electron/window-appearance.ts']) {
    try { report.sourceHashes[file] = hash((await fs.readFile(path.join(project, file), 'utf8')).replace(/\r\n/g, '\n')) } catch (error) { if (error.code !== 'ENOENT') throw error }
  }
  await start(); await page.getByRole('heading', { name: '让第一段故事，住进书架', exact: true }).waitFor(); assert.equal((await state()).games.length, 0)
  report.freshDefaultTheme = { saved: (await state()).settings.theme, nativeSource: report.identity.nativeThemeSource, nativeDark: report.identity.nativeDark }
  if (phase === 'after') { assert.equal(report.freshDefaultTheme.saved, 'system'); assert.equal(report.freshDefaultTheme.nativeSource, 'system') }
  await settings('light'); await page.waitForFunction(() => !document.documentElement.classList.contains('kun-dark-mode'))
  await capture('empty-light', conditions[0]); await capture('empty-light', conditions[3])
  const games = await seed()
  if (diagnosticForcedColors) {
    await settings('system'); await page.emulateMedia({ colorScheme: null, forcedColors: 'active', reducedMotion: 'reduce' })
    await capture('forced-colors-diagnostic', conditions[3]); report.forcedButton = await buttonColorEvidence(); report.forcedButtonPixels = await buttonPixelEvidence(); report.status = 'diagnostic-complete'
  } else if (diagnosticModal) {
    await navigate('偏好设置'); await page.locator('.preferences-settings').waitFor()
    await navigate('快捷收藏'); const quick = page.getByRole('dialog', { name: '快捷收藏', exact: true }); await quick.waitFor()
    await modalCaptionInteraction(quick, 'Quick collection from settings')
    report.status = 'passed'
  } else {
  assert.equal(await page.locator('.library-card.compact-card').count(), games.length)
  assert.equal(await page.locator('.library-card :is(.card-meta,.card-progress,.card-bottom,.favorite-button)').count(), 0)
  assert.equal(await page.locator('.library-card .card-title').count(), games.length)
  assert.ok(await card(0).locator('.card-title').getAttribute('title'))
  addCheck('Real production renderer retains eight compact cover/name tiles; long title tooltip and missing/bad-image placeholders are present')
  for (const theme of ['light', 'dark']) {
    await settings(theme)
    for (const condition of conditions) await capture(`grid-${theme}`, condition)
    await resize(report.ownWindowCaptureCondition)
    await application.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows()[0].center())
    await page.waitForTimeout(150); await ownWindowFrame(`grid-${theme}`)
  }
  await settings('light'); await resize(conditions[0])
  await card(0).getByRole('button', { name: names[0], exact: true }).focus(); await page.keyboard.press('Enter')
  const detail = page.locator('.game-detail-modal'); await detail.waitFor()
  await modalCaptionInteraction(detail, 'Detail from library')
  assert.ok(await detail.getByRole('textbox', { name: '记录我的评分', exact: true }).isVisible())
  for (const condition of [conditions[0], conditions[1], conditions[3]]) await capture('detail-light', condition)
  await detail.getByRole('button', { name: '关闭', exact: true }).click(); await detail.waitFor({ state: 'hidden' })
  await resize(conditions[0]); await page.getByRole('button', { name: '列表视图', exact: true }).click()
  assert.equal(await page.locator('.library-card.card-list').count(), games.length); assert.ok(await page.locator('.card-configure').count() > 0)
  for (const condition of [conditions[0], conditions[1], conditions[3]]) await capture('list-light', condition)
  await page.getByRole('button', { name: '封面视图', exact: true }).click()
  await navigate('偏好设置'); await page.locator('.preferences-settings').waitFor()
  await navigate('快捷收藏'); const quick = page.getByRole('dialog', { name: '快捷收藏', exact: true }); await quick.waitFor()
  await modalCaptionInteraction(quick, 'Quick collection from settings')
  await capture('quick-collections-light', conditions[3])
  await quick.getByRole('button', { name: /特别喜欢/ }).focus(); await page.keyboard.press('Enter'); await quick.waitFor({ state: 'hidden' })
  assert.equal(await page.locator('.library-card').count(), 1)
  await navigate('我的书架'); await navigate('偏好设置'); await page.locator('.preferences-settings').waitFor()
  for (const condition of [conditions[0], conditions[1], conditions[3]]) await capture('settings-light', condition, '.preferences-settings')
  await navigate('我的书架'); await page.getByRole('textbox', { name: '搜索书架', exact: true }).fill('不存在的合成标题')
  await page.getByRole('heading', { name: '这里暂时还没有游戏', exact: true }).waitFor(); await capture('filtered-empty-light', conditions[3])
  await page.getByRole('button', { name: '清空筛选', exact: true }).click()
  addCheck('Keyboard navigation, long-name detail, list configure actions, favourite/category modal, settings and filtered-empty state use actual preload/UI')
  if (phase === 'after') {
    for (const [navigation, selector, label] of [['分类收藏', '.collections-toolbar', 'collections'], ['游玩统计', '.statistics-panel, .stats-overview, .insights-workspace', 'statistics'], ['发现作品', 'input[aria-label="搜索作品名称"]', 'discover']]) {
      await navigate(navigation)
      if (label !== 'statistics') await page.locator(selector).first().waitFor()
      else await page.getByRole('heading', { name: '游玩统计', exact: true }).waitFor()
      for (const condition of [conditions[0], conditions[3]]) await capture(`${label}-light`, condition)
    }
    await navigate('资料整理'); await page.locator('.maintenance-modal').waitFor()
    for (const condition of [conditions[0], conditions[3]]) await capture('maintenance-light', condition)
    await page.keyboard.press('Escape'); await page.locator('.maintenance-modal').waitFor({ state: 'hidden' })
    await navigate('我的书架'); await settings('dark'); await resize(conditions[0])
    await card(1).getByRole('button', { name: names[1], exact: true }).click(); await detail.waitFor()
    await page.getByRole('button', { name: '封面与展示', exact: true }).click()
    const picker = page.getByRole('region', { name: '选择封面', exact: true }); await picker.waitFor()
    await page.waitForFunction(() => document.querySelector('.cover-picker')?.getAttribute('aria-busy') !== 'true')
    for (const condition of [conditions[0], conditions[3]]) await capture('detail-cover-picker-dark', condition, '.cover-picker')
    await detail.getByRole('button', { name: '关闭', exact: true }).click(); await detail.waitFor({ state: 'hidden' })
    addCheck('Collections/statistics/discovery/maintenance and dark detail-cover picker render at wide and 500px boundaries without document horizontal overflow')
    await settings('system'); await capture('grid-system', conditions[0])
    const caption = await page.evaluate(() => {
      const title = document.querySelector('.desktop-titlebar'), dragArea = document.querySelector('.desktop-caption-area'), home = document.querySelector('.desktop-caption-home'), overlay = navigator.windowControlsOverlay
      const r = overlay?.getTitlebarAreaRect(), titleRect = title?.getBoundingClientRect()
      return { overlayVisible: overlay?.visible ?? false, area: r ? { x: r.x, y: r.y, width: r.width, height: r.height } : null,
        titlebar: titleRect ? { x: titleRect.x, y: titleRect.y, width: titleRect.width, height: titleRect.height } : null,
        drag: dragArea ? getComputedStyle(dragArea).webkitAppRegion : null, homeRegion: home ? getComputedStyle(home).webkitAppRegion : null }
    })
    assert.ok(caption.titlebar, 'Integrated desktop titlebar missing'); assert.equal(caption.overlayVisible, true, 'Native overlay not visible')
    assert.ok(caption.area?.width > 0 && caption.area.height > 0); assert.equal(caption.drag, 'drag'); assert.equal(caption.homeRegion, 'no-drag')
    report.caption = caption
    await application.evaluate(async ({ BrowserWindow }) => { const win = BrowserWindow.getAllWindows()[0]; win.minimize(); await new Promise(resolve => setTimeout(resolve, 150)); if (!win.isMinimized()) throw Error('API minimize failed'); win.restore() })
    await application.evaluate(async ({ BrowserWindow }) => { const win = BrowserWindow.getAllWindows()[0]; win.maximize(); await new Promise(resolve => setTimeout(resolve, 150)); if (!win.isMaximized()) throw Error('API maximize failed'); win.unmaximize(); win.show(); win.focus() })
    await capture('restored-system', conditions[0])
    addCheck('Saved light/dark/system synchronizes nativeTheme, renderer class and media query; native titlebar overlay geometry is available; BrowserWindow minimize/maximize/restore API works')
    const systemPersisted = await state(); await stop(); await start(); await page.locator('.library-card').first().waitFor()
    assert.deepEqual((await state()).settings, systemPersisted.settings); assert.deepEqual((await state()).games, systemPersisted.games)
    const systemNative = await application.evaluate(({ nativeTheme }) => ({ source: nativeTheme.themeSource, dark: nativeTheme.shouldUseDarkColors }))
    assert.equal(systemNative.source, 'system')
    await page.waitForFunction(dark => document.documentElement.classList.contains('kun-dark-mode') === dark && matchMedia('(prefers-color-scheme: dark)').matches === dark, systemNative.dark)
    await capture('restarted-system', conditions[0])
    addCheck('System theme and native/media agreement survive a fresh process restart without changing host Windows theme')
    await page.emulateMedia({ colorScheme: null, forcedColors: 'active', reducedMotion: 'reduce' }); await capture('forced-colors-simulated', conditions[3])
    assert.equal(await page.evaluate(() => matchMedia('(forced-colors: active)').matches), true)
    assert.equal(await page.evaluate(() => matchMedia('(prefers-reduced-motion: reduce)').matches), true)
    report.forcedButton = await buttonColorEvidence(); report.forcedButtonPixels = await buttonPixelEvidence(); assertButtonPixelReadable(report.forcedButtonPixels)
    await page.emulateMedia({ colorScheme: null, forcedColors: 'none', reducedMotion: 'no-preference' }); await capture('motion-normal-simulated', conditions[0])
    addCheck('Forced-colors actual screenshot pixels retain visible add-game label/background and reduced-motion media works; this is simulated renderer accessibility, not an OS settings change')
  }
  await settings('dark'); const persisted = await state(); await stop(); await start(); await page.locator('.library-card').first().waitFor()
  assert.deepEqual((await state()).games, persisted.games); assert.deepEqual((await state()).settings, persisted.settings)
  if (phase === 'after') {
    const native = await application.evaluate(({ nativeTheme }) => ({ source: nativeTheme.themeSource, dark: nativeTheme.shouldUseDarkColors }))
    assert.deepEqual(native, { source: 'dark', dark: true })
    await page.waitForFunction(() => document.documentElement.classList.contains('kun-dark-mode') && matchMedia('(prefers-color-scheme: dark)').matches)
  }
  await capture('restarted-dark', conditions[0]); assert.deepEqual(report.rendererErrors, [])
  addCheck('Fresh process restart preserves library/settings with zero renderer exceptions; all program instances used this owned isolated profile')
  report.status = 'passed'
  }
} catch (error) { report.status = 'failed'; report.error = error.stack; failure = error }
finally {
  clearTimeout(timeout)
  await stop().catch(error => { report.closeError = error.message; report.status = 'failed'; failure ??= error })
  await fs.mkdir(output, { recursive: true }); await fs.writeFile(path.join(output, 'manifest.json'), JSON.stringify(report, null, 2))
  if (!diagnosticModal && !diagnosticForcedColors) await fs.writeFile(path.join(phaseRoot, 'manifest.json'), JSON.stringify(report, null, 2))
  if (path.dirname(temporary) !== tempRoot || !path.basename(temporary).startsWith('the-order-windows-experience-')) throw Error('Unexpected temporary cleanup path')
  await fs.rm(temporary, { recursive: true, force: true })
}
console.log(JSON.stringify({ status: report.status, phase, checks: report.checks.length, screenshots: report.screenshots.length, rendererErrors: report.rendererErrors.length, runManifest: path.join(output, 'manifest.json') }))
if (failure) throw failure
