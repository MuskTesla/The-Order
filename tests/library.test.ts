import assert from 'node:assert/strict'
import { test } from 'node:test'
import { promises as fs } from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { LibraryStore, scanFolder } from '../electron/library'
import type { WorkMetadata } from '../src/shared/types'

function work(id = 'work-1'): WorkMetadata {
  return { id, title: '测试作品', originalTitle: 'Original title', aliases: [], coverUrl: null, bannerUrl: null, description: '作品简介', releaseDate: null, releaseDatePrecision: null, developers: ['开发商'], tags: [], ratings: [], characters: [], references: [], webUrl: null, contentRating: null, fetchedAt: '2026-10-02T00:00:00.000Z' }
}
async function environment(t: { after: (callback: () => Promise<void>) => void }) {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'kun-manager-test-'))
  t.after(async () => { await fs.rm(root, { recursive: true, force: true }) })
  const store = new LibraryStore(path.join(root, 'data')); await store.initialize()
  return { root, store }
}

test('first manual association preserves a renamed local game and its personal data after restart', async t => {
  const { root, store } = await environment(t)
  const folder = path.join(root, 'Ordinary local folder')
  await fs.mkdir(folder); await fs.writeFile(path.join(folder, 'Game.exe'), 'inert fixture')
  await store.importCandidates(await scanFolder(folder))
  const item = store.getGames()[0]!
  await store.updateGame(item.id, { name: 'User confirmed edition', notes: 'Saved note', personalRating: 8, categories: ['Mine'], customTags: ['Personal'] })
  const linked = await store.linkMetadata(item.id, work())
  assert.equal(linked.name, 'User confirmed edition')
  assert.equal(linked.notes, 'Saved note')
  assert.equal(linked.personalRating, 8)
  const reopened = new LibraryStore(store.dataDir); await reopened.initialize()
  assert.deepEqual(reopened.getGames(), store.getGames())
})

test('legacy local custom and edition names survive association while ordinary default names follow the catalog', async t => {
  const { root, store } = await environment(t)
  for (const name of ['Ordinary folder', 'Synthetic Voyage REBOOT']) {
    const folder = path.join(root, name)
    await fs.mkdir(folder); await fs.writeFile(path.join(folder, 'Game.exe'), 'inert fixture')
    await store.importCandidates(await scanFolder(folder))
  }
  const ordinary = store.getGames().find(g => g.name === 'Ordinary folder')!
  await store.updateGame(ordinary.id, { notes: 'A note is not a rename' })
  assert.equal((await store.linkMetadata(ordinary.id, work('ordinary'))).name, '测试作品')
  assert.equal((await store.linkMetadata(ordinary.id, { ...work('ordinary'), title: 'Updated catalog title' })).name, 'Updated catalog title')
  const edition = store.getGames().find(g => g.name === 'Synthetic Voyage REBOOT')!
  assert.equal((await store.linkMetadata(edition.id, work('edition'))).name, edition.name)
  const backup = store.exportBackup()
  const legacy = backup.games.find(g => g.id === edition.id)!
  legacy.metadata = null; legacy.name = 'Legacy user title'
  const imported = new LibraryStore(path.join(root, 'legacy')); await imported.initialize()
  await imported.importBackup(backup)
  assert.equal((await imported.linkMetadata(edition.id, work('legacy'))).name, 'Legacy user title')
})

test('a user rename matching the current catalog title remains confirmed across later refresh and backup', async t => {
  const { root, store } = await environment(t)
  const item = await store.addVirtual(work())
  await store.updateGame(item.id, { name: 'A custom choice' })
  await store.updateGame(item.id, { name: work().title })
  await store.linkMetadata(item.id, { ...work(), title: 'Later catalog correction' })
  assert.equal(store.getGame(item.id).name, work().title)
  const restored = new LibraryStore(path.join(root, 'restored')); await restored.initialize()
  await restored.importBackup(store.exportBackup())
  await restored.linkMetadata(item.id, { ...work(), title: 'Another catalog correction' })
  assert.equal(restored.getGame(item.id).name, work().title)
})

