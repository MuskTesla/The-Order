import { promises as fs } from 'node:fs'
import path from 'node:path'
import type { LibraryGame } from '../src/shared/types'

export interface LocalTextEvidence {
  titles: Array<{ value: string; source: 'manual-title' | 'config-title' }>
  companies: string[]
  warnings: string[]
}

const FILE_LIMIT = 128 * 1024
const READ_LIMIT = 32 * 1024
const DOCUMENT_LIMIT = 8
const ENTRY_LIMIT = 128
const MAX_TITLES = 16
const DOCUMENT_NAME = /^(?:read[ _-]?me(?:[-_ ](?:ja|jp|zh|en))?|manual|お読みください|お読み下さい|はじめにお読みください|説明書|游戏说明|遊戲說明)\.(?:html?|txt)$/i
const CONFIG_NAME = /^(?:start|game|gameconfig|gameexe)\.ini$/i
const TITLE_KEY = /^(?:game_?title|product_?name|title|ゲームタイトル|タイトル|游戏名称|游戏标题|遊戲名稱)$/i
const COMPANY_KEY = /^(?:brand|maker|company|publisher|developer|メーカー|ブランド|会社名|厂商|品牌|开发商)$/i
const compact = (text: string) => text.normalize('NFKC').toLowerCase().replace(/[^\p{L}\p{N}]/gu, '')
const GENERIC = /^(?:readme|manual|usermanual|webmanual|onlinehelp|help|index|contents|tableofcontents|introduction|installation|systemrequirements|ゲーム|ゲームタイトル|タイトル|製品|本製品|ゲームマニュアル|マニュアル|説明書|取扱説明書|ユーザーマニュアル|ウェブマニュアル|webマニュアル|お読みください|はじめに|目次|ゲームについて|本製品について|製品について|游戏|游戏名称|游戏说明|说明书|使用说明|目録|目錄|目录|注意事項|ご注意|動作環境|起動方法|インストール方法|アンインストール方法|操作方法|game|gameexe|application|siglusengine|siglus|kirikiri|tvp|bgi|ethornell|reallive)$/u
const BOILERPLATE = /^(?:インストール|アンインストール|ゲームの(?:起動|終了|操作|削除)|操作説明|操作方法|著作権|免責|トラブル|サポート|修正履歴|更新履歴|動作環境|ご利用|ご注意|注意事項|版权|免责声明|安装方法|运行环境|操作说明)/u

