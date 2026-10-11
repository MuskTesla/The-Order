import assert from 'node:assert/strict'
import { test } from 'node:test'
import { promises as fs } from 'node:fs'
import { createRequire } from 'node:module'
import vm from 'node:vm'
import { parse, compileScript } from '@vue/compiler-sfc'
import { transform } from 'esbuild'
import { nextTick, reactive, type VNode } from 'vue'

// Render the actual SFC's compiled template, including its real computed props
// and native button handlers. No duplicate implementation of launch readiness.
const require = createRequire(new URL('../src/components/GameCard.vue', import.meta.url))
const component = (async () => {
  const source = await fs.readFile(new URL('../src/components/GameCard.vue', import.meta.url), 'utf8')
  const { descriptor } = parse(source)
  const script = compileScript(descriptor, { id: 'game-card-regression', inlineTemplate: true })
  const compiled = await transform(script.content, { loader: 'ts', format: 'cjs', target: 'es2022' })
  const module = { exports: {} as any }
  // This rendererless harness exercises the template. Actual frame sizing and
  // observer cleanup are checked in the isolated Electron cover regression.
  vm.runInNewContext(compiled.code, { module, exports: module.exports, require: (id: string) => id.endsWith('.vue') ? { default: { name: 'OrderMark' } }
    : id === 'vue' ? { ...require(id), onMounted() {}, onBeforeUnmount() {} } : require(id) })
  return module.exports.default
})()

const game = (installations: any[], preferredInstallationId: string | null = null) => ({
  id: 'fixture', name: 'Synthetic local game', metadata: null, installations, preferredInstallationId,
  status: 'planned', personalRating: null, favorite: false, totalPlaySeconds: 0, playCount: 0,
})
const installation = (id: string, executablePath: string | null) => ({ id, executablePath, folderPath: 'C:/synthetic-only', arguments: [] })
function nodes(root: VNode): VNode[] {
  return [root, ...(Array.isArray(root.children) ? root.children.flatMap(child => child && typeof child === 'object' ? nodes(child as VNode) : []) : [])]
}
function hasClass(node: VNode, name: string) { return String(node.props?.class ?? '').split(/\s+/).includes(name) }
function nodeText(node: VNode): string { return Array.isArray(node.children) ? node.children.map(child => child && typeof child === 'object' ? nodeText(child as VNode) : String(child ?? '')).join('') : String(node.children ?? '') }
async function mount(input: any) {
  const props = reactive({ selected: false, selectionMode: false, layout: 'grid', launchDisabled: false, includeNsfw: true, globalDisplay: 'blur', ...input })
  const events: unknown[][] = [], emit = (...args: unknown[]) => events.push(args)
  const render = (await component).setup(props, { expose() {}, emit })
  const tree = () => render({ $emit: emit }, []) as VNode
  const find = (name: string) => nodes(tree()).find(node => hasClass(node, name))
  return { props, events, tree, find }
}

test('list actions do not launch another installation when the default has no exe', async () => {
  const card = await mount({ game: game([installation('selected', null), installation('other', 'C:/synthetic-only/Game.exe')], 'selected'), layout: 'list' })
  assert.equal(card.find('card-launch'), undefined)
  const configure = card.find('card-configure')!
  assert.ok(configure); configure.props!.onClick()
  assert.deepEqual(card.events, [['configure']])
})

test('multiple installations require a valid default while a sole executable remains launchable', async () => {
  const multiple = await mount({ game: game([installation('one', 'C:/synthetic-only/A.exe'), installation('two', 'C:/synthetic-only/B.exe')]), layout: 'list' })
  assert.equal(multiple.find('card-launch'), undefined)
  const single = await mount({ game: game([installation('one', 'C:/synthetic-only/A.exe')]), layout: 'list' })
  assert.ok(single.find('card-launch')); assert.equal(single.find('card-configure'), undefined)
  single.find('card-launch')!.props!.onClick()
  assert.deepEqual(single.events, [['play']])
})

