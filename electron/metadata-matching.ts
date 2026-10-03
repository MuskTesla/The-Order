import { randomUUID } from 'node:crypto'
import { promises as fs } from 'node:fs'
import path from 'node:path'
import { gunzipSync } from 'node:zlib'
import type { LibraryGame, MatchOption, MatchReview, MetadataMatchingMode, MetadataMatchingState, SearchPage, WorkMetadata, ReleaseIdentity } from '../src/shared/types'
import { readGameIdentity, rankMetadataMatches, type GameIdentity, type TitleIndexRecord } from './game-identification'
import type { MetadataRequestToken } from './library'
import { hasExplicitGameVersion } from './title-matching'

export interface MatchingClient {
  resolveWorksByRefs(refs: string[]): Promise<WorkMetadata[]>
  searchWorks(query: string): Promise<SearchPage>
  getWork(id: string): Promise<WorkMetadata>
  getWorkReleases?(id: string): Promise<ReleaseIdentity[]>
}
interface MatchingLibrary { getGames(): LibraryGame[]; getGame(id: string): LibraryGame }
interface MatchingDependencies {
  client(signal: AbortSignal): MatchingClient
  capture?(game: LibraryGame, batchId: string): MetadataRequestToken
  apply(game: LibraryGame, work: WorkMetadata, token: MetadataRequestToken | undefined, signal: AbortSignal): Promise<boolean>
  index(): Promise<TitleIndexRecord[]>
  changed(): void
  readExecutableMetadata?(): boolean
}
const empty = (): MetadataMatchingState => ({ id: null, status: 'idle', total: 0, processed: 0, matched: 0, unmatched: 0, failed: 0, currentName: null, reviews: [], mode: 'missing' })
const clone = <T>(value: T): T => structuredClone(value)

export async function loadTitleIndex(file: string): Promise<TitleIndexRecord[]> {
  const bytes = await fs.readFile(file)
  if (bytes.length > 8 * 1024 * 1024) throw new Error('本地标题索引过大。')
  const data = JSON.parse(gunzipSync(bytes, { maxOutputLength: 32 * 1024 * 1024 }).toString('utf8'))
  if (data.version !== 1 || !Array.isArray(data.records) || data.records.length > 150000) throw new Error('本地标题索引格式不正确。')
  return data.records.filter((row: TitleIndexRecord) => Array.isArray(row.names) && Array.isArray(row.references) && row.names.every(name => typeof name === 'string') && row.references.every(ref => typeof ref === 'string'))
}

export class MetadataMatcher {
  private state = empty()
  private controller: AbortController | null = null
  private saveQueue: Promise<void> = Promise.resolve()
  private task: Promise<void> = Promise.resolve()
  private readonly file: string

