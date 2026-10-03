<script setup lang="ts">
import { computed, ref } from 'vue'
import { Activity, BookOpen, CalendarDays, CheckCheck, Clock3, History, Radio, Trophy } from 'lucide-vue-next'
import type { ActiveSession, LibraryGame } from '../shared/types'
import { formatPlayTime, getLibraryInsights } from '../library-insights'
import { gameCoverUrl } from '../cover-display'

const props = withDefaults(defineProps<{ games: LibraryGame[]; activeSession?: ActiveSession | null; includeNsfw?: boolean }>(), { includeNsfw: true })
const emit = defineEmits<{ open: [game: LibraryGame] }>()
const insights = computed(() => getLibraryInsights(props.games, props.activeSession))
const maximum = computed(() => Math.max(60, ...insights.value.days.map(day => day.seconds)))
const selectedDay = ref<string | null>(null)
const selected = computed(() => insights.value.days.find(day => day.key === selectedDay.value))
const failedImages = ref(new Set<string>())
function broken(url: string) { failedImages.value = new Set([...failedImages.value, url]) }
function coverUrl(game: LibraryGame) { return gameCoverUrl(game, props.includeNsfw) }
function coverFailed(event: Event) {
  const url = (event.currentTarget as HTMLImageElement).getAttribute('src')
  if (url) broken(url)
}
function dateTime(value: string) {
  const parsed = new Date(value)
  return Number.isFinite(parsed.getTime()) ? parsed.toLocaleString('zh-CN', { month: 'numeric', day: 'numeric', hour: '2-digit', minute: '2-digit', hour12: false }) : '日期未知'
}
</script>