test('the queued expected association rejects a cleared binding before any merge or saved-data change', async t => {
  const { store } = await environment(t)
  const item = await store.addVirtual(work('old'))
  const backup = store.exportBackup(); backup.games[0]!.metadata = null
  await store.removeGame(item.id); await store.importBackup(backup)
  const before = await fs.readFile(store.filePath, 'utf8')
  await assert.rejects(store.linkMetadata(item.id, work('old'), { expectedName: item.name, expectedMetadataId: 'old' }), /名称或资料已更新/)
  assert.equal(await fs.readFile(store.filePath, 'utf8'), before)
  assert.equal(store.getGame(item.id).metadata, null)
})

test('mutations serialize, save atomically, clone values, and preserve manual metadata', async t => {
  const { root, store } = await environment(t)
  const first = await store.addVirtual(work())
  const another = await store.addVirtual(work())
  assert.equal(first.id, another.id)
  await Promise.all([
    store.updateGame(first.id, { name: '我的名称', personalRating: 8.5, customTags: [' 日常 ', '日常', '剧情'], notes: '私人笔记' }),
    store.addVirtual(work('work-2')),
    store.updateGame(first.id, { favorite: true, status: 'playing' }),
  ])
  await store.linkMetadata(first.id, { ...work(), title: '新的来源名称' })
  const updated = store.getGame(first.id)
  assert.equal(updated.name, '我的名称')
  assert.equal(updated.personalRating, 8.5)
  assert.equal(updated.notes, '私人笔记')
  assert.equal(updated.favorite, true)
  assert.deepEqual(updated.customTags, ['日常', '剧情'])
  updated.customTags.push('不应该保存')
  store.getGames().pop()
  assert.equal(store.getGames().length, 2)
  assert.deepEqual(store.getGame(first.id).customTags, ['日常', '剧情'])
  const reloaded = new LibraryStore(path.join(root, 'data')); await reloaded.initialize()
  assert.deepEqual(reloaded.getGames(), store.getGames())
  assert.equal((await fs.readdir(path.join(root, 'data'))).some(name => name.endsWith('.tmp')), false)
})

test('optional title provenance survives restart and backup alongside legacy metadata without that field', async t => {
  const { root, store } = await environment(t)
  const legacy = await store.addVirtual(work('legacy-work'))
  const incoming: WorkMetadata = {
    ...work('with-title-evidence'),
    titleEvidence: [
      { text: '正式作品名称', kind: 'official', isMachine: false, language: 'zh-Hans' },
      { text: 'Official Latin Title', kind: 'latin', isMachine: false, language: null },
      { text: 'Machine translated title', kind: 'alias', isMachine: true, language: 'en' }
    ]
  }
  const expected = structuredClone(incoming.titleEvidence)
  const added = await store.addVirtual(incoming)
  incoming.titleEvidence![0]!.text = 'caller changed its copy'
  assert.deepEqual(store.getGame(added.id).metadata?.titleEvidence, expected)
  const reloaded = new LibraryStore(store.dataDir); await reloaded.initialize()
  assert.deepEqual(reloaded.getGame(added.id).metadata?.titleEvidence, expected)
  assert.deepEqual(reloaded.getGame(legacy.id).metadata, work('legacy-work'))
  assert.equal('titleEvidence' in reloaded.getGame(legacy.id).metadata!, false)
  const backup = reloaded.exportBackup()
  const imported = new LibraryStore(path.join(root, 'imported-titles')); await imported.initialize()
  assert.deepEqual(await imported.importBackup(backup), { added: 2, skipped: 0 })
  assert.deepEqual(imported.getGame(added.id).metadata?.titleEvidence, expected)
  assert.deepEqual(imported.getGame(legacy.id).metadata, work('legacy-work'))
})

test('invalid edits fail without changing stored data and do not poison the mutation queue', async t => {
  const { store } = await environment(t)
  const item = await store.addVirtual(work())
  await assert.rejects(store.updateGame(item.id, { personalRating: 11 }), /评分/)
  await assert.rejects(store.updateGame(item.id, { personalRating: NaN }), /评分/)
  await assert.rejects(store.updateGame(item.id, { status: 'unknown' as never }), /状态/)
  await assert.rejects(store.updateGame(item.id, { name: '  ' }), /不能为空/)
  await assert.rejects(store.updateGame(item.id, { totalPlaySeconds: 9000 } as never), /字段/)
  await assert.rejects(store.updateGame(item.id, { customTags: [null] as never }), /标签/)
  assert.equal(store.getGame(item.id).personalRating, null)
  await store.updateGame(item.id, { personalRating: 9 })
  assert.equal(store.getGame(item.id).personalRating, 9)
})

