<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { Heart, Star, Play, Check, Clock3, FolderPlus, ArrowUpRight } from 'lucide-vue-next'
import type { LibraryGame, NonPortraitCoverDisplay, WorkMetadata } from '../shared/types'
import { getLaunchInstallation } from '../library-insights'
import { gameCoverDisplay, gameCoverUrl, shouldBlurCover, shouldPreserveCover, workCoverUrl } from '../cover-display'
import OrderMark from './OrderMark.vue'

const props = withDefaults(defineProps<{
  game?: LibraryGame
  work?: WorkMetadata
  selected?: boolean
  selectionMode?: boolean
  layout?: 'grid' | 'list'
  launchDisabled?: boolean
  includeNsfw?: boolean
  globalDisplay?: NonPortraitCoverDisplay
}>(), { selected: false, selectionMode: false, layout: 'grid', launchDisabled: false, includeNsfw: true, globalDisplay: 'blur' })
defineEmits<{ open: []; favorite: []; play: []; configure: []; toggleSelected: [] }>()

const metadata = computed(() => props.work ?? props.game?.metadata)
const compactCard = computed(() => !!props.game && props.layout === 'grid')
const coverUrl = computed(() => props.game ? gameCoverUrl(props.game, props.includeNsfw) : workCoverUrl(props.work, props.includeNsfw))
const coverDisplay = computed(() => gameCoverDisplay(props.game, props.globalDisplay))
const title = computed(() => props.game?.name ?? metadata.value?.title ?? '未命名作品')
const rating = computed(() => metadata.value?.ratings.find(item => item.source === 'bangumi') ?? metadata.value?.ratings[0])
const local = computed(() => !!props.game?.installations.length)
const canLaunch = computed(() => !!props.game && !!getLaunchInstallation(props.game))
const developerLine = computed(() => metadata.value?.developers.join(' · ') || (props.game && !metadata.value ? '资料待补全' : '制作方暂未收录'))
const playtime = computed(() => {
  const seconds = props.game?.totalPlaySeconds ?? 0
  if (!seconds) return '尚未开始'
  if (seconds < 60) return '不足 1 分钟'
  if (seconds < 3600) return `${Math.floor(seconds / 60)} 分钟`
  return `${(seconds / 3600).toFixed(1)} 小时`
})
const personalRating = computed(() => props.game?.personalRating == null ? null : props.game.personalRating.toFixed(1))
const sourceLabel = (source: string) => ({ bangumi: 'Bangumi', vndb: 'VNDB', erogamescape: '批评空间', dlsite: 'DLsite' }[source] ?? source)
const displayedRating = computed(() => personalRating.value !== null
  ? { score: personalRating.value, scale: 10, label: '我的', personal: true }
  : rating.value ? { score: rating.value.score.toFixed(1), scale: rating.value.scale, label: sourceLabel(rating.value.source), personal: false } : null)
const broken = ref(false)
const loaded = ref(false)
const imageFrame = ref<HTMLButtonElement | null>(null)
const imageRatio = ref(0)
const frameRatio = ref(3 / 4)
const preserveCover = computed(() => loaded.value && !broken.value && imageRatio.value > 0
  && shouldPreserveCover(imageRatio.value, frameRatio.value, coverDisplay.value))
const blurCover = computed(() => preserveCover.value && shouldBlurCover(imageRatio.value, frameRatio.value, coverDisplay.value))
let frameObserver: ResizeObserver | undefined
onMounted(() => {
  const frame = imageFrame.value
  if (!frame) return
  const measure = () => {
    const { width, height } = frame.getBoundingClientRect()
    if (width > 0 && height > 0) frameRatio.value = width / height
  }
  measure()
  frameObserver = new ResizeObserver(measure)
  frameObserver.observe(frame)
})
onBeforeUnmount(() => frameObserver?.disconnect())
function coverLoaded(event: Event) {
  const image = event.currentTarget as HTMLImageElement
  imageRatio.value = image.naturalHeight > 0 ? image.naturalWidth / image.naturalHeight : 0
  loaded.value = true
}
watch(coverUrl, () => { broken.value = false; loaded.value = false; imageRatio.value = 0 })
const labels = { planned: '未开始', playing: '在玩', completed: '已完成', paused: '搁置', dropped: '放弃' }
</script>

