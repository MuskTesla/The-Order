import assert from 'node:assert/strict'
import { promises as fs } from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import test from 'node:test'
import { collectLocalGameEvidence } from '../electron/local-game-evidence'
import type { LibraryGame } from '../src/shared/types'

const align = (length: number, multiple = 4) => Math.ceil(length / multiple) * multiple
function versionBlock(key: string, value: Buffer = Buffer.alloc(0), type = 1, children: Buffer[] = []): Buffer {
  const encodedKey = Buffer.from(`${key}\0`, 'utf16le')
  const valueStart = align(6 + encodedKey.length)
  let length = valueStart + value.length
  const offsets = children.map(child => { const offset = align(length); length = offset + child.length; return offset })
  const result = Buffer.alloc(length)
  result.writeUInt16LE(length, 0)
  result.writeUInt16LE(type === 1 ? value.length / 2 : value.length, 2)
  result.writeUInt16LE(type, 4)
  encodedKey.copy(result, 6)
  value.copy(result, valueStart)
  children.forEach((child, index) => child.copy(result, offsets[index]))
  return result
}
function version(fields: Record<string, string>): Buffer {
  const strings = Object.entries(fields).map(([key, value]) => versionBlock(key, Buffer.from(`${value}\0`, 'utf16le')))
  return versionBlock('VS_VERSION_INFO', Buffer.alloc(52), 0, [versionBlock('StringFileInfo', undefined, 1, [versionBlock('041104b0', undefined, 1, strings)])])
}
function pe(fields: Record<string, string>, bits: 32 | 64 = 32): Buffer {
  const payload = version(fields), rawSize = align(0x100 + payload.length, 0x200)
  const optionalSize = bits === 32 ? 0xe0 : 0xf0
  const bytes = Buffer.alloc(0x400 + rawSize)
  bytes.write('MZ', 0); bytes.writeUInt32LE(0x80, 0x3c)
  bytes.write('PE\0\0', 0x80)
  bytes.writeUInt16LE(bits === 32 ? 0x14c : 0x8664, 0x84)
  bytes.writeUInt16LE(1, 0x86); bytes.writeUInt16LE(optionalSize, 0x94)
  const optional = 0x98, directories = optional + (bits === 32 ? 96 : 112)
  bytes.writeUInt16LE(bits === 32 ? 0x10b : 0x20b, optional)
  bytes.writeUInt32LE(16, directories - 4)
  bytes.writeUInt32LE(0x1000, directories + 16); bytes.writeUInt32LE(0x100 + payload.length, directories + 20)
  const section = optional + optionalSize
  bytes.write('.rsrc', section)
  bytes.writeUInt32LE(0x100 + payload.length, section + 8)
  bytes.writeUInt32LE(0x1000, section + 12)
  bytes.writeUInt32LE(rawSize, section + 16); bytes.writeUInt32LE(0x400, section + 20)
  // Type RT_VERSION -> name 1 -> language 1041 -> IMAGE_RESOURCE_DATA_ENTRY.
  for (const offset of [0x400, 0x420, 0x440]) bytes.writeUInt16LE(1, offset + 14)
  bytes.writeUInt32LE(16, 0x410); bytes.writeUInt32LE(0x80000020, 0x414)
  bytes.writeUInt32LE(1, 0x430); bytes.writeUInt32LE(0x80000040, 0x434)
  bytes.writeUInt32LE(1041, 0x450); bytes.writeUInt32LE(0x60, 0x454)
  bytes.writeUInt32LE(0x1100, 0x460); bytes.writeUInt32LE(payload.length, 0x464)
  payload.copy(bytes, 0x500)
  return bytes
}
function peLanguages(payloads: Buffer[]): Buffer {
  assert.ok(payloads.length > 0 && payloads.length <= 4)
  let payloadEnd = 0x100
  const offsets = payloads.map(payload => { const offset = align(payloadEnd); payloadEnd = offset + payload.length; return offset })
  const rawSize = align(payloadEnd, 0x200)
  const bytes = Buffer.alloc(0x400 + rawSize)
  pe({}).copy(bytes, 0, 0, 0x500)
  bytes.fill(0, 0x450, 0x500)
  bytes.writeUInt16LE(payloads.length, 0x44e)
  bytes.writeUInt32LE(payloadEnd, 0x98 + 96 + 20)
  bytes.writeUInt32LE(payloadEnd, 0x178 + 8)
  bytes.writeUInt32LE(rawSize, 0x178 + 16)
  payloads.forEach((payload, index) => {
    bytes.writeUInt32LE(1041 + index, 0x450 + index * 8)
    bytes.writeUInt32LE(0x80 + index * 16, 0x454 + index * 8)
    bytes.writeUInt32LE(0x1000 + offsets[index]!, 0x480 + index * 16)
    bytes.writeUInt32LE(payload.length, 0x484 + index * 16)
    payload.copy(bytes, 0x400 + offsets[index]!)
  })
  return bytes
}
async function environment(t: { after(callback: () => Promise<void>): void }) {
  const temporary = path.resolve(os.tmpdir())
  const root = await fs.mkdtemp(path.join(temporary, 'kgm-evidence-test-'))
  t.after(async () => {
    assert.equal(path.dirname(path.resolve(root)), temporary)
    assert.ok(path.basename(root).startsWith('kgm-evidence-test-'))
    await fs.rm(root, { recursive: true, force: true })
  })
  const folder = path.join(root, 'Games'); await fs.mkdir(folder)
  const game: LibraryGame = {
    id: 'local-game', name: 'Local name', metadata: null,
    installations: [{ id: 'install', folderPath: folder, executablePath: null, arguments: [] }], preferredInstallationId: 'install',
    status: 'planned', personalRating: null, favorite: false, categories: [], customTags: [], notes: '',
    totalPlaySeconds: 0, playCount: 0, sessions: [], addedAt: '2026-10-02T00:00:00Z', lastPlayedAt: null
  }
  return { root, folder, game }
}

