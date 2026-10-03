import assert from 'node:assert/strict'
import { spawn } from 'node:child_process'
import { once } from 'node:events'
import { mkdtemp, readFile, rm, writeFile } from 'node:fs/promises'
import os from 'node:os'
import path from 'node:path'
import { test } from 'node:test'
import { fileURLToPath } from 'node:url'

const script = fileURLToPath(new URL('../scripts/overnight-guard.mjs', import.meta.url))

async function fixture(t) {
  const temporaryRoot = path.resolve(os.tmpdir())
  const directory = await mkdtemp(path.join(temporaryRoot, 'the-order-guard-test-'))
  t.after(() => {
    const resolved = path.resolve(directory)
    assert.equal(path.dirname(resolved), temporaryRoot)
    assert.ok(path.basename(resolved).startsWith('the-order-guard-test-'))
    return rm(resolved, { recursive: true, force: true })
  })
  return path.join(directory, 'writer.lock')
}

function start(t, lockPath, options = {}) {
  const args = options.holdMs
    ? ['--input-type=module', '-e', `import { holdWriterLock } from ${JSON.stringify(new URL('../scripts/overnight-guard.mjs', import.meta.url).href)}; process.exitCode = await holdWriterLock({lockPath: process.argv[1], maxHoldMs: ${options.holdMs}});`, lockPath]
    : [script, '--hold', '--lock-path', lockPath]
  const child = spawn(process.execPath, args, { stdio: ['pipe', 'pipe', 'pipe'] })
  const messages = []
  let buffer = ''
  let stderr = ''
  child.stdout.setEncoding('utf8')
  child.stderr.setEncoding('utf8')
  child.stdout.on('data', (chunk) => {
    buffer += chunk
    for (;;) {
      const end = buffer.indexOf('\n')
      if (end < 0) break
      messages.push(JSON.parse(buffer.slice(0, end)))
      buffer = buffer.slice(end + 1)
    }
  })
  child.stderr.on('data', (chunk) => { stderr += chunk })
  const closed = once(child, 'close').then(([code]) => ({ code, messages, stderr }))
  t.after(async () => {
    if (child.exitCode === null) {
      child.stdin.end('release\n')
      await Promise.race([closed, new Promise((resolve) => setTimeout(resolve, 1000))])
      if (child.exitCode === null) child.kill()
      await closed
    }
  })
  async function waitFor(status) {
    const timeout = setTimeout(() => child.emit('guard-test-timeout'), 3000)
    try {
      while (!messages.some((message) => message.status === status)) {
        if (child.exitCode !== null) assert.fail(`Exited before ${status}: ${stderr}`)
        await Promise.race([
          once(child.stdout, 'data'),
          once(child, 'close').then(() => assert.fail(`Exited before ${status}: ${stderr}`)),
          once(child, 'guard-test-timeout').then(() => assert.fail(`Timed out before ${status}`)),
        ])
      }
      return messages.find((message) => message.status === status)
    } finally {
      clearTimeout(timeout)
    }
  }
  return { child, closed, waitFor }
}

test('atomic acquisition prevents a second writer, and release allows the next writer', { timeout: 10000 }, async (t) => {
  const lockPath = await fixture(t)
  const first = start(t, lockPath)
  const acquired = await first.waitFor('acquired')
  const saved = JSON.parse(await readFile(lockPath, 'utf8'))
  assert.equal(saved.pid, acquired.pid)
  assert.match(saved.token, /^[a-f0-9-]{36}$/)
  const second = await start(t, lockPath).closed
  assert.equal(second.code, 0)
  assert.equal(second.messages[0].status, 'skipped')
  assert.equal(await readFile(lockPath, 'utf8'), JSON.stringify(saved))
  first.child.stdin.write('release\n')
  assert.equal((await first.closed).code, 0)
  await assert.rejects(readFile(lockPath), { code: 'ENOENT' })
  const third = start(t, lockPath)
  await third.waitFor('acquired')
  third.child.stdin.end('release\n')
  assert.equal((await third.closed).code, 0)
})

test('stdin EOF releases only the held lock', { timeout: 5000 }, async (t) => {
  const lockPath = await fixture(t)
  const holder = start(t, lockPath)
  await holder.waitFor('acquired')
  holder.child.stdin.end()
  const result = await holder.closed
  assert.equal(result.code, 0)
  assert.equal(result.messages.at(-1).reason, 'stdin-closed')
  await assert.rejects(readFile(lockPath), { code: 'ENOENT' })
})

test('unknown and malformed existing locks remain untouched', { timeout: 5000 }, async (t) => {
  const lockPath = await fixture(t)
  await writeFile(lockPath, 'unknown writer, do not remove')
  const result = await start(t, lockPath).closed
  assert.equal(result.code, 0)
  assert.equal(result.messages[0].status, 'skipped')
  assert.equal(await readFile(lockPath, 'utf8'), 'unknown writer, do not remove')
})

test('a holder cannot release a lock whose token has changed', { timeout: 5000 }, async (t) => {
  const lockPath = await fixture(t)
  const holder = start(t, lockPath)
  await holder.waitFor('acquired')
  const foreign = JSON.stringify({ token: 'different-owner', pid: 123 })
  await writeFile(lockPath, foreign)
  holder.child.stdin.end('release\n')
  const result = await holder.closed
  assert.equal(result.code, 2)
  assert.equal(result.messages.at(-1).status, 'blocked')
  assert.equal(await readFile(lockPath, 'utf8'), foreign)
})

test('expiry leaves a blocker and prevents another acquisition', { timeout: 5000 }, async (t) => {
  const lockPath = await fixture(t)
  const holder = start(t, lockPath, { holdMs: 60 })
  await holder.waitFor('acquired')
  const result = await holder.closed
  assert.equal(result.code, 2)
  assert.equal(result.messages.at(-1).reason, 'maximum-hold-exceeded')
  const saved = await readFile(lockPath, 'utf8')
  const next = await start(t, lockPath).closed
  assert.equal(next.messages[0].status, 'skipped')
  assert.equal(await readFile(lockPath, 'utf8'), saved)
})
