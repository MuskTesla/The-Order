import assert from 'node:assert/strict'
import { test } from 'node:test'
import { promises as fs } from 'node:fs'
import vm from 'node:vm'
import { transform } from 'esbuild'
import * as vue from 'vue'
import * as libraryWorkspace from '../src/library-workspace'
import * as importWorkspace from '../src/import-workspace'
import * as libraryInsights from '../src/library-insights'
import * as savedLibraryViews from '../src/saved-library-views'

const deferred = () => {
  let resolve!: (value?: any) => void, reject!: (error: Error) => void
  const promise = new Promise<any>((yes, no) => { resolve = yes; reject = no })
  return { promise, resolve, reject }
}
// Every case exercises the same production setup. Compile it once instead of
// issuing another service transform for every isolated view lifecycle.
const compiledHarness = (async () => {
  const source = await fs.readFile(new URL('../src/App.vue', import.meta.url), 'utf8')
  const script = source.split('<script setup lang="ts">')[1]!.split('</script>')[0]!
  const expose = '\nglobalThis.audit = { snapshot, editor, busy, selectedGameId, selectedWork, selectedGame, detailOpen, showGame, showWork, saveGame, savePersonalRecord, personalRecordError, saveArguments, addToLibrary, removeGame };'
  const compiled = await transform(script.replace(/^import .*$/gm, '') + expose, { loader: 'ts', format: 'cjs', target: 'es2022' })
  return { script, compiled }
})()
async function harness(t: { after(callback: () => void): void }) {
  const { script, compiled } = await compiledHarness
  const messages: any[] = []
  const sandbox: any = { Error, ...vue, ...libraryWorkspace, ...importWorkspace, ...libraryInsights, ...savedLibraryViews, onMounted: () => {}, onUnmounted: () => {}, isPreview: false, useKunMessage: (...args: any[]) => messages.push(args), useKunAlert: async () => true, api: {}, document: { documentElement: { dataset: {}, classList: { toggle() {} } } }, matchMedia: () => ({ matches: false }) }
  for (const line of script.split('\n').filter(line => /^import \{/.test(line))) for (const name of line.match(/^import \{([^}]+)\}/)![1]!.split(',').map(name => name.trim())) if (!(name in sandbox)) sandbox[name] = name
  const scope = vue.effectScope(); t.after(() => scope.stop())
  vm.createContext(sandbox); scope.run(() => vm.runInContext(compiled.code, sandbox))
  const a = sandbox.audit
  const games = ['A', 'B'].map(id => ({ id, name: `Game ${id}`, metadata: { id: `work-${id}`, title: `Work ${id}` }, status: 'planned', personalRating: null, categories: [id], customTags: [id], notes: `Saved ${id}`, installations: [{ id: 'install', arguments: [`--${id}`] }], preferredInstallationId: 'install' }))
  const state = (items = games) => ({ games: items, settings: { baseUrl: '', includeNsfw: false, hasApiKey: false } })
  a.snapshot.value = state(); sandbox.api.getState = async () => state()
  return { a, api: sandbox.api, sandbox, games, state, messages }
}

test('discovery initializes the correct library editor before detail returns and preserves later edits', async t => {
  const { a, api, games } = await harness(t), detail = deferred(), saved: any[] = []
  await a.showGame(games[0]); a.editor.notes = 'Unsaved A'
  api.getWork = () => detail.promise
  api.updateGame = async (...args: any[]) => saved.push(args)
  const pending = a.showWork(games[1].metadata)
  assert.equal(a.selectedGame.value.id, 'B'); assert.equal(a.editor.name, 'Game B'); assert.equal(a.editor.notes, 'Saved B')
  await a.saveGame()
  assert.equal(saved[0][0], 'B'); assert.equal(saved[0][1].notes, 'Saved B'); assert.deepEqual(Array.from(saved[0][1].categories), ['B'])
  a.editor.notes = 'Fresh B draft'; a.editor.categories = 'Fresh category'; a.editor.launchArgs = '["--B-DRAFT"]'
  detail.resolve({ ...games[1].metadata, description: 'Full details' }); await pending
  assert.equal(a.editor.notes, 'Fresh B draft'); assert.equal(a.editor.categories, 'Fresh category'); assert.equal(a.editor.launchArgs, '["--B-DRAFT"]')
})

