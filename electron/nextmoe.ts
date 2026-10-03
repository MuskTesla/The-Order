import type { ConnectionResult, ReleaseIdentity, SearchPage, SourceRating, WorkMetadata, WorkReference } from '../src/shared/types'

export const DEFAULT_BASE_URL = 'https://api.nextmoe.dev/v2/'
const DECIMAL_ID = /^[0-9]{1,20}$/
const WORK_REF = /^(?:vndb:v[1-9][0-9]*|(?:bangumi|steam):[1-9][0-9]*)$/
const MAX_RESPONSE_BYTES = 8 * 1024 * 1024
const RELEASE_PAGE_SIZE = 100
const RELEASE_KINDS = new Set(['default', 'digital', 'physical', 'trial', 'patch'])
const KNOWN_SCALES: Readonly<Record<string, number>> = {
  vndb: 10, bangumi: 10, erogamescape: 100, dlsite: 5
}
const REFERENCE_HOSTS = new Set([
  'vndb.org', 'bgm.tv', 'bangumi.tv', 'chii.in', 'store.steampowered.com',
  'erogamescape.dyndns.org', 'howlongtobeat.com', 'www.dlsite.com', 'www.kungal.com'
])
const KNOWN_PROBLEM_CODES = new Set([
  'ENTITY_MERGED', 'RATE_LIMITED', 'QUOTA_EXCEEDED', 'NOT_FOUND', 'UNAUTHORIZED',
  'FORBIDDEN', 'INVALID_REQUEST', 'VALIDATION_ERROR', 'INVALID_API_KEY', 'EXPIRED_API_KEY',
  'INSUFFICIENT_SCOPE', 'UNKNOWN_INCLUDE', 'UNKNOWN_FIELD', 'LIMIT_TOO_LARGE', 'INTERNAL_ERROR',
  'SERVICE_UNAVAILABLE'
])

export interface NextMoeOptions { baseUrl: string; apiKey: string; includeNsfw: boolean; signal?: AbortSignal }
interface ClientDependencies {
  fetch?: typeof fetch
  delay?: (milliseconds: number, signal: AbortSignal) => Promise<void>
  timeoutMs?: number
}
type JsonObject = Record<string, unknown>
interface RequestScope { signal: AbortSignal; deadline: number; retries: number }

/** Only controlled messages leave the main process; server detail text may contain secrets. */
export class NextMoeError extends Error {
  constructor(message: string, public readonly code: string, public readonly status: number | null = null) {
    super(message)
    this.name = 'NextMoeError'
  }
}

export function normalizeBaseUrl(input: string): string {
  let url: URL
  try { url = new URL(input.trim()) } catch { throw new NextMoeError('API 地址格式不正确。', 'INVALID_BASE_URL') }
  const loopback = url.hostname === 'localhost' || url.hostname === '127.0.0.1' || url.hostname === '[::1]'
  if ((url.protocol !== 'https:' && !(url.protocol === 'http:' && loopback)) || url.username || url.password || url.search || url.hash) {
    throw new NextMoeError('API 地址须为 HTTPS，且不能包含账号、查询参数或片段；本机测试可用 HTTP。', 'INVALID_BASE_URL')
  }
  url.pathname = url.pathname === '/' ? '/v2/' : `${url.pathname.replace(/\/+$/, '')}/`
  return url.href
}

function object(value: unknown): JsonObject {
  return typeof value === 'object' && value !== null && !Array.isArray(value) ? value as JsonObject : {}
}
function rows(value: unknown): JsonObject[] { return Array.isArray(value) ? value.map(object) : [] }
function text(value: unknown): string { return typeof value === 'string' ? value.trim() : '' }
function unique(values: string[]): string[] { return [...new Set(values.filter(Boolean))] }
function localized(value: JsonObject, languages = ['zh-Hans', 'zh-Hant']): string {
  const names = object(value.localized)
  for (const language of languages) {
    const name = text(object(names[language]).value)
    if (name) return name
  }
  return text(value.display_name)
}
function imageUrl(value: unknown): string | null {
  const raw = text(object(value).url)
  if (!raw) return null
  try {
    const url = new URL(raw)
    return url.protocol === 'https:' && !url.username && !url.password ? url.href : null
  } catch { return null }
}