test('failed persistence rolls back the in-memory transaction', async t => {
  const { store } = await environment(t)
  const item = await store.addVirtual(work())
  await fs.rename(store.filePath, `${store.filePath}.saved`)
  await fs.mkdir(store.filePath)
  await assert.rejects(store.updateGame(item.id, { name: '不得保存' }))
  assert.equal(store.getGame(item.id).name, item.name)
  await fs.rmdir(store.filePath)
  await fs.rename(`${store.filePath}.saved`, store.filePath)
  await store.updateGame(item.id, { name: '恢复后可保存' })
  assert.equal(store.getGame(item.id).name, '恢复后可保存')
})

test('corrupt primary restores last-known-good backup and keeps damaged file', async t => {
  const { store, root } = await environment(t)
  const item = await store.addVirtual(work())
  await store.updateGame(item.id, { notes: '新的状态' })
  await fs.writeFile(store.filePath, '{broken json')
  const restored = new LibraryStore(path.join(root, 'data')); await restored.initialize()
  assert.equal(restored.getGame(item.id).notes, '')
  assert.match(restored.recoveryNotice!, /备份恢复/)
  assert((await fs.readdir(restored.dataDir)).some(name => name.startsWith('library.corrupt-')))
  await fs.writeFile(restored.filePath, '{broken')
  await fs.writeFile(restored.backupPath, '{broken')
  const failed = new LibraryStore(restored.dataDir)
  await assert.rejects(failed.initialize(), /原文件已保留/)
  assert.equal(await fs.readFile(restored.filePath, 'utf8'), '{broken')
})

test('scan handles Chinese and spaces, excludes installers, stops at a game directory, and skips links', async t => {
  const { root } = await environment(t)
  const games = path.join(root, 'Games'), first = path.join(games, '星空 游戏'), second = path.join(games, 'Multiple')
  await fs.mkdir(path.join(first, 'nested'), { recursive: true }); await fs.mkdir(second, { recursive: true })
  await fs.writeFile(path.join(first, '星空 游戏.exe'), '')
  await fs.writeFile(path.join(first, 'unins000.exe'), '')
  await fs.writeFile(path.join(first, 'nested', 'nested.exe'), '')
  await fs.writeFile(path.join(second, 'Multiple.exe'), '')
  await fs.writeFile(path.join(second, 'alternate.exe'), '')
  await fs.writeFile(path.join(second, 'setup.exe'), '')
  const hidden = path.join(root, 'outside'); await fs.mkdir(hidden); await fs.writeFile(path.join(hidden, 'external.exe'), '')
  try { await fs.symlink(hidden, path.join(games, 'linked'), process.platform === 'win32' ? 'junction' : 'dir') } catch (error) { if (!['EPERM', 'EACCES'].includes((error as NodeJS.ErrnoException).code ?? '')) throw error }
  const scanned = await scanFolder(games)
  assert.equal(scanned.length, 2)
  assert.equal(scanned.find(candidate => candidate.name === '星空 游戏')?.executables.length, 1)
  assert.equal(path.basename(scanned.find(candidate => candidate.name === 'Multiple')!.executables[0]!), 'Multiple.exe')
  assert(scanned.every(candidate => candidate.name !== 'nested' && candidate.name !== 'linked'))
  await assert.rejects(scanFolder(path.parse(root).root), /具体文件夹/)
})

test('local imports deduplicate canonical paths, select a sole exe, validate launch options, and remove only records', async t => {
  const { root, store } = await environment(t)
  const location = path.join(root, '测试 文件夹'); await fs.mkdir(location)
  const executable = path.join(location, 'game.exe'); await fs.writeFile(executable, 'not a real program')
  const candidates = await scanFolder(location)
  assert.deepEqual(await store.importCandidates(candidates), { added: 1, skipped: 0 })
  assert.deepEqual(await store.importCandidates(candidates), { added: 0, skipped: 1 })
  const item = store.getGames()[0]!
  assert.equal(item.installations[0]!.executablePath, executable)
  const outside = path.join(root, 'outside.exe'); await fs.writeFile(outside, '')
  await assert.rejects(store.setExecutable(item.id, undefined, outside), /目录内/)
  await assert.rejects(store.setLaunchArguments(item.id, item.installations[0]!.id, ['bad\0arg']), /参数/)
  await store.setLaunchArguments(item.id, item.installations[0]!.id, ['--title', '中文 title', '$(literal)'])
  assert.deepEqual(store.getGame(item.id).installations[0]!.arguments, ['--title', '中文 title', '$(literal)'])
  await store.removeGame(item.id)
  assert.equal(store.getGames().length, 0)
  assert.equal(await fs.readFile(executable, 'utf8'), 'not a real program')
})

