import assert from 'node:assert/strict'
import { promises as fs } from 'node:fs'
import { createServer } from 'node:http'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { _electron as electron } from '../node_modules/playwright/index.mjs'

const project = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const version = JSON.parse(await fs.readFile(path.join(project, 'package.json'), 'utf8')).version
const temporaryRoot = path.resolve(os.tmpdir())
const temporary = await fs.mkdtemp(path.join(temporaryRoot, 'the-order-production-origin-'))
let requests = 0, application
const server = createServer((_request, response) => {
  requests++
  response.writeHead(200, { 'content-type': 'text/html' })
  response.end('<!doctype html><title>Synthetic development page</title>Development fixture')
})
const errors = []
try {
  await new Promise((resolve, reject) => { server.once('error', reject); server.listen(5173, '127.0.0.1', resolve) })
  application = await electron.launch({
    executablePath: path.join(project, 'release/win-unpacked/The Order.exe'), args: [],
    env: { ...process.env, KGM_DATA_DIR: path.join(temporary, 'profile'), KGM_DEV_URL: 'http://127.0.0.1:5173' }, timeout: 30000
  })
  const identity = await application.evaluate(({ app }) => ({ packaged: app.isPackaged, version: app.getVersion() }))
  assert.equal(identity.packaged, true)
  assert.equal(identity.version, version)
  const page = await application.firstWindow()
  page.on('pageerror', error => errors.push(error.message))
  await page.waitForFunction(() => window.kunManager !== undefined)
  assert.equal(page.url(), 'kun-manager://app/index.html')
  assert.equal(requests, 0, 'A packaged app must ignore the development origin')
  const responsePromise = page.waitForResponse(response => response.url() === 'kun-manager://app/index.html')
  await page.reload()
  const response = await responsePromise
  const csp = (await response.allHeaders())['content-security-policy']
  assert.ok(csp)
  assert.ok(!csp.includes('127.0.0.1:5173'), 'Production CSP must not grant the development origin')
  const blocked = await page.evaluate(async () => {
    try { await fetch('http://127.0.0.1:5173/production-csp-check'); return false } catch { return true }
  })
  assert.equal(blocked, true)
  assert.equal(requests, 0, 'Production CSP must block the development request before it reaches the server')
  const state = await page.evaluate(() => window.kunManager.getState())
  assert.equal(state.version, version)
  assert.equal(state.games.length, 0)
  assert.deepEqual(errors, [])
  console.log(JSON.stringify({ ...identity, origin: page.url(), developmentRequests: requests, productionCsp: csp, developmentFetchBlocked: blocked, rendererErrors: errors }))
} finally {
  if (application) await application.close()
  await new Promise(resolve => server.close(resolve))
  if (path.dirname(path.resolve(temporary)) !== temporaryRoot || !path.basename(temporary).startsWith('the-order-production-origin-')) throw new Error('Unexpected temporary directory')
  await fs.rm(temporary, { recursive: true, force: true })
}