function referenceUrl(source: string, id: string): string | undefined {
  if (source === 'vndb' && /^[vr][0-9]+$/.test(id)) return `https://vndb.org/${id}`
  if (!DECIMAL_ID.test(id)) return undefined
  if (source === 'bangumi') return `https://bgm.tv/subject/${id}`
  if (source === 'steam') return `https://store.steampowered.com/app/${id}/`
  if (source === 'erogamescape') return `https://erogamescape.dyndns.org/~ap2/ero/toukei_kaiseki/game.php?game=${id}`
  if (source === 'howlongtobeat') return `https://howlongtobeat.com/game/${id}`
  return undefined
}

function suppliedReferenceUrl(value: unknown): string | undefined {
  try {
    const url = new URL(text(value))
    if (url.protocol === 'https:' && !url.username && !url.password && !url.port && REFERENCE_HOSTS.has(url.hostname)) return url.href
  } catch { /* Malformed optional links are omitted. */ }
  return undefined
}

function mapTitleEvidence(work: JsonObject, titles: JsonObject[]): NonNullable<WorkMetadata['titleEvidence']> {
  const evidence = new Map<string, NonNullable<WorkMetadata['titleEvidence']>[number]>()
  const add = (value: unknown, kind: string, isMachine: boolean, language: string | null) => {
    const name = text(value)
    if (!name || name.length >= 500) return
    const key = JSON.stringify([name, kind])
    const previous = evidence.get(key)
    if (previous) {
      // Conflicting or incomplete provenance must not upgrade a duplicate title.
      previous.isMachine ||= isMachine
      if (previous.language !== language) previous.language = null
    } else if (evidence.size < 499) evidence.set(key, { text: name, kind, isMachine, language })
  }
  // Elected collection romanization has no title_kind; keep it distinct from an
  // official title row even when their text happens to be identical.
  add(work.latin, 'latin', false, null)
  for (const row of titles) {
    const rawKind = text(row.title_kind)
    const kind = ['official', 'alias', 'abbreviation'].includes(rawKind) ? rawKind : 'unknown'
    const rawLanguage = text(row.lang)
    const language = rawLanguage && rawLanguage.length <= 32 ? rawLanguage : null
    const isMachine = row.is_machine !== false
    add(row.title, kind, isMachine, language)
    add(row.latin, kind, isMachine, language)
  }
  return [...evidence.values()]
}

