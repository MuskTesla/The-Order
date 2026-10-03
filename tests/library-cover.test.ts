import assert from 'node:assert/strict'
import { test } from 'node:test'
import { promises as fs } from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { LibraryStore } from '../electron/library'
import type { WorkCover, WorkMetadata } from '../src/shared/types'

const cover = (id = '10'): WorkCover => ({ id, url: `https://synthetic.invalid/${id}.webp`, width: 500, height: 700, votes: 0, sexual: 'safe' })
const work = (id = '101', covers: WorkCover[] = [cover()]): WorkMetadata => ({ id, title: `Work ${id}`, originalTitle: '', aliases: [], coverUrl: 'https://synthetic.invalid/default.webp', bannerUrl: null, covers, description: '', releaseDate: null, releaseDatePrecision: null, developers: [], tags: [], ratings: [], characters: [], references: [], webUrl: null, contentRating: null, fetchedAt: '2026-10-03T00:00:00.000Z' })
async function environment(t: { after(fn: () => Promise<void>): void }) {
  const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'the-order-personal-cover-'))
  t.after(() => fs.rm(directory, { recursive: true, force: true }))
  const store = new LibraryStore(directory); await store.initialize()
  const game = await store.addVirtual(work())
  return { store, game, directory }
}
const personal = (value: ReturnType<LibraryStore['getGame']>) => {
  const { metadata, coverChoice, nonPortraitCoverDisplay, ...rest } = value; return rest
}

test('old version-one books omit new fields and retain their existing default through persistence', async t => {
  const { store, game, directory } = await environment(t)
  const backup = store.exportBackup()
  delete backup.games[0]!.metadata!.covers
  await store.removeGame(game.id); await store.importBackup(backup)
  const restarted = new LibraryStore(directory); await restarted.initialize()
  const current = restarted.getGame(game.id)
  assert.equal(current.metadata?.coverUrl, backup.games[0]!.metadata!.coverUrl)
  for (const field of ['coverChoice', 'nonPortraitCoverDisplay']) assert.equal(Object.hasOwn(current, field), false)
  assert.equal(Object.hasOwn(current.metadata!, 'covers'), false)
  assert.equal(restarted.exportBackup().version, 1)
})

test('choice and display save atomically and survive restart and version-one export/import', async t => {
  const { store, game, directory } = await environment(t)
  const updated = await store.setGameCover(game.id, '101', cover(), 'plain')
  assert.deepEqual(updated.coverChoice, { workId: '101', cover: cover() })
  assert.equal(updated.nonPortraitCoverDisplay, 'plain')
  assert.deepEqual(personal(updated), personal(game))
  const restarted = new LibraryStore(directory); await restarted.initialize()
  assert.deepEqual(restarted.getGame(game.id), updated)
  const backup = store.exportBackup()
  await restarted.removeGame(game.id); await restarted.importBackup(backup)
  assert.deepEqual(restarted.getGame(game.id), updated)
})

test('same-work refresh preserves the latest choice even when it is absent from new artwork', async t => {
  const { store, game } = await environment(t)
  await store.updateGame(game.id, { name: 'Personal name', personalRating: 0, notes: 'Keep note', categories: ['Keep category'], customTags: ['Keep tag'] })
  const before = store.getGame(game.id)
  const refresh = await store.beginMetadataRequest(game.id, 'refresh')
  await store.setGameCover(game.id, '101', cover('20'), 'crop')
  const fresh = { ...work('101', []), coverUrl: 'https://synthetic.invalid/new-default.webp' }
  const updated = await store.linkMetadata(game.id, fresh, { requestToken: refresh.token, expectedName: refresh.game.name, expectedMetadataId: '101' })
  await store.finishMetadataRequest(refresh.token)
  assert.equal(updated.metadata?.coverUrl, fresh.coverUrl)
  assert.deepEqual(updated.coverChoice?.cover, cover('20'))
  assert.equal(updated.nonPortraitCoverDisplay, 'crop')
  assert.deepEqual(personal(updated), personal(before))
  const displayOnly = await store.setGameCover(game.id, '101', undefined, 'plain')
  assert.deepEqual(displayOnly.coverChoice, updated.coverChoice)
  const reset = await store.setGameCover(game.id, '101', null, 'blur')
  assert.equal(reset.coverChoice, undefined)
  assert.equal(reset.nonPortraitCoverDisplay, 'blur')
  assert.deepEqual(reset.metadata, fresh)
})