test('a merged work revealing an existing game initializes that editor once', async t => {
  const { a, api, games } = await harness(t), detail = deferred()
  await a.showGame(games[0]); a.editor.notes = 'Unsaved A'
  api.getWork = () => detail.promise
  const pending = a.showWork({ id: 'old-work-B', title: 'Old B reference' })
  assert.equal(a.editor.notes, ''); assert.equal(a.selectedGame.value, null)
  detail.resolve(games[1].metadata); await pending
  assert.equal(a.selectedGameId.value, 'B'); assert.equal(a.editor.notes, 'Saved B')
})

test('stale detail failure stays silent after close and reopening another request', async t => {
  const { a, api, games, messages } = await harness(t), first = deferred(), second = deferred()
  let requests = 0; api.getWork = () => ++requests === 1 ? first.promise : second.promise
  const old = a.showWork(games[0].metadata)
  a.detailOpen.value = false
  const fresh = a.showWork(games[1].metadata)
  a.editor.notes = 'B draft'
  first.reject(new Error('Old detail failure')); await old
  assert.equal(a.busy.has('detail'), true); assert.equal(messages.length, 0); assert.equal(a.editor.notes, 'B draft')
  second.resolve(games[1].metadata); await fresh
  assert.equal(a.busy.has('detail'), false); assert.equal(a.editor.notes, 'B draft')
})

test('library metadata notifications preserve drafts and supersede a previous work detail', async t => {
  const { a, api, games, state } = await harness(t), detail = deferred()
  api.getWork = () => detail.promise
  const pending = a.showWork(games[0].metadata)
  a.editor.notes = 'A draft'; a.editor.name = 'A draft name'
  const associated = { ...games[0], metadata: { id: 'new-work', title: 'New association' } }
  a.snapshot.value = state([associated, games[1]]); await vue.nextTick()
  assert.equal(a.selectedWork.value.id, 'new-work')
  detail.resolve(games[0].metadata); await pending
  assert.equal(a.selectedGame.value.id, 'A'); assert.equal(a.selectedWork.value.id, 'new-work')
  assert.equal(a.editor.notes, 'A draft'); assert.equal(a.editor.name, 'A draft name')
})

test('saving rejects editor data belonging to another game even when installation IDs coincide', async t => {
  const { a, api, games } = await harness(t)
  await a.showGame(games[0])
  a.selectedGameId.value = 'B'; a.selectedWork.value = games[1].metadata
  let saves = 0; api.updateGame = api.setLaunchArguments = async () => { saves++ }
  await a.saveGame(); await a.saveArguments()
  assert.equal(saves, 0)
})

test('late wishlist completion persists its game without changing a newer detail or draft', async t => {
  const { a, api, games, state } = await harness(t), addition = deferred()
  api.getWork = async (id: string) => ({ id, title: 'Work C' })
  await a.showWork({ id: 'work-C', title: 'Work C' })
  api.addVirtualGame = () => addition.promise
  const pending = a.addToLibrary()
  a.detailOpen.value = false; await a.showGame(games[1]); a.editor.notes = 'New B draft'
  const added = { ...games[0], id: 'C', name: 'Game C', metadata: { id: 'work-C', title: 'Work C' } }
  api.getState = async () => state([...games, added]); addition.resolve(added); await pending
  assert.equal(a.selectedGame.value.id, 'B'); assert.equal(a.editor.notes, 'New B draft'); assert.equal(a.detailOpen.value, true)
  assert.equal(a.snapshot.value.games.length, 3)
})

test('wishlist completion in the original view enables correctly initialized management', async t => {
  const { a, api, games, state } = await harness(t)
  const added = { ...games[0], id: 'C', name: 'Game C', notes: '', metadata: { id: 'work-C', title: 'Work C' } }
  api.getWork = async () => added.metadata; api.addVirtualGame = async () => added
  await a.showWork(added.metadata)
  api.getState = async () => state([...games, added]); await a.addToLibrary()
  assert.equal(a.selectedGame.value.id, 'C'); assert.equal(a.editor.name, 'Game C'); assert.equal(a.editor.notes, '')
})