function mapWork(value: unknown): WorkMetadata {
  const work = object(value)
  const id = text(work.id)
  if (work.object !== 'work' || !DECIMAL_ID.test(id)) {
    throw new NextMoeError('API 返回的作品数据格式不正确。', 'INVALID_RESPONSE')
  }
  const title = localized(work) || `作品 ${id}`
  const originalTitle = text(work.display_name) || title
  const titles = rows(work.titles)
  const references: WorkReference[] = rows(work.refs).flatMap(ref => {
    const source = text(ref.source)
    const externalId = text(ref.external_id)
    if (!source || !externalId) return []
    const url = referenceUrl(source, externalId)
    return [{ source, id: externalId, ...(url ? { url } : {}) }]
  })
  const referenceUrls = new Set(references.map(ref => ref.url).filter(Boolean))
  for (const link of rows(work.links)) {
    const source = text(link.source)
    const url = suppliedReferenceUrl(link.url)
    if (source && url && !referenceUrls.has(url)) {
      references.push({ source, id: url, url })
      referenceUrls.add(url)
    }
  }
  const ratings: SourceRating[] = rows(work.ratings).flatMap(rating => {
    const source = text(rating.source)
    const scale = KNOWN_SCALES[source]
    const score = rating.score
    // NextMoe returns native scores. Unknown sources have no documented denominator.
    if (!scale || typeof score !== 'number' || !Number.isFinite(score) || score < 0 || score > scale) return []
    const votes = typeof rating.vote_count === 'number' && Number.isSafeInteger(rating.vote_count) && rating.vote_count >= 0
      ? rating.vote_count : null
    return [{ source, score, scale, votes }]
  })
  const intros = rows(work.intros).filter(intro => text(intro.value))
  const description = ['zh-Hans', 'zh-Hant', 'ja', 'en'].map(lang => intros.find(intro => intro.lang === lang)).find(Boolean) ?? intros[0]
  const claim = object(work.claim)
  const claimId = text(claim.site_work_id)
  const releaseDate = text(work.release_date)
  return {
    id, title, originalTitle,
    // Collection include=titles elects latin/localized, while full titles[] only
    // arrives on detail reads. Keep romanizations from both documented shapes.
    aliases: unique([text(work.latin), ...titles.flatMap(item => [text(item.title), text(item.latin)])]
      .filter(item => item !== title && item !== originalTitle)),
    titleEvidence: mapTitleEvidence(work, titles),
    coverUrl: imageUrl(work.cover) ?? rows(work.covers).map(imageUrl).find(Boolean) ?? null,
    bannerUrl: imageUrl(work.banner),
    description: text(description?.value),
    releaseDate: /^\d{4}-\d{2}-\d{2}$/.test(releaseDate) ? releaseDate : null,
    releaseDatePrecision: text(work.release_date_precision) || null,
    developers: unique(rows(work.companies).filter(company => company.attribution_role === 'developer').map(company => localized(company))),
    tags: unique(rows(work.tags).filter(tag => tag.tier !== 'hidden' && (!tag.spoiler || tag.spoiler === 'none')).map(tag => localized(tag))),
    ratings,
    characters: rows(work.characters).filter(character => !character.spoiler || character.spoiler === 'none').flatMap(character => {
      const characterId = text(character.id)
      if (!DECIMAL_ID.test(characterId)) return []
      return [{
        id: characterId, name: localized(character) || `角色 ${characterId}`,
        originalName: text(character.display_name), imageUrl: imageUrl(character.image),
        role: text(character.roster_role), voiceActors: unique(rows(character.voices).map(voice => localized(voice)))
      }]
    }),
    references,
    webUrl: claim.site === 'kungal' && claim.state === 'live' && DECIMAL_ID.test(claimId) ? `https://www.kungal.com/galgame/${claimId}` : null,
    contentRating: text(work.content_rating) || null,
    fetchedAt: new Date().toISOString()
  }
}

function mapRelease(value: unknown, workId: string): ReleaseIdentity {
  const release = object(value)
  const id = text(release.id)
  // On a work sub-resource, the documented null parent means the URL's work.
  // An explicit foreign parent must never become evidence for this work.
  if (release.object !== 'release' || !DECIMAL_ID.test(id) || release.id !== id ||
    (release.work_id !== null && release.work_id !== workId)) {
    throw new NextMoeError('API 返回的发行记录或作品归属格式不正确。', 'INVALID_RESPONSE')
  }
  const title = text(release.title)
  if ((release.title != null && typeof release.title !== 'string') || title.length > 512 ||
    (release.refs !== undefined && !Array.isArray(release.refs))) {
    throw new NextMoeError('API 返回的发行资料格式不正确。', 'INVALID_RESPONSE')
  }
  const references: WorkReference[] = []
  const seen = new Set<string>()
  for (const value of release.refs ?? []) {
    const ref = object(value)
    const source = text(ref.source)
    const externalId = text(ref.external_id)
    if (!source || source.length > 64 || source !== ref.source || !externalId || externalId.length > 256 || externalId !== ref.external_id) {
      throw new NextMoeError('API 返回的发行来源编号格式不正确。', 'INVALID_RESPONSE')
    }
    const key = JSON.stringify([source, externalId])
    if (seen.has(key)) continue
    seen.add(key)
    const url = referenceUrl(source, externalId)
    references.push({ source, id: externalId, ...(url ? { url } : {}) })
  }
  const kind = text(release.release_kind)
  const date = text(release.date)
  return {
    id, workId, title, originalTitle: title, references,
    kind: RELEASE_KINDS.has(kind) ? kind : 'unknown',
    date: /^\d{4}-\d{2}-\d{2}$/.test(date) ? date : null
  }
}

function wait(milliseconds: number, signal: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    if (signal.aborted) { reject(signal.reason); return }
    const aborted = () => { clearTimeout(timer); reject(signal.reason) }
    const timer = setTimeout(() => { signal.removeEventListener('abort', aborted); resolve() }, milliseconds)
    signal.addEventListener('abort', aborted, { once: true })
  })
}

