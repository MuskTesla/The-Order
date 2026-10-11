import assert from 'node:assert/strict'
import { test } from 'node:test'
import { rankEvidenceMatches, type IdentityEvidence, type ReferenceEvidence, type TitleEvidence, type TitleIndexProof } from '../electron/evidence-matching'
import type { GameIdentity } from '../electron/game-identification'
import type { ReleaseIdentity, WorkMetadata } from '../src/shared/types'

function work(id: string, title: string, overrides: Partial<WorkMetadata> = {}): WorkMetadata {
  return { id, title, originalTitle: title, aliases: [], titleEvidence: [], coverUrl: null, bannerUrl: null, description: 'Complete fetched work details', releaseDate: null, releaseDatePrecision: null, developers: [], tags: [], ratings: [], characters: [], references: [], webUrl: null, contentRating: null, fetchedAt: '2026-10-02T00:00:00.000Z', ...overrides }
}

function identity(titles: TitleEvidence[], references: ReferenceEvidence[] = [], overrides: Partial<IdentityEvidence> = {}): GameIdentity & { evidence: IdentityEvidence } {
  return {
    names: titles.map(title => title.value), references: references.map(ref => ref.value),
    referenceSource: references.some(ref => ref.source === 'sidecar') ? 'sidecar' : references.some(ref => ref.source === 'title-index') ? 'title-index' : references.length ? 'steam' : 'none',
    evidence: { titles, references, companies: [], warnings: [], fingerprints: [], auxiliaryDirectory: false, ...overrides },
  }
}

function noneAutomatic(input: GameIdentity, works: WorkMetadata[], reason: string): void {
  const results = rankEvidenceMatches(input, works)
  assert(results.every(result => !result.automatic), `${reason}: ${JSON.stringify(results.map(({ work, confidence, reason, automatic }) => ({ id: work.id, confidence, reason, automatic })))}`)
}

function witchBridge(source: TitleEvidence['source'] = 'folder') {
  const localTitle = 'WITCH ON THE HOLY NIGHT'
  const proof: TitleIndexProof = { observedTitle: localTitle, source, recordNames: ['Witch on the Holy Night', 'Mahoutsukai no Yoru', '魔法使いの夜', '魔法使之夜'], references: ['vndb:v777', 'bangumi:5418'] }
  const input = identity([{ source, value: localTitle }], proof.references.map(value => ({ source: 'title-index', value })), { indexProofs: [proof] })
  const candidate = work('950', '魔法使之夜', {
    originalTitle: '魔法使いの夜', aliases: [localTitle],
    titleEvidence: [{ text: '魔法使之夜', kind: 'official', isMachine: false, language: 'zh' }, { text: '魔法使いの夜', kind: 'original', isMachine: false, language: 'ja' }, { text: localTitle, kind: 'alias', isMachine: false, language: 'en' }],
    references: [{ source: 'vndb', id: 'v777' }, { source: 'bangumi', id: '5418' }],
  })
  return { input, candidate, proof, localTitle }
}

function summerRelease(kind = 'default') {
  const title = 'Summer Pockets REFLECTION BLUE'
  const input = identity([{ source: 'folder', value: title }, { source: 'manual-title', value: title }], [{ source: 'title-index', value: 'vndb:v20424' }, { source: 'title-index', value: 'bangumi:200763' }])
  const anchored = work('4', '夏日口袋', { originalTitle: title, references: [{ source: 'vndb', id: 'v20424' }, { source: 'bangumi', id: '200763' }], titleEvidence: [{ text: title, kind: 'official', isMachine: false, language: 'en' }] })
  const thin = work('270330', title, { references: [{ source: 'dlsite', id: 'VJ015490' }] })
  const release: ReleaseIdentity = { id: 'release-rb', workId: '4', title, originalTitle: title, kind, date: '2020-06-26', references: [] }
  input.releases = { '4': [release] }
  return { input, anchored, thin, release, title }
}

test('copied sidecar IDs and titles cannot identify an unrelated folder and executable', () => {
  const input = identity([
    { source: 'folder', value: 'RIDDLE JOKER' },
    { source: 'executable-name', value: 'RiddleJoker' },
    { source: 'product-name', value: 'RIDDLE JOKER' },
    { source: 'sidecar', value: 'CLANNAD' },
  ], [{ source: 'sidecar', value: 'vndb:v4' }])
  noneAutomatic(input, [work('wrong', 'CLANNAD', { references: [{ source: 'vndb', id: 'v4' }] })], 'copied metadata is not local program evidence')
})

