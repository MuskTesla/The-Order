import assert from 'node:assert/strict'
import { test } from 'node:test'
import { promises as fs } from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { LibraryStore } from '../electron/library'
import type { BulkGamePatch, WorkMetadata } from '../src/shared/types'

const work = (id: string): WorkMetadata => ({ id, title: `Synthetic ${id}`, originalTitle: '', aliases: [], coverUrl: null, bannerUrl: null, description: '', releaseDate: null, releaseDatePrecision: null, developers: [], tags: [], ratings: [], characters: [], references: [], webUrl: null, contentRating: null, fetchedAt: '2026-10-03T00:00:00.000Z' })
async function environment(t: { after(callback: () => Promise<void>): void }) {
  const parent = path.resolve(os.tmpdir())
  const root = await fs.mkdtemp(path.join(parent, 'the-order-bulk-'))
  t.after(async () => {
    assert.equal(path.dirname(path.resolve(root)), parent)
    assert.ok(path.basename(root).startsWith('the-order-bulk-'))
    await fs.rm(root, { recursive: true, force: true })
  })
  const store = new LibraryStore(root); await store.initialize()
  const first = await store.addVirtual(work('first')), second = await store.addVirtual(work('second')), other = await store.addVirtual(work('other'))
  return { store, first, second, other }
}
async function saved(store: LibraryStore) {
  return { games: store.getGames(), primary: await fs.readFile(store.filePath, 'utf8'), backup: await fs.readFile(store.backupPath, 'utf8') }
}
function gate() {
  let resolve!: () => void
  const promise = new Promise<void>(done => { resolve = done })
  return { promise, resolve }
}

test('bulk changes persist together, retain per-game data, and return detached records in input order', async t => {
  const { store, first, second, other } = await environment(t)
  await store.updateGame(first.id, { name: 'Chosen title', notes: 'Keep my notes', categories: ['One', 'Remove'], customTags: ['First', 'Old'] })
  await store.updateGame(second.id, { categories: ['Two', 'Remove'], customTags: ['Second'] })
  await store.recordSession(first.id, { id: 'play', startedAt: '2026-10-03T00:00:00Z', endedAt: '2026-10-03T00:01:00Z', seconds: 60 })
  const before = store.getGames()
  const result = await store.updateGames([second.id, first.id], { status: 'completed', favorite: true, personalRating: 9.5, addCategories: [' New ', 'New'], removeCategories: ['Remove'], addTags: ['Shared'], removeTags: ['Old'] })
  assert.deepEqual(result.map(item => item.id), [second.id, first.id])
  assert.deepEqual(result[0]!.categories, ['Two', 'New'])
  assert.deepEqual(result[1]!.categories, ['One', 'New'])
  assert.deepEqual(result[0]!.customTags, ['Second', 'Shared'])
  assert.deepEqual(result[1]!.customTags, ['First', 'Shared'])
  for (const item of result) {
    const previous = before.find(game => game.id === item.id)!
    assert.deepEqual(item, { ...previous, status: 'completed', favorite: true, personalRating: 9.5, categories: item.categories, customTags: item.customTags })
  }
  assert.deepEqual(store.getGame(other.id), other)
  assert.deepEqual(JSON.parse(await fs.readFile(store.backupPath, 'utf8')).games, before, 'backup is the complete state before the batch')
  const reopened = new LibraryStore(store.dataDir); await reopened.initialize()
  assert.deepEqual(reopened.getGames(), store.getGames())
  result[0]!.categories.push('Mutated return'); result[1]!.name = 'Mutated return'
  assert.deepEqual(store.getGame(second.id).categories, ['Two', 'New'])
  assert.equal(store.getGame(first.id).name, 'Chosen title')
})

test('bulk ratings support zero, ten, clearing, and false favorites without treating them as an empty patch', async t => {
  const { store, first, second } = await environment(t), ids = [first.id, second.id]
  await store.updateGames(ids, { favorite: true, personalRating: 10 })
  assert.ok(store.getGames().filter(item => ids.includes(item.id)).every(item => item.favorite && item.personalRating === 10))
  await store.updateGames(ids, { favorite: false, personalRating: 0 })
  assert.ok(store.getGames().filter(item => ids.includes(item.id)).every(item => !item.favorite && item.personalRating === 0))
  await store.updateGames(ids, { personalRating: null })
  assert.ok(store.getGames().filter(item => ids.includes(item.id)).every(item => item.personalRating === null))
})

