import { randomUUID } from 'node:crypto'
import { execFile, spawn, type ChildProcess } from 'node:child_process'
import { promises as fs } from 'node:fs'
import path from 'node:path'
import { performance } from 'node:perf_hooks'
import type { ActiveSession, PlaySession } from '../src/shared/types'
import { LibraryStore } from './library'

export interface DirectoryProcess { pid: number; executablePath: string; createdAt?: number }
export interface TrackerOptions {
  spawnProcess?: (file: string, args: string[], cwd: string) => ChildProcess
  findProcesses?: (directory: string) => Promise<DirectoryProcess[]>
  monotonicNow?: () => number
  wallNow?: () => number
  checkpointIntervalMs?: number
  tickIntervalMs?: number
  launcherGraceMs?: number
  onError?: (error: Error) => void
}
interface Tracking {
  id: string
  gameId: string
  installationId: string
  folderPath: string
  startedAt: string
  accumulatedMs: number
  runningSince: number | null
  knownPids: Set<number>
  process: ChildProcess
  replacementPid: number | null
  stopping: boolean
  probing: boolean
  lastCheckpoint: number
}

// The directory travels through an environment variable, never PowerShell source.
const PROCESS_QUERY = `$ErrorActionPreference='Stop'; $prefix=[IO.Path]::GetFullPath($env:KUN_GAME_DIRECTORY).TrimEnd([char]92,[char]47)+[IO.Path]::DirectorySeparatorChar; @(Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) } | ForEach-Object { @{ pid=[int]$_.ProcessId; executablePath=$_.ExecutablePath; createdAt=([DateTimeOffset]$_.CreationDate).ToUnixTimeMilliseconds() } }) | ConvertTo-Json -Compress`

export async function directoryProcesses(directory: string): Promise<DirectoryProcess[]> {
  if (process.platform !== 'win32') return []
  return new Promise((resolve, reject) => {
    execFile('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', PROCESS_QUERY], {
      windowsHide: true, timeout: 5000, maxBuffer: 1024 * 1024,
      env: { ...process.env, KUN_GAME_DIRECTORY: directory, PSModulePath: path.join(process.env.SystemRoot ?? 'C:\\Windows', 'System32', 'WindowsPowerShell', 'v1.0', 'Modules') },
    }, (error, stdout) => {
      if (error) { reject(new Error('无法查询游戏目录内的进程。')); return }
      try {
        const parsed: unknown = stdout.trim() ? JSON.parse(stdout) : []
        const entries = Array.isArray(parsed) ? parsed : [parsed]
        resolve(entries.filter((item): item is DirectoryProcess => !!item && typeof item === 'object' && Number.isInteger(item.pid) && item.pid > 0 && typeof item.executablePath === 'string'))
      } catch { reject(new Error('游戏进程查询返回了无效结果。')) }
    })
  })
}
function within(directory: string, file: string): boolean {
  const root = process.platform === 'win32' ? directory.toLowerCase() : directory
  const target = process.platform === 'win32' ? file.toLowerCase() : file
  const relative = path.relative(root, target)
  return relative !== '..' && !relative.startsWith(`..${path.sep}`) && !path.isAbsolute(relative)
}

export class PlaytimeTracker {
  private active: Tracking | null = null
  private timer: ReturnType<typeof setInterval> | null = null
  private launching = false
  private closing = false
  private ticking = false
  private finishing: Promise<void> | null = null
  private readonly monotonicNow: () => number
  private readonly wallNow: () => number
  private readonly findProcesses: (directory: string) => Promise<DirectoryProcess[]>
  private readonly spawnProcess: (file: string, args: string[], cwd: string) => ChildProcess
  private readonly checkpointInterval: number
  private readonly tickInterval: number
  private readonly launcherGrace: number

  constructor(private readonly store: LibraryStore, private readonly onChange: () => void, private readonly options: TrackerOptions = {}) {
    this.monotonicNow = options.monotonicNow ?? (() => performance.now())
    this.wallNow = options.wallNow ?? Date.now
    this.findProcesses = options.findProcesses ?? directoryProcesses
    this.spawnProcess = options.spawnProcess ?? ((file, args, cwd) => spawn(file, args, { cwd, shell: false, windowsHide: false, stdio: 'ignore' }))
    this.checkpointInterval = options.checkpointIntervalMs ?? 15000
    this.tickInterval = options.tickIntervalMs ?? 1000
    this.launcherGrace = options.launcherGraceMs ?? 6000
  }

  getActiveSession(): ActiveSession | null {
    const active = this.active
    return active ? { gameId: active.gameId, installationId: active.installationId, startedAt: active.startedAt, elapsedSeconds: this.elapsed(active) } : null
  }

