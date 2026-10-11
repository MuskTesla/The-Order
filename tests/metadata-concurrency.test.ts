import assert from 'node:assert/strict'
import { test } from 'node:test'
import { promises as fs } from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { LibraryStore } from '../electron/library'
import type { WorkMetadata } from '../src/shared/types'

const work = (id: string, description = id): WorkMetadata => ({ id, title: 'Personal title', originalTitle: '', aliases: [], coverUrl: null, bannerUrl: null, description, releaseDate: null, releaseDatePrecision: null, developers: [], tags: [], ratings: [], characters: [], references: [], webUrl: null, contentRating: null, fetchedAt: '2026-10-03T00:00:00.000Z' })
async function environment(t: { after(fn: () => Promise<void>): void }) {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'the-order-revisions-'))
  t.after(() => fs.rm(root, { recursive: true, force: true }))
  const store = new LibraryStore(root); await store.initialize()
  const game = await store.addVirtual(work('A'))
  await store.updateGame(game.id, { name: 'Personal title' })
  return { store, game }
}
// Fallback reproduces the rejected candidate's real behavior, without a missing-method failure.
async function begin(store: LibraryStore, id: string, kind: 'refresh' | 'association') {
  const runtime = store as any
  return runtime.beginMetadataRequest ? runtime.beginMetadataRequest(id, kind) : { game: store.getGame(id), token: undefined }
}
function options(request: any) { return { expectedName: request.game.name, expectedMetadataId: request.game.metadata?.id ?? null, ...(request.token ? { requestToken: request.token } : {}), preserveMergedName: true } }
async function finish(store: LibraryStore, request: any) { if (request.token) await (store as any).finishMetadataRequest(request.token) }

async function localEnvironment(t: { after(fn: () => Promise<void>): void }) {
  const { store, game } = await environment(t)
  const backup = store.exportBackup(), item = backup.games.find(entry => entry.id === game.id)!
  for (let index = 0; index < 2; index++) {
    const folderPath = path.join(store.dataDir, `synthetic-installation-${index}`)
    await fs.mkdir(folderPath)
    const executablePath = path.join(folderPath, 'story.exe')
    await fs.writeFile(executablePath, '') // Inert fixture: never read as a PE or executed.
    item.installations.push({ id: `installation-${index}`, folderPath, executablePath, arguments: [] })
  }
  item.preferredInstallationId = item.installations[0]!.id
  const alternateExecutable = path.join(item.installations[0]!.folderPath, 'alternate.exe')
  await fs.writeFile(alternateExecutable, '')
  await store.removeGame(game.id); await store.importBackup(backup)
  return { store, game: store.getGame(game.id), alternateExecutable }
}

function holdNextWrite(store: LibraryStore) {
  let release!: () => void, entered!: () => void
  const blocked = new Promise<void>(resolve => { release = resolve })
  const started = new Promise<void>(resolve => { entered = resolve })
  const originalWrite = (store as any).atomicWrite.bind(store), writes: string[] = []
  ;(store as any).atomicWrite = async (file: string, contents: string) => {
    writes.push(file)
    if (writes.length === 1) { entered(); await blocked }
    return originalWrite(file, contents)
  }
  return { release, started, writes }
}

