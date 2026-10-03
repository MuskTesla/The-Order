import { app, BrowserWindow, dialog, ipcMain, net, protocol, safeStorage, session, shell, Tray, Menu, nativeImage } from 'electron'
import { promises as fs } from 'node:fs'
import { randomUUID } from 'node:crypto'
import path from 'node:path'
import { pathToFileURL } from 'node:url'
import { execFile } from 'node:child_process'
import { promisify } from 'node:util'
import { LibraryStore, scanFolder, validateLibraryBackup, type InstallationAttachmentToken } from './library'
import { inspectInstallationAttachment } from './installation-attachment'
import { PlaytimeTracker } from './playtime'
import { NextMoeClient } from './nextmoe'
import { SettingsStore } from './settings'
import { MetadataMatcher, loadTitleIndex } from './metadata-matching'
import { hasExplicitGameVersion } from './title-matching'
import { assertLocalCoverDecodes, normalizePhoto, readPhotoFile } from './local-cover'
import type { BulkGamePatch, GamePatch, NonPortraitCoverDisplay, ScanCandidate, SettingsInput, WorkCover } from '../src/shared/types'

app.setName('The Order')
const developmentUrl = !app.isPackaged && process.env.KGM_DEV_URL === 'http://127.0.0.1:5173' ? process.env.KGM_DEV_URL : null
if (process.platform === 'win32') app.setAppUserModelId('dev.local.kungalmanager')
const dataDir = process.env.KGM_DATA_DIR ? path.resolve(process.env.KGM_DATA_DIR) : path.join(app.getPath('appData'), 'KunGalManager')
app.setPath('userData', dataDir)
protocol.registerSchemesAsPrivileged([{ scheme: 'kun-manager', privileges: { standard: true, secure: true, supportFetchAPI: true } }])

let mainWindow: BrowserWindow | null = null
let tray: Tray | null = null
let shuttingDown = false
let allowQuit = false
let launchInProgress = false
const library = new LibraryStore(dataDir)
const settings = new SettingsStore(dataDir, { available: () => safeStorage.isEncryptionAvailable(), encrypt: value => safeStorage.encryptString(value), decrypt: value => safeStorage.decryptString(value) })
const notify = () => { mainWindow?.webContents.send('kun-manager:changed') }
let lastTrackerError = 0
const tracker = new PlaytimeTracker(library, notify, { onError: error => {
  if (Date.now() - lastTrackerError < 300000) return
  lastTrackerError = Date.now()
  if (mainWindow) void dialog.showMessageBox(mainWindow, { type: 'warning', title: '游玩记录提示', message: '游玩记录暂时无法保存。', detail: publicError(error) })
} })
let scanCandidates = new Map<string, ScanCandidate>()
const attachmentRequests = new Map<string, { gameId: string; token: InstallationAttachmentToken; candidates: Map<string, ScanCandidate>; createdAt: number }>()
const validId = (value: unknown): string => {
  if (typeof value !== 'string' || value.length < 1 || value.length > 100 || /[\r\n\0]/.test(value)) throw new Error('条目标识不正确。')
  return value
}
const api = () => new NextMoeClient(settings.getApiOptions())
let identityQueue: Promise<void> = Promise.resolve()
function withIdentityLock<T>(action: () => Promise<T>, allowDuringShutdown = false): Promise<T> {
  const run = identityQueue.then(() => {
    if (shuttingDown && !allowDuringShutdown) throw new Error('管理器正在关闭。')
    return action()
  })
  identityQueue = run.then(() => undefined, () => undefined)
  return run
}
function assertCanLink(localId: string, catalogId: string): void {
  const active = tracker.getActiveSession()
  if (!active) return
  const victim = library.getGames().find(game => game.id !== localId && game.metadata?.id === catalogId)
  if (victim?.id === active.gameId) throw new Error('另一安装正在计时，请先结束计时，再合并同一作品的安装。')
}
let titleIndex: ReturnType<typeof loadTitleIndex> | undefined
const matcher = new MetadataMatcher(dataDir, library, {
  client: signal => new NextMoeClient({ ...settings.getApiOptions(), signal }),
  index: () => titleIndex ??= loadTitleIndex(path.resolve(__dirname, '../data/title-index.json.gz')),
  changed: notify,
  readExecutableMetadata: () => settings.getPublic().readExecutableMetadata,
  capture: (game, batchId) => library.captureMetadataToken(game.id, batchId),
  apply: (original, work, requestToken, signal) => withIdentityLock(async () => {
    if (signal?.aborted) return false
    let current
    try { current = library.getGame(original.id) } catch { return false }
    if (current.name !== original.name || (current.metadata?.id ?? null) !== (original.metadata?.id ?? null)) return false
    assertCanLink(current.id, work.id)
    const defaultNames = current.installations.map(installation => path.basename(installation.folderPath))
    await library.linkMetadata(current.id, work, { preserveName: !current.metadata && (!defaultNames.includes(current.name) || hasExplicitGameVersion(current.name)), preserveMergedName: true, expectedName: original.name, expectedMetadataId: original.metadata?.id ?? null, requestToken, signal })
    notify(); return true
  })
})

