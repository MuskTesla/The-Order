import type { LibraryGame, PlayStatus, ScanCandidate } from './shared/types'
import { getLaunchInstallation } from './library-insights'

export type LibraryView = 'all' | 'local' | 'wishlist' | 'unmatched' | 'unconfigured'
export type LibrarySort = 'added' | 'recent' | 'name' | 'rating' | 'playtime'
export type LibraryLayout = 'grid' | 'list'
export interface LibraryPreferences { layout: LibraryLayout; sort: LibrarySort; overview: boolean }
export interface LibraryFilters { query: string; view: LibraryView; status: PlayStatus | 'all'; favorites: boolean; category: string; tag: string; minRating: string }
const sorts = new Set<LibrarySort>(['added', 'recent', 'name', 'rating', 'playtime'])
const defaultPreferences = (): LibraryPreferences => ({ layout: 'grid', sort: 'added', overview: true })
const preferenceKey = 'the-order:library:v1'
type PreferenceStorage = Pick<Storage, 'getItem' | 'setItem'>

export function readLibraryPreferences(storage?: PreferenceStorage): LibraryPreferences {
  try {
    const raw = JSON.parse(storage?.getItem(preferenceKey) ?? 'null')
    if (!raw || typeof raw !== 'object' || Array.isArray(raw)) return defaultPreferences()
    return { layout: raw.layout === 'list' ? 'list' : 'grid', sort: sorts.has(raw.sort) ? raw.sort : 'added', overview: typeof raw.overview === 'boolean' ? raw.overview : true }
  } catch { return defaultPreferences() }
}
export function saveLibraryPreferences(storage: PreferenceStorage | undefined, preferences: LibraryPreferences): void {
  try { storage?.setItem(preferenceKey, JSON.stringify({ layout: preferences.layout === 'list' ? 'list' : 'grid', sort: sorts.has(preferences.sort) ? preferences.sort : 'added', overview: preferences.overview })) } catch { /* Private mode or full storage must not prevent local library use. */ }
}
const normalizeSearch = (value: string) => value.normalize('NFKC').toLocaleLowerCase().replace(/[^\p{L}\p{N}]/gu, '')
export function matchesLibraryView(game: LibraryGame, view: LibraryView): boolean {
  switch (view) {
    case 'local': return game.installations.length > 0
    case 'wishlist': return game.installations.length === 0
    case 'unmatched': return game.installations.length > 0 && (!game.metadata || !game.metadata.coverUrl)
    case 'unconfigured': return game.installations.length > 0 && !getLaunchInstallation(game)?.executablePath
    default: return true
  }
}
export function libraryViewCounts(games: LibraryGame[]): Record<LibraryView, number> {
  return Object.fromEntries(['all', 'local', 'wishlist', 'unmatched', 'unconfigured'].map(view => [view, games.filter(game => matchesLibraryView(game, view as LibraryView)).length])) as Record<LibraryView, number>
}
export function filterLibraryGames(games: LibraryGame[], filters: LibraryFilters, sort: LibrarySort): LibraryGame[] {
  const terms = filters.query.trim().split(/\s+/).map(normalizeSearch).filter(Boolean)
  const threshold = filters.minRating === '' ? null : Number(filters.minRating)
  const result = games.filter(game => {
    if (!matchesLibraryView(game, filters.view) || (filters.status !== 'all' && game.status !== filters.status) || (filters.favorites && !game.favorite) || (filters.category && !game.categories.includes(filters.category)) || (filters.tag && ![...game.customTags, ...(game.metadata?.tags ?? [])].includes(filters.tag))) return false
    if (threshold !== null && (!Number.isFinite(threshold) || game.personalRating === null || game.personalRating < threshold)) return false
    if (!terms.length) return true
    const fields = [game.name, game.metadata?.title ?? '', game.metadata?.originalTitle ?? '', ...(game.metadata?.aliases ?? []), ...(game.metadata?.developers ?? []), ...game.categories, ...game.customTags, ...(game.metadata?.tags ?? []), ...game.installations.map(installation => installation.folderPath)].map(normalizeSearch)
    return terms.every(term => fields.some(field => field.includes(term)))
  })
  const numeric = (value: number | null | undefined) => Number.isFinite(value) ? value! : -1
  return result.sort((a, b) => {
    let difference = 0
    if (sort === 'name') difference = a.name.localeCompare(b.name, 'zh-CN')
    else if (sort === 'rating') difference = numeric(b.personalRating) - numeric(a.personalRating)
    else if (sort === 'playtime') difference = numeric(b.totalPlaySeconds) - numeric(a.totalPlaySeconds)
    else if (sort === 'recent') difference = (b.lastPlayedAt ?? '').localeCompare(a.lastPlayedAt ?? '')
    else difference = (b.addedAt ?? '').localeCompare(a.addedAt ?? '')
    return difference || a.name.localeCompare(b.name, 'zh-CN') || a.id.localeCompare(b.id)
  })
}
export function folderKey(value: string): string { return value.replace(/\\/g, '/').replace(/\/+$/, '').toLocaleLowerCase() }
export function knownCandidateIds(candidates: ScanCandidate[], games: LibraryGame[]): Set<string> {
  const folders = new Set(games.flatMap(game => game.installations.filter(installation => typeof installation.folderPath === 'string').map(installation => folderKey(installation.folderPath))))
  return new Set(candidates.filter(candidate => folders.has(folderKey(candidate.folderPath))).map(candidate => candidate.id))
}
export function splitLabels(value: string): string[] { return [...new Set(value.split(/[,，\n]/).map(label => label.trim()).filter(Boolean))] }
