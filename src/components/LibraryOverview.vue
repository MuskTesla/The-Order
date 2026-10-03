<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { ArrowUpRight, BookOpen, Play, Radio } from 'lucide-vue-next'
import type { ActiveSession, LibraryGame } from '../shared/types'
import { formatPlayTime, getContinueStory } from '../library-insights'

const props = defineProps<{ games: LibraryGame[]; activeSession?: ActiveSession | null }>()
const emit = defineEmits<{ open: [game: LibraryGame]; play: [game: LibraryGame]; filter: [value: string] }>()
const story = computed(() => getContinueStory(props.games, props.activeSession))
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
  <section v-if="story" class="library-overview" :class="{ 'has-active-story': story.reason === 'active' }" aria-label="继续游玩">
    <button type="button" class="story-cover" :aria-label="`查看 ${story.game.name}`" @click="emit('open', story.game)">
      <img v-if="story.game.metadata?.coverUrl && !brokenCover" :src="story.game.metadata.coverUrl" alt="" loading="lazy" referrerpolicy="no-referrer" @error="brokenCover = true" />
      <BookOpen v-else :size="23" />
    </button>
    <div class="story-copy">
      <span class="section-kicker"><Radio v-if="story.reason === 'active'" :size="13" /><BookOpen v-else :size="13" />{{ story.reason === 'active' ? '正在计时' : '继续游玩' }}</span>
      <button type="button" class="story-title" :title="story.game.name" @click="emit('open', story.game)">{{ story.game.name }}</button>
      <span class="story-time">{{ story.reason === 'active' ? `本次 ${formatPlayTime(activeSession?.elapsedSeconds ?? 0)}` : story.game.totalPlaySeconds ? `已记录 ${formatPlayTime(story.game.totalPlaySeconds)}` : '尚无游玩记录' }}</span>
    </div>
    <button type="button" class="continue-button" @click="primaryAction"><Play v-if="story.canPlay" :size="14" fill="currentColor" /><ArrowUpRight v-else :size="14" />{{ story.reason === 'active' ? '查看游戏' : '继续游玩' }}</button>
  </section>
</template>