test('ordinary rename and same-work refresh do not invalidate an in-flight cover choice', async t => {
  const { store, game } = await environment(t)
  const token = store.captureCoverRequest(game.id, '101')
  await store.updateGame(game.id, { name: 'Personal name' })
  await store.linkMetadata(game.id, { ...work(), description: 'Fresh metadata' })
  const updated = await store.setGameCover(game.id, '101', cover('20'), 'plain', token)
  assert.equal(updated.name, 'Personal name')
  assert.equal(updated.metadata?.description, 'Fresh metadata')
  assert.equal(updated.coverChoice?.cover.id, '20')
})

for (const newer of ['choice', 'reset', 'display'] as const) test(`newer ${newer} intent rejects an older artwork response`, async t => {
  const { store, game } = await environment(t)
  await store.setGameCover(game.id, '101', cover('10'), 'blur')
  const old = store.captureCoverRequest(game.id, '101')
  await store.setGameCover(game.id, '101', newer === 'choice' ? cover('30') : newer === 'reset' ? null : undefined, 'plain')
  const before = store.getGame(game.id), disk = await fs.readFile(store.filePath, 'utf8')
  await assert.rejects(store.setGameCover(game.id, '101', cover('20'), 'crop', old), /条目或封面选择已变化/)
  assert.deepEqual(store.getGame(game.id), before)
  assert.equal(await fs.readFile(store.filePath, 'utf8'), disk)
})

test('change of work clears personal artwork but retains display and all personal fields', async t => {
  const { store, game } = await environment(t)
  await store.setGameCover(game.id, '101', cover(), 'plain')
  const token = store.captureCoverRequest(game.id, '101')
  const changed = await store.linkMetadata(game.id, work('202'))
  assert.equal(changed.coverChoice, undefined)
  assert.equal(changed.nonPortraitCoverDisplay, 'plain')
  await assert.rejects(store.setGameCover(game.id, '101', cover('20'), 'crop', token), /条目或封面选择已变化/)
  await assert.rejects(store.setGameCover(game.id, '101', null), /作品关联已变化/)
})

test('association intent immediately invalidates artwork even if association later fails', async t => {
  const { store, game } = await environment(t)
  const token = store.captureCoverRequest(game.id, '101')
  const request = await store.beginMetadataRequest(game.id, 'association')
  assert.throws(() => store.captureCoverRequest(game.id, '101'), /正在更新/)
  await store.finishMetadataRequest(request.token)
  await assert.rejects(store.setGameCover(game.id, '101', cover(), 'crop', token), /条目或封面选择已变化/)
  assert.equal((await store.setGameCover(game.id, '101', cover())).coverChoice?.workId, '101')
})

test('deletion and restoration of the same game ID reject an old artwork response', async t => {
  const { store, game } = await environment(t)
  const token = store.captureCoverRequest(game.id, '101'), backup = store.exportBackup()
  await store.removeGame(game.id); await store.importBackup(backup)
  await assert.rejects(store.setGameCover(game.id, '101', cover(), 'crop', token), /条目或封面选择已变化/)
  assert.equal(store.getGame(game.id).coverChoice, undefined)
})

test('merge takes artwork for the destination work and preserves surviving explicit display preference', async t => {
  const { store, game } = await environment(t)
  await store.setGameCover(game.id, '101', cover('11'), 'plain')
  const destination = await store.addVirtual(work('202'))
  await store.setGameCover(destination.id, '202', cover('22'), 'crop')
  const merged = await store.linkMetadata(game.id, work('202'))
  assert.equal(merged.id, game.id)
  assert.deepEqual(merged.coverChoice, { workId: '202', cover: cover('22') })
  assert.equal(merged.nonPortraitCoverDisplay, 'plain')
  assert.equal(store.getGames().length, 1)
})

test('merge retains the surviving same-work artwork and inherits display only when absent', async t => {
  const { store, game, directory } = await environment(t)
  const backup = store.exportBackup()
  const target = backup.games[0]!
  target.coverChoice = { workId: '101', cover: cover('11') }
  const merged = { ...structuredClone(target), id: 'duplicate-legacy-row', coverChoice: { workId: '101', cover: cover('22') }, nonPortraitCoverDisplay: 'crop' as const }
  backup.games.push(merged)
  await fs.writeFile(store.filePath, JSON.stringify(backup))
  const reloaded = new LibraryStore(directory); await reloaded.initialize()
  const updated = await reloaded.linkMetadata(game.id, work())
  assert.deepEqual(updated.coverChoice, target.coverChoice)
  assert.equal(updated.nonPortraitCoverDisplay, 'crop')
})

