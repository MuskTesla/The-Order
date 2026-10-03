import assert from 'node:assert/strict'
import { test } from 'node:test'
import { promises as fs } from 'node:fs'
import vm from 'node:vm'
import { transform } from 'esbuild'
import * as vue from 'vue'
import * as libraryWorkspace from '../src/library-workspace'
import * as libraryInsights from '../src/library-insights'
import type { AppSnapshot, BulkGamePatch, LibraryGame, MetadataMatchingState, ScanCandidate, WorkMetadata } from '../src/shared/types'

function deferred<T>() {
  let resolve!: (value: T) => void, reject!: (error: Error) => void
  const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no })
  return { promise, resolve, reject }
}
const plain = <T>(value: T): T => JSON.parse(JSON.stringify(value))
function game(id: string): LibraryGame {
  return { id, name: `Voyage ${id}`, nameIsCustom: true, metadata: null, installations: [{ id: `install-${id}`, folderPath: `C:\\Synthetic\\${id}`, executablePath: null, arguments: [] }], preferredInstallationId: `install-${id}`, status: id === 'B' ? 'playing' : 'planned', personalRating: null, favorite: false, categories: [id], customTags: [], notes: `Saved ${id}`, totalPlaySeconds: 0, playCount: 0, sessions: [], addedAt: '2026-10-03T00:00:00.000Z', lastPlayedAt: null }
}
function candidate(id: string, folder = id): ScanCandidate { return { id, name: `Candidate ${id}`, folderPath: `C:\\Synthetic\\${folder}`, executables: [`C:\\Synthetic\\${folder}\\Game.exe`] } }
function completeGame(id: string): LibraryGame {
  const metadata: WorkMetadata = { id: `work-${id}`, title: `Work ${id}`, originalTitle: '', aliases: [], coverUrl: 'https://fixture.invalid/cover.png', bannerUrl: null, description: 'Already linked', releaseDate: null, releaseDatePrecision: null, developers: [], tags: [], ratings: [], characters: [], references: [], webUrl: null, contentRating: null, fetchedAt: '2026-10-03T00:00:00.000Z' }
  return { ...game(id), metadata }
}
function job(id = 'fixture-job', status: MetadataMatchingState['status'] = 'running', total = 1): MetadataMatchingState {
  return { id, status, total, processed: status === 'completed' ? total : 0, matched: 0, unmatched: 0, failed: 0, currentName: null, reviews: [] }
}
// Every case runs the same production source in its own Vue scope and VM.
// Compile once so the test subject cannot change midway through the suite.
const compiledHarness = (async () => {
  const source = await fs.readFile(new URL('../src/App.vue', import.meta.url), 'utf8')
  const script = source.split('<script setup lang="ts">')[1]!.split('</script>')[0]!
  const expose = '\nglobalThis.audit = { snapshot, page, busy, libraryQuery, statusFilter, favoritesOnly, filteredGames, selectionMode, selectedIds, batchStatus, batchFavorite, batchCategory, batchTag, batchCategoryMode, batchTagMode, enterSelection, leaveSelection, selectVisible, toggleSelection, organizeSelected, navigate, resetFilters, scanOpen, candidates, chosen, autoMatchOnImport, scanQuery, alreadyInLibrary, newCandidates, visibleCandidates, scan, importSelected, startMatching, cancelMatching, matchingLabel, reidentifying, matchingUnavailable, localGameIds, selectedLocalIds, identifySelected, identifyCurrentGame, identifyScannedGames, scannedLibraryIds, showGame, detailOpen, selectedGameId, editor };'
  const compiled = await transform(script.replace(/^import .*$/gm, '') + expose, { loader: 'ts', format: 'cjs', target: 'es2022' })
  return { script, compiled }
})()
async function harness(t: { after(callback: () => void): void }) {
  const { script, compiled } = await compiledHarness
  const messages: Array<{ message: string; kind: string }> = []
  const sandbox: any = { ...vue, ...libraryWorkspace, ...libraryInsights, onMounted: () => {}, onUnmounted: () => {}, isPreview: false,
    useKunMessage: (message: string, kind: string) => messages.push({ message, kind }), useKunAlert: async () => true, api: {},
    document: { documentElement: { classList: { toggle() {} } } }, matchMedia: () => ({ matches: false }) }
  for (const line of script.split('\n').filter(line => /^import \{/.test(line))) for (const name of line.match(/^import \{([^}]+)\}/)![1]!.split(',').map(name => name.trim())) if (!(name in sandbox)) sandbox[name] = name
  const scope = vue.effectScope(); t.after(() => scope.stop())
  vm.createContext(sandbox); scope.run(() => vm.runInContext(compiled.code, sandbox))
  const a = sandbox.audit, games = ['A', 'B', 'C'].map(game)
  const state = (items = games): AppSnapshot => ({ games: structuredClone(items), settings: { baseUrl: 'https://fixture.invalid/v2/', hasApiKey: true, includeNsfw: false, theme: 'light', readExecutableMetadata: false }, activeSession: null, dataPath: 'synthetic-only', version: 'fixture' })
  a.snapshot.value = state(); sandbox.api.getState = async () => state()
  await vue.nextTick()
  return { a, api: sandbox.api, games, state, messages }
}

