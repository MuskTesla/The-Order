import assert from 'node:assert/strict'
import { test } from 'node:test'
import { buildScanRows, commonScanDirectory, displayScanPath, filterScanRows, updateScanSelection, validScanSelection } from '../src/import-workspace'
import type { LibraryGame, ScanCandidate } from '../src/shared/types'

const candidate = (id: string, folderPath: string, name = id): ScanCandidate => ({ id, name, folderPath, executables: [] })
const game = (id: string, folders: string[], name = id): LibraryGame => ({
  id, name, metadata: null, installations: folders.map((folderPath, index) => ({ id: String(index), folderPath, executablePath: null, arguments: [] })),
  preferredInstallationId: folders.length ? '0' : null, status: 'planned', personalRating: null, favorite: false, categories: [], customTags: [], notes: '',
  totalPlaySeconds: 0, playCount: 0, sessions: [], addedAt: '2026-10-04T00:00:00.000Z', lastPlayedAt: null
})

test('exact ownership wins over another parent owner; Windows case and separators compare equally', () => {
  const parent = game('parent', ['D:\\Games\\Game']), exact = game('exact', ['d:/games/game/Bin/']), virtual = game('wish', [])
  const rows = buildScanRows([
    candidate('known', 'D:\\GAMES\\GAME\\bin\\'), candidate('child', 'D:/Games/Game/Tools'),
    candidate('parent', 'D:/Games'), candidate('sibling', 'D:/Games/Game2'), candidate('new', 'E:/Other/Game')
  ], [parent, exact, virtual])
  assert.deepEqual(rows.map(row => [row.candidate.id, row.status, row.owner?.id]), [
    ['known', 'known', 'exact'], ['child', 'conflict', 'parent'], ['parent', 'conflict', 'parent'], ['sibling', 'new', undefined], ['new', 'new', undefined]
  ])
})

test('ownership checks every installation and respects UNC share boundaries', () => {
  const owner = game('multi', ['D:/Games/First', '\\\\Server\\Share\\Game'])
  const rows = buildScanRows([candidate('known', '//server/share/GAME/'), candidate('child', '//Server/Share/Game/bin'), candidate('share', '//Server/Share2/Game')], [owner])
  assert.deepEqual(rows.map(row => row.status), ['known', 'conflict', 'new'])
})

test('NFKC phrase search matches names, full paths and owner names without stitching unrelated words', () => {
  const owner = game('library', ['D:/Library/Nine'], '９－ｎｉｎｅ Avalon')
  const rows = buildScanRows([candidate('known', 'd:\\library\\nine', '目录别名'), candidate('new', 'D:/Library/Other', 'ＡＶＡＬＯＮ Letters')], [owner])
  assert.deepEqual(filterScanRows(rows, '9-nine　AVALON', 'all').map(row => row.candidate.id), ['known'])
  assert.deepEqual(filterScanRows(rows, '9-nine LIBRARY', 'all'), [])
  assert.deepEqual(filterScanRows(rows, 'avalon letters', 'new').map(row => row.candidate.id), ['new'])
  assert.deepEqual(filterScanRows(rows, 'AVALON missing', 'all'), [])
  assert.deepEqual(filterScanRows(rows, 'D:/library', 'known').map(row => row.candidate.id), ['known'])
  assert.equal(filterScanRows(rows, '  ', 'all').length, 2)
  assert.equal(filterScanRows(rows, 'Avalon', 'conflict').length, 0)
})

test('a numbered title is not broadened by dates in the common folder path', () => {
  const rows = buildScanRows([candidate('three', 'D:/2026-10-04/模拟游戏 03', '模拟游戏 03'), candidate('four', 'D:/2026-10-04/模拟游戏 04', '模拟游戏 04')], [])
  assert.deepEqual(filterScanRows(rows, '模拟游戏 04', 'all').map(row => row.candidate.id), ['four'])
  assert.equal(filterScanRows(rows, '2026-10-04', 'all').length, 2, 'Explicit directory search remains supported')
})

test('selection validation rejects blocked and nonexistent IDs while preserving unique requested order', () => {
  const rows = buildScanRows([candidate('new-a', 'D:/New/A'), candidate('known', 'D:/Owned'), candidate('conflict', 'D:/Owned/bin'), candidate('new-b', 'D:/New/B')], [game('owner', ['D:/Owned'])])
  assert.deepEqual(validScanSelection(['new-b', 'known', 'ghost', 'new-a', 'conflict', 'new-b'], rows), ['new-b', 'new-a'])
  assert.deepEqual(validScanSelection(['ghost'], []), [])
})