test('default and explicitly disabled modes never open or read executables while preserving filename and Steam evidence', async t => {
  const { folder, game } = await environment(t)
  const selected = path.join(folder, 'bin', 'SelectedNovel.exe')
  await fs.mkdir(path.dirname(selected))
  await fs.writeFile(selected, pe({ ProductName: 'Executable metadata must stay unread', CompanyName: 'Unread company' }))
  await fs.writeFile(path.join(folder, 'NovelOne.exe'), pe({ ProductName: 'Also unread' }))
  await fs.writeFile(path.join(folder, 'NovelOne.ain'), 'Archive contents must stay unread')
  await fs.writeFile(path.join(folder, 'steam_emu.ini'), '[Steam]\nAppId=12345\n')
  game.installations[0]!.executablePath = selected
  const originalOpen = fs.open
  const opened: string[] = []
  const openSpy = t.mock.method(fs, 'open', (...args: Parameters<typeof fs.open>) => {
    const file = String(args[0]); opened.push(file)
    assert.equal(/\.(?:exe|ain)$/i.test(file), false, 'Default mode must not open any executable or archive content')
    return originalOpen(...args)
  })
  const readSpy = t.mock.method(fs, 'readFile', () => { throw new Error('Default mode must not call readFile for executable evidence') })
  for (const setting of [undefined, {}, { readExecutableMetadata: false }]) {
    const evidence = await collectLocalGameEvidence(game, setting)
    assert.deepEqual(evidence.titles, [
      { value: 'SelectedNovel', source: 'executable-name' },
      { value: 'NovelOne', source: 'executable-name' },
      { value: 'NovelOne', source: 'archive-name' }
    ])
    assert.deepEqual(evidence.references, [{ value: 'steam:12345', source: 'steam-config' }])
    assert.deepEqual(evidence.companies, [])
    assert.deepEqual(evidence.executableFingerprints, [])
    assert.deepEqual(evidence.warnings, [])
  }
  assert.equal(openSpy.mock.callCount(), 3, 'Only the small Steam config is opened once per call')
  assert.ok(opened.every(file => path.basename(file) === 'steam_emu.ini'))
  assert.equal(readSpy.mock.callCount(), 0)
})

for (const bits of [32, 64] as const) test(`reads PE${bits} UTF-16 version fields using resource RVAs without executing the file`, async t => {
  const { folder, game } = await environment(t)
  const executable = path.join(folder, 'Game.exe')
  const binary = pe({ ProductName: '星空の航路', FileDescription: '天使☆騒々 RE-BOOT!', CompanyName: 'ゆずソフト', OriginalFilename: 'TenshiOriginal.exe' }, bits)
  await fs.writeFile(executable, binary)
  const evidence = await collectLocalGameEvidence(game, { readExecutableMetadata: true })
  assert.ok(evidence.titles.some(title => title.value === '星空の航路' && title.source === 'product-name'))
  assert.ok(evidence.titles.some(title => title.value === '天使☆騒々 RE-BOOT!' && title.source === 'file-description'))
  assert.ok(evidence.titles.some(title => title.value === 'TenshiOriginal' && title.source === 'executable-name'))
  assert.deepEqual(evidence.companies, ['ゆずソフト'])
  assert.match(evidence.executableFingerprints[0]!, /^pe-version-sha256:[0-9a-f]{64}$/)
  assert.deepEqual(evidence.warnings, [])
  assert.deepEqual(await fs.readFile(executable), binary)
})

