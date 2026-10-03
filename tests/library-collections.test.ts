import assert from 'node:assert/strict'
import { test } from 'node:test'
import { findCollectionTags, findLibraryCollections, getLibraryCollections } from '../src/library-collections'
import type { LibraryGame } from '../src/shared/types'

function game(id: string, patch: Partial<LibraryGame> = {}): LibraryGame {
  return { id, name: id, nameIsCustom: true, metadata: null, installations: [], preferredInstallationId: null,
    status: 'planned', personalRating: null, favorite: false, categories: [], customTags: [], notes: '',
    totalPlaySeconds: 0, playCount: 0, sessions: [], addedAt: '2026-10-04T00:00:00.000Z', lastPlayedAt: null, ...patch }
}

test('overlapping and duplicate categories count a work once in each collection without editing its data', () => {
  const games = [game('a', { categories: ['周末', '周末', '重读'], status: 'playing', favorite: true }), game('b', { categories: ['周末'], status: 'completed' })]
  const original = structuredClone(games)
  const { categories, favorites } = getLibraryCollections(games)
  const weekend = categories.find(collection => collection.name === '周末')!
  assert.deepEqual(weekend.games.map(item => item.id), ['a', 'b'])
  assert.equal(weekend.playing, 1)
  assert.equal(weekend.completed, 1)
  assert.deepEqual(favorites.games.map(item => item.id), ['a'])
  assert.deepEqual(games, original)
})

test('a collection can be found by its label, personal title or remote alias with width and punctuation normalization', () => {
  const work = game('custom', { name: '个人名称', categories: ['周末慢读'], metadata: {
    id: 'work', title: '资料源作品名', originalTitle: '９－ｎｉｎｅ', aliases: ['Sea Breeze'], coverUrl: null,
    bannerUrl: null, description: '', releaseDate: null, releaseDatePrecision: null, developers: [], tags: [],
    ratings: [], characters: [], references: [], webUrl: null, contentRating: null, fetchedAt: '',
  } })
  const categories = getLibraryCollections([work, game('other', { categories: ['其他'] })]).categories
  for (const query of ['周末', '个人名称', '资料源', '9-nine', 'SEA breeze', '周末 nine']) {
    assert.deepEqual(findLibraryCollections(categories, query, 'name').map(item => item.name), ['周末慢读'])
  }
  assert.deepEqual(findLibraryCollections(categories, '周末 不存在', 'name'), [])
})

test('count and recent ordering have stable name ties and leave the original groups and games unchanged', () => {
  const games = [game('old', { categories: ['B', 'A'], addedAt: '2026-01-01T00:00:00Z' }), game('new', { categories: ['C', 'B'], addedAt: '2026-10-04T00:00:00Z' })]
  const categories = getLibraryCollections(games).categories
  const original = structuredClone(categories)
  assert.deepEqual(findLibraryCollections(categories, '', 'count').map(item => item.name), ['B', 'A', 'C'])
  assert.deepEqual(findLibraryCollections(categories, '', 'recent').map(item => item.name), ['B', 'C', 'A'])
  assert.deepEqual(findLibraryCollections([...categories].reverse(), '', 'name').map(item => item.name), ['A', 'B', 'C'])
  assert.deepEqual(categories, original)
})

test('an empty library and blank legacy categories do not invent groups or hide favorite works', () => {
  assert.deepEqual(getLibraryCollections([]).categories, [])
  const groups = getLibraryCollections([game('uncategorized', { categories: [''], favorite: true })])
  assert.deepEqual(groups.categories, [])
  assert.equal(groups.favorites.games.length, 1)
})

test('all tags remain searchable beyond the old 80 item limit, without changing stored labels', () => {
  const tags = Array.from({ length: 95 }, (_, index) => `标签${String(index + 1).padStart(3, '0')}`)
  const input = [...tags, '标签095', '９－ｎｉｎｅ']
  const original = [...input]
  assert.equal(findCollectionTags(input, '').length, 96)
  assert.deepEqual(findCollectionTags(input, '标签095'), ['标签095'])
  assert.deepEqual(findCollectionTags(input, '9-nine'), ['９－ｎｉｎｅ'])
  assert.deepEqual(input, original)
})