test('source IDs without any independent local title never authorize an association', () => {
  const candidate = work('1', 'A distinctive game', { references: [{ source: 'vndb', id: 'v1' }, { source: 'steam', id: '100' }] })
  for (const references of [
    [{ source: 'sidecar' as const, value: 'vndb:v1' }],
    [{ source: 'title-index' as const, value: 'vndb:v1', via: 'A distinctive game' }],
    [{ source: 'steam-config' as const, value: 'steam:100' }],
  ]) noneAutomatic(identity([{ source: 'sidecar', value: 'A distinctive game' }], references), [candidate], 'a source number needs independent local corroboration')
})

test('conflicting local 9-nine episodes veto an otherwise exact executable and matching source number', () => {
  const candidate = work('episode1', '9-nine- Episode 1', { references: [{ source: 'steam', id: '100' }] })
  for (const local of ['9-nine- Episode 2', '9-nine- Episode 3', '9-nine- New Episode']) {
    const input = identity([
      { source: 'folder', value: local }, { source: 'product-name', value: '9-nine- Episode 1' },
    ], [{ source: 'steam-config', value: 'steam:100' }])
    noneAutomatic(input, [candidate], `a contradictory folder chapter (${local}) must be surfaced`)
  }
})

test('conflicting FD, SANA, and remake versions veto an exact executable and matching reference', () => {
  for (const [folder, actual] of [
    ['Long title FD', 'Long title After'],
    ['纯白交响曲 SANA EDITION', '纯白交响曲 Remake for FHD'],
    ['Long title Remake FHD', 'Long title REMASTERED FHD'],
  ]) {
    const input = identity([{ source: 'folder', value: folder }, { source: 'product-name', value: actual }], [{ source: 'steam-file', value: 'steam:100' }])
    noneAutomatic(input, [work('wrong-version', actual, { references: [{ source: 'steam', id: '100' }] })], `${folder} must not silently become ${actual}`)
  }
})

test('a polluted alias cannot hide an explicit canonical chapter contradiction', () => {
  const local = '9-nine- Episode 2'
  const input = identity([{ source: 'folder', value: local }, { source: 'product-name', value: local }], [{ source: 'title-index', value: 'vndb:v1', via: local }])
  const candidate = work('wrong-chapter', '9-nine- Episode 1', { aliases: [local], references: [{ source: 'vndb', id: 'v1' }] })
  noneAutomatic(input, [candidate], 'a generic alias is weaker than an explicit primary-title chapter conflict')
})

test('two exact works and close independent candidates remain unresolved', () => {
  const common = identity([{ source: 'folder', value: 'Distinctive official title' }])
  noneAutomatic(common, [work('1', 'Distinctive official title'), work('2', 'Distinctive official title')], 'duplicate full titles are ambiguous')
  const split = identity([{ source: 'folder', value: 'First official title' }, { source: 'product-name', value: 'Second official title' }])
  noneAutomatic(split, [work('1', 'First official title'), work('2', 'Second official title')], 'two observed exact primary titles do not choose a winner')
  const narrow = identity([{ source: 'folder', value: 'Distinctive official title' }], [{ source: 'title-index', value: 'vndb:v1', via: 'Distinctive official title' }])
  noneAutomatic(narrow, [work('1', 'Distinctive official title', { references: [{ source: 'vndb', id: 'v1' }] }), work('2', 'Distinctive official title')], 'a matching index ID must not silently win over a second exact work')
})

test('a unique index or Steam configuration number corroborated by the canonical local name can identify a work', () => {
  for (const [source, value, refSource, id] of [
    ['title-index', 'vndb:v1', 'vndb', 'v1'], ['steam-config', 'steam:100', 'steam', '100'],
  ] as const) {
    const input = identity([{ source: 'folder', value: 'Distinctive official title' }], [{ source, value, via: 'Distinctive official title' }])
    const candidate = work('correct', 'Distinctive official title', { references: [{ source: refSource, id }] })
    const ranked = rankEvidenceMatches(input, [candidate, work('wrong', 'Completely different game')])
    assert.equal(ranked[0].work.id, 'correct')
    assert.equal(ranked[0].automatic, true)
  }
})

