// Fictional, offline trial data only. No existing profile is copied or replaced.
import { spawn } from 'node:child_process'
import { randomUUID } from 'node:crypto'
import { promises as fs } from 'node:fs'
import path from 'node:path'
import { createRequire } from 'node:module'
import { fileURLToPath } from 'node:url'
import { deflateSync } from 'node:zlib'

const script = fileURLToPath(import.meta.url)
const project = path.resolve(path.dirname(script), '..')
export const trialProfile = path.join(project, '.artifacts', 'overnight-2026-10-04', 'trial-profile')
export const trialMarker = 'the-order-overnight-2026-10-04-fictional-v1'
const markerFilename = '.synthetic-profile'
const date = '2026-10-04T00:00:00.000Z'

async function requirePlainParents(directory) {
  let current = directory
  while (current !== path.dirname(current)) {
    try {
      const entry = await fs.lstat(current)
      if (entry.isSymbolicLink() || !entry.isDirectory()) throw Error('试用目录经过链接或不是普通目录，未写入。')
    } catch (error) { if (error.code !== 'ENOENT') throw error }
    current = path.dirname(current)
  }
}

async function existingProfile(profile) {
  try {
    const entry = await fs.lstat(profile)
    if (!entry.isDirectory() || entry.isSymbolicLink()) throw Error('现有试用路径不是普通目录，未写入。')
  } catch (error) { if (error.code === 'ENOENT') return false; throw error }
  for (const filename of [markerFilename, 'library.json']) {
    let entry
    try { entry = await fs.lstat(path.join(profile, filename)) } catch { throw Error('现有试用目录来源不明或不完整，保留原内容并停止。') }
    if (!entry.isFile() || entry.isSymbolicLink()) throw Error('现有试用目录含非普通文件，未写入。')
  }
  if ((await fs.readFile(path.join(profile, markerFilename), 'utf8')).trim() !== trialMarker) throw Error('现有试用目录不是本晚虚构试用资料，保留原内容并停止。')
  return true
}

// Original geometric PNG scenes, generated without an image library or external art.
function syntheticPng(width, height, variant) {
  const crc = bytes => {
    let value = 0xffffffff
    for (const byte of bytes) {
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
  const palettes = [[[53, 82, 145], [223, 156, 165]], [[44, 95, 94], [193, 217, 150]], [[107, 77, 138], [226, 157, 178]], [[170, 98, 65], [247, 205, 122]], [[59, 109, 141], [164, 215, 235]], [[66, 80, 119], [174, 188, 225]]]
  const [top, bottom] = palettes[variant]
  const pixels = Buffer.alloc((width * 3 + 1) * height)
  for (let y = 0; y < height; y++) for (let x = 0; x < width; x++) {
    const offset = y * (width * 3 + 1) + 1 + x * 3, u = x / width, v = y / height
    const moon = (u - .72) ** 2 + ((v - .24) * height / width) ** 2 < .12 ** 2
    const mountain = v > .62 + .12 * Math.sin(u * 10 + variant)
    const frame = u < .035 || u > .965 || v < .035 || v > .965
    for (let channel = 0; channel < 3; channel++) pixels[offset + channel] = frame ? [235, 230, 212][channel] : moon ? [247, 228, 190][channel] : mountain ? Math.round(top[channel] * .62) : Math.round(top[channel] * (1 - v) + bottom[channel] * v)
  }
  const header = Buffer.alloc(13); header.writeUInt32BE(width); header.writeUInt32BE(height, 4); header[8] = 8; header[9] = 2
  return Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), chunk('IHDR', header), chunk('IDAT', deflateSync(pixels)), chunk('IEND', Buffer.alloc(0))])
}