for (const change of ['preferred installation', 'executable'] as const) {
  for (const queue of ['library save', 'delayed apply'] as const) test(`a ${change} edit rejects an automatic result waiting in the ${queue} queue`, async t => {
    const { store, game, alternateExecutable } = await localEnvironment(t)
    const target = await store.addVirtual(work('C', 'Keep separate'))
    const token = store.captureMetadataToken(game.id, 'earlier-recognition')
    const edit = () => change === 'preferred installation'
      ? store.updateGame(game.id, { preferredInstallationId: game.installations[1]!.id })
      : store.setExecutable(game.id, game.installations[0]!.id, alternateExecutable)
    const apply = () => store.linkMetadata(game.id, work('C', 'Obsolete local evidence'), {
      requestToken: token, expectedName: game.name, expectedMetadataId: 'A'
    })
    if (queue === 'library save') {
      const held = holdNextWrite(store)
      const editing = edit(); await held.started
      const rejected = assert.rejects(apply(), /名称或资料已更新/)
      held.release(); await editing; await rejected
      assert.equal(held.writes.length, 2, 'only the installation edit writes the backup and primary')
    } else {
      // Model an apply already accepted by recognition but waiting outside the
      // store, as the main process does while holding its identity lock.
      let release!: () => void
      const deferred = new Promise<void>(resolve => { release = resolve })
      const rejected = assert.rejects(deferred.then(apply), /名称或资料已更新/)
      await edit()
      const saved = await fs.readFile(store.filePath, 'utf8'), backup = await fs.readFile(store.backupPath, 'utf8')
      release(); await rejected
      assert.equal(await fs.readFile(store.filePath, 'utf8'), saved)
      assert.equal(await fs.readFile(store.backupPath, 'utf8'), backup)
    }
    const current = store.getGame(game.id)
    assert.equal(current.metadata?.id, 'A')
    assert.equal(store.getGame(target.id).metadata?.description, 'Keep separate')
    assert.equal(store.getGames().length, 2)
    if (change === 'preferred installation') assert.equal(current.preferredInstallationId, game.installations[1]!.id)
    else assert.equal(current.installations[0]!.executablePath, alternateExecutable)
    const reopened = new LibraryStore(store.dataDir); await reopened.initialize()
    assert.deepEqual(reopened.getGames(), store.getGames())
  })

  test(`a ${change} edit clears a merge target's same-batch automatic provenance`, async t => {
    const { store, game: target, alternateExecutable } = await localEnvironment(t)
    const source = await store.addVirtual(work('B'))
    const sourceToken = store.captureMetadataToken(source.id, 'same-batch')
    const targetToken = store.captureMetadataToken(target.id, 'same-batch')
    await store.linkMetadata(target.id, work('C'), { requestToken: targetToken })
    if (change === 'preferred installation') await store.updateGame(target.id, { preferredInstallationId: target.installations[1]!.id })
    else await store.setExecutable(target.id, target.installations[0]!.id, alternateExecutable)
    const saved = await fs.readFile(store.filePath, 'utf8')
    await assert.rejects(store.linkMetadata(source.id, work('C'), { requestToken: sourceToken }), /待合并.*已更新/)
    assert.equal(store.getGames().length, 2)
    assert.equal(await fs.readFile(store.filePath, 'utf8'), saved)
  })
}

test('queued launch arguments and personal edits keep an automatic token valid and survive its commit', async t => {
  const { store, game } = await localEnvironment(t)
  const token = store.captureMetadataToken(game.id, 'current-recognition')
  const held = holdNextWrite(store)
  const argumentsEdit = store.setLaunchArguments(game.id, game.installations[0]!.id, ['--windowed', 'literal title'])
  await held.started
  const personalEdit = store.updateGame(game.id, { notes: 'Keep these notes', personalRating: 0, favorite: true, categories: ['Read next'], status: 'playing' })
  const applied = store.linkMetadata(game.id, work('A', 'Fresh metadata'), { requestToken: token })
  held.release(); await argumentsEdit; await personalEdit
  const result = await applied
  assert.equal(held.writes.length, 6)
  assert.deepEqual(result.installations[0]!.arguments, ['--windowed', 'literal title'])
  assert.equal(result.notes, 'Keep these notes'); assert.equal(result.personalRating, 0)
  assert.equal(result.favorite, true); assert.equal(result.status, 'playing')
  assert.deepEqual(result.categories, ['Read next'])
  assert.equal(result.metadata?.description, 'Fresh metadata')
  const reopened = new LibraryStore(store.dataDir); await reopened.initialize()
  assert.deepEqual(reopened.getGames(), store.getGames())
})

test('reselecting the same installation and executable does not invalidate local evidence', async t => {
  const { store, game } = await localEnvironment(t)
  const token = store.captureMetadataToken(game.id)
  await store.updateGame(game.id, { preferredInstallationId: game.preferredInstallationId })
  await store.setExecutable(game.id, game.installations[0]!.id, game.installations[0]!.executablePath!)
  assert.equal(store.captureMetadataToken(game.id).generation, token.generation)
  await store.linkMetadata(game.id, work('A', 'Valid after unchanged selection'), { requestToken: token })
  assert.equal(store.getGame(game.id).metadata?.description, 'Valid after unchanged selection')
})

