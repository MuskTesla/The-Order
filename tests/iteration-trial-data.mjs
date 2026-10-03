// Creates only the public fictional trial library; never copies a user profile.
import { promises as fs } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const project = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const profile = path.join(project, '.artifacts', 'iteration-01', 'trial-profile')
await fs.mkdir(profile, { recursive: true })
const file = path.join(profile, 'library.json')
try { await fs.access(file); console.log('保留已有隔离试用书架。'); process.exit(0) } catch (error) { if (error.code !== 'ENOENT') throw error }
const date = '2026-10-03T00:00:00.000Z'
const names = ['【虚构测试】湖岸来信', '【虚构测试】当星光再次照亮那个没有名字的小镇时我们仍记得夏日约定', '【虚构测试】待补资料的小镇', '【虚构测试】尚未安装的约定', '【虚构测试】下一次相遇']
const games = []
for (const [i, name] of names.entries()) {
  const folder = path.join(profile, 'synthetic-games', `game-${i}`)
  if (i < 3) { await fs.mkdir(folder, { recursive: true }); await fs.writeFile(path.join(folder, 'SYNTHETIC.txt'), '虚构测试目录，没有游戏、存档或可执行程序。\n') }
  games.push({
    id: `trial-game-${i}`, name, nameIsCustom: true,
    metadata: i === 2 ? null : { id: `trial-work-${i}`, title: name, originalTitle: name, aliases: [], coverUrl: null, bannerUrl: null, description: '这是虚构的试用资料。没有使用游戏图片、正式书架、密钥或真实游玩记录。候选程序使用正常桌面入口；详情联网请求可能提示尚未配置密钥。', releaseDate: '2025-01-01', releaseDatePrecision: 'day', developers: ['虚构测试工房'], tags: ['虚构测试'], ratings: [{ source: 'bangumi', score: 8, scale: 10, votes: null }], characters: [], references: [], webUrl: null, contentRating: '全年龄', fetchedAt: date },
    installations: i < 3 ? [{ id: `trial-install-${i}`, folderPath: folder, executablePath: null, arguments: [] }] : [],
    preferredInstallationId: i < 3 ? `trial-install-${i}` : null,
    status: ['planned', 'playing', 'paused', 'planned', 'completed'][i], personalRating: i === 0 ? 0 : null, favorite: i === 0,
    categories: ['虚构测试'], customTags: ['隔离试用'], notes: '仅用于候选版本试用，可修改这些虚构个人字段。',
    totalPlaySeconds: 0, playCount: 0, sessions: [], addedAt: date, lastPlayedAt: null,
  })
}
await fs.writeFile(file, JSON.stringify({ format: 'kun-gal-manager', version: 1, exportedAt: date, games }, null, 2), { flag: 'wx' })
console.log('已建立虚构测试书架：' + profile)
