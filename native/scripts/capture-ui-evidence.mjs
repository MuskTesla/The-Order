import fs from 'node:fs/promises'
import path from 'node:path'
import { createHash } from 'node:crypto'

export function runtimeFromState(state, expected, now = Date.now()) {
  const tree = state.accessibility?.tree ?? ''
  const marker = 'THE_ORDER_RUNTIME '
  const line = tree.split('\n').find(line => line.includes(marker))
  if (!line) throw Error('Missing runtime diagnostics; evidence not saved')
  const json = line.slice(line.indexOf(marker) + marker.length)
  const runtime = JSON.parse(json.slice(0, json.lastIndexOf('}') + 1))
  if (runtime.schema !== 1 || !runtime.build || !runtime.checkpoint || !Number.isInteger(runtime.processId)
    || !Number.isFinite(Date.parse(runtime.timestamp)) || Math.abs(now - Date.parse(runtime.timestamp)) > 10000) throw Error('Invalid or stale runtime identity')
  for (const [key, value] of Object.entries(expected)) {
    const actual = key.split('.').reduce((parent, part) => parent?.[part], runtime)
    if (actual !== value) throw Error(`Scenario mismatch: ${key}; requested=${JSON.stringify(value)} actual=${JSON.stringify(actual)}`)
  }
  if (runtime.imageMetrics) {
    const m = runtime.imageMetrics
    for (const value of Object.values(m)) if (!Number.isFinite(value) || value < 0) throw Error('Invalid image metrics')
    if (m.ActiveDecodes > m.MaximumActiveDecodes || m.ActiveDecodes > m.Outstanding) throw Error('Inconsistent image metrics')
  }
  if (!Number.isInteger(runtime.itemCount) || runtime.itemCount < 0 || !Number.isInteger(runtime.totalCount) || runtime.totalCount < runtime.itemCount) throw Error('Invalid runtime counts')
  if (runtime.page === 'Library' && runtime.itemCount > 0 && !['error', 'loading', 'searching'].includes(runtime.state)) {
    if (!(runtime.viewport?.realizedContainerCount > 0 && runtime.viewport.viewportHeight > 0 && runtime.viewport.viewportWidth > 0 && runtime.viewport.firstContainerMs > 0)) throw Error('Library viewport is not realized')
  }
  if (!state.screenshots?.length || !state.window?.id) throw Error('Missing screenshot/window')
  return runtime
}

// The snapshot is acquired here on every call. Never close over a REPL cell's `state`.
// No UI input: callers inspect and operate through Computer Use before invoking capture.
export async function captureEvidence({ sky, window, expected, scenarioId, outputDirectory }) {
  if (!/^[a-z0-9][a-z0-9-]{0,100}$/.test(scenarioId)) throw Error('Invalid scenario ID')
  const captured = await sky.get_window_state({ window, include_screenshot: true, include_text: true })
  const runtime = runtimeFromState(captured, expected)
  const files = []
  const tree = Buffer.from(captured.accessibility.tree)
  files.push({ filename: 'ui.txt', bytes: tree })
  for (const [index, screenshot] of captured.screenshots.entries()) {
    const match = /^data:image\/(png|jpeg);base64,(.+)$/s.exec(screenshot.url)
    if (!match) throw Error('Unsupported screenshot payload')
    files.push({ filename: `window-${index}.${match[1] === 'png' ? 'png' : 'jpg'}`, bytes: Buffer.from(match[2], 'base64') })
  }
  const root = path.join(outputDirectory, scenarioId)
  const manifest = { scenarioId, capturedAt: new Date().toISOString(), expectedState: expected, runtime,
    window: captured.window, files: files.map(file => ({ filename: file.filename, bytes: file.bytes.length, sha256: createHash('sha256').update(file.bytes).digest('hex') })) }
  await fs.mkdir(outputDirectory, { recursive: true })
  await fs.mkdir(root) // Refuse to overwrite an earlier scene or silently reuse an artifact.
  for (const file of files) await fs.writeFile(path.join(root, file.filename), file.bytes, { flag: 'wx' })
  // Manifest is written last; a partial capture has no success marker.
  await fs.writeFile(path.join(root, 'manifest.json'), JSON.stringify(manifest, null, 2) + '\n', { flag: 'wx' })
  return { manifest, state: captured }
}