test('same-batch merges can accumulate three real synthetic installation records', async t => {
  const { store, game } = await environment(t)
  await store.removeGame(game.id)
  for (let index = 0; index < 3; index++) {
    const folderPath = path.join(store.dataDir, `copy-${index}`)
    await fs.mkdir(folderPath)
    const executable = path.join(folderPath, 'story.exe')
    await fs.writeFile(executable, '')
    await store.importCandidates([{ id: `candidate-${index}`, name: `Local copy ${index}`, folderPath, executables: [executable] }])
  }
  const copies = store.getGames(), tokens = copies.map(item => store.captureMetadataToken(item.id, 'all-copies'))
  for (const [index, copy] of copies.entries()) {
    await store.linkMetadata(copy.id, work('C'), { requestToken: tokens[index]! })
  }
  const result = store.getGames()[0]!
  assert.equal(store.getGames().length, 1)
  assert.equal(result.installations.length, 3)
  assert.equal(new Set(result.installations.map(item => item.folderPath)).size, 3)
  assert.equal(result.preferredInstallationId, copies[2]!.preferredInstallationId)
  const reopened = new LibraryStore(store.dataDir); await reopened.initialize()
  assert.deepEqual(reopened.getGames(), store.getGames())
})

test('an ABA binding transition rejects an old refresh inside the save queue', async t => {
  const { store, game } = await environment(t)
  const old = await begin(store, game.id, 'refresh')
  await store.linkMetadata(game.id, work('B'))
  await store.linkMetadata(game.id, work('A', 'Newest A'))
  const saved = await fs.readFile(store.filePath, 'utf8')
  await assert.rejects(store.linkMetadata(game.id, work('A', 'Obsolete A'), options(old)), /名称或资料已更新/)
  assert.equal(await fs.readFile(store.filePath, 'utf8'), saved)
  assert.equal(store.getGame(game.id).metadata?.description, 'Newest A')
})

test('a restored same-ID game rejects the previous lifecycle and preserves a merge victim', async t => {
  const { store, game } = await environment(t)
  const victim = await store.addVirtual(work('C', 'Keep this separate record'))
  const old = await begin(store, game.id, 'association')
  const backup = store.exportBackup()
  await store.removeGame(game.id); await store.importBackup(backup)
  await assert.rejects(store.linkMetadata(game.id, work('C'), options(old)), /名称或资料已更新/)
  assert.equal(store.getGame(victim.id).metadata?.id, 'C')
  assert.equal(store.getGames().length, 2)
})

test('an obsolete manual association cannot merge a victim after an ABA transition', async t => {
  const { store, game } = await environment(t)
  const victim = await store.addVirtual(work('C'))
  const old = await begin(store, game.id, 'association')
  const newer = await begin(store, game.id, 'association')
  await store.linkMetadata(game.id, work('B'), options(newer)); await finish(store, newer)
  const latest = await begin(store, game.id, 'association')
  await store.linkMetadata(game.id, work('A'), options(latest)); await finish(store, latest)
  await assert.rejects(store.linkMetadata(game.id, work('C'), options(old)), /名称或资料已更新/)
  assert.equal(store.getGame(victim.id).metadata?.id, 'C')
  assert.equal(store.getGame(game.id).metadata?.id, 'A')
})

test('only the newest requested same-ID refresh can commit, independent of completion order', async t => {
  const { store, game } = await environment(t)
  const old = await begin(store, game.id, 'refresh')
  const latest = await begin(store, game.id, 'refresh')
  await assert.rejects(store.linkMetadata(game.id, work('A', 'Older finishes first'), options(old)), /名称或资料已更新/)
  await store.linkMetadata(game.id, work('A', 'Newest request'), options(latest))
  assert.equal(store.getGame(game.id).metadata?.description, 'Newest request')
})

