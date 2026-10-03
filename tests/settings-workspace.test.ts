import assert from 'node:assert/strict'
import { test } from 'node:test'
import { promises as fs } from 'node:fs'
import vm from 'node:vm'
import { transform } from 'esbuild'
import * as vue from 'vue'
import * as libraryWorkspace from '../src/library-workspace'
import * as importWorkspace from '../src/import-workspace'
import * as libraryInsights from '../src/library-insights'
import * as savedLibraryViews from '../src/saved-library-views'
import * as coverDisplay from '../src/cover-display'
import type { PublicSettings } from '../src/shared/types'

function deferred<T>() { let resolve!: (value: T) => void, reject!: (error: Error) => void; const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no }); return { promise, resolve, reject } }
const plain = (value: unknown) => JSON.parse(JSON.stringify(value))
const compiled = (async () => {
  const source = await fs.readFile(new URL('../src/App.vue', import.meta.url), 'utf8')
  const script = source.split('<script setup lang="ts">')[1]!.split('</script>')[0]!
  return { script, code: (await transform(script.replace(/^import .*$/gm, '') + '\nglobalThis.audit = {snapshot,busy,settingsForm,settingsDirty,settingsError,connectionFeedback,connectionDraftDirty,saveSettings,saveAndTestConnection,testConnection,resetSettingsDraft};', { loader: 'ts', format: 'cjs', target: 'es2022' })).code }
})()
async function harness(t: { after(callback: () => void): void }) {
  const { script, code } = await compiled, messages: string[] = [], api: any = {}
  const sandbox: any = { ...vue, ...libraryWorkspace, ...importWorkspace, ...libraryInsights, ...savedLibraryViews, ...coverDisplay, Error, api, isPreview: false, onMounted() {}, onUnmounted() {}, useKunMessage: (message: string) => messages.push(message), useKunAlert: async () => true, document: { documentElement: { classList: { toggle() {} } } }, matchMedia: () => ({ matches: false }) }
  for (const line of script.split('\n').filter(line => /^import \{/.test(line))) for (const name of line.match(/^import \{([^}]+)\}/)![1]!.split(',').map(name => name.trim())) if (!(name in sandbox)) sandbox[name] = name
  const scope = vue.effectScope(); t.after(() => scope.stop()); vm.createContext(sandbox); scope.run(() => vm.runInContext(code, sandbox))
  const a = sandbox.audit, initial: PublicSettings = { baseUrl: 'https://fixture.invalid/v2/', hasApiKey: true, includeNsfw: false, theme: 'light', readExecutableMetadata: true, nonPortraitCoverDisplay: 'blur' }
  let saved = { ...initial }; const calls: any[] = []
  const state = () => ({ games: [], settings: { ...saved }, activeSession: null, dataPath: 'synthetic-only', version: 'fixture' })
  api.getState = async () => state()
  api.saveSettings = async (input: any) => { calls.push(structuredClone(input)); saved = { ...input, baseUrl: input.baseUrl.trim(), hasApiKey: input.clearApiKey ? false : !!input.apiKey || saved.hasApiKey }; delete (saved as any).apiKey; delete (saved as any).clearApiKey; return { ...saved } }
  api.testConnection = async () => ({ ok: true, message: 'Synthetic catalog reachable' })
  a.snapshot.value = state(); await vue.nextTick()
  return { a, api, calls, messages, initial, state, setSaved: (value: PublicSettings) => { saved = { ...value } } }
}

test('saved snapshots merge untouched fields without discarding unsaved connection or appearance input', async t => {
  const { a, initial } = await harness(t)
  assert.equal(a.settingsDirty.value, false)
  a.settingsForm.baseUrl = 'https://draft.invalid/v2/'; a.settingsForm.apiKey = 'synthetic-draft'; a.settingsForm.theme = 'dark'
  a.snapshot.value = { ...a.snapshot.value, settings: { ...initial, includeNsfw: true } }
  assert.equal(a.settingsForm.baseUrl, 'https://draft.invalid/v2/'); assert.equal(a.settingsForm.apiKey, 'synthetic-draft'); assert.equal(a.settingsForm.theme, 'dark'); assert.equal(a.settingsForm.includeNsfw, true)
  a.resetSettingsDraft(); assert.equal(a.settingsDirty.value, false); assert.equal(a.settingsForm.apiKey, ''); assert.equal(a.settingsForm.includeNsfw, true)
})