test('bulk submission captures cloneable IDs and relative changes once, independently of later draft mutations', async t => {
  const { a, api } = await harness(t), response = deferred<LibraryGame[]>(), calls: Array<{ ids: string[]; patch: BulkGamePatch }> = []
  api.updateGames = (ids: string[], patch: BulkGamePatch) => { calls.push({ ids, patch }); return response.promise }
  a.enterSelection(); a.toggleSelection('A'); a.toggleSelection('B')
  a.batchStatus.value = 'completed'; a.batchFavorite.value = 'no'
  a.batchCategory.value = ' Story ， Story, Short '; a.batchCategoryMode.value = 'add'
  a.batchTag.value = ' Finished\nFinished '; a.batchTagMode.value = 'remove'
  const pending = a.organizeSelected()
  assert.equal(a.busy.has('bulk'), true)
  assert.equal(vue.isProxy(calls[0]!.ids), false)
  assert.equal(vue.isProxy(calls[0]!.patch), false)
  assert.deepEqual(structuredClone(calls[0]), { ids: ['A', 'B'], patch: { status: 'completed', favorite: false, addCategories: ['Story', 'Short'], removeTags: ['Finished'] } })
  a.selectedIds.value.push('C'); a.batchStatus.value = 'paused'; a.batchCategory.value = 'Later'; a.batchTag.value = 'Later'
  await a.organizeSelected()
  assert.equal(calls.length, 1, 'double submission is locked while the first batch is pending')
  assert.deepEqual(plain(calls[0]), { ids: ['A', 'B'], patch: { status: 'completed', favorite: false, addCategories: ['Story', 'Short'], removeTags: ['Finished'] } })
  response.resolve([]); await pending
  assert.equal(a.selectionMode.value, false)
  assert.deepEqual(plain(a.selectedIds.value), [])
  assert.equal(a.busy.has('bulk'), false)
})

test('a cancelled and reopened selection keeps its new state when the previous batch completes', async t => {
  const { a, api } = await harness(t), response = deferred<LibraryGame[]>()
  api.updateGames = () => response.promise
  a.enterSelection(); a.toggleSelection('A'); a.batchFavorite.value = 'yes'
  const pending = a.organizeSelected()
  a.leaveSelection(); a.enterSelection()
  // Challenge completion ownership directly. Current controls lock selection
  // during the request; a newer selection state must still never be erased.
  a.selectedIds.value = ['B']; a.batchTag.value = 'Next batch'
  response.resolve([]); await pending
  assert.equal(a.selectionMode.value, true)
  assert.deepEqual(plain(a.selectedIds.value), ['B'])
  assert.equal(a.batchTag.value, 'Next batch')
  assert.equal(a.busy.has('bulk'), false)
})

