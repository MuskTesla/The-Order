import assert from 'node:assert/strict'
import { promises as fs } from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { _electron as electron } from '../node_modules/playwright/index.mjs'

const project = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const temporary = await fs.mkdtemp(path.join(os.tmpdir(), 'the-order-workspace-'))
const profile = path.join(temporary, 'profile')
const packaged = process.argv.includes('--packaged')
const now = new Date(), endedAt = new Date(now.getTime() - 3600000).toISOString()
const startedAt = new Date(now.getTime() - 4500000).toISOString()
const names = ['海岸来信', '双版本的故事', '尚未安装的约定', '等待资料的小镇', '雨中的车站', '下一次相遇']
const games = names.map((name, index) => ({
  id: `workspace-${index}`, name, nameIsCustom: true,
  metadata: index === 3 ? null : {
    id: `work-${index}`, title: `原始标题 ${index}`, originalTitle: `Original ${index}`, aliases: [`别名${index}`],
    coverUrl: 'https://workspace-fixture.invalid/cover.png', bannerUrl: null, description: '虚构的端到端测试作品。',
    releaseDate: null, releaseDatePrecision: null, developers: ['海风工房'], tags: ['日常'], ratings: [],
    characters: [], references: [], webUrl: null, contentRating: '全年龄', fetchedAt: now.toISOString(),
  },
  installations: [2, 5].includes(index) ? [] : [{
    id: `installation-${index}`, folderPath: path.join(temporary, `game-${index}`),
    executablePath: [1, 3].includes(index) ? null : path.join(temporary, `game-${index}`, 'Game.exe'), arguments: ['--preserve'],
  }],
  preferredInstallationId: [1, 2, 5].includes(index) ? null : `installation-${index}`,
  status: ['playing', 'completed', 'planned', 'planned', 'paused', 'planned'][index],
  personalRating: index === 0 ? 0 : null, favorite: index === 0, categories: ['原分类'], customTags: ['原标签'], notes: `已保存笔记 ${index}`,
  totalPlaySeconds: index === 0 ? 900 : index === 4 ? 3600 : 0, playCount: index === 0 ? 1 : 0,
  sessions: index === 0 ? [{ id: 'saved-session', startedAt, endedAt, seconds: 900 }] : [],
  addedAt: new Date(now.getTime() - index * 86400000).toISOString(), lastPlayedAt: index === 0 ? endedAt : null,
}))
games[1].installations.push({ id: 'installation-1-b', folderPath: path.join(temporary, 'game-1-b'), executablePath: path.join(temporary, 'game-1-b', 'Game.exe'), arguments: ['--second'] })
await fs.mkdir(profile, { recursive: true })
for (const game of games) for (const installation of game.installations) {
  await fs.mkdir(installation.folderPath, { recursive: true })
  for (const file of ['Game.exe', 'Chosen.exe']) await fs.writeFile(path.join(installation.folderPath, file), 'Inert fixture: never read as game metadata or execute.')
}
await fs.writeFile(path.join(profile, 'library.json'), JSON.stringify({ format: 'kun-gal-manager', version: 1, exportedAt: now.toISOString(), games }))
const launchOptions = {
  executablePath: path.join(project, packaged ? 'release/win-unpacked/The Order.exe' : 'node_modules/electron/dist/electron.exe'),
  args: packaged ? [] : [project], env: { ...process.env, KGM_DATA_DIR: profile }, timeout: 30000,
}
let application
const rendererErrors = []
async function start() {
  application = await electron.launch(launchOptions)
  await application.evaluate(({ dialog, session }) => {
    dialog.showOpenDialog = async (_window, options) => ({ canceled: false, filePaths: [process.getBuiltinModule('node:path').join(options.defaultPath, 'Chosen.exe')] })
    session.defaultSession.webRequest.onBeforeRequest({ urls: ['http://*/*', 'https://*/*'] }, (_details, callback) => callback({ cancel: true }))
  })
  const page = await application.firstWindow()
  page.on('pageerror', error => rendererErrors.push(error.message))
  await page.emulateMedia({ reducedMotion: 'reduce' })
  await page.locator('.library-card').nth(5).waitFor()
  return page
}
async function state(page) { return page.evaluate(() => window.kunManager.getState()) }
async function waitState(page, predicate) {
  const deadline = Date.now() + 10000
  while (Date.now() < deadline) { const value = await state(page); if (predicate(value)) return value; await new Promise(resolve => setTimeout(resolve, 30)) }
  throw new Error('Workspace transition did not persist')
}
const title = (page, name) => page.locator('.library-card').filter({ has: page.locator('.card-title', { hasText: name }) })
const smart = (page, label) => page.getByRole('group', { name: '书架快捷视图' }).getByRole('button', { name: new RegExp(`^${label}`) })
try {
  let page = await start()
  const search = page.getByRole('textbox', { name: '搜索书架', exact: true })
  for (const [label, count] of [['全部', 6], ['本地游戏', 4], ['想玩清单', 2], ['待补资料', 1], ['待设启动', 2]]) {
    assert.equal(await smart(page, label).locator('small').innerText(), String(count))
    await smart(page, label).click()
    assert.equal(await page.locator('.library-card').count(), count)
  }
  await smart(page, '全部').click()
  await search.fill('别名0 海风 日常')
  assert.equal(await page.locator('.library-card').count(), 1, 'AND search must match different fields')
  assert.equal(await page.locator('.card-title').innerText(), names[0])
  await search.fill('')
  await page.getByRole('button', { name: '筛选', exact: true }).click()
  await page.locator('.expanded-filters select').nth(2).selectOption('0')
  assert.equal(await page.locator('.library-card').count(), 1, 'A personal score of zero is a real rating')
  await page.locator('.expanded-filters select').nth(2).selectOption('')
  await page.getByRole('button', { name: '筛选', exact: true }).click()

  // Hidden selections are removed before the real renderer -> preload -> store bulk transaction.
  await page.getByRole('button', { name: '选择整理', exact: true }).click()
  await page.getByRole('button', { name: '全选当前结果', exact: true }).click()
  assert.equal(await page.getByRole('checkbox', { checked: true }).count(), 6)
  await search.fill(names[0])
  assert.match(await page.locator('.selection-toolbar strong').innerText(), /已选择 1 部/)
  await page.getByRole('combobox', { name: '批量游玩状态' }).selectOption('completed')
  await page.getByRole('combobox', { name: '批量收藏' }).selectOption('yes')
  await page.getByRole('textbox', { name: '批量分类' }).fill('周末，珍藏,周末')
  await page.getByRole('textbox', { name: '批量个人标签' }).fill('慢慢读')
  await page.getByRole('button', { name: '应用到 1 部', exact: true }).click()
  const organized = await waitState(page, value => value.games[0].categories.includes('周末'))
  assert.deepEqual(organized.games[0].categories, ['原分类', '周末', '珍藏'])
  assert.deepEqual(organized.games[0].customTags, ['原标签', '慢慢读'])
  assert.equal(organized.games[0].status, 'completed')
  assert.equal(organized.games[0].personalRating, 0)
  assert.deepEqual(organized.games[0].sessions, games[0].sessions)
  assert.equal(organized.games[0].notes, games[0].notes)
  assert.equal(organized.games[0].totalPlaySeconds, 900)
  for (let index = 1; index < 6; index++) assert.deepEqual(organized.games[index], games[index], 'Hidden games remain unchanged')
  await page.locator('.selection-toolbar').waitFor({ state: 'hidden' })
  await search.fill('')
  await page.getByRole('button', { name: '选择整理', exact: true }).click()
  await title(page, names[0]).getByRole('checkbox').click()
  await page.getByRole('combobox', { name: '分类操作' }).selectOption('remove')
  await page.getByRole('textbox', { name: '批量分类' }).fill('周末')
  await page.getByRole('button', { name: '应用到 1 部', exact: true }).click()
  await waitState(page, value => !value.games[0].categories.includes('周末'))
  await page.locator('.selection-toolbar').waitFor({ state: 'hidden' })
  const beforeInvalid = (await state(page)).games
  const rejected = await page.evaluate(async () => {
    try { await window.kunManager.updateGames(['workspace-0', 'missing-game'], { favorite: false }); return false } catch { return true }
  })
  assert.equal(rejected, true)
  assert.deepEqual((await state(page)).games, beforeInvalid, 'An invalid batch changes nothing')

  // A game with multiple installations and no preference can choose its first program.
  await title(page, names[1]).getByRole('button', { name: `为 ${names[1]} 选择启动程序`, exact: true }).click()
  await page.locator('.manage-form').waitFor()
  await page.getByRole('button', { name: '选择程序', exact: true }).click()
  const firstConfigured = await waitState(page, value => value.games[1].preferredInstallationId === 'installation-1')
  assert.equal(firstConfigured.games[1].installations[0].executablePath, path.join(temporary, 'game-1', 'Chosen.exe'))
  assert.equal(firstConfigured.games[1].installations[1].executablePath, games[1].installations[1].executablePath)
  await page.locator('.installation-row').nth(1).getByRole('button', { name: '设置程序', exact: true }).click()
  const secondConfigured = await waitState(page, value => value.games[1].preferredInstallationId === 'installation-1-b')
  assert.equal(secondConfigured.games[1].installations[1].executablePath, path.join(temporary, 'game-1-b', 'Chosen.exe'))
  assert.deepEqual(secondConfigured.games[1].installations[1].arguments, ['--second'])
  await page.keyboard.press('Escape')
  await page.locator('.kun-modal-panel').waitFor({ state: 'hidden' })

  await page.getByRole('button', { name: '列表视图', exact: true }).click()
  assert.equal(await page.locator('.game-list .card-list').count(), 6)
  await page.getByRole('combobox', { name: '游戏排序' }).selectOption('playtime')
  assert.equal(await page.locator('.card-title').first().innerText(), names[4])
  await page.locator('.sidebar').getByRole('button', { name: /游玩统计/ }).click()
  await page.locator('.play-statistics').waitFor()
  assert.equal(await page.getByRole('group', { name: '每日游玩时长' }).getByRole('button').count(), 14)
  assert.match(await page.locator('.statistics-summary .summary-tile').first().innerText(), /1 小时 15 分/)
  assert.match(await page.locator('.chart-note').innerText(), /另有 1 小时/)
  await page.locator('.day-column').last().click()
  assert.equal(await page.locator('.day-column').last().getAttribute('aria-pressed'), 'true')
  await page.locator('.most-played').getByRole('button', { name: `查看 ${names[4]}`, exact: true }).click()
  await page.locator('.kun-modal-panel').waitFor()
  await page.keyboard.press('Escape')
  await page.locator('.kun-modal-panel').waitFor({ state: 'hidden' })
  await page.keyboard.press('Control+k')
  await page.locator('.library-card').first().waitFor()
  assert.equal(await search.evaluate(element => element === document.activeElement), true)

  await page.evaluate(async () => { const api = window.kunManager; await api.saveSettings({ ...(await api.getState()).settings, theme: 'system', readExecutableMetadata: false }) })
  await page.emulateMedia({ colorScheme: 'dark' })
  await page.waitForFunction(() => document.documentElement.classList.contains('kun-dark-mode'))
  await page.emulateMedia({ colorScheme: 'light' })
  await page.waitForFunction(() => !document.documentElement.classList.contains('kun-dark-mode'))
  const layout = await page.evaluate(() => ({ width: innerWidth, content: document.documentElement.scrollWidth, nestedButtons: document.querySelectorAll('button button').length }))
  assert.ok(layout.content <= layout.width + 1)
  assert.equal(layout.nestedButtons, 0)
  await application.close()
  application = null
  page = await start()
  assert.equal(await page.getByRole('button', { name: '列表视图', exact: true }).getAttribute('aria-pressed'), 'true')
  assert.equal(await page.getByRole('combobox', { name: '游戏排序' }).inputValue(), 'playtime')
  const persisted = await state(page)
  assert.deepEqual(persisted.games, secondConfigured.games)
  assert.equal(persisted.settings.hasApiKey, false)
  assert.equal(rendererErrors.length, 0, rendererErrors.join('\n'))
  console.log(JSON.stringify({ status: 'passed', packaged, smartViews: 5, crossFieldSearch: true, scoreZero: true, visibleSelectionOnly: true, atomicBulk: true, relativeLabels: true, multiInstallationPicker: true, fourteenDayStatistics: true, systemThemeLive: true, keyboardSearch: true, restartPreserved: true, rendererErrors: 0, networkAllowed: 0, gameExecutions: 0 }))
} finally {
  if (application) await application.close()
  if (path.dirname(temporary) !== path.resolve(os.tmpdir()) || !path.basename(temporary).startsWith('the-order-workspace-')) throw new Error('Unexpected workspace fixture directory')
  await fs.rm(temporary, { recursive: true, force: true })
}
