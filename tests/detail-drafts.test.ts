import assert from 'node:assert/strict'
import { test } from 'node:test'
import { promises as fs } from 'node:fs'
import vm from 'node:vm'
import { transform } from 'esbuild'
import * as vue from 'vue'
import * as libraryWorkspace from '../src/library-workspace'
import * as libraryInsights from '../src/library-insights'
import * as savedLibraryViews from '../src/saved-library-views'
import * as coverDisplay from '../src/cover-display'
import * as importWorkspace from '../src/import-workspace'
import type { AppSnapshot, LibraryGame, WorkMetadata } from '../src/shared/types'

function deferred<T>() {
  let resolve!: (value: T) => void, reject!: (error: Error) => void
  const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no })
  return { promise, resolve, reject }
}
const plain = <T>(value: T): T => JSON.parse(JSON.stringify(value))
function work(id: string): WorkMetadata {
  return { id, title: `Work ${id}`, originalTitle: `Original ${id}`, aliases: [], coverUrl: null, bannerUrl: null, description: 'Synthetic details', releaseDate: null, releaseDatePrecision: null, developers: [], tags: ['synthetic-tag'], ratings: [], characters: [], references: [], webUrl: null, contentRating: null, fetchedAt: '2026-10-04T00:00:00.000Z' }
}
function game(id: string): LibraryGame {
  return { id, name: `Local ${id}`, nameIsCustom: true, metadata: work(`work-${id}`), installations: [
    { id: `${id}-primary`, folderPath: `C:\\Synthetic\\${id}\\primary`, executablePath: null, arguments: ['--primary'] },
    { id: `${id}-secondary`, folderPath: `C:\\Synthetic\\${id}\\secondary`, executablePath: null, arguments: ['--secondary'] }
  ], preferredInstallationId: `${id}-primary`, status: 'planned', personalRating: 0, favorite: false, categories: ['saved-category'], customTags: ['saved-tag'], notes: `Saved ${id}`, totalPlaySeconds: 0, playCount: 0, sessions: [], addedAt: '2026-10-04T00:00:00.000Z', lastPlayedAt: null }
}

// Run the real App setup, including its synchronous draft and modal watchers.
// Bridge replies and alerts are controlled; no local profile or game is used.
const compiledHarness = (async () => {
  const source = await fs.readFile(new URL('../src/App.vue', import.meta.url), 'utf8')
  const script = source.split('<script setup lang="ts">')[1]!.split('</script>')[0]!
  const expose = '\nglobalThis.audit = { snapshot, editor, busy, page, tagFilter, selectedGameId, selectedWork, selectedGame, detailOpen, detailDraftGroups, detailWriteBusy, detailClosePending, detailModalOpen, requestCloseDetail, showDetailTag, detailSaveError, argumentsSaveError, detailReadNeeded, reloadDetailState, personalRecordError, coverDraftDirty, showGame, showWork, saveGame, saveArguments, savePersonalRecord, setPreferred, markNameEdited, markArgumentsEdited };'
  const compiled = await transform(script.replace(/^import .*$/gm, '') + expose, { loader: 'ts', format: 'cjs', target: 'es2022' })
  return { script, compiled }
})()
async function harness(t: { after(callback: () => void): void }) {
  const { script, compiled } = await compiledHarness
  const messages: Array<{ message: string; kind: string }> = [], alerts: unknown[] = []
  const sandbox: any = { Error, ...vue, ...libraryWorkspace, ...libraryInsights, ...savedLibraryViews, ...coverDisplay, ...importWorkspace,
    onMounted: () => {}, onUnmounted: () => {}, isPreview: false,
    useKunMessage: (message: string, kind: string) => messages.push({ message, kind }),
    useKunAlert: async (options: unknown) => { alerts.push(options); return false }, api: {},
    document: { documentElement: { classList: { toggle() {} } }, querySelector: () => null }, matchMedia: () => ({ matches: false }) }
  for (const line of script.split('\n').filter(line => /^import \{/.test(line))) for (const name of line.match(/^import \{([^}]+)\}/)![1]!.split(',').map(name => name.trim())) if (!(name in sandbox)) sandbox[name] = name
  const scope = vue.effectScope(); t.after(() => scope.stop())
  vm.createContext(sandbox); scope.run(() => vm.runInContext(compiled.code, sandbox))
  const a = sandbox.audit, games = ['A', 'B'].map(game), server = plain(games)
  const state = (items = server): AppSnapshot => ({ games: plain(items), settings: { baseUrl: 'https://fixture.invalid/v2/', hasApiKey: true, includeNsfw: false, theme: 'light', readExecutableMetadata: false }, activeSession: null, dataPath: 'synthetic-only', version: 'fixture' })
  const patchGame = (id: string, patch: any) => {
    const index = server.findIndex(item => item.id === id)
    assert.notEqual(index, -1)
    server[index] = { ...server[index]!, ...plain(patch) }
    return plain(server[index]!)
  }
  const patchArguments = (id: string, installationId: string, args: string[]) => {
    const current = server.find(item => item.id === id)!
    assert.ok(current.installations.some(item => item.id === installationId))
    return patchGame(id, { installations: current.installations.map(item => item.id === installationId ? { ...item, arguments: plain(args) } : item) })
  }
  sandbox.api.getState = async () => state()
  sandbox.api.updateGame = async (id: string, patch: any) => patchGame(id, patch)
  sandbox.api.setLaunchArguments = async (id: string, installationId: string, args: string[]) => patchArguments(id, installationId, args)
  sandbox.api.getWork = async (id: string) => work(id)
  a.snapshot.value = state(); await a.showGame(games[0]!); await vue.nextTick()
  const groups = () => Array.from(a.detailDraftGroups.value) as string[]
  const changeArguments = (value: string) => { a.editor.launchArgs = value; a.markArgumentsEdited() }
  return { a, api: sandbox.api, sandbox, games, server, state, messages, alerts, patchGame, patchArguments, groups, changeArguments }
}

