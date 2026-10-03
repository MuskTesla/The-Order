import assert from 'node:assert/strict'
import { test } from 'node:test'
import { promises as fs } from 'node:fs'
import { createRequire } from 'node:module'
import vm from 'node:vm'
import { parse, compileScript } from '@vue/compiler-sfc'
import { transform } from 'esbuild'
import { nextTick, reactive, type VNode } from 'vue'
import type { LibraryGame, WorkCover, WorkMetadata } from '../src/shared/types'

const require = createRequire(new URL('../src/components/CoverPicker.vue', import.meta.url))
const component = (async () => {
  const source = await fs.readFile(new URL('../src/components/CoverPicker.vue', import.meta.url), 'utf8')
  const { descriptor } = parse(source)
  const script = compileScript(descriptor, { id: 'cover-picker-regression', inlineTemplate: true })
  const compiled = await transform(script.content, { loader: 'ts', format: 'cjs', target: 'es2022' })
  const module = { exports: {} as any }
  // Exercise the real SFC's drafts, markup and handlers. Native field events
  // are invoked directly; DOM focus and image rendering are desktop checks.
  vm.runInNewContext(compiled.code, { module, exports: module.exports, require: (id: string) => id === '@kungal/ui-vue'
    ? { KunButton: { name: 'KunButton' } }
    : id === 'vue' ? { ...require(id), withDirectives: (node: VNode) => node } : require(id) })
  return module.exports.default
})()
function nodes(root: VNode): VNode[] {
  return [root, ...(Array.isArray(root.children) ? root.children.flatMap(child => child && typeof child === 'object' ? nodes(child as VNode) : []) : [])]
}
function hasClass(node: VNode, name: string) { return String(node.props?.class ?? '').split(/\s+/).includes(name) }
function text(node: VNode): string { return Array.isArray(node.children) ? node.children.map(child => child && typeof child === 'object' ? text(child as VNode) : String(child ?? '')).join('') : String(node.children ?? '') }
const cover = (id: string, width = 600, height = 900, votes = 0): WorkCover => ({ id, url: `https://cover-fixture.invalid/${id}.svg`, width, height, votes, sexual: 'safe' })
const work = (covers: WorkCover[] | undefined = [cover('1'), cover('2', 1200, 600, 99)]): WorkMetadata => ({
  id: '84', title: 'Synthetic cover selection', originalTitle: '', aliases: [], coverUrl: 'https://cover-fixture.invalid/1.svg',
  covers, bannerUrl: null, description: '', releaseDate: null, releaseDatePrecision: null, developers: [], tags: [], ratings: [], characters: [], references: [], webUrl: null, contentRating: null, fetchedAt: '2026-10-03T00:00:00.000Z',
})
const game = (metadata = work()): LibraryGame => ({ id: 'synthetic', name: 'Synthetic cover selection', metadata, installations: [], preferredInstallationId: null, status: 'planned', personalRating: null, favorite: false, categories: [], customTags: [], notes: '', totalPlaySeconds: 0, playCount: 0, sessions: [], addedAt: '2026-10-03T00:00:00.000Z', lastPlayedAt: null })
async function mount(input: Record<string, unknown> = {}) {
  const props = reactive({ game: game() as LibraryGame | null, work: work() as WorkMetadata | null, loading: false, saving: false, error: '', includeNsfw: false, ...input })
  const events: unknown[][] = [], emit = (...args: unknown[]) => events.push(args)
  const render = (await component).setup(props, { expose() {}, emit })
  const tree = () => render({ $emit: emit }, []) as VNode
  const find = (name: string) => nodes(tree()).find(node => hasClass(node, name))!
  const radios = () => nodes(tree()).filter(node => node.props?.role === 'radio')
  const setDisplay = (value: string) => nodes(tree()).find(node => node.type === 'select')!.props!['onUpdate:modelValue'](value)
  return { props, events, tree, find, radios, setDisplay }
}

