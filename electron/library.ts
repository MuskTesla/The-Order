import { randomUUID } from 'node:crypto'
import { promises as fs } from 'node:fs'
import path from 'node:path'
import { hasExplicitGameVersion } from './title-matching'
import type { BulkGamePatch, GameInstallation, GamePatch, ImportResult, LibraryGame, PlaySession, ScanCandidate, WorkMetadata } from '../src/shared/types'

const FORMAT = 'kun-gal-manager'
const MAX_GAMES = 10000
const MAX_FILE_BYTES = 32 * 1024 * 1024
const STATUSES = new Set(['planned', 'playing', 'completed', 'paused', 'dropped'])
const EXCLUDED_EXECUTABLE = /^(?:unins\d*|uninstall(?:er)?|setup|install(?:er)?|vcredist.*|vc_redist.*|dxsetup|directx.*|redist.*|unitycrashhandler.*|crashreport.*|updater?|configure|configuration)(?:[._ -].*)?\.exe$/i
const SKIP_DIRECTORIES = new Set(['node_modules', '.git', '.svn', '.potatovn', '$recycle.bin', 'system volume information', 'windows', 'redist', '_commonredist', '汉化补丁', '补丁', 'patches', '_patch', 'backup', 'beifen'])

export interface MetadataRequestToken { epoch: string; gameId: string; generation: string; kind: 'refresh' | 'association' | 'automatic'; requestId: number; revision: number; automaticBatchId?: string }
type MetadataState = { generation: string; latestRefresh: number; pendingAssociation: number | null; pendingRefresh: number | null; revision: number; automaticBatchId?: string }
type Backup = { format: typeof FORMAT; version: 1; exportedAt: string; games: LibraryGame[] }
const clone = <T>(value: T): T => structuredClone(value)
// These fields select the local evidence used by recognition. Launch arguments
// and personal records do not change that identity and must not cancel a result.
const installationIdentity = (game: LibraryGame): string => JSON.stringify([
  game.preferredInstallationId,
  game.installations.map(({ id, folderPath, executablePath }) => [id, folderPath, executablePath])
])
const object = (value: unknown, label: string): Record<string, unknown> => {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error(`${label}格式不正确。`)
  return value as Record<string, unknown>
}
function text(value: unknown, label: string, maximum = 1000, empty = true): string {
  if (typeof value !== 'string' || value.length > maximum || value.includes('\0')) throw new Error(`${label}格式不正确。`)
  const result = value.trim()
  if (!empty && !result) throw new Error(`${label}不能为空。`)
  return result
}
function number(value: unknown, label: string, maximum = 1e12): number {
  if (typeof value !== 'number' || !Number.isFinite(value) || value < 0 || value > maximum) throw new Error(`${label}必须是有效的非负数字。`)
  return value
}
function list(value: unknown, label: string, maximum = 200): string[] {
  if (!Array.isArray(value) || value.length > maximum) throw new Error(`${label}格式不正确。`)
  return [...new Set(value.map(item => text(item, label, 200, false))) ]
}
function personalFields(raw: Record<string, unknown>): Pick<GamePatch, 'status' | 'personalRating' | 'favorite'> {
  const result: Pick<GamePatch, 'status' | 'personalRating' | 'favorite'> = {}
  if ('status' in raw) { if (!STATUSES.has(raw.status as string)) throw new Error('不支持的游玩状态。'); result.status = raw.status as LibraryGame['status'] }
  if ('personalRating' in raw) result.personalRating = raw.personalRating === null ? null : number(raw.personalRating, '个人评分', 10)
  if ('favorite' in raw) { if (typeof raw.favorite !== 'boolean') throw new Error('收藏设置格式不正确。'); result.favorite = raw.favorite }
  return result
}
function date(value: unknown, label: string): string {
  const result = text(value, label, 100, false)
  if (!Number.isFinite(Date.parse(result))) throw new Error(`${label}格式不正确。`)
  return new Date(result).toISOString()
}
function url(value: unknown, label: string): string | null {
  if (value === null || value === undefined || value === '') return null
  const result = text(value, label, 8192)
  let parsed: URL
  try { parsed = new URL(result) } catch { throw new Error(`${label}不是有效网址。`) }
  if (!['https:', 'http:'].includes(parsed.protocol) || parsed.username || parsed.password) throw new Error(`${label}只支持 HTTP/HTTPS 网址。`)
  return parsed.href
}
function metadata(value: unknown): WorkMetadata {
  const raw = object(value, '作品资料')
  // An aggregated work can legitimately contain many edition references (for
  // example STEINS;GATE has over 100). Retain them without an unbounded import.
  if (!Array.isArray(raw.ratings) || raw.ratings.length > 30 || !Array.isArray(raw.characters) || raw.characters.length > 500 || !Array.isArray(raw.references) || raw.references.length > 1000) throw new Error('作品资料列表格式不正确。')
  return {
    id: text(raw.id, '作品 ID', 300, false), title: text(raw.title, '标题', 2000, false),
    originalTitle: text(raw.originalTitle, '原名', 2000), aliases: list(raw.aliases, '别名'),
    ...(raw.titleEvidence === undefined ? {} : { titleEvidence: (() => {
      if (!Array.isArray(raw.titleEvidence) || raw.titleEvidence.length > 500) throw new Error('作品标题证据格式不正确。')
      return raw.titleEvidence.map(item => { const title = object(item, '标题证据'); if (typeof title.isMachine !== 'boolean') throw new Error('标题来源标记不正确。'); return { text: text(title.text, '作品标题', 500, false), kind: text(title.kind, '标题类型', 100, false), isMachine: title.isMachine, language: title.language === null ? null : text(title.language, '标题语言', 100) } })
    })() }),
    coverUrl: url(raw.coverUrl, '封面'), bannerUrl: url(raw.bannerUrl, '横幅'),
    description: text(raw.description, '简介', 200000), releaseDate: raw.releaseDate === null ? null : text(raw.releaseDate, '发行日期', 100),
    releaseDatePrecision: raw.releaseDatePrecision === null ? null : text(raw.releaseDatePrecision, '日期精度', 100),
    developers: list(raw.developers, '开发商'), tags: list(raw.tags, '标签', 1000),
    ratings: raw.ratings.map(item => { const r = object(item, '来源评分'); return { source: text(r.source, '评分来源', 100, false), score: number(r.score, '评分', 100000), scale: number(r.scale, '评分尺度', 100000), votes: r.votes === null ? null : number(r.votes, '评分人数') } }),
    characters: raw.characters.map(item => { const c = object(item, '角色'); return { id: text(c.id, '角色 ID', 300), name: text(c.name, '角色名', 2000), originalName: text(c.originalName, '角色原名', 2000), imageUrl: url(c.imageUrl, '角色图片'), role: text(c.role, '角色类型', 200), voiceActors: list(c.voiceActors, '声优') } }),
    references: raw.references.map(item => { const r = object(item, '关联来源'); const referenceUrl = url(r.url, '来源网址'); return { source: text(r.source, '来源', 100, false), id: text(r.id, '来源 ID', 300, false), ...(referenceUrl ? { url: referenceUrl } : {}) } }),
    webUrl: url(raw.webUrl, '作品网址'), contentRating: raw.contentRating === null ? null : text(raw.contentRating, '内容分级', 200), fetchedAt: date(raw.fetchedAt, '获取时间'),
  }
}
function validateArguments(value: unknown): string[] {
  if (!Array.isArray(value) || value.length > 100) throw new Error('启动参数必须是字符串数组，最多 100 项。')
  return value.map(item => {
    if (typeof item !== 'string' || item.includes('\0') || item.length > 8192) throw new Error('启动参数格式不正确。')
    return item
  })
}
function absolutePath(value: unknown, label: string): string {
  const result = text(value, label, 32768, false)
  if (!path.isAbsolute(result)) throw new Error(`${label}必须为绝对路径。`)
  return path.normalize(result)
}
function pathKey(value: string): string { const normalized = path.resolve(value); return process.platform === 'win32' ? normalized.toLowerCase() : normalized }
function within(root: string, target: string): boolean {
  const relative = path.relative(pathKey(root), pathKey(target))
  return relative === '' || (!relative.startsWith(`..${path.sep}`) && relative !== '..' && !path.isAbsolute(relative))
}
function installation(value: unknown): GameInstallation {
  const raw = object(value, '安装记录')
  const folderPath = absolutePath(raw.folderPath, '游戏目录')
  const executablePath = raw.executablePath === null ? null : absolutePath(raw.executablePath, '启动文件')
  if (executablePath && (!within(folderPath, executablePath) || path.extname(executablePath).toLowerCase() !== '.exe')) throw new Error('启动文件必须是游戏目录内的 exe。')
  return { id: text(raw.id, '安装 ID', 300, false), folderPath, executablePath, arguments: validateArguments(raw.arguments) }
}
function session(value: unknown): PlaySession {
  const raw = object(value, '游玩记录')
  const startedAt = date(raw.startedAt, '开始时间'), endedAt = date(raw.endedAt, '结束时间')
  if (Date.parse(endedAt) < Date.parse(startedAt)) throw new Error('结束时间不能早于开始时间。')
  return { id: text(raw.id, '游玩记录 ID', 300, false), startedAt, endedAt, seconds: Math.floor(number(raw.seconds, '游玩时长', 366 * 86400)) }
}
function game(value: unknown): LibraryGame {
  const raw = object(value, '游戏')
  if (!Array.isArray(raw.installations) || raw.installations.length > 100 || !Array.isArray(raw.sessions) || raw.sessions.length > 100000) throw new Error('游戏记录过大或格式不正确。')
  const installations = raw.installations.map(installation), sessions = raw.sessions.map(session)
  if (new Set(installations.map(i => i.id)).size !== installations.length || new Set(sessions.map(s => s.id)).size !== sessions.length) throw new Error('游戏记录含重复 ID。')
  const preferred = raw.preferredInstallationId === null ? null : text(raw.preferredInstallationId, '首选安装 ID', 300, false)
  if (preferred && !installations.some(item => item.id === preferred)) throw new Error('首选安装不存在。')
  if (!STATUSES.has(raw.status as string) || typeof raw.favorite !== 'boolean') throw new Error('游戏状态格式不正确。')
  if (raw.nameIsCustom !== undefined && typeof raw.nameIsCustom !== 'boolean') throw new Error('游戏名称来源标记不正确。')
  const result: LibraryGame = {
    ...(raw.nameIsCustom === undefined ? {} : { nameIsCustom: raw.nameIsCustom as boolean }),
    id: text(raw.id, '游戏 ID', 300, false), name: text(raw.name, '游戏名称', 2000, false), metadata: raw.metadata === null ? null : metadata(raw.metadata),
    installations, preferredInstallationId: preferred, status: raw.status as LibraryGame['status'], personalRating: raw.personalRating === null ? null : number(raw.personalRating, '个人评分', 10),
    favorite: raw.favorite, categories: list(raw.categories, '分类'), customTags: list(raw.customTags, '自定义标签'), notes: text(raw.notes, '笔记', 200000),
    totalPlaySeconds: Math.floor(number(raw.totalPlaySeconds, '总时长')), playCount: Math.floor(number(raw.playCount, '游玩次数', 1e8)), sessions,
    addedAt: date(raw.addedAt, '加入时间'), lastPlayedAt: raw.lastPlayedAt === null ? null : date(raw.lastPlayedAt, '上次游玩时间'),
  }
  if (result.totalPlaySeconds < sessions.reduce((sum, current) => sum + current.seconds, 0)) throw new Error('总时长小于已保存的游玩记录。')
  return result
}
function parseBackup(value: unknown): LibraryGame[] {
  const raw = object(value, '备份')
  if (raw.format !== FORMAT || raw.version !== 1 || !Array.isArray(raw.games) || raw.games.length > MAX_GAMES) throw new Error('不支持的备份格式或版本。请使用 The Order 或旧版鲸屿导出的 JSON。')
  const games = raw.games.map(game), ids = new Set<string>(), paths = new Set<string>()
  for (const item of games) {
    if (ids.has(item.id)) throw new Error('备份含重复游戏 ID。')
    ids.add(item.id)
    for (const location of item.installations) {
      const key = pathKey(location.folderPath)
      if (paths.has(key)) throw new Error('备份含重复游戏目录。')
      paths.add(key)
    }
  }
  return games
}
function makeGame(name: string, work: WorkMetadata | null = null): LibraryGame {
  return { id: randomUUID(), name, nameIsCustom: false, metadata: work, installations: [], preferredInstallationId: null, status: 'planned', personalRating: null, favorite: false, categories: [], customTags: [], notes: '', totalPlaySeconds: 0, playCount: 0, sessions: [], addedAt: new Date().toISOString(), lastPlayedAt: null }
}
function envelope(games: LibraryGame[]): Backup { return { format: FORMAT, version: 1, exportedAt: new Date().toISOString(), games: clone(games) } }