test('draft groups distinguish a saved zero score, cleared score, management edits, installation arguments and cover selection', { timeout: 10000 }, async t => {
  const { a, games, groups, changeArguments } = await harness(t)
  assert.deepEqual(groups(), [])
  a.editor.personalRating = ''; assert.deepEqual(groups(), ['个人记录'])
  a.editor.personalRating = '0'; assert.deepEqual(groups(), [])
  a.editor.categories = 'new-category'; assert.deepEqual(groups(), ['书架信息'])
  a.editor.notes = 'new-note'; changeArguments('["--draft"]'); a.coverDraftDirty.value = true
  assert.deepEqual(new Set(groups()), new Set(['个人记录', '书架信息', '启动参数', '封面与展示']))
  await a.showGame(games[1]!); assert.deepEqual(groups(), [])
})

test('saving personal records and management information clears only each acknowledged group, leaving launch parameters unsaved', { timeout: 10000 }, async t => {
  const { a, api, patchGame, groups, changeArguments } = await harness(t), patches: any[] = []
  a.editor.notes = 'Personal draft'; a.editor.categories = 'Management draft'; changeArguments('["--args-draft"]')
  api.updateGame = async (id: string, patch: any) => { patches.push(plain(patch)); return patchGame(id, patch) }
  await a.savePersonalRecord()
  assert.deepEqual(patches[0], { status: 'planned', personalRating: 0, notes: 'Personal draft' })
  assert.deepEqual(new Set(groups()), new Set(['书架信息', '启动参数']))
  await a.saveGame()
  assert.ok(!('name' in patches[1]), 'untouched names retain the existing omission rule')
  assert.deepEqual(groups(), ['启动参数'])
  await a.saveArguments(); assert.deepEqual(groups(), [])
})

for (const action of ['savePersonalRecord', 'saveGame'] as const) test(`${action} acknowledges its submitted values while edits entered during persistence remain dirty`, { timeout: 10000 }, async t => {
  const { a, api, patchGame, groups, messages } = await harness(t), reply = deferred<LibraryGame>(), calls: any[] = []
  a.editor.notes = 'Submitted note'; if (action === 'saveGame') a.editor.categories = 'Submitted category'
  api.updateGame = (id: string, patch: any) => { calls.push(plain(patch)); patchGame(id, patch); return reply.promise }
  const pending = a[action]()
  assert.equal(a.detailWriteBusy.value, true)
  a.editor.notes = 'Later note'; if (action === 'saveGame') a.editor.categories = 'Later category'
  reply.resolve(patchGame('A', calls[0])); await pending
  assert.equal(a.editor.notes, 'Later note'); assert.ok(groups().includes('个人记录'))
  if (action === 'saveGame') { assert.equal(a.editor.categories, 'Later category'); assert.ok(groups().includes('书架信息')) }
  assert.equal(calls[0].notes, 'Submitted note'); assert.equal(a.detailWriteBusy.value, false)
  assert.ok(!messages.some(item => /刚输入.*已保存|所有.*已保存/.test(item.message)))
})