test('a pending explicit association blocks refresh and a failed latest request never revives an older one', async t => {
  const { store, game } = await environment(t)
  const old = await begin(store, game.id, 'refresh')
  const manual = await begin(store, game.id, 'association')
  await assert.rejects(begin(store, game.id, 'refresh'), /关联正在更新/)
  await finish(store, manual)
  await assert.rejects(store.linkMetadata(game.id, work('A', 'Obsolete fallback'), options(old)), /名称或资料已更新/)
  const current = await begin(store, game.id, 'refresh')
  await store.linkMetadata(game.id, work('A', 'Explicit new refresh'), options(current))
})

test('metadata request tokens from a previous store session cannot be replayed after reopening', async t => {
  const { store, game } = await environment(t)
  const old = await begin(store, game.id, 'refresh')
  const reopened = new LibraryStore(store.dataDir); await reopened.initialize()
  await assert.rejects(reopened.linkMetadata(game.id, work('A', 'Previous session'), options(old)), /名称或资料已更新/)
})


test('generation is rechecked after earlier queued ABA writes, before an obsolete result can merge', async t => {
  const { store, game } = await environment(t)
  const victim = await store.addVirtual(work('C'))
  const old = await begin(store, game.id, 'refresh')
  const moving = store.linkMetadata(game.id, work('B'))
  const returning = store.linkMetadata(game.id, work('A', 'Queued latest A'))
  const rejected = assert.rejects(store.linkMetadata(game.id, work('C'), options(old)), /名称或资料已更新/)
  await moving; await returning; await rejected
  assert.equal(store.getGame(game.id).metadata?.description, 'Queued latest A')
  assert.equal(store.getGame(victim.id).metadata?.id, 'C')
})

test('an automated snapshot cannot bypass a manual ABA transition', async t => {
  const { store, game } = await environment(t)
  const token = store.captureMetadataToken(game.id)
  await store.linkMetadata(game.id, work('B')); await store.linkMetadata(game.id, work('A', 'Manual latest'))
  await assert.rejects(store.linkMetadata(game.id, work('A', 'Obsolete automated result'), { requestToken: token }), /名称或资料已更新/)
  assert.equal(store.getGame(game.id).metadata?.description, 'Manual latest')
})

test('automatic capture during an explicit source refresh cannot preempt that pending refresh', async t => {
  const { store, game } = await environment(t)
  const manual = await begin(store, game.id, 'refresh')
  const automatic = store.captureMetadataToken(game.id, 'later-automatic-job')
  const before = store.getGames(), saved = await fs.readFile(store.filePath, 'utf8'), backup = await fs.readFile(store.backupPath, 'utf8')
  await assert.rejects(store.linkMetadata(game.id, work('A', 'Automatic response'), { requestToken: automatic }), /名称或资料已更新/)
  assert.deepEqual(store.getGames(), before)
  assert.equal(await fs.readFile(store.filePath, 'utf8'), saved)
  assert.equal(await fs.readFile(store.backupPath, 'utf8'), backup)
  await store.linkMetadata(game.id, work('A', 'Explicit refresh result'), options(manual)); await finish(store, manual)
  assert.equal(store.getGame(game.id).metadata?.description, 'Explicit refresh result')
})

test('automatic capture after a completed source refresh remains valid despite its historical request ID', async t => {
  const { store, game } = await environment(t)
  const manual = await begin(store, game.id, 'refresh')
  await store.linkMetadata(game.id, work('A', 'Completed explicit refresh'), options(manual)); await finish(store, manual)
  const automatic = store.captureMetadataToken(game.id, 'later-automatic-job')
  await store.linkMetadata(game.id, work('A', 'New automatic response'), { requestToken: automatic })
  assert.equal(store.getGame(game.id).metadata?.description, 'New automatic response')
})

test('a finished unsuccessful explicit refresh cannot reuse its token to commit a late response', async t => {
  const { store, game } = await environment(t)
  const failed = await begin(store, game.id, 'refresh')
  await finish(store, failed)
  await assert.rejects(store.linkMetadata(game.id, work('A', 'Late finished response'), options(failed)), /名称或资料已更新/)
  assert.equal(store.getGame(game.id).metadata?.description, 'A')
})

