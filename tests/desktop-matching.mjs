import assert from 'node:assert/strict'
import { promises as fs } from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { _electron as electron } from '../node_modules/playwright/index.mjs'

const project = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const temporaryRoot = path.resolve(os.tmpdir())
const temporary = await fs.mkdtemp(path.join(temporaryRoot, 'kgm-desktop-matching-'))
const profile = path.join(temporary, 'profile')
const gamesFolder = path.join(temporary, '隔离的自动识别测试')
const ambiguousName = '鲸屿歧义回归样例'
const unknownName = '鲸屿未匹配回归噪声'
const fakeKey = 'test-desktop-matching-fixture-only'
const peFolderName = 'Whale Island Evidence Voyage'
const peTitle = 'Whale Island Evidence Voyage Starward Letters'
const peFolder = path.join(temporary, 'synthetic-program-fixture', peFolderName)
for (const name of ['CLANNAD', ambiguousName, unknownName]) {
  const directory = path.join(gamesFolder, name)
  await fs.mkdir(directory, { recursive: true })
  await fs.writeFile(path.join(directory, `${name}.exe`), 'Fixture only; never execute.')
}
// An inert PE resource fixture constructed entirely by this test. It has no code
// section or executable entry point and is used only to exercise bounded reads.
const align = value => Math.ceil(value / 4) * 4
function versionBlock(key, value = Buffer.alloc(0), type = 1, children = []) {
  const encodedKey = Buffer.from(`${key}\0`, 'utf16le'), valueStart = align(6 + encodedKey.length)
  let length = valueStart + value.length
  const offsets = children.map(child => { const offset = align(length); length = offset + child.length; return offset })
  const bytes = Buffer.alloc(length)
  bytes.writeUInt16LE(length, 0); bytes.writeUInt16LE(type === 1 ? value.length / 2 : value.length, 2); bytes.writeUInt16LE(type, 4)
  encodedKey.copy(bytes, 6); value.copy(bytes, valueStart)
  children.forEach((child, index) => child.copy(bytes, offsets[index]))
  return bytes
}
function syntheticPe(title) {
  const strings = Object.entries({ ProductName: title, FileDescription: title, CompanyName: 'Whale Island Fixture Studio' })
    .map(([key, value]) => versionBlock(key, Buffer.from(`${value}\0`, 'utf16le')))
  const payload = versionBlock('VS_VERSION_INFO', Buffer.alloc(52), 0, [versionBlock('StringFileInfo', undefined, 1, [versionBlock('041104b0', undefined, 1, strings)])])
  const rawSize = Math.ceil((0x100 + payload.length) / 0x200) * 0x200
  const bytes = Buffer.alloc(0x400 + rawSize)
  bytes.write('MZ', 0); bytes.writeUInt32LE(0x80, 0x3c); bytes.write('PE\0\0', 0x80)
  bytes.writeUInt16LE(0x14c, 0x84); bytes.writeUInt16LE(1, 0x86); bytes.writeUInt16LE(0xe0, 0x94)
  const optional = 0x98, directories = optional + 96, section = optional + 0xe0
  bytes.writeUInt16LE(0x10b, optional); bytes.writeUInt32LE(16, directories - 4)
  bytes.writeUInt32LE(0x1000, directories + 16); bytes.writeUInt32LE(0x100 + payload.length, directories + 20)
  bytes.write('.rsrc', section); bytes.writeUInt32LE(0x100 + payload.length, section + 8)
  bytes.writeUInt32LE(0x1000, section + 12); bytes.writeUInt32LE(rawSize, section + 16); bytes.writeUInt32LE(0x400, section + 20)
  for (const offset of [0x400, 0x420, 0x440]) bytes.writeUInt16LE(1, offset + 14)
  bytes.writeUInt32LE(16, 0x410); bytes.writeUInt32LE(0x80000020, 0x414)
  bytes.writeUInt32LE(1, 0x430); bytes.writeUInt32LE(0x80000040, 0x434)
  bytes.writeUInt32LE(1041, 0x450); bytes.writeUInt32LE(0x60, 0x454)
  bytes.writeUInt32LE(0x1100, 0x460); bytes.writeUInt32LE(payload.length, 0x464); payload.copy(bytes, 0x500)
  return bytes
}
await fs.mkdir(peFolder, { recursive: true })
await fs.writeFile(path.join(peFolder, 'Game.exe'), syntheticPe(peTitle))
const rawWork = (id, title, ref, developer) => ({
  object: 'work', id, display_name: title,
  refs: [{ source: 'vndb', external_id: ref }],
  cover: { url: `https://images.example/matching-fixture/${id}.png` },
  intros: [{ lang: 'zh-Hans', value: `${title}的完整测试简介` }],
  companies: [{ object: 'company', id: '1', display_name: developer, attribution_role: 'developer' }],
  ratings: [{ source: 'vndb', score: 8.6, vote_count: 30 }],
  characters: [{ id: '10', display_name: '测试角色', roster_role: 'main', spoiler: 'none' }],
  titles: [{ title, title_kind: 'official', is_machine: false, lang: 'ja' }], tags: [], release_date: '2004-04-28', release_date_precision: 'day'
})
const works = [rawWork('101', 'CLANNAD', 'v4', 'Key'), rawWork('201', ambiguousName, 'v90101', '测试厂商甲'), rawWork('202', ambiguousName, 'v90102', '测试厂商乙'), rawWork('301', peTitle, 'v90901', 'Whale Island Fixture Studio')]
const packaged = process.argv.includes('--packaged')
const launchOptions = {
  executablePath: path.join(project, packaged ? 'release/win-unpacked/The Order.exe' : 'node_modules/electron/dist/electron.exe'),
  args: packaged ? [] : [project], env: { ...process.env, KGM_DATA_DIR: profile }, timeout: 30000
}
let application
const errors = []
async function installTransport(mode, folder = gamesFolder) {
  await application.evaluate(({ dialog }, input) => {
    dialog.showOpenDialog = async () => ({ canceled: false, filePaths: [input.folder] })
    const state = { mode: input.mode, requests: [], release: null }
    globalThis.__matchingFixture = state
    globalThis.fetch = async (value, init) => {
      const url = new URL(String(value))
      if (url.origin !== 'https://api.nextmoe.dev') throw new Error('Unexpected fixture destination')
      if (new Headers(init?.headers).get('authorization') !== `Bearer ${input.key}`) throw new Error('Missing fixture authentication')
      if (init?.redirect !== 'error') throw new Error('Redirect policy missing')
      state.requests.push(url.pathname + url.search)
      if (state.mode === 'stall' || state.mode === 'pause-refs' && url.searchParams.has('refs')) {
        await new Promise((resolve, reject) => {
          if (init.signal.aborted) { reject(new Error('Fixture cancelled')); return }
          const aborted = () => reject(new Error('Fixture cancelled'))
          init.signal.addEventListener('abort', aborted, { once: true })
          state.release = () => { init.signal.removeEventListener('abort', aborted); state.mode = 'normal'; resolve() }
        })
      }
      let body
      if (url.pathname === '/v2/catalog/search') {
        const query = url.searchParams.get('q') ?? ''
        const matches = query.includes(input.ambiguousName) ? input.works.filter(work => work.display_name === input.ambiguousName)
          : query === input.peTitle ? input.works.filter(work => work.id === '301') : []
        body = { object: 'list', items: matches.map(work => ({ object: 'search_result', target_object: 'work', id: work.id, display_name: work.display_name })) }
      } else if (url.pathname === '/v2/catalog/works') {
        const refs = new Set((url.searchParams.get('refs') ?? '').split(','))
        const ids = new Set((url.searchParams.get('ids') ?? '').split(','))
        body = { object: 'list', items: input.works.filter(work => ids.has(work.id) || work.refs.some(ref => refs.has(`${ref.source}:${ref.external_id}`))), missing: [] }
      } else {
        body = input.works.find(work => url.pathname === `/v2/catalog/works/${work.id}`)
        if (!body) throw new Error('Unexpected fixture API request')
      }
      return new Response(JSON.stringify(body), { headers: { 'content-type': 'application/json' } })
    }
  }, { folder, key: fakeKey, works, ambiguousName, peTitle, mode })
}
async function launch(mode = 'normal') {
  application = await electron.launch(launchOptions)
  await installTransport(mode)
  const page = await application.firstWindow()
  page.on('pageerror', error => errors.push(error.message))
  await page.route('https://images.example/matching-fixture/**', route => route.fulfill({
    contentType: 'image/png', body: Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=', 'base64')
  }))
  await page.waitForFunction(() => window.kunManager !== undefined)
  return page
}
const snapshot = page => page.evaluate(() => window.kunManager.getState())
const fixtureSearchQueries = () => application.evaluate(() => globalThis.__matchingFixture.requests
  .map(value => new URL(value, 'https://api.nextmoe.dev'))
  .filter(url => url.pathname === '/v2/catalog/search')
  .map(url => url.searchParams.get('q')))