test('launch argument acknowledgement preserves later input and continues identifying it as unsaved', { timeout: 10000 }, async t => {
  const { a, api, patchArguments, groups, changeArguments } = await harness(t), reply = deferred<LibraryGame>(), calls: any[] = []
  changeArguments('["--submitted"]')
  api.setLaunchArguments = (id: string, installationId: string, args: string[]) => { calls.push([id, installationId, plain(args)]); patchArguments(id, installationId, args); return reply.promise }
  const pending = a.saveArguments(); changeArguments('["--later"]')
  reply.resolve(patchArguments('A', 'A-primary', ['--submitted'])); await pending
  assert.deepEqual(calls, [['A', 'A-primary', ['--submitted']]])
  assert.equal(a.editor.launchArgs, '["--later"]'); assert.deepEqual(groups(), ['启动参数'])
})

test('ordinary close needs no confirmation, while cancelling a dirty close preserves its entire draft', { timeout: 10000 }, async t => {
  const { a, games, alerts, groups, changeArguments } = await harness(t)
  assert.equal(await a.requestCloseDetail(), true); assert.equal(a.detailOpen.value, false); assert.equal(alerts.length, 0)
  await a.showGame(games[0]!); a.editor.notes = 'Keep note'; a.editor.name = 'Keep name'; a.markNameEdited(); changeArguments('["--keep"]')
  assert.equal(await a.requestCloseDetail(), false)
  assert.equal(a.detailOpen.value, true); assert.equal(a.editor.notes, 'Keep note'); assert.equal(a.editor.name, 'Keep name'); assert.equal(a.editor.launchArgs, '["--keep"]')
  assert.equal(alerts.length, 1); assert.equal(a.detailClosePending.value, false); assert.equal(groups().length, 3)
})

test('accepting discard closes only the current session and does not persist the discarded draft', { timeout: 10000 }, async t => {
  const { a, api, sandbox } = await harness(t); let writes = 0
  a.editor.notes = 'Discarded note'; api.updateGame = async () => { writes++; throw Error('Must not save on discard') }
  sandbox.useKunAlert = async () => true
  assert.equal(await a.requestCloseDetail(), true); assert.equal(a.detailOpen.value, false); assert.equal(writes, 0)
})

test('overlapping modal and explicit close requests share one confirmation and preserve the draft on cancellation', { timeout: 10000 }, async t => {
  const { a, sandbox } = await harness(t), confirmation = deferred<boolean>(); let alerts = 0
  a.editor.notes = 'Single-confirmation draft'; sandbox.useKunAlert = () => { alerts++; return confirmation.promise }
  const first = a.requestCloseDetail(); const second = a.requestCloseDetail(); a.detailModalOpen.value = false
  assert.equal(alerts, 1); assert.equal(a.detailClosePending.value, true); assert.equal(a.detailOpen.value, true)
  confirmation.resolve(false); await first; await second; await vue.nextTick()
  assert.equal(a.detailOpen.value, true); assert.equal(a.editor.notes, 'Single-confirmation draft'); assert.equal(a.detailClosePending.value, false)
})

for (const reopened of [false, true]) test(`an old discard confirmation cannot close a newer ${reopened ? 'same-game' : 'different-game'} detail`, { timeout: 10000 }, async t => {
  const { a, games, sandbox } = await harness(t), confirmation = deferred<boolean>()
  a.editor.notes = 'Old draft'; sandbox.useKunAlert = () => confirmation.promise
  const closing = a.requestCloseDetail()
  a.detailOpen.value = false; await a.showGame(games[reopened ? 0 : 1]!)
  a.editor.notes = 'Fresh draft'; confirmation.resolve(true); assert.equal(await closing, false)
  assert.equal(a.detailOpen.value, true); assert.equal(a.editor.notes, 'Fresh draft'); assert.equal(a.selectedGame.value.id, reopened ? 'A' : 'B')
})

