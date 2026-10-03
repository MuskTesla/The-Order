<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { BookOpen } from 'lucide-vue-next'
import type { NonPortraitCoverDisplay } from '../shared/types'
import { shouldBlurCover, shouldPreserveCover } from '../cover-display'

const props = withDefaults(defineProps<{ url: string | null; title: string; display?: NonPortraitCoverDisplay }>(), { display: 'blur' })
const frame = ref<HTMLElement | null>(null)
const ratio = ref(0), frameRatio = ref(3 / 4), loaded = ref(false), broken = ref(false)
let observer: ResizeObserver | undefined
watch(frame, element => {
  observer?.disconnect()
  if (!element) return
  const measure = () => { const rect = element.getBoundingClientRect(); if (rect.width > 0 && rect.height > 0) frameRatio.value = rect.width / rect.height }
  measure(); observer = new ResizeObserver(measure); observer.observe(element)
})
onBeforeUnmount(() => observer?.disconnect())
watch(() => props.url, () => { ratio.value = 0; loaded.value = false; broken.value = false })
const preserve = computed(() => loaded.value && !broken.value && shouldPreserveCover(ratio.value, frameRatio.value, props.display))
const blur = computed(() => loaded.value && !broken.value && shouldBlurCover(ratio.value, frameRatio.value, props.display))
function imageLoaded(event: Event) {
  const image = event.currentTarget as HTMLImageElement
  ratio.value = image.naturalHeight > 0 ? image.naturalWidth / image.naturalHeight : 0
  loaded.value = true
}
</script>

<template>
  <div ref="frame" class="detail-cover detail-artwork" :class="{ 'preserve-cover': preserve }" :aria-busy="!!url && !loaded && !broken">
    <img v-if="url && !broken" class="detail-foreground" :src="url" :alt="title" referrerpolicy="no-referrer" @load="imageLoaded" @error="broken = true" :class="{ loading: !loaded }" />
    <img v-if="blur && url" class="detail-backdrop" :src="url" alt="" aria-hidden="true" referrerpolicy="no-referrer" />
    <span v-if="!url || broken" class="detail-art-placeholder"><BookOpen :size="36" /><small>{{ broken ? '封面暂不可用' : '暂无封面' }}</small></span>
    <span v-else-if="!loaded" class="detail-art-placeholder" role="status">封面加载中</span>
  </div>
</template>

<style scoped>
.detail-artwork{position:relative;overflow:hidden;isolation:isolate;background:var(--hover);border:1px solid var(--line)}
.detail-artwork>img{position:absolute;inset:0;width:100%;height:100%;object-position:center}
.detail-foreground{object-fit:cover;z-index:1;transition:opacity .15s}.detail-foreground.loading{opacity:0}
.preserve-cover>.detail-foreground{object-fit:contain}
.detail-artwork>.detail-backdrop{object-fit:cover;filter:blur(12px) saturate(.8);transform:scale(1.12);opacity:.45;pointer-events:none;z-index:0}
.detail-art-placeholder{position:absolute;inset:0;display:flex;flex-direction:column;align-items:center;justify-content:center;gap:12px;color:var(--muted);font-size:12px}
.detail-art-placeholder small{font-size:12px}
</style>
