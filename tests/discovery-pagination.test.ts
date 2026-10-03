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

async function harness() {
  const source = await fs.readFile(new URL('../src/App.vue', import.meta.url), 'utf8')
  const script = source.split('<script setup lang="ts">')[1]!.split('</script>')[0]!
  const notices: string[] = []
  const sandbox: any = { ...vue, ...libraryWorkspace, ...importWorkspace, ...libraryInsights, ...savedLibraryViews, onMounted: () => {}, onUnmounted: () => {}, isPreview: false,
    useKunMessage: (message: string) => notices.push(message), useKunAlert: async () => true, api: {},
    document: { documentElement: { classList: { toggle() {} } } }, matchMedia: () => ({ matches: false }) }
  for (const line of script.split('\n').filter(line => /^import \{/.test(line))) {
    for (const name of line.match(/^import \{([^}]+)\}/)![1]!.split(',').map(name => name.trim())) if (!(name in sandbox)) sandbox[name] = name
  }
  const expose = '\nglobalThis.audit={snapshot,searchQuery,submittedQuery,searchResults,searchCursor,hasSearched,searchError,busy,search};'
  const compiled = await transform(script.replace(/^import .*$/gm, '') + expose, { loader: 'ts', format: 'cjs', target: 'es2022' })
  vm.createContext(sandbox); vm.runInContext(compiled.code, sandbox)
  const state = sandbox.audit
  state.snapshot.value = { games: [], settings: { baseUrl: 'https://fixture.invalid/v2/', includeNsfw: false, hasApiKey: false } }
  await vue.nextTick()
  state.searchQuery.value = 'first'
  return { state, api: sandbox.api, notices }
}
const work = (id: string) => ({ id, title: id })
function deferred() {
  let resolve!: (value: any) => void, reject!: (error: unknown) => void
  const promise = new Promise((yes, no) => { resolve = yes; reject = no })
  return { promise, resolve, reject }
}
const ids = (state: any) => [...state.searchResults.value].map((work: any) => work.id)

test('discovery deduplicates within and between pages and blocks double loading', async () => {
  const { state, api } = await harness(), page = deferred(), calls: any[] = []
  api.searchWorks = (query: string, cursor: string) => { calls.push([query, cursor]); return calls.length === 1 ? Promise.resolve({ items: [work('1'), work('1')], nextCursor: 'cur_next' }) : page.promise }
  await state.search(); assert.deepEqual(ids(state), ['1'])
  const pending = state.search(true); await state.search(true); await state.search()
  assert.equal(calls.length, 2)
  page.resolve({ items: [work('1'), work('2'), work('2')], nextCursor: null }); await pending
  assert.deepEqual(ids(state), ['1', '2']); assert.equal(state.searchCursor.value, null)
  assert.equal(state.busy.has('search'), false)
})

test('a page failure preserves displayed items and its previous cursor so retry fetches exactly that page', async () => {
  const { state, api } = await harness(), calls: any[] = []
  api.searchWorks = async (query: string, cursor: string) => { calls.push([query, cursor]); if (calls.length === 2) throw Error('Synthetic page failure'); return { items: [work(String(calls.length))], nextCursor: 'cur_next' } }
  await state.search(); await state.search(true)
  assert.deepEqual(ids(state), ['1']); assert.equal(state.searchCursor.value, 'cur_next')
  assert.ok(state.searchError.value); assert.equal(state.busy.has('search'), false)
  api.searchWorks = async (query: string, cursor: string) => { calls.push([query, cursor]); return { items: [work('2')], nextCursor: null } }
  await state.search(true)
  assert.equal(calls[2][0], 'first'); assert.equal(calls[2][1], 'cur_next')
  assert.deepEqual(ids(state), ['1', '2']); assert.equal(state.searchError.value, '')
})