test('specific descriptions survive generic engine products without claiming an informative fingerprint', async t => {
  const { folder, game } = await environment(t)
  await fs.writeFile(path.join(folder, 'Game.exe'), pe({ ProductName: 'SiglusEngine', FileDescription: 'as:9-nine- ARTEISIA', OriginalFilename: 'SiglusEngine.exe' }))
  const evidence = await collectLocalGameEvidence(game, { readExecutableMetadata: true })
  assert.deepEqual(evidence.titles, [{ value: 'as:9-nine- ARTEISIA', source: 'file-description' }])
  assert.deepEqual(evidence.executableFingerprints, [])
})

test('actual Kirikiri engine product and original-filename boilerplate are excluded while the game description remains', async t => {
  const { folder, game } = await environment(t)
  await fs.writeFile(path.join(folder, 'Game.exe'), pe({
    ProductName: 'TVP(KIRIKIRI) Z core / Scripting Platform for Win32', FileDescription: '天使☆騒々 RE-BOOT!', OriginalFilename: 'tvpwin32.exe'
  }))
  const evidence = await collectLocalGameEvidence(game, { readExecutableMetadata: true })
  assert.deepEqual(evidence.titles, [{ value: '天使☆騒々 RE-BOOT!', source: 'file-description' }])
  assert.deepEqual(evidence.executableFingerprints, [])
})

test('known vendor engines, utility descriptions and compiler resource filenames never become game identity', async t => {
  const { folder, game } = await environment(t)
  const file = path.join(folder, 'Game.exe')
  const generic = [
    "Siglus(VisualArt's ゲーム実行エンジン)", 'Ethornell - BURIKO General Interpreter', 'Siglus スタートアプリケーション',
    'Digital signature/file integrity check tool', 'ファイル破損チェックツール', 'エンジン設定', 'CMVS', 'BootStrap',
    'BGI', 'BHVC', 'AlphaROMdiE', 'SiglusEngine 一键解锁工具', 'CMVSCONFIG', 'cmvsConfig32',
    'cmvs32_CHS', 'BGI.chs', 'BGI_patch', 'BGI_CHS_patch', 'SiglusEngine_x64_CHS', 'BGI_patch_v1.2',
    'SiglusStart', 'QLIE', 'IMOSURUME', 'cs2', 'sigchk', 'KrPatcher'
  ]
  for (const value of generic) {
    await fs.writeFile(file, pe({ ProductName: value, FileDescription: value, OriginalFilename: 'WIN32.rc' }))
    const evidence = await collectLocalGameEvidence(game, { readExecutableMetadata: true })
    assert.deepEqual(evidence.titles, [], value)
    assert.deepEqual(evidence.executableFingerprints, [], value)
  }
})

test('engine metadata does not hide a specific game description and menu suffixes are stripped conservatively', async t => {
  const { folder, game } = await environment(t)
  const file = path.join(folder, 'SiglusEngine.exe')
  game.installations[0]!.executablePath = file
  await fs.writeFile(file, pe({ ProductName: "Siglus(VisualArt's ゲーム実行エンジン)", FileDescription: 'ましろ色シンフォニー SANA EDITION 開始メニュー', OriginalFilename: 'WIN32.rc' }))
  const evidence = await collectLocalGameEvidence(game, { readExecutableMetadata: true })
  assert.deepEqual(evidence.titles, [{ value: 'ましろ色シンフォニー SANA EDITION', source: 'file-description' }])
  assert.deepEqual(evidence.executableFingerprints, [])
  await fs.writeFile(file, pe({ ProductName: 'Ethornell - BURIKO General Interpreter', FileDescription: 'Ethereal Memories' }))
  assert.deepEqual((await collectLocalGameEvidence(game, { readExecutableMetadata: true })).titles, [{ value: 'Ethereal Memories', source: 'file-description' }])
  await fs.writeFile(file, pe({ ProductName: 'Specific mod launcher', FileDescription: '橘光柠水随想曲 中文模组启动器', OriginalFilename: 'KrPatcher.exe' }))
  const mod = await collectLocalGameEvidence(game, { readExecutableMetadata: true })
  assert.deepEqual(mod.titles, [])
  assert.deepEqual(mod.executableFingerprints, [])
})

