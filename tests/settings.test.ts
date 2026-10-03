import test from 'node:test'
import assert from 'node:assert/strict'
import { promises as fs } from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { SettingsStore } from '../electron/settings'
import type { SettingsInput } from '../src/shared/types'

const codec = { available: () => true, encrypt: (text: string) => Buffer.from(text.split('').reverse().join('')), decrypt: (buffer: Buffer) => buffer.toString().split('').reverse().join('') }
async function fixture() { const dir = await fs.mkdtemp(path.join(os.tmpdir(), 'kgm-settings-')); const store = new SettingsStore(dir, codec); await store.initialize(); return { dir, store } }

test('credentials are excluded from public settings and survive reload through the codec', async () => {
  const { dir, store } = await fixture()
  try {
    await store.save({ baseUrl: 'https://api.nextmoe.dev/v2', apiKey: 'test_private_credential', includeNsfw: true, theme: 'light' })
    assert.equal(store.getPublic().hasApiKey, true)
    assert.equal(JSON.stringify(store.getPublic()).includes('test_private'), false)
    assert.equal((await fs.readFile(path.join(dir, 'settings.json'), 'utf8')).includes('test_private_credential'), false)
    const second = new SettingsStore(dir, codec); await second.initialize()
    assert.equal(second.getApiOptions().apiKey, 'test_private_credential')
    await second.save({ ...second.getPublic(), apiKey: '' })
    assert.equal(second.getApiOptions().apiKey, 'test_private_credential')
  } finally { await fs.rm(dir, { recursive: true, force: true }) }
})

test('changing the endpoint origin clears previous credentials until a key is supplied again', async () => {
  const { dir, store } = await fixture()
  try {
    await store.save({ baseUrl: 'https://api.nextmoe.dev/v2/', apiKey: 'test_credential', includeNsfw: true, theme: 'light' })
    await store.save({ ...store.getPublic(), baseUrl: 'https://example.com/v2/' })
    assert.equal(store.getPublic().hasApiKey, false)
    assert.throws(() => store.getApiOptions(), /密钥/)
  } finally { await fs.rm(dir, { recursive: true, force: true }) }
})

test('unavailable encryption refuses key persistence and leaves the old config unchanged', async () => {
  const { dir } = await fixture()
  try {
    const store = new SettingsStore(dir, { ...codec, available: () => false }); await store.initialize()
    await assert.rejects(store.save({ baseUrl: 'https://api.nextmoe.dev/v2/', apiKey: 'test_credential', includeNsfw: true, theme: 'dark' }), /凭据保护/)
    assert.equal(store.getPublic().hasApiKey, false)
    assert.equal(store.getPublic().theme, 'light')
  } finally { await fs.rm(dir, { recursive: true, force: true }) }
})

test('clear key and reject malformed settings without writing secret text', async () => {
  const { dir, store } = await fixture()
  try {
    await store.save({ baseUrl: 'https://api.nextmoe.dev/v2/', apiKey: 'test_credential', includeNsfw: true, theme: 'light' })
    await assert.rejects(store.save({ ...store.getPublic(), apiKey: 'bad\ncredential' }), /单行/)
    await store.save({ ...store.getPublic(), clearApiKey: true })
    assert.equal(store.getPublic().hasApiKey, false)
  } finally { await fs.rm(dir, { recursive: true, force: true }) }
})

test('executable metadata reading is enabled in a new settings profile and survives an omitted-field save', async () => {
  const { dir, store } = await fixture()
  try {
    assert.equal(store.getPublic().readExecutableMetadata, true)
    await store.save({ baseUrl: 'https://api.nextmoe.dev/v2/', includeNsfw: true, theme: 'light' })
    assert.equal(store.getPublic().readExecutableMetadata, true)
    const reloaded = new SettingsStore(dir, codec); await reloaded.initialize()
    assert.equal(reloaded.getPublic().readExecutableMetadata, true)
  } finally { await fs.rm(dir, { recursive: true, force: true }) }
})