test('conflicting independent source IDs veto every candidate', () => {
  for (const references of [
    [{ source: 'title-index' as const, value: 'vndb:v1', via: 'Long game title' }, { source: 'title-index' as const, value: 'vndb:v2', via: 'Long game title' }],
    [{ source: 'title-index' as const, value: 'vndb:v1', via: 'Long game title' }, { source: 'steam-config' as const, value: 'steam:200' }],
  ]) {
    const input = identity([{ source: 'folder', value: 'Long game title' }], references)
    noneAutomatic(input, [work('1', 'Long game title', { references: [{ source: 'vndb', id: 'v1' }] }), work('2', 'Long game title', { references: [{ source: 'vndb', id: 'v2' }, { source: 'steam', id: '200' }] })], 'distinct local sources point to different works')
  }
})

test('repeating one PE title as product name and file description does not increase certainty', () => {
  const candidate = work('1', '空を見上げる物語', { aliases: ['Unverified alias'], developers: ['Studio Blue'] })
  const base = identity([{ source: 'folder', value: 'GameData' }, { source: 'product-name', value: 'Unverified alias' }], [], { companies: ['Studio Blue'] })
  const repeated = identity([...base.evidence.titles, { source: 'file-description', value: 'Unverified alias' }, { source: 'product-name', value: 'Unverified alias' }], [], { companies: ['Studio Blue'] })
  const single = rankEvidenceMatches(base, [candidate])[0], duplicate = rankEvidenceMatches(repeated, [candidate])[0]
  assert.equal(single.automatic, false)
  assert.equal(duplicate.automatic, false)
  assert.equal(duplicate.confidence, single.confidence)
})

test('a PE brand and repeated unverified alias never authorize a translated canonical work', () => {
  const candidate = work('unrelated', '本当の別の物語', {
    aliases: ['CLANNAD'], developers: ['Studio Blue'],
    titleEvidence: [{ text: 'CLANNAD', kind: 'alias', isMachine: false, language: 'en' }],
  })
  const input = identity([
    { source: 'folder', value: 'CLANNAD' },
    { source: 'product-name', value: 'CLANNAD' },
    { source: 'file-description', value: 'CLANNAD' },
  ], [], { companies: ['Studio Blue', 'Studio Blue'] })
  noneAutomatic(input, [candidate], 'a company and two fields from one resource do not verify an unrelated alias')
})

test('copied canonical PE or README text cannot use a polluted folder alias as an independent anchor', () => {
  const title = '本当の別の物語'
  const candidate = work('copied', title, { aliases: ['CLANNAD'], developers: ['Studio Blue'] })
  for (const source of ['product-name', 'file-description', 'readme-title'] as const) {
    const input = identity([{ source: 'folder', value: 'CLANNAD' }, { source, value: title }], [], { companies: ['Studio Blue'] })
    noneAutomatic(input, [candidate], `${source} may be copied; the unverified folder alias is not independent canonical support`)
  }
})

test('an official PE title remains useful with independent partial canonical folder support', () => {
  const candidate = work('correct', 'Starfall Memories')
  const input = identity([{ source: 'folder', value: 'Starfall' }, { source: 'product-name', value: 'Starfall Memories' }])
  const first = rankEvidenceMatches(input, [candidate])[0]
  assert.equal(first.automatic, true)
  const repeated = identity([...input.evidence.titles, { source: 'file-description', value: 'Starfall Memories' }, { source: 'product-name', value: 'Starfall Memories' }])
  assert.equal(rankEvidenceMatches(repeated, [candidate])[0].confidence, first.confidence)
})

test('engine labels and a matching developer cannot create a work identity', () => {
  const candidate = work('engine-alias', '星空の物語', { aliases: ['SiglusEngine', 'GameData'], developers: ['Studio Blue'] })
  const input = identity([{ source: 'folder', value: 'GameData' }, { source: 'product-name', value: 'SiglusEngine' }, { source: 'file-description', value: 'SiglusEngine' }], [], { companies: ['Studio Blue'] })
  noneAutomatic(input, [candidate], 'engine resource strings are not titles, even if the company is correct')
})

