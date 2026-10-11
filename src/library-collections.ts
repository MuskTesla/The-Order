import type { LibraryGame } from './shared/types'

export interface LibraryCollection {
  name: string
  games: LibraryGame[]
  playing: number
  completed: number
  latestAddedAt: string
}
export type CollectionSort = 'name' | 'count' | 'recent'

function summarize(name: string, games: LibraryGame[]): LibraryCollection {
  return {
    name, games, playing: games.filter(game => game.status === 'playing').length,
    completed: games.filter(game => game.status === 'completed').length,
    latestAddedAt: games.reduce((latest, game) => game.addedAt > latest ? game.addedAt : latest, ''),
  }
}

/** Read-only groups: one game counts once per label, even in a legacy duplicate list. */
export function getLibraryCollections(games: LibraryGame[]): { favorites: LibraryCollection; categories: LibraryCollection[] } {
  const groups = new Map<string, LibraryGame[]>()
  for (const game of games) for (const category of new Set(game.categories)) {
    if (!category) continue
    const members = groups.get(category) ?? []
    members.push(game)
    groups.set(category, members)
  }
  return {
    favorites: summarize('特别喜欢', games.filter(game => game.favorite)),
    categories: [...groups].map(([name, members]) => summarize(name, members)),
  }
}

const normalize = (value: string): string => value.normalize('NFKC').toLocaleLowerCase().replace(/[^\p{L}\p{N}]/gu, '')

export function findLibraryCollections(collections: LibraryCollection[], query: string, sort: CollectionSort): LibraryCollection[] {
  const terms = query.trim().split(/\s+/).map(normalize).filter(Boolean)
  return collections.filter(collection => {
    if (!terms.length) return true
    const fields = [collection.name, ...collection.games.flatMap(game => [
      game.name, game.metadata?.title ?? '', game.metadata?.originalTitle ?? '', ...(game.metadata?.aliases ?? []),
    ])].map(normalize)
    return terms.every(term => fields.some(field => field.includes(term)))
  }).sort((a, b) => {
    const order = sort === 'count' ? b.games.length - a.games.length
      : sort === 'recent' ? b.latestAddedAt.localeCompare(a.latestAddedAt) : 0
    return order || a.name.localeCompare(b.name, 'zh-CN') || a.name.localeCompare(b.name)
  })
}

export function findCollectionTags(tags: string[], query: string): string[] {
  const terms = query.trim().split(/\s+/).map(normalize).filter(Boolean)
  return [...new Set(tags)].filter(tag => terms.every(term => normalize(tag).includes(term))).sort((a, b) => a.localeCompare(b, 'zh-CN'))
}