test('busy launch uses native disabled without hiding the action or enabling configuration', async () => {
  const card = await mount({ game: game([installation('one', 'C:/synthetic-only/A.exe')]), layout: 'list', launchDisabled: true })
  assert.equal(card.find('card-launch')!.props!.disabled, true)
  assert.equal(card.find('card-configure'), undefined)
})

test('selection is an independent labeled checkbox and does not emit open', async () => {
  const card = await mount({ game: game([]), selected: true, selectionMode: true })
  const checkbox = card.find('card-select')!
  assert.equal(checkbox.type, 'button'); assert.equal(checkbox.props!.role, 'checkbox')
  assert.equal(checkbox.props!['aria-checked'], true)
  assert.match(checkbox.props!['aria-label'], /Synthetic local game/)
  checkbox.props!.onClick()
  assert.deepEqual(card.events, [['toggleSelected']])
  for (const button of nodes(card.tree()).filter(node => node.type === 'button')) {
    assert.equal(nodes(button).slice(1).some(node => node.type === 'button'), false, 'buttons must not be nested')
  }
})

test('unlinked local games show configuration, zero personal ratings take priority, and list markup retains open actions', async () => {
  const card = await mount({ game: { ...game([installation('one', null)]), personalRating: 0, metadata: { ratings: [{source:'bangumi',score:8.5,scale:10}], developers:[], tags:[] } }, layout: 'list' })
  assert.ok(hasClass(card.tree(), 'card-list'))
  assert.equal(nodeText(card.find('card-availability')!), '待设程序')
  assert.equal(nodeText(card.find('card-configure')!), '设置启动程序')
  assert.ok(nodeText(card.find('card-status')!).includes('未开始'))
  assert.ok(nodeText(card.find('card-rating-summary')!).includes('0.0'))
  assert.ok(nodeText(card.find('card-rating-summary')!).includes('我的'))
  assert.equal(nodes(card.tree()).filter(node => hasClass(node, 'card-rating-summary') || hasClass(node, 'card-personal-rating') || hasClass(node, 'card-rating')).length, 1)
  assert.equal(nodeText(card.find('card-rating-summary')!).includes('8.5'), false)
  card.find('card-title')!.props!.onClick(); card.find('card-image')!.props!.onClick()
  assert.deepEqual(card.events, [['open'], ['open']])
})

test('wishlist offers adding a local game, while launchable games omit a redundant local marker', async () => {
  const wishlist = await mount({ game: game([]), layout: 'list' })
  assert.equal(nodeText(wishlist.find('card-availability')!), '未安装')
  assert.equal(nodeText(wishlist.find('card-configure')!), '添加本地游戏')
  wishlist.find('card-configure')!.props!.onClick()
  assert.deepEqual(wishlist.events, [['configure']])
  const local = await mount({ game: game([installation('one', 'C:/synthetic-only/Game.exe')]), layout: 'list' })
  assert.equal(local.find('card-availability'), undefined)
  assert.equal(local.find('card-location'), undefined)
})

test('source rating is labeled when personal rating is absent, with no unrated or second score badge', async () => {
  const card = await mount({ game: { ...game([]), metadata: { ratings: [{source:'bangumi',score:8.5,scale:10}], developers:[], tags:[] } }, layout: 'list' })
  assert.ok(nodeText(card.find('card-rating-summary')!).includes('Bangumi'))
  assert.ok(nodeText(card.find('card-rating-summary')!).includes('8.5'))
  assert.equal(card.find('card-personal-rating'), undefined)
  assert.equal(card.find('card-rating'), undefined)
})

const coveredGame = () => ({ ...game([]), metadata: { id: 'fixture-work', title: 'Synthetic cover', coverUrl: 'https://cover-fixture.invalid/portrait.svg', ratings: [], developers: [], tags: [] } })