<template>
  <div class="play-statistics">
    <div class="statistics-summary">
      <article class="summary-tile"><span><Clock3 :size="15" />总游玩时间</span><strong>{{ formatPlayTime(insights.totalSeconds) }}</strong><small>已保存的游玩记录</small></article>
      <article class="summary-tile"><span><CalendarDays :size="15" />最近两周</span><strong>{{ formatPlayTime(insights.recentSeconds) }}</strong><small>{{ insights.activeDays }} 天留下了游玩记录</small></article>
      <article class="summary-tile"><span><CheckCheck :size="15" />作品完成率</span><strong>{{ insights.completionPercent }}<em>%</em></strong><small>{{ insights.completedCount }} / {{ games.length }} 部 · 占全部书架</small></article>
    </div>

    <section class="activity-panel" aria-labelledby="activity-heading">
      <header class="panel-heading"><div><span class="panel-eyebrow">故事的足迹</span><h2 id="activity-heading"><Activity :size="17" />最近 14 天</h2></div><div class="chart-reading" aria-live="polite"><strong>{{ selected ? formatPlayTime(selected.seconds) : formatPlayTime(insights.recentSeconds) }}</strong><span>{{ selected ? `${selected.label} 的已记录时长` : '两周累计 · 点击柱形查看每日时长' }}</span></div></header>
      <div class="activity-chart">
        <div class="chart-axis" aria-hidden="true"><span>{{ formatPlayTime(maximum) }}</span><span>{{ formatPlayTime(Math.round(maximum / 2)) }}</span><span>0</span></div>
        <div class="chart-plot" role="group" aria-label="每日游玩时长">
          <div class="chart-guides" aria-hidden="true"><i></i><i></i><i></i></div>
          <button v-for="day in insights.days" :key="day.key" class="day-column" :class="{ today: day.isToday, selected: selectedDay === day.key }" :aria-label="`${day.key}，${formatPlayTime(day.seconds)}`" :aria-pressed="selectedDay === day.key" :title="`${day.key} · ${formatPlayTime(day.seconds)}`" @click="selectedDay = selectedDay === day.key ? null : day.key">
            <span class="bar-track"><i :class="{ empty: !day.seconds }" :style="{ height: day.seconds ? `${Math.max(2, day.seconds / maximum * 100)}%` : '3px' }"></i></span><span class="day-label">{{ day.isToday ? '今天' : day.label }}</span>
          </button>
        </div>
      </div>
      <p v-if="!insights.recentSeconds" class="chart-empty"><BookOpen :size="14" />这两周还没有已保存的游玩记录。从书架启动游戏后，足迹会出现在这里。</p>
      <footer class="chart-note">以本地日期统计已保存时长。跨日记录按起止区间分摊；进行中的记录随保存更新。<span v-if="insights.undatedSeconds">另有 {{ formatPlayTime(insights.undatedSeconds) }} 仅保存在历史总时长中，未分配到日期。</span></footer>
    </section>

    <section v-if="insights.active" class="active-story" aria-label="正在进行的游戏"><span class="active-icon"><Radio :size="19" /></span><div><span>正在进行</span><button class="work-title" :title="insights.active.game.name" :aria-label="`查看 ${insights.active.game.name}`" @click="emit('open', insights.active.game)">{{ insights.active.game.name }}</button><p>开始于 {{ dateTime(insights.active.startedAt) }} · 本次时长单独展示，已保存部分计入统计</p></div><b>{{ formatPlayTime(insights.active.elapsedSeconds) }}</b></section>

    <div class="statistics-bottom">
      <section class="detail-panel" aria-labelledby="most-played-heading">
        <header class="panel-heading"><h2 id="most-played-heading"><Trophy :size="17" />投入最多的故事</h2><span>累计时长</span></header>
        <ol v-if="insights.mostPlayed.length" class="most-played">
          <li v-for="(game, index) in insights.mostPlayed" :key="game.id"><span class="rank">{{ String(index + 1).padStart(2, '0') }}</span><div class="small-cover"><img v-if="coverUrl(game) && !failedImages.has(coverUrl(game) ?? '')" :src="coverUrl(game) ?? undefined" alt="" referrerpolicy="no-referrer" loading="lazy" @error="coverFailed" /><BookOpen v-else :size="16" /></div><div class="rank-copy"><button class="work-title" :title="game.name" :aria-label="`查看 ${game.name}`" @click="emit('open', game)">{{ game.name }}</button><div class="rank-meter" aria-hidden="true"><i :style="{ width: `${Math.max(2, game.totalPlaySeconds / insights.mostPlayed[0]!.totalPlaySeconds * 100)}%` }"></i></div></div><span class="rank-time">{{ formatPlayTime(game.totalPlaySeconds) }}</span></li>
        </ol>
        <div v-else class="panel-empty"><Trophy :size="25" /><p>还没有游玩时长</p><span>每次从书架启动，都会留下真实记录。</span></div>
      </section>
      <section class="detail-panel" aria-labelledby="recent-sessions-heading">
        <header class="panel-heading"><h2 id="recent-sessions-heading"><History :size="17" />最近的游玩</h2><span>最多 8 次</span></header>
        <ol v-if="insights.recent.length" class="recent-sessions"><li v-for="record in insights.recent" :key="`${record.game.id}:${record.session.id}`"><span class="history-dot"></span><div><button class="work-title" :title="record.game.name" :aria-label="`查看 ${record.game.name}`" @click="emit('open', record.game)">{{ record.game.name }}</button><span>{{ dateTime(record.session.startedAt) }} — {{ dateTime(record.session.endedAt) }}</span></div><b>{{ formatPlayTime(record.session.seconds) }}</b></li></ol>
        <div v-else class="panel-empty"><History :size="25" /><p>{{ insights.active ? '本次故事还在进行' : '还没有已结束的记录' }}</p><span>{{ insights.active ? '当前记录显示在上方，结束计时后会收录于此。' : '有日期的游玩记录将在这里按时间排列。' }}</span></div>
      </section>
    </div>
  </div>
</template>

