<script setup lang="ts">
import { onMounted, onUnmounted } from 'vue'
import { installLiquidGlass } from '../liquid-glass'

let dispose: (() => void) | undefined
onMounted(() => { dispose = installLiquidGlass() })
onUnmounted(() => dispose?.())
</script>

<template><span hidden aria-hidden="true"></span></template>
