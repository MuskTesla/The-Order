import assert from 'node:assert/strict'
import { test } from 'node:test'
import { promises as fs } from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { LibraryStore, scanFolder } from '../electron/library'
import type { ScanCandidate, WorkMetadata } from '../src/shared/types'

const work = (id = 'synthetic-work'): WorkMetadata => ({
  id, title: '合成作品', originalTitle: '', aliases: [], coverUrl: null, bannerUrl: null, description: '合成资料', releaseDate: null,
  releaseDatePrecision: null, developers: [], tags: [], ratings: [], characters: [], references: [], webUrl: null, contentRating: null, fetchedAt: '2026-10-03T00:00:00.000Z'
})
async function environment(t: { after: (callback: () => Promise<void>) => void }) {
  const temporaryRoot = path.resolve(os.tmpdir())
  const root = await fs.mkdtemp(path.join(temporaryRoot, 'the-order-attachment-'))
  t.after(async () => {
    assert.equal(path.dirname(path.resolve(root)), temporaryRoot)
    assert.ok(path.basename(root).startsWith('the-order-attachment-'))
    await fs.rm(root, { recursive: true, force: true })
  })
  const store = new LibraryStore(path.join(root, 'profile')); await store.initialize()
  return { root, store }
}
async function candidate(root: string, name: string, filenames = ['Game.exe']): Promise<ScanCandidate> {
  const folderPath = path.join(root, name), executables: string[] = []
  await fs.mkdir(folderPath, { recursive: true })
  for (const filename of filenames) {
    const file = path.join(folderPath, filename)
    await fs.mkdir(path.dirname(file), { recursive: true })
    await fs.writeFile(file, 'inert synthetic fixture; never executed')
    executables.push(file)
  }
  return { id: `candidate-${name}`, name, folderPath, executables }
}
const gate = () => {
  let resolve!: () => void
  const promise = new Promise<void>(done => { resolve = done })
  return { promise, resolve }
}

for (const files of [['Launcher.exe'], [path.join('bin', 'Game.exe')]]) test(`attaching an explicit root with ${files[0]} preserves the existing work, personal fields and saved identity`, async t => {
  const { root, store } = await environment(t), item = await store.addVirtual(work())
  await store.updateGame(item.id, { name: '我的名称', status: 'paused', personalRating: 0, favorite: true, categories: ['个人分类'], customTags: ['个人标签'], notes: '个人笔记' })
  await store.recordSession(item.id, { id: 'synthetic-session', startedAt: '2026-10-03T00:00:00Z', endedAt: '2026-10-03T00:00:30Z', seconds: 30 })
  const before = store.getGame(item.id), selected = await candidate(root, 'Explicit game root', files)
  const attached = await store.attachInstallation(item.id, selected, store.captureInstallationAttachmentToken(item.id))
  const { installations, preferredInstallationId, ...personal } = attached
  const { installations: oldInstallations, preferredInstallationId: oldPreferred, ...oldPersonal } = before
  assert.deepEqual(personal, oldPersonal)
  assert.equal(oldInstallations.length, 0); assert.equal(oldPreferred, null)
  assert.equal(installations.length, 1)
  assert.equal(installations[0]!.folderPath, selected.folderPath)
  assert.equal(installations[0]!.executablePath, selected.executables[0])
  assert.equal(preferredInstallationId, installations[0]!.id)
  assert.equal(store.getGames().length, 1)
  const reopened = new LibraryStore(store.dataDir); await reopened.initialize()
  assert.deepEqual(reopened.getGame(item.id), attached)
  const restored = new LibraryStore(path.join(root, 'restored')); await restored.initialize(); await restored.importBackup(store.exportBackup())
  assert.deepEqual(restored.getGame(item.id), attached)
  const scan = await scanFolder(selected.folderPath), saved = await fs.readFile(store.filePath, 'utf8')
  if (files[0] === 'Launcher.exe') assert.deepEqual(await store.importCandidates(scan), { added: 0, skipped: 1 })
  else {
    assert.equal(scan[0]!.folderPath, path.join(selected.folderPath, 'bin'))
    await assert.rejects(store.importCandidates(scan), /范围重叠.*不能重复导入/)
    assert.equal(await fs.readFile(store.filePath, 'utf8'), saved)
  }
  assert.equal(store.getGames().length, 1)
})

