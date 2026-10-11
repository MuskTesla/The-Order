import { createHash } from 'node:crypto'
import { promises as fs, type BigIntStats } from 'node:fs'
import type { FileHandle } from 'node:fs/promises'
import path from 'node:path'
import type { LibraryGame } from '../src/shared/types'

export interface LocalGameEvidence {
  titles: Array<{ value: string; source: 'product-name' | 'file-description' | 'executable-name' | 'archive-name' }>
  companies: string[]
  references: Array<{ value: string; source: 'steam-config' }>
  /** Hash of bounded version metadata plus executable basename, never a whole-file identity. */
  executableFingerprints: string[]
  warnings: string[]
}

const VERSION_LIMIT = 1024 * 1024
const DIRECTORY_LIMIT = 512
const CONFIG_LIMIT = 128 * 1024
const CONFIG_NAMES = new Set(['steam_emu.ini', 'steam_api.ini', 'steam_api64.ini', 'steamconfig.ini', 'steam.ini', 'smartsteamemu.ini', 'coldclientloader.ini', 'steam_appid.txt'])
const ARCHIVES = new Set(['.ain', '.pfs', '.ypf'])
const HELPER = /^(?:unins\d*|uninst|uninstall(?:er)?|delfile|setup|install(?:er)?|vcredist.*|vc_redist.*|dxsetup|directx.*|redist.*|unitycrashhandler.*|crashreport.*|crashpad.*|updater?|configure|configuration|config|cmvsconfig(?:32|64)?|opensavefolder|openconfigfolder|sigchk|krpatcher|sigluscounter|bootmenu|(?:r18)?dlc|steamclient.*|steam_api.*)(?:[._ -].*)?\.exe$|(?:^|[._ -])(?:r18dlc(?:installer)?|dlcinstaller|patchinstaller|uninstaller|setup|installer)(?:[._ -].*)?\.exe$|check(?:32|64)?\.exe$/i
const OTHER_HELPER = /^(?:alpharomdie|smartsteamloader|steamloader|reipatcher)(?:[._ -].*)?\.exe$|^(?:patch(?:er)?|crack(?:er)?|unlocker|keygen)(?:[._ -](?:v?\d+(?:[._-]\d+)*|x64|x86|chs|cht|cn|en|jp))*\.exe$|[._ -](?:crack|patcher|unlocker|keygen)(?:[._ -](?:v?\d+(?:[._-]\d+)*|x64|x86|chs|cht|cn|en|jp))*\.exe$/i
const isHelper = (value: string) => HELPER.test(value) || OTHER_HELPER.test(value)
const compact = (value: string) => value.normalize('NFKC').toLowerCase().replace(/[^\p{L}\p{N}]/gu, '')
const GENERIC = /^(?:game(?:exe|application|app)?|application|app|launcher|launch|start|main|engine|bootstrap|cmvs(?:config)?|bhvc|cs2|qlie|imosurume|sigchk|krpatcher|unknown|productname|filedescription|mygame|samplegame|testgame|data\d*|script\d*|resource\d*|system\d*|patch\d*|scenario\d*|movie\d*|sound\d*|voice\d*|image\d*|bgm\d*|unity(?:player|game)?|unreal(?:engine)?|kirikiri(?:z|2)?|krkr(?:z|2)?|tvp|吉里吉里(?:z|2)?|bgi|buriko(?:generalinterpreter)?|siglus(?:engine|start)?|reallive(?:engine|launcher)?|advhd|adv(?:player|engine)?|artemis(?:engine)?|ethornell|renpy(?:game)?|nscripter|onscripter|rpgmaker(?:mv|mz|vx|vxa|xp)?|wolfrpgeditor|tyranobuilder)(?:\d+)?$/u
const ENGINE_DESCRIPTION = /^(?:tvpkirikiri|tvpwin(?:32|64)|kirikiri(?:z|2)?core|吉里吉里(?:z|2)?(?:core|コア)|siglus(?:engine|visualarts|スタートアプリケーション|ゲーム実行エンジン)|(?:ethornell)?burikogeneralinterpreter|unityplayer|renpyruntime)/u
const UTILITY_DESCRIPTION = /^(?:digitalsignature(?:fileintegrity)?checktool|fileintegritychecktool|ファイル(?:破損|整合性)チェック(?:ツール)?|エンジン設定|起動設定|環境設定|alpharomdie)|(?:siglus|alpharom).*(?:一键解锁|解锁工具|破解器)/u
const MENU_SUFFIX = /(?:\s|[_\-–—])+[（(]?(?:開始メニュー|起動メニュー|スタートメニュー|(?:中文)?模组启动器|start\s*menu|launcher)[）)]?$/i
function useful(value: string): boolean {
  const normalized = compact(value)
  const withoutEngineVariant = normalized.replace(/(?:v\d+)$/u, '').replace(/(?:(?:chs|cht|cn|jp|patch|crack|x64|x86|32bit|64bit))+$/u, '')
  return normalized.length >= 3 && /\p{L}/u.test(normalized) && !GENERIC.test(normalized) && !GENERIC.test(withoutEngineVariant) && !ENGINE_DESCRIPTION.test(normalized) && !UTILITY_DESCRIPTION.test(normalized) && !/^(?:microsoft|visualstudio|windowsapplication|todo)/u.test(normalized)
}
function clean(value: string): string {
  return value.split('\0')[0]!.replace(/[\u0001-\u001f\u007f]/g, ' ').replace(/\s+/g, ' ').trim().slice(0, 512)
}
function stem(value: string): string { return path.win32.basename(value.replace(/\//g, '\\')).replace(/\.(?:exe|ain|pfs|ypf)$/i, '') }
function within(root: string, candidate: string): boolean {
  const relative = path.relative(root, candidate)
  return relative === '' || relative !== '..' && !relative.startsWith(`..${path.sep}`) && !path.isAbsolute(relative)
}
function validRange(offset: number, length: number, size: number): boolean {
  return Number.isSafeInteger(offset) && Number.isSafeInteger(length) && offset >= 0 && length >= 0 && offset <= size && length <= size - offset
}
const invalid = () => new Error('Invalid bounded PE metadata')

class BoundedReader {
  private readBytes = 0
  constructor(private readonly file: FileHandle, readonly size: number) {}
  async read(offset: number, length: number): Promise<Buffer> {
    if (!validRange(offset, length, this.size) || length > VERSION_LIMIT || this.readBytes + length > VERSION_LIMIT + 64 * 1024) throw invalid()
    this.readBytes += length
    const buffer = Buffer.alloc(length)
    let count = 0
    while (count < length) {
      const { bytesRead } = await this.file.read(buffer, count, length - count, offset + count)
      if (!bytesRead) throw invalid()
      count += bytesRead
    }
    return buffer
  }
}

interface VersionFields { productNames: string[]; descriptions: string[]; companies: string[]; originalNames: string[] }
function parseVersion(bytes: Buffer): VersionFields {
  const output: VersionFields = { productNames: [], descriptions: [], companies: [], originalNames: [] }
  const fields: Record<string, string[]> = { ProductName: output.productNames, FileDescription: output.descriptions, CompanyName: output.companies, OriginalFilename: output.originalNames }
  const align = (value: number) => Math.ceil(value / 4) * 4
  const decoder = new TextDecoder('utf-16le', { fatal: true })
  let blocks = 0
  function block(offset: number, end: number, parents: string[]): number {
    if (++blocks > 1024 || parents.length > 5 || !validRange(offset, 6, end)) throw invalid()
    const length = bytes.readUInt16LE(offset), valueLength = bytes.readUInt16LE(offset + 2), type = bytes.readUInt16LE(offset + 4)
    if (length < 8 || !validRange(offset, length, end) || type > 1) throw invalid()
    const blockEnd = offset + length
    let cursor = offset + 6
    while (cursor + 2 <= blockEnd && cursor - offset < 520 && bytes.readUInt16LE(cursor) !== 0) cursor += 2
    if (cursor + 2 > blockEnd || bytes.readUInt16LE(cursor) !== 0) throw invalid()
    const key = decoder.decode(bytes.subarray(offset + 6, cursor))
    if (!parents.length && key !== 'VS_VERSION_INFO') throw invalid()
    cursor = align(cursor + 2)
    const valueBytes = valueLength * (type === 1 ? 2 : 1)
    if (valueBytes && !validRange(cursor, valueBytes, blockEnd)) throw invalid()
    if (parents.length === 3 && parents[0] === 'VS_VERSION_INFO' && parents[1] === 'StringFileInfo' && /^[0-9a-f]{8}$/i.test(parents[2]!) && fields[key] && type === 1) {
      const value = clean(decoder.decode(bytes.subarray(cursor, cursor + valueBytes)))
      if (value) fields[key]!.push(value)
    }
    cursor = align(cursor + valueBytes)
    while (cursor + 6 <= blockEnd) {
      if (bytes.readUInt16LE(cursor) === 0) {
        if (bytes.subarray(cursor, blockEnd).some(value => value !== 0)) throw invalid()
        break
      }
      cursor = align(block(cursor, blockEnd, [...parents, key]))
    }
    return blockEnd
  }
  block(0, bytes.length, [])
  return output
}

async function versionResources(file: FileHandle): Promise<Buffer[]> {
  const stat = await file.stat()
  if (!stat.isFile() || stat.size < 2) return []
  const reader = new BoundedReader(file, stat.size)
  const dos = await reader.read(0, Math.min(64, stat.size))
  if (dos[0] !== 0x4d || dos[1] !== 0x5a) return []
  if (dos.length < 64) throw invalid()
  const peOffset = dos.readUInt32LE(0x3c)
  if (peOffset < 64 || peOffset > 64 * 1024) throw invalid()
  const header = await reader.read(peOffset, 24)
  if (header.readUInt32LE(0) !== 0x00004550) throw invalid()
  const sectionCount = header.readUInt16LE(6), optionalSize = header.readUInt16LE(20)
  if (!sectionCount || sectionCount > 96 || optionalSize < 112 || optionalSize > 4096) throw invalid()
  const optional = await reader.read(peOffset + 24, optionalSize)
  const magic = optional.readUInt16LE(0)
  const directories = magic === 0x10b ? 96 : magic === 0x20b ? 112 : -1
  if (directories < 0 || optionalSize < directories + 24) throw invalid()
  if (optional.readUInt32LE(directories - 4) < 3) return []
  const resourceRva = optional.readUInt32LE(directories + 16), resourceSize = optional.readUInt32LE(directories + 20)
  if (!resourceRva || !resourceSize) return []
  if (resourceSize < 16 || resourceRva + resourceSize > 0x100000000) throw invalid()
  const table = await reader.read(peOffset + 24 + optionalSize, sectionCount * 40)
  const sections = Array.from({ length: sectionCount }, (_, index) => ({
    virtualAddress: table.readUInt32LE(index * 40 + 12), rawSize: table.readUInt32LE(index * 40 + 16), rawOffset: table.readUInt32LE(index * 40 + 20)
  }))
  function fileOffset(rva: number, length: number): number {
    if (!validRange(rva, length, 0x100000000)) throw invalid()
    const matching = sections.filter(section => rva >= section.virtualAddress && validRange(rva - section.virtualAddress, length, section.rawSize))
    if (matching.length !== 1) throw invalid()
    const section = matching[0]!
    const offset = section.rawOffset + rva - section.virtualAddress
    if (!validRange(offset, length, reader.size)) throw invalid()
    return offset
  }
  const readRelative = async (offset: number, size: number) => {
    if (!validRange(offset, size, resourceSize)) throw invalid()
    return reader.read(fileOffset(resourceRva + offset, size), size)
  }
  const visited = new Set<number>()
  async function directory(offset: number) {
    if (visited.has(offset) || visited.size >= 32) throw invalid()
    visited.add(offset)
    const directoryHeader = await readRelative(offset, 16)
    const count = directoryHeader.readUInt16LE(12) + directoryHeader.readUInt16LE(14)
    if (count > 256) throw invalid()
    const entries = await readRelative(offset + 16, count * 8)
    return Array.from({ length: count }, (_, index) => ({ name: entries.readUInt32LE(index * 8), target: entries.readUInt32LE(index * 8 + 4) }))
  }
  const root = await directory(0)
  const version = root.find(entry => entry.name === 16)
  if (!version) return []
  if (!(version.target & 0x80000000)) throw invalid()
  const names = await directory(version.target & 0x7fffffff)
  const resources: Buffer[] = []
  let resourceBytes = 0
  for (const name of names.slice(0, 4)) {
    if (!(name.target & 0x80000000)) throw invalid()
    const languages = await directory(name.target & 0x7fffffff)
    for (const language of languages.slice(0, 4)) {
      if (language.target & 0x80000000) throw invalid()
      const data = await readRelative(language.target, 16)
      const rva = data.readUInt32LE(0), size = data.readUInt32LE(4)
      if (size < 8 || size > VERSION_LIMIT || resourceBytes + size > VERSION_LIMIT) throw invalid()
      resourceBytes += size
      resources.push(await reader.read(fileOffset(rva, size), size))
    }
  }
  return resources
}

async function safeFile(root: string, candidate: string): Promise<string | null> {
  try {
    if ((await fs.lstat(candidate)).isSymbolicLink()) return null
    const actual = await fs.realpath(candidate)
    if (!within(root, actual) || !(await fs.stat(actual)).isFile()) return null
    return actual
  } catch { return null }
}
function sameFile(left: BigIntStats, right: BigIntStats): boolean {
  return left.isFile() && right.isFile() && left.dev === right.dev && left.ino === right.ino &&
    left.size === right.size && left.mtimeNs === right.mtimeNs && left.ctimeNs === right.ctimeNs
}
async function openContainedFile(root: string, candidate: string): Promise<{ handle: FileHandle; verify: () => Promise<void> }> {
  const actual = await safeFile(root, candidate)
  if (!actual) throw invalid()
  const expected = await fs.stat(actual, { bigint: true })
  const handle = await fs.open(actual, 'r')
  const verify = async () => {
    // Check the opened object, not just the pathname inspected before open().
    // Rechecking containment also detects replaced files and parent junctions.
    const current = await safeFile(root, actual)
    if (current !== actual || !sameFile(expected, await handle.stat({ bigint: true })) ||
      !sameFile(expected, await fs.stat(actual, { bigint: true }))) throw invalid()
  }
  try { await verify(); return { handle, verify } }
  catch (error) { await handle.close(); throw error }
}
async function configIds(root: string, file: string, plain: boolean): Promise<string[]> {
  const { handle, verify } = await openContainedFile(root, file)
  try {
    const size = (await handle.stat()).size
    if (size > CONFIG_LIMIT) throw new Error('Config exceeds limit')
    const bytes = await new BoundedReader(handle, size).read(0, size)
    await verify()
    let value: string
    if (bytes[0] === 0xff && bytes[1] === 0xfe) value = new TextDecoder('utf-16le', { fatal: true }).decode(bytes.subarray(2))
    else value = bytes.toString('utf8').replace(/^\ufeff/, '')
    if (plain) return /^[1-9][0-9]{0,19}$/.test(value.trim()) ? [value.trim()] : []
    // Inspect only an exact AppId setting; never expose credentials or arbitrary configuration text.
    return value.split(/\r?\n/).flatMap(line => {
      const match = /^\s*AppId\s*=\s*([1-9][0-9]{0,19})\s*(?:[;#].*)?$/i.exec(line)
      return match ? [match[1]!] : []
    })
  } finally { await handle.close() }
}

export async function collectLocalGameEvidence(game: LibraryGame, options: { readExecutableMetadata?: boolean } = {}): Promise<LocalGameEvidence> {
  const result: LocalGameEvidence = { titles: [], companies: [], references: [], executableFingerprints: [], warnings: [] }
  const installation = game.installations.find(item => item.id === game.preferredInstallationId) ?? game.installations[0]
  if (!installation) return result
  let root: string
  try { root = await fs.realpath(installation.folderPath) } catch { result.warnings.push('游戏目录暂时无法读取。'); return result }
  const addTitle = (value: string, source: LocalGameEvidence['titles'][number]['source']) => {
    value = clean(value)
    if (source === 'product-name' || source === 'file-description') value = value.replace(MENU_SUFFIX, '').trim()
    const program = source === 'product-name' || source === 'file-description'
    if (useful(value) && !result.titles.some(title => compact(title.value) === compact(value) &&
      (title.source === source || program && (title.source === 'product-name' || title.source === 'file-description')))) result.titles.push({ value, source })
  }
  const filenames: string[] = []
  try {
    let inspected = 0
    for await (const entry of await fs.opendir(root)) {
      if (++inspected > DIRECTORY_LIMIT) { result.warnings.push('目录文件较多，只读取了部分名称线索。'); break }
      if (entry.isFile() && !entry.isSymbolicLink()) filenames.push(entry.name)
    }
  } catch { result.warnings.push('部分游戏目录信息无法读取。') }
  const plausible = filenames.filter(name => /\.exe$/i.test(name) && !isHelper(name))
    .sort((left, right) => Number(compact(stem(right)) === compact(game.name)) - Number(compact(stem(left)) === compact(game.name)) || left.localeCompare(right))
  const executables: string[] = []
  let selectedExecutable: string | null = null
  if (installation.executablePath && /\.exe$/i.test(installation.executablePath) && !isHelper(path.basename(installation.executablePath))) {
    const selected = await safeFile(root, installation.executablePath)
    if (selected) { selectedExecutable = selected; executables.push(selected) }
    else result.warnings.push('已选择的启动程序不在可读取的游戏目录内。')
  }
  let additional = 0
  for (const name of plausible) {
    if (additional >= 4) break
    const actual = await safeFile(root, path.join(root, name))
    if (!actual || executables.includes(actual)) continue
    executables.push(actual); additional++
  }
  let selectedHasTitle = false
  for (const executable of executables) {
    const titlesBeforeExecutable = result.titles.length
    addTitle(stem(executable), 'executable-name')
    // Opening even a small executable region can trigger antivirus scanning.
    // Keep filename evidence available without opening executable contents by default.
    if (options.readExecutableMetadata !== true || selectedHasTitle && executable !== selectedExecutable) continue
    let handle: FileHandle | undefined
    try {
      const opened = await openContainedFile(root, executable)
      handle = opened.handle
      // Parse every language before committing any fields. A later malformed
      // resource invalidates the whole executable's version metadata.
      const versions = (await versionResources(handle)).map(resource => ({ resource, fields: parseVersion(resource) }))
      await opened.verify()
      // A renamed installer/patcher can copy the game's descriptive fields. Its
      // original utility identity invalidates all languages, including descriptions.
      if (versions.some(({ fields }) => fields.originalNames.some(name => isHelper(path.win32.basename(name))))) {
        result.titles.splice(titlesBeforeExecutable)
        continue
      }
      let fingerprintable = false
      for (const { fields } of versions) {
        const hasTitle = fields.productNames.some(name => useful(clean(name).replace(MENU_SUFFIX, '').trim())) ||
          fields.descriptions.some(name => useful(clean(name).replace(MENU_SUFFIX, '').trim()))
        if (executable === selectedExecutable && hasTitle) selectedHasTitle = true
        for (const name of fields.productNames) addTitle(name, 'product-name')
        for (const description of fields.descriptions) addTitle(description, 'file-description')
        for (const original of fields.originalNames) {
          // Version metadata occasionally names a compiler resource such as WIN32.rc.
          const extension = path.win32.extname(original)
          if ((!extension || extension.toLowerCase() === '.exe') && !isHelper(path.win32.basename(original))) addTitle(stem(original), 'executable-name')
        }
        if (hasTitle) for (const company of fields.companies) if (company && !/^(?:companyname|unknown|yourcompany|todo)/i.test(compact(company))) result.companies.push(company)
        if (fields.productNames.some(name => useful(name) && !MENU_SUFFIX.test(name))) {
          fingerprintable = true
        }
      }
      // Several language resources are still one executable's metadata source.
      if (fingerprintable) {
        const hash = createHash('sha256')
        for (const { resource } of versions) hash.update(resource)
        hash.update('\0').update(path.basename(executable).normalize('NFKC').toLowerCase())
        result.executableFingerprints.push(`pe-version-sha256:${hash.digest('hex')}`)
      }
    } catch { result.warnings.push(`已跳过无法安全读取的程序版本信息：${path.basename(executable)}`) }
    finally { await handle?.close() }
  }
  for (const name of filenames.filter(name => ARCHIVES.has(path.extname(name).toLowerCase()) && useful(stem(name)) && !/(?:[._ -](?:bak|backup|old)|beifen|备份)$/i.test(stem(name))).sort().slice(0, 5)) addTitle(stem(name), 'archive-name')
  const configs = filenames.filter(name => CONFIG_NAMES.has(name.toLowerCase())).map(name => path.join(root, name))
  configs.push(path.join(root, 'steam_settings', 'configs.app.ini'), path.join(root, 'steam_settings', 'steam_appid.txt'))
  for (const candidate of configs) {
    const actual = await safeFile(root, candidate)
    if (!actual) continue
    try { for (const id of await configIds(root, actual, path.basename(candidate).toLowerCase() === 'steam_appid.txt')) result.references.push({ value: `steam:${id}`, source: 'steam-config' }) }
    catch { result.warnings.push(`已跳过过大或无法读取的 Steam 配置：${path.basename(candidate)}`) }
  }
  result.companies = [...new Set(result.companies)]
  result.references = [...new Map(result.references.map(ref => [ref.value, ref])).values()]
  result.executableFingerprints = [...new Set(result.executableFingerprints)]
  result.warnings = [...new Set(result.warnings)]
  if (result.references.length > 1) result.warnings.push('检测到多个不同的 Steam App ID，需要核对。')
  return result
}