test('company substring coincidences are not corroboration and legal suffixes preserve exact brands', () => {
  const candidate = work('1', 'Distinctive official title', { developers: ['Key'] })
  const titles: TitleEvidence[] = [{ source: 'folder', value: candidate.title }]
  const baseline = rankEvidenceMatches(identity(titles), [candidate])[0]
  const unrelated = rankEvidenceMatches(identity(titles, [], { companies: ['Monkey'] }), [candidate])[0]
  assert.equal(unrelated.confidence, baseline.confidence, 'Key occurring inside Monkey is not a developer match')
  const exact = rankEvidenceMatches(identity(titles, [], { companies: ['Key Co., Ltd.'] }), [candidate])[0]
  assert(exact.confidence > baseline.confidence, 'a legal suffix does not hide an otherwise exact brand')
  const repeated = rankEvidenceMatches(identity(titles, [], { companies: ['Key Co., Ltd.', 'Key', 'Key'] }), [candidate])[0]
  assert.equal(repeated.confidence, exact.confidence, 'repeated company strings cannot add votes')
})

test('repeated PE titles and company matches cannot overrule a conflicting physical installation ID', () => {
  const title = 'Starfall Memories'
  const candidate = work('wrong-id', title, { developers: ['Studio Blue'], references: [{ source: 'steam', id: '200' }, { source: 'steam', id: '201' }] })
  const input = identity([
    { source: 'folder', value: title }, { source: 'product-name', value: title }, { source: 'file-description', value: title },
  ], [{ source: 'steam-file', value: 'steam:100' }], { companies: ['Studio Blue'], fingerprints: ['repeated-resource-fingerprint'] })
  noneAutomatic(input, [candidate], 'matching text and brand do not override the physically observed installation number')
})

test('conflicting PE chapter fields cannot be outvoted by repeated matching fields', () => {
  const title = '9-nine- Episode 2'
  const input = identity([
    { source: 'folder', value: title },
    ...Array.from({ length: 8 }, (): TitleEvidence => ({ source: 'product-name', value: title })),
    { source: 'file-description', value: '9-nine- Episode 1' },
  ], [{ source: 'steam-config', value: 'steam:100' }])
  noneAutomatic(input, [work('2', title, { references: [{ source: 'steam', id: '100' }] })], 'inconsistent version-resource fields require review regardless of repeats')
})

test('machine-generated PE name matches and fingerprints do not establish canonical identity', () => {
  const generated = 'A generated game translation'
  const input = identity([{ source: 'folder', value: 'GameData' }, { source: 'product-name', value: generated }, { source: 'file-description', value: generated }], [], { companies: ['Studio Blue'], fingerprints: ['opaque-resource-digest'] })
  const candidate = work('machine-title', generated, {
    originalTitle: '本当の原題', aliases: [generated], developers: ['Studio Blue'],
    titleEvidence: [{ text: generated, kind: 'official', isMachine: true, language: 'en' }, { text: '本当の原題', kind: 'original', isMachine: false, language: 'ja' }],
  })
  noneAutomatic(input, [candidate], 'a metadata digest is not an external executable-to-work database match')
})

test('conflicting canonical chapters in an aggregate record remain explicit review cases', () => {
  const local = '9-nine- Episode 2'
  const input = identity([{ source: 'folder', value: local }, { source: 'product-name', value: local }], [{ source: 'steam-file', value: 'steam:200' }])
  const aggregate = work('aggregate', '9-nine- Episode 1', {
    originalTitle: local, references: [{ source: 'steam', id: '100' }, { source: 'steam', id: '200' }],
    titleEvidence: [{ text: local, kind: 'official', isMachine: false, language: 'en' }],
  })
  noneAutomatic(input, [aggregate], 'work-level Steam lists do not bind a specific edition to a specific release; a contradictory primary chapter is not waived')
})

test('CLANNAD on the Adastra alias list cannot identify Adastra from a folder and executable', () => {
  const input = identity([{ source: 'folder', value: 'CLANNAD' }, { source: 'product-name', value: 'CLANNAD' }])
  noneAutomatic(input, [work('adastra', 'Adastra', { aliases: ['CLANNAD'] })], 'a polluted alias does not override the unrelated primary title')
})