function publicError(error: unknown): string {
  let message = error instanceof Error ? error.message : '操作失败，请重试。'
  message = message.replace(/nmk_(?:live|test)_[A-Za-z0-9_-]+/g, '[密钥已隐藏]').replace(/Bearer\s+\S+/gi, 'Bearer [密钥已隐藏]')
  return message.slice(0, 500)
}

function registerIpc(): void {
  const handle = (name: string, action: (...args: any[]) => unknown) => {
    ipcMain.handle('kun-manager:' + name, async (event, ...args: unknown[]) => {
      try {
        if (!mainWindow || event.sender !== mainWindow.webContents || event.senderFrame !== mainWindow.webContents.mainFrame) throw new Error('请求来源不正确。')
        if (shuttingDown) throw new Error('管理器正在关闭。')
        return { ok: true, value: await action(...args) }
      } catch (error) { return { ok: false, error: publicError(error) } }
    })
  }
  handle('getState', () => ({ games: library.getGames(), settings: settings.getPublic(), activeSession: tracker.getActiveSession(), dataPath: dataDir, version: app.getVersion(), matching: matcher.getState() }))
  handle('saveSettings', async (input: SettingsInput) => { await matcher.cancel(); const result = await settings.save(input); notify(); return result })
  handle('testConnection', () => api().testConnection())
  handle('searchWorks', (query: unknown, cursor: unknown) => {
    if (typeof query !== 'string' || query.length > 300 || cursor !== undefined && (typeof cursor !== 'string' || cursor.length > 4000)) throw new Error('搜索内容不正确。')
    return api().searchWorks(query, cursor as string | undefined)
  })
  handle('getWork', (id: unknown) => api().getWork(validId(id)))
  handle('addVirtualGame', async (id: unknown) => { const result = await library.addVirtual(await api().getWork(validId(id))); notify(); return result })
  handle('scanFolder', async () => {
    const result = await dialog.showOpenDialog(mainWindow!, { title: '选择游戏目录或包含多个游戏的文件夹', properties: ['openDirectory'] })
    if (result.canceled || !result.filePaths[0]) return []
    const candidates = await scanFolder(result.filePaths[0])
    scanCandidates = new Map(candidates.map(candidate => [candidate.id, candidate]))
    return candidates
  })
  handle('prepareInstallationAttachment', async (gameId: unknown) => {
    const id = validId(gameId), token = library.captureInstallationAttachmentToken(id)
    const result = await dialog.showOpenDialog(mainWindow!, { title: '选择这部游戏的根目录', properties: ['openDirectory'] })
    if (result.canceled || !result.filePaths[0]) return null
    const rootPath = await fs.realpath(result.filePaths[0])
    const found = await scanFolder(rootPath)
    // With one nested executable directory, the user explicitly selected its
    // enclosing game root. Multiple discoveries still require a specific choice.
    const candidates = found.map(candidate => ({ ...candidate, folderPath: found.length === 1 ? rootPath : candidate.folderPath }))
    const requestId = randomUUID()
    for (const [key, request] of attachmentRequests) if (Date.now() - request.createdAt > 600000) attachmentRequests.delete(key)
    if (attachmentRequests.size >= 8) attachmentRequests.delete(attachmentRequests.keys().next().value!)
    attachmentRequests.set(requestId, { gameId: id, token, candidates: new Map(candidates.map(candidate => [candidate.id, candidate])), createdAt: Date.now() })
    return { requestId, rootPath, candidates }
  })
  handle('attachLocalInstallation', async (requestId: unknown, candidateId: unknown) => {
    const key = validId(requestId), request = attachmentRequests.get(key)
    const candidate = request?.candidates.get(validId(candidateId))
    if (!request || !candidate || Date.now() - request.createdAt > 600000) throw new Error('目录选择已失效，请重新选择游戏根目录。')
    const result = await dialog.showOpenDialog(mainWindow!, { title: '选择这个目录中的游戏程序', defaultPath: candidate.folderPath, filters: [{ name: 'Windows 程序', extensions: ['exe'] }], properties: ['openFile'] })
    if (result.canceled || !result.filePaths[0]) return null
    const assertActive = () => { if (attachmentRequests.get(key) !== request) throw new Error('这次目录选择已取消，请重新选择。') }
    const updated = await withIdentityLock(async () => {
      assertActive()
      const inspected = await inspectInstallationAttachment(library.getGame(request.gameId), candidate.folderPath, result.filePaths[0]!)
      assertActive()
      return library.attachInstallation(request.gameId, { ...candidate, folderPath: inspected.folderPath, executables: [inspected.executablePath] }, request.token, assertActive, candidate.folderPath)
    })
    attachmentRequests.delete(key); notify(); return updated
  })
  handle('cancelInstallationAttachment', (requestId: unknown) => { attachmentRequests.delete(validId(requestId)) })
  handle('importLocalGames', async (ids: unknown, autoMatch: unknown = true) => {
    if (!Array.isArray(ids) || ids.length > 2000 || ids.some(id => typeof id !== 'string' || !scanCandidates.has(id))) throw new Error('请重新扫描后选择需要导入的游戏。')
    if (typeof autoMatch !== 'boolean') throw new Error('自动识别选项不正确。')
    const selected = ids.map(id => scanCandidates.get(id)!)
    const result = await library.importCandidates(selected); notify()
    const folders = new Set(selected.map(candidate => path.resolve(candidate.folderPath).toLowerCase()))
    const gameIds = library.getGames().filter(game => game.installations.some(installation => folders.has(path.resolve(installation.folderPath).toLowerCase())) && (!game.metadata || !game.metadata.coverUrl)).map(game => game.id)
    let matchingStarted = false
    if (autoMatch && settings.getPublic().hasApiKey && gameIds.length && matcher.getState().status !== 'running') { await matcher.start(gameIds); matchingStarted = true }
    return { ...result, matchingStarted }
  })
  handle('startMetadataMatching', (ids: unknown, mode: unknown = 'missing') => {
    if (ids !== undefined && (!Array.isArray(ids) || ids.length > 10000 || ids.some(id => typeof id !== 'string'))) throw new Error('资料补全列表不正确。')
    if (mode !== 'missing' && mode !== 'reidentify') throw new Error('游戏识别模式不正确。')
    return matcher.start(ids as string[] | undefined, mode)
  })
  handle('cancelMetadataMatching', () => matcher.cancel())
  handle('resolveMetadataMatch', async (gameId: unknown, workId: unknown, reviewId: unknown, decisionFingerprint: unknown) => {
    const id = validId(gameId)
    const guard = matcher.captureReview(id, validId(reviewId), validId(decisionFingerprint), { allowStale: workId === null })
    if (workId === null) return withIdentityLock(() => matcher.dismiss(guard))
    const catalogId = validId(workId), request = await library.beginMetadataRequest(id, 'association')
    try {
      matcher.assertReviewCurrent(guard, request.token)
      const work = await api().getWork(catalogId)
      return await withIdentityLock(async () => {
        matcher.assertReviewCurrent(guard, request.token); assertCanLink(id, work.id)
        await library.linkMetadata(id, work, { preserveMergedName: true, expectedName: request.game.name, expectedMetadataId: request.game.metadata?.id ?? null, requestToken: request.token, beforeCommit: () => matcher.assertReviewCurrent(guard, request.token) })
        notify(); return matcher.dismiss(guard, { afterCommit: true })
      })
    } finally { await library.finishMetadataRequest(request.token) }
  })
  handle('linkGame', async (gameId: unknown, workId: unknown, reviewId?: unknown, decisionFingerprint?: unknown) => {
    const localId = validId(gameId), catalogId = validId(workId)
    const review = matcher.getState().reviews.find(item => item.gameId === localId)
    const guard = reviewId !== undefined
      ? matcher.captureReview(localId, validId(reviewId), validId(decisionFingerprint), { allowStale: true })
      : review?.reviewId && review.decisionFingerprint ? matcher.captureReview(localId, review.reviewId, review.decisionFingerprint, { allowStale: true }) : null
    const request = await library.beginMetadataRequest(localId, 'association')
    try {
      if (guard) matcher.assertReviewCurrent(guard, request.token)
      const work = await api().getWork(catalogId)
      return await withIdentityLock(async () => {
        assertCanLink(localId, work.id)
        const result = await library.linkMetadata(localId, work, { preserveMergedName: true, expectedName: request.game.name, expectedMetadataId: request.game.metadata?.id ?? null, requestToken: request.token, ...(guard ? { beforeCommit: () => matcher.assertReviewCurrent(guard, request.token) } : {}) })
        if (guard) await matcher.dismiss(guard, { afterCommit: true }); notify(); return result
      })
    } finally { await library.finishMetadataRequest(request.token) }
  })
  handle('refreshGame', async (id: unknown) => {
    const request = await library.beginMetadataRequest(validId(id), 'refresh')
    try {
      const game = request.game
      if (!game.metadata) throw new Error('请先为游戏匹配 Kun 资料。')
      const work = await api().getWork(game.metadata.id)
      return await withIdentityLock(async () => {
        assertCanLink(game.id, work.id)
        const result = await library.linkMetadata(game.id, work, { preserveMergedName: true, expectedName: game.name, expectedMetadataId: game.metadata!.id, requestToken: request.token }); notify(); return result
      })
    } finally { await library.finishMetadataRequest(request.token) }
  })
  handle('setGameCover', async (gameId: unknown, expectedWorkId: unknown, coverId: unknown, display: unknown) => {
    const id = validId(gameId)
    if (expectedWorkId !== null && (typeof expectedWorkId !== 'string' || !/^[0-9]{1,20}$/.test(expectedWorkId))) throw new Error('封面作品编号不正确。')
    if (coverId !== undefined && coverId !== null && (typeof coverId !== 'string' || !/^[0-9]{1,20}$/.test(coverId))) throw new Error('封面编号不正确。')
    if (display !== undefined && display !== null && display !== 'blur' && display !== 'plain' && display !== 'crop') throw new Error('封面展示方式不正确。')
    if (typeof coverId === 'string' && expectedWorkId === null) throw new Error('请先关联作品，再选择资料封面。')
    const token = library.captureCoverRequest(id, expectedWorkId)
    let cover: WorkCover | null | undefined = coverId === null ? null : undefined
    if (typeof coverId === 'string') {
      const work = await api().getWork(expectedWorkId!)
      cover = work.id === expectedWorkId ? work.covers?.find(item => item.id === coverId) : undefined
      if (!cover) throw new Error('该封面已不可用或不属于当前作品，请重新载入。')
    }
    return withIdentityLock(async () => {
      const result = await library.setGameCover(id, expectedWorkId, cover, display as NonPortraitCoverDisplay | null | undefined, token, () => {
        if (cover?.sexual === 'explicit' && !settings.getPublic().includeNsfw) throw new Error('当前设置不允许选取成人封面。')
      })
      notify(); return result
    })
  })
  handle('pickLocalCover', async (gameId: unknown, expectedWorkId: unknown, display: unknown) => {
    const id = validId(gameId)
    if (expectedWorkId !== null && (typeof expectedWorkId !== 'string' || !/^[0-9]{1,20}$/.test(expectedWorkId))) throw new Error('封面作品编号不正确。')
    if (display !== undefined && display !== null && display !== 'blur' && display !== 'plain' && display !== 'crop') throw new Error('封面展示方式不正确。')
    const token = library.captureCoverRequest(id, expectedWorkId)
    const selected = await dialog.showOpenDialog(mainWindow!, { title: '选择本地封面照片', filters: [{ name: 'PNG / JPG 图片', extensions: ['png', 'jpg', 'jpeg'] }], properties: ['openFile'] })
    if (selected.canceled || !selected.filePaths[0]) return null
    const cover = normalizePhoto(await readPhotoFile(selected.filePaths[0]), nativeImage)
    return withIdentityLock(async () => {
      const result = await library.setLocalCover(id, expectedWorkId, cover, display as NonPortraitCoverDisplay | null | undefined, token)
      notify(); return result
    })
  })
  handle('updateGame', async (id: unknown, patch: GamePatch) => { const result = await library.updateGame(validId(id), patch); notify(); return result })
  handle('updateGames', async (ids: unknown, patch: BulkGamePatch) => {
    if (!Array.isArray(ids) || ids.length < 1 || ids.length > 1000) throw new Error('每次请选择 1 至 1000 部游戏整理。')
    const result = await library.updateGames(ids.map(validId), patch); notify(); return result
  })
  handle('pickExecutable', async (id: unknown, installationId?: string) => {
    const game = library.getGame(validId(id))
    const selected = installationId ? game.installations.find(item => item.id === installationId) : game.installations.find(item => item.id === game.preferredInstallationId) ?? game.installations[0]
    const result = await dialog.showOpenDialog(mainWindow!, { title: '选择游戏启动程序', defaultPath: selected?.folderPath, filters: [{ name: 'Windows 程序', extensions: ['exe'] }], properties: ['openFile'] })
    if (result.canceled || !result.filePaths[0]) return null
    const updated = await library.setExecutable(game.id, selected?.id, result.filePaths[0]); notify(); return updated
  })
  handle('setLaunchArguments', async (id: unknown, installationId: unknown, args: string[]) => { const result = await library.setLaunchArguments(validId(id), validId(installationId), args); notify(); return result })
  handle('launchGame', (id: unknown) => withIdentityLock(async () => {
    launchInProgress = true
    try { await tracker.launch(validId(id)); notify() } finally { launchInProgress = false }
  }))
  handle('finishSession', () => withIdentityLock(async () => { await tracker.finishSession(); notify() }))
  handle('removeGame', (id: unknown) => withIdentityLock(async () => {
    const gameId = validId(id)
    if (tracker.getActiveSession()?.gameId === gameId) throw new Error('请先结束此游戏的计时再移除条目。')
    await library.removeGame(gameId); notify()
  }))
  handle('exportLibrary', async () => {
    const result = await dialog.showSaveDialog(mainWindow!, { title: '导出书架备份', defaultPath: `The Order-${new Date().toISOString().slice(0, 10)}.json`, filters: [{ name: '书架备份', extensions: ['json'] }] })
    if (result.canceled || !result.filePath) return false
    await fs.writeFile(result.filePath, JSON.stringify(library.exportBackup(), null, 2), 'utf8')
    return true
  })
  handle('importLibrary', async () => {
    if (tracker.getActiveSession()) throw new Error('请先结束计时，再导入书架备份。')
    const result = await dialog.showOpenDialog(mainWindow!, { title: '导入 The Order 书架备份', filters: [{ name: '书架备份', extensions: ['json'] }], properties: ['openFile'] })
    if (result.canceled || !result.filePaths[0]) return null
    const stat = await fs.stat(result.filePaths[0]); if (stat.size > 32 * 1024 * 1024) throw new Error('备份文件超过 32 MB。')
    let contents: unknown
    try { contents = JSON.parse(await fs.readFile(result.filePaths[0], 'utf8')) } catch { throw new Error('备份文件不是有效 JSON。') }
    // Validate embedded raster data with Electron as well as the bounded
    // structural validator, before any imported records enter a transaction.
    const importGames = validateLibraryBackup(contents)
    for (const game of importGames) if (game.localCover) assertLocalCoverDecodes(game.localCover, nativeImage)
    return withIdentityLock(async () => {
      if (tracker.getActiveSession()) throw new Error('请先结束计时，再导入书架备份。')
      const imported = await library.importBackup(contents); notify(); return imported
    })
  })
  handle('openExternal', async (input: unknown) => {
    if (typeof input !== 'string' || input.length > 3000) throw new Error('链接不正确。')
    const url = new URL(input)
    if (url.protocol !== 'https:' || url.username || url.password) throw new Error('仅支持 HTTPS 网页链接。')
    await shell.openExternal(url.href)
  })
  handle('openGameFolder', async (id: unknown) => {
    const game = library.getGame(validId(id)); const installation = game.installations.find(item => item.id === game.preferredInstallationId) ?? game.installations[0]
    if (!installation) throw new Error('此条目尚未关联本地安装。')
    const error = await shell.openPath(installation.folderPath); if (error) throw new Error('游戏目录无法打开，请检查路径。')
  })
  handle('openDataFolder', async () => { const error = await shell.openPath(dataDir); if (error) throw new Error('数据目录无法打开。') })
}