test('atomic persistence failure rolls back both artwork and display', async t => {
  const { store, game } = await environment(t)
  await store.setGameCover(game.id, '101', cover('11'), 'plain')
  const before = store.getGame(game.id), primary = await fs.readFile(store.filePath, 'utf8')
  const original = (store as any).atomicWrite.bind(store)
  ;(store as any).atomicWrite = async (file: string, contents: string) => { if (file === store.filePath) throw Error('Synthetic primary write failure'); return original(file, contents) }
  await assert.rejects(store.setGameCover(game.id, '101', cover('22'), 'crop'), /Synthetic primary write failure/)
  assert.deepEqual(store.getGame(game.id), before)
  assert.equal(await fs.readFile(store.filePath, 'utf8'), primary)
})

test('queued choice checks latest intent before persistence and leaves personal edits intact', async t => {
  const { store, game } = await environment(t)
  let release!: () => void, entered!: () => void
  const blocked = new Promise<void>(resolve => { release = resolve }), started = new Promise<void>(resolve => { entered = resolve })
  const original = (store as any).atomicWrite.bind(store)
  let writes = 0
  ;(store as any).atomicWrite = async (file: string, contents: string) => { if (++writes === 1) { entered(); await blocked }; return original(file, contents) }
  const editing = store.updateGame(game.id, { notes: 'Keep concurrent note' }); await started
  const old = store.captureCoverRequest(game.id, '101')
  const obsolete = assert.rejects(store.setGameCover(game.id, '101', cover('11'), 'crop', old), /条目或封面选择已变化/)
  const current = store.setGameCover(game.id, '101', cover('22'), 'plain')
  release(); await editing; await obsolete; await current
  assert.equal(store.getGame(game.id).notes, 'Keep concurrent note')
  assert.equal(store.getGame(game.id).coverChoice?.cover.id, '22')
  assert.equal(writes, 4, 'only the note and current cover each write backup and primary')
})

test('malicious artwork, mismatched work and invalid display imports fail atomically', async t => {
  const { store, game } = await environment(t)
  const primary = await fs.readFile(store.filePath, 'utf8')
  const invalid = [
    { id: '../outside' }, { id: ' 10' }, { id: '123456789012345678901' }, { url: 'javascript:alert(1)' }, { url: 'http://synthetic.invalid/plain.webp' },
    { url: 'https://user:secret@synthetic.invalid/private.webp' }, { width: -1 }, { height: 0 }, { votes: 1.5 }, { sexual: 'unknown' }
  ]
  for (const patch of invalid) {
    await assert.rejects(store.setGameCover(game.id, '101', { ...cover(), ...patch } as WorkCover, 'plain'))
    const backup = store.exportBackup()
    backup.games[0]!.coverChoice = { workId: '101', cover: { ...cover(), ...patch } as WorkCover }
    await assert.rejects(store.importBackup(backup))
  }
  for (const patch of [{ coverChoice: { workId: '202', cover: cover() } }, { nonPortraitCoverDisplay: 'stretch' }]) {
    const backup = store.exportBackup(); Object.assign(backup.games[0]!, patch)
    await assert.rejects(store.importBackup(backup))
  }
  const oversized = store.exportBackup(); oversized.games[0]!.metadata!.covers = Array.from({ length: 501 }, (_, id) => cover(String(id)))
  await assert.rejects(store.importBackup(oversized), /封面列表/)
  await assert.rejects(store.setGameCover(game.id, '101', undefined, 'stretch' as any), /封面展示方式/)
  assert.deepEqual(store.getGame(game.id), game)
  assert.equal(await fs.readFile(store.filePath, 'utf8'), primary)
})

test('ordinary edits cannot bypass the atomic cover interface to overwrite display or artwork', async t => {
  const { store, game } = await environment(t)
  await store.setGameCover(game.id, '101', cover('10'), 'plain')
  const old = store.captureCoverRequest(game.id, '101'), before = store.getGame(game.id)
  await assert.rejects(store.updateGame(game.id, { nonPortraitCoverDisplay: 'crop' } as any), /不支持的字段/)
  await assert.rejects(store.updateGame(game.id, { coverChoice: { workId: '101', cover: cover('30') } } as any), /不支持的字段/)
  assert.deepEqual(store.getGame(game.id), before)
  assert.equal((await store.setGameCover(game.id, '101', cover('20'), 'blur', old)).coverChoice?.cover.id, '20', 'Rejected ordinary edits do not create a new cover intent')
})