function clean(value: string): string {
  return value.replace(/^\ufeff/, '').replace(/[\u0000-\u001f\u007f]/g, ' ').replace(/\s+/g, ' ').trim()
}
function meaningful(value: string): boolean {
  const key = compact(value)
  return value.length <= 180 && key.length >= 3 && /\p{L}/u.test(key) && !GENERIC.test(key) && !BOILERPLATE.test(value) && !/[<>\uFFFD]|https?:\/\/|(?:^|\s)[A-Za-z]:[\\/]/i.test(value)
}
function within(root: string, file: string): boolean {
  const relative = path.relative(root, file)
  return relative === '' || relative !== '..' && !relative.startsWith(`..${path.sep}`) && !path.isAbsolute(relative)
}
function entityText(value: string): string {
  return value.replace(/&(#x[0-9a-f]{1,6}|#[0-9]{1,7}|amp|quot|apos|lt|gt|nbsp);/gi, (whole, entity: string) => {
    const known: Record<string, string> = { amp: '&', quot: '"', apos: "'", lt: '<', gt: '>', nbsp: ' ' }
    if (!entity.startsWith('#')) return known[entity.toLowerCase()] ?? whole
    const code = entity[1]?.toLowerCase() === 'x' ? Number.parseInt(entity.slice(2), 16) : Number.parseInt(entity.slice(1), 10)
    return code >= 32 && code <= 0x10ffff && !(code >= 0xd800 && code <= 0xdfff) ? String.fromCodePoint(code) : ''
  })
}
function titleFromHeading(input: string): string[] {
  const text = clean(input)
  if (text.length > 500 || /^(?:メーカー|ブランド|開発元|厂商|品牌|開發商|developer|publisher|brand)\s*[:：]/i.test(text)) return []
  const quoted = [...text.matchAll(/[『「《]([^』」》\r\n]{3,180})[』」》]/g)].map(match => clean(match[1]!))
  if (quoted.length) return quoted.filter(meaningful)
  const value = text
    .replace(/^(?:お読みくださ[いイ]|お読み下さい|はじめにお読みください|read\s*me|(?:ゲーム)?タイトル|游戏名称|title\s*[:：])\s*[:：\-–—]?\s*/i, '')
    .replace(/\s*(?:[-–—|:：]\s*)?(?:(?:web|online|ウェブ|オンライン)?\s*(?:マニュアル|manual|説明書|取扱説明書)(?:へのリンク)?|について|開始メニュー|スタートメニュー)\s*$/i, '')
    .trim()
  return meaningful(value) ? [value] : []
}
function legacyScore(value: string): number {
  const kana = value.match(/[\u3040-\u30ff]/g)?.length ?? 0
  const han = value.match(/[\u4e00-\u9fff]/g)?.length ?? 0
  const halfWidth = value.match(/[\uff61-\uff9f]/g)?.length ?? 0
  const labels = value.match(/ゲーム|タイトル|メーカー|ブランド|マニュアル|游戏|标题|名称|厂商|品牌/g)?.length ?? 0
  return labels * 20 + kana * 2 + han * 0.5 - halfWidth * 4
}
function decode(bytes: Buffer, truncated: boolean): string {
  if (bytes[0] === 0xff && bytes[1] === 0xfe) return new TextDecoder('utf-16le', { fatal: true }).decode(bytes.subarray(2), { stream: truncated })
  if (bytes[0] === 0xfe && bytes[1] === 0xff) return new TextDecoder('utf-16be', { fatal: true }).decode(bytes.subarray(2), { stream: truncated })
  const declaration = /charset\s*=\s*["']?\s*([a-z0-9_-]+)/i.exec(bytes.subarray(0, 4096).toString('latin1'))?.[1]?.toLowerCase()
  const encoding = declaration && ({ 'utf-8': 'utf-8', utf8: 'utf-8', shift_jis: 'shift_jis', 'shift-jis': 'shift_jis', sjis: 'shift_jis', 'windows-31j': 'shift_jis', gbk: 'gb18030', gb2312: 'gb18030', gb18030: 'gb18030' } as Record<string, string>)[declaration]
  if (encoding) return new TextDecoder(encoding, { fatal: true }).decode(bytes, { stream: truncated })
  try { return new TextDecoder('utf-8', { fatal: true }).decode(bytes, { stream: truncated }) } catch { /* Older manuals often omit their encoding declaration. */ }
  const alternatives: string[] = []
  for (const candidate of ['shift_jis', 'gb18030']) {
    try { alternatives.push(new TextDecoder(candidate, { fatal: true }).decode(bytes, { stream: truncated })) } catch { /* Reject invalid byte sequences. */ }
  }
  if (!alternatives.length) throw new Error('Invalid manual encoding')
  return alternatives.sort((left, right) => legacyScore(right) - legacyScore(left))[0]!
}

/** Reads bounded text metadata from this installation only; never follows document links. */
export async function readLocalTextEvidence(game: LibraryGame): Promise<LocalTextEvidence> {
  const result: LocalTextEvidence = { titles: [], companies: [], warnings: [] }
  const installation = game.installations.find(item => item.id === game.preferredInstallationId) ?? game.installations[0]
  if (!installation) return result
  let root: string
  try { root = await fs.realpath(installation.folderPath) } catch { result.warnings.push('游戏目录暂时无法读取说明资料。'); return result }
  const addTitle = (value: string, source: LocalTextEvidence['titles'][number]['source']) => {
    value = clean(value)
    if (result.titles.length < MAX_TITLES && meaningful(value) && !result.titles.some(item => compact(item.value) === compact(value))) result.titles.push({ value, source })
  }
  const addCompany = (value: string) => {
    value = clean(value).replace(/^["'「『]|["'」』]$/g, '').trim()
    if (result.companies.length < 8 && value.length <= 100 && meaningful(value) && !result.companies.includes(value)) result.companies.push(value)
  }
  const candidates: Array<{ file: string; config: boolean }> = []
  // Only fixed immediate child directories are inspected; no recursive walk or parent traversal.
  for (const directory of ['', 'manual', 'Manual', 'help', 'StartData']) {
    const proposed = path.join(root, directory)
    let actual: string
    try {
      if ((await fs.lstat(proposed)).isSymbolicLink()) continue
      actual = await fs.realpath(proposed)
      if (!within(root, actual) || !(await fs.stat(actual)).isDirectory()) continue
      let count = 0
      for await (const entry of await fs.opendir(actual)) {
        if (++count > ENTRY_LIMIT) { result.warnings.push('说明目录文件较多，只检查了部分文件名。'); break }
        if (!entry.isFile() || entry.isSymbolicLink()) continue
        const name = entry.name.normalize('NFKC')
        const config = CONFIG_NAME.test(name) && (!directory || directory === 'StartData')
        const document = DOCUMENT_NAME.test(name) || /^(?:manual|help)$/i.test(directory) && /^index\.html?$/i.test(name)
        if (!config && !document) continue
        const file = path.join(actual, entry.name)
        if (!candidates.some(item => item.file.toLowerCase() === file.toLowerCase())) candidates.push({ file, config })
      }
    } catch { /* Optional manual/config directories are commonly absent. */ }
  }
  if (candidates.length > DOCUMENT_LIMIT) result.warnings.push('说明文件较多，只读取了部分标题资料。')
  for (const candidate of candidates.slice(0, DOCUMENT_LIMIT)) {
    let handle: Awaited<ReturnType<typeof fs.open>> | undefined
    try {
      if ((await fs.lstat(candidate.file)).isSymbolicLink()) continue
      const file = await fs.realpath(candidate.file)
      if (!within(root, file) || !/\.(?:html?|txt|ini)$/i.test(file)) continue
      const before = await fs.stat(file)
      if (!before.isFile() || before.size > FILE_LIMIT) { result.warnings.push('已跳过过大的说明文件。'); continue }
      handle = await fs.open(file, 'r')
      const current = await handle.stat()
      if (!current.isFile() || current.size > FILE_LIMIT || current.ino !== before.ino || current.dev !== before.dev || !within(root, await fs.realpath(candidate.file))) continue
      const buffer = Buffer.alloc(Math.min(current.size, READ_LIMIT))
      let read = 0
      while (read < buffer.length) {
        const chunk = await handle.read(buffer, read, buffer.length - read, read)
        if (!chunk.bytesRead) break
        read += chunk.bytesRead
      }
      const text = decode(buffer.subarray(0, read), current.size > read)
      if (text.includes('\0')) continue
      if (candidate.config) {
        for (const line of text.split(/\r?\n/)) {
          if (line.length > 512) continue
          const match = /^\s*#?\s*([^=;#\r\n]{1,32})\s*=\s*(?:"([^"\r\n]*)"|'([^'\r\n]*)'|([^;#\r\n]*))\s*(?:[;#].*)?$/.exec(line)
          if (!match) continue
          const key = match[1]!.trim(), value = match[2] ?? match[3] ?? match[4] ?? ''
          if (TITLE_KEY.test(key)) addTitle(value, 'config-title')
          if (COMPANY_KEY.test(key)) addCompany(value)
        }
        continue
      }
      const safe = text.replace(/<!--[\s\S]*?(?:-->|$)/g, ' ').replace(/<(script|style|svg|template|iframe|object|noscript)\b[^>]*>[\s\S]*?(?:<\/\1\s*>|$)/gi, ' ')
      const headings: string[] = []
      if (/\.html?$/i.test(file)) {
        for (const match of safe.matchAll(/<(title|h[1-3])\b[^>]*>([\s\S]*?)<\/\1\s*>/gi)) {
          if (headings.length >= 12) break
          headings.push(entityText(match[2]!.replace(/<[^>]*>/g, ' ')))
        }
      } else headings.push(...safe.split(/\r?\n/).slice(0, 8).filter(line => /[『「《][^』」》]+[』」》]|^(?:ゲームタイトル|タイトル|游戏名称|title)\s*[:：]/i.test(line.trim())))
      for (const heading of headings) for (const value of titleFromHeading(heading)) addTitle(value, 'manual-title')
      const plain = entityText(safe.replace(/<[^>]*>/g, ' ').replace(/[\t ]+/g, ' '))
      // These phrases explicitly label a product, unlike unrelated titles mentioned in prose or links.
      for (const match of plain.matchAll(/(?:弊社製品|本製品|ゲームタイトル|游戏名称)\s*[:：]?\s*[「『《]([^」』》\r\n]{3,180})[」』》]/g)) addTitle(match[1]!, 'manual-title')
      for (const line of [...headings, ...plain.split(/\r?\n/)].slice(0, 300)) {
        const labelled = /^\s*(?:メーカー|ブランド|開発元|厂商|品牌|開發商|developer|publisher|brand)\s*[:：]\s*([^\r\n]{3,100})\s*$/i.exec(line)
        if (labelled) addCompany(labelled[1]!)
        const brandedTitle = /^\s*([^『「\r\n]{3,60})[『「]([^』」\r\n]{3,180})[』」]について\s*$/.exec(line)
        if (brandedTitle && result.titles.some(item => compact(item.value) === compact(brandedTitle[2]!))) addCompany(brandedTitle[1]!)
      }
    } catch { result.warnings.push('部分说明标题或配置无法安全读取。') }
    finally { await handle?.close() }
  }
  result.warnings = [...new Set(result.warnings)]
  return result
}
