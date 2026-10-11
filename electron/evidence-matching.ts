import type { WorkMetadata } from '../src/shared/types'
import type { GameIdentity, RankedMetadataMatch } from './game-identification'
import { compareGameTitles, normalizeGameTitle } from './title-matching'

export type TitleSource = 'folder' | 'custom-name' | 'sidecar' | 'product-name' | 'file-description' | 'executable-name' | 'archive-name' | 'readme-title' | 'manual-title' | 'config-title'
export interface TitleEvidence { value: string; source: TitleSource }
export interface ReferenceEvidence { value: string; source: 'sidecar' | 'steam-file' | 'steam-config' | 'title-index'; via?: string }
export interface TitleIndexProof { observedTitle: string; source: TitleSource; recordNames: string[]; references: string[] }
export interface IdentityEvidence { titles: TitleEvidence[]; references: ReferenceEvidence[]; companies: string[]; warnings: string[]; fingerprints: string[]; auxiliaryDirectory: boolean; indexProofs?: TitleIndexProof[] }

const compact = (value: string) => normalizeGameTitle(value).replace(/\s/g, '')
const key = (source: string, id: string) => source === 'vndb' && /^v[1-9]\d*$/.test(id) || ['bangumi', 'steam'].includes(source) && /^[1-9]\d*$/.test(id) ? `${source}:${id}` : ''
const program = (source: TitleSource) => source === 'product-name' || source === 'file-description'
const local = (source: TitleSource) => source !== 'sidecar'
const anchorSource = (source: TitleSource) => ['folder', 'custom-name', 'product-name', 'file-description', 'executable-name', 'manual-title', 'config-title'].includes(source)
const proofSource = (source: TitleSource) => anchorSource(source) && source !== 'custom-name'
const machineOnly = (work: WorkMetadata, value: string) => {
  const provenance = (work.titleEvidence ?? []).filter(title => compact(title.text) === compact(value))
  return provenance.length > 0 && provenance.every(title => title.isMachine)
}
const official = (work: WorkMetadata) => (work.titleEvidence ?? []).filter(title => !title.isMachine && ['official', 'original', 'latin'].includes(title.kind)).flatMap(title => {
  // Some provider titles list several official localized spellings with an
  // explicit vertical-bar separator. Each named spelling keeps that provenance.
  const parts = title.text.split(/\s*[|｜]\s*/).filter(Boolean)
  return parts.length > 1 && parts.length <= 5 ? [title.text, ...parts] : [title.text]
})
const primaryNames = (work: WorkMetadata) => [work.title, work.originalTitle].filter(value => {
  const provenance = (work.titleEvidence ?? []).filter(title => compact(title.text) === compact(value))
  // Older records and collection projections may not include provenance. When
  // details explicitly mark a name as generated or merely an alias, do not
  // promote that same name back to official status through display_name.
  return !provenance.length || provenance.some(title => !title.isMachine && ['official', 'original', 'latin'].includes(title.kind))
})
const sameCompany = (a: string, b: string) => {
  const brand = (value: string) => normalizeGameTitle(value)
    .replace(/\b(?:copyright|corporation|company|co|ltd|limited|inc|llc|corp|\d{4})\b/g, '')
    .replace(/株式会社|有限会社/g, '').replace(/\s/g, '')
  const left = brand(a), right = brand(b)
  // A vendor hint may break a retrieval tie, but a substring such as Key in
  // Monkey does not identify a brand. It never grants automatic authority.
  return left.length >= 3 && right.length >= 3 && left === right
}
const grouped = (refs: string[]) => {
  const result = new Map<string, Set<string>>()
  for (const ref of refs) { const source = ref.split(':')[0]!; if (!result.has(source)) result.set(source, new Set()); result.get(source)!.add(ref) }
  return result
}

const exactDistinctive = (a: string, b: string) => {
  const comparison = compareGameTitles(a, b)
  return comparison.exact && comparison.distinctive && comparison.version === 'same' && !comparison.conflict
}