async function provisionKey(): Promise<boolean> {
  const index = process.argv.indexOf('--provision-key-file')
  if (index < 0) return false
  const file = process.argv[index + 1]
  if (!file || !path.isAbsolute(file)) throw new Error('凭据初始化文件路径不正确。')
  const script = "[void][Reflection.Assembly]::LoadWithPartialName('System.Security'); $h=[IO.File]::ReadAllText($env:KGM_PROVISION_FILE).Trim(); $b=[byte[]]::new($h.Length/2); for($i=0;$i -lt $b.Length;$i++){ $b[$i]=[Convert]::ToByte($h.Substring($i*2,2),16) }; $p=[Security.Cryptography.ProtectedData]::Unprotect($b,$null,[Security.Cryptography.DataProtectionScope]::CurrentUser); [Console]::Out.Write([Text.Encoding]::Unicode.GetString($p))"
  const result = await promisify(execFile)('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', script], { windowsHide: true, env: { ...process.env, KGM_PROVISION_FILE: file }, timeout: 15000, maxBuffer: 4096 })
  const current = settings.getPublic()
  await settings.save({ ...current, apiKey: result.stdout.trim() })
  return true
}

function createWindow(): void {
  mainWindow = new BrowserWindow({ width: 1440, height: 940, minWidth: 1000, minHeight: 700, title: 'The Order · Galgame 书架', icon: path.resolve(__dirname, '../dist/icon.png'), backgroundColor: '#f7faff', autoHideMenuBar: true, show: false, webPreferences: { preload: path.join(__dirname, 'preload.cjs'), contextIsolation: true, nodeIntegration: false, sandbox: true, webSecurity: true } })
  mainWindow.webContents.setWindowOpenHandler(() => ({ action: 'deny' }))
  mainWindow.webContents.on('will-navigate', event => { event.preventDefault() })
  mainWindow.on('close', event => {
    if (!allowQuit && (tracker.getActiveSession() || launchInProgress)) { event.preventDefault(); mainWindow?.hide(); return }
  })
  mainWindow.on('closed', () => { mainWindow = null })
  mainWindow.once('ready-to-show', () => mainWindow?.show())
  if (developmentUrl) void mainWindow.loadURL(developmentUrl)
  else void mainWindow.loadURL('kun-manager://app/index.html')
}

