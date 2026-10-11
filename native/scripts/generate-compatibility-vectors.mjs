// Reference generation only: real Electron library validation, fictional data, no profile input.
import fs from 'node:fs/promises'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { createHash } from 'node:crypto'
import { createRequire } from 'node:module'
import { spawnSync } from 'node:child_process'
import vm from 'node:vm'
import { buildSync, transformSync } from '../../node_modules/esbuild/lib/main.js'

if (process.platform !== 'win32') throw Error('Generate Windows reference vectors on Windows.')
const native = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const repository = path.dirname(native)
if (!process.versions.electron) {
  // Use the installed, locked Electron engine as the oracle without starting Chromium/UI
  // or opening a profile. This is reference generation, never the production read path.
  const env = { ...process.env, ELECTRON_RUN_AS_NODE: '1' }; delete env.NODE_OPTIONS
  const child = spawnSync(path.join(repository, 'node_modules/electron/dist/electron.exe'), [fileURLToPath(import.meta.url)], { env, stdio: 'inherit', windowsHide: true })
  if (child.error) throw child.error
  process.exit(child.status ?? 1)
}
const expectedElectron = JSON.parse(await fs.readFile(path.join(repository, 'package.json'), 'utf8')).devDependencies.electron
if (process.versions.electron !== expectedElectron) throw Error('Install the locked Electron reference before regenerating.')
const source = (await fs.readFile(path.join(repository, 'electron/library.ts'), 'utf8')).replace(/\r\n/g, '\n')
const compiled = buildSync({ entryPoints: [path.join(repository, 'electron/library.ts')], bundle: true, write: false, platform: 'node', format: 'cjs' }).outputFiles[0].text
const referenceModule = { exports: {} }
vm.runInNewContext(compiled, { exports: referenceModule.exports, module: referenceModule, require: createRequire(import.meta.url), process, Buffer, URL, console })
const validate = referenceModule.exports.validateLibraryBackup
if (typeof validate !== 'function') throw Error('Missing real legacy validation entry point.')
const keyStart = source.indexOf('function pathKey('), keyEnd = source.indexOf('\nfunction within(', keyStart)
if (keyStart < 0 || keyEnd < 0) throw Error('Inspect moved legacy pathKey before regenerating.')
const context = vm.createContext({ path, process })
vm.runInContext(transformSync(source.slice(keyStart, keyEnd) + '\nglobalThis.key = pathKey', { loader: 'ts' }).code, context)
const normal = JSON.parse(await fs.readFile(path.join(native, 'fixtures/normal/library.json'), 'utf8'))
const accepted = json => { try { validate(JSON.parse(json)); return true } catch { return false } }
const strings = [
  ['lone-high', '"\\ud800 lone surrogate"'], ['lone-low', '"\\udc00 lone surrogate"'],
  ['paired', '"\\ud83d\\ude00"'], ['mixed-surrogates', '"中\\ud800文😀\\udc00終"'],
  ['cjk', '"中文・日本語"'], ['emoji', '"😀🎮"'],
  ['mixed-escapes', '"直書き\\u4e2d\\n\\t\\\"\\\\\\/é\\ud800"']
].map(([label, token]) => {
  const doc = structuredClone(normal), game = doc.games[0], placeholder = 'LEGACY_STRING_TOKEN'
  game.name = game.notes = game.metadata.title = placeholder
  game.categories = [placeholder]; game.installations[0].arguments = [placeholder]
  const json = JSON.stringify(doc).replaceAll(JSON.stringify(placeholder), token)
  const value = JSON.parse(token)
  if (!accepted(json)) throw Error(`Legacy rejected string vector ${label}`)
  return { label, json, utf16: Array.from({ length: value.length }, (_, i) => value.charCodeAt(i)) }
})
const pairs = [
  ['trailing', 'C:\\Games\\A', 'C:\\Games\\A\\'], ['dot', 'C:\\Games\\A', 'C:\\Games\\A\\.'],
  ['parent', 'C:\\Games\\A', 'C:\\Games\\A\\Sub\\..'], ['slash', 'C:/Games/A', 'C:\\Games\\A'],
  ['case', 'c:\\games\\a', 'C:\\GAMES\\A'], ['repeated', 'C:\\Games\\A', 'C:\\\\Games\\\\A'],
  ['root-clamp', 'C:\\A', 'C:\\..\\..\\A'], ['drive-root', 'C:\\', 'c:\\Games\\..'],
  ['root-relative', '\\Games\\A', '/Games/A/'], ['root', '\\', '/'],
  ['unc-root', '\\\\server\\share', '//SERVER/share/'],
  ['unc-parent', '\\\\server\\share\\A', '\\\\server\\share\\A\\B\\..'],
  ['unc-clamp', '\\\\server\\share\\A', '\\\\server\\share\\..\\A'],
  ['unc-repeated', '\\\\server\\share\\A', '\\\\server\\\\share\\\\A'],
  ['whitespace', 'C:\\Games\\A', ' \ufeffC:\\Games\\A\\ \t'],
  ['different-name', 'C:\\Games\\A', 'C:\\Games\\AB'], ['different-drive', 'C:\\Games\\A', 'D:\\Games\\A'],
  ['different-share', '\\\\server\\share\\A', '\\\\server\\other\\A'],
  ['child-is-distinct', 'C:\\Games\\A', 'C:\\Games\\A\\Child'],
  ['trailing-dot-is-distinct', 'C:\\Games\\A', 'C:\\Games\\A.'],
  ['relative-rejected', 'C:\\Games\\A', 'Games\\A'], ['drive-relative-rejected', 'C:\\Games\\A', 'C:Games\\A'],
  ['three-leading-slashes', '\\\\server\\share\\A', '\\\\\\server\\share\\A'],
  ['unicode-dotted-i', 'C:\\Games\\İ', 'C:\\Games\\i\u0307'],
  ['unicode-final-sigma', 'C:\\Games\\ΟΣ', 'C:\\Games\\ος'],
  ['unicode-nonfinal-sigma', 'C:\\Games\\ΣΑ', 'C:\\Games\\σα'],
  ['unicode-ignorable-sigma', 'C:\\Games\\AΣ\u0301', 'C:\\Games\\aς\u0301'],
  ['unicode-distinct-sigma', 'C:\\Games\\ΟΣ', 'C:\\Games\\οσ'],
  ['unicode-cjk-emoji', 'C:\\Games\\中文🎮A', 'c:/games/中文🎮a/']
]
const paths = pairs.map(([label, left, right]) => {
  const doc = structuredClone(normal)
  doc.games[0].installations = [left, right].map((folderPath, i) => ({ id: `path-${i}`, folderPath, executablePath: null, arguments: [] }))
  doc.games[0].preferredInstallationId = null
  const keys = [left, right].map(p => path.isAbsolute(p.trim()) ? context.key(path.normalize(p.trim())) : null)
  const json = JSON.stringify(doc)
  return { label, left, right, keys, json, accepted: accepted(json) }
})
const numbers = [
  ['integer', '1', '4', '3', '9007199254740991'], ['decimal', '1.0', '4.0', '3.0', '9007199254740991.0'],
  ['exponent', '1e0', '4e0', '3e0', '9.007199254740991e15'],
  ['fraction-rejected', '1', '4.5', '3', '1'], ['numeric-string-rejected', '"1"', '4', '3', '1'],
  ['overflow-rejected', '1', '4', '3', '9007199254740992'], ['negative-rejected', '1', '-4', '3', '1']
].map(([label, version, width, height, votes]) => {
  const doc = structuredClone(normal)
  doc.games[0].metadata.covers = [{ id: '1', url: 'https://example.invalid/cover.jpg', width: 'NUMBER_WIDTH', height: 'NUMBER_HEIGHT', votes: 'NUMBER_VOTES', sexual: null }]
  doc.version = 'NUMBER_VERSION'; doc.games[1].localCover.width = 'NUMBER_WIDTH'; doc.games[1].localCover.height = 'NUMBER_HEIGHT'
  const json = JSON.stringify(doc).replaceAll('"NUMBER_VERSION"', version).replaceAll('"NUMBER_WIDTH"', width).replaceAll('"NUMBER_HEIGHT"', height).replaceAll('"NUMBER_VOTES"', votes)
  return { label, json, accepted: accepted(json) }
})
const result = { format: 'the-order-legacy-reference', version: 1, sourceSha256: createHash('sha256').update(source).digest('hex'), electron: process.versions.electron, node: process.versions.node, contextRoot: path.parse(process.cwd()).root, strings, paths, numbers }
if (numbers.filter(v => v.accepted).length !== 3 || !paths.some(v => v.accepted) || !paths.some(v => !v.accepted)) throw Error('Reference must contain the expected positive and negative controls.')
// Freeze the reference engine's locale-independent Unicode behavior instead of depending
// on the candidate machine's NLS/ICU version. Contextual final sigma is handled in C#.
const lower = {}, cased = [], ignorable = []
function addRange(ranges, cp) {
  const last = ranges.at(-1)
  if (last && last[1] === cp - 1) last[1] = cp
  else ranges.push([cp, cp])
}
for (let cp = 0; cp <= 0x10ffff; cp++) {
  if (cp >= 0xd800 && cp <= 0xdfff) continue
  const value = String.fromCodePoint(cp), mapped = value.toLowerCase()
  if (mapped !== value) lower[cp] = mapped
  if (/\p{Cased}/u.test(value)) addRange(cased, cp)
  if (/\p{Case_Ignorable}/u.test(value)) addRange(ignorable, cp)
}
const caseData = JSON.stringify({ unicode: process.versions.unicode, electron: process.versions.electron, node: process.versions.node, lower, cased, ignorable }) + '\n'
result.caseDataSha256 = createHash('sha256').update(caseData).digest('hex')
await fs.writeFile(path.join(native, 'TheOrder.Infrastructure/Legacy/LegacyUnicodeData.json'), caseData)
await fs.writeFile(path.join(native, 'fixtures/compatibility-vectors.json'), JSON.stringify(result, null, 2) + '\n')
console.log(JSON.stringify({ strings: strings.length, paths: paths.length, numbers: numbers.length, reference: 'electron/library.ts validateLibraryBackup + pathKey' }))
