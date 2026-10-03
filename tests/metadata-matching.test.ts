import assert from 'node:assert/strict'
import { promises as fs } from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import test from 'node:test'
import { LibraryStore, type MetadataRequestToken } from '../electron/library'
import { MetadataMatcher } from '../electron/metadata-matching'
import { readGameIdentity, rankMetadataMatches, type TitleIndexRecord } from '../electron/game-identification'
import type { LibraryGame, SearchPage, WorkMetadata, ReleaseIdentity } from '../src/shared/types'

const firstTitle = '晨雾航线物语'
const secondTitle = '月下灯塔回忆'
const indexRecord = (name: string, reference: string): TitleIndexRecord => ({ names: [name], references: [reference] })
function work(id: string, title: string, reference: string): WorkMetadata {
  const [source, externalId] = reference.split(':')
  return {
    id, title, originalTitle: title, aliases: [], coverUrl: `https://images.example/${id}.webp`, bannerUrl: null,
    description: `${title}的资料`, releaseDate: null, releaseDatePrecision: null, developers: ['测试开发商'],
    tags: [], ratings: [], characters: [], references: [{ source: source!, id: externalId! }],
    webUrl: null, contentRating: null, fetchedAt: '2026-10-02T00:00:00.000Z'
  }
}
function deferred<T>() {
  let resolve!: (value: T | PromiseLike<T>) => void
  const promise = new Promise<T>(done => { resolve = done })
  return { promise, resolve }
}

async function environment(t: { after(callback: () => Promise<void>): void }, names: string[]) {
  const temporaryRoot = path.resolve(os.tmpdir())
  const root = await fs.mkdtemp(path.join(temporaryRoot, 'kun-matching-test-'))
  const matchers: MetadataMatcher[] = []
  t.after(async () => {
    for (const matcher of matchers) await matcher.cancel()
    // Recursive cleanup is confined to the temporary directory created by this test.
    assert.equal(path.dirname(path.resolve(root)), temporaryRoot)
    assert.ok(path.basename(root).startsWith('kun-matching-test-'))
    await fs.rm(root, { recursive: true, force: true })
  })
  const dataDir = path.join(root, 'data')
  const library = new LibraryStore(dataDir)
  await library.initialize()
  for (const name of names) {
    const folderPath = path.join(root, 'Games', name)
    await fs.mkdir(folderPath, { recursive: true })
    const executable = path.join(folderPath, `${name}.exe`)
    await fs.writeFile(executable, 'test fixture; never executed')
    await library.importCandidates([{ id: name, name, folderPath, executables: [executable] }])
  }
  return { root, dataDir, library, matchers }
}
type Environment = Awaited<ReturnType<typeof environment>>
interface HarnessOptions {
  index?: TitleIndexRecord[]
  works?: WorkMetadata[]
  resolve?(refs: string[], signal: AbortSignal): Promise<WorkMetadata[]>
  search?(query: string, signal: AbortSignal): Promise<SearchPage>
  detail?(id: string, signal: AbortSignal): Promise<WorkMetadata>
  releases?(id: string, signal: AbortSignal): Promise<ReleaseIdentity[]>
  apply?(game: LibraryGame, metadata: WorkMetadata, token: MetadataRequestToken | undefined, signal: AbortSignal): Promise<boolean>
}
function harness(env: Environment, options: HarnessOptions = {}) {
  const calls = { refs: [] as string[][], search: [] as string[], detail: [] as string[], applied: [] as { gameId: string; workId: string }[], signals: [] as AbortSignal[], changed: 0, index: 0 }
  const matcher = new MetadataMatcher(env.dataDir, env.library, {
    client(signal) {
      calls.signals.push(signal)
      return {
        async resolveWorksByRefs(refs) {
          calls.refs.push([...refs])
          return options.resolve ? options.resolve(refs, signal) : structuredClone(options.works ?? [])
        },
        async searchWorks(query) {
          calls.search.push(query)
          return options.search ? options.search(query, signal) : { items: [], nextCursor: null }
        },
        ...(options.releases ? { getWorkReleases: (id: string) => options.releases!(id, signal) } : {}),
        async getWork(id) {
          calls.detail.push(id)
          if (options.detail) return options.detail(id, signal)
          const found = options.works?.find(candidate => candidate.id === id)
          assert.ok(found, `Unexpected detail request for ${id}`)
          return structuredClone(found)
        }
      }
    },
    capture: (game, batchId) => env.library.captureMetadataToken(game.id, batchId),
    async apply(game, metadata, token, signal) {
      calls.applied.push({ gameId: game.id, workId: metadata.id })
      if (options.apply) return options.apply(game, metadata, token, signal)
      await env.library.linkMetadata(game.id, metadata, { requestToken: token, signal })
      return true
    },
    async index() { calls.index++; return structuredClone(options.index ?? []) },
    changed() { calls.changed++ }
  })
  env.matchers.push(matcher)
  return { matcher, calls }
}

