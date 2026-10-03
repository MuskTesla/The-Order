import assert from 'node:assert/strict'
import { test } from 'node:test'
import { promises as fs } from 'node:fs'
import vm from 'node:vm'
import { transform } from 'esbuild'
import * as vue from 'vue'
import * as libraryWorkspace from '../src/library-workspace'
import * as libraryInsights from '../src/library-insights'

const deferred = () => {
  let resolve!: (value?: any) => void, reject!: (error: Error) => void
  const promise = new Promise<any>((yes, no) => { resolve = yes; reject = no })
  return { promise, resolve, reject }
}
async function harness() {
  const source = await fs.readFile(new URL('../src/App.vue', import.meta.url), 'utf8')
  const script = source.split('<script setup lang="ts">')[1]!.split('</script>')[0]!
  const messages: any[] = []
  const sandbox: any = { ...vue, ...libraryWorkspace, ...libraryInsights, onMounted: () => {}, onUnmounted: () => {}, isPreview: false, useKunMessage: (...args: any[]) => messages.push(args), useKunAlert: async () => true, api: {}, document: { documentElement: { classList: { toggle() {} } } }, matchMedia: () => ({ matches: false }) }
  for (const line of script.split('\n').filter(line => /^import \{/.test(line))) for (const name of line.match(/^import \{([^}]+)\}/)![1]!.split(',').map(name => name.trim())) if (!(name in sandbox)) sandbox[name] = name
  const expose = '\nglobalThis.audit = { snapshot, editor, busy, selectedGameId, selectedWork, selectedGame, detailOpen, showGame, showWork, saveGame, saveArguments, addToLibrary, removeGame };'
  const compiled = await transform(script.replace(/^import .*$/gm, '') + expose, { loader: 'ts', format: 'cjs', target: 'es2022' })
  vm.createContext(sandbox); vm.runInContext(compiled.code, sandbox)
  const a = sandbox.audit
  const games = ['A', 'B'].map(id => ({ id, name: `Game ${id}`, metadata: { id: `work-${id}`, title: `Work ${id}` }, status: 'planned', personalRating: null, categories: [id], customTags: [id], notes: `Saved ${id}`, installations: [{ id: 'install', arguments: [`--${id}`] }], preferredInstallationId: 'install' }))
  const state = (items = games) => ({ games: items, settings: { baseUrl: '', includeNsfw: false, hasApiKey: false } })
  a.snapshot.value = state(); sandbox.api.getState = async () => state()
  return { a, api: sandbox.api, sandbox, games, state, messages }
}

test('discovery initializes the correct library editor before detail returns and preserves later edits', async () => {
  const { a, api, games } = await harness(), detail = deferred(), saved: any[] = []
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

test('a merged work revealing an existing game initializes that editor once', async () => {
  const { a, api, games } = await harness(), detail = deferred()
  await a.showGame(games[0]); a.editor.notes = 'Unsaved A'
  api.getWork = () => detail.promise
  const pending = a.showWork({ id: 'old-work-B', title: 'Old B reference' })
  assert.equal(a.editor.notes, ''); assert.equal(a.selectedGame.value, null)
  detail.resolve(games[1].metadata); await pending
  assert.equal(a.selectedGameId.value, 'B'); assert.equal(a.editor.notes, 'Saved B')
})

test('stale detail failure stays silent after close and reopening another request', async () => {
  const { a, api, games, messages } = await harness(), first = deferred(), second = deferred()
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

test('library metadata notifications preserve drafts and supersede a previous work detail', async () => {
  const { a, api, games, state } = await harness(), detail = deferred()
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

test('saving rejects editor data belonging to another game even when installation IDs coincide', async () => {
  const { a, api, games } = await harness()
  await a.showGame(games[0])
  a.selectedGameId.value = 'B'; a.selectedWork.value = games[1].metadata
  let saves = 0; api.updateGame = api.setLaunchArguments = async () => { saves++ }
  await a.saveGame(); await a.saveArguments()
  assert.equal(saves, 0)
})

test('late wishlist completion persists its game without changing a newer detail or draft', async () => {
  const { a, api, games, state } = await harness(), addition = deferred()
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

test('wishlist completion in the original view enables correctly initialized management', async () => {
  const { a, api, games, state } = await harness()
  const added = { ...games[0], id: 'C', name: 'Game C', notes: '', metadata: { id: 'work-C', title: 'Work C' } }
  api.getWork = async () => added.metadata; api.addVirtualGame = async () => added
  await a.showWork(added.metadata)
  api.getState = async () => state([...games, added]); await a.addToLibrary()
  assert.equal(a.selectedGame.value.id, 'C'); assert.equal(a.editor.name, 'Game C'); assert.equal(a.editor.notes, '')
})

test('removal confirmation becomes stale after closing and reopening even the same game', async () => {
  const { a, api, games, sandbox } = await harness(), confirmation = deferred()
  sandbox.useKunAlert = () => confirmation.promise
  await a.showGame(games[0]); let removals = 0; api.removeGame = async () => { removals++ }
  const pending = a.removeGame()
  a.detailOpen.value = false; await a.showGame(games[0]); a.editor.notes = 'Reopened A draft'
  confirmation.resolve(true); await pending
  assert.equal(removals, 0); assert.equal(a.detailOpen.value, true); assert.equal(a.editor.notes, 'Reopened A draft')
})

test('late removal reply preserves another detail but normal removal closes its own vanished entry', async () => {
  const { a, api, games, state } = await harness(), removal = deferred()
  await a.showGame(games[0]); api.removeGame = () => removal.promise
  const pending = a.removeGame(); await Promise.resolve(); await Promise.resolve()
  a.detailOpen.value = false; await a.showGame(games[1]); a.editor.notes = 'New B draft'
  api.getState = async () => state([games[1]]); removal.resolve(); await pending
  assert.equal(a.detailOpen.value, true); assert.equal(a.selectedGame.value.id, 'B'); assert.equal(a.editor.notes, 'New B draft')
  api.removeGame = async () => { a.snapshot.value = state([]) }; api.getState = async () => state([])
  await a.removeGame()
  assert.equal(a.detailOpen.value, false)
})
