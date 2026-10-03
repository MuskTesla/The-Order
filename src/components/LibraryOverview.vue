<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { ArrowUpRight, BookOpen, CheckCheck, Clock3, HardDrive, Play, Radio, Sparkles } from 'lucide-vue-next'
import type { ActiveSession, LibraryGame } from '../shared/types'
import { formatPlayTime, getContinueStory, getLibraryInsights } from '../library-insights'

const props = defineProps<{ games: LibraryGame[]; activeSession?: ActiveSession | null }>()
const emit = defineEmits<{ open: [game: LibraryGame]; play: [game: LibraryGame]; filter: [value: string] }>()
const story = computed(() => getContinueStory(props.games, props.activeSession))
const insights = computed(() => getLibraryInsights(props.games, props.activeSession))
const brokenCover = ref(false)
watch(() => story.value?.game.metadata?.coverUrl, () => { brokenCover.value = false })
function primaryAction() {
  const current = story.value
  if (!current) return
  if (current.canPlay) emit('play', current.game)
  else emit('open', current.game)
}
</script>

<template>
  <section class="library-overview" aria-label="书架概览">
    <div v-if="story" class="continue-story">
      <button class="story-cover" :aria-label="`查看 ${story.game.name}`" @click="emit('open', story.game)">
        <img v-if="story.game.metadata?.coverUrl && !brokenCover" :src="story.game.metadata.coverUrl" alt="" referrerpolicy="no-referrer" @error="brokenCover = true" />
        <BookOpen v-else :size="25" />
      </button>
      <div class="story-copy">
        <span class="section-kicker"><Radio v-if="story.reason === 'active'" :size="12" /><BookOpen v-else :size="12" />{{ story.reason === 'active' ? '故事进行中' : '继续故事' }}</span>
        <button class="story-title" :title="story.game.name" @click="emit('open', story.game)">{{ story.game.name }}</button>
        <p v-if="story.reason === 'active'">本次 {{ formatPlayTime(activeSession?.elapsedSeconds ?? 0) }} <span class="live-dot"></span> 正在计时</p>
        <p v-else>{{ story.game.totalPlaySeconds ? `已记录 ${formatPlayTime(story.game.totalPlaySeconds)}` : '从这里开启下一段故事' }}</p>
        <button class="continue-button" @click="primaryAction"><Play v-if="story.canPlay" :size="12" fill="currentColor" /><ArrowUpRight v-else :size="13" />{{ story.reason === 'active' ? '查看游戏' : story.canPlay ? '继续游玩' : '查看与设置' }}</button>
      </div>
    </div>
    <div v-else class="continue-story quiet-story">
      <span class="empty-book"><BookOpen :size="26" /></span>
      <div class="story-copy"><span class="section-kicker">下一段故事</span><h2>好故事，慢慢读</h2><p>将作品设为“在玩”，下次从这里继续。</p><button class="continue-button" @click="emit('filter', 'local')">浏览本地游戏 <ArrowUpRight :size="13" /></button></div>
    </div>
    <div class="overview-numbers">
      <div class="overview-number"><span><Clock3 :size="13" />本周已记录</span><strong class="duration-value">{{ formatPlayTime(insights.weekSeconds) }}</strong><small>周一至今</small></div>
      <div class="overview-number"><span><CheckCheck :size="13" />已完成</span><strong>{{ insights.completedCount }}<small> / {{ games.length }}</small></strong><button @click="emit('filter', 'playing')">在玩 {{ insights.playingCount }} 部 <ArrowUpRight :size="11" /></button></div>
      <button class="overview-number number-link" @click="emit('filter', 'local')"><span><HardDrive :size="13" />本地游戏</span><strong>{{ insights.localCount }}</strong><small>查看本地书架 <ArrowUpRight :size="11" /></small></button>
      <button class="overview-number number-link" @click="emit('filter', 'unmatched')"><span><Sparkles :size="13" />待补资料</span><strong>{{ insights.unmatchedCount }}</strong><small>{{ insights.unmatchedCount ? '补齐封面与作品资料' : '资料已齐全' }} <ArrowUpRight :size="11" /></small></button>
    </div>
  </section>
</template>