test('configuring a second installation preserves the configured default, explicit default selection still works, and a missing default is repaired', async t => {
  const { root, store } = await environment(t), item = await store.addVirtual(work())
  const first = await candidate(root, 'First'), second = await candidate(root, 'Second', ['Game.exe', 'Alternate.exe'])
  await store.attachInstallation(item.id, first)
  const attached = await store.attachInstallation(item.id, second), [one, two] = attached.installations
  assert.equal(attached.preferredInstallationId, one!.id); assert.equal(two!.executablePath, null)
  await store.setLaunchArguments(item.id, two!.id, ['--personal'])
  const token = store.captureMetadataToken(item.id)
  const configured = await store.setExecutable(item.id, two!.id, second.executables[0]!)
  assert.equal(configured.preferredInstallationId, one!.id)
  assert.deepEqual(configured.installations[1]!.arguments, ['--personal'])
  await assert.rejects(store.linkMetadata(item.id, work('obsolete-result'), { requestToken: token }), /名称或资料已更新/)
  assert.equal((await store.updateGame(item.id, { preferredInstallationId: two!.id })).preferredInstallationId, two!.id)
  await store.setExecutable(item.id, one!.id, first.executables[0]!)
  assert.equal(store.getGame(item.id).preferredInstallationId, two!.id)
  await store.updateGame(item.id, { preferredInstallationId: null })
  assert.equal((await store.setExecutable(item.id, one!.id, first.executables[0]!)).preferredInstallationId, one!.id)
})

test('the first selected executable replaces an unconfigured default while a later attachment preserves it', async t => {
  const { root, store } = await environment(t), item = await store.addVirtual(work())
  const unresolved = await store.attachInstallation(item.id, await candidate(root, 'Unresolved', ['Game.exe', 'Alternate.exe']))
  assert.equal(unresolved.preferredInstallationId, unresolved.installations[0]!.id)
  assert.equal(unresolved.installations[0]!.executablePath, null)
  const playable = await store.attachInstallation(item.id, await candidate(root, 'Playable'))
  assert.equal(playable.preferredInstallationId, playable.installations[1]!.id)
  const next = await store.attachInstallation(item.id, await candidate(root, 'Another'))
  assert.equal(next.preferredInstallationId, playable.preferredInstallationId)
})

for (const relation of ['same', 'parent', 'child'] as const) for (const sameGame of [true, false]) test(`an attachment rejects a ${relation} directory ${sameGame ? 'on the same item' : 'owned by another item'} without saving any part`, async t => {
  const { root, store } = await environment(t), owner = await store.addVirtual(work('owner'))
  const owned = await candidate(root, 'Game'), nested = await candidate(owned.folderPath, 'bin')
  const initial = relation === 'parent' ? nested : owned
  await store.attachInstallation(owner.id, initial)
  const target = sameGame ? owner : await store.addVirtual(work('target'))
  const selected = relation === 'child' ? nested : owned
  const before = store.getGames(), disk = await fs.readFile(store.filePath, 'utf8')
  await assert.rejects(store.attachInstallation(target.id, selected), relation === 'same' ? /不能重复添加|已属于/ : /范围重叠/)
  assert.deepEqual(store.getGames(), before)
  assert.equal(await fs.readFile(store.filePath, 'utf8'), disk)
})

test('boundary, executable type and bounded list failures leave the original record intact', async t => {
  const { root, store } = await environment(t), item = await store.addVirtual(work())
  const selected = await candidate(root, 'Game'), outside = await candidate(root, 'Outside'), textFile = path.join(selected.folderPath, 'readme.txt')
  const fakeDirectory = path.join(selected.folderPath, 'Directory.exe'), installer = path.join(selected.folderPath, 'setup.exe')
  await fs.writeFile(textFile, 'fixture'); await fs.mkdir(fakeDirectory); await fs.writeFile(installer, 'fixture')
  const disk = await fs.readFile(store.filePath, 'utf8'), before = store.getGame(item.id)
  for (const files of [outside.executables, [textFile], [fakeDirectory], [installer], [], Array(201).fill(selected.executables[0])]) {
    await assert.rejects(store.attachInstallation(item.id, { ...selected, executables: files }), /启动文件|候选/)
    assert.deepEqual(store.getGame(item.id), before)
    assert.equal(await fs.readFile(store.filePath, 'utf8'), disk)
  }
  await assert.rejects(store.attachInstallation(item.id, { ...selected, folderPath: 'relative' }), /绝对路径/)
  await assert.rejects(store.attachInstallation(item.id, { ...selected, executables: [path.join(selected.folderPath, '..', 'Outside', 'Game.exe')] }), /根目录内/)
  await store.attachInstallation(item.id, selected)
  assert.equal(store.getGame(item.id).installations.length, 1, 'rejected transactions do not poison the queue')
})