test('explicit source matching joins multiple installations and preserves user state', async t => {
  const { root, store } = await environment(t)
  for (const folder of ['English', '中文']) { const directory = path.join(root, folder); await fs.mkdir(directory); await fs.writeFile(path.join(directory, 'game.exe'), ''); await store.importCandidates(await scanFolder(directory)) }
  const [first, second] = store.getGames()
  await store.updateGame(first!.id, { notes: '第一版笔记', personalRating: 8, customTags: ['汉化'] })
  await store.linkMetadata(first!.id, work())
  await store.updateGame(second!.id, { notes: '第二版笔记' })
  const merged = await store.linkMetadata(second!.id, work())
  assert.equal(store.getGames().length, 1)
  assert.equal(merged.installations.length, 2)
  assert.equal(merged.name, work().title)
  assert.equal(merged.personalRating, 8)
  assert.match(merged.notes, /第一版笔记/)
  assert.match(merged.notes, /第二版笔记/)
  assert.deepEqual(merged.customTags, ['汉化'])
})

test('automatic first links can preserve local names while manual links retain their default naming behavior', async t => {
  const { root, store } = await environment(t)
  for (const name of ['自定义本地名称', '手动绑定目录']) {
    const folder = path.join(root, name)
    await fs.mkdir(folder); await fs.writeFile(path.join(folder, 'game.exe'), '')
    await store.importCandidates(await scanFolder(folder))
  }
  const automatic = store.getGames().find(game => game.name === '自定义本地名称')!
  const manual = store.getGames().find(game => game.name === '手动绑定目录')!
  await store.updateGame(automatic.id, { notes: '我的备注', personalRating: 9 })
  const autoLinked = await store.linkMetadata(automatic.id, { ...work('auto'), title: '来源标题甲' }, {
    preserveName: true, expectedName: automatic.name, expectedMetadataId: null
  })
  const manualLinked = await store.linkMetadata(manual.id, { ...work('manual'), title: '来源标题乙' })
  assert.equal(autoLinked.name, automatic.name)
  assert.equal(autoLinked.metadata?.title, '来源标题甲')
  assert.equal(autoLinked.notes, '我的备注')
  assert.equal(autoLinked.personalRating, 9)
  assert.equal(manualLinked.name, '来源标题乙')
  // Later manual refreshes still follow the source title only when no custom name exists.
  assert.equal((await store.linkMetadata(automatic.id, { ...work('auto'), title: '来源标题甲修订' })).name, automatic.name)
  assert.equal((await store.linkMetadata(manual.id, { ...work('manual'), title: '来源标题乙修订' })).name, '来源标题乙修订')
  const reopened = new LibraryStore(store.dataDir); await reopened.initialize()
  assert.deepEqual(reopened.getGames(), store.getGames())
})

for (const scenario of [
  { label: 'automatic merge preserves the existing customized name', options: { preserveMergedName: true }, incomingName: '新导入目录', expectedName: '已有自定义名称', incomingRating: null },
  { label: 'an explicitly preserved incoming name wins over the existing customized name', options: { preserveMergedName: true, preserveName: true }, incomingName: '新导入自定义名称', expectedName: '新导入自定义名称', incomingRating: 7 },
  { label: 'manual merge keeps its default source-title naming behavior', options: {}, incomingName: '新导入目录', expectedName: '来源作品标题', incomingRating: null }
]) test(scenario.label, async t => {
  const { root, store } = await environment(t)
  const source = { ...work('same-work'), title: '来源作品标题' }
  for (const folderName of ['已有安装目录', '新导入目录']) {
    const folder = path.join(root, folderName)
    await fs.mkdir(folder); await fs.writeFile(path.join(folder, 'game.exe'), '')
    await store.importCandidates(await scanFolder(folder))
  }
  const existing = store.getGames().find(game => game.name === '已有安装目录')!
  const incoming = store.getGames().find(game => game.name === '新导入目录')!
  await store.linkMetadata(existing.id, source)
  await store.updateGame(existing.id, { name: '已有自定义名称', notes: '已有笔记', personalRating: 9, favorite: true, categories: ['已有分类'] })
  await store.updateGame(incoming.id, { ...(scenario.incomingName !== incoming.name ? { name: scenario.incomingName } : {}), notes: '新导入笔记', personalRating: scenario.incomingRating, customTags: ['新标签'] })
  const merged = await store.linkMetadata(incoming.id, source, scenario.options)
  assert.equal(merged.name, scenario.expectedName)
  assert.equal(merged.metadata?.title, source.title)
  assert.equal(merged.id, incoming.id)
  assert.equal(store.getGames().length, 1)
  assert.equal(merged.installations.length, 2)
  assert.equal(merged.personalRating, scenario.incomingRating ?? 9)
  assert.equal(merged.notes, '新导入笔记\n\n已有笔记')
  assert.equal(merged.favorite, true)
  assert.deepEqual(merged.categories, ['已有分类'])
  assert.deepEqual(merged.customTags, ['新标签'])
  const reopened = new LibraryStore(store.dataDir); await reopened.initialize()
  assert.deepEqual(reopened.getGames(), store.getGames())
})