test('visible selection operations preserve hidden choices and exclude blocked rows', () => {
  const rows = buildScanRows([candidate('hidden', 'D:/New/Hidden'), candidate('visible', 'D:/New/Visible'), candidate('known', 'D:/Owned'), candidate('conflict', 'D:/Owned/bin')], [game('owner', ['D:/Owned'])])
  const visible = [rows[1]!, rows[2]!, rows[3]!, rows[1]!]
  assert.deepEqual(updateScanSelection(['hidden', 'hidden'], visible, 'add'), ['hidden', 'visible'])
  assert.deepEqual(updateScanSelection(['hidden', 'visible'], visible, 'remove'), ['hidden'])
  assert.deepEqual(updateScanSelection(['hidden'], visible, 'only'), ['visible'])
  assert.deepEqual(updateScanSelection(['hidden'], [], 'add'), ['hidden'])
  assert.deepEqual(updateScanSelection(['hidden'], [], 'remove'), ['hidden'])
  assert.deepEqual(updateScanSelection(['hidden'], [], 'only'), [])
})

test('common directory is a whole shared Windows segment with first-path casing and separators', () => {
  assert.equal(commonScanDirectory([candidate('a', 'D:\\Games\\Ａ'), candidate('b', 'd:/games/B/bin')]), 'D:\\Games')
  assert.equal(commonScanDirectory([candidate('a', 'D:/Games/Game'), candidate('b', 'D:/Games/Game2')]), 'D:/Games')
  assert.equal(commonScanDirectory([candidate('a', 'D:/Games/Game'), candidate('b', 'D:/Games/Game/bin')]), 'D:/Games/Game')
})

test('single, empty, different-drive, drive-root and nonabsolute paths have no common directory', () => {
  assert.equal(commonScanDirectory([]), null)
  assert.equal(commonScanDirectory([candidate('a', 'D:/Games/A')]), null)
  assert.equal(commonScanDirectory([candidate('a', 'D:/Games/A'), candidate('b', 'E:/Games/B')]), null)
  assert.equal(commonScanDirectory([candidate('a', 'D:/A'), candidate('b', 'D:/B')]), null)
  assert.equal(commonScanDirectory([candidate('a', 'D:/'), candidate('b', 'D:/Games/B')]), null)
  assert.equal(commonScanDirectory([candidate('a', 'D:Games/A'), candidate('b', 'D:Games/B')]), null)
  assert.equal(commonScanDirectory([candidate('a', 'D:/Games/../Private'), candidate('b', 'D:/Games/B')]), null)
})

test('UNC may share a complete share root, but never only a server or different shares', () => {
  assert.equal(commonScanDirectory([candidate('a', '\\\\Server\\Share\\A'), candidate('b', '//server/share/B')]), '\\\\Server\\Share')
  assert.equal(commonScanDirectory([candidate('a', '//server/ShareA/A'), candidate('b', '//server/ShareB/B')]), null)
  assert.equal(commonScanDirectory([candidate('a', '//server/share/A'), candidate('b', '//other/share/B')]), null)
  assert.equal(commonScanDirectory([candidate('a', '//server'), candidate('b', '//server/share/B')]), null)
  assert.equal(commonScanDirectory([candidate('a', '//?/C:/A'), candidate('b', '//?/C:/B')]), null)
})

test('relative display respects directory boundaries and preserves outside paths', () => {
  assert.equal(displayScanPath('d:\\GAMES\\One\\bin', 'D:/Games'), 'One\\bin')
  assert.equal(displayScanPath('D:/Games2/One', 'D:/Games'), 'D:/Games2/One')
  assert.equal(displayScanPath('E:/Games/One', 'D:/Games'), 'E:/Games/One')
  assert.equal(displayScanPath('D:/Games/../Private', 'D:/Games'), 'D:/Games/../Private')
  assert.equal(displayScanPath('D:/Games/', 'D:/Games'), '.')
  assert.equal(displayScanPath('//server/share/One', '\\\\SERVER\\SHARE'), 'One')
  assert.equal(displayScanPath('//server/share2/One', '//server/share'), '//server/share2/One')
  assert.equal(displayScanPath('//server/share/One', '//server'), '//server/share/One')
  assert.equal(displayScanPath('D:/Games/One', 'D:'), 'D:/Games/One')
  assert.equal(displayScanPath('D:/Games/One/', null), 'D:/Games/One/')
})

test('helpers leave candidate, owner, row and selection inputs untouched', () => {
  const candidates = [candidate('a', 'D:/Games/A', 'Ａ'), candidate('b', 'D:/Games/B')], games = [game('owner', ['D:/Elsewhere'])]
  const beforeCandidates = structuredClone(candidates), beforeGames = structuredClone(games)
  const rows = buildScanRows(candidates, games), beforeRows = structuredClone(rows), ids = ['b', 'a', 'b']
  filterScanRows(rows, 'a', 'new'); validScanSelection(ids, rows)
  for (const operation of ['add', 'remove', 'only'] as const) updateScanSelection(ids, rows, operation)
  commonScanDirectory(candidates); displayScanPath(candidates[0]!.folderPath, 'D:/Games')
  assert.deepEqual(candidates, beforeCandidates); assert.deepEqual(games, beforeGames); assert.deepEqual(rows, beforeRows); assert.deepEqual(ids, ['b', 'a', 'b'])
  assert.equal(rows[0]!.candidate, candidates[0])
})
