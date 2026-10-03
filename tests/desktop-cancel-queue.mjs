import assert from 'node:assert/strict'
import { promises as fs } from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { _electron as electron } from '../node_modules/playwright/index.mjs'

const project = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const temporaryRoot = path.resolve(os.tmpdir())
const temporary = await fs.mkdtemp(path.join(temporaryRoot, 'the-order-cancel-queue-'))
const profile = path.join(temporary, 'profile')
await fs.mkdir(profile)
const date = '2026-10-03T00:00:00.000Z'
const metadata = (id, title) => ({ id, title, originalTitle: title, aliases: [], coverUrl: null, bannerUrl: null, description: 'Original synthetic metadata', releaseDate: null, releaseDatePrecision: null, developers: [], tags: [], ratings: [], characters: [], references: [], webUrl: null, contentRating: null, fetchedAt: date })
const games = ['target', 'blocker', 'positive-blocker'].map((id, i) => ({ id, name: `Queue fixture ${id}`, nameIsCustom: true, metadata: metadata(String(101 + i), `Queue work ${i}`), installations: id === 'target' ? [{ id: 'installation', folderPath: path.join(temporary, 'inert'), executablePath: null, arguments: [] }] : [], preferredInstallationId: id === 'target' ? 'installation' : null, status: 'planned', personalRating: 9, favorite: true, categories: ['Fixture'], customTags: ['Queue'], notes: 'Preserved synthetic notes', totalPlaySeconds: 0, playCount: 0, sessions: [], addedAt: date, lastPlayedAt: null }))
await fs.writeFile(path.join(profile, 'library.json'), JSON.stringify({ format: 'kun-gal-manager', version: 1, exportedAt: date, games }))
const fromAsar = process.argv.includes('--asar')
const errors = []
let application
try {
  application = await electron.launch({ executablePath: path.join(project, 'node_modules/electron/dist/electron.exe'), args: [path.join(project, 'tests/desktop-cancel-queue-bootstrap.cjs')], env: { ...process.env, KGM_DATA_DIR: profile, KGM_CANCEL_ASAR: fromAsar ? '1' : '0', KGM_DEV_URL: '' }, timeout: 30000 })
  await application.evaluate(({ session }) => {
    session.defaultSession.webRequest.onBeforeRequest({ urls: ['http://*/*', 'https://*/*'] }, (_, callback) => callback({ cancel: true }))
    globalThis.fetch = async value => {
      const url = new URL(String(value))
      if (url.origin !== 'https://api.nextmoe.dev' || !url.pathname.endsWith('/catalog/works/101')) throw new Error('Unexpected synthetic request')
      const control = globalThis.__cancelQueue
      control.requests++
      const raw = { object: 'work', id: '101', display_name: 'Queue refreshed work', images: { cover: { url: 'https://fixture.invalid/queue.png' } }, intros: [{ lang: 'zh-Hans', value: 'Updated synthetic metadata' }], refs: [], titles: [], tags: [], characters: [], ratings: [] }
      const bytes = new TextEncoder().encode(JSON.stringify(raw))
      let sent = false
      return new Response(new ReadableStream({ pull(controller) {
        if (!sent) { sent = true; controller.enqueue(bytes) }
        else { control.responseConsumed = true; controller.close() }
      } }), { headers: { 'content-type': 'application/json' } })
    }
  })
  const page = await application.firstWindow()
  page.on('pageerror', error => errors.push(error.message))
  await page.waitForFunction(() => window.kunManager !== undefined)
  await page.evaluate(async () => { const settings = (await window.kunManager.getState()).settings; await window.kunManager.saveSettings({ ...settings, apiKey: 'synthetic-cancel-queue-only', readExecutableMetadata: false }) })
  const poll = async (read, predicate, label) => { for (let i = 0; i < 200; i++) { const value = await read(); if (predicate(value)) return value; await new Promise(resolve => setTimeout(resolve, 20)) } throw new Error('Timed out: ' + label) }
  const snapshot = () => page.evaluate(() => window.kunManager.getState())
  const target = state => state.games.find(game => game.id === 'target')
  async function queueBehindRemoval(blocker) {
    await application.evaluate(() => { Object.assign(globalThis.__cancelQueue, { holdNext: true, responseConsumed: false }) })
    await page.evaluate(id => { globalThis.__blockedRemoval = window.kunManager.removeGame(id) }, blocker)
    await poll(() => application.evaluate(() => globalThis.__cancelQueue.held), Boolean, 'real identity queue blocker')
    await page.evaluate(() => window.kunManager.startMetadataMatching(['target']))
    await poll(() => application.evaluate(() => globalThis.__cancelQueue.responseConsumed), Boolean, 'synthetic response fully consumed')
    // Drain the main-process promise continuations after the response body closes;
    // the production apply callback is now waiting behind removeGame.
    await application.evaluate(async () => { for (let i = 0; i < 3; i++) await new Promise(resolve => setImmediate(resolve)) })
    const state = await snapshot()
    assert.equal(state.matching.status, 'running')
    assert.equal(state.matching.processed, 0)
    assert.equal(target(state).metadata.description, 'Original synthetic metadata')
  }
  async function releaseRemoval() {
    await application.evaluate(() => globalThis.__cancelQueue.release())
    await page.evaluate(() => globalThis.__blockedRemoval)
    await application.evaluate(async () => { for (let i = 0; i < 3; i++) await new Promise(resolve => setImmediate(resolve)) })
  }
  await queueBehindRemoval('blocker')
  const cancelled = await page.evaluate(() => window.kunManager.cancelMetadataMatching())
  assert.equal(cancelled.status, 'cancelled')
  await releaseRemoval()
  const afterCancel = await snapshot()
  assert.equal(target(afterCancel).metadata.description, 'Original synthetic metadata')
  assert.equal(afterCancel.matching.matched, 0)
  assert.equal(afterCancel.matching.processed, 0)
  assert.equal(afterCancel.matching.status, 'cancelled')
  assert.equal(afterCancel.games.some(game => game.id === 'blocker'), false)
  const cancelledLibrary = JSON.parse(await fs.readFile(path.join(profile, 'library.json'), 'utf8'))
  assert.equal(target(cancelledLibrary).metadata.description, 'Original synthetic metadata')
  assert.equal(target(cancelledLibrary).notes, 'Preserved synthetic notes')
  assert.equal(target(cancelledLibrary).personalRating, 9)
  assert.equal(cancelledLibrary.games.some(game => game.id === 'blocker'), false)

  // Positive control: an uncancelled operation queued in the same place commits.
  await queueBehindRemoval('positive-blocker')
  await releaseRemoval()
  const complete = await poll(snapshot, state => state.matching.status === 'completed', 'uncancelled queued commit')
  assert.equal(target(complete).metadata.description, 'Updated synthetic metadata')
  assert.equal(complete.matching.matched, 1)
  assert.equal(complete.matching.processed, 1)
  assert.equal(target(complete).notes, 'Preserved synthetic notes')
  assert.equal(target(complete).personalRating, 9)
  const persisted = JSON.parse(await fs.readFile(path.join(profile, 'library.json'), 'utf8'))
  assert.equal(target(persisted).metadata.description, 'Updated synthetic metadata')
  assert.deepEqual(errors, [])
  console.log(JSON.stringify({ realElectronIpc: true, realIdentityQueue: true, realLibraryStore: true, fromAsar, cancelledQueuedCommitBlocked: true, cancelledCountsStable: true, uncancelledQueuedCommitSaved: true, rendererErrors: errors }))
} finally {
  if (application) await application.close()
  if (path.dirname(path.resolve(temporary)) !== temporaryRoot || !path.basename(temporary).startsWith('the-order-cancel-queue-')) throw new Error('Unexpected temporary directory')
  await fs.rm(temporary, { recursive: true, force: true })
}