test('the initial selection is the source default even when another image has more votes', async () => {
  const picker = await mount()
  assert.equal(picker.radios()[0]!.props!['aria-checked'], true)
  assert.equal(picker.radios()[2]!.props!['aria-checked'], false)
  assert.equal(picker.find('picker-save').props!.disabled, true)
  picker.radios()[2]!.props!.onClick()
  picker.find('picker-save').props!.onClick()
  assert.deepEqual(picker.events, [['save', '2', 'blur']])
})

test('display-only edits explicitly preserve the current cover; restoring default is a distinct action', async () => {
  const personal = { ...game(), coverChoice: { workId: '84', cover: cover('2') } }
  const picker = await mount({ game: personal })
  picker.setDisplay('plain')
  picker.find('picker-save').props!.onClick()
  assert.deepEqual(picker.events, [['save', undefined, 'plain']])
  picker.radios()[0]!.props!.onClick()
  picker.find('picker-save').props!.onClick()
  assert.deepEqual(picker.events[1], ['save', null, 'plain'])
})

test('late details do not discard the draft and removed draft candidates cannot be saved', async () => {
  const picker = await mount()
  picker.radios()[2]!.props!.onClick()
  picker.setDisplay('crop')
  picker.props.work = work([cover('2', 1200, 600), cover('3')])
  await nextTick()
  assert.equal(picker.radios()[1]!.props!['aria-checked'], true)
  picker.find('picker-save').props!.onClick()
  assert.deepEqual(picker.events, [['save', '2', 'crop']])
  picker.props.work = work([cover('3')]); await nextTick()
  assert.equal(picker.find('picker-save').props!.disabled, true)
  assert.equal(picker.radios()[0]!.props!.tabindex, 0)
  assert.match(text(picker.tree()), /刚才选择的封面已不在可选列表/)
})

test('a saved cover missing from the new list can be retained but is not submitted as a new candidate', async () => {
  const personal = { ...game(), coverChoice: { workId: '84', cover: cover('77', 900, 600) } }
  const picker = await mount({ game: personal })
  assert.equal(picker.radios().length, 3)
  assert.equal(picker.radios().some(node => node.props!['aria-checked']), false)
  assert.equal(picker.find('preview-art').props!.src, 'https://cover-fixture.invalid/77.svg')
  picker.setDisplay('crop'); picker.find('picker-save').props!.onClick()
  assert.deepEqual(picker.events, [['save', undefined, 'crop']])
})

test('game identity and saved selection changes reset drafts without a deep work watcher', async () => {
  const picker = await mount()
  picker.radios()[2]!.props!.onClick(); picker.setDisplay('plain')
  picker.props.game = { ...game(), id: 'different', nonPortraitCoverDisplay: 'crop' }; await nextTick()
  assert.equal(picker.radios()[0]!.props!['aria-checked'], true)
  assert.equal(picker.find('picker-save').props!.disabled, true)
  picker.props.game = { ...game(), id: 'different', nonPortraitCoverDisplay: 'crop', coverChoice: { workId: '84', cover: cover('2') } }; await nextTick()
  assert.equal(picker.radios()[2]!.props!['aria-checked'], true)
  assert.equal(picker.find('picker-save').props!.disabled, true)
})

test('radio keyboard navigation wraps and supports Home and End with one tab stop', async () => {
  const picker = await mount()
  let prevented = 0
  await picker.radios()[0]!.props!.onKeydown({ key: 'ArrowLeft', preventDefault() { prevented++ } })
  assert.equal(picker.radios()[2]!.props!['aria-checked'], true)
  assert.equal(picker.radios().filter(node => node.props!.tabindex === 0).length, 1)
  await picker.radios()[2]!.props!.onKeydown({ key: 'Home', preventDefault() { prevented++ } })
  assert.equal(picker.radios()[0]!.props!['aria-checked'], true)
  await picker.radios()[0]!.props!.onKeydown({ key: 'End', preventDefault() { prevented++ } })
  assert.equal(picker.radios()[2]!.props!['aria-checked'], true)
  assert.equal(prevented, 3)
})