test('helper executable names cannot contribute their otherwise specific-looking version metadata', async t => {
  const { folder, game } = await environment(t)
  for (const name of ['uninst_cn.exe', 'uninst.exe', 'delfile.exe', 'NovelCheck.exe', 'sigchk.exe', 'KrPatcher.exe', 'cmvsCheck32.exe', 'cmvsConfig32.exe', 'SiglusCounter.exe', 'bootmenu.exe', 'OpenSaveFolder.exe']) {
    await fs.writeFile(path.join(folder, name), pe({ ProductName: 'Misleading specific game title', FileDescription: 'Another misleading title' }))
  }
  await fs.writeFile(path.join(folder, 'Rance01.exe'), '')
  const evidence = await collectLocalGameEvidence(game, { readExecutableMetadata: true })
  assert.deepEqual(evidence.titles, [{ value: 'Rance01', source: 'executable-name' }])
  assert.deepEqual(evidence.executableFingerprints, [])
})

test('generic engine titles, standalone Game.exe and ordinary non-PE contents create no false identity', async t => {
  const { folder, game } = await environment(t)
  await fs.writeFile(path.join(folder, 'Game.exe'), 'ProductName=Secret invented game; this is not a PE')
  await fs.writeFile(path.join(folder, 'UnityPlayer.exe'), pe({ ProductName: 'Unity Player', FileDescription: 'Unity Player', OriginalFilename: 'game.exe' }))
  await fs.writeFile(path.join(folder, 'data.ain'), 'do not inspect these contents')
  const evidence = await collectLocalGameEvidence(game, { readExecutableMetadata: true })
  assert.deepEqual(evidence.titles, [])
  assert.deepEqual(evidence.executableFingerprints, [])
})

test('main executable and archive names remain available without a preferred executable', async t => {
  const { folder, game } = await environment(t)
  await fs.writeFile(path.join(folder, 'Rance01.exe'), '')
  await fs.writeFile(path.join(folder, 'Rance01.ain'), 'archive is never read')
  await fs.writeFile(path.join(folder, 'Rance01beifen.ain'), 'backup is never read')
  await fs.writeFile(path.join(folder, 'OpenSaveFolder.exe'), pe({ ProductName: 'Misleading helper' }))
  await fs.writeFile(path.join(folder, 'dracuriot_Steam_R18DLC.exe'), pe({ ProductName: 'Misleading DLC installer' }))
  const evidence = await collectLocalGameEvidence(game, { readExecutableMetadata: true })
  assert.deepEqual(evidence.titles, [{ value: 'Rance01', source: 'executable-name' }, { value: 'Rance01', source: 'archive-name' }])
  assert.deepEqual(evidence.executableFingerprints, [])
})

test('a selected program with a useful title prevents unrelated secondary PE titles from being read', async t => {
  const { folder, game } = await environment(t)
  for (let i = 0; i < 8; i++) await fs.writeFile(path.join(folder, `Novel${i}.exe`), pe({ ProductName: `Distinct title ${i}` }))
  const selected = path.join(folder, 'bin', 'SelectedNovel.exe')
  await fs.mkdir(path.dirname(selected)); await fs.writeFile(selected, pe({ ProductName: 'Selected nested title' }))
  game.installations[0]!.executablePath = selected
  const originalOpen = fs.open
  const opened: string[] = []
  t.mock.method(fs, 'open', (...args: Parameters<typeof fs.open>) => { opened.push(String(args[0])); return originalOpen(...args) })
  const evidence = await collectLocalGameEvidence(game, { readExecutableMetadata: true })
  assert.equal(evidence.titles.filter(title => title.source === 'product-name').length, 1)
  assert.ok(evidence.titles.some(title => title.value === 'Selected nested title'))
  assert.equal(evidence.executableFingerprints.length, 1)
  assert.deepEqual(opened, [selected])
})

