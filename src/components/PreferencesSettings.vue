<script setup lang="ts">
import { nextTick } from 'vue'
import { KunSwitch } from '@kungal/ui-vue'
import { Check, Link2, Monitor, Upload, Download, FolderOpen, ArrowUpRight, RotateCcw, LoaderCircle } from 'lucide-vue-next'
import type { PublicSettings, Theme, NonPortraitCoverDisplay, ConnectionResult } from '../shared/types'

type Draft = { baseUrl: string; apiKey: string; includeNsfw: boolean; theme: Theme; readExecutableMetadata: boolean; nonPortraitCoverDisplay: NonPortraitCoverDisplay }
defineProps<{ draft: Draft; saved: PublicSettings | null; dirty: boolean; saving: boolean; testing: boolean; error: string; warning: string; affectsMatching: boolean; connection: (ConnectionResult & { baseUrl: string }) | null; connectionDirty: boolean; matching: boolean; dataPath: string; importing: boolean; preview: boolean }>()
const emit = defineEmits<{ change: [patch: Partial<Draft>]; save: []; saveTest: []; test: []; clearKey: []; reset: []; export: []; import: []; data: []; external: [url: string] }>()
async function keepSettingsFocusVisible(event: FocusEvent) {
  const target = event.target
  if (!(target instanceof HTMLElement) || target.closest('.settings-save-bar')) return
  await nextTick()
  const bar = target.closest('form')?.querySelector('.settings-save-bar')?.getBoundingClientRect()
  const control = (target.closest('label') ?? target).getBoundingClientRect()
  if (bar && bar.top < innerHeight && bar.bottom > 0 && control.bottom > bar.top) window.scrollBy({ top: control.bottom - bar.top + 12, behavior: 'instant' })
}
</script>