test('matches multiple imported games through one exact-ref batch and persists real library metadata', async t => {
  const env = await environment(t, [firstTitle, secondTitle])
  const before = env.library.getGames()
  const first = before.find(game => game.name === firstTitle)!
  await env.library.updateGame(first.id, { notes: '保留我的笔记', personalRating: 8, customTags: ['本地收藏'] })
  const works = [work('101', firstTitle, 'vndb:v101'), work('202', secondTitle, 'vndb:v202')]
  const { matcher, calls } = harness(env, {
    index: [indexRecord(firstTitle, 'vndb:v101'), indexRecord(secondTitle, 'vndb:v202')],
    // Reverse the batch result order and inject noise to forbid positional matching.
    works,
    resolve: async () => [works[1]!, work('999', '无关作品', 'vndb:v999'), works[0]!]
  })
  await matcher.initialize()
  assert.equal(matcher.getState().status, 'idle')
  assert.equal((await matcher.start()).status, 'running')
  await matcher.settle()
  const state = matcher.getState()
  assert.equal(state.status, 'completed')
  assert.deepEqual([state.total, state.processed, state.matched, state.unmatched, state.failed], [2, 2, 2, 0, 0])
  assert.equal(state.currentName, null)
  assert.deepEqual(state.reviews, [])
  assert.equal(calls.refs.length, 1)
  assert.deepEqual(new Set(calls.refs[0]), new Set(['vndb:v101', 'vndb:v202']))
  assert.deepEqual(calls.search, [])
  assert.deepEqual(new Set(calls.detail), new Set(['101', '202']))
  assert.equal(calls.index, 1)
  assert.ok(calls.changed >= 2)
  assert.equal(env.library.getGame(first.id).metadata?.id, '101')
  assert.equal(env.library.getGame(first.id).notes, '保留我的笔记')
  assert.equal(env.library.getGame(first.id).personalRating, 8)
  assert.deepEqual(env.library.getGame(first.id).customTags, ['本地收藏'])
  const reopened = new LibraryStore(env.dataDir)
  await reopened.initialize()
  assert.deepEqual(reopened.getGames(), env.library.getGames())
})

test('one automatic job may still merge independently imported installations of the same confirmed work', async t => {
  const env = await environment(t, [firstTitle, `[汉化] ${firstTitle}`])
  const before = env.library.getGames()
  assert.equal(before.length, 2)
  const { matcher } = harness(env, {
    index: [indexRecord(firstTitle, 'vndb:v101')], works: [work('101', firstTitle, 'vndb:v101')]
  })
  await matcher.initialize(); await matcher.start(); await matcher.settle()
  const state = matcher.getState()
  assert.equal(state.status, 'completed')
  assert.deepEqual([state.processed, state.matched, state.unmatched, state.failed], [2, 2, 0, 0])
  assert.deepEqual(state.reviews, [])
  const games = env.library.getGames()
  assert.equal(games.length, 1)
  assert.equal(games[0]!.metadata?.id, '101')
  assert.deepEqual(new Set(games[0]!.installations.map(installation => installation.id)),
    new Set(before.flatMap(game => game.installations.map(installation => installation.id))))
})

for (const kind of ['official', 'latin'] as const) test(`full ${kind} title evidence is rechecked before automatic linking`, { timeout: 5000 }, async t => {
  const localTitle = 'as 9-nine- ARTEISIA'
  const env = await environment(t, [localTitle])
  const game = env.library.getGames()[0]!
  const card = work('501', 'as:9-nine- アルテイシア', 'vndb:v501')
  const full: WorkMetadata = {
    ...card, aliases: ['as:9-nine- ARTEISIA'],
    titleEvidence: [{ text: 'as:9-nine- ARTEISIA', kind, isMachine: false, language: 'en' }]
  }
  const entered = deferred<void>(), response = deferred<WorkMetadata>()
  const { matcher, calls } = harness(env, {
    // The collection card has neither the complete alias nor its provenance.
    search: async () => ({ items: [structuredClone(card)], nextCursor: null }),
    detail: async id => { assert.equal(id, card.id); entered.resolve(); return response.promise }
  })
  await matcher.initialize()
  await matcher.start()
  await entered.promise
  assert.deepEqual(calls.applied, [], 'a similar elected title alone must not be applied')
  assert.equal(env.library.getGame(game.id).metadata, null)
  assert.equal(matcher.getState().matched, 0)
  response.resolve(full)
  await matcher.settle()
  const state = matcher.getState()
  assert.equal(state.status, 'completed')
  assert.deepEqual([state.processed, state.matched, state.unmatched, state.failed], [1, 1, 0, 0])
  assert.deepEqual(state.reviews, [])
  assert.deepEqual(calls.refs, [])
  assert.ok(calls.search.length > 0)
  assert.deepEqual(calls.detail, ['501'], 'verification should reuse the freshly enriched detail')
  assert.deepEqual(calls.applied, [{ gameId: game.id, workId: '501' }])
  assert.deepEqual(env.library.getGame(game.id).metadata?.titleEvidence, full.titleEvidence)
})

test('a cross-language card below the initial ranking threshold still receives details before matching', async t => {
  const localTitle = 'Silent Aurora Memories'
  const env = await environment(t, [localTitle])
  const game = env.library.getGames()[0]!
  const card = work('601', '静かな極光の記憶', 'vndb:v601')
  const identity = await readGameIdentity(game, [])
  const initial = rankMetadataMatches(identity, [card])[0]!
  assert.ok(initial.confidence < 0.4, 'the collection title has no text overlap with the local English name')
  assert.equal(initial.automatic, false)
  const full: WorkMetadata = {
    ...card, aliases: [localTitle],
    titleEvidence: [{ text: localTitle, kind: 'official', isMachine: false, language: 'en' }]
  }
  const { matcher, calls } = harness(env, {
    search: async () => ({ items: [structuredClone(card)], nextCursor: null }),
    detail: async id => { assert.equal(id, card.id); return structuredClone(full) }
  })
  await matcher.initialize()
  await matcher.start()
  await matcher.settle()
  const state = matcher.getState()
  assert.ok(calls.search.includes(localTitle))
  assert.deepEqual(calls.detail, ['601'], 'retrieved cross-language candidates must not be discarded before detail enrichment')
  assert.deepEqual(calls.applied, [{ gameId: game.id, workId: '601' }])
  assert.deepEqual([state.processed, state.matched, state.unmatched, state.failed], [1, 1, 0, 0])
  assert.deepEqual(state.reviews, [])
  assert.deepEqual(env.library.getGame(game.id).metadata?.titleEvidence, full.titleEvidence)
})