test('a generic selected launcher falls back to at most four other programs and preserves conflicting titles for review', async t => {
  const { folder, game } = await environment(t)
  const selected = path.join(folder, 'Game.exe')
  await fs.writeFile(selected, pe({ ProductName: 'SiglusEngine', FileDescription: 'SiglusStart', CompanyName: 'Engine vendor' }))
  game.installations[0]!.executablePath = selected
  for (let i = 0; i < 8; i++) await fs.writeFile(path.join(folder, `Novel${i}.exe`), pe({ ProductName: `Distinct title ${i}` }))
  const evidence = await collectLocalGameEvidence(game, { readExecutableMetadata: true })
  assert.equal(evidence.titles.filter(title => title.source === 'product-name').length, 4)
  assert.ok(evidence.titles.some(title => title.value === 'Distinct title 0'))
  assert.ok(evidence.titles.some(title => title.value === 'Distinct title 1'))
  assert.equal(evidence.executableFingerprints.length, 4)
  assert.deepEqual(evidence.companies, [])
  game.installations[0]!.executablePath = null
  const unselected = await collectLocalGameEvidence(game, { readExecutableMetadata: true })
  assert.ok(unselected.titles.some(title => title.value === 'Distinct title 0'))
  assert.ok(unselected.titles.some(title => title.value === 'Distinct title 1'))
})

test('exact utility names and delimited crack suffixes are skipped without rejecting genuine Crack titles', async t => {
  const { folder, game } = await environment(t)
  for (const name of ['AlphaROMdiE.exe', 'patch.exe', 'Patcher.exe', 'Tenshi_sz_Crack.exe', 'Novel_Patcher.exe', 'SmartSteamLoader.exe']) {
    await fs.writeFile(path.join(folder, name), pe({ ProductName: 'Misleading utility title', CompanyName: 'Utility vendor' }))
  }
  await fs.writeFile(path.join(folder, 'Crack of Dawn.exe'), pe({ ProductName: 'Crack of Dawn' }))
  const originalOpen = fs.open
  const opened: string[] = []
  t.mock.method(fs, 'open', (...args: Parameters<typeof fs.open>) => { opened.push(path.basename(String(args[0]))); return originalOpen(...args) })
  const evidence = await collectLocalGameEvidence(game, { readExecutableMetadata: true })
  assert.deepEqual(opened, ['Crack of Dawn.exe'])
  assert.ok(evidence.titles.some(title => title.value === 'Crack of Dawn'))
  assert.ok(evidence.titles.every(title => !title.value.includes('Misleading')))
  assert.deepEqual(evidence.companies, [])
})

test('all language resources must parse before any program metadata is accepted', async t => {
  const { folder, game } = await environment(t)
  const malformed = version({ ProductName: 'Broken second language' })
  malformed.writeUInt16LE(0, 0)
  await fs.writeFile(path.join(folder, 'Game.exe'), peLanguages([
    version({ ProductName: 'Must not survive partial parsing', CompanyName: 'Must not survive either' }), malformed
  ]))
  const evidence = await collectLocalGameEvidence(game, { readExecutableMetadata: true })
  assert.deepEqual(evidence.titles, [])
  assert.deepEqual(evidence.companies, [])
  assert.deepEqual(evidence.executableFingerprints, [])
  assert.ok(evidence.warnings.length > 0)
})

test('a renamed helper cannot supply copied game descriptions from any of its version languages', async t => {
  const { folder, game } = await environment(t)
  const file = path.join(folder, 'CopiedNovel.exe')
  for (const originalName of ['setup.exe', 'uninstall.exe', 'Crack.exe', 'Novel_Crack.exe', 'KrPatcher.exe']) {
    await fs.writeFile(file, peLanguages([
      version({ ProductName: 'Copied game title', FileDescription: 'Copied official description', CompanyName: 'Copied developer' }),
      version({ ProductName: 'Copied game title', OriginalFilename: originalName })
    ]))
    const evidence = await collectLocalGameEvidence(game, { readExecutableMetadata: true })
    assert.deepEqual(evidence.titles, [], originalName)
    assert.deepEqual(evidence.companies, [], originalName)
    assert.deepEqual(evidence.executableFingerprints, [], originalName)
  }
})

test('repeated product and description names in multiple languages remain one program title and one fingerprint', async t => {
  const { folder, game } = await environment(t)
  await fs.writeFile(path.join(folder, 'Game.exe'), peLanguages([
    version({ ProductName: 'Distinct Novel', FileDescription: 'DISTINCT NOVEL', CompanyName: 'Novel Studio' }),
    version({ ProductName: 'Distinct Novel', FileDescription: 'Distinct Novel', CompanyName: 'Novel Studio' })
  ]))
  const evidence = await collectLocalGameEvidence(game, { readExecutableMetadata: true })
  assert.deepEqual(evidence.titles, [{ value: 'Distinct Novel', source: 'product-name' }])
  assert.deepEqual(evidence.companies, ['Novel Studio'])
  assert.equal(evidence.executableFingerprints.length, 1)
  assert.deepEqual(evidence.warnings, [])
})