test('a draft changed after confirmation was requested is not discarded by the earlier confirmation', { timeout: 10000 }, async t => {
  const { a, sandbox } = await harness(t), confirmation = deferred<boolean>()
  a.editor.notes = 'Prompted draft'; sandbox.useKunAlert = () => confirmation.promise
  const closing = a.requestCloseDetail(); a.editor.notes = 'Later input'; confirmation.resolve(true)
  assert.equal(await closing, false); assert.equal(a.detailOpen.value, true); assert.equal(a.editor.notes, 'Later input')
})

test('cancelled tag navigation retains the current page and filters; accepted navigation applies them only after leaving', { timeout: 10000 }, async t => {
  const { a, sandbox } = await harness(t)
  a.page.value = 'discover'; a.tagFilter.value = 'old-tag'; a.editor.notes = 'Do not lose this note'
  await a.showDetailTag('new-tag')
  assert.equal(a.detailOpen.value, true); assert.equal(a.page.value, 'discover'); assert.equal(a.tagFilter.value, 'old-tag')
  sandbox.useKunAlert = async () => true; await a.showDetailTag('new-tag')
  assert.equal(a.detailOpen.value, false); assert.equal(a.page.value, 'library'); assert.equal(a.tagFilter.value, 'new-tag')
})

test('detail persistence prevents close confirmation while an atomic save is pending', { timeout: 10000 }, async t => {
  const { a, api, patchGame, alerts } = await harness(t), reply = deferred<LibraryGame>()
  a.editor.notes = 'Submitted'; api.updateGame = () => reply.promise
  const saving = a.saveGame()
  assert.equal(await a.requestCloseDetail(), false); a.detailModalOpen.value = false
  assert.equal(a.detailOpen.value, true); assert.equal(alerts.length, 0)
  reply.resolve(patchGame('A', { notes: 'Submitted' })); await saving
})

for (const action of ['saveGame', 'saveArguments'] as const) test(`${action} retains the draft and a sustained inline error after a rejected write, and a retry performs a real write`, { timeout: 10000 }, async t => {
  const { a, api, patchGame, patchArguments, groups, messages, changeArguments } = await harness(t); let writes = 0
  if (action === 'saveGame') a.editor.categories = 'Retry category'; else changeArguments('["--retry"]')
  const error = action === 'saveGame' ? a.detailSaveError : a.argumentsSaveError
  const bridge = action === 'saveGame' ? 'updateGame' : 'setLaunchArguments'
  api[bridge] = async () => { writes++; throw Error('Synthetic persistence refused') }
  await a[action]()
  assert.match(error.value, /Synthetic persistence refused|失败|未.*保存/); assert.equal(groups().length, 1)
  assert.ok(!messages.some(item => item.kind === 'success'))
  api[bridge] = async (...args: any[]) => { writes++; return action === 'saveGame' ? patchGame(args[0], args[1]) : patchArguments(args[0], args[1], args[2]) }
  await a[action]()
  assert.equal(writes, 2); assert.equal(error.value, ''); assert.deepEqual(groups(), [])
})

for (const action of ['savePersonalRecord', 'saveGame', 'saveArguments'] as const) test(`${action} distinguishes an acknowledged write from a failed snapshot read and recovers without another mutation`, { timeout: 10000 }, async t => {
  const { a, api, state, patchGame, patchArguments, groups, changeArguments } = await harness(t); let writes = 0, reads = 0
  if (action === 'saveArguments') changeArguments('["--committed"]'); else a.editor.notes = 'Committed note'
  api.updateGame = async (id: string, patch: any) => { writes++; return patchGame(id, patch) }
  api.setLaunchArguments = async (id: string, installationId: string, args: string[]) => { writes++; return patchArguments(id, installationId, args) }
  api.getState = async () => { reads++; throw Error('Synthetic snapshot unavailable') }
  await a[action]()
  assert.equal(writes, 1); assert.equal(a.detailReadNeeded.value, true)
  const error = action === 'savePersonalRecord' ? a.personalRecordError : action === 'saveGame' ? a.detailSaveError : a.argumentsSaveError
  assert.match(error.value, /已保存|已提交/)
  if (action === 'saveArguments') changeArguments('["--typed-after-failure"]'); else a.editor.notes = 'Typed after failure'
  await a.reloadDetailState(); assert.equal(writes, 1); assert.equal(a.detailReadNeeded.value, true)
  api.getState = async () => { reads++; return state() }; await a.reloadDetailState()
  assert.equal(writes, 1); assert.ok(reads >= 3); assert.equal(a.detailReadNeeded.value, false)
  assert.ok(groups().includes(action === 'saveArguments' ? '启动参数' : '个人记录'))
  assert.equal(action === 'saveArguments' ? a.editor.launchArgs : a.editor.notes, action === 'saveArguments' ? '["--typed-after-failure"]' : 'Typed after failure')
})

