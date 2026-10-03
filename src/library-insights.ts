import type { ActiveSession, GameInstallation, LibraryGame, PlaySession } from './shared/types'

const seconds = (value: number): number => Number.isFinite(value) ? Math.max(0, Math.floor(value)) : 0
const timestamp = (value: string | null): number => value ? Date.parse(value) : Number.NaN

/** Match the main process's preferred-or-only installation rule; existence is checked at launch. */
export function getLaunchInstallation(game: LibraryGame): GameInstallation | null {
  const installation = game.installations.find(item => item.id === game.preferredInstallationId)
    ?? (game.installations.length === 1 ? game.installations[0] : undefined)
  return installation?.folderPath && installation.executablePath && /\.exe$/i.test(installation.executablePath)
    ? installation : null
}

export interface ContinueStory { game: LibraryGame; reason: 'active' | 'playing' | 'recent'; canPlay: boolean }
function lastPlayed(game: LibraryGame): number {
  return game.sessions.reduce((latest, session) => {
    const ended = timestamp(session.endedAt)
    return Number.isFinite(ended) ? Math.max(latest, ended) : latest
  }, Number.isFinite(timestamp(game.lastPlayedAt)) ? timestamp(game.lastPlayedAt) : 0)
}
export function getContinueStory(games: LibraryGame[], active?: ActiveSession | null): ContinueStory | null {
  const current = active && games.find(game => game.id === active.gameId)
  if (current) return { game: current, reason: 'active', canPlay: false }
  const recentDates = new Map(games.map(game => [game.id, lastPlayed(game)]))
  const ordered = [...games].sort((a, b) => recentDates.get(b.id)! - recentDates.get(a.id)! || a.name.localeCompare(b.name, 'zh-CN'))
  const playing = ordered.filter(game => game.status === 'playing')
  const game = playing.find(game => getLaunchInstallation(game))
    ?? ordered.find(game => recentDates.get(game.id)! > 0 && getLaunchInstallation(game))
    ?? playing[0] ?? ordered.find(game => recentDates.get(game.id)! > 0)
  return game ? { game, reason: game.status === 'playing' ? 'playing' : 'recent', canPlay: !active && !!getLaunchInstallation(game) } : null
}

export function localDayKey(value: Date): string {
  return `${value.getFullYear()}-${String(value.getMonth() + 1).padStart(2, '0')}-${String(value.getDate()).padStart(2, '0')}`
}
function localDay(value: Date, offset = 0): Date { return new Date(value.getFullYear(), value.getMonth(), value.getDate() + offset) }

/** Allocate recorded seconds by wall-time overlap; cumulative rounding conserves integer seconds.
 * The tracker stores cumulative duration, not the position of pauses within a session. */
export function sessionSecondsInRange(session: PlaySession, from: number, to: number): number {
  const start = timestamp(session.startedAt), end = timestamp(session.endedAt), duration = seconds(session.seconds)
  if (!Number.isFinite(start) || !Number.isFinite(end) || end < start || !duration || to <= from) return 0
  return allocatedSeconds(start, end, duration, from, to)
}
function allocatedSeconds(start: number, end: number, duration: number, from: number, to: number): number {
  if (to <= from) return 0
  if (end === start) return start >= from && start < to ? duration : 0
  const left = Math.max(start, from), right = Math.min(end, to)
  if (right <= left) return 0
  return Math.round(duration * (right - start) / (end - start)) - Math.round(duration * (left - start) / (end - start))
}

export interface ActivityDay { key: string; label: string; seconds: number; isToday: boolean }
export interface RecentPlay { game: LibraryGame; session: PlaySession }
export interface LibraryInsights {
  totalSeconds: number; weekSeconds: number; recentSeconds: number; undatedSeconds: number
  localCount: number; unmatchedCount: number; playingCount: number; completedCount: number; completionPercent: number
  days: ActivityDay[]; activeDays: number; recent: RecentPlay[]; mostPlayed: LibraryGame[]
  active: { game: LibraryGame; elapsedSeconds: number; startedAt: string } | null
}

export function getLibraryInsights(games: LibraryGame[], active?: ActiveSession | null, now = new Date()): LibraryInsights {
  const cutoff = now.getTime(), today = localDay(now), weekStart = localDay(now, -((now.getDay() + 6) % 7)).getTime()
  const intervals = Array.from({ length: 14 }, (_, i) => ({ start: localDay(today, i - 13), end: localDay(today, i - 12) }))
  const days = intervals.map(({ start }) => ({ key: localDayKey(start), label: `${start.getMonth() + 1}/${start.getDate()}`, seconds: 0, isToday: start.getTime() === today.getTime() }))
  const activeGame = active && games.find(game => game.id === active.gameId)
  const running = active && activeGame ? { game: activeGame, elapsedSeconds: seconds(active.elapsedSeconds), startedAt: active.startedAt } : null
  let weekSeconds = 0, recordedSeconds = 0
  const recent: RecentPlay[] = []
  for (const game of games) for (const session of game.sessions) {
    const start = timestamp(session.startedAt), end = timestamp(session.endedAt)
    if (!Number.isFinite(start) || !Number.isFinite(end) || end < start || !seconds(session.seconds)) continue
    recordedSeconds += seconds(session.seconds)
    weekSeconds += allocatedSeconds(start, end, seconds(session.seconds), weekStart, cutoff)
    for (let i = 0; i < intervals.length; i++) days[i]!.seconds += allocatedSeconds(start, end, seconds(session.seconds), intervals[i]!.start.getTime(), Math.min(intervals[i]!.end.getTime(), cutoff))
    // An active checkpoint already contributes to saved totals/chart. Present it
    // once in the separate live row, never as a second completed session.
    if (start <= cutoff && !(running?.game.id === game.id && timestamp(running.startedAt) === start)) recent.push({ game, session })
  }
  const totalSeconds = games.reduce((sum, game) => sum + seconds(game.totalPlaySeconds), 0)
  const completedCount = games.filter(game => game.status === 'completed').length
  return {
    totalSeconds, weekSeconds, recentSeconds: days.reduce((sum, day) => sum + day.seconds, 0),
    undatedSeconds: Math.max(0, totalSeconds - recordedSeconds),
    localCount: games.filter(game => game.installations.length > 0).length,
    unmatchedCount: games.filter(game => game.installations.length > 0 && (!game.metadata || !game.metadata.coverUrl)).length,
    playingCount: games.filter(game => game.status === 'playing').length, completedCount,
    completionPercent: games.length ? Math.round(completedCount / games.length * 100) : 0,
    days, activeDays: days.filter(day => day.seconds > 0).length,
    recent: recent.sort((a, b) => timestamp(b.session.endedAt) - timestamp(a.session.endedAt)).slice(0, 8),
    mostPlayed: games.filter(game => seconds(game.totalPlaySeconds) > 0).sort((a, b) => b.totalPlaySeconds - a.totalPlaySeconds || a.name.localeCompare(b.name, 'zh-CN')).slice(0, 5),
    active: running
  }
}

export function formatPlayTime(value: number): string {
  const duration = seconds(value)
  if (duration < 60) return `${duration} 秒`
  if (duration < 3600) return `${Math.floor(duration / 60)} 分钟`
  const hours = Math.floor(duration / 3600), minutes = Math.floor(duration % 3600 / 60)
  return minutes ? `${hours} 小时 ${minutes} 分` : `${hours} 小时`
}