<style scoped>
.library-overview{display:grid;grid-template-columns:minmax(290px,1.1fr) minmax(390px,1.4fr);gap:25px;background:var(--surface);border:1px solid var(--line);border-radius:15px;padding:21px 25px;margin-bottom:25px;min-width:0}
.continue-story{display:flex;align-items:center;gap:17px;min-width:0}.story-cover{width:71px;height:99px;flex-shrink:0;background:var(--blue-soft);border-radius:7px;overflow:hidden;display:grid;place-items:center;color:var(--blue);box-shadow:0 4px 12px #23364b10}.story-cover img{width:100%;height:100%;object-fit:cover;object-position:top}.story-copy{min-width:0;flex:1}.section-kicker{display:flex;align-items:center;gap:6px;color:var(--blue);font-size:10px;letter-spacing:.7px;margin-bottom:7px}.story-title{display:-webkit-box;-webkit-line-clamp:2;-webkit-box-orient:vertical;overflow:hidden;overflow-wrap:anywhere;text-align:left;font-size:15px;line-height:1.55;color:var(--ink);font-weight:650;max-height:47px}.story-copy p{font-size:10px;color:var(--muted);margin-top:5px;display:flex;align-items:center;gap:5px;line-height:1.7}.continue-button{display:inline-flex;gap:6px;align-items:center;color:var(--blue);font-size:11px;font-weight:600;margin-top:10px;min-height:22px}.continue-button:hover{color:var(--ink)}.live-dot{width:5px;height:5px;border-radius:50%;background:#52b5a2;margin-left:6px}.empty-book{width:62px;height:80px;flex-shrink:0;border:1px solid var(--line);background:var(--blue-soft);border-radius:7px;color:var(--blue);display:grid;place-items:center}.quiet-story h2{font-size:18px;font-weight:600}.quiet-story p{display:block}.overview-numbers{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));align-items:center;gap:18px;padding-left:24px;border-left:1px solid var(--line)}.overview-number{display:flex;flex-direction:column;align-items:flex-start;gap:9px;min-width:0;text-align:left}.overview-number>span{display:flex;align-items:center;gap:5px;font-size:10px;color:var(--muted);white-space:nowrap}.overview-number>strong{font-size:25px;line-height:1.1;font-weight:650;color:var(--ink);font-variant-numeric:tabular-nums}.overview-number>strong.duration-value{font-size:16px;line-height:1.72;white-space:nowrap}.overview-number strong small{font-size:10px;color:var(--muted);font-weight:400}.overview-number>small,.overview-number>button{font-size:9px;color:var(--muted);display:flex;gap:3px;align-items:center;line-height:1.5}.overview-number>button{color:var(--blue)}.number-link:hover strong,.number-link:hover>small{color:var(--blue)}
@media(max-width:1200px){.library-overview{grid-template-columns:minmax(260px,1fr) minmax(305px,1.1fr);gap:16px;padding:18px}.overview-numbers{padding-left:17px;gap:11px;grid-template-columns:repeat(2,minmax(0,1fr))}.overview-number{gap:4px}.overview-number>strong{font-size:19px}.overview-number>strong.duration-value{font-size:14px;line-height:1.5}.overview-number>small,.overview-number>button{font-size:8px}.story-title{font-size:14px}}
@media(max-width:820px){.library-overview{grid-template-columns:minmax(0,1fr)}.overview-numbers{border-left:0;border-top:1px solid var(--line);padding:16px 0 0;grid-template-columns:repeat(4,minmax(0,1fr))}.overview-number>span{font-size:9px}.overview-number>strong.duration-value{white-space:normal}}
@media(max-height:780px){.library-overview{grid-template-columns:minmax(0,1fr);padding:12px 17px;margin-bottom:20px;gap:0}.overview-numbers{display:none}.continue-story{gap:14px}.story-cover{width:48px;height:66px}.story-copy{display:grid;grid-template-columns:minmax(0,1fr) auto;column-gap:18px}.section-kicker{grid-column:1;margin-bottom:4px;font-size:9px}.story-title,.quiet-story h2{grid-column:1;font-size:14px;-webkit-line-clamp:1}.story-copy p{grid-column:1;margin-top:3px;font-size:9px}.continue-button{grid-column:2;grid-row:1 / span 3;align-self:center;justify-self:end;margin-top:0;padding:10px 13px;background:var(--blue-soft);border-radius:7px;white-space:nowrap}.empty-book{width:48px;height:62px}.quiet-story p{line-height:1.7}}
</style>