test('bulk failure retains the original selection and operation so an explicit retry submits the same request', async t => {
  const { a, api, messages } = await harness(t), response = deferred<LibraryGame[]>(), calls: unknown[] = []
  api.updateGames = (ids: string[], patch: BulkGamePatch) => { calls.push(plain({ ids, patch })); return calls.length === 1 ? response.promise : Promise.resolve([]) }
  a.enterSelection(); a.toggleSelection('B'); a.batchCategory.value = 'Old category'; a.batchCategoryMode.value = 'remove'
  const pending = a.organizeSelected(); response.reject(new Error('Synthetic save failure')); await pending
  assert.equal(a.selectionMode.value, true)
  assert.deepEqual(plain(a.selectedIds.value), ['B'])
  assert.equal(a.batchCategory.value, 'Old category')
  assert.equal(a.batchCategoryMode.value, 'remove')
  assert.equal(a.busy.has('bulk'), false)
  assert.equal(messages.at(-1)?.kind, 'error')
  await a.organizeSelected()
  assert.deepEqual(calls[1], calls[0])
  assert.equal(a.selectionMode.value, false)
})

test('filters and external removal synchronously prune hidden selections without restoring them on reset', async t => {
  const { a, api, games, state } = await harness(t), calls: unknown[] = []
  api.updateGames = async (ids: string[], patch: BulkGamePatch) => { calls.push(plain({ ids, patch })); return [] }
  a.enterSelection(); a.selectVisible()
  assert.deepEqual(new Set(a.selectedIds.value), new Set(['A', 'B', 'C']))
  a.statusFilter.value = 'playing'
  assert.deepEqual(plain(a.selectedIds.value), ['B'])
  a.resetFilters()
  assert.deepEqual(plain(a.selectedIds.value), ['B'], 'revealing a previously hidden game does not implicitly reselect it')
  a.selectVisible(); a.libraryQuery.value = 'VoyageA'
  assert.deepEqual(plain(a.selectedIds.value), ['A'])
  a.snapshot.value = state(games.filter(item => item.id !== 'A'))
  assert.deepEqual(plain(a.selectedIds.value), [])
  a.batchFavorite.value = 'yes'; await a.organizeSelected()
  assert.equal(calls.length, 0, 'removed/hidden games cannot be included in the next batch')
})

test('empty bulk operations and empty selections do not send writes', async t => {
  const { a, api, messages } = await harness(t)
  let writes = 0; api.updateGames = async () => { writes++; return [] }
  a.enterSelection(); a.batchFavorite.value = 'yes'; await a.organizeSelected()
  a.batchFavorite.value = ''; a.toggleSelection('A'); a.batchCategory.value = ' , ，\n '; await a.organizeSelected()
  assert.equal(writes, 0)
  assert.equal(messages.at(-1)?.kind, 'info')
  assert.equal(a.selectionMode.value, true)
})

test('bulk completion after navigation does not return to the library or reopen selection', async t => {
  const { a, api } = await harness(t), response = deferred<LibraryGame[]>()
  api.updateGames = () => response.promise
  a.enterSelection(); a.toggleSelection('A'); a.batchFavorite.value = 'yes'
  const pending = a.organizeSelected(); a.navigate('stats')
  response.resolve([]); await pending
  assert.equal(a.page.value, 'stats')
  assert.equal(a.selectionMode.value, false)
  assert.deepEqual(plain(a.selectedIds.value), [])
})

test('scan preselects only new directories and filters a stale known candidate before invoking the bridge', async t => {
  const { a, api } = await harness(t), calls: unknown[] = []
  const known = { ...candidate('known', 'A'), folderPath: 'c:/SYNTHETIC/a/' }, fresh = candidate('fresh', 'Fresh')
  api.scanFolder = async () => [known, fresh]
  api.importLocalGames = async (ids: string[], match: boolean) => { calls.push({ ids, match }); return { added: 1, skipped: 0 } }
  await a.scan()
  assert.equal(a.scanOpen.value, true)
  assert.deepEqual(plain(a.chosen.value), ['fresh'])
  assert.deepEqual([...a.alreadyInLibrary.value], ['known'])
  assert.deepEqual(plain(a.newCandidates.value).map((item: ScanCandidate) => item.id), ['fresh'])
  a.chosen.value = ['known', 'fresh']
  await a.importSelected()
  assert.equal(vue.isProxy((calls[0] as any).ids), false)
  assert.deepEqual(structuredClone(calls[0]), { ids: ['fresh'], match: true })
  assert.equal(a.scanOpen.value, false)
  assert.equal(a.page.value, 'library')
})

