import assert from 'node:assert/strict'
import { test } from 'node:test'
import { promises as fs } from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { readGameIdentity, rankMetadataMatches, type GameIdentity, type TitleIndexRecord } from '../electron/game-identification'
import type { LibraryGame, WorkMetadata } from '../src/shared/types'

function work(id: string, title: string, overrides: Partial<WorkMetadata> = {}): WorkMetadata {
  return { id, title, originalTitle: title, aliases: [], coverUrl: null, bannerUrl: null, description: '', releaseDate: null, releaseDatePrecision: null, developers: [], tags: [], ratings: [], characters: [], references: [], webUrl: null, contentRating: null, fetchedAt: '2026-10-02T00:00:00.000Z', ...overrides }
}

function identity(names: string[], references: string[] = [], source: GameIdentity['referenceSource'] = references.length ? 'title-index' : 'none'): GameIdentity {
  return { names, references, referenceSource: source }
}

async function fixture(t: { after: (callback: () => Promise<void>) => void }, name = '测试作品') {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'kun-identity-'))
  t.after(async () => {
    assert.equal(path.dirname(path.resolve(root)), path.resolve(os.tmpdir()))
    assert.ok(path.basename(root).startsWith('kun-identity-'))
    await fs.rm(root, { recursive: true, force: true })
  })
  const folder = path.join(root, name)
  await fs.mkdir(folder)
  const game: LibraryGame = {
    id: 'local-1', name, metadata: null,
    installations: [{ id: 'install-1', folderPath: folder, executablePath: path.join(folder, 'Game.exe'), arguments: [] }],
    preferredInstallationId: 'install-1', status: 'planned', personalRating: null, favorite: false, categories: [], customTags: [], notes: '', totalPlaySeconds: 0, playCount: 0, sessions: [], addedAt: '2026-10-02T00:00:00.000Z', lastPlayedAt: null,
  }
  async function sidecar(value: unknown) {
    await fs.mkdir(path.join(folder, '.PotatoVN'), { recursive: true })
    await fs.writeFile(path.join(folder, '.PotatoVN', 'meta.json'), JSON.stringify(value))
  }
  return { root, folder, game, sidecar }
}

test('PotatoVN v2 sidecar identifies source IDs and names without importing user notes', async t => {
  const { game, sidecar } = await fixture(t, '包装目录')
  await sidecar({ Version: 2, Game: {
    Name: { Value: '常轨脱离Creative', IsLocked: false }, OriginalName: { Value: 'ハミダシクリエイティブ' },
    ChineseName: { Value: '常轨脱离Creative' }, CnName: '常轨脱离Creative',
    Ids: ['27407', '271746', 'vndb:v27407,bgm:271746,steam:null', null, null, null, null, '1293820'], Comment: 'private note',
  }, Installation: { Arguments: 'private arguments' } })
  const result = await readGameIdentity(game, [])
  assert.deepEqual(result.references, ['vndb:v27407', 'bangumi:271746', 'steam:1293820'])
  assert.equal(result.referenceSource, 'sidecar')
  assert.deepEqual(result.names, ['常轨脱离Creative', 'ハミダシクリエイティブ', '包装目录'])
  assert.equal(JSON.stringify(result).includes('private'), false)
  assert.deepEqual(structuredClone(result), result)
})

test('legacy sidecar accepts mixed source IDs and rejects VNDB release IDs and placeholders', async t => {
  const { game, sidecar } = await fixture(t)
  await sidecar({ Name: '旧格式标题', OriginalName: 'Old title', Ids: ['r100', '0', 'vndb:v123,bgm:456,steam:480,steam:null'] })
  const result = await readGameIdentity(game, [])
  assert.deepEqual(result.references, ['vndb:v123', 'bangumi:456'])
  assert.equal(result.names[0], '旧格式标题')
  await sidecar({ Ids: ['r100', '-1', 'vndb:null,bgm:null,steam:480'] })
  assert.deepEqual((await readGameIdentity(game, [])).references, [])
})

test('Steam app ID is bounded, validated, and usable without a sidecar', async t => {
  const { game, folder } = await fixture(t)
  const filename = path.join(folder, 'steam_appid.txt')
  await fs.writeFile(filename, '\uFEFF1293820\r\n')
  assert.deepEqual((await readGameIdentity(game, [])).references, ['steam:1293820'])
  assert.equal((await readGameIdentity(game, [])).referenceSource, 'steam')
  for (const contents of ['480', '0', 'null', '123\n456', '1'.repeat(129)]) {
    await fs.writeFile(filename, contents)
    assert.deepEqual((await readGameIdentity(game, [])).references, [])
  }
})

