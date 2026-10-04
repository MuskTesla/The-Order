import assert from 'node:assert/strict'
import { promises as fs } from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { test } from 'node:test'
import { prepareTrialProfile, trialMarker } from '../scripts/overnight-trial-data.mjs'

async function fixture(t) {
  const temporaryRoot = path.resolve(os.tmpdir())
  const directory = await fs.mkdtemp(path.join(temporaryRoot, 'the-order-night-trial-test-'))
  t.after(async () => {
    assert.equal(path.dirname(directory), temporaryRoot)
    assert.ok(path.basename(directory).startsWith('the-order-night-trial-test-'))
    await fs.rm(directory, { recursive: true, force: true })
  })
  return { directory, profile: path.join(directory, 'profile') }
}

test('creates a fictional offline profile with genuinely encoded local photos and preserves subsequent trial edits', { timeout: 30000 }, async t => {
  const { directory, profile } = await fixture(t)
  assert.deepEqual(await prepareTrialProfile({ profile }), { profile, created: true })
  assert.equal((await fs.readFile(path.join(profile, '.synthetic-profile'), 'utf8')).trim(), trialMarker)
  const library = JSON.parse(await fs.readFile(path.join(profile, 'library.json'), 'utf8'))
  assert.equal(library.format, 'kun-gal-manager'); assert.equal(library.version, 1); assert.equal(library.games.length, 6)
  assert.equal(await fs.stat(path.join(profile, 'settings.json')).catch(error => error.code), 'ENOENT')
  for (const [i, game] of library.games.entries()) {
    assert.match(game.name, /虚构试用/); assert.match(game.notes, /模拟个人记录/)
    assert.equal(game.metadata?.coverUrl ?? null, null)
    assert.deepEqual(game.metadata?.references ?? [], [])
    if (game.metadata) assert.match(game.metadata.id, /^[0-9]{1,20}$/)
    const jpeg = Buffer.from(game.localCover.dataUrl.split(',')[1], 'base64')
    assert.ok(jpeg.length < 128 * 1024 && jpeg.length > 1000)
    assert.deepEqual([...jpeg.subarray(0, 2)], [255, 216]); assert.deepEqual([...jpeg.subarray(-2)], [255, 217])
    assert.ok(jpeg.includes(Buffer.from([255, 218])))
    assert.deepEqual(await fs.readFile(path.join(profile, 'synthetic-photos', `fictional-cover-${i}.jpg`)), jpeg)
    assert.deepEqual([...Buffer.from(await fs.readFile(path.join(profile, 'synthetic-photos', `fictional-cover-${i}.png`))).subarray(0, 8)], [137, 80, 78, 71, 13, 10, 26, 10])
    for (const installation of game.installations) {
      assert.equal(installation.executablePath, null)
      assert.ok(installation.folderPath.startsWith(profile + path.sep))
      assert.match(await fs.readFile(path.join(installation.folderPath, 'SYNTHETIC.txt'), 'utf8'), /没有游戏/)
    }
    assert.equal(game.totalPlaySeconds, game.sessions.reduce((sum, session) => sum + session.seconds, 0))
  }
  assert.deepEqual((await fs.readdir(directory)).sort(), ['profile'])
  const edited = JSON.stringify({ ...library, games: library.games.map((game, i) => i === 0 ? { ...game, notes: '用户试用后修改的内容' } : game) })
  await fs.writeFile(path.join(profile, 'library.json'), edited)
  await fs.writeFile(path.join(profile, 'settings.json'), '{"sentinel":"keep trial settings"}')
  assert.deepEqual(await prepareTrialProfile({ profile }), { profile, created: false })
  assert.equal(await fs.readFile(path.join(profile, 'library.json'), 'utf8'), edited)
  assert.equal(await fs.readFile(path.join(profile, 'settings.json'), 'utf8'), '{"sentinel":"keep trial settings"}')
})

test('unknown, wrong-marker and partial profiles are rejected without creating or overwriting files', async t => {
  const { directory, profile } = await fixture(t)
  await fs.mkdir(profile)
  await assert.rejects(prepareTrialProfile({ profile }), /来源不明或不完整/)
  assert.deepEqual(await fs.readdir(profile), [])
  const bytes = '{"keep":"unknown library"}'
  await fs.writeFile(path.join(profile, 'library.json'), bytes)
  await fs.writeFile(path.join(profile, '.synthetic-profile'), 'another profile')
  await assert.rejects(prepareTrialProfile({ profile }), /不是本晚虚构/)
  assert.equal(await fs.readFile(path.join(profile, 'library.json'), 'utf8'), bytes)
  assert.deepEqual((await fs.readdir(directory)).sort(), ['profile'])
  await fs.unlink(path.join(profile, 'library.json'))
  await fs.writeFile(path.join(profile, '.synthetic-profile'), trialMarker)
  await assert.rejects(prepareTrialProfile({ profile }), /来源不明或不完整/)
  assert.deepEqual(await fs.readdir(profile), ['.synthetic-profile'])
})

test('relative targets and a linked parent cannot redirect writes to another profile', async t => {
  const { directory } = await fixture(t)
  await assert.rejects(prepareTrialProfile({ profile: 'relative-profile' }), /绝对路径/)
  const actual = path.join(directory, 'actual'), link = path.join(directory, 'linked')
  await fs.mkdir(actual)
  await fs.symlink(actual, link, process.platform === 'win32' ? 'junction' : 'dir')
  await assert.rejects(prepareTrialProfile({ profile: path.join(link, 'profile') }), /经过链接/)
  assert.deepEqual(await fs.readdir(actual), [])
})

test('two parallel preparations converge on one complete profile without leaving staging directories', { timeout: 30000 }, async t => {
  const { directory, profile } = await fixture(t)
  const results = await Promise.all([prepareTrialProfile({ profile }), prepareTrialProfile({ profile })])
  assert.equal(results.filter(result => result.created).length, 1)
  assert.equal(results.filter(result => !result.created).length, 1)
  assert.equal(JSON.parse(await fs.readFile(path.join(profile, 'library.json'), 'utf8')).games.length, 6)
  assert.deepEqual(await fs.readdir(directory), ['profile'])
})
