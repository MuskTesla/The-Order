import assert from 'node:assert/strict'
import { test } from 'node:test'
import { compareGameTitles, gameTitleVariants, hasExplicitGameVersion, normalizeGameTitle } from '../electron/title-matching'

test('normalization folds Unicode layout and Latin accents while preserving Japanese voiced characters', () => {
  assert.equal(normalizeGameTitle(' Ｃａｆé—Ｓｔｅｌｌａ！ '), 'cafe stella')
  assert.equal(normalizeGameTitle('Café Stella'), normalizeGameTitle('Cafe\u0301 Stella'))
  assert.equal(normalizeGameTitle('パルフェ'), 'パルフェ')
  assert.notEqual(normalizeGameTitle('パルフェ'), normalizeGameTitle('ハルフェ'))
  assert.equal(normalizeGameTitle('STEINS.GATE.RE.BOOT'), 'steins gate re boot')
  assert.equal(normalizeGameTitle('WHITE ALBUM２'), 'white album2')
})

test('packaging cleanup is narrow and full original text stays the first query', () => {
  const original = '【PC】常轨脱离Creative [简体中文] 汉化硬盘版'
  assert.equal(normalizeGameTitle(original), '常轨脱离creative')
  const variants = gameTitleVariants(original)
  assert.equal(variants[0], original)
  assert(variants.includes('常轨脱离Creative'))
  assert(normalizeGameTitle('作品 (全年龄版)').includes('全年龄版'))
  assert(normalizeGameTitle('作品 v2.0').includes('v2 0'))
})

test('query variants support punctuation/romanization layouts without inventing transliterations', () => {
  const variants = gameTitleVariants('as 9-nine- ARTEISIA')
  assert.equal(variants[0], 'as 9-nine- ARTEISIA')
  assert(variants.some(value => value.startsWith('as:9-nine-')))
  assert(variants.some(value => value.includes('9nine')))
  assert(variants.every(value => !value.includes('アルテイシア')))
  assert.equal(compareGameTitles('as 9-nine- ARTEISIA', 'as:9-nine- ARTEISIA').exact, true)
  const crossScript = compareGameTitles('as 9-nine- ARTEISIA', 'as:9-nine- アルテイシア')
  assert.equal(crossScript.exact, false)
  assert(crossScript.score < 0.8, 'official aliases or source IDs must establish a translation')
})

test('edition base queries retrieve candidates without declaring the base title equivalent', () => {
  const variants = gameTitleVariants('DRACU-RIOT! QHD Edition')
  assert.equal(variants[0], 'DRACU-RIOT! QHD Edition')
  assert(variants.includes('dracu riot'))
  assert(variants.includes('dracuriot'))
  assert(variants.includes('dracuriotqhd'))
  assert(variants.length <= 8)
  const comparison = compareGameTitles('DRACU-RIOT! QHD Edition', 'DRACU-RIOT!')
  assert.equal(comparison.version, 'unspecified')
  assert.equal(comparison.exact, false)
  assert(comparison.score >= 0.8 && comparison.score <= 0.88)
})

test('full-title layout variants remain exact and distinctive', () => {
  for (const [local, candidate] of [
    ['Senrenbanka', 'Senren Banka'],
    ['Café Stella', 'Cafe Stella'],
    ['STEINS.GATE.RE.BOOT', 'STEINS;GATE RE-BOOT!'],
    ['Summer Pockets -REFLECTION BLUE-', 'Summer Pockets REFLECTION BLUE'],
    ['天使☆騒々', '天使騒々'],
    ['2236 A.D', '2236 A.D.'],
  ]) {
    const result = compareGameTitles(local, candidate)
    assert.equal(result.exact, true, local)
    assert.equal(result.score, 1, local)
    assert.equal(result.version, 'same', local)
    assert.equal(result.conflict, null, local)
    assert.equal(result.distinctive, true, local)
  }
})