test('outside directory links are not followed and oversized or malformed files are ignored', async t => {
  const { game, root, folder, sidecar } = await fixture(t)
  const outside = path.join(root, 'outside')
  await fs.mkdir(outside)
  await fs.writeFile(path.join(outside, 'meta.json'), JSON.stringify({ Name: '不得读取', Ids: ['v555'] }))
  try {
    await fs.symlink(outside, path.join(folder, '.PotatoVN'), process.platform === 'win32' ? 'junction' : 'dir')
    assert.deepEqual((await readGameIdentity(game, [])).references, [])
    assert.equal((await readGameIdentity(game, [])).names.includes('不得读取'), false)
    await fs.unlink(path.join(folder, '.PotatoVN'))
  } catch (error) {
    if (!['EPERM', 'EACCES'].includes((error as NodeJS.ErrnoException).code ?? '')) throw error
  }
  await sidecar({ Name: 'Too large', Ids: ['v555'], Description: 'x'.repeat(1024 * 1024) })
  assert.deepEqual((await readGameIdentity(game, [])).references, [])
  await fs.writeFile(path.join(folder, '.PotatoVN', 'meta.json'), '{ broken json')
  assert.deepEqual((await readGameIdentity(game, [])).references, [])
})

test('a small file link within the selected installation is allowed', async t => {
  const { game, folder } = await fixture(t)
  await fs.writeFile(path.join(folder, 'appid.txt'), '1293820')
  try { await fs.symlink(path.join(folder, 'appid.txt'), path.join(folder, 'steam_appid.txt'), 'file') }
  catch (error) {
    if (['EPERM', 'EACCES'].includes((error as NodeJS.ErrnoException).code ?? '')) { t.skip('File symlink creation is unavailable'); return }
    throw error
  }
  assert.deepEqual((await readGameIdentity(game, [])).references, ['steam:1293820'])
})

test('unique exact title index supplies references after stripping only packaging and language noise', async t => {
  const { game } = await fixture(t, '【PC】常轨脱离Creative [简体中文] 汉化硬盘版')
  const index: TitleIndexRecord[] = [{ names: ['常轨脱离Creative', 'ハミダシクリエイティブ'], references: ['vndb:v27407', 'bgm:271746'] }]
  const result = await readGameIdentity(game, index)
  assert.equal(result.names[0], '常轨脱离Creative')
  assert.deepEqual(result.references, ['vndb:v27407', 'bangumi:271746'])
  assert.equal(result.referenceSource, 'title-index')
  assert(result.names.includes('ハミダシクリエイティブ'))
  assert.deepEqual(await readGameIdentity(game, index), result)
})

test('an independently read full manual title is searched before its shortened folder name', async t => {
  const { game, folder } = await fixture(t, '夜明け☆航路')
  const fullTitle = '夜明け☆航路 RE-BOOT!'
  await fs.writeFile(path.join(folder, 'お読みください.html'), `<meta charset="utf-8"><title>お読み下さい『${fullTitle}』</title>
    <h1>蒼い鳥ソフト『${fullTitle}』について</h1><p>Other Novel must not be uploaded.</p>`)
  const record: TitleIndexRecord = { names: [fullTitle, 'Dawn Voyage RE-BOOT!'], references: ['vndb:v101'] }
  const result = await readGameIdentity(game, [record], { readExecutableMetadata: false })
  assert.equal(result.queries?.[0], fullTitle)
  assert.ok(result.queries?.some(query => query === game.name))
  assert.ok(result.evidence?.titles.some(title => title.source === 'manual-title' && title.value === fullTitle))
  assert.deepEqual(result.evidence?.companies, ['蒼い鳥ソフト'])
  assert.deepEqual(result.evidence?.indexProofs, [{ observedTitle: fullTitle, source: 'manual-title', recordNames: record.names, references: ['vndb:v101'] }])
  assert.equal(JSON.stringify(result).includes('Other Novel must not be uploaded'), false)
  assert.deepEqual(result.references, ['vndb:v101'])
})