test('explicit thumbnails are excluded and saving is unavailable during loading or for external works', async () => {
  const explicit = { ...cover('3'), sexual: 'explicit' as const }
  const picker = await mount({ work: work([cover('1'), explicit]) })
  assert.equal(picker.radios().length, 2)
  assert.equal(nodes(picker.tree()).some(node => node.props?.src === explicit.url), false)
  picker.setDisplay('plain'); picker.props.loading = true
  assert.equal(picker.find('picker-save').props!.disabled, true)
  picker.find('picker-save').props!.onClick(); assert.deepEqual(picker.events, [])
  picker.props.loading = false; picker.props.game = null; await nextTick()
  assert.equal(picker.find('picker-save').props!.disabled, true)
  assert.match(text(picker.tree()), /加入愿望单后/)
})

test('preview follows the selected nonportrait presentation and image failures remain visible', async () => {
  const picker = await mount()
  picker.radios()[2]!.props!.onClick()
  assert.ok(picker.find('preview-backdrop'))
  picker.setDisplay('plain')
  assert.equal(picker.find('preview-backdrop'), undefined)
  assert.ok(hasClass(picker.find('picker-preview'), 'preserve-preview'))
  picker.setDisplay('crop')
  assert.equal(hasClass(picker.find('picker-preview'), 'preserve-preview'), false)
  picker.find('preview-art').props!.onError()
  assert.equal(picker.find('preview-art'), undefined)
  assert.match(text(picker.tree()), /封面暂不可用/)
})

test('fresh explicit classification hides a retained selection whose saved classification was safe or unknown', async () => {
  const stale = { ...cover('77', 900, 600), sexual: null }
  const latest = { ...stale, sexual: 'explicit' as const }
  const personal = { ...game(), coverChoice: { workId: '84', cover: stale } }
  const picker = await mount({ game: personal, work: work([cover('1'), latest]), includeNsfw: false })
  assert.equal(picker.radios().length, 2)
  assert.equal(nodes(picker.tree()).some(node => node.props?.src === stale.url), false)
  assert.equal(picker.find('preview-art'), undefined)
  picker.props.includeNsfw = true; await nextTick()
  assert.equal(picker.find('preview-art').props!.src, stale.url)
  picker.props.includeNsfw = false; await nextTick()
  assert.equal(picker.find('preview-art'), undefined)
})

test('a saved explicit image is hidden when it is also the default without a fresh classification list', async () => {
  const explicit = { ...cover('1'), sexual: 'explicit' as const }
  const personal = { ...game(), coverChoice: { workId: '84', cover: explicit } }
  const picker = await mount({ game: personal, work: { ...work(), covers: undefined }, includeNsfw: false })
  assert.equal(nodes(picker.tree()).some(node => node.props?.src === explicit.url), false)
  picker.radios()[0]!.props!.onClick()
  assert.equal(picker.find('preview-art'), undefined)
})

test('default preview agrees with the stored source default when the fresh catalogue elected another image', async () => {
  const stored = { ...game(), coverChoice: { workId: '84', cover: cover('77') } }
  const latest = { ...work(), coverUrl: cover('2').url }
  const picker = await mount({ game: stored, work: latest })
  picker.radios()[0]!.props!.onClick()
  assert.equal(picker.find('preview-art').props!.src, stored.metadata!.coverUrl)
  assert.notEqual(picker.find('preview-art').props!.src, latest.coverUrl)
  assert.match(text(picker.tree()), /默认封面会随“刷新资料”更新/)
  picker.find('picker-save').props!.onClick()
  assert.deepEqual(picker.events, [['save', null, 'blur']])
})

test('a filtered fresh list cannot erase an explicit classification already present in this game metadata', async () => {
  const retained = { ...cover('77', 900, 600), sexual: null }
  const storedWork = work([cover('1'), { ...retained, sexual: 'explicit' }])
  const stored = { ...game(storedWork), coverChoice: { workId: '84', cover: retained } }
  const picker = await mount({ game: stored, work: work([cover('1')]), includeNsfw: false })
  assert.equal(nodes(picker.tree()).some(node => node.props?.src === retained.url), false)
  assert.equal(picker.find('preview-art'), undefined)
  picker.setDisplay('plain'); picker.find('picker-save').props!.onClick()
  assert.deepEqual(picker.events, [['save', undefined, 'plain']])
})