test('episode numbers and sequel digits block close series-title false matches', () => {
  for (const [local, candidate] of [
    ['9-nine- Episode 1', '9-nine- Episode 2'],
    ['9-nine- Episode 2', '9-nine- Episode 3'],
    ['Rance 1', 'Rance 2'], ['WHITE ALBUM2', 'WHITE ALBUM3'],
    ['Ever17', 'Ever7'], ['Game2', 'Game3'], ['作品 第1章', '作品 第2章'],
    ['作品 第十二章', '作品 第十三章'], ['戦国†恋姫 BRAVE 壱', '戦国†恋姫 BRAVE 弐'],
  ]) {
    const result = compareGameTitles(local, candidate)
    assert.equal(result.version, 'conflict', `${local} vs ${candidate}`)
    assert(result.conflict, `${local} vs ${candidate}`)
    assert.equal(result.exact, false)
    assert(result.score <= 0.25)
  }
  const missing = compareGameTitles('WHITE ALBUM2', 'WHITE ALBUM')
  assert.equal(missing.version, 'unspecified')
  assert(missing.conflict)
  assert(missing.score <= 0.65)
})

test('standard chapter-number spelling can rank together without erasing the chapter', () => {
  for (const [local, candidate] of [
    ['9-nine- Episode 1', '9-nine- Episode I'],
    ['9-nine- Episode 2', '9-nine- Episode two'],
    ['Title Chapter 01', 'Title Chapter 1'],
    ['Rance 1', 'Rance I'],
    ['作品 第十二章', '作品 第12章'], ['戦国†恋姫 BRAVE 弐', '戦国†恋姫 BRAVE 2'],
  ]) {
    const result = compareGameTitles(local, candidate)
    assert.equal(result.conflict, null, `${local} vs ${candidate}`)
    assert.equal(result.version, 'same', `${local} vs ${candidate}`)
    assert(result.score >= 0.9, `${local} vs ${candidate}: ${result.score}`)
  }
})

test('leading zeros are retained in exact names even when chapter numbers are semantically consistent', () => {
  assert.notEqual(normalizeGameTitle('Rance1'), normalizeGameTitle('Rance01'))
  const result = compareGameTitles('Rance1', 'Rance01')
  assert.equal(result.exact, false)
  assert.equal(result.version, 'same')
  assert.equal(result.conflict, null)
  assert(result.score < 1 && result.score > 0.7)
})

test('New Episode is distinct from every numbered episode and is not silently folded into the base', () => {
  for (const episode of [1, 2, 3, 4]) {
    const result = compareGameTitles('9-nine-New Episode', `9-nine- Episode ${episode}`)
    assert.equal(result.version, 'conflict')
    assert(result.conflict)
    assert(result.score <= 0.25)
  }
  const base = compareGameTitles('9-nine-New Episode', '9-nine-')
  assert.equal(base.version, 'unspecified')
  assert.equal(base.exact, false)
  assert.equal(compareGameTitles('9-nine-新章', '9-nine- Episode 4').version, 'conflict')
  assert.equal(compareGameTitles('9-nine- New Chapter', '9-nine- New Episode').version, 'same')
})

test('named editions, remakes, reboot, resolution, and anniversary markers survive comparison', () => {
  for (const [local, candidate] of [
    ['纯白交响曲 SANA EDITION', '纯白交响曲 Remake for FHD'],
    ['Title Remake', 'Title REMASTERED'],
    ['Title Remake FHD', 'Title REMASTERED FHD'],
    ['Title QHD R18 Edition', 'Title QHD All Ages Edition'],
    ['Title QHD Edition', 'Title FHD Edition'],
    ['Title 10th Anniversary edition', 'Title 20th Anniversary edition'],
    ['Title v1.0', 'Title v2.0'],
  ]) {
    const result = compareGameTitles(local, candidate)
    assert.equal(result.version, 'conflict', `${local} vs ${candidate}`)
    assert(result.conflict)
    assert(result.score <= 0.35)
  }
  for (const [local, candidate] of [
    ['Summer Pockets -REFLECTION BLUE-', 'Summer Pockets'],
    ['纯白交响曲 SANA EDITION', '纯白交响曲'],
    ['纯白交响曲 Remake for FHD', '纯白交响曲'],
    ['STEINS.GATE.RE.BOOT', 'STEINS;GATE'],
    ['Fate stay night REMASTERED', 'Fate/stay night'],
    ['サクラノ詩 10th Anniversary edition', 'サクラノ詩'],
    ['2236 A.D. -R18 Edition-', '2236 A.D.'],
  ]) {
    const result = compareGameTitles(local, candidate)
    assert.equal(result.version, 'unspecified', `${local} vs ${candidate}`)
    assert.equal(result.exact, false)
    assert(result.score <= 0.88)
  }
})

