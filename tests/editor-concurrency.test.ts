import assert from 'node:assert/strict'
import { test } from 'node:test'
import { promises as fs } from 'node:fs'
import vm from 'node:vm'
import { transform } from 'esbuild'
import * as vue from 'vue'
import * as libraryWorkspace from '../src/library-workspace'
import * as libraryInsights from '../src/library-insights'

async function harness() {
  const source = await fs.readFile(new URL('../src/App.vue', import.meta.url), 'utf8')
  const script = source.split('<script setup lang="ts">')[1]!.split('</script>')[0]!
  const sandbox: any = { ...vue, ...libraryWorkspace, ...libraryInsights, onMounted: () => {}, onUnmounted: () => {}, isPreview: false, useKunMessage: () => {}, useKunAlert: async () => true, api: {}, document: { documentElement: { classList: { toggle() {} } } }, matchMedia: () => ({ matches: false }) }
  for (const line of script.split('\n').filter(line => /^import \{/.test(line))) for (const name of line.match(/^import \{([^}]+)\}/)![1]!.split(',').map(name => name.trim())) if (!(name in sandbox)) sandbox[name] = name
  const expose = '\nglobalThis.audit = { snapshot, editor, showGame, setPreferred, pickExecutable, saveArguments, saveGame, reload, markNameEdited: typeof markNameEdited === "function" ? markNameEdited : undefined, markArgumentsEdited: typeof markArgumentsEdited === "function" ? markArgumentsEdited : undefined };'
  const compiled = await transform(script.replace(/^import .*$/gm, '') + expose, { loader: 'ts', format: 'cjs', target: 'es2022' })
  vm.createContext(sandbox); vm.runInContext(compiled.code, sandbox)
  const a = sandbox.audit
  const game = { id: 'game', name: 'Name', metadata: { id: 'work', title: 'Name' }, status: 'planned', personalRating: null, categories: [], customTags: [], notes: 'saved', installations: [{ id: 'A', arguments: ['--A'] }, { id: 'B', arguments: ['--B'] }], preferredInstallationId: 'A' }
  const state = (games: any[]) => ({ games, settings: { baseUrl: '', includeNsfw: false, hasApiKey: false } })
  a.snapshot.value = state([game]); await a.showGame(game)
  return { a, api: sandbox.api, game, state }
}

test('a late preference reload cannot replace newly entered arguments for the displayed installation', async () => {
  const { a, api, game, state } = await harness()
  a.editor.notes = 'UNSAVED note'; a.editor.name = 'UNSAVED name'
  let release!: (value: any) => void
  const waiting = new Promise(resolve => { release = resolve })
  const updated = { ...game, preferredInstallationId: 'B' }
  api.updateGame = async () => updated; api.getState = () => waiting
  const pending = a.setPreferred('B'); await Promise.resolve(); await Promise.resolve()
  a.snapshot.value = state([updated]); await vue.nextTick()
  a.editor.launchArgs = '["--NEW-B-DRAFT"]'; a.markArgumentsEdited?.()
  release(state([updated])); await pending
  assert.equal(a.editor.launchArgs, '["--NEW-B-DRAFT"]')
  assert.equal(a.editor.notes, 'UNSAVED note'); assert.equal(a.editor.name, 'UNSAVED name')
  let saved: any
  api.setLaunchArguments = async (...args: any[]) => { saved = args; return updated }
  api.getState = async () => state([updated])
  await a.saveArguments(); assert.equal(saved[1], 'B'); assert.deepEqual(JSON.parse(JSON.stringify(saved[2])), ['--NEW-B-DRAFT'])
})

test('ordinary Save omits untouched name but editing it back to the same value explicitly confirms it', async () => {
  const { a, api, game, state } = await harness()
  const patches: any[] = []
  api.updateGame = async (_id: string, patch: any) => { patches.push(patch); return game }
  api.getState = async () => state([game])
  a.editor.notes = 'Only notes'; await a.saveGame()
  assert.equal('name' in patches[0], false)
  a.editor.name = 'Temporary'; a.markNameEdited?.(); a.editor.name = game.name; a.markNameEdited?.()
  await a.saveGame(); assert.equal(patches[1].name, game.name)
})

test('a delayed installation action cannot write another detail or its argument draft', async () => {
  const { a, api, game, state } = await harness()
  let release!: (value: any) => void
  api.pickExecutable = () => new Promise(resolve => { release = resolve })
  const other = { ...game, id: 'other', name: 'Other' }
  api.getState = async () => state([game, other])
  const pending = a.pickExecutable()
  a.snapshot.value = state([game, other]); await a.showGame(other)
  a.editor.notes = 'Other draft'; a.editor.launchArgs = '["--OTHER-DRAFT"]'; a.markArgumentsEdited?.()
  release({ ...game, preferredInstallationId: 'B' }); await pending
  assert.equal(a.editor.notes, 'Other draft'); assert.equal(a.editor.launchArgs, '["--OTHER-DRAFT"]')
})


test('an older A reload cannot roll back a newer B snapshot or route its draft to A', async () => {
  const { a, api, game, state } = await harness()
  let release!: (value: any) => void
  const older = new Promise(resolve => { release = resolve })
  let reads = 0
  const updated = { ...game, preferredInstallationId: 'B', notes: 'Newest server note' }
  api.getState = () => ++reads === 1 ? older : Promise.resolve(state([updated]))
  api.updateGame = async () => updated
  const pendingOld = a.reload()
  await a.setPreferred('B')
  a.editor.launchArgs = '["--NEW-B-DRAFT"]'; a.markArgumentsEdited()
  release(state([game])); await pendingOld
  assert.equal(a.snapshot.value.games[0].preferredInstallationId, 'B')
  assert.equal(a.snapshot.value.games[0].notes, 'Newest server note')
  assert.equal(a.editor.launchArgs, '["--NEW-B-DRAFT"]')
  let saved: any
  api.setLaunchArguments = async (...args: any[]) => { saved = args; return updated }
  await a.saveArguments(); assert.equal(saved[1], 'B')
})

test('an obsolete reload failure is ignored after a newer successful snapshot', async () => {
  const { a, api, game, state } = await harness()
  let reject!: (error: Error) => void
  const older = new Promise((_resolve, failure) => { reject = failure })
  let reads = 0
  api.getState = () => ++reads === 1 ? older : Promise.resolve(state([{ ...game, preferredInstallationId: 'B' }]))
  const pendingOld = a.reload(); await a.reload()
  reject(new Error('Obsolete read failure'))
  await assert.doesNotReject(pendingOld)
  assert.equal(a.snapshot.value.games[0].preferredInstallationId, 'B')
})

test('a newest reload failure remains visible and does not revive an older snapshot', async () => {
  const { a, api, game, state } = await harness()
  let release!: (value: any) => void
  const older = new Promise(resolve => { release = resolve })
  let reads = 0
  api.getState = () => ++reads === 1 ? older : Promise.reject(new Error('Latest read failure'))
  const pendingOld = a.reload()
  await assert.rejects(a.reload(), /Latest read failure/)
  release(state([{ ...game, preferredInstallationId: 'B' }])); await pendingOld
  assert.equal(a.snapshot.value.games[0].preferredInstallationId, 'A')
})
