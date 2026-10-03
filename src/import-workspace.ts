import type { LibraryGame, ScanCandidate } from './shared/types'
import { folderKey } from './library-workspace'

export type ScanStatus = 'new' | 'known' | 'conflict'
export type ScanView = 'all' | ScanStatus
export interface ScanRow { candidate: ScanCandidate; status: ScanStatus; owner?: LibraryGame }
export type ScanSelectionOperation = 'add' | 'remove' | 'only'

export function buildScanRows(candidates: readonly ScanCandidate[], games: readonly LibraryGame[]): ScanRow[] {
  if (!candidates.length) return []
  const installations = games.flatMap(owner => owner.installations.map(installation => ({ owner, key: folderKey(installation.folderPath) }))).filter(item => item.key)
  const exact = new Map<string, LibraryGame>()
  for (const item of installations) if (!exact.has(item.key)) exact.set(item.key, item.owner)
  return candidates.map(candidate => {
    const key = folderKey(candidate.folderPath), known = exact.get(key)
    if (known) return { candidate, status: 'known', owner: known }
    const overlap = key && installations.find(item => key.startsWith(item.key + '/') || item.key.startsWith(key + '/'))
    return overlap ? { candidate, status: 'conflict', owner: overlap.owner } : { candidate, status: 'new' }
  })
}

const searchText = (value: string) => value.normalize('NFKC').toLocaleLowerCase().replace(/\\/g, '/').replace(/\s+/g, ' ')
export function filterScanRows(rows: readonly ScanRow[], query: string, view: ScanView): ScanRow[] {
  const phrase = searchText(query).trim()
  return rows.filter(row => {
    if (view !== 'all' && row.status !== view) return false
    const fields = [row.candidate.name, row.candidate.folderPath, row.owner?.name ?? ''].map(searchText)
    return !phrase || fields.some(field => field.includes(phrase))
  })
}

export function validScanSelection(ids: readonly string[], rows: readonly ScanRow[]): string[] {
  const allowed = new Set(rows.filter(row => row.status === 'new').map(row => row.candidate.id))
  return [...new Set(ids)].filter(id => allowed.has(id))
}

// The caller validates the existing selection against the full scan before
// applying a visible-scope operation; hidden valid choices remain untouched.
export function updateScanSelection(ids: readonly string[], visibleRows: readonly ScanRow[], operation: ScanSelectionOperation): string[] {
  const visible = [...new Set(visibleRows.filter(row => row.status === 'new').map(row => row.candidate.id))]
  if (operation === 'only') return visible
  if (operation === 'add') return [...new Set([...ids, ...visible])]
  const removed = new Set(visible)
  return [...new Set(ids)].filter(id => !removed.has(id))
}

interface ParsedDirectory { parts: string[]; minimum: number; separator: string }
function parseDirectory(value: string): ParsedDirectory | null {
  const normalized = value.replace(/\\/g, '/').replace(/\/+$/, '')
  const separator = value.includes('\\') ? '\\' : '/'
  if (normalized.split('/').some(part => part === '.' || part === '..')) return null
  if (/^[a-z]:\//i.test(normalized)) return { parts: [normalized.slice(0, 2), ...normalized.slice(3).split('/').filter(Boolean)], minimum: 2, separator }
  if (normalized.startsWith('//')) {
    const segments = normalized.slice(2).split('/').filter(Boolean)
    if (segments.length < 2 || ['?', '.'].includes(segments[0]!)) return null
    return { parts: ['', '', ...segments], minimum: 4, separator }
  }
  return null
}

export function commonScanDirectory(candidates: readonly ScanCandidate[]): string | null {
  if (candidates.length < 2) return null
  const paths = candidates.map(candidate => parseDirectory(candidate.folderPath))
  const first = paths[0]
  if (!first || paths.some(value => !value)) return null
  let length = first.parts.length
  for (const value of paths.slice(1)) {
    const parts = value!.parts
    length = Math.min(length, parts.length)
    let common = 0
    while (common < length && first.parts[common]!.toLocaleLowerCase() === parts[common]!.toLocaleLowerCase()) common++
    length = common
  }
  return length < first.minimum ? null : first.parts.slice(0, length).join(first.separator)
}

export function displayScanPath(folder: string, common: string | null): string {
  if (!common) return folder
  const target = parseDirectory(folder), root = parseDirectory(common)
  if (!target || !root || root.parts.length < root.minimum || root.parts.length > target.parts.length ||
    !root.parts.every((part, index) => part.toLocaleLowerCase() === target.parts[index]!.toLocaleLowerCase())) return folder
  return target.parts.slice(root.parts.length).join(target.separator) || '.'
}
