// Runs the actual locked Electron implementation against disposable synthetic profiles.
// This is reference generation only; the native application never loads JavaScript.
import fs from 'node:fs/promises'
import os from 'node:os'
import path from 'node:path'
import vm from 'node:vm'
import { createRequire } from 'node:module'
import { createHash } from 'node:crypto'
import { fileURLToPath } from 'node:url'
import { spawnSync } from 'node:child_process'
import { buildSync } from '../../node_modules/esbuild/lib/main.js'
const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..')
if (!process.versions.electron) {
  const env = { ...process.env, ELECTRON_RUN_AS_NODE: '1' }; delete env.NODE_OPTIONS
  const run = spawnSync(path.join(repo, 'node_modules/electron/dist/electron.exe'), [fileURLToPath(import.meta.url)], { env, stdio: 'inherit', windowsHide: true })
  if (run.error) throw run.error
  process.exit(run.status ?? 1)
}
if (process.platform !== 'win32' || process.versions.electron !== JSON.parse(await fs.readFile(path.join(repo, 'package.json'))).devDependencies.electron) throw Error('Use locked Windows Electron')
const compiled = buildSync({ entryPoints: [path.join(repo, 'electron/library.ts')], bundle: true, write: false, platform: 'node', format: 'cjs' }).outputFiles[0].text
const clock = '2026-10-04T00:00:00.000Z'
class FixedDate extends Date { constructor(...args) { super(...(args.length ? args : [clock])) } static now() { return Date.parse(clock) } }
let sequence = 0
const require = createRequire(import.meta.url), module = { exports: {} }
vm.runInNewContext(compiled, { module, exports: module.exports, require: name => name === 'node:crypto' ? { ...require(name), randomUUID: () => `oracle-${++sequence}` } : require(name), process, Buffer, URL, console, structuredClone, Date: FixedDate })
const { LibraryStore } = module.exports
const normal = JSON.parse(await fs.readFile(path.join(repo, 'native/fixtures/normal/library.json')))
const work = { ...normal.games[0].metadata, id: '200', title: 'Catalog title' }
const entry = (id, extra = {}) => ({ id, name: 'Ordinary title', nameIsCustom: false, metadata: null, installations: [], preferredInstallationId: null, status: 'planned', personalRating: null, favorite: false, categories: [], customTags: [], notes: '', totalPlaySeconds: 0, playCount: 0, sessions: [], addedAt: clock, lastPlayedAt: null, ...extra })
const envelope = games => ({ format: 'kun-gal-manager', version: 1, exportedAt: clock, games })
const session = (id, seconds, extra = {}) => ({ id, seconds, startedAt: clock, endedAt: '2026-10-04T00:10:00.000Z', ...extra })
const install = (id, folder) => ({ id, folderPath: `Z:\\Phase2Synthetic\\${folder}`, executablePath: null, arguments: [' --keep ', '\ud800'] })
const cases = []
const add = (name, games, operation) => cases.push({ name, input: envelope(games), operation })
add('personal-all', [entry('a')], { kind: 'update', id: 'a', patch: { name: '  中文\ud800  ', notes: '  private\nnotes  ', favorite: true, personalRating: 0, status: 'playing', categories: [' a ', 'a', 'b'], customTags: ['x', 'x'] } })
for (const patch of [{ status: 'wrong' }, { favorite: null }, { personalRating: 11 }, { name: '' }, { unknown: 1 }, { preferredInstallationId: 'missing' }]) add(`invalid-patch-${cases.length}`, [entry('a')], { kind: 'update', id: 'a', patch })
add('delete', [entry('a'), entry('b')], { kind: 'delete', id: 'a' })
add('delete-missing', [entry('a')], { kind: 'delete', id: 'missing' })
add('add-virtual', [], { kind: 'add', work })
add('add-existing-keeps-personal', [entry('a', { metadata: work, favorite: true, notes: 'private' })], { kind: 'add', work })
add('import-order-skips', [entry('a', { metadata: work })], { kind: 'import', backup: envelope([entry('a'), entry('b', { metadata: work }), entry('c'), entry('d')]) })
add('import-folder-skip', [entry('a', { installations: [install('i', 'A')] })], { kind: 'import', backup: envelope([entry('b', { installations: [install('j', 'a')] }), entry('c')]) })
add('import-invalid-atomic', [entry('a')], { kind: 'import', backup: envelope([entry('b'), entry('c', { favorite: null })]) })
add('unknown-fields-dropped', [entry('a', { extension: { future: 1 }, installations: [{ ...install('i', 'A'), future: true }] })], { kind: 'update', id: 'a', patch: { notes: 'changed' } })
add('normalization', [entry(' a ', { name: '\ufeff Ordinary \ufeff', categories: [' a ', 'a'], addedAt: '2026-10-04T08:00:00+08:00', installations: [{ ...install(' i ', 'A'), folderPath: ' Z:/Phase2Synthetic/A/./ ' }], preferredInstallationId: ' i ' })], { kind: 'update', id: 'a', patch: {} })
add('preferred-normalized-id', [entry('a', { installations: [install(' i ', 'A')], preferredInstallationId: 'i' })], { kind: 'update', id: 'a', patch: {} })
const merge = (name, left = {}, right = {}, options = {}) => add(name, [entry('a', left), entry('b', { metadata: work, ...right }), entry('tail')], { kind: 'link', id: 'a', work, options })
merge('merge-defaults')
merge('merge-personal', { categories: ['a', 'b'], customTags: ['x'], personalRating: 0, notes: 'left', name: 'Mine', nameIsCustom: true }, { categories: ['b', 'c'], customTags: ['x', 'y'], personalRating: 9, notes: 'right', favorite: true, status: 'completed' })
merge('merge-fallback', {}, { personalRating: 0, notes: 'right', favorite: true, status: 'paused' })
merge('merge-left-wins', { status: 'dropped', notes: 'same', favorite: true, personalRating: 7 }, { status: 'completed', notes: 'same', personalRating: 9 })
merge('merge-installations', { installations: [install('i1', 'A')], preferredInstallationId: 'i1' }, { installations: [install('i2', 'B')], preferredInstallationId: 'i2' })
merge('merge-preferred-fallback', { installations: [install('i1', 'A')] }, { installations: [install('i2', 'B')], preferredInstallationId: 'i2' })
merge('merge-installation-id-conflict', { installations: [install('same', 'A')] }, { installations: [install('same', 'B')] })
merge('merge-history-cumulative', { totalPlaySeconds: 1000, playCount: 7, sessions: [session('s1', 100)] }, { totalPlaySeconds: 2000, playCount: 9, sessions: [session('s2', 200)], lastPlayedAt: clock })
for (const [a, b] of [[100, 200], [200, 100], [0, 100], [100, 0], [0, 0], [100, 100]]) merge(`merge-duplicate-session-${a}-${b}`, { totalPlaySeconds: 1000, playCount: 5, sessions: [session('s', a)] }, { totalPlaySeconds: 2000, playCount: 6, sessions: [session('s', b)] })
merge('merge-session-conflict', { totalPlaySeconds: 100, sessions: [session('s', 100)] }, { totalPlaySeconds: 200, sessions: [session('s', 200, { startedAt: '2026-10-03T00:00:00.000Z' })] })
merge('merge-partial-order', { totalPlaySeconds: 1000, playCount: 10, sessions: [session('s0', 3), session('s1', 1)] }, { totalPlaySeconds: 2000, playCount: 20, sessions: [session('s1', 2), session('s2', 0)] })
merge('merge-reverse-order', { totalPlaySeconds: 1000, sessions: [session('s0', 1), session('s1', 1), session('s2', 1)] }, { totalPlaySeconds: 2000, sessions: [session('s2', 2), session('s1', 2), session('s0', 2)] })
merge('merge-equal-seconds-keeps-whole-source', { totalPlaySeconds: 1000, sessions: [session('s', 100, { endedAt: '2026-10-04T00:05:00.000Z' })] }, { totalPlaySeconds: 2000, sessions: [session('s', 100)] })
add('import-duplicate-session-id', [entry('a')], { kind: 'import', backup: envelope([entry('b', { totalPlaySeconds: 100, sessions: [session('s', 1), session('s', 2)] })]) })
merge('merge-absent-name', { nameIsCustom: undefined, name: 'Legacy name' })
merge('merge-preserve-source', {}, {}, { preserveName: true })
merge('merge-preserve-target', {}, { nameIsCustom: true, name: 'Target name' }, { preserveMergedName: true })
merge('merge-metadata-custom', { metadata: { ...work, id: '100' }, name: 'Different', nameIsCustom: false })
const cover = { id: '44', url: 'https://example.invalid/cover.jpg', width: null, height: null, votes: 0, sexual: null }
merge('merge-cover-fallback', {}, { coverChoice: { workId: '200', cover }, nonPortraitCoverDisplay: 'crop' })
merge('merge-cover-change', { metadata: { ...work, id: '100' }, coverChoice: { workId: '100', cover }, nonPortraitCoverDisplay: null }, { coverChoice: { workId: '200', cover }, nonPortraitCoverDisplay: 'plain' })
merge('merge-local-cover', { localCover: normal.games.find(g => g.localCover).localCover }, { coverChoice: { workId: '200', cover } })
for (const name of ['Voyage REBOOT', 'Voyage HD', 'Voyage FD', 'Voyage after story', 'Voyage 第三章', 'Voyage Part II', 'Voyage v1.2', 'Voyage 2', 'Voyage REMAKE for FHD', 'Voyage special edition', 'Voyage [中文]']) merge(`title-${name}`, { name })
const root = await fs.mkdtemp(path.join(os.tmpdir(), 'the-order-oracle-'))
try {
  for (let index = 0; index < cases.length; index++) {
    const c = cases[index], dir = path.join(root, String(index)); await fs.mkdir(dir)
    await fs.writeFile(path.join(dir, 'library.json'), JSON.stringify(c.input))
    const store = new LibraryStore(dir); await store.initialize(); c.before = store.getGames()
    try {
      const o = c.operation
      c.result = (await (o.kind === 'update' ? store.updateGame(o.id, o.patch) : o.kind === 'delete' ? store.removeGame(o.id) : o.kind === 'add' ? store.addVirtual(o.work) : o.kind === 'import' ? store.importBackup(o.backup) : store.linkMetadata(o.id, o.work, o.options))) ?? null
      c.accepted = true
    } catch (e) { c.accepted = false; c.error = e.message; c.result = null }
    c.after = store.getGames(); c.serialized = await fs.readFile(store.filePath, 'utf8')
    c.backup = await fs.readFile(store.backupPath, 'utf8').catch(() => null)
    const restarted = new LibraryStore(dir); await restarted.initialize()
    if (JSON.stringify(restarted.getGames()) !== JSON.stringify(c.after)) throw Error(`Restart mismatch ${c.name}`)
  }
  const lifecycle = [
    { name: 'latest-request', games: [entry('a')], steps: [{ kind: 'begin', id: 'a', token: 'A' }, { kind: 'begin', id: 'a', token: 'B' }, { kind: 'link', id: 'a', token: 'B', work: { ...work, title: 'newer' } }, { kind: 'link', id: 'a', token: 'A', work }] },
    { name: 'delete-restore', games: [entry('a')], steps: [{ kind: 'begin', id: 'a', token: 'A' }, { kind: 'delete', id: 'a' }, { kind: 'import', backup: envelope([entry('a')]) }, { kind: 'link', id: 'a', token: 'A', work }] },
    { name: 'personal-coexists', games: [entry('a')], steps: [{ kind: 'begin', id: 'a', token: 'A' }, { kind: 'update', id: 'a', patch: { notes: 'private', favorite: true } }, { kind: 'link', id: 'a', token: 'A', work }] },
    { name: 'rename-invalidates', games: [entry('a')], steps: [{ kind: 'begin', id: 'a', token: 'A' }, { kind: 'update', id: 'a', patch: { name: 'Mine' } }, { kind: 'link', id: 'a', token: 'A', work }] },
    { name: 'merge-newer-target', games: [entry('a'), entry('b', { metadata: work })], steps: [{ kind: 'begin', id: 'a', token: 'A' }, { kind: 'begin', id: 'b', token: 'B' }, { kind: 'link', id: 'a', token: 'A', work }, { kind: 'finish', token: 'B' }, { kind: 'link', id: 'a', token: 'A', work }] }
  ]
  for (const c of lifecycle) {
    const dir = path.join(root, c.name); await fs.mkdir(dir); c.input = envelope(c.games); delete c.games
    await fs.writeFile(path.join(dir, 'library.json'), JSON.stringify(c.input))
    const store = new LibraryStore(dir); await store.initialize(); const tokens = {}; c.accepted = []
    for (const s of c.steps) {
      try {
        if (s.kind === 'begin') tokens[s.token] = (await store.beginMetadataRequest(s.id, 'refresh')).token
        else if (s.kind === 'link') await store.linkMetadata(s.id, s.work, { requestToken: tokens[s.token] })
        else if (s.kind === 'delete') await store.removeGame(s.id)
        else if (s.kind === 'import') await store.importBackup(s.backup)
        else if (s.kind === 'update') await store.updateGame(s.id, s.patch)
        else if (s.kind === 'finish') await store.finishMetadataRequest(tokens[s.token])
        c.accepted.push(true)
      } catch { c.accepted.push(false) }
    }
    c.after = store.getGames()
  }
  const recovery = []
  for (const [main, backup] of [['valid', 'valid'], ['corrupt', 'valid'], ['missing', 'valid'], ['corrupt', 'corrupt'], ['missing', 'missing']]) {
    const dir = path.join(root, `recover-${main}-${backup}`); await fs.mkdir(dir)
    for (const [state, file, id] of [[main, 'library.json', 'main'], [backup, 'library.json.bak', 'backup']])
      if (state !== 'missing') await fs.writeFile(path.join(dir, file), state === 'corrupt' ? '{broken' : JSON.stringify(envelope([entry(id)])))
    const store = new LibraryStore(dir), result = { main, backup }
    try { await store.initialize(); result.accepted = true; result.games = store.getGames(); result.recovered = !!store.recoveryNotice }
    catch { result.accepted = false }
    result.filesAfterOpen = (await fs.readdir(dir)).sort(); recovery.push(result)
  }
  const hashes = {}
  for (const file of ['electron/library.ts', 'electron/title-matching.ts', 'electron/local-cover.ts', 'native/scripts/generate-transaction-oracle.mjs']) hashes[file] = createHash('sha256').update((await fs.readFile(path.join(repo, file), 'utf8')).replace(/\r\n/g, '\n')).digest('hex')
  const baseVectors = JSON.parse(await fs.readFile(path.join(repo, 'native/fixtures/compatibility-vectors.json')))
  const validation = ['strings', 'paths', 'numbers'].flatMap(group => baseVectors[group].map(v => {
    const result = { name: `${group}-${v.label}`, json: v.json }
    try { result.games = module.exports.validateLibraryBackup(JSON.parse(v.json)); result.accepted = true }
    catch { result.accepted = false }
    return result
  }))
  // Size-boundary reference: validate compact input directly, without initialize rewriting it.
  const largeInput = envelope(['current-a', 'current-b'].map(id => entry(id, { name: 'Current main', totalPlaySeconds: 101234, playCount: 100017, sessions: Array.from({ length: 100000 }, (_, i) => session(`s-${i}`, 1, { endedAt: clock })) })))
  const largeValidated = module.exports.validateLibraryBackup(largeInput)
  const largeInputReference = { accepted: true, entries: largeValidated.length, sessions: largeValidated.reduce((n, g) => n + g.sessions.length, 0), compactBytes: Buffer.byteLength(JSON.stringify(largeInput)), prettyBytes: Buffer.byteLength(JSON.stringify(largeInput, null, 2)) }
  if (largeInputReference.compactBytes >= 32 * 1024 * 1024 || largeInputReference.prettyBytes <= 32 * 1024 * 1024) throw Error('Large boundary fixture no longer straddles writer limit')
  await fs.writeFile(path.join(repo, 'native/fixtures/transaction-oracle.json'), JSON.stringify({ clock, electron: process.versions.electron, hashes, cases, lifecycle, recovery, validation, largeInputReference }, null, 2) + '\n')
  console.log(JSON.stringify({ largeInputReference }))
  console.log(JSON.stringify({ cases: cases.length, accepted: cases.filter(c => c.accepted).length, rejected: cases.filter(c => !c.accepted).length, restartVerified: cases.length, lifecycle: lifecycle.length, recovery: recovery.length }))
} finally {
  if (path.dirname(root) !== os.tmpdir() || !path.basename(root).startsWith('the-order-oracle-')) throw Error('Unsafe cleanup')
  await fs.rm(root, { recursive: true, force: true })
}
