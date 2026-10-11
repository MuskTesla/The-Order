import assert from 'node:assert/strict'
import { test } from 'node:test'
import { filterLibraryGames, type LibraryFilters } from '../src/library-workspace'
import { createSavedView, describeSavedView, readSavedViews, sameSavedFilters, savedViewsKey, savedViewsLimit, validateSavedViews, writeSavedViews } from '../src/saved-library-views'
import type { LibraryGame } from '../src/shared/types'

const filters: LibraryFilters = { query: '', view: 'all', status: 'all', favorites: false, category: '', tag: '', minRating: '' }
const view = () => createSavedView('周末想读', { ...filters, query: '海岸', view: 'wishlist', status: 'planned', favorites: true, category: '周末', tag: '短篇', minRating: '0' }, 'name', 'weekend')
function storage(initial: string | null = null) {
  let saved = initial, writes = 0
  return { getItem(key: string) { assert.equal(key, savedViewsKey); return saved }, setItem(key: string, value: string) { assert.equal(key, savedViewsKey); saved = value; writes++ }, raw: () => saved, writes: () => writes }
}
const game = (id: string): LibraryGame => ({ id, name: '海岸来信', metadata: null, installations: [], preferredInstallationId: null, status: 'planned', favorite: true, personalRating: 0, categories: ['周末'], customTags: ['短篇'], notes: '', sessions: [], totalPlaySeconds: 0, playCount: 0, addedAt: '2026-10-04T00:00:00Z', lastPlayedAt: null })

test('complete combinations roundtrip as detached declared fields, including zero and wishlist', () => {
  const stored = storage(), original = view()
  const result = writeSavedViews(stored, [{ ...original, notes: 'must not persist', apiKey: 'synthetic credential field' } as typeof original])
  assert.equal(result.ok, true)
  const read = readSavedViews(stored)
  assert.equal(read.error, null)
  assert.deepEqual(read.views, [original])
  assert.doesNotMatch(stored.raw()!, /apiKey|notes|gameIds/)
  original.filters.category = '改变后的原对象'
  assert.equal(read.views[0]!.filters.category, '周末')
  assert.match(describeSavedView(read.views[0]!), /已评分/)
})

test('saved combinations remain dynamic and keep vanished labels without broadening results', () => {
  const saved = view(), a = game('a'), b = game('b')
  assert.deepEqual(filterLibraryGames([a], saved.filters, saved.sort).map(item => item.id), ['a'])
  assert.equal(filterLibraryGames([a, b], saved.filters, saved.sort).length, 2)
  a.categories = []; b.customTags = []
  assert.deepEqual(filterLibraryGames([a, b], saved.filters, saved.sort), [])
  assert.equal(saved.filters.category, '周末')
  assert.equal(saved.filters.tag, '短篇')
})

test('all internal library views and supported sort/status combinations validate', () => {
  for (const current of ['all', 'local', 'wishlist', 'unmatched', 'unconfigured'] as const) assert.equal(createSavedView('筛选', { ...filters, view: current, status: 'completed' }, 'recent', 'view').filters.view, current)
  for (const sort of ['added', 'recent', 'name', 'rating', 'playtime'] as const) assert.equal(createSavedView('筛选', filters, sort, 'view').sort, sort)
  assert.equal(sameSavedFilters(filters, { ...filters }), true)
  assert.equal(sameSavedFilters(filters, { ...filters, minRating: '0' }), false)
})

test('duplicate normalized names or IDs fail before any storage write', () => {
  const stored = storage(), a = createSavedView(' Ａvalon ', filters, 'added', 'a'), b = createSavedView('avalon', filters, 'name', 'b')
  assert.equal(writeSavedViews(stored, [a, b]).ok, false)
  assert.equal(writeSavedViews(stored, [a, { ...view(), id: 'a' }]).ok, false)
  assert.equal(stored.writes(), 0)
})

test('corrupt and unsupported saved records remain untouched instead of silently becoming writable empty data', () => {
  for (const raw of ['bad json', '{}', '{"version":2,"views":[]}', '{"version":1,"views":[{"id":"one"}]}', '{"version":1,"views":{}}', 'x'.repeat(200001)]) {
    const stored = storage(raw), result = readSavedViews(stored)
    assert.ok(result.error)
    assert.deepEqual(result.views, [])
    assert.equal(stored.raw(), raw)
    assert.equal(stored.writes(), 0)
  }
  assert.deepEqual(readSavedViews(storage()), { views: [], error: null })
})

test('unavailable or quota-exceeded storage reports failure without mutating prior records or candidate input', () => {
  const stored = storage(), saved = view()
  assert.equal(writeSavedViews(stored, [saved]).ok, true)
  const original = stored.raw(), input = structuredClone([saved])
  const failed = writeSavedViews({ getItem: stored.getItem, setItem() { throw Error('QuotaExceeded') } }, input)
  assert.equal(failed.ok, false)
  assert.equal(stored.raw(), original)
  assert.deepEqual(input, [saved])
  assert.equal(writeSavedViews(undefined, [saved]).ok, false)
  assert.ok(readSavedViews({ getItem() { throw Error('Unavailable') }, setItem() {} }).error)
})

test('bounded, typed records reject unsupported filters and oversized labels before saving', () => {
  for (const patch of [{ view: 'unknown' }, { status: 'unknown' }, { favorites: 'true' }, { category: 'x'.repeat(201) }, { query: 'x'.repeat(1001) }, { tag: null }, { minRating: '11' }, { minRating: ' ' }, { minRating: 'NaN' }]) assert.throws(() => createSavedView('名称', { ...filters, ...patch } as LibraryFilters, 'name', 'valid'))
  assert.throws(() => createSavedView(' ', filters, 'name', 'valid'))
  assert.throws(() => createSavedView('x'.repeat(81), filters, 'name', 'valid'))
  assert.throws(() => createSavedView('名称', filters, 'name', '../invalid'))
  const list = Array.from({ length: savedViewsLimit }, (_, i) => createSavedView(`筛选${i}`, filters, 'name', `view-${i}`))
  assert.equal(validateSavedViews(list).length, savedViewsLimit)
  assert.throws(() => validateSavedViews([...list, view()]))
})