test('bulk invalid selections and any unsupported field leave both saved files unchanged', async t => {
  const { store, first, second } = await environment(t)
  const before = await saved(store)
  const cases: Array<{ ids: unknown; patch: unknown }> = [
    { ids: [], patch: { favorite: true } },
    { ids: 'not-an-array', patch: { favorite: true } },
    { ids: [first.id, first.id], patch: { favorite: true } },
    { ids: [first.id, ` ${first.id} `], patch: { favorite: true } },
    { ids: [first.id, 'missing'], patch: { favorite: true } },
    { ids: [first.id, 1], patch: { favorite: true } },
    { ids: [first.id, ''], patch: { favorite: true } },
    { ids: Array.from({ length: 1001 }, (_, index) => `game-${index}`), patch: { favorite: true } },
    ...['name', 'notes', 'preferredInstallationId', 'categories', 'customTags', 'installations', 'metadata', 'unknown'].map(field => ({ ids: [first.id, second.id], patch: { favorite: true, [field]: 'unsupported' } })),
  ]
  for (const scenario of cases) {
    await assert.rejects(store.updateGames(scenario.ids as string[], scenario.patch as BulkGamePatch))
    assert.deepEqual(await saved(store), before)
  }
})

test('bulk validates every requested operation before any partial update is visible or persisted', async t => {
  const { store, first, second } = await environment(t), ids = [first.id, second.id]
  const before = await saved(store)
  const patches: unknown[] = [null, [], {}, { addCategories: [], removeTags: [] }, { status: 'unknown' }, { status: undefined }, { favorite: 'true' }, { personalRating: 10.1 }, { personalRating: -1 }, { personalRating: Number.NaN }, { addTags: 'tag' }, { removeCategories: [''] }, { addCategories: ['a\0b'] }, { addTags: ['x'.repeat(201)] }, { addCategories: Array.from({ length: 201 }, (_, index) => `cat-${index}`) }, { addTags: [' shared '], removeTags: ['shared'] }, { addCategories: ['same'], removeCategories: [' same '] }, { favorite: true, personalRating: Infinity }]
  for (const patch of patches) {
    await assert.rejects(store.updateGames(ids, patch as BulkGamePatch))
    assert.deepEqual(await saved(store), before)
  }
})

test('a list overflow on a later selected game rejects the entire batch including otherwise valid earlier games', async t => {
  const { store, first, second } = await environment(t)
  const full = Array.from({ length: 200 }, (_, index) => `cat-${index}`)
  await store.updateGame(second.id, { categories: full })
  const before = await saved(store)
  await assert.rejects(store.updateGames([first.id, second.id], { favorite: true, addCategories: ['One more'] }), /分类/)
  assert.deepEqual(await saved(store), before)
  const changed = await store.updateGames([first.id, second.id], { addCategories: ['Replacement'], removeCategories: ['cat-0'] })
  assert.deepEqual(changed[0]!.categories, ['Replacement'])
  assert.equal(changed[1]!.categories.length, 200)
  assert.ok(changed[1]!.categories.includes('Replacement'))
  assert.ok(!changed[1]!.categories.includes('cat-0'))
  await store.updateGame(second.id, { customTags: full })
  const beforeTags = await saved(store)
  await assert.rejects(store.updateGames([first.id, second.id], { status: 'completed', addTags: ['One more'] }), /标签/)
  assert.deepEqual(await saved(store), beforeTags)
})