<template>
  <article :class="['game-card', { 'library-card': !!game, 'compact-card': compactCard, 'card-list': layout === 'list', 'is-selected': selected, 'selection-mode': selectionMode }]">
    <button v-if="game && selectionMode" type="button" class="card-select" role="checkbox" :aria-checked="selected" :aria-label="`${selected ? '取消选择' : '选择'} ${title}`" @click="$emit('toggleSelected')"><Check v-if="selected" :size="16" :stroke-width="2.8" /></button>
    <button ref="imageFrame" type="button" class="card-image" :class="{ 'is-loading': coverUrl && !broken && !loaded, 'preserve-cover': preserveCover }" :aria-busy="!!coverUrl && !broken && !loaded" :aria-label="`查看 ${title}`" @click="$emit('open')">
      <img v-if="coverUrl && !broken" class="cover-foreground" :src="coverUrl" :alt="title" loading="lazy" referrerpolicy="no-referrer" @load="coverLoaded" @error="broken = true" :class="{ 'cover-loading': !loaded }" />
      <img v-if="blurCover && coverUrl" class="cover-backdrop" :src="coverUrl" alt="" aria-hidden="true" loading="lazy" referrerpolicy="no-referrer" />
      <div v-if="!coverUrl || broken" class="cover-placeholder"><span class="cover-placeholder-mark"><OrderMark /></span><strong>{{ title }}</strong><span>{{ broken ? '封面暂不可用' : '等待一段故事' }}</span><i aria-hidden="true"></i></div>
      <span v-if="coverUrl && !broken && !loaded" class="cover-loading-label" role="status">封面加载中</span>
      <span v-if="!compactCard" class="card-open">作品详情 <ArrowUpRight :size="13" /></span>
    </button>
    <button v-if="game && !compactCard" type="button" class="favorite-button" :class="{ active: game.favorite }" :aria-label="`${game.favorite ? '取消收藏' : '收藏游戏'} ${title}`" :aria-pressed="game.favorite" @click="$emit('favorite')"><Heart :size="16" :fill="game.favorite ? 'currentColor' : 'none'" /></button>
    <div class="card-copy">
      <div v-if="!compactCard && (game || displayedRating)" class="card-meta"><span v-if="game" class="card-status" :class="game.status"><span></span>{{ labels[game.status] }}</span><span v-if="displayedRating" class="card-rating-summary" :class="{ personal: displayedRating.personal }" :title="`${displayedRating.label}评分 ${displayedRating.score} / ${displayedRating.scale}`"><Star :size="12" fill="currentColor" /><strong>{{ displayedRating.score }}</strong><small>/{{ displayedRating.scale }}</small><span>{{ displayedRating.label }}</span></span></div>
      <button type="button" class="card-title" :title="title" @click="$emit('open')"><span v-if="compactCard" class="compact-title-text">{{ title }}</span><template v-else>{{ title }}</template></button>
      <p v-if="!compactCard" :title="developerLine">{{ developerLine }}</p>
      <div v-if="game && !compactCard" class="card-progress"><span><Clock3 :size="12" />{{ playtime }}</span><span v-if="game.playCount">{{ game.playCount }} 次游玩</span></div>
      <div v-if="!compactCard" class="card-bottom">
        <span v-if="game && (!local || !canLaunch)" class="card-location card-availability">{{ !local ? '未安装' : '待设程序' }}</span>
        <span v-if="!game" class="card-location">{{ metadata?.releaseDate?.slice(0, 4) || '发售日未知' }}</span>
        <button v-if="game && canLaunch" type="button" class="card-launch" :disabled="launchDisabled" :aria-label="`启动 ${title}`" :title="launchDisabled ? '已有游戏正在启动或计时' : `启动 ${title}`" @click="$emit('play')"><Play :size="13" fill="currentColor" /> 启动</button>
        <button v-else-if="game" type="button" class="card-configure" :aria-label="`${local ? '为' : '添加本地游戏：'} ${title}${local ? ' 设置启动程序' : ''}`" @click="$emit('configure')"><FolderPlus :size="13" />{{ local ? '设置启动程序' : '添加本地游戏' }}</button>
        <span v-else class="card-tag">{{ metadata?.tags[0] || 'Galgame' }}</span>
      </div>
    </div>
  </article>
</template>

<style scoped>
.game-card.library-card.compact-card {
  background:transparent;
  border:0;
  border-radius:0;
  box-shadow:none;
  overflow:visible;
}
.game-card.library-card.compact-card:hover {
  transform:none;
  box-shadow:none;
}
.game-card.library-card.compact-card .card-image {
  border:0;
  border-radius:10px;
}
.game-card.library-card.compact-card:hover .card-image>.cover-foreground {
  transform:none;
}
.game-card.library-card.compact-card .card-copy {
  display:block;
  flex:none;
  padding:8px 3px 0;
}
.game-card.library-card.compact-card .card-title {
  height:3em;
  display:flex;
  align-items:flex-start;
  font-size:14px;
  font-weight:500;
  line-height:1.5;
  color:var(--ink);
  text-wrap:wrap;
}
.compact-title-text {
  display:-webkit-box;
  -webkit-line-clamp:2;
  -webkit-box-orient:vertical;
  overflow:hidden;
  width:100%;
  text-align:left;
}
.game-card.library-card.compact-card .card-title:hover {
  color:var(--blue);
}
.game-card.library-card.compact-card.is-selected .card-image {
  outline:3px solid var(--blue);
  outline-offset:2px;
}
</style>