function problemCode(body: unknown): string {
  const code = text(object(body).code)
  // Echo only known protocol identifiers, never arbitrary server messages or HTML.
  return KNOWN_PROBLEM_CODES.has(code) ? code : 'HTTP_ERROR'
}

function httpError(status: number, code: string): NextMoeError {
  let message = 'API 请求失败，请稍后再试。'
  if (status === 401) message = 'API Key 无效或已过期，请在设置中重新填写。'
  else if (status === 403) message = 'API Key 没有访问该接口的权限。'
  else if (status === 404) message = '没有找到该作品，或当前设置不允许查看此内容。'
  else if (status === 400) message = 'API 不接受当前请求参数，请检查接口地址与版本。'
  else if (status === 429) message = code === 'QUOTA_EXCEEDED' ? 'API 配额已用尽，请在服务方控制台查看额度。' : 'API 请求过于频繁，请稍后再试。'
  else if (status >= 500) message = 'API 服务暂时不可用，请稍后再试。'
  else if (status >= 300 && status < 400) message = 'API 返回了重定向，已停止请求；请检查接口地址。'
  return new NextMoeError(message, code, status)
}

function cancelledError(): NextMoeError { return new NextMoeError('API 请求已取消。', 'CANCELLED') }
function abortError(signal: AbortSignal): NextMoeError {
  // The caller's abort reason can contain arbitrary text; only our controlled error escapes.
  return signal.reason instanceof NextMoeError && signal.reason.code === 'TIMEOUT' ? signal.reason : cancelledError()
}

export class NextMoeClient {
  private readonly baseUrl: string
  private readonly apiKey: string
  private readonly includeNsfw: boolean
  private readonly fetcher: typeof fetch
  private readonly delay: (milliseconds: number, signal: AbortSignal) => Promise<void>
  private readonly timeoutMs: number
  private readonly signal?: AbortSignal

  constructor(options: NextMoeOptions, dependencies: ClientDependencies = {}) {
    this.baseUrl = normalizeBaseUrl(options.baseUrl)
    this.apiKey = options.apiKey.trim()
    if (!this.apiKey || this.apiKey.length > 4096 || !/^[\x21-\x7e]+$/.test(this.apiKey)) {
      throw new NextMoeError('请先在设置中填写有效的 API Key。', 'MISSING_API_KEY')
    }
    this.includeNsfw = options.includeNsfw
    this.signal = options.signal
    this.fetcher = dependencies.fetch ?? globalThis.fetch
    this.delay = dependencies.delay ?? wait
    this.timeoutMs = dependencies.timeoutMs ?? 45_000
    if (!Number.isFinite(this.timeoutMs) || this.timeoutMs <= 0 || this.timeoutMs > 45_000) {
      throw new NextMoeError('请求超时设置无效。', 'INVALID_TIMEOUT')
    }
  }

  async resolveWorksByRefs(refs: string[]): Promise<WorkMetadata[]> {
    if (!Array.isArray(refs) || Array.from(refs).some(ref => typeof ref !== 'string' || ref !== ref.trim() || !WORK_REF.test(ref))) {
      throw new NextMoeError('作品来源编号须为有效的 VNDB、Bangumi 或 Steam 作品编号。', 'INVALID_REFS')
    }
    const requested = new Set(refs)
    const joined = [...requested].join(',')
    if (!requested.size || requested.size > 100 || joined.length > 4096) {
      throw new NextMoeError('每次可识别 1 至 100 个不同的来源编号，总长度不能超过 4096 个字符。', 'INVALID_REFS')
    }
    return this.withTimeout(async scope => {
      const body = object(await this.request('catalog/works', {
        refs: joined, include: 'refs,titles,covers,intros,companies,ratings,tags', nsfw: String(this.includeNsfw)
      }, scope))
      if (body.object !== 'list' || !Array.isArray(body.items) || (body.missing !== undefined &&
        (!Array.isArray(body.missing) || body.missing.some(ref => typeof ref !== 'string' || !requested.has(ref))))) {
        throw new NextMoeError('API 返回的来源编号识别结果格式不正确。', 'INVALID_RESPONSE')
      }
      const works = new Map<string, WorkMetadata>()
      for (const value of body.items) {
        const row = object(value)
        if (row.refs !== undefined && (!Array.isArray(row.refs) || row.refs.some(ref => {
          const entry = object(ref)
          return typeof entry.source !== 'string' || !entry.source || typeof entry.external_id !== 'string' || !entry.external_id
        }))) throw new NextMoeError('API 返回的作品来源编号格式不正确。', 'INVALID_RESPONSE')
        const work = mapWork(value)
        // Batch responses have no positional contract. Compare the verbatim upstream refs;
        // unrelated works and missing refs must never become automatic title matches.
        if (!rows(row.refs).some(ref => requested.has(`${ref.source}:${ref.external_id}`))) continue
        if (!works.has(work.id)) works.set(work.id, work)
      }
      // Different works claiming the same ref remain visible for the matcher to reject ambiguity.
      return [...works.values()]
    })
  }