test('relative edits use current queued values and snapshot the caller input', async t => {
  const { store, first, second, other } = await environment(t)
  const runtime = store as unknown as { atomicWrite(target: string, contents: string): Promise<void> }
  const original = runtime.atomicWrite.bind(store), entered = gate(), release = gate()
  let hold = true
  runtime.atomicWrite = async (target, contents) => {
    if (hold) { hold = false; entered.resolve(); await release.promise }
    return original(target, contents)
  }
  t.after(async () => { release.resolve(); runtime.atomicWrite = original })
  const earlier = store.updateGame(first.id, { categories: ['Fresh category'], customTags: ['Fresh tag'] })
  await entered.promise
  const ids = [first.id, second.id], patch = { addCategories: ['Batch category'], addTags: ['Batch tag'], favorite: true }
  const pending = store.updateGames(ids, patch)
  ids[0] = other.id; patch.addCategories[0] = 'Changed input'; patch.addTags.push('Changed input'); patch.favorite = false
  release.resolve(); await earlier; await pending
  assert.deepEqual(store.getGame(first.id).categories, ['Fresh category', 'Batch category'])
  assert.deepEqual(store.getGame(first.id).customTags, ['Fresh tag', 'Batch tag'])
  assert.deepEqual(store.getGame(second.id).categories, ['Batch category'])
  assert.equal(store.getGame(first.id).favorite, true)
  assert.deepEqual(store.getGame(other.id), other)
})

test('overlapping concurrent batches compose relative additions without losing either update', async t => {
  const { store, first, second } = await environment(t)
  await Promise.all([
    store.updateGames([first.id, second.id], { addCategories: ['First batch'], addTags: ['First tag'] }),
    store.updateGames([first.id, second.id], { addCategories: ['Second batch'], removeTags: ['First tag'], addTags: ['Second tag'] }),
  ])
  for (const id of [first.id, second.id]) {
    assert.deepEqual(store.getGame(id).categories, ['First batch', 'Second batch'])
    assert.deepEqual(store.getGame(id).customTags, ['Second tag'])
  }
})

test('a deletion ahead of a queued batch rejects the batch without changing any remaining selection', async t => {
  const { store, first, second } = await environment(t)
  const deletion = store.removeGame(second.id)
  const rejection = assert.rejects(store.updateGames([first.id, second.id], { favorite: true }), /游戏不存在/)
  await deletion; await rejection
  assert.deepEqual(store.getGame(first.id), first)
  const reopened = new LibraryStore(store.dataDir); await reopened.initialize()
  assert.deepEqual(reopened.getGames(), store.getGames())
})

test('failed bulk persistence preserves current memory and the primary file, and a retry succeeds', async t => {
  const { store, first, second } = await environment(t), ids = [first.id, second.id]
  const before = await saved(store)
  const runtime = store as unknown as { atomicWrite(target: string, contents: string): Promise<void> }
  const original = runtime.atomicWrite.bind(store)
  runtime.atomicWrite = async (target, contents) => {
    if (target === store.filePath) throw new Error('Synthetic disk full')
    return original(target, contents)
  }
  try { await assert.rejects(store.updateGames(ids, { favorite: true, addTags: ['Pending'] }), /Synthetic disk full/) }
  finally { runtime.atomicWrite = original }
  assert.deepEqual(store.getGames(), before.games)
  assert.equal(await fs.readFile(store.filePath, 'utf8'), before.primary)
  assert.deepEqual(JSON.parse(await fs.readFile(store.backupPath, 'utf8')).games, before.games)
  const reopened = new LibraryStore(store.dataDir); await reopened.initialize()
  assert.deepEqual(reopened.getGames(), before.games)
  await store.updateGames(ids, { favorite: true, addTags: ['Pending'] })
  assert.ok(ids.every(id => store.getGame(id).favorite))
  assert.deepEqual(store.getGame(first.id).customTags, ['Pending'])
})

test('bulk personal edits preserve name provenance and keep current automatic and explicit metadata tokens valid', async t => {
  const { store, first, second } = await environment(t)
  await store.updateGame(second.id, { name: second.name, notes: 'Keep notes' })
  const automatic = store.captureMetadataToken(first.id, 'bulk-proof'), explicit = await store.beginMetadataRequest(second.id, 'refresh')
  await store.updateGames([first.id, second.id], { status: 'playing', addCategories: ['New category'], favorite: true })
  assert.equal(store.getGame(first.id).nameIsCustom, false)
  assert.equal(store.getGame(second.id).nameIsCustom, true)
  await store.linkMetadata(first.id, { ...work('first'), title: 'Updated first title' }, { requestToken: automatic })
  await store.linkMetadata(second.id, { ...work('second'), title: 'Updated second title' }, { requestToken: explicit.token })
  await store.finishMetadataRequest(explicit.token)
  assert.equal(store.getGame(first.id).name, 'Updated first title')
  assert.equal(store.getGame(second.id).name, second.name)
  for (const id of [first.id, second.id]) {
    assert.equal(store.getGame(id).favorite, true)
    assert.equal(store.getGame(id).status, 'playing')
    assert.deepEqual(store.getGame(id).categories, ['New category'])
  }
})