<style scoped>
.work-title{display:-webkit-box;-webkit-line-clamp:2;-webkit-box-orient:vertical;overflow:hidden;overflow-wrap:anywhere;text-align:left;color:var(--ink);font-size:11px;font-weight:500;line-height:1.7}.work-title:hover{color:var(--blue)}.active-story .work-title{font-size:12px;font-weight:600;-webkit-line-clamp:1}
.play-statistics{display:grid;gap:23px;color:var(--ink)}.statistics-summary{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:20px}.summary-tile{background:var(--surface);border:1px solid var(--line);border-radius:13px;padding:23px 25px;display:flex;flex-direction:column;gap:11px;min-width:0}.summary-tile>span{display:flex;gap:8px;align-items:center;font-size:11px;color:var(--muted)}.summary-tile>span svg{color:var(--blue)}.summary-tile strong{font-size:clamp(21px,2vw,29px);font-weight:650;line-height:1.4;font-variant-numeric:tabular-nums;overflow-wrap:anywhere}.summary-tile em{font-size:18px;font-style:normal;margin-left:4px;font-weight:400;color:var(--muted)}.summary-tile small{font-size:10px;color:var(--muted)}.activity-panel,.detail-panel{border:1px solid var(--line);border-radius:14px;background:var(--surface);padding:24px 26px;min-width:0}.panel-heading{display:flex;align-items:center;justify-content:space-between;gap:20px;margin-bottom:24px}.panel-heading h2{display:flex;gap:8px;align-items:center;color:var(--ink);font-size:15px;font-weight:600}.panel-heading h2 svg{color:var(--blue)}.panel-eyebrow{font-size:9px;color:var(--muted);letter-spacing:1.2px;display:block;margin-bottom:7px}.panel-heading>span{font-size:10px;color:var(--muted)}.chart-reading{display:flex;flex-direction:column;align-items:flex-end;gap:4px}.chart-reading strong{font-size:17px;font-weight:600;font-variant-numeric:tabular-nums}.chart-reading span{font-size:9px;color:var(--muted)}.activity-chart{display:flex;height:192px;gap:14px;margin-top:30px}.chart-axis{display:flex;flex-direction:column;justify-content:space-between;padding-bottom:28px;min-width:56px;font-size:9px;color:var(--muted);text-align:right;font-variant-numeric:tabular-nums}.chart-plot{position:relative;display:grid;grid-template-columns:repeat(14,minmax(0,1fr));flex:1;gap:clamp(5px,1.4vw,20px);min-width:0}.chart-guides{position:absolute;pointer-events:none;inset:0 0 27px;display:flex;flex-direction:column;justify-content:space-between}.chart-guides i{height:1px;border-top:1px dashed var(--line)}.day-column{display:flex;flex-direction:column;align-items:center;justify-content:flex-end;gap:9px;position:relative;min-width:0;z-index:1;border-radius:5px;padding:0 2px}.bar-track{display:flex;align-items:flex-end;justify-content:center;width:100%;height:165px}.bar-track i{display:block;width:100%;max-width:36px;border-radius:5px 5px 2px 2px;background:color-mix(in srgb,var(--blue) 50%,var(--surface));transition:background .2s,height .2s;min-height:3px}.bar-track i.empty{background:var(--line)}.day-column.today .bar-track i:not(.empty),.day-column:hover .bar-track i:not(.empty),.day-column.selected .bar-track i:not(.empty){background:var(--blue)}.day-column.selected .day-label,.day-column.today .day-label{color:var(--blue);font-weight:600}.day-label{font-size:9px;color:var(--muted);height:18px;line-height:18px;white-space:nowrap;font-variant-numeric:tabular-nums}.chart-note{font-size:9px;color:var(--muted);line-height:1.9;border-top:1px solid var(--line);margin-top:17px;padding-top:13px}.chart-note>span{display:block}.chart-empty{display:flex;align-items:center;justify-content:center;gap:7px;margin-top:18px;font-size:11px;color:var(--muted);line-height:1.7;text-align:center}.active-story{display:flex;align-items:center;gap:14px;padding:17px 22px;border:1px solid color-mix(in srgb,var(--blue) 25%,var(--line));border-radius:12px;background:var(--blue-soft)}.active-icon{height:39px;width:39px;display:grid;place-items:center;color:var(--blue);background:var(--surface);border-radius:12px;flex-shrink:0}.active-story>div{min-width:0;flex:1}.active-story>div>span{display:block;font-size:9px;color:var(--blue);margin-bottom:5px}.active-story strong{display:block;font-size:12px;font-weight:600;white-space:nowrap;text-overflow:ellipsis;overflow:hidden}.active-story p{font-size:9px;color:var(--muted);line-height:1.7;margin-top:5px}.active-story b{font-size:15px;white-space:nowrap;font-weight:600;font-variant-numeric:tabular-nums}.statistics-bottom{display:grid;grid-template-columns:minmax(0,1fr) minmax(0,1fr);gap:23px}.detail-panel{padding:23px}.detail-panel .panel-heading{margin-bottom:12px}.detail-panel h2{font-size:13px}.most-played,.recent-sessions{list-style:none;padding:0;margin:0}.most-played li{display:flex;align-items:center;gap:12px;padding:13px 0;border-bottom:1px solid var(--line)}.most-played li:last-child,.recent-sessions li:last-child{border-bottom:0}.rank{font-size:11px;color:var(--muted);font-variant-numeric:tabular-nums;flex-shrink:0}.most-played li:first-child .rank{color:var(--blue)}.small-cover{height:45px;width:33px;flex-shrink:0;border-radius:4px;overflow:hidden;background:var(--blue-soft);color:var(--blue);display:grid;place-items:center}.small-cover img{width:100%;height:100%;object-fit:cover;object-position:top}.rank-copy{flex:1;min-width:0}.rank-copy strong,.recent-sessions strong{font-size:11px;font-weight:500;line-height:1.7;display:-webkit-box;-webkit-line-clamp:2;-webkit-box-orient:vertical;overflow:hidden;overflow-wrap:anywhere}.rank-meter{width:100%;height:3px;background:var(--hover);border-radius:3px;margin-top:7px;overflow:hidden}.rank-meter i{height:100%;background:var(--blue);opacity:.65;display:block;border-radius:3px}.rank-time{font-size:9px;color:var(--muted);white-space:nowrap;font-variant-numeric:tabular-nums}.recent-sessions li{display:flex;gap:11px;align-items:center;padding:14px 0;border-bottom:1px solid var(--line);min-height:68px}.history-dot{width:6px;height:6px;border-radius:50%;background:color-mix(in srgb,var(--blue) 55%,var(--surface));flex-shrink:0}.recent-sessions li>div{flex:1;min-width:0}.recent-sessions li>div>span{font-size:9px;color:var(--muted);display:block;line-height:1.7;margin-top:4px}.recent-sessions b{font-size:10px;font-weight:500;white-space:nowrap;font-variant-numeric:tabular-nums}.panel-empty{display:flex;flex-direction:column;align-items:center;justify-content:center;min-height:210px;text-align:center;color:var(--muted);gap:10px}.panel-empty svg{color:var(--blue);opacity:.65}.panel-empty p{font-size:12px}.panel-empty span{font-size:10px;line-height:1.8;max-width:230px}
@media(max-width:1150px){.summary-tile{padding:19px}.summary-tile strong{font-size:21px}.statistics-bottom{gap:16px}.detail-panel{padding:18px}.rank-time{max-width:70px;text-align:right;white-space:normal;line-height:1.6}.most-played li{gap:8px}.chart-axis{min-width:48px}.chart-plot{gap:8px}}
@media(max-width:850px){.statistics-bottom{grid-template-columns:minmax(0,1fr)}.statistics-summary{gap:10px}.summary-tile{padding:16px}.summary-tile strong{font-size:19px}.activity-panel{padding:20px 16px}.chart-plot{gap:3px}.day-label{font-size:8px}.chart-reading span{max-width:145px;text-align:right;line-height:1.6}.active-story{flex-wrap:wrap}.active-story b{margin-left:53px}.chart-empty{align-items:flex-start}}
@media(prefers-reduced-motion:reduce){.bar-track i{transition:none}}
</style>