export class LibraryStore {
  private games: LibraryGame[] = []
  private readonly metadataEpoch = randomUUID()
  private readonly metadataStates = new Map<string, MetadataState>()
  private metadataRequestId = 0
  private metadataRevision = 0
  private queue: Promise<unknown> = Promise.resolve()
  private lastGood = ''
  private initialized = false
  readonly filePath: string
  readonly backupPath: string
  recoveryNotice: string | null = null

  constructor(readonly dataDir: string) { this.filePath = path.join(dataDir, 'library.json'); this.backupPath = path.join(dataDir, 'library.json.bak') }

  async initialize(): Promise<void> {
    if (this.initialized) return
    await fs.mkdir(this.dataDir, { recursive: true })
    const read = async (file: string): Promise<{ exists: boolean; games?: LibraryGame[]; error?: unknown }> => {
      try {
        const stat = await fs.stat(file)
        if (stat.size > MAX_FILE_BYTES) throw new Error('游戏库文件超过 32 MB。')
        return { exists: true, games: parseBackup(JSON.parse(await fs.readFile(file, 'utf8'))) }
      } catch (error) { return (error as NodeJS.ErrnoException).code === 'ENOENT' ? { exists: false } : { exists: true, error } }
    }
    const primary = await read(this.filePath)
    if (primary.games) this.games = primary.games
    else {
      const backup = await read(this.backupPath)
      if (backup.games) {
        this.games = backup.games
        this.recoveryNotice = '游戏库主文件无法读取，已从上一次有效备份恢复。'
        if (primary.exists) await fs.copyFile(this.filePath, path.join(this.dataDir, `library.corrupt-${Date.now()}.json`))
        await this.atomicWrite(this.filePath, JSON.stringify(envelope(this.games), null, 2))
      } else if (primary.exists || backup.exists) throw new Error('游戏库及备份无法读取。原文件已保留，请从导出备份恢复。')
      else await this.atomicWrite(this.filePath, JSON.stringify(envelope([]), null, 2))
    }
    this.lastGood = JSON.stringify(envelope(this.games), null, 2)
    this.initialized = true
  }