test('expectedName is checked inside the mutation queue and rejects stale results before merging any records', async t => {
  const { store } = await environment(t)
  const selected = await store.addVirtual({ ...work('selected'), title: '识别开始时名称' })
  await store.addVirtual({ ...work('existing-target'), title: '不得被合并删除的游戏' })
  const before = store.getGames()
  // Queue a rename immediately before the network result, without waiting for persistence.
  const renaming = store.updateGame(selected.id, { name: '用户刚修改的名称' })
  const rejected = assert.rejects(store.linkMetadata(selected.id, work('existing-target'), {
    preserveName: true, expectedName: selected.name, expectedMetadataId: 'selected'
  }), /名称或资料已更新/)
  await renaming
  await rejected
  const expected = before.map(game => game.id === selected.id ? { ...game, name: '用户刚修改的名称', nameIsCustom: true } : game)
  assert.deepEqual(store.getGames(), expected)
  const reopened = new LibraryStore(store.dataDir); await reopened.initialize()
  assert.deepEqual(reopened.getGames(), expected)
})

test('expectedMetadataId guards both an initially unlinked game and a changed binding without touching saved data', async t => {
  const { root, store } = await environment(t)
  const folder = path.join(root, '名称保持不变')
  await fs.mkdir(folder); await fs.writeFile(path.join(folder, 'game.exe'), '')
  await store.importCandidates(await scanFolder(folder))
  const selected = store.getGames()[0]!
  await store.addVirtual({ ...work('stale-target'), title: '旧识别结果的合并目标' })
  await store.linkMetadata(selected.id, work('first-choice'), { preserveName: true })
  const assertUnchanged = async (expectedMetadataId: string | null) => {
    const before = store.getGames()
    const saved = await fs.readFile(store.filePath, 'utf8')
    await assert.rejects(store.linkMetadata(selected.id, work('stale-target'), {
      preserveName: true, expectedName: selected.name, expectedMetadataId
    }), /名称或资料已更新/)
    assert.deepEqual(store.getGames(), before)
    assert.equal(await fs.readFile(store.filePath, 'utf8'), saved)
    const reopened = new LibraryStore(store.dataDir); await reopened.initialize()
    assert.deepEqual(reopened.getGames(), before)
  }
  await assertUnchanged(null)
  await store.linkMetadata(selected.id, work('new-choice'), { preserveName: true })
  assert.equal(store.getGame(selected.id).name, selected.name)
  await assertUnchanged('first-choice')
  const accepted = await store.linkMetadata(selected.id, { ...work('new-choice'), description: '正确身份下的新资料' }, {
    preserveName: true, expectedName: selected.name, expectedMetadataId: 'new-choice'
  })
  assert.equal(accepted.metadata?.description, '正确身份下的新资料')
  assert.equal(accepted.name, selected.name)
})