test('cross-language proofs retain the observed title and names from its exact index row only', async t => {
  const { game } = await fixture(t, '雾中的庭园')
  const index: TitleIndexRecord[] = [
    { names: ['雾中的庭园', 'The Garden Beyond Dawn', '霧の庭園'], references: ['vndb:v202', 'bangumi:2202'] },
    // A second row carrying the same primary ID must not donate unobserved aliases.
    { names: ['A Different Localized Work', '別の作品'], references: ['vndb:v202', 'steam:3303'] },
    { names: ['Unrelated Record', '另一部作品'], references: ['vndb:v303'] },
  ]
  const result = await readGameIdentity(game, index, { readExecutableMetadata: false })
  assert.deepEqual(result.evidence?.indexProofs, [{
    observedTitle: '雾中的庭园', source: 'folder',
    recordNames: ['雾中的庭园', 'The Garden Beyond Dawn', '霧の庭園'], references: ['vndb:v202', 'bangumi:2202'],
  }])
  assert.ok(result.names.includes('The Garden Beyond Dawn'))
  assert.equal(result.names.includes('A Different Localized Work'), false)
  assert.equal(result.references.includes('steam:3303'), false)
  assert.equal(result.references.includes('vndb:v303'), false)
})

test('a title copied from an old sidecar never creates a same-row cross-language proof', async t => {
  const { game, sidecar } = await fixture(t, '包装文件夹')
  await sidecar({ Name: '雾中的庭园', OriginalName: 'The Garden Beyond Dawn', Ids: ['v202'] })
  const result = await readGameIdentity(game, [
    { names: ['雾中的庭园', 'The Garden Beyond Dawn', '霧の庭園'], references: ['vndb:v202', 'steam:3303'] },
  ], { readExecutableMetadata: false })
  assert.deepEqual(result.evidence?.indexProofs, [])
  assert.deepEqual(result.references, ['vndb:v202'])
  assert.ok(result.evidence?.references.every(reference => reference.source === 'sidecar'))
  assert.equal(result.names.includes('霧の庭園'), false)
})

test('identity text evidence stays inside the selected root and ignores a linked external manual', async t => {
  const { game, folder, root } = await fixture(t, '包装文件夹')
  await fs.writeFile(path.join(root, 'manual.html'), '<title>Parent Novel</title>')
  await fs.writeFile(path.join(folder, 'manual.html'), '<title>Selected Novel - Manual</title>')
  const outside = path.join(root, 'Outside'); await fs.mkdir(outside)
  await fs.writeFile(path.join(outside, 'manual.html'), '<title>Linked Outside Novel</title>')
  await fs.symlink(outside, path.join(folder, 'manual'), process.platform === 'win32' ? 'junction' : 'dir')
  const unselected = path.join(root, 'OtherInstall'); await fs.mkdir(unselected)
  await fs.writeFile(path.join(unselected, 'manual.html'), '<title>Unselected Novel</title>')
  game.installations.push({ id: 'unselected', folderPath: unselected, executablePath: null, arguments: [] })
  const result = await readGameIdentity(game, [], { readExecutableMetadata: false })
  assert.deepEqual(result.evidence?.titles.filter(title => title.source === 'manual-title' || title.source === 'config-title'), [
    { value: 'Selected Novel', source: 'manual-title' },
  ])
  assert.equal(result.queries?.[0], 'Selected Novel')
  for (const rejected of ['Parent Novel', 'Linked Outside Novel', 'Unselected Novel']) {
    assert.equal(result.names.includes(rejected), false)
    assert.equal(result.queries?.includes(rejected), false)
  }
})

test('matching index aliases remain ambiguous when CLANNAD also occurs on a Tomoyo record', async t => {
  const { game } = await fixture(t, 'CLANNAD')
  const index: TitleIndexRecord[] = [
    { names: ['CLANNAD', 'クラナド'], references: ['vndb:v4'] },
    { names: ['Tomoyo After', 'CLANNAD'], references: ['vndb:v12'] },
  ]
  const result = await readGameIdentity(game, index)
  assert.equal(result.referenceSource, 'none')
  assert.deepEqual(result.references, [])
  assert.equal(result.names.includes('Tomoyo After'), false)
})