  getGames(): LibraryGame[] { this.ready(); return clone(this.games) }
  getGame(id: string): LibraryGame { this.ready(); return clone(this.find(this.games, id)) }
  exportBackup(): Backup { this.ready(); return envelope(this.games) }

  private metadataState(id: string): MetadataState {
    let state = this.metadataStates.get(id)
    if (!state) { state = { generation: randomUUID(), latestRefresh: 0, pendingAssociation: null, pendingRefresh: null, revision: 0 }; this.metadataStates.set(id, state) }
    return state
  }
  captureMetadataToken(gameId: string, automaticBatchId?: string): MetadataRequestToken {
    this.ready(); this.find(this.games, gameId)
    const state = this.metadataState(gameId)
    return { epoch: this.metadataEpoch, gameId, generation: state.generation, kind: 'automatic', requestId: state.latestRefresh, revision: this.metadataRevision, ...(automaticBatchId ? { automaticBatchId } : {}) }
  }
  async beginMetadataRequest(gameId: string, kind: 'refresh' | 'association'): Promise<{ game: LibraryGame; token: MetadataRequestToken }> {
    return this.enqueue(() => {
      const game = this.find(this.games, gameId), state = this.metadataState(gameId)
      if (kind === 'refresh' && state.pendingAssociation !== null) throw new Error('资料关联正在更新，请稍后刷新。')
      const requestId = ++this.metadataRequestId
      if (kind === 'association') { state.generation = randomUUID(); state.pendingAssociation = requestId; state.pendingRefresh = null }
      else { state.latestRefresh = requestId; state.pendingRefresh = requestId }
      // Beginning a request records the user's latest intent even if it later
      // fails. An older request on another row must not merge this row away.
      state.revision = ++this.metadataRevision
      delete state.automaticBatchId
      return { game: clone(game), token: { epoch: this.metadataEpoch, gameId, generation: state.generation, kind, requestId, revision: this.metadataRevision } }
    })
  }
  async finishMetadataRequest(token: MetadataRequestToken): Promise<void> {
    await this.enqueue(() => {
      const state = this.metadataStates.get(token.gameId)
      if (token.epoch === this.metadataEpoch && token.kind === 'association' && state?.pendingAssociation === token.requestId) state.pendingAssociation = null
      if (token.epoch === this.metadataEpoch && token.kind === 'refresh' && state?.pendingRefresh === token.requestId) state.pendingRefresh = null
    })
  }
  private assertMetadataToken(gameId: string, token: MetadataRequestToken): void {
    const state = this.metadataStates.get(gameId)
    const same = token.epoch === this.metadataEpoch && token.gameId === gameId && state?.generation === token.generation
    const current = state && (token.kind === 'association'
      ? state.pendingAssociation === token.requestId
      : state.pendingAssociation === null && state.latestRefresh === token.requestId &&
        (token.kind === 'refresh' ? state.pendingRefresh === token.requestId : state.pendingRefresh === null))
    if (!same || !current) throw new Error('游戏名称或资料已更新，请重新识别。')
  }
  private assertMergeTargetCurrent(gameId: string, token?: MetadataRequestToken): void {
    const state = this.metadataState(gameId)
    if (state.pendingAssociation !== null || state.pendingRefresh !== null) throw new Error('待合并条目的资料正在更新，请完成后重新关联。')
    // One automatic batch may recognize several installations of the same
    // work. Only its own most recent commit can be merged by a sibling result;
    // explicit requests, renames and a restored lifecycle clear this provenance.
    const sameAutomaticBatch = token?.kind === 'automatic' && !!token.automaticBatchId && token.automaticBatchId === state.automaticBatchId
    if (token && state.revision > token.revision && !sameAutomaticBatch) throw new Error('待合并条目的名称或资料已更新，请重新关联。')
  }
  private protectsName(game: LibraryGame): boolean {
    // Only newly created default names have proven non-personal provenance.
    return game.nameIsCustom !== false || hasExplicitGameVersion(game.name) || !!game.metadata && game.name !== game.metadata.title
  }

