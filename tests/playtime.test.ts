import assert from 'node:assert/strict'
import { test } from 'node:test'
import { EventEmitter } from 'node:events'
import { promises as fs } from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import type { ChildProcess } from 'node:child_process'
import { LibraryStore, scanFolder } from '../electron/library'
import { PlaytimeTracker, directoryProcesses } from '../electron/playtime'

class FakeProcess extends EventEmitter {
  pid = 101
  killed = false
  kill() { this.killed = true; return true }
}
async function fixture(t: { after: (callback: () => Promise<void>) => void }) {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'kun-playtime-test-'))
  t.after(async () => { await fs.rm(root, { recursive: true, force: true }) })
  const directory = path.join(root, '中文 游戏'); await fs.mkdir(directory)
  const executable = path.join(directory, 'game.exe'); await fs.writeFile(executable, 'test fixture only')
  const store = new LibraryStore(path.join(root, 'data')); await store.initialize(); await store.importCandidates(await scanFolder(directory))
  return { root, directory, executable, store, game: store.getGames()[0]! }
}

test('launch uses an argument array and cwd, counts monotonic elapsed time, and finish never kills game', async t => {
  const { store, game, directory, executable } = await fixture(t)
  const child = new FakeProcess(), calls: unknown[][] = []
  let elapsed = 0, wall = Date.parse('2026-10-02T00:00:00Z'), changed = 0
  await store.setLaunchArguments(game.id, game.installations[0]!.id, ['--title', 'space and 中文', '$(literal)'])
  const tracker = new PlaytimeTracker(store, () => { changed++ }, {
    spawnProcess: (file, args, cwd) => { calls.push([file, args, cwd]); queueMicrotask(() => child.emit('spawn')); return child as unknown as ChildProcess },
    findProcesses: async () => [], monotonicNow: () => elapsed, wallNow: () => wall, tickIntervalMs: 100000, launcherGraceMs: 0,
  })
  t.after(() => tracker.shutdown())
  await tracker.launch(game.id)
  assert.deepEqual(calls[0], [executable, ['--title', 'space and 中文', '$(literal)'], directory])
  await assert.rejects(tracker.launch(game.id), /正在记录/)
  elapsed = 15750; wall += 15750
  assert.equal(tracker.getActiveSession()?.elapsedSeconds, 15)
  await tracker.checkpoint(); await tracker.checkpoint()
  assert.equal(store.getGame(game.id).totalPlaySeconds, 15)
  elapsed = 32500; wall -= 60000 // system clock changes do not reduce elapsed time
  await tracker.finishSession()
  assert.equal(tracker.getActiveSession(), null)
  assert.equal(store.getGame(game.id).totalPlaySeconds, 32)
  assert.equal(store.getGame(game.id).playCount, 1)
  assert.equal(child.killed, false)
  assert(changed >= 2)
})

test('a failed launch never records a play or duration', async t => {
  const { store, game } = await fixture(t), child = new FakeProcess()
  const tracker = new PlaytimeTracker(store, () => {}, {
    spawnProcess: () => { queueMicrotask(() => child.emit('error', new Error('failure'))); return child as unknown as ChildProcess }, findProcesses: async () => [],
  })
  await assert.rejects(tracker.launch(game.id), /无法启动/)
  assert.equal(tracker.getActiveSession(), null)
  assert.equal(store.getGame(game.id).playCount, 0)
  assert.equal(store.getGame(game.id).totalPlaySeconds, 0)
  await tracker.shutdown()
})

test('process exit finalizes elapsed seconds and shutdown stops future launches', async t => {
  const { store, game } = await fixture(t), child = new FakeProcess()
  let elapsed = 0
  const tracker = new PlaytimeTracker(store, () => {}, {
    spawnProcess: () => { queueMicrotask(() => child.emit('spawn')); return child as unknown as ChildProcess },
    findProcesses: async () => [], monotonicNow: () => elapsed, tickIntervalMs: 100000, launcherGraceMs: 0,
  })
  await tracker.launch(game.id)
  elapsed = 6500; child.emit('exit', 0, null)
  await tracker.finishSession()
  assert.equal(store.getGame(game.id).totalPlaySeconds, 6)
  await tracker.shutdown()
  await assert.rejects(tracker.launch(game.id), /正在关闭/)
  assert.equal(child.killed, false)
})

test('launcher handoff selects only a new process inside the selected game directory', async t => {
  const { store, game, directory, executable } = await fixture(t), child = new FakeProcess()
  let elapsed = 0, query = 0, changed = 0
  const tracker = new PlaytimeTracker(store, () => { changed++ }, {
    spawnProcess: () => { queueMicrotask(() => child.emit('spawn')); return child as unknown as ChildProcess },
    findProcesses: async () => ++query === 1 ? [{ pid: 500, executablePath: executable }] : [
      { pid: 500, executablePath: executable },
      { pid: 600, executablePath: path.join(`${directory}2`, 'wrong.exe') },
      { pid: 700, executablePath: executable },
    ],
    monotonicNow: () => elapsed, tickIntervalMs: 100000, launcherGraceMs: 1000,
  })
  t.after(() => tracker.shutdown())
  await tracker.launch(game.id)
  elapsed = 2300; child.emit('exit', 0, null)
  await new Promise(resolve => setImmediate(resolve))
  assert.equal(tracker.getActiveSession()?.elapsedSeconds, 2)
  elapsed = 9800
  await tracker.finishSession()
  assert.equal(store.getGame(game.id).totalPlaySeconds, 9)
  assert(changed >= 3)
  assert.equal(child.killed, false)
})

test('a failed final save keeps a paused session available for retry without double counting', async t => {
  const { store, game } = await fixture(t), child = new FakeProcess()
  let elapsed = 0
  const tracker = new PlaytimeTracker(store, () => {}, {
    spawnProcess: () => { queueMicrotask(() => child.emit('spawn')); return child as unknown as ChildProcess },
    findProcesses: async () => [], monotonicNow: () => elapsed, tickIntervalMs: 100000,
  })
  t.after(() => tracker.shutdown())
  await tracker.launch(game.id); elapsed = 15700; await tracker.checkpoint()
  elapsed = 32500
  const original = store.recordSession.bind(store)
  let failures = 1
  store.recordSession = async (...args) => { if (failures-- > 0) throw new Error('disk unavailable'); await original(...args) }
  await assert.rejects(tracker.shutdown(), /disk unavailable/)
  assert.equal(tracker.getActiveSession()?.elapsedSeconds, 32)
  elapsed = 90000
  assert.equal(tracker.getActiveSession()?.elapsedSeconds, 32)
  await tracker.finishSession()
  assert.equal(store.getGame(game.id).totalPlaySeconds, 32)
  assert.equal(store.getGame(game.id).playCount, 1)
  assert.equal(tracker.getActiveSession(), null)
  // A cancelled shutdown returns the manager to a usable state.
  await tracker.launch(game.id)
  await tracker.finishSession()
})

test('Windows directory process query is read only and handles an empty temporary game folder', { skip: process.platform !== 'win32' }, async t => {
  const { directory } = await fixture(t)
  const result = await directoryProcesses(directory)
  assert.deepEqual(result, [])
})
