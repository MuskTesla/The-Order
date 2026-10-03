import { promises as fs } from 'node:fs'
import path from 'node:path'
import { readGameIdentity, type GameIdentity } from './game-identification'
import type { LibraryGame, WorkMetadata } from '../src/shared/types'

const supportedSources = ['vndb', 'bangumi', 'steam'] as const
const canonicalReference = (source: string, value: string): string | null => {
  const kind = source.trim().toLowerCase() === 'bgm' ? 'bangumi' : source.trim().toLowerCase(), id = value.trim().toLowerCase()
  if (kind === 'vndb') return /^v?[1-9]\d{0,11}$/.test(id) ? `vndb:v${id.replace(/^v/, '')}` : null
  if (!['bangumi', 'steam'].includes(kind) || !/^[1-9]\d{0,11}$/.test(id) || kind === 'steam' && id === '480') return null
  return `${kind}:${id}`
}

// Only independently read local numbers count here. Titles, derived index
// mappings, missing provider sources and invalid/test IDs remain unknown.
export function attachmentReferenceConflict(work: WorkMetadata, identity: GameIdentity): string | null {
  const evidence = identity.evidence?.references ?? []
  // readGameIdentity bounds the returned evidence to 40 entries. At that limit
  // it may have omitted another local edition ID, so disjointness is unknown.
  if (evidence.length >= 40) return null
  const local = new Set(evidence.filter(ref => ref.source !== 'title-index').flatMap(ref => {
    const separator = ref.value.indexOf(':'), key = canonicalReference(ref.value.slice(0, separator), ref.value.slice(separator + 1))
    return separator < 0 || !key ? [] : [key]
  }))
  const current = new Set(work.references.flatMap(ref => { const key = canonicalReference(ref.source, ref.id); return key ? [key] : [] }))
  for (const source of supportedSources) {
    const localIds = [...local].filter(ref => ref.startsWith(`${source}:`)), currentIds = [...current].filter(ref => ref.startsWith(`${source}:`))
    if (localIds.length && currentIds.length && !localIds.some(id => current.has(id))) return source
  }
  return null
}

export async function inspectInstallationAttachment(game: LibraryGame, folder: string, file: string): Promise<{ folderPath: string; executablePath: string }> {
  if (!path.isAbsolute(folder) || !path.isAbsolute(file)) throw Error('安装目录和启动程序须为绝对路径。')
  const folderPath = await fs.realpath(folder)
  const pathKey = (value: string) => process.platform === 'win32' ? path.resolve(value).toLowerCase() : path.resolve(value)
  // Main captures a canonical directory when the user authorizes the scan.
  // A replaced junction must require a new choice, rather than authorizing its
  // new external target merely because both paths now resolve into that target.
  if (pathKey(folderPath) !== pathKey(folder)) throw Error('游戏根目录的实际位置已变化，请重新选择根目录。')
  if (!(await fs.stat(folderPath)).isDirectory()) throw Error('游戏根目录不存在或不是目录。')
  const executablePath = await fs.realpath(file)
  const relative = path.relative(process.platform === 'win32' ? folderPath.toLowerCase() : folderPath, process.platform === 'win32' ? executablePath.toLowerCase() : executablePath)
  if (relative === '..' || relative.startsWith(`..${path.sep}`) || path.isAbsolute(relative) || path.extname(executablePath).toLowerCase() !== '.exe' || !(await fs.stat(executablePath)).isFile()) throw Error('候选启动文件必须是游戏根目录内的有效 exe。')
  if (game.metadata) {
    const id = 'attachment-evidence'
    const localGame: LibraryGame = { ...game, installations: [{ id, folderPath, executablePath, arguments: [] }], preferredInstallationId: id }
    // Existing bounded readers only: known small sidecars/Steam configurations,
    // no title index, PE contents, network request or executable launch.
    const identity = await readGameIdentity(localGame, [], { readExecutableMetadata: false })
    const conflict = attachmentReferenceConflict(game.metadata, identity)
    if (conflict) throw Error(`本地目录的 ${conflict === 'steam' ? 'Steam' : conflict === 'vndb' ? 'VNDB' : 'Bangumi'} 编号与当前关联作品明确冲突。请先核对作品关联，或返回普通导入。`)
  }
  return { folderPath, executablePath }
}