  async searchWorks(query: string, cursor?: string): Promise<SearchPage> {
    const q = query.trim()
    if (!q || q.length > 512) throw new NextMoeError('请输入 1 至 512 个字符的作品名。', 'INVALID_QUERY')
    if (cursor !== undefined && (!cursor.startsWith('cur_') || cursor.length > 512)) {
      throw new NextMoeError('分页标记已失效，请重新搜索。', 'INVALID_CURSOR')
    }
    return this.withTimeout(async scope => {
      const body = object(await this.request('catalog/search', {
        object: 'work', q, limit: '20', nsfw: String(this.includeNsfw), ...(cursor ? { cursor } : {})
      }, scope))
      if (body.object !== 'list' || !Array.isArray(body.items) || body.items.length > 20) throw new NextMoeError('API 返回的搜索结果格式不正确。', 'INVALID_RESPONSE')
      const nextCursor = text(body.next_cursor)
      if (nextCursor && (!nextCursor.startsWith('cur_') || nextCursor.length > 512)) throw new NextMoeError('API 返回的分页标记格式不正确。', 'INVALID_RESPONSE')
      const ids = unique(body.items.map(value => {
        const hit = object(value)
        const id = text(hit.id)
        if (hit.object !== 'search_result' || hit.target_object !== 'work' || !DECIMAL_ID.test(id)) {
          throw new NextMoeError('API 返回的作品搜索命中格式不正确。', 'INVALID_RESPONSE')
        }
        return id
      }))
      if (!ids.length) return { items: [], nextCursor: nextCursor || null }

      // Search supplies relevance and pagination; the batch lane supplies card metadata.
      // Both requests share one timeout and retry budget. Never search again by title.
      const details = object(await this.request('catalog/works', {
        ids: ids.join(','), include: 'ratings,companies,refs,titles,covers', nsfw: String(this.includeNsfw)
      }, scope))
      if (details.object !== 'list' || !Array.isArray(details.items)) throw new NextMoeError('API 返回的作品列表格式不正确。', 'INVALID_RESPONSE')
      const requested = new Set(ids)
      const works = new Map<string, WorkMetadata>()
      for (const value of details.items) {
        if (!requested.has(text(object(value).id))) continue
        const work = mapWork(value)
        works.set(work.id, work)
      }
      // A work can disappear or become hidden between the two reads (batch missing[]).
      // Omit that hit while retaining the search cursor and all remaining hit positions.
      return { items: ids.flatMap(id => { const work = works.get(id); return work ? [work] : [] }), nextCursor: nextCursor || null }
    })
  }

  async getWork(id: string): Promise<WorkMetadata> {
    if (!DECIMAL_ID.test(id)) throw new NextMoeError('作品 ID 应为 1 至 20 位数字。', 'INVALID_ID')
    return this.withTimeout(async scope => {
      const visited = new Set<string>()
      let currentId = id
      for (let merges = 0; merges <= 3; merges++) {
        if (visited.has(currentId)) throw new NextMoeError('作品合并信息出现循环，请稍后重试。', 'MERGE_LOOP')
        visited.add(currentId)
        try {
          const body = await this.request(`catalog/works/${currentId}`, {
            nsfw: String(this.includeNsfw), include: 'titles,refs,links,intros,covers,companies,ratings,tags,characters'
          }, scope)
          return mapWork(body)
        } catch (error) {
          if (!(error instanceof MergedWork)) throw error
          currentId = error.currentId
        }
      }
      throw new NextMoeError('作品合并次数过多，请稍后重试。', 'MERGE_LIMIT')
    })
  }