<template>
  <div class="preferences-settings">
    <form class="settings-edit-form" aria-label="偏好设置表单" @submit.prevent="emit('save')" @focusin="keepSettingsFocusVisible">
      <section class="panel settings-panel">
        <div class="preferences-heading"><Link2 :size="21" /><div><h2>资料源连接</h2><p>NextMoe · 鲲作品目录与多源评分</p></div><span class="saved-key-state">{{ saved?.hasApiKey ? '已保存密钥' : '尚未配置密钥' }}</span></div>
        <label class="field-label">API Base URL<input :value="draft.baseUrl" spellcheck="false" autocomplete="off" @input="emit('change', { baseUrl: ($event.target as HTMLInputElement).value })" /></label>
        <label class="field-label">API 密钥<input :value="draft.apiKey" type="password" autocomplete="new-password" spellcheck="false" :placeholder="saved?.hasApiKey ? '留空保留已保存密钥' : '填写你的 NextMoe 密钥'" @input="emit('change', { apiKey: ($event.target as HTMLInputElement).value })" /></label>
        <p class="preferences-help">{{ preview ? '浏览器预览不保存密钥；连接需在桌面版设置。' : '密钥在本机加密保存。空白输入保留已保存密钥，新输入只有保存后才生效。目录读取需要 catalog:read 权限。' }}</p>
        <div class="preferences-actions"><button type="button" :disabled="saving || testing" @click="emit('saveTest')">{{ testing ? '正在测试连接…' : '保存并测试连接' }}</button><button type="button" :disabled="saving || testing || !saved?.hasApiKey" @click="emit('test')">测试已保存连接</button><button v-if="saved?.hasApiKey" type="button" :disabled="saving || testing" class="remove-key" @click="emit('clearKey')">移除已保存密钥</button></div>
        <p v-if="matching && saved?.hasApiKey" class="preferences-help">移除已保存密钥会停止当前识别，已完成的结果保留。</p>
        <div v-if="testing || connection" class="connection-feedback" role="status"><template v-if="testing"><LoaderCircle :size="16" class="spin" />正在检查已保存的连接…</template><template v-else-if="connection"><strong>{{ connection.ok ? '已保存连接可用' : '连接未通过' }}</strong><span>{{ connection.message }}</span><small>测试地址：{{ connection.baseUrl }}</small></template></div>
        <p v-if="connectionDirty" class="preferences-help">连接有未保存的输入；测试只使用已保存配置。</p>
      </section>
      <section class="panel settings-panel">
        <div class="preferences-heading"><Monitor :size="21" /><div><h2>界面与识别偏好</h2><p>一次明确保存，应用自己的使用习惯</p></div></div>
        <div class="preferences-columns"><label class="field-label">外观主题<select :value="draft.theme" @change="emit('change', { theme: ($event.target as HTMLSelectElement).value as Theme })"><option value="light">明亮</option><option value="dark">深色</option><option value="system">跟随系统</option></select></label><label class="field-label">封面默认展示<select :value="draft.nonPortraitCoverDisplay" aria-label="封面默认展示" @change="emit('change', { nonPortraitCoverDisplay: ($event.target as HTMLSelectElement).value as NonPortraitCoverDisplay })"><option value="blur">完整显示＋柔化填边</option><option value="plain">完整显示＋纯色填边</option><option value="crop">铺满（裁切边缘）</option></select></label></div>
        <p class="preferences-help">铺满适用于所有比例，包括长竖图；完整显示模式保留横版、方形与长竖图，常规竖图自动适配。详情中的独立选择优先，保存后生效。</p>
        <div class="preferences-option"><div class="preference-copy"><strong>显示成人分级作品</strong><p>保存后同时应用于书架展示、搜索与资料获取。</p></div><KunSwitch :model-value="draft.includeNsfw" label="显示成人分级作品" label-class-name="sr-only" @update:model-value="emit('change', { includeNsfw: $event })" /></div>
        <div class="preferences-option"><div class="preference-copy"><strong>读取程序内的游戏名称</strong><p>仅读取有大小和范围限制的版本资源，不运行程序。读取仍可能触发 Windows 安全扫描；仅对可信游戏开启。</p></div><KunSwitch :model-value="draft.readExecutableMetadata" label="读取程序内的游戏名称" label-class-name="sr-only" @update:model-value="emit('change', { readExecutableMetadata: $event })" /></div>
      </section>
      <footer class="settings-save-bar">
        <div><strong>{{ saving ? '正在保存提交的设置…' : error ? '保存需要重试' : dirty ? '有未保存的更改' : '所有设置已保存' }}</strong><span>保存资料源、外观、封面与识别偏好</span><p v-if="matching">{{ affectsMatching ? '资料源、密钥、分级或识别方式变更保存后会停止当前识别；已完成结果保留。' : '仅更改外观或封面展示不会停止当前识别。' }}</p></div>
        <div class="preferences-actions"><button type="button" :disabled="!dirty || saving || testing" @click="emit('reset')"><RotateCcw :size="15" />放弃未保存更改</button><button type="submit" class="preferences-save" :disabled="saving || testing || (!dirty && !error)"><Check :size="16" />保存全部设置</button></div>
        <p v-if="error" class="preferences-error" role="alert">{{ error }}</p>
        <p v-if="warning" class="preferences-error" role="alert">{{ warning }}</p>
      </footer>
    </form>
    <section class="panel settings-panel settings-backup"><div class="preferences-heading"><Upload :size="21" /><div><h2>书架备份与帮助</h2><p>这些操作立即执行，不需要保存上面的偏好</p></div></div><p class="preferences-help">备份收藏、分类、笔记、游玩记录与本地封面副本；不包含密钥、全局设置或游戏文件。</p><div class="preferences-actions"><button type="button" @click="emit('export')"><Upload :size="16" />导出书架</button><button type="button" :disabled="importing" @click="emit('import')"><Download :size="16" />导入备份</button><button type="button" @click="emit('data')"><FolderOpen :size="16" />数据文件夹</button></div><details class="preferences-help-details"><summary>数据位置与使用帮助</summary><p class="preferences-data-path">{{ dataPath }}</p><div class="preferences-actions"><button type="button" @click="emit('external', 'https://developer.nextmoe.dev/')">获取 API 密钥 <ArrowUpRight :size="14" /></button><button type="button" @click="emit('external', 'https://developer.nextmoe.dev/docs/native-app')">NextMoe 接口文档 <ArrowUpRight :size="14" /></button><button type="button" @click="emit('external', 'https://ui.kungal.com/')">了解 KunUI <ArrowUpRight :size="14" /></button></div></details></section>
  </div>
