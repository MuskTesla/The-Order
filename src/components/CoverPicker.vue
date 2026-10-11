<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import { KunButton } from '@kungal/ui-vue'
import { Check, FolderOpen, Image, LoaderCircle, RefreshCw, X } from 'lucide-vue-next'
import type { LibraryGame, NonPortraitCoverDisplay, WorkCover, WorkMetadata } from '../shared/types'
import { shouldBlurCover, shouldPreserveCover, workCoverUrl } from '../cover-display'

const props = defineProps<{
  work: WorkMetadata | null
  game: LibraryGame | null
  loading: boolean
  saving: boolean
  error: string
  includeNsfw: boolean
  globalDisplay?: NonPortraitCoverDisplay
}>()
const emit = defineEmits<{
  save: [coverId: string | null | undefined, display: NonPortraitCoverDisplay | null]
  pickLocal: [display: NonPortraitCoverDisplay | null]
  retry: []
  close: []
  dirty: [value: boolean]
}>()

const workId = computed(() => props.work?.id ?? props.game?.metadata?.id ?? '')
const identity = computed(() => `${props.game?.id ?? 'external'}:${workId.value}`)
const savedChoice = computed(() => props.game?.coverChoice?.workId === workId.value ? props.game.coverChoice : undefined)
const LOCAL_CHOICE = 'local-photo'
const savedId = computed(() => props.game?.localCover ? LOCAL_CHOICE : savedChoice.value?.cover.id ?? null)
const savedDisplay = computed(() => props.game?.nonPortraitCoverDisplay ?? 'global')
const choice = ref<string | null>(null)
const display = ref<NonPortraitCoverDisplay | 'global'>('global')
const effectiveDisplay = computed(() => display.value === 'global' ? props.globalDisplay ?? 'blur' : display.value)
const displayLabels: Record<NonPortraitCoverDisplay, string> = { blur: '完整显示＋柔化填边', plain: '完整显示＋纯色填边', crop: '铺满（裁切边缘）' }
const brokenUrls = ref(new Set<string>())
const previewRatio = ref(0)
const group = ref<HTMLElement | null>(null)
// A late detail response can add thumbnails without discarding an unfinished choice.
watch([() => identity.value, () => savedId.value, () => savedDisplay.value, () => props.game?.localCover?.dataUrl], () => {
  choice.value = savedId.value
  display.value = savedDisplay.value
}, { immediate: true, flush: 'sync' })
watch(identity, () => { brokenUrls.value = new Set() })

const knownExplicitUrls = computed(() => {
  const storedCovers = props.game?.metadata?.id === workId.value ? props.game.metadata.covers ?? [] : []
  const urls = new Set([...(props.work?.covers ?? []), ...storedCovers].filter(cover => cover.sexual === 'explicit').map(cover => cover.url))
  if (savedChoice.value?.cover.sexual === 'explicit') urls.add(savedChoice.value.cover.url)
  return urls
})
const covers = computed(() => (props.work?.covers ?? []).filter(cover => props.includeNsfw
  || cover.sexual !== 'explicit' && !knownExplicitUrls.value.has(cover.url)))
const defaultUrl = computed(() => {
  const defaultWork = props.game?.metadata?.id === workId.value ? props.game.metadata : props.work
  const url = workCoverUrl(defaultWork, props.includeNsfw)
  return !props.includeNsfw && url && knownExplicitUrls.value.has(url) ? null : url
})
type CoverOption = { id: string | null; cover: WorkCover | null; url: string | null; label: string }
const options = computed<CoverOption[]>(() => [
  ...(props.game?.localCover ? [{ id: LOCAL_CHOICE, cover: null, url: props.game.localCover.dataUrl, label: '当前本地图片' }] : []),
  { id: null, cover: covers.value.find(cover => cover.url === defaultUrl.value) ?? null, url: defaultUrl.value, label: workId.value ? '资料源默认封面' : '不使用封面' },
  ...covers.value.map((cover, index) => ({ id: cover.id, cover, url: cover.url, label: `封面 ${index + 1}` })),
])
const currentOption = computed(() => options.value.find(option => option.id === choice.value))
const unavailableChoice = computed(() => choice.value !== null && !currentOption.value)
const missingChoice = computed(() => choice.value !== null && !currentOption.value && choice.value === savedId.value)
const retainedCover = computed(() => savedChoice.value && (props.includeNsfw
  || savedChoice.value.cover.sexual !== 'explicit' && !knownExplicitUrls.value.has(savedChoice.value.cover.url)) ? savedChoice.value.cover : null)