  async getWorkReleases(id: string): Promise<ReleaseIdentity[]> {
    if (!DECIMAL_ID.test(id)) throw new NextMoeError('作品 ID 应为 1 至 20 位数字。', 'INVALID_ID')
    return this.withTimeout(async scope => {
      const visited = new Set<string>()
      let currentId = id
      for (let merges = 0; merges <= 3; merges++) {
        if (visited.has(currentId)) throw new NextMoeError('作品合并信息出现循环，请稍后重试。', 'MERGE_LOOP')
        visited.add(currentId)
        try {
          const releases = new Map<string, ReleaseIdentity>()
          const cursors = new Set<string>()
          let cursor: string | undefined
          // Bounded evidence lookup, not an exhaustive catalogue export. Every
          // page, retry and merge shares this operation's deadline and retry budget.
          for (let page = 0; page < 2; page++) {
            const body = object(await this.request(`catalog/works/${currentId}/releases`, {
              limit: String(RELEASE_PAGE_SIZE), nsfw: String(this.includeNsfw), ...(cursor ? { cursor } : {})
            }, scope))
            if (body.object !== 'list' || !Array.isArray(body.items) || body.items.length > RELEASE_PAGE_SIZE) {
              throw new NextMoeError('API 返回的发行列表格式不正确。', 'INVALID_RESPONSE')
            }
            const nextCursor = text(body.next_cursor)
            if (body.next_cursor !== undefined && (body.next_cursor !== nextCursor || nextCursor.length > 512 ||
              !/^cur_[A-Za-z0-9._~-]+$/.test(nextCursor) || cursors.has(nextCursor))) {
              throw new NextMoeError('API 返回的发行分页标记格式不正确。', 'INVALID_RESPONSE')
            }
            for (const value of body.items) {
              const release = mapRelease(value, currentId)
              if (!releases.has(release.id)) releases.set(release.id, release)
            }
            if (!nextCursor) break
            cursors.add(nextCursor)
            cursor = nextCursor
          }
          return [...releases.values()]
        } catch (error) {
          if (!(error instanceof MergedWork)) throw error
          // A canonical-work change invalidates both the old cursor and its rows.
          currentId = error.currentId
        }
      }
      throw new NextMoeError('作品合并次数过多，请稍后重试。', 'MERGE_LIMIT')
    })
  }

  async testConnection(): Promise<ConnectionResult> {
    try {
      await this.withTimeout(async scope => {
        const body = object(await this.request('catalog/works', { limit: '1', nsfw: String(this.includeNsfw) }, scope))
        if (body.object !== 'list' || !Array.isArray(body.items)) throw new NextMoeError('API 返回的数据格式不正确。', 'INVALID_RESPONSE')
      })
      return { ok: true, message: '连接成功，API Key 可以读取作品目录。' }
    } catch (error) {
      return { ok: false, message: error instanceof NextMoeError ? error.message : '连接失败，请检查网络与 API 设置。' }
    }
  }

  private async withTimeout<T>(action: (scope: RequestScope) => Promise<T>): Promise<T> {
    if (this.signal?.aborted) throw cancelledError()
    const controller = new AbortController()
    const error = new NextMoeError('API 请求超时，请稍后重试。', 'TIMEOUT')
    let rejectStopped!: (error: NextMoeError) => void
    const stopped = new Promise<never>((_, reject) => { rejectStopped = reject })
    const stop = (reason: NextMoeError) => {
      if (controller.signal.aborted) return
      controller.abort(reason)
      rejectStopped(reason)
    }
    const cancelled = () => stop(cancelledError())
    this.signal?.addEventListener('abort', cancelled, { once: true })
    const timer = setTimeout(() => stop(error), this.timeoutMs)
    try {
      return await Promise.race([stopped, action({ signal: controller.signal, deadline: Date.now() + this.timeoutMs, retries: 0 })])
    } catch (error) {
      if (controller.signal.aborted) throw abortError(controller.signal)
      throw error
    } finally {
      clearTimeout(timer)
      this.signal?.removeEventListener('abort', cancelled)
    }
  }