test('the upper bound accepts exactly 1000 selected games in one transaction', async t => {
  const { store, first } = await environment(t)
  const snapshot = store.exportBackup()
  snapshot.games = Array.from({ length: 1000 }, (_, index) => ({ ...structuredClone(first), id: `bulk-limit-${index}`, metadata: null, name: `Synthetic ${index}` }))
  await store.importBackup(snapshot)
  const ids = snapshot.games.map(item => item.id)
  const result = await store.updateGames(ids, { favorite: true })
  assert.equal(result.length, 1000)
  assert.ok(result.every(item => item.favorite))
  assert.equal(store.getGame(first.id).favorite, false)
  const reopened = new LibraryStore(store.dataDir); await reopened.initialize()
  assert.ok(ids.every(id => reopened.getGame(id).favorite))
})

test('merging colliding session IDs from different starts preserves both complete records without writing', async t => {
  const { store, first, second } = await environment(t)
  await store.updateGame(first.id, { categories: ['First category'], notes: 'First notes', personalRating: 9 })
  await store.updateGame(second.id, { categories: ['Second category'], notes: 'Second notes', favorite: true })
  await store.recordSession(first.id, { id: 'colliding-session', startedAt: '2026-10-01T00:00:00Z', endedAt: '2026-10-01T00:01:30Z', seconds: 90 })
  await store.recordSession(second.id, { id: 'colliding-session', startedAt: '2026-10-02T00:00:00Z', endedAt: '2026-10-02T00:01:30Z', seconds: 90 })
  const before = await saved(store)
  await assert.rejects(store.linkMetadata(first.id, work('second')), /游玩记录编号冲突，未合并，请保留备份后核对。/)
  assert.deepEqual(await saved(store), before)
  const reopened = new LibraryStore(store.dataDir); await reopened.initialize()
  assert.deepEqual(reopened.getGames(), before.games)
  assert.equal(reopened.getGame(first.id).totalPlaySeconds, 90)
  assert.equal(reopened.getGame(second.id).totalPlaySeconds, 90)
  assert.equal(reopened.getGame(first.id).playCount + reopened.getGame(second.id).playCount, 2)
})

test('merging matching session checkpoints keeps the longest checkpoint and retains personal data', async t => {
  const { store, first, second } = await environment(t)
  await store.updateGame(first.id, { categories: ['First category'], notes: 'First notes', personalRating: 9 })
  await store.updateGame(second.id, { categories: ['Second category'], notes: 'Second notes', favorite: true })
  const checkpoint = { id: 'same-session', startedAt: '2026-10-01T00:00:00Z' }
  await store.recordSession(first.id, { ...checkpoint, endedAt: '2026-10-01T00:00:30Z', seconds: 30 })
  await store.recordSession(second.id, { ...checkpoint, endedAt: '2026-10-01T00:01:30Z', seconds: 90 })
  const merged = await store.linkMetadata(first.id, work('second'))
  assert.equal(merged.totalPlaySeconds, 90)
  assert.equal(merged.playCount, 1)
  assert.equal(merged.sessions.length, 1)
  assert.equal(merged.sessions[0]!.seconds, 90)
  assert.equal(merged.sessions[0]!.endedAt, '2026-10-01T00:01:30.000Z')
  assert.deepEqual(merged.categories, ['First category', 'Second category'])
  assert.equal(merged.notes, 'First notes\n\nSecond notes')
  assert.equal(merged.personalRating, 9)
  assert.equal(merged.favorite, true)
  assert.throws(() => store.getGame(second.id), /游戏不存在/)
  const reopened = new LibraryStore(store.dataDir); await reopened.initialize()
  assert.deepEqual(reopened.getGames(), store.getGames())
})