test('an independently referenced cross-language card receives details ahead of three higher-scoring wrong works', async t => {
  const localTitle = 'WITCH ON THE HOLY NIGHT'
  const env = await environment(t, [localTitle])
  const game = env.library.getGames()[0]!
  const index = [indexRecord(localTitle, 'vndb:v777')]
  const card = work('950', '魔法使之夜', 'vndb:v777')
  const noise = [
    work('951', 'Witch On The Holy Night Bonus Disc', 'vndb:v901'),
    work('952', 'Witch On The Holy Night Another Story', 'vndb:v902'),
    work('953', 'Witch On The Holy Night Preview', 'vndb:v903')
  ]
  const identity = await readGameIdentity(game, index)
  assert.ok(identity.evidence?.references.some(ref => ref.value === 'vndb:v777' && ref.source === 'title-index'))
  const initial = rankMetadataMatches(identity, [...noise, card])
  const initialCorrect = initial.find(option => option.work.id === card.id)!
  assert.ok(initialCorrect.confidence < 0.4)
  assert.equal(initial.findIndex(option => option.work.id === card.id), 3, 'the three higher-scoring noise entries would previously fill the entire detail shortlist')
  assert.ok(initial.slice(0, 3).every(option => option.confidence > initialCorrect.confidence && option.work.id !== card.id))
  assert.ok(initial.every(option => !option.automatic), 'the external ref alone must not bypass title verification')
  const full: WorkMetadata = {
    ...card, aliases: [localTitle],
    titleEvidence: [{ text: localTitle, kind: 'official', isMachine: false, language: 'en' }]
  }
  const { matcher, calls } = harness(env, {
    index, works: [full, ...noise], resolve: async () => [structuredClone(card)],
    search: async () => ({ items: structuredClone([...noise, card]), nextCursor: null })
  })
  await matcher.initialize()
  await matcher.start()
  await matcher.settle()
  const state = matcher.getState()
  assert.deepEqual(calls.refs, [['vndb:v777']])
  assert.ok(calls.search.includes(localTitle))
  assert.equal(calls.detail[0], '950', 'independent reference evidence must prioritize detail retrieval')
  assert.equal(calls.detail.filter(id => id === '950').length, 1)
  assert.equal(calls.detail.length, 3, 'prioritization must retain the bounded detail budget')
  assert.deepEqual(calls.applied, [{ gameId: game.id, workId: '950' }])
  assert.deepEqual([state.processed, state.matched, state.unmatched, state.failed], [1, 1, 0, 0])
  assert.deepEqual(state.reviews, [])
  assert.equal(env.library.getGame(game.id).metadata?.id, '950')
  assert.deepEqual(env.library.getGame(game.id).metadata?.titleEvidence, full.titleEvidence)
})

test('an initially automatic base-work match still searches the local edition and retains equally strong edition candidates', async t => {
  const baseTitle = 'Starlit Chronicle'
  const localTitle = `${baseTitle} REMASTERED`
  const env = await environment(t, [localTitle])
  const game = env.library.getGames()[0]!
  await fs.writeFile(path.join(game.installations[0]!.folderPath, 'README.txt'), `${baseTitle}\nLocal installation notes.`)
  const index = [indexRecord(baseTitle, 'vndb:v701')]
  const base = work('701', baseTitle, 'vndb:v701')
  // Separate edition entries have precise titles but no external reference claim.
  const editions: WorkMetadata[] = ['702', '703'].map(id => ({ ...work(id, localTitle, 'vndb:v701'), references: [] }))
  const initial = rankMetadataMatches(await readGameIdentity(game, index), [base])[0]!
  assert.equal(initial.automatic, true, 'the base title found in README/index would otherwise end recognition prematurely')
  const { matcher, calls } = harness(env, {
    index, works: [base, ...editions], resolve: async () => [structuredClone(base)],
    search: async query => ({ items: structuredClone(query.includes('REMASTERED') ? editions : [base]), nextCursor: null })
  })
  await matcher.initialize()
  await matcher.start()
  await matcher.settle()
  const state = matcher.getState()
  assert.deepEqual(calls.refs, [['vndb:v701']])
  assert.ok(calls.search.includes(localTitle), 'an apparent base-work match must not skip the explicit edition query')
  assert.deepEqual(new Set(calls.detail), new Set(['701', '702', '703']))
  assert.deepEqual(calls.applied, [], 'the base work must not win merely because the title index omitted both editions')
  assert.deepEqual([state.processed, state.matched, state.unmatched, state.failed], [1, 0, 1, 0])
  assert.equal(state.reviews.length, 1)
  const editionOptions = state.reviews[0]!.options.filter(option => ['702', '703'].includes(option.work.id))
  assert.equal(editionOptions.length, 2)
  assert.equal(editionOptions[0]!.confidence, editionOptions[1]!.confidence)
  assert.equal(env.library.getGame(game.id).metadata, null)
})

test('conflicting works claiming one external ref remain review candidates without auto-applying the first', async t => {
  const env = await environment(t, [firstTitle])
  const works = [work('101', firstTitle, 'vndb:v101'), work('102', firstTitle, 'vndb:v101')]
  const unrelated = work('999', '毫不相干的作品', 'vndb:v999')
  const { matcher, calls } = harness(env, {
    index: [indexRecord(firstTitle, 'vndb:v101')], works: [...works, unrelated], resolve: async () => structuredClone(works),
    search: async () => ({ items: [unrelated, ...works], nextCursor: null })
  })
  await matcher.initialize()
  await matcher.start()
  await matcher.settle()
  const state = matcher.getState()
  assert.equal(state.status, 'completed')
  assert.deepEqual([state.processed, state.matched, state.unmatched, state.failed], [1, 0, 1, 0])
  assert.equal(state.reviews.length, 1)
  assert.deepEqual(new Set(state.reviews[0]!.options.map(option => option.work.id)), new Set(['101', '102']))
  assert.deepEqual(calls.applied, [])
  assert.deepEqual(new Set(calls.detail), new Set(['101', '102', '999']), 'the bounded shortlist is enriched without allowing unrelated data to resolve the conflicting refs')
  assert.equal(env.library.getGames()[0]!.metadata, null)
})