test('PE files without a version resource or with engine-only fields contribute no program identity', async t => {
  const { folder, game } = await environment(t)
  const file = path.join(folder, 'Game.exe')
  const noVersion = pe({ ProductName: 'Hidden unused payload' })
  noVersion.writeUInt32LE(10, 0x410)
  const noDirectory = pe({ ProductName: 'Hidden unused payload' })
  noDirectory.writeUInt32LE(0, 0x98 + 96 + 16)
  for (const binary of [noVersion, noDirectory, pe({ ProductName: 'SiglusEngine', FileDescription: 'SiglusStart', CompanyName: 'Engine vendor' })]) {
    await fs.writeFile(file, binary)
    const evidence = await collectLocalGameEvidence(game, { readExecutableMetadata: true })
    assert.deepEqual(evidence.titles, [])
    assert.deepEqual(evidence.companies, [])
    assert.deepEqual(evidence.executableFingerprints, [])
    assert.deepEqual(evidence.warnings, [])
  }
})

test('filename-only archive evidence is limited to five specific known resource names', async t => {
  const { folder, game } = await environment(t)
  for (let i = 0; i < 9; i++) await fs.writeFile(path.join(folder, `Novel${i}.ypf`), '')
  await fs.writeFile(path.join(folder, 'PrivateNotes.txt'), 'not evidence')
  const evidence = await collectLocalGameEvidence(game, { readExecutableMetadata: true })
  assert.equal(evidence.titles.length, 5)
  assert.ok(evidence.titles.every(title => title.source === 'archive-name'))
})

test('version fingerprints ignore overlays but change when informative version metadata changes', async t => {
  const { folder, game } = await environment(t)
  const file = path.join(folder, 'Game.exe')
  const binary = pe({ ProductName: 'Distinct original title', FileDescription: 'Edition one' })
  await fs.writeFile(file, binary)
  const first = await collectLocalGameEvidence(game, { readExecutableMetadata: true })
  await fs.appendFile(file, Buffer.alloc(8 * 1024 * 1024, 0x7f))
  const overlay = await collectLocalGameEvidence(game, { readExecutableMetadata: true })
  assert.deepEqual(overlay.executableFingerprints, first.executableFingerprints)
  await fs.writeFile(file, pe({ ProductName: 'Distinct original title', FileDescription: 'Edition two' }))
  assert.notDeepEqual((await collectLocalGameEvidence(game, { readExecutableMetadata: true })).executableFingerprints, first.executableFingerprints)
})

test('only bounded headers and version resources are read, never an executable overlay or whole-file hash', async t => {
  const { folder, game } = await environment(t)
  const file = path.join(folder, 'Game.exe')
  const binary = pe({ ProductName: 'Bounded title' })
  await fs.writeFile(file, Buffer.concat([binary, Buffer.alloc(8 * 1024 * 1024, 0x7f)]))
  const originalOpen = fs.open
  const reads: Array<{ position: number; length: number }> = []
  t.mock.method(fs, 'open', async (...args: Parameters<typeof fs.open>) => {
    const handle = await originalOpen(...args)
    const originalRead = handle.read.bind(handle)
    t.mock.method(handle, 'read', (buffer: Buffer, offset: number, length: number, position: number) => {
      reads.push({ position, length })
      assert.ok(position >= 0 && position + length <= binary.length)
      return originalRead(buffer, offset, length, position)
    })
    return handle
  })
  t.mock.method(fs, 'readFile', () => { throw new Error('Whole-file reads are forbidden') })
  const evidence = await collectLocalGameEvidence(game, { readExecutableMetadata: true })
  assert.ok(evidence.titles.some(title => title.value === 'Bounded title'))
  assert.ok(reads.length > 1)
  assert.ok(reads.reduce((sum, read) => sum + read.length, 0) <= 1024 * 1024 + 64 * 1024)
  assert.equal(reads[0]!.position, 0)
  assert.equal(reads[0]!.length, 64)
})

test('an executable replaced between path validation and open is rejected before reading its contents', async t => {
  const { folder, game } = await environment(t)
  const file = path.join(folder, 'Game.exe')
  await fs.writeFile(file, pe({ ProductName: 'Original title' }))
  const originalOpen = fs.open
  let reads = 0
  let replaced = false
  t.mock.method(fs, 'open', async (...args: Parameters<typeof fs.open>) => {
    if (String(args[0]) === file && !replaced) {
      replaced = true
      await fs.rename(file, path.join(folder, 'Original.backup'))
      await fs.writeFile(file, pe({ ProductName: 'Replaced title must not be read' }))
    }
    const handle = await originalOpen(...args)
    t.mock.method(handle, 'read', () => { reads++; throw new Error('Replacement must not be read') })
    return handle
  })
  const evidence = await collectLocalGameEvidence(game, { readExecutableMetadata: true })
  assert.equal(replaced, true)
  assert.equal(reads, 0)
  assert.deepEqual(evidence.titles, [])
  assert.deepEqual(evidence.executableFingerprints, [])
  assert.ok(evidence.warnings.length > 0)
})

