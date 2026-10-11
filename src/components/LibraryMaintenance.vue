<script setup lang="ts">
import { computed } from 'vue'
import { KunButton, KunModal } from '@kungal/ui-vue'
import { CheckCircle2, CircleHelp, FolderOpen, RefreshCw, Sparkles } from 'lucide-vue-next'
import SectionSignature from './SectionSignature.vue'
import type { LibraryGame, MetadataMatchingState } from '../shared/types'

const props = defineProps<{ modelValue: boolean; matching?: MetadataMatchingState; missing: LibraryGame[]; unconfigured: LibraryGame[]; unavailable: boolean; hasLocal: boolean }>()
const emit = defineEmits<{ 'update:modelValue': [value: boolean]; start: [mode: 'missing' | 'reidentify']; cancel: []; review: []; open: [game: LibraryGame]; configure: [game: LibraryGame]; filter: [view: 'unmatched' | 'unconfigured'] }>()
const open = computed({ get: () => props.modelValue, set: value => emit('update:modelValue', value) })
const reviews = computed(() => props.matching?.reviews.length ?? 0)
</script>

<template>
  <KunModal v-model="open" title="资料整理" size="xl" scroll-behavior="inside" inner-class-name="maintenance-modal">
    <SectionSignature name="Rewrite" />
    <p class="modal-help">找到需要补充或核对的作品。这里的操作不会覆盖你的个人记录。</p>
    <section class="maintenance-task" aria-label="资料识别任务">
      <div><h3>资料识别</h3><p v-if="matching?.status === 'running'" role="status">{{ matching.mode === 'reidentify' ? '正在重新识别' : '正在补全缺项' }} · {{ matching.processed }} / {{ matching.total }}<br />{{ matching.currentName || '正在准备…' }}</p>
        <p v-else-if="matching?.id">{{ matching.status === 'cancelled' ? '已停止，完成的结果已保留' : '本次任务已结束' }} · 匹配 {{ matching.matched }} 部 · 待定 {{ matching.unmatched }} 部<span v-if="matching.failed"> · 失败 {{ matching.failed }} 部</span></p>
        <p v-else>补全只处理缺资料或封面的本地游戏。重新识别会重新检查所有本地游戏，关联变化先交给你核对。</p></div>
      <div class="maintenance-actions"><KunButton v-if="matching?.status === 'running'" size="sm" variant="bordered" @click="emit('cancel')">停止识别</KunButton><template v-else><KunButton size="sm" :disabled="unavailable || !missing.length" @click="emit('start', 'missing')"><Sparkles :size="15" /> 只补缺失 {{ missing.length }} 部</KunButton><KunButton size="sm" variant="bordered" :disabled="unavailable || !hasLocal" @click="emit('start', 'reidentify')"><RefreshCw :size="15" /> 重新识别全部本地游戏</KunButton></template></div>
    </section>
    <button class="maintenance-review-link" :disabled="!reviews" @click="emit('review')"><CircleHelp :size="20" /><span><strong>待核对 {{ reviews }} 项</strong><small>{{ reviews ? '查看当前关联和候选后，再决定是否更换' : '目前没有需要你决定的资料' }}</small></span><span v-if="reviews">进入核对 →</span><CheckCircle2 v-else :size="20" /></button>
    <div class="maintenance-sections">
      <section><div class="maintenance-section-heading"><h3>缺资料 {{ missing.length }}</h3><button v-if="missing.length" class="text-button" @click="emit('filter', 'unmatched')">在书架查看全部</button></div><p v-if="!missing.length" class="maintenance-empty">本地作品的资料和封面已齐全。</p><div v-for="game in missing.slice(0, 6)" :key="game.id" class="maintenance-game-row"><span><strong>{{ game.name }}</strong><small>{{ game.metadata ? '缺少封面' : '尚未关联作品资料' }}</small></span><button class="text-button" @click="emit('open', game)">查看与关联</button></div></section>
      <section><div class="maintenance-section-heading"><h3>待设启动程序 {{ unconfigured.length }}</h3><button v-if="unconfigured.length" class="text-button" @click="emit('filter', 'unconfigured')">在书架查看全部</button></div><p v-if="!unconfigured.length" class="maintenance-empty">本地作品都有可用的启动设置。</p><div v-for="game in unconfigured.slice(0, 6)" :key="game.id" class="maintenance-game-row"><span><strong>{{ game.name }}</strong><small><FolderOpen :size="13" /> 已有本地安装</small></span><button class="text-button" @click="emit('configure', game)">设置程序</button></div></section>
    </div>
  </KunModal>
</template>