test('a title-index reference derived from a polluted alias is not independent evidence', () => {
  const input = identity([{ source: 'folder', value: 'CLANNAD' }], [{ source: 'title-index', value: 'vndb:v999', via: 'CLANNAD' }])
  noneAutomatic(input, [work('adastra', 'Adastra', { aliases: ['CLANNAD'], references: [{ source: 'vndb', id: 'v999' }] })], 'the same alias cannot prove itself through an index mapping')
})

test('a complete official alias with provenance can support a translated primary title only after details are known', () => {
  const input = identity([{ source: 'folder', value: 'as 9-nine- ARTEISIA' }])
  const result = work('correct', 'as:9-nine- アルテイシア', { aliases: ['as:9-nine- ARTEISIA'] })
  noneAutomatic(input, [result], 'search alias strings alone are unproven')
  assert.equal(rankEvidenceMatches(input, [{ ...result, titleEvidence: [{ text: 'as:9-nine- ARTEISIA', kind: 'official', isMachine: false, language: 'en' }] }])[0].automatic, true)
  noneAutomatic(input, [{ ...result, titleEvidence: [{ text: 'as:9-nine- ARTEISIA', kind: 'official', isMachine: true, language: 'en' }] }], 'machine generated names are not official evidence')
  noneAutomatic(input, [{ ...result, titleEvidence: [{ text: 'as:9-nine- ARTEISIA', kind: 'alias', isMachine: false, language: 'en' }] }], 'generic alias provenance is not official evidence')
})

test('generic GameData and auxiliary directories never silently become a game', () => {
  const candidate = work('1', 'Distinctive official title', { references: [{ source: 'steam', id: '100' }] })
  noneAutomatic(identity([{ source: 'folder', value: 'GameData' }, { source: 'executable-name', value: 'Game' }], [{ source: 'steam-config', value: 'steam:100' }]), [candidate], 'generic folder and engine names carry no game identity')
  noneAutomatic(identity([{ source: 'folder', value: 'Distinctive official title' }], [{ source: 'steam-config', value: 'steam:100' }], { auxiliaryDirectory: true }), [candidate], 'a patch/backup directory requires choosing the game root')
})

test('file and archive stems sharing one origin cannot authorize an unverified alias', () => {
  const titles: TitleEvidence[] = [{ source: 'folder', value: 'GameData' }, { source: 'executable-name', value: 'Rance01' }, { source: 'archive-name', value: 'Rance01' }]
  const candidate = work('wrong', 'Rance 1', { aliases: ['Rance01'], references: [{ source: 'vndb', id: 'v1' }] })
  noneAutomatic(identity(titles), [candidate], 'repeated file stems are one source')
  noneAutomatic(identity(titles, [{ source: 'title-index', value: 'vndb:v1', via: 'Rance01' }]), [candidate], 'a derived index cannot upgrade the same weak filename alias into a second source')
})

test('Rance01 program evidence selects the official Rance 01 remake, never the original work with a polluted alias', () => {
  const titles: TitleEvidence[] = [
    { source: 'folder', value: 'Rance 1' },
    { source: 'executable-name', value: 'Rance01' },
    { source: 'archive-name', value: 'Rance01' },
  ]
  const remake = work('557', 'Rance 01 - Hikari o Motomete', {
    originalTitle: 'ランス01 光をもとめて', aliases: ['Rance01', 'Rance 01', 'Rance 1'],
    titleEvidence: [{ text: 'Rance 01 - Hikari o Motomete', kind: 'official', isMachine: false, language: 'en' }],
    references: [{ source: 'vndb', id: 'v14022' }],
  })
  const original = work('215765', '兰斯－寻找小光－', {
    originalTitle: 'Rance -光を求めて-', aliases: ['Rance 1', 'Rance01'],
    references: [{ source: 'bangumi', id: '75442' }],
  })
  for (const refs of [[], [{ source: 'title-index' as const, value: 'vndb:v14022', via: 'Rance01' }]]) {
    const ranked = rankEvidenceMatches(identity(titles, refs), [original, remake])
    assert.equal(ranked[0].work.id, '557', `the explicit 01 program name must outrank an unverified alias on the original; refs=${JSON.stringify(refs)}; results=${JSON.stringify(ranked.map(({ work, confidence, automatic }) => ({ id: work.id, confidence, automatic })))}`)
    assert.equal(ranked[0].automatic, true, `official remake title plus local program evidence should resolve this case: ${JSON.stringify(ranked.map(({ work, confidence, automatic }) => ({ id: work.id, confidence, automatic })))}`)
    assert.equal(ranked.find(result => result.work.id === '215765')!.automatic, false)
  }
  noneAutomatic(identity(titles), [original], 'an original-only API result must not be accepted through polluted Rance01 aliases')
})