test('a nested directory changed into an outside junction during open cannot supply version metadata', async t => {
  const { root, folder, game } = await environment(t)
  const nested = path.join(folder, 'bin'), outside = path.join(root, 'Outside')
  await fs.mkdir(nested); await fs.mkdir(outside)
  const file = path.join(nested, 'Game.exe')
  await fs.writeFile(file, pe({ ProductName: 'Original title' }))
  await fs.writeFile(path.join(outside, 'Game.exe'), pe({ ProductName: 'Outside title must not be read' }))
  game.installations[0]!.executablePath = file
  const originalOpen = fs.open
  let reads = 0
  let replaced = false
  t.mock.method(fs, 'open', async (...args: Parameters<typeof fs.open>) => {
    if (String(args[0]) === file && !replaced) {
      replaced = true
      await fs.rename(nested, path.join(folder, 'old-bin'))
      await fs.symlink(outside, nested, process.platform === 'win32' ? 'junction' : 'dir')
    }
    const handle = await originalOpen(...args)
    t.mock.method(handle, 'read', () => { reads++; throw new Error('Outside contents must not be read') })
    return handle
  })
  const evidence = await collectLocalGameEvidence(game, { readExecutableMetadata: true })
  assert.equal(replaced, true)
  assert.equal(reads, 0)
  assert.deepEqual(evidence.titles, [])
  assert.deepEqual(evidence.executableFingerprints, [])
  assert.ok(evidence.warnings.length > 0)
})

test('an executable changed while its version resource is being read contributes no partial metadata', async t => {
  const { folder, game } = await environment(t)
  const file = path.join(folder, 'Game.exe')
  await fs.writeFile(file, pe({ ProductName: 'Changed title must not survive', CompanyName: 'Changed vendor' }))
  const originalOpen = fs.open
  let changed = false
  t.mock.method(fs, 'open', async (...args: Parameters<typeof fs.open>) => {
    const handle = await originalOpen(...args)
    const originalRead = handle.read.bind(handle)
    t.mock.method(handle, 'read', async (buffer: Buffer, offset: number, length: number, position: number) => {
      const result = await originalRead(buffer, offset, length, position)
      if (!changed && position >= 0x500) { changed = true; await fs.appendFile(file, Buffer.from([1])) }
      return result
    })
    return handle
  })
  const evidence = await collectLocalGameEvidence(game, { readExecutableMetadata: true })
  assert.equal(changed, true)
  assert.deepEqual(evidence.titles, [])
  assert.deepEqual(evidence.companies, [])
  assert.deepEqual(evidence.executableFingerprints, [])
  assert.ok(evidence.warnings.length > 0)
})

test('rejects invalid PE pointers, sections, resource cycles, oversized data and malformed version blocks', async t => {
  const { folder, game } = await environment(t)
  const file = path.join(folder, 'Game.exe')
  const corruptions = [
    (bytes: Buffer) => bytes.writeUInt32LE(0xfffffff0, 0x3c),
    (bytes: Buffer) => bytes.writeUInt16LE(97, 0x86),
    (bytes: Buffer) => bytes.writeUInt16LE(65535, 0x94),
    (bytes: Buffer) => bytes.writeUInt32LE(0xfffffff0, 0x178 + 20),
    (bytes: Buffer) => bytes.writeUInt16LE(65535, 0x40e),
    (bytes: Buffer) => bytes.writeUInt32LE(0x80000000, 0x414),
    (bytes: Buffer) => bytes.writeUInt32LE(0x80000020, 0x434),
    (bytes: Buffer) => bytes.writeUInt32LE(0x80000040, 0x454),
    (bytes: Buffer) => bytes.writeUInt32LE(0xffffffff, 0x460),
    (bytes: Buffer) => bytes.writeUInt32LE(1024 * 1024 + 1, 0x464),
    (bytes: Buffer) => bytes.writeUInt16LE(65535, 0x500),
    (bytes: Buffer) => bytes.writeUInt16LE(0, 0x500),
    (bytes: Buffer) => bytes.writeUInt16LE(65535, bytes.indexOf(Buffer.from('ProductName\0', 'utf16le')) - 4)
  ]
  for (const corrupt of corruptions) {
    const bytes = pe({ ProductName: 'Must never be trusted' }); corrupt(bytes)
    await fs.writeFile(file, bytes)
    const evidence = await collectLocalGameEvidence(game, { readExecutableMetadata: true })
    assert.deepEqual(evidence.titles, [])
    assert.deepEqual(evidence.executableFingerprints, [])
    assert.ok(evidence.warnings.length > 0)
  }
  await fs.writeFile(file, Buffer.from('MZ'))
  assert.ok((await collectLocalGameEvidence(game, { readExecutableMetadata: true })).warnings.length > 0)
})

