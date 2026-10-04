// Deterministic fictional data only. Never accepts a user profile as input.
import fs from 'node:fs/promises'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { createHash } from 'node:crypto'
import vm from 'node:vm'
import { transformSync } from '../../node_modules/esbuild/lib/main.js'

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const fixtures = path.join(root, 'fixtures')
const time = '2026-10-04T00:00:00.000Z'
const base = (id, name) => ({ id, name, metadata: null, installations: [], preferredInstallationId: null,
  status: 'planned', personalRating: null, favorite: false, categories: [], customTags: [], notes: '',
  totalPlaySeconds: 0, playCount: 0, sessions: [], addedAt: time, lastPlayedAt: null })
const metadata = { id: '18446744073709551616', title: '虚构作品', originalTitle: '架空の物語', aliases: ['Fictional Story'],
  coverUrl: null, bannerUrl: null, description: '仅用于原生客户端验证。', releaseDate: null, releaseDatePrecision: null,
  developers: ['合成开发组'], tags: ['测试'], ratings: [], characters: [], references: [], webUrl: null, contentRating: null, fetchedAt: time }
const first = { ...base('synthetic-a', '我的自定义名称 · 星海'), nameIsCustom: true, personalRating: 0, favorite: true,
  metadata, categories: ['合成资料'], customTags: ['中文'], notes: '虚构笔记', totalPlaySeconds: 7200, playCount: 3,
  sessions: [{ id: 'session-a', startedAt: time, endedAt: '2026-10-04T00:10:00.000Z', seconds: 600 }],
  installations: [{ id: 'install-a', folderPath: 'C:\\TheOrder-Synthetic\\星海', executablePath: 'C:\\TheOrder-Synthetic\\星海\\game.exe', arguments: ['--test', '空 格'] },
    { id: 'install-b', folderPath: 'C:\\TheOrder-Synthetic\\星海另一安装', executablePath: null, arguments: [] }], preferredInstallationId: 'install-a' }
// Same header-only fixture as the legacy structural validator. Never displayed/decoded as a raster.
const jpeg = Buffer.from([255,216,255,192,0,11,8,0,3,0,4,1,1,17,0,255,218,0,8,1,1,0,0,63,0,1,255,217])
const second = { ...base('synthetic-b', '未命名来源 · 雨夜'), localCover: { dataUrl: 'data:image/jpeg;base64,' + jpeg.toString('base64'), width: 4, height: 3 }, nonPortraitCoverDisplay: null }
const third = { ...base('synthetic-c', '原始名称 · 晨光'), nameIsCustom: false, nonPortraitCoverDisplay: 'crop' }
const envelope = games => ({ format: 'kun-gal-manager', version: 1, exportedAt: time, games })
for (const [name, content] of [['normal', JSON.stringify(envelope([first, second, third]), null, 2)], ['empty', JSON.stringify(envelope([]))], ['corrupt', '{"format":"kun-gal-manager","version":1,"games":[']]) {
  const directory = path.join(fixtures, name); await fs.mkdir(directory, { recursive: true })
  await fs.writeFile(path.join(directory, '.the-order-synthetic'), 'the-order-native-phase1-synthetic-v1\n')
  await fs.writeFile(path.join(directory, 'library.json'), content + '\n')
}
// Extract the actual current legacy function so the oracle is not a hand-copied implementation.
const source = (await fs.readFile(path.join(root, '../electron/metadata-matching.ts'), 'utf8')).replace(/\r\n/g, '\n')
const start = source.indexOf('const reviewIdentity ='), end = source.indexOf('const boundedText', start)
if (start < 0 || end < 0) throw Error('Legacy fingerprint source moved; inspect before regenerating')
const fn = source.slice(start, end)
const decisionStart = source.indexOf('return createHash', source.indexOf('private decisionFingerprint('))
const decisionEnd = source.indexOf('\n', decisionStart)
if (decisionStart < 0 || decisionEnd < 0) throw Error('Legacy decision fingerprint source moved')
const decision = source.slice(decisionStart, decisionEnd)
const context = vm.createContext({ createHash })
vm.runInContext(transformSync(fn + '\nglobalThis.compute = reviewIdentity; globalThis.decision = (game, token) => { ' + decision + ' };', { loader: 'ts', target: 'node22' }).code, context)
const inputs = [first, second, third,
  { ...first, name: '日本語・é e\u0301 😀 <>&\u2028\u2029\n\t"\\', nameIsCustom: undefined },
  { ...third, name: '\ud800 lone surrogate' },
  Object.fromEntries(Object.entries(first).reverse())]
const vectors = inputs.map((game, i) => {
  const token = i % 2 ? undefined : { epoch: 'epoch-中文', generation: 'generation-1', requestId: i + 1 }
  const identity = context.compute(game)
  return { label: `vector-${i}`, game, token, serialized: JSON.stringify([game.name, game.nameIsCustom, game.metadata?.id ?? null, game.preferredInstallationId, game.installations.map(({ id, folderPath, executablePath }) => [id, folderPath, executablePath])]), identity,
    decisionSerialized: JSON.stringify([identity, token?.epoch, token?.generation, token?.requestId]), decisionFingerprint: context.decision(game, token) }
})
await fs.writeFile(path.join(fixtures, 'fingerprints.json'), JSON.stringify({ format: 'the-order-fingerprint-reference', version: 1, vectorCount: 6, referenceFileSha256: createHash('sha256').update(source).digest('hex'), sourceSha256: createHash('sha256').update(fn + decision).digest('hex'), vectors }, null, 2) + '\n')
console.log('Generated synthetic fixtures and 6 source-derived fingerprint vectors.')