test('an executable reached through a directory junction cannot escape the selected root', async t => {
  const { root, store } = await environment(t), item = await store.addVirtual(work())
  const selected = await candidate(root, 'Game'), outside = await candidate(root, 'Outside'), link = path.join(selected.folderPath, 'linked')
  try { await fs.symlink(outside.folderPath, link, process.platform === 'win32' ? 'junction' : 'dir') }
  catch (error) { if (['EPERM', 'EACCES'].includes((error as NodeJS.ErrnoException).code ?? '')) { t.skip('test account cannot create links'); return } throw error }
  await assert.rejects(store.attachInstallation(item.id, { ...selected, executables: [path.join(link, 'Game.exe')] }), /根目录内/)
  assert.equal(store.getGame(item.id).installations.length, 0)
})

test('a canonical alias cannot duplicate a previously attached directory', async t => {
  const { root, store } = await environment(t), item = await store.addVirtual(work()), selected = await candidate(root, 'Game')
  await store.attachInstallation(item.id, selected)
  const link = path.join(root, 'Alias')
  try { await fs.symlink(selected.folderPath, link, process.platform === 'win32' ? 'junction' : 'dir') }
  catch (error) { if (['EPERM', 'EACCES'].includes((error as NodeJS.ErrnoException).code ?? '')) { t.skip('test account cannot create links'); return } throw error }
  await assert.rejects(store.attachInstallation(item.id, { ...selected, folderPath: link, executables: [path.join(link, 'Game.exe')] }), /不能重复添加/)
  assert.equal(store.getGame(item.id).installations.length, 1)
})

for (const change of ['rename', 'binding', 'installation', 'delete-restore'] as const) test(`a queued attachment rejects ${change} changes, including a restored identical ID`, async t => {
  const { root, store } = await environment(t), item = await store.addVirtual(work()), selected = await candidate(root, 'Game')
  const token = store.captureInstallationAttachmentToken(item.id), backup = store.exportBackup()
  const changes = change === 'rename' ? store.updateGame(item.id, { name: '后来修改的名称' })
    : change === 'binding' ? store.linkMetadata(item.id, work('new-binding'))
      : change === 'installation' ? store.attachInstallation(item.id, await candidate(root, 'Earlier installation'))
        : Promise.all([store.removeGame(item.id), store.importBackup(backup)])
  // Delete and restore are queued ahead of the attachment. The restored row is
  // byte-identical, so a simple field comparison would accept the old operation.
  const rejected = assert.rejects(store.attachInstallation(item.id, selected, token), /名称、关联或安装已变化/)
  await changes; await rejected
  assert.equal(store.getGame(item.id).installations.length, change === 'installation' ? 1 : 0)
})

test('later personal and launch-argument edits coexist with a queued valid attachment', async t => {
  const { root, store } = await environment(t), item = await store.addVirtual(work()), existing = await candidate(root, 'Existing')
  const installed = await store.attachInstallation(item.id, existing), token = store.captureInstallationAttachmentToken(item.id)
  const selected = await candidate(root, 'New'), personal = store.updateGame(item.id, { notes: '刚写的笔记', personalRating: 9, favorite: true })
  const argumentsEdit = store.setLaunchArguments(item.id, installed.installations[0]!.id, ['--later-edit'])
  const result = await store.attachInstallation(item.id, selected, token)
  await personal; await argumentsEdit
  assert.equal(result.notes, '刚写的笔记'); assert.equal(result.personalRating, 9); assert.equal(result.favorite, true)
  assert.deepEqual(result.installations[0]!.arguments, ['--later-edit'])
  assert.equal(result.preferredInstallationId, installed.preferredInstallationId)
  assert.equal(result.installations.length, 2)
})

test('an attachment snapshots the authorized candidate before waiting for an earlier save', async t => {
  const { root, store } = await environment(t), item = await store.addVirtual(work()), selected = await candidate(root, 'Authorized')
  const entered = gate(), release = gate(), runtime = store as unknown as { atomicWrite(target: string, contents: string): Promise<void> }
  const original = runtime.atomicWrite.bind(store)
  let paused = false
  runtime.atomicWrite = async (target, contents) => { if (!paused) { paused = true; entered.resolve(); await release.promise } await original(target, contents) }
  t.after(async () => { release.resolve(); runtime.atomicWrite = original })
  const edit = store.updateGame(item.id, { notes: '保持这条编辑' }); await entered.promise
  const pending = store.attachInstallation(item.id, selected)
  selected.folderPath = path.join(root, 'Unapproved'); selected.executables[0] = path.join(root, 'Unapproved', 'Injected.exe')
  release.resolve(); await edit
  const attached = await pending
  assert.equal(attached.installations[0]!.folderPath, path.join(root, 'Authorized'))
  assert.equal(attached.notes, '保持这条编辑')
})