const previewUrl = computed(() => currentOption.value?.url ?? (missingChoice.value ? retainedCover.value?.url ?? null : null))
const effectiveRatio = computed(() => {
  if (choice.value === LOCAL_CHOICE && props.game?.localCover) return previewRatio.value || props.game.localCover.width / props.game.localCover.height
  const cover = currentOption.value?.cover ?? (missingChoice.value ? retainedCover.value : null)
  return previewRatio.value || (cover?.width && cover.height ? cover.width / cover.height : 0)
})
const changed = computed(() => choice.value !== savedId.value || display.value !== savedDisplay.value)
// The detail owns leave protection; report drafts immediately, including resets
// after a confirmed save or switching to another game.
watch(changed, value => emit('dirty', value), { immediate: true, flush: 'sync' })
const canSave = computed(() => !!props.game && changed.value && !props.saving
  && (choice.value === savedId.value || !!currentOption.value))
const previewPreserve = computed(() => shouldPreserveCover(effectiveRatio.value, 3 / 4, effectiveDisplay.value))
const previewBlur = computed(() => shouldBlurCover(effectiveRatio.value, 3 / 4, effectiveDisplay.value))
watch(previewUrl, () => { previewRatio.value = 0 })

function failed(url: string | null) {
  if (url) brokenUrls.value = new Set([...brokenUrls.value, url])
}
function retry() { brokenUrls.value = new Set(); previewRatio.value = 0; emit('retry') }
function previewLoaded(event: Event) {
  const image = event.currentTarget as HTMLImageElement
  previewRatio.value = image.naturalHeight > 0 ? image.naturalWidth / image.naturalHeight : 0
}
function choose(id: string | null) { if (!props.saving) choice.value = id }
async function moveChoice(event: KeyboardEvent, index: number) {
  let target: number
  if (event.key === 'ArrowRight' || event.key === 'ArrowDown') target = (index + 1) % options.value.length
  else if (event.key === 'ArrowLeft' || event.key === 'ArrowUp') target = (index + options.value.length - 1) % options.value.length
  else if (event.key === 'Home') target = 0
  else if (event.key === 'End') target = options.value.length - 1
  else return
  event.preventDefault()
  if (props.saving) return
  choose(options.value[target]!.id)
  await nextTick()
  group.value?.querySelectorAll<HTMLButtonElement>('[role="radio"]')[target]?.focus()
}
function save() {
  if (canSave.value) emit('save', choice.value === savedId.value ? undefined : choice.value, display.value === 'global' ? null : display.value)
}
function pickLocal() {
  if (props.game && !props.saving) emit('pickLocal', display.value === 'global' ? null : display.value)
}
function dimensions(cover: WorkCover | null) {
  if (!cover?.width || !cover.height) return '尺寸未收录'
  return `${cover.width < cover.height ? '竖版' : cover.width === cover.height ? '方形' : '横版'} · ${cover.width} × ${cover.height}`
}
</script>

