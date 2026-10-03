import assert from 'node:assert/strict'
import { promises as fs } from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { _electron as electron } from '../node_modules/playwright/index.mjs'

const project = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const temporaryRoot = path.resolve(os.tmpdir())
const temporary = await fs.mkdtemp(path.join(temporaryRoot, 'the-order-cover-selection-'))
const profile = path.join(temporary, 'profile')
const candidate = process.argv.includes('--candidate')
const candidateRoot = path.join(project, '.artifacts', 'iteration-02', 'candidate', 'win-unpacked')
const executablePath = path.join(candidate ? candidateRoot : path.join(project, 'node_modules', 'electron', 'dist'), candidate ? 'The Order.exe' : 'electron.exe')
const output = path.join(project, '.artifacts', 'iteration-02')
const log = path.join(output, 'desktop-cover-selection.log')
const errors = [], checks = [], requests = []
let application

const image = (id, name, width, height, vote_count, sexual = 'safe') => ({
  id, url: `https://synthetic.invalid/${name}.webp`, width, height, vote_count, sexual
})
const landscape = image('81001', 'landscape-nine', 900, 600, 9)
const portrait = image('81002', 'portrait-two', 500, 700, 2)
const secondPortrait = image('81003', 'portrait-one', 500, 700, 1)
const square = image('82002', 'square-seven', 500, 500, 7)
const safeAdultFallback = image('83001', 'safe-landscape-twenty', 900, 600, 20)
const adultPortrait = image('83002', 'adult-portrait-two', 500, 700, 2, 'explicit')
const electedKnown = image('84001', 'known-slot-nine', 500, 700, 9)
const alternateKnown = image('84002', 'known-slot-other-two', 500, 700, 2)
const rawWork = (id, cover, covers) => ({
  object: 'work', id, display_name: `Synthetic cover work ${id}`, cover, covers,
  refs: [], titles: [], tags: [], characters: [], ratings: [], companies: [], intros: []
})
const works = [
  rawWork('901001', landscape, [landscape, portrait, secondPortrait,
    image('81004', 'unmeasured-fifty', null, null, 50),
    { ...image('81005', 'unsafe-hundred', 500, 700, 100), url: 'http://synthetic.invalid/unsafe.webp' }]),
  rawWork('901002', image('82001', 'other-landscape-three', 900, 600, 3), [
    image('82001', 'other-landscape-three', 900, 600, 3), square, image('82003', 'other-unknown-four', null, null, 4)]),
  rawWork('901003', adultPortrait, [adultPortrait, safeAdultFallback]),
  rawWork('901004', electedKnown, [{ ...electedKnown, width: null, height: null }, alternateKnown])
]
const env = { ...process.env }
for (const key of Object.keys(env)) if (key.startsWith('KGM_')) delete env[key]
delete env.ELECTRON_RUN_AS_NODE
delete env.NODE_OPTIONS
env.KGM_DATA_DIR = profile
const launchOptions = { executablePath, args: candidate ? [] : [project], env, timeout: 30000 }
const personal = game => {
  const { metadata, ...fields } = game
  return fields
}
const record = async result => {
  await fs.mkdir(output, { recursive: true })
  const text = JSON.stringify({ at: new Date().toISOString(), mode: candidate ? 'candidate' : 'source', ...result })
  await fs.appendFile(log, text + '\n')
  console.log(text)
}

async function start() {
  application = await electron.launch(launchOptions)
  await application.evaluate(({ session }, input) => {
    session.defaultSession.webRequest.onBeforeRequest({ urls: ['http://*/*', 'https://*/*'] }, (_, callback) => callback({ cancel: true }))
    globalThis.__coverSelectionFixture = { works: input.works, requests: [], unexpected: [] }
    globalThis.fetch = async (value, init) => {
      const url = new URL(String(value)), state = globalThis.__coverSelectionFixture
      const work = state.works.find(item => url.pathname === `/v2/catalog/works/${item.id}`)
      if (url.origin !== 'https://api.nextmoe.dev' || !work || init?.method && init.method !== 'GET') {
        state.unexpected.push('Unexpected transport destination or method')
        throw Error('Unexpected synthetic request')
      }
      if (!url.searchParams.get('include')?.split(',').includes('covers')) throw Error('Missing cover detail include')
      if (new Headers(init?.headers).get('Authorization') !== 'Bearer synthetic-cover-selection-key') throw Error('Wrong synthetic credential')
      state.requests.push({ id: work.id, nsfw: url.searchParams.get('nsfw') })
      return new Response(JSON.stringify(work), { headers: { 'content-type': 'application/json' } })
    }
  }, { works })
  const page = await application.firstWindow()
  page.on('pageerror', error => errors.push(error.message))
  await page.waitForFunction(() => window.kunManager !== undefined)
  const state = await page.evaluate(() => window.kunManager.getState())
  assert.equal(path.resolve(state.dataPath), path.resolve(profile), 'Every IPC must use the isolated profile')
  assert.equal(state.activeSession, null, 'Synthetic installations must never launch')
  assert.match(page.url(), /^kun-manager:\/\/app\/index.html/)
  const runtime = await application.evaluate(({ app }) => ({ packaged: app.isPackaged, appPath: app.getAppPath() }))
  assert.equal(runtime.packaged, candidate)
  assert.equal(path.resolve(runtime.appPath), candidate ? path.join(candidateRoot, 'resources', 'app.asar') : project)
  return page
}