test('fuzzy index titles never generate strong references and episode/version markers survive', async t => {
  const { game } = await fixture(t, '9-nine- Episode 2 [官中]')
  const index: TitleIndexRecord[] = [
    { names: ['9-nine- Episode 1'], references: ['vndb:v111'] },
    { names: ['9-nine- Episode 3'], references: ['vndb:v333'] },
  ]
  const result = await readGameIdentity(game, index)
  assert.equal(result.names[0], '9-nine- Episode 2')
  assert.deepEqual(result.references, [])
  game.name = 'Summer Pockets REFLECTION BLUE'
  game.installations = []
  assert.deepEqual((await readGameIdentity(game, [{ names: ['Summer Pockets'], references: ['vndb:v20424'] }])).references, [])
  for (const title of ['作品 Remake', '作品 FD', '作品 v2.0', '9-nine-新章', '作品 (全年龄版)']) {
    game.name = title
    assert.deepEqual((await readGameIdentity(game, [])).names, [title])
  }
})

test('conflicting titles from different installations cannot select one index record automatically', async t => {
  const { game, root } = await fixture(t, 'First title')
  const another = path.join(root, 'Second title')
  await fs.mkdir(another)
  game.installations.push({ id: 'other', folderPath: another, executablePath: null, arguments: [] })
  assert.deepEqual((await readGameIdentity(game, [
    { names: ['First title'], references: ['vndb:v1'] }, { names: ['Second title'], references: ['vndb:v2'] },
  ])).references, [])
})

test('same-work duplicate index rows merge without losing conflicting auxiliary IDs', async t => {
  const { game } = await fixture(t, 'Shared title')
  const result = await readGameIdentity(game, [
    { names: ['Shared title'], references: ['vndb:v1', 'bangumi:10'] },
    { names: ['Shared title'], references: ['vndb:v1', 'bangumi:20'] },
  ])
  assert.deepEqual(result.references, ['vndb:v1', 'bangumi:10', 'bangumi:20'])
  assert.equal(rankMetadataMatches(result, [work('1', 'Shared title', { references: [{ source: 'vndb', id: 'v1' }] })])[0].automatic, false)
})

test('identity bounds keep evidence of conflicts and never exceed eight names/references', async t => {
  const { game, sidecar, folder } = await fixture(t)
  await sidecar({ Name: 'N1', OriginalName: 'N2', ChineseName: 'N3', CnName: 'N4', Ids: ['v1', '10', [...Array.from({ length: 15 }, (_, n) => `vndb:v${n + 1}`), 'bgm:20', 'steam:100'].join(',')] })
  await fs.writeFile(path.join(folder, 'steam_appid.txt'), '200')
  const result = await readGameIdentity(game, [])
  assert(result.names.length <= 8)
  assert(result.references.length <= 8)
  assert(result.references.includes('vndb:v1') && result.references.includes('vndb:v2'))
  assert(result.references.includes('bangumi:10') && result.references.includes('bangumi:20'))
  assert(result.references.includes('steam:100') && result.references.includes('steam:200'))
})

test('a unique VNDB number is automatic even when auxiliary Bangumi/Steam references are absent', () => {
  const game = identity(['常轨脱离Creative'], ['vndb:v27407', 'bangumi:271746', 'steam:1293820'])
  const correct = work('1', 'ハミダシクリエイティブ', { references: [{ source: 'vndb', id: 'v27407' }] })
  const ranked = rankMetadataMatches(game, [work('other', '常轨脱离Creative'), correct])
  assert.equal(ranked[0].work.id, '1')
  assert.equal(ranked[0].automatic, true)
  assert.match(ranked[0].reason, /VNDB/)
  assert.equal(ranked[1].automatic, false)
  assert.equal(rankMetadataMatches(game, [{ ...correct, references: [] }])[0].automatic, false, 'fetched details must still carry the primary reference')
})

test('different works matching different source numbers or a conflicting auxiliary number require confirmation', () => {
  const game = identity(['A title'], ['vndb:v1', 'bangumi:10'])
  const vndb = work('1', 'A title', { references: [{ source: 'vndb', id: 'v1' }] })
  const bangumi = work('2', 'Another title', { references: [{ source: 'bangumi', id: '10' }] })
  assert(rankMetadataMatches(game, [vndb, bangumi]).every(match => !match.automatic))
  assert.equal(rankMetadataMatches(game, [{ ...vndb, references: [...vndb.references, { source: 'bangumi', id: '20' }] }])[0].automatic, false)
  assert.equal(rankMetadataMatches(identity(['A title'], ['vndb:v1', 'vndb:v2']), [vndb])[0].automatic, false)
  assert(rankMetadataMatches(game, [vndb, { ...vndb, id: 'different-work' }]).every(match => !match.automatic))
})