test('closing after an acknowledged save requires a successful state read before reopening the actual saved fields', { timeout: 10000 }, async t => {
  const { a, api, state, patchGame, groups } = await harness(t); let writes = 0
  a.editor.notes = 'Actually committed note'; a.editor.categories = 'Actually committed category'
  api.updateGame = async (id: string, patch: any) => { writes++; return patchGame(id, patch) }
  api.getState = async () => { throw Error('Synthetic snapshots unavailable') }
  await a.saveGame()
  assert.equal(a.detailReadNeeded.value, true); assert.deepEqual(groups(), [])
  assert.equal(await a.requestCloseDetail(), false)
  assert.equal(a.detailOpen.value, true); assert.equal(a.detailReadNeeded.value, true); assert.equal(writes, 1)
  assert.equal(a.editor.notes, 'Actually committed note')
  api.getState = async () => state()
  assert.equal(await a.requestCloseDetail(), true)
  assert.equal(a.detailOpen.value, false); assert.equal(a.detailReadNeeded.value, false); assert.equal(writes, 1)
  const actual = a.snapshot.value.games.find((item: LibraryGame) => item.id === 'A')
  assert.equal(actual.notes, 'Actually committed note'); assert.deepEqual(Array.from(actual.categories), ['Actually committed category'])
  await a.showGame(actual)
  assert.equal(a.editor.notes, 'Actually committed note'); assert.equal(a.editor.categories, 'Actually committed category')
  assert.deepEqual(groups(), []); assert.equal(writes, 1)
})

test('closing recovery preserves later input and still requires discard confirmation after reading the committed snapshot', { timeout: 10000 }, async t => {
  const { a, api, state, patchGame, groups, alerts } = await harness(t); let writes = 0
  a.editor.notes = 'Committed before read failure'
  api.updateGame = async (id: string, patch: any) => { writes++; return patchGame(id, patch) }
  api.getState = async () => { throw Error('Synthetic snapshots unavailable') }
  await a.saveGame(); assert.equal(a.detailReadNeeded.value, true)
  a.editor.notes = 'New input after acknowledgement'
  api.getState = async () => state()
  assert.equal(await a.requestCloseDetail(), false)
  assert.equal(a.detailOpen.value, true); assert.equal(a.detailReadNeeded.value, false)
  assert.equal(a.editor.notes, 'New input after acknowledgement'); assert.deepEqual(groups(), ['个人记录'])
  assert.equal(alerts.length, 1); assert.equal(writes, 1)
  assert.equal(a.snapshot.value.games.find((item: LibraryGame) => item.id === 'A').notes, 'Committed before read failure')
})

test('a delayed close recovery cannot close a newer game detail or overwrite its draft', { timeout: 10000 }, async t => {
  const { a, api, games, state, patchGame } = await harness(t), response = deferred<AppSnapshot>(); let writes = 0
  a.editor.notes = 'Acknowledged A'
  api.updateGame = async (id: string, patch: any) => { writes++; return patchGame(id, patch) }
  api.getState = async () => { throw Error('Synthetic initial read unavailable') }
  await a.saveGame(); assert.equal(a.detailReadNeeded.value, true)
  api.getState = () => response.promise
  const closing = a.requestCloseDetail()
  a.detailOpen.value = false; await a.showGame(games[1]!); a.editor.notes = 'New B input'
  response.resolve(state()); assert.equal(await closing, false)
  assert.equal(a.detailOpen.value, true); assert.equal(a.selectedGame.value.id, 'B')
  assert.equal(a.editor.notes, 'New B input'); assert.equal(a.detailReadNeeded.value, false); assert.equal(writes, 1)
})

