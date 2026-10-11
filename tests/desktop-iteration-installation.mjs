import assert from 'node:assert/strict'
import { promises as fs } from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { _electron as electron } from '../node_modules/playwright/index.mjs'

const project = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const temporaryRoot = path.resolve(os.tmpdir())
const temporary = await fs.mkdtemp(path.join(temporaryRoot, 'the-order-iteration-installation-'))
const profile = path.join(temporary, 'profile'), bootstrap = path.join(temporary, 'bootstrap.cjs')
const date = '2026-10-03T00:00:00.000Z'
const fixture = async (name, files = ['Game.exe']) => {
  const folder = path.join(temporary, name)
  for (const filename of files) { const file = path.join(folder, filename); await fs.mkdir(path.dirname(file), { recursive: true }); await fs.writeFile(file, 'Synthetic installation fixture; never execute.') }
  return { folder, files: files.map(file => path.join(folder, file)) }
}
const launcher = await fixture('Explicit root with launcher - Very long synthetic path for installation layout and program choice and executable selection', ['Launcher.exe', 'bin/Game.exe'])
const binOnly = await fixture('Explicit root only bin', ['bin/Game.exe'])
const collectionA = await fixture('Collection/Edition A'), collectionB = await fixture('Collection/Edition B')
const defaultA = await fixture('Default A'), defaultB = await fixture('Default B', ['Game.exe', 'Alternate.exe'])
const cancelFixture = await fixture('Cancelled or stale choice'), outside = await fixture('Outside authorized root')
const conflictingWork = await fixture('Known conflicting Steam work')
await fs.writeFile(path.join(conflictingWork.folder, 'steam_appid.txt'), '200')
const replacedRoot = await fixture('Canonical root replaced after scan'), originalJunctionTarget = await fixture('Original selected junction target')
const originalJunction = path.join(temporary, 'Original selected junction')
await fs.symlink(originalJunctionTarget.folder, originalJunction, process.platform === 'win32' ? 'junction' : 'dir')
const emptyFolder = path.join(temporary, 'No program'); await fs.mkdir(emptyFolder)
const metadata = (id, title) => ({ id, title, originalTitle: title, aliases: [], coverUrl: null, bannerUrl: null, description: 'Synthetic catalog metadata', releaseDate: null, releaseDatePrecision: null, developers: ['Synthetic studio'], tags: [], ratings: [], characters: [], references: [], webUrl: null, contentRating: null, fetchedAt: date })
const makeGame = (id, name) => ({ id, name, nameIsCustom: true, metadata: metadata(`work-${id}`, `Catalog ${name}`), installations: [], preferredInstallationId: null, status: 'paused', personalRating: 0, favorite: true, categories: ['Synthetic category'], customTags: ['Synthetic tag'], notes: `Saved note ${id}`, totalPlaySeconds: 30, playCount: 1, sessions: [{ id: `session-${id}`, startedAt: date, endedAt: '2026-10-03T00:00:30.000Z', seconds: 30 }], addedAt: date, lastPlayedAt: date })
const games = [makeGame('wish-ui', 'Synthetic wish'), makeGame('wish-bin', 'Synthetic bin wish'), makeGame('wish-collection', 'Synthetic collection wish'), makeGame('wish-cancel', 'Synthetic cancel wish'), makeGame('wish-stale', 'Synthetic stale wish'), makeGame('multi', 'Synthetic multiple installations')]
games.find(game => game.id === 'wish-cancel').metadata.references = [{ source: 'steam', id: '100' }]
const multi = games.find(game => game.id === 'multi')
multi.installations = [{ id: 'default-a', folderPath: defaultA.folder, executablePath: defaultA.files[0], arguments: ['--keep-first'] }, { id: 'default-b', folderPath: defaultB.folder, executablePath: null, arguments: ['--keep-second'] }]
multi.preferredInstallationId = 'default-a'
await fs.mkdir(profile)
await fs.writeFile(path.join(profile, 'library.json'), JSON.stringify({ format: 'kun-gal-manager', version: 1, exportedAt: date, games }))
// Install observation and safety guards before loading the current production
// build. IPC handlers, renderer, scanner, validation and persistence stay real.
await fs.writeFile(bootstrap, `
const { app, dialog, ipcMain, net, session, shell } = require('electron')
const childProcess = require('node:child_process')
const control = globalThis.__installationTest = { dialogs: [], dialogCalls: [], prepared: [], held: false, release: null, remoteRequests: 0, processAttempts: 0, externalActions: 0 }
for (const name of ['spawn', 'execFile', 'exec', 'fork']) childProcess[name] = () => { control.processAttempts++; throw Error('Process launch forbidden in installation regression') }
globalThis.fetch = async () => { control.remoteRequests++; throw Error('Network forbidden in installation regression') }
const localFetch = net.fetch.bind(net)
net.fetch = input => { if (!String(input).startsWith('file:')) { control.remoteRequests++; throw Error('Remote Electron fetch forbidden') } return localFetch(input) }
app.whenReady().then(() => session.defaultSession.webRequest.onBeforeRequest({ urls: ['http://*/*', 'https://*/*'] }, (_, callback) => { control.remoteRequests++; callback({ cancel: true }) }))
for (const name of ['openPath', 'openExternal']) shell[name] = async () => { control.externalActions++; throw Error('External action forbidden in installation regression') }
dialog.showOpenDialog = async (_window, options) => {
  const expected = control.dialogs.shift()
  if (!expected || !options.properties.includes(expected.kind)) throw Error('Unexpected native dialog: ' + options.title)
  control.dialogCalls.push({ title: options.title, kind: expected.kind })
  if (expected.hold) { control.held = true; await new Promise(resolve => { control.release = resolve }); control.held = false }
  return { canceled: expected.cancelled === true, filePaths: expected.cancelled ? [] : [expected.path] }
}
const register = ipcMain.handle.bind(ipcMain)
ipcMain.handle = (channel, handler) => register(channel, async (...args) => { const result = await handler(...args); if (channel === 'kun-manager:prepareInstallationAttachment' && result.ok && result.value) control.prepared.push(result.value); return result })
require(${JSON.stringify(path.join(project, 'dist-electron', 'main.cjs'))})
`)
const isolatedEnv = { ...process.env, KGM_DATA_DIR: profile }
delete isolatedEnv.KGM_TEST_EXECUTABLE; delete isolatedEnv.KGM_DEV_URL
const launchOptions = { executablePath: path.join(project, 'node_modules/electron/dist/electron.exe'), args: [bootstrap], env: isolatedEnv, timeout: 30000 }
let application
const errors = [], checks = [], attachmentLayout = []
const waitFor = async (read, predicate, label) => {
  const deadline = Date.now() + 10000
  while (Date.now() < deadline) { const value = await read(); if (predicate(value)) return value; await new Promise(resolve => setTimeout(resolve, 25)) }
  throw Error(`Timed out: ${label}`)
}
try {
  application = await electron.launch(launchOptions)
  let page = await application.firstWindow()
  await page.emulateMedia({ reducedMotion: 'reduce' })
  page.on('pageerror', error => errors.push(error.message))
  await page.waitForFunction(() => window.kunManager !== undefined)
  const state = () => page.evaluate(() => window.kunManager.getState())
  const game = async id => (await state()).games.find(item => item.id === id)
  const queueDialog = (kind, selectedPath, options = {}) => application.evaluate((_, entry) => { globalThis.__installationTest.dialogs.push(entry) }, { kind, path: selectedPath, ...options })
  const prepare = async (id, folder) => { await queueDialog('openDirectory', folder); return page.evaluate(id => window.kunManager.prepareInstallationAttachment(id), id) }
  const attach = async (request, file, index = 0, options = {}) => { await queueDialog('openFile', file, options); return page.evaluate(({ requestId, candidateId }) => window.kunManager.attachLocalInstallation(requestId, candidateId), { requestId: request.requestId, candidateId: request.candidates[index].id }) }
  const cancel = request => page.evaluate(id => window.kunManager.cancelInstallationAttachment(id), request.requestId)
  const assertUnchanged = async (before, label) => assert.deepEqual((await state()).games, before, label)
  const closeModal = async () => { await page.getByRole('dialog').getByRole('button', { name: '关闭', exact: true }).click(); await page.getByRole('dialog').waitFor({ state: 'hidden' }) }
  assert.equal((await state()).settings.hasApiKey, false)
  assert.equal((await state()).dataPath, profile)

  // Real card action, directory-preparation IPC, program-choice IPC and durable
  // attachment all run without a key; no source association is replaced.
  await queueDialog('openDirectory', launcher.folder, { hold: true })
  await page.getByRole('button', { name: /添加本地游戏.*Synthetic wish/ }).evaluate(button => { button.click(); button.click() })
  await waitFor(() => application.evaluate(() => globalThis.__installationTest.held), Boolean, 'held directory picker after repeated card activation')
  assert.equal(await application.evaluate(() => globalThis.__installationTest.dialogCalls.length), 1)
  await application.evaluate(() => globalThis.__installationTest.release())
  const choose = page.getByRole('dialog').getByRole('button', { name: '选择程序并添加', exact: true })
  await choose.waitFor()
  assert.match(await page.getByRole('dialog').innerText(), /Explicit root with launcher/)
  // Visibility alone permits an opening frame. Capture only after finite
  // modal/ancestor animations settle and the fully opaque frame is painted.
  await page.getByRole('dialog').evaluate(async panel => {
    const elements = [panel]
    for (let ancestor = panel.parentElement; ancestor; ancestor = ancestor.parentElement) elements.push(ancestor)
    const animations = [...new Set(elements.flatMap(element => element.getAnimations({ subtree: element === panel })))].filter(animation => animation.effect?.getComputedTiming().iterations !== Infinity)
    await Promise.allSettled(animations.map(animation => animation.finished))
    await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))
  })
  assert.equal(await page.getByRole('dialog').evaluate(panel => {
    for (let element = panel; element; element = element.parentElement) if (Number(getComputedStyle(element).opacity) < 1) return false
    return true
  }), true, 'review screenshot must show the fully opaque modal')
  const artifacts = path.join(project, '.artifacts'); await fs.mkdir(artifacts, { recursive: true })
  const capturePaintedWindow = async destination => {
    await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))))
    // Electron capturePage uses the whole compositor surface at its real zoom.
    // A Playwright screenshot can crop that surface after webContents zoom.
    const png = Buffer.from(await application.evaluate(async ({ BrowserWindow }) => (await BrowserWindow.getAllWindows()[0].webContents.capturePage()).toPNG().toString('base64')), 'base64')
    await fs.writeFile(destination, png)
  }
  assert.ok(launcher.folder.length >= 180, 'attachment layout uses a genuinely long absolute fixture path')
  const checkActionText = async condition => {
    await choose.scrollIntoViewIfNeeded()
    const measured = await choose.evaluate(button => {
      const label = '选择程序并添加', walker = document.createTreeWalker(button, NodeFilter.SHOW_TEXT), matching = []
      for (let node = walker.nextNode(); node; node = walker.nextNode()) if (node.textContent?.includes(label)) matching.push(node)
      if (matching.length !== 1) return { labelNodes: matching.length, violations: ['action label is not a single measurable text node'] }
      const node = matching[0], start = node.textContent.indexOf(label), range = document.createRange()
      range.setStart(node, start); range.setEnd(node, start + label.length)
      const simple = rect => ({ left: rect.left, right: rect.right, top: rect.top, bottom: rect.bottom, width: rect.width, height: rect.height })
      const textRects = [...range.getClientRects()].map(simple), buttonRect = simple(button.getBoundingClientRect()), violations = []
      if (textRects.length !== 1) violations.push('action label wraps or is absent')
      for (const rect of textRects) {
        if (rect.left < buttonRect.left - 0.5 || rect.right > buttonRect.right + 0.5 || rect.top < buttonRect.top - 0.5 || rect.bottom > buttonRect.bottom + 0.5) violations.push('text range extends beyond button bounds')
        if (rect.left < -0.5 || rect.top < -0.5 || rect.right > innerWidth + 0.5 || rect.bottom > innerHeight + 0.5) violations.push('text range extends beyond visible viewport')
        for (let ancestor = node.parentElement; ancestor; ancestor = ancestor.parentElement) {
          const style = getComputedStyle(ancestor), clip = ancestor.getBoundingClientRect()
          if (style.overflowX !== 'visible' && (rect.left < clip.left - 0.5 || rect.right > clip.right + 0.5)) violations.push(`horizontal text clipping by ${ancestor.tagName}.${ancestor.className}`)
          if (style.overflowY !== 'visible' && (rect.top < clip.top - 0.5 || rect.bottom > clip.bottom + 0.5)) violations.push(`vertical text clipping by ${ancestor.tagName}.${ancestor.className}`)
        }
      }
      return { labelNodes: matching.length, viewport: { width: innerWidth, height: innerHeight, dpr: devicePixelRatio }, textRects, buttonRect, violations }
    })
    attachmentLayout.push({ condition, ...measured })
    assert.deepEqual(measured.violations, [], `the complete visible action label must fit at ${condition}`)
  }
  await checkActionText('default window, zoom 1, long path')
  await capturePaintedWindow(path.join(artifacts, 'iteration-installation-ui.png'))
  const originalWindow = await application.evaluate(({ BrowserWindow }) => { const win = BrowserWindow.getAllWindows()[0]; return { size: win.getContentSize(), zoom: win.webContents.getZoomFactor() } })
  for (const zoom of [1, 1.5]) {
    await application.evaluate(({ BrowserWindow }, zoom) => { const win = BrowserWindow.getAllWindows()[0]; win.setContentSize(1280, 720); win.webContents.setZoomFactor(zoom) }, zoom)
    await page.waitForTimeout(180)
    await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))))
    await checkActionText(`1280x720 content DIP, zoom ${zoom}, long path`)
    if (zoom === 1.5) { const output = path.join(artifacts, 'iteration-01'); await fs.mkdir(output, { recursive: true }); await capturePaintedWindow(path.join(output, 'attachment-zoom150.png')) }
  }
  await application.evaluate(({ BrowserWindow }, original) => { const win = BrowserWindow.getAllWindows()[0]; win.setContentSize(...original.size); win.webContents.setZoomFactor(original.zoom) }, originalWindow)
  await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))))
  checks.push('complete action text range fits button, clipping ancestors and viewport at default, 1280x720 zoom 1 and zoom 1.5 with a long path')
  await queueDialog('openFile', launcher.files[1])
  await choose.click()
  const attached = await waitFor(() => game('wish-ui'), item => item.installations.length === 1, 'UI attachment persisted')
  const { installations, preferredInstallationId, ...personal } = attached
  const { installations: oldInstallations, preferredInstallationId: oldPreferred, ...expectedPersonal } = games[0]
  assert.deepEqual(personal, expectedPersonal); assert.equal(oldInstallations.length, 0); assert.equal(oldPreferred, null)
  assert.equal(installations[0].folderPath, launcher.folder); assert.equal(installations[0].executablePath, launcher.files[1])
  assert.equal(preferredInstallationId, installations[0].id)
  await choose.waitFor({ state: 'hidden' })
  assert.equal(await page.getByRole('button', { name: '启动 Synthetic wish', exact: true }).isEnabled(), true)
  checks.push('wish-card attachment crosses real IPC without a key; explicit launcher root and all personal/source fields preserved')
  checks.push('repeated card activation during a pending directory picker opens one native dialog and preserves its first result')

  const preparedBin = await prepare('wish-bin', binOnly.folder)
  assert.equal(preparedBin.candidates.length, 1); assert.equal(preparedBin.candidates[0].folderPath, binOnly.folder)
  assert.equal((await attach(preparedBin, binOnly.files[0])).installations[0].folderPath, binOnly.folder)
  const beforeScan = (await state()).games
  await queueDialog('openDirectory', binOnly.folder)
  await page.getByRole('button', { name: '添加游戏', exact: true }).click()
  await page.getByRole('dialog').locator('.scan-list input[type="checkbox"]').waitFor()
  assert.equal(await page.getByRole('dialog').locator('.scan-list input[type="checkbox"]').isDisabled(), true)
  assert.match(await page.getByRole('dialog').innerText(), /重叠|归属冲突/)
  await closeModal()
  await queueDialog('openDirectory', binOnly.folder)
  const scannedBin = await page.evaluate(() => window.kunManager.scanFolder())
  await assert.rejects(page.evaluate(id => window.kunManager.importLocalGames([id], false), scannedBin[0].id), /范围重叠/)
  await assertUnchanged(beforeScan, 'overlapping scanner root cannot create a second installation')
  await queueDialog('openDirectory', launcher.folder)
  await page.getByRole('button', { name: '添加游戏', exact: true }).click()
  await page.getByRole('dialog').locator('.scan-list input[type="checkbox"]').waitFor()
  assert.equal(await page.getByRole('dialog').locator('.scan-list input[type="checkbox"]').isDisabled(), true)
  assert.match(await page.getByRole('dialog').innerText(), /已在书架/)
  await closeModal()
  await queueDialog('openDirectory', launcher.folder)
  const scannedExact = await page.evaluate(() => window.kunManager.scanFolder())
  const duplicate = await page.evaluate(id => window.kunManager.importLocalGames([id], false), scannedExact[0].id)
  assert.equal(duplicate.added, 0); assert.equal(duplicate.skipped, 1)
  checks.push('bin-only root retained; repeated scan overlap disabled and rejected; exact existing root skipped')

  const beforeCancelled = (await state()).games
  await queueDialog('openDirectory', undefined, { cancelled: true })
  assert.equal(await page.evaluate(() => window.kunManager.prepareInstallationAttachment('wish-cancel')), null)
  const cancelledExe = await prepare('wish-cancel', cancelFixture.folder)
  assert.equal(await attach(cancelledExe, undefined, 0, { cancelled: true }), null)
  await cancel(cancelledExe)
  await assertUnchanged(beforeCancelled, 'cancelling either native chooser cannot create an empty installation')
  const empty = await prepare('wish-cancel', emptyFolder); assert.equal(empty.candidates.length, 0); await cancel(empty)
  await assert.rejects(page.evaluate(() => window.kunManager.attachLocalInstallation('forged-request', 'forged-candidate')), /失效/)
  const forged = await prepare('wish-cancel', cancelFixture.folder)
  await assert.rejects(page.evaluate(id => window.kunManager.attachLocalInstallation(id, 'forged-candidate'), forged.requestId), /失效/)
  await assert.rejects(attach(forged, outside.files[0]), /根目录内/)
  await cancel(forged); await assertUnchanged(beforeCancelled, 'forged request, candidate and outside executable cannot change the library')
  const sourceConflict = await prepare('wish-cancel', conflictingWork.folder)
  await assert.rejects(attach(sourceConflict, conflictingWork.files[0]), /Steam 编号.*明确冲突/)
  await cancel(sourceConflict); await assertUnchanged(beforeCancelled, 'known independently read Steam ID conflict cannot change the current work or add an installation')
  checks.push('directory/program cancellation, empty scan, forged IDs and outside executable leave no partial records')
  checks.push('independently read Steam file ID conflict rejects the attachment without a network request or replacing current metadata')

  await queueDialog('openDirectory', cancelFixture.folder)
  await page.getByRole('button', { name: /添加本地游戏.*Synthetic cancel wish/ }).click()
  await page.getByRole('dialog').getByRole('button', { name: '选择程序并添加', exact: true }).waitFor()
  const uiCancelled = await application.evaluate(() => globalThis.__installationTest.prepared.at(-1))
  await closeModal()
  await assert.rejects(page.evaluate(({ requestId, candidateId }) => window.kunManager.attachLocalInstallation(requestId, candidateId), { requestId: uiCancelled.requestId, candidateId: uiCancelled.candidates[0].id }), /失效/)
  await assertUnchanged(beforeCancelled, 'closing the UI dialog revokes its main-process authorization')
  const pickerCancelled = await prepare('wish-cancel', cancelFixture.folder)
  await queueDialog('openFile', cancelFixture.files[0], { hold: true })
  await page.evaluate(({ requestId, candidateId }) => { globalThis.__attachmentOutcome = window.kunManager.attachLocalInstallation(requestId, candidateId).then(value => ({ ok: true, value }), error => ({ ok: false, message: error.message })) }, { requestId: pickerCancelled.requestId, candidateId: pickerCancelled.candidates[0].id })
  await waitFor(() => application.evaluate(() => globalThis.__installationTest.held), Boolean, 'held program picker')
  await cancel(pickerCancelled); await application.evaluate(() => globalThis.__installationTest.release())
  const cancelledOutcome = await page.evaluate(() => globalThis.__attachmentOutcome)
  assert.equal(cancelledOutcome.ok, false); assert.match(cancelledOutcome.message, /失效|取消/)
  await assertUnchanged(beforeCancelled, 'cancelling a request while its program chooser is open rejects its later result')
  checks.push('closing attachment UI and cancelling during a native program picker revoke the request')

  const changed = await prepare('wish-stale', cancelFixture.folder)
  await page.evaluate(() => window.kunManager.updateGame('wish-stale', { name: 'Newer synthetic name' }))
  await assert.rejects(attach(changed, cancelFixture.files[0]), /名称、关联或安装已变化/)
  await cancel(changed)
  assert.equal((await game('wish-stale')).installations.length, 0)
  const personalRequest = await prepare('wish-stale', cancelFixture.folder)
  await page.evaluate(() => window.kunManager.updateGame('wish-stale', { notes: 'Newer personal note', personalRating: 9 }))
  const personalAttached = await attach(personalRequest, cancelFixture.files[0])
  assert.equal(personalAttached.notes, 'Newer personal note'); assert.equal(personalAttached.personalRating, 9)
  checks.push('stale name intent rejects old attachment; later rating and notes coexist with a valid attachment')

  const collection = await prepare('wish-collection', path.dirname(collectionA.folder))
  assert.equal(collection.candidates.length, 2)
  const index = collection.candidates.findIndex(item => item.folderPath === collectionB.folder)
  assert.ok(index >= 0)
  const chosen = await attach(collection, collectionB.files[0], index)
  assert.equal(chosen.installations[0].folderPath, collectionB.folder)
  const collision = await prepare('wish-cancel', launcher.folder)
  await assert.rejects(attach(collision, launcher.files[0]), /已属于/); await cancel(collision)
  const duplicateAttachment = await prepare('wish-ui', launcher.folder)
  await assert.rejects(attach(duplicateAttachment, launcher.files[0]), /不能重复添加/); await cancel(duplicateAttachment)
  const overlapping = await prepare('wish-cancel', path.dirname(launcher.files[1]))
  await assert.rejects(attach(overlapping, launcher.files[1]), /范围重叠/); await cancel(overlapping)
  checks.push('multiple scan discoveries retain the chosen game directory; same, other-owned and child roots cannot be attached twice')

  const beforeReplacedRoot = (await state()).games
  const scannedBeforeReplacement = await prepare('wish-cancel', replacedRoot.folder)
  await fs.rename(replacedRoot.folder, `${replacedRoot.folder}-original`)
  await fs.symlink(outside.folder, replacedRoot.folder, process.platform === 'win32' ? 'junction' : 'dir')
  await assert.rejects(attach(scannedBeforeReplacement, path.join(replacedRoot.folder, 'Game.exe')), /实际位置已变化/)
  await cancel(scannedBeforeReplacement); await assertUnchanged(beforeReplacedRoot, 'a changed junction target cannot reuse the scan authorization')
  const originalJunctionChoice = await prepare('wish-cancel', originalJunction)
  assert.equal(originalJunctionChoice.rootPath, originalJunctionTarget.folder)
  const originalJunctionAttached = await attach(originalJunctionChoice, path.join(originalJunction, 'Game.exe'))
  assert.equal(originalJunctionAttached.installations[0].folderPath, originalJunctionTarget.folder)
  assert.equal(originalJunctionAttached.installations[0].executablePath, originalJunctionTarget.files[0])
  checks.push('junction replacement after scan is rejected; selecting an original junction caches and attaches its canonical target correctly')

  const beforeDefault = await game('multi')
  await queueDialog('openFile', defaultB.files[0])
  const configured = await page.evaluate(() => window.kunManager.pickExecutable('multi', 'default-b'))
  assert.equal(configured.preferredInstallationId, 'default-a'); assert.deepEqual(configured.installations[1].arguments, ['--keep-second'])
  assert.equal(configured.metadata.id, beforeDefault.metadata.id); assert.equal(configured.notes, beforeDefault.notes)
  await page.evaluate(() => window.kunManager.updateGame('multi', { preferredInstallationId: 'default-b' }))
  await queueDialog('openFile', defaultA.files[0]); await page.evaluate(() => window.kunManager.pickExecutable('multi', 'default-a'))
  assert.equal((await game('multi')).preferredInstallationId, 'default-b')
  checks.push('setting a nondefault program preserves the configured default and launch arguments; explicit default changes remain effective')

  const expected = (await state()).games
  assert.equal((await state()).settings.hasApiKey, false)
  const control = await application.evaluate(() => ({ remoteRequests: globalThis.__installationTest.remoteRequests, processAttempts: globalThis.__installationTest.processAttempts, externalActions: globalThis.__installationTest.externalActions, remainingDialogs: globalThis.__installationTest.dialogs.length }))
  assert.deepEqual(control, { remoteRequests: 0, processAttempts: 0, externalActions: 0, remainingDialogs: 0 })
  assert.deepEqual(errors, [])
  await application.close(); application = await electron.launch(launchOptions)
  page = await application.firstWindow(); await page.waitForFunction(() => window.kunManager !== undefined)
  assert.deepEqual((await page.evaluate(() => window.kunManager.getState())).games, expected)
  checks.push('restart preserves the attached roots, chosen programs, sources and personal records')
  const report = { currentProjectBuild: true, independentSyntheticProfile: true, apiKeyUsed: false, checks, attachmentLayout, controls: control, rendererErrors: errors }
  await fs.writeFile(path.join(project, '.artifacts', 'iteration-installation.json'), JSON.stringify(report, null, 2))
  console.log(JSON.stringify(report))
} finally {
  if (application) await application.close()
  if (path.dirname(path.resolve(temporary)) !== temporaryRoot || !path.basename(temporary).startsWith('the-order-iteration-installation-')) throw Error('Unexpected temporary fixture directory')
  await fs.rm(temporary, { recursive: true, force: true })
}
