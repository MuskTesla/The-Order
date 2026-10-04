<script setup lang="ts">
import { onMounted, onUnmounted, ref } from 'vue'
import OrderMark from './OrderMark.vue'

defineEmits<{ home: [] }>()
const enabled = ref(false)
type WindowOverlay = EventTarget & { readonly visible: boolean }
let overlay: WindowOverlay | undefined
function syncOverlay() {
  enabled.value = overlay?.visible === true
  document.documentElement.classList.toggle('windows-caption', enabled.value)
}
onMounted(() => {
  overlay = (navigator as Navigator & { windowControlsOverlay?: WindowOverlay }).windowControlsOverlay
  syncOverlay()
  overlay?.addEventListener('geometrychange', syncOverlay)
})
onUnmounted(() => {
  overlay?.removeEventListener('geometrychange', syncOverlay)
  document.documentElement.classList.remove('windows-caption')
})
</script>

<template>
  <header v-if="enabled" class="desktop-titlebar" aria-label="应用标题栏">
    <div class="desktop-caption-area">
      <button type="button" class="desktop-caption-home" aria-label="The Order，返回我的书架" title="返回我的书架" @click="$emit('home')">
        <span aria-hidden="true"><OrderMark /></span><span>The Order</span>
      </button>
    </div>
  </header>
</template>