test('cancelling an in-flight ref batch ignores its later response and preserves every imported game', async t => {
  const env = await environment(t, [firstTitle, secondTitle])
  const before = env.library.getGames()
  const entered = deferred<void>(), response = deferred<WorkMetadata[]>()
  const { matcher, calls } = harness(env, {
    index: [indexRecord(firstTitle, 'vndb:v101'), indexRecord(secondTitle, 'vndb:v202')],
    resolve: async () => { entered.resolve(); return response.promise }
  })
  await matcher.initialize()
  await matcher.start()
  await entered.promise
  await assert.rejects(matcher.start(), /正在进行/)
  const cancelled = await matcher.cancel()
  assert.equal(cancelled.status, 'cancelled')
  assert.equal(cancelled.currentName, null)
  assert.equal(calls.signals[0]?.aborted, true)
  response.resolve([work('101', firstTitle, 'vndb:v101'), work('202', secondTitle, 'vndb:v202')])
  await matcher.settle()
  assert.deepEqual(calls.applied, [])
  assert.deepEqual(calls.search, [])
  assert.deepEqual(calls.detail, [])
  assert.deepEqual(env.library.getGames(), before)
  assert.equal(matcher.getState().matched, 0)
  const restarted = harness(env).matcher
  await restarted.initialize()
  assert.equal(restarted.getState().status, 'cancelled')
})

test('cancelling while detail verification is pending prevents the eventual result from being applied', async t => {
  const env = await environment(t, [firstTitle])
  const candidate = work('101', firstTitle, 'vndb:v101')
  const entered = deferred<void>(), response = deferred<WorkMetadata>()
  const { matcher, calls } = harness(env, {
    index: [indexRecord(firstTitle, 'vndb:v101')], works: [candidate],
    detail: async () => { entered.resolve(); return response.promise }
  })
  await matcher.initialize()
  await matcher.start()
  await entered.promise
  await matcher.cancel()
  response.resolve(candidate)
  await matcher.settle()
  assert.deepEqual(calls.applied, [])
  assert.equal(matcher.getState().matched, 0)
  assert.equal(matcher.getState().status, 'cancelled')
  assert.equal(env.library.getGames()[0]!.metadata, null)
})

test('cancelling an apply waiting in an identity queue prevents library writes and keeps the cancelled job unchanged', async t => {
  const env = await environment(t, [firstTitle])
  const before = env.library.getGames()
  const beforeFile = await fs.readFile(env.library.filePath, 'utf8')
  const beforeBackup = await fs.readFile(env.library.backupPath, 'utf8')
  const entered = deferred<void>(), release = deferred<void>()
  let applySignal: AbortSignal | undefined
  const { matcher, calls } = harness(env, {
    index: [indexRecord(firstTitle, 'vndb:v101')], works: [work('101', firstTitle, 'vndb:v101')],
    apply: async (game, metadata, token, signal) => {
      applySignal = signal
      entered.resolve()
      // The API reply already arrived; main's identity lock is still occupied.
      await release.promise
      await env.library.linkMetadata(game.id, metadata, {
        expectedName: game.name, expectedMetadataId: game.metadata?.id ?? null, requestToken: token, signal
      })
      return true
    }
  })
  await matcher.initialize(); await matcher.start(); await entered.promise
  const pending = matcher.settle()
  const cancelled = await matcher.cancel()
  assert.equal(applySignal, calls.signals[0], 'the write gets the same task signal as the API client')
  assert.equal(applySignal?.aborted, true)
  const changes = calls.changed
  release.resolve(); await pending
  assert.deepEqual(matcher.getState(), cancelled)
  assert.deepEqual([cancelled.processed, cancelled.matched, cancelled.unmatched, cancelled.failed], [0, 0, 0, 0])
  assert.equal(calls.changed, changes)
  assert.deepEqual(env.library.getGames(), before)
  assert.equal(await fs.readFile(env.library.filePath, 'utf8'), beforeFile)
  assert.equal(await fs.readFile(env.library.backupPath, 'utf8'), beforeBackup)
  const restarted = harness(env).matcher
  await restarted.initialize()
  assert.deepEqual(restarted.getState(), cancelled)
})

for (const outcome of ['success', 'refusal', 'error'] as const) test(`a late apply ${outcome} cannot change cancelled counters, reviews or persisted state`, async t => {
  const env = await environment(t, [firstTitle])
  const entered = deferred<void>(), release = deferred<void>()
  const { matcher, calls } = harness(env, {
    index: [indexRecord(firstTitle, 'vndb:v101')], works: [work('101', firstTitle, 'vndb:v101')],
    apply: async () => {
      entered.resolve(); await release.promise
      if (outcome === 'error') throw new Error('Delayed obsolete write failure')
      return outcome === 'success'
    }
  })
  await matcher.initialize(); await matcher.start(); await entered.promise
  const pending = matcher.settle()
  const cancelled = await matcher.cancel()
  const saved = await fs.readFile(path.join(env.dataDir, 'metadata-matching.json'), 'utf8')
  const changes = calls.changed
  release.resolve(); await pending
  assert.deepEqual(matcher.getState(), cancelled)
  assert.equal(calls.changed, changes)
  assert.equal(await fs.readFile(path.join(env.dataDir, 'metadata-matching.json'), 'utf8'), saved)
})