test('a library notification removes a newly known candidate from selection before import', async t => {
  const { a, api, games, state } = await harness(t)
  api.scanFolder = async () => [candidate('fresh', 'Fresh')]
  let writes = 0; api.importLocalGames = async () => { writes++; return { added: 0, skipped: 1 } }
  await a.scan(); assert.deepEqual(plain(a.chosen.value), ['fresh'])
  const imported = game('Imported'); imported.installations[0]!.folderPath = 'C:\\Synthetic\\Fresh'
  a.snapshot.value = state([...games, imported])
  assert.deepEqual(plain(a.chosen.value), [])
  await a.importSelected()
  assert.equal(writes, 0)
  assert.equal(a.scanOpen.value, true)
})

test('pending import captures candidate IDs and recognition preference once, and blocks duplicate submits', async t => {
  const { a, api } = await harness(t), response = deferred<{ added: number; skipped: number }>(), calls: unknown[] = []
  api.scanFolder = async () => [candidate('first-scan-1', 'Fresh1'), candidate('first-scan-2', 'Fresh2')]
  api.importLocalGames = (ids: string[], match: boolean) => { calls.push({ ids, match }); return response.promise }
  await a.scan(); a.autoMatchOnImport.value = false
  const pending = a.importSelected()
  a.chosen.value.splice(0, 1); a.autoMatchOnImport.value = true
  await a.importSelected()
  assert.equal(calls.length, 1)
  assert.deepEqual(structuredClone(calls[0]), { ids: ['first-scan-1', 'first-scan-2'], match: false })
  response.resolve({ added: 2, skipped: 0 }); await pending
  assert.equal(a.busy.has('import'), false)
  assert.equal(a.scanOpen.value, false)
})

test('closing a pending import leaves a later page untouched when its successful result arrives', async t => {
  const { a, api, games, state } = await harness(t), response = deferred<{ added: number; skipped: number }>()
  api.scanFolder = async () => [candidate('fresh', 'Fresh')]; api.importLocalGames = () => response.promise
  await a.scan(); const pending = a.importSelected()
  a.scanOpen.value = false; a.navigate('stats')
  const imported = game('Fresh'); api.getState = async () => state([...games, imported])
  response.resolve({ added: 1, skipped: 0 }); await pending
  assert.equal(a.scanOpen.value, false)
  assert.equal(a.page.value, 'stats')
  assert.ok(a.snapshot.value.games.some((item: LibraryGame) => item.id === 'Fresh'), 'the persisted import is still reflected in the library')
})

test('an old import completion cannot close a reopened scan or replace its new candidates and choices', async t => {
  const { a, api } = await harness(t), response = deferred<{ added: number; skipped: number }>()
  let scans = 0
  api.scanFolder = async () => ++scans === 1 ? [candidate('old-scan', 'Old')] : [candidate('new-scan', 'New')]
  api.importLocalGames = () => response.promise
  await a.scan(); const pending = a.importSelected()
  a.scanOpen.value = false; a.navigate('discover'); await a.scan(); a.scanQuery.value = 'New'
  response.resolve({ added: 1, skipped: 0 }); await pending
  assert.equal(a.scanOpen.value, true)
  assert.equal(a.page.value, 'discover')
  assert.deepEqual(plain(a.candidates.value).map((item: ScanCandidate) => item.id), ['new-scan'])
  assert.deepEqual(plain(a.chosen.value), ['new-scan'])
  assert.equal(a.scanQuery.value, 'New')
  assert.equal(a.busy.has('import'), false)
})

