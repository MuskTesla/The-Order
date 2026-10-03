import assert from 'node:assert/strict'
import { promises as fs } from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import test from 'node:test'
import { readLocalTextEvidence } from '../electron/local-text-evidence'
import type { LibraryGame } from '../src/shared/types'

async function environment(t: { after(callback: () => Promise<void>): void }) {
  const temporary = path.resolve(os.tmpdir())
  const root = await fs.mkdtemp(path.join(temporary, 'kgm-text-evidence-'))
  t.after(async () => {
    assert.equal(path.dirname(path.resolve(root)), temporary)
    assert.ok(path.basename(root).startsWith('kgm-text-evidence-'))
    await fs.rm(root, { recursive: true, force: true })
  })
  const folder = path.join(root, 'Games'); await fs.mkdir(folder)
  const game: LibraryGame = {
    id: 'local-game', name: 'Short local title', metadata: null,
    installations: [{ id: 'install', folderPath: folder, executablePath: null, arguments: [] }], preferredInstallationId: 'install',
    status: 'planned', personalRating: null, favorite: false, categories: [], customTags: [], notes: '',
    totalPlaySeconds: 0, playCount: 0, sessions: [], addedAt: '2026-10-02T00:00:00Z', lastPlayedAt: null
  }
  return { root, folder, game }
}

test('a Japanese manual supplies a full title and explicitly labelled brand without executable metadata', async t => {
  const { folder, game } = await environment(t)
  await fs.writeFile(path.join(folder, 'お読みください.html'), `<!doctype html><meta charset="utf-8">
    <title>お読み下さい『夜明け☆航路 RE-BOOT!』</title>
    <h1>蒼い鳥ソフト『夜明け☆航路 RE-BOOT!』について</h1>
    <p>この度は弊社製品「夜明け☆航路 RE-BOOT!」をお買い上げいただきありがとうございます。</p>`)
  await fs.writeFile(path.join(folder, 'Game.exe'), 'Program contents must not be opened')
  await fs.writeFile(path.join(folder, 'Novel.ypf'), 'Archive contents must not be opened')
  const opened: string[] = [], originalOpen = fs.open
  t.mock.method(fs, 'open', (...args: Parameters<typeof fs.open>) => {
    opened.push(String(args[0])); assert.match(String(args[0]), /\.html$/i)
    return originalOpen(...args)
  })
  const readSpy = t.mock.method(fs, 'readFile', () => { throw new Error('Whole file reads are forbidden') })
  const evidence = await readLocalTextEvidence(game)
  assert.deepEqual(evidence.titles, [{ value: '夜明け☆航路 RE-BOOT!', source: 'manual-title' }])
  assert.deepEqual(evidence.companies, ['蒼い鳥ソフト'])
  assert.equal(opened.length, 1)
  assert.equal(readSpy.mock.callCount(), 0)
  assert.deepEqual(evidence.warnings, [])
})

test('nested manual title retains an edition and decodes Shift-JIS declared by HTML', async t => {
  const { folder, game } = await environment(t)
  await fs.mkdir(path.join(folder, 'manual'))
  const title = Buffer.from('90af8bf382cc8d719848', 'hex') // 星空の航路, Shift-JIS
  await fs.writeFile(path.join(folder, 'manual', 'manual.html'), Buffer.concat([
    Buffer.from('<meta http-equiv="Content-Type" content="text/html; charset=Shift_JIS"><title>'), title,
    Buffer.from(' REFLECTION BLUE - WEB Manual</title>')
  ]))
  const evidence = await readLocalTextEvidence(game)
  assert.deepEqual(evidence.titles, [{ value: '星空の航路 REFLECTION BLUE', source: 'manual-title' }])
})

test('StartData title keys support Shift-JIS while unrelated and credential values are omitted', async t => {
  const { folder, game } = await environment(t)
  await fs.mkdir(path.join(folder, 'StartData'))
  await fs.writeFile(path.join(folder, 'StartData', 'Start.ini'), Buffer.concat([
    Buffer.from('#GAME_TITLE = "'), Buffer.from('90af8bf382cc8d719848', 'hex'),
    Buffer.from('"\r\nBrand = "Bluebird Studio"\r\nApiKey = "must-never-appear"\r\nExecutable = "other.exe"')
  ]))
  const evidence = await readLocalTextEvidence(game)
  assert.deepEqual(evidence.titles, [{ value: '星空の航路', source: 'config-title' }])
  assert.deepEqual(evidence.companies, ['Bluebird Studio'])
  assert.equal(JSON.stringify(evidence).includes('must-never-appear'), false)
})

test('GB18030 labelled game and brand keys are decoded without assuming Japanese', async t => {
  const { folder, game } = await environment(t)
  await fs.writeFile(path.join(folder, 'Game.ini'), Buffer.from('d3cecfb7c3fbb3c63db3bfceedbabdcfdf0ac6b7c5c63dc0b6c2b9c9e7', 'hex'))
  const evidence = await readLocalTextEvidence(game)
  assert.deepEqual(evidence.titles, [{ value: '晨雾航线', source: 'config-title' }])
  assert.deepEqual(evidence.companies, ['蓝鹿社'])
})

