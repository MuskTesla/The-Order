import assert from 'node:assert/strict'
import test from 'node:test'
import { isReactive, reactive, ref } from 'vue'
import { createDesktopBridge } from '../src/desktop-bridge'
import type { GamePatch, KunManagerAPI, LibraryGame, SettingsInput } from '../src/shared/types'

// These tests model the serialization boundary, not the main-process validators.
// Supplying a Vue Proxy directly to this mock fails just like Electron IPC.
function desktop(methods: Partial<KunManagerAPI>): KunManagerAPI {
  return methods as KunManagerAPI
}

const resultGame: LibraryGame = {
  id: 'game-1', name: 'Test game', metadata: null, installations: [], preferredInstallationId: null,
  status: 'planned', personalRating: null, favorite: false, categories: [], customTags: [], notes: '',
  totalPlaySeconds: 0, playCount: 0, sessions: [], addedAt: '2026-01-01T00:00:00.000Z', lastPlayedAt: null
}

test('selected IDs from a real Vue ref cross the desktop boundary as a detached plain array', async () => {
  const chosen = ref(['candidate-1', 'candidate-2'])
  let received: string[] | undefined
  let serialized: string[] | undefined
  const api = createDesktopBridge(desktop({
    async importLocalGames(ids) {
      received = ids
      serialized = structuredClone(ids)
      return { added: ids.length, skipped: 0 }
    }
  }))

  assert.equal(isReactive(chosen.value), true)
  const pending = api.importLocalGames(chosen.value)
  chosen.value[0] = 'changed-after-call'
  chosen.value.push('candidate-3')

  assert.deepEqual(await pending, { added: 2, skipped: 0 })
  assert.deepEqual(received, ['candidate-1', 'candidate-2'])
  assert.deepEqual(serialized, received)
  assert.equal(isReactive(received), false)
  assert.equal(isReactive(chosen.value), true)
  assert.deepEqual(chosen.value, ['changed-after-call', 'candidate-2', 'candidate-3'])
})

test('reactive settings become a plain snapshot without mutating the source form', async () => {
  const input = reactive<SettingsInput>({
    baseUrl: 'https://api.nextmoe.dev/v2/', apiKey: 'dummy-key-for-this-test', includeNsfw: true, theme: 'dark', appearanceStyle: 'glass'
  })
  let received: SettingsInput | undefined
  const api = createDesktopBridge(desktop({
    async saveSettings(value) {
      received = value
      const clone = structuredClone(value)
      return { baseUrl: clone.baseUrl, hasApiKey: Boolean(clone.apiKey), includeNsfw: clone.includeNsfw, theme: clone.theme, appearanceStyle: clone.appearanceStyle, readExecutableMetadata: clone.readExecutableMetadata ?? false }
    }
  }))

  const pending = api.saveSettings(input)
  input.apiKey = ''
  input.theme = 'light'
  input.appearanceStyle = 'classic'

  assert.deepEqual(await pending, { baseUrl: 'https://api.nextmoe.dev/v2/', hasApiKey: true, includeNsfw: true, theme: 'dark', appearanceStyle: 'glass', readExecutableMetadata: false })
  assert.equal(received?.apiKey, 'dummy-key-for-this-test')
  assert.equal(received?.theme, 'dark')
  assert.equal(received?.appearanceStyle, 'glass')
  assert.equal(isReactive(received), false)
  assert.equal(isReactive(input), true)
  assert.equal(input.apiKey, '')
  assert.equal(input.theme, 'light')
  assert.equal(input.appearanceStyle, 'classic')
})

test('updateGame copies nested reactive categories and tags even inside a plain outer object', async () => {
  const categories = reactive(['视觉小说', '收藏'])
  const customTags = reactive(['周末'])
  const patch: GamePatch = { name: 'My game', categories, customTags }
  let receivedId: string | undefined
  let received: GamePatch | undefined
  const api = createDesktopBridge(desktop({
    async updateGame(id, value) {
      receivedId = id
      received = value
      structuredClone(value)
      return resultGame
    }
  }))

  const pending = api.updateGame('game-1', patch)
  categories.push('稍后再玩')
  customTags[0] = '已经改变'
  patch.name = 'Renamed after call'

  assert.equal(await pending, resultGame)
  assert.equal(receivedId, 'game-1')
  assert.deepEqual(received, { name: 'My game', categories: ['视觉小说', '收藏'], customTags: ['周末'] })
  assert.equal(isReactive(received), false)
  assert.equal(isReactive(received?.categories), false)
  assert.equal(isReactive(received?.customTags), false)
  assert.equal(isReactive(categories), true)
  assert.equal(isReactive(customTags), true)
  assert.equal(patch.categories, categories)
  assert.equal(patch.customTags, customTags)
})