  private async request(path: string, parameters: Record<string, string>, scope: RequestScope): Promise<unknown> {
    const url = new URL(path, this.baseUrl)
    url.search = new URLSearchParams(parameters).toString()
    while (true) {
      if (scope.signal.aborted) throw abortError(scope.signal)
      if (Date.now() >= scope.deadline) throw new NextMoeError('API 请求超时，请稍后重试。', 'TIMEOUT')
      let response: Response
      try {
        response = await this.fetcher(url, {
          method: 'GET', headers: { Authorization: `Bearer ${this.apiKey}`, Accept: 'application/json' },
          signal: scope.signal, redirect: 'error', cache: 'no-store'
        })
      } catch {
        if (scope.signal.aborted) throw abortError(scope.signal)
        throw new NextMoeError('无法连接 API，请检查网络与接口地址。', 'NETWORK_ERROR')
      }
      const contentType = response.headers.get('content-type') ?? ''
      const isJson = /(?:application\/json|\+json)(?:\s*;|$)/i.test(contentType)
      if (response.status >= 300 && response.status < 400) {
        await response.body?.cancel().catch(() => {})
        throw httpError(response.status, 'REDIRECT_BLOCKED')
      }
      let body: unknown = null
      if (response.ok || isJson) {
        body = await this.readJson(response, scope.signal)
      } else {
        await response.body?.cancel().catch(() => {})
      }
      if (scope.signal.aborted) throw abortError(scope.signal)
      if (response.ok) return body
      const code = problemCode(body)
      if (response.status === 404 && code === 'ENTITY_MERGED') {
        const currentId = text(object(body).current_id)
        if (object(body).object !== 'work' || !DECIMAL_ID.test(currentId)) throw new NextMoeError('API 返回的作品合并信息格式不正确。', 'INVALID_RESPONSE')
        throw new MergedWork(currentId)
      }
      const retryable = [502, 503, 504].includes(response.status) || (response.status === 429 && code === 'RATE_LIMITED')
      if (!retryable || scope.retries >= 2) throw httpError(response.status, code)
      let milliseconds = 500 * 2 ** scope.retries
      const retryAfter = response.headers.get('retry-after')
      if (retryAfter) {
        const seconds = /^\d+(\.\d+)?$/.test(retryAfter) ? Number(retryAfter) : NaN
        const requestedDelay = Number.isFinite(seconds) ? seconds * 1000 : Date.parse(retryAfter) - Date.now()
        if (Number.isFinite(requestedDelay)) milliseconds = Math.max(milliseconds, requestedDelay)
      }
      if (milliseconds >= scope.deadline - Date.now()) throw httpError(response.status, code)
      scope.retries++
      await this.delay(milliseconds, scope.signal)
    }
  }

  private async readJson(response: Response, signal: AbortSignal): Promise<unknown> {
    if (!response.body) throw new NextMoeError('API 返回了空响应。', 'INVALID_RESPONSE')
    const reader = response.body.getReader()
    const aborted = () => { void reader.cancel().catch(() => {}) }
    signal.addEventListener('abort', aborted, { once: true })
    const decoder = new TextDecoder('utf-8', { fatal: true })
    let size = 0
    let data = ''
    try {
      while (true) {
        if (signal.aborted) throw abortError(signal)
        const { done, value } = await reader.read()
        if (signal.aborted) throw abortError(signal)
        if (done) break
        size += value.byteLength
        if (size > MAX_RESPONSE_BYTES) throw new NextMoeError('API 响应超出大小限制。', 'RESPONSE_TOO_LARGE')
        data += decoder.decode(value, { stream: true })
      }
      data += decoder.decode()
      return JSON.parse(data) as unknown
    } catch (error) {
      if (signal.aborted) throw abortError(signal)
      if (error instanceof NextMoeError) throw error
      throw new NextMoeError('API 返回了不完整或无效的 JSON 数据。', 'INVALID_JSON')
    } finally {
      signal.removeEventListener('abort', aborted)
      // Cancelling also releases an oversized or interrupted response without reading the rest.
      void reader.cancel().catch(() => {})
      reader.releaseLock()
    }
  }
}

class MergedWork extends Error {
  constructor(public readonly currentId: string) { super('ENTITY_MERGED') }
}