test('reads exact Steam AppId fields, deduplicates IDs and reports conflicts without exposing configuration text', async t => {
  const { folder, game } = await environment(t)
  const secret = 'NeverExposePrivateConfiguration'
  await fs.writeFile(path.join(folder, 'steam_emu.ini'), `[Settings]\nUserName=${secret}\nAppId=4012810\nFakeAppId=999\n;AppId=888\nAppIdSuffix=777\nAppId=0\nAppId=-5\n`)
  await fs.writeFile(path.join(folder, 'SteamConfig.ini'), '[Game]\nappid = 4012810 ; valid comment\n')
  await fs.writeFile(path.join(folder, 'steam_api.ini'), Buffer.concat([Buffer.from([0xff, 0xfe]), Buffer.from('[Game]\r\nAppId=12345\r\n', 'utf16le')]))
  const evidence = await collectLocalGameEvidence(game)
  assert.deepEqual(new Set(evidence.references.map(ref => ref.value)), new Set(['steam:4012810', 'steam:12345']))
  assert.ok(evidence.references.every(ref => ref.source === 'steam-config'))
  assert.ok(evidence.warnings.some(warning => warning.includes('多个不同')))
  assert.equal(JSON.stringify(evidence).includes(secret), false)
})

test('known nested Steam configs and appid text are contained, bounded and never interpreted as code', async t => {
  const { folder, game } = await environment(t)
  await fs.mkdir(path.join(folder, 'steam_settings'))
  await fs.writeFile(path.join(folder, 'steam_settings', 'steam_appid.txt'), '4012810\r\n')
  await fs.writeFile(path.join(folder, 'steam_settings', 'configs.app.ini'), '[app]\nappid=4012810\n')
  await fs.writeFile(path.join(folder, 'steam_api.ini'), `AppId=777\n${'x'.repeat(128 * 1024)}`)
  await fs.writeFile(path.join(folder, 'steam.ini'), 'AppId=$(999)\nAppId=123 & execute\n')
  await fs.writeFile(path.join(folder, 'unrelated.ini'), 'AppId=999')
  const evidence = await collectLocalGameEvidence(game)
  assert.deepEqual(evidence.references, [{ value: 'steam:4012810', source: 'steam-config' }])
  assert.ok(evidence.warnings.some(warning => warning.includes('过大')))
})

test('selected executables and nested config junctions cannot escape the game directory', async t => {
  const { root, folder, game } = await environment(t)
  const outside = path.join(root, 'Outside'); await fs.mkdir(outside)
  const executable = path.join(outside, 'OtherNovel.exe')
  await fs.writeFile(executable, pe({ ProductName: 'Outside private title' }))
  await fs.writeFile(path.join(outside, 'steam_appid.txt'), '99999')
  game.installations[0]!.executablePath = executable
  try { await fs.symlink(outside, path.join(folder, 'steam_settings'), process.platform === 'win32' ? 'junction' : 'dir') }
  catch (error) { if (!['EPERM', 'EACCES'].includes((error as NodeJS.ErrnoException).code ?? '')) throw error }
  const evidence = await collectLocalGameEvidence(game, { readExecutableMetadata: true })
  assert.deepEqual(evidence.titles, [])
  assert.deepEqual(evidence.references, [])
  assert.deepEqual(evidence.executableFingerprints, [])
  assert.ok(evidence.warnings.some(warning => warning.includes('启动程序')))
})

test('missing folders or installations return a controlled empty result', async t => {
  const { game } = await environment(t)
  game.installations[0]!.folderPath += '-missing'
  const missing = await collectLocalGameEvidence(game)
  assert.deepEqual(missing.titles, [])
  assert.ok(missing.warnings.length > 0)
  game.installations = []
  assert.deepEqual(await collectLocalGameEvidence(game), { titles: [], companies: [], references: [], executableFingerprints: [], warnings: [] })
})