test('import failure preserves choices for retry, while all-known scans never submit', async t => {
  const { a, api, messages } = await harness(t), response = deferred<{ added: number; skipped: number }>()
  let writes = 0
  api.scanFolder = async () => [candidate('fresh', 'Fresh')]
  api.importLocalGames = () => { writes++; return response.promise }
  await a.scan(); const pending = a.importSelected(); response.reject(new Error('Synthetic import failure')); await pending
  assert.equal(a.scanOpen.value, true)
  assert.deepEqual(plain(a.chosen.value), ['fresh'])
  assert.equal(a.busy.has('import'), false)
  assert.equal(messages.at(-1)?.kind, 'error')
  a.scanOpen.value = false
  api.scanFolder = async () => [candidate('known-A', 'A'), candidate('known-B', 'B')]
  await a.scan(); await a.importSelected()
  assert.deepEqual(plain(a.chosen.value), [])
  assert.equal(a.newCandidates.value.length, 0)
  assert.equal(writes, 1)
})

for (const condition of ['missing-key', 'already-running', 'start-pending'] as const) test(`import skips starting a second recognition job when ${condition}`, async t => {
  const { a, api, messages } = await harness(t), calls: unknown[] = []
  if (condition === 'missing-key') a.snapshot.value.settings.hasApiKey = false
  else if (condition === 'already-running') a.snapshot.value.matching = { id: 'existing', status: 'running', total: 1, processed: 0, matched: 0, unmatched: 0, failed: 0, currentName: null, reviews: [] }
  else a.busy.add('matching-start')
  api.scanFolder = async () => [candidate('fresh', 'Fresh')]
  api.importLocalGames = async (ids: string[], match: boolean) => { calls.push({ ids, match }); return { added: 1, skipped: 0 } }
  await a.scan(); await a.importSelected()
  assert.deepEqual(structuredClone(calls[0]), { ids: ['fresh'], match: false })
  assert.ok(!messages.some(message => message.message.includes('正在自动识别封面与资料')))
  if (condition !== 'missing-key') assert.match(messages.at(-1)!.message, /当前识别任务结束后，请点击“补全资料”/)
})

test('an actual pending recognition start allows local import but never starts a second automatic job', async t => {
  const { a, api, messages, state, games } = await harness(t), response = deferred<MetadataMatchingState>(), imported: unknown[] = []
  api.startMetadataMatching = () => response.promise
  const recognition = a.startMatching(['A'], 'reidentify')
  assert.equal(a.busy.has('matching-start'), true)
  assert.equal(a.snapshot.value.matching, undefined, 'the initial start has not delivered a running snapshot yet')
  api.scanFolder = async () => [candidate('fresh', 'Fresh')]
  api.importLocalGames = async (ids: string[], match: boolean) => { imported.push({ ids, match }); return { added: 1, skipped: 0, matchingStarted: false } }
  await a.scan(); await a.importSelected()
  assert.deepEqual(structuredClone(imported[0]), { ids: ['fresh'], match: false })
  assert.match(messages.at(-1)!.message, /已添加 1 部游戏/)
  assert.match(messages.at(-1)!.message, /当前识别任务结束后，请点击“补全资料”/)
  assert.ok(!messages.at(-1)!.message.includes('正在自动识别封面与资料'))
  assert.equal(a.busy.has('matching-start'), true)
  api.getState = async () => ({ ...state(games), matching: job('original') })
  response.resolve(job('original')); await recognition
})

test('a fully illustrated library can explicitly reidentify all local games while missing-only completion stays idle', async t => {
  const { a, api, state, messages } = await harness(t), calls: unknown[] = []
  const virtual = { ...completeGame('Virtual'), installations: [], preferredInstallationId: null }
  const games = [completeGame('A'), completeGame('B'), virtual]
  a.snapshot.value = state(games)
  const running = { ...state(games), matching: job('all-local', 'running', 2) }
  api.getState = async () => running
  api.startMetadataMatching = async (ids: string[], mode: string) => { calls.push({ ids, mode }); return running.matching }
  assert.equal(await a.startMatching(), false)
  assert.equal(calls.length, 0)
  assert.match(messages.at(-1)!.message, /没有需要补全/)
  assert.equal(a.matchingUnavailable.value, false)
  assert.deepEqual(plain(a.localGameIds.value), ['A', 'B'])
  assert.equal(await a.startMatching(undefined, 'reidentify'), true)
  assert.deepEqual(structuredClone(calls[0]), { ids: ['A', 'B'], mode: 'reidentify' })
  assert.equal(vue.isProxy((calls[0] as any).ids), false)
  assert.deepEqual(plain(a.snapshot.value.games), games, 'starting recognition never clears the existing metadata in the UI')
})