for (const outcome of ['success', 'refusal', 'error'] as const) test(`restarted matching ignores an older job's delayed apply ${outcome}`, async t => {
  const env = await environment(t, [firstTitle])
  const oldEntered = deferred<void>(), oldRelease = deferred<void>()
  const newEntered = deferred<void>(), newRelease = deferred<void>()
  let applyCount = 0
  const { matcher, calls } = harness(env, {
    index: [indexRecord(firstTitle, 'vndb:v101')], works: [work('101', firstTitle, 'vndb:v101')],
    apply: async (game, metadata, token, signal) => {
      if (++applyCount === 1) {
        oldEntered.resolve(); await oldRelease.promise
        assert.equal(signal.aborted, true)
        if (outcome === 'error') throw new Error('Obsolete job failure')
        return outcome === 'success'
      }
      newEntered.resolve(); await newRelease.promise
      assert.equal(signal.aborted, false)
      await env.library.linkMetadata(game.id, metadata, { requestToken: token, signal })
      return true
    }
  })
  await matcher.initialize()
  const oldJob = await matcher.start(); await oldEntered.promise
  const oldPending = matcher.settle()
  await matcher.cancel()
  await matcher.start(); await newEntered.promise
  const newer = matcher.getState()
  assert.notEqual(newer.id, oldJob.id)
  assert.equal(newer.status, 'running')
  const saved = await fs.readFile(path.join(env.dataDir, 'metadata-matching.json'), 'utf8')
  const changes = calls.changed
  oldRelease.resolve(); await oldPending
  assert.deepEqual(matcher.getState(), newer)
  assert.equal(calls.changed, changes, 'the old job must not emit an update for the new job')
  assert.equal(await fs.readFile(path.join(env.dataDir, 'metadata-matching.json'), 'utf8'), saved)
  assert.equal(env.library.getGames()[0]!.metadata, null)
  newRelease.resolve(); await matcher.settle()
  const completed = matcher.getState()
  assert.equal(completed.id, newer.id)
  assert.equal(completed.status, 'completed')
  assert.deepEqual([completed.processed, completed.matched, completed.unmatched, completed.failed], [1, 1, 0, 0])
  assert.deepEqual(completed.reviews, [])
  assert.equal(env.library.getGames()[0]!.metadata?.id, '101')
})

test('cancelling during the initial state save never starts identity reads or restores a cancelled currentName', async t => {
  const env = await environment(t, [firstTitle])
  const entered = deferred<void>(), release = deferred<void>()
  const originalWrite = fs.writeFile
  let held = false
  t.mock.method(fs, 'writeFile', async (...args: Parameters<typeof fs.writeFile>) => {
    if (!held && String(args[0]).startsWith(path.join(env.dataDir, 'metadata-matching.json.'))) {
      held = true; entered.resolve(); await release.promise
    }
    return originalWrite(...args)
  })
  const { matcher, calls } = harness(env, {
    index: [indexRecord(firstTitle, 'vndb:v101')], works: [work('101', firstTitle, 'vndb:v101')]
  })
  await matcher.initialize()
  const starting = matcher.start(); await entered.promise
  const cancelling = matcher.cancel()
  release.resolve(); await starting; await cancelling; await matcher.settle()
  const cancelled = matcher.getState()
  assert.equal(cancelled.status, 'cancelled')
  assert.equal(cancelled.currentName, null)
  assert.equal(calls.index, 0)
  assert.deepEqual(calls.refs, [])
  assert.deepEqual(calls.search, [])
  assert.deepEqual(calls.detail, [])
  assert.deepEqual(calls.applied, [])
  const restarted = harness(env).matcher
  await restarted.initialize()
  assert.deepEqual(restarted.getState(), cancelled)
})

test('a linked game without a cover refreshes only its known catalog ID and preserves user edits', async t => {
  const env = await environment(t, [firstTitle])
  const game = env.library.getGames()[0]!
  await env.library.linkMetadata(game.id, { ...work('77', firstTitle, 'vndb:v101'), coverUrl: null })
  await env.library.updateGame(game.id, { notes: '已有绑定，不能重新猜测', status: 'playing' })
  const canonical = work('78', firstTitle, 'vndb:v101')
  const { matcher, calls } = harness(env, {
    index: [indexRecord(firstTitle, 'vndb:v101')],
    detail: async id => { assert.equal(id, '77'); return canonical }
  })
  await matcher.initialize()
  await matcher.start([game.id])
  await matcher.settle()
  assert.deepEqual(calls.refs, [])
  assert.deepEqual(calls.search, [])
  assert.deepEqual(calls.detail, ['77'])
  assert.equal(matcher.getState().matched, 1)
  assert.equal(env.library.getGame(game.id).metadata?.id, '78')
  assert.equal(env.library.getGame(game.id).metadata?.coverUrl, canonical.coverUrl)
  assert.equal(env.library.getGame(game.id).notes, '已有绑定，不能重新猜测')
  assert.equal(env.library.getGame(game.id).status, 'playing')
})

test('explicit reidentification includes a fully covered local game and only refreshes the same independently verified work', async t => {
  const env = await environment(t, [firstTitle]), game = env.library.getGames()[0]!
  const original = work('101', firstTitle, 'vndb:v101')
  await env.library.linkMetadata(game.id, original)
  const updated = { ...original, description: '经本地证据核对后更新的简介' }
  const { matcher, calls } = harness(env, { index: [indexRecord(firstTitle, 'vndb:v101')], works: [updated] })
  await matcher.initialize()
  await assert.rejects(matcher.start([game.id]), /资料已齐全/)
  assert.deepEqual(calls.signals, [], 'ordinary fill does not silently reidentify covered games')
  const running = await matcher.start([game.id], 'reidentify'); await matcher.settle()
  assert.equal(running.mode, 'reidentify'); assert.equal(running.total, 1)
  assert.deepEqual(calls.refs, [['vndb:v101']], 'the previous metadata ID is not the retrieval source')
  assert.deepEqual(calls.detail, ['101']); assert.equal(matcher.getState().matched, 1)
  assert.equal(env.library.getGame(game.id).metadata?.description, updated.description)
})

