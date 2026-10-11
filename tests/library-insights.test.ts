import assert from 'node:assert/strict'
import { test } from 'node:test'
import { execFileSync } from 'node:child_process'
import { formatPlayTime, getContinueStory, getLaunchInstallation, getLibraryInsights, localDayKey, sessionSecondsInRange } from '../src/library-insights'
import type { GameInstallation, LibraryGame, PlaySession, WorkMetadata } from '../src/shared/types'

const location = (id = 'local', executablePath: string | null = 'C:\\Synthetic\\story.exe'): GameInstallation => ({ id, folderPath: 'C:\\Synthetic', executablePath, arguments: [] })
const game = (id: string, patch: Partial<LibraryGame> = {}): LibraryGame => ({ id, name: id, metadata: null, installations: [location()], preferredInstallationId: 'local', status: 'planned', personalRating: null, favorite: false, categories: [], customTags: [], notes: '', totalPlaySeconds: 0, playCount: 0, sessions: [], addedAt: '2026-10-01T00:00:00.000Z', lastPlayedAt: null, ...patch })
const date = (day: number, hour = 0, minute = 0) => new Date(2026, 9, day, hour, minute)
const play = (start: Date, end: Date, duration: number, id = 'session'): PlaySession => ({ id, startedAt: start.toISOString(), endedAt: end.toISOString(), seconds: duration })

test('continue story prioritizes the active game without offering another launch', () => {
  const current = game('current', { installations: [] }), other = game('other', { status: 'playing' })
  const choice = getContinueStory([other, current], { gameId: current.id, installationId: 'old', startedAt: date(3).toISOString(), elapsedSeconds: 9 })
  assert.equal(choice?.game.id, current.id); assert.equal(choice?.reason, 'active'); assert.equal(choice?.canPlay, false)
})

test('continue story prefers playable playing games then valid recent installations, never a random alternate executable', () => {
  const invalid = game('invalid', { status: 'playing', lastPlayedAt: date(3).toISOString(), installations: [location('preferred', null), location('other')], preferredInstallationId: 'preferred' })
  const playing = game('playing', { status: 'playing', lastPlayedAt: date(1).toISOString() })
  const recent = game('recent', { status: 'completed', lastPlayedAt: date(2).toISOString() })
  assert.equal(getLaunchInstallation(invalid), null)
  assert.equal(getContinueStory([invalid, recent, playing])?.game.id, 'playing')
  assert.equal(getContinueStory([invalid, recent])?.game.id, 'recent')
  assert.equal(getContinueStory([invalid]), null)
  assert.equal(getContinueStory([game('unplayed')]), null)
})

test('continue story does not show an unlaunchable recent game, while an active session remains viewable', () => {
  const unavailable = game('unavailable', { lastPlayedAt: date(2).toISOString(), installations: [] })
  assert.equal(getContinueStory([unavailable]), null)
  const choice = getContinueStory([unavailable], { gameId: unavailable.id, installationId: 'removed', startedAt: date(3).toISOString(), elapsedSeconds: 30 })
  assert.equal(choice?.reason, 'active')
  assert.equal(choice?.canPlay, false)
  assert.equal(getContinueStory([game('another', { status: 'playing' })], { gameId: 'missing', installationId: 'removed', startedAt: date(3).toISOString(), elapsedSeconds: 30 }), null)
})

test('preferred-or-only selection matches the tracker and does not mutate library order', () => {
  const single = game('single', { preferredInstallationId: null })
  assert.equal(getLaunchInstallation(single)?.id, 'local')
  assert.equal(getLaunchInstallation(game('many', { preferredInstallationId: null, installations: [location('one'), location('two')] })), null)
  assert.equal(getLaunchInstallation(game('bad extension', { installations: [location('local', 'C:\\Synthetic\\readme.txt')] })), null)
  const entries = [game('old', { lastPlayedAt: date(1).toISOString() }), game('new', { lastPlayedAt: date(2).toISOString() })]
  const before = structuredClone(entries)
  assert.equal(getContinueStory(entries)?.game.id, 'new')
  getLibraryInsights(entries, null, date(3, 12))
  assert.deepEqual(entries, before)
})

test('cross-midnight recorded seconds are divided by actual overlap with integer-second conservation', () => {
  const session = play(date(1, 23, 30), date(2, 1, 30), 3601)
  const result = getLibraryInsights([game('one', { sessions: [session], totalPlaySeconds: 3601 })], null, date(3, 12))
  const first = result.days.find(day => day.key === localDayKey(date(1)))!, second = result.days.find(day => day.key === localDayKey(date(2)))!
  assert.equal(first.seconds, 900); assert.equal(second.seconds, 2701)
  assert.equal(result.recentSeconds, 3601); assert.equal(result.totalSeconds, 3601)
  assert.equal(result.activeDays, 2)
})