test('library grid has only cover and a two-target readable title, with no metadata or action controls', async () => {
  const title = '合成超长作品名称：这是用于验证书架简洁显示与完整名称提示的测试条目'.repeat(3)
  const card = await mount({ game: { ...coveredGame(), name: title, favorite: true, status: 'playing', personalRating: 9.5, totalPlaySeconds: 7200, playCount: 3 } })
  assert.ok(hasClass(card.tree(), 'compact-card'))
  assert.equal(nodes(card.tree()).filter(node => node.type === 'button').length, 2)
  for (const name of ['favorite-button', 'card-open', 'card-meta', 'card-status', 'card-rating-summary', 'card-progress', 'card-bottom', 'card-launch', 'card-configure']) {
    assert.equal(card.find(name), undefined, `${name} must be absent from the simple library grid`)
  }
  assert.equal(nodes(card.tree()).some(node => node.type === 'p'), false)
  assert.equal(card.find('card-title')!.props!.title, title)
  assert.equal(nodeText(card.find('card-title')!), title)
  assert.equal(nodeText(card.find('compact-title-text')!), title)
  assert.equal(card.find('card-image')!.props!['aria-label'], `查看 ${title}`)
  card.find('card-image')!.props!.onClick(); card.find('card-title')!.props!.onClick()
  assert.deepEqual(card.events, [['open'], ['open']])
})

test('switching a local grid into list restores its metadata, favorite and executable action', async () => {
  const card = await mount({ game: { ...coveredGame(), installations: [installation('one', 'C:/synthetic-only/Game.exe')] } })
  assert.equal(card.find('favorite-button'), undefined)
  assert.equal(card.find('card-launch'), undefined)
  card.props.layout = 'list'; await nextTick()
  assert.equal(hasClass(card.tree(), 'compact-card'), false)
  assert.ok(hasClass(card.tree(), 'card-list'))
  assert.equal(card.find('compact-title-text'), undefined)
  assert.ok(card.find('card-meta'))
  assert.ok(card.find('card-progress'))
  card.find('favorite-button')!.props!.onClick(); card.find('card-launch')!.props!.onClick()
  assert.deepEqual(card.events, [['favorite'], ['play']])
  card.props.layout = 'grid'; await nextTick()
  assert.ok(hasClass(card.tree(), 'compact-card'))
  assert.equal(card.find('favorite-button'), undefined)
  assert.equal(card.find('card-launch'), undefined)
})

test('discovery work cards keep their source rating, developer and release information', async () => {
  const card = await mount({ work: { ...coveredGame().metadata, ratings: [{ source: 'bangumi', score: 8.5, scale: 10 }], developers: ['Synthetic studio'], releaseDate: '2026-01-02' } })
  assert.equal(hasClass(card.tree(), 'compact-card'), false)
  assert.ok(nodeText(card.find('card-rating-summary')!).includes('8.5'))
  assert.equal(nodes(card.tree()).find(node => node.type === 'p')?.props?.title, 'Synthetic studio')
  assert.equal(nodeText(card.find('card-location')!), '2026')
  assert.ok(card.find('card-open'))
})

test('loading covers do not display a placeholder or eagerly request a backdrop; ordinary portraits fill', async () => {
  const card = await mount({ game: coveredGame() })
  assert.equal(card.find('cover-placeholder'), undefined)
  assert.equal(card.find('cover-backdrop'), undefined)
  assert.equal(card.find('card-image')!.props!['aria-busy'], true)
  const image = card.find('cover-foreground')!
  assert.equal(image.props!.loading, 'lazy')
  assert.equal(image.props!.referrerpolicy, 'no-referrer')
  image.props!.onLoad({ currentTarget: { naturalWidth: 600, naturalHeight: 900 } })
  assert.equal(card.find('card-image')!.props!['aria-busy'], false)
  assert.equal(hasClass(card.find('card-image')!, 'preserve-cover'), false)
  assert.equal(card.find('cover-backdrop'), undefined)
  assert.equal(card.find('cover-placeholder'), undefined)
})

