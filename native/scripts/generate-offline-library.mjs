// Reproducible synthetic profiles only; generated libraries stay outside tracked fixtures.
import fs from 'node:fs/promises'
import path from 'node:path'
const value = key => { const index = process.argv.indexOf(key); return index < 0 ? undefined : process.argv[index + 1] }
const root = value('--out'), count = Number(value('--count') ?? 1000), shape = process.argv.includes('--readonly') ? 'readonly' : process.argv.includes('--corrupt') ? 'corrupt' : 'normal'
const coverFile = value('--cover-json')
const covers = coverFile ? JSON.parse(await fs.readFile(coverFile, 'utf8')) : null
const coverPool = covers ? Array.isArray(covers) ? covers : [covers] : []
if (coverPool.some(c => !c.dataUrl?.startsWith('data:image/jpeg;base64,') || c.dataUrl.length > 180000 || !Number.isInteger(c.width) || !Number.isInteger(c.height))) throw Error('Use a synthetic normalized JPEG cover fixture')
if (!root || !path.isAbsolute(root) || !Number.isInteger(count) || count < 0 || count > 10000) throw Error('Use --out <absolute NEW directory> --count <0..10000> [--readonly|--corrupt]')
const formal = path.resolve(process.env.APPDATA, 'KunGalManager').toLowerCase(), target = path.resolve(root).toLowerCase()
if (target === formal || target.startsWith(formal + path.sep) || formal.startsWith(target + path.sep)) throw Error('Formal profile boundary')
await fs.mkdir(root, { recursive: false }) // Never overwrite an existing profile.
const clock = '2026-10-04T00:00:00.000Z'
const work = i => ({ id: String(i + 100), title: `目录作品 ${i}`, originalTitle: `Original ${i}`, aliases: [`Alias ${i}`], coverUrl: i % 3 ? null : 'https://example.invalid/cover.png', bannerUrl: null, description: 'Generated offline fixture', releaseDate: null, releaseDatePrecision: null, developers: ['Synthetic Studio'], tags: ['剧情', `类型 ${i % 7}`], ratings: [], characters: [], references: [], webUrl: null, contentRating: null, fetchedAt: clock })
const entry = i => ({ id: `game-${String(i).padStart(5, '0')}`, name: `合成作品 ${String(i).padStart(5, '0')}`, nameIsCustom: false, metadata: i % 2 ? null : work(i), installations: i % 3 ? [] : [{ id: `install-${i}`, folderPath: `Z:\\SyntheticOnly\\Game${i}`, executablePath: i % 6 ? null : `Z:\\SyntheticOnly\\Game${i}\\Game.exe`, arguments: [] }], preferredInstallationId: i % 3 ? null : `install-${i}`, status: ['planned', 'playing', 'completed', 'paused', 'dropped'][i % 5], personalRating: i % 4 ? i % 11 : null, favorite: i % 4 === 0, categories: [`分类 ${i % 9}`], customTags: [`标签 ${i % 13}`], notes: `合成笔记 ${i}`, totalPlaySeconds: i * 60, playCount: i % 17, sessions: [], addedAt: clock, lastPlayedAt: null })
const games = shape === 'readonly' ? [0, 1].map(i => ({ ...entry(i), sessions: Array.from({ length: 100000 }, (_, j) => ({ id: `s-${j}`, startedAt: clock, endedAt: clock, seconds: 1 })), totalPlaySeconds: 100000 })) : Array.from({ length: count }, (_, i) => entry(i))
if (coverPool.length) for (const [i, game] of games.entries()) game.localCover = coverPool[i % coverPool.length]
const json = shape === 'corrupt' ? '{broken synthetic input' : JSON.stringify({ format: 'kun-gal-manager', version: 1, exportedAt: clock, games })
if (Buffer.byteLength(json) > 32 * 1024 * 1024) throw Error('Fixture input exceeds contract')
await fs.writeFile(path.join(root, '.the-order-synthetic'), 'the-order-native-phase1-synthetic-v1')
await fs.writeFile(path.join(root, 'library.json'), json)
console.log(JSON.stringify({ root, entries: games.length, bytes: Buffer.byteLength(json), shape }))