  async addVirtual(value: WorkMetadata): Promise<LibraryGame> {
    const work = metadata(value)
    return this.mutate(games => {
      const existing = games.find(item => item.metadata?.id === work.id)
      if (existing) return existing
      const item = makeGame(work.title, work); games.push(item); return item
    })
  }

  async importCandidates(candidates: ScanCandidate[]): Promise<ImportResult> {
    if (!Array.isArray(candidates) || candidates.length > 2000) throw new Error('导入候选过多。')
    return this.mutate(async games => {
      const known = new Set<string>()
      for (const item of games) for (const location of item.installations) known.add(await actualPathKey(location.folderPath))
      let added = 0, skipped = 0
      for (const candidate of candidates) {
        const folderPath = await fs.realpath(absolutePath(candidate.folderPath, '游戏目录'))
        if (!(await fs.stat(folderPath)).isDirectory()) throw new Error('游戏目录不存在。')
        const key = pathKey(folderPath)
        if (known.has(key)) { skipped++; continue }
        if (!Array.isArray(candidate.executables) || candidate.executables.length > 200) throw new Error('启动文件列表格式不正确。')
        const executables: string[] = []
        for (const file of candidate.executables) {
          const real = await fs.realpath(absolutePath(file, '启动文件'))
          if (!within(folderPath, real) || pathKey(path.dirname(real)) !== key || path.extname(real).toLowerCase() !== '.exe' || !(await fs.stat(real)).isFile()) throw new Error('候选启动文件必须位于游戏目录内。')
          if (!EXCLUDED_EXECUTABLE.test(path.basename(real))) executables.push(real)
        }
        if (!executables.length) { skipped++; continue }
        const item = makeGame(text(candidate.name, '游戏名称', 2000, false))
        const location: GameInstallation = { id: randomUUID(), folderPath, executablePath: executables.length === 1 ? executables[0]! : null, arguments: [] }
        item.installations.push(location); item.preferredInstallationId = location.id
        games.push(item); known.add(key); added++
      }
      return { added, skipped }
    })
  }

