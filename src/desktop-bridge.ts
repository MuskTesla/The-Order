import { toRaw } from 'vue'
import type { KunManagerAPI } from './shared/types'

// contextBridge cannot clone Vue proxies. Snapshot DTOs before entering preload.
function snapshotDto<T>(input: T): T {
  const value = toRaw(input)
  if (Array.isArray(value)) return value.map(item => snapshotDto(item)) as T
  if (value !== null && typeof value === 'object') {
    return Object.fromEntries(Object.entries(value).map(([key, item]) => [key, snapshotDto(item)])) as T
  }
  return value
}

export function createDesktopBridge(desktop: KunManagerAPI): KunManagerAPI {
  return {
    ...desktop,
    importLocalGames: (ids, autoMatch) => desktop.importLocalGames(snapshotDto(ids), autoMatch),
    startMetadataMatching: (ids, mode) => desktop.startMetadataMatching(ids === undefined ? undefined : snapshotDto(ids), mode),
    saveSettings: input => desktop.saveSettings(snapshotDto(input)),
    setGameCover: (id, workId, coverId, display) => desktop.setGameCover(id, workId, coverId, display),
    pickLocalCover: (id, workId, display) => desktop.pickLocalCover(id, workId, display),
    updateGame: (id, patch) => desktop.updateGame(id, snapshotDto(patch)),
    updateGames: (ids, patch) => desktop.updateGames(snapshotDto(ids), snapshotDto(patch)),
    setLaunchArguments: (id, installationId, args) => desktop.setLaunchArguments(id, installationId, snapshotDto(args))
  }
}
