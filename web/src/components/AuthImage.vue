<script setup lang="ts">
import { onBeforeUnmount, ref, watchEffect } from 'vue'
import { apiBlob } from '@/services/api'

const props = defineProps<{ path: string; large?: boolean }>()
const url = ref('')
const failed = ref(false)

watchEffect(async () => {
  failed.value = false
  try {
    const next = URL.createObjectURL(await apiBlob(props.path))
    if (url.value) URL.revokeObjectURL(url.value)
    url.value = next
  } catch {
    failed.value = true
  }
})
onBeforeUnmount(() => url.value && URL.revokeObjectURL(url.value))
</script>

<template>
  <img
    v-if="url"
    :src="url"
    alt="Patient photo"
    :style="
      large
        ? 'max-width: 100%; max-height: 56vh; border-radius: 12px'
        : 'max-width: 320px; max-height: 320px; border-radius: 12px'
    "
  />
  <span v-else-if="failed" class="error">Photo unavailable</span>
  <span v-else>Loading photo…</span>
</template>