  async linkMetadata(gameId: string, value: WorkMetadata, options: { preserveName?: boolean; preserveMergedName?: boolean; expectedName?: string; expectedMetadataId?: string | null; requestToken?: MetadataRequestToken; signal?: AbortSignal } = {}): Promise<LibraryGame> {
    const work = metadata(value)
    const assertNotCancelled = () => options.signal?.throwIfAborted()
    return this.mutate(games => {
      assertNotCancelled()
      if (options.requestToken) this.assertMetadataToken(gameId, options.requestToken)
      const item = this.find(games, gameId)
      if (options.expectedName !== undefined && item.name !== options.expectedName || 'expectedMetadataId' in options && (item.metadata?.id ?? null) !== options.expectedMetadataId) throw new Error('游戏名称或资料已更新，请重新识别。')
      const preserveName = options.preserveName || this.protectsName(item)
      // An explicit source-ID match can join installations; names never imply identity.
      const existing = games.find(other => other.id !== gameId && other.metadata?.id === work.id)
      const mergedName = options.preserveMergedName && !preserveName && existing?.metadata && this.protectsName(existing) ? existing.name : undefined
      if (existing) {
        this.assertMergeTargetCurrent(existing.id, options.requestToken)
        item.installations.push(...existing.installations)
        item.preferredInstallationId ??= existing.preferredInstallationId
        item.categories = [...new Set([...item.categories, ...existing.categories])]
        item.customTags = [...new Set([...item.customTags, ...existing.customTags])]
        item.favorite ||= existing.favorite
        item.personalRating ??= existing.personalRating
        if (item.status === 'planned') item.status = existing.status
        if (existing.notes && existing.notes !== item.notes) item.notes = [item.notes, existing.notes].filter(Boolean).join('\n\n')
        const ids = new Map(item.sessions.map(s => [s.id, s]))
        let duplicateSeconds = 0, duplicateCount = 0
        for (const play of existing.sessions) {
          const previous = ids.get(play.id)
          if (previous && previous.startedAt !== play.startedAt) throw new Error('游玩记录编号冲突，未合并，请保留备份后核对。')
          if (previous) { duplicateSeconds += Math.min(previous.seconds, play.seconds); if (previous.seconds > 0 && play.seconds > 0) duplicateCount++; if (play.seconds > previous.seconds) Object.assign(previous, play) }
          else { item.sessions.push(play); ids.set(play.id, play) }
        }
        item.totalPlaySeconds += existing.totalPlaySeconds - duplicateSeconds
        item.playCount += existing.playCount - duplicateCount
        if (existing.lastPlayedAt && (!item.lastPlayedAt || existing.lastPlayedAt > item.lastPlayedAt)) item.lastPlayedAt = existing.lastPlayedAt
        games.splice(games.indexOf(existing), 1)
      }
      item.metadata = work
      // User naming choices survive subsequent refreshes.
      if (mergedName !== undefined) { item.name = mergedName; item.nameIsCustom = true }
      else if (preserveName) item.nameIsCustom = true
      else { item.name = work.title; item.nameIsCustom = false }
      return item
    }, [gameId], assertNotCancelled, options.requestToken?.kind === 'automatic' ? options.requestToken.automaticBatchId : undefined)
  }

