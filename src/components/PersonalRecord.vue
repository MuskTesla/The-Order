<script setup lang="ts">
import { Check, NotebookPen } from 'lucide-vue-next'
import type { PlayStatus } from '../shared/types'

defineProps<{ status: PlayStatus; rating: string | number; notes: string; saving: boolean; error: string }>()
const emit = defineEmits<{ status: [value: PlayStatus]; rating: [value: string]; notes: [value: string]; save: [] }>()
const statuses: { value: PlayStatus; label: string }[] = [{ value: 'planned', label: '未开始' }, { value: 'playing', label: '在玩' }, { value: 'completed', label: '已完成' }, { value: 'paused', label: '搁置' }, { value: 'dropped', label: '放弃' }]
</script>

<template>
  <form class="personal-record" aria-label="我的记录" novalidate @submit.prevent="emit('save')">
    <div class="record-heading"><strong><NotebookPen :size="16" /> 我的记录</strong><small>状态、评分和笔记</small></div>
    <div class="record-fields">
      <label class="field-label">游玩状态<select :value="status" aria-label="记录游玩状态" @change="emit('status', ($event.target as HTMLSelectElement).value as PlayStatus)"><option v-for="item in statuses" :key="item.value" :value="item.value">{{ item.label }}</option></select></label>
      <label class="field-label"><span>我的评分 <small>0–10</small></span><div class="record-rating"><input :value="rating" type="text" inputmode="decimal" placeholder="还未评分" aria-label="记录我的评分" @input="emit('rating', ($event.target as HTMLInputElement).value)" /><button type="button" :disabled="rating === ''" aria-label="清除个人评分" @click="emit('rating', '')">清除</button></div></label>
      <button type="submit" class="record-save" :disabled="saving"><Check :size="15" />{{ saving ? '正在保存…' : '保存我的记录' }}</button>
    </div>
    <details class="record-notes"><summary>游玩笔记 <span>{{ notes ? notes.replace(/\s+/g, ' ').slice(0, 70) : '记下感想，或下一次接着玩的地方' }}</span></summary><label class="field-label">游玩笔记<textarea :value="notes" rows="4" aria-label="记录游玩笔记" placeholder="记下感想，或下一次接着玩的地方…" @input="emit('notes', ($event.target as HTMLTextAreaElement).value)"></textarea></label></details>
    <p v-if="error" class="record-error" role="alert">{{ error }}</p>
  </form>
</template>

<style scoped>
.personal-record{min-width:0;padding:14px;border:1px solid var(--line);border-radius:10px;background:var(--surface);margin:0 0 22px;color:var(--ink)}.record-heading{display:flex;align-items:center;justify-content:space-between;gap:10px;margin-bottom:12px;flex-wrap:wrap}.record-heading strong{display:flex;align-items:center;gap:7px;font-size:14px;font-weight:600}.record-heading>small{font-size:12px;color:var(--muted)}.record-fields{display:grid;grid-template-columns:minmax(0,1fr) minmax(0,1.1fr) auto;gap:12px;align-items:end}.record-fields>.field-label{min-width:0;margin-bottom:0;display:flex;flex-direction:column;gap:6px}.record-fields select,.record-rating input{width:100%;min-width:0;margin:0;font-size:13px;padding:8px;color:var(--ink);background:var(--surface);border:1px solid var(--line);border-radius:7px}.record-rating{display:flex;align-items:center;gap:8px}.record-rating>button{font-size:12px;color:var(--blue);flex:none;padding:5px 0}.record-save{display:flex;align-items:center;justify-content:center;gap:5px;padding:9px 11px;border:1px solid var(--blue);border-radius:7px;color:var(--blue);font-size:13px;white-space:nowrap}.record-save:disabled,.record-rating>button:disabled{opacity:.55;cursor:not-allowed}.record-notes{margin-top:13px;border-top:1px solid var(--line);padding-top:10px;font-size:13px}.record-notes summary{cursor:pointer;line-height:1.7;overflow-wrap:anywhere}.record-notes summary>span{font-size:12px;color:var(--muted);margin-left:8px}.record-notes .field-label{display:block;margin-top:10px}.record-notes textarea{display:block;width:100%;min-width:0;margin-top:6px;resize:vertical;font-size:13px;color:var(--ink);background:var(--surface);border:1px solid var(--line);border-radius:7px;padding:9px;line-height:1.8}.record-error{font-size:13px;color:var(--ink);line-height:1.7;margin-top:12px;overflow-wrap:anywhere}.personal-record :is(button,input,select,textarea,summary):focus-visible{outline:2px solid var(--blue);outline-offset:3px}@media(max-width:480px){.record-fields{grid-template-columns:minmax(0,1fr) minmax(0,1fr)}.record-save{grid-column:1/-1;justify-self:end}}
</style>
