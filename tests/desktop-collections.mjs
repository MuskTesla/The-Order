import assert from 'node:assert/strict'
import { promises as fs } from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { createHash } from 'node:crypto'
import { execFileSync } from 'node:child_process'
import { _electron as electron } from '../node_modules/playwright/index.mjs'

const project = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const baseline = process.argv.includes('--baseline')
const phase = baseline ? 'before' : 'after'
const output = path.join(project, '.artifacts/overnight-2026-10-04/round-0', phase)
const temporaryRoot = path.resolve(os.tmpdir())
const temporary = await fs.mkdtemp(path.join(temporaryRoot, 'the-order-collections-'))
const profile = path.join(temporary, 'profile')
const date = '2026-10-04T00:00:00.000Z'
const longCategory = '遥远海岸与那些没有忘记的夏日约定'.repeat(4)
const names = ['合成长标题 · ' + '当星光再次照亮那座没有名字的小镇时我们仍记得夏日约定与遥远海岸的回声'.repeat(3), '合成第二部', '合成缺图', '合成坏图', '合成旧作', 'SyntheticLongUnbrokenTitle'.repeat(5), '合成新作', '合成未分类']
const labels = [[longCategory, '周末慢读'], ['周末慢读', '短篇'], ['短篇', '重读'], ['番外'], ['海外'], ['EnglishCategoryWithoutSpaces'.repeat(4)], ['近年'], []]
const games = names.map((name, index) => ({
  id: `collection-${index}`, name, nameIsCustom: true, metadata: {
    id: `99400${index}`, title: name, originalTitle: name, aliases: index === 0 ? ['Sea Breeze'] : [],
    coverUrl: index === 2 ? null : `https://collections.invalid/${index === 3 ? 'broken' : `cover-${index}`}.svg`,
    bannerUrl: null, description: '隔离的虚构条目与原创测试图片。', releaseDate: null, releaseDatePrecision: null,
    developers: ['合成制作室'], tags: [], ratings: [], characters: [], references: [], webUrl: null, contentRating: null, fetchedAt: date,
  }, installations: [], preferredInstallationId: null, status: index === 0 ? 'playing' : index === 1 ? 'completed' : 'planned',
  personalRating: index === 0 ? 0 : null, favorite: index < 2, categories: labels[index],
  customTags: index === 0 ? Array.from({ length: 95 }, (_, tag) => `标签${String(tag + 1).padStart(3, '0')}`) : [],
  notes: `Saved synthetic note ${index}`, totalPlaySeconds: 0, playCount: 0, sessions: [],
  addedAt: `2026-10-0${index % 4 + 1}T00:00:00.000Z`, lastPlayedAt: null,
}))
const library = { format: 'kun-gal-manager', version: 1, exportedAt: date, games }
const sha = bytes => createHash('sha256').update(bytes).digest('hex')
const conditions = [{ width: 1440, height: 940, zoom: 1 }, { width: 1000, height: 700, zoom: 1 }, { width: 1440, height: 940, zoom: 1.5 }, { width: 1000, height: 700, zoom: 1.5 }]
const report = { phase, sourceCommit: execFileSync('git', ['rev-parse', 'HEAD'], { cwd: project, encoding: 'utf8' }).trim(), sourceDirty: Boolean(execFileSync('git', ['status', '--porcelain'], { cwd: project, encoding: 'utf8' }).trim()), fixtureSha256: sha(JSON.stringify(library)), sourceHashes: {}, runtimeHashes: {}, measurements: [], screenshots: [], checks: [], knownBaselineFailures: [], rendererErrors: [], syntheticOnly: true, realApiCalls: 0, realGameExecutions: 0, formalProfileAccessed: false }
const env = { ...process.env, KGM_DATA_DIR: profile }
delete env.KGM_TEST_EXECUTABLE
delete env.KGM_DEV_URL
let application, page
async function navigate(index) { await page.locator('.sidebar nav>.nav-item').nth(index).click() }
async function resize(condition) {
  await application.evaluate(({ BrowserWindow }, value) => {
    const windows = BrowserWindow.getAllWindows()
    if (windows.length !== 1) throw Error('Expected exactly one app window')
    const win = windows[0]
    if (win.isMaximized()) win.unmaximize()
    win.setContentSize(value.width, value.height)
    win.webContents.setZoomFactor(value.zoom)
  }, condition)
  await page.waitForFunction(value => Math.abs(innerWidth - value.width / value.zoom) < 2 && Math.abs(innerHeight - value.height / value.zoom) < 2, condition)
  await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))))
}
async function capture(label, condition, save = true, scrollTarget = null) {
  const measurement = await page.evaluate(() => ({
    viewport: { width: innerWidth, height: innerHeight, dpr: devicePixelRatio },
    documentWidth: document.documentElement.scrollWidth, documentClientWidth: document.documentElement.clientWidth,
    heading: document.querySelector('main h1')?.textContent,
    cards: [...document.querySelectorAll('.collection-card')].map(card => ({ width: card.getBoundingClientRect().width, right: card.getBoundingClientRect().right, scrollWidth: card.scrollWidth, clientWidth: card.clientWidth })),
  }))
  assert.ok(Math.abs(measurement.viewport.width - condition.width / condition.zoom) < 2, 'Actual viewport must match requested condition')
  assert.equal(measurement.heading, '分类收藏')
  report.measurements.push({ label, requested: condition, ...measurement })
  const overflow = measurement.documentWidth > measurement.viewport.width + 1
  if (baseline) { if (overflow) report.knownBaselineFailures.push({ label, reason: 'horizontal-overflow', ...measurement }) }
  else {
    assert.equal(overflow, false, label + ': page overflow')
    for (const card of measurement.cards) {
      assert.ok(card.right <= measurement.viewport.width + 1, label + ': card bounds')
      assert.ok(card.scrollWidth <= card.clientWidth + 1, label + ': card overflow')
    }
  }
  if (save) {
    if (scrollTarget) await page.locator(scrollTarget).first().scrollIntoViewIfNeeded()
    else await page.evaluate(() => window.scrollTo(0, 0))
    await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))))
    const encoded = await application.evaluate(async ({ BrowserWindow }) => {
      const windows = BrowserWindow.getAllWindows()
      if (windows.length !== 1) throw Error('Expected one window for native capture')
      return (await windows[0].webContents.capturePage()).toPNG().toString('base64')
    })
    const bytes = Buffer.from(encoded, 'base64')
    assert.ok(Math.abs(bytes.readUInt32BE(16) - condition.width * measurement.viewport.dpr / condition.zoom) < 4, 'Native image width must cover the actual window')
    assert.ok(Math.abs(bytes.readUInt32BE(20) - condition.height * measurement.viewport.dpr / condition.zoom) < 4, 'Native image height must cover the actual window')
    const file = `${label}-${condition.width}x${condition.height}-zoom${condition.zoom}.png`
    await fs.writeFile(path.join(output, file), bytes)
    report.screenshots.push({ file, requested: condition, actual: measurement.viewport, scrollTarget, pixelWidth: bytes.readUInt32BE(16), pixelHeight: bytes.readUInt32BE(20), sha256: sha(bytes) })
  }
}
try {
  await fs.mkdir(output, { recursive: true })
  await fs.mkdir(profile, { recursive: true })
  await fs.writeFile(path.join(profile, 'library.json'), JSON.stringify(library))
  if (!baseline) for (const file of ['src/App.vue', 'src/components/LibraryCollections.vue', 'src/library-collections.ts']) report.sourceHashes[file] = sha((await fs.readFile(path.join(project, file), 'utf8')).replace(/\r\n/g, '\n'))
  const baselineProgram = path.join(project, '.artifacts/iteration-03/candidate/win-unpacked/The Order.exe')
  if (baseline) {
    const receipt = JSON.parse(await fs.readFile(path.join(project, '.artifacts/iteration-03/package-check.json'), 'utf8'))
    report.baselineAsarSha256 = sha(await fs.readFile(path.join(path.dirname(baselineProgram), 'resources/app.asar')))
    assert.equal(report.baselineAsarSha256, '867f6175fdcf63a17a70aaac4b9f89ec56bf6eb711f57f8e377c11cc776a0274')
    report.businessSourceCommit = receipt.sourceCommit
  }
  application = await electron.launch({ executablePath: baseline ? baselineProgram : path.join(project, 'node_modules/electron/dist/electron.exe'), args: baseline ? [] : [project], env, timeout: 30000 })
  report.runtimeHashes = await application.evaluate(({ app }) => {
    const fs = process.getBuiltinModule('node:fs'), path = process.getBuiltinModule('node:path'), crypto = process.getBuiltinModule('node:crypto')
    const root = app.getAppPath(), html = fs.readFileSync(path.join(root, 'dist/index.html'), 'utf8')
    const files = ['dist/index.html', 'dist-electron/main.cjs', 'dist-electron/preload.cjs', ...[...html.matchAll(/(?:src|href)="([^"]+\.(?:js|css))"/g)].map(match => path.posix.normalize('dist/' + match[1].replace(/^\//, '')))]
    return Object.fromEntries(files.map(file => [file, crypto.createHash('sha256').update(fs.readFileSync(path.join(root, file))).digest('hex')]))
  })
  if (baseline) {
    const receipt = JSON.parse(await fs.readFile(path.join(project, '.artifacts/iteration-03/package-check.json'), 'utf8'))
    for (const expected of receipt.matchedFiles) assert.equal(report.runtimeHashes[expected.file], expected.sha256, 'Baseline must run the saved sleep-time code')
  }
  await application.evaluate(({ session }, root) => {
    const fs = process.getBuiltinModule('node:fs'), path = process.getBuiltinModule('node:path')
    session.defaultSession.protocol.handle('https', request => {
      const url = new URL(request.url)
      if (url.hostname !== 'collections.invalid' || !/^\/cover-[0-7]\.svg$/.test(url.pathname)) return new Response('Synthetic unavailable image', { status: 404 })
      return new Response(fs.readFileSync(path.join(root, 'tests/ui-fixtures', path.basename(url.pathname))), { headers: { 'content-type': 'image/svg+xml' } })
    })
    globalThis.fetch = () => { throw Error('Unexpected real catalogue request in collection test') }
  }, project)
  page = await application.firstWindow()
  page.on('pageerror', error => report.rendererErrors.push(error.message))
  await page.reload()
  await page.emulateMedia({ reducedMotion: 'reduce' })
  await page.locator('.library-card').first().waitFor()
  const initial = await page.evaluate(() => window.kunManager.getState())
  assert.equal(path.resolve(initial.dataPath), profile)
  assert.equal(initial.games.length, 8)
  await navigate(2)
  for (const condition of conditions) {
    await resize(condition)
    await capture('collections-light', condition)
    if (!baseline) for (const name of ['我的书架', '发现作品', '分类收藏', '游玩统计', '资料整理', '偏好设置']) assert.equal(await page.locator('.sidebar').getByRole('button', { name, exact: true }).count(), 1, name + ': compact navigation keeps its name')
  }
  await resize(conditions[0])
  if (!baseline) {
    await page.waitForFunction(() => [...document.querySelectorAll('.collection-art img')].every(image => image.complete))
    assert.ok(await page.locator('.collection-art img').count() > 0)
    assert.ok(await page.locator('.collection-art .detail-art-placeholder').count() > 0, 'Missing and broken covers remain explicit')
    const tops = await page.locator('.collection-card').evaluateAll(cards => cards.map(card => ({ row: card.getBoundingClientRect().top, art: card.querySelector('.collection-art').getBoundingClientRect().top })))
    for (const card of tops) assert.ok(Math.abs(card.row - card.art) < 2, 'Cover previews must align to the top regardless of title length')
    const query = page.getByRole('textbox', { name: '搜索分类或所含作品', exact: true })
    await query.fill('sea BREEZE')
    assert.equal(await page.locator('.collection-card').count(), 2)
    await query.fill('没有任何匹配的分类')
    await page.getByRole('heading', { name: '没有找到这个分类' }).waitFor()
    await capture('collections-no-result', conditions[0])
    await page.getByRole('button', { name: '清空搜索', exact: true }).click()
    await page.getByRole('combobox', { name: '分类排序', exact: true }).selectOption('count')
    assert.match(await page.locator('.collection-card').first().innerText(), /2 部作品/)
    const category = page.getByRole('button', { name: `打开分类：${longCategory}，1部作品`, exact: true })
    await category.focus()
    await page.keyboard.press('Enter')
    assert.equal(await page.locator('.library-card').count(), 1)
    assert.equal(await page.locator('.card-title').innerText(), names[0])
    await navigate(2)
    const tagSearch = page.getByRole('textbox', { name: '搜索标签', exact: true })
    await tagSearch.fill('标签095')
    assert.equal(await page.locator('.tag-cloud button').count(), 1)
    await page.getByRole('button', { name: '标签095', exact: true }).focus()
    await page.keyboard.press('Enter')
    assert.equal(await page.locator('.library-card').count(), 1, 'The 95th tag opens the exact shelf')
    await navigate(2)
    await page.getByRole('button', { name: '查看特别喜欢，2部作品', exact: true }).click()
    assert.equal(await page.locator('.library-card').count(), 2)
    await navigate(2)
    await page.getByRole('button', { name: '批量整理书架', exact: true }).click()
    assert.equal(await page.locator('.library-card').count(), 8)
    await page.getByRole('button', { name: '取消整理', exact: true }).click()
    const latest = await page.evaluate(() => window.kunManager.getState())
    assert.deepEqual(latest.games, initial.games, 'Collection browsing must never alter personal fields or data')
    await page.evaluate(async settings => window.kunManager.saveSettings({ ...settings, theme: 'dark' }), initial.settings)
    await navigate(2)
    await page.waitForFunction(() => document.documentElement.classList.contains('kun-dark-mode'))
    await resize(conditions[3])
    await capture('collections-dark', conditions[3])
    await capture('collections-dark-grid', conditions[3], true, '.collection-card')
    await page.evaluate(async settings => window.kunManager.saveSettings({ ...settings, theme: 'light' }), initial.settings)
    await page.waitForFunction(() => !document.documentElement.classList.contains('kun-dark-mode'))
    await capture('collections-light-grid', conditions[3], true, '.collection-card')
    await page.locator('.sidebar').getByRole('button', { name: '资料整理', exact: true }).click()
    await page.locator('.maintenance-modal').waitFor()
    await page.keyboard.press('Escape')
    await page.locator('.maintenance-modal').waitFor({ state: 'hidden' })
    await page.locator('.sidebar').getByRole('button', { name: '偏好设置', exact: true }).focus()
    await page.keyboard.press('Enter')
    await page.getByRole('heading', { name: '偏好设置', exact: true }).waitFor()
    await page.evaluate(async ids => { for (const id of ids) await window.kunManager.removeGame(id) }, initial.games.map(game => game.id))
    await navigate(2)
    await page.getByRole('heading', { name: '给故事留一个自己的位置', exact: true }).waitFor()
    assert.equal(await page.locator('.collection-card').count(), 0)
    assert.equal(await page.getByRole('button', { name: '查看特别喜欢，0部作品', exact: true }).count(), 1)
    assert.equal(await page.locator('.tag-section').count(), 0)
    assert.equal(await page.getByRole('button', { name: '批量整理书架', exact: true }).count(), 0)
    assert.doesNotMatch(await page.locator('.collections-empty').innerText(), /上方.*批量整理/, 'Empty library must not point to an unavailable bulk action')
    await capture('collections-empty', conditions[3], true, '.collections-empty')
    report.checks.push('Search names and aliases; unmatched recovery; stable sorting; keyboard category and tag95 navigation; favorites; batch entry; unchanged library before isolated empty-state fixture; cover success/missing/failure; aligned previews; dark/light 150% scrolled cards; maintenance Escape and named settings keyboard entry; real IPC deletion of synthetic fixtures and empty library')
  } else report.checks.push('Sleep-time code and identical fixture captured; actual dimensions checked; overflow recorded as existing failure')
  assert.deepEqual(report.rendererErrors, [])
  report.status = 'passed'
} catch (error) { report.status = 'failed'; report.error = error.stack; throw error }
finally {
  await fs.writeFile(path.join(output, 'manifest.json'), JSON.stringify(report, null, 2))
  if (application) await application.close()
  if (path.dirname(path.resolve(temporary)) !== temporaryRoot || !path.basename(temporary).startsWith('the-order-collections-')) throw Error('Unexpected test cleanup directory')
  await fs.rm(temporary, { recursive: true, force: true })
}
console.log(JSON.stringify({ phase, status: report.status, screenshots: report.screenshots.length, baselineFailures: report.knownBaselineFailures.length, rendererErrors: report.rendererErrors }))