<template>
  <section class="cover-picker" aria-label="选择封面" :aria-busy="loading || saving">
    <div class="picker-heading"><div><h3>选择封面</h3><p>只改变你的书架，之后仍可再次更换。<template v-if="workId">默认封面会随“刷新资料”更新。</template></p></div><button type="button" class="picker-close" aria-label="收起封面选择" :disabled="saving" @click="emit('close')"><X :size="18" /></button></div>
    <p v-if="!game" class="picker-notice">加入愿望单后，可以为这部作品保存自己的封面与展示方式。</p>
    <div class="picker-local-row"><KunButton class="picker-local-action" size="sm" variant="bordered" :aria-label="game?.localCover ? '更换本地图片' : '选择本地图片'" :disabled="saving || !game" @click="pickLocal"><FolderOpen :size="15" />{{ game?.localCover ? '更换本地图片…' : '选择本地图片…' }}</KunButton><span>PNG / JPG，保存压缩副本，不会上传。选择后立即应用当前展示方式。</span></div>
    <div class="picker-preview-row">
      <div class="picker-preview" :class="{ 'preserve-preview': previewPreserve }" aria-label="封面展示预览">
        <template v-if="previewUrl && !brokenUrls.has(previewUrl)"><img class="preview-art" :src="previewUrl" alt="所选封面预览" referrerpolicy="no-referrer" @load="previewLoaded" @error="failed(previewUrl)" /><img v-if="previewBlur" class="preview-backdrop" :src="previewUrl" alt="" aria-hidden="true" referrerpolicy="no-referrer" /></template>
        <span v-else class="picker-image-empty"><Image :size="26" /><small>{{ previewUrl ? '封面暂不可用' : '暂无封面' }}</small></span>
      </div>
      <div class="picker-display"><strong>{{ missingChoice ? '当前手选封面' : currentOption?.label ?? '请选择封面' }}</strong><span class="picker-preview-note">书架展示预览</span><label>封面展示<select v-model="display" :disabled="saving || !game" aria-label="封面展示"><option value="global">跟随全局（{{ displayLabels[globalDisplay ?? 'blur'] }}）</option><option value="blur">完整显示＋柔化填边</option><option value="plain">完整显示＋纯色填边</option><option value="crop">铺满（裁切边缘）</option></select></label><p>可跟随全局，也可只为这部游戏指定。铺满适用于所有比例，包括长竖图；完整显示模式保留特殊比例封面的全图，常规竖图自动适配。</p></div>
    </div>
    <p v-if="missingChoice" class="picker-notice">当前手选封面不在这次返回的列表中。可以保留它并修改展示方式，也可以恢复默认或选择下面的其他封面。</p>
    <p v-else-if="unavailableChoice" class="picker-notice" role="status">刚才选择的封面已不在可选列表中，请重新选择。尚未保存的展示方式会保留。</p>
    <div v-if="error" class="picker-error" role="alert"><span>{{ error }}</span><KunButton size="sm" variant="bordered" :disabled="loading || saving" @click="retry"><RefreshCw :size="14" /> 重试</KunButton></div>
    <p v-else-if="loading" class="picker-load" role="status"><LoaderCircle :size="16" class="spin" /> 正在读取可选封面…</p>
    <p v-else-if="!workId" class="picker-notice">尚未关联作品，可以先使用本地图片。关联作品后仍可更换封面。</p>
    <p v-else-if="work?.covers === undefined" class="picker-notice">可选封面尚未读取。可以先保留当前封面或恢复默认。<button type="button" class="picker-inline-action" :disabled="saving" @click="retry">读取可选封面</button></p>
    <p v-else-if="!covers.length" class="picker-notice">资料源暂无其他符合当前分级设置的封面，仍可使用默认封面。</p>
    <div ref="group" class="picker-options" role="radiogroup" aria-label="作品封面">
      <button v-for="(option, index) in options" :key="option.id ?? 'default'" type="button" class="picker-option" :class="{ selected: choice === option.id }" role="radio" :aria-checked="choice === option.id" :aria-label="option.label" :tabindex="choice === option.id || (unavailableChoice && index === 0) ? 0 : -1" :disabled="saving" @click="choose(option.id)" @keydown="moveChoice($event, index)">
        <span class="picker-thumb"><img v-if="option.url && !brokenUrls.has(option.url)" :src="option.url" alt="" loading="lazy" referrerpolicy="no-referrer" @error="failed(option.url)" /><span v-else class="picker-image-empty"><Image :size="20" /><small>{{ option.url ? '加载失败' : '暂无封面' }}</small></span><span v-if="choice === option.id" class="picker-check" aria-hidden="true"><Check :size="13" /></span></span>
        <strong>{{ option.label }}</strong><small v-if="option.id === LOCAL_CHOICE && game?.localCover">{{ game.localCover.width }} × {{ game.localCover.height }}</small><small v-else-if="option.id !== null">{{ dimensions(option.cover) }}</small><small v-if="option.id !== null && option.cover?.votes != null">{{ option.cover.votes }} 票</small><span v-if="savedId === option.id" class="picker-saved">当前使用</span>
      </button>
    </div>
    <div class="picker-footer"><span>{{ !game ? '加入书架后即可保存。' : changed ? '修改将在保存后应用到书架。' : '当前选择已保存。' }}</span><KunButton class="picker-save" size="sm" color="primary" :loading="saving" :disabled="!canSave" @click="save"><Check :size="14" /> 保存封面设置</KunButton></div>
  </section>
</template>