  async updateGame(gameId: string, patch: GamePatch): Promise<LibraryGame> {
    const raw = object(patch, '修改内容'), allowed = new Set(['name', 'status', 'personalRating', 'favorite', 'categories', 'customTags', 'notes', 'preferredInstallationId'])
    if (Object.keys(raw).some(key => !allowed.has(key))) throw new Error('修改包含不支持的字段。')
    return this.mutate(games => {
      const item = this.find(games, gameId)
      if ('name' in raw) { const name = text(raw.name, '游戏名称', 2000, false); item.nameIsCustom = true; item.name = name }
      Object.assign(item, personalFields(raw))
      if ('categories' in raw) item.categories = list(raw.categories, '分类')
      if ('customTags' in raw) item.customTags = list(raw.customTags, '标签')
      if ('notes' in raw) item.notes = text(raw.notes, '笔记', 200000)
      if ('preferredInstallationId' in raw) {
        if (raw.preferredInstallationId !== null && !item.installations.some(location => location.id === raw.preferredInstallationId)) throw new Error('首选安装不存在。')
        item.preferredInstallationId = raw.preferredInstallationId as string | null
      }
      return item
    }, 'name' in raw ? [gameId] : [])
  }

  async updateGames(gameIds: string[], patch: BulkGamePatch): Promise<LibraryGame[]> {
    if (!Array.isArray(gameIds) || gameIds.length === 0 || gameIds.length > 1000) throw new Error('批量修改请选择 1 至 1000 个游戏。')
    const ids = gameIds.map(id => text(id, '游戏 ID', 300, false))
    if (new Set(ids).size !== ids.length) throw new Error('批量修改含重复游戏 ID。')
    const raw = object(patch, '批量修改内容')
    const allowed = new Set(['status', 'personalRating', 'favorite', 'addCategories', 'removeCategories', 'addTags', 'removeTags'])
    if (Object.keys(raw).some(key => !allowed.has(key))) throw new Error('批量修改包含不支持的字段。')
    // Validate and copy input before waiting for earlier saves. The relative
    // changes themselves are applied to the current values inside the queue.
    const fields = personalFields(raw)
    const changes = (addKey: string, removeKey: string, label: string) => {
      const add = addKey in raw ? list(raw[addKey], label) : []
      const remove = removeKey in raw ? list(raw[removeKey], label) : []
      const removeSet = new Set(remove)
      if (add.some(value => removeSet.has(value))) throw new Error(`同一${label}不能同时添加和移除。`)
      return { add, remove: removeSet }
    }
    const categories = changes('addCategories', 'removeCategories', '分类')
    const tags = changes('addTags', 'removeTags', '标签')
    if (!Object.keys(fields).length && !categories.add.length && !categories.remove.size && !tags.add.length && !tags.remove.size) throw new Error('请选择至少一项批量修改。')
    const changedList = (current: string[], change: ReturnType<typeof changes>, label: string) =>
      list([...new Set([...current.filter(value => !change.remove.has(value)), ...change.add])], label)
    return this.mutate(games => {
      const byId = new Map(games.map(item => [item.id, item]))
      const selected = ids.map(id => { const item = byId.get(id); if (!item) throw new Error('游戏不存在，未保存批量修改。'); return item })
      for (const item of selected) {
        Object.assign(item, fields)
        item.categories = changedList(item.categories, categories, '分类')
        item.customTags = changedList(item.customTags, tags, '标签')
      }
      return selected
    })
  }

