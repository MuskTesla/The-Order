import assert from 'node:assert/strict'
import { test } from 'node:test'
import { promises as fs } from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { LibraryStore } from '../electron/library'
import type { WorkCover, WorkMetadata } from '../src/shared/types'
import { localCoverFixture, structuralJpeg } from './local-cover-fixtures'
import { MAX_LOCAL_COVER_BYTES } from '../electron/local-cover'

const remote: WorkCover = { id: '10', url: 'https://synthetic.invalid/remote.jpg', width: 500, height: 700, votes: 0, sexual: 'safe' }
const work = (id = '101'): WorkMetadata => ({ id, title: `Work ${id}`, originalTitle: '', aliases: [], coverUrl: remote.url, covers: [remote], bannerUrl: null, description: '', releaseDate: null, releaseDatePrecision: null, developers: [], tags: [], ratings: [], characters: [], references: [], webUrl: null, contentRating: null, fetchedAt: '2026-10-03T00:00:00.000Z' })
async function environment(t: { after(fn: () => Promise<void>): void }, linked = true) {
  const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'the-order-local-cover-')); t.after(() => fs.rm(directory, { recursive: true, force: true }))
  const store = new LibraryStore(directory); await store.initialize()
  let game = await store.addVirtual(work())
  if (!linked) {
    const backup = store.exportBackup(); backup.games[0]!.metadata = null
    await store.removeGame(game.id); await store.importBackup(backup); game = store.getGame(game.id)
  }
  return { directory, store, game }
}
test('local photo is independent of association and survives restart and single-file backup import', async t => {
  const { directory, store, game } = await environment(t, false)
  const initial = await store.setLocalCover(game.id, null, localCoverFixture(), null)
  assert.equal(initial.metadata, null); assert.equal(initial.nonPortraitCoverDisplay, null)
  const linked = await store.linkMetadata(game.id, work()), refreshed = await store.linkMetadata(game.id, { ...work(), description: 'fresh metadata' }), relinked = await store.linkMetadata(game.id, work('202'))
  for (const value of [linked, refreshed, relinked]) assert.deepEqual(value.localCover, localCoverFixture())
  const reloaded = new LibraryStore(directory); await reloaded.initialize(); assert.deepEqual(reloaded.getGame(game.id), relinked)
  const backup = reloaded.exportBackup(); assert.equal(backup.version, 1)
  await reloaded.removeGame(game.id); await reloaded.importBackup(backup)
  assert.deepEqual(reloaded.getGame(game.id), relinked)
})
test('remote or default choice explicitly clears local photo; display-only and follow-global changes preserve it', async t => {
  const { store, game } = await environment(t)
  await store.setGameCover(game.id, '101', remote, 'crop')
  let current = await store.setLocalCover(game.id, '101', localCoverFixture(), 'plain')
  assert.equal(current.coverChoice, undefined)
  current = await store.setGameCover(game.id, '101', undefined, null)
  assert.deepEqual(current.localCover, localCoverFixture()); assert.equal(current.nonPortraitCoverDisplay, null)
  current = await store.setGameCover(game.id, '101', remote)
  assert.equal(current.localCover, undefined); assert.deepEqual(current.coverChoice?.cover, remote)
  await store.setLocalCover(game.id, '101', localCoverFixture())
  current = await store.setGameCover(game.id, '101', null)
  assert.equal(current.localCover, undefined); assert.equal(current.coverChoice, undefined)
})
test('unbound local game can save display or reset cover without requiring a catalog identity', async t => {
  const { store, game } = await environment(t, false)
  await store.setLocalCover(game.id, null, localCoverFixture(), 'plain')
  const current = await store.setGameCover(game.id, null, undefined, 'crop')
  assert.equal(current.nonPortraitCoverDisplay, 'crop'); assert.deepEqual(current.localCover, localCoverFixture())
  await assert.rejects(store.setGameCover(game.id, null, remote), /先关联作品/)
  const reset = await store.setGameCover(game.id, null, null, null)
  assert.equal(reset.localCover, undefined); assert.equal(reset.nonPortraitCoverDisplay, null)
})
for (const newer of ['photo', 'remote', 'reset', 'display'] as const) test(`newer ${newer} intent rejects a late local photo and keeps other personal fields`, async t => {
  const { store, game } = await environment(t)
  const token = store.captureCoverRequest(game.id, '101')
  await store.updateGame(game.id, { name: 'My title', notes: 'Keep note', personalRating: 0 })
  if (newer === 'photo') await store.setLocalCover(game.id, '101', localCoverFixture(), 'plain')
  else await store.setGameCover(game.id, '101', newer === 'remote' ? remote : newer === 'reset' ? null : undefined, 'crop')
  const before = store.getGame(game.id), disk = await fs.readFile(store.filePath, 'utf8')
  await assert.rejects(store.setLocalCover(game.id, '101', localCoverFixture(), 'blur', token), /条目或封面选择已变化/)
  assert.deepEqual(store.getGame(game.id), before); assert.equal(await fs.readFile(store.filePath, 'utf8'), disk)
})
test('same-work refresh preserves an in-flight local photo but changed association invalidates it', async t => {
  const { store, game } = await environment(t)
  const token = store.captureCoverRequest(game.id, '101')
  await store.linkMetadata(game.id, { ...work(), description: 'fresh metadata' })
  await store.setLocalCover(game.id, '101', localCoverFixture(), 'plain', token)
  const old = store.captureCoverRequest(game.id, '101')
  const request = await store.beginMetadataRequest(game.id, 'association')
  await store.finishMetadataRequest(request.token)
  await assert.rejects(store.setLocalCover(game.id, '101', localCoverFixture(), 'crop', old), /条目或封面选择已变化/)
  assert.equal(store.getGame(game.id).nonPortraitCoverDisplay, 'plain')
})
test('removal and restoration of identical game ID invalidate an outstanding photo dialog', async t => {
  const { store, game } = await environment(t)
  const token = store.captureCoverRequest(game.id, '101'), backup = store.exportBackup()
  await store.removeGame(game.id); await store.importBackup(backup)
  await assert.rejects(store.setLocalCover(game.id, '101', localCoverFixture(), 'crop', token), /条目或封面选择已变化/)
  assert.equal(store.getGame(game.id).localCover, undefined)
})
test('merges preserve recipient personal cover, otherwise inherit a managed local photo', async t => {
  const { store, game } = await environment(t)
  const existing = await store.addVirtual(work('202')); await store.setLocalCover(existing.id, '202', localCoverFixture(), 'crop')
  let merged = await store.linkMetadata(game.id, work('202'))
  assert.deepEqual(merged.localCover, localCoverFixture()); assert.equal(merged.nonPortraitCoverDisplay, 'crop')
  await store.setGameCover(game.id, '202', remote, null)
  const duplicate = { ...store.getGame(game.id), id: 'duplicate-photo', localCover: localCoverFixture() }; delete duplicate.coverChoice
  const backup = store.exportBackup(); backup.games.push(duplicate)
  await fs.writeFile(store.filePath, JSON.stringify(backup))
  const reloaded = new LibraryStore(store.dataDir); await reloaded.initialize()
  merged = await reloaded.linkMetadata(game.id, work('202'))
  assert.equal(merged.localCover, undefined); assert.deepEqual(merged.coverChoice?.cover, remote)
  assert.equal(merged.nonPortraitCoverDisplay, null, 'explicit follow-global is a saved preference and survives merges')
})
test('photo/display write failure preserves previous valid state and subsequent saves remain usable', async t => {
  const { store, game } = await environment(t)
  const before = await store.setGameCover(game.id, '101', remote, 'plain'), disk = await fs.readFile(store.filePath, 'utf8')
  const original = (store as any).atomicWrite.bind(store)
  ;(store as any).atomicWrite = async (file: string, contents: string) => { if (file === store.filePath) throw Error('Synthetic photo persistence failure'); return original(file, contents) }
  await assert.rejects(store.setLocalCover(game.id, '101', localCoverFixture(), 'crop'), /Synthetic photo persistence failure/)
  assert.deepEqual(store.getGame(game.id), before); assert.equal(await fs.readFile(store.filePath, 'utf8'), disk)
  ;(store as any).atomicWrite = original
  assert.deepEqual((await store.setLocalCover(game.id, '101', localCoverFixture(), null)).localCover, localCoverFixture())
})
test('invalid local data, unsupported patch fields and oversized total libraries fail without replacement', async t => {
  const { store, game } = await environment(t), initial = store.exportBackup(), disk = await fs.readFile(store.filePath, 'utf8')
  for (const localCover of [{ ...localCoverFixture(), dataUrl: 'data:text/html;base64,PHNjcmlwdD4=' }, { ...localCoverFixture(), width: 2 }, { ...localCoverFixture(), dataUrl: 'file:///C:/Users/private.jpg' }]) {
    const backup = structuredClone(initial); backup.games[0]!.localCover = localCover
    await assert.rejects(store.importBackup(backup)); await assert.rejects(store.setLocalCover(game.id, '101', localCover))
  }
  await assert.rejects(store.updateGame(game.id, { localCover: localCoverFixture() } as any), /不支持的字段/)
  const backup = structuredClone(initial), jpeg = structuralJpeg(4, 3, MAX_LOCAL_COVER_BYTES - 100), photo = { dataUrl: 'data:image/jpeg;base64,' + jpeg.toString('base64'), width: 4, height: 3 }
  backup.games = Array.from({ length: 200 }, (_, index) => ({ ...structuredClone(initial.games[0]!), id: 'oversized-' + index, metadata: null, localCover: photo }))
  await assert.rejects(store.importBackup(backup), /32 MB/)
  assert.deepEqual(store.getGames(), initial.games); assert.equal(await fs.readFile(store.filePath, 'utf8'), disk)
})