test('removal confirmation becomes stale after closing and reopening even the same game', async t => {
  const { a, api, games, sandbox } = await harness(t), confirmation = deferred()
  sandbox.useKunAlert = () => confirmation.promise
  await a.showGame(games[0]); let removals = 0; api.removeGame = async () => { removals++ }
  const pending = a.removeGame()
  a.detailOpen.value = false; await a.showGame(games[0]); a.editor.notes = 'Reopened A draft'
  confirmation.resolve(true); await pending
  assert.equal(removals, 0); assert.equal(a.detailOpen.value, true); assert.equal(a.editor.notes, 'Reopened A draft')
})

test('late removal reply preserves another detail but normal removal closes its own vanished entry', async t => {
  const { a, api, games, state } = await harness(t), removal = deferred()
  await a.showGame(games[0]); api.removeGame = () => removal.promise
  const pending = a.removeGame(); await Promise.resolve(); await Promise.resolve()
  a.detailOpen.value = false; await a.showGame(games[1]); a.editor.notes = 'New B draft'
  api.getState = async () => state([games[1]]); removal.resolve(); await pending
  assert.equal(a.detailOpen.value, true); assert.equal(a.selectedGame.value.id, 'B'); assert.equal(a.editor.notes, 'New B draft')
  api.removeGame = async () => { a.snapshot.value = state([]) }; api.getState = async () => state([])
  await a.removeGame()
  assert.equal(a.detailOpen.value, false)
})

test('personal record saves only its three fields and keeps unrelated management drafts', async t => {
  const { a, api, games, messages } = await harness(t), calls: any[] = []
  await a.showGame(games[0]); a.editor.name = 'Unsaved name'; a.editor.categories = 'Unsaved category'; a.editor.tags = 'Unsaved tag'; a.editor.launchArgs = '["--UNSAVED"]'
  a.editor.status = 'playing'; a.editor.personalRating = '0'; a.editor.notes = 'New personal note'
  api.updateGame = async (id: string, patch: any) => calls.push([id, structuredClone(patch)])
  await a.savePersonalRecord()
  assert.deepEqual(JSON.parse(JSON.stringify(calls)), [['A', { status: 'playing', personalRating: 0, notes: 'New personal note' }]])
  assert.equal(a.editor.name, 'Unsaved name'); assert.equal(a.editor.categories, 'Unsaved category'); assert.equal(a.editor.tags, 'Unsaved tag'); assert.equal(a.editor.launchArgs, '["--UNSAVED"]')
  assert.equal(messages.at(-1)[1], 'success')
})

test('personal record rejects invalid scores visibly and clearing a score submits null', async t => {
  const { a, api, games, messages } = await harness(t), calls: any[] = []
  await a.showGame(games[0]); api.updateGame = async (_: string, patch: any) => calls.push(patch)
  for (const rating of ['11', '-1', 'NaN', '1e', '-']) { a.editor.personalRating = rating; await a.savePersonalRecord(); assert.match(a.personalRecordError.value, /0 到 10/); assert.equal(a.editor.personalRating, rating) }
  assert.equal(calls.length, 0); assert.equal(messages.filter(message => message[1] === 'success').length, 0)
  a.editor.personalRating = ''; await a.savePersonalRecord(); assert.equal(calls.at(-1).personalRating, null); assert.equal(a.personalRecordError.value, '')
})