  async setExecutable(gameId: string, installationId: string | undefined, filePath: string): Promise<LibraryGame> {
    return this.mutate(async games => {
      const item = this.find(games, gameId)
      const executable = await fs.realpath(absolutePath(filePath, '启动文件'))
      if (path.extname(executable).toLowerCase() !== '.exe' || !(await fs.stat(executable)).isFile()) throw new Error('请选择 exe 启动文件。')
      if (item.installations.length === 0 && installationId === undefined) {
        const folderPath = path.dirname(executable), key = pathKey(folderPath)
        for (const other of games) for (const existing of other.installations) {
          if (await actualPathKey(existing.folderPath) === key) throw new Error('此游戏目录已在库中，请在已有条目中关联作品。')
        }
        const location: GameInstallation = { id: randomUUID(), folderPath, executablePath: executable, arguments: [] }
        item.installations.push(location); item.preferredInstallationId = location.id
        return item
      }
      const location = this.findInstallation(item, installationId)
      const root = await fs.realpath(location.folderPath)
      if (!within(root, executable) || path.extname(executable).toLowerCase() !== '.exe' || !(await fs.stat(executable)).isFile()) throw new Error('请选择游戏目录内的 exe 启动文件。')
      location.executablePath = executable; item.preferredInstallationId = location.id
      return item
    })
  }

  async setLaunchArguments(gameId: string, installationId: string, args: string[]): Promise<LibraryGame> {
    const validated = validateArguments(args)
    return this.mutate(games => { const item = this.find(games, gameId); this.findInstallation(item, installationId).arguments = validated; return item })
  }

  async removeGame(gameId: string): Promise<void> {
    await this.mutate(games => { const item = this.find(games, gameId); games.splice(games.indexOf(item), 1) })
  }

  async importBackup(value: unknown): Promise<ImportResult> {
    const incoming = parseBackup(value)
    return this.mutate(async games => {
      let added = 0, skipped = 0
      const ids = new Set(games.map(item => item.id)), workIds = new Set(games.flatMap(item => item.metadata ? [item.metadata.id] : [])), known = new Set<string>()
      for (const item of games) for (const location of item.installations) known.add(await actualPathKey(location.folderPath))
      for (const item of incoming) {
        const keys = await Promise.all(item.installations.map(location => actualPathKey(location.folderPath)))
        if (ids.has(item.id) || (item.metadata && workIds.has(item.metadata.id)) || keys.some(key => known.has(key))) { skipped++; continue }
        games.push(item); ids.add(item.id); if (item.metadata) workIds.add(item.metadata.id); keys.forEach(key => known.add(key)); added++
      }
      return { added, skipped }
    })
  }

  async recordSession(gameId: string, value: PlaySession): Promise<void> {
    const incoming = session(value)
    await this.mutate(games => {
      const item = this.find(games, gameId), previous = item.sessions.find(play => play.id === incoming.id)
      if (previous && previous.startedAt !== incoming.startedAt) throw new Error('游玩记录 ID 冲突。')
      if (incoming.seconds === 0 || (previous && previous.seconds >= incoming.seconds)) return
      item.totalPlaySeconds += incoming.seconds - (previous?.seconds ?? 0)
      if (previous) Object.assign(previous, incoming)
      else { item.sessions.push(incoming); item.playCount++ }
      if (!item.lastPlayedAt || item.lastPlayedAt < incoming.startedAt) item.lastPlayedAt = incoming.startedAt
    })
  }