test('an older association cannot delete a merge target with a newer association pending', async t => {
  const { store, game } = await environment(t)
  const target = await store.addVirtual(work('C'))
  const older = await begin(store, game.id, 'association')
  const newer = await begin(store, target.id, 'association')
  const before = store.getGames(), saved = await fs.readFile(store.filePath, 'utf8'), backup = await fs.readFile(store.backupPath, 'utf8')
  await assert.rejects(store.linkMetadata(game.id, work('C', 'Obsolete response'), options(older)), /待合并.*正在更新/)
  assert.deepEqual(store.getGames(), before)
  assert.equal(await fs.readFile(store.filePath, 'utf8'), saved)
  assert.equal(await fs.readFile(store.backupPath, 'utf8'), backup)
  await finish(store, older)
  await store.linkMetadata(target.id, work('D', 'Latest explicit choice'), options(newer)); await finish(store, newer)
  assert.equal(store.getGame(target.id).metadata?.id, 'D')
  assert.equal(store.getGame(game.id).metadata?.id, 'A')
})

test('a pending target refresh blocks merging but a completed earlier refresh does not stay busy', async t => {
  const { store, game } = await environment(t)
  const target = await store.addVirtual(work('C'))
  const refresh = await begin(store, target.id, 'refresh')
  const association = await begin(store, game.id, 'association')
  await assert.rejects(store.linkMetadata(game.id, work('C'), options(association)), /待合并.*正在更新/)
  await finish(store, refresh) // An earlier failed request is no longer in flight.
  const merged = await store.linkMetadata(game.id, work('C'), options(association)); await finish(store, association)
  assert.equal(merged.metadata?.id, 'C')
  assert.equal(store.getGames().length, 1)
})

test('a completed newer target refresh cannot be overwritten by an older source response', async t => {
  const { store, game } = await environment(t)
  const target = await store.addVirtual(work('C'))
  const older = await begin(store, game.id, 'association')
  const newer = await begin(store, target.id, 'refresh')
  await store.linkMetadata(target.id, work('C', 'Fresh target metadata'), options(newer)); await finish(store, newer)
  await assert.rejects(store.linkMetadata(game.id, work('C', 'Stale response'), options(older)), /待合并.*已更新/)
  assert.equal(store.getGame(target.id).metadata?.description, 'Fresh target metadata')
  assert.equal(store.getGames().length, 2)
  await finish(store, older)
  const current = await begin(store, game.id, 'association')
  const merged = await store.linkMetadata(game.id, work('C', 'Fresh response from the new request'), options(current)); await finish(store, current)
  assert.equal(merged.metadata?.description, 'Fresh response from the new request')
  assert.equal(store.getGames().length, 1)
})

for (const kind of ['refresh', 'association'] as const) test(`a newer failed target ${kind} does not revive an obsolete source merge`, async t => {
  const { store, game } = await environment(t)
  const target = await store.addVirtual(work('C'))
  const older = await begin(store, game.id, 'association')
  const newer = await begin(store, target.id, kind)
  await finish(store, newer)
  await assert.rejects(store.linkMetadata(game.id, work('C'), options(older)), /待合并.*已更新/)
  assert.equal(store.getGames().length, 2)
  assert.equal(store.getGame(target.id).metadata?.id, 'C')
})

test('automatic source snapshots protect a restored same-ID merge target and a newly created target', async t => {
  const { store, game } = await environment(t)
  const target = await store.addVirtual(work('C'))
  const beforeRestore = store.captureMetadataToken(game.id), backup = store.exportBackup()
  await store.removeGame(target.id); await store.importBackup(backup)
  await assert.rejects(store.linkMetadata(game.id, work('C'), { requestToken: beforeRestore }), /待合并.*已更新/)
  assert.equal(store.getGame(target.id).metadata?.id, 'C')
  const beforeCreation = store.captureMetadataToken(game.id)
  const created = await store.addVirtual(work('D'))
  await assert.rejects(store.linkMetadata(game.id, work('D'), { requestToken: beforeCreation }), /待合并.*已更新/)
  assert.equal(store.getGame(created.id).metadata?.id, 'D')
  assert.equal(store.getGames().length, 3)
})