test('unusual proportions preserve the artwork with a decorative same-image backdrop, including image changes and failures', async () => {
  const card = await mount({ game: coveredGame() })
  card.find('cover-foreground')!.props!.onLoad({ currentTarget: { naturalWidth: 1200, naturalHeight: 600 } })
  assert.equal(hasClass(card.find('card-image')!, 'preserve-cover'), true)
  const backdrop = card.find('cover-backdrop')!
  assert.equal(backdrop.props!.src, card.find('cover-foreground')!.props!.src)
  assert.equal(backdrop.props!.alt, '')
  assert.equal(backdrop.props!['aria-hidden'], 'true')
  assert.equal(backdrop.props!.loading, 'lazy')
  card.props.game.metadata.coverUrl = 'https://cover-fixture.invalid/replacement.svg'
  await nextTick()
  assert.equal(card.find('cover-backdrop'), undefined)
  assert.equal(card.find('card-image')!.props!['aria-busy'], true)
  card.find('cover-foreground')!.props!.onLoad({ currentTarget: { naturalWidth: 300, naturalHeight: 1200 } })
  assert.ok(card.find('cover-backdrop'))
  card.find('cover-foreground')!.props!.onError()
  assert.equal(card.find('cover-backdrop'), undefined)
  assert.equal(card.find('cover-foreground'), undefined)
  assert.equal(card.find('card-image')!.props!['aria-busy'], false)
  assert.ok(nodeText(card.find('cover-placeholder')!).includes('封面暂不可用'))
  card.find('card-image')!.props!.onClick()
  assert.deepEqual(card.events, [['open']])
})

test('square artwork switches blur, plain, and crop immediately without reloading its foreground', async () => {
  const card = await mount({ game: coveredGame() })
  card.find('cover-foreground')!.props!.onLoad({ currentTarget: { naturalWidth: 800, naturalHeight: 800 } })
  assert.equal(hasClass(card.find('card-image')!, 'preserve-cover'), true)
  assert.ok(card.find('cover-backdrop'))
  card.props.game.nonPortraitCoverDisplay = 'plain'
  await nextTick()
  assert.equal(hasClass(card.find('card-image')!, 'preserve-cover'), true)
  assert.equal(card.find('cover-backdrop'), undefined)
  assert.equal(hasClass(card.find('cover-foreground')!, 'cover-loading'), false)
  card.props.game.nonPortraitCoverDisplay = 'crop'
  await nextTick()
  assert.equal(hasClass(card.find('card-image')!, 'preserve-cover'), false)
  assert.equal(card.find('cover-backdrop'), undefined)
  assert.equal(card.find('card-image')!.props!['aria-busy'], false)
  card.props.game.nonPortraitCoverDisplay = 'blur'
  await nextTick()
  assert.ok(card.find('cover-backdrop'))
  assert.equal(hasClass(card.find('cover-foreground')!, 'cover-loading'), false)
})

test('nonportrait covers follow saved global preferences until a per-game override is supplied or cleared', async () => {
  const card = await mount({ game: coveredGame(), globalDisplay: 'plain' })
  card.find('cover-foreground')!.props!.onLoad({ currentTarget: { naturalWidth: 1200, naturalHeight: 600 } })
  assert.equal(hasClass(card.find('card-image')!, 'preserve-cover'), true)
  assert.equal(card.find('cover-backdrop'), undefined)
  card.props.globalDisplay = 'crop'; await nextTick()
  assert.equal(hasClass(card.find('card-image')!, 'preserve-cover'), false)
  card.props.game.nonPortraitCoverDisplay = 'blur'; await nextTick()
  assert.ok(card.find('cover-backdrop'))
  card.props.globalDisplay = 'plain'; await nextTick()
  assert.ok(card.find('cover-backdrop'), 'existing per-game preferences retain their effect')
  card.props.game.nonPortraitCoverDisplay = null; await nextTick()
  assert.equal(card.find('cover-backdrop'), undefined)
  assert.equal(hasClass(card.find('card-image')!, 'preserve-cover'), true)
  assert.equal(card.find('card-image')!.props!['aria-busy'], false)
})

