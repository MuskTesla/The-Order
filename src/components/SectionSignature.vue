<script setup lang="ts">
defineProps<{ name: 'Avalon' | 'Planetarian' | 'Clover' | 'Memoria' | 'Rewrite' | 'Harmonia' }>()
</script>

<template>
  <div class="section-signature" aria-hidden="true">{{ name }}</div>
</template>

<style scoped>
.section-signature { margin-top:3px; font-family:Georgia,'Times New Roman',serif; font-size:13px; font-weight:400; line-height:1.25; letter-spacing:.6px; color:var(--muted); }
</style>
