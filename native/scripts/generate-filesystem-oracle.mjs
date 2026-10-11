// Executes locked Electron source; virtual trees exercise limits without 50k disk files.
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
 const run = spawnSync(path.join(repo, 'node_modules/electron/dist/electron.exe'), [fileURLToPath(import.meta.url)], { env: { ...process.env, ELECTRON_RUN_AS_NODE: '1' }, windowsHide: true, stdio: 'inherit' })
 if (run.error) throw run.error
 process.exit(run.status ?? 1)
}
const electron = JSON.parse(await fs.readFile(path.join(repo, 'package.json'))).devDependencies.electron
if (process.versions.electron !== electron) throw Error('Locked Electron required')
const require = createRequire(import.meta.url)
function actual(file, fakeFs) {
 const source = buildSync({ entryPoints: [path.join(repo, file)], bundle: true, write: false, platform: 'node', format: 'cjs' }).outputFiles[0].text
 const module = { exports: {} }
 vm.runInNewContext(source, { module, exports: module.exports, require: name => fakeFs && name === 'node:fs' ? { ...require(name), promises: fakeFs } : require(name), Buffer, URL, process, console, structuredClone })
 return module.exports
}
const { LibraryStore, scanFolder } = actual('electron/library.ts')
const { gameCoverUrl } = actual('src/cover-display.ts')
const root = await fs.mkdtemp(path.join(os.tmpdir(), 'the-order-filesystem-oracle-'))
const scans = [], operations = [], covers = [], limits = []
try {
 const gamesRoot = path.join(root, 'games'); await fs.mkdir(gamesRoot)
 for (const [name, files] of [['A', ['A.exe', 'launcher.exe', 'setup.exe']], ['B', ['game.exe']], ['工具', ['uninstall.exe']], ['补丁', ['patch.exe']]]) {
  await fs.mkdir(path.join(gamesRoot, name)); for (const file of files) await fs.writeFile(path.join(gamesRoot, name, file), 'synthetic-not-executable')
 }
 const candidates = await scanFolder(gamesRoot)
 for (const c of candidates) scans.push({ name: c.name, executables: c.executables.map(p => path.basename(p)) })
 const profile = path.join(root, 'profile'); await fs.mkdir(profile); const store = new LibraryStore(profile); await store.initialize()
 operations.push({ name: 'import', result: await store.importCandidates(candidates) })
 operations.push({ name: 'duplicate', result: await store.importCandidates(candidates) })
 const a = store.getGames().find(g => g.name === 'A')
 const extra = path.join(root, 'extra'); await fs.mkdir(extra); await fs.writeFile(path.join(extra, 'game.exe'), 'synthetic-not-executable')
 const attached = await store.attachInstallation(a.id, { id: 'candidate', name: 'extra', folderPath: extra, executables: [path.join(extra, 'game.exe')] })
 operations.push({ name: 'attach-to-unconfigured', count: attached.installations.length, preferredIndex: attached.installations.findIndex(i => i.id === attached.preferredInstallationId), executables: attached.installations.map(i => i.executablePath && path.basename(i.executablePath)) })
 await store.setLaunchArguments(a.id, attached.installations[1].id, ['two words', '', '--flag=中'])
 const changed = await store.updateGame(a.id, { preferredInstallationId: attached.installations[0].id })
 operations.push({ name: 'prefer-and-args', preferredIndex: changed.installations.findIndex(i => i.id === changed.preferredInstallationId), arguments: changed.installations[1].arguments })
 try { await store.attachInstallation(a.id, { id: 'duplicate', name: 'extra', folderPath: extra, executables: [path.join(extra, 'game.exe')] }); throw Error('Unexpected duplicate acceptance') } catch (error) { operations.push({ name: 'duplicate-attach', rejected: !error.message.includes('Unexpected') }) }
 const base = JSON.parse(await fs.readFile(path.join(repo, 'native/fixtures/normal/library.json'))).games[0]
 for (const spec of [
  { name: 'local-first', local: true, choice: 'safe', work: 'same', explicit: true },
  { name: 'choice', choice: 'safe', work: 'same', explicit: true },
  { name: 'stale-choice', choice: 'safe', work: 'other', explicit: true },
  { name: 'explicit-choice-hidden', choice: 'explicit', work: 'same', explicit: false },
  { name: 'explicit-default-hidden', choice: 'explicit', work: 'same', explicit: false, defaultSame: true },
  { name: 'default', explicit: true }, { name: 'no-metadata', none: true, explicit: true }
 ]) {
  const game = { ...base, metadata: spec.none ? null : { ...base.metadata, id: 'same', coverUrl: spec.defaultSame ? 'https://example.invalid/chosen.jpg' : 'https://example.invalid/default.jpg', covers: [] }, localCover: spec.local ? { dataUrl: 'data:image/jpeg;base64,/9j/2Q==', width: 1, height: 1 } : undefined, coverChoice: spec.choice ? { workId: spec.work, cover: { id: 'c', url: 'https://example.invalid/chosen.jpg', width: 100, height: 150, votes: 0, sexual: spec.choice === 'explicit' ? 'explicit' : 'safe' } } : undefined }
  covers.push({ name: spec.name, game, includeExplicit: spec.explicit, selected: gameCoverUrl(game, spec.explicit) })
 }
 const fakeRoot = 'Z:\\SyntheticOracle'
 const dirent = (name, directory) => ({ name, isFile: () => !directory, isDirectory: () => directory, isSymbolicLink: () => false })
 for (const mode of ['depth', 'directories', 'entries', 'candidates']) {
  let calls = 0
  const fake = { ...fs, realpath: async p => p, stat: async () => ({ isDirectory: () => true }), readdir: async p => {
   calls++
   if (mode === 'depth') return p.split('\\').length < 9 ? [dirent('child', true)] : [dirent('game.exe', false)]
   if (p === fakeRoot) return Array.from({ length: mode === 'directories' ? 5001 : mode === 'entries' ? 50001 : 2001 }, (_, i) => dirent(String(i), mode !== 'entries'))
   return mode === 'candidates' ? [dirent('game.exe', false)] : []
  } }
  try { const result = await actual('electron/library.ts', fake).scanFolder(fakeRoot); limits.push({ name: mode, accepted: true, count: result.length, calls }) }
  catch { limits.push({ name: mode, accepted: false, calls }) }
  if (calls === 0) throw Error(`Oracle filesystem was not exercised: ${mode}`)
 }
 const hashes = {}
 for (const file of ['electron/library.ts', 'src/cover-display.ts', 'native/scripts/generate-filesystem-oracle.mjs']) hashes[file] = createHash('sha256').update((await fs.readFile(path.join(repo, file), 'utf8')).replace(/\r\n/g, '\n')).digest('hex')
 await fs.writeFile(path.join(repo, 'native/fixtures/filesystem-oracle.json'), JSON.stringify({ electron, hashes, scans, operations, covers, limits }, null, 2) + '\n')
 console.log(JSON.stringify({ scans: scans.length, operations: operations.length, covers: covers.length, limits }))
} finally {
 if (path.dirname(root) !== os.tmpdir() || !path.basename(root).startsWith('the-order-filesystem-oracle-')) throw Error('Unsafe cleanup')
 await fs.rm(root, { recursive: true, force: true })
}
