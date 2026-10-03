import assert from 'node:assert/strict'
import { promises as fs } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { createHash } from 'node:crypto'
import { execFileSync } from 'node:child_process'
import { _electron as electron } from '../node_modules/playwright/index.mjs'

const project = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const baseline = process.argv.includes('--baseline'), phase = baseline ? 'before' : 'after'
const roundArgument = process.argv.find(arg => arg.startsWith('--evidence-round='))
assert.ok(!roundArgument || /^--evidence-round=[0-8]$/.test(roundArgument), 'Expected a bounded night evidence round')
const root = path.join(project, '.artifacts/overnight-2026-10-04', roundArgument ? `round-${roundArgument.at(-1)}/settings-workspace` : 'round-3'), output = path.join(root, phase), profile = path.join(root, 'ui-profile')
assert.equal(path.resolve(profile), path.resolve(root, 'ui-profile'))
assert.equal(path.dirname(profile), root)
const marker = path.join(profile, '.synthetic-test-profile')
if (await fs.stat(profile).catch(error => { if (error.code === 'ENOENT') return null; throw error })) {
  assert.equal(await fs.readFile(marker, 'utf8'), 'desktop-settings-workspace synthetic profile')
  await fs.rm(profile, { recursive: true })
}
await fs.mkdir(profile, { recursive: true }); await fs.mkdir(output, { recursive: true }); await fs.writeFile(marker, 'desktop-settings-workspace synthetic profile')
const library = { format: 'kun-gal-manager', version: 1, exportedAt: '2026-10-04T00:00:00.000Z', games: [] }
await fs.writeFile(path.join(profile, 'library.json'), JSON.stringify(library))
const hash = value => createHash('sha256').update(value).digest('hex')
const report = { phase, checkoutHead: execFileSync('git', ['rev-parse', 'HEAD'], { cwd: project, encoding: 'utf8' }).trim(), sourceDirty: !!execFileSync('git', ['status', '--porcelain'], { cwd: project, encoding: 'utf8' }).trim(), fixtureSha256: hash(JSON.stringify(library)), profile, screenshots: [], checks: [], observedIssues: [], rendererErrors: [], runtimeHashes: {}, sourceHashes: {}, syntheticOnly: true, realApiCalls: 0, formalProfileAccessed: false }
let app, page
const state = () => page.evaluate(() => window.kunManager.getState())
const field = text => page.locator('.field-label').filter({ hasText: text }).locator('input,select').first()
const save = () => page.getByRole('button', { name: baseline ? '保存设置' : '保存全部设置', exact: true })
const conditions = [{ width: 1440, height: 940, zoom: 1 }, { width: 1000, height: 700, zoom: 1.5 }]
async function resize(c) { await app.evaluate(({ BrowserWindow }, c) => { const window = BrowserWindow.getAllWindows()[0]; window.setContentSize(c.width, c.height); window.webContents.setZoomFactor(c.zoom) }, c); await page.waitForFunction(c => Math.abs(innerWidth - c.width / c.zoom) < 3 && Math.abs(innerHeight - c.height / c.zoom) < 3, c); await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))) }
async function capture(label, c, target = '.settings-panel') {
  await page.evaluate(() => document.querySelectorAll('[data-kun-toast-close]').forEach(button => button.click())); await page.waitForFunction(() => !document.querySelector('[data-kun-toast-close]'))
  await resize(c); await page.locator(target).first().scrollIntoViewIfNeeded(); await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))); await new Promise(resolve => setTimeout(resolve, 100))
  const actual = await page.evaluate(() => ({ width: innerWidth, height: innerHeight, dpr: devicePixelRatio, scrollWidth: document.documentElement.scrollWidth, heading: document.querySelector('.page-heading h1')?.textContent, action: document.querySelector('.settings-save-bar')?.getBoundingClientRect().toJSON() ?? null }))
  assert.equal(actual.heading, '偏好设置'); assert.ok(actual.scrollWidth <= actual.width + 2, label + ' overflow')
  const png = Buffer.from(await app.evaluate(async ({ BrowserWindow }) => { if (BrowserWindow.getAllWindows().length !== 1) throw Error('Expected one native window'); return (await BrowserWindow.getAllWindows()[0].webContents.capturePage()).toPNG().toString('base64') }), 'base64')
  const pixels = [png.readUInt32BE(16), png.readUInt32BE(20)]; assert.ok(Math.abs(pixels[0] - c.width * actual.dpr / c.zoom) < 4); assert.ok(Math.abs(pixels[1] - c.height * actual.dpr / c.zoom) < 4)
  const file = label + '-' + c.width + 'x' + c.height + '-zoom' + c.zoom + '.png'; await fs.writeFile(path.join(output, file), png); report.screenshots.push({ file, requested: c, actual, pixels, target, sha256: hash(png) })
}
async function armSave(mode) {
  await app.evaluate(({ ipcMain }, mode) => {
    const channel = 'kun-manager:saveSettings', original = ipcMain._invokeHandlers.get(channel)
    let release, entered; const gate = new Promise(resolve => { release = resolve }), enteredPromise = new Promise(resolve => { entered = resolve })
    globalThis.__settingsGate = { release, enteredPromise, calls: 0 }
    ipcMain.removeHandler(channel); ipcMain.handle(channel, async (event, ...args) => { globalThis.__settingsGate.calls++; entered(); let timer; try { if (mode === 'fail') return { ok: false, error: '合成设置保存失败，请重试。' }; await Promise.race([gate, new Promise((_, reject) => { timer = setTimeout(() => reject(Error('Bounded synthetic gate timed out')), 10000) })]); return await original(event, ...args) } finally { clearTimeout(timer); ipcMain.removeHandler(channel); ipcMain.handle(channel, original) } })
  }, mode)
}
async function armConnection(mode) {
  await app.evaluate(({ ipcMain }, mode) => {
    const channel = 'kun-manager:testConnection', original = ipcMain._invokeHandlers.get(channel)
    let release, entered; const gate = new Promise(resolve => { release = resolve }), enteredPromise = new Promise(resolve => { entered = resolve })
    globalThis.__connectionGate = { release, enteredPromise, calls: 0 }
    ipcMain.removeHandler(channel); ipcMain.handle(channel, async (event, ...args) => {
      globalThis.__connectionGate.calls++; entered(); let timer
      try {
        if (mode === 'fail') return { ok: true, value: { ok: false, message: '合成连接不可用；请检查已保存配置。' } }
        await Promise.race([gate, new Promise((_, reject) => { timer = setTimeout(() => reject(Error('Bounded synthetic connection gate timed out')), 10000) })])
        return await original(event, ...args)
      } finally { clearTimeout(timer); ipcMain.removeHandler(channel); ipcMain.handle(channel, original) }
    })
  }, mode)
}

