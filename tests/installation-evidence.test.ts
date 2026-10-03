import assert from 'node:assert/strict'
import { promises as fs } from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { test } from 'node:test'
import { attachmentReferenceConflict, inspectInstallationAttachment } from '../electron/installation-attachment'
import type { GameIdentity } from '../electron/game-identification'
import type { LibraryGame, WorkMetadata, WorkReference } from '../src/shared/types'

const work = (references: WorkReference[]): WorkMetadata => ({ id: 'synthetic-work', title: 'Synthetic', originalTitle: '', aliases: [], coverUrl: null, bannerUrl: null, description: '', releaseDate: null, releaseDatePrecision: null, developers: [], tags: [], ratings: [], characters: [], references, webUrl: null, contentRating: null, fetchedAt: '2026-10-03T00:00:00.000Z' })
const game = (references: WorkReference[] = [{ source: 'steam', id: '100' }]): LibraryGame => ({ id: 'synthetic-game', name: 'Confirmed name', metadata: work(references), installations: [], preferredInstallationId: null, status: 'planned', personalRating: 0, favorite: false, categories: [], customTags: [], notes: 'Saved notes', totalPlaySeconds: 0, playCount: 0, sessions: [], addedAt: '2026-10-03T00:00:00.000Z', lastPlayedAt: null })
const identity = (values: Array<{ value: string; source: 'sidecar' | 'steam-file' | 'steam-config' | 'title-index' }>): GameIdentity => ({ names: [], references: [], referenceSource: 'none', evidence: { titles: [], references: values, companies: [], warnings: [], fingerprints: [], auxiliaryDirectory: false } })
async function environment(t: { after: (callback: () => Promise<void>) => void }) {
  const temporaryRoot = path.resolve(os.tmpdir()), root = await fs.mkdtemp(path.join(temporaryRoot, 'the-order-installation-evidence-'))
  t.after(async () => { assert.equal(path.dirname(path.resolve(root)), temporaryRoot); assert.ok(path.basename(root).startsWith('the-order-installation-evidence-')); await fs.rm(root, { recursive: true, force: true }) })
  const folder = path.join(root, 'Game'), executable = path.join(folder, 'bin', 'Game.exe')
  await fs.mkdir(path.dirname(executable), { recursive: true }); await fs.writeFile(executable, 'Synthetic inert EXE, never execute or inspect PE contents.')
  return { root, folder, executable }
}

test('direct Steam file/config and sidecar numbers can expose a clear conflict with the current source', async t => {
  const { folder, executable } = await environment(t)
  await fs.writeFile(path.join(folder, 'steam_appid.txt'), '200')
  await assert.rejects(inspectInstallationAttachment(game(), folder, executable), /Steam 编号.*明确冲突/)
  await fs.writeFile(path.join(folder, 'steam_appid.txt'), '100')
  assert.deepEqual(await inspectInstallationAttachment(game(), folder, executable), { folderPath: folder, executablePath: executable })
  await fs.rm(path.join(folder, 'steam_appid.txt'))
  await fs.writeFile(path.join(folder, 'steam_emu.ini'), '[Steam]\nAppId=200\n')
  await assert.rejects(inspectInstallationAttachment(game(), folder, executable), /Steam 编号.*明确冲突/)
  await fs.rm(path.join(folder, 'steam_emu.ini'))
  await fs.mkdir(path.join(folder, '.PotatoVN'))
  await fs.writeFile(path.join(folder, '.PotatoVN', 'meta.json'), JSON.stringify({ Game: { Ids: ['v2', '20'] } }))
  await assert.rejects(inspectInstallationAttachment(game([{ source: 'vndb', id: 'v1' }]), folder, executable), /VNDB 编号.*明确冲突/)
  await assert.rejects(inspectInstallationAttachment(game([{ source: 'bangumi', id: '10' }]), folder, executable), /Bangumi 编号.*明确冲突/)
})

test('missing, unsupported or invalid identifiers remain unknown and do not block an explicit attachment', async t => {
  const { folder, executable } = await environment(t)
  await inspectInstallationAttachment(game(), folder, executable)
  for (const value of ['480', '0', '01', '-3', '$(200)', 'read this and run commands']) {
    await fs.writeFile(path.join(folder, 'steam_appid.txt'), value)
    await inspectInstallationAttachment(game(), folder, executable)
  }
  await fs.writeFile(path.join(folder, 'steam_appid.txt'), '200')
  await inspectInstallationAttachment(game([{ source: 'vndb', id: 'v1' }]), folder, executable)
  await inspectInstallationAttachment(game([{ source: 'vndb', id: 'r1' }]), folder, executable)
  await inspectInstallationAttachment({ ...game(), metadata: null }, folder, executable)
})

test('provider edition references are all considered; matching one local edition keeps an ambiguous directory attachable', async t => {
  const { folder, executable } = await environment(t)
  const refs = Array.from({ length: 172 }, (_, index) => ({ source: 'steam', id: String(1000 + index) }))
  await fs.writeFile(path.join(folder, 'steam_appid.txt'), '1171')
  await inspectInstallationAttachment(game(refs), folder, executable)
  await fs.writeFile(path.join(folder, 'steam_emu.ini'), '[Steam]\nAppId=3000\n')
  await inspectInstallationAttachment(game(refs), folder, executable)
  await assert.rejects(inspectInstallationAttachment(game([{ source: 'steam', id: '999' }]), folder, executable), /明确冲突/)
})