test('legacy settings without the option load with executable metadata reading enabled without rewriting the file', async () => {
  const { dir } = await fixture()
  try {
    const filename = path.join(dir, 'settings.json')
    const legacy = JSON.stringify({ version: 1, baseUrl: 'https://api.nextmoe.dev/v2/', encryptedKey: codec.encrypt('legacy_test_key').toString('base64'), includeNsfw: false, theme: 'dark' })
    await fs.writeFile(filename, legacy)
    const reloaded = new SettingsStore(dir, codec); await reloaded.initialize()
    assert.equal(reloaded.getPublic().readExecutableMetadata, true)
    assert.equal(reloaded.getPublic().theme, 'dark')
    assert.equal(reloaded.getPublic().includeNsfw, false)
    assert.equal(reloaded.getApiOptions().apiKey, 'legacy_test_key')
    assert.equal(await fs.readFile(filename, 'utf8'), legacy, 'loading an older config must not rewrite its contents')
  } finally { await fs.rm(dir, { recursive: true, force: true }) }
})

test('executable metadata reading can be explicitly disabled, re-enabled and disabled across restarts', async () => {
  const { dir, store } = await fixture()
  try {
    await store.save({ ...store.getPublic(), readExecutableMetadata: false })
    const initiallyDisabled = new SettingsStore(dir, codec); await initiallyDisabled.initialize()
    assert.equal(initiallyDisabled.getPublic().readExecutableMetadata, false)
    await store.save({ ...store.getPublic(), readExecutableMetadata: true })
    assert.equal(store.getPublic().readExecutableMetadata, true)
    assert.equal(JSON.parse(await fs.readFile(path.join(dir, 'settings.json'), 'utf8')).readExecutableMetadata, true)
    const reloaded = new SettingsStore(dir, codec); await reloaded.initialize()
    assert.equal(reloaded.getPublic().readExecutableMetadata, true)
    await reloaded.save({ ...reloaded.getPublic(), readExecutableMetadata: false })
    const disabled = new SettingsStore(dir, codec); await disabled.initialize()
    assert.equal(disabled.getPublic().readExecutableMetadata, false)
  } finally { await fs.rm(dir, { recursive: true, force: true }) }
})

for (const choice of [true, false]) test(`saving unrelated settings without the optional field preserves the explicit ${choice} choice`, async () => {
  const { dir, store } = await fixture()
  try {
    await store.save({ ...store.getPublic(), apiKey: 'preserved_test_key', readExecutableMetadata: choice })
    await store.save({ baseUrl: store.getPublic().baseUrl, includeNsfw: false, theme: 'system' })
    assert.equal(store.getPublic().readExecutableMetadata, choice)
    assert.equal(store.getPublic().theme, 'system')
    assert.equal(store.getPublic().includeNsfw, false)
    assert.equal(store.getApiOptions().apiKey, 'preserved_test_key')
    const reloaded = new SettingsStore(dir, codec); await reloaded.initialize()
    assert.equal(reloaded.getPublic().readExecutableMetadata, choice)
    assert.equal(reloaded.getPublic().theme, 'system')
  } finally { await fs.rm(dir, { recursive: true, force: true }) }
})

test('invalid opt-in types are rejected atomically and do not alter settings, credentials, or the saved file', async () => {
  const { dir, store } = await fixture()
  try {
    await store.save({ ...store.getPublic(), apiKey: 'unchanged_test_key', readExecutableMetadata: true })
    const previous = store.getPublic(), filename = path.join(dir, 'settings.json'), contents = await fs.readFile(filename, 'utf8')
    for (const invalid of [null, 'true', 'false', 0, 1, {}, []]) {
      await assert.rejects(store.save({ baseUrl: 'https://example.com/v2/', includeNsfw: false, theme: 'dark', clearApiKey: true, readExecutableMetadata: invalid } as unknown as SettingsInput), /格式/)
      assert.deepEqual(store.getPublic(), previous)
      assert.equal(store.getApiOptions().apiKey, 'unchanged_test_key')
      assert.equal(await fs.readFile(filename, 'utf8'), contents)
    }
    await store.save({ ...store.getPublic(), readExecutableMetadata: false })
    assert.equal(store.getPublic().readExecutableMetadata, false, 'invalid requests must not poison the serialized save queue')
  } finally { await fs.rm(dir, { recursive: true, force: true }) }
})