for (const action of ['saveGame', 'saveArguments'] as const) for (const failure of [false, true]) test(`a late ${action} ${failure ? 'failure' : 'success'} cannot describe or overwrite another detail draft`, { timeout: 10000 }, async t => {
  const { a, api, games, messages, changeArguments } = await harness(t), reply = deferred<LibraryGame>()
  if (action === 'saveGame') a.editor.categories = 'Old category'; else changeArguments('["--old"]')
  api[action === 'saveGame' ? 'updateGame' : 'setLaunchArguments'] = () => reply.promise
  const saving = a[action](); a.detailOpen.value = false; await a.showGame(games[1]!)
  a.editor.notes = 'New B note'; changeArguments('["--new-B"]')
  if (failure) reply.reject(Error('Old detail failure')); else reply.resolve(games[0]!)
  await saving
  assert.equal(a.selectedGame.value.id, 'B'); assert.equal(a.editor.notes, 'New B note'); assert.equal(a.editor.launchArgs, '["--new-B"]')
  assert.equal(a.detailSaveError.value, ''); assert.equal(a.argumentsSaveError.value, ''); assert.equal(messages.length, 0)
})

test('cancelling a preferred-installation change preserves the old argument draft and performs no installation mutation', { timeout: 10000 }, async t => {
  const { a, api, alerts, groups, changeArguments } = await harness(t); let writes = 0
  changeArguments('["--unsaved-primary"]'); api.updateGame = async () => { writes++; throw Error('Cancelled switch must not write') }
  await a.setPreferred('A-secondary')
  assert.equal(writes, 0); assert.equal(alerts.length, 1); assert.equal(a.selectedGame.value.preferredInstallationId, 'A-primary')
  assert.equal(a.editor.launchArgs, '["--unsaved-primary"]'); assert.deepEqual(groups(), ['启动参数'])
})

test('accepting an installation switch does not discard the old draft when its write fails', { timeout: 10000 }, async t => {
  const { a, api, sandbox, groups, changeArguments } = await harness(t)
  changeArguments('["--keep-if-switch-fails"]'); sandbox.useKunAlert = async () => true
  api.updateGame = async () => { throw Error('Synthetic installation switch refused') }
  await a.setPreferred('A-secondary')
  assert.equal(a.selectedGame.value.preferredInstallationId, 'A-primary')
  assert.equal(a.editor.launchArgs, '["--keep-if-switch-fails"]'); assert.deepEqual(groups(), ['启动参数'])
})

test('an accepted installation switch preserves new input made after its discard confirmation', { timeout: 10000 }, async t => {
  const { a, api, sandbox, patchGame, patchArguments, changeArguments } = await harness(t), reply = deferred<LibraryGame>(), writes: any[] = []
  changeArguments('["--discard-confirmed"]'); sandbox.useKunAlert = async () => true
  api.updateGame = () => reply.promise
  const changing = a.setPreferred('A-secondary'); await Promise.resolve(); await Promise.resolve()
  changeArguments('["--typed-after-confirmation"]')
  reply.resolve(patchGame('A', { preferredInstallationId: 'A-secondary' })); await changing
  assert.equal(a.editor.launchArgs, '["--typed-after-confirmation"]')
  api.setLaunchArguments = async (id: string, installationId: string, args: string[]) => { writes.push([id, installationId, plain(args)]); return patchArguments(id, installationId, args) }
  await a.saveArguments()
  assert.deepEqual(writes, [['A', 'A-primary', ['--typed-after-confirmation']]])
})

test('an external preferred-installation change retains the old draft and saving it targets its original installation', { timeout: 10000 }, async t => {
  const { a, api, patchGame, patchArguments, state, server, groups, changeArguments } = await harness(t), writes: any[] = []
  changeArguments('["--unsaved-primary"]')
  patchGame('A', { preferredInstallationId: 'A-secondary' }); a.snapshot.value = state(); await vue.nextTick()
  assert.equal(a.editor.launchArgs, '["--unsaved-primary"]'); assert.ok(groups().includes('启动参数'))
  api.setLaunchArguments = async (id: string, installationId: string, args: string[]) => { writes.push([id, installationId, plain(args)]); return patchArguments(id, installationId, args) }
  await a.saveArguments()
  assert.deepEqual(writes, [['A', 'A-primary', ['--unsaved-primary']]])
  assert.deepEqual(server[0]!.installations.find(item => item.id === 'A-secondary')!.arguments, ['--secondary'])
  assert.deepEqual(JSON.parse(a.editor.launchArgs), ['--secondary'])
  assert.deepEqual(groups(), [])
})

