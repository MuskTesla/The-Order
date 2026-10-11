import { randomUUID } from 'node:crypto'
import { mkdir, open, readFile, stat, unlink } from 'node:fs/promises'
import path from 'node:path'
import { createInterface } from 'node:readline'
import { fileURLToPath } from 'node:url'

const MAX_HOLD_MS = 10 * 60 * 60 * 1000
const repositoryRoot = path.dirname(path.dirname(fileURLToPath(import.meta.url)))
const defaultLockPath = path.join(repositoryRoot, '.artifacts', 'overnight-2026-10-04', 'writer.lock')

// This only guards cooperating writers. Check other active work and Git state before writing.
// An existing or expired lock is never automatically removed.
export async function holdWriterLock({ lockPath, maxHoldMs = MAX_HOLD_MS }) {
  if (!path.isAbsolute(lockPath) || !Number.isSafeInteger(maxHoldMs) || maxHoldMs <= 0 || maxHoldMs > MAX_HOLD_MS) {
    throw new Error('An absolute lock path and a hold limit of at most 10 hours are required.')
  }

  const report = (status, detail = {}) => {
    process.stdout.write(`${JSON.stringify({ status, pid: process.pid, lockPath, ...detail })}\n`)
  }
  await mkdir(path.dirname(lockPath), { recursive: true })
  let handle
  try {
    handle = await open(lockPath, 'wx', 0o600)
  } catch (error) {
    if (error.code !== 'EEXIST') throw error
    report('skipped', { reason: 'existing-lock' })
    return 0
  }

  const token = randomUUID()
  try {
    await handle.writeFile(JSON.stringify({ token, pid: process.pid, acquiredAt: new Date().toISOString() }))
    await handle.sync()
  } catch (error) {
    await handle.close()
    // A partial lock remains a blocker rather than making a second writer safe to start.
    throw error
  }
  const identity = await handle.stat()

  return await new Promise((resolve) => {
    let finishing = false
    const input = createInterface({ input: process.stdin, terminal: false })
    let timer
    const finish = async (reason, release) => {
      if (finishing) return
      finishing = true
      clearTimeout(timer)
      process.removeListener('SIGTERM', onSignal)
      process.removeListener('SIGINT', onSignal)
      input.close()
      process.stdin.pause()
      process.stdin.destroy()
      let code = 0
      try {
        if (!release) {
          code = 2
          report('blocked', { reason })
        } else {
          const saved = JSON.parse(await readFile(lockPath, 'utf8'))
          const current = await stat(lockPath)
          if (saved.token !== token || current.dev !== identity.dev || current.ino !== identity.ino) {
            throw new Error('Lock ownership changed; the current lock has been preserved.')
          }
          await unlink(lockPath)
          report('released', { reason })
        }
      } catch (error) {
        code = 2
        report('blocked', { reason: 'ownership-or-release-failed', message: error.message })
      } finally {
        await handle.close()
        resolve(code)
      }
    }
    const onSignal = () => { void finish('signal', true) }
    input.on('line', (line) => {
      if (line.trim() === 'release') void finish('release', true)
    })
    input.on('close', () => { void finish('stdin-closed', true) })
    process.on('SIGTERM', onSignal)
    process.on('SIGINT', onSignal)
    timer = setTimeout(() => { void finish('maximum-hold-exceeded', false) }, maxHoldMs)
    report('acquired')
  })
}

async function main() {
  const args = process.argv.slice(2)
  if (args.length === 1 && args[0] === '--help') {
    process.stdout.write('node scripts/overnight-guard.mjs --hold [--lock-path <absolute path>]\nKeep stdin open while writing; send release and a newline when finished.\n')
    return 0
  }
  if (args[0] !== '--hold' || (args.length !== 1 && !(args.length === 3 && args[1] === '--lock-path'))) {
    throw new Error('Use --hold, optionally followed by --lock-path <absolute path>.')
  }
  return await holdWriterLock({ lockPath: args[2] ?? defaultLockPath })
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    process.exitCode = await main()
  } catch (error) {
    process.stderr.write(`${JSON.stringify({ status: 'blocked', message: error.message })}\n`)
    process.exitCode = 2
  }
}