test('generic headings and unrelated linked titles never become product evidence or get fetched', async t => {
  const { folder, game } = await environment(t)
  await fs.writeFile(path.join(folder, 'manual.html'), `<title>Game Manual</title><h1>ゲームマニュアル</h1>
    <h2>インストール方法</h2><h2>操作方法</h2><h3>Contents</h3>
    <p>See also 『Another Unrelated Novel』</p><a href="https://example.invalid/title">An unrelated work</a>`)
  t.mock.method(globalThis, 'fetch', () => { throw new Error('Manual links must never be fetched') })
  const evidence = await readLocalTextEvidence(game)
  assert.deepEqual(evidence.titles, [])
  assert.deepEqual(evidence.companies, [])
})

test('scripts, styles, comments and templates neither run nor contribute title evidence', async t => {
  const { folder, game } = await environment(t)
  await fs.writeFile(path.join(folder, 'manual.html'), `<script>throw new Error('must not run'); '<h1>Forged Script Title</h1>'</script>
    <style>.x:before { content:'<title>Forged Style</title>' }</style>
    <!-- <h2>Forged Comment</h2> --><template><h1>Forged Template</h1></template>
    <title>Silver &amp; Gold &#9734; - Manual</title><h2>メーカー：Bluebird Studio</h2>`)
  const evidence = await readLocalTextEvidence(game)
  assert.deepEqual(evidence.titles, [{ value: 'Silver & Gold ☆', source: 'manual-title' }])
  assert.deepEqual(evidence.companies, ['Bluebird Studio'])
})

test('only the selected installation is read, with no parent, sibling or arbitrary configuration scan', async t => {
  const { folder, root, game } = await environment(t)
  await fs.writeFile(path.join(root, 'manual.html'), '<h1>Parent Title</h1>')
  await fs.writeFile(path.join(folder, 'manual.html'), '<h1>Unselected Title</h1>')
  const selected = path.join(root, 'Selected'); await fs.mkdir(selected)
  await fs.writeFile(path.join(selected, 'Start.ini'), 'GameTitle=Selected Novel')
  await fs.writeFile(path.join(selected, 'private.ini'), 'GameTitle=Private Config Title')
  game.installations.push({ id: 'selected', folderPath: selected, executablePath: null, arguments: [] })
  game.preferredInstallationId = 'selected'
  assert.deepEqual((await readLocalTextEvidence(game)).titles, [{ value: 'Selected Novel', source: 'config-title' }])
})

test('linked manual directory outside the installation is not opened', async t => {
  const { folder, root, game } = await environment(t)
  const outside = path.join(root, 'Outside'); await fs.mkdir(outside)
  await fs.writeFile(path.join(outside, 'manual.html'), '<h1>Escaped Title</h1>')
  await fs.symlink(outside, path.join(folder, 'manual'), process.platform === 'win32' ? 'junction' : 'dir')
  const openSpy = t.mock.method(fs, 'open', () => { throw new Error('Linked files must not be opened') })
  assert.deepEqual((await readLocalTextEvidence(game)).titles, [])
  assert.equal(openSpy.mock.callCount(), 0)
})

test('oversized, malformed and excessively long title files are skipped with bounded reads', async t => {
  const { folder, game } = await environment(t)
  await fs.writeFile(path.join(folder, 'manual.html'), '<title>' + 'x'.repeat(129 * 1024) + '</title>')
  await fs.writeFile(path.join(folder, 'readme.html'), '<title>' + 'y'.repeat(600) + '</title><h1>')
  await fs.writeFile(path.join(folder, 'game.ini'), 'GameTitle=' + 'z'.repeat(600))
  await fs.writeFile(path.join(folder, 'start.ini'), Buffer.from([0, 0, 1, 2, 0, 3]))
  const opened: string[] = [], originalOpen = fs.open
  t.mock.method(fs, 'open', (...args: Parameters<typeof fs.open>) => { opened.push(String(args[0])); return originalOpen(...args) })
  const evidence = await readLocalTextEvidence(game)
  assert.deepEqual(evidence.titles, [])
  assert.equal(opened.some(file => path.basename(file) === 'manual.html'), false)
  assert.ok(evidence.warnings.some(warning => warning.includes('过大')))
})

test('a bounded UTF-8 prefix ending inside a multibyte character keeps its correct encoding', async t => {
  const { folder, game } = await environment(t)
  const head = Buffer.from('<title>夜明けの航路</title>')
  const filler = Buffer.alloc(32 * 1024 - head.length - 1, 32)
  await fs.writeFile(path.join(folder, 'manual.html'), Buffer.concat([head, filler, Buffer.from('天使の終わり')]))
  assert.deepEqual((await readLocalTextEvidence(game)).titles, [{ value: '夜明けの航路', source: 'manual-title' }])
})

test('the document count is bounded even when every permitted filename exists', async t => {
  const { folder, game } = await environment(t)
  for (const directory of ['', 'manual', 'help']) {
    await fs.mkdir(path.join(folder, directory), { recursive: true })
    for (const name of ['manual.html', 'manual.htm', 'readme.html', 'readme.htm']) await fs.writeFile(path.join(folder, directory, name), '<title>Moonlit Harbor</title>')
  }
  const originalOpen = fs.open
  const spy = t.mock.method(fs, 'open', (...args: Parameters<typeof fs.open>) => originalOpen(...args))
  const evidence = await readLocalTextEvidence(game)
  assert.equal(spy.mock.callCount(), 8)
  assert.deepEqual(evidence.titles, [{ value: 'Moonlit Harbor', source: 'manual-title' }])
  assert.ok(evidence.warnings.some(warning => warning.includes('部分标题')))
})