for (const later of ['system', 'light'] as const) test(`save preserves later edits, including a return to previous ${later} value`, async t => {
  const { a, api, initial, setSaved, calls } = await harness(t), response = deferred<PublicSettings>()
  api.saveSettings = (input: any) => { calls.push(structuredClone(input)); return response.promise }
  a.settingsForm.theme = 'dark'; a.settingsForm.apiKey = 'synthetic-submitted'
  const pending = a.saveSettings(); a.settingsForm.theme = later; a.settingsForm.apiKey = 'synthetic-later'
  a.snapshot.value = { ...a.snapshot.value, settings: { ...initial } } // cancellation notification is not a save acknowledgement
  assert.equal(a.settingsForm.theme, later)
  const saved = { ...initial, theme: 'dark' as const }; setSaved(saved); response.resolve(saved)
  assert.equal(await pending, true); assert.equal(a.settingsForm.theme, later); assert.equal(a.settingsForm.apiKey, 'synthetic-later'); assert.equal(a.settingsDirty.value, true); assert.equal(calls.length, 1)
})

test('successful acknowledgement normalizes submitted fields and clears only the submitted credential', async t => {
  const { a, calls } = await harness(t)
  a.settingsForm.baseUrl = ' https://normalized.invalid/v2/ '; a.settingsForm.apiKey = ' synthetic-submitted '; a.settingsForm.nonPortraitCoverDisplay = 'crop'
  assert.equal(await a.saveSettings(), true)
  assert.equal(calls[0].baseUrl, 'https://normalized.invalid/v2/'); assert.equal(calls[0].apiKey, 'synthetic-submitted'); assert.equal(a.settingsDirty.value, false); assert.equal(a.settingsForm.apiKey, '')
})

test('removing the saved credential submits saved preferences only and preserves every unsaved draft', async t => {
  const { a, initial, calls } = await harness(t)
  a.settingsForm.theme = 'dark'; a.settingsForm.apiKey = 'synthetic-do-not-submit'; a.settingsForm.baseUrl = 'https://draft.invalid/'; a.settingsForm.includeNsfw = true
  await a.saveSettings(true)
  assert.deepEqual(plain(calls[0]), { baseUrl: initial.baseUrl, includeNsfw: initial.includeNsfw, theme: initial.theme, readExecutableMetadata: true, nonPortraitCoverDisplay: 'blur', clearApiKey: true })
  assert.equal(a.snapshot.value.settings.hasApiKey, false); assert.equal(a.settingsForm.apiKey, 'synthetic-do-not-submit'); assert.equal(a.settingsForm.theme, 'dark'); assert.equal(a.settingsForm.baseUrl, 'https://draft.invalid/'); assert.equal(a.settingsForm.includeNsfw, true)
})

test('saved-with-warning confirms persistence and preserves later drafts', async t => {
  const { a, api, initial, setSaved, messages } = await harness(t), response = deferred<PublicSettings & { warning?: string }>()
  api.saveSettings = () => response.promise
  a.settingsForm.theme = 'dark'; a.settingsForm.apiKey = 'synthetic-submitted'
  const pending = a.saveSettings(); a.settingsForm.theme = 'system'; a.settingsForm.apiKey = 'synthetic-later'
  const saved = { ...initial, theme: 'dark' as const }, warning = '设置已保存，但停止识别状态暂未写入磁盘。'
  setSaved(saved); response.resolve({ ...saved, warning })
  assert.equal(await pending, true); assert.equal(a.snapshot.value.settings.theme, 'dark'); assert.equal(a.snapshot.value.settings.warning, undefined)
  assert.equal(a.settingsForm.theme, 'system'); assert.equal(a.settingsForm.apiKey, 'synthetic-later'); assert.equal(a.settingsDirty.value, true)
  assert.equal(a.settingsError.value, ''); assert.ok(messages.includes(warning))
})

test('failed save retains drafts, never tests and can be retried without false success', async t => {
  const { a, api, messages } = await harness(t), original = api.saveSettings; let tests = 0
  api.testConnection = async () => { tests++; return { ok: true, message: 'Synthetic' } }
  api.saveSettings = async () => { throw new Error('Synthetic write failure nmk_test_SYNTHETIC') }
  a.settingsForm.apiKey = 'synthetic-retained'; a.settingsForm.theme = 'dark'; await a.saveAndTestConnection()
  assert.equal(tests, 0); assert.equal(messages.length, 0); assert.match(a.settingsError.value, /设置保存未确认/); assert.doesNotMatch(a.settingsError.value, /nmk_test_/); assert.equal(a.settingsForm.apiKey, 'synthetic-retained'); assert.equal(a.settingsDirty.value, true)
  api.saveSettings = original; await a.saveAndTestConnection(); assert.equal(tests, 1); assert.equal(a.settingsError.value, ''); assert.equal(a.connectionFeedback.value.ok, true)
})