test('a virtual work accepts an explicitly selected local executable', async t => {
  const { root, store } = await environment(t)
  const item = await store.addVirtual(work()), folder = path.join(root, 'chosen-game')
  await fs.mkdir(folder); const executable = path.join(folder, 'game.exe'); await fs.writeFile(executable, '')
  const updated = await store.setExecutable(item.id, undefined, executable)
  assert.equal(updated.installations.length, 1)
  assert.equal(updated.installations[0]!.folderPath, folder)
  assert.equal(updated.installations[0]!.executablePath, executable)
  assert.equal(updated.preferredInstallationId, updated.installations[0]!.id)
  const another = await store.addVirtual(work('work-2'))
  await assert.rejects(store.setExecutable(another.id, undefined, executable), /已在库中/)
})

test('merging beyond schema limits fails atomically and both originals remain reloadable', async t => {
  const { root, store } = await environment(t)
  const first = await store.addVirtual(work('first')), second = await store.addVirtual(work('second'))
  await store.updateGame(first.id, { customTags: Array.from({ length: 150 }, (_, i) => `A${i}`) })
  await store.updateGame(second.id, { customTags: Array.from({ length: 150 }, (_, i) => `B${i}`) })
  await assert.rejects(store.linkMetadata(second.id, work('first')), /标签/)
  assert.equal(store.getGames().length, 2)
  const reopened = new LibraryStore(path.join(root, 'data')); await reopened.initialize()
  assert.deepEqual(reopened.getGames(), store.getGames())
})

test('backup roundtrip validates the complete document before importing and skips existing records', async t => {
  const { root, store } = await environment(t)
  const item = await store.addVirtual(work())
  await store.updateGame(item.id, { notes: '保留笔记', categories: ['收藏'], personalRating: 9.5 })
  await store.recordSession(item.id, { id: 'session-1', startedAt: '2026-10-02T00:00:00Z', endedAt: '2026-10-02T00:01:30Z', seconds: 90 })
  const exported = store.exportBackup(), destination = new LibraryStore(path.join(root, 'imported')); await destination.initialize()
  assert.deepEqual(await destination.importBackup(exported), { added: 1, skipped: 0 })
  assert.deepEqual(destination.getGames(), store.getGames())
  assert.deepEqual(await destination.importBackup(exported), { added: 0, skipped: 1 })
  const invalid = structuredClone(exported); invalid.games[0]!.personalRating = 99
  await assert.rejects(destination.importBackup(invalid), /评分/)
  assert.deepEqual(destination.getGames(), store.getGames())
  await assert.rejects(destination.importBackup({ ...exported, version: 2 }), /版本/)
  assert.equal(JSON.stringify(exported).includes('apiKey'), false)
})

test('cumulative checkpoints never double count and reject conflicting session identity', async t => {
  const { store } = await environment(t), item = await store.addVirtual(work())
  const base = { id: 'same-session', startedAt: '2026-10-02T00:00:00Z', endedAt: '2026-10-02T00:03:00Z' }
  await Promise.all([store.recordSession(item.id, { ...base, seconds: 15 }), store.recordSession(item.id, { ...base, seconds: 30 }), store.recordSession(item.id, { ...base, seconds: 20 }), store.recordSession(item.id, { ...base, seconds: 30 })])
  assert.equal(store.getGame(item.id).totalPlaySeconds, 30)
  assert.equal(store.getGame(item.id).playCount, 1)
  assert.equal(store.getGame(item.id).sessions.length, 1)
  await assert.rejects(store.recordSession(item.id, { ...base, startedAt: '2026-10-02T00:01:00Z', seconds: 80 }), /ID 冲突/)
  await store.recordSession(item.id, { ...base, id: 'zero-session', seconds: 0 })
  assert.equal(store.getGame(item.id).playCount, 1)
})

test('library scanning excludes patch attachment executables and rejects selecting them as games', async t => {
  const temporaryRoot = path.resolve(os.tmpdir())
  const root = await fs.mkdtemp(path.join(temporaryRoot, 'kun-library-patch-'))
  t.after(async () => {
    assert.equal(path.dirname(path.resolve(root)), temporaryRoot)
    assert.ok(path.basename(root).startsWith('kun-library-patch-'))
    await fs.rm(root, { recursive: true, force: true })
  })
  const body = path.join(root, 'A distinctive game', 'StartData')
  const patch = path.join(root, 'A distinctive game', '汉化补丁', 'GameData')
  await fs.mkdir(body, { recursive: true }); await fs.mkdir(patch, { recursive: true })
  await fs.writeFile(path.join(body, 'SiglusEngine.exe'), 'fixture, never executed')
  await fs.writeFile(path.join(patch, 'SiglusEngine_CJK.exe'), 'fixture, never executed')
  const candidates = await scanFolder(root)
  assert.deepEqual(candidates.map(item => item.folderPath), [body])
  await assert.rejects(scanFolder(patch), /补丁或备份目录/)
  assert.equal(await fs.readFile(path.join(patch, 'SiglusEngine_CJK.exe'), 'utf8'), 'fixture, never executed', 'scanning leaves the patch on disk')
})

