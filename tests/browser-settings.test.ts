import assert from 'node:assert/strict'
import { test } from 'node:test'
import { promises as fs } from 'node:fs'
import vm from 'node:vm'
import { transform } from 'esbuild'
import type { KunManagerAPI, SettingsInput } from '../src/shared/types'

// Exercise the real browser fallback with no desktop bridge, storage, or network.
async function preview() {
  const source = await fs.readFile(new URL('../src/bridge.ts', import.meta.url), 'utf8')
  const script = source.replace(/^import .*$/gm, '').replace(/^export /gm, '') + '\nglobalThis.previewAPI = api;'
  const { code } = await transform(script, { loader: 'ts', format: 'cjs', target: 'es2022' })
  const sandbox: any = { window: {}, structuredClone, fetch: async () => ({ ok: false }) }
  vm.createContext(sandbox); vm.runInContext(code, sandbox)
  return sandbox.previewAPI as KunManagerAPI
}

test('browser preview preserves each interface style across omitted saves without storing a credential', async () => {
  const api = await preview()
  assert.equal((await api.getState()).settings.appearanceStyle, 'windows')
  for (const appearanceStyle of ['classic', 'windows', 'glass'] as const) {
    const saved = await api.saveSettings({ ...(await api.getState()).settings, appearanceStyle, apiKey: 'synthetic-browser-key' })
    assert.equal(saved.appearanceStyle, appearanceStyle)
    assert.equal(saved.hasApiKey, false)
    assert.equal(JSON.stringify(saved).includes('synthetic-browser-key'), false)
    await api.saveSettings({ baseUrl: saved.baseUrl, includeNsfw: saved.includeNsfw, theme: 'dark' })
    assert.equal((await api.getState()).settings.appearanceStyle, appearanceStyle)
    assert.equal((await api.getState()).settings.theme, 'dark')
  }
  const freshPage = await preview()
  assert.equal((await freshPage.getState()).settings.appearanceStyle, 'windows', 'browser preview remains explicitly in-memory')
})

test('invalid browser style is rejected before changing settings or emitting a saved notification', async () => {
  const api = await preview(), saved = (await api.getState()).settings
  let notifications = 0
  const stop = api.onChanged(() => notifications++)
  for (const appearanceStyle of [null, 'Glass', true, 2, {}, []]) {
    await assert.rejects(api.saveSettings({ ...saved, theme: 'dark', appearanceStyle } as unknown as SettingsInput), /界面风格/)
    assert.deepEqual((await api.getState()).settings, saved)
  }
  assert.equal(notifications, 0)
  await api.saveSettings({ ...saved, appearanceStyle: 'glass' })
  assert.equal(notifications, 1)
  stop()
})