  async launch(gameId: string): Promise<void> {
    if (this.closing) throw new Error('管理器正在关闭。')
    if (this.active || this.launching || this.finishing) throw new Error('已有游戏正在记录时长，请先结束当前记录。')
    this.launching = true
    try {
      const game = this.store.getGame(gameId)
      const location = game.installations.find(item => item.id === game.preferredInstallationId) ?? (game.installations.length === 1 ? game.installations[0] : undefined)
      if (!location) throw new Error('请先添加本地游戏目录。')
      if (!location.executablePath) throw new Error('游戏含多个启动文件，请先选择要启动的 exe。')
      const directory = await fs.realpath(location.folderPath), file = await fs.realpath(location.executablePath)
      if (!within(directory, file) || path.extname(file).toLowerCase() !== '.exe' || !(await fs.stat(file)).isFile()) throw new Error('启动文件不存在或不在游戏目录内，请重新选择。')
      const existing = await this.findProcesses(directory).catch(() => [] as DirectoryProcess[])
      if (this.closing) throw new Error('管理器正在关闭。')
      const child = this.spawnProcess(file, [...location.arguments], path.dirname(file))
      let exited = false
      let tracking: Tracking | null = null
      child.once('exit', () => {
        exited = true
        if (tracking && this.active === tracking && !tracking.stopping) {
          this.pause(tracking)
          void this.afterExit(tracking).catch(error => this.report(error))
        }
      })
      await new Promise<void>((resolve, reject) => {
        child.once('spawn', resolve)
        child.once('error', () => reject(new Error('无法启动游戏。请检查启动文件、访问权限及必要运行库。')))
      })
      const now = this.monotonicNow()
      tracking = {
        id: randomUUID(), gameId, installationId: location.id, folderPath: directory,
        startedAt: new Date(this.wallNow()).toISOString(), accumulatedMs: 0, runningSince: now,
        knownPids: new Set(existing.map(item => item.pid)), process: child, replacementPid: null,
        stopping: false, probing: false, lastCheckpoint: now,
      }
      if (child.pid) tracking.knownPids.add(child.pid)
      this.active = tracking
      child.on('error', () => this.report(new Error('游戏进程发生错误，请检查游戏是否仍在运行。')))
      this.timer = setInterval(() => { void this.tick() }, this.tickInterval)
      this.timer.unref()
      this.onChange()
      if (exited) { this.pause(tracking); void this.afterExit(tracking).catch(error => this.report(error)) }
      if (this.closing) await this.finishSession()
    } finally { this.launching = false }
  }

  /** Save the same session cumulatively; LibraryStore applies only its new seconds. */
  async checkpoint(): Promise<void> {
    const active = this.active
    if (!active || active.stopping) return
    await this.store.recordSession(active.gameId, this.record(active))
    active.lastCheckpoint = this.monotonicNow()
  }

  async finishSession(): Promise<void> {
    if (this.finishing) return this.finishing
    const active = this.active
    if (!active) return
    active.stopping = true
    this.pause(active)
    if (this.timer) { clearInterval(this.timer); this.timer = null }
    const record = this.record(active)
    // Preserve the frozen record until persistence succeeds. Never signal the game.
    this.finishing = (async () => {
      try {
        await this.store.recordSession(active.gameId, record)
        if (this.active === active) this.active = null
      }
      catch (error) {
        if (!this.closing && !this.timer) {
          this.timer = setInterval(() => { void this.finishSession().catch(retryError => this.report(retryError)) }, 15000)
          this.timer.unref()
        }
        throw error
      }
      finally { this.onChange() }
    })()
    try { await this.finishing } finally { this.finishing = null }
  }

  async shutdown(): Promise<void> {
    this.closing = true
    try {
      while (this.launching) await new Promise<void>(resolve => setTimeout(resolve, 10))
      await this.finishSession()
    } catch (error) { this.closing = false; throw error }
  }

  private elapsed(active: Tracking): number {
    return Math.floor(Math.max(0, active.accumulatedMs + (active.runningSince === null ? 0 : this.monotonicNow() - active.runningSince)) / 1000)
  }
  private record(active: Tracking): PlaySession {
    return { id: active.id, startedAt: active.startedAt, endedAt: new Date(Math.max(Date.parse(active.startedAt), this.wallNow())).toISOString(), seconds: this.elapsed(active) }
  }
  private pause(active: Tracking): void {
    if (active.runningSince !== null) { active.accumulatedMs += Math.max(0, this.monotonicNow() - active.runningSince); active.runningSince = null }
  }
  private async tick(): Promise<void> {
    if (this.ticking) return
    this.ticking = true
    try {
      const active = this.active
      if (!active || active.stopping) return
      if (active.replacementPid && !active.probing) {
        let processes: DirectoryProcess[]
        try { processes = await this.findProcesses(active.folderPath) }
        catch (error) { this.pause(active); await this.finishSession(); throw error }
        if (!processes.some(item => item.pid === active.replacementPid && within(active.folderPath, item.executablePath))) {
          this.pause(active); await this.afterExit(active)
        }
      }
      if (this.active === active && !active.stopping && this.monotonicNow() - active.lastCheckpoint >= this.checkpointInterval) await this.checkpoint()
      this.onChange()
    } catch (error) { this.report(error) }
    finally { this.ticking = false }
  }
  private async afterExit(active: Tracking): Promise<void> {
    if (active.probing || active.stopping || this.active !== active) return
    active.probing = true
    active.replacementPid = null
    try {
      // Count only time with a tracked process; launcher probing does not add time.
      const deadline = Date.now() + this.launcherGrace
      while (!active.stopping && this.active === active && Date.now() < deadline) {
        let processes: DirectoryProcess[]
        try { processes = await this.findProcesses(active.folderPath) } catch { break }
        const replacement = processes.filter(item => !active.knownPids.has(item.pid) && within(active.folderPath, item.executablePath) && (!item.createdAt || item.createdAt >= Date.parse(active.startedAt) - 1000)).sort((left, right) => (right.createdAt ?? 0) - (left.createdAt ?? 0))[0]
        if (replacement) {
          active.knownPids.add(replacement.pid); active.replacementPid = replacement.pid
          active.runningSince = this.monotonicNow(); this.onChange(); return
        }
        await new Promise<void>(resolve => { const timeout = setTimeout(resolve, Math.min(750, Math.max(1, deadline - Date.now()))); timeout.unref() })
      }
      if (!active.stopping && this.active === active) await this.finishSession()
    } finally { active.probing = false }
  }
  private report(error: unknown): void { this.options.onError?.(error instanceof Error ? error : new Error(String(error))) }
}