async function encodePhotos(staging) {
  const env = { ...process.env, KGM_DATA_DIR: staging }
  for (const key of Object.keys(env)) if (key.startsWith('KGM_') && key !== 'KGM_DATA_DIR') delete env[key]
  delete env.ELECTRON_RUN_AS_NODE; delete env.NODE_OPTIONS
  const executable = createRequire(import.meta.url)('electron')
  return await new Promise((resolve, reject) => {
    // This is a no-window codec helper, not the production application entry.
    const child = spawn(executable, [script, '--encode-photos', staging], { cwd: project, env, windowsHide: true, stdio: ['ignore', 'ignore', 'pipe'] })
    let errors = '', timedOut = false
    child.stderr.on('data', bytes => { errors = (errors + bytes.toString()).slice(-4000) })
    const timer = setTimeout(() => { timedOut = true; child.kill() }, 20000)
    child.once('error', error => { clearTimeout(timer); reject(error) })
    child.once('close', code => {
      clearTimeout(timer)
      if (timedOut || code !== 0) { reject(Error(`原创试用封面编码失败（${timedOut ? '超时' : code}）；未启动程序。${errors}`)); return }
      resolve()
    })
  })
}

async function runCodec(staging) {
  if (!process.versions.electron || !path.isAbsolute(staging) || !/^\.trial-staging-[a-f0-9-]{36}$/.test(path.basename(staging)) || path.resolve(process.env.KGM_DATA_DIR ?? '') !== path.resolve(staging)) throw Error('编码进程必须使用明确隔离的资料目录。')
  await requirePlainParents(staging)
  const { app, nativeImage } = createRequire(import.meta.url)('electron')
  await fs.mkdir(path.join(staging, 'codec-cache'))
  app.setPath('userData', path.join(staging, 'codec-cache'))
  // nativeImage has no BrowserWindow dependency. Awaiting app.whenReady() in an
  // ESM entry's top-level promise would prevent Electron from finishing startup.
  try {
    for (let i = 0; i < 6; i++) {
      const filename = path.join(staging, 'synthetic-photos', `fictional-cover-${i}.png`)
      const image = nativeImage.createFromBuffer(await fs.readFile(filename))
      if (image.isEmpty()) throw Error('原创 PNG 不能解码。')
      const jpeg = image.toJPEG(88)
      if (jpeg.length > 128 * 1024) throw Error('原创封面超过书架允许的大小。')
      await fs.writeFile(filename.replace(/\.png$/, '.jpg'), jpeg, { flag: 'wx' })
    }
  } finally { app.quit() }
}