  private ready(): void { if (!this.initialized) throw new Error('游戏库尚未初始化。') }
  private find(games: LibraryGame[], id: string): LibraryGame { const result = games.find(item => item.id === id); if (!result) throw new Error('游戏不存在。'); return result }
  private findInstallation(item: LibraryGame, id?: string): GameInstallation { const result = item.installations.find(location => location.id === (id ?? item.preferredInstallationId)) ?? (!id && item.installations.length === 1 ? item.installations[0] : undefined); if (!result) throw new Error('请先选择本地安装目录。'); return result }
  private enqueue<T>(operation: () => T | Promise<T>): Promise<T> {
    this.ready()
    const pending = this.queue.then(operation)
    this.queue = pending.catch(() => undefined)
    return pending
  }
  private mutate<T>(operation: (games: LibraryGame[]) => T | Promise<T>, metadataChanges: string[] = [], beforeCommit?: () => void, automaticBatchId?: string): Promise<T> {
    return this.enqueue(async () => {
      const draft = clone(this.games), result = await operation(draft)
      if (draft.length > MAX_GAMES) throw new Error('游戏库超过 10000 条。')
      const validated = parseBackup(envelope(draft))
      const serialized = JSON.stringify(envelope(validated), null, 2)
      if (Buffer.byteLength(serialized) > MAX_FILE_BYTES) throw new Error('游戏库超过 32 MB，未保存修改。')
      // Cancellation is honored until persistence starts. Once it starts, finish
      // the atomic transaction so memory and the saved document stay consistent.
      beforeCommit?.()
      await this.atomicWrite(this.backupPath, this.lastGood)
      await this.atomicWrite(this.filePath, serialized)
      const previous = new Map(this.games.map(game => [game.id, game]))
      const currentIds = new Set(validated.map(game => game.id))
      for (const id of this.metadataStates.keys()) if (!currentIds.has(id)) this.metadataStates.delete(id)
      for (const game of validated) {
        const old = previous.get(game.id)
        if (!old || metadataChanges.includes(game.id) || old.name !== game.name || old.nameIsCustom !== game.nameIsCustom || JSON.stringify(old.metadata) !== JSON.stringify(game.metadata) || installationIdentity(old) !== installationIdentity(game)) {
          const state = this.metadataState(game.id)
          state.generation = randomUUID(); state.revision = ++this.metadataRevision
          state.automaticBatchId = automaticBatchId
        }
      }
      this.games = validated; this.lastGood = serialized
      return clone(result)
    })
  }
  private async atomicWrite(target: string, contents: string): Promise<void> {
    const temporary = `${target}.${randomUUID()}.tmp`
    try {
      const handle = await fs.open(temporary, 'wx', 0o600)
      try { await handle.writeFile(contents, 'utf8'); await handle.sync() } finally { await handle.close() }
      await fs.rename(temporary, target)
    } finally { await fs.rm(temporary, { force: true }).catch(() => undefined) }
  }
}

async function actualPathKey(folder: string): Promise<string> { return pathKey(await fs.realpath(folder).catch(() => folder)) }

export async function scanFolder(folderPath: string): Promise<ScanCandidate[]> {
  const root = await fs.realpath(absolutePath(folderPath, '扫描目录'))
  if (/(?:^|[\\/])(?:汉化补丁|补丁|patches|_patch|backup|beifen)(?:[\\/]|$)/i.test(root)) throw new Error('这是补丁或备份目录，请选择实际游戏根目录或游戏库。')
  if (!(await fs.stat(root)).isDirectory()) throw new Error('请选择文件夹。')
  const forbidden = [path.parse(root).root, process.env.WINDIR, process.env.ProgramFiles, process.env['ProgramFiles(x86)'], process.env.ProgramData].filter((item): item is string => !!item)
  if (forbidden.some(item => pathKey(item) === pathKey(root))) throw new Error('请选择存放游戏的具体文件夹，不能扫描磁盘根目录或系统程序目录。')
  const candidates: ScanCandidate[] = [], queue = [{ directory: root, depth: 0 }], visited = new Set<string>()
  let inspected = 0, directories = 0
  while (queue.length) {
    const current = queue.shift()!
    if (++directories > 5000 || inspected > 50000 || candidates.length >= 2000) throw new Error('扫描范围过大，请选择更具体的游戏文件夹。')
    const key = pathKey(current.directory)
    if (visited.has(key)) continue
    visited.add(key)
    let entries
    try { entries = await fs.readdir(current.directory, { withFileTypes: true }) } catch { continue }
    inspected += entries.length
    if (inspected > 50000) throw new Error('扫描范围过大，请选择更具体的游戏文件夹。')
    const executables = entries.filter(entry => entry.isFile() && !entry.isSymbolicLink() && path.extname(entry.name).toLowerCase() === '.exe' && !EXCLUDED_EXECUTABLE.test(entry.name)).map(entry => path.join(current.directory, entry.name))
    if (executables.length) {
      const name = path.basename(current.directory), compact = (s: string) => s.toLowerCase().replace(/[^\p{L}\p{N}]/gu, '')
      executables.sort((left, right) => Number(compact(path.parse(right).name) === compact(name)) - Number(compact(path.parse(left).name) === compact(name)) || left.localeCompare(right))
      candidates.push({ id: randomUUID(), name, folderPath: current.directory, executables }); continue
    }
    if (current.depth >= 6) continue
    for (const entry of entries) if (entry.isDirectory() && !entry.isSymbolicLink() && !SKIP_DIRECTORIES.has(entry.name.toLowerCase())) queue.push({ directory: path.join(current.directory, entry.name), depth: current.depth + 1 })
  }
  return candidates
}
