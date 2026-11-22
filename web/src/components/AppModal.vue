<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'

const props = defineProps<{ open: boolean; title: string }>()
const emit = defineEmits<{ close: [] }>()
const dlg = ref<HTMLDialogElement>()

function sync() {
  const d = dlg.value
  if (!d) return
  if (props.open && !d.open) d.showModal()
  else if (!props.open && d.open) d.close()
}
watch(() => props.open, sync, { flush: 'post' })
onMounted(sync)
</script>

<template>
  <!-- native <dialog>: focus trap, Esc to close and an inert background come for free -->
  <dialog ref="dlg" @close="emit('close')" @click.self="emit('close')">
    <div v-if="open" class="body">
      <header>
        <h2>{{ title }}</h2>
        <button type="button" class="x" aria-label="Close" @click="emit('close')">&times;</button>
      </header>
      <slot />
    </div>
  </dialog>
</template>

<style scoped>
dialog {
  border: 0;
  padding: 0;
  border-radius: 28px;
  background: var(--sheet);
  color: var(--ink);
  width: min(560px, calc(100vw - 32px));
  box-shadow: 0 30px 60px -24px rgba(0, 0, 0, 0.6);
}
dialog::backdrop {
  background: rgba(0, 0, 0, 0.6);
}
.body {
  padding: var(--space-5);
  display: flex;
  flex-direction: column;
  gap: var(--space-3);
}
header {
  display: flex;
  align-items: center;
  justify-content: space-between;
}
h2 {
  margin: 0;
  font: 600 22px/28px var(--font);
}
.x {
  min-height: 36px;
  width: 36px;
  padding: 0;
  font-size: 22px;
  line-height: 1;
}
</style>