export async function prepareTrialProfile({ profile = trialProfile } = {}) {
  if (!path.isAbsolute(profile)) throw Error('试用目录必须是绝对路径。')
  profile = path.resolve(profile)
  await requirePlainParents(profile)
  if (await existingProfile(profile)) return { profile, created: false }
  const parent = path.dirname(profile)
  await fs.mkdir(parent, { recursive: true })
  await requirePlainParents(parent)
  const staging = path.join(parent, `.trial-staging-${randomUUID()}`)
  await fs.mkdir(staging)
  try {
    await fs.mkdir(path.join(staging, 'synthetic-photos'))
    const dimensions = [[600, 900], [600, 900], [600, 900], [900, 560], [640, 640], [600, 900]]
    for (const [i, [width, height]] of dimensions.entries()) await fs.writeFile(path.join(staging, 'synthetic-photos', `fictional-cover-${i}.png`), syntheticPng(width, height, i), { flag: 'wx' })
    await encodePhotos(staging)
    const names = ['【虚构试用】湖岸来信', '【虚构试用】当星光再次照亮那个没有名字的小镇时我们仍记得夏日约定', '【虚构试用】等待整理的小镇', '【虚构试用】尚未安装的约定', '【虚构试用】冬日之后', '【虚构试用】另一次相遇']
    const statuses = ['planned', 'playing', 'paused', 'planned', 'completed', 'dropped']
    const games = []
    for (const [i, name] of names.entries()) {
      const folder = path.join(profile, 'synthetic-games', `game-${i}`)
      const installations = i < 3 ? [{ id: `overnight-trial-install-${i}`, folderPath: folder, executablePath: null, arguments: [] }] : []
      if (i < 3) {
        const stagedFolder = path.join(staging, 'synthetic-games', `game-${i}`)
        await fs.mkdir(stagedFolder, { recursive: true })
        await fs.writeFile(path.join(stagedFolder, 'SYNTHETIC.txt'), '虚构试用目录，没有游戏、存档或可执行程序。\n', { flag: 'wx' })
      }
      const jpeg = await fs.readFile(path.join(staging, 'synthetic-photos', `fictional-cover-${i}.jpg`))
      const seconds = i === 1 ? 3600 : i === 4 ? 7200 : 0
      games.push({
        id: `overnight-trial-game-${i}`, name, nameIsCustom: true,
        metadata: i === 2 ? null : {
          // A valid IPC shape, explicitly fictional and outside ordinary catalogue
          // numbering. This is not evidence of a real linked catalogue work.
          id: `99999000000000000${i}`, title: name, originalTitle: name, aliases: [`Fictional Trial ${i + 1}`], coverUrl: null, bannerUrl: null,
          description: '这是明确虚构的离线试用资料，不对应真实 Galgame。封面为原创几何图，评分和游玩记录均为模拟样例；没有执行游戏或验证联网资料服务。可以修改个人记录、分类，保存常用筛选，使用 synthetic-photos 中的图片切换本地封面。',
          releaseDate: '2025-01-01', releaseDatePrecision: 'day', developers: ['虚构试用工房'], tags: ['虚构试用', i % 2 ? '夏日' : '湖岸'],
          ratings: [{ source: '虚构样例', score: 8, scale: 10, votes: null }], characters: [], references: [], webUrl: null, contentRating: '全年龄', fetchedAt: date,
        },
        localCover: { dataUrl: 'data:image/jpeg;base64,' + jpeg.toString('base64'), width: dimensions[i][0], height: dimensions[i][1] },
        installations, preferredInstallationId: installations[0]?.id ?? null, status: statuses[i], personalRating: i === 0 ? 0 : i === 4 ? 8 : null,
        favorite: i === 0 || i === 1, categories: [i < 3 ? '本地试用' : '愿望试用', i % 2 ? '夏日约定' : '湖岸来信', ...(i === 1 ? ['【虚构长分类】想在有时间的时候慢慢游玩的温柔故事收藏'] : [])],
        customTags: ['虚构试用', '隔离数据'], notes: '【模拟个人记录】此条目可以自由编辑。所有游玩时长为合成示例，不代表实际运行。', totalPlaySeconds: seconds, playCount: seconds ? 1 : 0,
        sessions: seconds ? [{ id: `fictional-session-${i}`, startedAt: `2026-10-03T${i === 1 ? '10' : '09'}:00:00.000Z`, endedAt: '2026-10-03T11:00:00.000Z', seconds }] : [],
        addedAt: `2026-10-0${i < 3 ? '3' : '2'}T0${i}:00:00.000Z`, lastPlayedAt: seconds ? '2026-10-03T11:00:00.000Z' : null,
      })
    }
    await fs.rm(path.join(staging, 'codec-cache'), { recursive: true, force: true })
    await fs.writeFile(path.join(staging, 'synthetic-photos', 'README.txt'), '原创几何测试图片；不是个人照片或游戏素材。PNG/JPG 可用于详情页的本地封面选择。\n', { flag: 'wx' })
    await fs.writeFile(path.join(staging, 'library.json'), JSON.stringify({ format: 'kun-gal-manager', version: 1, exportedAt: date, games }, null, 2), { flag: 'wx' })
    await fs.writeFile(path.join(staging, markerFilename), trialMarker + '\n', { flag: 'wx' })
    // Do not replace a profile created by a parallel launcher or any other writer.
    try { await fs.rename(staging, profile) } catch (error) {
      if (await existingProfile(profile)) return { profile, created: false }
      throw error
    }
    return { profile, created: true }
  } finally {
    // Only our unique same-parent staging directory is eligible for cleanup.
    if (path.dirname(staging) !== parent || !path.basename(staging).startsWith('.trial-staging-')) throw Error('试用临时目录边界检查失败。')
    await fs.rm(staging, { recursive: true, force: true })
  }
}

if (path.resolve(process.argv[1] ?? '') === script) {
  try {
    if (process.argv[2] === '--encode-photos' && process.argv.length === 4) await runCodec(process.argv[3])
    else {
      if (process.argv.length !== 2) throw Error('试用生成器不接受其他资料目录或参数。')
      const result = await prepareTrialProfile()
      console.log(`${result.created ? '已创建虚构隔离书架' : '保留已有隔离书架及试用修改'}：${result.profile}`)
    }
  } catch (error) { console.error(error.message); process.exitCode = 1 }
}