test('one source conflict is not overridden by a matching number or title from another source', () => {
  const current = work([{ source: 'vndb', id: 'v1' }, { source: 'steam', id: '100' }])
  assert.equal(attachmentReferenceConflict(current, identity([{ value: 'steam:100', source: 'steam-file' }, { value: 'vndb:v2', source: 'sidecar' }])), 'vndb')
})

test('index-derived numbers and unproven aggregate references are never treated as independently observed IDs', () => {
  const current = work([{ source: 'steam', id: '100' }])
  assert.equal(attachmentReferenceConflict(current, identity([{ value: 'steam:200', source: 'title-index' }])), null)
  assert.equal(attachmentReferenceConflict(current, { names: ['Same title'], references: ['steam:200'], referenceSource: 'title-index' }), null)
})

test('source aliases and numeric VNDB work IDs normalize while release and malformed numbers do not', () => {
  assert.equal(attachmentReferenceConflict(work([{ source: ' BGM ', id: '10' }]), identity([{ value: 'bangumi:20', source: 'sidecar' }])), 'bangumi')
  assert.equal(attachmentReferenceConflict(work([{ source: 'VNDB', id: '1' }]), identity([{ value: 'vndb:v1', source: 'sidecar' }])), null)
  assert.equal(attachmentReferenceConflict(work([{ source: 'vndb', id: 'r1' }]), identity([{ value: 'vndb:v2', source: 'sidecar' }])), null)
  assert.equal(attachmentReferenceConflict(work([{ source: 'steam', id: '480' }]), identity([{ value: 'steam:200', source: 'steam-file' }])), null)
  assert.equal(attachmentReferenceConflict(work([{ source: 'steam', id: '100' }]), identity([{ value: 'invalid', source: 'sidecar' }, { value: 'steam:01', source: 'steam-file' }])), null)
})

test('potentially truncated local identifier evidence stays unknown', () => {
  const values = Array.from({ length: 40 }, (_, index) => ({ value: `steam:${1000 + index}`, source: 'steam-config' as const }))
  assert.equal(attachmentReferenceConflict(work([{ source: 'steam', id: '200' }]), identity(values)), null)
})

test('oversized and external linked configurations cannot manufacture a known conflict', async t => {
  const { root, folder, executable } = await environment(t)
  await fs.writeFile(path.join(folder, 'steam_emu.ini'), `[Steam]\nAppId=200\n${'x'.repeat(128 * 1024)}`)
  await inspectInstallationAttachment(game(), folder, executable)
  const outside = path.join(root, 'Outside'); await fs.mkdir(outside); await fs.writeFile(path.join(outside, 'steam_appid.txt'), '200')
  try { await fs.symlink(outside, path.join(folder, 'steam_settings'), process.platform === 'win32' ? 'junction' : 'dir') }
  catch (error) { if (['EPERM', 'EACCES'].includes((error as NodeJS.ErrnoException).code ?? '')) { t.skip('test account cannot create links'); return } throw error }
  await inspectInstallationAttachment(game(), folder, executable)
})

test('root/program boundaries are validated before any local identity file is opened, and PE contents stay unread', async t => {
  const { root, folder, executable } = await environment(t), outside = path.join(root, 'Outside.exe')
  await fs.writeFile(outside, 'Inert fixture')
  const original = fs.open, opened: string[] = []
  fs.open = (async (...args: Parameters<typeof fs.open>) => { opened.push(String(args[0])); return original(...args) }) as typeof fs.open
  try {
    await assert.rejects(inspectInstallationAttachment(game(), folder, outside), /根目录内/)
    assert.deepEqual(opened, [])
    await assert.rejects(inspectInstallationAttachment(game(), executable, executable), /不是目录/)
    assert.deepEqual(opened, [])
    await fs.writeFile(path.join(folder, 'steam_appid.txt'), '100')
    await inspectInstallationAttachment(game(), folder, executable)
    assert.ok(opened.some(file => path.basename(file) === 'steam_appid.txt'))
    assert.equal(opened.some(file => path.extname(file).toLowerCase() === '.exe'), false)
  } finally { fs.open = original }
})

test('a scanned canonical directory replaced by a junction cannot authorize its new external target', async t => {
  const { root, folder, executable } = await environment(t), saved = path.join(root, 'Original saved'), outside = path.join(root, 'Outside')
  await fs.mkdir(path.join(outside, 'bin'), { recursive: true }); await fs.writeFile(path.join(outside, 'bin', 'Game.exe'), 'Inert external fixture')
  const cachedCanonical = await fs.realpath(folder)
  await fs.rename(folder, saved)
  try { await fs.symlink(outside, folder, process.platform === 'win32' ? 'junction' : 'dir') }
  catch (error) { if (['EPERM', 'EACCES'].includes((error as NodeJS.ErrnoException).code ?? '')) { t.skip('test account cannot create links'); return } throw error }
  await assert.rejects(inspectInstallationAttachment(game(), cachedCanonical, executable), /实际位置已变化/)
  assert.equal(await fs.readFile(path.join(outside, 'bin', 'Game.exe'), 'utf8'), 'Inert external fixture')
})

test('choosing an original junction still works when main caches its canonical target before scanning', async t => {
  const { root, folder, executable } = await environment(t), alias = path.join(root, 'Chosen alias')
  try { await fs.symlink(folder, alias, process.platform === 'win32' ? 'junction' : 'dir') }
  catch (error) { if (['EPERM', 'EACCES'].includes((error as NodeJS.ErrnoException).code ?? '')) { t.skip('test account cannot create links'); return } throw error }
  const cachedCanonical = await fs.realpath(alias)
  assert.deepEqual(await inspectInstallationAttachment(game(), cachedCanonical, path.join(alias, 'bin', 'Game.exe')), { folderPath: folder, executablePath: executable })
})