try {
  await fs.mkdir(profile, { recursive: true })
  const folder = path.join(temporary, 'Synthetic installation')
  await fs.mkdir(folder)
  const inertExecutable = path.join(folder, 'Fixture.exe')
  await fs.writeFile(inertExecutable, 'Inert synthetic fixture; never execute.')
  const seeded = {
    id: 'cover-local-fixture', name: 'Personal synthetic title', nameIsCustom: true, metadata: null,
    installations: [{ id: 'cover-installation-fixture', folderPath: folder, executablePath: inertExecutable, arguments: ['--synthetic-only'] }],
    preferredInstallationId: 'cover-installation-fixture', status: 'paused', personalRating: 0, favorite: true,
    categories: ['Synthetic category'], customTags: ['Synthetic tag'], notes: 'Personal synthetic note',
    totalPlaySeconds: 60, playCount: 1,
    sessions: [{ id: 'cover-session-fixture', startedAt: '2026-10-03T00:00:00.000Z', endedAt: '2026-10-03T00:01:00.000Z', seconds: 60 }],
    addedAt: '2026-10-03T00:00:00.000Z', lastPlayedAt: '2026-10-03T00:01:00.000Z'
  }
  await fs.writeFile(path.join(profile, 'library.json'), JSON.stringify({ format: 'kun-gal-manager', version: 1, exportedAt: seeded.addedAt, games: [seeded] }))
  let page = await start()
  assert.deepEqual((await page.evaluate(() => window.kunManager.getState())).games, [seeded])
  await page.evaluate(async () => {
    const api = window.kunManager
    await api.saveSettings({ ...(await api.getState()).settings, apiKey: 'synthetic-cover-selection-key', includeNsfw: false, readExecutableMetadata: false })
  })
  const details = await page.evaluate(async () => Promise.all(['901001', '901002', '901003', '901004'].map(id => window.kunManager.getWork(id))))
  assert.deepEqual(details.map(work => work.coverUrl), [portrait.url, square.url, safeAdultFallback.url, electedKnown.url])
  checks.push('real getWork IPC: highest-voted usable portrait, no-portrait fallback, adult filter, same-URL dimension reconciliation')
  const wishlist = await page.evaluate(() => window.kunManager.addVirtualGame('901002'))
  assert.equal(wishlist.metadata.coverUrl, square.url)
  assert.deepEqual(wishlist.installations, [])
  checks.push('addVirtualGame persists the detail-selected no-portrait fallback')
  let linked = await page.evaluate(() => window.kunManager.linkGame('cover-local-fixture', '901001'))
  assert.equal(linked.metadata.coverUrl, portrait.url)
  assert.deepEqual(personal(linked), personal(seeded))
  checks.push('linkGame preserves name, zero rating, notes, categories, tags, status, favourite, installation and saved sessions')
  await application.evaluate(() => {
    globalThis.__coverSelectionFixture.works.find(work => work.id === '901001').covers.find(cover => cover.id === '81003').vote_count = 5
  })
  linked = await page.evaluate(() => window.kunManager.refreshGame('cover-local-fixture'))
  assert.equal(linked.metadata.coverUrl, secondPortrait.url)
  assert.deepEqual(personal(linked), personal(seeded))
  checks.push('refreshGame replaces a previous cover from fresh portrait votes while preserving every personal field')
  linked = await page.evaluate(() => window.kunManager.linkGame('cover-local-fixture', '901004'))
  assert.equal(linked.metadata.coverUrl, electedKnown.url)
  assert.deepEqual(personal(linked), personal(seeded))
  checks.push('manual relinking uses the reconciled portrait vote and preserves personal data')
  await page.evaluate(async () => {
    const api = window.kunManager
    await api.saveSettings({ ...(await api.getState()).settings, includeNsfw: true })
  })
  assert.equal((await page.evaluate(() => window.kunManager.getWork('901003'))).coverUrl, adultPortrait.url)
  await page.evaluate(async () => {
    const api = window.kunManager
    await api.saveSettings({ ...(await api.getState()).settings, includeNsfw: false })
  })
  assert.equal((await page.evaluate(() => window.kunManager.getWork('901003'))).coverUrl, safeAdultFallback.url)
  checks.push('adult-image opt-in is reversible through normal settings IPC without changing saved books')
  const expected = await page.evaluate(() => window.kunManager.getState())
  const transport = await application.evaluate(() => globalThis.__coverSelectionFixture)
  assert.deepEqual(transport.unexpected, [])
  requests.push(...transport.requests)
  assert.equal(requests.length, 10, 'Cover selection adds no extra catalogue or image-probe requests')
  const persisted = JSON.parse(await fs.readFile(path.join(profile, 'library.json'), 'utf8'))
  assert.deepEqual(persisted.games, expected.games)
  await application.close(); application = undefined
  page = await start()
  const restarted = await page.evaluate(() => window.kunManager.getState())
  assert.deepEqual(restarted.games, expected.games)
  assert.equal(restarted.settings.includeNsfw, false)
  assert.equal(restarted.settings.hasApiKey, true)
  assert.deepEqual((await application.evaluate(() => globalThis.__coverSelectionFixture.requests)), [])
  checks.push('library JSON and a clean native restart retain selected covers and all personal data without automatic refresh')
  assert.deepEqual(errors, [])
  await record({ status: 'passed', checks, requestCount: requests.length, syntheticOnly: true, formalProfileAccessed: false, rendererErrors: errors })
} catch (error) {
  await record({ status: 'failed', message: error.message, checks, syntheticOnly: true, formalProfileAccessed: false })
  throw error
} finally {
  if (application) await application.close()
  if (path.dirname(path.resolve(temporary)) !== temporaryRoot || !path.basename(temporary).startsWith('the-order-cover-selection-')) throw Error('Unexpected cover fixture directory')
  await fs.rm(temporary, { recursive: true, force: true })
}
