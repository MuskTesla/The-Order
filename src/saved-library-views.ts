import type { LibraryFilters, LibrarySort } from './library-workspace'

export interface SavedLibraryView { id: string; name: string; filters: LibraryFilters; sort: LibrarySort }
export interface SavedViewEditor { id: string | null; name: string }
export const savedViewsKey = 'the-order:saved-library-views:v1'
export const savedViewsLimit = 50
type ViewStorage = Pick<Storage, 'getItem' | 'setItem'>
const views = new Set(['all', 'local', 'wishlist', 'unmatched', 'unconfigured'])
const statuses = new Set(['all', 'planned', 'playing', 'completed', 'paused', 'dropped'])
const sorts = new Set(['added', 'recent', 'name', 'rating', 'playtime'])
const nameKey = (name: string) => name.normalize('NFKC').toLocaleLowerCase()
function object(value: unknown): Record<string, unknown> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw Error('常用筛选记录格式不正确。')
  return value as Record<string, unknown>
}
function text(value: unknown, maximum: number): string {
  if (typeof value !== 'string' || value.length > maximum || /[\u0000-\u001f\u007f]/.test(value)) throw Error('常用筛选的文字过长或格式不正确。')
  return value
}

/** Persist only the declared view fields, never a library snapshot or credentials. */
export function createSavedView(name: string, filters: LibraryFilters, sort: LibrarySort, id: string = crypto.randomUUID()): SavedLibraryView {
  const title = text(name, 80).trim()
  if (!title) throw Error('请为常用筛选填写名称。')
  if (!/^[\w-]{1,80}$/.test(id)) throw Error('常用筛选的编号不正确。')
  if (!views.has(filters.view) || !statuses.has(filters.status) || !sorts.has(sort) || typeof filters.favorites !== 'boolean') throw Error('常用筛选包含不支持的条件。')
  const minimum = text(filters.minRating, 10)
  if (minimum !== '' && (!minimum.trim() || !Number.isFinite(Number(minimum)) || Number(minimum) < 0 || Number(minimum) > 10)) throw Error('评分条件必须在0到10分之间。')
  return { id, name: title, sort, filters: {
    query: text(filters.query, 1000), view: filters.view, status: filters.status, favorites: filters.favorites,
    category: text(filters.category, 200), tag: text(filters.tag, 200), minRating: minimum,
  } }
}

export function validateSavedViews(value: unknown): SavedLibraryView[] {
  if (!Array.isArray(value) || value.length > savedViewsLimit) throw Error(`最多保存${savedViewsLimit}个常用筛选。`)
  const ids = new Set<string>(), names = new Set<string>()
  return value.map(input => {
    const raw = object(input), filters = object(raw.filters)
    const view = createSavedView(text(raw.name, 80), filters as unknown as LibraryFilters, raw.sort as LibrarySort, text(raw.id, 80))
    if (ids.has(view.id) || names.has(nameKey(view.name))) throw Error('已经有同名的常用筛选，请换一个名称。')
    ids.add(view.id); names.add(nameKey(view.name))
    return view
  })
}

export function readSavedViews(storage?: ViewStorage): { views: SavedLibraryView[]; error: string | null } {
  try {
    if (!storage) throw Error('Storage unavailable')
    const saved = storage.getItem(savedViewsKey)
    if (saved === null) return { views: [], error: null }
    if (saved.length > 200000) throw Error('Saved views too large')
    const payload = object(JSON.parse(saved))
    if (payload.version !== 1) throw Error('Unsupported version')
    return { views: validateSavedViews(payload.views), error: null }
  } catch {
    return { views: [], error: '常用筛选暂时无法读取。旧记录未被覆盖，书架仍可正常使用。' }
  }
}

export function writeSavedViews(storage: ViewStorage | undefined, input: SavedLibraryView[]): { ok: true; views: SavedLibraryView[] } | { ok: false; error: string } {
  let next: SavedLibraryView[]
  try { next = validateSavedViews(input) } catch (error) { return { ok: false, error: error instanceof Error ? error.message : '常用筛选格式不正确。' } }
  try {
    if (!storage) throw Error('Storage unavailable')
    storage.setItem(savedViewsKey, JSON.stringify({ version: 1, views: next }))
    return { ok: true, views: next }
  } catch { return { ok: false, error: '常用筛选未能保存，请重试。原来的筛选和书架没有改变。' } }
}

export function sameSavedFilters(a: LibraryFilters, b: LibraryFilters): boolean {
  return a.query === b.query && a.view === b.view && a.status === b.status && a.favorites === b.favorites && a.category === b.category && a.tag === b.tag && a.minRating === b.minRating
}
export function describeSavedView(view: Pick<SavedLibraryView, 'filters' | 'sort'>): string {
  const f = view.filters
  const conditions = [({ all: '全部作品', local: '本地游戏', wishlist: '愿望单', unmatched: '缺资料', unconfigured: '待设启动程序' })[f.view]]
  if (f.status !== 'all') conditions.push(({ planned: '未开始', playing: '在玩', completed: '已完成', paused: '搁置', dropped: '放弃' })[f.status])
  if (f.query) conditions.push(`搜索：${f.query}`)
  if (f.favorites) conditions.push('特别喜欢')
  if (f.category) conditions.push(`分类：${f.category}`)
  if (f.tag) conditions.push(`标签：${f.tag}`)
  if (f.minRating !== '') conditions.push(f.minRating === '0' ? '已评分' : `评分≥${f.minRating}`)
  conditions.push(({ added: '最近添加', recent: '最近游玩', name: '名称排序', rating: '我的评分', playtime: '游玩时长' })[view.sort])
  return conditions.join(' · ')
}