test('an old automatic provider title cannot prove itself during reidentification', async t => {
  const env = await environment(t, ['GameData']), game = env.library.getGames()[0]!
  const old = work('999', firstTitle, 'vndb:v999')
  await env.library.linkMetadata(game.id, old)
  const before = env.library.getGame(game.id)
  assert.equal(before.name, firstTitle); assert.equal(before.nameIsCustom, false)
  const { matcher, calls } = harness(env, { index: [indexRecord(firstTitle, 'vndb:v999')], works: [old], search: async () => ({ items: [old], nextCursor: null }) })
  await matcher.initialize(); await matcher.start([game.id], 'reidentify'); await matcher.settle()
  assert.equal(calls.refs.flat().includes('vndb:v999'), false)
  assert.equal(calls.search.includes(firstTitle), false)
  assert.deepEqual(calls.applied, []); assert.equal(matcher.getState().matched, 0)
  assert.match(matcher.getState().reviews[0]!.reason, /尚无足够.*当前关联/)
  assert.deepEqual(env.library.getGame(game.id), before)
})

test('an explicit personal title remains evidence and a same-ID refresh preserves personal fields and sessions', async t => {
  const env = await environment(t, ['GameData']), game = env.library.getGames()[0]!
  await env.library.linkMetadata(game.id, work('101', '旧显示标题', 'vndb:v101'))
  await env.library.updateGame(game.id, { name: firstTitle, status: 'completed', notes: '个人笔记', personalRating: 9, favorite: true, categories: ['珍藏'], customTags: ['已读'] })
  await env.library.recordSession(game.id, { id: 'synthetic-session', startedAt: '2026-10-03T10:00:00Z', endedAt: '2026-10-03T10:01:00Z', seconds: 60 })
  const before = env.library.getGame(game.id), refreshed = work('101', firstTitle, 'vndb:v101')
  const { matcher, calls } = harness(env, { index: [indexRecord(firstTitle, 'vndb:v101')], works: [refreshed] })
  await matcher.initialize(); await matcher.start([game.id], 'reidentify'); await matcher.settle()
  assert.deepEqual(calls.refs, [['vndb:v101']]); assert.equal(matcher.getState().matched, 1)
  assert.deepEqual(env.library.getGame(game.id), { ...before, metadata: refreshed })
})

test('a confidently different work becomes a review without replacing the binding or merging an existing library entry', async t => {
  const env = await environment(t, [firstTitle]), game = env.library.getGames()[0]!
  await env.library.linkMetadata(game.id, work('999', secondTitle, 'vndb:v999'))
  const correct = work('101', firstTitle, 'vndb:v101')
  await env.library.addVirtual(correct)
  const before = env.library.getGames(), saved = await fs.readFile(env.library.filePath, 'utf8')
  const { matcher, calls } = harness(env, { index: [indexRecord(firstTitle, 'vndb:v101')], works: [correct] })
  await matcher.initialize(); await matcher.start(undefined, 'reidentify'); await matcher.settle()
  const result = matcher.getState()
  assert.equal(result.total, 1, 'pure online entries cannot be reidentified from local content')
  assert.equal(result.matched, 0); assert.equal(result.unmatched, 1); assert.deepEqual(calls.applied, [])
  assert.deepEqual(calls.detail, ['101']); assert.equal(result.reviews[0]!.options[0]!.work.id, '101')
  assert.match(result.reviews[0]!.reason, /另一部作品.*保留/)
  assert.deepEqual(env.library.getGames(), before); assert.equal(await fs.readFile(env.library.filePath, 'utf8'), saved)
})

test('a canonical ID change discovered through local evidence requires confirmation in reidentify mode', async t => {
  const env = await environment(t, [firstTitle]), game = env.library.getGames()[0]!
  const original = work('101', firstTitle, 'vndb:v101'), canonical = work('102', firstTitle, 'vndb:v101')
  await env.library.linkMetadata(game.id, original)
  const { matcher, calls } = harness(env, { index: [indexRecord(firstTitle, 'vndb:v101')], works: [original], detail: async () => canonical })
  await matcher.initialize(); await matcher.start([game.id], 'reidentify'); await matcher.settle()
  assert.deepEqual(calls.applied, []); assert.equal(env.library.getGame(game.id).metadata?.id, '101')
  assert.equal(matcher.getState().reviews[0]!.options[0]!.work.id, '102')
})

test('unbound games retain normal automatic matching in reidentify mode', async t => {
  const env = await environment(t, [firstTitle])
  const { matcher } = harness(env, { index: [indexRecord(firstTitle, 'vndb:v101')], works: [work('101', firstTitle, 'vndb:v101')] })
  await matcher.initialize(); await matcher.start(undefined, 'reidentify'); await matcher.settle()
  assert.equal(matcher.getState().matched, 1); assert.equal(env.library.getGames()[0]!.metadata?.id, '101')
})

test('cancelling reidentification while details are pending preserves the existing binding and persisted mode', async t => {
  const env = await environment(t, [firstTitle]), game = env.library.getGames()[0]!
  const original = work('101', firstTitle, 'vndb:v101')
  await env.library.linkMetadata(game.id, original)
  const before = env.library.getGame(game.id), entered = deferred<void>(), response = deferred<WorkMetadata>()
  const { matcher, calls } = harness(env, { index: [indexRecord(firstTitle, 'vndb:v101')], works: [original], detail: async () => { entered.resolve(); return response.promise } })
  await matcher.initialize(); await matcher.start([game.id], 'reidentify'); await entered.promise; await matcher.cancel()
  response.resolve({ ...original, description: 'cancelled refresh' }); await matcher.settle()
  assert.deepEqual(calls.applied, []); assert.deepEqual(env.library.getGame(game.id), before)
  const reopened = harness(env).matcher; await reopened.initialize()
  assert.equal(reopened.getState().status, 'cancelled'); assert.equal(reopened.getState().mode, 'reidentify')
  assert.deepEqual(reopened.getState().reviews, [])
})