test('a successful write followed by reload failure reports persistence accurately and skips automatic test', async t => {
  const { a, api } = await harness(t); let tests = 0
  a.settingsForm.theme = 'dark'; api.getState = async () => { throw new Error('Synthetic snapshot unavailable') }; api.testConnection = async () => { tests++ }
  await a.saveAndTestConnection(); assert.match(a.settingsError.value, /设置已保存，但刷新失败/); assert.equal(a.snapshot.value.settings.theme, 'dark'); assert.equal(a.settingsDirty.value, false); assert.equal(tests, 0)
})

test('save and connection actions share a lock and combined action tests only after the save acknowledgement', async t => {
  const { a, api, initial, setSaved } = await harness(t), response = deferred<PublicSettings>(); let writes = 0, tests = 0
  api.saveSettings = () => { writes++; return response.promise }; api.testConnection = async () => { tests++; return { ok: true, message: 'Synthetic' } }
  a.settingsForm.theme = 'dark'; const pending = a.saveAndTestConnection(); await a.saveSettings(true); await a.testConnection(); await a.saveAndTestConnection(); assert.equal(writes, 1); assert.equal(tests, 0)
  const saved = { ...initial, theme: 'dark' as const }; setSaved(saved); response.resolve(saved); await pending; assert.equal(tests, 1)
  const connection = deferred<any>(); api.testConnection = () => connection.promise; const checking = a.testConnection(); a.settingsForm.theme = 'light'; assert.equal(await a.saveSettings(), false); a.resetSettingsDraft(); assert.equal(a.settingsForm.theme, 'light'); connection.resolve({ ok: false, message: 'Synthetic unavailable' }); await checking; assert.equal(a.connectionFeedback.value.ok, false)
})

test('connection feedback follows saved configuration, not new inputs, and is invalidated even by same-public-state key replacement', async t => {
  const { a } = await harness(t); await a.testConnection(); const tested = a.connectionFeedback.value.baseUrl
  a.settingsForm.baseUrl = 'https://not-yet-saved.invalid/'; a.settingsForm.apiKey = 'synthetic-new'; assert.equal(a.connectionDraftDirty.value, true); assert.equal(a.connectionFeedback.value.baseUrl, tested)
  a.resetSettingsDraft(); a.settingsForm.apiKey = 'synthetic-replacement'; await a.saveSettings(); assert.equal(a.connectionFeedback.value, null)
})

test('late connection result for externally replaced saved configuration is discarded', async t => {
  const { a, api, initial } = await harness(t), response = deferred<any>()
  api.testConnection = () => response.promise; const pending = a.testConnection()
  a.snapshot.value = { ...a.snapshot.value, settings: { ...initial, baseUrl: 'https://different.invalid/' } }
  response.resolve({ ok: true, message: 'Old configuration only' }); await pending; assert.equal(a.connectionFeedback.value, null)
})

test('failed credential removal leaves stored status and typed input intact', async t => {
  const { a, api } = await harness(t); a.settingsForm.apiKey = 'synthetic-draft'; api.saveSettings = async () => { throw new Error('Synthetic removal failure') }
  assert.equal(await a.saveSettings(true), false); assert.equal(a.snapshot.value.settings.hasApiKey, true); assert.equal(a.settingsForm.apiKey, 'synthetic-draft'); assert.match(a.settingsError.value, /设置保存未确认/)
})


test('a notification matching the submission is not an acknowledgement when the save reply fails; retry remains possible', async t => {
  const { a, api, initial, calls, setSaved } = await harness(t), response = deferred<PublicSettings>(), original = api.saveSettings
  a.settingsForm.theme = 'dark'; api.saveSettings = () => response.promise; const pending = a.saveSettings()
  const reported = { ...initial, theme: 'dark' as const }; setSaved(reported); a.snapshot.value = { ...a.snapshot.value, settings: reported }
  response.reject(new Error('Synthetic transport acknowledgement unavailable')); assert.equal(await pending, false)
  assert.match(a.settingsError.value, /设置保存未确认/)
  api.saveSettings = original; assert.equal(await a.saveSettings(), true); assert.equal(calls.length, 1); assert.equal(a.settingsError.value, '')
})
