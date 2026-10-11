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
const output = path.join(project, '.artifacts', 'iteration-02', 'cover-preferences')
const log = path.join(output, 'backend-desktop-cover-selection.log')
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
  const { metadata, coverChoice, nonPortraitCoverDisplay, ...fields } = game
  return fields
}
const record = async result => {
  await fs.mkdir(output, { recursive: true })
  const text = JSON.stringify({ at: new Date().toISOString(), mode: candidate ? 'candidate' : 'source', contract: 'upstream-default-and-personal-choice', ...result })
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
      const serialized = JSON.stringify(work)
      if (state.holdNextId === work.id) {
        delete state.holdNextId; state.held = true
        await new Promise(resolve => { state.release = resolve })
      }
      return new Response(serialized, { headers: { 'content-type': 'application/json' } })
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
  assert.deepEqual(details.map(work => work.coverUrl), [landscape.url, works[1].cover.url, safeAdultFallback.url, electedKnown.url])
  assert.deepEqual(details[0].covers.map(cover => cover.id), ['81001', '81002', '81003', '81004'])
  assert.deepEqual(details[2].covers.map(cover => cover.id), ['83001'])
  checks.push('real getWork IPC retains upstream default regardless of shape or votes and filters unsafe/adult choice rows')
  const wishlist = await page.evaluate(() => window.kunManager.addVirtualGame('901002'))
  assert.equal(wishlist.metadata.coverUrl, works[1].cover.url)
  assert.deepEqual(wishlist.installations, [])
  checks.push('addVirtualGame persists the upstream default')
  let linked = await page.evaluate(() => window.kunManager.linkGame('cover-local-fixture', '901001'))
  assert.equal(linked.metadata.coverUrl, landscape.url)
  assert.deepEqual(personal(linked), personal(seeded))
  checks.push('linkGame preserves name, zero rating, notes, categories, tags, status, favourite, installation and saved sessions')
  linked = await page.evaluate(() => window.kunManager.setGameCover('cover-local-fixture', '901001', '81002', 'plain'))
  assert.equal(linked.coverChoice.cover.url, portrait.url)
  assert.equal(linked.nonPortraitCoverDisplay, 'plain')
  assert.equal(linked.metadata.coverUrl, landscape.url, 'Personal artwork must not overwrite the upstream default')
  await application.evaluate(() => {
    const work = globalThis.__coverSelectionFixture.works.find(work => work.id === '901001')
    work.cover = work.covers.find(cover => cover.id === '81003')
    work.covers = work.covers.filter(cover => cover.id !== '81002')
  })
  linked = await page.evaluate(() => window.kunManager.refreshGame('cover-local-fixture'))
  assert.equal(linked.metadata.coverUrl, secondPortrait.url)
  assert.equal(linked.coverChoice.cover.url, portrait.url)
  assert.equal(linked.metadata.covers.some(cover => cover.id === '81002'), false)
  assert.deepEqual(personal(linked), personal(seeded))
  linked = await page.evaluate(() => window.kunManager.setGameCover('cover-local-fixture', '901001', undefined, 'crop'))
  assert.equal(linked.coverChoice.cover.url, portrait.url)
  assert.equal(linked.nonPortraitCoverDisplay, 'crop')
  checks.push('choice/display save together and same-work refresh retains saved artwork even when absent from the new pool')
  await page.evaluate(async () => {
    const api = window.kunManager
    await api.saveSettings({ ...(await api.getState()).settings, clearApiKey: true })
  })
  const offlineCalls = await application.evaluate(() => globalThis.__coverSelectionFixture.requests.length)
  linked = await page.evaluate(() => window.kunManager.setGameCover('cover-local-fixture', '901001', null, 'plain'))
  assert.equal(linked.coverChoice, undefined)
  assert.equal(linked.metadata.coverUrl, secondPortrait.url)
  linked = await page.evaluate(() => window.kunManager.setGameCover('cover-local-fixture', '901001', undefined, 'blur'))
  assert.equal(linked.nonPortraitCoverDisplay, 'blur')
  assert.equal(await application.evaluate(() => globalThis.__coverSelectionFixture.requests.length), offlineCalls)
  checks.push('restore default and display-only changes work without credentials or network requests')
  await page.evaluate(async () => {
    const api = window.kunManager
    await api.saveSettings({ ...(await api.getState()).settings, apiKey: 'synthetic-cover-selection-key' })
  })
  await application.evaluate((_electron, cover) => {
    const state = globalThis.__coverSelectionFixture
    state.works.find(work => work.id === '901001').covers.push(cover)
    state.holdNextId = '901001'
  }, portrait)
  await page.evaluate(() => {
    window.__staleCoverResult = window.kunManager.setGameCover('cover-local-fixture', '901001', '81002', 'crop').then(() => ({ accepted: true }), error => ({ accepted: false, message: error.message }))
  })
  assert.equal(await application.evaluate(async () => {
    for (let attempt = 0; attempt < 100; attempt++) {
      if (globalThis.__coverSelectionFixture.held) return true
      await new Promise(resolve => setTimeout(resolve, 20))
    }
    return false
  }), true, 'Synthetic cover request must be held before the later reset')
  await page.evaluate(() => window.kunManager.setGameCover('cover-local-fixture', '901001', null, 'plain'))
  await application.evaluate(() => { globalThis.__coverSelectionFixture.release(); delete globalThis.__coverSelectionFixture.release })
  const stale = await page.evaluate(() => window.__staleCoverResult)
  assert.equal(stale.accepted, false)
  assert.match(stale.message, /条目或封面选择已变化/)
  assert.equal((await page.evaluate(() => window.kunManager.getState())).games.find(game => game.id === 'cover-local-fixture').coverChoice, undefined)
  checks.push('a delayed selection cannot overwrite a newer reset through the real preload/IPC entry')
  await page.evaluate(() => window.kunManager.setGameCover('cover-local-fixture', '901001', '81003', 'plain'))
  linked = await page.evaluate(() => window.kunManager.linkGame('cover-local-fixture', '901004'))
  assert.equal(linked.metadata.coverUrl, electedKnown.url)
  assert.equal(linked.coverChoice, undefined)
  assert.equal(linked.nonPortraitCoverDisplay, 'plain')
  assert.deepEqual(personal(linked), personal(seeded))
  checks.push('manual relinking clears artwork for the previous work while preserving display and personal data')
  for (const [workId, coverId, display] of [['901004', '81002', 'crop'], ['901004', 'https://synthetic.invalid/injected.webp', 'crop'], ['901001', '81002', 'crop'], ['901004', null, 'stretch']]) {
    const rejected = await page.evaluate(async args => {
      try { await window.kunManager.setGameCover('cover-local-fixture', ...args); return false } catch { return true }
    }, [workId, coverId, display])
    assert.equal(rejected, true)
  }
  checks.push('foreign artwork IDs, supplied URLs, changed work IDs and unsupported displays are rejected by IPC')
  assert.equal(await page.evaluate(async () => {
    try { await window.kunManager.updateGame('cover-local-fixture', { nonPortraitCoverDisplay: 'crop' }); return false } catch { return true }
  }), true)
  assert.equal((await page.evaluate(() => window.kunManager.getState())).games.find(game => game.id === 'cover-local-fixture').nonPortraitCoverDisplay, 'plain')
  checks.push('ordinary updateGame cannot bypass the single atomic cover/display interface')
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
  const adultBook = await page.evaluate(() => window.kunManager.addVirtualGame('901003'))
  assert.equal(await page.evaluate(async id => {
    try { await window.kunManager.setGameCover(id, '901003', '83002', 'plain'); return false } catch { return true }
  }, adultBook.id), true)
  const savedWishlist = await page.evaluate(id => window.kunManager.setGameCover(id, '901002', '82002', 'crop'), wishlist.id)
  assert.equal(savedWishlist.coverChoice.cover.url, square.url)
  assert.equal(savedWishlist.nonPortraitCoverDisplay, 'crop')
  checks.push('adult-image opt-in is reversible and explicit personal artwork cannot be selected when disabled')
  const expected = await page.evaluate(() => window.kunManager.getState())
  const transport = await application.evaluate(() => ({ requests: globalThis.__coverSelectionFixture.requests, unexpected: globalThis.__coverSelectionFixture.unexpected }))
  assert.deepEqual(transport.unexpected, [])
  requests.push(...transport.requests)
  assert.equal(requests.length, 17, 'Requests are only normal detail reads, refreshes and one detail verification per new personal selection')
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