for (const change of ['association', 'ABA', 'name', 'installation', 'late-error'] as const) test(`a ${change} during reidentification discards old suggestions and preserves the latest record`, async t => {
  const env = await environment(t, [firstTitle]), game = env.library.getGames()[0]!
  const original = work('999', secondTitle, 'vndb:v999'), candidate = work('101', firstTitle, 'vndb:v101')
  await env.library.linkMetadata(game.id, original)
  const entered = deferred<void>(), release = deferred<void>()
  const { matcher, calls } = harness(env, { index: [indexRecord(firstTitle, 'vndb:v101')], works: [candidate], detail: async () => {
    entered.resolve(); await release.promise
    if (change === 'late-error') throw new Error('Delayed obsolete detail failure')
    return candidate
  } })
  await matcher.initialize(); await matcher.start([game.id], 'reidentify'); await entered.promise
  if (change === 'name') await env.library.updateGame(game.id, { name: '我的新名称' })
  else if (change === 'installation') await env.library.updateGame(game.id, { preferredInstallationId: null })
  else {
    await env.library.linkMetadata(game.id, work('202', '人工确认的作品', 'vndb:v202'))
    if (change === 'ABA') await env.library.linkMetadata(game.id, original)
  }
  const current = env.library.getGame(game.id)
  release.resolve(); await matcher.settle()
  assert.deepEqual(calls.applied, []); assert.deepEqual(env.library.getGame(game.id), current)
  const result = matcher.getState()
  assert.deepEqual(result.reviews, []); assert.deepEqual([result.processed, result.matched, result.unmatched, result.failed], [1, 0, 1, 0])
})

test('personal edits during reidentification do not discard a valid same-ID refresh', async t => {
  const env = await environment(t, [firstTitle]), game = env.library.getGames()[0]!, candidate = work('101', firstTitle, 'vndb:v101')
  await env.library.linkMetadata(game.id, candidate)
  const entered = deferred<void>(), release = deferred<void>()
  const updated = { ...candidate, description: 'New independently confirmed description' }
  const { matcher } = harness(env, { index: [indexRecord(firstTitle, 'vndb:v101')], works: [candidate], detail: async () => { entered.resolve(); await release.promise; return updated } })
  await matcher.initialize(); await matcher.start([game.id], 'reidentify'); await entered.promise
  await env.library.updateGame(game.id, { notes: '新笔记', personalRating: 8, categories: ['新分类'] })
  release.resolve(); await matcher.settle()
  assert.equal(matcher.getState().matched, 1)
  const current = env.library.getGame(game.id)
  assert.equal(current.metadata?.description, updated.description); assert.equal(current.notes, '新笔记'); assert.equal(current.personalRating, 8)
  assert.deepEqual(current.categories, ['新分类'])
})

test('a fatal delayed reference error creates no review for changed or removed sources and preserves total accounting', async t => {
  const env = await environment(t, [firstTitle, secondTitle, '第三部测试作品'])
  const games = env.library.getGames(), entered = deferred<void>(), release = deferred<void>()
  const { matcher } = harness(env, { index: [indexRecord(firstTitle, 'vndb:v101')], resolve: async () => { entered.resolve(); await release.promise; throw new Error('Synthetic reference failure') } })
  await matcher.initialize(); await matcher.start(undefined, 'reidentify'); await entered.promise
  await env.library.linkMetadata(games[0]!.id, work('303', '人工选择', 'vndb:v303'))
  await env.library.removeGame(games[1]!.id)
  release.resolve(); await matcher.settle()
  const result = matcher.getState()
  assert.deepEqual([result.total, result.processed, result.matched, result.unmatched, result.failed], [3, 3, 0, 2, 1])
  assert.deepEqual(result.reviews.map(review => review.gameId), [games[2]!.id])
  assert.equal(env.library.getGame(games[0]!.id).metadata?.id, '303')
})

test('legacy matching state without a mode remains readable and interrupted work does not resume', async t => {
  const env = await environment(t, [firstTitle])
  await fs.writeFile(path.join(env.dataDir, 'metadata-matching.json'), JSON.stringify({ id: 'legacy', status: 'running', total: 1, processed: 0, matched: 0, unmatched: 0, failed: 0, currentName: firstTitle, reviews: [] }))
  const { matcher, calls } = harness(env); await matcher.initialize()
  assert.equal(matcher.getState().mode, 'missing'); assert.equal(matcher.getState().status, 'cancelled')
  assert.equal(matcher.getState().currentName, null); assert.deepEqual(calls.signals, [])
})

test('completed work status and review candidates survive restart; dismiss is persistent and leaves games intact', async t => {
  const env = await environment(t, [firstTitle])
  const game = env.library.getGames()[0]!
  const works = [work('101', firstTitle, 'vndb:v101'), work('102', firstTitle, 'vndb:v101')]
  const { matcher } = harness(env, { index: [indexRecord(firstTitle, 'vndb:v101')], works })
  await matcher.initialize()
  await matcher.start()
  await matcher.settle()
  const saved = matcher.getState()
  assert.equal(saved.reviews.length, 1)
  const restarted = harness(env).matcher
  await restarted.initialize()
  assert.deepEqual(restarted.getState(), saved)
  const externalSnapshot = restarted.getState()
  externalSnapshot.reviews[0]!.options.length = 0
  externalSnapshot.reviews.length = 0
  assert.equal(restarted.getState().reviews[0]!.options.length, 2)
  await restarted.dismiss(game.id)
  const afterDismiss = harness(env).matcher
  await afterDismiss.initialize()
  assert.deepEqual(afterDismiss.getState().reviews, [])
  assert.deepEqual(env.library.getGame(game.id), game)
  assert.equal((await fs.readdir(env.dataDir)).some(name => name.endsWith('.tmp')), false)
})

