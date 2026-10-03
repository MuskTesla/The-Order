// Creates only the public fictional trial library; never copies a user profile.
import { promises as fs } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { deflateSync } from 'node:zlib'

const project = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const iterationFlag = process.argv.indexOf('--iteration')
const iteration = iterationFlag < 0 ? '01' : process.argv[iterationFlag + 1]
if (!['01', '02', '03'].includes(iteration)) throw Error('未知候选轮次。')
const profile = path.join(project, '.artifacts', `iteration-${iteration}`, 'trial-profile')
await fs.mkdir(profile, { recursive: true })
const file = path.join(profile, 'library.json')
try { await fs.access(file); console.log('保留已有隔离试用书架。'); process.exit(0) } catch (error) { if (error.code !== 'ENOENT') throw error }
const date = '2026-10-03T00:00:00.000Z'
const names = ['【虚构测试】湖岸来信', '【虚构测试】当星光再次照亮那个没有名字的小镇时我们仍记得夏日约定', '【虚构测试】待补资料的小镇', '【虚构测试】尚未安装的约定', '【虚构测试】下一次相遇']
const games = []
// Original raster fixtures for choosing a local file in the isolated trial.
// No photo or game artwork is copied into these generated files.
if (iteration === '03') {
  const photos = path.join(profile, 'synthetic-photos')
  await fs.mkdir(photos, { recursive: true })
  const crc = buffer => {
    let value = 0xffffffff
    for (const byte of buffer) {
      value ^= byte
      for (let bit = 0; bit < 8; bit++) value = (value >>> 1) ^ (value & 1 ? 0xedb88320 : 0)
    }
    return (value ^ 0xffffffff) >>> 0
  }
  const chunk = (kind, bytes) => {
    const name = Buffer.from(kind), length = Buffer.alloc(4), checksum = Buffer.alloc(4)
    length.writeUInt32BE(bytes.length); checksum.writeUInt32BE(crc(Buffer.concat([name, bytes])))
    return Buffer.concat([length, name, bytes, checksum])
  }
  for (const [name, width, height] of [['landscape', 1000, 600], ['portrait', 600, 900], ['square', 800, 800]]) {
    const header = Buffer.alloc(13); header.writeUInt32BE(width, 0); header.writeUInt32BE(height, 4); header[8] = 8; header[9] = 2
    const pixels = Buffer.alloc((width * 3 + 1) * height)
    for (let y = 0; y < height; y++) for (let x = 0; x < width; x++) {
      const offset = y * (width * 3 + 1) + 1 + x * 3
      const frame = x < width * .06 || x > width * .94 || y < height * .06 || y > height * .94
      pixels[offset] = frame ? 246 : Math.round(70 + 130 * x / width)
      pixels[offset + 1] = frame ? 227 : Math.round(100 + 100 * y / height)
      pixels[offset + 2] = frame ? 169 : 190
    }
    const png = Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), chunk('IHDR', header), chunk('IDAT', deflateSync(pixels)), chunk('IEND', Buffer.alloc(0))])
    await fs.writeFile(path.join(photos, `${name}.png`), png, { flag: 'wx' })
  }
  await fs.writeFile(path.join(photos, 'README.txt'), '原创合成测试图片，可自由用于本地封面试用；不是个人照片或游戏素材。\n', { flag: 'wx' })
}
for (const [i, name] of names.entries()) {
  const folder = path.join(profile, 'synthetic-games', `game-${i}`)
  if (i < 3) { await fs.mkdir(folder, { recursive: true }); await fs.writeFile(path.join(folder, 'SYNTHETIC.txt'), '虚构测试目录，没有游戏、存档或可执行程序。\n') }
  games.push({
    id: `trial-game-${i}`, name, nameIsCustom: true,
    metadata: i === 2 ? null : { id: iteration === '03' ? String(987000 + i) : `trial-work-${i}`, title: name, originalTitle: name, aliases: [], coverUrl: null, bannerUrl: null, description: '这是虚构的试用资料。没有使用游戏图片、正式书架、密钥或真实游玩记录。候选程序使用正常桌面入口；详情联网请求可能提示尚未配置密钥。', releaseDate: '2025-01-01', releaseDatePrecision: 'day', developers: ['虚构测试工房'], tags: ['虚构测试'], ratings: [{ source: 'bangumi', score: 8, scale: 10, votes: null }], characters: [], references: [], webUrl: null, contentRating: '全年龄', fetchedAt: date },
    installations: i < 3 ? [{ id: `trial-install-${i}`, folderPath: folder, executablePath: null, arguments: [] }] : [],
    preferredInstallationId: i < 3 ? `trial-install-${i}` : null,
    status: ['planned', 'playing', 'paused', 'planned', 'completed'][i], personalRating: i === 0 ? 0 : null, favorite: i === 0,
    categories: ['虚构测试'], customTags: ['隔离试用'], notes: '仅用于候选版本试用，可修改这些虚构个人字段。',
    totalPlaySeconds: 0, playCount: 0, sessions: [], addedAt: date, lastPlayedAt: null,
  })
}
await fs.writeFile(file, JSON.stringify({ format: 'kun-gal-manager', version: 1, exportedAt: date, games }, null, 2), { flag: 'wx' })
console.log('已建立虚构测试书架：' + profile)