test('read-only recovery after committing an old installation draft loads the current default without repeating the argument write', { timeout: 10000 }, async t => {
  const { a, api, patchGame, patchArguments, state, groups, changeArguments } = await harness(t), writes: any[] = []
  changeArguments('["--committed-primary"]')
  patchGame('A', { preferredInstallationId: 'A-secondary' }); a.snapshot.value = state(); await vue.nextTick()
  assert.equal(a.editor.launchArgs, '["--committed-primary"]')
  api.setLaunchArguments = async (id: string, installationId: string, args: string[]) => { writes.push([id, installationId, plain(args)]); return patchArguments(id, installationId, args) }
  api.getState = async () => { throw Error('Synthetic post-commit snapshot unavailable') }
  await a.saveArguments()
  assert.equal(a.detailReadNeeded.value, true)
  assert.match(a.argumentsSaveError.value, /已保存|已提交/)
  assert.deepEqual(writes, [['A', 'A-primary', ['--committed-primary']]])
  api.getState = async () => state(); await a.reloadDetailState()
  assert.equal(a.detailReadNeeded.value, false)
  assert.equal(a.argumentsSaveError.value, '')
  assert.deepEqual(writes, [['A', 'A-primary', ['--committed-primary']]])
  assert.deepEqual(JSON.parse(a.editor.launchArgs), ['--secondary'])
  assert.deepEqual(groups(), [])
})

test('read-only recovery keeps later input associated with the old installation after its earlier argument write was acknowledged', { timeout: 10000 }, async t => {
  const { a, api, patchGame, patchArguments, state, groups, changeArguments } = await harness(t), writes: any[] = []
  changeArguments('["--committed-primary"]')
  patchGame('A', { preferredInstallationId: 'A-secondary' }); a.snapshot.value = state(); await vue.nextTick()
  api.setLaunchArguments = async (id: string, installationId: string, args: string[]) => { writes.push([id, installationId, plain(args)]); return patchArguments(id, installationId, args) }
  api.getState = async () => { throw Error('Synthetic post-commit snapshot unavailable') }
  await a.saveArguments(); assert.equal(a.detailReadNeeded.value, true)
  changeArguments('["--later-primary"]')
  api.getState = async () => state(); await a.reloadDetailState()
  assert.equal(a.detailReadNeeded.value, false)
  assert.equal(a.editor.launchArgs, '["--later-primary"]')
  assert.deepEqual(groups(), ['启动参数'])
  assert.deepEqual(writes, [['A', 'A-primary', ['--committed-primary']]])
  await a.saveArguments()
  assert.deepEqual(writes, [['A', 'A-primary', ['--committed-primary']], ['A', 'A-primary', ['--later-primary']]])
  assert.deepEqual(JSON.parse(a.editor.launchArgs), ['--secondary'])
  assert.deepEqual(groups(), [])
})

test('changing an installation while a draft is saving cannot route a later argument edit to the new installation', { timeout: 10000 }, async t => {
  const { a, api, patchArguments, patchGame, state, changeArguments } = await harness(t), reply = deferred<LibraryGame>(), writes: any[] = []
  changeArguments('["--submitted-primary"]')
  api.setLaunchArguments = (id: string, installationId: string, args: string[]) => { writes.push([id, installationId, plain(args)]); patchArguments(id, installationId, args); return reply.promise }
  const saving = a.saveArguments(); changeArguments('["--later-primary"]')
  patchGame('A', { preferredInstallationId: 'A-secondary' }); a.snapshot.value = state(); await vue.nextTick()
  reply.resolve(patchGame('A', {})); await saving
  assert.equal(a.editor.launchArgs, '["--later-primary"]')
  api.setLaunchArguments = async (id: string, installationId: string, args: string[]) => { writes.push([id, installationId, plain(args)]); return patchArguments(id, installationId, args) }
  await a.saveArguments()
  assert.deepEqual(writes, [['A', 'A-primary', ['--submitted-primary']], ['A', 'A-primary', ['--later-primary']]])
  assert.deepEqual(JSON.parse(a.editor.launchArgs), ['--secondary'])
})