test('failed attachment persistence rolls back without consuming its identity token; a retry remains valid', async t => {
  const { root, store } = await environment(t), item = await store.addVirtual(work()), selected = await candidate(root, 'Game')
  const token = store.captureInstallationAttachmentToken(item.id), before = store.getGame(item.id)
  const runtime = store as unknown as { atomicWrite(target: string, contents: string): Promise<void> }, original = runtime.atomicWrite.bind(store)
  runtime.atomicWrite = async (target, contents) => { if (target === store.filePath) throw new Error('synthetic persistence failure'); await original(target, contents) }
  try { await assert.rejects(store.attachInstallation(item.id, selected, token), /synthetic persistence failure/) }
  finally { runtime.atomicWrite = original }
  assert.deepEqual(store.getGame(item.id), before)
  const reopened = new LibraryStore(store.dataDir); await reopened.initialize(); assert.deepEqual(reopened.getGame(item.id), before)
  assert.equal((await store.attachInstallation(item.id, selected, token)).installations.length, 1)
})

for (const cancelAfterPersistenceStarts of [false, true]) test(`attachment cancellation ${cancelAfterPersistenceStarts ? 'after persistence starts completes the atomic save' : 'while queued prevents any attachment save'}`, async t => {
  const { root, store } = await environment(t), item = await store.addVirtual(work()), selected = await candidate(root, 'Game')
  const entered = gate(), release = gate(), runtime = store as unknown as { atomicWrite(target: string, contents: string): Promise<void> }
  const original = runtime.atomicWrite.bind(store)
  let held = false, cancelled = false, checks = 0
  runtime.atomicWrite = async (target, contents) => { if (!held) { held = true; entered.resolve(); await release.promise } await original(target, contents) }
  t.after(async () => { release.resolve(); runtime.atomicWrite = original })
  let earlier
  if (!cancelAfterPersistenceStarts) { earlier = store.updateGame(item.id, { notes: 'Earlier queued edit' }); await entered.promise }
  const attachment = store.attachInstallation(item.id, selected, undefined, () => { checks++; if (cancelled) throw Error('synthetic attachment cancelled') })
  // Persistence already began in the success branch. Cancellation must not
  // cause the durable write to succeed while memory incorrectly rolls back.
  if (cancelAfterPersistenceStarts) await entered.promise
  cancelled = true
  const rejected = !cancelAfterPersistenceStarts ? assert.rejects(attachment, /synthetic attachment cancelled/) : undefined
  release.resolve(); await earlier
  if (rejected) await rejected
  else assert.equal((await attachment).installations.length, 1)
  assert.equal(checks, 1)
  const reopened = new LibraryStore(store.dataDir); await reopened.initialize()
  assert.deepEqual(reopened.getGames(), store.getGames())
  assert.equal(store.getGame(item.id).installations.length, cancelAfterPersistenceStarts ? 1 : 0)
})

test('a root replaced by a junction while the attachment waits in the save queue loses its earlier authorization', async t => {
  const { root, store } = await environment(t), item = await store.addVirtual(work()), selected = await candidate(root, 'Authorized'), outside = await candidate(root, 'Outside')
  const entered = gate(), release = gate(), runtime = store as unknown as { atomicWrite(target: string, contents: string): Promise<void> }, original = runtime.atomicWrite.bind(store)
  let paused = false
  runtime.atomicWrite = async (target, contents) => { if (!paused) { paused = true; entered.resolve(); await release.promise } await original(target, contents) }
  t.after(async () => { release.resolve(); runtime.atomicWrite = original })
  const earlier = store.updateGame(item.id, { notes: 'Earlier saved edit' }); await entered.promise
  const attachment = store.attachInstallation(item.id, selected, undefined, undefined, selected.folderPath)
  const rejected = assert.rejects(attachment, /实际位置已变化/)
  await fs.rename(selected.folderPath, `${selected.folderPath}-original`)
  try { await fs.symlink(outside.folderPath, selected.folderPath, process.platform === 'win32' ? 'junction' : 'dir') }
  catch (error) {
    if (['EPERM', 'EACCES'].includes((error as NodeJS.ErrnoException).code ?? '')) { await fs.rename(`${selected.folderPath}-original`, selected.folderPath); release.resolve(); await earlier; await attachment; await rejected.catch(() => undefined); t.skip('test account cannot create links'); return }
    throw error
  }
  release.resolve(); await earlier; await rejected
  assert.equal(store.getGame(item.id).installations.length, 0)
  assert.equal(store.getGame(item.id).notes, 'Earlier saved edit')
  const reopened = new LibraryStore(store.dataDir); await reopened.initialize(); assert.deepEqual(reopened.getGames(), store.getGames())
})
