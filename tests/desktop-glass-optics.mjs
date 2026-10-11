import assert from 'node:assert/strict'
import { promises as fs } from 'node:fs'
import path from 'node:path'
import os from 'node:os'
import { createHash } from 'node:crypto'
import { _electron as electron } from '../node_modules/playwright/index.mjs'
import { prepareTrialProfile } from '../scripts/overnight-trial-data.mjs'

// Optical fixture, not a product screenshot: the real packaged material is put
// over a controlled pattern to prove edge displacement reaches rendered pixels.
const index = process.argv.indexOf('--executable')
assert.ok(index >= 0 && process.argv[index + 1], 'Explicit candidate executable required')
const executable = path.resolve(process.argv[index + 1])
const output = path.resolve('.artifacts/iteration-06/optics', new Date().toISOString().replace(/[:.]/g, '-'))
const temporary = await fs.mkdtemp(path.join(os.tmpdir(), 'the-order-optics-'))
const profile = path.join(temporary, 'profile')
const hash = bytes => createHash('sha256').update(bytes).digest('hex')
const report = { executable, profile, fixture: 'Generated checkerboard beneath the actual production toolbar. Decorative tint disabled in this optical fixture only.', screenshots: [], rendererErrors: [], formalProfileAccessed: false, realApiCalls: 0 }
let app
await fs.mkdir(output, { recursive: true })
try {
  await prepareTrialProfile({ profile })
  const env = { ...process.env }
  for (const key of Object.keys(env)) if (key.startsWith('KGM_')) delete env[key]
  delete env.ELECTRON_RUN_AS_NODE; delete env.NODE_OPTIONS
  env.KGM_DATA_DIR = profile
  app = await electron.launch({ executablePath: executable, args: [], env, timeout: 30000 })
  const page = await app.firstWindow(); page.on('pageerror', error => report.rendererErrors.push(error.message))
  await app.evaluate(({ session, BrowserWindow, app }, expected) => {
    if (app.getPath('userData') !== expected) throw Error('Wrong profile')
    session.defaultSession.webRequest.onBeforeRequest({ urls: ['http://*/*', 'https://*/*'] }, (_request, callback) => callback({ cancel: true }))
    const win = BrowserWindow.getAllWindows()[0]; win.setContentSize(1000, 700); win.webContents.setZoomFactor(1)
  }, profile)
  await page.waitForFunction(() => window.kunManager !== undefined)
  await page.locator('.sidebar').getByRole('button', { name: '偏好设置', exact: true }).click()
  await page.locator('.preferences-settings input[value="glass"]').check()
  await page.locator('.preferences-settings .field-label').filter({ hasText: '外观主题' }).locator('select').selectOption('light')
  await page.getByRole('button', { name: '保存全部设置', exact: true }).click()
  await page.waitForFunction(() => document.documentElement.dataset.appearance === 'glass')
  await page.locator('.sidebar').getByRole('button', { name: '我的书架', exact: true }).click()
  await page.locator('.compact-library-topbar[data-liquid-surface]').waitFor()
  await page.evaluate(() => {
    const pattern = document.createElement('div')
    pattern.style.cssText = 'position:fixed;inset:0;z-index:1000;background:repeating-conic-gradient(#111 0% 25%,#eee 0% 50%) 0 0 / 16px 16px;pointer-events:none'
    document.body.append(pattern)
    const toolbar = document.querySelector('.compact-library-topbar')
    toolbar.style.cssText += ';position:fixed!important;left:120px!important;top:200px!important;width:640px!important;height:180px!important;min-height:0!important;max-height:none!important;margin:0!important;box-sizing:border-box!important;z-index:1001!important;background:transparent!important;border:0!important;border-radius:24px!important;box-shadow:none!important;transform:scale(.96)!important;transform-origin:top left!important;'
    for (const child of toolbar.children) child.style.visibility = 'hidden'
    const label = document.createElement('span'); label.textContent = '清晰的中文 / The Order'
    label.style.cssText = 'position:absolute;left:140px;top:65px;width:320px;height:50px;display:grid;place-items:center;background:white;color:black;border-radius:0;font:20px Segoe UI;visibility:visible'
    toolbar.append(label)
    const style = document.createElement('style'); style.textContent = '.compact-library-topbar::after{display:none!important}'
    document.head.append(style)
  })
  await page.waitForFunction(() => {
    const node = document.querySelector('.compact-library-topbar'), filter = document.getElementById(node.dataset.liquidSurface)
    return filter?.getAttribute('width') === '640' && filter.getAttribute('height') === '180'
  })
  assert.ok(Math.abs(await page.locator('.compact-library-topbar').evaluate(node => node.getBoundingClientRect().width) - 614.4) < 1, 'Fixture must include the dialog-scale geometry case')
  await page.locator('.compact-library-topbar').evaluate(node => node.style.setProperty('transform', 'none', 'important'))
  report.transformedSurfaceUsesLocalGeometry = true
  const capture = async label => {
    await page.waitForTimeout(180)
    const data = await app.evaluate(async ({ BrowserWindow }) => {
      const image = (await BrowserWindow.getAllWindows()[0].webContents.capturePage({ x: 120, y: 200, width: 640, height: 180 })).resize({ width: 640, height: 180, quality: 'best' })
      return { png: image.toPNG().toString('base64'), bitmap: image.toBitmap().toString('base64') }
    })
    const file = path.join(output, `${label}.png`), bytes = Buffer.from(data.png, 'base64')
    await fs.writeFile(file, bytes); report.screenshots.push({ file, sha256: hash(bytes) })
    return Buffer.from(data.bitmap, 'base64')
  }
  const refracted = await capture('production-edge-refraction')
  report.filter = await page.locator('.compact-library-topbar').evaluate(node => {
    const filter = document.getElementById(node.dataset.liquidSurface)
    const values = [...filter.querySelectorAll('feDisplacementMap')].map(item => item.getAttribute('scale'))
    filter.querySelectorAll('feDisplacementMap').forEach(item => item.setAttribute('scale', '0'))
    return { backdrop: getComputedStyle(node).backdropFilter, wavelengths: values, geometry: [filter.getAttribute('width'), filter.getAttribute('height')] }
  })
  const flat = await capture('same-material-zero-refraction')
  const difference = (left, top, width, height) => {
    let total = 0, count = 0
    for (let y = top; y < top + height; y++) for (let x = left; x < left + width; x++) for (let channel = 0; channel < 3; channel++) {
      const i = (y * 640 + x) * 4 + channel; total += Math.abs(flat[i] - refracted[i]); count++
    }
    return total / count
  }
  report.difference = { edge: difference(5, 35, 10, 110), interior: difference(50, 35, 70, 110), foreground: difference(145, 70, 310, 40) }
  assert.ok(report.difference.edge > 5, 'Actual edge pixels must refract, not merely parse a CSS filter')
  assert.ok(report.difference.edge > report.difference.interior * 3, 'Displacement must be concentrated around the edge')
  assert.ok(report.difference.interior < 3, 'The interior must stay almost unchanged')
  assert.equal(report.difference.foreground, 0, 'Foreground labels must remain sharp and unfiltered')
  assert.deepEqual(report.rendererErrors, [])
  report.runtimeHashes = { asar: hash(await fs.readFile(path.join(path.dirname(executable), 'resources/app.asar'))) }
  report.status = 'passed'
  console.log(JSON.stringify({ status: report.status, difference: report.difference, output }))
} catch (error) { report.status = 'failed'; report.error = String(error.stack ?? error); throw error }
finally {
  if (app) await app.close()
  await fs.writeFile(path.join(output, 'manifest.json'), JSON.stringify(report, null, 2))
  if (path.dirname(temporary) === os.tmpdir() && path.basename(temporary).startsWith('the-order-optics-')) await fs.rm(temporary, { recursive: true, force: true })
}