test('personal save captures the submitted draft once while later input remains editable and unsaved', async t => {
  const { a, api, games, state, messages } = await harness(t), response = deferred(), calls: any[] = []
  await a.showGame(games[0]); a.editor.notes = 'Submitted note'; a.editor.personalRating = '8'
  api.updateGame = async (_: string, patch: any) => { calls.push(JSON.parse(JSON.stringify(patch))); await response.promise }
  const pending = a.savePersonalRecord(); await a.savePersonalRecord()
  a.editor.notes = 'Typed while saving'; a.editor.personalRating = '9'
  api.getState = async () => state([{ ...games[0], ...calls[0] }, games[1]])
  response.resolve(); await pending
  assert.equal(calls.length, 1); assert.equal(calls[0].notes, 'Submitted note'); assert.equal(calls[0].personalRating, 8)
  assert.equal(a.editor.notes, 'Typed while saving'); assert.equal(a.editor.personalRating, '9'); assert.match(messages.at(-1)[0], /还未保存/)
  api.updateGame = async (_: string, patch: any) => calls.push(JSON.parse(JSON.stringify(patch)))
  await a.savePersonalRecord(); assert.equal(calls.at(-1).notes, 'Typed while saving'); assert.equal(calls.at(-1).personalRating, 9)
})

for (const reopened of [false, true]) test(`late personal save does not claim a newer ${reopened ? 'reopened A' : 'B'} draft was saved`, async t => {
  const { a, api, games, messages } = await harness(t), response = deferred()
  await a.showGame(games[0]); a.editor.notes = 'Submitted A'; api.updateGame = () => response.promise
  const pending = a.savePersonalRecord(); a.detailOpen.value = false; await a.showGame(games[reopened ? 0 : 1]); a.editor.notes = 'Newer draft'
  response.resolve(); await pending
  assert.equal(a.editor.notes, 'Newer draft'); assert.equal(messages.length, 0); assert.equal(a.personalRecordError.value, ''); assert.equal(a.busy.has('save-game'), false)
})

test('personal save failure preserves the draft, exposes the error and allows a real retry', async t => {
  const { a, api, games, messages } = await harness(t)
  await a.showGame(games[0]); a.editor.notes = 'Retryable draft'; api.updateGame = async () => { throw Error('Synthetic persistence failure') }
  await a.savePersonalRecord(); assert.equal(a.editor.notes, 'Retryable draft'); assert.equal(a.personalRecordError.value, 'Synthetic persistence failure'); assert.equal(messages.filter(message => message[1] === 'success').length, 0)
  let submitted: any; api.updateGame = async (_: string, patch: any) => { submitted = patch }
  await a.savePersonalRecord(); assert.equal(submitted.notes, 'Retryable draft'); assert.equal(a.personalRecordError.value, ''); assert.equal(messages.at(-1)[1], 'success')
})

test('personal save refuses a draft belonging to another game and stays silent on a late failure', async t => {
  const { a, api, games, messages } = await harness(t), response = deferred(); let calls = 0
  await a.showGame(games[0]); a.selectedGameId.value = games[1].id; api.updateGame = async () => { calls++; return response.promise }
  await a.savePersonalRecord(); assert.equal(calls, 0)
  await a.showGame(games[0]); const pending = a.savePersonalRecord(); a.detailOpen.value = false; await a.showGame(games[1]); a.editor.notes = 'B untouched'
  response.reject(Error('Late old-view failure')); await pending
  assert.equal(a.editor.notes, 'B untouched'); assert.equal(a.personalRecordError.value, ''); assert.equal(messages.length, 0)
})

test('personal save distinguishes a committed record from a failed snapshot refresh', async t => {
  const { a, api, games, state, messages } = await harness(t); let persisted: any, calls = 0
  await a.showGame(games[0]); a.editor.notes = 'Committed draft'; a.editor.personalRating = '7.5'
  api.updateGame = async (_: string, patch: any) => { persisted = { ...games[0], ...patch }; calls++ }
  api.getState = async () => { throw Error('Synthetic snapshot failure') }
  await a.savePersonalRecord()
  assert.equal(calls, 1); assert.equal(persisted.notes, 'Committed draft'); assert.equal(persisted.personalRating, 7.5)
  assert.match(a.personalRecordError.value, /记录已保存.*刷新失败/); assert.equal(a.editor.notes, 'Committed draft'); assert.equal(a.editor.personalRating, '7.5')
  assert.equal(messages.at(-1)[1], 'warn'); assert.equal(messages.filter(message => message[1] === 'success').length, 0)
  api.getState = async () => state([persisted, games[1]]); await a.savePersonalRecord()
  assert.equal(a.personalRecordError.value, ''); assert.equal(messages.at(-1)[1], 'success')
})
