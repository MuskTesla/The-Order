<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { Heart, Star, Play, HardDrive, Bookmark, Check, Clock3, FolderPlus, ArrowUpRight } from 'lucide-vue-next'
import type { LibraryGame, WorkMetadata } from '../shared/types'
import OrderMark from './OrderMark.vue'

const props = withDefaults(defineProps<{
  game?: LibraryGame
  work?: WorkMetadata
  selected?: boolean
  selectionMode?: boolean
  layout?: 'grid' | 'list'
  launchDisabled?: boolean
}>(), { selected: false, selectionMode: false, layout: 'grid', launchDisabled: false })
defineEmits<{ open: []; favorite: []; play: []; configure: []; toggleSelected: [] }>()

const metadata = computed(() => props.work ?? props.game?.metadata)
const title = computed(() => props.game?.name ?? metadata.value?.title ?? '未命名作品')
const rating = computed(() => metadata.value?.ratings.find(item => item.source === 'bangumi') ?? metadata.value?.ratings[0])
const local = computed(() => !!props.game?.installations.length)
// Resolve the same default as the launcher; another installation's exe is not a fallback.
const defaultInstallation = computed(() => props.game?.installations.find(item => item.id === props.game?.preferredInstallationId)
  ?? (props.game?.installations.length === 1 ? props.game.installations[0] : undefined))
const canLaunch = computed(() => !!defaultInstallation.value?.executablePath)
const developerLine = computed(() => metadata.value?.developers.join(' · ') || (props.game && !metadata.value ? '本地收藏 · 待补全资料' : '制作方暂未收录'))
const playtime = computed(() => {
  const seconds = props.game?.totalPlaySeconds ?? 0
  if (!seconds) return '尚未开始'
  if (seconds < 60) return '不足 1 分钟'
  if (seconds < 3600) return `${Math.floor(seconds / 60)} 分钟`
  return `${(seconds / 3600).toFixed(1)} 小时`
})
const personalRating = computed(() => props.game?.personalRating == null ? null : props.game.personalRating.toFixed(1))
const sourceLabel = (source: string) => ({ bangumi: 'Bangumi', vndb: 'VNDB', erogamescape: '批评空间', dlsite: 'DLsite' }[source] ?? source)
const broken = ref(false)
const loaded = ref(false)
watch(() => metadata.value?.coverUrl, () => { broken.value = false; loaded.value = false })
const labels = { planned: '想玩', playing: '在玩', completed: '已完成', paused: '搁置', dropped: '放弃' }
</script>

<template>
  <article :class="['game-card', { 'library-card': !!game, 'card-list': layout === 'list', 'is-selected': selected, 'selection-mode': selectionMode }]">
    <button v-if="game && selectionMode" type="button" class="card-select" role="checkbox" :aria-checked="selected" :aria-label="`${selected ? '取消选择' : '选择'} ${title}`" @click="$emit('toggleSelected')"><Check v-if="selected" :size="16" :stroke-width="2.8" /></button>
    <button type="button" class="card-image" :aria-label="`查看 ${title}`" @click="$emit('open')">
      <img v-if="metadata?.coverUrl && !broken" :src="metadata.coverUrl" :alt="title" loading="lazy" referrerpolicy="no-referrer" @load="loaded = true" @error="broken = true" :class="{ 'cover-loading': !loaded }" />
      <div v-else class="cover-placeholder"><span class="cover-placeholder-mark"><OrderMark /></span><strong>{{ title }}</strong><span>{{ broken ? '封面暂不可用' : '等待一段故事' }}</span><i aria-hidden="true"></i></div>
      <span v-if="metadata?.coverUrl && !broken && !loaded" class="cover-loading-label" role="status">封面加载中</span>
      <span v-if="rating" class="card-rating" :title="`${sourceLabel(rating.source)} · ${rating.score.toFixed(1)} / ${rating.scale}`"><Star :size="12" fill="currentColor" />{{ rating.score.toFixed(1) }}<small>/{{ rating.scale }}</small><span>{{ sourceLabel(rating.source) }}</span></span>
      <span class="card-open">作品详情 <ArrowUpRight :size="13" /></span>
    </button>
    <button v-if="game" type="button" class="favorite-button" :class="{ active: game.favorite }" :aria-label="`${game.favorite ? '取消收藏' : '收藏游戏'} ${title}`" :aria-pressed="game.favorite" @click="$emit('favorite')"><Heart :size="16" :fill="game.favorite ? 'currentColor' : 'none'" /></button>
    <div class="card-copy">
      <div v-if="game" class="card-meta"><span class="card-status" :class="game.status"><span></span>{{ labels[game.status] }}</span><span class="card-personal-rating" :class="{ unrated: personalRating === null }" :title="personalRating === null ? '还没有填写个人评分' : `我的评分 ${personalRating} / 10`"><Star :size="12" :fill="personalRating === null ? 'none' : 'currentColor'" />{{ personalRating ?? '未评分' }}<small v-if="personalRating !== null">我的</small></span></div>
      <button type="button" class="card-title" :title="title" @click="$emit('open')">{{ title }}</button>
      <p :title="developerLine">{{ developerLine }}</p>
      <div v-if="game" class="card-progress"><span><Clock3 :size="12" />{{ playtime }}</span><span v-if="game.playCount">{{ game.playCount }} 次游玩</span></div>
      <div class="card-bottom">
        <span v-if="game" class="card-location" :title="local ? `${game.installations.length} 处本地安装` : '尚未添加本地安装'"><HardDrive v-if="local" :size="13" /><Bookmark v-else :size="13" />{{ local ? '本地' : '想玩清单' }}</span>
        <span v-else class="card-location">{{ metadata?.releaseDate?.slice(0, 4) || '发售日未知' }}</span>
        <button v-if="game && canLaunch" type="button" class="card-launch" :disabled="launchDisabled" :aria-label="`启动 ${title}`" :title="launchDisabled ? '已有游戏正在启动或计时' : `启动 ${title}`" @click="$emit('play')"><Play :size="13" fill="currentColor" /> 启动</button>
        <button v-else-if="game" type="button" class="card-configure" :aria-label="`为 ${title} 选择启动程序`" @click="$emit('configure')"><FolderPlus :size="13" />选择启动程序</button>
        <span v-else class="card-tag">{{ metadata?.tags[0] || 'Galgame' }}</span>
      </div>
    </div>
  </article>
</template>
