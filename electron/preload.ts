import { contextBridge, ipcRenderer } from 'electron'
import type { KunManagerAPI } from '../src/shared/types'

async function invoke<T>(method: string, ...args: unknown[]): Promise<T> {
  const result = await ipcRenderer.invoke('kun-manager:' + method, ...args) as { ok: true; value: T } | { ok: false; error: string }
  if (!result.ok) throw new Error(result.error)
  return result.value
}

const api: KunManagerAPI = {
  getState: () => invoke('getState'),
  saveSettings: input => invoke('saveSettings', input),
  testConnection: () => invoke('testConnection'),
  searchWorks: (query, cursor) => invoke('searchWorks', query, cursor),
  getWork: id => invoke('getWork', id),
  addVirtualGame: id => invoke('addVirtualGame', id),
  scanFolder: () => invoke('scanFolder'),
  prepareInstallationAttachment: id => invoke('prepareInstallationAttachment', id),
  attachLocalInstallation: (requestId, candidateId) => invoke('attachLocalInstallation', requestId, candidateId),
  cancelInstallationAttachment: requestId => invoke('cancelInstallationAttachment', requestId),
  importLocalGames: (ids, autoMatch) => invoke('importLocalGames', ids, autoMatch),
  startMetadataMatching: (ids, mode) => invoke('startMetadataMatching', ids, mode),
  cancelMetadataMatching: () => invoke('cancelMetadataMatching'),
  resolveMetadataMatch: (gameId, workId, reviewId, fingerprint) => invoke('resolveMetadataMatch', gameId, workId, reviewId, fingerprint),
  linkGame: (gameId, workId, reviewId, fingerprint) => invoke('linkGame', gameId, workId, reviewId, fingerprint),
  refreshGame: gameId => invoke('refreshGame', gameId),
  setGameCover: (gameId, workId, coverId, display) => invoke('setGameCover', gameId, workId, coverId, display),
  pickLocalCover: (gameId, workId, display) => invoke('pickLocalCover', gameId, workId, display),
  updateGame: (id, patch) => invoke('updateGame', id, patch),
  updateGames: (ids, patch) => invoke('updateGames', ids, patch),
  pickExecutable: (id, installationId) => invoke('pickExecutable', id, installationId),
  setLaunchArguments: (id, installationId, args) => invoke('setLaunchArguments', id, installationId, args),
  launchGame: id => invoke('launchGame', id),
  finishSession: () => invoke('finishSession'),
  removeGame: id => invoke('removeGame', id),
  exportLibrary: () => invoke('exportLibrary'),
  importLibrary: () => invoke('importLibrary'),
  openExternal: url => invoke('openExternal', url),
  openGameFolder: id => invoke('openGameFolder', id),
  openDataFolder: () => invoke('openDataFolder'),
  onChanged: callback => {
    const listener = () => callback()
    ipcRenderer.on('kun-manager:changed', listener)
    return () => ipcRenderer.removeListener('kun-manager:changed', listener)
  }
}
contextBridge.exposeInMainWorld('kunManager', api)