test('machine generated display or original titles cannot become canonical exact evidence', () => {
  const generated = 'A machine generated translation'
  const input = identity([{ source: 'folder', value: generated }])
  for (const candidate of [
    work('machine-display', generated, { originalTitle: '本当の原題' }),
    work('machine-original', '本当の原題', { originalTitle: generated }),
  ]) {
    candidate.titleEvidence = [{ text: generated, kind: 'official', isMachine: true, language: 'en' }, { text: '本当の原題', kind: 'original', isMachine: false, language: 'ja' }]
    noneAutomatic(input, [candidate], 'an explicitly machine-generated string cannot regain official status through title/originalTitle')
  }
})

test('a copied README and its derived title-index number do not independently identify a generic folder', () => {
  const title = 'Distinctive official title'
  const candidate = work('copied-readme', title, {
    references: [{ source: 'vndb', id: 'v123' }],
    titleEvidence: [{ text: title, kind: 'official', isMachine: false, language: 'en' }],
  })
  const titles: TitleEvidence[] = [{ source: 'folder', value: 'GameData' }, { source: 'readme-title', value: title }]
  noneAutomatic(identity(titles), [candidate], 'a README without corroborating folder/program evidence is insufficient')
  noneAutomatic(identity(titles, [{ source: 'title-index', value: 'vndb:v123', via: title }]), [candidate], 'the README title and the number derived from that same title share one origin')
})

test('a physical Steam ID and local title outrank same-title catalog projections without that ID', () => {
  const title = 'STEINS;GATE RE:BOOT'
  const input = identity([{ source: 'folder', value: 'STEINS.GATE.RE.BOOT' }, { source: 'executable-name', value: 'sgre_steam' }], [{ source: 'steam-config', value: 'steam:4012810' }])
  const anchored = work('60', '命运石之门', {
    aliases: [title], references: [{ source: 'vndb', id: 'v2002' }, { source: 'steam', id: '412830' }, { source: 'steam', id: '4012810' }],
    titleEvidence: [{ text: title, kind: 'alias', isMachine: false, language: 'en' }],
  })
  const projections = [work('222214', title, { references: [{ source: 'bangumi', id: '521699' }] }), work('236013', title, { references: [{ source: 'erogamescape', id: '40762' }] })]
  const ranked = rankEvidenceMatches(input, [...projections, anchored])
  assert.equal(ranked[0].work.id, '60')
  assert.equal(ranked[0].automatic, true)
  assert(ranked.slice(1).every(result => !result.automatic))
  assert.match(ranked[0].reason, /Steam/)
})

test('physical IDs still require real local text and cannot override edition or chapter conflicts', () => {
  for (const [localTitle, wrongTitle] of [['9-nine- Episode 2', '9-nine- Episode 1'], ['Title SANA EDITION', 'Title Remake for FHD']]) {
    const input = identity([{ source: 'folder', value: localTitle }], [{ source: 'steam-config', value: 'steam:100' }])
    const wrong = work('wrong', wrongTitle, { aliases: [localTitle], references: [{ source: 'steam', id: '100' }] })
    noneAutomatic(input, [wrong], 'a correct source number cannot erase the explicit edition mismatch')
  }
  const generic = identity([{ source: 'folder', value: 'GameData' }, { source: 'readme-title', value: 'A copied official title' }], [{ source: 'steam-config', value: 'steam:100' }])
  noneAutomatic(generic, [work('1', 'A copied official title', { references: [{ source: 'steam', id: '100' }] })], 'a README alone is not a physical title anchor')
})