function createTray(): void {
  tray = new Tray(nativeImage.createFromPath(path.resolve(__dirname, '../dist/icon.png')).resize({ width: 32, height: 32 }))
  tray.setToolTip('The Order · 书架与游玩计时')
  tray.setContextMenu(Menu.buildFromTemplate([{ label: '打开书架', click: () => { if (!mainWindow) createWindow(); mainWindow?.show(); mainWindow?.focus() } }, { type: 'separator' }, { label: '退出管理器', click: () => { allowQuit = true; app.quit() } }]))
  tray.on('double-click', () => { mainWindow?.show(); mainWindow?.focus() })
}

if (!app.requestSingleInstanceLock()) app.quit()
else {
  app.on('second-instance', () => { if (!mainWindow) createWindow(); mainWindow?.show(); mainWindow?.focus() })
  app.whenReady().then(async () => {
    await library.initialize(); await settings.initialize(); await matcher.initialize()
    await provisionKey()
    if (process.argv.includes('--provision-only')) { allowQuit = true; app.quit(); return }
    const dist = path.resolve(__dirname, '../dist')
    protocol.handle('kun-manager', request => {
      const url = new URL(request.url)
      const local = path.resolve(dist, '.' + decodeURIComponent(url.pathname))
      if (url.host !== 'app' || local !== dist && !local.startsWith(dist + path.sep)) return new Response('Not found', { status: 404 })
      return net.fetch(pathToFileURL(local).toString())
    })
    session.defaultSession.setPermissionRequestHandler((_contents, _permission, callback) => callback(false))
    session.defaultSession.webRequest.onHeadersReceived((details, callback) => {
      const dev = developmentUrl !== null
      const csp = `default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' https: data:; font-src 'self' data:; connect-src 'self'${dev ? ' http://127.0.0.1:5173 ws://127.0.0.1:5173' : ''}; object-src 'none'; base-uri 'self'; frame-src 'none'`
      callback({ responseHeaders: { ...details.responseHeaders, 'Content-Security-Policy': [csp] } })
    })
    registerIpc(); createTray(); createWindow()
    if (library.recoveryNotice) void dialog.showMessageBox(mainWindow!, { type: 'warning', title: '书架恢复提示', message: library.recoveryNotice })
    setInterval(notify, 5000).unref()
  }).catch(error => { dialog.showErrorBox('The Order 启动失败', publicError(error)); allowQuit = true; app.quit() })
  app.on('window-all-closed', () => { if (!tracker.getActiveSession() && !launchInProgress) app.quit() })
  app.on('activate', () => { if (!mainWindow) createWindow(); mainWindow?.show() })
  app.on('before-quit', event => {
    if (shuttingDown) return
    event.preventDefault(); shuttingDown = true; allowQuit = true
    void matcher.shutdown().then(() => withIdentityLock(() => tracker.shutdown(), true)).then(() => { tray?.destroy(); app.quit() }).catch(async error => {
      shuttingDown = false; allowQuit = false
      if (!mainWindow) createWindow()
      mainWindow?.show()
      await dialog.showMessageBox(mainWindow!, { type: 'error', title: '游玩记录保存失败', message: '退出已取消，请保存游玩记录后再退出。', detail: publicError(error) })
    })
  })
}