test('a valid merge retains newer personal fields, installations and sessions without treating them as identity changes', async t => {
  const { store, game } = await environment(t)
  const target = await store.addVirtual(work('C'))
  const initial = store.exportBackup()
  for (const [i, entry] of initial.games.entries()) {
    entry.installations = [{ id: `installation-${i}`, folderPath: path.join(store.dataDir, `inert-installation-${i}`), executablePath: null, arguments: [] }]
    entry.preferredInstallationId = entry.installations[0]!.id
  }
  await store.removeGame(game.id); await store.removeGame(target.id); await store.importBackup(initial)
  const oldRefresh = await begin(store, target.id, 'refresh')
  await store.linkMetadata(target.id, work('C', 'Already refreshed before association'), options(oldRefresh)); await finish(store, oldRefresh)
  const request = await begin(store, game.id, 'association')
  await store.updateGame(game.id, { notes: 'Source notes', categories: ['source'], customTags: ['source tag'] })
  await store.updateGame(target.id, { notes: 'New target notes', categories: ['target'], customTags: ['target tag'], favorite: true, personalRating: 9, status: 'completed' })
  await store.recordSession(game.id, { id: 'source-session', startedAt: '2026-10-03T01:00:00.000Z', endedAt: '2026-10-03T01:00:10.000Z', seconds: 10 })
  await store.recordSession(target.id, { id: 'target-session', startedAt: '2026-10-03T02:00:00.000Z', endedAt: '2026-10-03T02:00:20.000Z', seconds: 20 })
  const merged = await store.linkMetadata(game.id, work('C'), options(request)); await finish(store, request)
  assert.equal(merged.notes, 'Source notes\n\nNew target notes')
  assert.deepEqual(merged.categories, ['source', 'target'])
  assert.deepEqual(merged.customTags, ['source tag', 'target tag'])
  assert.equal(merged.favorite, true); assert.equal(merged.personalRating, 9); assert.equal(merged.status, 'completed')
  assert.equal(merged.installations.length, 2); assert.equal(merged.sessions.length, 2)
  assert.equal(merged.totalPlaySeconds, 30); assert.equal(merged.playCount, 2)
  const reopened = new LibraryStore(store.dataDir); await reopened.initialize()
  assert.deepEqual(reopened.getGames(), store.getGames())
})

test('an old refresh completion cannot clear a newer pending target refresh', async t => {
  const { store, game } = await environment(t)
  const target = await store.addVirtual(work('C'))
  const first = await begin(store, target.id, 'refresh'), second = await begin(store, target.id, 'refresh')
  const source = await begin(store, game.id, 'association')
  await finish(store, first)
  await assert.rejects(store.linkMetadata(game.id, work('C'), options(source)), /待合并.*正在更新/)
  await finish(store, second)
  await store.linkMetadata(game.id, work('C'), options(source))
  assert.equal(store.getGames().length, 1)
})

test('cancellation while linkMetadata waits in the save queue prevents every write and merge', async t => {
  const { store, game } = await environment(t)
  const target = await store.addVirtual(work('C'))
  const request = await begin(store, game.id, 'association')
  const controller = new AbortController()
  let release!: () => void, entered!: () => void
  const blocked = new Promise<void>(resolve => { release = resolve }), started = new Promise<void>(resolve => { entered = resolve })
  const originalWrite = (store as any).atomicWrite.bind(store)
  const writes: string[] = []
  ;(store as any).atomicWrite = async (file: string, contents: string) => {
    writes.push(file)
    if (writes.length === 1) { entered(); await blocked }
    return originalWrite(file, contents)
  }
  const personalEdit = store.updateGame(game.id, { notes: 'Keep this earlier edit' })
  await started
  const rejected = assert.rejects(store.linkMetadata(game.id, work('C'), { ...options(request), signal: controller.signal }), { name: 'AbortError' })
  controller.abort(); release()
  await personalEdit; await rejected; await finish(store, request)
  assert.equal(writes.length, 2, 'only the earlier edit writes its backup and primary document')
  assert.equal(store.getGame(game.id).notes, 'Keep this earlier edit')
  assert.equal(store.getGame(game.id).metadata?.id, 'A')
  assert.equal(store.getGame(target.id).metadata?.id, 'C')
  assert.equal(store.getGames().length, 2)
  const reopened = new LibraryStore(store.dataDir); await reopened.initialize()
  assert.deepEqual(reopened.getGames(), store.getGames())
})