test('same-row index provenance bridges a complete non-machine alias to the canonical work', () => {
  for (const source of ['folder', 'product-name', 'file-description', 'executable-name'] as const) {
    const { input, candidate } = witchBridge(source)
    const ranked = rankEvidenceMatches(input, [candidate])
    assert.equal(ranked[0].automatic, true, source)
    assert.match(ranked[0].reason, /同一索引/)
    input.evidence.indexProofs = []
    noneAutomatic(input, [candidate], 'bare references and an alias are weaker than a checked same-row bridge')
  }
})

test('the index can bridge across scripts even when the catalog omits the local alias', () => {
  const { input, candidate } = witchBridge()
  candidate.aliases = []
  candidate.titleEvidence = candidate.titleEvidence!.filter(title => title.kind !== 'alias')
  assert.equal(rankEvidenceMatches(input, [candidate])[0].automatic, true)
})

test('manual and configuration bridges require an independent directory or program anchor', () => {
  for (const source of ['manual-title', 'config-title'] as const) {
    const { input, candidate, localTitle } = witchBridge(source)
    input.evidence.titles.push({ source: 'folder', value: 'GameData' })
    noneAutomatic(input, [candidate], 'a copied HTML/manual/config title in a generic directory is not sufficient')
    input.evidence.titles.push({ source: 'folder', value: localTitle })
    assert.equal(rankEvidenceMatches(input, [candidate])[0].automatic, true)
  }
})

test('sidecar, README, and archive-only observations cannot form independent index bridges', () => {
  for (const source of ['sidecar', 'readme-title', 'archive-name'] as const) {
    const { input, candidate } = witchBridge(source)
    input.evidence.titles.push({ source: 'folder', value: 'GameData' })
    noneAutomatic(input, [candidate], source)
  }
})

test('an index bridge must validate the observed title, canonical title, and reference within one row', () => {
  const { input, candidate, proof, localTitle } = witchBridge()
  input.evidence.indexProofs = [
    { ...proof, recordNames: [localTitle, 'Unrelated canonical title'] },
    { ...proof, recordNames: ['魔法使之夜'] },
  ]
  noneAutomatic(input, [candidate], 'two half-proofs cannot be stitched together')
  input.evidence.indexProofs = [{ ...proof, references: ['vndb:v999'] }]
  noneAutomatic(input, [candidate], 'a canonical spelling is insufficient when the row identifies a different work')
  input.evidence.indexProofs = [{ ...proof, observedTitle: 'Another local title' }]
  noneAutomatic(input, [candidate], 'the observed text must actually be present in local evidence')
  input.evidence.indexProofs = [{ ...proof, source: 'product-name' }]
  noneAutomatic(input, [candidate], 'a folder observation must not be relabeled as program metadata')
})

test('a CLANNAD alias on Adastra cannot complete a canonical index bridge', () => {
  const input = identity([{ source: 'folder', value: 'CLANNAD' }], [{ source: 'title-index', value: 'vndb:v999' }], {
    indexProofs: [{ source: 'folder', observedTitle: 'CLANNAD', recordNames: ['CLANNAD'], references: ['vndb:v999'] }],
  })
  noneAutomatic(input, [work('adastra', 'Adastra', { aliases: ['CLANNAD'], references: [{ source: 'vndb', id: 'v999' }] })], 'an external index ID alone must not promote a polluted API alias')
})

test('machine-generated aliases or canonical names cannot complete an index bridge', () => {
  const { input, candidate, localTitle } = witchBridge()
  candidate.titleEvidence = candidate.titleEvidence!.map(title => title.text === localTitle ? { ...title, isMachine: true } : title)
  noneAutomatic(input, [candidate], 'a generated alias cannot be upgraded through index provenance')
  candidate.titleEvidence = [
    { text: candidate.title, kind: 'official', isMachine: true, language: 'zh' },
    { text: candidate.originalTitle, kind: 'original', isMachine: true, language: 'ja' },
    { text: localTitle, kind: 'alias', isMachine: false, language: 'en' },
  ]
  noneAutomatic(input, [candidate], 'a generated canonical endpoint cannot complete the bridge')
})

test('a verified index bridge does not suppress a competing different VNDB identity', () => {
  const { input, candidate, localTitle } = witchBridge()
  const other = work('other-vndb', localTitle, { references: [{ source: 'vndb', id: 'v999' }] })
  noneAutomatic(input, [candidate, other], 'a different work identity with a full official matching title still needs review')
})