test('FD and After are separate expansion markers and require evidence against the base', () => {
  for (const extra of ['FD', 'Fan Disc', 'After', 'After Story']) {
    const result = compareGameTitles(`Long title ${extra}`, 'Long title')
    assert.equal(result.version, 'unspecified', extra)
    assert.equal(result.exact, false)
    assert(result.score <= 0.88)
  }
  assert.equal(compareGameTitles('Long title FD', 'Long title After').version, 'conflict')
  assert.equal(compareGameTitles('Long title FD', 'Long title Fan Disc').version, 'same')
})

test('explicit version labels can be preserved when metadata uses a broader work title', () => {
  for (const title of ['DRACU-RIOT! QHD Edition', 'STEINS.GATE.RE.BOOT', 'Fate stay night REMASTERED', 'Summer Pockets REFLECTION BLUE', '纯白交响曲 SANA EDITION', '纯白交响曲 Remake for FHD', 'サクラノ詩 10th Anniversary edition', 'Long title FD', '9-nine- New Episode', '9-nine- Episode 1']) {
    assert.equal(hasExplicitGameVersion(title), true, title)
  }
  for (const title of ['AIR', 'Rance1', 'Rance01', '2236 A.D', 'RIDDLE JOKER']) assert.equal(hasExplicitGameVersion(title), false, title)
})

test('short titles remain nondistinctive and ASCII substrings do not create strong matches', () => {
  const air = compareGameTitles('AIR', 'AIR')
  assert.equal(air.exact, true)
  assert.equal(air.distinctive, false)
  for (const candidate of ['CHAIR', 'FAIRY', 'AIR 2']) {
    const result = compareGameTitles('AIR', candidate)
    assert.equal(result.exact, false)
    assert.equal(result.distinctive, false)
    assert(result.score <= 0.45)
  }
  assert(compareGameTitles('Dream', 'Dreamland').score <= 0.6)
  assert.equal(compareGameTitles('Game', 'Game2').exact, false)
})

test('meaningless directory and engine names cannot become identity queries', () => {
  for (const name of ['GameData', 'Game_Data', 'Game', 'ONScripter', 'SiglusEngine', 'AdvHD', 'data', 'win64', '游戏目录']) {
    assert.deepEqual(gameTitleVariants(name), [], name)
    const result = compareGameTitles(name, name)
    assert.equal(result.score, 0, name)
    assert.equal(result.exact, false, name)
    assert.equal(result.distinctive, false, name)
    assert(result.conflict)
  }
})

test('a polluted exact alias does not provide authority over a conflicting primary title', () => {
  const local = 'CLANNAD', wrongPrimary = 'Tomoyo After', pollutedAlias = 'CLANNAD'
  const alias = compareGameTitles(local, pollutedAlias), primary = compareGameTitles(local, wrongPrimary)
  assert.equal(alias.exact, true, 'a string helper cannot discover which work owns an alias')
  assert.equal(primary.exact, false)
  assert.equal(primary.version, 'unspecified')
  assert(primary.score < 0.5)
  const versionAlias = compareGameTitles('Summer Pockets', 'Summer Pockets')
  const versionPrimary = compareGameTitles('Summer Pockets', 'Summer Pockets REFLECTION BLUE')
  assert.equal(versionAlias.exact, true)
  assert.equal(versionPrimary.version, 'unspecified', 'the caller must also compare the actual work title')
})

test('scores are bounded, comparisons are symmetric, and reasonable typos rank above unrelated names', () => {
  const near = compareGameTitles('RIDDLE JOKER', 'RIDDLE JOKR')
  const wrong = compareGameTitles('RIDDLE JOKER', 'Summer Pockets')
  assert(near.score > wrong.score + 0.3)
  assert.equal(near.exact, false)
  const pairs = [['AIR', 'CHAIR'], ['Title FD', 'Title'], ['Café Stella', 'Cafe Stella'], ['9-nine Episode 1', '9-nine Episode 2']]
  for (const [a, b] of pairs) {
    const forward = compareGameTitles(a, b), reverse = compareGameTitles(b, a)
    assert.equal(forward.score, reverse.score)
    assert.equal(forward.version, reverse.version)
    assert(forward.score >= 0 && forward.score <= 1)
  }
  assert.deepEqual(gameTitleVariants('x'.repeat(501)), [])
  assert.equal(compareGameTitles('', 'A title').score, 0)
  assert.equal(normalizeGameTitle('x'.repeat(501)), '')
})
