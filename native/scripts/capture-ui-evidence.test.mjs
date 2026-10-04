import test from 'node:test'
import assert from 'node:assert/strict'
import fs from 'node:fs/promises'
import os from 'node:os'
import path from 'node:path'
import { captureEvidence, runtimeFromState } from './capture-ui-evidence.mjs'
const fixture = (changes = {}) => {
  const runtime = { schema: 1, build: 'mvid', checkpoint: 'candidate-sha', processId: 123, timestamp: new Date().toISOString(), itemCount: 10000, totalCount: 10000,
    viewMode: 'grid', theme: 'Dark', readOnly: false, state: 'normal', page: 'Library', viewport: { realizedContainerCount: 51, viewportWidth: 1100, viewportHeight: 600, firstContainerMs: 900 }, ...changes }
  return { window: { id: 1, app: 'synthetic' }, accessibility: { tree: 'text THE_ORDER_RUNTIME ' + JSON.stringify(runtime) + ' ID: RuntimeEvidence' }, screenshots: [{ id: 'fresh', url: 'data:image/png;base64,aGVsbG8=' }] }
}
for (const [key, value] of Object.entries({ itemCount: 1000, totalCount: 5000, viewMode: 'list', theme: 'Light', readOnly: true, state: 'error', page: 'Details', build: 'another-build', importStage: 'Scanning', candidateCount: 20, installationCount: 2, missingInstallations: 1, localCoverCount: 10000, scanRoot: 'Z:\\different', duplicateCandidates: 2, onlineState: 'Loaded', onlineCount: 2, onlineDetails: 'Loaded', onlineWorkId: '86', onlineHasCover: true, onlineConnection: 'Unauthorized' })) {
  test(`reject mismatched ${key}`, () => assert.throws(() => runtimeFromState(fixture(), { [key]: value }), /Scenario mismatch/))
}
test('reject stale or unrealized capture', () => {
  assert.throws(() => runtimeFromState(fixture({ timestamp: '2000-01-01' }), {}), /stale/)
  assert.throws(() => runtimeFromState(fixture({ viewport: { realizedContainerCount: 0 } }), {}), /viewport/)
})

test('image workload assertions use actual nested metrics', () => {
  const state = fixture({ imageMetrics: { Requests: 42, Errors: 1, Outstanding: 0, ActiveDecodes: 0, MaximumActiveDecodes: 2 } })
  runtimeFromState(state, { 'imageMetrics.Errors': 1, 'imageMetrics.Outstanding': 0 })
  assert.throws(() => runtimeFromState(state, { 'imageMetrics.Errors': 0 }), /mismatch/)
  assert.throws(() => runtimeFromState(fixture({ imageMetrics: { ActiveDecodes: 5, MaximumActiveDecodes: 2, Outstanding: 1 } }), {}), /metrics/)
})

for (const [key, value] of Object.entries({ 'recognition.status': 'Stale', 'recognition.candidateCount': 2,
  'recognition.candidates.0.veto': true, 'recognition.candidates.0.high': true, 'recognition.unavailable': true,
  'recognition.decision': 'Committed', 'recognition.progress.Processing': 2, 'recognition.reviewCount': 7 })) {
  test(`reject mismatched ${key}`, () => assert.throws(() => runtimeFromState(fixture({ page:'Recognition', recognition:{ status:'NeedsReview', candidateCount:0, candidates:[], unavailable:false, decision:null, progress:{ Processing:0 }, reviewCount:0 } }), { [key]:value }), /Scenario mismatch/))
}
test('every scene obtains its own snapshot and mismatch writes no evidence', async () => {
  const outputDirectory = await fs.mkdtemp(path.join(os.tmpdir(), 'the-order-capture-test-'))
  let current = fixture(); let captures = 0
  const sky = { get_window_state: async () => { captures++; return current } }
  try {
    await captureEvidence({ sky, window: current.window, expected: { itemCount: 10000 }, scenarioId: 'first', outputDirectory })
    current = fixture({ theme: 'Light', viewMode: 'list' })
    const result = await captureEvidence({ sky, window: current.window, expected: { theme: 'Light', viewMode: 'list' }, scenarioId: 'second', outputDirectory })
    assert.equal(result.manifest.runtime.theme, 'Light'); assert.equal(captures, 2)
    const first = JSON.parse(await fs.readFile(path.join(outputDirectory, 'first/manifest.json')))
    assert.equal(first.runtime.theme, 'Dark')
    await assert.rejects(captureEvidence({ sky, window: current.window, expected: { itemCount: 1 }, scenarioId: 'bad', outputDirectory }), /mismatch/)
    await assert.rejects(fs.stat(path.join(outputDirectory, 'bad')), { code: 'ENOENT' })
  } finally { await fs.rm(outputDirectory, { recursive: true, force: true }) }
})