test('formal releases distinguish the supported edition from a thin same-title catalog record', () => {
  for (const kind of ['default', 'digital', 'physical']) {
    const { input, anchored, thin } = summerRelease(kind)
    const ranked = rankEvidenceMatches(input, [thin, anchored])
    assert.equal(ranked[0].work.id, '4', kind)
    assert.equal(ranked[0].automatic, true, kind)
    assert.equal(ranked[1].automatic, false, kind)
    assert.match(ranked[0].reason, /正式发行/)
    input.releases = {}
    noneAutomatic(input, [thin, anchored], 'without release evidence, the earlier same-title ambiguity rule remains in effect')
  }
})

test('patch, trial, unknown, and mismatched releases cannot resolve same-title ambiguity', () => {
  for (const kind of ['patch', 'trial', 'unknown', 'demo', '']) {
    const { input, anchored, thin } = summerRelease(kind)
    noneAutomatic(input, [anchored, thin], kind || 'empty release kind')
  }
  const { input, anchored, thin, release } = summerRelease()
  input.releases = { '4': [{ ...release, workId: 'another-work' }] }
  noneAutomatic(input, [anchored, thin], 'a release attached to another work cannot validate this candidate')
  input.releases = { '4': [{ ...release, title: 'Summer Pockets', originalTitle: 'Summer Pockets' }] }
  noneAutomatic(input, [anchored, thin], 'a base-work release does not validate the explicit REFLECTION BLUE edition')
})

test('formal releases require index support and cannot rehabilitate a copied manual in GameData', () => {
  const { input, anchored, thin, title } = summerRelease()
  input.evidence.references = []
  noneAutomatic(input, [anchored, thin], 'a release title alone is not independent index or installation evidence')
  input.evidence.references = [{ source: 'title-index', value: 'vndb:v20424' }]
  input.evidence.titles = [{ source: 'folder', value: 'GameData' }, { source: 'manual-title', value: title }]
  input.evidence.indexProofs = [{ source: 'manual-title', observedTitle: title, recordNames: [title], references: ['vndb:v20424'] }]
  noneAutomatic(input, [anchored, thin], 'copied HTML and its derived number remain one origin even with a matching release')
})

test('an edition release does not erase an explicit competing VNDB work identity', () => {
  const { input, anchored, thin } = summerRelease()
  thin.references.push({ source: 'vndb', id: 'v999' })
  noneAutomatic(input, [anchored, thin], 'the thin record is no longer merely an unidentified duplicate')
})

test('release counts and references are not extra votes and do not increase confidence', () => {
  const { input, anchored, thin, release } = summerRelease()
  const first = rankEvidenceMatches(input, [anchored, thin])[0]
  input.releases = { '4': Array.from({ length: 25 }, (_, index) => ({ ...release, id: `release-${index}`, references: [{ source: 'steam', id: `${10000 + index}` }, { source: 'vndb', id: 'v999' }] })) }
  const repeated = rankEvidenceMatches(input, [anchored, thin])[0]
  assert.equal(repeated.automatic, true)
  assert.equal(repeated.confidence, first.confidence)
})

test('explicit official localized spellings can match individually while unverified aliases cannot', () => {
  const local = '常轨脱离Creative'
  const input = identity([{ source: 'folder', value: local }], [{ source: 'title-index', value: 'vndb:v27449', via: local }])
  const candidate = work('17', '常轨脱离Creative | 常轨脱离的创造力 | 常轨', {
    originalTitle: 'ハミダシクリエイティブ', references: [{ source: 'vndb', id: 'v27449' }],
    titleEvidence: [{ text: '常轨脱离Creative | 常轨脱离的创造力 | 常轨', kind: 'official', isMachine: false, language: 'zh-Hans' }],
  })
  assert.equal(rankEvidenceMatches(input, [candidate])[0].automatic, true)
  noneAutomatic(input, [{ ...candidate, title: 'ハミダシクリエイティブ', aliases: [candidate.title], titleEvidence: [{ text: candidate.title, kind: 'alias', isMachine: false, language: null }] }], 'only explicit official title provenance supports separately listed names')
})
