import type { AppSnapshot, KunManagerAPI, LibraryGame, WorkMetadata } from './shared/types'
import { createDesktopBridge } from './desktop-bridge'

export const isPreview = !window.kunManager
const listeners = new Set<() => void>()
const state: AppSnapshot = { games: [], settings: { baseUrl: 'https://api.nextmoe.dev/v2/', hasApiKey: false, includeNsfw: false, theme: 'light', readExecutableMetadata: true }, activeSession: null, dataPath: '浏览器预览 · 仅保存在内存', version: '0.7.1' }
let works: WorkMetadata[] = []
let loaded: Promise<void> | undefined
const clone = <T>(value: T): T => structuredClone(value)
const changed = () => listeners.forEach(fn => fn())
const desktopOnly = async (): Promise<never> => { throw new Error('请打开桌面版使用这项本地功能。') }
const findGame = (id: string) => { const game = state.games.find(g => g.id === id); if (!game) throw new Error('游戏不在书架中。'); return game }
const findWork = (id: string) => { const work = works.find(w => w.id === id); if (!work) throw new Error('预览资料中没有这部作品。'); return work }
const addWork = (work: WorkMetadata): LibraryGame => ({ id: `preview-${work.id}`, name: work.title, metadata: clone(work), installations: [], preferredInstallationId: null, status: 'planned', personalRating: null, favorite: false, categories: [], customTags: [], notes: '', totalPlaySeconds: 0, playCount: 0, sessions: [], addedAt: new Date().toISOString(), lastPlayedAt: null })
async function loadPreview() {
  loaded ??= (async () => {
    try {
      const response = await fetch('./preview-data.json')
      if (!response.ok) return
      const data = await response.json()
      const items = Array.isArray(data) ? data : data.items
      if (Array.isArray(items)) { works = items.filter((w: WorkMetadata) => typeof w.id === 'string' && typeof w.title === 'string'); state.games = works.slice(0, 12).map(addWork) }
    } catch { /* A preview without sample metadata remains a usable empty state. */ }
  })()
  await loaded
}
const preview: KunManagerAPI = {
  async getState() { await loadPreview(); return clone(state) },
  async saveSettings(input) { state.settings = { baseUrl: input.baseUrl, hasApiKey: false, includeNsfw: input.includeNsfw, theme: input.theme, readExecutableMetadata: input.readExecutableMetadata ?? state.settings.readExecutableMetadata }; changed(); return clone(state.settings) },
  async testConnection() { return { ok: false, message: '界面预览不连接 API，也不保存密钥。请在桌面版测试。' } },
  async searchWorks(query) { await loadPreview(); const q = query.toLocaleLowerCase(); return { items: clone(works.filter(w => [w.title, w.originalTitle, ...w.aliases].some(s => s.toLocaleLowerCase().includes(q)))), nextCursor: null } },
  async getWork(id) { await loadPreview(); return clone(findWork(id)) },
  async addVirtualGame(id) { const existing = state.games.find(g => g.metadata?.id === id); if (existing) return clone(existing); const game = addWork(findWork(id)); state.games.push(game); changed(); return clone(game) },
  scanFolder: desktopOnly, importLocalGames: desktopOnly,
  startMetadataMatching: desktopOnly, cancelMetadataMatching: desktopOnly, resolveMetadataMatch: desktopOnly,
  async linkGame(gameId, workId) { const game = findGame(gameId); game.metadata = clone(findWork(workId)); changed(); return clone(game) },
  async refreshGame(id) { return clone(findGame(id)) },
  async updateGame(id, patch) { const game = findGame(id); Object.assign(game, clone(patch)); changed(); return clone(game) },
  async updateGames(ids, patch) {
    if (!ids.length || ids.length > 1000 || new Set(ids).size !== ids.length) throw new Error('请选择 1 至 1000 部不同的游戏。')
    const targets = ids.map(id => clone(findGame(id)))
    for (const game of targets) {
      if (patch.status !== undefined) game.status = patch.status
      if (patch.favorite !== undefined) game.favorite = patch.favorite
      if (patch.personalRating !== undefined) game.personalRating = patch.personalRating
      game.categories = [...new Set([...game.categories.filter(value => !patch.removeCategories?.includes(value)), ...(patch.addCategories ?? [])])]
      game.customTags = [...new Set([...game.customTags.filter(value => !patch.removeTags?.includes(value)), ...(patch.addTags ?? [])])]
      if (game.categories.length > 200 || game.customTags.length > 200) throw new Error('分类或标签超过 200 项。')
    }
    state.games = state.games.map(game => targets.find(target => target.id === game.id) ?? game)
    changed(); return clone(targets)
  },
  pickExecutable: desktopOnly, setLaunchArguments: desktopOnly, launchGame: desktopOnly, finishSession: desktopOnly,
  async removeGame(id) { state.games = state.games.filter(g => g.id !== id); changed() },
  exportLibrary: desktopOnly, importLibrary: desktopOnly,
  async openExternal(url) { if (/^https?:\/\//i.test(url)) window.open(url, '_blank', 'noopener,noreferrer') },
  openGameFolder: desktopOnly, openDataFolder: desktopOnly,
  onChanged(callback) { listeners.add(callback); return () => listeners.delete(callback) }
}
export const api: KunManagerAPI = window.kunManager ? createDesktopBridge(window.kunManager) : preview
