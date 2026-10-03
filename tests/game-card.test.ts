import assert from 'node:assert/strict'
import { test } from 'node:test'
import { promises as fs } from 'node:fs'
import { createRequire } from 'node:module'
import vm from 'node:vm'
import { parse, compileScript } from '@vue/compiler-sfc'
import { transform } from 'esbuild'
import { reactive, type VNode } from 'vue'

// Render the actual SFC's compiled template, including its real computed props
// and native button handlers. No duplicate implementation of launch readiness.
const require = createRequire(new URL('../src/components/GameCard.vue', import.meta.url))
const component = (async () => {
  const source = await fs.readFile(new URL('../src/components/GameCard.vue', import.meta.url), 'utf8')
  const { descriptor } = parse(source)
  const script = compileScript(descriptor, { id: 'game-card-regression', inlineTemplate: true })
  const compiled = await transform(script.content, { loader: 'ts', format: 'cjs', target: 'es2022' })
  const module = { exports: {} as any }
  vm.runInNewContext(compiled.code, { module, exports: module.exports, require: (id: string) => id.endsWith('.vue') ? { default: { name: 'OrderMark' } } : require(id) })
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
  const props = reactive({ selected: false, selectionMode: false, layout: 'grid', launchDisabled: false, ...input })
  const events: unknown[][] = [], emit = (...args: unknown[]) => events.push(args)
  const render = (await component).setup(props, { expose() {}, emit })
  const tree = () => render({ $emit: emit }, []) as VNode
  const find = (name: string) => nodes(tree()).find(node => hasClass(node, name))
  return { props, events, tree, find }
}

test('a different installation having an exe does not enable the missing default', async () => {
  const card = await mount({ game: game([installation('selected', null), installation('other', 'C:/synthetic-only/Game.exe')], 'selected') })
  assert.equal(card.find('card-launch'), undefined)
  const configure = card.find('card-configure')!
  assert.ok(configure); configure.props!.onClick()
  assert.deepEqual(card.events, [['configure']])
})

test('multiple installations require a valid default while a sole executable remains launchable', async () => {
  const multiple = await mount({ game: game([installation('one', 'C:/synthetic-only/A.exe'), installation('two', 'C:/synthetic-only/B.exe')]) })
  assert.equal(multiple.find('card-launch'), undefined)
  const single = await mount({ game: game([installation('one', 'C:/synthetic-only/A.exe')]) })
  assert.ok(single.find('card-launch')); assert.equal(single.find('card-configure'), undefined)
  single.find('card-launch')!.props!.onClick()
  assert.deepEqual(single.events, [['play']])
})

test('busy launch uses native disabled without hiding the action or enabling configuration', async () => {
  const card = await mount({ game: game([installation('one', 'C:/synthetic-only/A.exe')]), launchDisabled: true })
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
  const wishlist = await mount({ game: game([]) })
  assert.equal(nodeText(wishlist.find('card-availability')!), '未安装')
  assert.equal(nodeText(wishlist.find('card-configure')!), '添加本地游戏')
  wishlist.find('card-configure')!.props!.onClick()
  assert.deepEqual(wishlist.events, [['configure']])
  const local = await mount({ game: game([installation('one', 'C:/synthetic-only/Game.exe')]) })
  assert.equal(local.find('card-availability'), undefined)
  assert.equal(local.find('card-location'), undefined)
})

test('source rating is labeled when personal rating is absent, with no unrated or second score badge', async () => {
  const card = await mount({ game: { ...game([]), metadata: { ratings: [{source:'bangumi',score:8.5,scale:10}], developers:[], tags:[] } } })
  assert.ok(nodeText(card.find('card-rating-summary')!).includes('Bangumi'))
  assert.ok(nodeText(card.find('card-rating-summary')!).includes('8.5'))
  assert.equal(card.find('card-personal-rating'), undefined)
  assert.equal(card.find('card-rating'), undefined)
})
