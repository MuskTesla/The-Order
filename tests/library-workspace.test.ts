import assert from 'node:assert/strict'
import { test } from 'node:test'
import { filterLibraryGames, folderKey, knownCandidateIds, libraryViewCounts, readLibraryPreferences, saveLibraryPreferences, splitLabels, type LibraryFilters } from '../src/library-workspace'
import type { LibraryGame } from '../src/shared/types'

const filters: LibraryFilters = { query: '', view: 'all', status: 'all', favorites: false, category: '', tag: '', minRating: '' }
const game = (id: string): LibraryGame => ({ id, name: `Personal ${id}`, metadata: { id, title: '中文正式名', originalTitle: '９－ｎｉｎｅ', aliases: ['よつのもの'], coverUrl: null, bannerUrl: null, description: '', releaseDate: null, releaseDatePrecision: null, developers: ['Key Studio'], tags: ['剧情'], ratings: [], characters: [], references: [], webUrl: null, contentRating: null, fetchedAt: new Date().toISOString() }, installations: [{ id: 'first', folderPath: `C:\\Synthetic\\${id}`, executablePath: `C:\\Synthetic\\${id}\\Game.exe`, arguments: [] }], preferredInstallationId: 'first', status: 'planned', personalRating: null, favorite: false, categories: ['Weekend'], customTags: ['Steam'], notes: '', totalPlaySeconds: 0, playCount: 0, sessions: [], addedAt: '2026-10-03T00:00:00.000Z', lastPlayedAt: null })

test('search can find the provider title behind a custom library name', () => {
  assert.equal(filterLibraryGames([game('a')], { ...filters, query: '中文正式名' }, 'added').length, 1)
})
test('full-width and punctuation variants match without changing stored titles', () => {
  const item = game('a')
  assert.equal(filterLibraryGames([item], { ...filters, query: '9-nine' }, 'name').length, 1)
  assert.equal(item.metadata?.originalTitle, '９－ｎｉｎｅ')
})
test('multiple search words can match distinct name, developer and personal-tag fields', () => {
  assert.equal(filterLibraryGames([game('a')], { ...filters, query: '中文 Key Steam' }, 'name').length, 1)
  assert.equal(filterLibraryGames([game('a')], { ...filters, query: 'Key unrelated' }, 'name').length, 0)
})
test('zero-rating filter includes a rated zero and excludes unrated records', () => {
  const zero = { ...game('zero'), personalRating: 0 }
  assert.deepEqual(filterLibraryGames([game('unrated'), zero], { ...filters, minRating: '0' }, 'rating').map(item => item.id), ['zero'])
  assert.deepEqual(filterLibraryGames([zero], { ...filters, minRating: 'invalid' }, 'rating'), [])
})
test('sorting is stable across input ordering and leaves the library snapshot untouched', () => {
  const a = game('a'), b = game('b'), input = [b, a]
  assert.deepEqual(filterLibraryGames(input, filters, 'added').map(item => item.id), ['a', 'b'])
  assert.deepEqual(input.map(item => item.id), ['b', 'a'])
})
test('smart views separate local, wishlist, missing metadata and missing default launcher', () => {
  const local = game('local'), wishlist = { ...game('wish'), installations: [], preferredInstallationId: null }
  const wrongDefault = { ...game('wrong'), installations: [{ ...game('wrong').installations[0]!, executablePath: null }, { ...game('wrong').installations[0]!, id: 'second' }] }
  const covered = { ...local, metadata: { ...local.metadata!, coverUrl: 'https://fixture.invalid/cover.png' } }
  const counts = libraryViewCounts([covered, wishlist, wrongDefault])
  assert.deepEqual(counts, { all: 3, local: 2, wishlist: 1, unmatched: 1, unconfigured: 1 })
  assert.deepEqual(filterLibraryGames([covered, wishlist, wrongDefault], { ...filters, view: 'unconfigured' }, 'name').map(item => item.id), ['wrong'])
})
test('scan duplicates compare Windows case, separators and trailing slashes', () => {
  const item = game('a')
  const candidates = [{ id: 'known', name: 'Alias', folderPath: 'c:/synthetic/A/', executables: [] }, { id: 'new', name: 'a', folderPath: 'D:/other/a', executables: [] }]
  assert.deepEqual([...knownCandidateIds(candidates, [item])], ['known'])
  assert.equal(folderKey('C:\\GAMES\\Title\\'), 'c:/games/title')
})
test('labels trim Chinese and English separators and deduplicate without inventing labels', () => {
  assert.deepEqual(splitLabels(' 周末，Steam,周末\n剧情 '), ['周末', 'Steam', '剧情'])
})
test('UI preferences persist only supported view fields and validate corrupt values', () => {
  let saved = ''
  const storage = { getItem: () => saved, setItem: (_key: string, value: string) => { saved = value } }
  saveLibraryPreferences(storage, { layout: 'list', sort: 'recent', overview: false })
  assert.deepEqual(readLibraryPreferences(storage), { layout: 'list', sort: 'recent', overview: false })
  saved = '{"layout":"unsafe","sort":"unknown","overview":"yes","apiKey":"ignored"}'
  assert.deepEqual(readLibraryPreferences(storage), { layout: 'grid', sort: 'added', overview: true })
  saved = 'not json'; assert.deepEqual(readLibraryPreferences(storage), { layout: 'grid', sort: 'added', overview: true })
})
test('unavailable browser storage never blocks the library', () => {
  const storage = { getItem() { throw new Error('Unavailable') }, setItem() { throw new Error('Unavailable') } }
  assert.deepEqual(readLibraryPreferences(storage), { layout: 'grid', sort: 'added', overview: true })
  assert.doesNotThrow(() => saveLibraryPreferences(storage, { layout: 'list', sort: 'name', overview: true }))
})