test('missing-only completion sends only incomplete local games and retains its explicit mode', async t => {
  const { a, api, state } = await harness(t), calls: unknown[] = []
  const games = [completeGame('A'), game('B'), { ...completeGame('C'), metadata: { ...completeGame('C').metadata!, coverUrl: null } }]
  a.snapshot.value = state(games)
  const running = { ...state(games), matching: job('missing', 'running', 2) }
  api.getState = async () => running
  api.startMetadataMatching = async (ids: string[], mode: string) => { calls.push({ ids, mode }); return running.matching }
  await a.startMatching()
  assert.deepEqual(structuredClone(calls[0]), { ids: ['B', 'C'], mode: 'missing' })
})

test('recognizing selected games captures plain local IDs without clearing the newer selection or its drafts', async t => {
  const { a, api, state } = await harness(t), response = deferred<MetadataMatchingState>(), calls: unknown[] = []
  const virtual = { ...completeGame('Virtual'), installations: [], preferredInstallationId: null }
  const games = [completeGame('A'), completeGame('B'), virtual]
  a.snapshot.value = state(games)
  const running = { ...state(games), matching: job('selected') }
  api.getState = async () => running
  api.startMetadataMatching = (ids: string[], mode: string) => { calls.push({ ids, mode }); return response.promise }
  a.enterSelection(); a.toggleSelection('A'); a.toggleSelection('Virtual')
  const pending = a.identifySelected()
  a.leaveSelection(); a.enterSelection(); a.toggleSelection('B'); a.batchTag.value = 'Next operation'
  await a.identifySelected(); await a.startMatching(undefined, 'reidentify')
  assert.equal(calls.length, 1)
  assert.deepEqual(structuredClone(calls[0]), { ids: ['A'], mode: 'reidentify' })
  response.resolve(running.matching); await pending
  assert.equal(a.selectionMode.value, true)
  assert.deepEqual(plain(a.selectedIds.value), ['B'])
  assert.equal(a.batchTag.value, 'Next operation')
})

test('per-game recognition targets the original local game and preserves a subsequently opened detail and draft', async t => {
  const { a, api, state } = await harness(t), response = deferred<MetadataMatchingState>(), calls: unknown[] = []
  const games = [completeGame('A'), completeGame('B')]
  a.snapshot.value = state(games)
  const running = { ...state(games), matching: job('detail') }
  api.getState = async () => running
  api.startMetadataMatching = (ids: string[], mode: string) => { calls.push({ ids, mode }); return response.promise }
  await a.showGame(games[0]); const pending = a.identifyCurrentGame()
  a.detailOpen.value = false; await a.showGame(games[1]); a.editor.notes = 'Unsaved B note'
  await a.identifyCurrentGame()
  assert.equal(calls.length, 1)
  assert.deepEqual(structuredClone(calls[0]), { ids: ['A'], mode: 'reidentify' })
  response.resolve(running.matching); await pending
  assert.equal(a.detailOpen.value, true)
  assert.equal(a.selectedGameId.value, 'B')
  assert.equal(a.editor.notes, 'Unsaved B note')
})

test('explicit recognition excludes absent and virtual IDs, and an empty local selection never calls the API', async t => {
  const { a, api, state, messages } = await harness(t), calls: unknown[] = []
  const virtual = { ...completeGame('Virtual'), installations: [], preferredInstallationId: null }
  const games = [completeGame('A'), virtual]
  a.snapshot.value = state(games)
  api.startMetadataMatching = async (ids: string[], mode: string) => { calls.push({ ids, mode }); return job() }
  await a.startMatching([], 'reidentify'); await a.startMatching(['Virtual', 'missing'], 'reidentify')
  await a.showGame(virtual); await a.identifyCurrentGame()
  assert.equal(calls.length, 0)
  assert.match(messages.at(-1)!.message, /没有可重新识别的本地游戏/)
  api.getState = async () => ({ ...state(games), matching: job() })
  await a.startMatching(['A', 'A', 'Virtual', 'missing'], 'reidentify')
  assert.deepEqual(structuredClone(calls[0]), { ids: ['A'], mode: 'reidentify' })
})

