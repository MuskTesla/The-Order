import assert from 'node:assert/strict'
import { promises as fs } from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { _electron as electron } from '../node_modules/playwright/index.mjs'

const project = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const tempRoot = path.resolve(os.tmpdir()), temporary = await fs.mkdtemp(path.join(tempRoot, 'the-order-photo-codec-')), profile = path.join(temporary, 'profile')
const candidate = process.argv.includes('--candidate'), candidateRoot = path.join(project, '.artifacts', 'iteration-03', 'candidate', 'win-unpacked')
const executablePath = path.join(candidate ? candidateRoot : path.join(project, 'node_modules', 'electron', 'dist'), candidate ? 'The Order.exe' : 'electron.exe')
const env = { ...process.env }
for (const key of Object.keys(env)) if (key.startsWith('KGM_')) delete env[key]
delete env.ELECTRON_RUN_AS_NODE; delete env.NODE_OPTIONS; env.KGM_DATA_DIR = profile
const seeded = { id: 'photo-codec-fixture', name: 'Synthetic raster fixture', metadata: null, installations: [], preferredInstallationId: null, status: 'planned', personalRating: 0, favorite: false, categories: [], customTags: [], notes: 'Keep personal note', totalPlaySeconds: 0, playCount: 0, sessions: [], addedAt: '2026-10-03T00:00:00.000Z', lastPlayedAt: null }
const checks = [], errors = [], orientations = [], dimensions = []
let application
const output = path.join(project, '.artifacts', 'iteration-03', 'local-cover-codec')
const launchOptions = { executablePath, args: candidate ? [] : [project], env, timeout: 30000 }
async function start() {
  application = await electron.launch(launchOptions)
  await application.evaluate(({ session, dialog }) => {
    session.defaultSession.webRequest.onBeforeRequest({ urls: ['http://*/*', 'https://*/*'] }, (_, callback) => callback({ cancel: true }))
    globalThis.fetch = async () => { throw Error('No network allowed in raster codec tests') }
    globalThis.__photoTest = { selected: null, canceled: false, expectedFilters: ['png', 'jpg', 'jpeg'] }
    dialog.showOpenDialog = async (_window, options) => {
      if (JSON.stringify(options.filters?.[0]?.extensions) !== JSON.stringify(globalThis.__photoTest.expectedFilters)) throw Error('Unexpected native image filters')
      return { canceled: globalThis.__photoTest.canceled, filePaths: globalThis.__photoTest.selected ? [globalThis.__photoTest.selected] : [] }
    }
  })
  const page = await application.firstWindow(); page.on('pageerror', error => errors.push(error.message))
  await page.waitForFunction(() => !!window.kunManager)
  const state = await page.evaluate(() => window.kunManager.getState())
  assert.equal(path.resolve(state.dataPath), path.resolve(profile)); assert.equal(state.activeSession, null)
  const runtime = await application.evaluate(({ app }) => ({ packaged: app.isPackaged, appPath: app.getAppPath() }))
  assert.equal(runtime.packaged, candidate)
  assert.equal(path.resolve(runtime.appPath), candidate ? path.join(candidateRoot, 'resources', 'app.asar') : project)
  return page
}
async function select(page, filename, display = null) {
  await application.evaluate((_, selected) => { globalThis.__photoTest.selected = selected }, filename)
  return page.evaluate(display => window.kunManager.pickLocalCover('photo-codec-fixture', null, display), display)
}
async function pixels(cover) {
  return application.evaluate(({ nativeImage }, input) => {
    const image = nativeImage.createFromBuffer(Buffer.from(input.dataUrl.split(',')[1], 'base64')), size = image.getSize(), bitmap = image.toBitmap()
    const at = (x, y) => Array.from(bitmap.subarray((y * size.width + x) * 4, (y * size.width + x) * 4 + 4))
    const left = Math.floor(size.width * .2), right = Math.floor(size.width * .8), top = Math.floor(size.height * .2), bottom = Math.floor(size.height * .8)
    return { ...size, corners: [at(left, top), at(right, top), at(left, bottom), at(right, bottom)] }
  }, cover)
}
try {
  await fs.mkdir(profile, { recursive: true })
  await fs.writeFile(path.join(profile, 'library.json'), JSON.stringify({ format: 'kun-gal-manager', version: 1, exportedAt: seeded.addedAt, games: [seeded] }))
  let page = await start()
  const fixtureData = await application.evaluate(({ nativeImage }) => {
    const width = 600, height = 400, bitmap = Buffer.alloc(width * height * 4)
    const colors = [[0, 0, 255, 255], [0, 255, 0, 255], [255, 0, 0, 255], [0, 255, 255, 255]]
    for (let y = 0; y < height; y++) for (let x = 0; x < width; x++) {
      const color = colors[(y >= height / 2 ? 2 : 0) + (x >= width / 2 ? 1 : 0)], offset = (y * width + x) * 4
      for (let c = 0; c < 4; c++) bitmap[offset + c] = color[c]
    }
    const image = nativeImage.createFromBitmap(bitmap, { width, height, scaleFactor: 1 })
    const alpha = Buffer.alloc(400 * 200 * 4)
    for (let y = 0; y < 200; y++) for (let x = 200; x < 400; x++) { const offset = (y * 400 + x) * 4; alpha[offset + 2] = 128; alpha[offset + 3] = 128 }
    const alphaImage = nativeImage.createFromBitmap(alpha, { width: 400, height: 200, scaleFactor: 1 })
    const noise = Buffer.alloc(2200 * 1800 * 4)
    let random = 0x12345678
    for (let offset = 0; offset < noise.length; offset += 4) { for (let c = 0; c < 3; c++) { random ^= random << 13; random ^= random >>> 17; random ^= random << 5; noise[offset + c] = random & 255 }; noise[offset + 3] = 255 }
    return { jpeg: image.toJPEG(95).toString('base64'), png: image.toPNG().toString('base64'), alpha: alphaImage.toPNG().toString('base64'), noise: nativeImage.createFromBitmap(noise, { width: 2200, height: 1800, scaleFactor: 1 }).toPNG().toString('base64') }
  })
  const jpeg = Buffer.from(fixtureData.jpeg, 'base64')
  const exif = orientation => {
    const body = Buffer.from([69, 120, 105, 102, 0, 0, 73, 73, 42, 0, 8, 0, 0, 0, 1, 0, 18, 1, 3, 0, 1, 0, 0, 0, orientation, 0, 0, 0, 0, 0, 0, 0])
    const marker = Buffer.alloc(4); marker[0] = 255; marker[1] = 225; marker.writeUInt16BE(body.length + 2, 2)
    return Buffer.concat([jpeg.subarray(0, 2), marker, body, jpeg.subarray(2)])
  }
  const expected = [[0, 1, 2, 3], [1, 0, 3, 2], [3, 2, 1, 0], [2, 3, 0, 1], [0, 2, 1, 3], [2, 0, 3, 1], [3, 1, 2, 0], [1, 3, 0, 2]]
  const identity = rgba => rgba[0] > 180 ? 2 : rgba[1] > 180 && rgba[2] > 180 ? 3 : rgba[1] > 180 ? 1 : 0
  for (let orientation = 1; orientation <= 8; orientation++) {
    const filename = path.join(temporary, `orientation-${orientation}.jpg`); await fs.writeFile(filename, exif(orientation))
    const saved = await select(page, filename), image = await pixels(saved.localCover), encoded = Buffer.from(saved.localCover.dataUrl.split(',')[1], 'base64')
    assert.equal(image.width, orientation >= 5 ? 400 : 600); assert.equal(image.height, orientation >= 5 ? 600 : 400)
    assert.deepEqual(image.corners.map(identity), expected[orientation - 1]); assert.equal(encoded.includes(Buffer.from('Exif\0\0')), false)
    assert.equal(saved.notes, seeded.notes); assert.equal(saved.personalRating, 0)
    orientations.push({ orientation, width: image.width, height: image.height, corners: image.corners.map(identity), bytes: encoded.length })
  }
  checks.push('all eight EXIF orientations become correctly rotated/mirrored JPEG pixels with EXIF/GPS removed')
  const alphaPath = path.join(temporary, 'transparent.png'); await fs.writeFile(alphaPath, Buffer.from(fixtureData.alpha, 'base64'))
  let saved = await select(page, alphaPath, 'plain'), image = await pixels(saved.localCover)
  assert.deepEqual(image.corners[0], [255, 255, 255, 255])
  assert.equal(image.corners[1][2] >= 240, true); assert.equal(image.corners[1][0] >= 115 && image.corners[1][0] <= 140, true)
  checks.push('PNG transparency and half-transparent premultiplied pixels flatten to white rather than dark margins')
  const noisePath = path.join(temporary, 'large-photo.png'); await fs.writeFile(noisePath, Buffer.from(fixtureData.noise, 'base64'))
  saved = await select(page, noisePath)
  assert.equal(Math.max(saved.localCover.width, saved.localCover.height) <= 1600, true)
  assert.equal(Buffer.from(saved.localCover.dataUrl.split(',')[1], 'base64').length <= 128 * 1024, true)
  dimensions.push({ source: [2200, 1800], normalized: [saved.localCover.width, saved.localCover.height], bytes: Buffer.from(saved.localCover.dataUrl.split(',')[1], 'base64').length })
  checks.push('large noisy PNG is resized and compressed within both the 1600-edge and 128 KiB caps')
  const before = saved, badCases = [['active.png', Buffer.from('<svg onload="alert(1)"></svg>')], ['truncated.jpg', jpeg.subarray(0, 64)], ['header-only.jpg', Buffer.from([255,216,255,192,0,11,8,0,3,0,4,1,1,17,0,255,218,0,8,1,1,0,0,63,0,1,255,217])]]
  const huge = Buffer.from(fixtureData.png, 'base64'); huge.writeUInt32BE(10000, 16); huge.writeUInt32BE(10000, 20); badCases.push(['huge-pixels.png', huge])
  for (const [name, contents] of badCases) { const filename = path.join(temporary, name); await fs.writeFile(filename, contents); await assert.rejects(select(page, filename)); assert.deepEqual((await page.evaluate(() => window.kunManager.getState())).games[0], before) }
  const oversized = path.join(temporary, 'oversized.png'), file = await fs.open(oversized, 'w'); await file.truncate(20 * 1024 * 1024 + 1); await file.close()
  await assert.rejects(select(page, oversized), /20 MB/)
  assert.deepEqual((await page.evaluate(() => window.kunManager.getState())).games[0], before)
  checks.push('active SVG content, truncation, decodable-header-only junk, excessive dimensions and excessive bytes do not replace the prior photo or presentation')
  await fs.rename(noisePath, path.join(temporary, 'source-moved.png'))
  await application.close(); application = undefined; page = await start()
  saved = (await page.evaluate(() => window.kunManager.getState())).games[0]
  assert.deepEqual(saved, before); assert.equal((await pixels(saved.localCover)).width, before.localCover.width)
  checks.push('local cover survives removal of the original path and a native restart using the isolated profile')
  const exportedPath = path.join(temporary, 'synthetic-photo-backup.json')
  await application.evaluate(({ dialog }, filePath) => {
    dialog.showSaveDialog = async (_window, options) => {
      if (JSON.stringify(options.filters?.[0]?.extensions) !== JSON.stringify(['json'])) throw Error('Unexpected backup export filter')
      return { canceled: false, filePath }
    }
  }, exportedPath)
  assert.equal(await page.evaluate(() => window.kunManager.exportLibrary()), true)
  const exported = JSON.parse(await fs.readFile(exportedPath, 'utf8'))
  assert.equal(exported.version, 1); assert.deepEqual(exported.games, [saved])
  assert.deepEqual(exported.games[0].localCover, before.localCover)
  await page.evaluate(() => window.kunManager.removeGame('photo-codec-fixture'))
  assert.deepEqual((await page.evaluate(() => window.kunManager.getState())).games, [])
  await application.evaluate((_, selected) => {
    globalThis.__photoTest.selected = selected; globalThis.__photoTest.expectedFilters = ['json']
  }, exportedPath)
  const imported = await page.evaluate(() => window.kunManager.importLibrary())
  assert.equal(imported.added, 1); assert.equal(imported.skipped, 0)
  assert.deepEqual((await page.evaluate(() => window.kunManager.getState())).games, [saved])
  assert.equal((await pixels((await page.evaluate(() => window.kunManager.getState())).games[0].localCover)).width, before.localCover.width)
  const rendererImage = await page.evaluate(async cover => {
    const image = new Image()
    await new Promise((resolve, reject) => { image.onload = resolve; image.onerror = () => reject(Error('Imported local image did not decode in the renderer')); image.src = cover.dataUrl })
    return { width: image.naturalWidth, height: image.naturalHeight }
  }, saved.localCover)
  assert.deepEqual(rendererImage, { width: saved.localCover.width, height: saved.localCover.height })
  checks.push('real exportLibrary/importLibrary IPC preserves embedded JPEG, display preference and all personal fields after removal, with successful native and renderer decode')
  const previousDisk = await fs.readFile(path.join(profile, 'library.json'), 'utf8')
  const broken = structuredClone(exported)
  broken.games[0].localCover = { dataUrl: 'data:image/jpeg;base64,' + Buffer.from([255,216,255,192,0,11,8,0,3,0,4,1,1,17,0,255,218,0,8,1,1,0,0,63,0,1,255,217]).toString('base64'), width: 4, height: 3 }
  const brokenPath = path.join(temporary, 'broken-embedded-photo.json'), invalidPath = path.join(temporary, 'invalid-photo-format.json')
  await fs.writeFile(brokenPath, JSON.stringify(broken)); await fs.writeFile(invalidPath, JSON.stringify({ ...exported, format: 'unsupported-format' }))
  for (const [filename, expectedError] of [[brokenPath, /不是有效的 PNG\/JPG/], [invalidPath, /不支持的备份格式或版本/]]) {
    await application.evaluate((_, selected) => { globalThis.__photoTest.selected = selected }, filename)
    await assert.rejects(page.evaluate(() => window.kunManager.importLibrary()), expectedError)
    assert.deepEqual((await page.evaluate(() => window.kunManager.getState())).games, [saved])
    assert.equal(await fs.readFile(path.join(profile, 'library.json'), 'utf8'), previousDisk)
  }
  checks.push('malformed embedded JPEG and unsupported backup envelope fail through real import IPC without replacing the valid library or photo')
  assert.deepEqual(errors, [])
  await fs.mkdir(output, { recursive: true })
  const result = { mode: candidate ? 'candidate' : 'source', passed: true, checks, orientations, dimensions, rendererErrors: errors, syntheticOnly: true, formalProfileAccessed: false }
  await fs.writeFile(path.join(output, candidate ? 'candidate.json' : 'source.json'), JSON.stringify(result, null, 2)); console.log(JSON.stringify(result))
} finally {
  await application?.close()
  if (path.dirname(path.resolve(temporary)) !== tempRoot || !path.basename(temporary).startsWith('the-order-photo-codec-')) throw Error('Unexpected photo fixture directory')
  await fs.rm(temporary, { recursive: true, force: true })
}