test('aggregated works retain more than 100 edition references through save and restart with a bounded import', async t => {
  const { root, store } = await environment(t)
  const many = { ...work('60'), references: [
    { source: 'steam', id: '4012810' }, { source: 'vndb', id: 'v8213' },
    ...Array.from({ length: 172 }, (_, i) => ({ source: 'vndb', id: `r${128730 + i}` }))
  ] }
  const added = await store.addVirtual(many)
  const reloaded = new LibraryStore(path.join(root, 'data')); await reloaded.initialize()
  assert.deepEqual(reloaded.getGame(added.id).metadata?.references, many.references)
  const excessive = { ...many, id: 'excessive', references: Array.from({ length: 1001 }, (_, i) => ({ source: 'steam', id: String(10000 + i) })) }
  await assert.rejects(store.addVirtual(excessive), /作品资料列表格式/)
  assert.deepEqual(store.getGame(added.id).metadata?.references, many.references)
})


test('legacy custom provenance survives title equality, merge, restart and backup restoration', async t => {
  const { root, store } = await environment(t)
  const existing = await store.addVirtual({ ...work('legacy'), title: 'Old source title' })
  const folder = path.join(root, 'New ordinary folder'); await fs.mkdir(folder); await fs.writeFile(path.join(folder, 'Game.exe'), '')
  await store.importCandidates(await scanFolder(folder))
  const incoming = store.getGames().find(g => g.id !== existing.id)!
  const backup = store.exportBackup(); backup.games.find(g => g.id === existing.id)!.name = 'Legacy personal title'
  delete backup.games.find(g => g.id === existing.id)!.nameIsCustom
  await store.removeGame(existing.id); await store.importBackup(backup)
  const merged = await store.linkMetadata(incoming.id, { ...work('legacy'), title: 'Legacy personal title' }, { preserveMergedName: true })
  assert.equal(merged.nameIsCustom, true)
  const reopened = new LibraryStore(store.dataDir); await reopened.initialize()
  await reopened.linkMetadata(merged.id, { ...work('legacy'), title: 'Later source title' })
  assert.equal(reopened.getGame(merged.id).name, 'Legacy personal title')
  const restored = new LibraryStore(path.join(root, 'restored')); await restored.initialize(); await restored.importBackup(reopened.exportBackup())
  await restored.linkMetadata(merged.id, { ...work('legacy'), title: 'Another source title' })
  assert.equal(restored.getGame(merged.id).name, 'Legacy personal title')
})

test('unknown legacy name equal to a folder is conservatively preserved without guessing its origin', async t => {
  const { root, store } = await environment(t)
  const folder = path.join(root, 'Possibly personal folder name'); await fs.mkdir(folder); await fs.writeFile(path.join(folder, 'Game.exe'), '')
  await store.importCandidates(await scanFolder(folder)); const game = store.getGames()[0]!
  const backup = store.exportBackup(); delete backup.games[0]!.nameIsCustom
  await store.removeGame(game.id); await store.importBackup(backup)
  const linked = await store.linkMetadata(game.id, work())
  assert.equal(linked.name, game.name); assert.equal(linked.nameIsCustom, true)
})

test('an explicit unchanged-name patch confirms the name while an ordinary notes patch does not', async t => {
  const { store } = await environment(t)
  const ordinary = await store.addVirtual(work('ordinary'))
  await store.updateGame(ordinary.id, { notes: 'Only notes' })
  await store.linkMetadata(ordinary.id, { ...work('ordinary'), title: 'New ordinary title' })
  assert.equal(store.getGame(ordinary.id).name, 'New ordinary title')
  const chosen = await store.addVirtual(work('chosen'))
  await store.updateGame(chosen.id, { name: chosen.name })
  assert.equal(store.getGame(chosen.id).nameIsCustom, true)
  await store.linkMetadata(chosen.id, { ...work('chosen'), title: 'New source title' })
  assert.equal(store.getGame(chosen.id).name, chosen.name)
})