test('cancellation after persistence starts completes one consistent atomic transaction', async t => {
  const { store, game } = await environment(t)
  const target = await store.addVirtual(work('C'))
  const request = await begin(store, game.id, 'association')
  const controller = new AbortController(), originalWrite = (store as any).atomicWrite.bind(store)
  let writes = 0
  ;(store as any).atomicWrite = async (file: string, contents: string) => {
    if (++writes === 1) controller.abort()
    return originalWrite(file, contents)
  }
  const merged = await store.linkMetadata(game.id, work('C'), { ...options(request), signal: controller.signal }); await finish(store, request)
  assert.equal(controller.signal.aborted, true); assert.equal(writes, 2)
  assert.equal(merged.metadata?.id, 'C'); assert.equal(store.getGames().length, 1)
  assert.throws(() => store.getGame(target.id), /游戏不存在/)
  const reopened = new LibraryStore(store.dataDir); await reopened.initialize()
  assert.deepEqual(reopened.getGames(), store.getGames())
})

test('the same automatic batch merges duplicate entries and retains both current personal records', async t => {
  const { store, game } = await environment(t)
  const second = await store.addVirtual(work('B'))
  const firstToken = store.captureMetadataToken(game.id, 'batch-one'), secondToken = store.captureMetadataToken(second.id, 'batch-one')
  await store.linkMetadata(game.id, work('C'), { requestToken: firstToken })
  await store.updateGame(game.id, { notes: 'First installation notes', categories: ['first'] })
  await store.updateGame(second.id, { notes: 'Second installation notes', categories: ['second'] })
  const merged = await store.linkMetadata(second.id, work('C'), { requestToken: secondToken, preserveMergedName: true })
  assert.equal(store.getGames().length, 1)
  assert.equal(merged.id, second.id)
  assert.equal(merged.metadata?.id, 'C')
  assert.equal(merged.notes, 'Second installation notes\n\nFirst installation notes')
  assert.deepEqual(merged.categories, ['second', 'first'])
})

test('different automatic batches cannot overwrite one another through merging', async t => {
  const { store, game } = await environment(t)
  const target = await store.addVirtual(work('B'))
  const sourceToken = store.captureMetadataToken(game.id, 'old-batch'), targetToken = store.captureMetadataToken(target.id, 'new-batch')
  await store.linkMetadata(target.id, work('C', 'New batch data'), { requestToken: targetToken })
  await assert.rejects(store.linkMetadata(game.id, work('C', 'Old batch data'), { requestToken: sourceToken }), /待合并.*已更新/)
  assert.equal(store.getGame(target.id).metadata?.description, 'New batch data')
  assert.equal(store.getGames().length, 2)
})

for (const intervention of ['refresh', 'association', 'rename', 'restore'] as const) test(`a ${intervention} after an automatic commit removes permission for same-batch sibling merging`, async t => {
  const { store, game } = await environment(t)
  const target = await store.addVirtual(work('B'))
  const sourceToken = store.captureMetadataToken(game.id, 'same-batch'), targetToken = store.captureMetadataToken(target.id, 'same-batch')
  await store.linkMetadata(target.id, work('C'), { requestToken: targetToken })
  if (intervention === 'refresh' || intervention === 'association') {
    const explicit = await begin(store, target.id, intervention)
    await assert.rejects(store.linkMetadata(game.id, work('C'), { requestToken: sourceToken }), /待合并.*正在更新/)
    await finish(store, explicit) // Even a failed latest explicit choice ends the batch exception.
  } else if (intervention === 'rename') {
    await store.updateGame(target.id, { name: 'Newest user title' })
  } else {
    const backup = store.exportBackup(); await store.removeGame(target.id); await store.importBackup(backup)
  }
  await assert.rejects(store.linkMetadata(game.id, work('C'), { requestToken: sourceToken }), /待合并.*已更新/)
  assert.equal(store.getGames().length, 2)
  assert.equal(store.getGame(target.id).metadata?.id, 'C')
})