test('multi-day rounding, midnight endpoints and a clock-adjusted zero-length interval keep their recorded seconds', () => {
  const session = play(date(1), date(4), 7)
  const parts = [1, 2, 3, 4].map(day => sessionSecondsInRange(session, date(day).getTime(), date(day + 1).getTime()))
  assert.deepEqual(parts, [2, 3, 2, 0]); assert.equal(parts.reduce((sum, part) => sum + part, 0), 7)
  const frozen = play(date(2), date(2), 30)
  assert.equal(sessionSecondsInRange(frozen, date(1).getTime(), date(2).getTime()), 0)
  assert.equal(sessionSecondsInRange(frozen, date(2).getTime(), date(3).getTime()), 30)
  assert.equal(sessionSecondsInRange(play(date(2), date(1), 30), date(1).getTime(), date(3).getTime()), 0)
})

test('week starts on Monday and only the part of a Sunday session inside this week is counted', () => {
  const session = play(date(4, 23), date(5, 1), 7200) // 2026-10-04 is Sunday.
  const result = getLibraryInsights([game('one', { sessions: [session], totalPlaySeconds: 7200 })], null, date(5, 12))
  assert.equal(result.weekSeconds, 3600)
  assert.equal(result.recentSeconds, 7200)
  assert.equal(result.days.length, 14); assert.equal(result.days.at(-1)?.isToday, true)
})

test('a saved active checkpoint is counted once and is not displayed as a finished recent session', () => {
  const checkpoint = play(date(3, 10), date(3, 10, 10), 600)
  const previous = play(date(2, 10), date(2, 10, 5), 300, 'previous')
  const entry = game('running', { sessions: [previous, checkpoint], totalPlaySeconds: 900 })
  const active = { gameId: entry.id, installationId: 'local', startedAt: checkpoint.startedAt, elapsedSeconds: 617 }
  const result = getLibraryInsights([entry], active, date(3, 10, 11))
  assert.equal(result.totalSeconds, 900); assert.equal(result.recentSeconds, 900)
  assert.equal(result.active?.elapsedSeconds, 617)
  assert.deepEqual(result.recent.map(row => row.session.id), ['previous'])
  assert.equal(getLibraryInsights([entry], null, date(3, 11)).recent[0]?.session.id, 'session')
})

test('missing historical session dates are disclosed rather than inventing activity', () => {
  const result = getLibraryInsights([game('legacy', { totalPlaySeconds: 10000, status: 'completed' }), game('wish', { installations: [] })], null, date(3, 12))
  assert.equal(result.undatedSeconds, 10000); assert.equal(result.recentSeconds, 0)
  assert.equal(result.completionPercent, 50); assert.equal(result.localCount, 1)
  assert.equal(result.unmatchedCount, 1); assert.equal(result.recent.length, 0)
  assert.deepEqual(result.mostPlayed.map(entry => entry.id), ['legacy'])
  const empty = getLibraryInsights([], null, date(3))
  assert.equal(empty.completionPercent, 0); assert.equal(empty.activeDays, 0); assert.equal(empty.totalSeconds, 0)
  assert.equal(empty.days.every(day => day.seconds === 0), true)
})

test('missing-cover counts follow local matching eligibility and history is bounded to eight records', () => {
  const metadata = { coverUrl: 'https://synthetic.invalid/cover.png' } as WorkMetadata
  const entries = [game('complete', { metadata }), game('no cover', { metadata: { ...metadata, coverUrl: null } }), game('no metadata'), game('wish', { installations: [] })]
  entries[0]!.sessions = Array.from({ length: 10 }, (_, i) => play(date(1, i), date(1, i, 1), 60, String(i)))
  entries[0]!.totalPlaySeconds = 600
  const result = getLibraryInsights(entries, null, date(3))
  assert.equal(result.unmatchedCount, 2); assert.equal(result.localCount, 3)
  assert.deepEqual(result.recent.map(row => row.session.id), ['9', '8', '7', '6', '5', '4', '3', '2'])
})

test('invalid timestamps and future days do not fabricate recent activity', () => {
  const sessions = [play(date(4), date(4, 1), 3600), { ...play(date(1), date(1, 1), 100), startedAt: 'unknown' }]
  const result = getLibraryInsights([game('one', { sessions, totalPlaySeconds: 3700 })], null, date(3, 12))
  assert.equal(result.recentSeconds, 0); assert.equal(result.recent.length, 0)
  assert.equal(formatPlayTime(Number.NaN), '0 秒'); assert.equal(formatPlayTime(59), '59 秒')
  assert.equal(formatPlayTime(3661), '1 小时 1 分')
})

test('natural-day allocation handles a 23-hour daylight-saving day without shifting neighbouring dates', () => {
  const moduleUrl = new URL('../src/library-insights.ts', import.meta.url).href
  const script = `import { sessionSecondsInRange } from ${JSON.stringify(moduleUrl)};
    const start = new Date(2026, 2, 7, 12), end = new Date(2026, 2, 9, 12);
    const session = { id:'dst', startedAt:start.toISOString(), endedAt:end.toISOString(), seconds:(end-start)/1000 };
    console.log(JSON.stringify([7,8,9].map(day=>sessionSecondsInRange(session, +new Date(2026,2,day), +new Date(2026,2,day+1)))));`
  const output = execFileSync(process.execPath, ['--import', 'tsx', '--input-type=module', '-e', script], { encoding: 'utf8', env: { ...process.env, TZ: 'America/New_York' }, windowsHide: true })
  assert.deepEqual(JSON.parse(output), [43200, 82800, 43200])
})