test('an empty advancing page remains traversable while a repeated cursor terminates loading', async () => {
  const { state, api, notices } = await harness(), cursors: any[] = []
  const pages = [{ items: [work('1')], nextCursor: 'cur_one' }, { items: [], nextCursor: 'cur_two' }, { items: [work('1'), work('2')], nextCursor: 'cur_one' }]
  api.searchWorks = async (_query: string, cursor: string) => { cursors.push(cursor); return pages.shift() }
  await state.search(); await state.search(true)
  assert.equal(state.searchCursor.value, 'cur_two'); assert.match(notices.at(-1)!, /没有新增/)
  await state.search(true); assert.equal(state.searchCursor.value, null); assert.deepEqual(ids(state), ['1', '2'])
  assert.match(notices.at(-1)!, /停止加载/)
  await state.search(true); assert.equal(cursors.length, 3)
})

test('a duplicate-only same-cursor page stops immediately and a fresh search may use that cursor again', async () => {
  const { state, api } = await harness()
  api.searchWorks = async () => ({ items: [work('1')], nextCursor: 'cur_same' })
  await state.search(); await state.search(true)
  assert.equal(state.searchCursor.value, null); assert.deepEqual(ids(state), ['1'])
  await state.search(); assert.equal(state.searchCursor.value, 'cur_same')
})

test('typing away and back invalidates a late first page without automatically sending requests', async () => {
  const { state, api } = await harness(), response = deferred(); let calls = 0
  api.searchWorks = () => { calls++; return response.promise }
  const pending = state.search(); state.searchQuery.value = 'second'; state.searchQuery.value = 'first'
  response.resolve({ items: [work('obsolete')], nextCursor: 'cur_obsolete' }); await pending
  assert.equal(calls, 1); assert.deepEqual(ids(state), []); assert.equal(state.searchCursor.value, null)
  assert.equal(state.hasSearched.value, false); assert.equal(state.busy.has('search'), false)
})

for (const outcome of ['success', 'error'] as const) test(`a stale page ${outcome} cannot replace a newer query or clear its loading state`, async () => {
  const { state, api, notices } = await harness(), old = deferred(), current = deferred()
  let calls = 0
  api.searchWorks = () => ++calls === 1 ? Promise.resolve({ items: [work('first')], nextCursor: 'cur_old' }) : calls === 2 ? old.promise : current.promise
  await state.search(); const oldPending = state.search(true)
  state.searchQuery.value = 'second'; const currentPending = state.search()
  if (outcome === 'success') old.resolve({ items: [work('obsolete')], nextCursor: 'cur_old' }); else old.reject(Error('Old page failure'))
  await oldPending
  assert.deepEqual(ids(state), []); assert.equal(state.busy.has('search'), true); assert.equal(state.searchError.value, ''); assert.equal(notices.length, 0)
  current.resolve({ items: [work('second')], nextCursor: 'cur_new' }); await currentPending
  assert.deepEqual(ids(state), ['second']); assert.equal(state.searchCursor.value, 'cur_new'); assert.equal(state.submittedQuery.value, 'second')
})

test('changing saved discovery filters invalidates an in-flight page and never reuses its old cursor', async () => {
  const { state, api } = await harness(), old = deferred(), calls: any[] = []
  api.searchWorks = (query: string, cursor: string) => { calls.push([query, cursor]); return cursor ? old.promise : Promise.resolve({ items: [work(String(calls.length))], nextCursor: 'cur_old' }) }
  await state.search(); const pending = state.search(true)
  state.snapshot.value = { ...state.snapshot.value, settings: { ...state.snapshot.value.settings, includeNsfw: true } }
  await vue.nextTick()
  old.resolve({ items: [work('hidden-old')], nextCursor: 'cur_late' }); await pending
  assert.deepEqual(ids(state), []); assert.equal(state.searchCursor.value, null)
  await state.search(); assert.equal(calls.at(-1)[1], undefined); assert.deepEqual(ids(state), ['3'])
})