</template>

<style scoped>
.preferences-settings{max-width:1050px;min-width:0}.settings-edit-form{min-width:0}.settings-panel{padding:22px;margin:0 0 18px}.preferences-heading{display:flex;align-items:center;gap:12px;margin-bottom:20px;color:var(--blue)}.preferences-heading>div{flex:1;min-width:0}.preferences-heading h2{font-size:17px;color:var(--ink)}.preferences-heading p,.preferences-help{font-size:13px;line-height:1.8;color:var(--muted)}.saved-key-state{font-size:12px;color:var(--muted);flex:none}.preferences-settings .field-label{font-size:13px;color:var(--muted);margin-bottom:16px;min-width:0}.preferences-settings .field-label :is(input,select){font-size:14px;min-width:0;color:var(--ink);background:var(--surface);border-color:var(--line)}.preferences-actions{display:flex;align-items:center;flex-wrap:wrap;gap:9px}.preferences-actions button{display:inline-flex;align-items:center;justify-content:center;gap:6px;padding:9px 12px;border:1px solid var(--line);border-radius:8px;font-size:13px;color:var(--blue);background:var(--surface)}.preferences-actions button:disabled{opacity:.55;cursor:not-allowed}.preferences-actions .preferences-save{color:#fff;background:var(--blue);border-color:var(--blue)}.preferences-actions .remove-key{color:var(--muted);border-color:transparent}.preferences-columns{display:grid;grid-template-columns:minmax(0,1fr) minmax(0,1.5fr);gap:18px}.preferences-option{display:flex;align-items:center;gap:20px;padding-top:17px;margin-top:17px;border-top:1px solid var(--line)}.preferences-option>.preference-copy{min-width:0;flex:1}.preferences-option strong{font-size:14px;color:var(--ink)}.preferences-option p{font-size:13px;line-height:1.8;color:var(--muted);margin-top:4px}.settings-save-bar{position:sticky;bottom:10px;z-index:5;display:flex;align-items:center;justify-content:space-between;gap:12px;flex-wrap:wrap;padding:14px 16px;margin:0 0 22px;border:1px solid var(--line);border-radius:10px;background:var(--surface);box-shadow:0 4px 16px #162f5512}.settings-save-bar strong{font-size:14px;color:var(--ink)}.settings-save-bar span,.settings-save-bar>div>p{display:block;font-size:12px;line-height:1.7;color:var(--muted)}.preferences-error{width:100%;font-size:13px;line-height:1.8;color:var(--ink);overflow-wrap:anywhere}.connection-feedback{display:flex;flex-wrap:wrap;align-items:center;gap:6px 10px;padding:12px;margin-top:12px;border:1px solid var(--line);border-radius:8px;font-size:13px;line-height:1.7;color:var(--ink)}.connection-feedback small{width:100%;color:var(--muted);overflow-wrap:anywhere}.preferences-help{margin-bottom:12px}.preferences-help-details{margin-top:18px;font-size:13px;color:var(--muted)}.preferences-help-details summary{cursor:pointer}.preferences-data-path{padding:12px 0;overflow-wrap:anywhere}.preferences-settings :is(button,input,select,summary):focus-visible{outline:2px solid var(--blue);outline-offset:3px}.preferences-option :deep(label:has(input:focus-visible)){outline:2px solid var(--blue);outline-offset:4px;border-radius:20px}@media(max-width:700px){.settings-panel{padding:16px}.preferences-heading{gap:9px;flex-wrap:wrap}.saved-key-state{margin-left:30px}.preferences-columns{grid-template-columns:minmax(0,1fr);gap:0}.settings-save-bar{bottom:5px;padding:12px;align-items:stretch}.settings-save-bar>.preferences-actions{width:100%;justify-content:flex-end}.preferences-option{gap:12px}}
</style>