test('local covers display for unassociated books and take priority over filtered remote images', async () => {
  const localCover = { dataUrl: 'data:image/jpeg;base64,c3ludGhldGlj', width: 1200, height: 600 }
  const card = await mount({ game: { ...game([]), localCover }, includeNsfw: false, globalDisplay: 'plain' })
  assert.equal(card.find('cover-foreground')!.props!.src, localCover.dataUrl)
  card.find('cover-foreground')!.props!.onLoad({ currentTarget: { naturalWidth: 1200, naturalHeight: 600 } })
  assert.equal(hasClass(card.find('card-image')!, 'preserve-cover'), true)
  assert.equal(card.find('cover-backdrop'), undefined)
  card.props.game.metadata = { ...coveredGame().metadata, covers: [{ id: '1', url: 'https://cover-fixture.invalid/explicit.svg', width: 1200, height: 600, votes: 1, sexual: 'explicit' }] }
  card.props.game.coverChoice = { workId: 'fixture-work', cover: card.props.game.metadata.covers[0] }
  await nextTick()
  assert.equal(card.find('cover-foreground')!.props!.src, localCover.dataUrl)
  assert.equal(card.find('card-image')!.props!['aria-busy'], false)
  delete card.props.game.localCover; await nextTick()
  assert.equal(card.find('cover-foreground')!.props!.src, card.props.game.metadata.coverUrl)
  assert.equal(card.find('card-image')!.props!['aria-busy'], true)
})

test('a changed personal choice resets image loading and clearing or reassociating it returns to the default', async () => {
  const card = await mount({ game: coveredGame() })
  card.find('cover-foreground')!.props!.onLoad({ currentTarget: { naturalWidth: 600, naturalHeight: 900 } })
  const personal = { id: 'chosen-cover', url: 'https://cover-fixture.invalid/chosen.svg', width: 1200, height: 600, votes: 9, sexual: 'safe' }
  card.props.game.coverChoice = { workId: 'fixture-work', cover: personal }
  await nextTick()
  assert.equal(card.find('cover-foreground')!.props!.src, personal.url)
  assert.equal(card.find('card-image')!.props!['aria-busy'], true)
  assert.equal(card.find('cover-backdrop'), undefined)
  card.find('cover-foreground')!.props!.onLoad({ currentTarget: { naturalWidth: 1200, naturalHeight: 600 } })
  assert.equal(card.find('cover-backdrop')!.props!.src, personal.url)
  card.props.game.coverChoice = undefined
  await nextTick()
  assert.equal(card.find('cover-foreground')!.props!.src, card.props.game.metadata.coverUrl)
  assert.equal(card.find('cover-backdrop'), undefined)
  card.props.game.coverChoice = { workId: 'fixture-work', cover: personal }
  await nextTick()
  card.find('cover-foreground')!.props!.onLoad({ currentTarget: { naturalWidth: 1200, naturalHeight: 600 } })
  card.props.game.metadata.id = 'reassociated-work'
  await nextTick()
  assert.equal(card.find('cover-foreground')!.props!.src, card.props.game.metadata.coverUrl)
  assert.equal(card.find('cover-backdrop'), undefined)
  assert.equal(card.find('card-image')!.props!['aria-busy'], true)
})

test('saved NSFW filtering hides explicit personal and known cached default artwork in both layers', async () => {
  const entry = coveredGame()
  const explicit = { id: 'explicit-cover', url: 'https://cover-fixture.invalid/explicit.svg', width: 1200, height: 600, votes: 9, sexual: 'explicit' }
  const card = await mount({ game: { ...entry, coverChoice: { workId: 'fixture-work', cover: explicit } } })
  card.find('cover-foreground')!.props!.onLoad({ currentTarget: { naturalWidth: 1200, naturalHeight: 600 } })
  assert.ok(card.find('cover-backdrop'))
  card.props.includeNsfw = false
  await nextTick()
  assert.equal(card.find('cover-foreground')!.props!.src, entry.metadata.coverUrl)
  assert.equal(card.find('cover-backdrop'), undefined)
  card.props.game.metadata.coverUrl = explicit.url
  card.props.game.metadata.covers = [explicit]
  await nextTick()
  assert.equal(card.find('cover-foreground'), undefined)
  assert.equal(card.find('cover-backdrop'), undefined)
  assert.ok(card.find('cover-placeholder'))
  card.props.includeNsfw = true
  await nextTick()
  assert.equal(card.find('cover-foreground')!.props!.src, explicit.url)
  assert.equal(card.find('card-image')!.props!['aria-busy'], true)
})
