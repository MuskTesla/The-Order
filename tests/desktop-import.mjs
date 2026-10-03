import assert from 'node:assert/strict'
import { promises as fs } from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { _electron as electron } from '../node_modules/playwright/index.mjs'

const project = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const temporary = await fs.mkdtemp(path.join(os.tmpdir(), 'kgm-desktop-import-'))
const profile = path.join(temporary, 'profile')
const gamesFolder = path.join(temporary, '游戏测试目录 with spaces')
await fs.mkdir(gamesFolder, { recursive: true })
for (let i = 1; i <= 37; i++) {
  const gameDirectory = path.join(gamesFolder, `测试游戏 ${String(i).padStart(2, '0')}`)
  await fs.mkdir(gameDirectory)
  await fs.writeFile(path.join(gameDirectory, 'Game.exe'), 'Import fixture only; this file is never executed.')
}
const packaged = process.argv.includes('--packaged')
const launchOptions = {
  executablePath: path.join(project, packaged ? 'release/win-unpacked/The Order.exe' : 'node_modules/electron/dist/electron.exe'),
  args: packaged ? [] : [project],
  env: { ...process.env, KGM_DATA_DIR: profile },
  timeout: 30000
}
let application
const errors = []
try {
  application = await electron.launch(launchOptions)
  // Replace only the native folder picker; scan, UI selection, IPC and persistence remain real.
  await application.evaluate(({ dialog }, folder) => {
    dialog.showOpenDialog = async () => ({ canceled: false, filePaths: [folder] })
  }, gamesFolder)
  const page = await application.firstWindow()
  page.on('pageerror', error => errors.push(error.message))
  await page.waitForFunction(() => window.kunManager !== undefined)
  assert.equal((await page.evaluate(() => window.kunManager.getState())).settings.readExecutableMetadata, true)
  await page.locator('.sidebar').getByRole('button', { name: /偏好设置/ }).click()
  const executableSetting = page.locator('.setting-line, .preferences-option').filter({ hasText: '读取程序内的游戏名称' })
  assert.equal(await executableSetting.locator('input[type="checkbox"]').isChecked(), true)
  await executableSetting.locator('label').click()
  await page.getByRole('button', { name: /^保存(?:全部)?设置$/ }).click()
  for (let attempt = 0; attempt < 100; attempt++) {
    if ((await page.evaluate(() => window.kunManager.getState())).settings.readExecutableMetadata === false) break
    await new Promise(resolve => setTimeout(resolve, 30))
  }
  assert.equal((await page.evaluate(() => window.kunManager.getState())).settings.readExecutableMetadata, false)
  await page.locator('.sidebar nav').getByRole('button', { name: /我的书架/ }).click()
  await page.getByRole('button', { name: '添加游戏', exact: true }).click()
  await page.getByRole('button', { name: '确认导入 37 部', exact: true }).waitFor()
  if (await page.locator('.scan-views').count()) {
    assert.equal(await page.getByRole('button', { name: '可导入 37', exact: true }).count(), 1)
    assert.equal(await page.getByRole('button', { name: '已在书架 0', exact: true }).count(), 1)
  } else {
    assert.match(await page.locator('.scanning-summary').innerText(), /37\s*部可导入/)
    assert.match(await page.locator('.scanning-summary').innerText(), /0\s*部已在书架/)
  }
  assert.equal(await page.locator('.scan-list input:checked').count(), 37)
  await page.getByRole('button', { name: '清空选择', exact: true }).click()
  assert.equal(await page.locator('.scan-list input:checked').count(), 0)
  await page.getByRole('button', { name: '全选新游戏', exact: true }).click()
  await page.locator('.scan-list input').nth(0).uncheck()
  assert.equal(await page.locator('.scan-list input:checked').count(), 36)
  await page.locator('.scan-list input').nth(0).check()
  await page.getByRole('button', { name: '确认导入 37 部', exact: true }).click()
  await page.waitForFunction(() =>
    document.body.textContent.includes('An object could not be cloned') || document.querySelector('.heading-count')?.textContent.trim() === '37',
  undefined, { timeout: 10000 })
  const afterImport = await page.evaluate(() => window.kunManager.getState())
  assert.equal(afterImport.settings.readExecutableMetadata, false)
  assert.equal(afterImport.games.length, 37, `UI import must cross the desktop bridge and persist all selected games; UI: ${(await page.locator('body').innerText()).slice(-1200)}`)
  await page.getByRole('button', { name: '确认导入 37 部', exact: true }).waitFor({ state: 'hidden' })
  assert.equal(await page.locator('.game-card').count(), 37)

  const firstGame = afterImport.games[0]
  const firstCard = page.locator('.game-card').filter({ has: page.getByRole('button', { name: firstGame.name, exact: true }) })
  assert.equal(await firstCard.getByRole('button', { name: `启动 ${firstGame.name}`, exact: true }).isEnabled(), true)
  await firstCard.getByRole('button', { name: `收藏游戏 ${firstGame.name}`, exact: true }).click()
  await firstCard.getByRole('button', { name: `取消收藏 ${firstGame.name}`, exact: true }).waitFor()
  await page.evaluate(id => window.kunManager.updateGame(id, { notes: 'Keep synthetic personal notes', personalRating: 8.5, categories: ['Preserved category'], customTags: ['Preserved tag'] }), firstGame.id)
  const beforeDuplicate = await page.evaluate(() => window.kunManager.getState())

  // Existing rows are visible but never selectable; the real backend also
  // rejects duplicate additions if a caller sends their scanner-issued IDs.
  await page.getByRole('button', { name: '添加游戏', exact: true }).click()
  const confirmEmpty = page.getByRole('button', { name: '确认导入 0 部', exact: true })
  await confirmEmpty.waitFor()
  assert.equal(await confirmEmpty.isDisabled(), true)
  assert.equal(await page.locator('.scan-list input:checked').count(), 0)
  assert.equal(await page.locator('.scan-list input:disabled').count(), 37)
  assert.equal(await page.locator('.scan-list .scan-known').count(), 37)
  if (await page.locator('.scan-views').count()) {
    assert.equal(await page.getByRole('button', { name: '可导入 0', exact: true }).count(), 1)
    assert.equal(await page.getByRole('button', { name: '已在书架 37', exact: true }).count(), 1)
    assert.equal(await page.getByRole('button', { name: '全选新游戏', exact: true }).isDisabled(), true)
  } else {
    assert.match(await page.locator('.scanning-summary').innerText(), /0\s*部可导入/)
    assert.match(await page.locator('.scanning-summary').innerText(), /37\s*部已在书架/)
    await page.getByRole('button', { name: '全选新游戏', exact: true }).click()
  }
  assert.equal(await page.locator('.scan-list input:checked').count(), 0)
  assert.equal(await confirmEmpty.isDisabled(), true)
  const duplicateIds = await page.locator('.scan-list input').evaluateAll(inputs => inputs.map(input => input.value))
  assert.equal(duplicateIds.length, 37)
  const duplicateResult = await page.evaluate(ids => window.kunManager.importLocalGames(ids, false), duplicateIds)
  assert.equal(duplicateResult.added, 0)
  assert.equal(duplicateResult.skipped, 37)
  assert.deepEqual((await page.evaluate(() => window.kunManager.getState())).games, beforeDuplicate.games)
  await page.getByRole('dialog').getByRole('button', { name: '取消', exact: true }).click()
  await page.getByRole('dialog').waitFor({ state: 'hidden' })
  assert.ok((await fs.stat(path.join(gamesFolder, '测试游戏 01', 'Game.exe'))).isFile())
  assert.equal(errors.length, 0, errors.join('\n'))
  await application.close()
  application = await electron.launch(launchOptions)
  const reloaded = await application.firstWindow()
  await reloaded.waitForFunction(() => window.kunManager !== undefined)
  const restarted = await reloaded.evaluate(() => window.kunManager.getState())
  assert.equal(restarted.games.length, 37)
  assert.deepEqual(restarted.games, beforeDuplicate.games)
  assert.equal(restarted.settings.readExecutableMetadata, false)
  console.log(JSON.stringify({ packaged, executableMetadataDefaultOn: true, explicitOptOutPreserved: true, realUiImport: true, selectedAndSaved: 37, accessibleCardActions: true, knownScanRowsDisabled: 37, duplicateBackendResult: duplicateResult, personalDataPreserved: true, reloadPreserved: true, gameFilesRetained: true, rendererErrors: errors.length }))
} finally {
  if (application) await application.close()
  // Delete only the exact directory created by this test in the system temporary folder.
  if (path.dirname(temporary) !== os.tmpdir() || !path.basename(temporary).startsWith('kgm-desktop-import-')) throw new Error('Unexpected test directory')
  await fs.rm(temporary, { recursive: true, force: true })
}
