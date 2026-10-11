import fs from 'node:fs/promises'
import path from 'node:path'
import os from 'node:os'
import vm from 'node:vm'
import { createRequire } from 'node:module'
import { fileURLToPath } from 'node:url'
import { spawnSync } from 'node:child_process'
import { createHash } from 'node:crypto'
import { buildSync } from '../../node_modules/esbuild/lib/main.js'
const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..')
if (!process.versions.electron) {
 const env = { ...process.env, ELECTRON_RUN_AS_NODE: '1' }; delete env.NODE_OPTIONS
 const run = spawnSync(path.join(repo, 'node_modules/electron/dist/electron.exe'), [fileURLToPath(import.meta.url)], { env, windowsHide: true, stdio: 'inherit' })
 if (run.error) throw run.error
 process.exit(run.status ?? 1)
}
const expected = JSON.parse(await fs.readFile(path.join(repo, 'package.json'))).devDependencies.electron
if (process.versions.electron !== expected) throw Error('Use locked Electron')
const require = createRequire(import.meta.url)
function actual(file) {
 const source = buildSync({ entryPoints: [path.join(repo, file)], bundle: true, write: false, platform: 'node', format: 'cjs' }).outputFiles[0].text
 const module = { exports: {} }; vm.runInNewContext(source, { module, exports: module.exports, require, Buffer, URL, process, console, structuredClone })
 return module.exports
}
const { filterLibraryGames, splitLabels } = actual('src/library-workspace.ts'), { LibraryStore } = actual('electron/library.ts')
const clock = '2026-10-04T00:00:00.000Z'
const base = JSON.parse(await fs.readFile(path.join(repo, 'native/fixtures/normal/library.json'))).games[0]
const games = Array.from({ length: 12 }, (_, i) => ({ ...base, id: `entry-${i}`, name: ['中文正式名', '９－ｎｉｎｅ', 'Alpha', 'alpha', '苹果', '重逢', 'İSTANBUL', 'ΟΣ', 'Part 10', 'Part 2', '零评分', '无评分'][i], nameIsCustom: true, metadata: i % 3 ? { ...base.metadata, id: String(100 + i), title: '中文目录名', originalTitle: 'Original', aliases: ['よつのもの', '９－ｎｉｎｅ'], developers: ['Key Studio'], tags: ['剧情'], coverUrl: i % 2 ? null : 'https://example.invalid/cover.png' } : null, localCover: undefined, coverChoice: undefined, installations: i % 3 ? [{ id: `install-${i}`, folderPath: `Z:\\Fixture\\${i}`, executablePath: i % 2 ? null : `Z:\\Fixture\\${i}\\Game.exe`, arguments: [] }] : [], preferredInstallationId: i % 3 ? `install-${i}` : null, status: ['planned', 'playing', 'completed', 'paused', 'dropped'][i % 5], personalRating: i === 10 ? 0 : i === 11 ? null : i, favorite: i % 2 === 0, categories: ['周末', i % 2 ? 'Case' : 'case'], customTags: ['Steam', '个人'], totalPlaySeconds: 1000 + i, playCount: 2, sessions: [], addedAt: `2026-10-${String(i + 1).padStart(2, '0')}T00:00:00.000Z`, lastPlayedAt: i % 2 ? clock : null }))
const defaults = { query: '', view: 'all', status: 'all', favorites: false, category: '', tag: '', minRating: '' }
const queries = []
function query(name, fields = {}, sort = 'added') { const filters = { ...defaults, ...fields }; queries.push({ name, filters, sort, ids: filterLibraryGames(games, filters, sort).map(g => g.id) }) }
for (const sort of ['added', 'recent', 'name', 'rating', 'playtime']) query(`sort-${sort}`, {}, sort)
for (const view of ['all', 'local', 'wishlist', 'unmatched', 'unconfigured']) query(`view-${view}`, { view })
for (const status of ['planned', 'playing', 'completed', 'paused', 'dropped']) query(`status-${status}`, { status })
for (const value of ['中文目录名', '9-nine', '中文 Key Steam', 'Key unrelated', 'İSTANBUL', 'istanbul', 'ΟΣ', 'ος', 'よつ', 'Z Fixture 2', '!!!', 'Alpha\ufeffSteam', '个人\n周末', 'not present']) query(`search-${queries.length}`, { query: value }, 'name')
for (const field of [{ favorites: true }, { category: 'Case' }, { category: 'case' }, { tag: '剧情' }, { tag: 'Steam' }, { minRating: '0' }, { minRating: '8' }, { minRating: 'invalid' }, { view: 'local', status: 'playing', favorites: false, tag: '剧情' }]) query(`filter-${queries.length}`, field)
const labels = [' 周末，Steam,周末\n剧情 ', 'Case,case,Case', ',，\n', '\ufeffhello\ufeff,hello', 'a\u0085,b'].map(input => ({ input, result: splitLabels(input) }))
const batches = [
 { name: 'append-remove', ids: ['entry-0','entry-1'], patch: { status: 'completed', favorite: true, addCategories: [' New ', 'New'], removeCategories: ['周末'], addTags: [' A ', 'a'], removeTags: ['Steam'] } },
 { name: 'order-case', ids: ['entry-0','entry-1'], patch: { addCategories: ['case','Case'], addTags: ['个人', 'new'] } },
 { name: 'conflict-label', ids: ['entry-0'], patch: { addTags: [' x '], removeTags: ['x'] } },
 { name: 'empty-label', ids: ['entry-0'], patch: { addTags: [' '] } },
 { name: 'missing-id', ids: ['entry-0','missing'], patch: { favorite: false } },
 { name: 'duplicate-id', ids: ['entry-0','entry-0'], patch: { status: 'playing' } },
 { name: 'empty-patch', ids: ['entry-0'], patch: {} },
 { name: 'invalid-status', ids: ['entry-0'], patch: { status: 'invalid' } }
]
const root = await fs.mkdtemp(path.join(os.tmpdir(), 'the-order-ui-oracle-'))
try {
 for (const b of batches) {
  const dir = path.join(root, b.name); await fs.mkdir(dir)
  await fs.writeFile(path.join(dir, 'library.json'), JSON.stringify({ format: 'kun-gal-manager', version: 1, exportedAt: clock, games }))
  const store = new LibraryStore(dir); await store.initialize()
  try { await store.updateGames(b.ids, b.patch); b.accepted = true } catch { b.accepted = false }
  b.after = store.getGames()
 }
 const hashes = {}
 for (const file of ['src/library-workspace.ts','src/library-insights.ts','electron/library.ts','native/scripts/generate-offline-ui-oracle.mjs']) hashes[file] = createHash('sha256').update((await fs.readFile(path.join(repo,file),'utf8')).replace(/\r\n/g,'\n')).digest('hex')
 await fs.writeFile(path.join(repo, 'native/fixtures/offline-ui-oracle.json'), JSON.stringify({ electron: expected, hashes, games, queries, labels, batches }, null, 2) + '\n')
 console.log(JSON.stringify({ queries: queries.length, labels: labels.length, batches: batches.length }))
} finally {
 if (path.dirname(root) !== os.tmpdir() || !path.basename(root).startsWith('the-order-ui-oracle-')) throw Error('Unsafe cleanup')
 await fs.rm(root, { recursive: true, force: true })
}