  constructor(dataDir: string, private readonly library: MatchingLibrary, private readonly dependencies: MatchingDependencies) { this.file = path.join(dataDir, 'metadata-matching.json') }
  getState(): MetadataMatchingState {
    const games = new Set(this.library.getGames().map(game => game.id))
    return clone({ ...this.state, reviews: this.state.reviews.filter(review => games.has(review.gameId)) })
  }
  async initialize(): Promise<void> {
    try {
      const stat = await fs.stat(this.file)
      if (stat.size > 16 * 1024 * 1024) return
      const saved = JSON.parse(await fs.readFile(this.file, 'utf8'))
      if (!['idle', 'running', 'completed', 'cancelled'].includes(saved.status) || !Array.isArray(saved.reviews) || saved.reviews.length > 10000) return
      for (const field of ['total', 'processed', 'matched', 'unmatched', 'failed']) if (!Number.isInteger(saved[field]) || saved[field] < 0 || saved[field] > 10000) return
      if (saved.id !== null && typeof saved.id !== 'string') return
      if (saved.mode !== undefined && !['missing', 'reidentify'].includes(saved.mode)) return
      saved.reviews = saved.reviews.filter((review: MatchReview) => typeof review.gameId === 'string' && typeof review.name === 'string' && typeof review.folderPath === 'string' && typeof review.reason === 'string' && Array.isArray(review.options) && review.options.length <= 5 && review.options.every(option => option.work && typeof option.work.id === 'string' && typeof option.work.title === 'string' && typeof option.work.originalTitle === 'string' && Array.isArray(option.work.developers) && typeof option.confidence === 'number'))
      this.state = { ...saved, mode: saved.mode ?? 'missing', currentName: null, status: saved.status === 'running' ? 'cancelled' : saved.status }
    } catch { /* Interrupted or corrupt job data never prevents the library opening. */ }
  }
  async start(ids?: string[], mode: MetadataMatchingMode = 'missing'): Promise<MetadataMatchingState> {
    if (!['missing', 'reidentify'].includes(mode)) throw new Error('资料识别模式不正确。')
    if (this.state.status === 'running') throw new Error('资料识别正在进行，请等待完成或先取消。')
    const all = this.library.getGames()
    const requested = ids === undefined ? null : new Set(ids)
    const games = all.filter(game => (!requested || requested.has(game.id)) && game.installations.length && (mode === 'reidentify' || !game.metadata || !game.metadata.coverUrl))
    if (!games.length) throw new Error(mode === 'reidentify' ? '没有可重新识别的本地游戏。' : '本地游戏的封面和资料已齐全。')
    const jobId = randomUUID()
    const tokens = new Map(games.map(game => [game.id, this.dependencies.capture?.(game, jobId)]))
    const controller = new AbortController()
    // Validate credentials synchronously, before marking a job as running.
    const client = this.dependencies.client(controller.signal)
    this.controller = controller
    const selected = new Set(games.map(game => game.id))
    this.state = { ...empty(), id: jobId, mode, status: 'running', total: games.length, reviews: this.state.reviews.filter(review => !selected.has(review.gameId)) }
    const job = this.state
    await this.save()
    if (this.state !== job || controller.signal.aborted) return this.getState()
    this.dependencies.changed()
    this.task = this.run(job, games, client, controller.signal, tokens).catch(async error => {
      if (this.state !== job || controller.signal.aborted) return
      job.status = 'completed'; job.currentName = null
      for (const game of games.slice(job.processed)) {
        if (this.sourceCurrent(game, tokens.get(game.id), jobId)) { job.failed++; this.addReview(job, game, [], this.message(error)) }
        else { job.unmatched++; job.reviews = job.reviews.filter(review => review.gameId !== game.id) }
      }
      job.processed = job.total
      await this.save().catch(() => undefined); this.dependencies.changed()
    })
    return this.getState()
  }
  async cancel(): Promise<MetadataMatchingState> {
    this.controller?.abort()
    if (this.state.status === 'running') { this.state.status = 'cancelled'; this.state.currentName = null; await this.save(); this.dependencies.changed() }
    return this.getState()
  }
  async shutdown(): Promise<void> { await this.cancel(); await this.saveQueue }
  async settle(): Promise<void> { await this.task }
  async dismiss(gameId: string): Promise<MetadataMatchingState> {
    this.state.reviews = this.state.reviews.filter(review => review.gameId !== gameId)
    await this.save(); this.dependencies.changed(); return this.getState()
  }
  private message(error: unknown): string { return (error instanceof Error ? error.message : '资料获取失败，请稍后重试。').replace(/nmk_(?:live|test)_[\w-]+/g, '[密钥已隐藏]').replace(/Bearer\s+\S+/gi, 'Bearer [密钥已隐藏]').slice(0, 300) }
  private addReview(job: MetadataMatchingState, game: LibraryGame, options: MatchOption[], reason: string): void {
    job.reviews = job.reviews.filter(review => review.gameId !== game.id)
    job.reviews.push({ gameId: game.id, name: game.name, folderPath: game.installations[0]?.folderPath ?? '', reason, options: options.slice(0, 5) })
  }
  private sourceCurrent(original: LibraryGame, token: MetadataRequestToken | undefined, batchId: string): boolean {
    try {
      const current = this.library.getGame(original.id)
      const locations = (game: LibraryGame) => JSON.stringify(game.installations.map(({ id, folderPath, executablePath }) => ({ id, folderPath, executablePath })))
      if (current.name !== original.name || current.nameIsCustom !== original.nameIsCustom || current.preferredInstallationId !== original.preferredInstallationId ||
        JSON.stringify(current.metadata) !== JSON.stringify(original.metadata) || locations(current) !== locations(original)) return false
      if (token) {
        const now = this.dependencies.capture?.(current, batchId)
        if (!now || now.epoch !== token.epoch || now.gameId !== token.gameId || now.generation !== token.generation || now.requestId !== token.requestId) return false
      }
      return true
    } catch { return false }
  }
  private async run(job: MetadataMatchingState, games: LibraryGame[], client: MatchingClient, signal: AbortSignal, tokens: Map<string, MetadataRequestToken | undefined>): Promise<void> {
    const alive = () => this.state === job && job.status === 'running' && !signal.aborted
    if (!alive()) return
    job.currentName = '读取本地名称与作品编号'; this.dependencies.changed()
    const index = await this.dependencies.index().catch(() => [] as TitleIndexRecord[])
    if (!alive()) return
    const identities: GameIdentity[] = []
    const readExecutableMetadata = this.dependencies.readExecutableMetadata?.() === true
    for (const game of games) {
      if (!alive()) return
      const installation = game.installations.find(item => item.id === game.preferredInstallationId) ?? game.installations[0]
      // An old provider title is not an independent local observation. Preserve
      // an explicitly personal title; otherwise derive the read-only input from
      // the preferred installation, without changing the actual library record.
      const input = job.mode === 'reidentify' && game.metadata && game.nameIsCustom !== true
        ? { ...game, name: installation ? path.basename(installation.folderPath) : '', metadata: null }
        : game
      identities.push(game.metadata && job.mode !== 'reidentify' ? { names: [], references: [], referenceSource: 'none' } : await readGameIdentity(input, index, { readExecutableMetadata }))
    }
    const references = [...new Set(identities.flatMap(identity => identity.references))]
    const referenceWorks = new Map<string, WorkMetadata>()
    for (let offset = 0; offset < references.length;) {
      if (!alive()) return
      const batch: string[] = []
      while (offset < references.length && batch.length < 100 && [...batch, references[offset]!].join(',').length <= 4096) batch.push(references[offset++]!)
      if (!batch.length) break
      const works = await client.resolveWorksByRefs(batch)
      if (!alive()) return
      for (const work of works) referenceWorks.set(work.id, work)
    }
    const details = new Map<string, WorkMetadata>()
    const releases = new Map<string, ReleaseIdentity[]>()
    const searches = new Map<string, WorkMetadata[]>()
    for (let position = 0; position < games.length; position++) {
      if (!alive()) return
      const game = games[position]!, identity = identities[position]!
      job.currentName = game.name; this.dependencies.changed()
      try {
        let work: WorkMetadata | undefined
        let options: ReturnType<typeof rankMetadataMatches> = []
        if (game.metadata && job.mode !== 'reidentify') work = await client.getWork(game.metadata.id)
        else {
          const refs = new Set(identity.references)
          const found = [...referenceWorks.values()].filter(candidate => candidate.references.some(ref => refs.has(`${ref.source}:${ref.id}`)))
          options = rankMetadataMatches(identity, found)
          // A base-work index can omit a separate edition. Retrieve the complete
          // local edition title even when the base reference initially matches.
          const verifyEdition = identity.evidence?.titles.some(title => ['folder', 'custom-name', 'product-name', 'file-description', 'readme-title', 'manual-title', 'config-title'].includes(title.source) && hasExplicitGameVersion(title.value)) === true
          if (!options.some(option => option.automatic) || verifyEdition) {
            // Try a small set of distinctive titles. Never discard episode/version words.
            for (const name of (identity.queries ?? identity.names).slice(0, 6)) {
              if (!alive()) return
              let results = searches.get(name)
              if (!results) { results = (await client.searchWorks(name)).items; searches.set(name, results) }
              found.push(...results)
              options = rankMetadataMatches(identity, [...new Map(found.map(candidate => [candidate.id, candidate])).values()])
              if (options.some(option => option.automatic) && !verifyEdition) break
            }
            // Collection endpoints expose only elected names. Fetch a bounded
            // shortlist to compare complete official titles, aliases and editions.
            // Translated collection names may share no characters with a local
            // title. The response to that title's query still deserves a bounded
            // detail read; its ranking alone never authorizes an association.
            const localReferences = new Set(identity.evidence ? identity.evidence.references.filter(ref => ref.source !== 'sidecar').map(ref => ref.value) : identity.references)
            const referenceHit = (candidate: WorkMetadata) => candidate.references.some(ref => localReferences.has(`${ref.source}:${ref.id}`))
            const shortlist = [...options].sort((a, b) => Number(referenceHit(b.work)) - Number(referenceHit(a.work)) || b.confidence - a.confidence).slice(0, 3)
            for (const candidate of shortlist) {
              if (!alive()) return
              const detail = details.get(candidate.work.id) ?? await client.getWork(candidate.work.id)
              details.set(candidate.work.id, detail)
              const position = found.findIndex(work => work.id === candidate.work.id)
              if (position >= 0) found.splice(position, 1, detail)
              // Retain every alternate candidate while deciding, including any
              // conflicting refs. Full details cannot erase competing evidence.
              for (let n = found.length - 1; n >= 0; n--) if (found[n]!.id === candidate.work.id && n !== position) found.splice(n, 1)
            }
            if (verifyEdition && client.getWorkReleases) {
              // An elected work title may aggregate several editions. A small
              // independently anchored shortlist can use its release catalog
              // to confirm the observed edition, without counting releases as votes.
              for (const candidate of shortlist.filter(option => referenceHit(option.work)).slice(0, 2)) {
                if (!alive()) return
                let editions = releases.get(candidate.work.id)
                if (!editions) {
                  try { editions = await client.getWorkReleases(candidate.work.id) }
                  catch (error) {
                    if (!alive()) return
                    if (['UNAUTHORIZED', 'FORBIDDEN', 'INVALID_API_KEY', 'EXPIRED_API_KEY', 'INSUFFICIENT_SCOPE', 'QUOTA_EXCEEDED'].includes((error as { code?: string }).code ?? '')) throw error
                    // This supplementary endpoint may be absent on compatible
                    // hosts. Existing title/reference gates still apply.
                    editions = []
                  }
                  releases.set(candidate.work.id, editions)
                }
                identity.releases ??= {}
                identity.releases[candidate.work.id] = editions
              }
            }
            options = rankMetadataMatches(identity, [...new Map(found.map(candidate => [candidate.id, candidate])).values()])
          }
          const chosen = options.find(option => option.automatic)
          if (chosen) {
            work = details.get(chosen.work.id) ?? await client.getWork(chosen.work.id)
            details.set(chosen.work.id, work)
            const verified = rankMetadataMatches(identity, [work, ...options.filter(option => option.work.id !== chosen.work.id).map(option => option.work)])
            options = verified
            if (!verified.some(option => option.work.id === work!.id && option.automatic)) work = undefined
          }
        }
        if (!alive()) return
        const suggestions = options.filter(option => option.confidence >= 0.4).map(({ work, confidence, reason }) => ({ work, confidence, reason }))
        if (!this.sourceCurrent(game, tokens.get(game.id), job.id!)) {
          // A newer manual choice supersedes this snapshot. Do not leave an old
          // suggestion or error for a different binding/installation to confirm.
          job.unmatched++; job.reviews = job.reviews.filter(review => review.gameId !== game.id)
        } else if (job.mode === 'reidentify' && game.metadata && work?.id !== game.metadata.id) {
          job.unmatched++
          this.addReview(job, game, suggestions, work
            ? '本地线索指向另一部作品，当前关联和个人记录已保留。请核对候选后确认更换。'
            : '重新识别尚无足够的独立证据，当前关联和个人记录已保留。可核对候选或重新搜索。')
        } else {
          // The write may wait behind an identity/store transaction. Cancellation
          // must reach that queue, and a late completion cannot update another job.
          const applied = work && await this.dependencies.apply(game, work, tokens.get(game.id), signal)
          if (!alive()) return
          if (applied) { job.matched++; job.reviews = job.reviews.filter(review => review.gameId !== game.id) }
          else {
            job.unmatched++
            if (this.sourceCurrent(game, tokens.get(game.id), job.id!)) this.addReview(job, game, suggestions, identity.evidence?.auxiliaryDirectory ? '这是补丁或备份目录，请选择实际游戏根目录；可移除此书架条目，磁盘文件会保留。' : suggestions.length ? '候选证据接近或有冲突，请核对作品、章节与版本。' : '暂未找到足够的独立证据，可重新搜索或稍后重试。')
          }
        }
      } catch (error) {
        if (!alive()) return
        if (!this.sourceCurrent(game, tokens.get(game.id), job.id!)) { job.unmatched++; job.reviews = job.reviews.filter(review => review.gameId !== game.id) }
        else { job.failed++; this.addReview(job, game, [], this.message(error)) }
        if (['UNAUTHORIZED', 'FORBIDDEN', 'INVALID_API_KEY', 'EXPIRED_API_KEY', 'INSUFFICIENT_SCOPE', 'QUOTA_EXCEEDED'].includes((error as { code?: string }).code ?? '')) {
          job.processed++
          throw error
        }
      }
      job.processed++; await this.save(); this.dependencies.changed()
    }
    if (!alive()) return
    job.status = 'completed'; job.currentName = null; await this.save(); this.dependencies.changed()
  }
  private save(): Promise<void> {
    const document = JSON.stringify(this.state)
    const run = this.saveQueue.then(async () => {
      await fs.mkdir(path.dirname(this.file), { recursive: true })
      const temporary = `${this.file}.${randomUUID()}.tmp`
      try { await fs.writeFile(temporary, document, 'utf8'); await fs.rename(temporary, this.file) }
      finally { await fs.rm(temporary, { force: true }).catch(() => undefined) }
    })
    this.saveQueue = run.catch(() => undefined)
    return run
  }
}