async function waitSnapshot(page, predicate, label) {
  const deadline = Date.now() + 15000
  while (true) {
    const state = await snapshot(page)
    if (predicate(state)) return state
    assert.ok(Date.now() < deadline, `${label}: ${JSON.stringify(state.matching)}`)
    await new Promise(resolve => setTimeout(resolve, 30))
  }
}
async function close() { await application.close(); application = undefined }
async function setExecutableMetadata(page, enabled) {
  await page.locator('.sidebar').getByRole('button', { name: /偏好设置/ }).click()
  const setting = page.locator('.setting-line').filter({ hasText: '读取程序内的游戏名称' })
  if (await setting.locator('input[type="checkbox"]').isChecked() !== enabled) await setting.locator('label').click()
  await page.getByRole('button', { name: '保存设置', exact: true }).click()
  await waitSnapshot(page, state => state.settings.readExecutableMetadata === enabled, 'Executable metadata setting was not saved')
  await page.locator('.sidebar nav').getByRole('button', { name: /我的书架/ }).click()
}

try {
  let page = await launch('pause-refs')
  assert.equal((await snapshot(page)).settings.readExecutableMetadata, true)
  const settings = await page.evaluate(key => window.kunManager.saveSettings({ baseUrl: 'https://api.nextmoe.dev/v2/', apiKey: key, includeNsfw: true, theme: 'light' }), fakeKey)
  assert.equal(settings.hasApiKey, true)
  assert.equal('apiKey' in settings, false)
  const stored = await fs.readFile(path.join(profile, 'settings.json'), 'utf8')
  assert.equal(stored.includes(fakeKey), false)
  assert.ok(JSON.parse(stored).encryptedKey)

  // New profiles start enabled. Explicitly save both choices, then keep reading
  // disabled throughout the original import/cancellation/restart regressions.
  await page.locator('.sidebar').getByRole('button', { name: /偏好设置/ }).click()
  const executableSetting = page.locator('.setting-line').filter({ hasText: '读取程序内的游戏名称' })
  const executableSwitch = executableSetting.locator('input[type="checkbox"]')
  assert.equal(await executableSwitch.isChecked(), true)
  await executableSetting.locator('label').click()
  assert.equal(await executableSwitch.isChecked(), false)
  await page.getByRole('button', { name: '保存设置', exact: true }).click()
  await waitSnapshot(page, state => state.settings.readExecutableMetadata === false, 'Initial executable metadata opt-out was not saved')
  await executableSetting.locator('label').click()
  assert.equal(await executableSwitch.isChecked(), true)
  await page.getByRole('button', { name: '保存设置', exact: true }).click()
  await waitSnapshot(page, state => state.settings.readExecutableMetadata === true, 'Executable metadata opt-in was not saved')
  await executableSetting.locator('label').click()
  assert.equal(await executableSwitch.isChecked(), false)
  await page.getByRole('button', { name: '保存设置', exact: true }).click()
  await waitSnapshot(page, state => state.settings.readExecutableMetadata === false, 'Executable metadata opt-out was not saved')
  assert.equal(JSON.parse(await fs.readFile(path.join(profile, 'settings.json'), 'utf8')).readExecutableMetadata, false)
  await page.locator('.sidebar nav').getByRole('button', { name: /我的书架/ }).click()

  await page.getByRole('button', { name: '添加游戏', exact: true }).click()
  await page.getByRole('button', { name: '确认导入 3 部', exact: true }).waitFor()
  const automaticSwitch = page.locator('.import-match-option input[type="checkbox"]')
  assert.equal(await automaticSwitch.isChecked(), true)
  assert.equal(await automaticSwitch.isEnabled(), true)
  await page.getByRole('button', { name: '确认导入 3 部', exact: true }).click()
  await waitSnapshot(page, state => state.games.length === 3 && state.matching?.status === 'running', 'Automatic job did not start')
  await page.getByRole('button', { name: '确认导入 3 部', exact: true }).waitFor({ state: 'hidden' })
  await page.locator('.sidebar nav').getByRole('button', { name: '发现作品', exact: true }).click()
  await page.locator('.matching-banner.running').waitFor()
  await page.locator('.sidebar nav').getByRole('button', { name: /我的书架/ }).click()
  assert.equal((await snapshot(page)).matching.status, 'running')
  assert.equal((await snapshot(page)).settings.readExecutableMetadata, false)
  // Wait until the real client has reached its first exact-ref request before releasing it.
  await waitSnapshot(page, state => state.matching?.currentName !== null, 'Automatic job did not report progress')
  for (let attempt = 0; attempt < 100; attempt++) {
    if (await application.evaluate(() => Boolean(globalThis.__matchingFixture.release))) break
    await new Promise(resolve => setTimeout(resolve, 30))
  }
  assert.equal(await application.evaluate(() => { const release = globalThis.__matchingFixture.release; if (release) release(); return Boolean(release) }), true)
  await waitSnapshot(page, state => state.matching?.status === 'completed', 'Automatic job did not complete')
  let state = await snapshot(page)
  assert.equal(state.settings.readExecutableMetadata, false)
  const clannad = state.games.find(game => game.name === 'CLANNAD')
  const ambiguous = state.games.find(game => game.name === ambiguousName)
  assert.ok(clannad.metadata, JSON.stringify(state.matching))
  assert.equal(clannad.metadata.id, '101')
  assert.ok(clannad.metadata.coverUrl)
  assert.equal(clannad.metadata.description, 'CLANNAD的完整测试简介')
  assert.deepEqual(clannad.metadata.developers, ['Key'])
  assert.equal(clannad.metadata.characters.length, 1)
  assert.equal(state.matching.matched, 1)
  assert.equal(state.matching.reviews.length, 2)
  const refsCalls = await application.evaluate(() => globalThis.__matchingFixture.requests.filter(url => url.includes('refs=')))
  assert.ok(refsCalls.some(url => new URL(url, 'https://api.nextmoe.dev').searchParams.get('refs').split(',').includes('vndb:v4')))
  await page.locator('.maintenance-entry').click()
  await page.locator('.maintenance-review-link').click()
  await page.locator('.review-queue').getByRole('button', { name: ambiguousName, exact: true }).click()
  assert.equal(await page.locator('.review-option').count(), 2)
  await page.locator('.review-option').filter({ hasText: '测试厂商甲' }).click()
  await page.getByRole('button', { name: '确认关联', exact: true }).click()
  await waitSnapshot(page, state => state.games.find(game => game.id === ambiguous.id)?.metadata?.id === '201', 'Manual review did not link selected work')
  state = await snapshot(page)
  assert.equal(state.matching.reviews.length, 1)
  await close()

  page = await launch('stall')
  state = await snapshot(page)
  assert.equal(state.games.length, 3)
  assert.equal(state.settings.hasApiKey, true)
  assert.equal(state.settings.readExecutableMetadata, false)
  assert.equal(state.matching.status, 'completed')
  assert.equal(state.matching.reviews.length, 1)
  assert.equal(state.matching.reviews[0].name, unknownName)
  await page.locator('.maintenance-entry').click()
  await page.getByRole('button', { name: /只补缺失/ }).click()
  await page.keyboard.press('Escape')
  await page.locator('.matching-banner.running').waitFor()
  for (let attempt = 0; attempt < 100; attempt++) {
    if (await application.evaluate(() => globalThis.__matchingFixture.requests.length > 0)) break
    await new Promise(resolve => setTimeout(resolve, 30))
  }
  assert.ok(await application.evaluate(() => globalThis.__matchingFixture.requests.length > 0), 'Cancellation must interrupt an in-flight request')
  await page.locator('.sidebar nav').getByRole('button', { name: '游玩统计', exact: true }).click()
  await page.getByRole('button', { name: '取消补全', exact: true }).click()
  await waitSnapshot(page, state => state.matching?.status === 'cancelled', 'Automatic job did not cancel')
  state = await snapshot(page)
  assert.equal(state.games.length, 3)
  assert.equal(state.settings.readExecutableMetadata, false)
  assert.equal(state.games.filter(game => game.metadata).length, 2)
  assert.equal(state.games.find(game => game.name === unknownName).metadata, null)
  await close()

  page = await launch()
  state = await snapshot(page)
  assert.equal(state.matching.status, 'cancelled')
  assert.equal(state.settings.readExecutableMetadata, false)
  assert.equal(state.games.length, 3)
  assert.equal(state.games.filter(game => game.metadata).length, 2)
  for (const name of ['CLANNAD', ambiguousName, unknownName]) assert.ok((await fs.stat(path.join(gamesFolder, name, `${name}.exe`))).isFile())

  // The synthetic program's full title exists only inside its version resource.
  // Import it with reading off first: its short folder name cannot fetch a match.
  await installTransport('normal', peFolder)
  await page.getByRole('button', { name: '添加游戏', exact: true }).click()
  await page.getByRole('button', { name: '确认导入 1 部', exact: true }).click()
  await page.getByRole('button', { name: '确认导入 1 部', exact: true }).waitFor({ state: 'hidden' })
  await waitSnapshot(page, state => state.games.length === 4 && state.matching?.status === 'completed', 'Disabled synthetic import did not finish')
  state = await snapshot(page)
  const peGame = state.games.find(game => game.installations.some(installation => installation.folderPath === peFolder))
  assert.ok(peGame)
  assert.equal(peGame.metadata, null)
  const disabledQueries = await fixtureSearchQueries()
  assert.ok(disabledQueries.includes(peFolderName), JSON.stringify({ phase: 'disabled', fixtureQueries: disabledQueries }))
  assert.equal(disabledQueries.includes(peTitle), false, 'Disabled reading must not expose the title stored only in the synthetic version resource')

  // Switch on through the real UI, then use normal matching to read and resolve
  // that bounded PE title. No fixture program is ever launched.
  await setExecutableMetadata(page, true)
  await page.locator('.maintenance-entry').click()
  await page.getByRole('button', { name: /只补缺失/ }).click()
  await page.keyboard.press('Escape')
  await waitSnapshot(page, state => state.matching?.status === 'completed' && state.games.find(game => game.id === peGame.id)?.metadata?.id === '301', 'Enabled synthetic PE metadata was not matched')
  state = await snapshot(page)
  const identified = state.games.find(game => game.id === peGame.id)
  assert.equal(identified.metadata.title, peTitle)
  assert.equal(identified.playCount, 0)
  assert.equal(state.activeSession, null)
  const enabledQueries = (await fixtureSearchQueries()).slice(disabledQueries.length)
  // Only synthetic fixture queries are printed; headers and credentials stay private.
  console.log(JSON.stringify({ syntheticPeQueries: { disabled: disabledQueries, enabled: enabledQueries } }))
  assert.ok(enabledQueries.includes(peTitle), JSON.stringify({ phase: 'enabled', fixtureQueries: enabledQueries }))
  await setExecutableMetadata(page, false)
  assert.equal(JSON.parse(await fs.readFile(path.join(profile, 'settings.json'), 'utf8')).readExecutableMetadata, false)
  assert.ok((await fs.stat(path.join(peFolder, 'Game.exe'))).isFile())
  assert.deepEqual(errors, [])
  console.log(JSON.stringify({ packaged, executableMetadataDefaultOn: true, executableMetadataSettingRoundtrip: true, explicitOptOutPreserved: true, syntheticPeRecognitionEnabled: true, syntheticPeRecognitionDisabled: true, automaticImportDefault: true, realClientParsing: true, exactRefMatched: true, fullMetadataSaved: true, manualReviewConfirmed: true, pageChangesRetainProgress: true, cancelledJobPreservesGames: true, restartedStatePreserved: true, rendererErrors: errors.length }))
} finally {
  if (application) await close()
  if (path.dirname(path.resolve(temporary)) !== temporaryRoot || !path.basename(temporary).startsWith('kgm-desktop-matching-')) throw new Error('Unexpected test directory')
  await fs.rm(temporary, { recursive: true, force: true })
}