async function setup() {
  const env = { ...process.env, KGM_DATA_DIR: profile }; delete env.KGM_TEST_EXECUTABLE; delete env.KGM_DEV_URL
  app = await electron.launch({ executablePath: path.join(project, 'node_modules/electron/dist/electron.exe'), args: [project], env, timeout: 30000 })
  await app.evaluate(() => { globalThis.__connectionCalls = 0; globalThis.fetch = async () => { globalThis.__connectionCalls++; return new Response(JSON.stringify({ object: 'list', items: [] }), { headers: { 'content-type': 'application/json' } }) } })
  page = await app.firstWindow(); page.on('pageerror', error => report.rendererErrors.push(error.message)); await page.reload(); await page.emulateMedia({ reducedMotion: 'reduce' }); await page.getByRole('button', { name: '偏好设置', exact: true }).click(); await page.locator('.settings-panel').first().waitFor(); assert.equal(path.resolve((await state()).dataPath), profile)
}
try {
  const html = await fs.readFile(path.join(project, 'dist/index.html'), 'utf8')
  for (const file of ['dist/index.html', 'dist-electron/main.cjs', 'dist-electron/preload.cjs', ...[...html.matchAll(/(?:src|href)="([^"]+\.(?:js|css))"/g)].map(match => path.posix.normalize('dist/' + match[1].replace(/^\//, '')))]) report.runtimeHashes[file] = hash(await fs.readFile(path.join(project, file)))
  if (baseline) { const prior = JSON.parse(await fs.readFile(path.join(project, '.artifacts/overnight-2026-10-04/round-2/after/manifest.json'), 'utf8')); assert.deepEqual(report.runtimeHashes, prior.runtimeHashes); report.businessSourceCommit = '33f5c9fd780553c9c4549a281b98c0669ce30773' }
  else for (const file of ['src/App.vue', 'src/components/PreferencesSettings.vue', 'src/workspace-refresh.css']) report.sourceHashes[file] = hash((await fs.readFile(path.join(project, file), 'utf8')).replace(/\r\n/g, '\n'))
  await setup()
  for (const c of conditions) { await capture('settings-source', c); await capture('settings-preferences', c, '.settings-panel:nth-of-type(2)') }
  await field('外观主题').selectOption('dark'); await field('API 密钥').fill('synthetic-settings-submitted'); await armSave('hold'); await save().click(); await app.evaluate(() => Promise.race([globalThis.__settingsGate.enteredPromise, new Promise((_, reject) => setTimeout(() => reject(Error('Gate not entered')), 3000))])); await field('外观主题').selectOption('system'); await field('API 密钥').fill('synthetic-settings-later-input'); if (!baseline) { await capture('settings-saving-with-later-input', conditions[1], '.settings-save-bar'); assert.equal(await save().isDisabled(), true) }; await app.evaluate(() => globalThis.__settingsGate.release()); await page.waitForFunction(async () => (await window.kunManager.getState()).settings.theme === 'dark'); await save().waitFor(); await page.waitForFunction(() => !document.querySelector('.settings-save-bar button:disabled') || !document.querySelector('.settings-save-bar')); await new Promise(resolve => setTimeout(resolve, 100))
  const laterPreserved = await field('外观主题').inputValue() === 'system' && await field('API 密钥').inputValue() === 'synthetic-settings-later-input'
  report.checks.push({ laterInputPreserved: laterPreserved }); if (baseline) { assert.equal(laterPreserved, false); report.observedIssues.push('Saving overwrites later theme/key input') } else assert.equal(laterPreserved, true)
  await field('API 密钥').fill('synthetic-settings-not-to-save'); await page.getByRole('button', { name: baseline ? '移除密钥' : '移除已保存密钥', exact: true }).click(); await page.waitForFunction(async () => (await window.kunManager.getState()).settings.hasApiKey === false, null, { timeout: baseline ? 700 : 5000 }).catch(error => { if (!baseline) throw error })
  const removed = !(await state()).settings.hasApiKey; report.checks.push({ storedKeyRemoved: removed }); if (baseline) { assert.equal(removed, false); report.observedIssues.push('Remove key instead saves the typed key') } else { assert.equal(removed, true); assert.equal(await field('API 密钥').inputValue(), 'synthetic-settings-not-to-save') }

  if (!baseline) {
    const backupBefore = hash(await fs.readFile(path.join(profile, 'library.json')))
    // A failed write is kept inline, leaves the draft available, and never starts a connection check.
    await armSave('fail'); await page.getByRole('button', { name: '保存并测试连接', exact: true }).click()
    await page.locator('.preferences-error').filter({ hasText: '设置保存未确认' }).waitFor()
    assert.equal(await field('API 密钥').inputValue(), 'synthetic-settings-not-to-save')
    assert.equal(await app.evaluate(() => globalThis.__connectionCalls), 0)
    for (const c of conditions) await capture('settings-save-failed', c, '.settings-save-bar')
    await page.getByRole('button', { name: '保存并测试连接', exact: true }).click()
    await page.locator('.connection-feedback').filter({ hasText: '已保存连接可用' }).waitFor()
    assert.equal((await state()).settings.theme, 'system'); assert.equal((await state()).settings.hasApiKey, true)
    assert.equal(await field('API 密钥').inputValue(), ''); assert.equal(await page.locator('.preferences-error').count(), 0)
    for (const c of conditions) await capture('settings-saved-and-tested', c)
    const encrypted = JSON.parse(await fs.readFile(path.join(profile, 'settings.json'), 'utf8'))
    assert.ok(encrypted.encryptedKey); assert.ok(!JSON.stringify(encrypted).includes('synthetic-settings-not-to-save'))
    report.checks.push({ failedWriteKeptDraft: true, failureDidNotTest: true, savedThenTested: true, storedCredentialEncrypted: true })
    // The separate test acts on saved configuration, never on the new URL input.
    const savedUrl = (await state()).settings.baseUrl
    await field('API Base URL').fill('https://synthetic-unsaved.invalid/v2/')
    await page.getByRole('button', { name: '测试已保存连接', exact: true }).click()
    await page.locator('.connection-feedback').filter({ hasText: '已保存连接可用' }).waitFor()
    assert.equal((await state()).settings.baseUrl, savedUrl)
    await page.getByText('连接有未保存的输入；测试只使用已保存配置。', { exact: true }).waitFor()
    await page.getByRole('button', { name: '放弃未保存更改', exact: true }).click(); assert.equal(await field('API Base URL').inputValue(), savedUrl)
    await armConnection('fail'); await page.getByRole('button', { name: '测试已保存连接', exact: true }).click()
    await page.locator('.connection-feedback').filter({ hasText: '连接未通过' }).waitFor()
    for (const c of conditions) await capture('settings-connection-failed', c)
    await armConnection('hold'); await page.getByRole('button', { name: '测试已保存连接', exact: true }).click()
    await app.evaluate(() => Promise.race([globalThis.__connectionGate.enteredPromise, new Promise((_, reject) => setTimeout(() => reject(Error('Connection gate not entered')), 3000))]))
    assert.equal(await page.getByRole('button', { name: '移除已保存密钥', exact: true }).isDisabled(), true)
    await capture('settings-connection-loading', conditions[1])
    await app.evaluate(() => globalThis.__connectionGate.release()); await page.locator('.connection-feedback').filter({ hasText: '已保存连接可用' }).waitFor()
    report.checks.push({ testUsesSavedUrl: true, connectionFailureInline: true, sharedOperationLock: true })
    // Keyboard-only activation of the switch and save action uses the actual rendered controls.
    await field('外观主题').selectOption('dark'); await field('非竖版封面默认展示').selectOption('plain')
    const reading = page.getByRole('switch', { name: '读取程序内的游戏名称', exact: true }); const wasReading = (await state()).settings.readExecutableMetadata
    await reading.focus(); await page.keyboard.press('Space'); await save().focus(); await page.keyboard.press('Enter')
    await page.waitForFunction(async () => (await window.kunManager.getState()).settings.theme === 'dark')
    await page.getByText('所有设置已保存', { exact: true }).waitFor(); assert.equal((await state()).settings.nonPortraitCoverDisplay, 'plain'); assert.equal((await state()).settings.readExecutableMetadata, !wasReading)
    assert.equal(await page.locator('html').evaluate(el => el.classList.contains('kun-dark-mode')), true)
    for (const c of conditions) { await capture('settings-dark-preferences', c, '.settings-panel:nth-of-type(2)'); await resize(c); await field('外观主题').focus(); await reading.focus(); await page.waitForFunction(() => { const input = [...document.querySelectorAll('[role=switch]')].find(el => el.closest('label')?.textContent?.trim() === '读取程序内的游戏名称'); const control = input?.closest('label')?.getBoundingClientRect(), bar = document.querySelector('.settings-save-bar')?.getBoundingClientRect(); return document.activeElement === input && control && bar && control.top >= 0 && control.bottom < bar.top }); await capture('settings-dark-switches', c, '.preferences-option:last-of-type'); await capture('settings-dark-backup', c, '.settings-backup') }
    const keyboard = await page.evaluate(() => ({ saveLabel: document.querySelector('.preferences-save')?.textContent, switchNames: [...document.querySelectorAll('[role=switch]')].map(el => el.closest('label')?.textContent?.trim()) }))
    assert.ok(keyboard.switchNames.includes('读取程序内的游戏名称')); assert.equal(hash(await fs.readFile(path.join(profile, 'library.json'))), backupBefore)
    const saved = (await state()).settings; await app.close(); app = undefined; await setup(); assert.deepEqual((await state()).settings, saved)
    assert.equal(await field('API 密钥').inputValue(), ''); assert.equal(await save().isDisabled(), true); assert.equal(await page.locator('html').evaluate(el => el.classList.contains('kun-dark-mode')), true)
    assert.equal(hash(await fs.readFile(path.join(profile, 'library.json'))), backupBefore)
    report.checks.push({ keyboardSwitchAndSave: true, keyboardFocusAboveStickyBar: true, darkAndZoomChecked: true, actualProcessRestartPreservedSettings: true, libraryUnchanged: true })
  }
  assert.deepEqual(report.rendererErrors, []); report.status = 'passed'
} catch (error) { report.status = 'failed'; report.error = error.stack; throw error }
finally { await fs.writeFile(path.join(output, 'manifest.json'), JSON.stringify(report, null, 2)); if (app) await app.close() }
console.log(JSON.stringify({ phase, status: report.status, screenshots: report.screenshots.length, observedIssues: report.observedIssues }))