test('single-source Steam/Bangumi can identify a unique work; duplicate batch rows are deduplicated', () => {
  for (const [source, id] of [['steam', '100'], ['bangumi', '10']] as const) {
    const candidate = work('1', 'Title', { references: [{ source, id }] })
    const ranked = rankMetadataMatches(identity(['Title'], [`${source}:${id}`], source === 'steam' ? 'steam' : 'sidecar'), [candidate, candidate])
    assert.equal(ranked.length, 1)
    assert.equal(ranked[0].automatic, true)
  }
})

test('additional releases on one work do not conflict with an included local source ID', () => {
  const game = identity(['2236 A.D.'], ['vndb:v18073', 'bangumi:12345', 'steam:800820'])
  const candidate = work('2236', '2236 A.D.', { references: [
    { source: 'vndb', id: 'v18073' }, { source: 'steam', id: '862510' },
    { source: 'bangumi', id: '54321' }, { source: 'bangumi', id: '12345' },
    ...Array.from({ length: 12 }, (_, index) => ({ source: 'steam', id: `${900000 + index}` })),
    { source: 'steam', id: '800820' },
  ] })
  const ranked = rankMetadataMatches(game, [candidate])
  assert.equal(ranked[0].automatic, true, 'an expected release may occur beyond the local eight-reference limit')
  assert.match(ranked[0].reason, /VNDB/)
  assert.equal(rankMetadataMatches(game, [{ ...candidate, references: candidate.references.filter(ref => ref.id !== '800820') }])[0].automatic, false, 'an auxiliary source with exclusively different IDs still conflicts')
  assert.equal(rankMetadataMatches(identity(game.names, [...game.references, 'steam:862510']), [candidate])[0].automatic, false, 'disagreeing local installation IDs remain ambiguous')
  assert(rankMetadataMatches(game, [candidate, work('other', 'Another work', { references: [{ source: 'steam', id: '800820' }] })]).every(match => !match.automatic), 'different works supported by the same evidence remain ambiguous')
})

test('clear unique full titles identify 常轨 and the correct 9-nine episode', () => {
  const games = [work('1', '9-nine- Episode 1'), work('2', '9-nine- Episode 2'), work('3', '9-nine- Episode 3'), work('4', '9-nine-新章')]
  const ranked = rankMetadataMatches(identity(['9-nine- Episode 2 [简体中文]']), games)
  assert.equal(ranked[0].work.id, '2')
  assert.equal(ranked[0].automatic, true)
  assert(ranked.slice(1).every(match => !match.automatic))
  const common = rankMetadataMatches(identity(['常轨脱离Creative']), [work('1', '常轨脱离Creative', { originalTitle: 'ハミダシクリエイティブ' }), work('2', '常轨脱离Creative凸')])
  assert.equal(common[0].automatic, true)
  assert.equal(common[1].automatic, false)
})

test('version differences and alias-only matches cannot become automatic', () => {
  for (const [base, edition] of [
    ['Summer Pockets', 'Summer Pockets REFLECTION BLUE'],
    ['Title', 'Title Remake'], ['Title', 'Title FD'], ['9-nine- Episode 1', '9-nine- Episode 2'],
  ]) {
    const ranked = rankMetadataMatches(identity([base]), [work('1', edition, { aliases: [base] })])
    assert.equal(ranked[0].automatic, false)
  }
  const ambiguous = rankMetadataMatches(identity(['Shared title']), [work('1', 'Shared title'), work('2', 'Shared title')])
  assert(ambiguous.every(match => !match.automatic))
})

test('CLANNAD alias pollution cannot beat its clear primary title or authorize Tomoyo After alone', () => {
  const clannad = work('1', 'CLANNAD', { originalTitle: 'クラナド' })
  const tomoyo = work('2', 'Tomoyo After', { aliases: ['CLANNAD', 'クラナド'] })
  const ranked = rankMetadataMatches(identity(['CLANNAD']), [tomoyo, clannad])
  assert.equal(ranked[0].work.id, '1')
  assert.equal(ranked[0].automatic, true)
  assert.equal(ranked[1].automatic, false)
  assert.equal(rankMetadataMatches(identity(['CLANNAD']), [tomoyo])[0].automatic, false)
})