<style scoped>
.cover-picker { margin:0 0 22px; padding:18px; border:1px solid var(--line); border-radius:12px; background:var(--surface); color:var(--ink); }
.picker-heading { display:flex; align-items:flex-start; gap:14px; justify-content:space-between; margin-bottom:16px; }
.picker-heading h3 { margin:0; font-size:16px; line-height:1.5; }
.picker-heading p,.picker-display p { margin:5px 0 0; font-size:12px; line-height:1.7; color:var(--muted); }
.picker-local-row { display:flex; align-items:center; flex-wrap:wrap; gap:10px; margin:0 0 16px; }
.picker-local-row>span { font-size:11px; color:var(--muted); line-height:1.7; flex:1; min-width:170px; }
.picker-close { display:grid; place-items:center; padding:6px; border-radius:6px; color:var(--muted); flex-shrink:0; }
.picker-close:hover { background:var(--hover); }
.picker-preview-row { display:flex; align-items:flex-start; gap:18px; margin-bottom:16px; }
.picker-preview { width:120px; aspect-ratio:3/4; flex-shrink:0; position:relative; overflow:hidden; isolation:isolate; border:1px solid var(--line); border-radius:8px; background:var(--app-bg); }
.picker-preview img { position:absolute; inset:0; width:100%; height:100%; }
.preview-art { object-fit:cover; object-position:center; z-index:1; }
.preserve-preview .preview-art { object-fit:contain; }
.preview-backdrop { object-fit:cover; filter:blur(12px) saturate(.8); opacity:.45; transform:scale(1.12); pointer-events:none; z-index:0; }
.picker-display { min-width:0; flex:1; padding-top:2px; }
.picker-display>strong { display:block; font-size:14px; overflow-wrap:anywhere; }
.picker-preview-note { display:block; font-size:11px; color:var(--muted); margin:4px 0 13px; }
.picker-display label { display:flex; flex-direction:column; gap:7px; max-width:320px; font-size:12px; font-weight:600; }
.picker-display select { min-width:0; width:100%; padding:8px 9px; border:1px solid var(--line); border-radius:7px; font-size:12px; font-weight:400; }
.picker-image-empty { display:flex; align-items:center; justify-content:center; flex-direction:column; gap:8px; width:100%; height:100%; color:var(--muted); }
.picker-image-empty small { font-size:11px; }
.picker-notice { margin:0 0 14px; color:var(--muted); font-size:12px; line-height:1.7; }
.picker-inline-action { margin-left:8px; color:var(--blue); text-decoration:underline; text-underline-offset:3px; }
.picker-error { display:flex; align-items:center; justify-content:space-between; gap:12px; margin-bottom:14px; padding:10px 12px; border:1px solid var(--line); border-radius:7px; font-size:12px; line-height:1.6; }
.picker-error>span { min-width:0; overflow-wrap:anywhere; }
.picker-load { display:flex; gap:8px; align-items:center; font-size:12px; color:var(--muted); margin:0 0 14px; }
.picker-options { display:grid; grid-template-columns:repeat(auto-fill,minmax(94px,1fr)); align-items:start; gap:10px; max-height:310px; overflow:auto; padding:4px; margin:0 -4px; }
.picker-option { min-width:0; display:flex; flex-direction:column; gap:4px; padding:6px; border:1px solid var(--line); background:var(--surface); color:var(--ink); border-radius:8px; text-align:left; }
.picker-option:hover { background:var(--hover); }
.picker-option.selected { border-color:var(--blue); box-shadow:0 0 0 1px var(--blue); background:var(--blue-soft); }
.picker-thumb { width:100%; aspect-ratio:3/4; position:relative; display:block; overflow:hidden; background:var(--app-bg); border-radius:4px; }
.picker-thumb>img { width:100%; height:100%; object-fit:contain; }
.picker-option>strong { font-size:11px; font-weight:600; line-height:1.5; overflow-wrap:anywhere; }
.picker-option>small { color:var(--muted); font-size:10px; line-height:1.4; overflow-wrap:anywhere; }
.picker-check { position:absolute; right:5px; bottom:5px; display:grid; place-items:center; width:21px; height:21px; border-radius:50%; background:var(--blue); color:var(--surface); }
.picker-saved { color:var(--blue); font-size:10px; line-height:1.4; }
.picker-footer { display:flex; align-items:center; justify-content:space-between; flex-wrap:wrap; gap:10px; margin-top:16px; padding-top:14px; border-top:1px solid var(--line); }
.picker-footer>span { font-size:11px; color:var(--muted); }
@media(max-width:600px) { .cover-picker { padding:13px; }.picker-preview-row { gap:12px; }.picker-preview { width:88px; }.picker-display label { font-size:11px; }.picker-display select { font-size:11px; }.picker-heading p,.picker-display p { font-size:11px; }.picker-options { grid-template-columns:repeat(auto-fill,minmax(80px,1fr)); }.picker-error { align-items:flex-start; flex-direction:column; } }
</style>