test('a persisted running job reopens as cancelled without silently starting network work', async t => {
  const env = await environment(t, [firstTitle])
  const entered = deferred<void>(), response = deferred<WorkMetadata[]>()
  const { matcher } = harness(env, {
    index: [indexRecord(firstTitle, 'vndb:v101')],
    resolve: async () => { entered.resolve(); return response.promise }
  })
  await matcher.initialize()
  const running = await matcher.start()
  await entered.promise
  const restarted = harness(env)
  await restarted.matcher.initialize()
  const state = restarted.matcher.getState()
  assert.equal(state.id, running.id)
  assert.equal(state.status, 'cancelled')
  assert.equal(state.total, 1)
  assert.equal(state.processed, 0)
  assert.equal(state.currentName, null)
  assert.deepEqual(restarted.calls.signals, [])
  await matcher.cancel()
  response.resolve([])
  await matcher.settle()
})

test('an apply refusal leaves the game for review and is not counted as an automatic success', async t => {
  const env = await environment(t, [firstTitle])
  const { matcher, calls } = harness(env, {
    index: [indexRecord(firstTitle, 'vndb:v101')], works: [work('101', firstTitle, 'vndb:v101')],
    apply: async () => false
  })
  await matcher.initialize()
  await matcher.start()
  await matcher.settle()
  const state = matcher.getState()
  assert.equal(calls.applied.length, 1)
  assert.deepEqual([state.processed, state.matched, state.unmatched, state.failed], [1, 0, 1, 0])
  assert.equal(state.reviews.length, 1)
  assert.equal(state.reviews[0]!.options[0]!.work.id, '101')
  assert.equal(env.library.getGames()[0]!.metadata, null)
})

test('detail data that no longer matches the selected identity is not applied automatically', async t => {
  const env = await environment(t, [firstTitle])
  const { matcher, calls } = harness(env, {
    index: [indexRecord(firstTitle, 'vndb:v101')], works: [work('101', firstTitle, 'vndb:v101')],
    detail: async () => work('101', '完全无关的另一作品', 'vndb:v999')
  })
  await matcher.initialize()
  await matcher.start()
  await matcher.settle()
  assert.deepEqual(calls.applied, [])
  assert.equal(matcher.getState().matched, 0)
  assert.equal(matcher.getState().reviews.length, 1)
  assert.equal(env.library.getGames()[0]!.metadata, null)
})

test('a targeted job leaves unselected games untouched and failed detail requests remain reviewable', async t => {
  const env = await environment(t, [firstTitle, secondTitle])
  const selected = env.library.getGames().find(game => game.name === firstTitle)!
  const other = env.library.getGames().find(game => game.name === secondTitle)!
  const { matcher, calls } = harness(env, {
    index: [indexRecord(firstTitle, 'vndb:v101'), indexRecord(secondTitle, 'vndb:v202')],
    works: [work('101', firstTitle, 'vndb:v101')],
    detail: async () => { throw new Error('暂时无法读取作品资料') }
  })
  await matcher.initialize()
  await matcher.start([selected.id])
  await matcher.settle()
  const state = matcher.getState()
  assert.equal(state.total, 1)
  assert.deepEqual([state.processed, state.matched, state.unmatched, state.failed], [1, 0, 0, 1])
  assert.deepEqual(calls.refs, [['vndb:v101']])
  assert.equal(state.reviews[0]!.gameId, selected.id)
  assert.match(state.reviews[0]!.reason, /无法读取/)
  assert.deepEqual(env.library.getGame(other.id), other)
  assert.deepEqual(calls.applied, [])
})

for (const kind of ['default', 'trial', 'patch'] as const) test(`edition release ${kind} is rechecked against duplicates before applying metadata`, async t => {
  const title = 'Summer Pockets REFLECTION BLUE'
  const env = await environment(t, [title])
  const anchored = work('4', title, 'vndb:v20424')
  const thin = work('270330', title, 'dlsite:VJ013550')
  const entered = deferred<void>(), response = deferred<ReleaseIdentity[]>()
  const requests: string[] = []
  const { matcher, calls } = harness(env, {
    index: [indexRecord(title, 'vndb:v20424')], works: [anchored, thin],
    resolve: async () => [anchored], search: async () => ({ items: [thin], nextCursor: null }),
    releases: async id => { requests.push(id); entered.resolve(); return response.promise }
  })
  await matcher.initialize(); await matcher.start(); await entered.promise
  assert.deepEqual(calls.applied, [], 'metadata stays unlinked while edition evidence is pending')
  response.resolve([{ id: '213462', workId: '4', title, originalTitle: title, kind, date: '2020-06-26', references: [] }])
  await matcher.settle()
  assert.deepEqual(requests, ['4'], 'unanchored duplicate records cannot create edition evidence')
  assert.equal(matcher.getState().failed, 0)
  assert.equal(matcher.getState().matched, kind === 'default' ? 1 : 0)
  assert.equal(env.library.getGames()[0].metadata?.id ?? null, kind === 'default' ? '4' : null)
})

test('cancellation during edition retrieval prevents a late release from applying metadata', async t => {
  const title = 'Summer Pockets REFLECTION BLUE'
  const env = await environment(t, [title])
  const anchored = work('4', title, 'vndb:v20424')
  const thin = work('270330', title, 'dlsite:VJ013550')
  const entered = deferred<void>(), response = deferred<ReleaseIdentity[]>()
  const { matcher, calls } = harness(env, {
    index: [indexRecord(title, 'vndb:v20424')], works: [anchored, thin],
    resolve: async () => [anchored], search: async () => ({ items: [thin], nextCursor: null }),
    releases: async (_id, signal) => { entered.resolve(); const result = await response.promise; assert.equal(signal.aborted, true); return result }
  })
  await matcher.initialize(); await matcher.start(); await entered.promise; await matcher.cancel()
  response.resolve([{ id: '213462', workId: '4', title, originalTitle: title, kind: 'default', date: null, references: [] }])
  await matcher.settle()
  assert.equal(matcher.getState().status, 'cancelled')
  assert.deepEqual(calls.applied, [])
  assert.equal(env.library.getGames()[0].metadata, null)
})