test('recognition with no saved key guides to settings and closes the initiating overlays without sending a job', async t => {
  const { a, api, games, messages } = await harness(t)
  a.snapshot.value.settings.hasApiKey = false
  let calls = 0; api.startMetadataMatching = async () => { calls++; return job() }
  await a.showGame(games[0]); a.scanOpen.value = true
  assert.equal(await a.startMatching(['A'], 'reidentify'), false)
  assert.equal(calls, 0)
  assert.equal(a.page.value, 'settings')
  assert.equal(a.detailOpen.value, false)
  assert.equal(a.scanOpen.value, false)
  assert.match(messages.at(-1)!.message, /先保存 API 密钥/)
})

test('an already running recognition task prevents every new entry point from submitting', async t => {
  const { a, api, games } = await harness(t)
  a.snapshot.value.matching = job('already-running')
  let calls = 0; api.startMetadataMatching = async () => { calls++; return job() }
  a.enterSelection(); a.toggleSelection('A'); await a.showGame(games[0])
  a.candidates.value = [candidate('known-A', 'A')]; a.scanOpen.value = true
  await a.startMatching(undefined, 'reidentify'); await a.identifySelected(); await a.identifyCurrentGame(); await a.identifyScannedGames()
  assert.equal(a.matchingUnavailable.value, true)
  assert.equal(calls, 0)
  assert.equal(a.scanOpen.value, true)
})

test('mixed scanning recognizes existing library IDs once per game and preserves the new import selection', async t => {
  const { a, api, state } = await harness(t), calls: unknown[] = []
  const existing = completeGame('A')
  existing.installations.push({ ...existing.installations[0]!, id: 'alternate', folderPath: 'C:\\Synthetic\\Alternate' })
  const games = [existing, completeGame('B')]
  a.snapshot.value = state(games)
  api.scanFolder = async () => [{ ...candidate('scanner-A', 'A'), folderPath: 'c:/synthetic/a/' }, candidate('scanner-alt', 'Alternate'), candidate('scanner-new', 'New')]
  api.importLocalGames = async () => { throw new Error('Recognition must not reimport existing games') }
  const running = { ...state(games), matching: job('known-scan') }
  api.getState = async () => running
  api.startMetadataMatching = async (ids: string[], mode: string) => { calls.push({ ids, mode }); return running.matching }
  a.page.value = 'discover'; await a.scan()
  assert.deepEqual(plain(a.scannedLibraryIds.value), ['A'])
  assert.deepEqual(plain(a.chosen.value), ['scanner-new'])
  await a.identifyScannedGames()
  assert.deepEqual(structuredClone(calls[0]), { ids: ['A'], mode: 'reidentify' })
  assert.equal(a.scanOpen.value, true)
  assert.equal(a.page.value, 'discover')
  assert.deepEqual(plain(a.chosen.value), ['scanner-new'])
  assert.deepEqual(plain(a.snapshot.value.games), games)
})

test('recognizing an all-known scan closes only its own modal and returns to the library', async t => {
  const { a, api, state, games } = await harness(t)
  api.scanFolder = async () => [candidate('scanner-A', 'A')]
  const running = { ...state(games), matching: job('all-known') }
  api.getState = async () => running
  api.startMetadataMatching = async () => running.matching
  a.page.value = 'discover'; await a.scan(); await a.identifyScannedGames()
  assert.equal(a.scanOpen.value, false)
  assert.equal(a.page.value, 'library')
})