test('an outer reactive patch with explicitly nested proxies is recursively unwrapped', async () => {
  const nested = reactive(['nested category'])
  const patch = reactive<GamePatch>({ categories: nested, customTags: reactive(['nested tag']), favorite: true })
  let received: GamePatch | undefined
  const api = createDesktopBridge(desktop({
    async updateGame(_id, value) { received = value; structuredClone(value); return resultGame }
  }))

  await api.updateGame('game-1', patch)
  assert.deepEqual(received, { categories: ['nested category'], customTags: ['nested tag'], favorite: true })
  assert.notEqual(received, patch)
  assert.notEqual(received?.categories, nested)
  assert.equal(isReactive(patch), true)
  assert.equal(isReactive(patch.categories), true)
  assert.equal(isReactive(patch.customTags), true)
})

test('launch argument arrays are detached while game and installation IDs pass through unchanged', async () => {
  const args = ref(['-windowed', 'folder with spaces', ''])
  let received: string[] | undefined
  let ids: string[] | undefined
  const api = createDesktopBridge(desktop({
    async setLaunchArguments(gameId, installationId, values) {
      ids = [gameId, installationId]
      received = values
      structuredClone(values)
      return resultGame
    }
  }))

  const pending = api.setLaunchArguments('game-1', 'installation-1', args.value)
  args.value.splice(0, 1, '-fullscreen')

  assert.equal(await pending, resultGame)
  assert.deepEqual(ids, ['game-1', 'installation-1'])
  assert.deepEqual(received, ['-windowed', 'folder with spaces', ''])
  assert.equal(isReactive(received), false)
  assert.equal(isReactive(args.value), true)
  assert.deepEqual(args.value, ['-fullscreen', 'folder with spaces', ''])
})

test('snapshotting preserves undefined and NaN so main-process validation sees the original values', async () => {
  const patch = reactive<GamePatch>({ personalRating: Number.NaN, notes: undefined, preferredInstallationId: null })
  let received: GamePatch | undefined
  const api = createDesktopBridge(desktop({
    async updateGame(_id, value) { received = structuredClone(value); return resultGame }
  }))

  await api.updateGame('game-1', patch)
  assert.equal(Number.isNaN(received?.personalRating), true)
  assert.equal(Object.hasOwn(received!, 'notes'), true)
  assert.equal(received?.notes, undefined)
  assert.equal(received?.preferredInstallationId, null)
  assert.equal(Number.isNaN(patch.personalRating), true)
  assert.equal(Object.hasOwn(patch, 'notes'), true)
})

test('change subscriptions forward the original callback and unsubscribe function', () => {
  let observed: (() => void) | undefined
  let notifications = 0
  let removals = 0
  const callback = () => { notifications++ }
  const unsubscribe = () => { removals++ }
  const api = createDesktopBridge(desktop({ onChanged(value) { observed = value; return unsubscribe } }))

  const stop = api.onChanged(callback)
  assert.equal(observed, callback)
  assert.equal(stop, unsubscribe)
  observed!()
  assert.equal(notifications, 1)
  stop()
  assert.equal(removals, 1)
})

test('reidentification mode and detached reactive game IDs reach preload together', async () => {
  const selected = ref(['local-1', 'local-2'])
  const received: unknown[] = []
  const api = createDesktopBridge(desktop({
    async startMetadataMatching(ids, mode) {
      received.push(structuredClone({ ids, mode }))
      return { id: 'job', status: 'running', total: ids?.length ?? 1, processed: 0, matched: 0, unmatched: 0, failed: 0, currentName: null, reviews: [], mode }
    }
  }))
  const pending = api.startMetadataMatching(selected.value, 'reidentify')
  selected.value[0] = 'edited-later'
  await pending
  await api.startMetadataMatching()
  assert.deepEqual(received, [{ ids: ['local-1', 'local-2'], mode: 'reidentify' }, { ids: undefined, mode: undefined }])
  assert.deepEqual(selected.value, ['edited-later', 'local-2'])
})