function indexBridges(evidence: IdentityEvidence, work: WorkMetadata, canonical: string[], refs: Set<string>): Array<{ source: TitleSource; references: string[] }> {
  return (evidence.indexProofs ?? []).flatMap(proof => {
    // Every edge must be witnessed by this one row. Two partial rows, a copied
    // README, and an alias which proves itself via a derived ID cannot combine.
    if (!proofSource(proof.source) || machineOnly(work, proof.observedTitle)) return []
    if (!evidence.titles.some(title => title.source === proof.source && exactDistinctive(title.value, proof.observedTitle))) return []
    if (!proof.recordNames.some(name => exactDistinctive(name, proof.observedTitle))) return []
    if (!canonical.some(name => proof.recordNames.some(recordName => exactDistinctive(name, recordName)))) return []
    if ([...grouped(proof.references).values()].some(values => values.size > 1)) return []
    const matches = proof.references.filter(ref => refs.has(ref))
    return matches.length ? [{ source: proof.source, references: matches }] : []
  })
}

/** Coefficients order candidates. They are not calibrated correctness probabilities.
 * A high coefficient is insufficient: explicit evidence and uniqueness gates apply. */
export function rankEvidenceMatches(identity: GameIdentity, works: WorkMetadata[]): RankedMetadataMatch[] {
  const evidence = identity.evidence!
  const trusted = [...new Set(evidence.references.filter(ref => ref.source !== 'sidecar').map(ref => ref.value))]
  const weak = [...new Set(evidence.references.filter(ref => ref.source === 'sidecar').map(ref => ref.value))]
  const expected = grouped(trusted)
  const localTitles = evidence.titles.filter(title => local(title.source))
  const folders = localTitles.filter(title => title.source === 'folder' || title.source === 'custom-name')
  const executables = localTitles.filter(title => program(title.source))
  const resources = localTitles.filter(title => ['executable-name', 'archive-name', 'readme-title', 'manual-title', 'config-title'].includes(title.source))
  const anchors = localTitles.filter(title => anchorSource(title.source))
  const conflictingInput = [...expected.values()].some(values => values.size > 1)
  const unique = [...new Map(works.map(work => [work.id, work])).values()]
  const candidates = unique.map(work => {
    const refs = new Set(work.references.map(ref => key(ref.source, ref.id)).filter(Boolean))
    const actual = grouped([...refs])
    const weakMatched = weak.filter(ref => refs.has(ref))
    const referenceConflict = [...expected].some(([source, values]) => actual.has(source) && ![...actual.get(source)!].some(value => values.has(value)))
    const primary = primaryNames(work)
    const canonical = [...new Set([...primary, ...official(work)])].filter(Boolean)
    const allNames = [...new Set([...canonical, ...work.aliases.filter(name => !machineOnly(work, name))])]
    const bridges = indexBridges(evidence, work, canonical, refs)
    const bridgeReferences = [...new Set(bridges.flatMap(bridge => bridge.references))]
    const directoryOrProgramBridge = bridges.some(bridge => ['folder', 'product-name', 'file-description', 'executable-name'].includes(bridge.source))
    const matched = [...new Set([...trusted.filter(ref => refs.has(ref)), ...bridgeReferences])]
    const best = (titles: TitleEvidence[], names: string[]) => titles.flatMap(title => names.map(name => ({ title, name, ...compareGameTitles(title.value, name) }))).sort((a, b) => b.score - a.score)[0]
    const folder = best(folders, canonical), pe = best(executables, canonical), resource = best(resources, canonical)
    const folderAny = best(folders, allNames), peAny = best(executables, allNames), resourceAny = best(resources, allNames)
    const installationAny = best(anchors.filter(title => title.source !== 'manual-title' && title.source !== 'config-title'), allNames)
    const fullCanonical = [folder, pe, resource].some(match => match?.exact && match.distinctive)
    const preciseExecutable = resources.some(title => title.source === 'executable-name' && /^[a-z]{4,}\d{2,}$/i.test(compact(title.value)) && canonical.some(name => compact(name).startsWith(compact(title.value)) && (compareGameTitles(title.value, name).version !== 'conflict')))
    const company = evidence.companies.some(company => work.developers.some(developer => sameCompany(company, developer)))
    // A full independently read title that specifies another chapter/version is a
    // veto even when an old sidecar or a fuzzy search happens to carry this ID.
    const titleConflict = localTitles.some(title => primary.some(name => { const comparison = compareGameTitles(title.value, name); return comparison.version === 'conflict' && comparison.score >= 0.12 }))
    // An unrelated executable primary title cannot be rescued by a polluted alias
    // when both names are readable in the same Latin script.
    const latinContradiction = [...executables, ...folders].some(title => compareGameTitles(title.value, title.value).distinctive && /^[\x20-\x7e]+$/.test(title.value) && primary.every(name => /^[\x20-\x7e]+$/.test(name)) && Math.max(...primary.map(name => compareGameTitles(title.value, name).score)) < 0.25)
    const bestScore = Math.max(folder?.score ?? 0, pe?.score ?? 0, resource?.score ?? 0)
    const bestAny = Math.max(folderAny?.score ?? 0, peAny?.score ?? 0, resourceAny?.score ?? 0)
    // Derived index refs still need actual local text support. Index aliases and
    // sidecar titles never count as a second independent observation.
    const corroborates = bestAny >= 0.65 || [folderAny, peAny, resourceAny].some(match => match?.exact)
    const physicallyReadId = evidence.references.some(ref => ref.source === 'steam-file' || ref.source === 'steam-config') && matched.some(ref => evidence.references.some(local => local.value === ref && (local.source === 'steam-file' || local.source === 'steam-config')))
    const canonicalSupport = [folder, pe, resource].some(match => match?.exact)
    // A README may have been copied with a patch. Its index ID and title are
    // one observation, so require an anchor in the directory or actual program.
    const localAnchor = (folderAny?.score ?? 0) >= 0.5 || (peAny?.score ?? 0) >= 0.5 || preciseExecutable || directoryOrProgramBridge
    const physicalCorroboration = physicallyReadId && ((installationAny?.score ?? 0) >= 0.65 || !!installationAny?.exact)
    const indexBridge = bridgeReferences.length > 0 && localAnchor
    const indexedReference = evidence.references.some(ref => ref.source === 'title-index' && refs.has(ref.value)) || indexBridge
    const fullLocalTitles = anchors.filter(title => canonical.some(name => exactDistinctive(title.value, name)))
    const confirmedRelease = indexedReference && localAnchor && fullLocalTitles.length > 0 && (identity.releases?.[work.id] ?? []).some(release =>
      release.workId === work.id && ['default', 'digital', 'physical'].includes(release.kind.toLowerCase()) &&
      fullLocalTitles.some(title => [release.title, release.originalTitle].some(name => exactDistinctive(title.value, name))))
    let score = bestScore * 0.89
    let reason = bestAny > 0 ? '名称相近，继续核对作品、章节和版本' : '缺少对应的本地作品线索'
    let eligible = false
    // Level 3: an installation ID plus local text. Level 2: a confirmed formal
    // release or a cross-language index bridge. A direct canonical title stays
    // at level 1 until a formal release distinguishes it from catalog duplicates.
    // Multiple releases/refs do not add levels or independent votes.
    let evidenceLevel = 1
    if (physicalCorroboration) {
      eligible = true; evidenceLevel = 3; score = 0.99
      reason = '本地 Steam 编号与可读作品名称相互印证'
    } else if (confirmedRelease) {
      eligible = true; evidenceLevel = 2; score = 0.985
      reason = '本地完整标题、索引编号与正式发行名称一致'
    } else if (indexBridge) {
      eligible = true; evidenceLevel = fullCanonical ? 1 : 2; score = 0.98
      reason = '同一索引记录连接本地名称、官方名称与作品编号'
    } else if (matched.length && corroborates && (canonicalSupport || preciseExecutable) && localAnchor) {
      score = 0.97 + Math.min(0.02, matched.length * 0.005)
      eligible = true
      reason = `本地${matched.some(ref => ref.startsWith('steam:')) ? '编号与标题' : '标题索引编号与完整资料'}一致`
    } else if (!trusted.length && fullCanonical) {
      const exactFolder = folder?.exact && folder.distinctive
      const exactProgram = pe?.exact && pe.distinctive
      const exactReadme = resource?.exact && resource.distinctive && resource.title.source === 'readme-title'
      // A copied version resource or README cannot borrow an unverified alias
      // as its independent directory anchor. Verified cross-language index
      // bridges and physical installation IDs are handled above.
      eligible = !!exactFolder || !!exactProgram && (folder?.score ?? 0) >= 0.5 || !!exactReadme && (folder?.score ?? 0) >= 0.65
      score = eligible ? 0.95 : 0.88
      reason = exactProgram ? '程序内的完整作品名称与详情一致' : exactFolder ? '完整官方标题唯一对应' : '本地资料中的完整标题一致'
    } else if (!trusted.length && preciseExecutable && (folderAny?.score ?? 0) >= 0.5) {
      eligible = true; score = 0.955; reason = '主程序名称中的作品编号与官方完整标题对应'
    } else {
      score = Math.max(score, bestAny * 0.82)
      if (fullCanonical) score = Math.max(score, 0.93)
      if (weakMatched.length && corroborates) { score = Math.max(score, 0.87); reason = '旧识别编号与部分名称一致，仍需独立确认' }
    }
    // File-name and resource-name repeats share one origin and cannot add votes.
    if (company && score > 0) score = Math.min(0.99, score + 0.01)
    if (titleConflict || latinContradiction || referenceConflict || conflictingInput || evidence.auxiliaryDirectory) {
      eligible = false; evidenceLevel = 0; score = Math.min(score, 0.6)
      reason = evidence.auxiliaryDirectory ? '位于补丁或备份目录，请选择实际游戏根目录' : conflictingInput || referenceConflict ? '独立本地编号存在冲突' : '本地程序或目录指向不同的作品、章节或版本'
    }
    return { work, confidence: Math.max(0, Math.min(0.99, score)), reason, automatic: false, eligible, matched, weakMatched, fullCanonical, physicallyReadId, evidenceLevel, vndbIds: [...refs].filter(ref => ref.startsWith('vndb:')) }
  })
  const supported = candidates.filter(candidate => candidate.matched.length)
  candidates.sort((a, b) => Number(b.eligible) - Number(a.eligible) || (a.eligible && b.eligible ? b.evidenceLevel - a.evidenceLevel : 0) || b.confidence - a.confidence || a.work.id.localeCompare(b.work.id))
  const best = candidates[0]
  if (best?.eligible) {
    const peers = candidates.slice(1).filter(candidate => candidate.evidenceLevel >= best.evidenceLevel)
    const contender = peers.find(candidate => candidate.eligible || candidate.confidence >= 0.85 || candidate.matched.length)
    const margin = contender ? best.confidence - contender.confidence : 1
    const splitReferences = best.matched.length > 0 && supported.length > 1
    const sameNameWithoutIndependentId = !best.physicallyReadId && best.fullCanonical && peers.some(candidate => candidate.fullCanonical && candidate.confidence >= 0.9)
    // A release row can distinguish a rich work record from a thin duplicate,
    // but cannot erase a competing, explicitly different VNDB work identity.
    const differentWorkIdentity = best.evidenceLevel === 2 && best.vndbIds.length > 0 && candidates.slice(1).some(candidate =>
      candidate.vndbIds.length > 0 && !candidate.vndbIds.some(ref => best.vndbIds.includes(ref)) &&
      (candidate.fullCanonical || candidate.eligible || candidate.matched.length > 0))
    if (!splitReferences && !sameNameWithoutIndependentId && !differentWorkIdentity && margin >= 0.075) best.automatic = true
    else best.reason = splitReferences || differentWorkIdentity ? '来源编号同时支持多个作品，需要核对' : '前两候选证据接近，需要补充线索'
  }
  return candidates.map(({ work, confidence, reason, automatic }) => ({ work, confidence, reason, automatic }))
}