test('a late recognition start from an old scan cannot close a new scan or change its current page', async t => {
  const { a, api, state, games } = await harness(t), response = deferred<MetadataMatchingState>(), calls: unknown[] = []
  let scans = 0
  api.scanFolder = async () => ++scans === 1 ? [candidate('old-known', 'A')] : [candidate('new-known', 'B'), candidate('new-choice', 'New')]
  api.startMetadataMatching = (ids: string[], mode: string) => { calls.push({ ids, mode }); return response.promise }
  const running = { ...state(games), matching: job('old-scan') }
  api.getState = async () => running
  await a.scan(); const pending = a.identifyScannedGames()
  a.scanOpen.value = false; a.navigate('stats'); await a.scan(); a.scanQuery.value = 'New'
  response.resolve(running.matching); await pending
  assert.deepEqual(structuredClone(calls[0]), { ids: ['A'], mode: 'reidentify' })
  assert.equal(a.scanOpen.value, true)
  assert.equal(a.page.value, 'stats')
  assert.deepEqual(plain(a.candidates.value).map((item: ScanCandidate) => item.id), ['new-known', 'new-choice'])
  assert.deepEqual(plain(a.chosen.value), ['new-choice'])
  assert.equal(a.scanQuery.value, 'New')
})

for (const newer of ['cancelled', 'different-job'] as const) test(`a late start reply cannot revive or claim a ${newer} recognition state`, async t => {
  const { a, api, state, games, messages } = await harness(t), response = deferred<MetadataMatchingState>()
  api.scanFolder = async () => [candidate('known', 'A')]
  api.startMetadataMatching = () => response.promise
  const latest = { ...state(games), matching: newer === 'cancelled' ? job('original', 'cancelled') : job('newer-job') }
  api.getState = async () => latest
  await a.scan(); const pending = a.identifyScannedGames()
  a.snapshot.value = latest
  response.resolve(job('original')); await pending
  assert.deepEqual(plain(a.snapshot.value.matching), latest.matching)
  assert.equal(a.scanOpen.value, true, 'a superseded start does not dismiss the current scan')
  assert.ok(!messages.some(message => message.kind === 'success'))
  assert.equal(a.busy.has('matching-start'), false)
})

test('recognition empty result and request failure retain the scan for retry and report the actual outcome', async t => {
  const { a, api, state, games, messages } = await harness(t)
  api.scanFolder = async () => [candidate('known', 'A')]
  api.startMetadataMatching = async () => job('empty', 'completed', 0)
  api.getState = async () => ({ ...state(games), matching: job('empty', 'completed', 0) })
  await a.scan(); await a.identifyScannedGames()
  assert.equal(a.scanOpen.value, true)
  assert.match(messages.at(-1)!.message, /没有可处理/)
  api.startMetadataMatching = async () => { throw new Error('Synthetic start failure') }
  await a.identifyScannedGames()
  assert.equal(a.scanOpen.value, true)
  assert.equal(messages.at(-1)!.kind, 'error')
  assert.equal(a.busy.has('matching-start'), false)
})

test('recognition progress labels distinguish reidentification from ordinary and legacy completion jobs', async t => {
  const { a } = await harness(t)
  for (const status of ['running', 'completed', 'cancelled'] as const) {
    a.snapshot.value.matching = { ...job('reidentify', status), mode: 'reidentify' }
    assert.equal(a.reidentifying.value, true)
    assert.match(a.matchingLabel.value, /重新识别/)
    assert.doesNotMatch(a.matchingLabel.value, /补全/)
    a.snapshot.value.matching = job('legacy', status)
    assert.equal(a.reidentifying.value, false)
    assert.match(a.matchingLabel.value, /补全/)
  }
})

test('a delayed cancellation reply cannot replace the state of a newer recognition job', async t => {
  const { a, api, state, games } = await harness(t), response = deferred<MetadataMatchingState>()
  a.snapshot.value.matching = { ...job('old'), mode: 'reidentify' }
  api.cancelMetadataMatching = () => response.promise
  const pending = a.cancelMatching()
  const newer = { ...state(games), matching: { ...job('newer'), mode: 'reidentify' as const } }
  a.snapshot.value = newer; api.getState = async () => newer
  response.resolve({ ...job('old', 'cancelled'), mode: 'reidentify' }); await pending
  assert.deepEqual(plain(a.snapshot.value.matching), newer.matching)
  assert.equal(a.matchingUnavailable.value, true)
  assert.equal(a.busy.has('matching-cancel'), false)
})
