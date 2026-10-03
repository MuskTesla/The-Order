<script setup lang="ts">
const imageUrl = `${import.meta.env.BASE_URL}the-order.png`
</script>

<template>
  <img class="order-mark" :src="imageUrl" alt="The Order" draggable="false" decoding="async" />
</template>
